namespace PCMode.Engine.Tests;

/// <summary>
/// A machine's playback devices, made up.
/// </summary>
/// <remarks>
/// The reason <see cref="IAudioEndpoints"/> is an interface. Two devices sharing
/// a name, a television that is switched off, Core Audio not answering at all --
/// none of those can be arranged on the machine running the tests, and all of
/// them are the cases where the rules actually matter.
/// </remarks>
internal sealed class FakeAudioEndpoints : IAudioEndpoints
{
    private readonly List<AudioEndpoint> _active = [];

    public FakeAudioEndpoints(params string[] names)
    {
        foreach (string name in names)
        {
            Add(name);
        }
    }

    /// <summary>Thrown from every call, when set.</summary>
    public Exception? Fails { get; set; }

    /// <summary>The ID of the current default, or null for none.</summary>
    public string? Default { get; set; }

    /// <summary>Every ID handed to MakeDefault, in order.</summary>
    public List<string> MadeDefault { get; } = [];

    /// <summary>Each endpoint's volume, by ID, for the ones that have one.</summary>
    public Dictionary<string, double> Volumes { get; } = [];

    /// <summary>IDs with no volume control at all, like some HDMI endpoints.</summary>
    public HashSet<string> WithoutVolume { get; } = [];

    /// <summary>
    /// Refuses to move the default, without failing everything else.
    /// </summary>
    /// <remarks>
    /// What a machine looks like when the undocumented policy interface accepts
    /// the call and Windows keeps the default where it was -- which is the one
    /// failure Party mode must not build a mirror on top of.
    /// </remarks>
    public bool IgnoresMakeDefault { get; set; }

    /// <summary>Adds an active device. Names may repeat, which is the point.</summary>
    public FakeAudioEndpoints Add(string name)
    {
        _active.Add(new AudioEndpoint($"id-{_active.Count}", name));
        return this;
    }

    /// <summary>The ID the nth added device got.</summary>
    public string IdOf(int index) => _active[index].Id;

    public IReadOnlyList<AudioEndpoint> ActivePlayback() => Fails is null ? _active : throw Fails;

    public AudioEndpoint? DefaultPlayback() => Fails is not null
        ? throw Fails
        : _active.FirstOrDefault(endpoint => endpoint.Id == Default);

    public void MakeDefault(string endpointId)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        MadeDefault.Add(endpointId);

        if (!IgnoresMakeDefault)
        {
            Default = endpointId;
        }
    }

    public double? ReadVolume(string endpointId) => Fails is not null
        ? throw Fails
        : WithoutVolume.Contains(endpointId) ? null : Volumes.GetValueOrDefault(endpointId, 0.5);

    public void SetVolume(string endpointId, double level)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        if (WithoutVolume.Contains(endpointId))
        {
            throw new InvalidOperationException($"'{endpointId}' has no volume control.");
        }

        Volumes[endpointId] = level;
    }
}
