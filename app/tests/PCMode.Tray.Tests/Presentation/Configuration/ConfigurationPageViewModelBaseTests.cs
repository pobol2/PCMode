using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Presentation.Configuration;

/// <summary>
/// The mechanics every configuration ViewModel shares -- dirty tracking,
/// Save/Discard, and the <see cref="IConfigurationWorkspace"/> read helpers --
/// tested once against a minimal double rather than five times against each
/// feature. Per Main Dev Guide rule 4: generic page mechanics belong in
/// shared infrastructure, so this is where they are protected.
/// </summary>
public sealed class ConfigurationPageViewModelBaseTests
{
    [Fact]
    public void Editing_a_dirty_tracked_property_marks_unsaved_changes()
    {
        var model = new TestViewModel(new FakeWorkspace());

        Assert.False(model.HasUnsavedChanges);

        model.Value = "edited";

        Assert.True(model.HasUnsavedChanges);
        Assert.True(model.CanDiscard);
    }

    [Fact]
    public void TrySaveChanges_clears_dirty_state_and_the_error_on_success()
    {
        var model = new TestViewModel(new FakeWorkspace()) { Value = "edited" };

        bool saved = model.TrySaveChanges();

        Assert.True(saved);
        Assert.Null(model.ErrorMessage);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(1, model.SaveCalls);
    }

    [Fact]
    public void TrySaveChanges_reports_the_refusal_and_keeps_the_edit_dirty()
    {
        var model = new TestViewModel(new FakeWorkspace()) { Value = "edited", RefuseSaveWith = "No." };

        bool saved = model.TrySaveChanges();

        Assert.False(saved);
        Assert.Equal("No.", model.ErrorMessage);
        Assert.True(model.HasUnsavedChanges);
    }

    [Fact]
    public void DiscardChanges_clears_the_error_and_reloads_from_the_workspace()
    {
        var model = new TestViewModel(new FakeWorkspace()) { Value = "edited", RefuseSaveWith = "No." };
        model.TrySaveChanges();
        Assert.NotNull(model.ErrorMessage);

        model.DiscardChanges();

        Assert.Null(model.ErrorMessage);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(1, model.DiscardCalls);
        Assert.Equal("loaded", model.Value);
    }

    [Fact]
    public void RunWithoutDirtyTracking_suppresses_dirty_tracking_for_a_load()
    {
        var model = new TestViewModel(new FakeWorkspace());

        model.LoadWithoutDirtyTracking("from disk");

        Assert.Equal("from disk", model.Value);
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public void Busy_state_change_notifies_CanSave()
    {
        var model = new TestViewModel(new FakeWorkspace());
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.SetBusy(true);

        Assert.Contains(nameof(TestViewModel.CanSave), raised);
    }

    [Fact]
    public void ReadProviders_of_one_kind_reads_from_the_workspace()
    {
        var provider = new StubProvider("theme");
        var model = new TestViewModel(new FakeWorkspace(BuildSnapshot(provider)));

        IReadOnlyList<StubProvider> providers = model.PublicReadProviders();

        Assert.Same(provider, Assert.Single(providers));
    }

    [Fact]
    public void ReadProviders_reports_a_problem_instead_of_throwing_when_the_configuration_cannot_be_read()
    {
        var model = new TestViewModel(new FakeWorkspace());

        (IReadOnlyList<StubProvider> providers, string? problem) = model.PublicReadProvidersWithProblem();

        Assert.Empty(providers);
        Assert.NotNull(problem);
        Assert.Contains("could not be read", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunActivationAsync_reports_a_verified_outcome_through_status_and_a_workspace_note()
    {
        var workspace = new FakeWorkspace();
        var model = new TestViewModel(workspace);

        await model.PublicRunActivationAsync(_ => Task.FromResult(new ActivationOutcome(true, "Activated.")));

        Assert.Equal("Activated.", model.StatusMessage);
        Assert.Null(model.ErrorMessage);
        Assert.False(model.IsBusy);
        Assert.Contains("Activated.", workspace.Notes);
    }

    [Fact]
    public async Task RunActivationAsync_reports_an_unverified_outcome_as_an_error_and_a_workspace_warning()
    {
        var workspace = new FakeWorkspace();
        var model = new TestViewModel(workspace);

        await model.PublicRunActivationAsync(_ => Task.FromResult(new ActivationOutcome(false, "Could not verify.")));

        Assert.Equal("Could not verify.", model.StatusMessage);
        Assert.Equal("Could not verify.", model.ErrorMessage);
        Assert.Contains("Could not verify.", workspace.Warnings);
    }

    [Fact]
    public async Task RunActivationAsync_reports_a_thrown_exception_through_a_workspace_fault_instead_of_propagating()
    {
        var workspace = new FakeWorkspace();
        var model = new TestViewModel(workspace);

        await model.PublicRunActivationAsync(_ => throw new InvalidOperationException("boom"));

        Assert.False(model.IsBusy);
        Assert.NotNull(model.ErrorMessage);
        Assert.Contains("boom", model.StatusMessage);
        Assert.Contains(model.StatusMessage!, workspace.Warnings);
    }

    [Fact]
    public async Task RunActivationAsync_refuses_while_the_editor_has_unsaved_changes()
    {
        var model = new TestViewModel(new FakeWorkspace()) { Value = "edited" };
        bool activateRan = false;

        await model.PublicRunActivationAsync(_ =>
        {
            activateRan = true;
            return Task.FromResult(new ActivationOutcome(true, "Activated."));
        });

        Assert.False(activateRan);
        Assert.NotNull(model.ErrorMessage);
    }

    private static (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers) BuildSnapshot(
        IModeResourceProvider provider)
    {
        IReadOnlyList<IModeResourceProvider> providers = [provider];
        ModeConfiguration configuration = ModeConfiguration.Parse(
            """{"schemaVersion":1,"spaces":[]}""",
            providers);

        return (configuration, providers);
    }

    private sealed class StubProvider(string key) : IModeResourceProvider
    {
        public string Key { get; } = key;

        public string DisplayName => Key;

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => null;

        public void Apply(string value, CancellationToken cancellationToken)
        {
        }
    }

    /// <summary>Exposes the protected base members a real feature ViewModel builds on, for testing in isolation.</summary>
    private sealed class TestViewModel(IConfigurationWorkspace workspace) : ConfigurationPageViewModelBase(workspace)
    {
        public int RefreshCalls { get; private set; }

        public int DiscardCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public string? RefuseSaveWith { get; set; }

        private string _value = string.Empty;

        public string Value
        {
            get => _value;
            set => SetDirtyProperty(ref _value, value);
        }

        public override bool CanSave => HasUnsavedChanges && !IsBusy;

        public override void SelectKey(object key) => SelectedKey = key;

        public override void Refresh()
        {
            RefreshCalls++;
            RunWithoutDirtyTracking(() => Value = "loaded");
            ClearDirtyState();
        }

        public void LoadWithoutDirtyTracking(string value) =>
            RunWithoutDirtyTracking(() => Value = value);

        public void SetBusy(bool value) => IsBusy = value;

        public IReadOnlyList<StubProvider> PublicReadProviders() => ReadProviders<StubProvider>().Providers;

        public (IReadOnlyList<StubProvider> Providers, string? Problem) PublicReadProvidersWithProblem() =>
            ReadProviders<StubProvider>();

        public Task PublicRunActivationAsync(Func<CancellationToken, Task<ActivationOutcome>> activate) =>
            RunActivationAsync("Test", "Activating...", activate);

        protected override string? SaveChangesCore()
        {
            SaveCalls++;

            if (RefuseSaveWith is not null)
            {
                return RefuseSaveWith;
            }

            ClearDirtyState();
            return null;
        }

        protected override void DiscardChangesCore()
        {
            DiscardCalls++;
            Refresh();
        }
    }
}
