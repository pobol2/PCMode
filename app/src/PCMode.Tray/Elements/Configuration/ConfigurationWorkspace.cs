using PCMode.Engine;
using PCMode.Tray.Platform.Logging;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>Provides configuration pages with the configured engine providers.</summary>
public interface IConfigurationWorkspace
{
    /// <summary>The data root, or null to use the engine default.</summary>
    string? Root { get; }

    /// <summary>Reads the configuration and returns providers configured from it.</summary>
    IReadOnlyList<IModeResourceProvider> ReadProviders();

    /// <summary>
    /// Reads the configuration document itself alongside its providers. Pages
    /// that only need providers should prefer <see cref="ReadProviders"/>; this
    /// is for pages -- Spaces above all -- that also need the document's own
    /// structure, such as its Space/Activity/Source hierarchy.
    /// </summary>
    (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers) Read();

    /// <summary>
    /// Makes a saved change on disk the running configuration, and returns what
    /// to tell the user about it.
    /// </summary>
    /// <param name="saved">What the page would say if activation went perfectly.</param>
    /// <remarks>
    /// Reads as success when nothing is running to activate -- the caller's
    /// change is on disk, and nothing was running that could disagree with it --
    /// and appends the reason otherwise, without throwing either way.
    /// </remarks>
    string Apply(string saved);

    /// <summary>Records something that went as intended.</summary>
    void Note(string category, string message);

    /// <summary>Records something the user has been shown as a problem.</summary>
    void Warn(string category, string message);

    /// <summary>Records a failure, with the exception behind it.</summary>
    void Fault(string category, string message, Exception exception);
}

/// <summary>The engine configuration available to the presentation layer.</summary>
public sealed class ConfigurationWorkspace : IConfigurationWorkspace
{
    private readonly LocalLog? _log;
    private readonly IAudioRuntime? _audio;
    private readonly Func<RuntimeActivationOutcome>? _activate;

    private ConfigurationWorkspace(
        string? root,
        LocalLog? log,
        IAudioRuntime? audio,
        Func<RuntimeActivationOutcome>? activate)
    {
        Root = root;
        _log = log;
        _audio = audio;
        _activate = activate;
    }

    public string? Root { get; }

    /// <param name="root">Null for the default data root.</param>
    /// <param name="log">Null to discard what a page reports.</param>
    /// <param name="audio">
    /// The one <see cref="AudioHostLink"/> every <c>AudioProvider</c> this
    /// workspace builds shares, so an audio target started through one call is
    /// the target every later call sees. Null when nothing needs it -- the
    /// providers still work, they simply cannot start a Host or report on one.
    /// </param>
    /// <param name="activate">
    /// Null when nothing is running to activate into -- a test, or a page built
    /// before the runtime exists. Saving still writes; it simply does not take
    /// effect until something else reloads.
    /// </param>
    public static ConfigurationWorkspace For(
        string? root = null,
        LocalLog? log = null,
        IAudioRuntime? audio = null,
        Func<RuntimeActivationOutcome>? activate = null) => new(root, log, audio, activate);

    public IReadOnlyList<IModeResourceProvider> ReadProviders() => Read().Providers;

    public (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers) Read()
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(Root ?? PCModePaths.DefaultRoot);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(Root, json, _audio);

        return (ModeConfiguration.Parse(json, providers), providers);
    }

    public string Apply(string saved)
    {
        RuntimeActivationOutcome? outcome = _activate?.Invoke();

        string message = outcome is null or { Activated: true }
            ? saved
            : $"{saved} {outcome.Message}";

        _log?.Information("Configuration", message);

        return message;
    }

    public void Note(string category, string message) => _log?.Information(category, message);

    public void Warn(string category, string message) => _log?.Warning(category, message);

    public void Fault(string category, string message, Exception exception) =>
        _log?.Error(category, message, exception);
}
