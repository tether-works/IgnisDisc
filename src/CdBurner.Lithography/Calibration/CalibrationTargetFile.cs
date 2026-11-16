// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks
// The Blu-ray parameter and mapping helpers, the stage-to-artwork table and the default search ranges follow
// DiscImageStudio src/DiscImageStudio.App/ViewModels/CalibrationViewModel.cs at commit
// 619ec7505ac8955f35fe2f353ebda74b6ea90dda (Copyright (c) 2026 Disc Image Studio contributors, GPL-3.0-or-later),
// so that a target written here holds the same bytes as one written by upstream. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd;

namespace CdBurner.Lithography.Calibration;

/// <summary>The three calibration discs of the workflow, in the order they are burned.</summary>
public enum CalibrationStage
{
    /// <summary>Three wide bands with angle marks: first estimate of both radii.</summary>
    Coarse,

    /// <summary>Wavy light/dark boundary: refines both radii, keeps the pitch coefficients.</summary>
    Boundary,

    /// <summary>Reference cross: fits both radii and the three pitch coefficients.</summary>
    Fine,
}

/// <summary>What <see cref="CalibrationTargetFile.Write"/> put on disk.</summary>
public sealed record CalibrationTargetFileResult(string ImagePath, string RecordPath, long ImageBytes, string? CueSheetPath);

/// <summary>
/// Creates a calibration target for a stage and writes it as a burnable file (CD: <c>.wav</c> with a CUE sheet or
/// <c>.raw</c>; DVD and Blu-ray: <c>.iso</c>) together with its calibration record (<c>.calibration.json</c>).
/// The record freezes the parameters the target was generated with; the fit of a photo of the burned disc needs it.
/// </summary>
public static class CalibrationTargetFile
{
    public const string RecordExtension = ".calibration.json";

    public static CalibrationArtwork ArtworkFor(CalibrationStage stage) => stage switch
    {
        CalibrationStage.Coarse => CalibrationArtwork.AngleSweepBands,
        CalibrationStage.Boundary => CalibrationArtwork.WavyBoundary,
        CalibrationStage.Fine => CalibrationArtwork.ReferenceCross,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    /// <summary>The stage a saved target belongs to, or null for artwork of older upstream versions.</summary>
    public static CalibrationStage? StageOf(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Artwork switch
        {
            CalibrationArtwork.AngleSweepBands => CalibrationStage.Coarse,
            CalibrationArtwork.WavyBoundary => CalibrationStage.Boundary,
            CalibrationArtwork.ReferenceCross => CalibrationStage.Fine,
            _ => null,
        };
    }

    /// <summary>A new target for <paramref name="stage"/> and an empty record around it, with the search range
    /// upstream uses for that stage (boundary: 0.01 mm, fixed; others: 0.02 mm, widened when needed).</summary>
    public static CalibrationSession CreateSession(CalibrationParameters parameters, CalibrationStage stage,
        bool cdInterleave = true)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateOutputParameters(parameters);
        CalibrationArtwork artwork = ArtworkFor(stage);
        CalibrationTarget target = CalibrationTarget.Create(parameters, artwork: artwork);
        bool boundary = artwork == CalibrationArtwork.WavyBoundary;
        CalibrationFitOptions options = new(
            SearchCenter: parameters,
            RadiusRangeMm: boundary ? .01 : .02,
            AdaptiveRobustScale: true,
            AutoRange: boundary ? false : null);
        return new CalibrationSession(target, [], options, cdInterleave);
    }

    /// <summary>The record file that belongs to an image file: same name, <c>.calibration.json</c>.</summary>
    public static string RecordPathFor(string imagePath)
        => Path.ChangeExtension(Path.GetFullPath(imagePath), RecordExtension);

    /// <summary>Writes the target image and its record. Refuses to replace existing files unless
    /// <paramref name="overwrite"/> is set; a failed or cancelled run leaves neither file behind.</summary>
    public static CalibrationTargetFileResult Write(CalibrationSession session, string imagePath,
        bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        session.Validate();
        CalibrationTarget target = session.Target;
        ValidateOutputParameters(target.Parameters);
        string path = Path.GetFullPath(imagePath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        bool cd = target.Parameters.Kind == CalibrationDiscKind.Cd;
        if (cd ? extension is not (".wav" or ".raw") : extension != ".iso")
            throw new ArgumentException(cd
                ? "A CD calibration target is written as .wav (with a CUE sheet) or .raw."
                : "A DVD or Blu-ray calibration target is written as .iso.", nameof(imagePath));
        string recordPath = RecordPathFor(path);
        string? cuePath = cd && extension == ".wav" ? Path.ChangeExtension(path, ".cue") : null;
        string? dvdSidecar = target.Parameters.Kind == CalibrationDiscKind.Dvd ? path + ".json" : null;
        string?[] outputs = [path, recordPath, cuePath, dvdSidecar];
        if (!overwrite)
        {
            foreach (string? existing in outputs)
                if (existing is not null && File.Exists(existing))
                    throw new IOException($"The file already exists: {existing}");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            foreach (string? existing in outputs)
                if (existing is not null) File.Delete(existing);
            string? writtenCue = null;
            switch (target.Parameters.Kind)
            {
                case CalibrationDiscKind.Cd:
                    writtenCue = CdTrackGenerator.GeneratePattern(target, path, session.CdInterleave,
                        cancellationToken: cancellationToken).CueSheetPath;
                    break;
                case CalibrationDiscKind.Dvd:
                    DvdImageGenerator.GenerateCalibration(target, path, DvdOptions(target), cancellationToken);
                    break;
                default:
                    BluRayDiscParameters disc = BluRayParameters(target.Parameters);
                    using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        BluRayStreamingGenerator.GenerateToStream(output,
                            new BluRayStreamingOptions(disc, 0, disc.OutputSectors), BluRayPatternMapping(target),
                            progress: null, cancellationToken: cancellationToken);
                    }

                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            CalibrationSessionJson.Save(recordPath, session);
            return new CalibrationTargetFileResult(path, recordPath, new FileInfo(path).Length, writtenCue);
        }
        catch
        {
            // A target without its record (or half an image) must not be burned by mistake.
            foreach (string? partial in outputs)
            {
                if (partial is null) continue;
                try { File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            throw;
        }
    }

    /// <summary>Checks the parameters against the encoder of their medium, not only the calibration model.</summary>
    public static void ValidateOutputParameters(CalibrationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        switch (parameters.Kind)
        {
            case CalibrationDiscKind.BluRay:
                BluRayParameters(parameters);
                break;
            case CalibrationDiscKind.Dvd:
                if (parameters.Sectors > uint.MaxValue)
                    throw new ArgumentException("The DVD sector count is too large.", nameof(parameters));
                new DvdStreamingOptions((uint)parameters.Sectors, parameters.InnerRadiusMm, parameters.OuterRadiusMm,
                    parameters.LinearDensity, 0,
                    PitchLinear: parameters.PitchLinear, PitchQuadratic: parameters.PitchQuadratic,
                    PitchCubic: parameters.PitchCubic,
                    UsableSectors: parameters.UsableSectors is { } usable ? checked((uint)usable) : null).Validate();
                break;
            default:
                new CdDiscParameters(parameters.InnerRadiusMm, parameters.OuterRadiusMm, parameters.Sectors,
                    parameters.LinearDensity,
                    PitchLinear: parameters.PitchLinear, PitchQuadratic: parameters.PitchQuadratic,
                    PitchCubic: parameters.PitchCubic, UsableSectors: parameters.UsableSectors).Validate();
                break;
        }
    }

    public static DvdStreamingOptions DvdOptions(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CalibrationParameters p = target.Parameters;
        if (p.Kind != CalibrationDiscKind.Dvd)
            throw new ArgumentException("Expected a DVD calibration target.", nameof(target));
        return new DvdStreamingOptions(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm, p.LinearDensity, 0,
            PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic,
            ReadCompatibility: p.DvdReadCompatibility,
            UsableSectors: p.UsableSectors is { } usable ? checked((uint)usable) : null,
            ReadCompatibilityMode: target.EffectiveDvdReadCompatibilityMode);
    }

    public static BluRayDiscParameters BluRayParameters(CalibrationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Kind != CalibrationDiscKind.BluRay)
            throw new ArgumentException("Expected Blu-ray calibration parameters.", nameof(parameters));
        BluRayDiscParameters disc = new(
            checked((uint)parameters.Sectors), 1, parameters.InnerRadiusMm, parameters.OuterRadiusMm,
            PitchLinear: parameters.PitchLinear, PitchQuadratic: parameters.PitchQuadratic,
            PitchCubic: parameters.PitchCubic,
            UsableSectors: parameters.UsableSectors is { } usable ? checked((uint)usable) : null);
        disc.Validate();
        return disc;
    }

    /// <summary>Full-disc calibration payload: pattern inside the saved drawing range, zeros outside, so the
    /// image still covers every address.</summary>
    public static BluRayPatternRangeMapping BluRayPatternMapping(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        BluRayDiscParameters disc = BluRayParameters(target.Parameters);
        uint start = checked((uint)target.Parameters.DrawingStartSector);
        uint end = target.Parameters.DrawingEndSectorExclusive == 0
            ? disc.OutputSectors
            : checked((uint)target.Parameters.DrawingEndSectorExclusive);
        if (target.HasWideAddressBands)
        {
            return new BluRayPatternRangeMapping(disc, start, end, target.WideBandSectorRanges,
                target.Parameters.BluRayInvertMapping,
                target.HasAngleSweep ? new CalibrationAngleSweepSampler(target).Switches : null);
        }

        // Schema 14 uses the reference picture's own orientation (picture Y down, track Y up).
        if (target.SchemaVersion == 14 && target.Artwork == CalibrationArtwork.ReferenceCross)
        {
            return new BluRayPatternRangeMapping(disc, BluRayReferenceRaster.Value, start, end,
                target.Parameters.BluRayInvertMapping,
                Math.Max(0, target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm - 1e-10),
                target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm + 1e-10);
        }

        // Blu-ray track coordinates use Y up; the calibration canvas uses Y down. Older records keep the sampler
        // they were burned with.
        return new BluRayPatternRangeMapping(disc,
            target.SchemaVersion >= 14 ? (x, y) => target.Sample(x, -y) : target.Sample,
            start, end, target.Parameters.BluRayInvertMapping, allowParallelSampling: true);
    }

    private static readonly Lazy<BluRayGray8Raster> BluRayReferenceRaster = new(() => new BluRayGray8Raster(
        CalibrationReferencePattern.PixelSize, CalibrationReferencePattern.PixelSize,
        CalibrationReferencePattern.ReadGrayPixels()));
}
