namespace PCMode.Engine.Tests;

/// <summary>
/// What the Applications category reports, built from the same
/// <see cref="ApplicationProvider"/> a transition would ask.
/// </summary>
public sealed class ApplicationDiscovererTests
{
    private static ApplicationProvider Steam(IProcessHost host, bool reactivate = false) =>
        new(
            new ApplicationDefinition
            {
                Name = "Steam",
                ProcessName = "steam",
                Start = new LaunchCommand { Uri = "steam://open/bigpicture" },
                Reactivate = reactivate,
            },
            host
        );

    [Fact]
    public void Reports_a_running_application()
    {
        var host = new FakeProcessHost().Handles("steam").AlreadyRunning("steam");
        ApplicationProvider steam = Steam(host);

        HardwareItem item = Assert.Single(new ApplicationDiscoverer([steam]).Discover());

        Assert.Equal("Steam", item.Name);
        Assert.Equal(HardwareItemStatus.Ok, item.Status);
        Assert.Equal("Running.", item.Summary);
    }

    [Fact]
    public void Reports_an_installed_but_stopped_application()
    {
        var host = new FakeProcessHost().Handles("steam");
        ApplicationProvider steam = Steam(host);

        HardwareItem item = Assert.Single(new ApplicationDiscoverer([steam]).Discover());

        Assert.Equal(HardwareItemStatus.Ok, item.Status);
        Assert.Equal("Installed, not running.", item.Summary);
    }

    [Fact]
    public void Reports_an_application_nothing_handles_as_not_detected_rather_than_an_error()
    {
        // Nothing on this machine opens steam:// links -- it is not installed,
        // which is an ordinary answer, not a fault.
        var host = new FakeProcessHost();
        ApplicationProvider steam = Steam(host);

        HardwareItem item = Assert.Single(new ApplicationDiscoverer([steam]).Discover());

        Assert.Equal(HardwareItemStatus.NotDetected, item.Status);
    }

    [Fact]
    public void Reports_a_failed_installed_check_as_an_error_rather_than_not_detected()
    {
        // AvailabilityOf asks the host whether anything handles steam://
        // links; when that question itself cannot be answered, that is a
        // problem worth flagging, not the ordinary "not installed".
        ApplicationProvider steam = Steam(new UriCheckFailsHost());

        HardwareItem item = Assert.Single(new ApplicationDiscoverer([steam]).Discover());

        Assert.Equal(HardwareItemStatus.Error, item.Status);
    }

    [Fact]
    public void Ignores_providers_that_are_not_applications()
    {
        var host = new FakeProcessHost().Handles("steam").AlreadyRunning("steam");
        var providers = new IModeResourceProvider[] { new FakeProvider("theme"), Steam(host) };

        IReadOnlyList<HardwareItem> items = new ApplicationDiscoverer(providers).Discover();

        HardwareItem item = Assert.Single(items);
        Assert.Equal("Steam", item.Name);
    }

    [Fact]
    public void No_applications_configured_is_not_a_problem()
    {
        Assert.Empty(new ApplicationDiscoverer([]).Discover());
    }

    [Fact]
    public void A_broken_application_does_not_stop_the_others_from_being_reported()
    {
        var okHost = new FakeProcessHost().Handles("steam").AlreadyRunning("steam");

        ApplicationProvider steam = Steam(okHost);

        ApplicationProvider broken = new(
            new ApplicationDefinition
            {
                Name = "Broken",
                ProcessName = "broken",
                Start = new LaunchCommand { Uri = "broken://open" },
            },
            new UriCheckFailsHost()
        );

        IReadOnlyList<HardwareItem> items = new ApplicationDiscoverer([steam, broken]).Discover();

        Assert.Equal(2, items.Count);
        Assert.Equal(HardwareItemStatus.Ok, items.Single(item => item.Name == "Steam").Status);
        Assert.Equal(HardwareItemStatus.Error, items.Single(item => item.Name == "Broken").Status);
    }

    /// <summary>
    /// A host whose HandlesUriScheme cannot answer, so that AvailabilityOf
    /// itself turns the failure into <see cref="AvailabilityState.Unknown"/> --
    /// the one path in <see cref="ApplicationProvider"/> a busy or missing
    /// process list does not reach.
    /// </summary>
    private sealed class UriCheckFailsHost : IProcessHost
    {
        public bool IsRunning(string processName) => false;

        public void Start(LaunchCommand command) { }

        public void Kill(string processName) { }

        public bool WaitUntilStopped(string processName, TimeSpan within, CancellationToken cancellationToken) => true;

        public bool WaitForWindow(string processName, TimeSpan within, CancellationToken cancellationToken) => false;

        public bool Activate(string processName) => false;

        public void SendKeys(string processName, string keys) { }

        public bool FileExists(string path) => false;

        public bool HandlesUriScheme(string scheme) =>
            throw new InvalidOperationException("the shell could not be asked.");
    }
}
