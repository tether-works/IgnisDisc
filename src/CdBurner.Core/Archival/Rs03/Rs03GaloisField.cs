using System.Runtime.CompilerServices;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Lookup tables for GF(2^8) defined by primitive polynomial x^8 + x^7 + x^2 + x + 1 (0x187).
/// </summary>
internal static class Rs03GaloisField
{
    public const int FieldSize = Rs03Constants.FieldSize;
    public const int FieldMax = Rs03Constants.FieldMax;
    public const int AlphaZero = Rs03Constants.AlphaZero;

    public static readonly byte[] AlphaTo = new byte[FieldSize];
    public static readonly byte[] ExpWrapped = new byte[FieldSize * 2];
    public static readonly int[] IndexOf = new int[FieldSize];

    static Rs03GaloisField()
    {
        int x = 1;
        for (int i = 0; i < FieldMax; i++)
        {
            IndexOf[x] = i;
            AlphaTo[i] = (byte)x;
            x <<= 1;
            if ((x & FieldSize) != 0)
            {
                x ^= Rs03Constants.FieldGeneratorPoly;
            }
        }

        if (x != 1)
        {
            throw new InvalidOperationException("Failed to initialize GF(2^8) tables for polynomial 0x187.");
        }

        IndexOf[0] = AlphaZero;
        AlphaTo[AlphaZero] = 0;

        for (int i = 0; i < ExpWrapped.Length; i++)
        {
            ExpWrapped[i] = AlphaTo[ModFieldMax(i)];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ModFieldMax(int x)
    {
        while (x >= FieldMax)
        {
            x -= FieldMax;
            x = (x >> 8) + (x & FieldMax);
        }
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }
        return ExpWrapped[IndexOf[a] + IndexOf[b]];
    }

    public static void BuildNibbleTables(byte factor, Span<byte> lo, Span<byte> hi)
    {
        for (int i = 0; i < 16; i++)
        {
            lo[i] = Multiply((byte)i, factor);
            hi[i] = Multiply((byte)(i << 4), factor);
        }
    }
}
