using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Presentation.Configuration;

/// <summary>
/// The section-composition mechanics a page-level ViewModel built on
/// <see cref="IConfigurationSection"/> shares -- load/save/discard fan-out,
/// dirty propagation, and validation aggregation -- tested once against a
/// minimal double rather than once per real sectioned page. Per Main Dev
/// Guide rule 4: generic page mechanics belong in shared infrastructure, so
/// this is where they are protected.
/// </summary>
public sealed class IConfigurationSectionTests
{
    [Fact]
    public void SelectKey_loads_every_section_without_marking_the_page_dirty()
    {
        var sectionA = new FakeSection();
        var sectionB = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), sectionA, sectionB);

        model.SelectKey("desk-1");

        Assert.Equal("desk-1", sectionA.LoadedKey);
        Assert.Equal("desk-1", sectionB.LoadedKey);
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public void A_sections_onChanged_callback_marks_the_page_dirty()
    {
        var section = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), section);
        model.SelectKey("desk-1");

        section.RaiseChanged();

        Assert.True(model.HasUnsavedChanges);
    }

    [Fact]
    public void Save_calls_every_section_and_clears_dirty_state_on_success()
    {
        var sectionA = new FakeSection();
        var sectionB = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), sectionA, sectionB);
        model.SelectKey("desk-1");
        sectionA.RaiseChanged();

        bool saved = model.TrySaveChanges();

        Assert.True(saved);
        Assert.Equal(1, sectionA.SaveCalls);
        Assert.Equal(1, sectionB.SaveCalls);
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public void Save_stops_at_the_first_failing_section_and_does_not_save_the_rest()
    {
        var sectionA = new FakeSection { SaveFailureMessage = "Section A refused." };
        var sectionB = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), sectionA, sectionB);
        model.SelectKey("desk-1");
        sectionA.RaiseChanged();

        bool saved = model.TrySaveChanges();

        Assert.False(saved);
        Assert.Equal("Section A refused.", model.ErrorMessage);
        Assert.Equal(1, sectionA.SaveCalls);
        Assert.Equal(0, sectionB.SaveCalls);
        Assert.True(model.HasUnsavedChanges);
    }

    [Fact]
    public void ValidationProblem_reports_the_first_sections_problem_and_blocks_every_save()
    {
        var sectionA = new FakeSection { Problem = "Section A is invalid." };
        var sectionB = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), sectionA, sectionB);
        model.SelectKey("desk-1");
        sectionA.RaiseChanged();

        Assert.Equal("Section A is invalid.", model.ValidationProblem);

        bool saved = model.TrySaveChanges();

        Assert.False(saved);
        Assert.Equal(0, sectionA.SaveCalls);
        Assert.Equal(0, sectionB.SaveCalls);
    }

    [Fact]
    public void Discard_reloads_every_section_from_the_current_key_and_clears_dirty_state()
    {
        var section = new FakeSection();
        var model = new TestPageViewModel(new FakeWorkspace(), section);
        model.SelectKey("desk-1");
        section.RaiseChanged();

        model.DiscardChanges();

        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(2, section.LoadCalls); // once on select, once on discard's reload
    }

    private sealed class FakeSection : IConfigurationSection
    {
        private Action? _onChanged;

        public object? LoadedKey { get; private set; }

        public int LoadCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public string? SaveFailureMessage { get; set; }

        public string? Problem { get; set; }

        public string? ValidationProblem => Problem;

        public void WireOnChanged(Action onChanged) => _onChanged = onChanged;

        public void RaiseChanged() => _onChanged?.Invoke();

        public void Load(object? entityKey)
        {
            LoadedKey = entityKey;
            LoadCalls++;
        }

        public string? Save()
        {
            SaveCalls++;
            return SaveFailureMessage;
        }
    }

    /// <summary>
    /// The minimal shape a real sectioned page's ViewModel takes: it owns the
    /// selected key and composes a fixed list of sections, wiring each one's
    /// onChanged callback to its own <see cref="ConfigurationPageViewModelBase.MarkDirty"/>
    /// and fanning Load/Save/Discard out to every section in the same
    /// declared order.
    /// </summary>
    private sealed class TestPageViewModel : ConfigurationPageViewModelBase
    {
        private readonly IReadOnlyList<FakeSection> _sections;

        public TestPageViewModel(IConfigurationWorkspace workspace, params FakeSection[] sections)
            : base(workspace)
        {
            _sections = sections;

            foreach (FakeSection section in _sections)
            {
                section.WireOnChanged(MarkDirty);
            }
        }

        public override bool CanSave => HasUnsavedChanges && !IsBusy && ValidationProblem is null;

        public override string? ValidationProblem =>
            _sections
                .Select(section => section.ValidationProblem)
                .FirstOrDefault(problem => problem is not null);

        public override void SelectKey(object key)
        {
            SelectedKey = key;

            RunWithoutDirtyTracking(() =>
            {
                foreach (FakeSection section in _sections)
                {
                    section.Load(key);
                }
            });

            ClearDirtyState();
        }

        public override void Refresh()
        {
        }

        protected override string? SaveChangesCore()
        {
            foreach (FakeSection section in _sections)
            {
                if (section.ValidationProblem is string problem)
                {
                    return problem;
                }
            }

            foreach (FakeSection section in _sections)
            {
                if (section.Save() is string error)
                {
                    return error;
                }
            }

            ClearDirtyState();
            return null;
        }

        protected override void DiscardChangesCore() => SelectKey(SelectedKey!);
    }
}
