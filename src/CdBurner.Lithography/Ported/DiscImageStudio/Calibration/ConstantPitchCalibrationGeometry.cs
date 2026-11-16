// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/Calibration/ConstantPitchCalibrationGeometry.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/Calibration/ConstantPitchCalibrationGeometry.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Calibration;

/// <summary>Exact Archimedean arc length, independent of any media encoder.</summary>
internal sealed class ConstantPitchCalibrationGeometry
{
    private readonly CalibrationParameters _parameters;
    private readonly double _innerPrimitive;

    internal ConstantPitchCalibrationGeometry(CalibrationParameters parameters)
    {
        _parameters = parameters;
        double low = 1e-15, high = 1;
        double Length(double growth) => Primitive(parameters.OuterRadiusMm, growth)
            - Primitive(parameters.InnerRadiusMm, growth);
        while (Length(high) > parameters.TrackLengthMm) high *= 2;
        for (int i = 0; i < 100; i++)
        {
            double middle = (low + high) / 2;
            if (Length(middle) > parameters.TrackLengthMm) low = middle; else high = middle;
        }
        Growth = (low + high) / 2;
        _innerPrimitive = Primitive(parameters.InnerRadiusMm, Growth);
    }

    internal double Growth { get; }
    internal double ProgressAtRadius(double radius) => (Primitive(radius, Growth) - _innerPrimitive) / _parameters.TrackLengthMm;

    internal bool TryAtProgress(double progress, out double radius)
    {
        radius = double.NaN;
        double targetPrimitive = _innerPrimitive + progress * _parameters.TrackLengthMm;
        if (!double.IsFinite(targetPrimitive) || targetPrimitive <= 0) return false;
        double squared = _parameters.InnerRadiusMm * _parameters.InnerRadiusMm
            + 2 * Growth * progress * _parameters.TrackLengthMm;
        radius = Math.Sqrt(Math.Max(squared, Growth * Growth));
        for (int i = 0; i < (Growth <= 0.01 ? 2 : 12); i++)
        {
            double next = radius - (Primitive(radius, Growth) - targetPrimitive)
                * Growth / Math.Sqrt(radius * radius + Growth * Growth);
            radius = next > 0 ? next : radius / 2;
        }
        if (progress == 0) radius = _parameters.InnerRadiusMm;
        if (progress == 1) radius = _parameters.OuterRadiusMm;
        return double.IsFinite(radius) && radius > 0;
    }

    private static double Primitive(double radius, double growth)
        => (radius * Math.Sqrt(radius * radius + growth * growth)
            + growth * growth * Math.Asinh(radius / growth)) / (2 * growth);
}
