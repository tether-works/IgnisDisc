// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Cd/CdAudioByteOrder.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Cd/CdAudioByteOrder.cs
// IgnisDisc change: namespace only. See THIRD-PARTY-NOTICES.md.

using System.IO;

namespace CdBurner.Lithography.Cd;

public enum CdAudioByteOrder
{
    BigEndian,
    LittleEndian,
}

internal static class CdAudioSamples
{
    internal static void Validate(CdAudioByteOrder byteOrder)
    {
        if (!Enum.IsDefined(byteOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        }
    }

    internal static void SwapByteOrder(Span<byte> samples)
    {
        if ((samples.Length & 1) != 0)
        {
            throw new InvalidDataException("CD audio must contain complete 16-bit samples.");
        }

        for (int index = 0; index < samples.Length; index += 2)
        {
            (samples[index], samples[index + 1]) = (samples[index + 1], samples[index]);
        }
    }

    internal static void WriteSector(Stream output, byte[] sector, CdAudioByteOrder byteOrder)
    {
        // The delay compensation produces the existing cdrecord big-endian order.
        // Convert only after compensation: swapping its input changes the picture.
        if (byteOrder == CdAudioByteOrder.LittleEndian)
        {
            SwapByteOrder(sector);
        }

        output.Write(sector);
    }
}
