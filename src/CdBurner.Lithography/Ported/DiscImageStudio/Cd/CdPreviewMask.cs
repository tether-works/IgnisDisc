// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Cd/CdPreviewMask.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Cd/CdPreviewMask.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Cd;

/// <summary>Uses the same pixel coordinates as CD output projection, independently of source-image mapping.</summary>
internal static class CdPreviewMask
{
    internal static void Apply(Span<byte> pixels, int size, CdDiscParameters actual)
    {
        double centre = size / 2.0;
        double millimetresPerPixel = actual.PreviewImageOuterRadiusMm / centre;
        double innerSquared = actual.InnerRadiusMm * actual.InnerRadiusMm;
        double outerSquared = actual.OutputOuterRadiusMm * actual.OutputOuterRadiusMm;
        for (int y = 0; y < size; y++)
        {
            double dy = (y - centre) * millimetresPerPixel;
            double dySquared = dy * dy;
            for (int x = 0; x < size; x++)
            {
                double dx = (x - centre) * millimetresPerPixel;
                double radiusSquared = dx * dx + dySquared;
                if (radiusSquared < innerSquared || radiusSquared >= outerSquared)
                    pixels[((y * size + x) * 4) + 3] = 0;
            }
        }
    }
}
