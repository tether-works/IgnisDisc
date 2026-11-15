// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Solver/CalibrationReferenceImageSampler.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Solver/CalibrationReferenceImageSampler.cs
// IgnisDisc change: namespace; only SampleRectangle and BilinearSampleStability are ported (the calibration
// reference sampler itself is not). See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Dvd.Solver;

internal readonly record struct SampleRectangle(double MinX, double MaxX, double MinY, double MaxY)
{
    internal bool Contains(double x, double y) => x > MinX && x < MaxX && y > MinY && y < MaxY;

    internal static bool TryFromPixels(double left, double right, double top, double bottom,
        double centreX, double centreY, double scale, double xMm, double yMm, out SampleRectangle region)
    {
        region = default;
        if (!double.IsFinite(scale) || scale <= 0) return false;
        double minX = (left - centreX) / scale, maxX = (right - centreX) / scale;
        double minY = (centreY - bottom) / scale, maxY = (centreY - top) / scale;
        double extent = Math.Max(Math.Max(Math.Abs(minX), Math.Abs(maxX)),
            Math.Max(Math.Abs(minY), Math.Abs(maxY)));
        // Cover both this inverse transform and the original forward mapping.
        // The caller separately accounts for geometry/trajectory position error.
        const double epsilon = 2.2204460492503131e-16;
        double guard = 128 * epsilon * (extent + (Math.Abs(centreX) + Math.Abs(centreY) + 1) / scale);
        if (!double.IsFinite(guard)) return false;
        region = new(minX + guard, maxX - guard, minY + guard, maxY - guard);
        return region.Contains(xMm, yMm);
    }
}

internal static class BilinearSampleStability
{
    // Original scalar arithmetic has positive weights and byte-valued channels.
    // With A>=1 and the restricted region's A>=Aanchor/2, rounding error in
    // A/Y and the premultiplied-gradient calculation is far below this budget.
    private const double RoundingBudget = 1e-7;

    internal static bool TryGetRadius(double alpha, double luminance, double alphaGradient,
        double premultipliedLuminanceGradient, out double radius)
    {
        radius = 0;
        if (!double.IsFinite(alpha) || !double.IsFinite(luminance) || alpha < 1) return false;
        double alphaMargin = 0.5 - Math.Abs(alpha - Math.Round(alpha)) - RoundingBudget;
        double luminanceMargin = 0.5 - Math.Abs(luminance - Math.Round(luminance)) - RoundingBudget;
        if (alphaMargin <= 0 || luminanceMargin <= 0) return false;
        double minimumAlpha = alpha / 2;
        // For Y=L/A, |dY| <= (|dL|+255|dA|)/minimumAlpha.
        // The gradients sum the maximum absolute x/y derivatives over the cell,
        // so the result is valid for an axis-aligned square of this pixel radius.
        double luminanceGradient = (premultipliedLuminanceGradient + 255 * alphaGradient + RoundingBudget) / minimumAlpha;
        radius = luminanceMargin / luminanceGradient;
        if (alphaGradient > 0)
            radius = Math.Min(radius, Math.Min(alphaMargin, minimumAlpha - RoundingBudget) / alphaGradient);
        return double.IsFinite(radius) && radius > 0;
    }
}
