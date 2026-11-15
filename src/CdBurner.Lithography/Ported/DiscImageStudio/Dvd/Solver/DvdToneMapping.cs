// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Solver/DvdToneMapping.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Solver/DvdToneMapping.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.


namespace CdBurner.Lithography.Dvd.Solver;

/// <summary>
/// Binary texture decision shared by the dispersion writer, the constraint mapper and the
/// calibration preview. Dithering shifts the threshold per payload sample, so the sampled
/// picture keeps its midtones while still using only the two proven textures.
/// </summary>
public static class DvdToneMapping
{
    public static bool UseWhite(
        byte luminance,
        byte threshold,
        ToneProfile? tone,
        long sampleIndex)
    {
        return tone is { } profile
            ? profile.UseWhite(luminance, sampleIndex, threshold)
            : luminance >= threshold;
    }
}
