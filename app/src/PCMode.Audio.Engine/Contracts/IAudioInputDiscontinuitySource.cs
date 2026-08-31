namespace PCMode.Audio.Engine.Contracts;

/// <summary>
/// Exposes changes in an input stream's source timeline without coupling
/// the audio engine to a particular transport.
/// </summary>
public interface IAudioInputDiscontinuitySource
{
    long DiscontinuityGeneration { get; }
}
