// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/BinaryGrayscale.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/BinaryGrayscale.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography;

/// <summary>Shared black/white image quantization, independent of disc encoding.</summary>
public static class BinaryGrayscale
{
    public static ReadOnlySpan<byte> Levels => [0, 255];

    public static int GetLevelIndex(byte luminance) => luminance < 128 ? 0 : 1;

    public static byte GetLuminance(int levelIndex) => levelIndex switch
    {
        0 => 0,
        1 => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(levelIndex), "Grayscale level must be 0..1."),
    };

    public static byte Quantize(byte luminance) => GetLuminance(GetLevelIndex(luminance));
}
