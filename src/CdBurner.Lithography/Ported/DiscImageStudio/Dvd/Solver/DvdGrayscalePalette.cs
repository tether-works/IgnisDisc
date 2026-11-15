// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Solver/DvdGrayscalePalette.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Solver/DvdGrayscalePalette.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.


namespace CdBurner.Lithography.Dvd.Solver;

/// <summary>Black and white DVD tones, expressed as scrambled bytes Q presented to EFMPlus.</summary>
public static class DvdGrayscalePalette
{
    public const byte BlackScrambledByte = 0xA5;
    public const byte WhiteScrambledByte = 0x92;

    public static ReadOnlySpan<byte> ScrambledBytes =>
        [BlackScrambledByte, WhiteScrambledByte];

    public static byte GetScrambledByte(byte luminance)
        => GetScrambledByteForLevel(BinaryGrayscale.GetLevelIndex(luminance));

    public static byte GetScrambledByteForLevel(int levelIndex)
        => levelIndex switch
        {
            0 => BlackScrambledByte,
            1 => WhiteScrambledByte,
            _ => throw new ArgumentOutOfRangeException(nameof(levelIndex)),
        };

    public static bool TryGetLuminance(byte scrambledByte, out byte luminance)
    {
        int value = scrambledByte switch
        {
            BlackScrambledByte => 0,
            WhiteScrambledByte => 255,
            // Legacy four-tone ISOs used Q=66 as dark gray. On the tested disc it
            // merges with A5 at ordinary viewing angles; never generate it anew.
            0x66 => 0,
            // Historical three-/four-tone output is still readable. Its B9 tone
            // retains the nominal dark appearance rather than being called white.
            0xB9 => 85,
            _ => -1,
        };
        luminance = value < 0 ? (byte)0 : (byte)value;
        return value >= 0;
    }
}
