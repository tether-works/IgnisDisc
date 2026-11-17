using System.Globalization;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Presets;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private const string DiscArtCalibrateUsage =
        "Usage: ignisdisc-cli disc-art calibrate target --media cd|dvd|bd --stage coarse|boundary|fine --output <file> [options]\n"
        + "       ignisdisc-cli disc-art calibrate fit --points <record.calibration.json> [--candidate <n>] [--drive-model <text> --media-id <text>] [--store <file>]\n"
        + "       ignisdisc-cli disc-art calibrate list [--store <file>]";

    private static async Task<int> HandleDiscArtCommandAsync(
        CommandLineArgs cmd, TextWriter stdout, TextWriter stderr,
        CdBurner.Core.Abstractions.IDiscDeviceService deviceService, CdBurner.Core.Abstractions.IBurnEngineBackend burnEngine)
    {
        string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
        switch (verb)
        {
            case "generate":
                return HandleDiscArtGenerate(cmd, stdout, stderr);
            case "preview":
                return HandleDiscArtPreview(cmd, stdout);
            case "burn":
                return await HandleDiscArtBurnAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false);
            case "calibrate":
                return HandleDiscArtCalibrate(cmd, stdout, stderr);
            default:
                throw new CommandLineException(DiscArtGenerateUsage + "\n" + DiscArtPreviewUsage + "\n" + DiscArtBurnUsage
                    + "\n" + DiscArtCalibrateUsage);
        }
    }

    private static int HandleDiscArtCalibrate(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        if (cmd.Positionals.Count < 2)
        {
            throw new CommandLineException(DiscArtCalibrateUsage);
        }

        return cmd.Positionals[1].ToLowerInvariant() switch
        {
            "target" => HandleCalibrateTarget(cmd, stdout),
            "fit" => HandleCalibrateFit(cmd, stdout, stderr),
            "list" => HandleCalibrateList(cmd, stdout),
            _ => throw new CommandLineException(DiscArtCalibrateUsage),
        };
    }

    private static int HandleCalibrateTarget(CommandLineArgs cmd, TextWriter stdout)
    {
        CalibrationDiscKind kind = ParseCalibrationMedia(cmd.GetRequiredOption("media", "--media cd|dvd|bd"));
        CalibrationStage stage = cmd.GetRequiredOption("stage", "--stage coarse|boundary|fine").ToLowerInvariant() switch
        {
            "coarse" => CalibrationStage.Coarse,
            "boundary" => CalibrationStage.Boundary,
            "fine" => CalibrationStage.Fine,
            string other => throw new CommandLineException(
                $"Unknown --stage '{other}'. Use coarse, boundary or fine."),
        };
        string rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException(kind == CalibrationDiscKind.Cd
                ? "Missing required option: --output <file.wav|file.raw>"
                : "Missing required option: --output <file.iso>");
        }

        string outputPath = Path.GetFullPath(rawOutput);
        (CalibrationParameters parameters, string source) = ResolveCalibrationParameters(cmd, kind);
        bool interleave = !cmd.HasFlag("no-interleave");

        CalibrationSession session;
        CalibrationTargetFileResult written;
        try
        {
            session = CalibrationTargetFile.CreateSession(parameters, stage, interleave);
            written = CalibrationTargetFile.Write(session, outputPath, cmd.HasFlag("overwrite"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new CommandLineException(ex.Message);
        }

        CalibrationParameters p = session.Target.Parameters;
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                media = MediaName(kind),
                stage = stage.ToString().ToLowerInvariant(),
                artwork = session.Target.Artwork.ToString(),
                targetId = session.Target.Id,
                parametersFrom = source,
                outputPath = written.ImagePath,
                recordPath = written.RecordPath,
                cueSheetPath = written.CueSheetPath,
                bytesWritten = written.ImageBytes,
                parameters = ParametersDto(p),
            });
        }
        else
        {
            stdout.WriteLine($"Calibration target written ({MediaName(kind)}, {stage.ToString().ToLowerInvariant()} stage).");
            stdout.WriteLine($"  Target file : {written.ImagePath} ({written.ImageBytes:N0} bytes)");
            if (written.CueSheetPath is not null) stdout.WriteLine($"  CUE sheet   : {written.CueSheetPath}");
            stdout.WriteLine($"  Record      : {written.RecordPath}");
            stdout.WriteLine($"  Parameters  : {source}");
            WriteParameterLines(stdout, p);
            stdout.WriteLine("  Keep the record: fitting a photo of the burned disc needs it.");
        }

        return 0;
    }

    private static int HandleCalibrateFit(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string pointsPath = Path.GetFullPath(cmd.GetRequiredOption("points", "--points <record.calibration.json>"));
        if (!File.Exists(pointsPath))
        {
            throw new CommandLineException($"Calibration record not found: {pointsPath}");
        }

        string? driveModel = cmd.GetOption("drive-model");
        string? mediaId = cmd.GetOption("media-id");
        bool save = !string.IsNullOrWhiteSpace(driveModel) || !string.IsNullOrWhiteSpace(mediaId);
        if (save && (string.IsNullOrWhiteSpace(driveModel) || string.IsNullOrWhiteSpace(mediaId)))
        {
            throw new CommandLineException("To save the result give both --drive-model <text> and --media-id <text>.");
        }

        int? requestedCandidate = null;
        if (cmd.GetOption("candidate") is { } rawCandidate)
        {
            if (!int.TryParse(rawCandidate, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
            {
                throw new CommandLineException($"Invalid --candidate value '{rawCandidate}'. Expected a number starting at 1.");
            }

            requestedCandidate = number;
        }

        CalibrationSession session;
        CalibrationRecordFit fit;
        try
        {
            session = CalibrationSessionJson.Load(pointsPath);
            fit = CalibrationRecordFitter.Fit(session);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            throw new CommandLineException($"{Path.GetFileName(pointsPath)}: {ex.Message}");
        }

        IReadOnlyList<CalibrationFitCandidate> candidates = fit.Result.Candidates;
        if (requestedCandidate is int wanted && wanted > candidates.Count)
        {
            throw new CommandLineException(
                $"--candidate {wanted} does not exist: the fit produced {candidates.Count} candidate(s).");
        }

        // A fit the fitter itself flags is applied only when the person names the candidate, as upstream's
        // "confirm the outline, then apply" step does. A clean fit applies its first candidate.
        int? chosenIndex = requestedCandidate is int n ? n - 1 : CalibrationRecordFitter.DefaultCandidateIndex(fit);
        CalibrationFitCandidate? chosen = chosenIndex is int index ? candidates[index] : null;
        string stageName = CalibrationRecordFitter.StageName(session.Target);

        string? storePath = null;
        if (save && chosen is not null)
        {
            storePath = Path.GetFullPath(cmd.GetOption("store") is { Length: > 0 } custom
                ? custom : CalibrationStore.GetDefaultPath());
            try
            {
                CalibrationStore.Save(storePath, new CalibrationStoreEntry(driveModel!, mediaId!, chosen.Parameters,
                    DateTimeOffset.UtcNow, session.Target.Id, stageName, chosen.RmsErrorMm));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                throw new CommandLineException($"Could not save the calibration: {ex.Message}");
            }
        }

        bool success = chosen is not null;
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success,
                error = success ? null : "The fit did not give a result that can be applied without a choice. Check the points, or name a candidate with --candidate <n>.",
                recordPath = pointsPath,
                targetId = session.Target.Id,
                media = MediaName(session.Target.Parameters.Kind),
                stage = stageName,
                measurement = fit.Measurement.ToString(),
                pointCount = fit.PointCount,
                fitSucceeded = fit.Result.Succeeded,
                searchRangeMm = fit.RadiusRangeMm,
                searchRangeExpanded = fit.RangeExpanded,
                warnings = fit.Result.Warnings,
                generatedWith = ParametersDto(session.Target.Parameters),
                chosenCandidate = chosenIndex + 1,
                result = chosen is null ? null : ParametersDto(chosen.Parameters),
                candidates = candidates.Select((c, i) => new
                {
                    number = i + 1,
                    rmsErrorMm = c.RmsErrorMm,
                    maxErrorMm = c.MaxErrorMm,
                    outlierCount = c.OutlierCount,
                    rotationRadians = c.RotationRadians,
                    parameters = ParametersDto(c.Parameters),
                }),
                saved = storePath is not null,
                storePath,
                driveModel = storePath is null ? null : driveModel!.Trim(),
                mediaId = storePath is null ? null : mediaId!.Trim(),
            });
            return success ? 0 : 1;
        }

        stdout.WriteLine($"Calibration fit of {Path.GetFileName(pointsPath)}");
        stdout.WriteLine($"  Target      : {session.Target.Id} ({MediaName(session.Target.Parameters.Kind)}, {stageName})");
        stdout.WriteLine($"  Measurement : {DescribeMeasurement(fit.Measurement)}, {fit.PointCount} point(s)");
        if (fit.RadiusRangeMm > 0)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Search range: +/-{fit.RadiusRangeMm:0.###} mm{(fit.RangeExpanded ? " (widened)" : string.Empty)}"));
        }

        stdout.WriteLine("  Generated with:");
        WriteParameterLines(stdout, session.Target.Parameters);
        foreach (string warning in fit.Result.Warnings)
        {
            stdout.WriteLine($"  Note        : {warning}");
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            CalibrationFitCandidate c = candidates[i];
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Candidate {i + 1} : inner {c.Parameters.InnerRadiusMm:R} mm, outer {c.Parameters.OuterRadiusMm:R} mm, deviation {c.RmsErrorMm:0.####} mm (largest {c.MaxErrorMm:0.###} mm, {c.OutlierCount} outlier(s))"));
        }

        if (chosen is null)
        {
            stderr.WriteLine(candidates.Count > 0
                ? "Error: The fit is flagged (see the notes). Check the points, or apply a candidate with --candidate <n>."
                : "Error: The fit gave no result. Check the points and the record.");
            return 1;
        }

        stdout.WriteLine($"  Result (candidate {chosenIndex + 1}):");
        WriteParameterLines(stdout, chosen.Parameters);
        stdout.WriteLine(storePath is not null
            ? $"  Saved for drive '{driveModel!.Trim()}' and media '{mediaId!.Trim()}' in {storePath}"
            : "  Not saved (give --drive-model and --media-id to save).");
        return 0;
    }

    private static int HandleCalibrateList(CommandLineArgs cmd, TextWriter stdout)
    {
        string storePath = Path.GetFullPath(cmd.GetOption("store") is { Length: > 0 } custom
            ? custom : CalibrationStore.GetDefaultPath());
        CalibrationStoreDocument document;
        try
        {
            document = CalibrationStore.Load(storePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new CommandLineException(ex.Message);
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                storePath,
                entries = document.Entries.Select(e => new
                {
                    driveModel = e.DriveModel,
                    mediaId = e.MediaId,
                    media = MediaName(e.Parameters.Kind),
                    stage = e.Stage,
                    savedUtc = e.SavedUtc,
                    targetId = e.TargetId,
                    rmsErrorMm = e.RmsErrorMm,
                    parameters = ParametersDto(e.Parameters),
                }),
            });
            return 0;
        }

        stdout.WriteLine($"Saved calibrations in {storePath}: {document.Entries.Count}");
        foreach (CalibrationStoreEntry e in document.Entries)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {e.DriveModel} | {e.MediaId} | {MediaName(e.Parameters.Kind)} | {e.Stage} | {e.SavedUtc:yyyy-MM-dd} | inner {e.Parameters.InnerRadiusMm:R} mm, outer {e.Parameters.OuterRadiusMm:R} mm"));
        }

        return 0;
    }

    private static CalibrationDiscKind ParseCalibrationMedia(string media) => media.ToLowerInvariant() switch
    {
        "cd" or "cd-r" => CalibrationDiscKind.Cd,
        "dvd" or "dvd-r" or "dvd+r" => CalibrationDiscKind.Dvd,
        "bd" or "bd-r" or "bluray" or "blu-ray" => CalibrationDiscKind.BluRay,
        _ => throw new CommandLineException($"Unknown --media '{media}'. Use cd, dvd or bd."),
    };

    private static string MediaName(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => "CD",
        CalibrationDiscKind.Dvd => "DVD",
        _ => "BD",
    };

    private static string DescribeMeasurement(CalibrationMeasurementKind kind) => kind switch
    {
        CalibrationMeasurementKind.Rings => "band edges",
        CalibrationMeasurementKind.BoundarySegments => "boundary points",
        _ => "points along one line",
    };

    private static object ParametersDto(CalibrationParameters p) => new
    {
        innerRadiusMm = p.InnerRadiusMm,
        outerRadiusMm = p.OuterRadiusMm,
        sectors = p.Sectors,
        usableSectors = p.UsableSectors,
        pitchLinear = p.PitchLinear,
        pitchQuadratic = p.PitchQuadratic,
        pitchCubic = p.PitchCubic,
    };

    private static void WriteParameterLines(TextWriter stdout, CalibrationParameters p)
    {
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    inner radius {p.InnerRadiusMm:R} mm, outer radius {p.OuterRadiusMm:R} mm, {p.Sectors} sectors"));
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    pitch coefficients {p.PitchLinear:R}, {p.PitchQuadratic:R}, {p.PitchCubic:R}"));
    }

    /// <summary>
    /// Start values for a new target, in this order: a calibration saved for the drive model and media ID (when both
    /// are given and one exists), else the disc preset; single values given on the command line replace either.
    /// </summary>
    private static (CalibrationParameters Parameters, string Source) ResolveCalibrationParameters(
        CommandLineArgs cmd, CalibrationDiscKind kind)
    {
        string? driveModel = cmd.GetOption("drive-model");
        string? mediaId = cmd.GetOption("media-id");
        if (string.IsNullOrWhiteSpace(driveModel) != string.IsNullOrWhiteSpace(mediaId))
        {
            throw new CommandLineException("Give both --drive-model <text> and --media-id <text>, or neither.");
        }

        CalibrationParameters? parameters = null;
        string source = string.Empty;
        if (!string.IsNullOrWhiteSpace(driveModel))
        {
            string storePath = Path.GetFullPath(cmd.GetOption("store") is { Length: > 0 } custom
                ? custom : CalibrationStore.GetDefaultPath());
            try
            {
                CalibrationStoreEntry? entry = CalibrationStore.Find(CalibrationStore.Load(storePath), driveModel, mediaId!, kind);
                if (entry is not null)
                {
                    parameters = entry.Parameters;
                    source = $"saved calibration for drive '{entry.DriveModel}' and media '{entry.MediaId}'";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                throw new CommandLineException(ex.Message);
            }
        }

        if (parameters is null)
        {
            (parameters, string presetId) = PresetCalibrationParameters(cmd, kind);
            source = $"disc preset '{presetId}'";
        }

        double? inner = OptionalDouble(cmd, "inner-radius-mm");
        double? outer = OptionalDouble(cmd, "outer-radius-mm");
        double? pitchLinear = OptionalDouble(cmd, "pitch-linear");
        double? pitchQuadratic = OptionalDouble(cmd, "pitch-quadratic");
        double? pitchCubic = OptionalDouble(cmd, "pitch-cubic");
        long? sectors = null;
        if (cmd.GetOption("sectors") is { } rawSectors)
        {
            if (!long.TryParse(rawSectors, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
            {
                throw new CommandLineException($"Invalid --sectors value '{rawSectors}'. Expected a positive whole number.");
            }

            sectors = parsed;
        }

        if (inner is not null || outer is not null || sectors is not null
            || pitchLinear is not null || pitchQuadratic is not null || pitchCubic is not null)
        {
            parameters = parameters with
            {
                InnerRadiusMm = inner ?? parameters.InnerRadiusMm,
                OuterRadiusMm = outer ?? parameters.OuterRadiusMm,
                Sectors = sectors ?? parameters.Sectors,
                PitchLinear = pitchLinear ?? parameters.PitchLinear,
                PitchQuadratic = pitchQuadratic ?? parameters.PitchQuadratic,
                PitchCubic = pitchCubic ?? parameters.PitchCubic,
            };
            if (sectors is not null)
            {
                // A different capacity is a different disc: the whole of it is usable and drawn.
                parameters = parameters with
                {
                    UsableSectors = kind == CalibrationDiscKind.BluRay ? sectors : null,
                    DrawingEndSectorExclusive = kind == CalibrationDiscKind.BluRay ? sectors.Value : 0,
                };
            }

            source += ", with values from the command line";
        }

        return (parameters, source);
    }

    private static (CalibrationParameters Parameters, string PresetId) PresetCalibrationParameters(
        CommandLineArgs cmd, CalibrationDiscKind kind)
    {
        string presetId = cmd.GetOption("disc-preset") ?? kind switch
        {
            CalibrationDiscKind.Cd => "cd-80",
            CalibrationDiscKind.Dvd => "dvd-5-120mm",
            _ => "bd-r-sl-25gb",
        };
        DiscPresetJsonDocument doc = LoadDiscPresets(cmd.GetOption("disc-presets-file"));
        switch (kind)
        {
            case CalibrationDiscKind.Cd:
            {
                CdDiscPresetDefinition preset = doc.CdPresets.FirstOrDefault(
                    p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                    ?? throw UnknownPreset("CD", presetId, doc.CdPresets.Select(p => p.Id));
                return (new CalibrationParameters(kind, preset.InnerRadiusMm, preset.OuterRadiusMm, preset.Sectors,
                    preset.LinearVelocityMmPerSecond, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic,
                    UsableSectors: preset.UsableSectors), preset.Id);
            }

            case CalibrationDiscKind.Dvd:
            {
                DvdDiscPresetDefinition preset = doc.DvdPresets.FirstOrDefault(
                    p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                    ?? throw UnknownPreset("DVD", presetId, doc.DvdPresets.Select(p => p.Id));
                return (new CalibrationParameters(kind, preset.InnerRadiusMm, preset.OuterRadiusMm, preset.TotalSectors,
                    preset.ChannelBitLengthNm, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic,
                    UsableSectors: preset.UsableSectors), preset.Id);
            }

            default:
            {
                IReadOnlyList<BluRayDiscPresetDefinition> presets = doc.BluRayPresets ?? [];
                BluRayDiscPresetDefinition preset = presets.FirstOrDefault(
                    p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                    ?? throw UnknownPreset("Blu-ray", presetId, presets.Select(p => p.Id));
                BluRayDiscParameters disc;
                try
                {
                    disc = new BluRayDiscParameters(preset.TotalSectors, 1, preset.InnerRadiusMm, preset.OuterRadiusMm,
                        PitchLinear: preset.PitchLinear, PitchQuadratic: preset.PitchQuadratic,
                        PitchCubic: preset.PitchCubic, UsableSectors: preset.UsableSectors);
                    disc.Validate();
                }
                catch (ArgumentException ex)
                {
                    throw new CommandLineException($"Blu-ray preset '{preset.Id}': {ex.Message}");
                }

                return (new CalibrationParameters(kind, disc.InnerRadiusMm, disc.OuterRadiusMm, disc.TotalSectors,
                    CalibrationParameters.BluRayChannelBitLengthNm, disc.PitchLinear, disc.PitchQuadratic,
                    disc.PitchCubic, BluRayInvertMapping: preset.InvertMapping, DrawingStartSector: 0,
                    DrawingEndSectorExclusive: disc.OutputSectors, UsableSectors: disc.OutputSectors), preset.Id);
            }
        }
    }

    private static CommandLineException UnknownPreset(string media, string presetId, IEnumerable<string> available)
        => new($"Unknown {media} disc preset '{presetId}'. Available {media} presets: {string.Join(", ", available)}");

    private static DiscPresetJsonDocument LoadDiscPresets(string? presetsFile)
    {
        DiscPresetJsonDocument doc = DiscPresetJsonStore.LoadBuiltIn();
        string? userPath = null;
        if (!string.IsNullOrWhiteSpace(presetsFile))
        {
            userPath = Path.GetFullPath(presetsFile);
            if (!File.Exists(userPath))
            {
                throw new CommandLineException($"Disc presets file not found: {userPath}");
            }
        }
        else if (File.Exists(DiscPresetJsonStore.GetDefaultPath()))
        {
            userPath = DiscPresetJsonStore.GetDefaultPath();
        }

        return userPath is null ? doc : DiscPresetJsonStore.Merge(doc, DiscPresetJsonStore.Load(userPath));
    }

    private static double? OptionalDouble(CommandLineArgs cmd, string name)
    {
        if (cmd.GetOption(name) is not { } raw) return null;
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new CommandLineException($"Invalid --{name} value '{raw}'. Expected a number.");
        }

        return value;
    }
}
