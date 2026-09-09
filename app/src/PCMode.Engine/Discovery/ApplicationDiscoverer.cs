namespace PCMode.Engine;

/// <summary>
/// The applications this machine's own configuration knows about, and whether
/// each looks installed and running.
/// </summary>
/// <remarks>
/// "RELEVANT TO PCMODE" MEANS "SOMEBODY CONFIGURED IT"
///
/// There is no way to discover an arbitrary application PCMode might one day
/// want without inventing a catalogue of well-known launchers, which is a
/// feature this version deliberately does not have. What this can do instead is
/// tell someone, about the applications their own modes already name, which of
/// them this machine can actually see -- reusing <see cref="ApplicationProvider"/>
/// rather than re-implementing "is this installed" a second time.
///
/// A machine with no applications configured yet reports none, which is the
/// ordinary state of a fresh configuration and not a failure.
/// </remarks>
public sealed class ApplicationDiscoverer(IReadOnlyList<IModeResourceProvider> providers) : IHardwareDiscoverer
{
    private readonly IReadOnlyList<IModeResourceProvider> _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));

    public string Category => "Applications";

    public IReadOnlyList<HardwareItem> Discover()
    {
        var items = new List<HardwareItem>();

        foreach (IModeResourceProvider provider in _providers)
        {
            if (provider is not ApplicationProvider application)
            {
                continue;
            }

            items.Add(Describe(application));
        }

        return items;
    }

    /// <summary>
    /// One application's line, built from the same reads a transition would
    /// make -- never from a second, parallel notion of "installed".
    /// </summary>
    private static HardwareItem Describe(ApplicationProvider application)
    {
        ApplicationDefinition definition = application.Definition;

        List<HardwareDetail> details =
        [
            new("Process name", definition.ProcessName),
            new("Start command", definition.Start.Describe()),
        ];

        ResourceAvailability availability = SafeAvailability(application);

        if (availability.State == AvailabilityState.Unavailable)
        {
            return new HardwareItem(
                definition.Name,
                HardwareItemStatus.NotDetected,
                Capitalize(availability.Explain()),
                details
            );
        }

        if (availability.State == AvailabilityState.Unknown)
        {
            return new HardwareItem(
                definition.Name,
                HardwareItemStatus.Error,
                Capitalize(availability.Explain()),
                details
            );
        }

        string? running = SafeRead(application);

        string summary = running switch
        {
            ApplicationCatalog.Running => "Running.",
            ApplicationCatalog.Stopped => "Installed, not running.",
            _ => "Installed.",
        };

        return new HardwareItem(definition.Name, HardwareItemStatus.Ok, summary, details);
    }

    /// <summary>
    /// Whether it is installed, the same question a transition would ask before
    /// starting it. A read that fails is reported rather than hidden: only
    /// <see cref="ApplicationProvider.AvailabilityOf"/> itself decides what
    /// "could not be checked" means.
    /// </summary>
    private static ResourceAvailability SafeAvailability(ApplicationProvider application)
    {
        try
        {
            return application.AvailabilityOf(ApplicationCatalog.Running);
        }
        catch (Exception exception)
        {
            return ResourceAvailability.Unknown(exception.Message);
        }
    }

    private static string? SafeRead(ApplicationProvider application)
    {
        try
        {
            return application.Read();
        }
        catch (Exception)
        {
            // Read() already turns its own failures into null; this is only a
            // backstop against something unexpected reaching all the way here.
            return null;
        }
    }

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
