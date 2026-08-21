using PCMode.Tray.Presentation;

namespace PCMode.Tray.Tests;

/// <summary>
/// <see cref="ObservableEditableRow"/>'s contract -- the shared base every
/// plain "row with an injected onChanged callback" (Applications' requirement
/// rows, Television's input/setting/profile rows, Rgb's LED/device/scene
/// rows) derives from instead of reimplementing the same
/// SetProperty-then-notify-the-owner shape. Tested once against a minimal
/// double rather than once per feature, per Main Dev Guide rule 5.
/// </summary>
public sealed class ObservableEditableRowTests
{
    [Fact]
    public void Set_notifies_the_owner_and_raises_PropertyChanged_when_the_value_changes()
    {
        int changedCount = 0;
        var row = new TestRow(() => changedCount++);
        var raisedProperties = new List<string?>();
        row.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        row.Name = "new value";

        Assert.Equal(1, changedCount);
        Assert.Contains(nameof(TestRow.Name), raisedProperties);
    }

    [Fact]
    public void Set_does_not_notify_the_owner_when_the_value_is_unchanged()
    {
        int changedCount = 0;
        var row = new TestRow(() => changedCount++) { Name = "same" };

        changedCount = 0;
        row.Name = "same";

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void NotifyChanged_reports_to_the_owner_independently_of_Set()
    {
        int changedCount = 0;
        var row = new TestRow(() => changedCount++);

        row.ReportExternalChange();

        Assert.Equal(1, changedCount);
    }

    private sealed class TestRow : ObservableEditableRow
    {
        private string _name = string.Empty;

        public TestRow(Action onChanged)
            : base(onChanged)
        {
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public void ReportExternalChange() => NotifyChanged();
    }
}
