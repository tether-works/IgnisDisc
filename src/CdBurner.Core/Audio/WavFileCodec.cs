using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Pure C# RIFF WAVE encoder and decoder supporting 8/16/24/32-bit PCM and IEEE Float formats,
/// automatic resampling to 44,100 Hz, mono-to-stereo channel upmixing, and standard 44-byte RIFF headers.
/// </summary>
public static class WavFileCodec
{
    private const long MaxRiffDataBytes = (long)uint.MaxValue - 36;
    private static readonly byte[] RiffHeader = "RIFF"u8.ToArray();
    private static readonly byte[] WaveHeader = "WAVE"u8.ToArray();
    private static readonly byte[] FmtHeader = "fmt "u8.ToArray();
    private static readonly byte[] DataHeader = "data"u8.ToArray();

    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatIeeeFloat = 0x0003;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// Creates a standard 44-byte RIFF WAVE header for canonical Red Book or custom PCM streams.
    /// </summary>
    public static byte[] CreateWavHeader(
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample) =>
        CreateWavHeader((long)pcmByteLength, sampleRate, channels, bitsPerSample, isFloat: false);

    /// <summary>
    /// Creates a standard 44-byte RIFF WAVE header for PCM or IEEE float streams up to the 4 GiB RIFF limit.
    /// </summary>
    public static byte[] CreateWavHeader(
        long pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample,
        bool isFloat = false)
    {
        byte[] header = new byte[44];
        WriteWavHeader(header.AsSpan(), pcmByteLength, sampleRate, channels, bitsPerSample, isFloat);
        return header;
    }

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header to a span.
    /// </summary>
    public static void WriteWavHeader(
        Span<byte> destination,
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample) =>
        WriteWavHeader(destination, (long)pcmByteLength, sampleRate, channels, bitsPerSample, isFloat: false);

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header to a span for PCM or IEEE float payloads up to the 4 GiB RIFF limit.
    /// </summary>
    public static void WriteWavHeader(
        Span<byte> destination,
        long pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample,
        bool isFloat = false)
    {
        if (destination.Length < 44)
        {
            throw new ArgumentException("Destination span must be at least 44 bytes.", nameof(destination));
        }

        if (pcmByteLength < 0 || pcmByteLength > MaxRiffDataBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pcmByteLength),
                pcmByteLength,
                $"PCM byte length must be between 0 and {MaxRiffDataBytes} bytes for a RIFF WAVE header.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        }

        if (bitsPerSample <= 0 || (bitsPerSample % 8) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Bits per sample must be a positive multiple of 8.");
        }

        short bytesPerSample = (short)(bitsPerSample / 8);
        short blockAlign = checked((short)(channels * bytesPerSample));
        uint byteRate = checked((uint)((long)sampleRate * blockAlign));
        uint totalChunkSize = (uint)(pcmByteLength + 36);
        uint dataSize = (uint)pcmByteLength;
        ushort audioFormat = isFloat ? WaveFormatIeeeFloat : WaveFormatPcm;

        // "RIFF"
        RiffHeader.CopyTo(destination[..4]);
        // ChunkSize (FileSize - 8)
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), totalChunkSize);
        // "WAVE"
        WaveHeader.CopyTo(destination.Slice(8, 4));
        // "fmt "
        FmtHeader.CopyTo(destination.Slice(12, 4));
        // Subchunk1Size = 16 for standard PCM / IEEE float
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), 16);
        // AudioFormat (1 = PCM, 3 = IEEE float)
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(20, 2), audioFormat);
        // NumChannels
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(22, 2), (ushort)channels);
        // SampleRate
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), (uint)sampleRate);
        // ByteRate
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), byteRate);
        // BlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(32, 2), (ushort)blockAlign);
        // BitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(34, 2), (ushort)bitsPerSample);
        // "data"
        DataHeader.CopyTo(destination.Slice(36, 4));
        // Subchunk2Size = pcmByteLength
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(40, 4), dataSize);
    }

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header directly to a stream.
    /// </summary>
    public static void WriteWavHeader(
        Stream stream,
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample) =>
        WriteWavHeader(stream, (long)pcmByteLength, sampleRate, channels, bitsPerSample, isFloat: false);

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header directly to a stream.
    /// </summary>
    public static void WriteWavHeader(
        Stream stream,
        long pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample,
        bool isFloat = false)
    {
        byte[] header = CreateWavHeader(pcmByteLength, sampleRate, channels, bitsPerSample, isFloat);
        stream.Write(header, 0, header.Length);
    }

    /// <summary>
    /// Writes uncompressed PCM audio bytes along with a standard 44-byte RIFF WAVE header to disk.
    /// </summary>
    public static void WriteWavFile(
        string filePath,
        ReadOnlySpan<byte> pcmData,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] header = CreateWavHeader(pcmData.Length, sampleRate, channels, bitsPerSample);
        fileStream.Write(header, 0, header.Length);
        fileStream.Write(pcmData);
    }

    /// <summary>
    /// Inspects and parses the format metadata of a RIFF WAVE file.
    /// </summary>
    public static AudioFormatMetadata ProbeAudioFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadMetadata(stream, filePath);
    }

    /// <summary>
    /// Parses format metadata from a RIFF WAVE stream.
    /// </summary>
    public static AudioFormatMetadata ReadMetadata(Stream stream, string filePath = "")
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        long initialPos = stream.CanSeek ? stream.Position : 0;

        try
        {
            var header = ParseWavHeader(reader, stream);

            int bytesPerFrame = header.Channels * (header.BitsPerSample / 8);
            long totalFrames = bytesPerFrame > 0 ? header.DataSize / bytesPerFrame : 0;
            double durationSec = header.SampleRate > 0 ? (double)totalFrames / header.SampleRate : 0;
            TimeSpan duration = TimeSpan.FromSeconds(durationSec);

            // Canonical Red Book: 44,100 Hz, 16-bit (2 bytes), 2 channels = 4 bytes per frame
            long redBookFrames = (long)Math.Round(durationSec * OpticalConstants.CdAudioSampleRate);
            long totalRedBookBytes = redBookFrames * OpticalConstants.CdAudioBytesPerSample;

            string formatDesc = header.IsFloat
                ? $"WAV IEEE Float {header.BitsPerSample}-bit"
                : $"WAV PCM {header.BitsPerSample}-bit";

            return new AudioFormatMetadata
            {
                FilePath = filePath,
                FormatName = formatDesc,
                Channels = header.Channels,
                SampleRate = header.SampleRate,
                BitsPerSample = header.BitsPerSample,
                Duration = duration,
                TotalPcmBytes = header.DataSize,
                TotalRedBookBytes = totalRedBookBytes,
                Title = header.Title,
                Artist = header.Artist
            };
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = initialPos;
            }
        }
    }

    /// <summary>
    /// Reads and decodes a RIFF WAVE file into canonical Red Book CD-DA PCM
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static MemoryStream OpenRedBookPcmStream(string filePath)
    {
        byte[] pcm = DecodeToRedBookPcm(filePath);
        return new MemoryStream(pcm, 0, pcm.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Returns the byte offset and length of the PCM data chunk of a WAVE file that is already in
    /// Red Book format (44,100 Hz, 16-bit integer PCM, 2 channels). Used to read or patch samples in place.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a Red Book PCM WAVE file.</exception>
    public static (long Offset, long Length) LocateRedBookPcmData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var parsed = ParseWavHeader(reader, stream);
        if (parsed.IsFloat || parsed.BitsPerSample != 16 || parsed.Channels != 2 || parsed.SampleRate != 44100)
        {
            throw new InvalidDataException(
                $"Expected 44,100 Hz 16-bit stereo PCM, found {parsed.SampleRate} Hz {parsed.BitsPerSample}-bit {parsed.Channels}-channel.");
        }

        long length = Math.Min(parsed.DataSize, Math.Max(0, stream.Length - parsed.DataOffset));
        return (parsed.DataOffset, length - (length % 4));
    }

    /// <summary>
    /// Decodes a RIFF WAVE file into canonical Red Book PCM bytes.
    /// </summary>
    public static byte[] DecodeToRedBookPcm(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeToRedBookPcm(stream);
    }

    /// <summary>
    /// Decodes any supported RIFF WAVE stream into canonical Red Book PCM bytes
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static byte[] DecodeToRedBookPcm(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var parsed = ParseWavHeader(reader, stream);

        if (parsed.DataSize <= 0)
        {
            return Array.Empty<byte>();
        }

        // Seek to data chunk start
        if (stream.CanSeek && stream.Position != parsed.DataOffset)
        {
            stream.Position = parsed.DataOffset;
        }

        bool isRedBookStereo = parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
                               parsed.Channels == OpticalConstants.CdAudioChannels &&
                               parsed.BitsPerSample == OpticalConstants.CdAudioBitsPerSample &&
                               !parsed.IsFloat;
        bool isRedBookMono = parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
                             parsed.Channels == 1 &&
                             parsed.BitsPerSample == 16 &&
                             !parsed.IsFloat;

        // Everything that needs resampling, re-quantizing or downmixing streams through the shared band-limited
        // converter (same quality as FLAC, no whole-file float copies, no 2 GB limit).
        if (!isRedBookStereo && !isRedBookMono)
        {
            return ConvertViaRedBookConverter(stream, parsed);
        }

        int bytesPerSample = parsed.BitsPerSample / 8;
        int bytesPerInputFrame = parsed.Channels * bytesPerSample;
        long inputFramesLong = bytesPerInputFrame > 0 ? parsed.DataSize / bytesPerInputFrame : 0;

        if (inputFramesLong <= 0)
        {
            return Array.Empty<byte>();
        }

        long outputBytesLong = inputFramesLong * OpticalConstants.CdAudioBytesPerSample;
        if (inputFramesLong > RedBookPcmConverter.MaxOutputFrames || outputBytesLong > Array.MaxLength)
        {
            throw new InvalidDataException("WAV audio is longer than any Red Book Audio CD can hold.");
        }

        int inputFrames = (int)inputFramesLong;
        int exactInputBytes = checked(inputFrames * bytesPerInputFrame);
        byte[] rawAudioBytes = new byte[exactInputBytes];
        ReadExactOrThrowInvalidData(stream, rawAudioBytes, "Invalid WAVE file: unexpected end of stream in 'data' chunk.");

        // Fast-path 1: Already Red Book PCM (44.1 kHz, 16-bit signed LE, 2 channels, non-float)
        if (parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
            parsed.Channels == OpticalConstants.CdAudioChannels &&
            parsed.BitsPerSample == OpticalConstants.CdAudioBitsPerSample &&
            !parsed.IsFloat)
        {
            return rawAudioBytes;
        }

        // Fast-path 2: 44.1 kHz, 16-bit signed LE, 1 channel (Mono) -> direct stereo duplication without float conversion
        if (parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
            parsed.Channels == 1 &&
            parsed.BitsPerSample == 16 &&
            !parsed.IsFloat)
        {
            byte[] stereoBytes = new byte[checked(inputFrames * 4)];
            ReadOnlySpan<byte> inSpan = rawAudioBytes;
            Span<byte> outSpan = stereoBytes;
            for (int i = 0; i < inputFrames; i++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(inSpan.Slice(i * 2, 2));
                BinaryPrimitives.WriteInt16LittleEndian(outSpan.Slice(i * 4, 2), sample);
                BinaryPrimitives.WriteInt16LittleEndian(outSpan.Slice(i * 4 + 2, 2), sample);
            }
            return stereoBytes;
        }

        // Unreachable: only the two Red Book fast paths above reach this point.
        throw new InvalidOperationException("Unexpected WAV layout after fast-path selection.");
    }

    /// <summary>
    /// Streams the data chunk through <see cref="RedBookPcmConverter"/> in fixed-size blocks: band-limited resampling,
    /// multichannel downmix and dithered re-quantization, using memory proportional to the Red Book output only.
    /// </summary>
    private static byte[] ConvertViaRedBookConverter(Stream stream, ParsedWavInfo parsed)
    {
        int bytesPerSample = parsed.BitsPerSample / 8;
        int channels = parsed.Channels;
        if (channels is < 1 or > 8)
        {
            throw new NotSupportedException($"WAV files with {channels} channels are not supported (1 to 8).");
        }

        if (parsed.IsFloat ? parsed.BitsPerSample != 32 : bytesPerSample is < 1 or > 4 || parsed.BitsPerSample % 8 != 0)
        {
            throw new NotSupportedException($"WAV bit depth {parsed.BitsPerSample}{(parsed.IsFloat ? " (float)" : string.Empty)} is not supported.");
        }

        int frameBytes = channels * bytesPerSample;
        long totalFrames = parsed.DataSize / frameBytes;
        if (totalFrames <= 0)
        {
            return Array.Empty<byte>();
        }

        if (RedBookPcmConverter.ExpectedOutputFrames(totalFrames, parsed.SampleRate) > RedBookPcmConverter.MaxOutputFrames)
        {
            throw new InvalidDataException("WAV audio is longer than any Red Book Audio CD can hold.");
        }

        // 32-bit float is mapped onto the full signed 32-bit integer range.
        int sourceBits = parsed.IsFloat ? 32 : parsed.BitsPerSample;
        var converter = new RedBookPcmConverter(parsed.SampleRate, totalFrames);

        const int blockFrames = 16384;
        byte[] buffer = new byte[blockFrames * frameBytes];
        var samples = new int[channels][];
        for (int c = 0; c < channels; c++) samples[c] = new int[blockFrames];

        long remaining = totalFrames;
        while (remaining > 0)
        {
            int want = (int)Math.Min(blockFrames, remaining);
            int gotBytes = 0;
            int wantBytes = want * frameBytes;
            while (gotBytes < wantBytes)
            {
                int r = stream.Read(buffer, gotBytes, wantBytes - gotBytes);
                if (r <= 0) break;
                gotBytes += r;
            }

            int got = gotBytes / frameBytes;
            if (got < want)
            {
                throw new InvalidDataException("Invalid WAVE file: unexpected end of stream in 'data' chunk.");
            }

            ReadOnlySpan<byte> span = buffer;
            for (int i = 0; i < got; i++)
            {
                int frameOffset = i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int o = frameOffset + (c * bytesPerSample);
                    samples[c][i] = bytesPerSample switch
                    {
                        1 => span[o] - 128, // 8-bit WAV is unsigned
                        2 => BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o, 2)),
                        3 => (span[o] | (span[o + 1] << 8) | (span[o + 2] << 16)) << 8 >> 8, // sign-extend 24-bit
                        _ => parsed.IsFloat
                            ? FloatToInt32(BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o, 4)))
                            : BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4))
                    };
                }
            }

            converter.WriteBlock(samples, channels, sourceBits, got);
            remaining -= got;
        }

        return converter.Finish();

        static int FloatToInt32(float f)
        {
            if (float.IsNaN(f)) return 0;
            // ±1.0 is full scale; the 2^31 scale keeps -1.0 exact (int.MinValue) and clamps +1.0 to int.MaxValue.
            double scaled = Math.Round(Math.Clamp(f, -1.0f, 1.0f) * 2147483648.0);
            return (int)Math.Clamp(scaled, int.MinValue, int.MaxValue);
        }
    }


    private sealed class ParsedWavInfo
    {
        public ushort AudioFormat { get; set; }
        public ushort Channels { get; set; }
        public int SampleRate { get; set; }
        public ushort BitsPerSample { get; set; }
        public bool IsFloat { get; set; }
        public long DataOffset { get; set; }
        public long DataSize { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
    }

    private static ParsedWavInfo ParseWavHeader(BinaryReader reader, Stream stream)
    {
        Span<byte> riffHeader = stackalloc byte[12];
        ReadExactOrThrowInvalidData(stream, riffHeader, "Invalid WAVE file: truncated RIFF header.");

        if (!riffHeader[..4].SequenceEqual(RiffHeader))
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'RIFF' header signature.");
        }

        uint fileSizeMinus8 = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader.Slice(4, 4));
        if (!riffHeader.Slice(8, 4).SequenceEqual(WaveHeader))
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'WAVE' format signature.");
        }

        if (fileSizeMinus8 < 4)
        {
            throw new InvalidDataException("Invalid WAVE file: RIFF size is smaller than the WAVE header.");
        }

        if (stream.CanSeek && fileSizeMinus8 != uint.MaxValue && (8L + fileSizeMinus8) > stream.Length)
        {
            throw new InvalidDataException(
                $"Invalid WAVE file: truncated RIFF payload (header declares {8L + fileSizeMinus8} bytes, stream has {stream.Length} bytes).");
        }

        var info = new ParsedWavInfo();
        bool fmtFound = false;
        bool dataFound = false;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmtBytes = stackalloc byte[16];
        Span<byte> extBytes = stackalloc byte[24];
        Span<byte> listType = stackalloc byte[4];
        Span<byte> tagHeader = stackalloc byte[8];

        while (!stream.CanSeek || stream.Position < stream.Length)
        {
            int headerRead = ReadAtMost(stream, chunkHeader);
            if (headerRead == 0)
            {
                break;
            }
            if (headerRead < 8)
            {
                throw new InvalidDataException("Invalid WAVE file: truncated RIFF chunk header.");
            }

            ReadOnlySpan<byte> chunkIdBytes = chunkHeader[..4];
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
            long chunkStart = stream.CanSeek ? stream.Position : 0;
            long bytesConsumedInChunk = 0;

            if (stream.CanSeek && (chunkStart + chunkSize) > stream.Length)
            {
                string chunkName = Encoding.ASCII.GetString(chunkIdBytes);
                throw new InvalidDataException(
                    $"Invalid WAVE file: truncated '{chunkName}' chunk (expected {chunkSize} bytes, found {Math.Max(0, stream.Length - chunkStart)} bytes).");
            }

            if (chunkIdBytes.SequenceEqual(FmtHeader))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Invalid WAVE file: 'fmt ' chunk size must be at least 16 bytes.");
                }

                fmtFound = true;
                ReadExactOrThrowInvalidData(stream, fmtBytes, "Invalid WAVE file: truncated 'fmt ' chunk.");
                info.AudioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(0, 2));
                info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(2, 2));
                info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(4, 4));
                info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(14, 2));
                bytesConsumedInChunk = 16;

                if (info.AudioFormat != WaveFormatPcm &&
                    info.AudioFormat != WaveFormatIeeeFloat &&
                    info.AudioFormat != WaveFormatExtensible)
                {
                    throw new NotSupportedException($"Unsupported WAVE audio format tag: 0x{info.AudioFormat:X4}.");
                }

                if (info.Channels is < 1 or > 32)
                {
                    throw new InvalidDataException($"Invalid WAVE channel count: {info.Channels}.");
                }

                if (info.SampleRate is < 1000 or > 384000)
                {
                    throw new InvalidDataException($"Invalid WAVE sample rate: {info.SampleRate} Hz.");
                }

                if (info.AudioFormat == WaveFormatIeeeFloat)
                {
                    info.IsFloat = true;
                }
                else if (info.AudioFormat == WaveFormatExtensible && chunkSize >= 40)
                {
                    ReadExactOrThrowInvalidData(stream, extBytes, "Invalid WAVE file: truncated extensible 'fmt ' chunk.");
                    bytesConsumedInChunk += 24;
                    var subFormat = new Guid(extBytes.Slice(8, 16));
                    if (subFormat == SubFormatIeeeFloat)
                    {
                        info.IsFloat = true;
                    }
                    else if (subFormat != SubFormatPcm)
                    {
                        throw new NotSupportedException($"Unsupported WAVE extensible sub-format: {subFormat}.");
                    }
                }
            }
            else if (chunkIdBytes.SequenceEqual(DataHeader))
            {
                dataFound = true;
                info.DataOffset = chunkStart;
                info.DataSize = chunkSize;

                // If fmt was already found, we can proceed or keep scanning for LIST metadata
                if (stream.CanSeek)
                {
                    long nextPos = chunkStart + chunkSize;
                    if ((chunkSize % 2) != 0 && nextPos < stream.Length)
                    {
                        nextPos++; // Pad byte
                    }
                    stream.Seek(nextPos, SeekOrigin.Begin);
                    continue;
                }
                break;
            }
            else if (chunkIdBytes.SequenceEqual("LIST"u8) && chunkSize > 4)
            {
                ReadExactOrThrowInvalidData(stream, listType, "Invalid WAVE file: truncated 'LIST' chunk.");
                bytesConsumedInChunk += 4;
                long infoRead = 0;
                long maxInfoBytes = (long)chunkSize - 4;
                if (listType.SequenceEqual("INFO"u8))
                {
                    while (infoRead + 8 <= maxInfoBytes)
                    {
                        if (ReadAtMost(stream, tagHeader) < 8) break;
                        bytesConsumedInChunk += 8;
                        infoRead += 8;
                        ReadOnlySpan<byte> tagBytes = tagHeader[..4];
                        uint tagSize = BinaryPrimitives.ReadUInt32LittleEndian(tagHeader.Slice(4, 4));
                        if (tagSize > 0 && tagSize <= 65536 && infoRead + tagSize <= maxInfoBytes)
                        {
                            byte[] valBytes = new byte[tagSize];
                            ReadExactOrThrowInvalidData(stream, valBytes, "Invalid WAVE file: truncated 'LIST' metadata entry.");
                            bytesConsumedInChunk += valBytes.Length;
                            infoRead += valBytes.Length;
                            string text = Encoding.ASCII.GetString(valBytes).TrimEnd('\0', ' ');
                            if (tagBytes.SequenceEqual("INAM"u8))
                            {
                                info.Title = text;
                            }
                            else if (tagBytes.SequenceEqual("IART"u8))
                            {
                                info.Artist = text;
                            }
                            if ((tagSize % 2) != 0 && infoRead < maxInfoBytes)
                            {
                                if (stream.ReadByte() >= 0)
                                {
                                    bytesConsumedInChunk++;
                                    infoRead++;
                                }
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            // Seek past current chunk (or stream-skip on non-seekable streams without large allocations)
            if (stream.CanSeek)
            {
                long nextPos = chunkStart + chunkSize;
                if ((chunkSize % 2) != 0 && nextPos < stream.Length)
                {
                    nextPos++; // RIFF word alignment
                }
                stream.Seek(nextPos, SeekOrigin.Begin);
            }
            else
            {
                long payloadToSkip = Math.Max(0L, (long)chunkSize - bytesConsumedInChunk);
                SkipStreamBytes(stream, payloadToSkip, "Invalid WAVE file: unexpected end of stream inside RIFF chunk.");
                if ((chunkSize % 2) != 0)
                {
                    _ = stream.ReadByte();
                }
            }
        }

        if (!fmtFound)
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'fmt ' chunk.");
        }
        if (!dataFound)
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'data' chunk.");
        }

        return info;
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(total));
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static void ReadExactOrThrowInvalidData(Stream stream, Span<byte> buffer, string errorMessage)
    {
        if (ReadAtMost(stream, buffer) < buffer.Length)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private static void SkipStreamBytes(Stream stream, long byteCount, string errorMessage)
    {
        if (byteCount <= 0)
        {
            return;
        }

        const int scratchSize = 8192;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(scratchSize);
        try
        {
            long remaining = byteCount;
            while (remaining > 0)
            {
                int want = (int)Math.Min(scratchSize, remaining);
                int read = stream.Read(scratch, 0, want);
                if (read <= 0)
                {
                    throw new InvalidDataException(errorMessage);
                }
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
