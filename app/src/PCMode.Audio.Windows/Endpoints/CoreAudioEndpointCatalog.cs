using System.Runtime.InteropServices;
using PCMode.Audio.Core.Interop;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Windows.Formats;
using PCMode.Audio.Windows.Interop;

namespace PCMode.Audio.Windows.Endpoints;

public sealed class CoreAudioEndpointCatalog
{
    public IReadOnlyList<CoreAudioEndpointSnapshot> GetActiveRenderEndpoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var result = new List<CoreAudioEndpointSnapshot>();

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;

        try
        {
            enumerator = CoreAudioInterop.CreateEnumerator();

            WasapiError.ThrowIfFailed(
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out devices),
                "IMMDeviceEnumerator.EnumAudioEndpoints"
            );

            WasapiError.ThrowIfFailed(devices!.GetCount(out uint count), "IMMDeviceCollection.GetCount");

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IAudioClient? audioClient = null;
                IntPtr formatPointer = IntPtr.Zero;

                try
                {
                    WasapiError.ThrowIfFailed(devices!.Item(i, out device), "IMMDeviceCollection.Item");

                    string id = CoreAudioInterop.GetDeviceId(device!);

                    string name = CoreAudioInterop.GetFriendlyName(device!);

                    Guid audioClientId = typeof(IAudioClient).GUID;

                    WasapiError.ThrowIfFailed(
                        device!.Activate(ref audioClientId, ClsCtx.All, IntPtr.Zero, out object? clientObject),
                        "IMMDevice.Activate(IAudioClient)"
                    );

                    audioClient = (IAudioClient)clientObject!;

                    WasapiError.ThrowIfFailed(audioClient.GetMixFormat(out formatPointer), "IAudioClient.GetMixFormat");

                    WaveFormatInfo native = WaveFormatAdapter.Read(formatPointer);

                    AudioFormat format = WaveFormatAdapter.ToAudioFormat(native);

                    result.Add(new CoreAudioEndpointSnapshot(new AudioEndpointId(id), name, format, native));
                }
                catch (NotSupportedException)
                {
                    // "Usable" means usable by this first
                    // mono/stereo, no-resampler implementation.
                }
                finally
                {
                    if (formatPointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(formatPointer);
                    }

                    CoreAudioInterop.Release(audioClient);

                    CoreAudioInterop.Release(device);
                }
            }
        }
        finally
        {
            CoreAudioInterop.Release(devices);
            CoreAudioInterop.Release(enumerator);
        }

        return result;
    }
}
