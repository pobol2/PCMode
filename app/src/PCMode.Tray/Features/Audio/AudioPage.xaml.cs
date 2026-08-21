using System.Globalization;
using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Audio;

public partial class AudioPage : ConfigurationPageShell
{
    public AudioPage(AudioViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioViewModel viewModel)
        {
            await viewModel.ActivateSelectedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Moves the delay by a fixed step, which is how two outputs are actually
    /// aligned: a slider that spans two seconds cannot be nudged by a
    /// millisecond, and a millisecond at a time is what the ear needs near the
    /// answer.
    /// </summary>
    private void OnNudgeDelayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioViewModel viewModel ||
            sender is not FrameworkElement { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int step))
        {
            return;
        }

        viewModel.DelayMs += step;
    }
}
