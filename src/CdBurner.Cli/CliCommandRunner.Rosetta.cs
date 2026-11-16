using System.Globalization;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private static int HandleRosettaCommand(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        if (string.Equals(cmd.Subcommand, "rosetta", StringComparison.OrdinalIgnoreCase))
        {
            if (cmd.Positionals.Count < 1
                || !string.Equals(cmd.Positionals[0], "ladder", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(
                    "Usage: ignisdisc-cli rosetta ladder --media dvd --output <file.iso> [--json-output <file.json>] [--disc-preset <id>] [--disc-presets-file <path>] [--start-angle <deg>] [--plan-only] [--json]");
            }
        }

        string media = cmd.GetOption("media") ?? "dvd";
        if (!string.Equals(media, "dvd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(media, "dvd-r", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException(
                $"Unsupported --media '{media}'. 'rosetta ladder' currently supports DVD-R ('--media dvd').");
        }

        string? rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out");
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException("Missing required option: --output <file.iso>");
        }

        string isoFullPath = Path.GetFullPath(rawOutput);
        string? rawJsonOutput = cmd.GetOption("json-output");
        string jsonFullPath = string.IsNullOrWhiteSpace(rawJsonOutput)
            ? isoFullPath + ".json"
            : Path.GetFullPath(rawJsonOutput);

        string presetId = cmd.GetOption("disc-preset") ?? "dvd-5-120mm";
        string? presetsFile = cmd.GetOption("disc-presets-file");
        DvdDiscPresetDefinition preset = ResolveDvdPreset(presetId, presetsFile);

        double startAngleDegrees = 0.0;
        if (cmd.GetOption("start-angle") is { } rawAngle)
        {
            if (string.IsNullOrWhiteSpace(rawAngle)
                || !double.TryParse(rawAngle, NumberStyles.Float, CultureInfo.InvariantCulture, out startAngleDegrees)
                || !double.IsFinite(startAngleDegrees))
            {
                throw new CommandLineException($"Invalid --start-angle value '{rawAngle}'. Expected a finite number in degrees.");
            }
        }

        RosettaLadderLayout layout;
        try
        {
            layout = RosettaLadderDisc.CreateLayout(RosettaLadderOptions.FromDvdPreset(preset, startAngleDegrees));
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException(ex.Message);
        }

        if (cmd.HasFlag("plan-only"))
        {
            string? jsonDir = Path.GetDirectoryName(jsonFullPath);
            if (!string.IsNullOrEmpty(jsonDir))
            {
                Directory.CreateDirectory(jsonDir);
            }
            File.WriteAllText(jsonFullPath, RosettaLadderDisc.SerializeManifestJson(layout.Manifest));

            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    planOnly = true,
                    media = "DVD-R",
                    discPresetId = preset.Id,
                    outputPath = isoFullPath,
                    jsonPath = jsonFullPath,
                    totalSectors = layout.Manifest.FillSectors,
                    zoneCount = layout.Manifest.Zones.Count,
                    rungCount = layout.Manifest.Rungs.Count,
                    zones = layout.Manifest.Zones,
                    rungs = layout.Manifest.Rungs,
                });
            }
            else
            {
                stdout.WriteLine($"Plan only: wrote Rosetta ladder manifest to {jsonFullPath}");
                stdout.WriteLine($"  Disc Preset : {preset.Id} ({layout.Manifest.FillSectors:N0} sectors)");
                stdout.WriteLine($"  Zones / Rungs: {layout.Manifest.Zones.Count} / {layout.Manifest.Rungs.Count}");
                stdout.WriteLine($"  Target ISO  : {isoFullPath} (not written)");
            }

            return 0;
        }

        FastDvdStreamSummary summary = RosettaLadderDisc.WriteIsoFile(layout, isoFullPath, jsonFullPath);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                planOnly = false,
                media = "DVD-R",
                discPresetId = preset.Id,
                outputPath = isoFullPath,
                jsonPath = jsonFullPath,
                bytesWritten = summary.BytesWritten,
                sectorsWritten = summary.SectorsWritten,
                elapsedSeconds = summary.Elapsed.TotalSeconds,
                zoneCount = layout.Manifest.Zones.Count,
                rungCount = layout.Manifest.Rungs.Count,
            });
        }
        else
        {
            stdout.WriteLine("Rosetta resolution-ladder DVD-R image generated.");
            stdout.WriteLine($"  Output ISO   : {isoFullPath} ({summary.BytesWritten:N0} bytes)");
            stdout.WriteLine($"  Manifest JSON: {jsonFullPath}");
            stdout.WriteLine($"  Disc Preset  : {preset.Id}");
            stdout.WriteLine($"  Sectors      : {summary.SectorsWritten:N0}");
            stdout.WriteLine($"  Zones        : {layout.Manifest.Zones.Count}");
            stdout.WriteLine($"  Rungs        : {layout.Manifest.Rungs.Count}");
            stdout.WriteLine($"  Burn with    : ignisdisc-cli burn-iso --drive <id> --iso \"{isoFullPath}\" --native");
        }

        return 0;
    }

    private static DvdDiscPresetDefinition ResolveDvdPreset(string presetId, string? presetsFile)
    {
        DiscPresetJsonDocument doc = DiscPresetJsonStore.LoadBuiltIn();
        if (!string.IsNullOrWhiteSpace(presetsFile))
        {
            string fullPresetsPath = Path.GetFullPath(presetsFile);
            if (!File.Exists(fullPresetsPath))
            {
                throw new CommandLineException($"Disc presets file not found: {fullPresetsPath}");
            }
            doc = DiscPresetJsonStore.Merge(doc, DiscPresetJsonStore.Load(fullPresetsPath));
        }
        else
        {
            string defaultPath = DiscPresetJsonStore.GetDefaultPath();
            if (File.Exists(defaultPath))
            {
                doc = DiscPresetJsonStore.Merge(doc, DiscPresetJsonStore.Load(defaultPath));
            }
        }

        DvdDiscPresetDefinition? match = doc.DvdPresets.FirstOrDefault(
            p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        string available = string.Join(", ", doc.DvdPresets.Select(p => p.Id));
        throw new CommandLineException($"Unknown DVD disc preset '{presetId}'. Available DVD presets: {available}");
    }
}
