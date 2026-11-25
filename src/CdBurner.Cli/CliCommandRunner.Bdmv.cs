using System.Globalization;
using CdBurner.Core.BluRayVideo;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Core.Subtitles;

namespace CdBurner.Cli;

/// <summary>The <c>author-bdmv</c> command: Blu-ray Video (BDMV) and AVCHD authoring without burning.</summary>
public static partial class CliCommandRunner
{
    /// <summary>Tests replace FFmpeg with a fake runner for the current async flow.</summary>
    internal static readonly AsyncLocal<IExternalToolRunner?> BdmvToolRunnerOverride = new();

    private static readonly string[] BdmvFlags = ["overwrite", "generate-subtitles"];

    private static readonly string[] BdmvOptions =
    [
        "input", "output", "target", "label", "video-format", "frame-rate", "audio", "audio-channels", "audio-language",
        "video-bitrate", "max-video-bitrate", "chapters", "subtitle-track", "subtitle-language", "ffmpeg"
    ];

    private static BdmvAuthoringOptions BuildBdmvOptions(CommandLineArgs cmd)
    {
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = cmd.GetOption("ffmpeg") };

        options.Target = (cmd.GetOption("target") ?? "bluray").Trim().ToLowerInvariant() switch
        {
            "bluray" or "blu-ray" or "bd" or "bdmv" => BdmvDiscTarget.BluRay,
            "avchd" => BdmvDiscTarget.Avchd,
            var other => throw new CommandLineException($"Invalid --target '{other}'. Use bluray or avchd.")
        };

        if (cmd.GetOption("label") is { } label)
        {
            options.VolumeLabel = label;
        }

        if (cmd.GetOption("video-format") is { } format)
        {
            options.VideoFormat = format.Trim().ToLowerInvariant() switch
            {
                "1080p" => BdmvVideoFormat.Hd1080p,
                "1080i" => BdmvVideoFormat.Hd1080i,
                "720p" => BdmvVideoFormat.Hd720p,
                _ => throw new CommandLineException($"Invalid --video-format '{format}'. Use 1080p, 1080i or 720p.")
            };
        }
        else if (options.Target == BdmvDiscTarget.Avchd)
        {
            options.VideoFormat = BdmvVideoFormat.Hd1080i;
            options.FrameRate = BdmvFrameRate.Fps25;
        }

        if (cmd.GetOption("frame-rate") is { } rate)
        {
            options.FrameRate = rate.Trim() switch
            {
                "23.976" or "23.98" => BdmvFrameRate.Fps23976,
                "24" => BdmvFrameRate.Fps24,
                "25" => BdmvFrameRate.Fps25,
                "29.97" => BdmvFrameRate.Fps2997,
                "50" => BdmvFrameRate.Fps50,
                "59.94" => BdmvFrameRate.Fps5994,
                _ => throw new CommandLineException($"Invalid --frame-rate '{rate}'. Use 23.976, 24, 25, 29.97, 50 or 59.94.")
            };
        }
        else if (cmd.GetOption("video-format") is { } && options.VideoFormat == BdmvVideoFormat.Hd1080i)
        {
            options.FrameRate = BdmvFrameRate.Fps25;
        }

        options.AudioCodec = (cmd.GetOption("audio") ?? "ac3").Trim().ToLowerInvariant() switch
        {
            "ac3" or "ac-3" or "dolby" => BdmvAudioCodec.Ac3,
            "lpcm" or "pcm" => BdmvAudioCodec.Lpcm,
            var other => throw new CommandLineException($"Invalid --audio '{other}'. Use ac3 or lpcm.")
        };

        if (cmd.GetOption("audio-channels") is { } channels)
        {
            options.AudioChannels = int.TryParse(channels, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c is 2 or 6
                ? c
                : throw new CommandLineException($"Invalid --audio-channels '{channels}'. Use 2 or 6.");
        }

        if (cmd.GetOption("audio-language") is { } audioLanguage)
        {
            options.AudioLanguage = RequireLanguage(audioLanguage, "--audio-language");
        }

        if (cmd.GetOption("video-bitrate") is { } bitrate)
        {
            options.VideoBitrateKbps = ParseKbps(bitrate, "--video-bitrate");
            options.MaxVideoBitrateKbps = Math.Max(options.MaxVideoBitrateKbps, options.VideoBitrateKbps);
        }
        if (cmd.GetOption("max-video-bitrate") is { } maxBitrate)
        {
            options.MaxVideoBitrateKbps = ParseKbps(maxBitrate, "--max-video-bitrate");
        }

        if (cmd.GetOption("chapters") is { } chapters)
        {
            string trimmed = chapters.Trim().ToLowerInvariant();
            if (trimmed is "none" or "off")
            {
                options.ChapterIntervalSeconds = 0;
            }
            else if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds is >= 0 and <= 3600)
            {
                options.ChapterIntervalSeconds = seconds;
            }
            else
            {
                throw new CommandLineException($"Invalid --chapters '{chapters}'. Use 'none' or an integer from 0 to 3600 seconds.");
            }
        }

        foreach (string raw in cmd.GetOptions("subtitle-track"))
        {
            if (!DvdLanguageCodes.TryParseTrackSpec(raw, out string language, out string path))
            {
                throw new CommandLineException($"Invalid --subtitle-track '{raw}'. Use <lang>:<path>, for example eng:subtitles.srt.");
            }
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Subtitle track file not found: '{full}'.", full);
            }
            options.Subtitles.Add(new BdmvSubtitleSpec(full, language));
        }
        if (cmd.HasFlag("generate-subtitles"))
        {
            string language = cmd.GetOption("subtitle-language") ?? "eng";
            options.Subtitles.Add(new BdmvSubtitleSpec(null, language.Length == 3 ? language.ToLowerInvariant() : "eng", GenerateWithWhisper: true));
        }

        string? problem = BdmvAuthoringService.ValidateOptions(options);
        if (problem != null)
        {
            throw new CommandLineException(problem);
        }
        return options;
    }

    private static string RequireLanguage(string value, string option)
    {
        string v = value.Trim().ToLowerInvariant();
        return v.Length == 3 && v.All(c => c is >= 'a' and <= 'z')
            ? v
            : throw new CommandLineException($"Invalid {option} '{value}'. Use a three-letter language code such as eng.");
    }

    private static int ParseKbps(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kbps) && kbps is >= 1000 and <= 40_000
            ? kbps
            : throw new CommandLineException($"Invalid {option} '{value}'. Use an integer from 1000 to 40000 kbit/s.");

    private static async Task<int> HandleAuthorBdmvAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string input = cmd.GetOption("input")
            ?? throw new CommandLineException("Missing required option: --input <movie[;movie...]>");
        string output = Path.GetFullPath(cmd.GetRequiredOption("output", "--output <folder|file.iso>"));
        bool overwrite = cmd.HasFlag("overwrite");
        var movies = input.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath).ToList();
        foreach (string movie in movies)
        {
            if (!File.Exists(movie))
            {
                throw new FileNotFoundException($"Movie file does not exist: '{movie}'.", movie);
            }
        }

        var options = BuildBdmvOptions(cmd);
        bool outputIsIso = string.Equals(Path.GetExtension(output), ".iso", StringComparison.OrdinalIgnoreCase);
        if (outputIsIso)
        {
            EnsureOutputCanBeWritten(output, overwrite);
        }
        else if (Directory.Exists(Path.Combine(output, "BDMV")) && Directory.EnumerateFileSystemEntries(Path.Combine(output, "BDMV")).Any())
        {
            if (!overwrite)
            {
                throw new CommandLineException($"Target folder '{output}' already contains a BDMV folder. Pass --overwrite to replace it.");
            }
            Directory.Delete(Path.Combine(output, "BDMV"), recursive: true);
            string certificate = Path.Combine(output, "CERTIFICATE");
            if (Directory.Exists(certificate))
            {
                Directory.Delete(certificate, recursive: true);
            }
        }

        string stagingRoot = outputIsIso
            ? Path.Combine(Path.GetTempPath(), "IgnisDisc_CliBdmv_" + Guid.NewGuid().ToString("N"))
            : output;
        try
        {
            var service = new BdmvAuthoringService { ToolRunner = BdmvToolRunnerOverride.Value ?? new ProcessToolRunner() };
            var progress = cmd.IsJson ? null : new Progress<(double Percent, string Status)>(p => stderr.WriteLine($"[{p.Percent,5:F1}%] {p.Status}"));
            var result = await service.AuthorAsync(movies, stagingRoot, options, progress).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Blu-ray Video authoring failed: {result.ErrorMessage}");
            }

            long totalBytes = result.TotalSizeBytes;
            if (outputIsIso)
            {
                await BdmvAuthoringService.BuildImageAsync(stagingRoot, output, options).ConfigureAwait(false);
                totalBytes = new FileInfo(output).Length;
            }

            string target = options.Target == BdmvDiscTarget.Avchd ? "avchd" : "bluray";
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    output,
                    outputKind = outputIsIso ? "iso" : "folder",
                    target,
                    volumeLabel = options.VolumeLabel,
                    videoFormat = options.VideoFormat.ToString(),
                    frameRate = options.FrameRate.ToString(),
                    audio = options.AudioCodec.ToString(),
                    udfRevision = outputIsIso ? "2.50" : null,
                    titleCount = result.Clips.Count,
                    clips = result.Clips.Select(c => new { clipName = c.ClipName, sourcePackets = c.SourcePackets, entryPoints = c.EntryPoints, subtitles = c.Subtitles }).ToArray(),
                    files = result.GeneratedFiles,
                    totalBytes,
                    verification = "structural",
                    playerTested = false,
                    logNotes = result.LogNotes
                });
                return 0;
            }

            stdout.WriteLine("Blu-ray Video authoring completed (no disc written):");
            stdout.WriteLine($"  Output:       {output} ({(outputIsIso ? "UDF 2.50 image" : "BDMV folder")})");
            stdout.WriteLine($"  Disc type:    {(options.Target == BdmvDiscTarget.Avchd ? "AVCHD (DVD-R / DVD+R DL)" : "Blu-ray Video (BD-R / BD-RE)")}");
            stdout.WriteLine($"  Titles:       {result.Clips.Count}");
            stdout.WriteLine($"  Size:         {FormatBytes(totalBytes)}");
            stdout.WriteLine("  Checked:      file structure only; not tested on a player.");
            foreach (string note in result.LogNotes)
            {
                stdout.WriteLine($"  Note:         {note}");
            }
            return 0;
        }
        finally
        {
            if (outputIsIso && Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
