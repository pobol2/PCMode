using System.Text.Json.Nodes;

namespace PCMode.Engine.Tests;

/// <summary>
/// DDC/CI resources inside a real <see cref="ModeEngine"/> transition, next to an
/// unrelated resource -- not just the provider called directly.
/// </summary>
/// <remarks>
/// <see cref="DdcMonitorTests"/> and <see cref="DdcMonitorProviderSetTests"/>
/// prove the providers themselves behave correctly in isolation. Neither proves
/// what Step 10's acceptance criteria are actually about: how a DDC resource
/// behaves once it is one line among several in a real transition. That turns
/// out to be governed entirely by <see cref="ModeEngine"/>'s own sequential,
/// all-or-nothing dispatch -- the same rule every other resource type is
/// already held to (see <see cref="TransitionStep.Optional"/>'s remarks: "an
/// optional resource that is present is applied and allowed to fail like any
/// other") -- so what these tests confirm is that DDC resources are ordinary
/// citizens of that rule rather than a special case, in either direction:
///
/// - configured and reachable, they converge alongside another resource in the
///   same transition;
/// - configured and reachable but failing on write, they fail the transition
///   without undoing a resource that already succeeded before them;
/// - configured but unreachable and marked optional, they are omitted rather
///   than failing the transition, and do not stop a resource after them from
///   still being applied.
/// </remarks>
public sealed class DdcTransitionTests
{
    private const string DevicePath =
        @"\\?\DISPLAY#GSM5B09#5&39b3a707&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly DdcCapabilities BrightnessOnly = new(
        new Dictionary<DdcVcpCode, DdcVcpCapability> { [DdcVcpCode.Brightness] = new(DdcVcpCode.Brightness, []) }
    );

    [Fact]
    public async Task A_configured_monitor_converges_through_a_real_transition_alongside_another_resource()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        var other = new FakeProvider("other", "idle") { Allowed = ["go"] };

        using World world = Build(monitors, resources: """{"other":"go","ddc.Left.brightness":"75"}""", other);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Desktop");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal("go", Assert.Single(other.Applied));
        Assert.Equal((DevicePath, DdcVcpCode.Brightness, 75u), Assert.Single(monitors.Writes));
    }

    [Fact]
    public async Task A_required_write_that_never_lands_fails_the_transition_without_undoing_what_already_succeeded()
    {
        // "other" is declared first and has no dependency on the DDC resource, so
        // configuration order -- the plan's tie-break for independent resources
        // -- applies it before the monitor's turn comes. By the time the DDC
        // write fails, "other" is already done, and the point of this test is
        // that failing later does not unwind it.
        var monitors = new FakeDdcMonitors { TimeOutWrites = true };
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        var other = new FakeProvider("other", "idle") { Allowed = ["go"] };

        using World world = Build(monitors, resources: """{"other":"go","ddc.Left.brightness":"90"}""", other);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Desktop");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.False(outcome.StateRecorded);

        // Already succeeded, and left that way -- not rolled back because a
        // later, unrelated resource then failed.
        Assert.Equal("go", Assert.Single(other.Applied));

        TransitionStep ddcStep = outcome.Steps.Single(step => step.Resource == "ddc.Left.brightness");
        Assert.Equal(TransitionStepStatus.Failed, ddcStep.Status);
        Assert.Equal(2, ddcStep.Attempts); // DdcBrightnessProvider.PolicyFor: MaxAttempts = 2.
        Assert.Contains("did not respond", ddcStep.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_optional_monitor_is_omitted_and_does_not_stop_the_resource_after_it()
    {
        // The device path is configured but never added to the fake bus, so
        // capability probing genuinely fails the same way an asleep or
        // disconnected monitor would -- not a special "make it unavailable" flag.
        var monitors = new FakeDdcMonitors();

        var other = new FakeProvider("other", "idle") { Allowed = ["go"] };

        using World world = Build(
            monitors,
            resources: """{"ddc.Left.brightness":{"value":"50","optional":true},"other":"go"}""",
            other
        );

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Desktop");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal("go", Assert.Single(other.Applied));
        Assert.Empty(monitors.Writes);

        TransitionStep omitted = Assert.Single(outcome.Omitted);
        Assert.Equal("ddc.Left.brightness", omitted.Resource);
        Assert.Contains("did not answer", omitted.Problem, StringComparison.Ordinal);
    }

    private static World Build(FakeDdcMonitors monitors, string resources, FakeProvider other)
    {
        // Built through JsonObject rather than a hand-written literal:
        // DevicePath's own backslashes and braces are real Windows device-path
        // characters, not something safe to splice into JSON text unescaped.
        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["providers"] = new JsonObject { ["ddcMonitors"] = new JsonObject { ["Left"] = DevicePath } },
            ["spaces"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "space",
                    ["name"] = "Space",
                    ["activities"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "desktop",
                            ["name"] = "Desktop",
                            ["resources"] = JsonNode.Parse(resources),
                        },
                    },
                },
            },
        };

        string json = document.ToJsonString();

        List<IModeResourceProvider> providers = [.. EngineBootstrap.DdcMonitorProviders(json, monitors), other];

        return new World(json, providers);
    }

    /// <summary>A whole engine, built the same way <c>EngineRuntime</c> builds a real one.</summary>
    private sealed class World : IDisposable
    {
        public World(string document, IReadOnlyList<IModeResourceProvider> providers)
        {
            Root = new TemporaryRoot(document);

            ModeConfiguration configuration = ModeConfiguration.LoadOrCreate(Root.Path, providers);

            Engine = new ModeEngine(configuration, new ModeStateStore(Root.StateFile), providers, new FakeTime());
        }

        public TemporaryRoot Root { get; }

        public ModeEngine Engine { get; }

        public void Dispose() => Root.Dispose();
    }
}
