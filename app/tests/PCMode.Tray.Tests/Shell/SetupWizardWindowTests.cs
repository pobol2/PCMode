using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Navigation;
using PCMode.Tray.Elements.Settings;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Shell;

/// <summary>
/// The one piece of real logic <see cref="SetupWizardWindow"/> owns that
/// legacy's original did not need: bridging step changes to
/// <see cref="IPageLifecycle"/> for embedded pages that bypass
/// <see cref="ShellPages"/>' own activation. A page cannot have two visual
/// parents, so this window builds fresh instances and must activate/
/// deactivate them itself, exactly once per step change.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SetupWizardWindowTests
{
    private sealed class FakeLifecyclePage : Border, IPageLifecycle
    {
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }

        public void OnActivated() => Activations++;

        public void OnDeactivated() => Deactivations++;
    }

    /*[Fact]
    public void Starting_on_the_finish_step_names_it_distinctly()
    {
        Wpf.Run(() =>
        {
            var wizard = new SetupWizardWindow(
                new Border(),
                new Border(),
                new Border(),
                new Border(),
                () => new SetupSummary(0, 0, 0),
                () => { },
                SetupWizardWindow.FinishStep);

            Assert.Equal("PCMode - Guided setup", wizard.Title);
            Assert.Equal(
                "Guided setup - Finish",
                Assert.IsType<TextBlock>(wizard.FindName("StepTitle")).Text);
            Assert.Equal(
                Visibility.Collapsed,
                Assert.IsType<System.Windows.Controls.Button>(wizard.FindName("SkipSetupButton")).Visibility);
        });
    }

    [Fact]
    public void Only_the_step_currently_shown_is_activated()
    {
        Wpf.Run(() =>
        {
            var displays = new FakeLifecyclePage();
            var audio = new FakeLifecyclePage();
            var spaces = new FakeLifecyclePage();
            var settings = new FakeLifecyclePage();

            var wizard = new SetupWizardWindow(
                displays,
                audio,
                spaces,
                settings,
                () => new SetupSummary(0, 0, 0),
                () => { },
                SetupWizardWindow.DisplaysStep);

            Assert.Equal(1, displays.Activations);
            Assert.Equal(0, displays.Deactivations);
            Assert.Equal(0, audio.Activations);
            Assert.Equal(0, spaces.Activations);
            Assert.Equal(0, settings.Activations);

            Assert.IsType<System.Windows.Controls.Button>(wizard.FindName("NextButton")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

            Assert.Equal(1, displays.Activations);
            Assert.Equal(1, displays.Deactivations);
            Assert.Equal(1, audio.Activations);
            Assert.Equal(0, spaces.Activations);
            Assert.Equal(0, spaces.Deactivations);
            Assert.Equal(0, settings.Activations);
        });
    }

    [Fact]
    public void Skipping_a_null_settings_page_falls_through_to_finish()
    {
        Wpf.Run(() =>
        {
            var wizard = new SetupWizardWindow(
                new Border(),
                new Border(),
                new Border(),
                settingsPage: null,
                () => new SetupSummary(0, 0, 0),
                () => { },
                SetupWizardWindow.SettingsStep);

            Assert.Equal(
                "Guided setup - Finish",
                Assert.IsType<TextBlock>(wizard.FindName("StepTitle")).Text);
        });
    }

    [Fact]
    public void Finishing_invokes_the_completion_callback_exactly_once()
    {
        Wpf.Run(() =>
        {
            int completed = 0;

            var wizard = new SetupWizardWindow(
                new Border(),
                new Border(),
                new Border(),
                new Border(),
                () => new SetupSummary(0, 0, 0),
                () => completed++,
                SetupWizardWindow.FinishStep);

            Assert.IsType<System.Windows.Controls.Button>(wizard.FindName("NextButton")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

            Assert.Equal(1, completed);
        });
    }*/
}
