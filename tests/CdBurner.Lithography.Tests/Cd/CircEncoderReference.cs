namespace CdBurner.Lithography.Tests.Cd;

/// <summary>
/// The data-byte path of the drive's CIRC encoder, written out from ECMA-130 (2nd edition, 1996) annex C,
/// figure C.4 ("Structure of output of CIRC encoder"), independently of the ported delay table. For the 24 data
/// bytes of output F2-frame n, the figure names the input word W(m) with m = 12n + w - 12 * delay and its half
/// (A = byte 2w of the F1-frame, B = byte 2w + 1). Output bytes 12 to 15 (Q parity) and 28 to 31 (P parity) carry no
/// input bytes; the 24 remaining positions are numbered here as data slots 0 to 23 in output order.
/// </summary>
internal static class CircEncoderReference
{
    private const int D = 4;

    /// <summary>Figure C.4 rows for output bytes 0-11 and 16-27: (word w, delay in F1-frames, half A or B).</summary>
    private static readonly (int Word, int Delay, bool IsB)[] Slots =
    [
        (0, 3, false), (0, D + 2, true), (4, (2 * D) + 3, false), (4, (3 * D) + 2, true),
        (8, (4 * D) + 3, false), (8, (5 * D) + 2, true), (1, (6 * D) + 3, false), (1, (7 * D) + 2, true),
        (5, (8 * D) + 3, false), (5, (9 * D) + 2, true), (9, (10 * D) + 3, false), (9, (11 * D) + 2, true),
        (2, (16 * D) + 1, false), (2, 17 * D, true), (6, (18 * D) + 1, false), (6, 19 * D, true),
        (10, (20 * D) + 1, false), (10, 21 * D, true), (3, (22 * D) + 1, false), (3, 23 * D, true),
        (7, (24 * D) + 1, false), (7, 25 * D, true), (11, (26 * D) + 1, false), (11, 27 * D, true),
    ];

    internal const int BytesPerFrame = 24;

    /// <summary>Upstream's audit (docs/PHYSICAL_MODEL_AUDIT.md, finding four): the compensated stream comes out of
    /// the encoder this many F1-frames after its logical position.</summary>
    internal const int ReferenceShiftFrames = 111;

    /// <summary>Longest and shortest delay of a byte through the encoder (ECMA-130 C.9).</summary>
    internal const int LongestDelayFrames = 108;

    /// <summary>For F1-frame byte b (0-23): the data slot it leaves in and its delay in F1-frames.</summary>
    internal static (int Slot, int Delay) PathOf(int f1Byte)
    {
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            (int word, int delay, bool isB) = Slots[slot];
            if ((2 * word) + (isB ? 1 : 0) == f1Byte) return (slot, delay);
        }

        throw new ArgumentOutOfRangeException(nameof(f1Byte));
    }

    /// <summary>
    /// Runs F1-frame bytes (stream order, as the drive receives them) through the figure C.4 data path and returns
    /// output data slots: element [n * 24 + s] is data slot s of output frame n. Slots fed from before the stream are
    /// -1; the result is long enough to hold every input byte.
    /// </summary>
    internal static int[] Encode(IReadOnlyList<int> f1Stream)
    {
        int frames = (f1Stream.Count / BytesPerFrame) + LongestDelayFrames + 1;
        int[] output = new int[frames * BytesPerFrame];
        Array.Fill(output, -1);
        for (int index = 0; index < f1Stream.Count; index++)
        {
            (int slot, int delay) = PathOf(index % BytesPerFrame);
            output[(((index / BytesPerFrame) + delay) * BytesPerFrame) + slot] = f1Stream[index];
        }

        return output;
    }

    /// <summary>
    /// Stream positions a primed stream fills from its lead-in: position k (F1-byte order) reaches output frame
    /// k / 24 + delay, which shows logical frame (that - 111). A negative logical frame lies before the stretch.
    /// Upstream's unprimed delay line left zero placeholders exactly there.
    /// </summary>
    internal static bool IsBeforeStretch(long streamPosition)
    {
        (_, int delay) = PathOf((int)(streamPosition % BytesPerFrame));
        return (streamPosition / BytesPerFrame) + delay - ReferenceShiftFrames < 0;
    }

    /// <summary>
    /// Returns a copy of a generated track with the positions <see cref="IsBeforeStretch"/> names set back to zero,
    /// which is what upstream's unprimed interleaver wrote there, so the copy can be compared with upstream's golden
    /// hash. Little-endian audio swaps the two bytes of every 16-bit sample. The replaced values are returned.
    /// </summary>
    internal static byte[] AsUpstreamWrote(byte[] file, int dataOffset, bool littleEndian, out List<byte> replaced)
    {
        byte[] copy = (byte[])file.Clone();
        replaced = [];
        for (long position = 0; dataOffset + position < copy.Length; position++)
        {
            long f1Position = littleEndian ? position ^ 1 : position;
            if (f1Position / BytesPerFrame > ReferenceShiftFrames) break;
            if (!IsBeforeStretch(f1Position)) continue;
            replaced.Add(copy[dataOffset + position]);
            copy[dataOffset + position] = 0;
        }

        return copy;
    }
}
