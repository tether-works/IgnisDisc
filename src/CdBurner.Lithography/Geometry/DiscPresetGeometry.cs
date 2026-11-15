using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.Geometry;

/// <summary>
/// Converts disc preset parameters (radii in millimetres, nominal track length in metres, and optional
/// normalized cubic pitch coefficients) into IgnisDisc's unified <see cref="SpiralGeometry"/> and
/// <see cref="ITrackPitchModel"/> representation.
/// </summary>
public static class DiscPresetGeometry
{
    /// <summary>Nominal CD sector track length in metres (ECMA-130: 1.2 m/s / 75 sectors/s).</summary>
    public const double CdNominalSectorLengthMeters = 1.2 / 75.0;

    /// <summary>Nominal DVD sector track length in metres (ECMA-267: 26 * 1488 * 133.3 nm).</summary>
    public const double DvdNominalSectorLengthMeters = 26.0 * 1488.0 * 133.3e-9;

    /// <summary>Blu-ray channel bits per 32-sector Recording Unit Block, including run-in and run-out (ISO/IEC 30190 §14).</summary>
    public const long BluRayChannelBitsPerRecordingUnit = 2760L + (496L * 1932L) + 1104L;

    /// <summary>Nominal Blu-ray channel bit length in metres (ISO/IEC 30190: 74.5 nm).</summary>
    public const double BluRayChannelBitLengthMeters = 74.5e-9;

    private const int QuadraturePanels = 64;

    // Positive half of the 8-point Gauss-Legendre rule on [-1, 1].
    private static readonly double[] GaussNodes =
        [0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363];
    private static readonly double[] GaussWeights =
        [0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763];

    /// <summary>
    /// Validates that normalized cubic pitch coefficients are finite and that
    /// <c>S(x) = 1 + linear*x + quadratic*x^2 + cubic*x^3 &gt; 0</c> everywhere on <c>[0, 1]</c>.
    /// </summary>
    public static void ValidateNormalizedPitchCoefficients(double linear, double quadratic, double cubic)
    {
        if (!double.IsFinite(linear))
        {
            throw new ArgumentOutOfRangeException(nameof(linear), linear, "Pitch linear coefficient must be finite.");
        }
        if (!double.IsFinite(quadratic))
        {
            throw new ArgumentOutOfRangeException(nameof(quadratic), quadratic, "Pitch quadratic coefficient must be finite.");
        }
        if (!double.IsFinite(cubic))
        {
            throw new ArgumentOutOfRangeException(nameof(cubic), cubic, "Pitch cubic coefficient must be finite.");
        }

        if (!IsPositiveShapeOnUnitInterval(linear, quadratic, cubic))
        {
            throw new ArgumentOutOfRangeException(
                nameof(linear),
                "Normalized pitch shape 1 + c1*x + c2*x^2 + c3*x^3 must stay positive for 0 <= x <= 1.");
        }
    }

    /// <summary>
    /// Solves the base radial growth <c>b</c> (and base pitch <c>p0 = 2*pi*b</c>) from the exact arc-length
    /// integral over <c>[innerRadiusMm, outerRadiusMm]</c> and returns a <see cref="ConstantTrackPitch"/> or
    /// <see cref="CubicTrackPitch"/> in SI metres.
    /// </summary>
    public static ITrackPitchModel CreatePitchModel(
        double innerRadiusMm,
        double outerRadiusMm,
        double nominalTrackLengthMeters,
        double pitchLinear = 0.0,
        double pitchQuadratic = 0.0,
        double pitchCubic = 0.0)
    {
        if (!double.IsFinite(innerRadiusMm) || innerRadiusMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(innerRadiusMm), innerRadiusMm,
                "Inner radius must be finite and greater than zero (mm).");
        }
        if (!double.IsFinite(outerRadiusMm) || outerRadiusMm <= innerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), outerRadiusMm,
                "Outer radius must be finite and greater than inner radius (mm).");
        }

        double r0 = innerRadiusMm * 1.0e-3;
        double r1 = outerRadiusMm * 1.0e-3;
        double span = r1 - r0;
        if (!double.IsFinite(r0) || !double.IsFinite(r1) || span <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), outerRadiusMm,
                "Radius span in metres must be finite and greater than zero.");
        }
        if (!double.IsFinite(nominalTrackLengthMeters) || nominalTrackLengthMeters <= span)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalTrackLengthMeters), nominalTrackLengthMeters,
                "Nominal track length must be finite and exceed the radial span.");
        }

        ValidateNormalizedPitchCoefficients(pitchLinear, pitchQuadratic, pitchCubic);

        if (pitchLinear == 0.0 && pitchQuadratic == 0.0 && pitchCubic == 0.0)
        {
            double low = 1.0e-18;
            double high = Math.Max(r1, 1.0e-3);
            while (ConstantArcLength(r0, r1, high) > nominalTrackLengthMeters)
            {
                high *= 2.0;
                if (!double.IsFinite(high))
                {
                    throw new ArgumentOutOfRangeException(nameof(nominalTrackLengthMeters), nominalTrackLengthMeters,
                        "Could not bracket constant track pitch for the requested track length.");
                }
            }

            for (int iteration = 0; iteration < 120; iteration++)
            {
                double middle = 0.5 * (low + high);
                if (ConstantArcLength(r0, r1, middle) > nominalTrackLengthMeters)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            double b = 0.5 * (low + high);
            return new ConstantTrackPitch(2.0 * Math.PI * b);
        }

        double thinLength = IntegrateThinLength(r0, r1, span, pitchLinear, pitchQuadratic, pitchCubic);
        double lower = 0.0;
        double upper = nominalTrackLengthMeters / thinLength;
        double k = upper;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            IntegrateExactLength(
                r0,
                r1,
                span,
                pitchLinear,
                pitchQuadratic,
                pitchCubic,
                k,
                out double length,
                out double derivative);
            double error = length - nominalTrackLengthMeters;
            if (Math.Abs(error) <= nominalTrackLengthMeters * 2.0e-15)
            {
                break;
            }
            if (error > 0.0)
            {
                upper = k;
            }
            else
            {
                lower = k;
            }

            double next = k - (error / derivative);
            if (!double.IsFinite(next) || next <= lower || next >= upper)
            {
                next = lower + 0.5 * (upper - lower);
            }
            if (next == k)
            {
                break;
            }
            k = next;
        }

        double growth = 1.0 / k;
        if (!double.IsFinite(growth) || growth <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nominalTrackLengthMeters), nominalTrackLengthMeters,
                "The pitch coefficients cannot be resolved at this track length.");
        }

        double p0 = 2.0 * Math.PI * growth;
        return new CubicTrackPitch(
            a0: p0,
            a1: p0 * pitchLinear / span,
            a2: p0 * pitchQuadratic / (span * span),
            a3: p0 * pitchCubic / (span * span * span),
            referenceRadiusMeters: r0,
            minRadiusMeters: r0,
            maxRadiusMeters: r1);
    }

    /// <summary>Creates a <see cref="SpiralGeometry"/> spanning <paramref name="totalSectors"/> across the preset radii.</summary>
    public static SpiralGeometry CreateSpiral(
        double innerRadiusMm,
        double outerRadiusMm,
        double nominalTrackLengthMeters,
        long totalSectors,
        double pitchLinear = 0.0,
        double pitchQuadratic = 0.0,
        double pitchCubic = 0.0,
        long lbaAtStartRadius = 0,
        double startAngleRadians = 0.0)
    {
        ITrackPitchModel pitch = CreatePitchModel(
            innerRadiusMm,
            outerRadiusMm,
            nominalTrackLengthMeters,
            pitchLinear,
            pitchQuadratic,
            pitchCubic);

        return SpiralGeometry.CreateSpanningSectors(
            pitch,
            innerRadiusMm * 1.0e-3,
            outerRadiusMm * 1.0e-3,
            totalSectors,
            lbaAtStartRadius,
            startAngleRadians);
    }

    /// <summary>Creates a <see cref="SpiralGeometry"/> from a CD preset definition.</summary>
    public static SpiralGeometry FromCdPreset(
        CdDiscPresetDefinition preset,
        long lbaAtStartRadius = 0,
        double startAngleRadians = 0.0)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Sectors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset.Sectors,
                "CD preset sectors must be greater than zero.");
        }

        double nominalLength = preset.Sectors * CdNominalSectorLengthMeters;
        return CreateSpiral(
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            nominalLength,
            preset.Sectors,
            preset.PitchLinear,
            preset.PitchQuadratic,
            preset.PitchCubic,
            lbaAtStartRadius,
            startAngleRadians);
    }

    /// <summary>Creates a <see cref="SpiralGeometry"/> from a DVD preset definition.</summary>
    public static SpiralGeometry FromDvdPreset(
        DvdDiscPresetDefinition preset,
        long lbaAtStartRadius = 0,
        double startAngleRadians = 0.0)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.TotalSectors == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset.TotalSectors,
                "DVD preset totalSectors must be greater than zero.");
        }

        double nominalLength = preset.TotalSectors * DvdNominalSectorLengthMeters;
        return CreateSpiral(
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            nominalLength,
            preset.TotalSectors,
            preset.PitchLinear,
            preset.PitchQuadratic,
            preset.PitchCubic,
            lbaAtStartRadius,
            startAngleRadians);
    }

    /// <summary>Creates a <see cref="SpiralGeometry"/> from a Blu-ray preset definition.</summary>
    public static SpiralGeometry FromBluRayPreset(
        BluRayDiscPresetDefinition preset,
        long lbaAtStartRadius = 0,
        double startAngleRadians = 0.0)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.TotalSectors == 0 || preset.TotalSectors % DiscPresetJsonStore.BluRaySectorsPerCluster != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset.TotalSectors,
                $"Blu-ray preset totalSectors must be a positive multiple of {DiscPresetJsonStore.BluRaySectorsPerCluster}.");
        }

        long clusters = preset.TotalSectors / DiscPresetJsonStore.BluRaySectorsPerCluster;
        double nominalLength = clusters * (double)BluRayChannelBitsPerRecordingUnit * BluRayChannelBitLengthMeters;
        return CreateSpiral(
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            nominalLength,
            preset.TotalSectors,
            preset.PitchLinear,
            preset.PitchQuadratic,
            preset.PitchCubic,
            lbaAtStartRadius,
            startAngleRadians);
    }

    private static bool IsPositiveShapeOnUnitInterval(double c1, double c2, double c3)
    {
        bool PositiveAt(double x)
        {
            double value = EvaluateShape(c1, c2, c3, x);
            return double.IsFinite(value) && value > 0.0;
        }

        if (!PositiveAt(0.0) || !PositiveAt(1.0))
        {
            return false;
        }

        double scale = Math.Max(Math.Abs(c1), Math.Max(Math.Abs(c2), Math.Abs(c3)));
        if (scale == 0.0)
        {
            return true;
        }

        // Scale derivative coefficients before computing discriminant to avoid overflow.
        double a = 3.0 * (c3 / scale);
        double b = 2.0 * (c2 / scale);
        double c = c1 / scale;

        bool CheckRoot(double x) => !(x > 0.0 && x < 1.0) || PositiveAt(x);

        if (a == 0.0)
        {
            return b == 0.0 || CheckRoot(-c / b);
        }

        double discriminant = (b * b) - (4.0 * a * c);
        if (discriminant < 0.0)
        {
            return true;
        }

        double q = -0.5 * (b + Math.CopySign(Math.Sqrt(discriminant), b));
        if (q == 0.0)
        {
            return CheckRoot(-b / (2.0 * a));
        }

        return CheckRoot(q / a) && CheckRoot(c / q);
    }

    private static double EvaluateShape(double c1, double c2, double c3, double x)
        => ((c3 * x + c2) * x + c1) * x + 1.0;

    private static double IntegrateThinLength(double r0, double r1, double span, double c1, double c2, double c3)
    {
        double panelWidth = span / QuadraturePanels;
        double halfPanel = 0.5 * panelWidth;
        double sum = 0.0;

        for (int panel = 0; panel < QuadraturePanels; panel++)
        {
            double center = r0 + (panel + 0.5) * panelWidth;
            double panelSum = 0.0;
            for (int i = 0; i < GaussNodes.Length; i++)
            {
                double offset = halfPanel * GaussNodes[i];
                double rMinus = center - offset;
                double rPlus = center + offset;
                double sMinus = EvaluateShape(c1, c2, c3, (rMinus - r0) / span);
                double sPlus = EvaluateShape(c1, c2, c3, (rPlus - r0) / span);
                panelSum += GaussWeights[i] * ((rMinus / sMinus) + (rPlus / sPlus));
            }
            sum += halfPanel * panelSum;
        }

        return sum;
    }

    private static void IntegrateExactLength(
        double r0,
        double r1,
        double span,
        double c1,
        double c2,
        double c3,
        double k,
        out double length,
        out double derivative)
    {
        double panelWidth = (r1 - r0) / QuadraturePanels;
        double halfPanel = 0.5 * panelWidth;
        double totalLength = 0.0;
        double totalDerivative = 0.0;

        for (int panel = 0; panel < QuadraturePanels; panel++)
        {
            double center = r0 + (panel + 0.5) * panelWidth;
            double panelLength = 0.0;
            double panelDerivative = 0.0;
            for (int i = 0; i < GaussNodes.Length; i++)
            {
                double offset = halfPanel * GaussNodes[i];
                EvaluateIntegrand(center - offset, r0, span, c1, c2, c3, k, out double dsMinus, out double dDkMinus);
                EvaluateIntegrand(center + offset, r0, span, c1, c2, c3, k, out double dsPlus, out double dDkPlus);
                panelLength += GaussWeights[i] * (dsMinus + dsPlus);
                panelDerivative += GaussWeights[i] * (dDkMinus + dDkPlus);
            }
            totalLength += halfPanel * panelLength;
            totalDerivative += halfPanel * panelDerivative;
        }

        length = totalLength;
        derivative = totalDerivative;
    }

    private static void EvaluateIntegrand(
        double r,
        double r0,
        double span,
        double c1,
        double c2,
        double c3,
        double k,
        out double ds,
        out double dDsDk)
    {
        double radial = r / EvaluateShape(c1, c2, c3, (r - r0) / span);
        double z = k * radial;
        ds = Hypot(1.0, z);
        dDsDk = radial * (z / ds);
    }

    private static double ConstantArcLength(double r0, double r1, double b)
        => (ConstantPrimitive(r1, b) - ConstantPrimitive(r0, b)) / b;

    private static double ConstantPrimitive(double r, double b)
        => 0.5 * (r * Hypot(r, b) + (b * b * Math.Asinh(r / b)));

    private static double Hypot(double a, double b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        double max = Math.Max(a, b);
        if (max == 0.0)
        {
            return 0.0;
        }
        double ratio = Math.Min(a, b) / max;
        return max * Math.Sqrt(1.0 + (ratio * ratio));
    }
}
