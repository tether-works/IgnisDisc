// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks
// The choice of fitter per target and the widening of the search range follow DiscImageStudio
// src/DiscImageStudio.App/ViewModels/CalibrationViewModel.cs (SolveAsync, FitWithAutoRange) and
// CalibrationRingViewModel.cs (SolveRingsAsync) at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda
// (Copyright (c) 2026 Disc Image Studio contributors, GPL-3.0-or-later). See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Calibration;

/// <summary>Which measurement of a calibration record was fitted.</summary>
public enum CalibrationMeasurementKind
{
    /// <summary>Edges of the wide bands (coarse stage) or radii of visible rings.</summary>
    Rings,

    /// <summary>Points along light/dark boundaries (boundary stage).</summary>
    BoundarySegments,

    /// <summary>Points along one arm of the cross or along the single line (fine stage).</summary>
    CenterlineTrace,
}

/// <summary>
/// Result of fitting one record. <see cref="Result"/> is the fitter's own answer; <see cref="RadiusRangeMm"/> is
/// the search range that produced it and <see cref="RangeExpanded"/> says it is wider than the record asked for.
/// </summary>
public sealed record CalibrationRecordFit(
    CalibrationMeasurementKind Measurement, int PointCount, CalibrationFitResult Result,
    double RadiusRangeMm, bool RangeExpanded);

/// <summary>
/// Fits the measurement points saved in a calibration record. The points are in millimetres from the disc centre
/// on the rectified photo (x right, y down), as the record's <c>photo</c> section stores them; the photo itself is
/// not read. The angle marks of a coarse target need the photo's pixels and are not read here, so a coarse record
/// gives the wide-band estimate of the two radii.
/// </summary>
public static class CalibrationRecordFitter
{
    // Search ranges tried in turn when the record allows widening (upstream FitWithAutoRange).
    private static readonly double[] AutoRangesMm = [0.1, 0.5, 2, 5];

    public static CalibrationRecordFit Fit(CalibrationSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        CalibrationTarget target = session.Target;
        CalibrationPhotoSession photo = session.Photo
            ?? throw new ArgumentException("The calibration record holds no measurements (no photo section).");
        if (!photo.RectificationConfirmed)
            throw new ArgumentException("The photo rectification in this record is not confirmed, so its points are not disc coordinates.");

        if (target.HasBoundaryPattern)
        {
            IReadOnlyList<CalibrationBoundarySegment> segments = photo.BoundarySegments ?? [];
            int points = segments.Sum(segment => segment.Points.Count);
            if (points == 0)
                throw new ArgumentException("The record of a boundary target holds no boundary points.");
            CalibrationFitOptions options = OptionsFor(session, fitPitch: false);
            // The boundary stage never widens its range on its own.
            return new CalibrationRecordFit(CalibrationMeasurementKind.BoundarySegments, points,
                CalibrationBoundaryFitter.Fit(target, segments, options, cancellationToken),
                options.RadiusRangeMm, false);
        }

        if (photo.RingObservations is { Count: > 0 } rings && photo.Traces.All(trace => trace.Points.Count == 0))
        {
            return new CalibrationRecordFit(CalibrationMeasurementKind.Rings, rings.Count,
                CalibrationRingFitter.Fit(target, rings, cancellationToken), 0, false);
        }

        int tracePoints = photo.Traces.Sum(trace => trace.Points.Count);
        if (tracePoints == 0)
            throw new ArgumentException("The calibration record holds no measurement points.");
        CalibrationFitOptions traceOptions = OptionsFor(session, fitPitch: !target.HasSingleRadialLine);
        bool auto = session.FitOptions?.AutoRange ?? true;
        (CalibrationFitResult result, double range, bool expanded) =
            FitWithAutoRange(target, photo.Traces, traceOptions, auto, cancellationToken);
        return new CalibrationRecordFit(CalibrationMeasurementKind.CenterlineTrace, tracePoints, result, range, expanded);
    }

    private static CalibrationFitOptions OptionsFor(CalibrationSession session, bool fitPitch)
    {
        CalibrationFitOptions saved = session.FitOptions ?? new CalibrationFitOptions(
            RadiusRangeMm: session.Target.HasBoundaryPattern ? .01 : .02);
        return saved with
        {
            SearchCenter = saved.SearchCenter ?? session.Target.Parameters,
            FitDvdPitch = saved.FitDvdPitch && fitPitch,
            AdaptiveRobustScale = true,
        };
    }

    /// <summary>
    /// Widens the radius range one step at a time until the best answer lies inside it. An answer that sits on the
    /// edge of the range is a limit of the search, not a measurement.
    /// </summary>
    private static (CalibrationFitResult Result, double RangeMm, bool Expanded) FitWithAutoRange(
        CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces, CalibrationFitOptions options,
        bool auto, CancellationToken cancellationToken)
    {
        if (!auto)
            return (CalibrationCrossFitter.Fit(target, traces, options, cancellationToken), options.RadiusRangeMm, false);

        CalibrationFitResult? best = null;
        CalibrationFitResult? last = null;
        double bestRange = options.RadiusRangeMm;
        IEnumerable<double> ranges = AutoRangesMm.Prepend(options.RadiusRangeMm)
            .Where(range => range >= options.RadiusRangeMm).Distinct().OrderBy(range => range);
        foreach (double range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibrationFitResult result = CalibrationCrossFitter.Fit(target, traces,
                options with { RadiusRangeMm = range }, cancellationToken);
            last = result;
            // On Blu-ray the traced curve continues past the drawing band, so a narrow range can return an inside
            // answer with a residual too large to use. Keep widening while the fit is that poor.
            bool unusableBluRay = target.Parameters.Kind == CalibrationDiscKind.BluRay && result.RmsErrorMm > 0.8;
            if (result.Succeeded && !result.BoundaryLimited && !unusableBluRay)
                return (result, range, range > options.RadiusRangeMm);
            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }

            if (!result.Succeeded && IsInputFailure(result)) break;
        }

        CalibrationFitResult fallback = best ?? last
            ?? CalibrationCrossFitter.Fit(target, traces, options, cancellationToken);
        return (fallback, bestRange, bestRange > options.RadiusRangeMm);
    }

    // A wider range cannot repair too few points, a short trace or a trace that turns back.
    private static bool IsInputFailure(CalibrationFitResult result) => !result.Succeeded
        && result.Warnings.Any(warning =>
            warning.Contains("Mark at least", StringComparison.Ordinal)
            || warning.Contains("covers too narrow a range", StringComparison.Ordinal)
            || warning.Contains("doubles back inward", StringComparison.Ordinal)
            || warning.Contains("nearly half a turn", StringComparison.Ordinal));
}
