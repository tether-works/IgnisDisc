// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/Encoding/GaloisField256.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/Encoding/GaloisField256.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Dvd.Encoding;

internal static class GaloisField256
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static GaloisField256()
    {
        int value = 1;
        for (int exponent = 0; exponent < 255; exponent++)
        {
            Exp[exponent] = (byte)value;
            Log[value] = (byte)exponent;
            value <<= 1;
            if ((value & 0x100) != 0)
            {
                value ^= 0x11D;
            }
        }

        for (int exponent = 255; exponent < Exp.Length; exponent++)
        {
            Exp[exponent] = Exp[exponent - 255];
        }
    }

    internal static byte Multiply(byte left, byte right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        return Exp[Log[left] + Log[right]];
    }

    internal static byte AlphaPower(int exponent) => Exp[((exponent % 255) + 255) % 255];
}
