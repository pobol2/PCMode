using PCMode.Tray.Features.Layouts;

namespace PCMode.Tray.Tests;

/// <summary>
/// <see cref="LayoutsViewModel.CanSave"/>'s validation gate -- regression
/// coverage for a bug found during the Step 6.2 consolidation pass:
/// <c>TopologyProblem</c> special-cased "no monitor included" to null instead
/// of asking <see cref="LayoutsViewModel"/>'s own <c>Validate</c> helper (which
/// already returns "Include at least one monitor." for that exact case), so a
/// brand-new draft -- which starts with every monitor excluded -- left the
/// Save button clickable until the store refused the write.
/// </summary>
public sealed class LayoutsViewModelTests
{
    private const string TwoMonitorsConfiguration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}],
         "providers":{"display":{"monitors":{"Left":"FAKE-PATH-1","Right":"FAKE-PATH-2"}}}}
        """;

    [Fact]
    public void CanSave_is_false_for_a_new_layout_with_no_monitor_included()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new LayoutsViewModel(Workspaces.At(root.Path));

        Assert.Null(viewModel.StartNewLayout("Test layout"));

        Assert.True(viewModel.Rows.Count > 0);
        Assert.All(viewModel.Rows, row => Assert.False(row.Included));
        Assert.True(viewModel.HasUnsavedChanges);

        Assert.NotNull(viewModel.ValidationProblem);
        Assert.Contains("Include at least one monitor", viewModel.ValidationProblem, StringComparison.Ordinal);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void CanSave_becomes_true_once_a_monitor_is_included_and_primary()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new LayoutsViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");

        LayoutMonitorRow row = viewModel.Rows[0];
        row.Included = true;
        row.Primary = true;

        Assert.Null(viewModel.ValidationProblem);
        Assert.True(viewModel.CanSave);
    }
}
