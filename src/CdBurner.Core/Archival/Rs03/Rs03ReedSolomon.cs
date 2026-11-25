using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Clean-room GF(2^8) Reed-Solomon RS(255, 255 - nroots) encoder, syndrome calculator, and erasure/error decoder
/// using CCSDS parameters (generator 0x187, first root 112, primitive element 11).
/// </summary>
public sealed class Rs03ReedSolomon
{
    public int ParityRootCount { get; }
    public int DataSymbolCount { get; }
    public int InitialShift { get; }

    private readonly int[] _generatorPolyIndices;
    private readonly byte[] _generatorPolyCoeffs;
    private readonly byte[] _tapLo16;
    private readonly byte[] _tapHi16;
    private readonly byte[] _synLo16;
    private readonly byte[] _synHi16;
    private readonly byte[] _syndromeRoots;

    public Rs03ReedSolomon(int parityRootCount)
    {
        if (parityRootCount < Rs03Constants.MinRoots || parityRootCount > Rs03Constants.MaxRoots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parityRootCount),
                parityRootCount,
                $"Parity root count must be between {Rs03Constants.MinRoots} and {Rs03Constants.MaxRoots}.");
        }

        ParityRootCount = parityRootCount;
        DataSymbolCount = Rs03Constants.FieldMax - parityRootCount;
        InitialShift = (parityRootCount - (DataSymbolCount % parityRootCount)) % parityRootCount;

        int[] poly = new int[parityRootCount + 1];
        poly[0] = 1;

        int root = Rs03Constants.FirstConsecutiveRoot * Rs03Constants.PrimitiveElement;
        for (int i = 0; i < parityRootCount; i++, root += Rs03Constants.PrimitiveElement)
        {
            poly[i + 1] = 1;
            for (int j = i; j > 0; j--)
            {
                if (poly[j] != 0)
                {
                    poly[j] = poly[j - 1] ^ Rs03GaloisField.AlphaTo[Rs03GaloisField.ModFieldMax(Rs03GaloisField.IndexOf[poly[j]] + root)];
                }
                else
                {
                    poly[j] = poly[j - 1];
                }
            }
            poly[0] = Rs03GaloisField.AlphaTo[Rs03GaloisField.ModFieldMax(Rs03GaloisField.IndexOf[poly[0]] + root)];
        }

        _generatorPolyCoeffs = new byte[parityRootCount];
        _generatorPolyIndices = new int[parityRootCount + 1];
        for (int i = 0; i < parityRootCount; i++)
        {
            _generatorPolyCoeffs[i] = (byte)poly[i];
            _generatorPolyIndices[i] = Rs03GaloisField.IndexOf[poly[i]];
        }
        _generatorPolyIndices[parityRootCount] = Rs03GaloisField.IndexOf[poly[parityRootCount]];

        _tapLo16 = new byte[parityRootCount * 16];
        _tapHi16 = new byte[parityRootCount * 16];
        for (int t = 0; t < parityRootCount; t++)
        {
            Rs03GaloisField.BuildNibbleTables(
                _generatorPolyCoeffs[t],
                _tapLo16.AsSpan(t * 16, 16),
                _tapHi16.AsSpan(t * 16, 16));
        }

        _syndromeRoots = new byte[parityRootCount];
        _synLo16 = new byte[parityRootCount * 16];
        _synHi16 = new byte[parityRootCount * 16];
        for (int i = 0; i < parityRootCount; i++)
        {
            byte rootVal = Rs03GaloisField.AlphaTo[
                Rs03GaloisField.ModFieldMax((Rs03Constants.FirstConsecutiveRoot + i) * Rs03Constants.PrimitiveElement)];
            _syndromeRoots[i] = rootVal;
            Rs03GaloisField.BuildNibbleTables(
                rootVal,
                _synLo16.AsSpan(i * 16, 16),
                _synHi16.AsSpan(i * 16, 16));
        }
    }

    /// <summary>
    /// Encodes <paramref name="dataAndCrcLayers"/> (count = <see cref="DataSymbolCount"/>, each slice length = <paramref name="sliceBytes"/>)
    /// into <paramref name="parityLayers"/> (count = <see cref="ParityRootCount"/>, each slice length = <paramref name="sliceBytes"/>).
    /// </summary>
    public void EncodeLayers(
        ReadOnlySpan<byte[]> dataAndCrcLayers,
        ReadOnlySpan<byte[]> parityLayers,
        int sliceBytes)
    {
        if (dataAndCrcLayers.Length != DataSymbolCount)
        {
            throw new ArgumentException($"Expected {DataSymbolCount} data+CRC layers.", nameof(dataAndCrcLayers));
        }
        if (parityLayers.Length != ParityRootCount)
        {
            throw new ArgumentException($"Expected {ParityRootCount} parity layers.", nameof(parityLayers));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(sliceBytes);
        if (sliceBytes == 0)
        {
            return;
        }

        for (int p = 0; p < ParityRootCount; p++)
        {
            parityLayers[p].AsSpan(0, sliceBytes).Clear();
        }

        int shift = InitialShift;
        byte[] feedbackBuffer = GC.AllocateUninitializedArray<byte>(sliceBytes);

        for (int layer = 0; layer < DataSymbolCount; layer++)
        {
            ReadOnlySpan<byte> data = dataAndCrcLayers[layer].AsSpan(0, sliceBytes);
            Span<byte> headParity = parityLayers[shift].AsSpan(0, sliceBytes);

            XorBuffers(data, headParity, feedbackBuffer);
            MultiplyBufferAssign(_generatorPolyCoeffs[0], _tapLo16.AsSpan(0, 16), _tapHi16.AsSpan(0, 16), feedbackBuffer, headParity);

            int p = shift + 1;
            if (p == ParityRootCount)
            {
                p = 0;
            }
            for (int tap = ParityRootCount - 1; tap >= 1; tap--)
            {
                MultiplyBufferXor(
                    _generatorPolyCoeffs[tap],
                    _tapLo16.AsSpan(tap * 16, 16),
                    _tapHi16.AsSpan(tap * 16, 16),
                    feedbackBuffer,
                    parityLayers[p].AsSpan(0, sliceBytes));

                p++;
                if (p == ParityRootCount)
                {
                    p = 0;
                }
            }

            shift++;
            if (shift == ParityRootCount)
            {
                shift = 0;
            }
        }
    }

    /// <summary>
    /// Computes all <see cref="ParityRootCount"/> syndromes for <paramref name="byteCount"/> consecutive codewords across 255 layers.
    /// Result layout: <c>syndromes[rootIndex * byteCount + b]</c>.
    /// </summary>
    public void ComputeSyndromes(
        ReadOnlySpan<byte[]> layers,
        int byteOffset,
        int byteCount,
        Span<byte> syndromes)
    {
        if (layers.Length != Rs03Constants.FieldMax)
        {
            throw new ArgumentException($"Expected {Rs03Constants.FieldMax} layers.", nameof(layers));
        }
        if (syndromes.Length < ParityRootCount * byteCount)
        {
            throw new ArgumentException("Syndrome buffer too small.", nameof(syndromes));
        }

        for (int rootIndex = 0; rootIndex < ParityRootCount; rootIndex++)
        {
            Span<byte> synRow = syndromes.Slice(rootIndex * byteCount, byteCount);
            layers[0].AsSpan(byteOffset, byteCount).CopyTo(synRow);

            ReadOnlySpan<byte> lo = _synLo16.AsSpan(rootIndex * 16, 16);
            ReadOnlySpan<byte> hi = _synHi16.AsSpan(rootIndex * 16, 16);
            byte rootVal = _syndromeRoots[rootIndex];

            for (int j = 1; j < Rs03Constants.FieldMax; j++)
            {
                HornerStep(synRow, layers[j].AsSpan(byteOffset, byteCount), rootVal, lo, hi);
            }
        }
    }

    /// <summary>
    /// Attempts to repair a column of <paramref name="byteCount"/> codewords in place across all 255 layers,
    /// given known erasure layer indices in <paramref name="erasurePositions"/>.
    /// Returns true if all codewords in the column satisfy all parity equations after repair.
    /// </summary>
    public bool TryDecodeColumn(
        ReadOnlySpan<byte[]> layers,
        int byteOffset,
        int byteCount,
        ReadOnlySpan<int> erasurePositions,
        out int unexpectedErrorMaskBitCount)
    {
        unexpectedErrorMaskBitCount = 0;
        if (layers.Length != Rs03Constants.FieldMax)
        {
            throw new ArgumentException($"Expected {Rs03Constants.FieldMax} layers.", nameof(layers));
        }
        if (erasurePositions.Length > ParityRootCount)
        {
            return false;
        }

        Span<bool> isKnownErasure = stackalloc bool[Rs03Constants.FieldMax];
        isKnownErasure.Clear();
        for (int i = 0; i < erasurePositions.Length; i++)
        {
            int pos = erasurePositions[i];
            if ((uint)pos >= Rs03Constants.FieldMax)
            {
                return false;
            }
            isKnownErasure[pos] = true;
        }

        byte[] synBlock = new byte[ParityRootCount * byteCount];
        ComputeSyndromes(layers, byteOffset, byteCount, synBlock);

        int erasureCount = erasurePositions.Length;
        Span<int> erasureBaseLambda = stackalloc int[ParityRootCount + 1];
        erasureBaseLambda.Clear();
        erasureBaseLambda[0] = 1;
        for (int i = 0; i < erasureCount; i++)
        {
            int u = Rs03GaloisField.ModFieldMax(Rs03Constants.PrimitiveElement * (Rs03Constants.FieldMax - 1 - erasurePositions[i]));
            for (int j = i + 1; j > 0; j--)
            {
                int prevIdx = Rs03GaloisField.IndexOf[erasureBaseLambda[j - 1]];
                if (prevIdx != Rs03GaloisField.AlphaZero)
                {
                    erasureBaseLambda[j] ^= Rs03GaloisField.ExpWrapped[u + prevIdx];
                }
            }
        }

        Span<int> syn = stackalloc int[ParityRootCount];
        Span<int> lambda = stackalloc int[ParityRootCount + 1];
        Span<int> bPoly = stackalloc int[ParityRootCount + 1];
        Span<int> tPoly = stackalloc int[ParityRootCount + 1];
        Span<int> omega = stackalloc int[ParityRootCount + 1];
        Span<int> reg = stackalloc int[ParityRootCount + 1];
        Span<int> roots = stackalloc int[ParityRootCount];
        Span<int> locs = stackalloc int[ParityRootCount];
        Span<bool> unexpectedFound = stackalloc bool[Rs03Constants.FieldMax];
        unexpectedFound.Clear();

        for (int bi = 0; bi < byteCount; bi++)
        {
            int synOr = 0;
            for (int i = 0; i < ParityRootCount; i++)
            {
                byte sVal = synBlock[i * byteCount + bi];
                synOr |= sVal;
                syn[i] = Rs03GaloisField.IndexOf[sVal];
            }

            if (synOr == 0)
            {
                continue;
            }

            erasureBaseLambda.CopyTo(lambda);
            for (int i = 0; i <= ParityRootCount; i++)
            {
                bPoly[i] = Rs03GaloisField.IndexOf[lambda[i]];
            }

            int r = erasureCount;
            int el = erasureCount;
            while (++r <= ParityRootCount)
            {
                int discr = 0;
                for (int i = 0; i < r; i++)
                {
                    if (lambda[i] != 0 && syn[r - 1 - i] != Rs03GaloisField.AlphaZero)
                    {
                        discr ^= Rs03GaloisField.ExpWrapped[Rs03GaloisField.IndexOf[lambda[i]] + syn[r - 1 - i]];
                    }
                }

                int discrIdx = Rs03GaloisField.IndexOf[discr];
                if (discrIdx == Rs03GaloisField.AlphaZero)
                {
                    for (int i = ParityRootCount; i > 0; i--)
                    {
                        bPoly[i] = bPoly[i - 1];
                    }
                    bPoly[0] = Rs03GaloisField.AlphaZero;
                }
                else
                {
                    tPoly[0] = lambda[0];
                    for (int i = 0; i < ParityRootCount; i++)
                    {
                        tPoly[i + 1] = bPoly[i] != Rs03GaloisField.AlphaZero
                            ? lambda[i + 1] ^ Rs03GaloisField.ExpWrapped[discrIdx + bPoly[i]]
                            : lambda[i + 1];
                    }

                    if (2 * el <= r + erasureCount - 1)
                    {
                        el = r + erasureCount - el;
                        for (int i = 0; i <= ParityRootCount; i++)
                        {
                            bPoly[i] = lambda[i] == 0
                                ? Rs03GaloisField.AlphaZero
                                : Rs03GaloisField.ModFieldMax(Rs03GaloisField.IndexOf[lambda[i]] - discrIdx + Rs03GaloisField.FieldMax);
                        }
                    }
                    else
                    {
                        for (int i = ParityRootCount; i > 0; i--)
                        {
                            bPoly[i] = bPoly[i - 1];
                        }
                        bPoly[0] = Rs03GaloisField.AlphaZero;
                    }

                    tPoly.CopyTo(lambda);
                }
            }

            int degLambda = 0;
            for (int i = 0; i <= ParityRootCount; i++)
            {
                lambda[i] = Rs03GaloisField.IndexOf[lambda[i]];
                if (lambda[i] != Rs03GaloisField.AlphaZero)
                {
                    degLambda = i;
                }
            }

            if (degLambda == 0 || degLambda > ParityRootCount)
            {
                return false;
            }

            lambda.Slice(1, degLambda).CopyTo(reg.Slice(1, degLambda));
            int foundCount = 0;
            for (int i = 1, k = Rs03Constants.PrimthRoot - 1;
                 i <= Rs03GaloisField.FieldMax;
                 i++, k = Rs03GaloisField.ModFieldMax(k + Rs03Constants.PrimthRoot))
            {
                int q = 1;
                for (int j = degLambda; j > 0; j--)
                {
                    if (reg[j] != Rs03GaloisField.AlphaZero)
                    {
                        reg[j] = Rs03GaloisField.ModFieldMax(reg[j] + j);
                        q ^= Rs03GaloisField.AlphaTo[reg[j]];
                    }
                }

                if (q != 0)
                {
                    continue;
                }

                roots[foundCount] = i;
                locs[foundCount] = k;
                if (++foundCount == degLambda)
                {
                    break;
                }
            }

            if (foundCount != degLambda)
            {
                return false;
            }

            int degOmega = degLambda - 1;
            for (int i = 0; i <= degOmega; i++)
            {
                int tmp = 0;
                for (int j = i; j >= 0; j--)
                {
                    if (syn[i - j] != Rs03GaloisField.AlphaZero && lambda[j] != Rs03GaloisField.AlphaZero)
                    {
                        tmp ^= Rs03GaloisField.ExpWrapped[syn[i - j] + lambda[j]];
                    }
                }
                omega[i] = Rs03GaloisField.IndexOf[tmp];
            }

            for (int j = foundCount - 1; j >= 0; j--)
            {
                int num1 = 0;
                for (int i = degOmega; i >= 0; i--)
                {
                    if (omega[i] != Rs03GaloisField.AlphaZero)
                    {
                        num1 ^= Rs03GaloisField.ExpWrapped[Rs03GaloisField.ModFieldMax(omega[i] + i * roots[j])];
                    }
                }

                if (num1 == 0)
                {
                    continue;
                }

                int num2 = Rs03GaloisField.ExpWrapped[
                    Rs03GaloisField.ModFieldMax(roots[j] * (Rs03Constants.FirstConsecutiveRoot - 1) + Rs03GaloisField.FieldMax)];

                int den = 0;
                for (int i = Math.Min(degLambda, ParityRootCount - 1) & ~1; i >= 0; i -= 2)
                {
                    if (lambda[i + 1] != Rs03GaloisField.AlphaZero)
                    {
                        den ^= Rs03GaloisField.ExpWrapped[Rs03GaloisField.ModFieldMax(lambda[i + 1] + i * roots[j])];
                    }
                }

                if (den == 0)
                {
                    return false;
                }

                int loc = locs[j];
                byte correction = Rs03GaloisField.ExpWrapped[
                    Rs03GaloisField.ModFieldMax(
                        Rs03GaloisField.IndexOf[num1] +
                        Rs03GaloisField.IndexOf[num2] +
                        Rs03GaloisField.FieldMax -
                        Rs03GaloisField.IndexOf[den])];

                layers[loc][byteOffset + bi] ^= correction;
                if (!isKnownErasure[loc] && !unexpectedFound[loc])
                {
                    unexpectedFound[loc] = true;
                    unexpectedErrorMaskBitCount++;
                }
            }
        }

        // Confirm zero syndromes after applying corrections.
        ComputeSyndromes(layers, byteOffset, byteCount, synBlock);
        for (int i = 0; i < ParityRootCount * byteCount; i++)
        {
            if (synBlock[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void XorBuffers(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 32 <= dest.Length; i += 32)
            {
                Vector256<byte> va = Vector256.LoadUnsafe(ref Unsafe.AsRef(in a[i]));
                Vector256<byte> vb = Vector256.LoadUnsafe(ref Unsafe.AsRef(in b[i]));
                (va ^ vb).StoreUnsafe(ref dest[i]);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= dest.Length; i += 16)
            {
                Vector128<byte> va = Vector128.LoadUnsafe(ref Unsafe.AsRef(in a[i]));
                Vector128<byte> vb = Vector128.LoadUnsafe(ref Unsafe.AsRef(in b[i]));
                (va ^ vb).StoreUnsafe(ref dest[i]);
            }
        }

        for (; i < dest.Length; i++)
        {
            dest[i] = (byte)(a[i] ^ b[i]);
        }
    }

    private static void MultiplyBufferAssign(
        byte coeff,
        ReadOnlySpan<byte> lo16,
        ReadOnlySpan<byte> hi16,
        ReadOnlySpan<byte> src,
        Span<byte> dest)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<byte> tlo = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0])));
            Vector256<byte> thi = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0])));
            Vector256<byte> mask = Vector256.Create((byte)0x0F);

            for (; i + 32 <= src.Length; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector256<byte> loIdx = v & mask;
                Vector256<byte> hiIdx = Avx2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & mask;
                Vector256<byte> prod = Avx2.Shuffle(tlo, loIdx) ^ Avx2.Shuffle(thi, hiIdx);
                prod.StoreUnsafe(ref dest[i]);
            }
        }
        else if (Ssse3.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));
            Vector128<byte> mask = Vector128.Create((byte)0x0F);

            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector128<byte> loIdx = v & mask;
                Vector128<byte> hiIdx = Sse2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & mask;
                Vector128<byte> prod = Ssse3.Shuffle(tlo, loIdx) ^ Ssse3.Shuffle(thi, hiIdx);
                prod.StoreUnsafe(ref dest[i]);
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));
            Vector128<byte> mask = Vector128.Create((byte)0x0F);

            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector128<byte> loIdx = v & mask;
                Vector128<byte> hiIdx = AdvSimd.ShiftRightLogical(v, 4);
                Vector128<byte> prod = AdvSimd.Arm64.VectorTableLookup(tlo, loIdx) ^ AdvSimd.Arm64.VectorTableLookup(thi, hiIdx);
                prod.StoreUnsafe(ref dest[i]);
            }
        }

        for (; i < src.Length; i++)
        {
            byte v = src[i];
            dest[i] = coeff == 0 || v == 0 ? (byte)0 : Rs03GaloisField.Multiply(v, coeff);
        }
    }

    private static void MultiplyBufferXor(
        byte coeff,
        ReadOnlySpan<byte> lo16,
        ReadOnlySpan<byte> hi16,
        ReadOnlySpan<byte> src,
        Span<byte> dest)
    {
        if (coeff == 0)
        {
            return;
        }

        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<byte> tlo = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0])));
            Vector256<byte> thi = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0])));
            Vector256<byte> mask = Vector256.Create((byte)0x0F);

            for (; i + 32 <= src.Length; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector256<byte> cur = Vector256.LoadUnsafe(ref dest[i]);
                Vector256<byte> loIdx = v & mask;
                Vector256<byte> hiIdx = Avx2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & mask;
                Vector256<byte> prod = Avx2.Shuffle(tlo, loIdx) ^ Avx2.Shuffle(thi, hiIdx);
                (cur ^ prod).StoreUnsafe(ref dest[i]);
            }
        }
        else if (Ssse3.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));
            Vector128<byte> mask = Vector128.Create((byte)0x0F);

            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector128<byte> cur = Vector128.LoadUnsafe(ref dest[i]);
                Vector128<byte> loIdx = v & mask;
                Vector128<byte> hiIdx = Sse2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & mask;
                Vector128<byte> prod = Ssse3.Shuffle(tlo, loIdx) ^ Ssse3.Shuffle(thi, hiIdx);
                (cur ^ prod).StoreUnsafe(ref dest[i]);
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));
            Vector128<byte> mask = Vector128.Create((byte)0x0F);

            for (; i + 16 <= src.Length; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[i]));
                Vector128<byte> cur = Vector128.LoadUnsafe(ref dest[i]);
                Vector128<byte> loIdx = v & mask;
                Vector128<byte> hiIdx = AdvSimd.ShiftRightLogical(v, 4);
                Vector128<byte> prod = AdvSimd.Arm64.VectorTableLookup(tlo, loIdx) ^ AdvSimd.Arm64.VectorTableLookup(thi, hiIdx);
                (cur ^ prod).StoreUnsafe(ref dest[i]);
            }
        }

        for (; i < src.Length; i++)
        {
            byte v = src[i];
            if (v != 0)
            {
                dest[i] ^= (byte)(lo16[v & 0x0F] ^ hi16[v >> 4]);
            }
        }
    }

    private static void HornerStep(
        Span<byte> synRow,
        ReadOnlySpan<byte> layerData,
        byte rootVal,
        ReadOnlySpan<byte> lo16,
        ReadOnlySpan<byte> hi16)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<byte> tlo = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0])));
            Vector256<byte> thi = Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0])));
            Vector256<byte> mask = Vector256.Create((byte)0x0F);

            for (; i + 32 <= synRow.Length; i += 32)
            {
                Vector256<byte> s = Vector256.LoadUnsafe(ref synRow[i]);
                Vector256<byte> d = Vector256.LoadUnsafe(ref Unsafe.AsRef(in layerData[i]));
                Vector256<byte> loIdx = s & mask;
                Vector256<byte> hiIdx = Avx2.ShiftRightLogical(s.AsUInt16(), 4).AsByte() & mask;
                Vector256<byte> next = d ^ Avx2.Shuffle(tlo, loIdx) ^ Avx2.Shuffle(thi, hiIdx);
                next.StoreUnsafe(ref synRow[i]);
            }
        }
        else if (Ssse3.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));
            Vector128<byte> mask = Vector128.Create((byte)0x0F);

            for (; i + 16 <= synRow.Length; i += 16)
            {
                Vector128<byte> s = Vector128.LoadUnsafe(ref synRow[i]);
                Vector128<byte> d = Vector128.LoadUnsafe(ref Unsafe.AsRef(in layerData[i]));
                Vector128<byte> loIdx = s & mask;
                Vector128<byte> hiIdx = Sse2.ShiftRightLogical(s.AsUInt16(), 4).AsByte() & mask;
                Vector128<byte> next = d ^ Ssse3.Shuffle(tlo, loIdx) ^ Ssse3.Shuffle(thi, hiIdx);
                next.StoreUnsafe(ref synRow[i]);
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> tlo = Vector128.LoadUnsafe(ref Unsafe.AsRef(in lo16[0]));
            Vector128<byte> thi = Vector128.LoadUnsafe(ref Unsafe.AsRef(in hi16[0]));

            for (; i + 16 <= synRow.Length; i += 16)
            {
                Vector128<byte> s = Vector128.LoadUnsafe(ref synRow[i]);
                Vector128<byte> d = Vector128.LoadUnsafe(ref Unsafe.AsRef(in layerData[i]));
                Vector128<byte> loIdx = s & Vector128.Create((byte)0x0F);
                Vector128<byte> hiIdx = AdvSimd.ShiftRightLogical(s, 4);
                Vector128<byte> next = d ^ AdvSimd.Arm64.VectorTableLookup(tlo, loIdx) ^ AdvSimd.Arm64.VectorTableLookup(thi, hiIdx);
                next.StoreUnsafe(ref synRow[i]);
            }
        }

        for (; i < synRow.Length; i++)
        {
            byte s = synRow[i];
            synRow[i] = (byte)(layerData[i] ^ (s == 0 ? 0 : Rs03GaloisField.Multiply(s, rootVal)));
        }
    }
}
