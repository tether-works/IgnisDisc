using CdBurner.Core.DvdVideo;
using CdBurner.Core.Subtitles;

namespace CdBurner.Cli;

/// <summary>
/// Subtitle commands: <c>install-subtitle-tool</c>, <c>generate-subtitles</c>, and the options
/// <c>burn-dvd --generate-subtitles [--subtitle-language &lt;code&gt;]</c> use.
/// </summary>
public static partial class CliCommandRunner
{
    /// <summary>
    /// <c>install-subtitle-tool [--plan-only] [--json]</c>: downloads whisper.cpp and a speech model for this PC
    /// (pinned by SHA-256), or with <c>--plan-only</c> only says what it would download.
    /// </summary>
    private static async Task<int> HandleInstallSubtitleToolAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        WhisperInstallPlan plan = WhisperCatalog.ChoosePlanForThisPc();
        if (cmd.HasFlag("plan-only"))
        {
            WhisperInstallation? installed = WhisperLocator.Find();
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    build = plan.Build.ToString(),
                    program = plan.Program.Url,
                    model = plan.Model.Url,
                    downloadBytes = plan.DownloadBytes,
                    reason = plan.Reason,
                    installed = installed?.Describe()
                });
                return 0;
            }

            stdout.WriteLine($"Subtitle tool for this PC: {plan.Reason}");
            stdout.WriteLine($"  Program: {plan.Program.Url}");
            stdout.WriteLine($"  Model:   {plan.Model.Url}");
            stdout.WriteLine($"  Download: {plan.DownloadBytes / 1_000_000.0:N0} MB");
            stdout.WriteLine($"  Installed now: {installed?.Describe() ?? "nothing"}");
            return 0;
        }

        var progress = cmd.IsJson ? null : new ThrottledLineProgress(stderr);
        if (!cmd.IsJson)
        {
            stdout.WriteLine($"{plan.Reason} Download: {plan.DownloadBytes / 1_000_000.0:N0} MB.");
        }

        WhisperInstallResult result = await WhisperToolInstaller.InstallAsync(plan, progress: progress).ConfigureAwait(false);
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                build = plan.Build.ToString(),
                cli = result.Installation?.CliPath,
                model = result.Installation?.ModelPath,
                error = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            stderr.WriteLine($"Subtitle tool install failed: {result.ErrorMessage}");
            return 1;
        }

        stdout.WriteLine($"Installed: {result.Installation!.Describe()}");
        stdout.WriteLine($"  {result.Installation.CliPath}");
        stdout.WriteLine($"  {result.Installation.ModelPath}");
        return 0;
    }

    /// <summary>
    /// <c>generate-subtitles --input &lt;movie&gt; [--output &lt;file.srt&gt;] [--language &lt;code|auto&gt;] [--json]</c>:
    /// makes subtitles by speech recognition. The .srt goes to the subtitle cache; <c>--output</c> also copies it to
    /// the given file (never written next to the movie unless that is the path given).
    /// </summary>
    private static async Task<int> HandleGenerateSubtitlesAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string input = cmd.GetRequiredOption("input", "--input <movie>");
        string? output = cmd.GetOption("output");
        string language = cmd.GetOption("language") ?? "en";
        if (!SubtitleGenerationService.IsValidLanguage(language))
        {
            throw new CommandLineException($"--language must be a two- or three-letter code (for example en) or 'auto', not '{language}'.");
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Movie file not found: {input}", input);
        }

        var progress = cmd.IsJson ? null : new ThrottledSubtitleProgress(stderr);
        SubtitleGenerationResult result = await new SubtitleGenerationService().GenerateAsync(
            input,
            new SubtitleGenerationOptions { Language = language, FfmpegPath = cmd.GetOption("ffmpeg") },
            progress).ConfigureAwait(false);

        string? copiedTo = null;
        if (result.Success && !string.IsNullOrWhiteSpace(output))
        {
            copiedTo = Path.GetFullPath(output);
            string? dir = Path.GetDirectoryName(copiedTo);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(result.SrtPath!, copiedTo, overwrite: true);
        }

        double? speed = result.AudioDuration is { } audio && result.Elapsed.TotalSeconds > 0
            ? audio.TotalSeconds / result.Elapsed.TotalSeconds
            : null;

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                srt = result.SrtPath,
                copiedTo,
                subtitles = result.CueCount,
                elapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1),
                audioSeconds = result.AudioDuration is { } a ? Math.Round(a.TotalSeconds, 1) : (double?)null,
                timesRealTime = speed is { } s ? Math.Round(s, 1) : (double?)null,
                fromCache = result.FromCache,
                backend = result.Backend,
                message = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            stderr.WriteLine(result.ErrorMessage);
            return 1;
        }

        stdout.WriteLine($"{result.CueCount} machine-generated subtitles: {result.SrtPath}");
        if (copiedTo != null)
        {
            stdout.WriteLine($"  Copied to: {copiedTo}");
        }

        if (result.FromCache)
        {
            stdout.WriteLine("  Reused from an earlier run (same file, model and language).");
        }
        else
        {
            string audioText = result.AudioDuration is { } a ? $" for {a.TotalMinutes:F1} min of audio" : string.Empty;
            string speedText = speed is { } s ? $" ({s:F1}x real time)" : string.Empty;
            stdout.WriteLine($"  Took {result.Elapsed.TotalMinutes:F1} min{audioText}{speedText} on the {result.Backend ?? "unknown device"}.");
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            stdout.WriteLine($"  Note: {result.ErrorMessage}");
        }

        stdout.WriteLine("  Speech recognition mishears some words, especially names; check the file before relying on it.");
        return 0;
    }

    /// <summary>
    /// Checks <c>burn-dvd</c>'s subtitle options and copies them into the authoring options. Fails before anything is
    /// authored when generation is asked for but the tool is not installed.
    /// </summary>
    private static void ApplySubtitleOptions(CommandLineArgs cmd, DvdAuthoringOptions authorOptions)
    {
        if (!cmd.HasFlag("generate-subtitles"))
        {
            return;
        }

        string language = cmd.GetOption("subtitle-language") ?? "en";
        if (!SubtitleGenerationService.IsValidLanguage(language))
        {
            throw new CommandLineException($"--subtitle-language must be a two- or three-letter code (for example en) or 'auto', not '{language}'.");
        }

        if (WhisperLocator.Find() == null)
        {
            throw new CommandLineException("--generate-subtitles needs the optional subtitle tool. Install it first with: ignisdisc-cli install-subtitle-tool");
        }

        authorOptions.GenerateSubtitles = true;
        authorOptions.SubtitleLanguage = language;
    }

    /// <summary>Writes subtitle progress to a console stream every 5% (on the calling thread, in order).</summary>
    private sealed class ThrottledSubtitleProgress(TextWriter writer) : IProgress<SubtitleGenerationProgress>
    {
        private int _lastStep = -1;

        public void Report(SubtitleGenerationProgress value)
        {
            int step = (int)(value.Percent / 5);
            if (step != _lastStep)
            {
                _lastStep = step;
                writer.WriteLine(value.Status);
            }
        }
    }

    /// <summary>Writes download status lines, skipping repeats of the same whole percentage band (every 5%).</summary>
    private sealed class ThrottledLineProgress(TextWriter writer) : IProgress<string>
    {
        private string? _lastKey;

        public void Report(string value)
        {
            // "Downloading X... 37% (...)" -> key "Downloading X... 35"; other messages always print.
            int pct = value.IndexOf("% (", StringComparison.Ordinal);
            string key = value;
            if (pct > 0)
            {
                int start = value.LastIndexOf(' ', pct) + 1;
                if (int.TryParse(value.AsSpan(start, pct - start), out int p))
                {
                    key = value[..start] + (p / 5 * 5);
                }
            }

            if (key != _lastKey)
            {
                _lastKey = key;
                writer.WriteLine(value);
            }
        }
    }

    /// <summary>Forwards only the subtitle steps of DVD authoring progress to the console, every 5%.</summary>
    private sealed class SubtitleStepProgress(TextWriter writer) : IProgress<(double Percent, string Status)>
    {
        private string? _last;

        public void Report((double Percent, string Status) value)
        {
            if (!value.Status.StartsWith("Subtitles", StringComparison.Ordinal))
            {
                return;
            }

            // Keep the line when the whole-5% band of the trailing "NN%" changes.
            string status = value.Status;
            int pctIndex = status.LastIndexOf('%');
            string key = status;
            if (pctIndex > 0)
            {
                int start = status.LastIndexOf(' ', pctIndex) + 1;
                if (int.TryParse(status.AsSpan(start, pctIndex - start), out int p))
                {
                    key = status[..start] + (p / 5 * 5);
                }
            }

            if (key != _last)
            {
                _last = key;
                writer.WriteLine(status);
            }
        }
    }
}
