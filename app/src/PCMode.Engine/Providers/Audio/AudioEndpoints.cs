namespace PCMode.Engine;

/// <summary>One playback endpoint Windows currently has.</summary>
/// <param name="Id">
/// The Core Audio endpoint ID. Used within a single call and never stored: see
/// <see cref="IAudioEndpoints"/> for why.
/// </param>
/// <param name="Name">
/// The friendly name Windows shows in Sound settings, for example
/// "Speakers (Realtek(R) Audio)".
/// </param>
public sealed record AudioEndpoint(string Id, string Name);

/// <summary>
/// Windows Core Audio, behind a seam so tests never change the real default.
/// </summary>
/// <remarks>
/// WHY A SEAM
///
/// The alternative is a test suite that switches the default playback device of
/// whatever machine it runs on. Every case worth testing here -- a name that
/// matches nothing, a name that matches twice, Core Audio not answering at all --
/// is one a real machine will not produce on request, so the interesting rules
/// would be the untested ones.
///
/// NOTHING IS CACHED ACROSS CALLS
///
/// Every method enumerates afresh. An endpoint ID is not guaranteed stable, and
/// the HDMI endpoint in particular exists only while the television is awake and
/// driving the display: it disappears and comes back as the set is power-cycled.
/// A resolved ID kept from an earlier call would be a handle to a device that may
/// no longer be there, which is why configuration names devices and this
/// interface resolves them again every time.
/// </remarks>
public interface IAudioEndpoints
{
    /// <summary>
    /// Every playback endpoint that is active right now.
    /// </summary>
    /// <remarks>
    /// Active only. A disabled, unplugged or not-present endpoint is not
    /// something a mode should be able to select, and offering one would produce
    /// a transition that appeared to succeed and changed nothing audible.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Core Audio could not be reached.</exception>
    IReadOnlyList<AudioEndpoint> ActivePlayback();

    /// <summary>
    /// The endpoint sound comes out of now, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Null is an ordinary answer rather than a failure: a machine with no
    /// active playback device has no default, which is unusual but legitimate.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Core Audio could not be reached.</exception>
    AudioEndpoint? DefaultPlayback();

    /// <summary>Makes that endpoint the default. Throws on failure.</summary>
    void MakeDefault(string endpointId);

    /// <summary>
    /// That endpoint's master volume, 0 to 1, or null when it has none.
    /// </summary>
    /// <remarks>
    /// The slider in Sound settings, not an application's own level. An audio
    /// target needs it because balancing a soundbar against a pair of speakers is
    /// partly done on the endpoints themselves: a route's own gain changes only
    /// what that route sends, and cannot move a device somebody has left at a
    /// level of their own.
    ///
    /// Null rather than an exception for an endpoint with no volume control,
    /// which some HDMI and virtual devices genuinely are.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Core Audio could not be reached.</exception>
    double? ReadVolume(string endpointId);

    /// <summary>Sets that endpoint's master volume, 0 to 1. Throws on failure.</summary>
    void SetVolume(string endpointId, double level);
}
