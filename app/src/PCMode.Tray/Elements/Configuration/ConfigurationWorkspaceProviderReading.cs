using PCMode.Engine;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The provider-reading helpers every configuration ViewModel and section
/// needs, extracted from <c>ConfigurationPageViewModelBase</c> so a section
/// (which does not derive from it -- see <c>IConfigurationSection</c>) gets
/// the same "build providers fresh from disk, report a problem instead of
/// throwing" contract without a second copy of the try/catch.
/// </summary>
public static class ConfigurationWorkspaceProviderReading
{
    /// <summary>
    /// The configured providers of one kind, built fresh from the file -- the
    /// same providers a real startup would build. Empty with a problem
    /// message when the configuration itself could not be read.
    /// </summary>
    public static (IReadOnlyList<TProvider> Providers, string? Problem) ReadProviders<TProvider>(
        this IConfigurationWorkspace workspace)
        where TProvider : IModeResourceProvider
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            return ([.. workspace.ReadProviders().OfType<TProvider>()], null);
        }
        catch (ModeConfigurationException exception)
        {
            return ([], $"The configuration could not be read. {exception.Message}");
        }
    }

    /// <summary>The single configured provider of one kind, when a caller has at most one.</summary>
    public static (TProvider? Provider, string? Problem) ReadProvider<TProvider>(this IConfigurationWorkspace workspace)
        where TProvider : IModeResourceProvider
    {
        (IReadOnlyList<TProvider> providers, string? problem) = workspace.ReadProviders<TProvider>();
        return (providers.FirstOrDefault(), problem);
    }
}
