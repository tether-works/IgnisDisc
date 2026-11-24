using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Geometry;

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
        + "[--files <folder> [--files-outer]] [--append-to <base.cue> | --pregap-art <base.cue> [--pregap-seconds <s>]] [--label <text>] [--tone photo|binary] "
        + "[--start-angle <deg>] [--scale <x>] [--offset-x-mm <mm>] [--offset-y-mm <mm>] [--rotate <deg>] [--fit ring|disc] [--art-inner-radius-mm <mm>] [--art-outer-radius-mm <mm>] [--title-ring <text> [--title-ring-height-mm <mm>] [--title-ring-date yyyy-MM-dd|none]] [--read-compat] [--no-interleave] [--rim-header] [--plan-only] [--overwrite] [--json]";

    private const string DiscArtPreviewUsage =
        "Usage: ignisdisc-cli disc-art preview --image <file.iso|.wav|.raw> --output <file.png> [--size <px>] [--json]";

    private const string DiscArtBurnUsage =
        "Usage: ignisdisc-cli disc-art burn --drive <id> --image <file.iso|.cue> [any burn-iso or burn-audio option] [--json]";

    private static int HandleDiscArtGenerate(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        CalibrationDiscKind kind = ParseCalibrationMedia(cmd.GetRequiredOption("media", "--media cd|dvd|bd"));
        DiscArtTitleRingRequest? titleRing = ParseTitleRing(cmd);
        // A title ring may stand alone; otherwise a picture is required.
        string picture = titleRing is null
            ? cmd.GetRequiredOption("picture", "--picture <file.png>")
            : cmd.GetOption("picture") ?? string.Empty;
        if (titleRing is not null && cmd.GetOption("picture") is { } givenPicture && string.IsNullOrWhiteSpace(givenPicture))
        {
            throw new CommandLineException("--picture needs a file: --picture <file.png>");
        }
        string rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException(kind == CalibrationDiscKind.Cd
                ? "Missing required option: --output <file.wav|file.raw>"
                : "Missing required option: --output <file.iso>");
        }

        (CalibrationParameters parameters, string source) = ResolveCalibrationParameters(cmd, kind);
        MeasuredTurnCorrection? turnCorrection = null;
        if (cmd.GetOption("turn-map") is { } turnMapPath)
        {
            // Experimental: the disc's spiral as measured by read timing replaces the radii and pitch coefficients.
            if (kind != CalibrationDiscKind.Cd)
            {
                throw new CommandLineException("--turn-map is for CDs (--media cd) for now.");
            }

            try
            {
                turnCorrection = MeasuredTurnCorrection.Load(Path.GetFullPath(turnMapPath))
                    .WithAddressOffset(OptionalDouble(cmd, "turn-map-offset") ?? 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or System.Text.Json.JsonException)
            {
                throw new CommandLineException($"Cannot read the turn map: {ex.Message}");
            }

            if (Math.Abs((parameters.LinearDensity / 75.0) - turnCorrection.SectorLengthMm) > 1e-9)
            {
                throw new CommandLineException(string.Create(CultureInfo.InvariantCulture,
                    $"The turn map assumes {turnCorrection.SectorLengthMm} mm per sector, the disc parameters {parameters.LinearDensity / 75.0} mm."));
            }

            parameters = parameters with
            {
                InnerRadiusMm = turnCorrection.InnerRadiusMm,
                OuterRadiusMm = turnCorrection.OuterRadiusMmAt(parameters.Sectors),
                PitchLinear = 0,
                PitchQuadratic = 0,
                PitchCubic = 0,
            };
            source += string.Create(CultureInfo.InvariantCulture,
                $", angles from the measured turn map '{Path.GetFileName(turnMapPath)}' ({turnCorrection.StationCount} stations, offset {turnCorrection.AddressOffsetSectors} sectors)");
        }

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

        // Experimental, off unless --pregap-art is given: the picture fills track 1's hidden pre-gap of the base CUE.
        string? preGapCue = cmd.GetOption("pregap-art");
        if (preGapCue is not null && string.IsNullOrWhiteSpace(preGapCue))
        {
            throw new CommandLineException("--pregap-art needs the CUE sheet of the music: --pregap-art <base.cue>");
        }

        int? preGapSeconds = null;
        if (preGapCue is not null)
        {
            if (kind != CalibrationDiscKind.Cd)
            {
                throw new CommandLineException("--pregap-art is for audio CDs (--media cd).");
            }

            if (baseCue is not null)
            {
                throw new CommandLineException("Use either --append-to (picture as the last track) or --pregap-art (picture before track 1), not both.");
            }

            preGapSeconds = DiscArtImageBuilder.CdPreGapDefaultSeconds;
            if (cmd.GetOption("pregap-seconds") is { } rawSeconds)
            {
                if (!int.TryParse(rawSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                    || seconds is < DiscArtImageBuilder.CdPreGapMinimumSeconds or > DiscArtImageBuilder.CdPreGapMaximumSeconds)
                {
                    throw new CommandLineException(
                        $"Invalid --pregap-seconds value '{rawSeconds}'. Expected whole seconds from {DiscArtImageBuilder.CdPreGapMinimumSeconds} to {DiscArtImageBuilder.CdPreGapMaximumSeconds}.");
                }

                preGapSeconds = seconds;
            }

            baseCue = preGapCue;
        }
        else if (cmd.GetOption("pregap-seconds") is not null)
        {
            throw new CommandLineException("--pregap-seconds sets the length of --pregap-art; give --pregap-art <base.cue> too.");
        }

        DiscArtTransform transform = new(
            OptionalDouble(cmd, "scale") ?? 1,
            OptionalDouble(cmd, "offset-x-mm") ?? 0,
            OptionalDouble(cmd, "offset-y-mm") ?? 0,
            OptionalDouble(cmd, "rotate") ?? 0,
            // A pre-gap picture covers a few millimetres at the inner edge: by default it is sized to that ring.
            (cmd.GetOption("fit") ?? (preGapSeconds is null ? "disc" : "ring")).ToLowerInvariant() switch
            {
                "disc" => DiscArtFit.Disc,
                "ring" => DiscArtFit.Ring,
                string other => throw new CommandLineException($"Unknown --fit '{other}'. Use ring or disc."),
            });

        string? rawLabel = cmd.GetOption("label");
        string effectiveLabel = !string.IsNullOrWhiteSpace(rawLabel) ? rawLabel.Trim() : "DISC_ART";
        string rimTitle = !string.IsNullOrWhiteSpace(rawLabel)
            ? rawLabel.Trim()
            : Path.GetFileNameWithoutExtension(picture);
        if (string.IsNullOrWhiteSpace(rimTitle))
        {
            rimTitle = "DISC_ART";
        }

        var request = new DiscArtRequest(
            kind switch
            {
                CalibrationDiscKind.Cd => DiscArtMedium.Cd,
                CalibrationDiscKind.Dvd => DiscArtMedium.Dvd,
                _ => DiscArtMedium.BluRay,
            },
            picture.Length == 0 ? string.Empty : Path.GetFullPath(picture),
            parameters,
            Path.GetFullPath(rawOutput),
            tone,
            OptionalDouble(cmd, "start-angle") ?? 0,
            files is null ? null : Path.GetFullPath(files),
            filesOuter ? PictureDiscFilesPlacement.OuterFiles : PictureDiscFilesPlacement.InnerFiles,
            effectiveLabel,
            baseCue is null ? null : Path.GetFullPath(baseCue),
            CdInterleave: !cmd.HasFlag("no-interleave"),
            DvdReadCompatibility: cmd.HasFlag("read-compat"),
            Overwrite: cmd.HasFlag("overwrite"),
            Transform: transform,
            ArtInnerRadiusMm: OptionalDouble(cmd, "art-inner-radius-mm"),
            ArtOuterRadiusMm: OptionalDouble(cmd, "art-outer-radius-mm"),
            TitleRing: titleRing,
            CdPreGapSeconds: preGapSeconds,
            RimHeader: cmd.HasFlag("rim-header")
                ? new DiscArtRimHeader(rimTitle, DateOnly.FromDateTime(DateTime.Now))
                : null,
            CdTurnCorrection: turnCorrection);

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
    /// drive would report one session starting at LBA 0 and a computer would find the picture there and no files. So it
    /// is burned in two sessions with direct drive commands (<see cref="PictureDiscTwoSessionBurner"/>): the picture
    /// zone left open, then the files laid out again for the drive's next writable address, closing the disc.
    /// </summary>
    private static async Task<int> HandleDiscArtTwoSessionBurnAsync(CommandLineArgs cmd, string image, PictureDiscLayout layout,
        TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        foreach (string flag in new[] { "windows-writer", "test-write", "test-first", "no-close", "overwrite" })
        {
            if (cmd.HasFlag(flag))
            {
                throw new CommandLineException(
                    $"--{flag} does not apply to this image: the picture inside and the files at the outer edge are burned as two "
                    + "sessions with direct drive commands on a blank DVD-R or DVD+R, and the second session closes the disc.");
            }
        }

        if (cmd.GetOption("layer-break") is not null)
        {
            throw new CommandLineException("--layer-break does not apply to this image: it is burned as two sessions on a single-layer DVD-R or DVD+R.");
        }

        if (PictureDiscTwoSessionService.WhyLayoutNotSupported(layout) is { } layoutError)
        {
            throw new CommandLineException(layoutError);
        }

        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);
        var status = recorder.QueryMediaStatus();
        var options = new DataBurnOptions
        {
            Speed = ParseSpeed(cmd.GetOption("speed"), recorder, status.MediaType),
            StrictVerify = cmd.HasFlag("strict-verify"),
            DisableBufferUnderrunFree = cmd.HasFlag("no-burnfree"),
        };
        var postAction = ParsePostBurnAction(cmd);

        if (!cmd.IsJson)
        {
            stdout.WriteLine("Burning in two sessions: the picture first (left open), then the files at the drive's next writable address.");
        }

        var service = new PictureDiscTwoSessionService(burnEngine, cmd.GetOption("log-dir"));
        PictureDiscTwoSessionResult result = await service.BurnAsync(recorder, image, layout, options).ConfigureAwait(false);
        var engine = service.LastEngine;
        ExecutePostBurnActionIfSucceeded(postAction, result.Success, recorder);

        string? error = PictureDiscTwoSessionService.FormatError(result);
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                isoPath = image,
                twoSessions = true,
                nothingWritten = result.NothingWritten,
                pictureSectors = result.PictureSectors,
                predictedFilesSessionStartLba = result.PredictedFilesSessionStartLba,
                filesSessionStartLba = result.FilesSessionStartLba,
                filesSessionSectors = result.FilesSessionSectors,
                capacitySectors = result.CapacitySectors,
                writesAccepted = (result.PictureSession?.WritesAccepted ?? false) && (result.FilesSession?.WritesAccepted ?? false),
                sessionClosed = (result.PictureSession?.SessionClosed ?? false) && (result.FilesSession?.SessionClosed ?? false),
                tocChecked = (result.PictureSession?.TocChecked ?? false) && (result.FilesSession?.TocChecked ?? false),
                filesCompared = result.FilesSession?.FilesCompared ?? false,
                pictureAreaCompared = result.PictureSession?.PictureAreaCompared ?? false,
                pictureVerified = result.PictureSession?.Verified ?? false,
                filesVerified = result.FilesSession?.Verified ?? false,
                verificationNote = result.PictureSession?.VerificationNote,
                warning = result.Warning,
                directWrite = NativeDataJson(engine),
                error,
            });
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Two-session picture disc burned:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Picture:        session 1, sectors 0 to {result.PictureSectors - 1:N0}, {(result.PictureSession!.Verified ? "read back: passed" : result.PictureSession.VerificationNote ?? "not compared")}");
            stdout.WriteLine($"  Files:          session 2, sectors {result.FilesSessionStartLba:N0} to {result.FilesSessionStartLba + result.FilesSessionSectors - 1:N0}, read back: {(result.FilesSession!.Verified ? "passed" : "no")}");
            if (result.Warning != null) stdout.WriteLine($"  Note:           {result.Warning}");
            WriteNativeDataSummary(stdout, engine);
            return 0;
        }

        stderr.WriteLine($"Burn failed: {error}");
        WriteNativeDataSummary(stderr, engine);
        return 1;
    }

    private static Task<int> HandleDiscArtBurnAsync(
        CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        string image = Path.GetFullPath(cmd.GetRequiredOption("image", "--image <file.iso|.cue>"));
        string driveId = ResolveTargetDriveId(cmd);
        string extension = Path.GetExtension(image).ToLowerInvariant();
        var forwarded = new CommandLineArgs();
        foreach (var option in cmd.Options) forwarded.Options[option.Key] = option.Value;
        foreach (string flag in cmd.Flags) forwarded.Flags.Add(flag);
        forwarded.Positionals.AddRange(cmd.Positionals);
        forwarded.Options.Remove("image");
        forwarded.Options["drive"] = driveId;

        switch (extension)
        {
            case ".iso":
                if (DiscArtImageBuilder.ReadOuterFilesLayout(image) is { } outer)
                {
                    return HandleDiscArtTwoSessionBurnAsync(cmd, image, outer, stdout, stderr, deviceService, burnEngine);
                }

                forwarded.Subcommand = "burn-iso";
                forwarded.Options["iso"] = image;
                return HandleBurnIsoAsync(forwarded, stdout, stderr, deviceService, burnEngine);
            case ".cue":
                // The picture is the last track of a Session-at-Once disc; only the direct writer can place it, so it is
                // the default unless the person chose an engine.
                if (cmd.HasFlag("windows-writer") && HasPictureInTrackOnePreGap(image))
                {
                    throw new CommandLineException(
                        "This CUE sheet holds a picture in track 1's pre-gap. The Windows writer writes every pre-gap as silence and would leave it out; burn it without --windows-writer (direct drive commands).");
                }

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

    /// <summary>True when track 1 of the CUE sheet has its INDEX 00 in a file of its own (as --pregap-art writes it).</summary>
    private static bool HasPictureInTrackOnePreGap(string cuePath)
    {
        try
        {
            CdBurner.Core.Images.CueSheet sheet = CdBurner.Core.Images.CueSheetParser.ParseFile(cuePath);
            return sheet.Tracks.Count > 0 && sheet.Tracks[0].SplitIndex00 is { DedicatedFileName: not null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            return false; // burn-audio reports an unreadable sheet itself
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
        preGap = plan.CdPreGapSectors is not { } preGapSectors ? null : new
        {
            experimental = true,
            sectors = preGapSectors,
            track1Index01Lba = preGapSectors,
            warning = DiscArtImageBuilder.CdPreGapWarning,
        },
        files = plan.FilesLayout is not { } layout ? null : new
        {
            placement = layout.Placement == PictureDiscFilesPlacement.InnerFiles ? "inner" : "outer",
            filesSessionStartLba = layout.FilesSessionStartLba,
            volumeEndLba = layout.VolumeEndLba,
            gapStartLba = layout.GapStartLba,
            gapEndLba = layout.GapEndLba,
        },
        band = plan.Band is not { } band ? null : new
        {
            requestedInnerRadiusMm = band.RequestedInnerRadiusMm,
            requestedOuterRadiusMm = band.RequestedOuterRadiusMm,
            startLba = band.StartLba,
            endLba = band.EndLba,
            innerRadiusMm = band.InnerRadiusMm,
            outerRadiusMm = band.OuterRadiusMm,
            fullImageSectors = band.FullImageSectors,
            savedSectors = band.SavedSectors,
            savedSecondsAt1x = band.SavedSecondsAt1x,
        },
        titleRing = plan.TitleRing is not { } ring ? null : new
        {
            text = ring.Text,
            requestedHeightMm = ring.RequestedHeightMm,
            startLba = ring.StartLba,
            endLba = ring.EndLba,
            innerRadiusMm = ring.InnerRadiusMm,
            outerRadiusMm = ring.OuterRadiusMm,
            capitalHeightMm = ring.CapitalHeightMm,
            glyphPixelMm = ring.GlyphPixelMm,
            textStartAngleDegrees = ring.TextStartAngleDegrees,
            textSpanDegrees = ring.TextSpanDegrees,
            withPicture = ring.WithPicture,
        },
        rimHeader = plan.RimHeader is not { } header ? null : new
        {
            text = header.HeaderText,
            sha256Prefix = header.Sha256Prefix,
            innerRadiusMm = header.InnerRadiusMm,
            outerRadiusMm = header.OuterRadiusMm,
            scale = header.Scale,
            capitalHeightMm = header.CapitalHeightMm,
            glyphCellWidthMm = header.GlyphCellWidthMm,
            glyphCellHeightMm = header.GlyphCellHeightMm,
            turnsPerGlyphRow = header.TurnsPerGlyphRow,
            trackPitchUm = header.TrackPitchUm,
        },
    };

    /// <summary>
    /// --title-ring &lt;text&gt; [--title-ring-height-mm &lt;mm&gt;] [--title-ring-date yyyy-MM-dd|none]. The date
    /// defaults to today on this computer; null when there is no --title-ring.
    /// </summary>
    private static DiscArtTitleRingRequest? ParseTitleRing(CommandLineArgs cmd)
    {
        string? text = cmd.GetOption("title-ring");
        double? height = OptionalDouble(cmd, "title-ring-height-mm");
        string? rawDate = cmd.GetOption("title-ring-date");
        if (text is null)
        {
            if (height is not null || rawDate is not null)
            {
                throw new CommandLineException("--title-ring-height-mm and --title-ring-date need --title-ring <text>.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CommandLineException("--title-ring needs text: --title-ring <text>");
        }

        DateOnly? date;
        if (rawDate is null)
        {
            date = DateOnly.FromDateTime(DateTime.Now);
        }
        else if (string.Equals(rawDate, "none", StringComparison.OrdinalIgnoreCase))
        {
            date = null;
        }
        else if (DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            date = parsed;
        }
        else
        {
            throw new CommandLineException($"Invalid --title-ring-date value '{rawDate}'. Expected yyyy-MM-dd or none.");
        }

        return new DiscArtTitleRingRequest(text, date, height ?? DiscArtTitleRingRequest.DefaultHeightMm);
    }

    private static void WriteDiscArtPlanLines(TextWriter stdout, DiscArtPlan plan)
    {
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Picture     : sectors {plan.PictureStartLba:N0} to {plan.PictureEndLba - 1:N0} ({plan.PictureEndLba - plan.PictureStartLba:N0} sectors), radius {plan.PictureInnerRadiusMm:0.###} to {plan.PictureOuterRadiusMm:0.###} mm"));
        if (plan.CdPreGapSectors is { } preGapSectors)
        {
            stdout.WriteLine($"  Pre-gap     : track 1's INDEX 00 holds the picture (LBA 0 to {preGapSectors - 1:N0}); track 1 starts at LBA {preGapSectors:N0}");
            stdout.WriteLine($"  Base CUE    : the existing tracks take {plan.BaseCueSectors ?? 0:N0} sectors and move out by {preGapSectors:N0}");
            stdout.WriteLine($"  Warning     : {DiscArtImageBuilder.CdPreGapWarning}");
        }
        else if (plan.BaseCueSectors is { } baseSectors)
        {
            stdout.WriteLine($"  Base CUE    : the existing tracks take {baseSectors:N0} sectors; the picture is the last track");
        }

        if (plan.FilesLayout is { } layout)
        {
            stdout.WriteLine(layout.Placement == PictureDiscFilesPlacement.InnerFiles
                ? $"  Files       : sectors 0 to {layout.VolumeEndLba - 1:N0} (inner), then {layout.GapSectorCount:N0} empty sectors"
                : $"  Files       : sectors {layout.FilesSessionStartLba:N0} to {layout.VolumeEndLba - 1:N0} (outer session), after {layout.GapSectorCount:N0} empty sectors");
            if (layout.Placement == PictureDiscFilesPlacement.OuterFiles && layout.Medium != PictureDiscMedium.Dvd)
            {
                stdout.WriteLine("  Note        : files at the outer edge need two sessions, which are written on DVD-R and DVD+R only so far;");
                stdout.WriteLine("                'disc-art burn' refuses this image. Leave out --files-outer to burn it now.");
            }
            else if (layout.Placement == PictureDiscFilesPlacement.OuterFiles)
            {
                stdout.WriteLine("  Note        : 'disc-art burn' writes this as two sessions with direct drive commands on a blank DVD-R");
                stdout.WriteLine("                or DVD+R: the picture (left open), then the files at the drive's next writable address.");
            }
        }

        if (plan.Band is { } band)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Art band    : drawn from r = {band.InnerRadiusMm:0.###} to {band.OuterRadiusMm:0.###} mm (asked {(band.RequestedInnerRadiusMm is { } i ? i.ToString("0.###", CultureInfo.InvariantCulture) : "start")} to {(band.RequestedOuterRadiusMm is { } o ? o.ToString("0.###", CultureInfo.InvariantCulture) : "end")} mm); sectors before it are zero"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Burn saved  : {band.SavedSectors:N0} of {band.FullImageSectors:N0} sectors not written, {band.SavedSecondsAt1x / 60:0.0} min at 1x; the rest of the disc stays blank"));
        }

        if (plan.TitleRing is { } ring)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Title ring  : \"{ring.Text}\" in sectors {ring.StartLba:N0} to {ring.EndLba - 1:N0}, r = {ring.InnerRadiusMm:0.###} to {ring.OuterRadiusMm:0.###} mm; capitals {ring.CapitalHeightMm:0.##} mm tall, strokes {ring.GlyphPixelMm:0.###} mm"));
            stdout.WriteLine(ring.WithPicture
                ? "                the picture stops where the ring starts"
                : "                no picture: the sectors before the ring are zero");
        }

        if (plan.RimHeader is { } header)
        {
            stdout.WriteLine($"  Rim header  : {header.HeaderText}");
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"                radius {header.InnerRadiusMm:0.###} to {header.OuterRadiusMm:0.###} mm, capitals {header.CapitalHeightMm:0.###} mm tall (scale {header.Scale:0.###}), glyph cells {header.GlyphCellWidthMm * 1000:0} x {header.GlyphCellHeightMm * 1000:0} um, {header.TurnsPerGlyphRow} track turns per glyph row"));
            if (header.Scale < 1)
            {
                stdout.WriteLine("                The ring at the rim is narrower than 4 mm, so the header is smaller than full size: read it with a magnifier or microscope.");
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
