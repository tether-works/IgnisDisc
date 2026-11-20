using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private const string RosettaUsage =
        "Usage: ignisdisc-cli rosetta ladder|build|burn [options] (see 'ignisdisc-cli rosetta --help')";

    private static async Task<int> HandleRosettaCommandAsync(
        CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        // 'rosetta-ladder' is the older single-purpose name and takes no verb.
        if (!string.Equals(cmd.Subcommand, "rosetta", StringComparison.OrdinalIgnoreCase))
        {
            return HandleRosettaLadder(cmd, stdout);
        }

        string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
        return verb switch
        {
            "ladder" => HandleRosettaLadder(cmd, stdout),
            "build" => await HandleRosettaBuildAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "burn" => await HandleRosettaBurnAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            _ => throw new CommandLineException(RosettaUsage),
        };
    }

    private static int HandleRosettaLadder(CommandLineArgs cmd, TextWriter stdout)
    {
        string media = cmd.GetOption("media") ?? "dvd";
        bool isDvd = string.Equals(media, "dvd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(media, "dvd-r", StringComparison.OrdinalIgnoreCase);
        bool isCd = string.Equals(media, "cd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(media, "cd-r", StringComparison.OrdinalIgnoreCase);
        if (!isDvd && !isCd)
        {
            throw new CommandLineException(
                $"Unsupported --media '{media}'. 'rosetta ladder' supports DVD-R ('--media dvd') and CD-R ('--media cd').");
        }

        bool noInterleave = cmd.HasFlag("no-interleave");
        if (noInterleave && isDvd)
        {
            throw new CommandLineException("--no-interleave applies only to CD-R ('--media cd').");
        }

        string? rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out");
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException(isCd
                ? "Missing required option: --output <file.wav|file.raw>"
                : "Missing required option: --output <file.iso>");
        }

        string outputFullPath = Path.GetFullPath(rawOutput);
        if (isCd)
        {
            string ext = Path.GetExtension(outputFullPath);
            if (!ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".raw", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(
                    "CD-R ladder output must be .wav (PCM audio + CUE) or .raw (big-endian audio).");
            }
        }

        string? rawJsonOutput = cmd.GetOption("json-output");
        string jsonFullPath = string.IsNullOrWhiteSpace(rawJsonOutput)
            ? outputFullPath + ".json"
            : Path.GetFullPath(rawJsonOutput);

        string presetId = cmd.GetOption("disc-preset") ?? (isCd ? "cd-80" : "dvd-5-120mm");
        string? presetsFile = cmd.GetOption("disc-presets-file");
        DiscPresetJsonDocument presetDoc = LoadLadderPresetDocument(presetsFile);

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

        RosettaLadderOptions ladderOptions;
        if (isCd)
        {
            CdDiscPresetDefinition cdPreset = ResolveCdPreset(presetDoc, presetId);
            ladderOptions = RosettaLadderOptions.FromCdPreset(cdPreset, startAngleDegrees, interleave: !noInterleave);
        }
        else
        {
            DvdDiscPresetDefinition dvdPreset = ResolveDvdPreset(presetDoc, presetId);
            ladderOptions = RosettaLadderOptions.FromDvdPreset(dvdPreset, startAngleDegrees);
        }

        RosettaLadderLayout layout;
        try
        {
            layout = RosettaLadderDisc.CreateLayout(ladderOptions);
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException(ex.Message);
        }

        string mediaLabel = layout.Manifest.Media;
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
                    media = mediaLabel,
                    discPresetId = presetId,
                    outputPath = outputFullPath,
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
                stdout.WriteLine($"  Media        : {mediaLabel}");
                stdout.WriteLine($"  Disc Preset  : {presetId} ({layout.Manifest.FillSectors:N0} sectors)");
                stdout.WriteLine($"  Zones / Rungs: {layout.Manifest.Zones.Count} / {layout.Manifest.Rungs.Count}");
                stdout.WriteLine($"  Target Output: {outputFullPath} (not written)");
            }

            return 0;
        }

        if (isCd)
        {
            CdGenerationSummary cdSummary = RosettaLadderDisc.WriteCdFile(layout, outputFullPath, jsonFullPath);
            long fileBytes = new FileInfo(outputFullPath).Length;

            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    planOnly = false,
                    media = mediaLabel,
                    discPresetId = presetId,
                    outputPath = outputFullPath,
                    cueSheetPath = cdSummary.CueSheetPath,
                    jsonPath = jsonFullPath,
                    bytesWritten = fileBytes,
                    sectorsWritten = cdSummary.Sectors,
                    elapsedSeconds = cdSummary.Elapsed.TotalSeconds,
                    zoneCount = layout.Manifest.Zones.Count,
                    rungCount = layout.Manifest.Rungs.Count,
                });
            }
            else
            {
                stdout.WriteLine("Rosetta resolution-ladder CD-R track generated.");
                stdout.WriteLine($"  Output Track : {outputFullPath} ({fileBytes:N0} bytes)");
                if (cdSummary.CueSheetPath is not null)
                {
                    stdout.WriteLine($"  CUE Sheet    : {cdSummary.CueSheetPath}");
                }
                stdout.WriteLine($"  Manifest JSON: {jsonFullPath}");
                stdout.WriteLine($"  Disc Preset  : {presetId}");
                stdout.WriteLine($"  Sectors      : {cdSummary.Sectors:N0}");
                stdout.WriteLine($"  Zones        : {layout.Manifest.Zones.Count}");
                stdout.WriteLine($"  Rungs        : {layout.Manifest.Rungs.Count}");
                if (cdSummary.CueSheetPath is not null)
                {
                    stdout.WriteLine($"  Burn with    : ignisdisc-cli burn-audio --drive <id> --tracks \"{cdSummary.CueSheetPath}\" --dao --native");
                }
            }

            return 0;
        }

        FastDvdStreamSummary summary = RosettaLadderDisc.WriteIsoFile(layout, outputFullPath, jsonFullPath);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                planOnly = false,
                media = mediaLabel,
                discPresetId = presetId,
                outputPath = outputFullPath,
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
            stdout.WriteLine($"  Output ISO   : {outputFullPath} ({summary.BytesWritten:N0} bytes)");
            stdout.WriteLine($"  Manifest JSON: {jsonFullPath}");
            stdout.WriteLine($"  Disc Preset  : {presetId}");
            stdout.WriteLine($"  Sectors      : {summary.SectorsWritten:N0}");
            stdout.WriteLine($"  Zones        : {layout.Manifest.Zones.Count}");
            stdout.WriteLine($"  Rungs        : {layout.Manifest.Rungs.Count}");
            stdout.WriteLine($"  Burn with    : ignisdisc-cli burn-iso --drive <id> --iso \"{outputFullPath}\" --native");
        }

        return 0;
    }

    private static DiscPresetJsonDocument LoadLadderPresetDocument(string? presetsFile)
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
        return doc;
    }

    private static DvdDiscPresetDefinition ResolveDvdPreset(DiscPresetJsonDocument doc, string presetId)
    {
        DvdDiscPresetDefinition? match = doc.DvdPresets.FirstOrDefault(
            p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        string available = string.Join(", ", doc.DvdPresets.Select(p => p.Id));
        throw new CommandLineException($"Unknown DVD disc preset '{presetId}'. Available DVD presets: {available}");
    }

    private static CdDiscPresetDefinition ResolveCdPreset(DiscPresetJsonDocument doc, string presetId)
    {
        CdDiscPresetDefinition? match = doc.CdPresets.FirstOrDefault(
            p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        string available = string.Join(", ", doc.CdPresets.Select(p => p.Id));
        throw new CommandLineException($"Unknown CD disc preset '{presetId}'. Available CD presets: {available}");
    }
}
