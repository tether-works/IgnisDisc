// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks
// Written for IgnisDisc from the one-line description of the periodic-9b-16-v1 rule. No code from another project
// is used here. The class and method names match what the ported DiscImageStudio writer calls.

using CdBurner.Lithography.Dvd.Encoding;

namespace CdBurner.Lithography.Dvd.Solver;

/// <summary>
/// The periodic-9b-16-v1 read-compatibility rule for DVD picture sectors.
///
/// Rule: look at every 16th byte of a 2048-byte sector (offsets 0, 16, ..., 2032; 128 positions). If the byte
/// belongs to the picture and its scrambled value Q is neither A5 nor 9B, make Q equal 9B. The sector holds user
/// bytes P, and Q = P XOR mask, so the stored byte becomes 9B XOR mask.
///
/// Bytes that do not belong to the picture (file data, sector parts the picture leaves alone) are never touched;
/// the caller marks ownership per byte.
/// </summary>
internal static class DvdReadCompatibilityGuard
{
    internal const string ModeName = DvdReadCompatibilityModes.Periodic9B16;

    /// <summary>Distance in bytes between two guarded positions.</summary>
    internal const int PeriodBytes = 16;

    /// <summary>Scrambled value written at a guarded position.</summary>
    internal const byte GuardScrambledByte = 0x9B;

    /// <summary>Scrambled value that is left as it is at a guarded position (the dispersed picture byte).</summary>
    internal const byte KeptScrambledByte = 0xA5;

    /// <summary>
    /// Applies the rule to one sector in place.
    /// </summary>
    /// <param name="payload">2048 user bytes of the sector (before scrambling).</param>
    /// <param name="scramblerMask">The 2048 scrambler bytes of this sector (they depend on its PSN).</param>
    /// <param name="ownership">One entry per byte: non-zero where the picture owns the byte.</param>
    /// <returns>How many bytes were changed.</returns>
    internal static int ApplySector(
        Span<byte> payload, ReadOnlySpan<byte> scramblerMask, ReadOnlySpan<byte> ownership)
    {
        const int sectorBytes = DvdEccBlockEncoder.PayloadBytesPerSector;
        if (payload.Length != sectorBytes)
            throw new ArgumentException($"The sector must hold {sectorBytes} bytes.", nameof(payload));
        if (scramblerMask.Length != sectorBytes)
            throw new ArgumentException($"The scrambler mask must hold {sectorBytes} bytes.", nameof(scramblerMask));
        if (ownership.Length != sectorBytes)
            throw new ArgumentException($"The ownership map must hold {sectorBytes} entries.", nameof(ownership));

        int changed = 0;
        for (int offset = 0; offset < sectorBytes; offset += PeriodBytes)
        {
            bool pictureOwnsByte = ownership[offset] != 0;
            if (!pictureOwnsByte)
                continue;

            byte mask = scramblerMask[offset];
            byte scrambled = (byte)(payload[offset] ^ mask);
            if (scrambled == KeptScrambledByte || scrambled == GuardScrambledByte)
                continue;

            payload[offset] = (byte)(GuardScrambledByte ^ mask);
            changed++;
        }

        return changed;
    }
}
