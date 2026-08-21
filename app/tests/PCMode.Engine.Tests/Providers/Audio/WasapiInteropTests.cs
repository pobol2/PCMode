using System.Reflection;
using System.Runtime.InteropServices;

namespace PCMode.Engine.Tests;

public sealed class WasapiInteropTests
{
    [Fact]
    public void Keeps_loopback_capture_on_the_ordinary_shared_client()
    {
        int captureFlags =
            Wasapi.StreamFlagsLoopback |
            Wasapi.StreamFlagsEventCallback |
            Wasapi.StreamFlagsNoPersist;

        Assert.False(Wasapi.CanUseLowLatencySharedStream(captureFlags));
    }

    [Fact]
    public void Gives_the_low_latency_render_client_only_supported_flags()
    {
        int renderFlags = Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsNoPersist;

        Assert.True(Wasapi.CanUseLowLatencySharedStream(renderFlags));
        Assert.Equal(
            Wasapi.StreamFlagsEventCallback,
            Wasapi.LowLatencySharedStreamFlags(renderFlags));
    }

    [Fact]
    public void Preserves_hresult_returns_so_fallbacks_can_inspect_them()
    {
        Type[] interfaces =
        [
            typeof(Wasapi.IMMDeviceEnumerator),
            typeof(Wasapi.IMMDevice),
            typeof(Wasapi.IAudioClient),
            typeof(Wasapi.IAudioClient3),
            typeof(Wasapi.IAudioCaptureClient),
            typeof(Wasapi.IAudioRenderClient),
            typeof(Wasapi.IAudioEndpointVolume),
        ];

        string[] missing =
        [
            .. interfaces
                .SelectMany(type => type.GetMethods())
                .Where(method =>
                    !method.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig))
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Empty(missing);
    }
}
