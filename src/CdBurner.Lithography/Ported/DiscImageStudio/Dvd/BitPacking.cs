// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DvdImageSolver/BitPacking.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DvdImageSolver/BitPacking.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

namespace CdBurner.Lithography.Dvd;

internal static class BitPacking
{
    internal static byte[] PackMsbFirst(ReadOnlySpan<byte> bits)
    {
        byte[] packed = new byte[(bits.Length + 7) / 8];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index] != 0)
            {
                packed[index / 8] |= (byte)(0x80 >> (index & 7));
            }
        }

        return packed;
    }
}
