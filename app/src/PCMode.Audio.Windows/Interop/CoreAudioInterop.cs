using PCMode.Audio.Core.Interop;

namespace PCMode.Audio.Windows.Interop;

/// <summary>
/// The endpoint-naming and enumeration types live in
/// <see cref="PCMode.Audio.Core.Interop"/> now, shared with the engine's own
/// device picker. This file keeps only what is specific to actually opening a
/// stream: <see cref="WasapiError"/> and the streaming interfaces declared
/// alongside it in WasapiInterop.cs.
/// </summary>
internal static class CoreAudioInterop
{
    public static IMMDeviceEnumerator CreateEnumerator() => CoreAudioEndpointInterop.CreateEnumerator();

    public static string GetDeviceId(IMMDevice device) => CoreAudioEndpointInterop.GetDeviceId(device);

    public static string GetFriendlyName(IMMDevice device)
    {
        string name = CoreAudioEndpointInterop.GetFriendlyName(device);

        return string.IsNullOrEmpty(name) ? "(Unnamed audio endpoint)" : name;
    }

    public static void Release(object? value) => CoreAudioEndpointInterop.Release(value);
}
