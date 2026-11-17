using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Iso;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Cli;

/// <summary>
/// <c>disc-art generate | preview | burn</c>: a picture drawn in the data sectors of a DVD or BD (with or without files on
/// the same disc) or a CD picture track, a read-back preview of the result, and the burn of the finished image. The work
/// is done by <see cref="DiscArtImageBuilder"/>; the burn only forwards to <c>burn-iso</c> or <c>burn-audio</c>.
/// </summary>
public static partial class CliCommandRunner
{
    private const string DiscArtGenerateUsage =
        "Usage: ignisdisc-cli disc-art generate --media cd|dvd|bd --picture <file.png> --output <file> "
        + "[--files <folder> [--files-outer]] [--append-to <base.cue>] [--label <text>] [--tone photo|binary] "
        + "[--start-angle <deg>] [--scale <x>] [--offset-x-mm <mm>] [--offset-y-mm <mm>] [--rotate <deg>] [--fit ring|disc] [--read-compat] [--no-interleave] [--plan-only] [--overwrite] [--json]";

    private const string DiscArtPreviewUsage =
        "Usage: ignisdisc-cli disc-art preview --image <file.iso|.wav|.raw> --output <file.png> [--size <px>] [--json]";

    private const string DiscArtBurnUsage =
        "Usage: ignisdisc-cli disc-art burn --drive <id> --image <file.iso|.cue> [any burn-iso or burn-audio option] [--json]";

    private static int HandleDiscArtGenerate(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        CalibrationDiscKind kind = ParseCalibrationMedia(cmd.GetRequiredOption("media", "--media cd|dvd|bd"));
        string picture = cmd.GetRequiredOption("picture", "--picture <file.png>");
        string rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException(kind == CalibrationDiscKind.Cd
                ? "Missing required option: --output <file.wav|file.raw>"
                : "Missing required option: --output <file.iso>");
        }

        (CalibrationParameters parameters, string source) = ResolveCalibrationParameters(cmd, kind);
        ToneProfile tone = (cmd.GetOption("tone") ?? "photo").ToLowerInvariant() switch
        {
            "photo" => ToneProfile.Photo,
            "binary" => ToneProfile.LegacyBinary,
            string other => throw new CommandLineException($"Unknown --tone '{other}'. Use photo or binary."),
        };

        string? files = cmd.GetOption("files");
        if (files is not null && string.IsNullOrWhiteSpace(files))
        {
            throw new CommandLineException("--files needs a folder: --files <folder>");
        }

        bool filesOuter = cmd.HasFlag("files-outer");
        if (filesOuter && files is null)
        {
            throw new CommandLineException("--files-outer places the files of --files <folder> at the outer end of the disc; give --files too.");
        }

        string? baseCue = cmd.GetOption("append-to");
        if (baseCue is not null && string.IsNullOrWhiteSpace(baseCue))
        {
            throw new CommandLineException("--append-to needs a CUE sheet: --append-to <base.cue>");
        }

        DiscArtTransform transform = new(
            OptionalDouble(cmd, "scale") ?? 1,
            OptionalDouble(cmd, "offset-x-mm") ?? 0,
            OptionalDouble(cmd, "offset-y-mm") ?? 0,
            OptionalDouble(cmd, "rotate") ?? 0,
            (cmd.GetOption("fit") ?? "disc").ToLowerInvariant() switch
            {
                "disc" => DiscArtFit.Disc,
                "ring" => DiscArtFit.Ring,
                string other => throw new CommandLineException($"Unknown --fit '{other}'. Use ring or disc."),
            });

        var request = new DiscArtRequest(
            kind switch
            {
                CalibrationDiscKind.Cd => DiscArtMedium.Cd,
                CalibrationDiscKind.Dvd => DiscArtMedium.Dvd,
                _ => DiscArtMedium.BluRay,
            },
            Path.GetFullPath(picture),
            parameters,
            Path.GetFullPath(rawOutput),
            tone,
            OptionalDouble(cmd, "start-angle") ?? 0,
            files is null ? null : Path.GetFullPath(files),
            filesOuter ? PictureDiscFilesPlacement.OuterFiles : PictureDiscFilesPlacement.InnerFiles,
            cmd.GetOption("label") is { Length: > 0 } label ? label : "DISC_ART",
            baseCue is null ? null : Path.GetFullPath(baseCue),
            CdInterleave: !cmd.HasFlag("no-interleave"),
            DvdReadCompatibility: cmd.HasFlag("read-compat"),
            Overwrite: cmd.HasFlag("overwrite"),
            Transform: transform);

        string media = MediaName(kind);
        try
        {
            if (cmd.HasFlag("plan-only"))
            {
                DiscArtPlan plan = DiscArtImageBuilder.Plan(request);
                if (cmd.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        planOnly = true,
                        media,
                        parametersFrom = source,
                        outputPath = request.OutputPath,
                        plan = PlanDto(plan),
                    });
                }
                else
                {
                    stdout.WriteLine($"Plan for a {media} picture disc (nothing was written).");
                    stdout.WriteLine($"  Parameters  : {source}");
                    WriteParameterLines(stdout, parameters);
                    WriteDiscArtPlanLines(stdout, plan);
                    stdout.WriteLine($"  Output      : {request.OutputPath}");
                }

                return 0;
            }

            var progress = new ConsoleProgress(cmd.IsJson ? null : stderr);
            DiscArtResult result = DiscArtImageBuilder.Generate(request, progress);
            string? burnImage = result.CueSheetPath
                ?? (result.OutputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? result.OutputPath : null);
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    media,
                    parametersFrom = source,
                    plan = PlanDto(result.Plan),
                    outputPath = result.OutputPath,
                    cueSheetPath = result.CueSheetPath,
                    sidecarPath = result.SidecarPath,
                    bytesWritten = result.BytesWritten,
                    elapsedSeconds = result.Elapsed.TotalSeconds,
                    burnImage,
                });
            }
            else
            {
                stdout.WriteLine($"Picture disc image written ({media}).");
                stdout.WriteLine($"  Parameters  : {source}");
                WriteParameterLines(stdout, parameters);
                WriteDiscArtPlanLines(stdout, result.Plan);
                stdout.WriteLine($"  Image       : {result.OutputPath} ({result.BytesWritten:N0} bytes)");
                if (result.CueSheetPath is not null) stdout.WriteLine($"  CUE sheet   : {result.CueSheetPath}");
                stdout.WriteLine($"  Description : {result.SidecarPath}");
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Elapsed     : {result.Elapsed.TotalSeconds:0.0} s"));
                if (burnImage is not null)
                {
                    stdout.WriteLine($"Burn with: ignisdisc-cli disc-art burn --drive <id> --image \"{burnImage}\"");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CommandLineException(ex.Message);
        }
    }

    private static int HandleDiscArtPreview(CommandLineArgs cmd, TextWriter stdout)
    {
        string image = Path.GetFullPath(cmd.GetRequiredOption("image", "--image <file.iso|.wav|.raw>"));
        string rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException("Missing required option: --output <file.png>");
        }

        int size = 1024;
        if (cmd.GetOption("size") is { } rawSize
            && (!int.TryParse(rawSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 64 or > 8192))
        {
            throw new CommandLineException($"Invalid --size value '{rawSize}'. Expected a number of pixels from 64 to 8192.");
        }

        string output;
        try
        {
            output = DiscArtImageBuilder.Preview(image, Path.GetFullPath(rawOutput), size);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
            or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CommandLineException(ex.Message);
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new { success = true, image, outputPath = output, size });
        }
        else
        {
            stdout.WriteLine($"Preview written: {output} ({size} x {size} pixels).");
        }

        return 0;
    }

    /// <summary>
    /// An outer-files image keeps its file system in a second session near the end of the disc. Burned in one pass, the
    /// drive reports one session starting at LBA 0, so a computer looks for the files at the start of the disc, finds
    /// the picture, and shows no files. Until a two-session burn exists (picture session, then a files session built
    /// for the drive's next writable address), such an image is refused here.
    /// </summary>
    private static void RefuseOuterFilesBurn(string image)
    {
        string sidecar = image + ".disc-art.json";
        if (!File.Exists(sidecar))
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));
            if (doc.RootElement.TryGetProperty("filesPlacement", out var placement)
                && string.Equals(placement.GetString(), "OuterFiles", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(
                    "This image has the picture inside and the files at the outer edge. Burning it needs two sessions "
                    + "(picture first, then the files), which IgnisDisc cannot write yet; in one pass the files would not "
                    + "show up on a computer. Generate it again without --files-outer (files inside, picture outside).");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable description changes nothing about how the image is burned.
        }
    }

    private static Task<int> HandleDiscArtBurnAsync(
        CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        string image = Path.GetFullPath(cmd.GetRequiredOption("image", "--image <file.iso|.cue>"));
        cmd.GetRequiredOption("drive", "--drive <id>");
        string extension = Path.GetExtension(image).ToLowerInvariant();
        var forwarded = new CommandLineArgs();
        foreach (var option in cmd.Options) forwarded.Options[option.Key] = option.Value;
        foreach (string flag in cmd.Flags) forwarded.Flags.Add(flag);
        forwarded.Positionals.AddRange(cmd.Positionals);
        forwarded.Options.Remove("image");

        switch (extension)
        {
            case ".iso":
                RefuseOuterFilesBurn(image);
                forwarded.Subcommand = "burn-iso";
                forwarded.Options["iso"] = image;
                return HandleBurnIsoAsync(forwarded, stdout, stderr, deviceService, burnEngine);
            case ".cue":
                // The picture is the last track of a Session-at-Once disc; only the direct writer can place it, so it is
                // the default unless the person chose an engine.
                forwarded.Subcommand = "burn-audio";
                forwarded.Options["tracks"] = image;
                forwarded.Flags.Add("dao");
                if (!cmd.HasFlag("native") && !cmd.HasFlag("windows-writer"))
                {
                    forwarded.Flags.Add("native");
                }

                return HandleBurnAudioAsync(forwarded, stdout, stderr, deviceService, burnEngine);
            case ".wav":
            case ".raw":
                throw new CommandLineException(
                    $"A CD picture track is burned through its CUE sheet. Burn the .cue next to it: {Path.ChangeExtension(image, ".cue")}");
            default:
                throw new CommandLineException($"Cannot burn '{Path.GetFileName(image)}'. Give the .iso (DVD or BD) or the .cue (CD) that 'disc-art generate' made.");
        }
    }

    private static object PlanDto(DiscArtPlan plan) => new
    {
        totalSectors = plan.TotalSectors,
        pictureStartLba = plan.PictureStartLba,
        pictureEndLba = plan.PictureEndLba,
        pictureInnerRadiusMm = plan.PictureInnerRadiusMm,
        pictureOuterRadiusMm = plan.PictureOuterRadiusMm,
        baseCueSectors = plan.BaseCueSectors,
        files = plan.FilesLayout is not { } layout ? null : new
        {
            placement = layout.Placement == PictureDiscFilesPlacement.InnerFiles ? "inner" : "outer",
            filesSessionStartLba = layout.FilesSessionStartLba,
            volumeEndLba = layout.VolumeEndLba,
            gapStartLba = layout.GapStartLba,
            gapEndLba = layout.GapEndLba,
        },
    };

    private static void WriteDiscArtPlanLines(TextWriter stdout, DiscArtPlan plan)
    {
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Picture     : sectors {plan.PictureStartLba:N0} to {plan.PictureEndLba - 1:N0} ({plan.PictureEndLba - plan.PictureStartLba:N0} sectors), radius {plan.PictureInnerRadiusMm:0.###} to {plan.PictureOuterRadiusMm:0.###} mm"));
        if (plan.BaseCueSectors is { } baseSectors)
        {
            stdout.WriteLine($"  Base CUE    : the existing tracks take {baseSectors:N0} sectors; the picture is the last track");
        }

        if (plan.FilesLayout is { } layout)
        {
            stdout.WriteLine(layout.Placement == PictureDiscFilesPlacement.InnerFiles
                ? $"  Files       : sectors 0 to {layout.VolumeEndLba - 1:N0} (inner), then {layout.GapSectorCount:N0} empty sectors"
                : $"  Files       : sectors {layout.FilesSessionStartLba:N0} to {layout.VolumeEndLba - 1:N0} (outer session), after {layout.GapSectorCount:N0} empty sectors");
            if (layout.Placement == PictureDiscFilesPlacement.OuterFiles)
            {
                stdout.WriteLine("  Note        : files at the outer edge need a two-session burn, which is not available yet;");
                stdout.WriteLine("                'disc-art burn' refuses this image. Leave out --files-outer to burn it now.");
            }
        }

        stdout.WriteLine($"  Image size  : {plan.TotalSectors:N0} sectors");
    }

    /// <summary>Prints a progress line to a stream about every tenth of the work; null prints nothing.</summary>
    private sealed class ConsoleProgress(TextWriter? writer) : IProgress<DiscJobProgress>
    {
        private int _lastTenth = -1;

        public void Report(DiscJobProgress value)
        {
            if (writer is null || value.Fraction is not { } fraction) return;
            int tenth = (int)(fraction * 10);
            if (tenth == _lastTenth) return;
            _lastTenth = tenth;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {value.Message} {fraction * 100:0}%"));
        }
    }
}
