using System.Runtime.InteropServices;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Windows.Interop;

namespace PCMode.Audio.Windows.Formats;

public enum NativeSampleFormat
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32
}

public readonly record struct WaveFormatInfo(
    NativeSampleFormat SampleFormat,
    int SampleRate,
    int ChannelCount,
    int BitsPerSample,
    int ValidBitsPerSample,
    int BlockAlign,
    uint ChannelMask);

public static class WaveFormatAdapter
{
    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatIeeeFloat = 0x0003;
    private const ushort WaveFormatExtensibleTag = 0xFFFE;

    private static readonly Guid PcmSubFormat =
        new("00000001-0000-0010-8000-00AA00389B71");

    private static readonly Guid FloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    public static WaveFormatInfo Read(
        IntPtr formatPointer)
    {
        if (formatPointer == IntPtr.Zero)
        {
            throw new ArgumentNullException(
                nameof(formatPointer));
        }

        WaveFormatEx format =
            Marshal.PtrToStructure<WaveFormatEx>(
                formatPointer);

        int validBits = format.BitsPerSample;
        uint channelMask = 0;
        Guid? subFormat = null;

        if (format.FormatTag ==
            WaveFormatExtensibleTag)
        {
            if (format.ExtraSize < 22)
            {
                throw new NotSupportedException(
                    "Invalid WAVEFORMATEXTENSIBLE structure.");
            }

            WaveFormatExtensible extensible =
                Marshal.PtrToStructure<WaveFormatExtensible>(
                    formatPointer);

            validBits =
                extensible.ValidBitsPerSample == 0
                    ? format.BitsPerSample
                    : extensible.ValidBitsPerSample;

            channelMask = extensible.ChannelMask;
            subFormat = extensible.SubFormat;
        }

        NativeSampleFormat sampleFormat =
            ResolveSampleFormat(
                format.FormatTag,
                subFormat,
                format.BitsPerSample);

        var result =
            new WaveFormatInfo(
                sampleFormat,
                checked((int)format.SamplesPerSecond),
                format.Channels,
                format.BitsPerSample,
                validBits,
                format.BlockAlign,
                channelMask);

        Validate(result);

        return result;
    }

    public static AudioFormat ToAudioFormat(
        WaveFormatInfo format)
    {
        Validate(format);

        if (format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                "The initial WASAPI path supports only mono or stereo endpoints. " +
                "Channel mapping is intentionally not implemented yet.");
        }

        return new AudioFormat(
            format.SampleRate,
            format.ChannelCount);
    }

    internal static IntPtr AllocateFloat32(
        AudioFormat format)
    {
        if (!format.IsValid)
        {
            throw new ArgumentException(
                "Audio format must be valid.",
                nameof(format));
        }

        if (format.ChannelCount is < 1 or > 2)
        {
            throw new NotSupportedException(
                "The current engine stream supports only mono or stereo canonical audio.");
        }

        const int bytesPerSample =
            sizeof(float);

        ushort blockAlign =
            checked(
                (ushort)(format.ChannelCount *
                         bytesPerSample));

        var native =
            new WaveFormatExtensible
            {
                Format =
                    new WaveFormatEx
                    {
                        FormatTag =
                            WaveFormatExtensibleTag,

                        Channels =
                            checked((ushort)format.ChannelCount),

                        SamplesPerSecond =
                            checked((uint)format.SampleRate),

                        AverageBytesPerSecond =
                            checked(
                                (uint)(format.SampleRate *
                                       blockAlign)),

                        BlockAlign =
                            blockAlign,

                        BitsPerSample =
                            32,

                        ExtraSize =
                            22
                    },

                ValidBitsPerSample =
                    32,

                ChannelMask =
                    format.ChannelCount == 1
                        ? 0x00000004u
                        : 0x00000003u,

                SubFormat =
                    FloatSubFormat
            };

        IntPtr pointer =
            Marshal.AllocCoTaskMem(
                Marshal.SizeOf<
                    WaveFormatExtensible>());

        Marshal.StructureToPtr(
            native,
            pointer,
            false);

        return pointer;
    }

    public static unsafe void NativeToFloat(
        IntPtr source,
        Span<float> destination,
        int frameCount,
        WaveFormatInfo format,
        bool silent = false)
    {
        Validate(format);

        int sampleCount = checked(
            frameCount * format.ChannelCount);

        if (destination.Length < sampleCount)
        {
            throw new ArgumentException(
                "Destination is too small.",
                nameof(destination));
        }

        Span<float> output =
            destination[..sampleCount];

        if (silent)
        {
            output.Clear();
            return;
        }

        if (source == IntPtr.Zero)
        {
            throw new ArgumentNullException(
                nameof(source));
        }

        byte* bytes = (byte*)source;

        for (int i = 0; i < sampleCount; i++)
        {
            output[i] =
                format.SampleFormat switch
                {
                    NativeSampleFormat.Float32 =>
                        ((float*)bytes)[i],

                    NativeSampleFormat.Pcm16 =>
                        ReadPcm16(
                            bytes + i * 2,
                            format.ValidBitsPerSample),

                    NativeSampleFormat.Pcm24 =>
                        ReadPcm24(
                            bytes + i * 3,
                            format.ValidBitsPerSample),

                    NativeSampleFormat.Pcm32 =>
                        ReadPcm32(
                            bytes + i * 4,
                            format.ValidBitsPerSample),

                    _ => throw new NotSupportedException()
                };
        }
    }

    public static unsafe void FloatToNative(
        ReadOnlySpan<float> source,
        IntPtr destination,
        int frameCount,
        WaveFormatInfo format)
    {
        Validate(format);

        int sampleCount = checked(
            frameCount * format.ChannelCount);

        if (source.Length < sampleCount)
        {
            throw new ArgumentException(
                "Source is too small.",
                nameof(source));
        }

        if (destination == IntPtr.Zero)
        {
            throw new ArgumentNullException(
                nameof(destination));
        }

        byte* bytes = (byte*)destination;

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = source[i];

            switch (format.SampleFormat)
            {
                case NativeSampleFormat.Float32:
                    ((float*)bytes)[i] = sample;
                    break;

                case NativeSampleFormat.Pcm16:
                    WritePcm16(
                        bytes + i * 2,
                        sample,
                        format.ValidBitsPerSample);
                    break;

                case NativeSampleFormat.Pcm24:
                    WritePcm24(
                        bytes + i * 3,
                        sample,
                        format.ValidBitsPerSample);
                    break;

                case NativeSampleFormat.Pcm32:
                    WritePcm32(
                        bytes + i * 4,
                        sample,
                        format.ValidBitsPerSample);
                    break;

                default:
                    throw new NotSupportedException();
            }
        }
    }

    private static NativeSampleFormat ResolveSampleFormat(
        ushort formatTag,
        Guid? subFormat,
        int bitsPerSample)
    {
        bool isFloat =
            formatTag == WaveFormatIeeeFloat ||
            (formatTag == WaveFormatExtensibleTag &&
             subFormat == FloatSubFormat);

        if (isFloat && bitsPerSample == 32)
        {
            return NativeSampleFormat.Float32;
        }

        bool isPcm =
            formatTag == WaveFormatPcm ||
            (formatTag == WaveFormatExtensibleTag &&
             subFormat == PcmSubFormat);

        if (!isPcm)
        {
            throw new NotSupportedException(
                $"Unsupported Windows audio format tag 0x{formatTag:X4}.");
        }

        return bitsPerSample switch
        {
            16 => NativeSampleFormat.Pcm16,
            24 => NativeSampleFormat.Pcm24,
            32 => NativeSampleFormat.Pcm32,

            _ => throw new NotSupportedException(
                $"Unsupported PCM sample size: {bitsPerSample} bits.")
        };
    }

    private static void Validate(
        WaveFormatInfo format)
    {
        if (format.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(format));
        }

        if (format.ChannelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(format));
        }

        if (format.ValidBitsPerSample <= 0 ||
            format.ValidBitsPerSample >
            format.BitsPerSample)
        {
            throw new NotSupportedException(
                "Invalid PCM valid-bit count.");
        }

        int expectedBlockAlign =
            checked(
                format.ChannelCount *
                (format.BitsPerSample / 8));

        if (expectedBlockAlign !=
            format.BlockAlign)
        {
            throw new NotSupportedException(
                "Unexpected native block alignment.");
        }
    }

    private static unsafe float ReadPcm16(
        byte* source,
        int validBits)
    {
        short raw = *(short*)source;

        int shift = 16 - validBits;
        int value = raw >> shift;

        return ScaleSignedToFloat(
            value,
            validBits);
    }

    private static unsafe float ReadPcm24(
        byte* source,
        int validBits)
    {
        int raw =
            source[0] |
            (source[1] << 8) |
            (source[2] << 16);

        if ((raw & 0x00800000) != 0)
        {
            raw |= unchecked((int)0xFF000000);
        }

        int shift = 24 - validBits;
        int value = raw >> shift;

        return ScaleSignedToFloat(
            value,
            validBits);
    }

    private static unsafe float ReadPcm32(
        byte* source,
        int validBits)
    {
        int raw = *(int*)source;

        int shift = 32 - validBits;
        int value = raw >> shift;

        return ScaleSignedToFloat(
            value,
            validBits);
    }

    private static float ScaleSignedToFloat(
        long value,
        int validBits)
    {
        double scale =
            Math.Pow(
                2.0,
                validBits - 1);

        return (float)(value / scale);
    }

    private static unsafe void WritePcm16(
        byte* destination,
        float sample,
        int validBits)
    {
        long quantized =
            Quantize(sample, validBits);

        short raw = checked(
            (short)(
                quantized <<
                (16 - validBits)));

        *(short*)destination = raw;
    }

    private static unsafe void WritePcm24(
        byte* destination,
        float sample,
        int validBits)
    {
        long quantized =
            Quantize(sample, validBits);

        int raw = checked(
            (int)(
                quantized <<
                (24 - validBits)));

        destination[0] =
            (byte)(raw & 0xFF);

        destination[1] =
            (byte)((raw >> 8) & 0xFF);

        destination[2] =
            (byte)((raw >> 16) & 0xFF);
    }

    private static unsafe void WritePcm32(
        byte* destination,
        float sample,
        int validBits)
    {
        long quantized =
            Quantize(sample, validBits);

        int raw = checked(
            (int)(
                quantized <<
                (32 - validBits)));

        *(int*)destination = raw;
    }

    private static long Quantize(
        float sample,
        int validBits)
    {
        if (sample <= -1.0f)
        {
            return -(1L << (validBits - 1));
        }

        if (sample >= 1.0f)
        {
            return (1L << (validBits - 1)) - 1;
        }

        double maximum =
            (1L << (validBits - 1)) - 1;

        return (long)Math.Round(
            sample * maximum,
            MidpointRounding.AwayFromZero);
    }
}