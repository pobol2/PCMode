namespace PCMode.Engine;

/// <summary>
/// Builds the discoverers a real machine inspects with.
/// </summary>
/// <remarks>
/// The discovery counterpart to <see cref="EngineBootstrap"/>: one place where
/// the shipped set of categories is written down, so every caller that wants
/// the real thing -- the tray, the setup wizard, the diagnostics view -- gets
/// it built the same way, and a test that wants fakes builds its own list
/// instead of going anywhere near this method.
/// </remarks>
public static class HardwareDiscoveryBootstrap
{
    /// <summary>
    /// The discoverers the shipped application inspects with.
    /// </summary>
    /// <param name="engine">
    /// The running engine, when there is one, so that the applications a
    /// machine's own configuration names can be included. Without it there are
    /// simply no application entries, which is the right answer for a caller
    /// that has not built an engine at all -- a setup wizard running before any
    /// configuration exists, for instance.
    /// </param>
    /// <param name="controllers">
    /// The XInput source to use, when a caller wants to own its lifetime -- it
    /// holds a loaded library handle for as long as it lives. Without one, a
    /// fresh source is loaded and left for the process to reclaim on exit,
    /// which is the right answer for a caller that only ever asks once.
    /// </param>
    public static IReadOnlyList<IHardwareDiscoverer> ProductionDiscoverers(
        ModeEngine? engine = null,
        IGamepadCapabilitiesSource? controllers = null) =>
    [
        // The engine's own DisplayProvider/AudioProvider, when there is one, so
        // that discovery sees exactly the aliases a transition would resolve.
        // Without an engine -- no configuration built yet -- a fresh, unconfigured
        // provider still answers, it simply has no aliases to report.
        new DisplayDiscoverer(engine?.Providers.OfType<DisplayProvider>().FirstOrDefault() ?? new DisplayProvider()),
        new AudioOutputDiscoverer(engine?.Providers.OfType<AudioProvider>().FirstOrDefault() ?? new AudioProvider()),
        new ControllerDiscoverer(controllers ?? XInputCapabilitiesSource.Load()),
        new ApplicationDiscoverer(engine?.Providers ?? []),
    ];
}
