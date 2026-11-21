using CdBurner.Core.DvdVideo;
using CdBurner.Core.Subtitles;

namespace CdBurner.Core.Tests;

/// <summary>
/// Subtitle generation with FFmpeg and whisper.cpp replaced by fakes: the commands passed, where files go (never next
/// to the movie), the cache, tidying of the output, failures and cancellation; plus DVD authoring's use of it.
/// </summary>
public sealed class SubtitleGenerationTests : IDisposable
{
    // Lines in the shape the real tools print (FFmpeg banner and -progress output; whisper-cli -pp on an RTX 3090).
    private const string FfmpegDurationLine = "  Duration: 00:01:40.00, start: 0.000000, bitrate: 1234 kb/s";
    private const string WhisperCudaLine = "whisper_backend_init_gpu: device 0: CUDA0 (type: 1)";
    private const string WhisperNoGpuLine = "whisper_backend_init_gpu: no GPU found";

    private const string RawWhisperSrt =
        "1\n00:00:00,000 --> 00:00:07,060\n If you can't see, hear, or feel something, it doesn't exist.\n\n" +
        "2\n00:00:07,880 --> 00:00:07,880\n zero length\n\n" +
        "3\n00:00:08,000 --> 00:00:09,000\n   \n\n" +
        "4\n00:00:17,380 --> 00:00:17,880\n Really?\n\n";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_subtitles_" + Guid.NewGuid().ToString("N"));
    private readonly string _movieDir;
    private readonly string _movie;
    private readonly string _cacheRoot;
    private readonly string _ffmpeg;
    private readonly WhisperInstallation _whisper;

    public SubtitleGenerationTests()
    {
        _movieDir = Path.Combine(_tempDir, "Movies [2008]");
        Directory.CreateDirectory(_movieDir);
        _movie = Path.Combine(_movieDir, "Feature Film.mp4");
        File.WriteAllBytes(_movie, new byte[65536]);
        _cacheRoot = Path.Combine(_tempDir, "cache");

        string tools = Path.Combine(_tempDir, "tools");
        Directory.CreateDirectory(tools);
        _ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        File.WriteAllText(_ffmpeg, "fake");
        string cli = Path.Combine(tools, "whisper-cli.exe");
        File.WriteAllText(cli, "fake");
        string model = Path.Combine(tools, "ggml-large-v3-turbo.bin");
        File.WriteAllText(model, "fake");
        _whisper = new WhisperInstallation(cli, model, WhisperBuildKind.CudaX64);
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
    }

    private SubtitleGenerationOptions Options(string language = "en") => new()
    {
        Language = language,
        FfmpegPath = _ffmpeg,
        Whisper = _whisper,
        CacheRoot = _cacheRoot,
        Threads = 4
    };

    [Fact]
    public async Task Generate_RunsFfmpegThenWhisperAndWritesATidiedSrtToTheCache()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath);
        var reports = new List<SubtitleGenerationProgress>();

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(
            _movie, Options(), new InlineProgress<SubtitleGenerationProgress>(reports.Add));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.FromCache);
        Assert.Equal(2, result.CueCount); // the zero-length and empty cues are dropped
        Assert.Equal("NVIDIA GPU (CUDA0)", result.Backend);
        Assert.Equal(TimeSpan.FromSeconds(100), result.AudioDuration);
        Assert.Null(result.ErrorMessage);

        // Output in the cache, named after the movie; nothing new next to the movie.
        Assert.StartsWith(_cacheRoot, result.SrtPath);
        Assert.Equal("Feature Film.srt", Path.GetFileName(result.SrtPath));
        Assert.Equal(new[] { "Feature Film.mp4" }, Directory.GetFiles(_movieDir).Select(f => Path.GetFileName(f)));
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:05,700\r\nIf you can't see, hear, or feel something, it doesn't exist.\r\n\r\n" +
            "2\r\n00:00:17,380 --> 00:00:17,880\r\nReally?\r\n\r\n",
            File.ReadAllText(result.SrtPath!));

        // FFmpeg: first audio track to 16 kHz mono PCM in the job folder.
        var ffmpegCall = runner.Calls[0];
        Assert.Equal(_ffmpeg, ffmpegCall.Exe);
        Assert.Contains(_movie, ffmpegCall.Args);
        AssertSequence(ffmpegCall.Args, "-map", "0:a:0");
        AssertSequence(ffmpegCall.Args, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "audio.wav");

        // whisper-cli: ASCII-only relative names inside the job folder, SRT output, progress lines on.
        var whisperCall = runner.Calls[1];
        Assert.Equal(_whisper.CliPath, whisperCall.Exe);
        Assert.Equal(ffmpegCall.WorkingDirectory, whisperCall.WorkingDirectory);
        AssertSequence(whisperCall.Args, "-m", _whisper.ModelPath);
        AssertSequence(whisperCall.Args, "-f", "audio.wav", "-l", "en", "-osrt", "-of", "subtitles", "-pp", "-t", "4");
        AssertSequence(whisperCall.Args, "-mc", "0"); // without it a real film looped on one line

        // The job folder (with the extracted audio) is removed afterwards.
        Assert.False(Directory.Exists(whisperCall.WorkingDirectory));
        Assert.Equal(100, reports[^1].Percent);
        Assert.Contains(reports, r => r.Percent is > 10 and < 100 && r.Status.Contains("50%"));
        Assert.Contains(reports, r => r.Percent is > 0 and <= 10 && r.Status.Contains("Reading the audio"));
    }

    [Fact]
    public async Task Generate_WithTheVoiceActivityModel_PassesItAndUsesItsOwnCacheEntry()
    {
        string vad = Path.Combine(Path.GetDirectoryName(_whisper.ModelPath)!, "ggml-silero-v6.2.0.bin");
        File.WriteAllText(vad, "fake");
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath);
        var service = new SubtitleGenerationService(runner);
        var withVad = Options();
        withVad.Whisper = _whisper with { VadModelPath = vad };

        SubtitleGenerationResult plain = await service.GenerateAsync(_movie, Options());
        SubtitleGenerationResult filtered = await service.GenerateAsync(_movie, withVad);

        Assert.DoesNotContain("--vad", runner.Calls[1].Args);
        AssertSequence(runner.Calls[3].Args, "--vad", "-vm", vad, "-vt", "0.35", "-vp", "200");
        Assert.False(filtered.FromCache);
        Assert.NotEqual(plain.SrtPath, filtered.SrtPath);
    }

    [Fact]
    public async Task Generate_SecondTimeReusesTheCacheWithoutRunningTools()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath);
        var service = new SubtitleGenerationService(runner);
        SubtitleGenerationResult first = await service.GenerateAsync(_movie, Options());
        SubtitleGenerationResult second = await service.GenerateAsync(_movie, Options());

        Assert.True(second.Success);
        Assert.True(second.FromCache);
        Assert.Equal(first.SrtPath, second.SrtPath);
        Assert.Equal(2, runner.Calls.Count);

        // Another language, or a changed movie file, is a different cache entry.
        SubtitleGenerationResult german = await service.GenerateAsync(_movie, Options("de"));
        Assert.False(german.FromCache);
        Assert.NotEqual(first.SrtPath, german.SrtPath);
        File.SetLastWriteTimeUtc(_movie, DateTime.UtcNow.AddMinutes(5));
        Assert.False((await service.GenerateAsync(_movie, Options())).FromCache);
    }

    [Fact]
    public async Task Generate_ReportsAMovieWithoutAudio()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath)
        {
            FfmpegExitCode = 1,
            FfmpegLines = ["Stream map '0:a:0' matches no streams."],
            WriteAudio = false
        };

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, Options());

        Assert.False(result.Success);
        Assert.Contains("no audio track", result.ErrorMessage);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Generate_ReportsWhisperFailureWithItsLastLines()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath)
        {
            WhisperExitCode = 3,
            WhisperLines = ["whisper_init_from_file_with_params_no_state: failed to open model"],
            WriteSrt = false
        };

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, Options());

        Assert.False(result.Success);
        Assert.Contains("exit code 3", result.ErrorMessage);
        Assert.Contains("failed to open model", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(_cacheRoot, "*.srt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Generate_GpuBuildThatFellBackToTheProcessor_SucceedsWithAWarning()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath) { WhisperLines = [WhisperNoGpuLine, "progress = 100%"] };

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, Options());

        Assert.True(result.Success);
        Assert.Equal("processor", result.Backend);
        Assert.Contains("no usable NVIDIA GPU", result.ErrorMessage);
    }

    [Fact]
    public async Task Generate_VulkanBuild_NamesTheGpuThatWasUsed()
    {
        // Lines as the Vulkan build of whisper-cli 1.9.5 printed them on a PC with an RTX 3090 and Intel UHD 770,
        // with the Intel GPU chosen (it is then the only listed device, so it becomes Vulkan0).
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath)
        {
            WhisperLines =
            [
                "ggml_vulkan: Found 1 Vulkan devices:",
                "ggml_vulkan: 0 = Intel(R) UHD Graphics 770 (Intel Corporation) | uma: 1 | fp16: 1 | bf16: 0 | fp4: 0 | warp size: 32 | shared memory: 32768 | int dot: 1 | matrix cores: none",
                "whisper_backend_init_gpu: device 0: Vulkan0 (type: 1)",
                "whisper_backend_init_gpu: found GPU device 0: Vulkan0 (type: 1, cnt: 0)",
                "whisper_backend_init_gpu: using Vulkan0 backend",
                WhisperNoGpuLine, // the voice activity model runs on the processor and prints this afterwards
                "progress = 100%"
            ]
        };
        var options = Options();
        options.Whisper = _whisper with { Build = WhisperBuildKind.VulkanX64 };

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, options);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Intel(R) UHD Graphics 770 (Vulkan0)", result.Backend);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Generate_VulkanBuildWithNoUsableGpu_SucceedsWithADriverWarning()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath)
        {
            WhisperLines = ["ggml_vulkan: No devices found.", "whisper_backend_init_gpu: device 0: CPU (type: 0)", WhisperNoGpuLine, "progress = 100%"]
        };
        var options = Options();
        options.Whisper = _whisper with { Build = WhisperBuildKind.VulkanX64 };

        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, options);

        Assert.True(result.Success);
        Assert.Equal("processor", result.Backend);
        Assert.Contains("Vulkan", result.ErrorMessage);
        Assert.Contains("graphics driver", result.ErrorMessage);
    }

    [Theory]
    [InlineData("CUDA0", "NVIDIA GPU (CUDA0)")]
    [InlineData("Vulkan1", "AMD Radeon RX 7900 XTX (Vulkan1)")]
    [InlineData("Vulkan2", "GPU (Vulkan2)")]                 // not in the listed devices
    [InlineData("CPU", "processor")]
    public void DescribeBackend_NamesTheDevice(string device, string expected)
    {
        var names = new Dictionary<string, string> { ["Vulkan0"] = "Intel(R) UHD Graphics 770", ["Vulkan1"] = "AMD Radeon RX 7900 XTX" };
        Assert.Equal(expected, SubtitleGenerationService.DescribeBackend(device, names));
    }

    [Fact]
    public async Task Generate_CancelledDuringRecognition_LeavesNoSubtitlesOrJobFiles()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath) { CancelDuringWhisper = cts };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SubtitleGenerationService(runner).GenerateAsync(_movie, Options(), cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(_cacheRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(new[] { "Feature Film.mp4" }, Directory.GetFiles(_movieDir).Select(f => Path.GetFileName(f)));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("e")]
    [InlineData("en; del")]
    [InlineData("")]
    public async Task Generate_RejectsALanguageThatIsNotACode(string language)
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath);
        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(_movie, Options(language));

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Generate_MissingMovieFailsWithoutRunningTools()
    {
        var runner = new FakeRunner(_ffmpeg, _whisper.CliPath);
        SubtitleGenerationResult result = await new SubtitleGenerationService(runner)
            .GenerateAsync(Path.Combine(_movieDir, "nope.mp4"), Options());

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void NarrowArgument_UsesARelativeAsciiPathWhenTheFullPathHasOtherCharacters()
    {
        // whisper-cli reads arguments in the ANSI code page: "José" in a user name broke full paths on a real run.
        string root = Path.Combine(_tempDir, "Jos\u00e9");
        string model = Path.Combine(root, "Programs", "whisper.cpp", "models", "ggml-base.bin");
        string work = Path.Combine(root, "IgnisDisc", "SubtitleCache", "0123456789abcdef", "work");

        Assert.Equal(@"..\..\..\..\Programs\whisper.cpp\models\ggml-base.bin", SubtitleGenerationService.ToNarrowArgument(model, work));
        Assert.Equal(@"C:\plain\ggml-base.bin", SubtitleGenerationService.ToNarrowArgument(@"C:\plain\ggml-base.bin", work));
    }

    [Fact]
    public void SrtCueList_ParsesDotOrCommaMillisecondsAndMultiLineText()
    {
        var cues = SrtCueList.Parse("1\r\n00:01:02.5 --> 00:01:04,250\r\nLine one\r\nLine two\r\n\r\n2\r\n01:00:00,000 --> 01:00:01,000\r\nLast");

        Assert.Equal(2, cues.Count);
        Assert.Equal(new TimeSpan(0, 0, 1, 2, 500), cues[0].Start);
        Assert.Equal(new TimeSpan(0, 0, 1, 4, 250), cues[0].End);
        Assert.Equal("Line one\nLine two", cues[0].Text);
        Assert.Equal(TimeSpan.FromHours(1), cues[1].Start);
        Assert.Equal("1\r\n00:01:02,500 --> 00:01:04,250\r\nLine one\r\nLine two\r\n\r\n", SrtCueList.Format([cues[0]]));
    }

    [Fact]
    public void SplitLong_BreaksAWholeStretchOfSpeechIntoReadableCuesAndKeepsItsTime()
    {
        // A cue as whisper.cpp wrote it with voice activity detection on a real film (10 s, 130 characters).
        var cue = new SrtCue(TimeSpan.FromSeconds(213.25), TimeSpan.FromSeconds(223.07),
            "On the 15th of May, in the jungle of New, heat of the day, the pool of the pool, he was splashing, enjoying the jungle's great joys.");
        var shortCue = new SrtCue(TimeSpan.FromSeconds(227), TimeSpan.FromSeconds(230), "Courtney the elephant heard a small noise.");

        IReadOnlyList<SrtCue> split = SrtCueList.SplitLong([cue, shortCue]);

        Assert.Equal(3, split.Count);
        Assert.All(split, c => Assert.True(c.Text.Length <= SrtCueList.MaxCueCharacters, c.Text));
        Assert.Equal(cue.Start, split[0].Start);
        Assert.Equal(split[0].End, split[1].Start);
        Assert.Equal(cue.End, split[1].End);
        Assert.Equal(cue.Text, split[0].Text + " " + split[1].Text);
        Assert.Same(shortCue, split[2]);
    }

    [Fact]
    public async Task Resolver_GeneratesOnlyForMoviesWithoutSubtitlesAndOnlyWhenAsked()
    {
        string second = Path.Combine(_movieDir, "Second.mkv");
        File.WriteAllBytes(second, new byte[16]);
        // Authoring's own rule finds this one (name.eng.srt beside the movie), so it is not generated.
        File.WriteAllText(Path.Combine(_movieDir, "Second.eng.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhi\n");
        string third = Path.Combine(_movieDir, "Third.mkv");
        File.WriteAllBytes(third, new byte[16]);
        string given = Path.Combine(_tempDir, "given.srt");
        File.WriteAllText(given, "1\n00:00:00,000 --> 00:00:01,000\nhi\n");
        var generated = new List<string>();
        Task<SubtitleGenerationResult> Generator(string movie, IProgress<SubtitleGenerationProgress>? p, CancellationToken ct)
        {
            generated.Add(movie);
            return Task.FromResult(new SubtitleGenerationResult(true, Path.Combine(_cacheRoot, "x.srt"), 7, TimeSpan.FromMinutes(2), null, false, "NVIDIA GPU (CUDA0)", null));
        }

        var off = new DvdAuthoringOptions();
        Assert.Null(await DvdSubtitleSourceResolver.ResolveAsync([_movie, second, third], off, new List<string>(), null, Generator, CancellationToken.None));
        Assert.Empty(generated);
        Assert.Empty(off.SubtitleFiles);

        var on = new DvdAuthoringOptions { GenerateSubtitles = true };
        on.SubtitleFiles[third] = given;
        var notes = new List<string>();
        Assert.Null(await DvdSubtitleSourceResolver.ResolveAsync([_movie, second, third], on, notes, null, Generator, CancellationToken.None));
        Assert.Equal(new[] { _movie }, generated);
        Assert.Equal(Path.Combine(_cacheRoot, "x.srt"), on.SubtitleFiles[_movie]);
        Assert.False(on.SubtitleFiles.ContainsKey(second));
        Assert.Equal(given, on.SubtitleFiles[third]);
        Assert.Contains(notes, n => n.Contains("7 machine-generated subtitles") && n.Contains("NVIDIA GPU"));
        Assert.Contains(notes, n => n.Contains("Second.eng.srt") && n.Contains("not generated"));
    }

    [Fact]
    public void Tidy_DropsRunsOfRepeatedLinesAndShortensCuesLeftOnScreenTooLong()
    {
        // Shapes seen on real films: a line repeated in a loop, and a short line stretched over a long silence.
        var cues = new List<SrtCue>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(30), "I'm sorry."),
            new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(41), "Don't touch it."),
            new(TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(42), "Don't touch it."),
            new(TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(43), "Don't touch it."),
            new(TimeSpan.FromSeconds(43), TimeSpan.FromSeconds(44), "don't touch it"),
            new(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(51), "Hello."),
        };

        IReadOnlyList<SrtCue> tidy = SrtCueList.LimitDuration(SrtCueList.DropRepeats(cues));

        Assert.Equal(new[] { "I'm sorry.", "Don't touch it.", "Don't touch it.", "Hello." }, tidy.Select(c => c.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1500 + 70 * 10), tidy[0].End - tidy[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), tidy[3].End - tidy[3].Start); // already short enough: unchanged
        var longLine = new SrtCue(TimeSpan.Zero, TimeSpan.FromSeconds(60), new string('a', 84));
        Assert.Equal(TimeSpan.FromSeconds(7), SrtCueList.LimitDuration([longLine])[0].End);
    }

    [Fact]
    public async Task Authoring_StopsBeforeTranscodingWhenRequestedSubtitlesFail()
    {
        string outDir = Path.Combine(_tempDir, "authored");
        var service = new DvdVideoAuthoringService
        {
            SubtitleGenerator = (movie, p, ct) => Task.FromResult(SubtitleGenerationResult.Fail("model missing"))
        };
        var options = new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, GenerateSubtitles = true };

        DvdAuthoringResult result = await service.AuthorAsync(_movie, outDir, options);

        Assert.False(result.Success);
        Assert.Contains("Feature Film.mp4", result.ErrorMessage);
        Assert.Contains("model missing", result.ErrorMessage);
        string videoTs = Path.Combine(outDir, "VIDEO_TS");
        Assert.True(!Directory.Exists(videoTs) || Directory.GetFiles(videoTs).Length == 0);
    }

    [Fact]
    public async Task Authoring_WithGeneratedSubtitles_RecordsThemAndCompletes()
    {
        string srt = Path.Combine(_cacheRoot, "Feature Film.srt");
        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(srt, "1\r\n00:00:00,000 --> 00:00:01,000\r\nHello\r\n");
        var service = new DvdVideoAuthoringService
        {
            SubtitleGenerator = (movie, p, ct) => Task.FromResult(new SubtitleGenerationResult(true, srt, 1, TimeSpan.FromSeconds(5), null, false, "processor", null))
        };
        var options = new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, GenerateSubtitles = true };

        DvdAuthoringResult result = await service.AuthorAsync(_movie, Path.Combine(_tempDir, "authored"), options);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(srt, options.SubtitleFiles[_movie]);
        Assert.Contains(result.LogNotes, n => n.Contains("machine-generated"));
    }

    private static void AssertSequence(IReadOnlyList<string> args, params string[] expected)
    {
        for (int i = 0; i + expected.Length <= args.Count; i++)
        {
            if (args.Skip(i).Take(expected.Length).SequenceEqual(expected))
            {
                return;
            }
        }

        Assert.Fail($"Expected '{string.Join(" ", expected)}' in '{string.Join(" ", args)}'.");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Stands in for ffmpeg.exe and whisper-cli.exe: writes their output files and prints their lines.</summary>
    private sealed class FakeRunner(string ffmpeg, string whisper) : IExternalToolRunner
    {
        public List<(string Exe, IReadOnlyList<string> Args, string WorkingDirectory)> Calls { get; } = [];
        public int FfmpegExitCode { get; init; }
        public string[] FfmpegLines { get; init; } = [FfmpegDurationLine, "out_time_us=50000000", "progress=end"];
        public bool WriteAudio { get; init; } = true;
        public int WhisperExitCode { get; init; }
        public string[] WhisperLines { get; init; } = [WhisperCudaLine, "whisper_print_progress_callback: progress =  50%", "progress = 100%"];
        public bool WriteSrt { get; init; } = true;
        public CancellationTokenSource? CancelDuringWhisper { get; init; }

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? onOutputLine, CancellationToken cancellationToken)
        {
            Calls.Add((executablePath, arguments.ToList(), workingDirectory));
            Assert.True(Directory.Exists(workingDirectory));
            if (executablePath == ffmpeg)
            {
                foreach (string line in FfmpegLines) onOutputLine?.Invoke(line);
                if (WriteAudio) File.WriteAllBytes(Path.Combine(workingDirectory, arguments[^1]), new byte[1000]);
                return Task.FromResult(FfmpegExitCode);
            }

            Assert.Equal(whisper, executablePath);
            Assert.True(File.Exists(Path.Combine(workingDirectory, "audio.wav")));
            foreach (string line in WhisperLines) onOutputLine?.Invoke(line);
            if (CancelDuringWhisper != null)
            {
                CancelDuringWhisper.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (WriteSrt) File.WriteAllText(Path.Combine(workingDirectory, "subtitles.srt"), RawWhisperSrt);
            return Task.FromResult(WhisperExitCode);
        }
    }
}

/// <summary>
/// The real process runner: both output streams are read together (no hang when a tool fills one of them) and
/// cancellation ends the tool.
/// </summary>
public sealed class ProcessToolRunnerTests
{
    [Fact]
    public async Task ReadsLargeOutputOnBothStreamsWithoutHanging()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        int lines = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        int exit = await new ProcessToolRunner().RunAsync(
            cmd,
            ["/d", "/c", "for /L %i in (1,1,3000) do @(echo out %i& echo err %i 1>&2)"],
            Path.GetTempPath(),
            _ => lines++,
            timeout.Token);

        Assert.Equal(0, exit);
        Assert.Equal(6000, lines);
    }

    [Fact]
    public async Task CancellationEndsTheTool()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Task<int> runTask = new ProcessToolRunner().RunAsync(
            ping,
            ["-n", "60", "127.0.0.1"],
            Path.GetTempPath(),
            line =>
            {
                if (!string.IsNullOrEmpty(line))
                {
                    started.TrySetResult();
                }
            },
            cts.Token);

        try
        {
            // Wait until the child process has started and written output before cancelling.
            Task first = await Task.WhenAny(started.Task, runTask).WaitAsync(TimeSpan.FromSeconds(15));
            if (first == runTask)
            {
                int exitCode = await runTask;
                Assert.Fail($"Tool exited with code {exitCode} before emitting output.");
            }

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        }
        catch
        {
            cts.Cancel();
            try
            {
                await runTask;
            }
            catch
            {
            }

            throw;
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }
}
