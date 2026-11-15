using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.Tests.Presets;

public sealed class DiscPresetSpiralAgreementTests
{
    private const double OneNanometerMeters = 1.0e-9;
    private const double AngleToleranceRadians = 1.0e-6;
    private const double StartAngleRadians = 0.25;

    [Fact]
    public void AllBuiltInPresets_AgreeWithUpstreamExactArcSpiralWithin1NmAnd1MicroRad()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        int presetCount = builtIn.CdPresets.Count + builtIn.DvdPresets.Count + builtIn.BluRayPresets!.Count;
        Assert.Equal(17, presetCount);

        foreach (CdDiscPresetDefinition preset in builtIn.CdPresets)
        {
            SpiralGeometry ignis = DiscPresetGeometry.FromCdPreset(preset, lbaAtStartRadius: 0, startAngleRadians: StartAngleRadians);
            double nominalLengthMm = preset.Sectors * (DiscPresetGeometry.CdNominalSectorLengthMeters * 1_000.0);
            var upstream = UpstreamExactSpiralReference.Create(
                preset.InnerRadiusMm,
                preset.OuterRadiusMm,
                nominalLengthMm,
                preset.PitchLinear,
                preset.PitchQuadratic,
                preset.PitchCubic);

            AssertSpiralAgreement(preset.Id, ignis, upstream, preset.Sectors);
        }

        foreach (DvdDiscPresetDefinition preset in builtIn.DvdPresets)
        {
            SpiralGeometry ignis = DiscPresetGeometry.FromDvdPreset(preset, lbaAtStartRadius: 0, startAngleRadians: StartAngleRadians);
            double nominalLengthMm = preset.TotalSectors * (DiscPresetGeometry.DvdNominalSectorLengthMeters * 1_000.0);
            var upstream = UpstreamExactSpiralReference.Create(
                preset.InnerRadiusMm,
                preset.OuterRadiusMm,
                nominalLengthMm,
                preset.PitchLinear,
                preset.PitchQuadratic,
                preset.PitchCubic);

            AssertSpiralAgreement(preset.Id, ignis, upstream, preset.TotalSectors);
        }

        foreach (BluRayDiscPresetDefinition preset in builtIn.BluRayPresets!)
        {
            SpiralGeometry ignis = DiscPresetGeometry.FromBluRayPreset(preset, lbaAtStartRadius: 0, startAngleRadians: StartAngleRadians);
            double clusters = preset.TotalSectors / (double)DiscPresetJsonStore.BluRaySectorsPerCluster;
            double nominalLengthMm = clusters
                * DiscPresetGeometry.BluRayChannelBitsPerRecordingUnit
                * (DiscPresetGeometry.BluRayChannelBitLengthMeters * 1_000.0);
            var upstream = UpstreamExactSpiralReference.Create(
                preset.InnerRadiusMm,
                preset.OuterRadiusMm,
                nominalLengthMm,
                preset.PitchLinear,
                preset.PitchQuadratic,
                preset.PitchCubic);

            AssertSpiralAgreement(preset.Id, ignis, upstream, preset.TotalSectors);
        }
    }

    [Fact]
    public void DiscPresetGeometry_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiscPresetGeometry.ValidateNormalizedPitchCoefficients(double.NaN, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiscPresetGeometry.ValidateNormalizedPitchCoefficients(-1.0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiscPresetGeometry.CreatePitchModel(58.0, 24.0, 100.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiscPresetGeometry.CreatePitchModel(24.0, 58.0, 0.030));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiscPresetGeometry.FromBluRayPreset(
                new BluRayDiscPresetDefinition("bad-bd", "BD", "Unaligned", 1000, 24.0, 58.0)));
    }

    private static void AssertSpiralAgreement(
        string presetId,
        SpiralGeometry ignis,
        UpstreamExactSpiralReference upstream,
        long totalSectors)
    {
        // End of the last sector must never exceed TotalTrackLengthMeters due to 1-ULP division rounding.
        DiscPosition endPos = ignis.PositionAtLba(totalSectors - 1, 1.0);
        Assert.InRange(endPos.RadiusMeters, ignis.StartRadiusMeters, ignis.MaxRadiusMeters);

        const int sampleCount = 1001;
        double maxRadiusDiff = 0.0;
        double maxAngleDiff = 0.0;

        for (int i = 0; i < sampleCount; i++)
        {
            double u = i / (double)(sampleCount - 1);
            double ignisArc = u * ignis.TotalTrackLengthMeters;
            DiscPosition ignisPos = ignis.PositionAtTrackLength(ignisArc);
            (double upstreamRadiusMm, double upstreamDeltaTheta) = upstream.AtProgress(u);

            double radiusDiff = Math.Abs(ignisPos.RadiusMeters - (upstreamRadiusMm * 1.0e-3));
            double ignisDeltaTheta = StartAngleRadians - ignisPos.AngleRadians;
            double angleDiff = Math.Abs(ignisDeltaTheta - upstreamDeltaTheta);

            maxRadiusDiff = Math.Max(maxRadiusDiff, radiusDiff);
            maxAngleDiff = Math.Max(maxAngleDiff, angleDiff);
        }

        Assert.True(
            maxRadiusDiff <= OneNanometerMeters,
            $"Preset '{presetId}' radius drift {maxRadiusDiff:E3} m exceeded {OneNanometerMeters:E3} m.");
        Assert.True(
            maxAngleDiff <= AngleToleranceRadians,
            $"Preset '{presetId}' angle drift {maxAngleDiff:E3} rad exceeded {AngleToleranceRadians:E3} rad.");
    }

    /// <summary>
    /// Test-only independent implementation of upstream's exact arc-length spiral
    /// (<c>ds = sqrt(1 + (r / (b * S(x)))^2) dr</c>, adaptive 8-point Gauss-Legendre quadrature + Hermite segments)
    /// used strictly to cross-check IgnisDisc's unified <see cref="SpiralGeometry"/>.
    /// </summary>
    private sealed class UpstreamExactSpiralReference
    {
        private readonly double _innerRadiusMm;
        private readonly double _outerRadiusMm;
        private readonly double _trackLengthMm;
        private readonly double _range;
        private readonly double _linear;
        private readonly double _quadratic;
        private readonly double _cubic;
        private readonly bool _isConstant;
        private readonly double _b;
        private readonly double _innerPrimitive;
        private readonly double _totalAngle;
        private readonly double _lengthScale;
        private readonly Segment[] _segments;

        private static readonly double[] Nodes =
            [0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363];
        private static readonly double[] Weights =
            [0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763];

        private UpstreamExactSpiralReference(
            double innerRadiusMm,
            double outerRadiusMm,
            double trackLengthMm,
            double linear,
            double quadratic,
            double cubic)
        {
            _innerRadiusMm = innerRadiusMm;
            _outerRadiusMm = outerRadiusMm;
            _trackLengthMm = trackLengthMm;
            _range = outerRadiusMm - innerRadiusMm;
            _linear = linear;
            _quadratic = quadratic;
            _cubic = cubic;
            _isConstant = linear == 0 && quadratic == 0 && cubic == 0;

            if (_isConstant)
            {
                double lo = 1e-15;
                double hi = Math.Max(outerRadiusMm, 1.0);
                while (ConstantArc(innerRadiusMm, outerRadiusMm, hi) > trackLengthMm)
                {
                    hi *= 2;
                }
                for (int i = 0; i < 120; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (ConstantArc(innerRadiusMm, outerRadiusMm, mid) > trackLengthMm)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                _b = 0.5 * (lo + hi);
                _innerPrimitive = Primitive(innerRadiusMm, _b);
                _totalAngle = _range / _b;
                _lengthScale = 1.0;
                _segments = [];
                return;
            }

            double thinLength = Integrate(innerRadiusMm, outerRadiusMm, 1.0, thin: true).Length;
            double lower = 0.0;
            double upper = trackLengthMm / thinLength;
            double inverseGrowth = upper;
            for (int iteration = 0; iteration < 80; iteration++)
            {
                Integral value = Integrate(innerRadiusMm, outerRadiusMm, inverseGrowth);
                double error = value.Length - trackLengthMm;
                if (Math.Abs(error) <= trackLengthMm * 2e-15)
                {
                    break;
                }
                if (error > 0)
                {
                    upper = inverseGrowth;
                }
                else
                {
                    lower = inverseGrowth;
                }
                double next = inverseGrowth - (error / value.Derivative);
                if (!double.IsFinite(next) || next <= lower || next >= upper)
                {
                    next = lower + 0.5 * (upper - lower);
                }
                if (next == inverseGrowth)
                {
                    break;
                }
                inverseGrowth = next;
            }

            _b = 1.0 / inverseGrowth;
            List<Segment> segments = [];
            BuildSegments(innerRadiusMm, outerRadiusMm, inverseGrowth, segments, 0);
            _segments = [.. segments];
            double arc = 0, angle = 0, arcCarry = 0, angleCarry = 0;
            for (int i = 0; i < _segments.Length; i++)
            {
                Segment seg = _segments[i];
                _segments[i] = seg with { ArcStart = arc, AngleStart = angle };
                AddCompensated(ref arc, ref arcCarry, seg.ArcDelta);
                AddCompensated(ref angle, ref angleCarry, seg.AngleDelta);
            }
            _lengthScale = trackLengthMm / arc;
            _totalAngle = angle;
        }

        public static UpstreamExactSpiralReference Create(
            double innerRadiusMm,
            double outerRadiusMm,
            double trackLengthMm,
            double linear,
            double quadratic,
            double cubic)
            => new(innerRadiusMm, outerRadiusMm, trackLengthMm, linear, quadratic, cubic);

        public (double RadiusMm, double DeltaAngleRadians) AtProgress(double u)
        {
            double arcLengthMm = u * _trackLengthMm;
            if (_isConstant)
            {
                if (u <= 0)
                {
                    return (_innerRadiusMm, 0);
                }
                if (u >= 1)
                {
                    return (_outerRadiusMm, _totalAngle);
                }

                double radius = Math.Clamp(
                    Math.Sqrt(_innerRadiusMm * _innerRadiusMm + 2 * _b * arcLengthMm),
                    _innerRadiusMm,
                    _outerRadiusMm);
                for (int i = 0; i < 5; i++)
                {
                    double error = (Primitive(radius, _b) - _innerPrimitive) / _b - arcLengthMm;
                    double derivative = Math.Sqrt(radius * radius + _b * _b) / _b;
                    radius = Math.Clamp(radius - error / derivative, _innerRadiusMm, _outerRadiusMm);
                }
                return (radius, (radius - _innerRadiusMm) / _b);
            }

            if (arcLengthMm <= 0)
            {
                return (_innerRadiusMm, 0);
            }
            if (arcLengthMm >= _trackLengthMm)
            {
                return (_outerRadiusMm, _totalAngle);
            }

            double unscaled = arcLengthMm / _lengthScale;
            int low = 0, high = _segments.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_segments[mid].ArcStart <= unscaled)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }
            Segment segment = _segments[low];
            double t = InvertHermite(
                unscaled - segment.ArcStart,
                segment.ArcDelta,
                segment.Width * segment.ArcDerivativeStart,
                segment.Width * segment.ArcDerivativeEnd);
            return (
                segment.RadiusStart + segment.Width * t,
                segment.AngleStart + Hermite(t, segment.AngleDelta, segment.Width * segment.AngleDerivativeStart, segment.Width * segment.AngleDerivativeEnd));
        }

        private double Shape(double radius)
        {
            double x = (radius - _innerRadiusMm) / _range;
            return ((_cubic * x + _quadratic) * x + _linear) * x + 1.0;
        }

        private Integral Integrate(double first, double last, double inverseGrowth, bool thin = false)
            => IntegrateAdaptive(first, last, inverseGrowth, thin, 0);

        private Integral IntegrateAdaptive(double first, double last, double inverseGrowth, bool thin, int depth)
        {
            Integral whole = Quadrature(first, last, inverseGrowth, thin);
            double middle = first + 0.5 * (last - first);
            Integral left = Quadrature(first, middle, inverseGrowth, thin);
            Integral right = Quadrature(middle, last, inverseGrowth, thin);
            Integral split = left + right;
            if ((Math.Abs(split.Length - whole.Length) <= Math.Abs(split.Length) * 8e-15
                    && Math.Abs(split.Angle - whole.Angle) <= Math.Abs(split.Angle) * 8e-15)
                || depth >= 24 || middle == first || middle == last)
            {
                return split;
            }
            return IntegrateAdaptive(first, middle, inverseGrowth, thin, depth + 1)
                + IntegrateAdaptive(middle, last, inverseGrowth, thin, depth + 1);
        }

        private Integral Quadrature(double first, double last, double inverseGrowth, bool thin = false)
        {
            double middle = first + 0.5 * (last - first);
            double half = 0.5 * (last - first);
            double length = 0, angle = 0, derivative = 0;
            for (int i = 0; i < Nodes.Length; i++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    double radius = middle + sign * half * Nodes[i];
                    double inverseShape = 1.0 / Shape(radius);
                    double radial = radius * inverseShape;
                    double z = inverseGrowth * radial;
                    double ds = thin ? radial : Hypot(1.0, z);
                    length += Weights[i] * ds;
                    angle += Weights[i] * inverseShape * inverseGrowth;
                    if (!thin)
                    {
                        derivative += Weights[i] * radial * (z / ds);
                    }
                }
            }
            return new Integral(half * length, half * angle, half * derivative);
        }

        private void BuildSegments(double first, double last, double inverseGrowth, List<Segment> segments, int depth)
        {
            double width = last - first;
            double middle = first + 0.5 * width;
            Integral whole = Quadrature(first, last, inverseGrowth);
            Integral left = Quadrature(first, middle, inverseGrowth);
            Integral right = Quadrature(middle, last, inverseGrowth);
            Integral split = left + right;
            double a0 = inverseGrowth / Shape(first), a1 = inverseGrowth / Shape(last);
            double s0 = Hypot(1.0, first * a0), s1 = Hypot(1.0, last * a1);
            const double angleTolerance = 5e-10;
            double lengthTolerance = angleTolerance * _innerRadiusMm;
            bool accepted = Math.Abs(split.Angle - whole.Angle) < angleTolerance / 16
                && Math.Abs(split.Length - whole.Length) < lengthTolerance / 16;
            for (int i = 1; accepted && i <= 3; i++)
            {
                double t = i / 4.0;
                Integral actual = i == 2 ? left : Quadrature(first, first + width * t, inverseGrowth);
                accepted = Math.Abs(Hermite(t, split.Angle, width * a0, width * a1) - actual.Angle) < angleTolerance
                    && Math.Abs(Hermite(t, split.Length, width * s0, width * s1) - actual.Length) < lengthTolerance;
            }
            if (!accepted && depth < 24 && middle != first && middle != last)
            {
                BuildSegments(first, middle, inverseGrowth, segments, depth + 1);
                BuildSegments(middle, last, inverseGrowth, segments, depth + 1);
                return;
            }
            segments.Add(new Segment(first, width, 0, 0, split.Length, split.Angle, s0, s1, a0, a1));
        }

        private static double Hermite(double t, double delta, double d0, double d1)
            => t * (d0 + t * (3 * delta - 2 * d0 - d1 + t * (d0 + d1 - 2 * delta)));

        private static double InvertHermite(double value, double delta, double d0, double d1)
        {
            double t = Math.Clamp(value / delta, 0, 1);
            double q = 3 * delta - 2 * d0 - d1;
            double c = d0 + d1 - 2 * delta;
            for (int i = 0; i < 4; i++)
            {
                double error = t * (d0 + t * (q + t * c)) - value;
                double derivative = d0 + t * (2 * q + 3 * t * c);
                t = Math.Clamp(t - error / derivative, 0, 1);
            }
            return t;
        }

        private static double ConstantArc(double r0, double r1, double b)
            => (Primitive(r1, b) - Primitive(r0, b)) / b;

        private static double Primitive(double r, double b)
            => 0.5 * (r * Math.Sqrt(r * r + b * b) + b * b * Math.Asinh(r / b));

        private static double Hypot(double a, double b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            double max = Math.Max(a, b);
            if (max == 0)
            {
                return 0;
            }
            double ratio = Math.Min(a, b) / max;
            return max * Math.Sqrt(1 + ratio * ratio);
        }

        private static void AddCompensated(ref double sum, ref double carry, double value)
        {
            double adjusted = value - carry;
            double next = sum + adjusted;
            carry = (next - sum) - adjusted;
            sum = next;
        }

        private readonly record struct Integral(double Length, double Angle, double Derivative)
        {
            public static Integral operator +(Integral a, Integral b)
                => new(a.Length + b.Length, a.Angle + b.Angle, a.Derivative + b.Derivative);
        }

        private readonly record struct Segment(
            double RadiusStart,
            double Width,
            double ArcStart,
            double AngleStart,
            double ArcDelta,
            double AngleDelta,
            double ArcDerivativeStart,
            double ArcDerivativeEnd,
            double AngleDerivativeStart,
            double AngleDerivativeEnd);
    }
}
