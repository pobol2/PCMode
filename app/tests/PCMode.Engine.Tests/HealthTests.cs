namespace PCMode.Engine.Tests;

/// <summary>
/// Reading a transition as per-resource health, in the terms a screen -- not a
/// log -- needs to show.
/// </summary>
public sealed class HealthTests
{
    [Fact]
    public void A_correct_resource_reads_as_correct()
    {
        TransitionOutcome outcome = Outcome(
            Step("theme", TransitionStepStatus.AlreadyCorrect));

        ModeHealth health = ModeHealth.From(outcome, DateTimeOffset.UnixEpoch);

        Assert.Equal(ResourceHealthState.Correct, Assert.Single(health.Resources).State);
        Assert.Equal(ResourceHealthState.Correct, health.Overall);
        Assert.Empty(health.Concerns);
    }

    [Fact]
    public void A_failed_required_resource_is_failed_and_dominates_overall()
    {
        TransitionOutcome outcome = Outcome(
            Step("theme", TransitionStepStatus.AlreadyCorrect),
            Step("tv", TransitionStepStatus.Failed, problem: "no picture", optional: false));

        ModeHealth health = ModeHealth.From(outcome, DateTimeOffset.UnixEpoch);

        ResourceHealth tv = Assert.Single(health.Resources, resource => resource.Key == "tv");
        Assert.Equal(ResourceHealthState.Failed, tv.State);
        Assert.Equal("no picture", tv.Detail);
        Assert.Equal(ResourceHealthState.Failed, health.Overall);
        Assert.Contains(tv, health.Concerns);
    }

    [Fact]
    public void A_failed_optional_resource_is_degraded_not_failed()
    {
        TransitionOutcome outcome = Outcome(
            Step("theme", TransitionStepStatus.AlreadyCorrect),
            Step("rgb", TransitionStepStatus.Failed, problem: "unreachable", optional: true));

        ModeHealth health = ModeHealth.From(outcome, DateTimeOffset.UnixEpoch);

        Assert.Equal(ResourceHealthState.Degraded, Assert.Single(health.Resources, r => r.Key == "rgb").State);
        Assert.Equal(ResourceHealthState.Degraded, health.Overall);
    }

    [Fact]
    public void An_omitted_resource_is_unavailable_and_does_not_drag_overall_down_from_correct()
    {
        TransitionOutcome outcome = Outcome(
            Step("theme", TransitionStepStatus.AlreadyCorrect),
            Step("rgb", TransitionStepStatus.Omitted, problem: "OpenRGB is not installed.", optional: true));

        ModeHealth health = ModeHealth.From(outcome, DateTimeOffset.UnixEpoch);

        Assert.Equal(ResourceHealthState.Unavailable, Assert.Single(health.Resources, r => r.Key == "rgb").State);
        Assert.Equal(ResourceHealthState.Correct, health.Overall);
    }

    [Fact]
    public void A_resource_not_reached_is_pending()
    {
        TransitionOutcome outcome = Outcome(
            Step("first", TransitionStepStatus.Failed, problem: "no", optional: false),
            Step("second", TransitionStepStatus.NotReached));

        ModeHealth health = ModeHealth.From(outcome, DateTimeOffset.UnixEpoch);

        Assert.Equal(ResourceHealthState.Pending, Assert.Single(health.Resources, r => r.Key == "second").State);

        // The required failure still wins the overall verdict.
        Assert.Equal(ResourceHealthState.Failed, health.Overall);
    }

    [Fact]
    public void Failed_outranks_degraded_which_outranks_pending()
    {
        TransitionOutcome outcome = Outcome(
            Step("a", TransitionStepStatus.NotReached),
            Step("b", TransitionStepStatus.Failed, problem: "x", optional: true));

        Assert.Equal(ResourceHealthState.Degraded, ModeHealth.From(outcome, DateTimeOffset.UnixEpoch).Overall);

        TransitionOutcome worse = Outcome(
            Step("a", TransitionStepStatus.Failed, problem: "x", optional: true),
            Step("b", TransitionStepStatus.Failed, problem: "y", optional: false));

        Assert.Equal(ResourceHealthState.Failed, ModeHealth.From(worse, DateTimeOffset.UnixEpoch).Overall);
    }

    [Fact]
    public void A_provider_that_reports_extra_diagnostics_is_asked_for_them()
    {
        var reporting = new ReportingProvider("audio");

        TransitionOutcome outcome = Outcome(
            Step("audio", TransitionStepStatus.AlreadyCorrect));

        ModeHealth health = ModeHealth.From(
            outcome,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, IModeResourceProvider>(StringComparer.OrdinalIgnoreCase) { ["audio"] = reporting });

        Assert.Equal("extra detail", Assert.Single(health.Resources).Diagnostics);
    }

    [Fact]
    public void A_provider_whose_diagnostics_throw_does_not_break_the_health_read()
    {
        var reporting = new ReportingProvider("audio") { Throws = true };

        TransitionOutcome outcome = Outcome(Step("audio", TransitionStepStatus.AlreadyCorrect));

        ModeHealth health = ModeHealth.From(
            outcome,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, IModeResourceProvider>(StringComparer.OrdinalIgnoreCase) { ["audio"] = reporting });

        Assert.Contains("Diagnostics unavailable", Assert.Single(health.Resources).Diagnostics);
    }

    private static TransitionOutcome Outcome(params TransitionStep[] steps) =>
        new("Movie", TransitionStatus.Succeeded, false, "test outcome", steps, true, null, true);

    private static TransitionStep Step(
        string key,
        TransitionStepStatus status,
        string? problem = null,
        bool optional = false) =>
        new(key, key, "value", "value", status, problem, null, "value", 1, optional);

    private sealed class ReportingProvider(string key) : IHealthReportingProvider
    {
        public string Key { get; } = key;

        public string DisplayName => Key;

        public bool Throws { get; init; }

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => "value";

        public void Apply(string value, CancellationToken cancellationToken)
        {
        }

        public string? HealthDetail(string target) =>
            Throws ? throw new InvalidOperationException("boom") : "extra detail";
    }
}
