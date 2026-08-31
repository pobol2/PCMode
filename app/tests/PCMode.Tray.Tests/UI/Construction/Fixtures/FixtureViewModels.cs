using System.Collections.ObjectModel;

namespace PCMode.Tray.Tests.UI.Construction.Fixtures;

/// <summary>
/// Fake data shaped like a two-level block tree, to prove the view-model
/// to block connection. These are plain objects on purpose: nothing here
/// implements <see cref="System.ComponentModel.INotifyPropertyChanged"/> or
/// knows a control exists, because a view model reaches its block purely by
/// its type, through an implicit data template.
/// </summary>
public sealed class FixtureRootViewModel(string header, FixtureDetailViewModel detail)
{
    public string Header { get; } = header;

    public ObservableCollection<FixtureItemViewModel> Items { get; } = [];

    public FixtureDetailViewModel Detail { get; } = detail;
}

/// <inheritdoc cref="FixtureRootViewModel"/>
public sealed class FixtureDetailViewModel(string primary, string secondary)
{
    public string Primary { get; } = primary;

    public string Secondary { get; } = secondary;
}

/// <summary>A leaf: resolves to terminal content, which ends the recursion.</summary>
public sealed class FixtureItemViewModel(string name)
{
    public string Name { get; } = name;
}

/// <summary>
/// Feeds <see cref="FixtureStrictBlock"/>, whose one region admits only a
/// detail block. <see cref="Only"/> is deliberately untyped so a test can put
/// the wrong thing in it.
/// </summary>
public sealed class FixtureStrictViewModel(object only)
{
    public object Only { get; } = only;
}
