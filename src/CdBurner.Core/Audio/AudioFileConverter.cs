using System.Globalization;

namespace CdBurner.Core.Audio;

/// <summary>What <see cref="AudioFileConverter.ConvertAsync"/> should produce.</summary>
/// <param name="Targets">Output formats (not WAV); each format only once.</param>
/// <param name="OutputFolder">Folder that receives the converted files.</param>
/// <param name="NamingTemplate">Template read from each file's tags; null keeps each input's path relative to its input root.</param>
/// <param name="FfmpegPath">Path to ffmpeg.exe.</param>
/// <param name="Overwrite">Replace outputs that already exist (they are skipped otherwise).</param>
public sealed record AudioConvertOptions(
    IReadOnlyList<AudioExportTarget> Targets,
    string OutputFolder,
    string? NamingTemplate,
    string FfmpegPath,
    bool Overwrite = false);

/// <summary>One file to convert. <paramref name="InputRoot"/> is the folder the user picked, or the file's own folder.</summary>
public sealed record AudioConvertInput(string Path, string InputRoot);

/// <summary>Outcome for one input and one output format.</summary>
public sealed record AudioConvertFileResult(string Input, AudioRipFormat Format, string? Output, string? Error, bool Skipped);

/// <summary>Outcome of <see cref="AudioFileConverter.ConvertAsync"/>.</summary>
public sealed record AudioConvertResult(IReadOnlyList<AudioConvertFileResult> Files, IReadOnlyList<string> Warnings)
{
    public int Converted => Files.Count(f => f.Output != null && f.Error == null && !f.Skipped);
}

/// <summary>
/// Converts existing audio files (WAV, FLAC, MP3, AAC, Ogg, Opus, WMA, AIFF) to FLAC, MP3, Opus or AAC with ffmpeg,
/// carrying over tags and, for MP3 and FLAC, embedded cover art. Inputs are never changed, and an output that would
/// land on an input file is refused.
/// </summary>
public static class AudioFileConverter
{
    private static readonly string[] InputExtensions =
        [".wav", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aif", ".aiff"];

    private static readonly string[] LossyExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma"];

    /// <summary>.wav .flac .mp3 .m4a .aac .ogg .opus .wma .aif .aiff (compared case-insensitively).</summary>
    public static IReadOnlyList<string> SupportedInputExtensions { get; } = InputExtensions;

    /// <summary>Expands files and folders (folders recursively when asked) into inputs sorted by full path; unsupported files are skipped.</summary>
    public static IReadOnlyList<AudioConvertInput> CollectInputs(IEnumerable<string> paths, bool recursive)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var found = new Dictionary<string, AudioConvertInput>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path = Path.GetFullPath(raw.Trim());
            if (File.Exists(path))
            {
                if (IsSupported(path))
                {
                    found.TryAdd(path, new AudioConvertInput(path, Path.GetDirectoryName(path)!));
                }
            }
            else if (Directory.Exists(path))
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string file in Directory.EnumerateFiles(path, "*", option))
                {
                    if (IsSupported(file))
                    {
                        found.TryAdd(Path.GetFullPath(file), new AudioConvertInput(Path.GetFullPath(file), path));
                    }
                }
            }
        }

        return found.Values.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSupported(string path) =>
        InputExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a plain-language problem with <paramref name="options"/>, or null when the conversion can run.</summary>
    public static string? CheckPrerequisites(AudioConvertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Targets == null || options.Targets.Count == 0)
        {
            return "Choose at least one output format.";
        }

        if (string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            return "The output folder is empty.";
        }

        var seen = new HashSet<AudioRipFormat>();
        foreach (var target in options.Targets)
        {
            string name = AudioRipFormats.DisplayName(target.Format);
            if (target.Format == AudioRipFormat.Wav)
            {
                return "WAV is not an output format for converting files. Choose flac, mp3, opus or aac.";
            }

            if (!seen.Add(target.Format))
            {
                return $"{name} is chosen more than once; each output format can be used only once.";
            }

            if (target.Mp3VbrQuality is < 0 or > 9)
            {
                return "MP3 quality must be between 0 (best) and 9.";
            }

            bool vbr = target.Format == AudioRipFormat.Mp3 && target.Mp3VbrQuality.HasValue;
            if (vbr && target.BitrateKbps > 0)
            {
                return "MP3 takes either a bitrate or a VBR quality, not both.";
            }

            if (target.BitrateKbps > 0)
            {
                string? bitrateError = AudioRipFormats.ValidateBitrate(target.Format, target.BitrateKbps);
                if (bitrateError != null)
                {
                    return bitrateError;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(options.FfmpegPath) || !File.Exists(options.FfmpegPath))
        {
            return AudioRipFormats.MissingFfmpegMessage(options.Targets[0].Format);
        }

        return options.NamingTemplate == null ? null : AudioNamingTemplate.Validate(options.NamingTemplate);
    }

    /// <summary>
    /// Builds the ffmpeg arguments for one conversion. Tags are copied from the input; an embedded picture is kept
    /// for MP3 and FLAC only. MP3 uses <c>-q:a</c> when a VBR quality is set, otherwise <c>-b:a</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(AudioExportTarget target, string inputPath, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (target.Format == AudioRipFormat.Wav)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "WAV is not an output format for converting files.");
        }

        if (target.Mp3VbrQuality is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "MP3 quality must be between 0 (best) and 9.");
        }

        int bitrate = target.BitrateKbps > 0 ? target.BitrateKbps : AudioRipFormats.DefaultBitrateKbps(target.Format);
        bool withCover = target.Format is AudioRipFormat.Mp3 or AudioRipFormat.Flac;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", inputPath, "-map", "0:a:0" };
        if (withCover)
        {
            args.AddRange(["-map", "0:v:0?", "-c:v", "copy", "-disposition:v", "attached_pic"]);
        }

        args.AddRange(["-map_metadata", "0"]);
        switch (target.Format)
        {
            case AudioRipFormat.Flac:
                args.AddRange(["-c:a", "flac"]);
                break;
            case AudioRipFormat.Mp3:
                args.AddRange(["-c:a", "libmp3lame"]);
                if (target.Mp3VbrQuality is { } quality)
                {
                    args.AddRange(["-q:a", quality.ToString(CultureInfo.InvariantCulture)]);
                }
                else
                {
                    args.AddRange(["-b:a", $"{bitrate}k"]);
                }

                args.AddRange(["-id3v2_version", "3"]);
                break;
            case AudioRipFormat.Opus:
                args.AddRange(["-c:a", "libopus", "-b:a", $"{bitrate}k", "-ar", "48000"]);
                break;
            case AudioRipFormat.Aac:
                args.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k"]);
                break;
        }

        args.Add(outputPath);
        return args;
    }

    private static string EncoderName(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Flac => "flac",
        AudioRipFormat.Mp3 => "libmp3lame",
        AudioRipFormat.Opus => "libopus",
        _ => "aac"
    };

    /// <summary>
    /// Converts every input to every target format. A file that fails is recorded and the next one is tried; an
    /// output that already exists is skipped unless <see cref="AudioConvertOptions.Overwrite"/> is set.
    /// </summary>
    public static async Task<AudioConvertResult> ConvertAsync(
        IReadOnlyList<AudioConvertInput> inputs,
        AudioConvertOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        string? problem = CheckPrerequisites(options);
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }

        string outputRoot = Path.GetFullPath(options.OutputFolder);
        string rootPrefix = outputRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var inputPaths = new HashSet<string>(inputs.Select(i => Path.GetFullPath(i.Path)), StringComparer.OrdinalIgnoreCase);
        var results = new List<AudioConvertFileResult>();
        var warnings = new List<string>();

        int lossy = inputs.Count(i => LossyExtensions.Contains(Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase));
        if (lossy > 0)
        {
            warnings.Add($"{lossy} file(s) were already lossy; converting them cannot restore quality and adds a little more loss.");
        }

        // A missing encoder in this ffmpeg build fails every file of that format with one clear message.
        var encoderProblems = new Dictionary<AudioRipFormat, string>();
        foreach (var target in options.Targets)
        {
            string encoder = EncoderName(target.Format);
            if (!await FfmpegProcess.HasEncoderAsync(options.FfmpegPath, encoder, cancellationToken).ConfigureAwait(false))
            {
                encoderProblems[target.Format] =
                    $"This ffmpeg build ({options.FfmpegPath}) has no {encoder} encoder, so it cannot write {AudioRipFormats.DisplayName(target.Format)}. " +
                    "Install the optional ffmpeg download from IgnisDisc, which includes it.";
            }
        }

        bool needFormatFolder = options.Targets.Count > 1 &&
            (options.NamingTemplate == null || !options.NamingTemplate.Contains("{format}", StringComparison.OrdinalIgnoreCase));
        var plannedOutputs = new Dictionary<AudioRipFormat, HashSet<string>>();
        int done = 0;
        int total = inputs.Count * options.Targets.Count;

        foreach (var input in inputs)
        {
            string inputFull = Path.GetFullPath(input.Path);
            IReadOnlyDictionary<string, string>? tags = null;

            foreach (var target in options.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                string label = AudioRipFormats.DisplayName(target.Format);
                progress?.Report($"{label} {done}/{total}: {Path.GetFileName(input.Path)}");

                if (encoderProblems.TryGetValue(target.Format, out string? encoderProblem))
                {
                    results.Add(new AudioConvertFileResult(inputFull, target.Format, null, encoderProblem, false));
                    continue;
                }

                string output;
                try
                {
                    if (options.NamingTemplate != null && tags == null)
                    {
                        tags = await ReadTagsAsync(options.FfmpegPath, inputFull, cancellationToken).ConfigureAwait(false);
                    }

                    output = RenderOutputPath(input, inputFull, target, options, tags, outputRoot, needFormatFolder);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    results.Add(new AudioConvertFileResult(inputFull, target.Format, null, ex.Message, false));
                    continue;
                }

                if (!output.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new AudioConvertFileResult(inputFull, target.Format, null, $"The output path '{output}' is outside the output folder.", false));
                    continue;
                }

                if (!plannedOutputs.TryGetValue(target.Format, out var used))
                {
                    plannedOutputs[target.Format] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                output = MakeUnique(output, AudioRipFormats.Extension(target.Format), used);

                if (inputPaths.Contains(output))
                {
                    results.Add(new AudioConvertFileResult(inputFull, target.Format, output, "The output would replace an input file, so it was not written.", false));
                    continue;
                }

                if (File.Exists(output) && !options.Overwrite)
                {
                    warnings.Add($"Skipped '{output}': it already exists (use --overwrite to replace it).");
                    results.Add(new AudioConvertFileResult(inputFull, target.Format, output, null, true));
                    continue;
                }

                results.Add(await ConvertOneAsync(inputFull, output, target, options.FfmpegPath, cancellationToken).ConfigureAwait(false));
            }
        }

        return new AudioConvertResult(results, warnings);
    }

    /// <summary>
    /// Checks an input file before invoking ffmpeg so truncated RIFF/WAV files fail explicitly instead of
    /// letting ffmpeg emit a partial output file with exit code 0.
    /// </summary>
    internal static string? ValidateInputFile(string inputFull)
    {
        if (string.Equals(Path.GetExtension(inputFull), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                WavFileCodec.ProbeAudioFile(inputFull);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                return ex.Message;
            }
            catch (NotSupportedException)
            {
                // Valid RIFF/WAVE structure with a non-PCM codec tag (e.g. ADPCM) that ffmpeg can still decode.
            }
        }

        return null;
    }

    private static async Task<AudioConvertFileResult> ConvertOneAsync(
        string inputFull, string output, AudioExportTarget target, string ffmpegPath, CancellationToken cancellationToken)
    {
        string? validationError = ValidateInputFile(inputFull);
        if (validationError != null)
        {
            return new AudioConvertFileResult(inputFull, target.Format, null, validationError, false);
        }

        string directory = Path.GetDirectoryName(output)!;
        string temp = Path.Combine(directory, "~ignis-" + Guid.NewGuid().ToString("N") + AudioRipFormats.Extension(target.Format));
        try
        {
            Directory.CreateDirectory(directory);
            var args = BuildArguments(target, inputFull, temp);
            var (exitCode, _, stderr) = await FfmpegProcess.CaptureAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                string tail = stderr.Length > 500 ? stderr[^500..] : stderr;
                return new AudioConvertFileResult(inputFull, target.Format, output, $"ffmpeg failed with exit code {exitCode}: {tail.Trim()}", false);
            }

            File.Move(temp, output, overwrite: true);
            return new AudioConvertFileResult(inputFull, target.Format, output, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new AudioConvertFileResult(inputFull, target.Format, output, ex.Message, false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string RenderOutputPath(
        AudioConvertInput input,
        string inputFull,
        AudioExportTarget target,
        AudioConvertOptions options,
        IReadOnlyDictionary<string, string>? tags,
        string outputRoot,
        bool needFormatFolder)
    {
        string extension = AudioRipFormats.Extension(target.Format);
        if (options.NamingTemplate == null)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(input.InputRoot), inputFull);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                relative = Path.GetFileName(inputFull);
            }

            relative = Path.ChangeExtension(relative, extension);
            if (needFormatFolder)
            {
                relative = Path.Combine(AudioRipFormats.DisplayName(target.Format), relative);
            }

            return Path.GetFullPath(Path.Combine(outputRoot, relative));
        }

        string template = needFormatFolder ? @"{format}\" + options.NamingTemplate : options.NamingTemplate;
        var t = tags ?? new Dictionary<string, string>();
        string stem = Path.GetFileNameWithoutExtension(inputFull);
        string title = Get(t, "title") ?? stem;
        string artist = Get(t, "artist") ?? string.Empty;
        string albumArtist = Get(t, "album_artist") ?? Get(t, "albumartist") ?? artist;
        string album = Get(t, "album") ?? string.Empty;
        string? date = Get(t, "date") ?? Get(t, "year");
        string? year = date != null && date.Length >= 4 && date[..4].All(char.IsAsciiDigit) ? date[..4] : null;
        (int trackNumber, int trackTotal) = ParseTrack(Get(t, "track") ?? Get(t, "tracknumber"), Get(t, "tracktotal") ?? Get(t, "totaltracks"));
        int disc = 1;
        string? discText = Get(t, "disc") ?? Get(t, "discnumber");
        if (discText != null && int.TryParse(discText.Split('/')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedDisc) && parsedDisc > 0)
        {
            disc = parsedDisc;
        }

        var fields = new AudioNamingFields(albumArtist, artist, album, year, trackNumber, trackTotal, title, target.Format, disc);
        string rendered = AudioNamingTemplate.Render(template, fields, extension);
        return Path.GetFullPath(Path.Combine(outputRoot, rendered));
    }

    private static string? Get(IReadOnlyDictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static (int Number, int Total) ParseTrack(string? track, string? total)
    {
        int number = 0;
        int count = 0;
        if (track != null)
        {
            string[] parts = track.Split('/');
            int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number);
            if (parts.Length > 1)
            {
                int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count);
            }
        }

        if (count == 0 && total != null)
        {
            int.TryParse(total.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count);
        }

        return (number, count);
    }

    private static string MakeUnique(string fullPath, string extension, HashSet<string> used)
    {
        if (used.Add(fullPath))
        {
            return fullPath;
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        string stem = Path.GetFileName(fullPath);
        stem = stem[..^extension.Length];
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Reads a file's tags with <c>ffmpeg -f ffmetadata</c>; keys are lower-cased. A file ffmpeg cannot read gives no tags.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadTagsAsync(string ffmpegPath, string inputPath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await FfmpegProcess.CaptureAsync(
            ffmpegPath, ["-hide_banner", "-nostdin", "-i", inputPath, "-f", "ffmetadata", "-"], cancellationToken).ConfigureAwait(false);
        return exitCode == 0 ? ParseFfMetadata(stdout) : new Dictionary<string, string>();
    }

    /// <summary>Parses the global section of ffmetadata text (<c>key=value</c> lines after <c>;FFMETADATA1</c>).</summary>
    internal static IReadOnlyDictionary<string, string> ParseFfMetadata(string text)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                break; // chapter and stream sections follow the global tags
            }

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            int eq = FindUnescaped(line, '=');
            if (eq <= 0)
            {
                continue;
            }

            string key = Unescape(line[..eq]).Trim().ToLowerInvariant();
            tags.TryAdd(key, Unescape(line[(eq + 1)..]));
        }

        return tags;
    }

    private static int FindUnescaped(string line, char c)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
