// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;

namespace CdBurner.Lithography.Calibration;

/// <summary>Which outlines a rectification rests on.</summary>
public enum DiscPhotoRectificationKind
{
    /// <summary>Outer rim and a bare 7.5 mm hole, as the rectifier expects.</summary>
    Standard,

    /// <summary>Outer rim and a hole edge that is not 7.5 mm from the centre (bevel, clamping ring).</summary>
    HoleRadiusFound,

    /// <summary>The outer rim alone; the hole outline did not match a flat disc and was not used.</summary>
    RimOnly,
}

/// <summary>The rectification of a disc photo and how it was reached.</summary>
/// <param name="Fit">The rectifier's result (transform, per-outline errors, message).</param>
/// <param name="HoleRadiusMm">Radius the hole outline was fitted to: 7.5 mm unless the visible edge needed another.</param>
/// <param name="Kind">Which outlines the fit rests on.</param>
public sealed record DiscPhotoRectification(CalibrationPhotoFitResult Fit, double HoleRadiusMm, DiscPhotoRectificationKind Kind);

/// <summary>
/// Rectifies a disc photo against the 120 mm outer rim and the centre hole. The hole edge seen in a photo is often
/// not the bare 7.5 mm hole (a bevel, the clamping ring or the felt below it show up as the nearest strong edge), and
/// the rectifier refuses points that do not match its radius. So the standard 7.5 mm is tried first; only when that is
/// refused is the radius searched, and the result says which one was used. The 120 mm outer rim sets the scale; the
/// hole circle mainly fixes the centre and the tilt, so a hole radius that is a little off matters less than an
/// outer rim that is off.
/// </summary>
public static class DiscPhotoRectifier
{
    public const double NominalHoleRadiusMm = 7.5;
    private const double SearchLowMm = 6.0;
    private const double SearchHighMm = 14.0;
    private const double SearchStepMm = 0.25;

    public static DiscPhotoRectification Fit(IReadOnlyList<CalibrationPoint> outer, IReadOnlyList<CalibrationPoint> hole,
        double outerRadiusMm = 60, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(hole);
        if (outer.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y))
            || hole.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        {
            return new DiscPhotoRectification(
                new CalibrationPhotoFitResult(false, null, double.NaN, double.NaN, "The outline contains invalid coordinates."),
                NominalHoleRadiusMm, DiscPhotoRectificationKind.Standard);
        }
        // No hole outline at all (none found in the photo, none clicked): the rim alone has to do.
        if (hole.Count == 0 && outer.Count >= 5) return FitRimOnly(outer, outerRadiusMm, null, cancellationToken);
        CalibrationPhotoFitResult standard = CalibrationPhotoRectifier.Fit(outer, hole, outerRadiusMm, NominalHoleRadiusMm, cancellationToken);
        if (standard.Succeeded) return new DiscPhotoRectification(standard, NominalHoleRadiusMm, DiscPhotoRectificationKind.Standard);
        // Only a disagreement of the two circles is worth searching; fewer than five points, collinear points and the
        // like fail for every radius with the same message, so return the first answer.
        if (double.IsNaN(standard.HoleRmsPixels)) return new DiscPhotoRectification(standard, NominalHoleRadiusMm, DiscPhotoRectificationKind.Standard);

        DiscPhotoRectification? best = null;
        double bestError = double.PositiveInfinity;
        for (double radius = SearchLowMm; radius <= SearchHighMm + 1e-9; radius += SearchStepMm)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibrationPhotoFitResult result = CalibrationPhotoRectifier.Fit(outer, hole, outerRadiusMm, radius, cancellationToken);
            if (!result.Succeeded) continue;
            double error = Math.Max(result.OuterRmsPixels, result.HoleRmsPixels);
            if (error < bestError)
            {
                bestError = error;
                best = new DiscPhotoRectification(result with
                {
                    Message = string.Create(CultureInfo.InvariantCulture,
                        $"Rectified. The hole edge in this photo is {radius:0.##} mm from the centre, not the bare 7.5 mm (a bevel or the clamping ring is the strongest edge there), so that radius was used. The outer rim (60 mm) sets the scale. Outline errors: outer edge {result.OuterRmsPixels:0.##} px, hole edge {result.HoleRmsPixels:0.##} px."),
                }, radius, DiscPhotoRectificationKind.HoleRadiusFound);
            }
        }

        return best ?? FitRimOnly(outer, outerRadiusMm, standard, cancellationToken);
    }

    /// <summary>
    /// The hole ring and the rim do not belong to one flat circle pair: the ring is on a bevel or lower down and moves
    /// against the rim as the camera looks at it. Fit from the rim alone: a concentric hole circle is made from the rim
    /// points themselves (an affine image of a circle scales about its centre), so only the rim's own ellipse decides
    /// the result. <paramref name="refused"/> is the answer to give back when the rim alone does not fit either; null
    /// when there was no hole outline to begin with.
    /// </summary>
    private static DiscPhotoRectification FitRimOnly(IReadOnlyList<CalibrationPoint> outer, double outerRadiusMm,
        CalibrationPhotoFitResult? refused, CancellationToken cancellationToken)
    {
        double centreX = (outer.Max(p => p.X) + outer.Min(p => p.X)) / 2;
        double centreY = (outer.Max(p => p.Y) + outer.Min(p => p.Y)) / 2;
        double ratio = NominalHoleRadiusMm / outerRadiusMm;
        CalibrationPoint[] synthetic = outer
            .Select(p => new CalibrationPoint(centreX + ratio * (p.X - centreX), centreY + ratio * (p.Y - centreY))).ToArray();
        CalibrationPhotoFitResult rimOnly = CalibrationPhotoRectifier.Fit(outer, synthetic, outerRadiusMm, NominalHoleRadiusMm, cancellationToken);
        if (!rimOnly.Succeeded)
            return new DiscPhotoRectification(refused ?? rimOnly, NominalHoleRadiusMm, DiscPhotoRectificationKind.Standard);
        string why = refused is null
            ? "No hole edge was marked, so the hole was not used."
            : "The hole edge seen in this photo did not match a flat 120 mm disc (it sits at a different depth than the rim), so it was not used.";
        return new DiscPhotoRectification(rimOnly with
        {
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Rectified from the outer rim alone (outer edge error {rimOnly.OuterRmsPixels:0.##} px). {why} Take the photo straight from above for the best result."),
        }, NominalHoleRadiusMm, DiscPhotoRectificationKind.RimOnly);
    }
}
