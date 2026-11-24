using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class RipLogAndStreamingFlacTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-riplog-" + Guid.NewGuid().ToString("N"));

    public RipLogAndStreamingFlacTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static RipLogContext CreateSampleContext(byte[]? hmacKey = null)
    {
        var tracks = new[]
        {
            new AudioTrackRipInfo(
                TrackNumber: 1,
                FilePath: @"C:\rips\Test Album.flac",
                Crc32: 0x1234ABCD,
                AccurateRipV1: 0x00112233,
                AccurateRipV2: 0x44556677,
                AudioTrackIndex: 0,
                UnreadableSectors: 0,
                RecoveredSectors: 2,
                SuspectSectors: 0,
                Title: "First Track",
                Performer: "Test Artist",
                Isrc: "USAA12600001",
                ReplayGainTrackGain: -3.25,
                ReplayGainTrackPeak: 0.985,
                CtdbTrackCrc: 0xAABBCCDD,
                PreEmphasis: true,
                DeEmphasisApplied: false,
                StartLba: 150,
                SectorCount: 2100,
                PreGapFrames: 150,
                CopyPermitted: true,
                FourChannel: false,
                PeakLevelPercent: 98.5),
            new AudioTrackRipInfo(
                TrackNumber: 2,
                FilePath: @"C:\rips\Test Album.flac",
                Crc32: 0x99887766,
                AccurateRipV1: 0x11223344,
                AccurateRipV2: 0x55667788,
                AudioTrackIndex: 1,
                UnreadableSectors: 0,
                RecoveredSectors: 0,
                SuspectSectors: 0,
                Title: "Second Track",
                Performer: "Test Artist",
                Isrc: "USAA12600002",
                ReplayGainTrackGain: -1.10,
                ReplayGainTrackPeak: 0.750,
                CtdbTrackCrc: 0x11220033,
                PreEmphasis: false,
                DeEmphasisApplied: false,
                StartLba: 2250,
                SectorCount: 1800,
                PreGapFrames: 75,
                CopyPermitted: false,
                FourChannel: true,
                PeakLevelPercent: 75.0)
        };

        var toc = new[]
        {
            new DiscTocTrack(1, 150, 2100, true, PreEmphasis: true, CopyPermitted: true),
            new DiscTocTrack(2, 2250, 1800, true, FourChannel: true),
            new DiscTocTrack(3, 15450, 500, false)
        };

        var cdText = new CdTextAlbumMetadata("Test Album", "Test Artist", Songwriter: "Song Writer");
        var readOffset = new ReadOffsetResolution(
            6,
            ReadOffsetSource.AccurateRipDatabase,
            "+6 samples (AccurateRip DriveOffsets.bin)",
            "AccurateRip database (500 submissions)",
            SubmissionsOrConfidence: 500);

        var rip = AudioCdRipResult.Succeeded(
            new[] { @"C:\rips\Test Album.flac" },
            4050,
            TimeSpan.FromSeconds(4.5),
            cueSheetPath: @"C:\rips\Test Album.cue",
            cdText: cdText,
            trackInfos: tracks,
            badSectorLbas: Array.Empty<int>(),
            secureModeUsed: true,
            replayGainAlbumGain: -2.40,
            replayGainAlbumPeak: 0.985,
            recoveryRepair: null,
            hiddenTrackOneAudioPath: null,
            ctdbDiscCrc: 0xDEADBEEF,
            readOffset: readOffset,
            mode: AudioRipMode.ImageCue,
            imageFilePath: @"C:\rips\Test Album.flac",
            hiddenTrackOneAudioFrames: 150,
            ripLogPath: @"C:\rips\Test Album.log",
            discTocTracks: toc,
            mediaCatalogNumber: "0012345678905",
            gapDetectionPerformed: true,
            overreadIntoLeadInOut: true,
            secureModeRequested: true,
            driveUniqueId: "VDRV-01",
            driveVendorId: "HL-DT-ST",
            driveProductId: "DVDRAM GP60NB50",
            driveProductRevision: "RF01");

        var discIds = new DiscIdentifiers("AbCdEfGhIjKlMnOpQrStUvWxYz1-", 0x020A1B03, 0x00123456, 0x00789ABC, 2, "CtdbTocId123456789012345678-", "150:2250:-15450:15950");
        var arVerdicts = new Dictionary<int, AccurateRipTrackVerdict?>
        {
            [1] = new AccurateRipTrackVerdict(true, 14, 15, MatchedV2: true),
            [2] = new AccurateRipTrackVerdict(false, 0, 10, MatchedV2: false)
        };
        var ctdbVerdicts = new Dictionary<int, CtdbTrackVerdict?>
        {
            [1] = new CtdbTrackVerdict(true, 8, 9),
            [2] = new CtdbTrackVerdict(true, 8, 9)
        };
        var repair = new CtdbRepairResult(CtdbRepairStatus.Repaired, "Repaired 4 samples.", 42, 8, 0, 4);

        return new RipLogContext(
            rip,
            discIds,
            AccurateRipStatus: "checked",
            AccurateRipVerdicts: arVerdicts,
            CtdbStatus: "checked",
            CtdbDiscMatch: (true, 8, 9),
            CtdbTrackVerdicts: ctdbVerdicts,
            CtdbRepair: repair,
            IgnisDiscVersion: "1.15.0",
            TimestampUtc: new DateTimeOffset(2026, 11, 24, 12, 30, 0, TimeSpan.Zero),
            HmacKey: hmacKey,
            OutputFormatLabel: "FLAC");
    }

    [Fact]
    public void RipLogFormatter_IncludesAllArchivalSections_AndValidatorPasses()
    {
        var context = CreateSampleContext();
        string log = RipLogFormatter.Format(context);

        Assert.Contains("IgnisDisc Optical Studio V1.15.0", log);
        Assert.Contains("HL-DT-ST DVDRAM GP60NB50 (Rev RF01) [VDRV-01]", log);
        Assert.Contains("+6 samples (AccurateRip DriveOffsets.bin)", log);
        Assert.Contains("Secure (C2 error pointers)", log);
        Assert.Contains("Overread into Lead-In and Lead-Out : Yes", log);
        Assert.Contains("Q sub-channel binary search", log);
        Assert.Contains("TOC of the extracted CD", log);
        Assert.Contains("Hidden Track One Audio (HTOA)", log);
        Assert.Contains("Peak level     98.5 %", log);
        Assert.Contains("Copy CRC       1234ABCD", log);
        Assert.Contains("Flags          DCP PRE", log);
        Assert.Contains("Flags          4CH", log);
        Assert.Contains("Accurately ripped (confidence 14/15, v2)", log);
        Assert.Contains("Cannot be verified as accurate (0/10 submissions match)", log);
        Assert.Contains("CUETools Database (CTDB) summary", log);
        Assert.Contains("Repair         repaired - Repaired 4 samples.", log);
        Assert.Contains("==== Log checksum ", log);
        Assert.Contains("SHA-256: ", log);

        var validation = RipLogValidator.Validate(log);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(validation.RecordedSha256, validation.ComputedSha256);
        Assert.Equal(validation.RecordedHmacSha256, validation.ComputedHmacSha256);

        // LF-normalized copy also validates cleanly
        var lfValidation = RipLogValidator.Validate(log.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(lfValidation.IsValid, lfValidation.Message);
    }

    [Fact]
    public void RipLogValidator_DetectsBodyTamper_FooterTamper_Truncation_AndWrongKey()
    {
        byte[] customKey = Encoding.UTF8.GetBytes("archive-secret-key");
        string log = RipLogFormatter.Format(CreateSampleContext(customKey));

        Assert.True(RipLogValidator.Validate(log, customKey).IsValid);
        Assert.False(RipLogValidator.Validate(log).IsValid); // default key must reject custom-key log

        // 1. Body tamper: change a single hex digit in Copy CRC
        string bodyTampered = log.Replace("1234ABCD", "1234ABCE", StringComparison.Ordinal);
        Assert.NotEqual(log, bodyTampered);
        var bodyRes = RipLogValidator.Validate(bodyTampered, customKey);
        Assert.False(bodyRes.IsValid);

        // 2. Tamper HMAC checksum line
        string[] lines = log.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int hmacIdx = Array.FindIndex(lines, l => l.StartsWith("==== Log checksum ", StringComparison.Ordinal));
        Assert.True(hmacIdx > 0);
        char origChar = lines[hmacIdx][18];
        char flippedChar = origChar == '0' ? '1' : '0';
        var hmacTamperedLines = (string[])lines.Clone();
        hmacTamperedLines[hmacIdx] = hmacTamperedLines[hmacIdx][..18] + flippedChar + hmacTamperedLines[hmacIdx][19..];
        Assert.False(RipLogValidator.Validate(string.Join('\n', hmacTamperedLines), customKey).IsValid);

        // 3. Tamper SHA-256 line
        var shaTamperedLines = (string[])lines.Clone();
        char origShaChar = shaTamperedLines[hmacIdx + 1][9];
        shaTamperedLines[hmacIdx + 1] = shaTamperedLines[hmacIdx + 1][..9] + (origShaChar == 'A' ? 'B' : 'A') + shaTamperedLines[hmacIdx + 1][10..];
        Assert.False(RipLogValidator.Validate(string.Join('\n', shaTamperedLines), customKey).IsValid);

        // 4. Truncated footer
        string noFooter = string.Join('\n', lines[..hmacIdx]);
        Assert.False(RipLogValidator.Validate(noFooter, customKey).IsValid);

        // 5. Trailing text injected after footer
        string trailing = log + "Injected trailing note\n";
        Assert.False(RipLogValidator.Validate(trailing, customKey).IsValid);
    }

    [Fact]
    public void WriteFlacFromWavFile_StreamsMultipleBlocksWithValidMd5AndBitExactAudio()
    {
        int totalSamples = 12_345; // spans >3 FLAC blocks of 4096 samples
        byte[] pcm = new byte[totalSamples * 4];
        var rng = new Random(77);
        rng.NextBytes(pcm);

        string wavPath = Path.Combine(_dir, "stream_src.wav");
        string flacPath = Path.Combine(_dir, "stream_dst.flac");
        WavFileCodec.WriteWavFile(wavPath, pcm);

        var tags = new AudioTrackTags("Album", "Artist", "Album", 0, 2, ReplayGainAlbumGain: "-2.00 dB", ReplayGainAlbumPeak: "0.900000");
        FlacFileCodec.WriteFlacFromWavFile(wavPath, flacPath, tags, songwriter: "Writer");

        var integrity = FlacFileCodec.CheckIntegrity(flacPath);
        Assert.False(integrity.IsDamaged, integrity.Describe());
        Assert.True(integrity.Md5Matches);
        Assert.Equal(totalSamples, integrity.DecodedSamples);

        byte[] decoded = FlacFileCodec.DecodeToRedBookPcm(flacPath);
        Assert.Equal(pcm, decoded);

        using var fs = File.OpenRead(flacPath);
        var meta = FlacFileCodec.ProbeMetadata(fs);
        Assert.Equal("Album", meta.Album);
        Assert.Equal("Writer", meta.Songwriter);
        Assert.Equal("-2.00 dB", meta.ReplayGainAlbumGain);
    }
}
