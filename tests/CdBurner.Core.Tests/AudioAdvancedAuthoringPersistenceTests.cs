using System.Buffers.Binary;
using System.IO;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class AudioAdvancedAuthoringPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public AudioAdvancedAuthoringPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_AudioAdv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] MakePcm(int frames, byte seed)
    {
        byte[] pcm = new byte[frames * OpticalConstants.RawSectorSize];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)((i + seed) % 251 + 1);
        }
        return pcm;
    }

    private static byte[] MinimalPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    [Fact]
    public async Task ProjectSerializer_RoundTrips_PreEmphasis_PreGap_Htoa_TrackAlbum_CdText_And_CoverArt()
    {
        string track1Wav = Path.Combine(_tempDir, "t1.wav");
        string track2Wav = Path.Combine(_tempDir, "t2.wav");
        string htoaWav = Path.Combine(_tempDir, "htoa.wav");
        string coverPng = Path.Combine(_tempDir, "cover.png");
        string projPath = Path.Combine(_tempDir, "master.cdbproj");

        byte[] htoaPcm = MakePcm(150, 7);
        WavFileCodec.WriteWavFile(track1Wav, MakePcm(300, 1));
        WavFileCodec.WriteWavFile(track2Wav, MakePcm(300, 2));
        WavFileCodec.WriteWavFile(htoaWav, htoaPcm);
        byte[] coverBytes = MinimalPng();
        await File.WriteAllBytesAsync(coverPng, coverBytes);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Advanced Master",
            AlbumPerformer = "Studio Ensemble",
            Songwriter = "Lead Writer",
            UpcEan = "0123456789012",
            IncludeCdText = true,
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            CoverArtPath = coverPng,
            CoverArtBytes = coverBytes,
            CoverArtMimeType = "image/png"
        };

        compilation.AddTrack(new AudioCdTrackItem(
            1,
            track1Wav,
            "Opening Act",
            "Studio Ensemble",
            Isrc: "USABC2600001",
            PreGapFrames: 225)
        {
            Album = "Disc One Compilation",
            Songwriter = "Writer One",
            DurationFrames = 300,
            PreEmphasis = true,
            HiddenAudioFilePath = htoaWav,
            HiddenAudioFrames = 150,
            HiddenAudioPcmStreamFactory = () => WavFileCodec.OpenRedBookPcmStream(htoaWav)
        });

        compilation.AddTrack(new AudioCdTrackItem(
            2,
            track2Wav,
            "Second Movement",
            "Guest Artist",
            Isrc: "USABC2600002",
            PreGapFrames: 75)
        {
            Album = "Bonus Disc",
            Songwriter = "Writer Two",
            DurationFrames = 300,
            PreEmphasis = false
        });

        var doc = ProjectSerializer.FromAudioCdCompilation(compilation, "Advanced Master Project");
        await ProjectSerializer.SaveToFileAsync(doc, projPath);

        var loadedDoc = await ProjectSerializer.LoadFromFileAsync(projPath);
        Assert.Empty(ProjectSerializer.ValidateProjectSources(loadedDoc));

        var reloaded = ProjectSerializer.ToAudioCdCompilation(loadedDoc);
        Assert.Equal("Advanced Master", reloaded.AlbumTitle);
        Assert.Equal("Studio Ensemble", reloaded.AlbumPerformer);
        Assert.Equal("Lead Writer", reloaded.Songwriter);
        Assert.Equal("0123456789012", reloaded.UpcEan);
        Assert.Equal(coverPng, reloaded.CoverArtPath);
        Assert.Equal(coverBytes, reloaded.CoverArtBytes);
        Assert.Equal("image/png", reloaded.CoverArtMimeType);

        Assert.Equal(2, reloaded.Tracks.Count);
        var t1 = reloaded.Tracks[0];
        Assert.Equal("Opening Act", t1.Title);
        Assert.Equal("Disc One Compilation", t1.Album);
        Assert.Equal("Writer One", t1.Songwriter);
        Assert.Equal("USABC2600001", t1.Isrc);
        Assert.Equal(225, t1.PreGapFrames);
        Assert.True(t1.PreEmphasis);
        Assert.Equal(htoaWav, t1.HiddenAudioFilePath);
        Assert.Equal(150, t1.HiddenAudioFrames);
        Assert.NotNull(t1.HiddenAudioPcmStreamFactory);

        using (var htoaStream = t1.HiddenAudioPcmStreamFactory!())
        using (var ms = new MemoryStream())
        {
            await htoaStream.CopyToAsync(ms);
            Assert.Equal(htoaPcm, ms.ToArray());
        }

        var t2 = reloaded.Tracks[1];
        Assert.Equal("Second Movement", t2.Title);
        Assert.Equal("Guest Artist", t2.Performer);
        Assert.Equal("Bonus Disc", t2.Album);
        Assert.Equal("Writer Two", t2.Songwriter);
        Assert.Equal("USABC2600002", t2.Isrc);
        Assert.Equal(75, t2.PreGapFrames);
        Assert.False(t2.PreEmphasis);
    }

    [Fact]
    public void ProjectSerializer_ValidateProjectSources_DetectsMissingCoverAndHtoaFiles()
    {
        var comp = new AudioCdCompilation
        {
            CoverArtPath = Path.Combine(_tempDir, "missing-cover.jpg")
        };
        string existingWav = Path.Combine(_tempDir, "present.wav");
        WavFileCodec.WriteWavFile(existingWav, MakePcm(10, 3));
        comp.AddTrack(new AudioCdTrackItem(1, existingWav, "T1", "Artist")
        {
            HiddenAudioFilePath = Path.Combine(_tempDir, "missing-htoa.wav"),
            HiddenAudioFrames = 10
        });

        var doc = ProjectSerializer.FromAudioCdCompilation(comp);
        var missing = ProjectSerializer.ValidateProjectSources(doc);
        Assert.Contains(comp.CoverArtPath, missing);
        Assert.Contains(comp.Tracks[0].HiddenAudioFilePath!, missing);
    }

    [Fact]
    public void JewelCaseCoverGenerator_EmbedsCoverArt_AndMarksHtoaAndPreEmphasis()
    {
        byte[] png = MinimalPng();
        var comp = new AudioCdCompilation
        {
            AlbumTitle = "Cover Case",
            AlbumPerformer = "Case Artist",
            CoverArtBytes = png,
            CoverArtMimeType = "image/png"
        };
        comp.AddTrack(new AudioCdTrackItem(1, "a.wav", "First", "Case Artist", PreGapFrames: 150)
        {
            DurationFrames = 300,
            PreEmphasis = true,
            HiddenAudioFrames = 225
        });

        string html = JewelCaseCoverGenerator.GeneratePrintableCoverHtml(comp);
        Assert.Contains($"data:image/png;base64,{Convert.ToBase64String(png)}", html, StringComparison.Ordinal);
        Assert.Contains("Hidden Track One Audio (HTOA)", html, StringComparison.Ordinal);
        Assert.Contains("[PRE]", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OggOpusMetadata_EmbedsMultiPageCoverArt_WithValidOggCrcsAndHeaderRoundTrip()
    {
        // Build a minimal valid Ogg Opus stream (OpusHead + OpusTags + 1 audio page) per RFC 7845.
        string opusPath = Path.Combine(_tempDir, "sample.opus");
        File.WriteAllBytes(opusPath, BuildMinimalOpusStream(serial: 0x12345678, totalGranule: 48000, preSkip: 312));

        // 72 KB PNG payload forces the OpusTags packet across two Ogg pages (> 65,025 bytes).
        byte[] largePng = new byte[72 * 1024];
        byte[] header = MinimalPng();
        header.CopyTo(largePng, 0);
        for (int i = header.Length; i < largePng.Length; i++)
        {
            largePng[i] = (byte)(i * 13);
        }

        OggOpusMetadata.EmbedCoverArt(opusPath, largePng, "image/png");

        byte[] rewritten = File.ReadAllBytes(opusPath);
        VerifyAllOggPagesHaveValidCrcAndSequence(rewritten);

        var gapless = GaplessStreamInfo.TryRead(opusPath);
        Assert.NotNull(gapless);
        Assert.Equal(GaplessCodec.Opus, gapless!.Codec);
        Assert.Equal(48000 - 312, gapless.TrimmedSamples);
        Assert.NotNull(gapless.Comments);
        Assert.Equal("Opus Track", gapless.Comments!["TITLE"]);
        Assert.True(OggOpusMetadata.TryExtractCoverArt(gapless.Comments, out byte[]? extracted, out string? mime));
        Assert.Equal("image/png", mime);
        Assert.Equal(largePng, extracted);
    }

    private static byte[] BuildMinimalOpusStream(uint serial, long totalGranule, ushort preSkip)
    {
        using var ms = new MemoryStream();

        // Page 0: OpusHead (RFC 7845 §5.1)
        byte[] opusHead = new byte[19];
        "OpusHead"u8.CopyTo(opusHead);
        opusHead[8] = 1; // version
        opusHead[9] = 2; // channels
        BinaryPrimitives.WriteUInt16LittleEndian(opusHead.AsSpan(10, 2), preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(opusHead.AsSpan(12, 4), 44100);
        WritePage(ms, headerType: 0x02, granule: 0, serial, seq: 0, opusHead);

        // Page 1: OpusTags (RFC 7845 §5.2)
        byte[] opusTags = BuildOpusTags("IgnisTest", ["TITLE=Opus Track", "ARTIST=Test Artist"]);
        WritePage(ms, headerType: 0x00, granule: 0, serial, seq: 1, opusTags);

        // Page 2: EOS audio page with dummy Opus TOC byte (0xF8 = 20ms stereo)
        WritePage(ms, headerType: 0x04, granule: totalGranule, serial, seq: 2, [0xF8, 0xFF, 0xFE]);

        return ms.ToArray();
    }

    private static byte[] BuildOpusTags(string vendor, IReadOnlyList<string> comments)
    {
        using var ms = new MemoryStream();
        ms.Write("OpusTags"u8);
        Span<byte> u32 = stackalloc byte[4];
        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)vendorBytes.Length);
        ms.Write(u32);
        ms.Write(vendorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)comments.Count);
        ms.Write(u32);
        foreach (string c in comments)
        {
            byte[] cb = Encoding.UTF8.GetBytes(c);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)cb.Length);
            ms.Write(u32);
            ms.Write(cb);
        }
        return ms.ToArray();
    }

    private static void WritePage(Stream output, byte headerType, long granule, uint serial, uint seq, byte[] payload)
    {
        byte[] page = new byte[28 + payload.Length];
        "OggS"u8.CopyTo(page.AsSpan(0, 4));
        page[4] = 0;
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), seq);
        page[26] = 1;
        page[27] = (byte)payload.Length;
        payload.CopyTo(page.AsSpan(28));
        uint crc = OggOpusMetadata.ComputeOggCrc32(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), crc);
        output.Write(page);
    }

    private static void VerifyAllOggPagesHaveValidCrcAndSequence(byte[] data)
    {
        int offset = 0;
        uint expectedSeq = 0;
        while (offset < data.Length)
        {
            Assert.Equal("OggS", Encoding.ASCII.GetString(data, offset, 4));
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 18, 4));
            Assert.Equal(expectedSeq++, seq);

            int segCount = data[offset + 26];
            int payloadLen = 0;
            for (int i = 0; i < segCount; i++)
            {
                payloadLen += data[offset + 27 + i];
            }
            int pageLen = 27 + segCount + payloadLen;
            byte[] copy = data.AsSpan(offset, pageLen).ToArray();
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(22, 4));
            copy[22] = copy[23] = copy[24] = copy[25] = 0;
            uint computed = OggOpusMetadata.ComputeOggCrc32(copy);
            Assert.Equal(computed, storedCrc);
            offset += pageLen;
        }
        Assert.True(expectedSeq >= 4, "Multi-page cover art should produce at least 4 Ogg pages.");
    }
}
