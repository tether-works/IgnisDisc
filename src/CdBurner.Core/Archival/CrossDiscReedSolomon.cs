using System.Numerics;
using System.Runtime.CompilerServices;
using CdBurner.Core.Archival.Rs03;

namespace CdBurner.Core.Archival;

/// <summary>
/// Systematic GF(2^8) Reed-Solomon codec over a column-scaled Cauchy matrix for cross-disc archive parity.
/// Parity disc 0 is always the bitwise XOR of all data discs; parity discs 1..K-1 provide full erasure recovery
/// for any combination of up to K lost data or parity discs (with N + K &lt;= 255).
/// </summary>
public sealed class CrossDiscReedSolomon
{
    /// <summary>Algorithm identifier recorded in parity metadata.</summary>
    public const string AlgorithmName = "RS-GF256-Cauchy";

    /// <summary>Maximum total discs (data + parity) supported in GF(2^8).</summary>
    public const int MaxTotalDiscs = Rs03GaloisField.FieldMax; // 255

    private static readonly byte[] MulTable = BuildMultiplyTable();

    private readonly byte[,] _encodeMatrix;

    /// <summary>Number of data discs (N &gt;= 1).</summary>
    public int DataDiscCount { get; }

    /// <summary>Number of parity discs (K &gt;= 1).</summary>
    public int ParityDiscCount { get; }

    /// <summary>
    /// Initializes a cross-disc Reed-Solomon codec for <paramref name="dataDiscCount"/> data discs and
    /// <paramref name="parityDiscCount"/> parity discs.
    /// </summary>
    public CrossDiscReedSolomon(int dataDiscCount, int parityDiscCount)
    {
        if (dataDiscCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dataDiscCount), dataDiscCount, "Data disc count must be at least 1.");
        }
        if (parityDiscCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parityDiscCount), parityDiscCount, "Parity disc count must be at least 1.");
        }
        if (dataDiscCount + parityDiscCount > MaxTotalDiscs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parityDiscCount),
                parityDiscCount,
                $"Total discs ({dataDiscCount} + {parityDiscCount}) cannot exceed {MaxTotalDiscs}.");
        }

        DataDiscCount = dataDiscCount;
        ParityDiscCount = parityDiscCount;
        _encodeMatrix = BuildEncodeMatrix(dataDiscCount, parityDiscCount);
    }

    /// <summary>Returns the encoding coefficient M[parityIndex, dataIndex] in GF(2^8).</summary>
    public byte GetCoefficient(int parityIndex, int dataIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parityIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(parityIndex, ParityDiscCount);
        ArgumentOutOfRangeException.ThrowIfNegative(dataIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dataIndex, DataDiscCount);
        return _encodeMatrix[parityIndex, dataIndex];
    }

    /// <summary>
    /// Accumulates <c>dst[k] ^= factor * src[k]</c> in GF(2^8) across <paramref name="src"/>.
    /// </summary>
    public static void MultiplyXor(byte factor, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (dst.Length < src.Length)
        {
            throw new ArgumentException("Destination buffer is smaller than source buffer.", nameof(dst));
        }

        int length = src.Length;
        if (factor == 0 || length == 0)
        {
            return;
        }

        if (factor == 1)
        {
            XorInto(src, dst[..length]);
            return;
        }

        ReadOnlySpan<byte> table = MulTable.AsSpan(factor * Rs03GaloisField.FieldSize, Rs03GaloisField.FieldSize);
        int i = 0;
        int unrollLimit = length - 7;
        for (; i < unrollLimit; i += 8)
        {
            dst[i] ^= table[src[i]];
            dst[i + 1] ^= table[src[i + 1]];
            dst[i + 2] ^= table[src[i + 2]];
            dst[i + 3] ^= table[src[i + 3]];
            dst[i + 4] ^= table[src[i + 4]];
            dst[i + 5] ^= table[src[i + 5]];
            dst[i + 6] ^= table[src[i + 6]];
            dst[i + 7] ^= table[src[i + 7]];
        }
        for (; i < length; i++)
        {
            dst[i] ^= table[src[i]];
        }
    }

    /// <summary>
    /// Computes the m x m inverse matrix over GF(2^8) for reconstructing <paramref name="missingDataIndices"/>
    /// from <paramref name="selectedParityIndices"/> (both 0-based, same length m &lt;= ParityDiscCount).
    /// </summary>
    public byte[,] BuildRecoveryMatrix(
        IReadOnlyList<int> missingDataIndices,
        IReadOnlyList<int> selectedParityIndices)
    {
        ArgumentNullException.ThrowIfNull(missingDataIndices);
        ArgumentNullException.ThrowIfNull(selectedParityIndices);

        int m = missingDataIndices.Count;
        if (m == 0)
        {
            return new byte[0, 0];
        }
        if (selectedParityIndices.Count != m)
        {
            throw new ArgumentException("Selected parity disc count must match missing data disc count.", nameof(selectedParityIndices));
        }
        if (m > ParityDiscCount)
        {
            throw new InvalidOperationException($"Cannot recover {m} missing data discs with only {ParityDiscCount} parity discs.");
        }

        byte[,] sub = new byte[m, m];
        for (int r = 0; r < m; r++)
        {
            int pIdx = selectedParityIndices[r];
            if ((uint)pIdx >= (uint)ParityDiscCount)
            {
                throw new ArgumentOutOfRangeException(nameof(selectedParityIndices), pIdx, "Parity index out of range.");
            }
            for (int c = 0; c < m; c++)
            {
                int dIdx = missingDataIndices[c];
                if ((uint)dIdx >= (uint)DataDiscCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(missingDataIndices), dIdx, "Data index out of range.");
                }
                sub[r, c] = _encodeMatrix[pIdx, dIdx];
            }
        }

        return InvertMatrix(sub, m);
    }

    /// <summary>Inverts an n x n matrix over GF(2^8) using Gauss-Jordan elimination.</summary>
    internal static byte[,] InvertMatrix(byte[,] matrix, int size)
    {
        byte[,] aug = new byte[size, size * 2];
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                aug[r, c] = matrix[r, c];
            }
            aug[r, size + r] = 1;
        }

        for (int col = 0; col < size; col++)
        {
            int pivot = -1;
            for (int row = col; row < size; row++)
            {
                if (aug[row, col] != 0)
                {
                    pivot = row;
                    break;
                }
            }

            if (pivot < 0)
            {
                throw new InvalidOperationException("Recovery matrix is singular and cannot be inverted.");
            }

            if (pivot != col)
            {
                for (int k = 0; k < size * 2; k++)
                {
                    (aug[col, k], aug[pivot, k]) = (aug[pivot, k], aug[col, k]);
                }
            }

            byte pivotVal = aug[col, col];
            if (pivotVal != 1)
            {
                byte inv = InvertScalar(pivotVal);
                for (int k = 0; k < size * 2; k++)
                {
                    aug[col, k] = Rs03GaloisField.Multiply(aug[col, k], inv);
                }
            }

            for (int row = 0; row < size; row++)
            {
                if (row == col)
                {
                    continue;
                }
                byte factor = aug[row, col];
                if (factor == 0)
                {
                    continue;
                }
                for (int k = 0; k < size * 2; k++)
                {
                    aug[row, k] ^= Rs03GaloisField.Multiply(factor, aug[col, k]);
                }
            }
        }

        byte[,] invMatrix = new byte[size, size];
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                invMatrix[r, c] = aug[r, size + c];
            }
        }
        return invMatrix;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte InvertScalar(byte value)
    {
        if (value == 0)
        {
            throw new DivideByZeroException("Zero has no multiplicative inverse in GF(2^8).");
        }
        int log = Rs03GaloisField.IndexOf[value];
        int invLog = log == 0 ? 0 : Rs03GaloisField.FieldMax - log;
        return Rs03GaloisField.AlphaTo[invLog];
    }

    private static byte[,] BuildEncodeMatrix(int dataCount, int parityCount)
    {
        // Column-scaled Cauchy matrix:
        // x_i = i (0..K-1), y_j = K + j (0..N-1). All x_i and y_j are distinct in GF(2^8) since N + K <= 255.
        // M[i, j] = (x_0 ^ y_j) / (x_i ^ y_j), so row 0 is all 1s (pure XOR) and every square submatrix is invertible.
        var matrix = new byte[parityCount, dataCount];
        for (int j = 0; j < dataCount; j++)
        {
            byte yj = (byte)(parityCount + j);
            matrix[0, j] = 1;
            for (int i = 1; i < parityCount; i++)
            {
                byte xi = (byte)i;
                byte denom = (byte)(xi ^ yj);
                matrix[i, j] = Rs03GaloisField.Multiply(yj, InvertScalar(denom));
            }
        }
        return matrix;
    }

    private static byte[] BuildMultiplyTable()
    {
        var table = new byte[Rs03GaloisField.FieldSize * Rs03GaloisField.FieldSize];
        for (int f = 1; f < Rs03GaloisField.FieldSize; f++)
        {
            int offset = f * Rs03GaloisField.FieldSize;
            byte factor = (byte)f;
            for (int v = 1; v < Rs03GaloisField.FieldSize; v++)
            {
                table[offset + v] = Rs03GaloisField.Multiply(factor, (byte)v);
            }
        }
        return table;
    }

    private static void XorInto(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && src.Length >= Vector<byte>.Count)
        {
            int vecSize = Vector<byte>.Count;
            int vecLimit = src.Length - vecSize;
            for (; i <= vecLimit; i += vecSize)
            {
                var va = new Vector<byte>(src.Slice(i, vecSize));
                var vb = new Vector<byte>(dst.Slice(i, vecSize));
                (va ^ vb).CopyTo(dst.Slice(i, vecSize));
            }
        }
        for (; i < src.Length; i++)
        {
            dst[i] ^= src[i];
        }
    }
}
