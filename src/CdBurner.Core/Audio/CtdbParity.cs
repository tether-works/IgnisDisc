namespace CdBurner.Core.Audio;

/// <summary>
/// Arithmetic in the Galois field GF(2^16) used by CUETools Database (CTDB) parity: primitive polynomial
/// x^16 + x^12 + x^3 + x + 1 (0x1100B) with generator α = x. Field constants follow CUETools
/// (<c>CUETools.Parity/Galois.cs</c>, <c>https://github.com/gchudov/cuetools.net</c>, GPL-2.0-or-later).
/// </summary>
public static class CtdbGalois16
{
    /// <summary>Primitive polynomial of the field.</summary>
    public const int Polynomial = 0x1100B;

    /// <summary>Number of non-zero field elements (the multiplicative group order).</summary>
    public const int Order = 65535;

    // ExpTable is doubled so a product can index log(a) + log(b) without a modulo.
    private static readonly ushort[] ExpTable = new ushort[Order * 2];
    private static readonly int[] LogTable = new int[Order + 1];

    static CtdbGalois16()
    {
        int d = 1;
        for (int i = 0; i < Order; i++)
        {
            ExpTable[i] = ExpTable[Order + i] = (ushort)d;
            LogTable[d] = i;
            d <<= 1;
            if ((d & 0x10000) != 0)
            {
                d ^= Polynomial;
            }
        }
    }

    /// <summary>α raised to <paramref name="exponent"/> (any integer, reduced modulo the group order).</summary>
    public static ushort Pow(long exponent)
    {
        int e = (int)(exponent % Order);
        if (e < 0)
        {
            e += Order;
        }

        return ExpTable[e];
    }

    /// <summary>Discrete logarithm base α of a non-zero element.</summary>
    public static int Log(ushort value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Zero has no logarithm.");
        }

        return LogTable[value];
    }

    /// <summary>Field product <c>a · b</c>.</summary>
    public static ushort Mul(ushort a, ushort b) =>
        a == 0 || b == 0 ? (ushort)0 : ExpTable[LogTable[a] + LogTable[b]];

    /// <summary>Field quotient <c>a / b</c>; <paramref name="b"/> must be non-zero.</summary>
    public static ushort Div(ushort a, ushort b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Division by zero in GF(2^16).");
        }

        return a == 0 ? (ushort)0 : ExpTable[LogTable[a] - LogTable[b] + Order];
    }

    /// <summary>α^e for 0 &lt;= e &lt; 131070, without a modulo (hot loops keep their exponent in range).</summary>
    internal static ushort ExpAt(int e) => ExpTable[e];

    /// <summary><c>a · α^e</c> for 0 &lt;= e &lt; 65535 (the hot path of syndrome accumulation).</summary>
    internal static ushort MulAlphaPow(ushort a, int e) => a == 0 ? (ushort)0 : ExpTable[LogTable[a] + e];
}

/// <summary>
/// Layout of the CTDB parity code over a disc's audio. The audio is a sequence of 16-bit little-endian symbols
/// (left and right samples alternate). It is cut into rows ("strides") of 11,760 symbols (10 sectors); symbol
/// column <c>p</c> of rows 1..n forms one Reed-Solomon codeword, where row 0 and the last 1..2 rows are left out
/// exactly as for the CTDB CRC-32 window (<see cref="CtdbChecksum.GetDiscSampleWindow"/>).
/// </summary>
public sealed class CtdbParityLayout
{
    /// <summary>Symbols per row: 5,880 stereo samples × 2 channels.</summary>
    public const int Stride = CtdbChecksum.StrideSamples * 2;

    /// <summary>Largest number of syndromes per column used for repair (CUETools uses at most 16).</summary>
    public const int MaxNpar = 16;

    /// <summary>Creates the layout for a disc of <paramref name="totalDiscAudioSamples"/> stereo samples.</summary>
    public CtdbParityLayout(long totalDiscAudioSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDiscAudioSamples);
        TotalSymbols = totalDiscAudioSamples * 2;
        long rows = TotalSymbols / Stride - 2;
        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalDiscAudioSamples), "The disc is too short for CTDB parity (fewer than 3 rows of 5,880 samples).");
        }

        if (rows + MaxNpar > CtdbGalois16.Order)
        {
            throw new ArgumentOutOfRangeException(nameof(totalDiscAudioSamples), "The disc is too long for CTDB parity.");
        }

        RowCount = (int)rows;
    }

    /// <summary>Total 16-bit symbols in the disc audio.</summary>
    public long TotalSymbols { get; }

    /// <summary>Number of rows that make up each codeword (CUETools' <c>stridecount</c>).</summary>
    public int RowCount { get; }

    /// <summary>
    /// Index in the disc symbol stream of codeword symbol <paramref name="row"/> (0-based within the codeword) of
    /// column <paramref name="column"/>, when the rip is shifted by <paramref name="shiftSymbols"/> against the
    /// database entry (see <see cref="CtdbSyndromeAccumulator.GetShiftedSyndromes"/>).
    /// </summary>
    public long SymbolIndex(int column, int row, int shiftSymbols) =>
        Stride + (long)shiftSymbols + column + (long)row * Stride;
}

/// <summary>
/// Streams a disc's audio once and computes, for every one of the 11,760 columns, the Reed-Solomon syndromes
/// <c>S_i = Σ_k d_k · α^(i·(n-1-k))</c> for i = 0..npar-1 over the column's n data symbols d_0..d_(n-1).
/// These are the values CTDB stores for each submission, so the XOR of a local and a stored syndrome set is the
/// syndrome set of the difference between the two rips. Rows 0, 1, n and n+1 are kept as raw samples so that
/// syndromes can be re-aligned for a rip that is shifted by less than one row against the database entry.
/// </summary>
public sealed class CtdbSyndromeAccumulator
{
    private const int Stride = CtdbParityLayout.Stride;

    private readonly CtdbParityLayout _layout;
    private readonly int _npar;
    private readonly ushort[] _syndromes; // [column * npar + i]
    private readonly ushort[] _row0 = new ushort[Stride];
    private readonly ushort[] _row1 = new ushort[Stride];
    private readonly ushort[] _rowN = new ushort[Stride];
    private readonly ushort[] _rowN1 = new ushort[Stride];
    private long _symbolIndex;
    private int _pendingByte = -1;

    /// <summary>Creates an accumulator for a disc with the given layout and number of syndromes per column.</summary>
    public CtdbSyndromeAccumulator(CtdbParityLayout layout, int npar = CtdbParityLayout.MaxNpar)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (npar is < 2 or > CtdbParityLayout.MaxNpar || npar % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(npar), "npar must be an even number from 2 to 16.");
        }

        _layout = layout;
        _npar = npar;
        _syndromes = new ushort[Stride * npar];
    }

    /// <summary>The layout this accumulator was created for.</summary>
    public CtdbParityLayout Layout => _layout;

    /// <summary>Number of syndromes computed per column.</summary>
    public int Npar => _npar;

    /// <summary>True once every symbol of the disc has been appended.</summary>
    public bool IsComplete => _symbolIndex == _layout.TotalSymbols && _pendingByte < 0;

    /// <summary>Appends disc audio bytes in order. Chunks need not be sample-aligned.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        int pos = 0;
        if (_pendingByte >= 0 && pcm.Length > 0)
        {
            AppendSymbol((ushort)(_pendingByte | (pcm[0] << 8)));
            _pendingByte = -1;
            pos = 1;
        }

        for (; pos + 1 < pcm.Length; pos += 2)
        {
            AppendSymbol((ushort)(pcm[pos] | (pcm[pos + 1] << 8)));
        }

        if (pos < pcm.Length)
        {
            _pendingByte = pcm[pos];
        }
    }

    private void AppendSymbol(ushort d)
    {
        long g = _symbolIndex++;
        if (g >= _layout.TotalSymbols)
        {
            throw new InvalidOperationException("More audio was appended than the disc layout holds.");
        }

        long rowLong = g / Stride;
        int column = (int)(g - rowLong * Stride);
        int row = (int)rowLong;
        int n = _layout.RowCount;

        if (row == 0) _row0[column] = d;
        if (row == 1) _row1[column] = d;
        if (row == n) _rowN[column] = d;
        if (row == n + 1) _rowN1[column] = d;

        if (row < 1 || row > n)
        {
            return;
        }

        // Horner step for every syndrome: S_i <- S_i · α^i + d.
        int baseIndex = column * _npar;
        _syndromes[baseIndex] ^= d;
        for (int i = 1; i < _npar; i++)
        {
            _syndromes[baseIndex + i] = (ushort)(CtdbGalois16.MulAlphaPow(_syndromes[baseIndex + i], i) ^ d);
        }
    }

    /// <summary>
    /// Syndromes of the rip re-aligned by <paramref name="shiftSymbols"/> (an even number with
    /// |shift| &lt; 11,760): column p then holds the symbols at <c>11,760 + shift + p + k·11,760</c>.
    /// A shift of <c>+2·o</c> matches a database entry whose audio starts <c>o</c> samples later than this rip
    /// (CUETools reports that as an offset of <c>-o</c>).
    /// Returns <paramref name="npar"/> values per column, laid out <c>[column * npar + i]</c>.
    /// </summary>
    public ushort[] GetShiftedSyndromes(int shiftSymbols, int npar, int firstColumn = 0, int columnCount = Stride)
    {
        EnsureComplete();
        ValidateShift(shiftSymbols);
        if (npar < 1 || npar > _npar)
        {
            throw new ArgumentOutOfRangeException(nameof(npar));
        }

        if (firstColumn < 0 || columnCount < 0 || firstColumn + columnCount > Stride)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }

        var result = new ushort[columnCount * npar];
        int n = _layout.RowCount;
        for (int c = 0; c < columnCount; c++)
        {
            int p = firstColumn + c;
            int q = p + shiftSymbols;
            for (int i = 0; i < npar; i++)
            {
                ushort value;
                if (q >= 0 && q < Stride)
                {
                    value = _syndromes[q * _npar + i];
                }
                else if (q >= Stride)
                {
                    // Rows 2..n+1 of column q': drop row 1, add row n+1.
                    int qq = q - Stride;
                    ushort s = _syndromes[qq * _npar + i];
                    s ^= CtdbGalois16.Mul(_row1[qq], CtdbGalois16.Pow((long)i * (n - 1)));
                    value = (ushort)(CtdbGalois16.Mul(s, CtdbGalois16.Pow(i)) ^ _rowN1[qq]);
                }
                else
                {
                    // Rows 0..n-1 of column q': add row 0, drop row n.
                    int qq = q + Stride;
                    ushort s = (ushort)(_syndromes[qq * _npar + i] ^ _rowN[qq]);
                    s = CtdbGalois16.Mul(s, CtdbGalois16.Pow(-i));
                    value = (ushort)(s ^ CtdbGalois16.Mul(_row0[qq], CtdbGalois16.Pow((long)i * (n - 1))));
                }

                result[c * npar + i] = value;
            }
        }

        return result;
    }

    private void EnsureComplete()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                $"Only {_symbolIndex} of {_layout.TotalSymbols} audio symbols were appended.");
        }
    }

    internal static void ValidateShift(int shiftSymbols)
    {
        if (shiftSymbols % 2 != 0 || shiftSymbols <= -Stride || shiftSymbols >= Stride)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shiftSymbols), "The shift must be an even number of symbols smaller than one row (11,760).");
        }
    }
}

/// <summary>
/// Reed-Solomon error correction for one CTDB column: Berlekamp-Massey for the error locator, a search of the
/// column's n positions for its roots, and Forney's formula for the error values (first consecutive root α^0).
/// Corrects up to npar/2 wrong symbols per column and reports anything beyond that as uncorrectable.
/// </summary>
public static class CtdbReedSolomon
{
    /// <summary>
    /// Decodes the error syndromes <paramref name="errorSyndromes"/> (local XOR stored) of one column of
    /// <paramref name="rowCount"/> symbols. On success, <paramref name="rows"/> and <paramref name="values"/> receive
    /// <paramref name="errorCount"/> codeword rows (0-based) and the values to XOR into those symbols.
    /// </summary>
    /// <param name="errorSyndromes">npar error syndromes.</param>
    /// <param name="rowCount">Symbols in the column.</param>
    /// <param name="rows">Receives the rows in error.</param>
    /// <param name="values">Receives the error values.</param>
    /// <param name="errorCount">Number of errors found.</param>
    /// <param name="maxErrors">
    /// Largest number of errors to accept (default and upper limit npar/2). A smaller limit makes a false match
    /// much less likely: random syndromes almost always fit a locator of degree npar/2, rarely one of lower degree.
    /// </param>
    /// <returns>False when the damage in this column is beyond what its syndromes can correct.</returns>
    public static bool TryDecode(
        ReadOnlySpan<ushort> errorSyndromes,
        int rowCount,
        Span<int> rows,
        Span<ushort> values,
        out int errorCount,
        int maxErrors = int.MaxValue)
    {
        int npar = errorSyndromes.Length;
        errorCount = 0;
        if (npar < 2 || rowCount < 1 || rowCount + npar > CtdbGalois16.Order)
        {
            throw new ArgumentOutOfRangeException(nameof(errorSyndromes));
        }

        bool anyError = false;
        foreach (ushort s in errorSyndromes)
        {
            anyError |= s != 0;
        }

        if (!anyError)
        {
            return true;
        }

        // Berlekamp-Massey: smallest LFSR (error locator Λ, Λ(0) = 1) that generates the syndromes.
        Span<ushort> lambda = stackalloc ushort[npar + 1];
        Span<ushort> prev = stackalloc ushort[npar + 1];
        Span<ushort> temp = stackalloc ushort[npar + 1];
        lambda.Clear();
        prev.Clear();
        lambda[0] = 1;
        prev[0] = 1;
        int degree = 0;
        int gap = 1;
        ushort prevDiscrepancy = 1;

        for (int r = 0; r < npar; r++)
        {
            ushort d = errorSyndromes[r];
            for (int j = 1; j <= degree; j++)
            {
                d ^= CtdbGalois16.Mul(lambda[j], errorSyndromes[r - j]);
            }

            if (d == 0)
            {
                gap++;
                continue;
            }

            ushort scale = CtdbGalois16.Div(d, prevDiscrepancy);
            if (2 * degree <= r)
            {
                lambda.CopyTo(temp);
                for (int j = 0; j + gap <= npar; j++)
                {
                    lambda[j + gap] ^= CtdbGalois16.Mul(scale, prev[j]);
                }

                degree = r + 1 - degree;
                temp.CopyTo(prev);
                prevDiscrepancy = d;
                gap = 1;
            }
            else
            {
                for (int j = 0; j + gap <= npar; j++)
                {
                    lambda[j + gap] ^= CtdbGalois16.Mul(scale, prev[j]);
                }

                gap++;
            }
        }

        maxErrors = Math.Min(maxErrors, npar / 2);
        if (degree == 0 || degree > maxErrors || degree > rows.Length || degree > values.Length || lambda[degree] == 0)
        {
            return false;
        }

        if (!FindErrorRows(lambda[..(degree + 1)], rowCount, rows[..degree]))
        {
            return false; // locator roots fall outside the column: too much damage
        }

        // Ω(z) = S(z) · Λ(z) mod z^npar.
        Span<ushort> omega = stackalloc ushort[npar];
        omega.Clear();
        for (int i = 0; i < npar; i++)
        {
            for (int j = 0; j <= degree && j <= i; j++)
            {
                omega[i] ^= CtdbGalois16.Mul(errorSyndromes[i - j], lambda[j]);
            }
        }

        // Forney: e = X · Ω(X^-1) / Λ'(X^-1); Λ' keeps only the odd-power terms in characteristic 2.
        for (int e = 0; e < degree; e++)
        {
            int locatorLog = rowCount - 1 - rows[e];
            ushort xInv = CtdbGalois16.Pow(-locatorLog);
            ushort xInvSquared = CtdbGalois16.Mul(xInv, xInv);
            ushort derivative = 0;
            ushort xInvPow = 1; // (X^-1)^(j-1) for odd j
            for (int j = 1; j <= degree; j += 2)
            {
                derivative ^= CtdbGalois16.Mul(lambda[j], xInvPow);
                xInvPow = CtdbGalois16.Mul(xInvPow, xInvSquared);
            }

            if (derivative == 0)
            {
                return false;
            }

            ushort value = CtdbGalois16.Mul(CtdbGalois16.Pow(locatorLog), CtdbGalois16.Div(Evaluate(omega, xInv), derivative));
            if (value == 0)
            {
                return false;
            }

            values[e] = value;
        }

        errorCount = degree;
        return true;
    }

    /// <summary>
    /// Finds the rows k (locator X = α^(n-1-k)) where Λ(X^-1) = 0. Requires exactly deg Λ distinct roots inside the
    /// column. Degree 1 is solved directly; higher degrees step through the rows, multiplying term j by α^j per row.
    /// </summary>
    private static bool FindErrorRows(ReadOnlySpan<ushort> lambda, int rowCount, Span<int> rows)
    {
        int degree = lambda.Length - 1;
        if (degree == 1)
        {
            // Λ(z) = 1 + λ1·z has its root at z = 1/λ1, so X = λ1.
            int locatorLog = CtdbGalois16.Log(lambda[1]);
            if (locatorLog >= rowCount)
            {
                return false;
            }

            rows[0] = rowCount - 1 - locatorLog;
            return true;
        }

        // term_j(k) = λ_j · (X_k^-1)^j with X_k^-1 = α^-(n-1-k): start at k = 0 and multiply by α^j per step.
        Span<int> termLog = stackalloc int[degree + 1];
        for (int j = 1; j <= degree; j++)
        {
            termLog[j] = lambda[j] == 0
                ? -1
                : (int)(((long)CtdbGalois16.Log(lambda[j]) - (long)j * (rowCount - 1)) % CtdbGalois16.Order + CtdbGalois16.Order) % CtdbGalois16.Order;
        }

        int found = 0;
        for (int k = 0; k < rowCount; k++)
        {
            int sum = 1;
            for (int j = 1; j <= degree; j++)
            {
                int t = termLog[j];
                if (t < 0)
                {
                    continue;
                }

                sum ^= CtdbGalois16.ExpAt(t);
                t += j;
                termLog[j] = t >= CtdbGalois16.Order ? t - CtdbGalois16.Order : t;
            }

            if (sum == 0)
            {
                rows[found++] = k;
                if (found == degree)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ushort Evaluate(ReadOnlySpan<ushort> coefficients, ushort x)
    {
        ushort result = 0;
        for (int j = coefficients.Length - 1; j >= 0; j--)
        {
            result = (ushort)(CtdbGalois16.Mul(result, x) ^ coefficients[j]);
        }

        return result;
    }
}
