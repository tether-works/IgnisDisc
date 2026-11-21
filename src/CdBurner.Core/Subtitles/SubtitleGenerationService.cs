using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.DvdVideo;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// Settings for <see cref="SubtitleGenerationService"/>.
/// </summary>
public sealed class SubtitleGenerationOptions
{
    /// <summary>
    /// Spoken language as a two- or three-letter code (for example "en", "de") or "auto" to let the model guess
    /// from the first 30 seconds. Default "en".
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>FFmpeg to use; found with <see cref="FfmpegLocator"/> when null.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>whisper.cpp program and model; found with <see cref="WhisperLocator.Find"/> when null.</summary>
    public WhisperInstallation? Whisper { get; set; }

    /// <summary>
    /// Folder for generated subtitles; default <c>%LOCALAPPDATA%\IgnisDisc\SubtitleCache</c>. Nothing is ever
    /// written next to the movie.
    /// </summary>
    public string? CacheRoot { get; set; }

    /// <summary>Processor threads for whisper.cpp; default about two thirds of the logical processors (at most 16).</summary>
    public int? Threads { get; set; }
}

/// <summary>Progress of one subtitle job: 0-100 and a status line.</summary>
public sealed record SubtitleGenerationProgress(double Percent, string Status);

/// <summary>
/// Outcome of one subtitle job.
/// </summary>
/// <param name="Success">True when an .srt with at least one subtitle was produced.</param>
/// <param name="SrtPath">The .srt file (in the cache folder).</param>
/// <param name="CueCount">Number of subtitles in it.</param>
/// <param name="Elapsed">Time the job took (zero when taken from the cache).</param>
/// <param name="AudioDuration">Length of the movie's audio, when FFmpeg reported it.</param>
/// <param name="FromCache">True when an earlier result for the same file, model and language was reused.</param>
/// <param name="Backend">What did the recognition: "NVIDIA GPU (CUDA0)", "Intel(R) UHD Graphics 770 (Vulkan0)",
/// "processor", or null when unknown.</param>
/// <param name="ErrorMessage">Why it failed, or a warning on success (for example a GPU that could not be used).</param>
public sealed record SubtitleGenerationResult(
    bool Success,
    string? SrtPath,
    int CueCount,
    TimeSpan Elapsed,
    TimeSpan? AudioDuration,
    bool FromCache,
    string? Backend,
    string? ErrorMessage)
{
    /// <summary>A failed job with the reason.</summary>
    public static SubtitleGenerationResult Fail(string message, TimeSpan elapsed = default) =>
        new(false, null, 0, elapsed, null, false, null, message);
}

/// <summary>
/// Makes subtitles for a movie with speech recognition: FFmpeg extracts the first audio track as 16 kHz mono WAV,
/// whisper.cpp writes an .srt, and the result is tidied (<see cref="SrtCueList"/>) and kept in a cache keyed by the
/// movie's path, size and date plus model and language, so authoring the same movie again is instant. The subtitles
/// are machine-generated: names and quiet or overlapping speech will sometimes be misheard.
/// </summary>
public sealed class SubtitleGenerationService
{
    /// <summary>File name used inside the job folder for the extracted audio.</summary>
    public const string AudioFileName = "audio.wav";

    /// <summary>Output base name passed to whisper.cpp (it appends .srt).</summary>
    public const string WhisperOutputBaseName = "subtitles";

    private static readonly Regex LanguageCode = new("^(auto|[a-z]{2,3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FfmpegDuration = new(@"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FfmpegOutTime = new(@"^out_time_us=(?<us>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhisperProgress = new(@"progress\s*=\s*(?<p>\d+)%", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhisperGpuDevice = new(@"whisper_backend_init_gpu:\s*device\s*\d+:\s*(?<dev>\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The Vulkan back end lists its devices before whisper.cpp picks one, for example
    // "ggml_vulkan: 1 = Intel(R) UHD Graphics 770 (Intel Corporation) | uma: 1 | fp16: 1 | ...". Device "Vulkan1" is
    // entry 1 of that list.
    private static readonly Regex VulkanDeviceName = new(@"^ggml_vulkan:\s*(?<i>\d+)\s*=\s*(?<name>[^|]+?)\s*(\([^()|]*\))?\s*\|", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// whisper.cpp decoding settings added to every run. <c>-mc 0</c> stops each 30-second window from being fed the
    /// text of the one before: with the default, an 86-minute feature film fell into a loop (one name 274 times
    /// against 80 in the human subtitles) and its word error rate was 52.8%; with <c>-mc 0</c> it was 23.1%.
    /// </summary>
    public static IReadOnlyList<string> DecodingArguments { get; } = ["-mc", "0"];

    /// <summary>
    /// Voice activity detection settings: speech threshold 0.35 (default 0.5) and 200 ms padding (default 30 ms),
    /// so quiet lines are kept. On the same film the default settings dropped real dialogue (word error rate 29.8%);
    /// these with <c>-mc 0</c> gave 24.9% and left 9 lines where the human subtitles had none (all real sounds),
    /// against 45 invented ones ("We'll be right back.", "Thank you.") without voice activity detection.
    /// </summary>
    public static IReadOnlyList<string> VadArguments { get; } = ["-vt", "0.35", "-vp", "200"];

    private readonly IExternalToolRunner _runner;

    /// <summary>Creates the service; <paramref name="runner"/> replaces the real process runner in tests.</summary>
    public SubtitleGenerationService(IExternalToolRunner? runner = null)
    {
        _runner = runner ?? new ProcessToolRunner();
    }

    /// <summary>Default cache folder: <c>%LOCALAPPDATA%\IgnisDisc\SubtitleCache</c>.</summary>
    public static string GetDefaultCacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "SubtitleCache");

    /// <summary>True when <paramref name="language"/> is "auto" or a two- or three-letter lowercase code.</summary>
    public static bool IsValidLanguage(string? language) => language != null && LanguageCode.IsMatch(language);

    /// <summary>
    /// Generates (or reuses) subtitles for <paramref name="moviePath"/> and returns the .srt path.
    /// </summary>
    public async Task<SubtitleGenerationResult> GenerateAsync(
        string moviePath,
        SubtitleGenerationOptions? options = null,
        IProgress<SubtitleGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moviePath);
        options ??= new SubtitleGenerationOptions();
        var stopwatch = Stopwatch.StartNew();

        string movie = Path.GetFullPath(moviePath);
        var movieInfo = new FileInfo(movie);
        if (!movieInfo.Exists)
        {
            return SubtitleGenerationResult.Fail($"Movie file not found: {movie}");
        }

        string language = (options.Language ?? "en").Trim().ToLowerInvariant();
        if (!IsValidLanguage(language))
        {
            return SubtitleGenerationResult.Fail($"Subtitle language '{options.Language}' is not a two- or three-letter code (for example en) or 'auto'.");
        }

        WhisperInstallation? whisper = options.Whisper ?? WhisperLocator.Find();
        if (whisper == null)
        {
            return SubtitleGenerationResult.Fail("The subtitle tool (whisper.cpp) is not installed. Install it with the 1-click download on the DVD-Video screen or 'ignisdisc-cli install-subtitle-tool'.");
        }

        string? ffmpeg = FfmpegLocator.FindFfmpegExecutable(options.FfmpegPath);
        if (ffmpeg == null)
        {
            return SubtitleGenerationResult.Fail("FFmpeg is needed to read the movie's audio and was not found. Install it with the 1-click FFmpeg download.");
        }

        string cacheRoot = string.IsNullOrWhiteSpace(options.CacheRoot) ? GetDefaultCacheRoot() : Path.GetFullPath(options.CacheRoot);
        string settings = string.Join(" ", DecodingArguments) + (whisper.VadModelPath != null ? " vad:" + Path.GetFileName(whisper.VadModelPath) + " " + string.Join(" ", VadArguments) : string.Empty);
        string entryDir = Path.Combine(cacheRoot, ComputeCacheKey(movieInfo, whisper.ModelName, language, settings));
        string srtPath = Path.Combine(entryDir, Path.GetFileNameWithoutExtension(movie) + ".srt");

        int cached = TryCountCues(srtPath);
        if (cached > 0)
        {
            progress?.Report(new SubtitleGenerationProgress(100, $"Using subtitles made earlier for {movieInfo.Name}."));
            return new SubtitleGenerationResult(true, srtPath, cached, TimeSpan.Zero, null, true, null, null);
        }

        Directory.CreateDirectory(entryDir);
        FileStream jobLock;
        try
        {
            jobLock = new FileStream(Path.Combine(entryDir, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return SubtitleGenerationResult.Fail($"Subtitles for {movieInfo.Name} are already being made by another job.");
        }

        string workDir = Path.Combine(entryDir, "work");
        try
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }

            Directory.CreateDirectory(workDir);

            // 1. Audio: first audio track, 16 kHz mono 16-bit PCM, the input whisper.cpp expects.
            progress?.Report(new SubtitleGenerationProgress(0, $"Reading the audio of {movieInfo.Name}..."));
            TimeSpan? duration = null;
            var ffmpegTail = new Queue<string>();
            int ffmpegExit = await _runner.RunAsync(
                ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1",
                 "-i", movie, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                 AudioFileName],
                workDir,
                line =>
                {
                    Remember(ffmpegTail, line);
                    Match d = FfmpegDuration.Match(line);
                    if (duration == null && d.Success)
                    {
                        duration = ParseHms(d);
                    }

                    Match t = FfmpegOutTime.Match(line);
                    if (t.Success && duration is { TotalSeconds: > 0 } total &&
                        long.TryParse(t.Groups["us"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long us))
                    {
                        double pct = Math.Clamp(us / 1_000_000.0 / total.TotalSeconds, 0, 1) * 10.0;
                        progress?.Report(new SubtitleGenerationProgress(pct, $"Reading the audio of {movieInfo.Name}... {pct * 10:F0}%"));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            string audioPath = Path.Combine(workDir, AudioFileName);
            if (ffmpegExit != 0 || !File.Exists(audioPath) || new FileInfo(audioPath).Length <= 44)
            {
                string why = ffmpegTail.Any(l => l.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
                    ? "the movie has no audio track"
                    : $"FFmpeg exit code {ffmpegExit}: {string.Join(" | ", ffmpegTail.TakeLast(3))}";
                return SubtitleGenerationResult.Fail($"Could not read the audio of {movieInfo.Name}: {why}", stopwatch.Elapsed);
            }

            // 2. Speech recognition. whisper-cli reads its arguments in the ANSI code page, so it gets short ASCII
            //    names relative to the job folder instead of full paths that may contain other characters.
            string? modelArgument = ToNarrowArgument(whisper.ModelPath, workDir);
            if (modelArgument == null)
            {
                return SubtitleGenerationResult.Fail($"whisper.cpp cannot open the model at '{whisper.ModelPath}' because the path contains characters outside ASCII.", stopwatch.Elapsed);
            }

            int threads = options.Threads is > 0 ? options.Threads.Value : Math.Clamp(Environment.ProcessorCount * 2 / 3, 1, 16);
            string? backend = null;
            var vulkanNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var whisperTail = new Queue<string>();
            var whisperArgs = new List<string>
            {
                "-m", modelArgument, "-f", AudioFileName, "-l", language, "-osrt", "-of", WhisperOutputBaseName,
                "-pp", "-t", threads.ToString(CultureInfo.InvariantCulture)
            };
            whisperArgs.AddRange(DecodingArguments);

            // Voice activity detection: only stretches that hold speech are transcribed. Without it a feature film's
            // opening music came out as "We'll be right back." every 30 seconds.
            string? vadArgument = whisper.VadModelPath == null ? null : ToNarrowArgument(whisper.VadModelPath, workDir);
            if (vadArgument != null)
            {
                whisperArgs.AddRange(["--vad", "-vm", vadArgument]);
                whisperArgs.AddRange(VadArguments);
            }

            progress?.Report(new SubtitleGenerationProgress(10, $"Recognizing speech in {movieInfo.Name}..."));
            int whisperExit = await _runner.RunAsync(
                whisper.CliPath,
                whisperArgs,
                workDir,
                line =>
                {
                    Remember(whisperTail, line);
                    Match vk = VulkanDeviceName.Match(line);
                    if (vk.Success)
                    {
                        vulkanNames["Vulkan" + vk.Groups["i"].Value] = vk.Groups["name"].Value;
                    }

                    Match dev = WhisperGpuDevice.Match(line);
                    if (dev.Success)
                    {
                        backend = DescribeBackend(dev.Groups["dev"].Value, vulkanNames);
                    }
                    else if (backend == null && line.Contains("no GPU found", StringComparison.OrdinalIgnoreCase))
                    {
                        backend = "processor";
                    }

                    Match p = WhisperProgress.Match(line);
                    if (p.Success && int.TryParse(p.Groups["p"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pct))
                    {
                        double overall = 10 + Math.Clamp(pct, 0, 100) * 0.9;
                        progress?.Report(new SubtitleGenerationProgress(overall, $"Recognizing speech in {movieInfo.Name}... {pct}%"));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            string rawSrt = Path.Combine(workDir, WhisperOutputBaseName + ".srt");
            if (whisperExit != 0 || !File.Exists(rawSrt))
            {
                return SubtitleGenerationResult.Fail(
                    $"Speech recognition failed (whisper.cpp exit code {whisperExit}): {string.Join(" | ", whisperTail.TakeLast(3))}",
                    stopwatch.Elapsed);
            }

            // Tidy: drop empty cues and repeated-line loops, split long stretches of speech into readable cues, and
            // stop short lines from staying on screen through the silence after them.
            IReadOnlyList<SrtCue> cues = SrtCueList.LimitDuration(SrtCueList.SplitLong(SrtCueList.DropRepeats(SrtCueList.Clean(
                SrtCueList.Parse(await File.ReadAllTextAsync(rawSrt, Encoding.UTF8, cancellationToken).ConfigureAwait(false))))));
            if (cues.Count == 0)
            {
                return SubtitleGenerationResult.Fail($"No speech was recognized in {movieInfo.Name}.", stopwatch.Elapsed);
            }

            // Written beside, then renamed: a half-written file is never taken as a finished one.
            string tempSrt = srtPath + ".tmp";
            await File.WriteAllTextAsync(tempSrt, SrtCueList.Format(cues), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(tempSrt, srtPath, overwrite: true);

            string? warning = backend != "processor" ? null : whisper.Build switch
            {
                WhisperBuildKind.CudaX64 => "The GPU build is installed but whisper.cpp found no usable NVIDIA GPU, so the processor was used (much slower). Check the NVIDIA driver.",
                WhisperBuildKind.VulkanX64 => "The GPU build is installed but whisper.cpp found no GPU with Vulkan support, so the processor was used (much slower with this model). Update the graphics driver.",
                _ => null
            };
            progress?.Report(new SubtitleGenerationProgress(100, $"{cues.Count} subtitles made for {movieInfo.Name} in {stopwatch.Elapsed.TotalMinutes:F1} min."));
            return new SubtitleGenerationResult(true, srtPath, cues.Count, stopwatch.Elapsed, duration, false, backend, warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return SubtitleGenerationResult.Fail($"Subtitle generation failed: {ex.Message}", stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteDirectory(workDir);

            jobLock.Dispose();
        }
    }

    /// <summary>
    /// Cache folder name for a movie: SHA-256 of its full path, size, last-write time, the model and the language
    /// (first 16 hex digits), and the recognition <paramref name="settings"/>. Any change to the file gives a new key,
    /// so stale subtitles are never reused.
    /// </summary>
    public static string ComputeCacheKey(FileInfo movie, string modelName, string language, string settings = "")
    {
        ArgumentNullException.ThrowIfNull(movie);
        string identity = string.Join("|",
            movie.FullName.ToUpperInvariant(),
            movie.Length.ToString(CultureInfo.InvariantCulture),
            movie.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            modelName,
            language,
            settings,
            WhisperCatalog.WhisperVersion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    /// <summary>
    /// A form of <paramref name="path"/> made only of ASCII characters, for tools that read their arguments in the
    /// ANSI code page: the path itself, the path relative to <paramref name="workingDirectory"/>, or the Windows short
    /// (8.3) name. Null when none of these is ASCII.
    /// </summary>
    public static string? ToNarrowArgument(string path, string workingDirectory)
    {
        if (Ascii.IsValid(path))
        {
            return path;
        }

        string relative = Path.GetRelativePath(workingDirectory, path);
        if (Ascii.IsValid(relative))
        {
            return relative;
        }

        string? shortPath = TryGetShortPath(path);
        return shortPath != null && Ascii.IsValid(shortPath) ? shortPath : null;
    }

    private static string? TryGetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, [Out] char[] lpszShortPath, uint cchBuffer);

    private static int TryCountCues(string srtPath)
    {
        try
        {
            return File.Exists(srtPath) ? SrtCueList.Parse(File.ReadAllText(srtPath, Encoding.UTF8)).Count : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Names the device whisper.cpp reported ("CUDA0", "Vulkan1", "CPU") for status lines: the GPU name when the Vulkan
    /// back end listed it, otherwise the device name; anything else is the processor.
    /// </summary>
    public static string DescribeBackend(string device, IReadOnlyDictionary<string, string> vulkanNames)
    {
        if (device.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase))
        {
            return $"NVIDIA GPU ({device})";
        }

        if (device.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return vulkanNames.TryGetValue(device, out string? name) ? $"{name} ({device})" : $"GPU ({device})";
        }

        return "processor";
    }

    private static void Remember(Queue<string> tail, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        tail.Enqueue(line.Trim());
        while (tail.Count > 8)
        {
            tail.Dequeue();
        }
    }

    private static TimeSpan ParseHms(Match m) => TimeSpan.FromSeconds(
        int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600 +
        int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60 +
        double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture));

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
