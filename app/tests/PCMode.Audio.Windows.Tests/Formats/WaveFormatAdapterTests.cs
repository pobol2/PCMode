using System.Runtime.InteropServices;
using PCMode.Audio.Windows.Formats;
using Xunit;

namespace PCMode.Audio.Windows.Tests.Formats;

public sealed class WaveFormatAdapterTests
{
    [Fact]
    public void Float32_format_maps_to_core_format()
    {
        var native = new WaveFormatInfo(
            NativeSampleFormat.Float32,
            SampleRate: 48_000,
            ChannelCount: 2,
            BitsPerSample: 32,
            ValidBitsPerSample: 32,
            BlockAlign: 8,
            ChannelMask: 3
        );

        var format = WaveFormatAdapter.ToAudioFormat(native);

        Assert.Equal(48_000, format.SampleRate);

        Assert.Equal(2, format.ChannelCount);
    }

    [Fact]
    public void Pcm16_converts_to_float()
    {
        short[] native = [short.MinValue, -16384, 16384, short.MaxValue];

        IntPtr buffer = Marshal.AllocHGlobal(native.Length * sizeof(short));

        try
        {
            Marshal.Copy(native, 0, buffer, native.Length);

            float[] samples = new float[4];

            var format = new WaveFormatInfo(NativeSampleFormat.Pcm16, 48_000, 2, 16, 16, 4, 3);

            WaveFormatAdapter.NativeToFloat(buffer, samples, frameCount: 2, format);

            Assert.Equal(-1.0f, samples[0]);

            Assert.InRange(samples[1], -0.5001f, -0.4999f);

            Assert.InRange(samples[2], 0.4999f, 0.5001f);

            Assert.InRange(samples[3], 0.9998f, 1.0f);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Float_converts_to_pcm16()
    {
        float[] samples = [-1.0f, -0.5f, 0.5f, 1.0f];

        IntPtr buffer = Marshal.AllocHGlobal(samples.Length * sizeof(short));

        try
        {
            var format = new WaveFormatInfo(NativeSampleFormat.Pcm16, 48_000, 2, 16, 16, 4, 3);

            WaveFormatAdapter.FloatToNative(samples, buffer, frameCount: 2, format);

            short[] result = new short[4];

            Marshal.Copy(buffer, result, 0, result.Length);

            Assert.Equal(short.MinValue, result[0]);

            Assert.InRange(result[1], -16385, -16383);

            Assert.InRange(result[2], 16383, 16385);

            Assert.Equal(short.MaxValue, result[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Surround_format_is_refused_for_initial_path()
    {
        var native = new WaveFormatInfo(NativeSampleFormat.Float32, 48_000, 6, 32, 32, 24, 0x3F);

        Assert.Throws<NotSupportedException>(() => WaveFormatAdapter.ToAudioFormat(native));
    }
}
