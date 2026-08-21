namespace PCMode.Engine.Tests;

/// <summary>
/// A resource that records what was done to it and can be told to misbehave.
/// </summary>
/// <remarks>
/// Every case worth testing -- a provider that throws, one that cannot read
/// itself, one that blocks -- is a case a real machine will not produce on
/// request. That is the whole reason the provider interface is an interface.
/// </remarks>
internal sealed class FakeProvider(string key, string? current = null) :
    IDependentProvider, IAvailableProvider, IResourceObservabilityProvider
{
    private readonly List<string> _applied = [];

    public string Key { get; } = key;

    public string DisplayName { get; init; } = key;

    /// <summary>
    /// Whether this resource has a trustworthy runtime signal. True by default,
    /// which keeps every existing test's behavior unchanged; set false to make
    /// this fake behave like a launch-only application.
    /// </summary>
    public bool Observable { get; init; } = true;

    public bool CanObserve(string target) => Observable;

    /// <summary>
    /// What each target needs, keyed by target.
    /// </summary>
    /// <remarks>
    /// Keyed by target rather than flat, because that is the distinction the
    /// planner exists to keep: one value of a resource can need the television
    /// on while another needs nothing at all.
    /// </remarks>
    public IReadOnlyDictionary<string, ResourceRequirement[]>? Requires { get; init; }

    /// <summary>Resource keys this runs after when they are in the same mode.</summary>
    public IReadOnlyList<string> RunsAfter { get; init; } = [];

    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        Requires is not null && Requires.TryGetValue(target, out ResourceRequirement[]? required)
            ? required
            : [];

    /// <summary>What Read reports. Null stands for "cannot tell".</summary>
    public string? Current { get; set; } = current;

    /// <summary>Values this provider accepts. Null accepts anything non-empty.</summary>
    public IReadOnlyCollection<string>? Allowed { get; init; }

    /// <summary>Thrown from Apply, when set.</summary>
    public Exception? Fails { get; set; }

    /// <summary>Held open at the start of Apply, when set.</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>Set when Read throws rather than answering.</summary>
    public bool ReadThrows { get; init; }

    /// <summary>What <see cref="AvailabilityOf"/> reports. Null stands for "available".</summary>
    public ResourceAvailability? Unavailable { get; set; }

    public ResourceAvailability AvailabilityOf(string target) => Unavailable ?? ResourceAvailability.Available;

    /// <summary>Every value handed to Apply, in order.</summary>
    public IReadOnlyList<string> Applied => _applied;

    public bool IsValidValue(string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = $"{Key} needs a value.";
            return false;
        }

        if (Allowed is not null && !Allowed.Contains(value))
        {
            problem = $"'{value}' is not one of {string.Join(", ", Allowed)}.";
            return false;
        }

        problem = null;
        return true;
    }

    public string? Read() => ReadThrows
        ? throw new InvalidOperationException($"{Key} cannot be read.")
        : Current;

    public void Apply(string value, CancellationToken cancellationToken)
    {
        Gate?.Task.GetAwaiter().GetResult();

        if (Fails is not null)
        {
            throw Fails;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _applied.Add(value);

        // An unobservable fake mirrors ApplicationProvider's real launch-only
        // behavior: it never claims a readback state, so Read() keeps
        // returning null and every activation dispatches again.
        if (Observable)
        {
            Current = value;
        }
    }
}

/// <summary>A folder that goes away with the test.</summary>
internal sealed class TemporaryRoot : IDisposable
{
    public TemporaryRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PCMode.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>A folder with a document already written, for the stores that need one on disk before construction.</summary>
    public TemporaryRoot(string document)
        : this()
    {
        Write(document);
    }

    public string Path { get; }

    public string ConfigurationFile => PCModePaths.ConfigurationFile(Path);

    public string StateFile => PCModePaths.StateFile(Path);

    public void Write(string json) => File.WriteAllText(ConfigurationFile, json);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }
}
