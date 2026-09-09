using PCMode.Tray.Elements;

namespace PCMode.Tray.Tests.Presentation;

/// <summary>
/// The IsBusy/Rows/Problem/StatusMessage shape shared by every "Current
/// hardware" panel (Audio's discovered outputs, RGB's OpenRGB controllers,
/// Displays' monitor registry), tested once against a fake scan rather than
/// once per feature.
/// </summary>
public sealed class HardwareInventoryListTests
{
    private sealed class FakeRow(string name) : IHardwareInventoryRow
    {
        public string FriendlyName => name;
        public string Name => name;
        public bool ShowUnderlyingDevice => false;
        public string? BadgeText => null;
        public bool HasBadge => false;
        public string ActionLabel => string.Empty;
        public bool CanUseActions => false;
        public bool ShowActivate => false;
        public bool CanActivate => false;
    }

    [Fact]
    public async Task ScanAsync_populates_rows_and_toggles_busy_around_the_scan()
    {
        var list = new HardwareInventoryList<FakeRow>(() =>
            Task.FromResult<(IReadOnlyList<FakeRow>, string?)>(([new FakeRow("a"), new FakeRow("b")], null))
        );

        Assert.True(list.CanScan);

        await list.ScanAsync();

        Assert.False(list.IsBusy);
        Assert.True(list.CanScan);
        Assert.Equal(2, list.Rows.Count);
        Assert.Null(list.Problem);
        Assert.False(list.HasProblem);
        Assert.Equal("2 device(s) found.", list.StatusMessage);
    }

    [Fact]
    public async Task ScanAsync_reports_a_problem_and_clears_the_status_message()
    {
        var list = new HardwareInventoryList<FakeRow>(() =>
            Task.FromResult<(IReadOnlyList<FakeRow>, string?)>(([], "The scan failed."))
        );

        await list.ScanAsync();

        Assert.Empty(list.Rows);
        Assert.Equal("The scan failed.", list.Problem);
        Assert.True(list.HasProblem);
        Assert.Null(list.StatusMessage);
    }

    [Fact]
    public async Task ScanAsync_is_a_no_op_while_a_scan_is_already_running()
    {
        var scanStarted = new TaskCompletionSource();
        var releaseScan = new TaskCompletionSource();
        int scanCalls = 0;

        var list = new HardwareInventoryList<FakeRow>(async () =>
        {
            scanCalls++;
            scanStarted.TrySetResult();
            await releaseScan.Task;
            return ([new FakeRow("a")], (string?)null);
        });

        Task first = list.ScanAsync();
        await scanStarted.Task;

        Assert.True(list.IsBusy);

        Task second = list.ScanAsync();
        releaseScan.SetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, scanCalls);
        Assert.Single(list.Rows);
    }

    [Fact]
    public void Apply_replaces_rows_without_running_the_scan_delegate()
    {
        int scanCalls = 0;
        var list = new HardwareInventoryList<FakeRow>(() =>
        {
            scanCalls++;
            return Task.FromResult<(IReadOnlyList<FakeRow>, string?)>(([], null));
        });

        list.Apply([new FakeRow("a")], problem: null);

        Assert.Single(list.Rows);
        Assert.Equal(0, scanCalls);
    }
}
