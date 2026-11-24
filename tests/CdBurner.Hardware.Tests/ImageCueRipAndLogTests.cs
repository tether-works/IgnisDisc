using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class ImageCueRipAndLogTests : IDisposable
{
    private const int SectorSize = OpticalConstants.RawSectorSize;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-imagecue-" + Guid.NewGuid().ToString("N"));

    public ImageCueRipAndLogTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task RipImageCue_Flac_WithHtoaPreGapsCdTextIsrcFlagsAndCatalog_ReloadsViaSingleFileAlbumSourceBitIdenticallyAndValidatesLog()
    {
        byte[] htoaPcm = RandomPcm(1001, 300);
        byte[] t1MainPcm = RandomPcm(1002, 900);
        byte[] t2GapPcm = RandomPcm(1003, 150);
        byte[] t2MainPcm = RandomPcm(1004, 450);
        byte[] t3MainPcm = RandomPcm(1005, 375);

        byte[] cdText = CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata(
            "Archival Image Album",
            "Studio Quartet",
            Songwriter: "Album Composer",
            Tracks: new[]
            {
                new CdTextTrackMetadata(1, "Prelude", "Studio Quartet", Songwriter: "Writer One", Isrc: "USAA12600101"),
                new CdTextTrackMetadata(2, "Interlude", "Guest Soloist", Songwriter: "Writer Two", Isrc: "USAA12600102"),
                new CdTextTrackMetadata(3, "Finale", "Studio Quartet", Songwriter: "Writer Three", Isrc: "USAA12600103")
            }));

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "Prelude",
                Performer = "Studio Quartet",
                Songwriter = "Writer One",
                Isrc = "USAA12600101",
                PreGapFrames = 150,
                PreGapAudioFrames = 300,
                PreGapPcmStreamFactory = () => new MemoryStream(htoaPcm),
                TotalPcmBytes = t1MainPcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1MainPcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Interlude",
                Performer = "Guest Soloist",
                Songwriter = "Writer Two",
                Isrc = "USAA12600102",
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
                Title = "Finale",
                Performer = "Studio Quartet",
                Songwriter = "Writer Three",
                Isrc = "USAA12600103",
                PreGapFrames = 0,
                FourChannel = true,
                TotalPcmBytes = t3MainPcm.Length,
                PcmStreamFactory = () => new MemoryStream(t3MainPcm)
            }
        };

        using var recorder = CreateRecorder(VirtualDiscMedium.CreateBlankCdR700());
        var (burnEngine, _) = CreateEngine();
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

        string ripDir = Path.Combine(_dir, "rip-image-cue-flac");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(
            recorder,
            new VirtualBurnEngine(recorder),
            ripDir,
            new AudioRipOptions
            {
                Mode = AudioRipMode.ImageCue,
                ImageFormat = AudioRipFormat.Flac,
                GenerateCueSheet = true,
                SecureMode = true,
                CalculateReplayGain = true,
                WriteRipLog = true
            },
            null,
            CancellationToken.None);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(AudioRipMode.ImageCue, ripResult.Mode);
        Assert.NotNull(ripResult.ImageFilePath);
        Assert.True(File.Exists(ripResult.ImageFilePath));
        Assert.EndsWith(".flac", ripResult.ImageFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(ripResult.RippedFilePaths);
        Assert.Equal(300, ripResult.HiddenTrackOneAudioFrames);
        Assert.NotNull(ripResult.CueSheetPath);
        Assert.True(File.Exists(ripResult.CueSheetPath));
        Assert.NotNull(ripResult.RipLogPath);
        Assert.True(File.Exists(ripResult.RipLogPath));

        // Verify pure RFC 9639 FLAC integrity and MD5 match
        var flacIntegrity = FlacFileCodec.CheckIntegrity(ripResult.ImageFilePath!);
        Assert.False(flacIntegrity.IsDamaged, flacIntegrity.Describe());
        Assert.True(flacIntegrity.Md5Matches);

        // Verify CUE sheet contents
        string cueText = await File.ReadAllTextAsync(ripResult.CueSheetPath!);
        Assert.Contains("CATALOG 0012345678905", cueText);
        Assert.Contains("TITLE \"Archival Image Album\"", cueText);
        Assert.Contains("PERFORMER \"Studio Quartet\"", cueText);
        Assert.Contains("SONGWRITER \"Album Composer\"", cueText);
        Assert.Contains($"FILE \"{Path.GetFileName(ripResult.ImageFilePath)}\" WAVE", cueText);
        Assert.Contains("ISRC USAA12600101", cueText);
        Assert.Contains("ISRC USAA12600102", cueText);
        Assert.Contains("ISRC USAA12600103", cueText);
        Assert.Contains("FLAGS DCP PRE", cueText);
        Assert.Contains("FLAGS 4CH", cueText);

        var parsedCue = CueSheetParser.Parse(cueText);
        Assert.Single(parsedCue.Files);
        Assert.Equal(3, parsedCue.Files[0].Tracks.Count);
        Assert.Equal(new MsfAddress(0, 0, 0), parsedCue.Files[0].Tracks[0].Indices[0].Position);
        Assert.Equal(0, parsedCue.Files[0].Tracks[0].Indices[0].IndexNumber);
        Assert.Equal(new MsfAddress(0, 4, 0), parsedCue.Files[0].Tracks[0].Indices[1].Position);
        Assert.Equal(1, parsedCue.Files[0].Tracks[0].Indices[1].IndexNumber);
        Assert.Equal(new MsfAddress(0, 16, 0), parsedCue.Files[0].Tracks[1].Indices[0].Position);
        Assert.Equal(new MsfAddress(0, 18, 0), parsedCue.Files[0].Tracks[1].Indices[1].Position);
        Assert.Equal(new MsfAddress(0, 24, 0), parsedCue.Files[0].Tracks[2].Indices[0].Position);

        // Reload via SingleFileAlbumSource + AudioCdProject.ImportCueSheet and check bit-exactness
        var reloaded = new AudioCdCompilation();
        int imported = reloaded.ImportCueSheet(ripResult.CueSheetPath!);
        Assert.Equal(3, imported);
        Assert.Equal("0012345678905", reloaded.UpcEan);
        Assert.Equal("Archival Image Album", reloaded.AlbumTitle);
        Assert.Equal("Studio Quartet", reloaded.AlbumPerformer);

        Assert.Equal(300, reloaded.Tracks[0].HiddenAudioFrames);
        Assert.NotNull(reloaded.Tracks[0].HiddenAudioPcmStreamFactory);
        Assert.Equal(htoaPcm, ReadAll(reloaded.Tracks[0].HiddenAudioPcmStreamFactory!()));
        Assert.Equal(t1MainPcm.Concat(t2GapPcm).ToArray(), ReadAll(reloaded.Tracks[0].CustomPcmStreamFactory!()));
        Assert.Equal(t2MainPcm, ReadAll(reloaded.Tracks[1].CustomPcmStreamFactory!()));
        Assert.Equal(t3MainPcm, ReadAll(reloaded.Tracks[2].CustomPcmStreamFactory!()));

        Assert.True(reloaded.Tracks[1].PreEmphasis);
        Assert.True(reloaded.Tracks[1].CopyPermitted);
        Assert.Equal(150, reloaded.Tracks[1].Index00LeadFrames);
        Assert.True(reloaded.Tracks[2].FourChannel);

        // Also slice directly through SingleFileAlbumSource to verify exact track boundaries
        var albumSource = new SingleFileAlbumSource(ripResult.ImageFilePath!);
        Assert.Equal(300 + 900 + 150 + 450 + 375, albumSource.TotalFrames);
        Assert.Equal(htoaPcm, ReadAll(albumSource.CreateSliceFactory(0, 300)()));
        Assert.Equal(t1MainPcm, ReadAll(albumSource.CreateSliceFactory(300, 900)()));
        Assert.Equal(t2GapPcm, ReadAll(albumSource.CreateSliceFactory(1200, 150)()));
        Assert.Equal(t2MainPcm, ReadAll(albumSource.CreateSliceFactory(1350, 450)()));
        Assert.Equal(t3MainPcm, ReadAll(albumSource.CreateSliceFactory(1800, 375)()));

        // Validate generated rip log and tamper detection
        string logText = await File.ReadAllTextAsync(ripResult.RipLogPath!);
        Assert.Contains("Image + CUE (Single-file)", logText);
        Assert.Contains("Output format       : FLAC", logText);
        Assert.Contains("Hidden Track One Audio (HTOA)", logText);
        Assert.Contains("Flags          DCP PRE", logText);
        Assert.Contains("Flags          4CH", logText);
        Assert.True(RipLogValidator.ValidateFile(ripResult.RipLogPath!).IsValid);

        string tamperedLog = logText.Replace("Archival Image Album", "Tampered Album", StringComparison.Ordinal);
        Assert.False(RipLogValidator.Validate(tamperedLog).IsValid);
    }

    [Fact]
    public async Task RipImageCue_SilentExtendedTrack1PreGap_PreservesPregapDirectiveAndReburnsAtSameLba()
    {
        byte[] t1Pcm = RandomPcm(2001, 300);
        byte[] t2Pcm = RandomPcm(2002, 300);

        var sources = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "Track One",
                Performer = "Silent Artist",
                PreGapFrames = 375, // 150 lead-in + 225 silent frames at LBA 0..224
                TotalPcmBytes = t1Pcm.Length,
                PcmStreamFactory = () => new MemoryStream(t1Pcm)
            },
            new()
            {
                TrackNumber = 2,
                Title = "Track Two",
                Performer = "Silent Artist",
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

        string ripDir = Path.Combine(_dir, "rip-image-silent-pregap");
        var ripResult = await new AudioCdRipperService().RipAudioCdAsync(
            srcRecorder,
            new VirtualBurnEngine(srcRecorder),
            ripDir,
            new AudioRipOptions
            {
                Mode = AudioRipMode.ImageCue,
                ImageFormat = AudioRipFormat.Wav,
                GenerateCueSheet = true,
                SecureMode = false,
                CalculateReplayGain = false
            });

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(0, ripResult.HiddenTrackOneAudioFrames);
        Assert.EndsWith(".wav", ripResult.ImageFilePath!, StringComparison.OrdinalIgnoreCase);

        string cueText = await File.ReadAllTextAsync(ripResult.CueSheetPath!);
        Assert.Contains("PREGAP 00:03:00", cueText);

        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(ripResult.CueSheetPath!);
        Assert.Equal(375, compilation.Tracks[0].PreGapFrames);
        Assert.Equal(t1Pcm, ReadAll(compilation.Tracks[0].CustomPcmStreamFactory!()));
        Assert.Equal(t2Pcm, ReadAll(compilation.Tracks[1].CustomPcmStreamFactory!()));
    }
}
