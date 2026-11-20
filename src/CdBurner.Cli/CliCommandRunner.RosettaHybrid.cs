using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.Cli;

/// <summary>
/// <c>rosetta build | burn</c>: a hybrid Rosetta DVD-R or CD-R. The archive vault (text, page index, optional files,
/// manifests, PAR2) sits inside the pages' inner radius as a normal ISO 9660 + Joliet + UDF volume, then a zero gap,
/// then the microscope-readable pages and the rim header. The work is done by <see cref="RosettaHybridDisc"/>; the burn
/// forwards to <c>burn-iso</c> (DVD-R) or <c>burn-audio --dao --native</c> (mixed-mode CD-R).
/// </summary>
public static partial class CliCommandRunner
{
    private const string RosettaBuildUsage =
        "Usage: ignisdisc-cli rosetta build --text <folder> --output <file.iso|file.cue|file.bin> [--title <text>] [--date yyyy-mm-dd] "
        + "[--files <folder>] [--cells hobbyist|high-density|<rung id>] [--pages-radius <mm>] [--redundancy <0-100>] [--no-fill] "
        + "[--no-rim] [--media dvd|cd] [--no-interleave] [--disc-preset <id>] [--drive-model <text> --media-id <text>] [--start-angle <deg>] "
        + "[--plan-only] [--overwrite] [--json]";

    private const string RosettaBurnUsage =
        "Usage: ignisdisc-cli rosetta burn --drive <id> --image <file.iso|file.cue> [any burn-iso or burn-audio option] [--json]";

    private static async Task<int> HandleRosettaBuildAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string media = (cmd.GetOption("media") ?? "dvd").ToLowerInvariant();
        bool isCd = media is "cd" or "cd-r";
        bool isDvd = media is "dvd" or "dvd-r";
        if (!isCd && !isDvd)
        {
            throw new CommandLineException($"Unsupported --media '{media}'. 'rosetta build' supports DVD-R ('--media dvd') and CD-R ('--media cd').");
        }

        bool noInterleave = cmd.HasFlag("no-interleave");
        if (noInterleave && isDvd)
        {
            throw new CommandLineException("--no-interleave applies only to CD-R ('--media cd').");
        }

        string text = cmd.GetOption("text") is { Length: > 0 } t ? Path.GetFullPath(t) : throw new CommandLineException(RosettaBuildUsage);
        string output = (cmd.GetOption("output") ?? cmd.GetOption("out")) is { Length: > 0 } o
            ? Path.GetFullPath(o)
            : throw new CommandLineException(isCd
                ? "Missing required option: --output <file.cue|file.bin>"
                : "Missing required option: --output <file.iso>");

        string title = cmd.GetOption("title") is { Length: > 0 } givenTitle
            ? givenTitle
            : new DirectoryInfo(text).Name;
        DateOnly date = DateOnly.FromDateTime(DateTime.UtcNow);
        if (cmd.GetOption("date") is { } rawDate
            && !DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            throw new CommandLineException($"Invalid --date '{rawDate}'. Expected yyyy-mm-dd.");
        }

        RosettaPagePreset preset;
        string cells = cmd.GetOption("cells") ?? "hobbyist";
        try
        {
            preset = RosettaPresetCatalog.BuiltIn.GetPreset(cells);
        }
        catch (KeyNotFoundException)
        {
            string known = string.Join(", ", RosettaPresetCatalog.BuiltIn.Presets.Select(p => p.Id)
                .Concat(RosettaPresetCatalog.BuiltIn.Ladder.Select(p => p.Id)));
            throw new CommandLineException($"Unknown --cells '{cells}'. Known: {known}.");
        }

        string redundancyOption = cmd.GetOption("redundancy") ?? "10";
        if (!int.TryParse(redundancyOption, NumberStyles.None, CultureInfo.InvariantCulture, out int redundancy) || redundancy > 100)
        {
            throw new CommandLineException($"Invalid --redundancy '{redundancyOption}'. Expected a percentage from 0 to 100.");
        }

        string? files = cmd.GetOption("files");
        if (files is not null && string.IsNullOrWhiteSpace(files))
        {
            throw new CommandLineException("--files needs a folder: --files <folder>");
        }

        CalibrationDiscKind discKind = isCd ? CalibrationDiscKind.Cd : CalibrationDiscKind.Dvd;
        (CalibrationParameters disc, string source) = ResolveCalibrationParameters(cmd, discKind);
        string? par2 = redundancy > 0 && !cmd.HasFlag("plan-only") ? Par2Runner.FindPar2Executable() : null;
        string mediaLabel = isCd ? "CD-R" : "DVD-R";
        var request = new RosettaHybridRequest(
            text, disc, output, title, date, preset,
            FilesFolder: files is null ? null : Path.GetFullPath(files),
            PagesInnerRadiusMm: OptionalDouble(cmd, "pages-radius") ?? RosettaHybridDisc.DefaultPagesInnerRadiusMm,
            RedundancyPercent: redundancy,
            FillWithRecoveryData: !cmd.HasFlag("no-fill"),
            IncludeRimHeader: !cmd.HasFlag("no-rim"),
            StartAngleDegrees: OptionalDouble(cmd, "start-angle") ?? 0,
            Par2Path: par2,
            Overwrite: cmd.HasFlag("overwrite"),
            CdInterleave: !noInterleave);

        try
        {
            if (cmd.HasFlag("plan-only"))
            {
                RosettaHybridPlan plan = RosettaHybridDisc.Plan(request);
                if (cmd.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        planOnly = true,
                        media = mediaLabel,
                        parametersFrom = source,
                        outputPath = output,
                        cdInterleave = isCd ? plan.CdInterleave : (bool?)null,
                        plan = RosettaPlanDto(plan),
                    });
                }
                else
                {
                    stdout.WriteLine($"Plan for a hybrid Rosetta {mediaLabel} (nothing was written).");
                    stdout.WriteLine($"  Disc        : {source}, {plan.DiscSectors:N0} sectors");
                    WriteRosettaPlanLines(stdout, plan);
                    stdout.WriteLine($"  Output      : {output}");
                }

                return 0;
            }

            if (redundancy > 0 && par2 is null)
            {
                throw new CommandLineException(
                    "PAR2 recovery data needs par2 (install it with 'ignisdisc-cli install-par2'), or pass --redundancy 0 for manifests only.");
            }

            var progress = new RosettaConsoleProgress(cmd.IsJson ? null : stderr);
            RosettaHybridResult result = await RosettaHybridDisc.GenerateAsync(request, progress).ConfigureAwait(false);
            string burnImage = result.CueSheetPath ?? result.OutputPath;
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    media = mediaLabel,
                    parametersFrom = source,
                    plan = RosettaPlanDto(result.Plan),
                    outputPath = result.OutputPath,
                    cueSheetPath = result.CueSheetPath,
                    binaryPath = result.BinaryPath,
                    sidecarPath = result.SidecarPath,
                    cdInterleave = isCd ? result.Plan.CdInterleave : (bool?)null,
                    volumeLabel = result.VolumeLabel,
                    setId = result.SetId,
                    vaultEndLba = result.Layout.VolumeEndLba,
                    gapSectors = result.Layout.GapSectorCount,
                    pagesStartLba = result.Layout.PictureStartLba,
                    pagesEndLba = result.Layout.PictureEndLba,
                    recoverySectors = result.RecoverySectors,
                    recoveryBlocks = result.RecoveryBlocks,
                    bytesWritten = result.BytesWritten,
                    elapsedSeconds = result.Elapsed.TotalSeconds,
                });
            }
            else
            {
                stdout.WriteLine($"Hybrid Rosetta {mediaLabel} image written.");
                stdout.WriteLine($"  Disc        : {source}, {result.Plan.DiscSectors:N0} sectors");
                WriteRosettaPlanLines(stdout, result.Plan);
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Vault used  : sectors 0 to {result.Layout.VolumeEndLba - 1:N0} ({result.Layout.VolumeEndLba * 2048.0 / 1048576:N1} MB, recovery data {result.RecoverySectors * 2048.0 / 1048576:N1} MB), label {result.VolumeLabel}"));
                stdout.WriteLine($"  Zero gap    : sectors {result.Layout.GapStartLba:N0} to {result.Layout.GapEndLba - 1:N0} ({result.Layout.GapSectorCount:N0} sectors)");
                if (result.CueSheetPath is not null)
                {
                    stdout.WriteLine($"  CUE sheet   : {result.CueSheetPath}");
                    stdout.WriteLine($"  Binary      : {result.BinaryPath} ({result.BytesWritten:N0} bytes)");
                }
                else
                {
                    stdout.WriteLine($"  Image       : {result.OutputPath} ({result.BytesWritten:N0} bytes)");
                }
                stdout.WriteLine($"  Description : {result.SidecarPath}");
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Elapsed     : {result.Elapsed.TotalSeconds:0.0} s"));
                stdout.WriteLine($"Burn with: ignisdisc-cli rosetta burn --drive <id> --image \"{burnImage}\"");
                stdout.WriteLine($"Check later with: ignisdisc-cli recover --drive <id> --out <folder>");
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CommandLineException(ex.Message);
        }
    }

    private static Task<int> HandleRosettaBurnAsync(
        CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        string image = Path.GetFullPath(cmd.GetRequiredOption("image", "--image <file.iso|file.cue>"));
        cmd.GetRequiredOption("drive", "--drive <id>");
        string ext = Path.GetExtension(image).ToLowerInvariant();
        if (ext == ".bin")
        {
            throw new CommandLineException(
                $"A hybrid Rosetta CD-R is burned through its CUE sheet. Burn the .cue next to it: {Path.ChangeExtension(image, ".cue")}");
        }

        if (ext is not (".iso" or ".cue"))
        {
            throw new CommandLineException($"Cannot burn '{Path.GetFileName(image)}'. Give the .iso (DVD-R) or .cue (CD-R) that 'rosetta build' made.");
        }

        if (!File.Exists(image + RosettaHybridDisc.SidecarSuffix))
        {
            throw new CommandLineException(
                $"'{Path.GetFileName(image)}' has no {RosettaHybridDisc.SidecarSuffix} description next to it, so it was not made by "
                + "'rosetta build'. Burn other images with burn-iso or burn-audio.");
        }

        var forwarded = new CommandLineArgs();
        foreach (var option in cmd.Options) forwarded.Options[option.Key] = option.Value;
        foreach (string flag in cmd.Flags) forwarded.Flags.Add(flag);
        forwarded.Positionals.AddRange(cmd.Positionals.Skip(1));
        forwarded.Options.Remove("image");

        if (ext == ".cue")
        {
            forwarded.Subcommand = "burn-audio";
            forwarded.Options["tracks"] = image;
            forwarded.Flags.Add("dao");
            if (!cmd.HasFlag("native") && !cmd.HasFlag("windows-writer"))
            {
                forwarded.Flags.Add("native");
            }
            return HandleBurnAudioAsync(forwarded, stdout, stderr, deviceService, burnEngine);
        }

        forwarded.Subcommand = "burn-iso";
        forwarded.Options["iso"] = image;
        return HandleBurnIsoAsync(forwarded, stdout, stderr, deviceService, burnEngine);
    }

    private static object RosettaPlanDto(RosettaHybridPlan plan) => new
    {
        discSectors = plan.DiscSectors,
        vaultCeilingSectors = plan.VaultCeilingSectors,
        vaultCeilingBytes = plan.VaultCeilingBytes,
        vaultInputBytes = plan.VaultInputBytes,
        gapMinimumSectors = CdBurner.Core.Iso.PictureDiscLayoutPlanner.MinimumGapSectors,
        pagesStartLba = plan.PagesStartLba,
        pagesEndLba = plan.PagesEndLba,
        pagesStartRadiusMm = plan.PagesStartRadiusMm,
        pagesInnerRadiusMm = plan.PagesInnerRadiusMm,
        pagesOuterRadiusMm = plan.PagesOuterRadiusMm,
        rimInnerRadiusMm = plan.RimInnerRadiusMm,
        cells = new { presetId = plan.PresetId, widthUm = plan.CellWidthUm, heightUm = plan.CellHeightUm },
        columns = plan.Columns,
        bodyRows = plan.BodyRows,
        charactersPerPage = plan.CharactersPerPage,
        pageCapacity = plan.PageCapacity,
        pagesUsed = plan.PagesUsed,
        pagesFree = plan.PagesFree,
        documents = plan.Documents.Select(d => new { title = d.Title, firstPage = d.FirstPageNumber, lastPage = d.LastPageNumber }),
        textSha256 = plan.TextSha256,
    };

    private static void WriteRosettaPlanLines(TextWriter stdout, RosettaHybridPlan plan)
    {
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Vault       : sectors 0 to {plan.VaultCeilingSectors - 1:N0} at most, inside r = {plan.PagesInnerRadiusMm:0.###} mm: room for {plan.VaultCeilingBytes / 1048576.0:N1} MB (files, manifests, recovery data, file system)"));
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"                text, page index and files to store: {plan.VaultInputBytes / 1048576.0:N2} MB"));
        stdout.WriteLine($"  Gap         : at least {CdBurner.Core.Iso.PictureDiscLayoutPlanner.MinimumGapSectors:N0} zero sectors (2 MiB) before the pages");
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Pages       : sectors {plan.PagesStartLba:N0} to {plan.PagesEndLba - 1:N0}, text from r = {plan.PagesInnerRadiusMm:0.###} to {plan.PagesOuterRadiusMm:0.###} mm"));
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Cells       : {plan.PresetName} {plan.CellWidthUm:0.###} x {plan.CellHeightUm:0.###} um, {plan.Columns} x {plan.BodyRows} characters per page ({plan.CharactersPerPage:N0})"));
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Page space  : {plan.PagesUsed:N0} of {plan.PageCapacity:N0} pages used ({plan.DocumentCount} documents), {plan.PagesFree:N0} free (about {plan.PagesFree * plan.CharactersPerPage:N0} characters)"));
        stdout.WriteLine(plan.RimInnerRadiusMm is { } rim
            ? string.Create(CultureInfo.InvariantCulture, $"  Rim         : title, date and SHA-256 prefix {plan.TextSha256[..12]} from r = {rim:0.###} mm")
            : "  Rim         : none (--no-rim)");
        stdout.WriteLine($"  Text SHA-256: {plan.TextSha256}");
        stdout.WriteLine("  Note        : capacities are from this layout; whether these cells can be read is measured by a resolution-ladder burn.");
    }

    /// <summary>Progress lines for the pages about every tenth, and every vault staging message; null prints nothing.</summary>
    private sealed class RosettaConsoleProgress(TextWriter? writer) : IProgress<DiscJobProgress>
    {
        private int _lastTenth = -1;

        public void Report(DiscJobProgress value)
        {
            if (writer is null) return;
            if (value.Fraction is not { } fraction)
            {
                writer.WriteLine($"  {value.Message}");
                return;
            }

            int tenth = (int)(fraction * 10);
            if (tenth == _lastTenth && value.Stage == "pages") return;
            _lastTenth = tenth;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {value.Message} {fraction * 100:0}%"));
        }
    }
}
