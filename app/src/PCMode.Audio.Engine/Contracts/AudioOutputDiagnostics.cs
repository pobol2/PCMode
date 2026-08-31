namespace PCMode.Audio.Engine.Contracts;

/// <summary>
/// What an output stream observed about its own contact with the device,
/// as opposed to what the route above it believes happened. The two can
/// disagree, and every failed attempt at the display-topology waveform-jump
/// bug so far has come from guessing which one was right: a route can see a
/// long gap between its own <c>Process</c> calls without the endpoint ever
/// running dry, and an endpoint can run dry without any single call the
/// route makes taking noticeably long. These are the output stream's own
/// side of that question.
/// </summary>
/// <param name="RecoveryFadeCount">
/// How many times the stream armed its recovery fade -- i.e. concluded that
/// what it was about to submit was the first audio after a break and needed
/// ramping in rather than being handed over at full amplitude.
/// </param>
/// <param name="EndpointDryCount">
/// How many times the endpoint was found with zero frames queued: every
/// frame ever submitted already consumed, so the device was being fed
/// silence in place of audio. Unlike any wall-clock measurement this needs
/// no threshold -- it is WASAPI reporting the discontinuity rather than this
/// code inferring one.
/// </param>
/// <param name="LongestSubmitGapMilliseconds">
/// The longest wall-clock gap ever seen between one chunk reaching the
/// device and the next, whether or not it was long enough to arm anything.
/// Diagnostic only: it is what says whether a stall was tens or hundreds of
/// milliseconds, which is exactly what the earlier fixed thresholds were
/// guessing at.
/// </param>
/// <param name="LongestWriteMilliseconds">
/// The longest a single <c>Write</c> call took end to end. Read against
/// <paramref name="LongestSubmitGapMilliseconds"/> this splits a stall in
/// two: if the two are close, the caller was blocked inside the write itself
/// and the endpoint starved while it waited there; if the write was quick
/// but the gap was long, the caller never called at all for that stretch and
/// the audio went missing somewhere upstream of this stream entirely.
/// </param>
/// <param name="LongestBufferWaitMilliseconds">
/// The longest single wait for the endpoint to report room for more audio.
/// Splits a slow write in two once more: a wait this long means the audio
/// engine stopped asking for data and then resumed, so the device was
/// suspended by something outside this process; a long write with a short
/// wait means one of the interop calls themselves blocked, which is the
/// device being reset underneath a live stream rather than merely paused.
/// </param>
public readonly record struct AudioOutputDiagnostics(
    long RecoveryFadeCount,
    long EndpointDryCount,
    double LongestSubmitGapMilliseconds,
    double LongestWriteMilliseconds,
    double LongestBufferWaitMilliseconds);
