using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class AudioCodecEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public AudioCodecEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_AudioEdge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CueSheetParser_ParseFile_And_ParseFileAsync_DecodeWindows1252Text()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding win1252 = Encoding.GetEncoding(1252);

        const string cueText =
            "TITLE \"Café & Motörhead — Edition €\"\r\n" +
            "PERFORMER \"Beyoncé Knowles\"\r\n" +
            "FILE \"01 - Café £10.wav\" WAVE\r\n" +
            "  TRACK 01 AUDIO\r\n" +
            "    TITLE \"Déjà Vu\"\r\n" +
            "    INDEX 01 00:00:00\r\n";

        string cuePath = Path.Combine(_tempDir, "win1252.cue");
        File.WriteAllBytes(cuePath, win1252.GetBytes(cueText));

        CueSheet syncParsed = CueSheetParser.ParseFile(cuePath);
        CueSheet asyncParsed = await CueSheetParser.ParseFileAsync(cuePath);

        foreach (CueSheet sheet in new[] { syncParsed, asyncParsed })
        {
            Assert.Equal("Café & Motörhead — Edition €", sheet.Title);
            Assert.Equal("Beyoncé Knowles", sheet.Performer);
            Assert.Single(sheet.Files);
            Assert.Equal("01 - Café £10.wav", sheet.Files[0].FileName);
            Assert.Equal("Déjà Vu", sheet.Files[0].Tracks[0].Title);
            Assert.DoesNotContain('\uFFFD', sheet.Title!);
            Assert.DoesNotContain('\uFFFD', sheet.Performer!);
            Assert.DoesNotContain('\uFFFD', sheet.Files[0].FileName);
        }
    }

    [Fact]
    public void CueSheetParser_ValidatesTrackAndIndexNumbers_AndSupportsApeWvTtaFileTypes()
    {
        const string spaceNamesCue =
            """
            FILE My Lossless Track 01.ape APE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            FILE My WavPack Track 02.wv WV
              TRACK 02 AUDIO
                INDEX 01 00:00:00
            FILE My TrueAudio Track 03.tta TTA
              TRACK 03 AUDIO
                INDEX 01 00:00:00
            """;

        CueSheet parsed = CueSheetParser.Parse(spaceNamesCue);
        Assert.Equal(3, parsed.Files.Count);
        Assert.Equal(("My Lossless Track 01.ape", "APE"), (parsed.Files[0].FileName, parsed.Files[0].FileType));
        Assert.Equal(("My WavPack Track 02.wv", "WV"), (parsed.Files[1].FileName, parsed.Files[1].FileType));
        Assert.Equal(("My TrueAudio Track 03.tta", "TTA"), (parsed.Files[2].FileName, parsed.Files[2].FileType));

        Assert.Throws<FormatException>(() => CueSheetParser.Parse("FILE \"a.bin\" BINARY\n  TRACK XX AUDIO\n    INDEX 01 00:00:00"));
        Assert.Throws<FormatException>(() => CueSheetParser.Parse("FILE \"a.bin\" BINARY\n  TRACK 00 AUDIO\n    INDEX 01 00:00:00"));
        Assert.Throws<FormatException>(() => CueSheetParser.Parse("FILE \"a.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX XX 00:00:00"));
        Assert.Throws<FormatException>(() => CueSheetParser.Parse("FILE \"a.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 100 00:00:00"));
        Assert.False(CueSheetParser.TryParse("FILE \"a.bin\" BINARY\n  TRACK BAD AUDIO", out _, out string? err));
        Assert.NotNull(err);
    }

    [Fact]
    public void RawCdAudioFile_FileSliceStream_MotorolaBigEndian_HandlesOneByteAndOddReads()
    {
        byte[] bigEndianSector = new byte[OpticalConstants.RawSectorSize];
        byte[] expectedLittleEndian = new byte[OpticalConstants.RawSectorSize];
        for (int i = 0; i < OpticalConstants.RawSectorSize; i += 2)
        {
            byte hi = (byte)((i * 7 + 3) & 0xFF);
            byte lo = (byte)((i * 13 + 5) & 0xFF);
            bigEndianSector[i] = hi;
            bigEndianSector[i + 1] = lo;
            expectedLittleEndian[i] = lo;
            expectedLittleEndian[i + 1] = hi;
        }

        string binPath = Path.Combine(_tempDir, "motorola.bin");
        File.WriteAllBytes(binPath, bigEndianSector);

        var factory = RawCdAudioFile.CreateSliceFactory(binPath, startFrame: 0, frameCount: 1, bigEndian: true);

        // 1-byte sequential reads across the entire sector
        using (Stream oneByteStream = factory())
        {
            byte[] actualOneByte = new byte[OpticalConstants.RawSectorSize];
            byte[] single = new byte[1];
            for (int pos = 0; pos < OpticalConstants.RawSectorSize; pos++)
            {
                int read = oneByteStream.Read(single, 0, 1);
                Assert.Equal(1, read);
                actualOneByte[pos] = single[0];
            }
            Assert.Equal(0, oneByteStream.Read(single, 0, 1));
            Assert.Equal(expectedLittleEndian, actualOneByte);
        }

        // 3-byte odd-sized reads and odd seek offset
        using (Stream oddStream = factory())
        {
            byte[] actualOdd = new byte[OpticalConstants.RawSectorSize];
            int offset = 0;
            byte[] chunk = new byte[3];
            while (offset < OpticalConstants.RawSectorSize)
            {
                int read = oddStream.Read(chunk, 0, chunk.Length);
                Assert.True(read > 0, $"Premature EOF at offset {offset}.");
                Array.Copy(chunk, 0, actualOdd, offset, read);
                offset += read;
            }
            Assert.Equal(expectedLittleEndian, actualOdd);

            oddStream.Position = 5;
            byte[] sliceFromOdd = new byte[3];
            Assert.Equal(3, oddStream.Read(sliceFromOdd, 0, 3));
            Assert.Equal(expectedLittleEndian.AsSpan(5, 3).ToArray(), sliceFromOdd);
        }
    }

    [Fact]
    public void WavFileCodec_WriteWavHeader_SupportsGreaterThan2GiBWithoutSignedOverflow()
    {
        const long over2GiB = 2_500_000_000L;
        byte[] over2GiBHeader = WavFileCodec.CreateWavHeader(over2GiB, 192000, 2, 32);
        Assert.Equal((uint)(over2GiB + 36), BinaryPrimitives.ReadUInt32LittleEndian(over2GiBHeader.AsSpan(4, 4)));
        Assert.Equal((uint)over2GiB, BinaryPrimitives.ReadUInt32LittleEndian(over2GiBHeader.AsSpan(40, 4)));

        // Boundary where int pcmByteLength + 36 overflows signed 32-bit int
        const int signedOverflowInput = int.MaxValue - 10;
        byte[] boundaryHeader = WavFileCodec.CreateWavHeader(signedOverflowInput, 44100, 2, 16);
        uint expectedChunkSize = (uint)((long)signedOverflowInput + 36);
        Assert.Equal(expectedChunkSize, BinaryPrimitives.ReadUInt32LittleEndian(boundaryHeader.AsSpan(4, 4)));
        Assert.Equal((uint)signedOverflowInput, BinaryPrimitives.ReadUInt32LittleEndian(boundaryHeader.AsSpan(40, 4)));

        Assert.Throws<ArgumentOutOfRangeException>(() => WavFileCodec.CreateWavHeader(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WavFileCodec.CreateWavHeader((long)uint.MaxValue - 35));
    }

    [Fact]
    public void WavFileCodec_GreaterThan2GiB_StreamsHighRateAndRefusesOversizedFastPath()
    {
        const long dataBytes = 2_500_000_000L; // ~2.33 GiB

        // High-rate 8-channel 32-bit PCM (>2 GiB input -> ~35 MB Red Book output) streams through RedBookPcmConverter
        using (var highRateStream = new SparseZeroWavStream(dataBytes, sampleRate: 384000, channels: 8, bitsPerSample: 32))
        {
            AudioFormatMetadata meta = WavFileCodec.ReadMetadata(highRateStream, "huge.wav");
            Assert.Equal(dataBytes, meta.TotalPcmBytes);

            highRateStream.Position = 0;
            byte[] redBook = WavFileCodec.DecodeToRedBookPcm(highRateStream);
            long inputFrames = dataBytes / (8 * 4);
            long expectedFrames = RedBookPcmConverter.ExpectedOutputFrames(inputFrames, 384000);
            Assert.Equal(expectedFrames * OpticalConstants.CdAudioBytesPerSample, redBook.Length);
        }

        // >2 GiB 44.1 kHz stereo fast-path cannot fit in a managed byte[] or Red Book CD and must throw rather than truncate
        using (var hugeRedBookStream = new SparseZeroWavStream(dataBytes, sampleRate: 44100, channels: 2, bitsPerSample: 16))
        {
            Assert.Throws<InvalidDataException>(() => WavFileCodec.DecodeToRedBookPcm(hugeRedBookStream));
        }
    }

    [Fact]
    public void WavFileCodec_RejectsTruncatedRiffAndDataChunks_AndValidateInputFileRejects256ByteTruncatedWav()
    {
        // Valid 1-second WAV header truncated at 256 bytes
        byte[] fullPcm = new byte[44100 * 4];
        string validWav = Path.Combine(_tempDir, "valid.wav");
        WavFileCodec.WriteWavFile(validWav, fullPcm);

        string truncatedWav = Path.Combine(_tempDir, "truncated256.wav");
        File.WriteAllBytes(truncatedWav, File.ReadAllBytes(validWav)[..256]);

        Assert.Throws<InvalidDataException>(() => WavFileCodec.ProbeAudioFile(truncatedWav));
        Assert.Throws<InvalidDataException>(() => WavFileCodec.DecodeToRedBookPcm(truncatedWav));
        Assert.NotNull(AudioFileConverter.ValidateInputFile(truncatedWav));

        // Truncated RIFF header (8 bytes), truncated fmt chunk (20 bytes), truncated chunk header (40 bytes)
        foreach (int len in new[] { 8, 20, 40 })
        {
            byte[] slice = File.ReadAllBytes(validWav)[..len];
            using var ms = new MemoryStream(slice);
            Assert.Throws<InvalidDataException>(() => WavFileCodec.ReadMetadata(ms));
        }

        // Non-seekable stream with unknown JUNK chunk skips without large ReadBytes allocations
        byte[] smallPcm = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00];
        using var wavWithJunk = new MemoryStream();
        byte[] header = WavFileCodec.CreateWavHeader(smallPcm.Length + 8 + 131072, 44100, 2, 16);
        wavWithJunk.Write(header.AsSpan(0, 36));
        wavWithJunk.Write("JUNK"u8);
        byte[] junkLen = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(junkLen, 131072);
        wavWithJunk.Write(junkLen);
        wavWithJunk.Write(new byte[131072]);
        wavWithJunk.Write("data"u8);
        byte[] dataLen = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dataLen, (uint)smallPcm.Length);
        wavWithJunk.Write(dataLen);
        wavWithJunk.Write(smallPcm);

        using var nonSeekable = new NonSeekableStream(wavWithJunk.ToArray());
        byte[] decoded = WavFileCodec.DecodeToRedBookPcm(nonSeekable);
        Assert.Equal(smallPcm, decoded);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SparseZeroWavStream : Stream
    {
        private readonly byte[] _header;
        private readonly long _totalLength;
        private long _position;

        public SparseZeroWavStream(long dataBytes, int sampleRate, short channels, short bitsPerSample)
        {
            _header = WavFileCodec.CreateWavHeader(dataBytes, sampleRate, channels, bitsPerSample);
            _totalLength = _header.Length + dataBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _totalLength);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _totalLength || count <= 0) return 0;
            int total = 0;
            if (_position < _header.Length)
            {
                int fromHeader = (int)Math.Min(count, _header.Length - _position);
                Array.Copy(_header, (int)_position, buffer, offset, fromHeader);
                _position += fromHeader;
                offset += fromHeader;
                count -= fromHeader;
                total += fromHeader;
            }
            if (count > 0 && _position < _totalLength)
            {
                int zeros = (int)Math.Min(count, _totalLength - _position);
                Array.Clear(buffer, offset, zeros);
                _position += zeros;
                total += zeros;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _totalLength + offset
        };

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
