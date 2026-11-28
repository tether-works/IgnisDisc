using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class AudioGapRipAndRoundTripTests : IDisposable
{
    private const int SectorSize = OpticalConstants.RawSectorSize;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-gap-" + Guid.NewGuid().ToString("N"));

    public AudioGapRipAndRoundTripTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] RandomPcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * SectorSize];
        new Random(seed).NextBytes(pcm);
        // Ensure at least one non-zero byte even on tiny slices
        if (pcm.Length > 0 && pcm.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            pcm[0] = 0x5A;
        }
        return pcm;
    }

    private (NativeAudioBurnEngine Engine, Func<VirtualMmcDrive?> Drive) CreateEngine()
    {
        VirtualMmcDrive? seen = null;
        var engine = new NativeAudioBurnEngine(_dir)
        {
            Wait = _ => { },
            ConfigureVirtualDrive = d => seen = d
        };
        return (engine, () => seen);
    }

    private static VirtualDiscRecorder CreateRecorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    private static byte[] ReadWavPcm(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        return all.AsSpan(44).ToArray();
    }

    [Fact]
    public async Task RipAudioCdAsync_DetectsHtoaPreGapFlagsAndMcn_WithBinarySearchAndExactCue()
    {
        byte[] htoaPcm = RandomPcm(100, 750);
        byte[] t1MainPcm = RandomPcm(101, 2100);
        byte[] t2GapPcm = RandomPcm(102, 150);
        byte[] t2MainPcm = RandomPcm(103, 300);
        byte[] t3MainPcm = RandomPcm(104, 300);

        byte[] cdText = CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata(
            "Gap Album",
            "Gap Artist",
            Songwriter: "Writer",
            Tracks: new[]
            {
                new CdTextTrackMetadata(1, "Opening", "Gap Artist"),
                new CdTextTrackMetadata(2, "PreGap Track", "Gap Artist", Isrc: "USAA12600011"),
                new CdTextTrackMetadata(3, "Gapless Track", "Gap Artist", Isrc: "USAA12600012")
            }));

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "Opening",
                Performer = "Gap Artist",
                PreGapFrames = 150,
                PreGapAudioFrames = 750,
                PreGapPcmStreamFactory = () => new MemoryStream(htoaPcm),
                TotalPcmBytes = t1MainPcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1MainPcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "PreGap Track",
                Performer = "Gap Artist",
                Isrc = "USAA12600011",
                PreGapFrames = 0,
                PreGapAudioFrames = 150,
                PreGapPcmStreamFactory = () => new MemoryStream(t2GapPcm),
                PreEmphasis = true,
                CopyPermitted = true,
                TotalPcmBytes = t2MainPcm.Length,
                PcmStreamFactory = () => new MemoryStream(t2MainPcm)
            },
            new()
            {
                TrackNumber = 3,
                Title = "Gapless Track",
                Performer = "Gap Artist",
                Isrc = "USAA12600012",
                PreGapFrames = 0,
                TotalPcmBytes = t3MainPcm.Length,
                PcmStreamFactory = () => new MemoryStream(t3MainPcm)
            }
        };

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, getDrive) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            recorder,
            sources,
            new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                CdTextBytes = cdText,
                MediaCatalogNumber = "0012345678905"
            },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);

        var drive = getDrive()!;
        int readsBeforeRip = drive.SubChannelReadCdCommands;

        string ripDir = Path.Combine(_dir, "rip-htoa");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(
            recorder,
            new VirtualBurnEngine(recorder),
            ripDir,
            new AudioRipOptions
            {
                GenerateCueSheet = true,
                SecureMode = false,
                CalculateReplayGain = false
            },
            null,
            CancellationToken.None);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TracksRipped);
        Assert.Equal(3, ripResult.TrackInfos.Count);
        Assert.NotNull(ripResult.HiddenTrackOneAudioPath);
        Assert.Equal(4, ripResult.RippedFilePaths.Count);
        Assert.Equal(ripResult.HiddenTrackOneAudioPath, ripResult.RippedFilePaths[0]);
        Assert.True(File.Exists(ripResult.HiddenTrackOneAudioPath));

        int subChannelProbes = drive.SubChannelReadCdCommands - readsBeforeRip;
        Assert.InRange(subChannelProbes, 1, 20);

        string cueText = await File.ReadAllTextAsync(ripResult.CueSheetPath!);
        Assert.Contains("CATALOG 0012345678905", cueText);
        Assert.Contains("FILE \"00 - Gap Artist - Hidden Track One Audio.wav\" WAVE", cueText);
        Assert.Contains("    INDEX 00 00:00:00", cueText);
        Assert.Contains("FILE \"01 - Gap Artist - Opening.wav\" WAVE", cueText);
        Assert.Contains("    FLAGS DCP PRE", cueText);
        Assert.Contains("    INDEX 00 00:28:00", cueText);
        Assert.Contains("    ISRC USAA12600011", cueText);
        Assert.Contains("    ISRC USAA12600012", cueText);

        var parsed = CueSheetParser.Parse(cueText);
        Assert.Equal("0012345678905", parsed.Catalog);
        Assert.Equal(3, parsed.Files.Count);
        Assert.NotNull(parsed.Files[0].Tracks[0].SplitIndex00);
        Assert.Equal("00 - Gap Artist - Hidden Track One Audio.wav", parsed.Files[0].Tracks[0].SplitIndex00!.DedicatedFileName);
        Assert.Null(parsed.Files[0].Tracks[0].PreGap);
        Assert.Equal(new[] { "DCP", "PRE" }, parsed.Files[1].Tracks[0].Flags);
        Assert.Equal(new MsfAddress(0, 28, 0), parsed.Files[1].Tracks[0].SplitIndex00!.Position);
        Assert.Null(parsed.Files[2].Tracks[0].SplitIndex00);

        Assert.Equal(htoaPcm, ReadWavPcm(ripResult.HiddenTrackOneAudioPath!));
        Assert.Equal(t1MainPcm.Concat(t2GapPcm).ToArray(), ReadWavPcm(ripResult.TrackInfos[0].FilePath));
        Assert.Equal(t2MainPcm, ReadWavPcm(ripResult.TrackInfos[1].FilePath));
        Assert.Equal(t3MainPcm, ReadWavPcm(ripResult.TrackInfos[2].FilePath));
    }

    [Fact]
    public async Task RipAudioCdAsync_SilentExtendedTrack1PreGap_EmitsPregapWithoutTrack0FileAndReburnsAtSameLba()
    {
        byte[] t1Pcm = RandomPcm(201, 300);
        byte[] t2Pcm = RandomPcm(202, 300);

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "Track One",
                Performer = "Silent Gap Artist",
                PreGapFrames = 375, // 150 lead-in + 225 silent frames at LBA 0..224
                PreEmphasis = true,
                TotalPcmBytes = t1Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1Pcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Track Two",
                Performer = "Silent Gap Artist",
                PreGapFrames = 0,
                TotalPcmBytes = t2Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t2Pcm)
            }
        };

        using var srcRecorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, _) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            srcRecorder,
            sources,
            new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = false },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(225, srcRecorder.CurrentMedium!.Sessions[0].Tracks[0].StartLba);

        string ripDir = Path.Combine(_dir, "rip-silent-pregap");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(
            srcRecorder,
            new VirtualBurnEngine(srcRecorder),
            ripDir,
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = false, CalculateReplayGain = false },
            null,
            CancellationToken.None);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Null(ripResult.HiddenTrackOneAudioPath);
        Assert.Equal(2, ripResult.RippedFilePaths.Count);
        Assert.Empty(Directory.GetFiles(ripDir, "00 - *.wav"));

        string cueText = await File.ReadAllTextAsync(ripResult.CueSheetPath!);
        Assert.Contains("    FLAGS PRE", cueText);
        Assert.DoesNotContain("FLAGS DCP", cueText);
        Assert.Contains("    PREGAP 00:03:00", cueText);

        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(ripResult.CueSheetPath!);
        Assert.Equal(375, compilation.Tracks[0].PreGapFrames);
        Assert.True(compilation.Tracks[0].PreEmphasis);
        Assert.False(compilation.Tracks[0].CopyPermitted);

        using var dstRecorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (dstEngine, _) = CreateEngine();
        var burnService = new AudioCdBurnService(dstEngine);
        var reburn = await burnService.BurnCompilationAsync(
            dstRecorder,
            compilation,
            new AudioBurnOptions { UseDiscAtOnce = true, KeepIndexMarks = true });
        Assert.True(reburn.Success, reburn.ErrorMessage);
        Assert.Equal(225, dstRecorder.CurrentMedium!.Sessions[0].Tracks[0].StartLba);
    }

    [Fact]
    public async Task SubChannelFallback_WhenDriveRejectsReadCdSubChannel_FindsSameIndex00AndFlagsPre()
    {
        byte[] t1Pcm = RandomPcm(301, 600);
        byte[] t2GapPcm = RandomPcm(302, 150);
        byte[] t2Pcm = RandomPcm(303, 300);

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "First",
                Performer = "Artist",
                PreGapFrames = 150,
                PreEmphasis = true,
                TotalPcmBytes = t1Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1Pcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Second",
                Performer = "Artist",
                PreGapFrames = 0,
                PreGapAudioFrames = 150,
                PreGapPcmStreamFactory = () => new MemoryStream(t2GapPcm),
                PreEmphasis = true,
                TotalPcmBytes = t2Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t2Pcm)
            }
        };

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, getDrive) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            recorder,
            sources,
            new AudioBurnOptions { UseDiscAtOnce = true },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);

        var drive = getDrive()!;
        drive.RejectsReadCdSubChannel = true;

        string ripDir = Path.Combine(_dir, "rip-fallback");
        var ripResult = await new AudioCdRipperService().RipAudioCdAsync(
            recorder,
            new VirtualBurnEngine(recorder),
            ripDir,
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = false, CalculateReplayGain = false });

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(0, drive.SubChannelReadCdCommands);

        var cue = CueSheetParser.ParseFile(ripResult.CueSheetPath!);
        Assert.Contains("PRE", cue.Files[0].Tracks[0].Flags);
        Assert.Contains("PRE", cue.Files[1].Tracks[0].Flags);
        Assert.Equal(new MsfAddress(0, 8, 0), cue.Files[1].Tracks[0].SplitIndex00!.Position);
    }

    [Fact]
    public async Task SubChannelBcdCalibration_OnTwelveTrackDisc_FindsTrack10To12Index00()
    {
        var sources = new List<AudioTrackSource>(12);
        for (int t = 1; t <= 12; t++)
        {
            byte[] main = RandomPcm(400 + t, 300);
            int gap = t switch
            {
                10 => 75,
                12 => 100,
                _ => 0
            };
            byte[]? gapAudio = gap > 0 ? RandomPcm(500 + t, gap) : null;
            sources.Add(new AudioTrackSource
            {
                TrackNumber = t,
                Title = $"Track {t:D2}",
                Performer = "BCD Band",
                PreGapFrames = t == 1 ? 150 : 0,
                PreGapAudioFrames = gap,
                PreGapPcmStreamFactory = gapAudio != null ? () => new MemoryStream(gapAudio) : null,
                TotalPcmBytes = main.Length,
                PcmStreamFactory = () => new MemoryStream(main)
            });
        }

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, getDrive) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            recorder,
            sources,
            new AudioBurnOptions { UseDiscAtOnce = true },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);

        getDrive()!.ReturnsBcdInReadCdSubChannel = true;

        string ripDir = Path.Combine(_dir, "rip-bcd");
        var ripResult = await new AudioCdRipperService().RipAudioCdAsync(
            recorder,
            new VirtualBurnEngine(recorder),
            ripDir,
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = false, CalculateReplayGain = false });

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        var cue = CueSheetParser.ParseFile(ripResult.CueSheetPath!);
        Assert.Equal(12, cue.Files.Count);

        // Track 9 WAV = 300 main + 75 Track 10 gap -> Track 10 INDEX 00 at 00:04:00 (300 frames)
        Assert.NotNull(cue.Files[9].Tracks[0].SplitIndex00);
        Assert.Equal(new MsfAddress(0, 4, 0), cue.Files[9].Tracks[0].SplitIndex00!.Position);

        // Track 11 is gapless -> no INDEX 00
        Assert.Null(cue.Files[10].Tracks[0].SplitIndex00);

        // Track 11 WAV = 300 main + 100 Track 12 gap -> Track 12 INDEX 00 at 00:04:00 (300 frames)
        Assert.NotNull(cue.Files[11].Tracks[0].SplitIndex00);
        Assert.Equal(new MsfAddress(0, 4, 0), cue.Files[11].Tracks[0].SplitIndex00!.Position);
    }

    [Fact]
    public async Task GapDetector_NonPositionFramesAndTocShiftWithoutIndex00_NeverInventsPhantomGap()
    {
        byte[] t1Pcm = RandomPcm(601, 600);
        byte[] t2Pcm = RandomPcm(602, 300);

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "One",
                Performer = "Artist",
                PreGapFrames = 150,
                TotalPcmBytes = t1Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1Pcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Two",
                Performer = "Artist",
                PreGapFrames = 0,
                TotalPcmBytes = t2Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t2Pcm)
            }
        };

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, getDrive) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            recorder,
            sources,
            new AudioBurnOptions { UseDiscAtOnce = true },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);

        var drive = getDrive()!;
        drive.ShiftsTrackStartsInToc = true;
        drive.NonPositionSubChannelLbas.Add(300);
        drive.NonPositionSubChannelLbas.Add(598);
        drive.NonPositionSubChannelLbas.Add(599);

        // Read shifted TOC directly from the MMC drive so Track 2 starts at LBA 601 while Q INDEX 01 is at LBA 600.
        byte[] tocBuf = new byte[64];
        var tocRes = ((IScsiTransport)drive).ExecuteCommand(
            MmcCommandBuilder.BuildReadTocPmaAtipCdb(MmcCommandBuilder.TocFormatFormattedToc, 1, 64, msf: false),
            tocBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            TimeSpan.FromSeconds(5));
        Assert.True(tocRes.IsSuccess);
        var tocTracks = MmcResponseParser.ParseToc(tocBuf.AsSpan(0, (int)tocRes.DataTransferLength), isMsf: false);
        Assert.Equal(601, tocTracks[1].StartLba);

        var discovered = new List<AudioDiscoveredTrack>
        {
            new(1, tocTracks[0].StartLba, tocTracks[1].StartLba - tocTracks[0].StartLba, 150, "One", "Artist"),
            new(2, tocTracks[1].StartLba, tocTracks[2].StartLba - tocTracks[1].StartLba, 0, "Two", "Artist")
        };

        using var session = new MmcScsiSubChannelSession(drive, ownsTransport: false);
        var detected = AudioGapDetector.Detect(discovered, session);

        Assert.Null(detected.Tracks[2].Index00Lba);
    }

    [Fact]
    public async Task RipAudioCdAsync_DeletesPartialWavOnCancellation_AndPreservesIndex00WhenPrecedingTrackSkipped()
    {
        byte[] t1Pcm = RandomPcm(701, 600);
        byte[] t2GapPcm = RandomPcm(702, 150);
        byte[] t2Pcm = RandomPcm(703, 300);

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "First",
                Performer = "Artist",
                PreGapFrames = 150,
                TotalPcmBytes = t1Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1Pcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Second",
                Performer = "Artist",
                PreGapFrames = 0,
                PreGapAudioFrames = 150,
                PreGapPcmStreamFactory = () => new MemoryStream(t2GapPcm),
                TotalPcmBytes = t2Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t2Pcm)
            }
        };

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, _) = CreateEngine();
        var burnResult = await burnEngine.BurnAudioTracksAsync(
            recorder,
            sources,
            new AudioBurnOptions { UseDiscAtOnce = true },
            null,
            CancellationToken.None);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);

        // 1. Rip only track 2 (skipping track 1): CUE sheet preserves Track 2's 2-second pre-gap as PREGAP
        string partialRipDir = Path.Combine(_dir, "rip-track2-only");
        var ripper = new AudioCdRipperService();
        var track2Only = await ripper.RipAudioCdAsync(
            recorder,
            new VirtualBurnEngine(recorder),
            partialRipDir,
            new AudioRipOptions
            {
                TrackNumbersToRip = [2],
                GenerateCueSheet = true,
                SecureMode = false,
                CalculateReplayGain = false
            });
        Assert.True(track2Only.Success, track2Only.ErrorMessage);
        CueSheet parsed = CueSheetParser.ParseFile(track2Only.CueSheetPath!);
        Assert.Single(parsed.Files);
        Assert.Equal(new MsfAddress(0, 2, 0), parsed.Files[0].Tracks[0].PreGap);

        // 2. Cancel mid-rip: partial .wav file must be deleted cleanly in finally/catch
        string cancelDir = Path.Combine(_dir, "rip-cancel");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<BurnProgressSnapshot>(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ripper.RipAudioCdAsync(
                recorder,
                new VirtualBurnEngine(recorder),
                cancelDir,
                new AudioRipOptions
                {
                    GenerateCueSheet = false,
                    SecureMode = false,
                    CalculateReplayGain = false
                },
                progress,
                cts.Token));
        Assert.Empty(Directory.GetFiles(cancelDir, "*.wav"));
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
