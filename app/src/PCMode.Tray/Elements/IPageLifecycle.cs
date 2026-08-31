namespace PCMode.Tray.Elements;

/// <summary>
/// What a page's view model wants to be told about becoming, and ceasing to
/// be, the page on screen.
/// </summary>
/// <remarks>
/// Implemented by view models rather than by controls. A page is a block tree
/// built from a view model by an implicit template, so there is no page class
/// left to hang this on -- and the things it drives (load once on first open,
/// poll live machine state only while visible, stop polling when hidden) were
/// always view-model concerns that only lived in a control because that is
/// where WPF happened to put them.
///
/// Declared here rather than beside the navigation that raises it because both
/// sides depend on it: the shell calls these, and feature view models
/// implement them. Putting it in the shell would make <c>Features</c> depend
/// upward on <c>Shell</c> for a two-method contract that mentions no shell.
/// </remarks>
public interface IPageLifecycle
{
    /// <summary>This page has become the one on screen.</summary>
    void OnActivated();

    /// <summary>Another page has taken over.</summary>
    void OnDeactivated();
}
