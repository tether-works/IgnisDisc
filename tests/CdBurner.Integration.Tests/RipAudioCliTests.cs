using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration tests for the <c>rip-audio</c> CLI command covering uncompressed WAV extraction,
/// lossless FLAC transcoding with metadata tags and CUE rewriting, track subset selection,
/// error validation, and disc ID calculation.
/// </summary>
public sealed class RipAudioCliTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly VirtualDiscMedium _medium;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public RipAudioCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipAudioCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder(
            uniqueId: "VDRV-01",
            vendorId: "IGNIS",
            productId: "VIRTUAL DVD/CD-RW",
            volumePath: @"V:\");

        _medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(_medium);

        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public async Task InitializeAsync()
    {
        // 1. Generate 3 synthetic sine-wave PCM streams (as in the audio burn tests)
        // Track 1: 440 Hz, 50 frames
        // Track 2: 880 Hz, 75 frames
        // Track 3: 1760 Hz, 100 frames
        const int track1Frames = 50;
        const int track2Frames = 75;
        const int track3Frames = 100;

        byte[] track1Pcm = GenerateSineWavePcm(440.0, track1Frames);
        byte[] track2Pcm = GenerateSineWavePcm(880.0, track2Frames);
        byte[] track3Pcm = GenerateSineWavePcm(1760.0, track3Frames);

        string wavDir = Path.Combine(_tempDir, "input_wavs");
        Directory.CreateDirectory(wavDir);

        string wavPath1 = Path.Combine(wavDir, "input_track1.wav");
        string wavPath2 = Path.Combine(wavDir, "input_track2.wav");
        string wavPath3 = Path.Combine(wavDir, "input_track3.wav");

        WavFileCodec.WriteWavFile(wavPath1, track1Pcm);
        WavFileCodec.WriteWavFile(wavPath2, track2Pcm);
        WavFileCodec.WriteWavFile(wavPath3, track3Pcm);

        // 2. Create compilation with Album/Track CD-Text metadata and DiscAtOnceGapless mode
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Synthetic Harmonics Vol 1",
            AlbumPerformer = "The Oscillation Trio",
            Songwriter = "Nikola Tesla",
            Composer = "Johann Sebastian Sine",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        compilation.AddTrack(new AudioCdTrackItem(
            1,
            wavPath1,
            "A4 Concert Pitch",
            "The Oscillation Trio",
            "USAA12600001",
            PreGapFrames: 150));

        compilation.AddTrack(new AudioCdTrackItem(
            2,
            wavPath2,
            "A5 Octave Leap",
            "The Oscillation Trio",
            "USAA12600002",
            PreGapFrames: 0));

        compilation.AddTrack(new AudioCdTrackItem(
            3,
            wavPath3,
            "A6 Harmonic Shimmer",
            "The Oscillation Trio",
            "USAA12600003",
            PreGapFrames: 0));

        // 3. Burn compilation to virtual recorder with CD-Text
        using var decoder = new MediaFoundationAudioDecoder();
        var burnService = new AudioCdBurnService(_burnEngine, decoder);
        var burnResult = await burnService.BurnCompilationAsync(_recorder, compilation);

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(track1Frames + track2Frames + track3Frames, burnResult.SectorsWritten);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _recorder.Dispose();
        _medium.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup issues
        }
    }

    private static (StringWriter stdout, StringWriter stderr) CreateStringWriters() =>
        (new StringWriter(), new StringWriter());

    private static JsonDocument ParseJsonOutput(StringWriter stdout)
    {
        string text = stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected non-empty stdout JSON output.");
        return JsonDocument.Parse(text);
    }

    private static byte[] GenerateSineWavePcm(double frequency, int frameCount, short amplitude = 16000)
    {
        int sampleRate = OpticalConstants.CdAudioSampleRate;
        int totalStereoSamples = frameCount * OpticalConstants.CdAudioSamplesPerFrame;
        byte[] pcm = new byte[totalStereoSamples * OpticalConstants.CdAudioBytesPerSample];

        for (int i = 0; i < totalStereoSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }

        return pcm;
    }

    [Fact]
    public async Task RipAudio_Wav_Json_RipsAllTracksAndReportsDiscIds()
    {
        string outDir = Path.Combine(_tempDir, "WavJsonOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Equal(3, wavFiles.Length);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.Equal(Path.GetFullPath(outDir), root.GetProperty("outputFolder").GetString());
        Assert.Equal(3, root.GetProperty("tracksRipped").GetInt32());
        Assert.Equal(225, root.GetProperty("sectorsRead").GetInt32());

        string? mbId = root.GetProperty("musicBrainzId").GetString();
        Assert.NotNull(mbId);
        Assert.Equal(28, mbId.Length);

        string? freeDb = root.GetProperty("freeDbId").GetString();
        Assert.NotNull(freeDb);
        Assert.Equal(8, freeDb.Length);

        Assert.Equal("cd-text", root.GetProperty("metadataSource").GetString());
        Assert.NotEqual("off", root.GetProperty("secureMode").GetString()); // secure reading is the default

        Assert.Contains(wavFiles, f => f.Contains("A4 Concert Pitch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wavFiles, f => f.Contains("A5 Octave Leap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wavFiles, f => f.Contains("A6 Harmonic Shimmer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RipAudio_CleanRip_IsComplete_WithExitCode0()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", Path.Combine(_tempDir, "Clean"), "--json" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("suspectSectors").GetInt32());
    }

    [Fact]
    public async Task RipAudio_SectorStillSuspectAfterReReads_IsNotComplete_WithExitCode2()
    {
        _recorder.CurrentMedium!.SimulatedC2Errors[80] = 1000; // never reads cleanly
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", Path.Combine(_tempDir, "Suspect"), "--json" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(2, exitCode);
        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());    // the files were written
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());  // but not all of them can be trusted
        Assert.Equal(1, doc.RootElement.GetProperty("suspectSectors").GetInt32());
    }

    [Fact]
    public async Task RipAudio_SectorStillSuspect_SaysSoInTheFirstLine()
    {
        _recorder.CurrentMedium!.SimulatedC2Errors[80] = 1000;
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", Path.Combine(_tempDir, "SuspectText") },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(2, exitCode);
        Assert.StartsWith("Audio rip finished, but 1 sector(s) may be wrong (1 still suspect after re-reads, 0 unreadable):",
            stdout.ToString().Split(Environment.NewLine).First(l => l.StartsWith("Audio rip", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RipAudio_NoSecure_ReadsOnce()
    {
        string outDir = Path.Combine(_tempDir, "NoSecureOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--no-secure", "--json" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = ParseJsonOutput(stdout);
        Assert.Equal("off", doc.RootElement.GetProperty("secureMode").GetString());
    }

    [Fact]
    public async Task RipAudio_TrackSubset_RipsOnlyRequestedTracks()
    {
        string outDir = Path.Combine(_tempDir, "SubsetOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--tracks", "1,3" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Equal(2, wavFiles.Length);
        Assert.Contains(wavFiles, f => Path.GetFileName(f).StartsWith("01", StringComparison.Ordinal));
        Assert.Contains(wavFiles, f => Path.GetFileName(f).StartsWith("03", StringComparison.Ordinal));
        Assert.DoesNotContain(wavFiles, f => Path.GetFileName(f).StartsWith("02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RipAudio_InvalidFormat_ReturnsError()
    {
        string outDir = Path.Combine(_tempDir, "InvalidFormatOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            // A format no build can make: "mp3" depended on whether the installed FFmpeg has an MP3 encoder.
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "xyz" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
        string err = stderr.ToString();
        Assert.Contains("Invalid audio format 'xyz'", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RipAudio_InvalidTracks_ReturnsError()
    {
        string outDir = Path.Combine(_tempDir, "InvalidTracksOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--tracks", "1,x" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("20000")]
    public async Task RipAudio_InvalidReadOffset_ReturnsError(string offset)
    {
        string outDir = Path.Combine(_tempDir, "InvalidOffsetOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--read-offset", offset },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
        Assert.Contains("read offset", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RipAudio_Json_ReportsReadOffsetAndPerTrackChecksums()
    {
        string outDir = Path.Combine(_tempDir, "ChecksumOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--read-offset", "-6", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = ParseJsonOutput(stdout);
        Assert.Equal(-6, doc.RootElement.GetProperty("readOffset").GetInt32());
        var checksums = doc.RootElement.GetProperty("trackChecksums");
        Assert.Equal(3, checksums.GetArrayLength());
        foreach (var t in checksums.EnumerateArray())
        {
            Assert.Equal(8, t.GetProperty("crc32").GetString()!.Length);
            Assert.Equal(8, t.GetProperty("accurateRipV1").GetString()!.Length);
            Assert.Equal(8, t.GetProperty("accurateRipV2").GetString()!.Length);
        }
    }

    [Fact]
    public async Task RipAudio_Flac_EncodesAndRewritesCue()
    {
        if (!FfmpegLocator.IsFfmpegAvailable())
        {
            return;
        }

        string outDir = Path.Combine(_tempDir, "FlacOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "flac" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        var flacFiles = Directory.GetFiles(outDir, "*.flac");
        Assert.Equal(3, flacFiles.Length);

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Empty(wavFiles);

        string cuePath = Path.Combine(outDir, "album.cue");
        Assert.True(File.Exists(cuePath), "Expected album.cue companion file to exist.");

        string cueContent = File.ReadAllText(cuePath);
        Assert.Contains(".flac", cueContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".wav", cueContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlacEncoder_BuildArguments_OrdersArgumentsAndOmitsEmptyTags()
    {
        // 1. Without ISRC and Date
        var tagsNoOpt = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: null,
            Date: null);

        var argsNoOpt = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsNoOpt);

        Assert.Equal("-hide_banner", argsNoOpt[0]);
        Assert.Equal("-nostdin", argsNoOpt[1]);
        Assert.Equal("-y", argsNoOpt[2]);
        Assert.Equal("-i", argsNoOpt[3]);
        Assert.Equal(@"C:\test\in.wav", argsNoOpt[4]);
        Assert.Equal("-map_metadata", argsNoOpt[5]);
        Assert.Equal("-1", argsNoOpt[6]);
        Assert.Equal("-c:a", argsNoOpt[7]);
        Assert.Equal("flac", argsNoOpt[8]);
        Assert.Equal("-compression_level", argsNoOpt[9]);
        Assert.Equal("8", argsNoOpt[10]);

        Assert.Contains("TITLE=Track Alpha", argsNoOpt);
        Assert.Contains("ARTIST=Artist Beta", argsNoOpt);
        Assert.Contains("ALBUM=Album Gamma", argsNoOpt);
        Assert.Contains("TRACKNUMBER=2", argsNoOpt);
        Assert.Contains("TRACKTOTAL=10", argsNoOpt);

        Assert.DoesNotContain(argsNoOpt, a => a.StartsWith("ISRC=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(argsNoOpt, a => a.StartsWith("DATE=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\test\out.flac", argsNoOpt[^1]);

        // 2. With ISRC and Date
        var tagsWithOpt = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: "USAA12600001",
            Date: "2026-10-30");

        var argsWithOpt = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsWithOpt);

        Assert.Contains("ISRC=USAA12600001", argsWithOpt);
        Assert.Contains("DATE=2026-10-30", argsWithOpt);
        Assert.Equal(@"C:\test\out.flac", argsWithOpt[^1]);

        int isrcIdx = argsWithOpt.ToList().IndexOf("ISRC=USAA12600001");
        Assert.True(isrcIdx > 0);
        Assert.Equal("-metadata", argsWithOpt[isrcIdx - 1]);

        int dateIdx = argsWithOpt.ToList().IndexOf("DATE=2026-10-30");
        Assert.True(dateIdx > 0);
        Assert.Equal("-metadata", argsWithOpt[dateIdx - 1]);

        // 3. Empty / whitespace strings should be omitted
        var tagsEmpty = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: "   ",
            Date: "");

        var argsEmpty = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsEmpty);
        Assert.DoesNotContain(argsEmpty, a => a.StartsWith("ISRC=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(argsEmpty, a => a.StartsWith("DATE=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RipAudio_BurnNativeCueWithoutDao_VerifyCue_RoundTripsProgramAreaAndCueSheetIdentically()
    {
        byte[] Pcm(int seed, int frames)
        {
            byte[] buf = new byte[frames * OpticalConstants.RawSectorSize];
            new Random(seed).NextBytes(buf);
            if (buf.Length > 0 && buf.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                buf[0] = 0x5A;
            }
            return buf;
        }

        byte[] htoa = Pcm(700, 750);
        byte[] t1Main = Pcm(701, 2100);
        byte[] t2Gap = Pcm(702, 150);
        byte[] t2Main = Pcm(703, 300);
        byte[] t3Main = Pcm(704, 300);

        byte[] cdText = CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata(
            "RoundTrip Album",
            "RoundTrip Artist",
            Tracks: new[]
            {
                new CdTextTrackMetadata(1, "Track One", "RoundTrip Artist"),
                new CdTextTrackMetadata(2, "Track Two", "RoundTrip Artist", Isrc: "USAA12600021"),
                new CdTextTrackMetadata(3, "Track Three", "RoundTrip Artist", Isrc: "USAA12600022")
            }));

        var srcRecorder = new VirtualDiscRecorder("VDRV-SRC", "IGNIS", "VIRTUAL SRC", @"S:\");
        var srcMedium = VirtualDiscMedium.CreateBlankCdR700();
        srcRecorder.InsertMedia(srcMedium);

        var dstRecorder = new VirtualDiscRecorder("VDRV-DST", "IGNIS", "VIRTUAL DST", @"D:\");
        var dstMedium = VirtualDiscMedium.CreateBlankCdR700();
        dstRecorder.InsertMedia(dstMedium);

        var multiDeviceService = new TestDeviceService(srcRecorder, dstRecorder);
        var srcEngine = new NativeAudioBurnEngine(Path.Combine(_tempDir, "src-logs"));

        var srcBurn = await srcEngine.BurnAudioTracksAsync(
            srcRecorder,
            new List<AudioTrackSource>
            {
                new()
                {
                    TrackNumber = 1,
                    Title = "Track One",
                    Performer = "RoundTrip Artist",
                    PreGapFrames = 150,
                    PreGapAudioFrames = 750,
                    PreGapPcmStreamFactory = () => new MemoryStream(htoa),
                    TotalPcmBytes = t1Main.Length,
                    PcmStreamFactory = () => new MemoryStream(t1Main)
                },
                new()
                {
                    TrackNumber = 2,
                    Title = "Track Two",
                    Performer = "RoundTrip Artist",
                    Isrc = "USAA12600021",
                    PreGapFrames = 0,
                    PreGapAudioFrames = 150,
                    PreGapPcmStreamFactory = () => new MemoryStream(t2Gap),
                    PreEmphasis = true,
                    CopyPermitted = true,
                    TotalPcmBytes = t2Main.Length,
                    PcmStreamFactory = () => new MemoryStream(t2Main)
                },
                new()
                {
                    TrackNumber = 3,
                    Title = "Track Three",
                    Performer = "RoundTrip Artist",
                    Isrc = "USAA12600022",
                    PreGapFrames = 0,
                    TotalPcmBytes = t3Main.Length,
                    PcmStreamFactory = () => new MemoryStream(t3Main)
                }
            },
            new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                CdTextBytes = cdText,
                MediaCatalogNumber = "0012345678905"
            },
            null,
            CancellationToken.None);
        Assert.True(srcBurn.Success, srcBurn.ErrorMessage);

        string ripOut = Path.Combine(_tempDir, "cli-roundtrip-rip");
        var (ripStdout, ripStderr) = CreateStringWriters();
        int ripExit = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-SRC", "--output", ripOut, "--no-replaygain", "--json" },
            ripStdout,
            ripStderr,
            multiDeviceService,
            new VirtualBurnEngine(srcRecorder));
        Assert.Equal(0, ripExit);

        string cuePath = Path.Combine(ripOut, "album.cue");
        Assert.True(File.Exists(cuePath));

        // Burn ripped .cue with --native (without --dao) onto fresh blank virtual medium.
        var (burnStdout, burnStderr) = CreateStringWriters();
        int burnExit = await CliCommandRunner.RunAsync(
            new[] { "burn-audio", "--drive", "VDRV-DST", "--tracks", cuePath, "--native", "--json" },
            burnStdout,
            burnStderr,
            multiDeviceService,
            new VirtualBurnEngine(dstRecorder));
        Assert.Equal(0, burnExit);

        var (verifyStdout, verifyStderr) = CreateStringWriters();
        int verifyExit = await CliCommandRunner.RunAsync(
            new[] { "verify", "--drive", "VDRV-DST", "--cue", cuePath, "--json" },
            verifyStdout,
            verifyStderr,
            multiDeviceService,
            new VirtualBurnEngine(dstRecorder));
        Assert.Equal(0, verifyExit);

        Assert.NotNull(srcMedium.RecordedMmcDrive);
        Assert.NotNull(dstMedium.RecordedMmcDrive);
        Assert.Equal(srcMedium.RecordedMmcDrive!.RecordedCueSheet, dstMedium.RecordedMmcDrive!.RecordedCueSheet);
        Assert.Equal(
            srcMedium.RecordedMmcDrive!.ProgramAreaWritten.ToArray(),
            dstMedium.RecordedMmcDrive!.ProgramAreaWritten.ToArray());
    }

    [Fact]
    public async Task RipAudio_EmitsCtdbIdsWithoutNetworkByDefault_AndQueriesWhenVerifyCtdbPassed()
    {
        string outOff = Path.Combine(_tempDir, "cli-ctdb-off");
        var spyOff = new SpyHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var clientOff = new HttpClient(spyOff);

        var (stdoutOff, stderrOff) = CreateStringWriters();
        int exitOff = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outOff, "--json"],
            stdoutOff,
            stderrOff,
            _deviceService,
            _burnEngine,
            clientOff);
        Assert.Equal(0, exitOff);
        Assert.Empty(spyOff.Requests);

        using var docOff = ParseJsonOutput(stdoutOff);
        var rootOff = docOff.RootElement;
        string? tocId = rootOff.GetProperty("ctdbTocId").GetString();
        string? discCrc = rootOff.GetProperty("ctdbDiscCrc").GetString();
        Assert.NotNull(tocId);
        Assert.Equal(28, tocId!.Length);
        Assert.NotNull(discCrc);
        Assert.Equal(8, discCrc!.Length);
        Assert.False(rootOff.TryGetProperty("ctdb", out _));

        var trackChecksums = rootOff.GetProperty("trackChecksums").EnumerateArray().ToList();
        Assert.Equal(3, trackChecksums.Count);
        var trackCrcs = new List<string>(3);
        foreach (var tc in trackChecksums)
        {
            string? c = tc.GetProperty("ctdbCrc").GetString();
            Assert.NotNull(c);
            Assert.Equal(8, c!.Length);
            trackCrcs.Add(c.ToLowerInvariant());
        }

        string xml = $"""<ctdb><entry id="42" crc32="{discCrc.ToLowerInvariant()}" confidence="7" trackcrcs="{string.Join(' ', trackCrcs)}" /></ctdb>""";
        string outOn = Path.Combine(_tempDir, "cli-ctdb-on");
        var spyOn = new SpyHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        });
        using var clientOn = new HttpClient(spyOn);

        var (stdoutOn, stderrOn) = CreateStringWriters();
        int exitOn = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outOn, "--verify-ctdb", "--json"],
            stdoutOn,
            stderrOn,
            _deviceService,
            _burnEngine,
            clientOn);
        Assert.Equal(0, exitOn);
        Assert.Single(spyOn.Requests);
        Assert.Equal("/lookup2.php", spyOn.Requests[0].AbsolutePath);

        using var docOn = ParseJsonOutput(stdoutOn);
        var ctdbObj = docOn.RootElement.GetProperty("ctdb");
        Assert.Equal("checked", ctdbObj.GetProperty("status").GetString());
        Assert.True(ctdbObj.GetProperty("discAccurate").GetBoolean());
        Assert.Equal(7, ctdbObj.GetProperty("discConfidence").GetInt32());
    }

    [Fact]
    public async Task RipAudio_RepairsUnreadableSectorsFromCtdbParity_AndConfirmsWithAccurateRip()
    {
        // 1. Clean reference rip, no network.
        string outClean = Path.Combine(_tempDir, "cli-ctdb-clean");
        using var offline = new HttpClient(new SpyHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var (stdoutClean, stderrClean) = CreateStringWriters();
        Assert.Equal(0, await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outClean, "--json"],
            stdoutClean, stderrClean, _deviceService, _burnEngine, offline));
        using var cleanDoc = ParseJsonOutput(stdoutClean);
        var clean = cleanDoc.RootElement;
        string discCrc = clean.GetProperty("ctdbDiscCrc").GetString()!.ToLowerInvariant();
        byte[] cleanPcm = TrackWavs(clean).SelectMany(WavFileCodec.DecodeToRedBookPcm).ToArray();
        var arV2 = clean.GetProperty("trackChecksums").EnumerateArray()
            .Select(t => Convert.ToUInt32(t.GetProperty("accurateRipV2").GetString(), 16))
            .ToArray();

        // 2. A CTDB entry with parity for the clean audio, and an AccurateRip pressing that matches it.
        var spy = new FakeCtdbServer(cleanPcm, discCrc, arV2);
        using var online = new HttpClient(spy);

        // 3. Two unreadable sectors in track 2 (LBA 50..124), zero-filled by --continue-on-error.
        _medium.SimulatedBadLbas.Add(83);
        _medium.SimulatedBadLbas.Add(84);
        string outDamaged = Path.Combine(_tempDir, "cli-ctdb-damaged");
        var (stdout, stderr) = CreateStringWriters();
        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outDamaged, "--continue-on-error", "--verify-ctdb", "--json"],
            stdout, stderr, _deviceService, _burnEngine, online);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("badSectors").GetArrayLength());
        var repair = root.GetProperty("ctdbRepair");
        Assert.Equal("repaired", repair.GetProperty("status").GetString());
        Assert.True(repair.GetProperty("accurateRipContacted").GetBoolean());
        Assert.Equal(2, repair.GetProperty("tracks")[0].GetProperty("trackNumber").GetInt32());
        Assert.True(root.GetProperty("ctdb").GetProperty("discAccurate").GetBoolean());
        Assert.Equal(0, exit); // a confirmed repair is a clean rip
        Assert.Equal(cleanPcm, TrackWavs(root).SelectMany(WavFileCodec.DecodeToRedBookPcm).ToArray());
        Assert.Contains(spy.Requests, u => u.Host == "www.accuraterip.com");
        Assert.False(Directory.Exists(Path.Combine(outDamaged, CtdbRepairService.CorrectedFolderName)));

        static IEnumerable<string> TrackWavs(JsonElement json) => json.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString()!)
            .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("00", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    // ---- MusicBrainz edition choice, library layout, cover art -------------------------------------------------

    private const string DeReleaseId = "0d3e1c3a-7a4b-4b1a-9c55-0a8f6b6a1e01";
    private const string CaReleaseId = "5b8d2a4c-1f3e-4d6a-8b7c-2e9f0a1b3c02";
    private const string UsReleaseId = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c03";

    private static readonly byte[] TinyPng =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01];

    /// <summary>
    /// Three releases that share the virtual disc's ID: tracks 1 and 2 agree, track 3 is titled differently on the
    /// German release. (The virtual disc has three tracks, so the differing track is number 3.)
    /// </summary>
    private static string ThreeReleasesJson()
    {
        static string Release(string id, string date, string country, string track3) => $$"""
            {
              "id": "{{id}}", "title": "Sehnsucht", "date": "{{date}}", "country": "{{country}}",
              "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ],
              "media": [ { "tracks": [
                { "position": 1, "title": "Spieluhr", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] },
                { "position": 2, "title": "Bück dich", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] },
                { "position": 3, "title": "{{track3}}", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] }
              ] } ]
            }
            """;

        return "{ \"releases\": [" +
            Release(DeReleaseId, "1998-01-13", "DE", "Du riechst so gut '98") + "," +
            Release(CaReleaseId, "1997", "CA", "Stripped") + "," +
            Release(UsReleaseId, "1997", "US", "Stripped") + "] }";
    }

    private static SpyHttpHandler MusicBrainzAndCoverStub() => new(request =>
    {
        string host = request.RequestUri!.Host;
        if (host.Contains("coverartarchive", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPng) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ThreeReleasesJson(), Encoding.UTF8, "application/json")
        };
    });

    [Fact]
    public async Task RipAudio_Lookup_WithDifferingReleases_StopsBeforeReadingTheDisc()
    {
        string outDir = Path.Combine(_tempDir, "choose-edition");
        var spy = MusicBrainzAndCoverStub();
        using var http = new HttpClient(spy);
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outDir, "--lookup", "--json"],
            stdout, stderr, _deviceService, _burnEngine, http);

        Assert.Equal(1, exit);
        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("--release <number>", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("track 3: Du riechst so gut '98 / Stripped", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        var releases = root.GetProperty("releases").EnumerateArray().ToList();
        Assert.Equal(3, releases.Count);
        Assert.Equal(3, releases[2].GetProperty("index").GetInt32());
        Assert.Equal(UsReleaseId, releases[2].GetProperty("id").GetString());
        Assert.Equal("US", releases[2].GetProperty("country").GetString());
        var difference = Assert.Single(root.GetProperty("differences").EnumerateArray());
        Assert.Equal(3, difference.GetProperty("trackNumber").GetInt32());
        Assert.Equal(2, difference.GetProperty("titles").GetArrayLength());

        // The disc was not read: nothing was written and only MusicBrainz was contacted.
        Assert.True(!Directory.Exists(outDir) || Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length == 0);
        Assert.All(spy.Requests, r => Assert.Contains("musicbrainz", r.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RipAudio_ReleaseChoice_FilesTheAlbumInTheLibraryWithCoverFromTheArchive()
    {
        string library = Path.Combine(_tempDir, "library");
        var spy = MusicBrainzAndCoverStub();
        using var http = new HttpClient(spy);
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--lookup", "--release", "3", "--library", library, "--format", "wav", "--json"],
            stdout, stderr, _deviceService, _burnEngine, http);

        Assert.Equal(0, exit);
        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        string albumDir = Path.Combine(library, "Rammstein", "Sehnsucht (1997)");
        Assert.True(File.Exists(Path.Combine(albumDir, "03 - Stripped.wav")));
        Assert.True(File.Exists(Path.Combine(albumDir, "01 - Spieluhr.wav")));
        Assert.Equal("coverartarchive", root.GetProperty("coverSource").GetString());
        Assert.True(File.Exists(Path.Combine(albumDir, "folder.png")));
        Assert.Equal(TinyPng, File.ReadAllBytes(Path.Combine(albumDir, "folder.png")));
        Assert.Equal(UsReleaseId, root.GetProperty("release").GetProperty("id").GetString());
        Assert.Equal("1997", root.GetProperty("release").GetProperty("date").GetString());
        Assert.Equal(3, root.GetProperty("files").GetArrayLength());
        var export = Assert.Single(root.GetProperty("exports").EnumerateArray());
        Assert.Equal("WAV", export.GetProperty("format").GetString());
        Assert.Single(root.GetProperty("folderImages").EnumerateArray());
        Assert.Single(root.GetProperty("cueSheets").EnumerateArray());
        Assert.Contains(spy.Requests, r => r.AbsolutePath == $"/release/{UsReleaseId}/front-500");

        // The hidden working folder and the WAV files it held are gone.
        Assert.Empty(Directory.GetDirectories(library, ".rip-work-*"));
    }

    [Fact]
    public async Task RipAudio_ReleaseChoice_ByIdAndWithoutCoverLookup_WritesNoFolderImage()
    {
        string library = Path.Combine(_tempDir, "library-id");
        var spy = MusicBrainzAndCoverStub();
        using var http = new HttpClient(spy);
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--lookup", "--release", DeReleaseId.ToUpperInvariant(), "--no-cover-lookup",
             "--library", library, "--output", Path.Combine(_tempDir, "work-id"), "--format", "wav", "--json"],
            stdout, stderr, _deviceService, _burnEngine, http);

        Assert.Equal(0, exit);
        using var doc = ParseJsonOutput(stdout);
        Assert.False(doc.RootElement.TryGetProperty("coverSource", out var source) && source.ValueKind == JsonValueKind.String);
        Assert.True(File.Exists(Path.Combine(library, "Rammstein", "Sehnsucht (1998)", "03 - Du riechst so gut '98.wav")));
        Assert.Empty(Directory.GetFiles(library, "folder.*", SearchOption.AllDirectories));
        Assert.DoesNotContain(spy.Requests, r => r.Host.Contains("coverartarchive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RipAudio_UnknownRelease_ListsTheReleasesAndReadsNothing()
    {
        string outDir = Path.Combine(_tempDir, "unknown-release");
        using var http = new HttpClient(MusicBrainzAndCoverStub());
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outDir, "--lookup", "--release", "9", "--json"],
            stdout, stderr, _deviceService, _burnEngine, http);

        Assert.Equal(1, exit);
        using var doc = ParseJsonOutput(stdout);
        Assert.Equal(3, doc.RootElement.GetProperty("releases").GetArrayLength());
        Assert.False(Directory.Exists(outDir) && Directory.GetFiles(outDir, "*.wav").Length > 0);
    }

    [Fact]
    public async Task RipAudio_SameFormatTwice_IsAnError()
    {
        string outDir = Path.Combine(_tempDir, "twice");
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "wav,wav", "--json"],
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(1, exit);
        using var doc = ParseJsonOutput(stdout);
        Assert.Contains("more than once", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir) && Directory.GetFiles(outDir, "*.wav").Length > 0);
    }

    [Fact]
    public async Task RipAudio_GenericBitrateWithTwoLossyFormats_NamesThePerFormatOptions()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", Path.Combine(_tempDir, "two-lossy"), "--bitrate", "192", "--format", "mp3,opus", "--json"],
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(1, exit);
        using var doc = ParseJsonOutput(stdout);
        string error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("--mp3-bitrate", error, StringComparison.Ordinal);
        Assert.Contains("--opus-bitrate", error, StringComparison.Ordinal);
        Assert.Contains("--aac-bitrate", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mp3-quality", "V10")]
    [InlineData("--mp3-quality", "x")]
    [InlineData("--naming", "{bogus}\\{title}")]
    [InlineData("--opus-bitrate", "128")] // opus is not among the chosen formats
    public async Task RipAudio_BadExportOptions_FailBeforeTheDiscIsRead(string option, string value)
    {
        string outDir = Path.Combine(_tempDir, "bad-option-" + Guid.NewGuid().ToString("N"));
        var (stdout, stderr) = CreateStringWriters();

        int exit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "wav", option, value, "--json"],
            stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task RipAudio_WithDeEmphasisFlag_AttenuatesPreEmphasisTrackAndKeepsRawAccurateRipChecksums()
    {
        const int sectors = 30;
        const int sampleRate = OpticalConstants.CdAudioSampleRate;
        byte[] preEmphPcm = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < preEmphPcm.Length / 4; i++)
        {
            short s = (short)Math.Round(0.6 * short.MaxValue * Math.Sin(2.0 * Math.PI * 15000.0 * i / sampleRate));
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(preEmphPcm.AsSpan(i * 4, 2), s);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(preEmphPcm.AsSpan(i * 4 + 2, 2), s);
        }

        byte[] normalPcm = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < normalPcm.Length / 4; i++)
        {
            short s = (short)Math.Round(0.6 * short.MaxValue * Math.Sin(2.0 * Math.PI * 15000.0 * i / sampleRate));
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(normalPcm.AsSpan(i * 4, 2), s);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(normalPcm.AsSpan(i * 4 + 2, 2), s);
        }

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.Raw2352Mode = true;
        medium.WriteSectors(0, preEmphPcm, OpticalConstants.RawSectorSize);
        medium.WriteSectors(sectors, normalPcm, OpticalConstants.RawSectorSize);
        var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = sectors * 2 };
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 1,
            Title = "Pre-Emph Track",
            StartLba = 0,
            SectorCount = sectors,
            IsAudio = true,
            PreEmphasis = true
        });
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 2,
            Title = "Flat Track",
            StartLba = sectors,
            SectorCount = sectors,
            IsAudio = true,
            PreEmphasis = false
        });
        medium.Sessions.Add(session);
        recorder.InsertMedia(medium);

        var deviceService = new TestDeviceService(recorder);
        var engine = new VirtualBurnEngine(recorder);

        // Baseline rip without --de-emphasis
        string rawDir = Path.Combine(_tempDir, "deemph-raw");
        var (rawOut, rawErr) = CreateStringWriters();
        int rawExit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", rawDir, "--no-replaygain", "--json"],
            rawOut, rawErr, deviceService, engine);
        Assert.Equal(0, rawExit);

        // De-emphasized rip with --de-emphasis
        string deEmphDir = Path.Combine(_tempDir, "deemph-applied");
        var (deOut, deErr) = CreateStringWriters();
        int deExit = await CliCommandRunner.RunAsync(
            ["rip-audio", "--drive", "VDRV-01", "--output", deEmphDir, "--de-emphasis", "--no-replaygain", "--json"],
            deOut, deErr, deviceService, engine);
        Assert.Equal(0, deExit);

        using var rawDoc = ParseJsonOutput(rawOut);
        using var deDoc = ParseJsonOutput(deOut);
        var rawTracks = rawDoc.RootElement.GetProperty("trackChecksums");
        var deTracks = deDoc.RootElement.GetProperty("trackChecksums");
        Assert.Equal(rawTracks[0].GetProperty("accurateRipV1").GetString(), deTracks[0].GetProperty("accurateRipV1").GetString());
        Assert.Equal(rawTracks[0].GetProperty("accurateRipV2").GetString(), deTracks[0].GetProperty("accurateRipV2").GetString());

        byte[] track1DeEmph = (await File.ReadAllBytesAsync(Path.Combine(deEmphDir, "01 - Unknown Artist - Pre-Emph Track.wav")))[44..];
        byte[] track2DeEmph = (await File.ReadAllBytesAsync(Path.Combine(deEmphDir, "02 - Unknown Artist - Flat Track.wav")))[44..];
        Assert.Equal(normalPcm, track2DeEmph);

        // Track 1 (15 kHz pre-emphasis) must be attenuated by ~8.87 dB (-10.4 dB to -7.4 dB).
        double rmsIn = 0, rmsOut = 0;
        int skip = 500;
        int totalFrames = preEmphPcm.Length / 4;
        for (int i = skip; i < totalFrames; i++)
        {
            double a = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(preEmphPcm.AsSpan(i * 4, 2));
            double b = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(track1DeEmph.AsSpan(i * 4, 2));
            rmsIn += a * a;
            rmsOut += b * b;
        }
        double gainDb = 20.0 * Math.Log10(Math.Sqrt(rmsOut / (totalFrames - skip)) / Math.Sqrt(rmsIn / (totalFrames - skip)));
        Assert.InRange(gainDb, -9.5, -8.2);

        // CUE sheet omits FLAGS PRE when de-emphasis was already applied during rip.
        string rawCue = await File.ReadAllTextAsync(Path.Combine(rawDir, "album.cue"));
        string deCue = await File.ReadAllTextAsync(Path.Combine(deEmphDir, "album.cue"));
        Assert.Contains("FLAGS PRE", rawCue, StringComparison.Ordinal);
        Assert.DoesNotContain("FLAGS PRE", deCue, StringComparison.Ordinal);

        // Exporting the raw rip with ApplyDeEmphasis=true de-emphasizes Track 1 in the export without modifying raw WAV.
        var rawRipResult = await new AudioCdRipperService().RipAudioCdAsync(
            recorder, engine, Path.Combine(_tempDir, "deemph-export-src"), new AudioRipOptions { ApplyDeEmphasis = false });
        Assert.True(rawRipResult.Success);
        var exporter = new AudioLibraryExporter();
        var exportDir = Path.Combine(_tempDir, "deemph-export-dst");
        var exported = await exporter.ExportAsync(
            rawRipResult,
            new AudioExportOptions(
                [new AudioExportTarget(AudioRipFormat.Wav)],
                exportDir,
                AudioNamingTemplate.Flat,
                new AudioAlbumInfo("Artist", "Album", null),
                FfmpegPath: null,
                KeepWav: true,
                ApplyDeEmphasis: true));
        byte[] exportedTrack1 = (await File.ReadAllBytesAsync(exported.Files[0].Path))[44..];
        Assert.Equal(track1DeEmph, exportedTrack1);
        byte[] stillRawTrack1 = (await File.ReadAllBytesAsync(rawRipResult.TrackInfos[0].FilePath))[44..];
        Assert.Equal(preEmphPcm, stillRawTrack1);
    }

    private sealed class SpyHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != null)
            {
                Requests.Add(request.RequestUri);
            }
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TestDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public TestDeviceService(params IDiscRecorderBackend[] recorders)
        {
            _recorders = recorders;
        }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
        {
            return includeVirtual ? _recorders : _recorders.Where(r => !r.IsVirtual).ToList();
        }

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
        {
            return _recorders.FirstOrDefault(r =>
                string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath?.TrimEnd('\\'), uniqueId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
    }
}
