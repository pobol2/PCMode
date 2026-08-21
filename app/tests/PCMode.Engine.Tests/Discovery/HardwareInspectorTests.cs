namespace PCMode.Engine.Tests;

/// <summary>A discoverer that answers exactly what a test tells it to.</summary>
internal sealed class FakeDiscoverer(string category, IReadOnlyList<HardwareItem>? items = null, Exception? fails = null)
    : IHardwareDiscoverer
{
    public string Category => category;

    public IReadOnlyList<HardwareItem> Discover() => fails is not null ? throw fails : items ?? [];
}

/// <summary>
/// What runs every discoverer and keeps one's failure from being every
/// category's failure.
/// </summary>
public sealed class HardwareInspectorTests
{
    [Fact]
    public void Runs_every_discoverer_and_keeps_their_order()
    {
        var displays = new FakeDiscoverer("Displays", [new HardwareItem("Monitor", HardwareItemStatus.Ok, "1920x1080", [])]);
        var audio = new FakeDiscoverer("Audio outputs", []);

        IReadOnlyList<HardwareCategoryResult> results = HardwareInspector.Discover([displays, audio]);

        Assert.Equal(["Displays", "Audio outputs"], results.Select(result => result.Category));
        Assert.Single(results[0].Items);
        Assert.Empty(results[1].Items);
        Assert.All(results, result => Assert.Null(result.Problem));
    }

    [Fact]
    public void One_category_failing_does_not_stop_the_others()
    {
        var displays = new FakeDiscoverer("Displays", [new HardwareItem("Monitor", HardwareItemStatus.Ok, "ok", [])]);
        var audio = new FakeDiscoverer("Audio outputs", fails: new InvalidOperationException("Core Audio could not be reached."));
        var controllers = new FakeDiscoverer("Controllers", [new HardwareItem("Controller 1", HardwareItemStatus.NotDetected, "Not connected.", [])]);

        IReadOnlyList<HardwareCategoryResult> results = HardwareInspector.Discover([displays, audio, controllers]);

        Assert.Equal(3, results.Count);

        HardwareCategoryResult displayResult = results.Single(result => result.Category == "Displays");
        Assert.Null(displayResult.Problem);
        Assert.Single(displayResult.Items);

        HardwareCategoryResult audioResult = results.Single(result => result.Category == "Audio outputs");
        Assert.Equal("Core Audio could not be reached.", audioResult.Problem);
        Assert.Empty(audioResult.Items);

        HardwareCategoryResult controllerResult = results.Single(result => result.Category == "Controllers");
        Assert.Null(controllerResult.Problem);
        Assert.Single(controllerResult.Items);
    }

    [Fact]
    public void An_empty_category_is_not_a_problem()
    {
        var controllers = new FakeDiscoverer("Controllers", []);

        IReadOnlyList<HardwareCategoryResult> results = HardwareInspector.Discover([controllers]);

        Assert.Null(results[0].Problem);
        Assert.Empty(results[0].Items);
    }
}
