// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Calibration;

/// <summary>
/// What <see cref="DiscPhotoDetector"/> found in a photo. Every position is in pixels of the photo that was searched.
/// </summary>
/// <param name="OuterPoints">Points on the outer rim of the disc.</param>
/// <param name="HolePoints">Points on the visible edge of the centre hole; empty when the hole was not found.</param>
/// <param name="CenterX">Centre of the outer circle.</param>
/// <param name="CenterY">Centre of the outer circle.</param>
/// <param name="OuterRadius">Radius of the outer circle.</param>
/// <param name="HoleRadius">Radius of the hole edge that was found, or 0 when none was.</param>
/// <param name="RimEdgePoints">How many separate edge readings the rim fit rests on.</param>
/// <param name="RimScatterPixels">Spread of those readings around the fitted outline (photo pixels).</param>
/// <param name="HoleScatterPixels">The same for the hole edge; 0 when no hole was found.</param>
public sealed record DiscPhotoDetection(
    IReadOnlyList<CalibrationPoint> OuterPoints, IReadOnlyList<CalibrationPoint> HolePoints,
    double CenterX, double CenterY, double OuterRadius, double HoleRadius,
    int RimEdgePoints, double RimScatterPixels, double HoleScatterPixels)
{
    /// <summary>Visible hole radius as a fraction of the outer radius (a bare 15 mm hole in a 120 mm disc is 0.125).</summary>
    public double HoleFraction => OuterRadius > 0 ? HoleRadius / OuterRadius : 0;
}

/// <summary>
/// Finds the outer rim and the centre-hole edge of a disc lying roughly face-up in a photo, so the calibration screen
/// can mark them without clicks. The photo is shrunk to about 900 pixels, the disc's centre is found by letting every
/// strong edge vote along its own direction (a circle's edge normals meet at its centre), the rim is then read along
/// 72 rays from that centre, and a circle is fitted with outliers (reflections, a hand, a case edge) dropped. The hole
/// is searched the same way in a window around where a standard disc puts it. Works for a photo taken from above;
/// a strongly tilted disc is an ellipse and fits a circle worse, so it may be refused. Returns null when no clear
/// circular rim is found.
/// </summary>
public static class DiscPhotoDetector
{
    /// <summary>Longest side of the working copy the search runs on.</summary>
    public const int WorkingSide = 900;

    private const int OuterRays = 72;
    private const int HoleRays = 36;
    private const double MinOuterFraction = 0.25; // of the shorter side of the working copy
    private const double MaxOuterFraction = 0.55;

    public static DiscPhotoDetection? Detect(BgraRaster photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        photo.Validate();
        if (photo.Width < 64 || photo.Height < 64) return null;

        double scale = Math.Min(1.0, (double)WorkingSide / Math.Max(photo.Width, photo.Height));
        int w = Math.Max(1, (int)Math.Round(photo.Width * scale));
        int h = Math.Max(1, (int)Math.Round(photo.Height * scale));
        float[] gray = Blur(Shrink(photo, w, h), w, h, 1.5);
        Gradient(gray, w, h, out float[] gx, out float[] gy, out float[] mag);

        double shortSide = Math.Min(w, h);
        double minRadius = shortSide * MinOuterFraction;
        double maxRadius = shortSide * MaxOuterFraction;
        if (!FindCentre(gx, gy, mag, w, h, (int)minRadius, (int)maxRadius, out double centreX, out double centreY)) return null;

        Circle? outer = null;
        foreach (bool falling in new[] { true, false })
        {
            Circle? candidate = FitRim(gray, w, h, centreX, centreY, minRadius, maxRadius, falling);
            if (candidate is not null && (outer is null || candidate.Points.Count > outer.Points.Count)) outer = candidate;
        }

        if (outer is null || outer.Points.Count < OuterRays * 2 / 5) return null;
        if (outer.Radius < minRadius * 0.9 || outer.Radius > maxRadius * 1.1) return null;

        Circle? hole = FindHole(gray, w, h, outer);
        double inverse = 1.0 / scale;
        // The search ran on a shrunken copy, so each rim point can be a few photo pixels off; the rectifier accepts
        // an error of only 0.6 % of the radius. Read every rim point again at full size, close to where it was found.
        var fullPoints = outer.Points.Select(p => (X: p.X * inverse, Y: p.Y * inverse)).ToList();
        Circle fullRim = new(outer.CentreX * inverse, outer.CentreY * inverse, outer.Radius * inverse, fullPoints);
        Circle refined = RefineRim(photo, fullRim, IsFalling(gray, w, h, outer), 3.0 * inverse) ?? fullRim;
        if (!TrySmoothOutline(refined.Points, refined.CentreX, refined.CentreY, OutlinePoints, out CalibrationPoint[] outerPoints, out double outerScatter))
            return null;
        // A real rim scatters about 1 % of its radius around its ellipse; noise or a clutter of edges that happen to
        // lie on a rough circle scatter several times more. Better no answer than a wrong one.
        if (!double.IsFinite(outerScatter) || outerScatter > MaxRimScatterFraction * refined.Radius) return null;
        CalibrationPoint[] holePoints = [];
        double holeScatter = 0;
        double holeRadiusFull = 0;
        if (hole is not null)
        {
            var holeFull = hole.Points.Select(p => (X: p.X * inverse, Y: p.Y * inverse)).ToList();
            if (TrySmoothOutline(holeFull, hole.CentreX * inverse, hole.CentreY * inverse, OutlinePoints, out CalibrationPoint[] smoothHole, out double scatter)
                && double.IsFinite(scatter)
                && scatter <= MaxRimScatterFraction * 2 * refined.Radius)
            {
                holePoints = smoothHole;
                holeScatter = scatter;
                holeRadiusFull = hole.Radius * inverse;
            }
        }

        return new DiscPhotoDetection(outerPoints, holePoints, refined.CentreX, refined.CentreY,
            refined.Radius, holeRadiusFull, refined.Points.Count, outerScatter, holeScatter);
    }

    private const int OutlinePoints = 48;
    private const double MaxRimScatterFraction = 0.02;

    /// <summary>
    /// Fits the outline as an ellipse (radius = mean + first and second harmonics of the angle, exact for a circle seen
    /// from a slight angle) through every measured point, and returns <paramref name="count"/> points on that curve
    /// together with the scatter of the measured points around it. One edge point can be several pixels off; the
    /// ellipse through sixty of them is not.
    /// </summary>
    internal static bool TrySmoothOutline(IReadOnlyList<(double X, double Y)> measured,
        double cx, double cy, int count, out CalibrationPoint[] points, out double scatter)
    {
        const int Terms = 5;
        points = [];
        scatter = double.PositiveInfinity;
        if (measured.Count < Terms) return false;

        double[,] normal = new double[Terms, Terms];
        double[] right = new double[Terms];
        var samples = new List<(double Angle, double Radius)>(measured.Count);
        foreach ((double x, double y) in measured)
        {
            double angle = Math.Atan2(y - cy, x - cx);
            double radius = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            samples.Add((angle, radius));
            double[] basis = Basis(angle);
            for (int i = 0; i < Terms; i++)
            {
                right[i] += basis[i] * radius;
                for (int j = 0; j < Terms; j++) normal[i, j] += basis[i] * basis[j];
            }
        }

        double[]? coefficients = SolveSmall(normal, right);
        if (coefficients is null) return false;

        double sumSquares = 0;
        foreach ((double angle, double radius) in samples)
        {
            double model = Basis(angle).Zip(coefficients, (b, c) => b * c).Sum();
            sumSquares += (radius - model) * (radius - model);
        }

        points = new CalibrationPoint[count];
        for (int k = 0; k < count; k++)
        {
            double angle = 2 * Math.PI * k / count;
            double radius = Basis(angle).Zip(coefficients, (b, c) => b * c).Sum();
            points[k] = new CalibrationPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }

        scatter = Math.Sqrt(sumSquares / samples.Count);
        return double.IsFinite(scatter);
    }

    private static double[] Basis(double angle)
        => [1, Math.Cos(angle), Math.Sin(angle), Math.Cos(2 * angle), Math.Sin(2 * angle)];

    /// <summary>Gaussian elimination with partial pivoting for the small normal equations; null when singular.</summary>
    private static double[]? SolveSmall(double[,] matrix, double[] right)
    {
        int n = right.Length;
        double[,] a = (double[,])matrix.Clone();
        double[] b = (double[])right.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;
            if (pivot != col)
            {
                for (int c = 0; c < n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double factor = a[r, col] / a[col, col];
                for (int c = col; c < n; c++) a[r, c] -= factor * a[col, c];
                b[r] -= factor * b[col];
            }
        }

        double[] x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < n; c++) sum -= a[r, c] * x[c];
            x[r] = sum / a[r, r];
        }

        return x;
    }

    /// <summary>True when the disc is brighter than what lies outside its rim (the usual case on a dark surface).</summary>
    private static bool IsFalling(float[] gray, int w, int h, Circle rim)
    {
        double inside = 0, outside = 0;
        int count = 0;
        foreach ((double x, double y) in rim.Points)
        {
            double dx = x - rim.CentreX, dy = y - rim.CentreY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) continue;
            float a = Sample(gray, w, h, x - dx / length * 4, y - dy / length * 4);
            float b = Sample(gray, w, h, x + dx / length * 4, y + dy / length * 4);
            if (float.IsNaN(a) || float.IsNaN(b)) continue;
            inside += a;
            outside += b;
            count++;
        }

        return count == 0 || inside >= outside;
    }

    /// <summary>Moves each rim point to the strongest brightness step within <paramref name="window"/> pixels of it, at the photo's own resolution.</summary>
    private static Circle? RefineRim(BgraRaster photo, Circle rim, bool falling, double window)
    {
        const double Step = 0.5;
        int count = (int)(2 * window / Step) + 1;
        var refined = new List<(double X, double Y)>();
        foreach ((double px, double py) in rim.Points)
        {
            double dx = px - rim.CentreX, dy = py - rim.CentreY;
            double r0 = Math.Sqrt(dx * dx + dy * dy);
            if (r0 < 1) continue;
            double ux = dx / r0, uy = dy / r0;
            double[] value = new double[count];
            bool inside = true;
            for (int k = 0; k < count; k++)
            {
                double r = r0 - window + k * Step;
                double luma = Luma(photo, rim.CentreX + ux * r, rim.CentreY + uy * r);
                if (double.IsNaN(luma)) { inside = false; break; }
                value[k] = luma;
            }

            if (!inside) continue;
            // Light smoothing (about 2 photo pixels wide) before taking the slope.
            double[] smooth = SmoothLine(value, 4);
            int best = -1;
            double bestScore = 0;
            for (int k = 1; k < count - 1; k++)
            {
                double slope = (smooth[k + 1] - smooth[k - 1]) / 2;
                double score = falling ? -slope : slope;
                if (score > bestScore) { bestScore = score; best = k; }
            }

            if (best <= 1 || best >= count - 2 || bestScore < 0.05) continue; // at the window's edge: not the rim
            double rr = r0 - window + best * Step;
            refined.Add((rim.CentreX + ux * rr, rim.CentreY + uy * rr));
        }

        Circle? fit = FitWithRejection(refined, Math.Max(1.5, window / 3));
        return fit is not null && fit.Points.Count >= OuterRays * 2 / 5 ? fit : null;
    }

    private static double Luma(BgraRaster photo, double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= photo.Width - 1 || y0 >= photo.Height - 1) return double.NaN;
        double fx = x - x0, fy = y - y0;
        double Pixel(int xx, int yy)
        {
            int i = (yy * photo.Width + xx) * 4;
            return 0.114 * photo.Pixels[i] + 0.587 * photo.Pixels[i + 1] + 0.299 * photo.Pixels[i + 2];
        }

        return (Pixel(x0, y0) * (1 - fx) + Pixel(x0 + 1, y0) * fx) * (1 - fy)
             + (Pixel(x0, y0 + 1) * (1 - fx) + Pixel(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>
    /// A square-ish crop around the detected disc with a margin of <paramref name="margin"/> times its radius on every
    /// side, kept inside the photo. Returns the cut-out and its top-left corner in the original photo.
    /// </summary>
    public static (BgraRaster Cropped, int Left, int Top) CropToDisc(BgraRaster photo, DiscPhotoDetection found, double margin = 0.06)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(found);
        double half = found.OuterRadius * (1 + margin);
        int left = Math.Clamp((int)Math.Floor(found.CenterX - half), 0, photo.Width - 1);
        int top = Math.Clamp((int)Math.Floor(found.CenterY - half), 0, photo.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(found.CenterX + half), left + 1, photo.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(found.CenterY + half), top + 1, photo.Height);
        int cropWidth = right - left;
        int cropHeight = bottom - top;
        byte[] pixels = new byte[checked(cropWidth * cropHeight * 4)];
        for (int y = 0; y < cropHeight; y++)
            Buffer.BlockCopy(photo.Pixels, ((top + y) * photo.Width + left) * 4, pixels, y * cropWidth * 4, cropWidth * 4);
        return (new BgraRaster(cropWidth, cropHeight, pixels), left, top);
    }

    // ---------------------------------------------------------------- working copy

    private sealed record Circle(double CentreX, double CentreY, double Radius, List<(double X, double Y)> Points);

    /// <summary>Area-averaged brightness (0-255) of the photo at the working size.</summary>
    private static float[] Shrink(BgraRaster photo, int w, int h)
    {
        float[] result = new float[w * h];
        double stepX = (double)photo.Width / w;
        double stepY = (double)photo.Height / h;
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)(y * stepY);
            int y1 = Math.Max(y0 + 1, Math.Min(photo.Height, (int)((y + 1) * stepY)));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x * stepX);
                int x1 = Math.Max(x0 + 1, Math.Min(photo.Width, (int)((x + 1) * stepX)));
                double sum = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int index = (yy * photo.Width + x0) * 4;
                    for (int xx = x0; xx < x1; xx++, index += 4)
                        sum += 0.114 * photo.Pixels[index] + 0.587 * photo.Pixels[index + 1] + 0.299 * photo.Pixels[index + 2];
                }

                result[y * w + x] = (float)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }

        return result;
    }

    private static float[] Blur(float[] source, int w, int h, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        float[] kernel = new float[radius * 2 + 1];
        float total = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma));
            total += kernel[i + radius];
        }

        for (int i = 0; i < kernel.Length; i++) kernel[i] /= total;
        float[] pass = new float[source.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] * source[y * w + Math.Clamp(x + k, 0, w - 1)];
                pass[y * w + x] = sum;
            }

        float[] result = new float[source.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] * pass[Math.Clamp(y + k, 0, h - 1) * w + x];
                result[y * w + x] = sum;
            }

        return result;
    }

    private static void Gradient(float[] gray, int w, int h, out float[] gx, out float[] gy, out float[] mag)
    {
        gx = new float[w * h];
        gy = new float[w * h];
        mag = new float[w * h];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float dx = gray[i - w + 1] + 2 * gray[i + 1] + gray[i + w + 1] - gray[i - w - 1] - 2 * gray[i - 1] - gray[i + w - 1];
                float dy = gray[i + w - 1] + 2 * gray[i + w] + gray[i + w + 1] - gray[i - w - 1] - 2 * gray[i - w] - gray[i - w + 1];
                gx[i] = dx;
                gy[i] = dy;
                mag[i] = MathF.Sqrt(dx * dx + dy * dy);
            }
    }

    // ---------------------------------------------------------------- the disc centre

    /// <summary>Hough voting: each strong edge pixel votes for the points a radius away along its gradient, both ways.</summary>
    private static bool FindCentre(float[] gx, float[] gy, float[] mag, int w, int h, int minRadius, int maxRadius,
        out double centreX, out double centreY)
    {
        centreX = centreY = 0;
        float[] sorted = (float[])mag.Clone();
        Array.Sort(sorted);
        float threshold = sorted[(int)(sorted.Length * 0.90)];
        if (threshold <= 0) return false; // a flat picture has no edges
        var edges = new List<int>();
        for (int i = 0; i < mag.Length; i++) if (mag[i] > threshold) edges.Add(i);

        float[] accumulator = new float[w * h];
        foreach (int i in edges)
        {
            float ux = gx[i] / mag[i];
            float uy = gy[i] / mag[i];
            int x = i % w;
            int y = i / w;
            for (int r = minRadius; r < maxRadius; r += 3)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int cx = (int)MathF.Round(x + sign * r * ux);
                    int cy = (int)MathF.Round(y + sign * r * uy);
                    if (cx >= 0 && cx < w && cy >= 0 && cy < h) accumulator[cy * w + cx] += 1;
                }
            }
        }

        float[] smooth = Blur(accumulator, w, h, 3);
        int best = 0;
        for (int i = 1; i < smooth.Length; i++) if (smooth[i] > smooth[best]) best = i;
        if (smooth[best] <= 0) return false;
        centreX = best % w;
        centreY = best / w;
        return true;
    }

    // ---------------------------------------------------------------- rays and circles

    private static float Sample(float[] gray, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1) return float.NaN;
        float fx = (float)(x - x0);
        float fy = (float)(y - y0);
        int i = y0 * w + x0;
        return (gray[i] * (1 - fx) + gray[i + 1] * fx) * (1 - fy) + (gray[i + w] * (1 - fx) + gray[i + w + 1] * fx) * fy;
    }

    /// <summary>
    /// Along one ray, the radius of the strongest brightness step between two radii. <paramref name="mode"/>: 1 = the
    /// strongest fall going outwards, -1 = the strongest rise, 0 = the strongest of either. Null when the ray leaves the
    /// photo or the step is too weak to mean anything.
    /// </summary>
    private static (double Radius, double Strength)? RayEdge(float[] gray, int w, int h, double cx, double cy, double angle,
        double lowRadius, double highRadius, int mode)
    {
        const double Step = 0.5;
        int count = (int)((highRadius - lowRadius) / Step);
        if (count < 6) return null;
        double[] value = new double[count];
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        for (int k = 0; k < count; k++)
        {
            float v = Sample(gray, w, h, cx + (lowRadius + k * Step) * cos, cy + (lowRadius + k * Step) * sin);
            if (float.IsNaN(v)) return null;
            value[k] = v;
        }

        double[] slope = new double[count];
        for (int k = 0; k < count; k++)
            slope[k] = (value[Math.Min(count - 1, k + 1)] - value[Math.Max(0, k - 1)]) / (k == 0 || k == count - 1 ? 1 : 2);
        slope = SmoothLine(slope, 1.0 / Step);
        int bestIndex = -1;
        double bestScore = 0;
        for (int k = 0; k < count; k++)
        {
            double score = mode switch { 1 => -slope[k], -1 => slope[k], _ => Math.Abs(slope[k]) };
            if (score > bestScore) { bestScore = score; bestIndex = k; }
        }

        if (bestIndex < 0 || bestScore < 0.15) return null;
        return (lowRadius + bestIndex * Step, bestScore);
    }

    private static double[] SmoothLine(double[] values, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        double[] kernel = new double[radius * 2 + 1];
        double total = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = Math.Exp(-0.5 * i * i / (sigma * sigma));
            total += kernel[i + radius];
        }

        double[] result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++) sum += kernel[k + radius] / total * values[Math.Clamp(i + k, 0, values.Length - 1)];
            result[i] = sum;
        }

        return result;
    }

    /// <summary>Algebraic least-squares circle through the points (Kasa fit).</summary>
    internal static bool TryFitCircle(IReadOnlyList<(double X, double Y)> points, out double cx, out double cy, out double radius)
    {
        cx = cy = radius = 0;
        if (points.Count < 3) return false;
        // Center coordinates before forming the normal equations so large offsets do not lose precision.
        double meanX = 0, meanY = 0;
        foreach ((double x, double y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
            meanX += x;
            meanY += y;
        }
        meanX /= points.Count;
        meanY /= points.Count;

        // Solve [2dx 2dy 1] * [lcx lcy c]^T = dx^2 + dy^2 by the normal equations.
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = points.Count, bx = 0, by = 0, b1 = 0;
        foreach ((double px, double py) in points)
        {
            double x = px - meanX, y = py - meanY;
            double a = 2 * x, b = 2 * y, rhs = x * x + y * y;
            sxx += a * a; sxy += a * b; sx += a; syy += b * b; sy += b;
            bx += a * rhs; by += b * rhs; b1 += rhs;
        }

        double[,] m = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        double[] v = { bx, by, b1 };
        double det = Det3(m);
        double traceScale = sxx + syy;
        if (!double.IsFinite(det) || Math.Abs(det) < Math.Max(1e-9, n * traceScale * traceScale * 1e-12)) return false;
        double[] solution = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double[,] t = (double[,])m.Clone();
            for (int r = 0; r < 3; r++) t[r, c] = v[r];
            solution[c] = Det3(t) / det;
        }

        double lcx = solution[0];
        double lcy = solution[1];
        double r2 = solution[2] + lcx * lcx + lcy * lcy;
        if (!double.IsFinite(lcx) || !double.IsFinite(lcy) || !double.IsFinite(r2) || r2 <= 0) return false;
        cx = meanX + lcx;
        cy = meanY + lcy;
        radius = Math.Sqrt(r2);
        return double.IsFinite(cx) && double.IsFinite(cy) && double.IsFinite(radius);
    }

    private static double Det3(double[,] m)
        => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
         - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
         + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    /// <summary>Fits a circle and drops points far from it until the rest agree.</summary>
    private static Circle? FitWithRejection(List<(double X, double Y)> points, double minSpread)
    {
        for (int round = 0; round < 6 && points.Count >= 8; round++)
        {
            if (!TryFitCircle(points, out double cx, out double cy, out double r)) return null;
            double[] residual = points.Select(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - r).ToArray();
            double mean = residual.Average();
            double sd = Math.Sqrt(residual.Select(e => (e - mean) * (e - mean)).Average());
            double limit = Math.Max(minSpread, 2.5 * sd);
            var kept = points.Where((p, i) => Math.Abs(residual[i]) < limit).ToList();
            if (kept.Count == points.Count) return new Circle(cx, cy, r, points);
            points = kept;
        }

        return null;
    }

    private static Circle? FitRim(float[] gray, int w, int h, double cx, double cy, double minRadius, double maxRadius, bool falling)
    {
        Circle? first = FitRimOnce(gray, w, h, cx, cy, minRadius, maxRadius, falling);
        if (first is null) return null;
        // Second pass: read every ray again, but only close to the rim just found, so a ray that was drawn to a
        // reflection or the table edge the first time finds the rim now.
        Circle? second = FitRimOnce(gray, w, h, first.CentreX, first.CentreY, first.Radius * 0.92, first.Radius * 1.08, falling);
        return second is not null && second.Points.Count >= first.Points.Count ? second : first;
    }

    private static Circle? FitRimOnce(float[] gray, int w, int h, double cx, double cy, double lowRadius, double highRadius, bool falling)
    {
        var points = new List<(double X, double Y)>();
        for (int k = 0; k < OuterRays; k++)
        {
            double angle = 2 * Math.PI * k / OuterRays;
            (double Radius, double Strength)? edge = RayEdge(gray, w, h, cx, cy, angle, lowRadius, highRadius, falling ? 1 : -1);
            if (edge is { } e) points.Add((cx + e.Radius * Math.Cos(angle), cy + e.Radius * Math.Sin(angle)));
        }

        return FitWithRejection(points, 2.0);
    }

    /// <summary>
    /// The visible hole edge: searched in a window around where a standard disc puts it (a bare 15 mm hole is 0.125 of
    /// the outer radius; a bevel or the clamping ring makes it look larger), over small shifts of the centre because
    /// the hole and the rim are not exactly concentric in a photo.
    /// </summary>
    private static Circle? FindHole(float[] gray, int w, int h, Circle outer)
    {
        double low = outer.Radius * 0.09;
        double high = outer.Radius * 0.22;
        double bestScore = 0;
        List<(double X, double Y)>? bestPoints = null;
        for (int dx = -8; dx <= 8; dx += 2)
            for (int dy = -8; dy <= 8; dy += 2)
            {
                double cx = outer.CentreX + dx;
                double cy = outer.CentreY + dy;
                var points = new List<(double X, double Y)>();
                double strength = 0;
                var radii = new List<double>();
                for (int k = 0; k < HoleRays; k++)
                {
                    double angle = 2 * Math.PI * k / HoleRays;
                    (double Radius, double Strength)? edge = RayEdge(gray, w, h, cx, cy, angle, low, high, 0);
                    if (edge is not { } e) continue;
                    points.Add((cx + e.Radius * Math.Cos(angle), cy + e.Radius * Math.Sin(angle)));
                    radii.Add(e.Radius);
                    strength += e.Strength;
                }

                if (points.Count < HoleRays / 2) continue;
                double meanRadius = radii.Average();
                double spread = Math.Sqrt(radii.Select(r => (r - meanRadius) * (r - meanRadius)).Average());
                double score = strength / points.Count / (1 + spread);
                if (score > bestScore) { bestScore = score; bestPoints = points; }
            }

        if (bestPoints is null) return null;
        Circle? fitted = FitWithRejection(bestPoints, 1.0);
        if (fitted is null || fitted.Points.Count < HoleRays / 2) return null;
        // A circle that is not roughly round or not near the rim's centre is a reflection, not the hole.
        double drift = Math.Sqrt(Math.Pow(fitted.CentreX - outer.CentreX, 2) + Math.Pow(fitted.CentreY - outer.CentreY, 2));
        return drift < outer.Radius * 0.06 ? fitted : null;
    }
}
