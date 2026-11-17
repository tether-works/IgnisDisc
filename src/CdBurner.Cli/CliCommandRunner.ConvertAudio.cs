using System.Globalization;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;

namespace CdBurner.Cli;

/// <summary>
/// Audio export helpers shared by <c>rip-audio</c> and <c>convert-audio</c> (format lists, per-format bitrates,
/// MusicBrainz release choice errors) and the <c>convert-audio</c> command itself.
/// </summary>
public static partial class CliCommandRunner
{
    private static string FormatLabel(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Wav => "WAV",
        AudioRipFormat.Flac => "FLAC",
        AudioRipFormat.Mp3 => "MP3",
        AudioRipFormat.Opus => "OPUS",
        _ => "AAC"
    };

    /// <summary>
    /// Reads <c>--format</c> (a comma list), <c>--bitrate</c>, <c>--mp3-bitrate</c>, <c>--mp3-quality</c>,
    /// <c>--opus-bitrate</c> and <c>--aac-bitrate</c> into output targets. <paramref name="defaultFormat"/> null
    /// means <c>--format</c> is required. Throws <see cref="CommandLineException"/> with a plain message.
    /// </summary>
    internal static IReadOnlyList<AudioExportTarget> ParseAudioTargets(CommandLineArgs cmd, string? defaultFormat)
    {
        string formatText = defaultFormat != null
            ? cmd.GetOption("format") ?? defaultFormat
            : cmd.GetRequiredOption("format", "--format flac|mp3|opus|aac[,...]");

        var formats = new List<AudioRipFormat>();
        foreach (string token in formatText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!AudioRipFormats.TryParse(token, out AudioRipFormat format))
            {
                throw new CommandLineException($"Invalid audio format '{token}'. Supported formats are wav, flac, mp3, opus, aac.");
            }

            if (formats.Contains(format))
            {
                throw new CommandLineException($"The format '{token}' is listed more than once in --format.");
            }

            formats.Add(format);
        }

        if (formats.Count == 0)
        {
            throw new CommandLineException($"Invalid audio format '{formatText}'. Supported formats are wav, flac, mp3, opus, aac.");
        }

        int generic = ParseKbpsOption(cmd, "bitrate");
        int mp3Kbps = ParseKbpsOption(cmd, "mp3-bitrate");
        int opusKbps = ParseKbpsOption(cmd, "opus-bitrate");
        int aacKbps = ParseKbpsOption(cmd, "aac-bitrate");
        int? mp3Quality = ParseMp3QualityOption(cmd);

        if (generic > 0)
        {
            var lossy = formats.Where(AudioRipFormats.UsesBitrate).ToList();
            if (lossy.Count == 1)
            {
                switch (lossy[0])
                {
                    case AudioRipFormat.Mp3 when mp3Kbps > 0: throw GenericAndSpecificBitrate("--mp3-bitrate");
                    case AudioRipFormat.Opus when opusKbps > 0: throw GenericAndSpecificBitrate("--opus-bitrate");
                    case AudioRipFormat.Aac when aacKbps > 0: throw GenericAndSpecificBitrate("--aac-bitrate");
                    case AudioRipFormat.Mp3: mp3Kbps = generic; break;
                    case AudioRipFormat.Opus: opusKbps = generic; break;
                    default: aacKbps = generic; break;
                }
            }
            else if (formats.Count == 1)
            {
                throw new CommandLineException(AudioRipFormats.ValidateBitrate(formats[0], generic) ?? "A bitrate does not apply to this format.");
            }
            else
            {
                throw new CommandLineException(
                    "--bitrate applies only when exactly one lossy format (mp3, opus or aac) is chosen. " +
                    "Use --mp3-bitrate, --mp3-quality, --opus-bitrate or --aac-bitrate to set each format separately.");
            }
        }

        RequireFormatFor(formats, AudioRipFormat.Mp3, mp3Kbps > 0, "--mp3-bitrate");
        RequireFormatFor(formats, AudioRipFormat.Mp3, mp3Quality.HasValue, "--mp3-quality");
        RequireFormatFor(formats, AudioRipFormat.Opus, opusKbps > 0, "--opus-bitrate");
        RequireFormatFor(formats, AudioRipFormat.Aac, aacKbps > 0, "--aac-bitrate");
        if (mp3Kbps > 0 && mp3Quality.HasValue)
        {
            throw new CommandLineException("MP3 takes either a bitrate (--mp3-bitrate) or a VBR quality (--mp3-quality), not both.");
        }

        var targets = new List<AudioExportTarget>();
        foreach (var format in formats)
        {
            int kbps = format switch
            {
                AudioRipFormat.Mp3 => mp3Kbps,
                AudioRipFormat.Opus => opusKbps,
                AudioRipFormat.Aac => aacKbps,
                _ => 0
            };
            if (kbps > 0)
            {
                string? bitrateError = AudioRipFormats.ValidateBitrate(format, kbps);
                if (bitrateError != null)
                {
                    throw new CommandLineException(bitrateError);
                }
            }

            targets.Add(new AudioExportTarget(format, kbps, format == AudioRipFormat.Mp3 ? mp3Quality : null));
        }

        return targets;

        static CommandLineException GenericAndSpecificBitrate(string specific) =>
            new($"Use either --bitrate or {specific}, not both.");
    }

    private static void RequireFormatFor(List<AudioRipFormat> formats, AudioRipFormat needed, bool optionGiven, string optionName)
    {
        if (optionGiven && !formats.Contains(needed))
        {
            throw new CommandLineException($"{optionName} applies to {AudioRipFormats.DisplayName(needed)}, which is not in --format.");
        }
    }

    private static int ParseKbpsOption(CommandLineArgs cmd, string name)
    {
        string? text = cmd.GetOption(name);
        if (text == null)
        {
            return 0;
        }

        if (!int.TryParse(text.TrimEnd('k', 'K'), NumberStyles.None, CultureInfo.InvariantCulture, out int kbps) || kbps <= 0)
        {
            throw new CommandLineException($"Invalid bitrate '{text}'. Expected kbit/s as a whole number (e.g. --{name} 192).");
        }

        return kbps;
    }

    private static int? ParseMp3QualityOption(CommandLineArgs cmd)
    {
        string? text = cmd.GetOption("mp3-quality");
        if (text == null)
        {
            return null;
        }

        string digits = text.Trim().TrimStart('v', 'V');
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int quality) || quality is < 0 or > 9)
        {
            throw new CommandLineException($"Invalid MP3 quality '{text}'. Use V0 (best) to V9, for example --mp3-quality V2.");
        }

        return quality;
    }

    private static int WriteSimpleError(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, string message) =>
        HandleError(cmd, stdout, stderr, message);

    /// <summary>Stops <c>rip-audio</c> before the disc is read: lists the releases that share the disc ID and what differs.</summary>
    private static int WriteReleaseChoiceError(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        string driveId,
        IReadOnlyList<MusicBrainzRelease> releases,
        string headline)
    {
        var differences = MusicBrainzReleaseChoice.Differences(releases);
        var lines = new List<string> { headline, "Releases:" };
        for (int i = 0; i < releases.Count; i++)
        {
            lines.Add("  " + MusicBrainzReleaseChoice.Describe(releases[i], i + 1));
        }

        if (differences.Count > 0)
        {
            lines.Add("They differ in:");
            foreach (var difference in differences)
            {
                string where = difference.TrackNumber == 0 ? "album" : $"track {difference.TrackNumber}";
                lines.Add($"  {where}: {string.Join(" / ", difference.Titles)}");
            }
        }

        lines.Add("Choose one with --release <number>.");
        string message = string.Join(Environment.NewLine, lines);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = false,
                driveId,
                error = message,
                releases = releases.Select((r, i) => new
                {
                    index = i + 1,
                    id = r.ReleaseId,
                    title = r.Title,
                    artist = r.Artist,
                    country = r.Country,
                    date = r.Date
                }).ToList(),
                differences = differences.Select(d => new { trackNumber = d.TrackNumber, titles = d.Titles }).ToList()
            });
            return 1;
        }

        stderr.WriteLine("Error: " + message);
        return 1;
    }

    /// <summary>
    /// <c>convert-audio --input &lt;file|folder&gt; --output &lt;folder&gt; --format &lt;list&gt;</c>: converts existing audio
    /// files with ffmpeg. Exit 0 = every file converted or skipped, 2 = some failed, 1 = usage error or nothing to do.
    /// </summary>
    private static async Task<int> HandleConvertAudioAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string inputText = cmd.GetRequiredOption("input", "--input <file|folder>");
        string outputFolder = cmd.GetRequiredOption("output", "--output <folder>");
        var targets = ParseAudioTargets(cmd, null);

        string? naming = cmd.GetOption("naming");
        if (naming != null)
        {
            string? namingProblem = AudioNamingTemplate.Validate(naming);
            if (namingProblem != null)
            {
                throw new CommandLineException(namingProblem);
            }
        }

        var paths = inputText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new CommandLineException($"Input not found: '{path}'.");
            }
        }

        var inputs = AudioFileConverter.CollectInputs(paths, cmd.HasFlag("recursive"));
        if (inputs.Count == 0)
        {
            throw new CommandLineException(
                "No audio files found to convert (supported: " + string.Join(" ", AudioFileConverter.SupportedInputExtensions) +
                "). Add --recursive to include subfolders.");
        }

        string? ffmpegPath = FfmpegLocator.FindFfmpegExecutable();
        if (ffmpegPath == null)
        {
            throw new CommandLineException(AudioRipFormats.MissingFfmpegMessage(targets[0].Format));
        }

        var options = new AudioConvertOptions(targets, outputFolder, naming, ffmpegPath, cmd.HasFlag("overwrite"));
        string? problem = AudioFileConverter.CheckPrerequisites(options);
        if (problem != null)
        {
            throw new CommandLineException(problem);
        }

        var result = await AudioFileConverter.ConvertAsync(inputs, options).ConfigureAwait(false);
        int failed = result.Files.Count(f => f.Error != null);
        int skipped = result.Files.Count(f => f.Skipped);
        int exitCode = failed > 0 ? 2 : 0;

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = failed == 0,
                converted = result.Converted,
                failed,
                skipped,
                files = result.Files.Select(f => new
                {
                    input = f.Input,
                    format = FormatLabel(f.Format),
                    output = f.Output,
                    error = f.Error,
                    skipped = f.Skipped
                }).ToList(),
                warnings = result.Warnings
            });
            return exitCode;
        }

        stdout.WriteLine($"Converted {result.Converted} file(s); {skipped} skipped, {failed} failed.");
        foreach (var file in result.Files)
        {
            string status = file.Error != null ? $"FAILED: {file.Error}" : file.Skipped ? "skipped (exists)" : "ok";
            stdout.WriteLine($"  [{FormatLabel(file.Format)}] {file.Input} -> {file.Output ?? "(none)"}  {status}");
        }

        foreach (string warning in result.Warnings)
        {
            stdout.WriteLine($"  Warning: {warning}");
        }

        return exitCode;
    }
}
