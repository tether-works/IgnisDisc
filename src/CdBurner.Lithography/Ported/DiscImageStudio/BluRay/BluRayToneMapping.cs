// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.BluRay/BluRayToneMapping.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.BluRay/BluRayToneMapping.cs
// IgnisDisc change: namespace. See THIRD-PARTY-NOTICES.md.


namespace CdBurner.Lithography.BluRay;

/// <summary>Applies the shared tone profile once, at source-image resolution: sRGB
/// linearization, brightness and byte-level dither decide each pixel's binary state before
/// the 3T/6T mapping. Payload positions are far denser than any practical raster, so this
/// is the only place a BD picture is quantized.</summary>
public static class BluRayToneMapping
{
    public static BluRayGray8Raster Apply(BluRayGray8Raster source, ToneProfile tone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ToneProfile effective = tone.Normalized();
        if (effective.IsLegacyBinary)
        {
            return source;
        }

        byte[] pixels = source.Pixels.ToArray();
        for (int index = 0; index < pixels.Length; index++)
        {
            if ((index & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            pixels[index] = effective.UseWhite(pixels[index], index) ? (byte)255 : (byte)0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new BluRayGray8Raster(source.Width, source.Height, pixels);
    }
}
