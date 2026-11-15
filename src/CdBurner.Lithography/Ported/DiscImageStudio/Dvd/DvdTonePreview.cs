// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Dvd/DvdTonePreview.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Dvd/DvdTonePreview.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Dvd.Solver;

namespace CdBurner.Lithography.Dvd;

/// <summary>
/// Source-side preview of the dispersion tone mapping. The generated geometry doubles as the
/// measured geometry, so the picture shows what the writer will lay down without reading an
/// ISO first; the read-back simulation stays the authoritative check.
/// </summary>
public static class DvdTonePreview
{
    public static CalibrationPreviewSummary Render(
        string imagePath,
        string outputPath,
        DvdStreamingOptions options,
        int previewSize,
        int samplesPerSector,
        CancellationToken cancellationToken = default,
        uint drawingStartLba = 0,
        uint? drawingEndLbaExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        uint drawingEnd = drawingEndLbaExclusive ?? options.OutputSectors;
        if (drawingStartLba >= drawingEnd || drawingEnd > options.OutputSectors)
            throw new ArgumentOutOfRangeException(nameof(drawingEndLbaExclusive),
                "The drawing range must lie inside the disc.");
        CalibrationPreviewOptions preview = new(
            options.TotalSectors,
            drawingStartLba,
            drawingEnd - drawingStartLba,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.ChannelBitLengthNm,
            Clockwise: true,
            options.LuminanceThreshold,
            options.AlphaThreshold,
            previewSize,
            samplesPerSector,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic,
            options.PitchLinear,
            options.PitchQuadratic,
            options.PitchCubic,
            options.Tone,
            SourceImageOuterRadiusMm: options.SamplingImageOuterRadiusMm,
            PreviewOuterRadiusMm: options.OutputOuterRadiusMm);
        return CalibrationPreviewRenderer.Render(
            imagePath,
            outputPath,
            preview,
            cancellationToken);
    }
}
