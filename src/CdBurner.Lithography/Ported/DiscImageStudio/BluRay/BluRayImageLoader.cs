// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/Shared/BluRayImageLoader.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/Shared/BluRayImageLoader.cs
// IgnisDisc changes: namespace; pictures are decoded by IgnisDisc's own PNG reader (Imaging/PngRaster.cs) instead of
// upstream's ImageIO project, and can also be passed in as a BGRA raster; the header-only source check is left out
// because the PNG reader applies its own size limits. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.BluRay;

/// <summary>Loads an image as Gray8 without choosing a Blu-ray intensity-to-byte mapping.</summary>
public static class BluRayImageLoader
{
    /// <summary>Uses encoded-space Rec. 709 weights and composites straight alpha over
    /// white. Without a tone profile the conversion preserves source dimensions and performs
    /// no thresholding, dithering, ICC conversion or optical-reflectance calibration.
    /// A tone profile is applied here, at source resolution: BD payload positions are far
    /// denser than any practical raster, so dithering per byte is neither needed nor
    /// reproducible.</summary>
    public static BluRayGray8Raster Load(string path, CancellationToken cancellationToken = default)
        => Load(path, tone: null, cancellationToken);

    public static BluRayGray8Raster Load(string path, ToneProfile? tone,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return FromRaster(PngRaster.Load(path), tone, cancellationToken);
    }

    public static BluRayGray8Raster FromRaster(BgraRaster frame, ToneProfile? tone = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();
        int width = frame.Width, height = frame.Height;
        byte[] bgra = frame.Pixels;
        byte[] gray = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x;
                int offset = pixel * 4;
                int weighted = 2126 * bgra[offset + 2] + 7152 * bgra[offset + 1] + 722 * bgra[offset];
                int alpha = bgra[offset + 3];
                gray[pixel] = (byte)((weighted * alpha + 2_550_000 * (255 - alpha) + 1_275_000) / 2_550_000);
            }
        }

        if (tone is { IsLegacyBinary: false } effective)
        {
            for (int pixel = 0; pixel < gray.Length; pixel++)
            {
                if ((pixel & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                gray[pixel] = effective.UseWhite(gray[pixel], pixel) ? (byte)255 : (byte)0;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(width, height, gray);
    }

    public static BluRayImagePayloadMapping CreateMapping(string path, BluRayDiscParameters disc,
        IBluRayLuminanceMapping mapping, CancellationToken cancellationToken = default,
        ToneProfile? tone = null)
    {
        // Validate the unresolved strategy before loading any source file.
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        return new(disc, Load(path, tone, cancellationToken), mapping);
    }
}
