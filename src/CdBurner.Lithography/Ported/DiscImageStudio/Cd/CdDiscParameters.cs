// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Cd/CdDiscParameters.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Cd/CdDiscParameters.cs
// IgnisDisc changes: namespace; the cubic track-pitch coefficients (upstream DvdTrackGeometry) and the calibration
// target constructor are left out, so only the constant-pitch geometry is here. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Cd;

public sealed record CdDiscParameters(
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double LinearVelocityMmPerSecond = 1200.0,
    double StartAngleRadians = 0.0,
    double ImageOuterRadiusMm = 57.5,
    long? UsableSectors = null,
    double? SourceImageOuterRadiusMm = null)
{
    public const int BytesPerSector = 2352;

    /// <summary>ECMA-130 CLV scanning speed; the UI no longer exposes it as a parameter.</summary>
    public const double StandardLinearVelocityMmPerSecond = 1200.0;

    /// <summary>Radius on the source image canvas that maps to the disc outer edge.</summary>
    public const double StandardImageOuterRadiusMm = 57.5;

    public long OutputSectors => UsableSectors ?? Sectors;
    public long TotalBytes => checked(OutputSectors * BytesPerSector);
    public long GeometryBytes => checked(Sectors * BytesPerSector);
    public double OutputOuterRadiusMm => RadiusFromByte(TotalBytes);
    public double PreviewImageOuterRadiusMm => UsableSectors.HasValue ? OutputOuterRadiusMm : ImageOuterRadiusMm;
    public double SamplingImageOuterRadiusMm => SourceImageOuterRadiusMm ?? PreviewImageOuterRadiusMm;

    public double TotalTimeSeconds => Sectors / 75.0;

    private double RadiusSquaredRange =>
        (OuterRadiusMm * OuterRadiusMm) - (InnerRadiusMm * InnerRadiusMm);

    public void Validate()
    {
        if (Sectors <= 0 || OutputSectors <= 0 || OutputSectors > Sectors)
        {
            throw new ArgumentOutOfRangeException(nameof(Sectors), "CD sector count must be positive.");
        }

        if (!double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterRadiusMm),
                "CD outer radius must be greater than its positive inner radius.");
        }

        if (!double.IsFinite(LinearVelocityMmPerSecond) || LinearVelocityMmPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LinearVelocityMmPerSecond),
                "CD linear velocity must be positive.");
        }

        if (!double.IsFinite(StartAngleRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(StartAngleRadians));
        }

        if (!double.IsFinite(ImageOuterRadiusMm) || ImageOuterRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ImageOuterRadiusMm),
                "Image outer radius must be positive.");
        }

        if (SourceImageOuterRadiusMm is { } sourceRadius && (!double.IsFinite(sourceRadius) || sourceRadius <= 0))
            throw new ArgumentOutOfRangeException(nameof(SourceImageOuterRadiusMm), "Source image radius must be positive and finite.");
        _ = GeometryBytes;
        _ = TotalBytes;
    }

    public double RadiusFromByte(long globalByte)
    {
        double fraction = Math.Clamp((double)globalByte / GeometryBytes, 0.0, 1.0);
        return Math.Sqrt(
            (InnerRadiusMm * InnerRadiusMm) + (fraction * RadiusSquaredRange));
    }

    public double ThetaFromRadius(double radiusMm)
        => StartAngleRadians
            + ((2.0 * LinearVelocityMmPerSecond * TotalTimeSeconds / RadiusSquaredRange)
                * (radiusMm - InnerRadiusMm));

    public (double RadiusMm, double ThetaRadians) PolarFromByte(long globalByte)
    {
        double radius = RadiusFromByte(globalByte);
        return (radius, ThetaFromRadius(radius));
    }

    public (double X, double Y) ImagePointFromByte(long globalByte, int imageSize)
        => PointFromByte(globalByte, imageSize, PreviewImageOuterRadiusMm);

    public (double X, double Y) SourceImagePointFromByte(long globalByte, int imageSize)
        => PointFromByte(globalByte, imageSize, SamplingImageOuterRadiusMm);

    private (double X, double Y) PointFromByte(long globalByte, int imageSize, double imageOuterRadiusMm)
    {
        (double radius, double theta) = PolarFromByte(globalByte);
        double imageRadius = imageSize / 2.0;
        double pixelRadius = imageRadius * radius / imageOuterRadiusMm;
        double centre = imageSize / 2.0;
        return (
            centre + (pixelRadius * Math.Cos(theta)),
            centre + (pixelRadius * Math.Sin(theta)));
    }
}
