namespace CdBurner.Cli;

/// <summary>
/// Known value options and per-command option/flag validation tables for <see cref="CliCommandRunner"/>.
/// </summary>
public static partial class CliCommandRunner
{
    private static readonly HashSet<string> GlobalFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "help", "h"
    };

    private static readonly HashSet<string> TopLevelFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "help", "h", "version", "v"
    };

    private static readonly HashSet<string> KnownValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac-bitrate", "after-title", "append-to", "art-inner-radius-mm", "art-outer-radius-mm",
        "artist", "aspect", "bitrate", "block-size", "boot-bios", "boot-uefi", "candidate",
        "cells", "chapters", "compare", "config", "copies", "cover", "cover-art", "crossfade",
        "csv", "cue", "data-folder", "data-label", "date", "dir", "disc", "disc-preset",
        "disc-presets-file", "drive", "drive-model", "ffmpeg", "files", "fill-picture", "fit",
        "fixed-time", "format", "from", "image", "images", "inner-radius-mm", "input", "iso",
        "json-output", "key", "label", "language", "layer-break", "library", "log-dir",
        "loudness", "media", "media-id", "menu", "menu-autoplay", "menu-title", "mp3-bitrate",
        "mp3-quality", "naming", "offset-x-mm", "offset-y-mm", "opus-bitrate", "order", "out",
        "outer-radius-mm", "output", "pages-radius", "par2", "path", "picture", "pitch-cubic",
        "pitch-linear", "pitch-quadratic", "points", "post-action", "pregap", "pregap-art",
        "pregap-seconds", "read-offset", "redundancy", "release", "retries", "rotate", "scale",
        "sectors", "set", "settings-file", "size", "songwriter", "source", "spacing", "speed",
        "stage", "staging", "standard", "start-angle", "store", "subtitle-language",
        "subtitle-size", "subtitles-default", "target", "text", "title", "title-names",
        "title-ring", "title-ring-date", "title-ring-height-mm", "to", "tone", "tracks",
        "tracks-file", "turn-map", "turn-map-offset", "udf-rev", "upc", "value",
        "video-bitrate", "video-ts", "wait-minutes", "write-offset"
    };

    private sealed record CommandOptionSpec(HashSet<string> Flags, HashSet<string> Options)
    {
        public static CommandOptionSpec Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public static CommandOptionSpec Of(IEnumerable<string> flags, IEnumerable<string> options) =>
            new(
                new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(options, StringComparer.OrdinalIgnoreCase));

        public static CommandOptionSpec Merge(params CommandOptionSpec[] specs)
        {
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in specs)
            {
                flags.UnionWith(s.Flags);
                options.UnionWith(s.Options);
            }
            return new CommandOptionSpec(flags, options);
        }
    }

    private static readonly string[] CalibrationParamOptions =
    [
        "disc-preset", "disc-presets-file", "drive-model", "media-id", "store",
        "inner-radius-mm", "outer-radius-mm", "sectors",
        "pitch-linear", "pitch-quadratic", "pitch-cubic"
    ];

    private static readonly string[] AudioTargetOptions =
    [
        "format", "bitrate", "mp3-bitrate", "mp3-quality", "opus-bitrate", "aac-bitrate"
    ];

    private static readonly string[] AudioCompilationFlags =
    [
        "dao", "no-cd-text", "normalize", "peak-normalize", "rms-level"
    ];

    private static readonly string[] AudioCompilationOptions =
    [
        "tracks", "tracks-file", "title", "artist", "songwriter", "upc",
        "loudness", "crossfade", "pregap"
    ];

    private static readonly string[] DvdAuthoringFlags =
    [
        "dual-layer", "fit-to-disc", "no-ffmpeg", "no-menu", "generate-subtitles"
    ];

    private static readonly string[] DvdAuthoringOptions =
    [
        "label", "ffmpeg", "standard", "aspect", "menu", "menu-title", "menu-autoplay",
        "after-title", "chapters", "subtitles-default", "subtitle-size", "video-bitrate",
        "title-names", "subtitle-language"
    ];

    private static readonly CommandOptionSpec BurnIsoSpec = CommandOptionSpec.Of(
        ["native", "test-write", "test-first", "windows-writer", "strict-verify", "verify", "no-verify", "close", "no-close", "overwrite", "no-burnfree", "confirm-shutdown"],
        ["drive", "iso", "speed", "log-dir", "layer-break", "post-action"]);

    private static readonly CommandOptionSpec BurnDataSpec = CommandOptionSpec.Of(
        ["native", "test-write", "test-first", "windows-writer", "strict-verify", "verify", "no-verify", "close", "no-close", "overwrite", "append", "no-burnfree", "udf", "joliet", "sha256sums", "blake3sums", "confirm-shutdown"],
        ["drive", "input", "label", "speed", "boot-bios", "boot-uefi", "udf-rev", "fixed-time", "log-dir", "layer-break", "post-action", "fill-picture", .. CalibrationParamOptions]);

    private static readonly CommandOptionSpec BurnAudioSpec = CommandOptionSpec.Of(
        ["plan-only", "native", "test-write", "test-first", "recovery-session", "blue-book", "no-burnfree", "allow-damaged", "lookup", "confirm-shutdown", .. AudioCompilationFlags],
        ["drive", "speed", "data-folder", "data-label", "log-dir", "write-offset", "post-action", "fill-picture", "drive-model", "media-id", "store", .. AudioCompilationOptions]);

    private static readonly CommandOptionSpec BurnDvdSpec = CommandOptionSpec.Of(
        ["native", "test-write", "test-first", "windows-writer", "no-burnfree", "no-verify", "verify", "overwrite", "confirm-shutdown", .. DvdAuthoringFlags],
        ["drive", "video-ts", "input", "speed", "log-dir", "post-action", "fill-picture", "layer-break", .. DvdAuthoringOptions, .. CalibrationParamOptions]);

    private static readonly CommandOptionSpec RosettaLadderSpec = CommandOptionSpec.Of(
        ["no-interleave", "plan-only"],
        ["media", "output", "out", "json-output", "disc-preset", "disc-presets-file", "start-angle"]);

    private static readonly CommandOptionSpec RosettaBuildSpec = CommandOptionSpec.Of(
        ["no-interleave", "plan-only", "no-fill", "no-rim", "overwrite"],
        ["media", "text", "output", "out", "title", "date", "cells", "redundancy", "files", "pages-radius", "start-angle", .. CalibrationParamOptions]);

    private static readonly CommandOptionSpec RosettaBurnSpec = CommandOptionSpec.Merge(
        BurnIsoSpec,
        BurnAudioSpec,
        CommandOptionSpec.Of([], ["image", "drive"]));

    private static readonly CommandOptionSpec DiscArtGenerateSpec = CommandOptionSpec.Of(
        ["files-outer", "no-interleave", "read-compat", "overwrite", "rim-header", "plan-only"],
        [
            "media", "picture", "output", "out", "turn-map", "turn-map-offset", "tone", "files",
            "append-to", "pregap-art", "pregap-seconds", "scale", "offset-x-mm", "offset-y-mm",
            "rotate", "fit", "label", "start-angle", "art-inner-radius-mm", "art-outer-radius-mm",
            "title-ring", "title-ring-height-mm", "title-ring-date", .. CalibrationParamOptions
        ]);

    private static readonly CommandOptionSpec DiscArtPreviewSpec = CommandOptionSpec.Of(
        [],
        ["image", "output", "out", "size"]);

    private static readonly CommandOptionSpec DiscArtBurnSpec = CommandOptionSpec.Merge(
        BurnIsoSpec,
        BurnAudioSpec,
        CommandOptionSpec.Of([], ["image", "drive"]));

    private static readonly CommandOptionSpec DiscArtCalibrateTargetSpec = CommandOptionSpec.Of(
        ["no-interleave", "overwrite"],
        ["media", "stage", "output", "out", .. CalibrationParamOptions]);

    private static readonly CommandOptionSpec DiscArtCalibrateFitSpec = CommandOptionSpec.Of(
        [],
        ["points", "drive-model", "media-id", "candidate", "store"]);

    private static readonly CommandOptionSpec DiscArtCalibrateListSpec = CommandOptionSpec.Of(
        [],
        ["store"]);

    private static readonly CommandOptionSpec DiscArtMeasureSpec = CommandOptionSpec.Of(
        [],
        ["drive", "output", "out", "from", "to", "spacing"]);

    private static readonly Dictionary<string, CommandOptionSpec> CommandSpecs = BuildCommandSpecs();

    private static readonly IReadOnlyList<string> SupportedCommandNames =
    [
        "drives", "drive-report", "media-info", "eject", "load", "build-iso", "burn-iso",
        "burn-data", "burn-audio", "burn-dvd", "install-subtitle-tool", "generate-subtitles",
        "rip", "rip-audio", "convert-audio", "copy-disc", "check-disc", "detect-offset",
        "archive-stage", "archive-burn", "archive-verify", "recover", "verify", "erase", "scan",
        "settings", "tools", "install-ffmpeg", "install-par2", "check-update", "image-info",
        "convert-image", "author-dvd", "jewel-cover", "audio-plan", "burn-queue", "book-type",
        "repair-par2", "rosetta", "disc-art"
    ];

    private static Dictionary<string, CommandOptionSpec> BuildCommandSpecs()
    {
        var specs = new Dictionary<string, CommandOptionSpec>(StringComparer.OrdinalIgnoreCase);

        void Add(CommandOptionSpec spec, params string[] names)
        {
            foreach (string name in names)
            {
                specs[name] = spec;
            }
        }

        Add(CommandOptionSpec.Empty, "drives", "list-drives", "check-update");
        Add(CommandOptionSpec.Of([], ["drive"]), "media-info", "mediainfo", "info", "drive-report", "drivereport", "eject", "load", "detect-offset", "detectoffset");
        Add(CommandOptionSpec.Of(["udf", "joliet", "sha256sums", "blake3sums"], ["input", "output", "label", "boot-bios", "boot-uefi", "udf-rev", "fixed-time"]), "build-iso", "buildiso");
        Add(BurnIsoSpec, "burn-iso", "burniso");
        Add(BurnDataSpec, "burn-data", "burndata");
        Add(BurnAudioSpec, "burn-audio", "burnaudio");
        Add(BurnDvdSpec, "burn-dvd", "burndvd");
        Add(CommandOptionSpec.Of(["raw"], ["drive", "output"]), "rip", "rip-disc");
        Add(CommandOptionSpec.Of(
            ["lookup", "keep-wav", "no-folder-image", "overwrite", "continue-on-error", "verify-accuraterip", "verify-ctdb", "no-ctdb-repair", "no-cover-lookup", "repair", "repair-from-recovery", "no-secure", "secure", "no-replaygain", "de-emphasis"],
            ["drive", "library", "output", "release", "naming", "tracks", "read-offset", "cover", "cover-art", .. AudioTargetOptions]),
            "rip-audio", "ripaudio");
        Add(CommandOptionSpec.Of(["overwrite", "recursive"], ["input", "output", "naming", .. AudioTargetOptions]), "convert-audio", "convertaudio");
        Add(CommandOptionSpec.Of([], ["drive", "compare"]), "check-disc", "checkdisc");
        Add(CommandOptionSpec.Of(["native", "raw", "allow-suspect"], ["source", "target", "copies", "wait-minutes", "log-dir", "keep-image", "speed"]), "copy-disc", "copydisc", "copy");
        Add(CommandOptionSpec.Of(["plan-only", "keep-folders", "no-fill"], ["input", "staging", "media", "redundancy", "order"]), "archive-stage", "archivestage");
        Add(CommandOptionSpec.Of(
            ["native", "test-write", "test-first", "windows-writer", "strict-verify", "verify", "no-verify", "overwrite", "no-burnfree", "health-certificate", "confirm-shutdown"],
            ["drive", "disc", "speed", "log-dir", "post-action"]),
            "archive-burn", "archiveburn");
        Add(CommandOptionSpec.Of([], ["path"]), "archive-verify", "archiveverify");
        Add(CommandOptionSpec.Of(["keep-image"], ["out", "image", "drive", "retries"]), "recover", "archive-recover");
        Add(CommandOptionSpec.Of(["strict-verify", "no-cd-text"], ["drive", "cue", "iso", "log-dir", "title", "artist", "write-offset"]), "verify", "verify-disc");
        Add(CommandOptionSpec.Of(["full"], ["drive"]), "erase", "erase-disc");
        Add(CommandOptionSpec.Of([], ["drive", "block-size", "csv", "output"]), "scan", "scan-disc");
        Add(CommandOptionSpec.Of(["plan-only"], []), "install-subtitle-tool");
        Add(CommandOptionSpec.Of([], ["input", "output", "language", "ffmpeg"]), "generate-subtitles");
        Add(CommandOptionSpec.Of([], ["settings-file", "config", "key", "value"]), "settings");
        Add(CommandOptionSpec.Of([], ["ffmpeg", "par2"]), "tools");
        Add(CommandOptionSpec.Of(["plan-only", "overwrite"], ["dir"]), "install-ffmpeg", "install-par2");
        Add(CommandOptionSpec.Of([], ["image", "input"]), "image-info", "imageinfo");
        Add(CommandOptionSpec.Of(["overwrite"], ["input", "output", "cue"]), "convert-image", "convertimage");
        Add(CommandOptionSpec.Of(["overwrite", .. DvdAuthoringFlags], ["input", "video-ts", "output", .. DvdAuthoringOptions]), "author-dvd", "authordvd");
        Add(CommandOptionSpec.Of(["overwrite", .. AudioCompilationFlags], ["output", .. AudioCompilationOptions]), "jewel-cover", "jewelcover");
        Add(CommandOptionSpec.Of(AudioCompilationFlags, ["media", .. AudioCompilationOptions]), "audio-plan", "audioplan");
        Add(CommandOptionSpec.Of(
            ["strict-verify", "verify", "no-verify", "close", "no-close", "no-burnfree", "overwrite", "continue-on-error", "confirm-shutdown"],
            ["drive", "images", "speed", "wait-minutes", "post-action"]),
            "burn-queue", "burnqueue");
        Add(CommandOptionSpec.Of([], ["drive", "set"]), "book-type", "booktype");
        Add(CommandOptionSpec.Of(["delete-backups"], ["path", "par2"]), "repair-par2", "repairpar2");
        Add(RosettaLadderSpec, "rosetta-ladder", "rosettaladder");

        return specs;
    }

    private static void ValidateTopLevelOptions(CommandLineArgs cmd)
    {
        foreach (string flag in cmd.Flags)
        {
            if (!TopLevelFlags.Contains(flag))
            {
                throw new CommandLineException($"Unknown option '--{flag}'. Use --help for usage information.");
            }
        }

        foreach (string option in cmd.Options.Keys)
        {
            if (TopLevelFlags.Contains(option))
            {
                throw new CommandLineException($"Option '--{option}' does not take a value.");
            }
            throw new CommandLineException($"Unknown option '--{option}'. Use --help for usage information.");
        }
    }

    private static void ValidateCommandOptions(CommandLineArgs cmd, string subcommand)
    {
        CommandOptionSpec? spec = ResolveCommandSpec(cmd, subcommand);
        if (spec == null)
        {
            // Unknown command/verb: let the command dispatcher report its standard unknown command/usage error.
            return;
        }

        foreach (string flag in cmd.Flags)
        {
            if (!GlobalFlags.Contains(flag) && !spec.Flags.Contains(flag))
            {
                if (spec.Options.Contains(flag))
                {
                    throw new CommandLineException($"Missing value for option '--{flag}'.");
                }
                throw new CommandLineException($"Unknown option '--{flag}' for '{FormatCommandLabel(cmd, subcommand)}'. Use --help for usage information.");
            }
        }

        foreach (string option in cmd.Options.Keys)
        {
            if (GlobalFlags.Contains(option) || spec.Flags.Contains(option))
            {
                throw new CommandLineException($"Option '--{option}' does not take a value.");
            }

            if (!spec.Options.Contains(option))
            {
                throw new CommandLineException($"Unknown option '--{option}' for '{FormatCommandLabel(cmd, subcommand)}'. Use --help for usage information.");
            }
        }
    }

    private static CommandOptionSpec? ResolveCommandSpec(CommandLineArgs cmd, string subcommand)
    {
        if (subcommand == "rosetta")
        {
            string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
            return verb switch
            {
                "ladder" => RosettaLadderSpec,
                "build" => RosettaBuildSpec,
                "burn" => RosettaBurnSpec,
                _ => CommandOptionSpec.Merge(RosettaLadderSpec, RosettaBuildSpec, RosettaBurnSpec)
            };
        }

        if (subcommand is "disc-art" or "discart")
        {
            string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
            if (verb == "calibrate")
            {
                string subVerb = cmd.Positionals.Count > 1 ? cmd.Positionals[1].ToLowerInvariant() : string.Empty;
                return subVerb switch
                {
                    "target" => DiscArtCalibrateTargetSpec,
                    "fit" => DiscArtCalibrateFitSpec,
                    "list" => DiscArtCalibrateListSpec,
                    _ => CommandOptionSpec.Merge(DiscArtCalibrateTargetSpec, DiscArtCalibrateFitSpec, DiscArtCalibrateListSpec)
                };
            }

            return verb switch
            {
                "generate" => DiscArtGenerateSpec,
                "preview" => DiscArtPreviewSpec,
                "burn" => DiscArtBurnSpec,
                "measure" => DiscArtMeasureSpec,
                _ => CommandOptionSpec.Merge(
                    DiscArtGenerateSpec, DiscArtPreviewSpec, DiscArtBurnSpec,
                    DiscArtCalibrateTargetSpec, DiscArtCalibrateFitSpec, DiscArtCalibrateListSpec, DiscArtMeasureSpec)
            };
        }

        return CommandSpecs.GetValueOrDefault(subcommand);
    }

    private static string FormatCommandLabel(CommandLineArgs cmd, string subcommand)
    {
        if (subcommand == "rosetta" && cmd.Positionals.Count > 0)
        {
            return $"rosetta {cmd.Positionals[0].ToLowerInvariant()}";
        }
        if (subcommand is "disc-art" or "discart" && cmd.Positionals.Count > 0)
        {
            string verb = cmd.Positionals[0].ToLowerInvariant();
            if (verb == "calibrate" && cmd.Positionals.Count > 1)
            {
                return $"disc-art calibrate {cmd.Positionals[1].ToLowerInvariant()}";
            }
            return $"disc-art {verb}";
        }
        return subcommand;
    }
}
