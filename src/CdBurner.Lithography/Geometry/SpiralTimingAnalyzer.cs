using System.Globalization;
using System.Text.Json;

namespace CdBurner.Lithography.Geometry;

/// <summary>One timed sector read in a spiral-mapping capture (<c>i,lba,done_us,took_us,ok</c>).</summary>
public readonly record struct TimingCaptureSample(
    int Index,
    int Lba,
    double DoneMicroseconds,
    double TookMicroseconds,
    bool Ok);

/// <summary>Result of analysing a read-timing capture into a <see cref="MeasuredTurnCorrection"/>.</summary>
public sealed record SpiralTimingMeasurementResult
{
    public bool Success { get; init; }
    public string? RefusalReason { get; init; }
    public MeasuredTurnCorrection? TurnCorrection { get; init; }
    public double TurnCount { get; init; }
    public double FirstStationSector { get; init; }
    public double LastStationSector { get; init; }
    public int StationCount { get; init; }
    public double RotationPeriodMicroseconds { get; init; }
    public double RotationRpm { get; init; }
    public double PeriodDriftPpm { get; init; }
    public double WholeTurnScatterDegrees { get; init; }
    public double StationScatterMedianDegrees { get; init; }
    public double StationScatterWorstDegrees { get; init; }
    public double ChainMarginTurns { get; init; }
    public int TotalReadings { get; init; }
    public int KeptReadings { get; init; }
    public int DroppedReadings { get; init; }
    public int FailedReadings { get; init; }
    public int CacheHitReadings { get; init; }
    public int UnsteadyReadings { get; init; }
    public int SmoothingDegree { get; init; }
    public double SmoothingLooErrorDegrees { get; init; }
    public double SmoothingResidualDegrees { get; init; }

    /// <summary>Total turns between two sector addresses according to the fitted spiral and smoothed table.</summary>
    public double TurnsBetween(double fromSector, double toSector)
    {
        if (TurnCorrection is null)
        {
            throw new InvalidOperationException("No turn correction table was produced.");
        }

        return TotalTurnsAt(TurnCorrection, toSector) - TotalTurnsAt(TurnCorrection, fromSector);
    }

    internal static double TotalTurnsAt(MeasuredTurnCorrection table, double sector)
    {
        double r0 = table.InnerRadiusMm;
        double k = table.RadiusSquaredPerSectorMm2;
        double baseTurns = table.SectorLengthMm * (Math.Sqrt((r0 * r0) + (k * sector)) - r0) / (Math.PI * k);
        return baseTurns + table.TurnsAt(sector);
    }
}

/// <summary>
/// Turns an alternating anchor/station read-timing capture into a smoothed <see cref="MeasuredTurnCorrection"/> table.
/// </summary>
public static class SpiralTimingAnalyzer
{
    /// <summary>Nominal CD sector track length: 1,200 mm/s divided by 75 sectors/s.</summary>
    public const double DefaultSectorLengthMm = 16.0;

    /// <summary>Cycles shorter than 60 ms come from the drive's read-ahead cache rather than a disc revolution.</summary>
    public const double CacheHitThresholdMicroseconds = 60_000.0;

    /// <summary>Largest accepted distance of an anchor-to-anchor cycle from an integer number of turns.</summary>
    public const double WholeTurnTolerance = 0.02;

    /// <summary>Refuse the capture when more than this share of non-cache cycles fail the whole-turn check.</summary>
    public const double MaxUnsteadyFraction = 0.10;

    /// <summary>Refuse the capture when station-to-station chaining comes closer than 0.25 turn to a slip.</summary>
    public const double MaxChainMarginTurns = 0.25;

    private const int MinimumStations = 4;
    private const int MinimumStationGoodReads = 8;
    private const double MinPeriodMicroseconds = 8_000.0;
    private const double MaxPeriodMicroseconds = 60_000.0;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Reads a capture CSV written by <see cref="WriteCaptureCsv"/> or the timing probe.</summary>
    public static IReadOnlyList<TimingCaptureSample> ReadCaptureCsv(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var samples = new List<TimingCaptureSample>();
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (header is null)
        {
            throw new InvalidDataException($"Capture file '{Path.GetFileName(path)}' is empty.");
        }

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length < 5)
            {
                throw new InvalidDataException($"Capture line has fewer than 5 columns: '{line}'.");
            }

            int index = int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
            int lba = int.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            double doneUs = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
            double tookUs = double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture);
            bool ok = int.Parse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture) != 0;
            samples.Add(new TimingCaptureSample(index, lba, doneUs, tookUs, ok));
        }

        return samples;
    }

    /// <summary>Writes the raw timing capture alongside the turn table.</summary>
    public static void WriteCaptureCsv(string path, IReadOnlyList<TimingCaptureSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var writer = new StreamWriter(path);
        writer.WriteLine("i,lba,done_us,took_us,ok");
        for (int i = 0; i < samples.Count; i++)
        {
            TimingCaptureSample s = samples[i];
            writer.WriteLine(FormattableString.Invariant(
                $"{s.Index},{s.Lba},{s.DoneMicroseconds:F1},{s.TookMicroseconds:F1},{(s.Ok ? 1 : 0)}"));
        }
    }

    /// <summary>Writes the turn table in the JSON schema read by <see cref="MeasuredTurnCorrection.Load"/>.</summary>
    public static void WriteTurnMapJson(string path, MeasuredTurnCorrection table)
    {
        ArgumentNullException.ThrowIfNull(table);
        WriteTurnMapJson(
            path,
            table.InnerRadiusMm,
            table.RadiusSquaredPerSectorMm2,
            table.SectorLengthMm,
            table.StationSectors,
            table.CorrectionTurns);
    }

    /// <summary>Writes the turn table in the JSON schema read by <see cref="MeasuredTurnCorrection.Load"/>.</summary>
    public static void WriteTurnMapJson(
        string path,
        double innerRadiusMm,
        double radiusSquaredPerSectorMm2,
        double sectorLengthMm,
        IReadOnlyList<double> stationSectors,
        IReadOnlyList<double> correctionTurns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(stationSectors);
        ArgumentNullException.ThrowIfNull(correctionTurns);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            innerRadiusMm,
            radiusSquaredPerSectorMm2,
            sectorLengthMm,
            stationSectors,
            correctionTurns,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Analyses an alternating anchor/sample capture and returns either a smoothed <see cref="MeasuredTurnCorrection"/>
    /// or a plain refusal reason when the spin is unsteady, the chain margin is unsafe, or data is insufficient.
    /// </summary>
    public static SpiralTimingMeasurementResult Analyze(
        IReadOnlyList<TimingCaptureSample> samples,
        double sectorLengthMm = DefaultSectorLengthMm,
        double? periodGuessMicroseconds = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(sectorLengthMm) || sectorLengthMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorLengthMm), sectorLengthMm, "Sector length must be positive.");
        }

        if (samples.Count < 3 || samples.Count % 2 == 0)
        {
            return Refused("The capture has an unexpected row layout (expected alternating anchor and sample reads).");
        }

        int anchorLba = samples[0].Lba;
        int pairCount = (samples.Count - 1) / 2;
        double[] x = new double[pairCount];
        double[] d1 = new double[pairCount];
        double[] cyc = new double[pairCount];
        bool[] good = new bool[pairCount];

        int failedCount = 0;
        int cacheHitCount = 0;
        int candidateCount = 0;

        for (int i = 0; i < pairCount; i++)
        {
            TimingCaptureSample g0 = samples[2 * i];
            TimingCaptureSample s = samples[2 * i + 1];
            TimingCaptureSample g1 = samples[2 * i + 2];

            x[i] = s.Lba;
            d1[i] = s.DoneMicroseconds - g0.DoneMicroseconds;
            cyc[i] = g1.DoneMicroseconds - g0.DoneMicroseconds;

            bool okPair = g0.Ok && s.Ok && g1.Ok
                && double.IsFinite(d1[i]) && double.IsFinite(cyc[i])
                && d1[i] > 0.0 && cyc[i] > 0.0;

            if (!okPair)
            {
                failedCount++;
                continue;
            }

            if (cyc[i] <= CacheHitThresholdMicroseconds || s.Lba == anchorLba)
            {
                cacheHitCount++;
                continue;
            }

            good[i] = true;
            candidateCount++;
        }

        if (candidateCount < MinimumStations * MinimumStationGoodReads)
        {
            return Refused(
                $"Too few readable non-cache timing readings ({candidateCount} of {pairCount}).",
                total: pairCount, failed: failedCount, cacheHit: cacheHitCount);
        }

        double? periodOpt = FindRotationPeriod(cyc, good, candidateCount, periodGuessMicroseconds);
        if (periodOpt is not double t0)
        {
            return Refused(
                "The drive's spin rate is not steady enough to determine a rotation period.",
                total: pairCount, failed: failedCount, cacheHit: cacheHitCount);
        }

        double[] kTurns = new double[pairCount];
        double[] ratioScratch = new double[candidateCount];
        int rIdx = 0;
        for (int i = 0; i < pairCount; i++)
        {
            kTurns[i] = Math.Round(cyc[i] / t0);
            if (good[i] && kTurns[i] >= 1.0)
            {
                ratioScratch[rIdx++] = cyc[i] / kTurns[i];
            }
        }

        if (rIdx == 0)
        {
            return Refused(
                "The drive's spin rate is not steady enough to determine a rotation period.",
                total: pairCount, failed: failedCount, cacheHit: cacheHitCount);
        }

        t0 = Median(ratioScratch.AsSpan(0, rIdx));
        int unsteadyCount = 0;
        int keptCount = 0;
        for (int i = 0; i < pairCount; i++)
        {
            double k = Math.Round(cyc[i] / t0);
            kTurns[i] = k;
            if (!good[i])
            {
                continue;
            }

            double offTurn = Math.Abs(cyc[i] - (k * t0)) / t0;
            if (offTurn > WholeTurnTolerance)
            {
                good[i] = false;
                unsteadyCount++;
            }
            else
            {
                keptCount++;
            }
        }

        if (unsteadyCount > candidateCount * MaxUnsteadyFraction)
        {
            return Refused(
                $"The drive's spin is not steady: {unsteadyCount} of {candidateCount} non-cache cycles were more than {WholeTurnTolerance:0.00} turn from a whole rotation.",
                total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
        }

        if (keptCount < MinimumStations * MinimumStationGoodReads)
        {
            return Refused(
                $"Too few steady timing readings remained ({keptCount} of {pairCount}).",
                total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
        }

        double[] rawPeriod = new double[pairCount];
        for (int i = 0; i < pairCount; i++)
        {
            rawPeriod[i] = cyc[i] / Math.Max(1.0, kTurns[i]);
        }

        double[] tLoc = new double[pairCount];
        double[] windowScratch = new double[51];
        for (int i = 0; i < pairCount; i++)
        {
            int lo = Math.Max(0, i - 25);
            int hi = Math.Min(pairCount - 1, i + 25);
            int wCount = 0;
            for (int j = lo; j <= hi; j++)
            {
                if (good[j])
                {
                    windowScratch[wCount++] = rawPeriod[j];
                }
            }

            tLoc[i] = wCount > 0 ? Median(windowScratch.AsSpan(0, wCount)) : t0;
        }

        double medianPeriod = MedianCopy(tLoc);
        double minTloc = double.PositiveInfinity;
        double maxTloc = double.NegativeInfinity;
        for (int i = 0; i < pairCount; i++)
        {
            if (tLoc[i] < minTloc) minTloc = tLoc[i];
            if (tLoc[i] > maxTloc) maxTloc = tLoc[i];
        }

        double driftPpm = ((maxTloc - minTloc) / medianPeriod) * 1_000_000.0;

        double[] cycleErrors = new double[keptCount];
        double[] phase = new double[pairCount];
        int eIdx = 0;
        for (int i = 0; i < pairCount; i++)
        {
            double frac = d1[i] / tLoc[i];
            phase[i] = frac - Math.Floor(frac);
            if (good[i])
            {
                cycleErrors[eIdx++] = (cyc[i] - (kTurns[i] * tLoc[i])) / tLoc[i];
            }
        }

        double wholeTurnScatterDeg = PopulationStdDev(cycleErrors) * 360.0;

        // Group samples into stations (offsets 0, 1, 2, 4, ...).
        var stations = GroupStations(x, good);
        if (stations.Count < MinimumStations)
        {
            return Refused(
                $"Too few valid stations ({stations.Count}; at least {MinimumStations} are needed).",
                total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
        }

        // Step 1: sectors per turn at each station from small offsets (<= 32).
        var initialA0 = new List<double>(stations.Count);
        var initialNSq = new List<double>(stations.Count);
        for (int s = 0; s < stations.Count; s++)
        {
            int[] idx = stations[s];
            double baseSector = x[idx[0]];
            var smallOff = new List<double>(8);
            var smallPh = new List<double>(8);
            for (int j = 0; j < idx.Length; j++)
            {
                double off = x[idx[j]] - baseSector;
                if (off <= 32.0)
                {
                    smallOff.Add(off);
                    smallPh.Add(phase[idx[j]]);
                }
            }

            if (smallOff.Count < 2)
            {
                continue;
            }

            double[] unwrapped = new double[smallOff.Count];
            unwrapped[0] = smallPh[0];
            double? rate = null;
            for (int j = 1; j < smallOff.Count; j++)
            {
                if (rate is null)
                {
                    unwrapped[j] = smallPh[j] + (smallPh[j] < smallPh[0] ? 1.0 : 0.0);
                }
                else
                {
                    unwrapped[j] = smallPh[j] + Math.Round(unwrapped[0] + (rate.Value * smallOff[j]) - smallPh[j]);
                }

                rate = (unwrapped[j] - unwrapped[0]) / smallOff[j];
            }

            if (LinearRegression(smallOff, unwrapped, out double slope, out _) && slope > 0.0)
            {
                initialA0.Add(baseSector);
                double nLocal = 1.0 / slope;
                initialNSq.Add(nLocal * nLocal);
            }
        }

        if (initialA0.Count < MinimumStations
            || !LinearRegression(initialA0, initialNSq, out double c, out double n0sq)
            || !double.IsFinite(c) || !double.IsFinite(n0sq) || c <= 0.0 || n0sq <= 0.0)
        {
            return Refused(
                "Could not establish an initial spiral pitch from the station readings.",
                total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
        }

        int m = stations.Count;
        double[] a0 = new double[m];
        for (int i = 0; i < m; i++)
        {
            a0[i] = x[stations[i][0]];
        }

        for (int i = 0; i < m; i++)
        {
            if (n0sq + (c * a0[i]) <= 0.0)
            {
                return Refused(
                    "The spiral fit diverged (invalid radius growth); the spin rate may be unsteady.",
                    total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
            }
        }

        // Step 2: 4 passes of unwrapping against constant-pitch model + station chaining + Gauss-Newton refit.
        double[] turns = new double[m];
        double[] res = new double[m];
        double[] rho = new double[m];
        double[] sig = new double[m];
        double[] scat = new double[m];
        double[] un = new double[m];
        double chainMargin = 0.0;
        double pOffset = 0.0;

        for (int iter = 0; iter < 4; iter++)
        {
            for (int s = 0; s < m; s++)
            {
                int[] idx = stations[s];
                double baseSector = a0[s];
                double[] off = new double[idx.Length];
                double[] r = new double[idx.Length];
                for (int j = 0; j < idx.Length; j++)
                {
                    off[j] = x[idx[j]] - baseSector;
                    r[j] = WrapHalf(phase[idx[j]] - ConstantSpiralModel(n0sq, c, x[idx[j]]));
                    if (j > 0)
                    {
                        r[j] = r[0] + WrapHalf(r[j] - r[0]);
                    }
                }

                if (!LinearRegression(off, r, out double sl, out double ic))
                {
                    return Refused(
                        "Station residual regression failed.",
                        total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
                }

                rho[s] = ic;
                sig[s] = sl;

                double[] resid = new double[idx.Length];
                for (int j = 0; j < idx.Length; j++)
                {
                    resid[j] = r[j] - ((sl * off[j]) + ic);
                }

                scat[s] = PopulationStdDev(resid);
            }

            un[0] = 0.0;
            chainMargin = 0.0;
            for (int i = 1; i < m; i++)
            {
                double pred = 0.5 * (sig[i] + sig[i - 1]) * (a0[i] - a0[i - 1]);
                double d = rho[i] - rho[i - 1];
                double whole = Math.Round(pred - d);
                double margin = Math.Abs(pred - d - whole);
                if (margin > chainMargin)
                {
                    chainMargin = margin;
                }

                un[i] = un[i - 1] + d + whole;
            }

            for (int i = 0; i < m; i++)
            {
                turns[i] = ConstantSpiralModel(n0sq, c, a0[i]) + un[i];
            }

            double p0 = n0sq;
            double p1 = c;
            double p2 = 0.0;
            double[] rhs = new double[m];
            double[,] jac = new double[m, 3];

            for (int gn = 0; gn < 30; gn++)
            {
                double e0 = p0 * 1e-7;
                double e1 = p1 * 1e-7;
                for (int i = 0; i < m; i++)
                {
                    double f0 = ConstantSpiralModel(p0, p1, a0[i]);
                    rhs[i] = turns[i] - (f0 + p2);
                    jac[i, 0] = (ConstantSpiralModel(p0 + e0, p1, a0[i]) - f0) / e0;
                    jac[i, 1] = (ConstantSpiralModel(p0, p1 + e1, a0[i]) - f0) / e1;
                    jac[i, 2] = 1.0;
                }

                if (!SolveScaledLeastSquares(jac, rhs, out double[] dp))
                {
                    return Refused(
                        "Constant-pitch spiral fit did not converge.",
                        total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
                }

                // Backtrack if a step leaves positive domain (n0sq > 0, c > 0, n0sq + c*a > 0).
                double step = 1.0;
                while (step > 1e-4)
                {
                    double np0 = p0 + (step * dp[0]);
                    double np1 = p1 + (step * dp[1]);
                    if (np0 > 0.0 && np1 > 0.0 && IsValidSpiralDomain(np0, np1, a0))
                    {
                        p0 = np0;
                        p1 = np1;
                        p2 += step * dp[2];
                        break;
                    }

                    step *= 0.5;
                }

                if (step <= 1e-4)
                {
                    return Refused(
                        "Constant-pitch spiral fit left valid disc radii (spin rate may be unsteady).",
                        total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount);
                }
            }

            n0sq = p0;
            c = p1;
            pOffset = p2;
        }

        if (!double.IsFinite(chainMargin) || chainMargin > MaxChainMarginTurns)
        {
            return Refused(
                FormattableString.Invariant(
                    $"Could not chain stations reliably: worst chain margin is {chainMargin:0.000} turn (limit {MaxChainMarginTurns:0.00} turn)."),
                total: pairCount, kept: keptCount, failed: failedCount, cacheHit: cacheHitCount, unsteady: unsteadyCount,
                chainMargin: chainMargin);
        }

        for (int i = 0; i < m; i++)
        {
            res[i] = turns[i] - (ConstantSpiralModel(n0sq, c, a0[i]) + pOffset);
        }

        // Step 3: Chebyshev smoothing with leave-one-out cross-validation.
        double aMin = a0[0];
        double aMax = a0[^1];
        double span = aMax - aMin;
        if (span <= 0.0)
        {
            return Refused("Station span is zero.");
        }

        double[] uu = new double[m];
        for (int i = 0; i < m; i++)
        {
            double u = (a0[i] - aMin) / span;
            uu[i] = (2.0 * u) - 1.0;
        }

        int maxDeg = Math.Min(20, m - 2);
        double[] cvErrors = new double[maxDeg + 1];
        double[][] fits = new double[maxDeg + 1][];
        double minCv = double.PositiveInfinity;

        for (int deg = 0; deg <= maxDeg; deg++)
        {
            double[,] vander = ChebyshevVandermonde(uu, deg);
            if (!ComputeOrthonormalFit(vander, res, out double[] fit, out double[] hatDiag))
            {
                cvErrors[deg] = double.PositiveInfinity;
                fits[deg] = res;
                continue;
            }

            double sumSq = 0.0;
            for (int i = 0; i < m; i++)
            {
                double denom = 1.0 - Math.Clamp(hatDiag[i], 0.0, 0.99);
                double loo = (res[i] - fit[i]) / denom;
                sumSq += loo * loo;
            }

            double rmse = Math.Sqrt(sumSq / m);
            cvErrors[deg] = rmse;
            fits[deg] = fit;
            if (rmse < minCv)
            {
                minCv = rmse;
            }
        }

        int bestDeg = 0;
        for (int deg = 0; deg <= maxDeg; deg++)
        {
            if (cvErrors[deg] <= 1.05 * minCv)
            {
                bestDeg = deg;
                break;
            }
        }

        double[] smooth = fits[bestDeg];
        double[] smoothDiff = new double[m];
        for (int i = 0; i < m; i++)
        {
            smoothDiff[i] = res[i] - smooth[i];
        }

        double kMm = sectorLengthMm / (2.0 * Math.PI);
        double innerRadiusMm = Math.Sqrt(n0sq) * kMm;
        double radiusSqPerSectorMm2 = c * kMm * kMm;

        var table = new MeasuredTurnCorrection(
            innerRadiusMm,
            radiusSqPerSectorMm2,
            sectorLengthMm,
            a0,
            smooth);

        return new SpiralTimingMeasurementResult
        {
            Success = true,
            TurnCorrection = table,
            TurnCount = turns[^1] - turns[0],
            FirstStationSector = a0[0],
            LastStationSector = a0[^1],
            StationCount = m,
            RotationPeriodMicroseconds = medianPeriod,
            RotationRpm = 60_000_000.0 / medianPeriod,
            PeriodDriftPpm = driftPpm,
            WholeTurnScatterDegrees = wholeTurnScatterDeg,
            StationScatterMedianDegrees = MedianCopy(scat) * 360.0,
            StationScatterWorstDegrees = Max(scat) * 360.0,
            ChainMarginTurns = chainMargin,
            TotalReadings = pairCount,
            KeptReadings = keptCount,
            DroppedReadings = pairCount - keptCount,
            FailedReadings = failedCount,
            CacheHitReadings = cacheHitCount,
            UnsteadyReadings = unsteadyCount,
            SmoothingDegree = bestDeg,
            SmoothingLooErrorDegrees = cvErrors[bestDeg] * 360.0,
            SmoothingResidualDegrees = PopulationStdDev(smoothDiff) * 360.0,
        };
    }

    private static SpiralTimingMeasurementResult Refused(
        string reason,
        int total = 0,
        int kept = 0,
        int failed = 0,
        int cacheHit = 0,
        int unsteady = 0,
        double chainMargin = 0.0) => new()
    {
        Success = false,
        RefusalReason = reason,
        TotalReadings = total,
        KeptReadings = kept,
        DroppedReadings = total - kept,
        FailedReadings = failed,
        CacheHitReadings = cacheHit,
        UnsteadyReadings = unsteady,
        ChainMarginTurns = chainMargin,
    };

    private static double? FindRotationPeriod(double[] cyc, bool[] good, int goodCount, double? guess)
    {
        double[] goodCyc = new double[goodCount];
        int idx = 0;
        for (int i = 0; i < cyc.Length; i++)
        {
            if (good[i])
            {
                goodCyc[idx++] = cyc[i];
            }
        }

        double[] err = new double[goodCount];
        if (guess is double g && double.IsFinite(g) && g > 0.0)
        {
            double lo = g * 0.99;
            double hi = g * 1.01;
            double bestT = lo;
            double bestMed = double.PositiveInfinity;
            for (double t = lo; t < hi; t += 0.25)
            {
                double med = PeriodMedianError(goodCyc, t, err);
                if (med < bestMed)
                {
                    bestMed = med;
                    bestT = t;
                }
            }

            return bestT;
        }

        // Subsample up to 64 evenly spaced readings for a fast coarse pre-filter, then verify exact 0.25 us steps.
        int subCount = Math.Min(goodCount, 64);
        double[] subCyc = new double[subCount];
        double[] subErr = new double[subCount];
        for (int i = 0; i < subCount; i++)
        {
            subCyc[i] = goodCyc[(int)((long)i * goodCount / subCount)];
        }

        // Scan 60,000 down to 8,000 in 1.0 us steps with a wider threshold, then test exact 0.25 us grid inside hits.
        // Measure cycle error in turns (|cyc - k*T|/T) so halves of the true period (T/2, T/3) do not look artificially cleaner,
        // and require >= 75% of cycles within WholeTurnTolerance so 2*T0 cannot win when >50% of cycles are even multiples.
        for (double coarse = MaxPeriodMicroseconds - 0.25; coarse >= MinPeriodMicroseconds; coarse -= 1.0)
        {
            if (PeriodMedianError(subCyc, coarse, subErr, out double minK, out double subInTol) >= 0.008
                || minK < 1.0
                || subInTol < 0.70)
            {
                continue;
            }

            double top = Math.Min(MaxPeriodMicroseconds - 0.25, coarse + 1.0);
            double bottom = Math.Max(MinPeriodMicroseconds, coarse - 1.0);
            for (double t = top; t >= bottom; t -= 0.25)
            {
                double med = PeriodMedianError(goodCyc, t, err, out double fullMinK, out double fullInTol);
                if (fullMinK >= 1.0 && med < 0.005 && fullInTol >= 0.75)
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static double PeriodMedianError(double[] cyc, double period, double[] scratch)
        => PeriodMedianError(cyc, period, scratch, out _, out _);

    private static double PeriodMedianError(double[] cyc, double period, double[] scratch, out double minK, out double fractionWithinTolerance)
    {
        minK = double.PositiveInfinity;
        int inTolerance = 0;
        for (int i = 0; i < cyc.Length; i++)
        {
            double k = Math.Round(cyc[i] / period);
            if (k < minK)
            {
                minK = k;
            }

            if (k >= 1.0)
            {
                double turnErr = Math.Abs(cyc[i] - (k * period)) / period;
                scratch[i] = turnErr;
                if (turnErr <= WholeTurnTolerance)
                {
                    inTolerance++;
                }
            }
            else
            {
                scratch[i] = double.PositiveInfinity;
            }
        }

        fractionWithinTolerance = cyc.Length > 0 ? (double)inTolerance / cyc.Length : 0.0;
        return Median(scratch.AsSpan(0, cyc.Length));
    }

    private static List<int[]> GroupStations(double[] x, bool[] good)
    {
        int n = x.Length;
        int[] stationOf = new int[n];
        int start = 0;
        for (int i = 1; i < n; i++)
        {
            double offPrev = x[i - 1] - x[start];
            double expect = x[start] + (offPrev == 0.0 ? 1.0 : offPrev * 2.0);
            if (x[i] != expect)
            {
                start = i;
                stationOf[i] = stationOf[i - 1] + 1;
            }
            else
            {
                stationOf[i] = stationOf[i - 1];
            }
        }

        int stationCount = stationOf[^1] + 1;
        var buckets = new List<int>[stationCount];
        var firstGood = new bool[stationCount];
        var hasFirst = new bool[stationCount];
        for (int i = 0; i < stationCount; i++)
        {
            buckets[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            int s = stationOf[i];
            if (!hasFirst[s])
            {
                hasFirst[s] = true;
                firstGood[s] = good[i];
            }

            if (good[i])
            {
                buckets[s].Add(i);
            }
        }

        var result = new List<int[]>(stationCount);
        for (int s = 0; s < stationCount; s++)
        {
            if (firstGood[s] && buckets[s].Count >= MinimumStationGoodReads)
            {
                result.Add([.. buckets[s]]);
            }
        }

        return result;
    }

    private static double ConstantSpiralModel(double n0sq, double c, double a)
        => (2.0 / c) * (Math.Sqrt(n0sq + (c * a)) - Math.Sqrt(n0sq));

    private static bool IsValidSpiralDomain(double n0sq, double c, double[] a0)
    {
        for (int i = 0; i < a0.Length; i++)
        {
            if (n0sq + (c * a0[i]) <= 0.0)
            {
                return false;
            }
        }

        return true;
    }

    private static double WrapHalf(double v)
    {
        double t = v + 0.5;
        return t - Math.Floor(t) - 0.5;
    }

    private static bool LinearRegression(IReadOnlyList<double> xs, IReadOnlyList<double> ys, out double slope, out double intercept)
    {
        int n = xs.Count;
        if (n < 2)
        {
            slope = 0.0;
            intercept = 0.0;
            return false;
        }

        double mx = 0.0;
        double my = 0.0;
        for (int i = 0; i < n; i++)
        {
            mx += xs[i];
            my += ys[i];
        }

        mx /= n;
        my /= n;

        double sxx = 0.0;
        double sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - mx;
            sxx += dx * dx;
            sxy += dx * (ys[i] - my);
        }

        if (sxx <= 0.0)
        {
            slope = 0.0;
            intercept = 0.0;
            return false;
        }

        slope = sxy / sxx;
        intercept = my - (slope * mx);
        return double.IsFinite(slope) && double.IsFinite(intercept);
    }

    private static bool SolveScaledLeastSquares(double[,] matrix, double[] rhs, out double[] solution)
    {
        int m = matrix.GetLength(0);
        int n = matrix.GetLength(1);
        solution = new double[n];

        double[,] a = (double[,])matrix.Clone();
        double[] b = (double[])rhs.Clone();
        double[] colScale = new double[n];

        for (int j = 0; j < n; j++)
        {
            double normSq = 0.0;
            for (int i = 0; i < m; i++)
            {
                normSq += a[i, j] * a[i, j];
            }

            if (normSq <= 0.0 || !double.IsFinite(normSq))
            {
                return false;
            }

            double scale = Math.Sqrt(normSq);
            colScale[j] = scale;
            for (int i = 0; i < m; i++)
            {
                a[i, j] /= scale;
            }
        }

        // Householder QR decomposition on scaled m x n matrix.
        for (int k = 0; k < n; k++)
        {
            double normSq = 0.0;
            for (int i = k; i < m; i++)
            {
                normSq += a[i, k] * a[i, k];
            }

            double norm = Math.Sqrt(normSq);
            if (norm <= 1e-14 || !double.IsFinite(norm))
            {
                return false;
            }

            double alpha = a[k, k] >= 0.0 ? -norm : norm;
            double v0 = a[k, k] - alpha;
            a[k, k] = alpha;

            double vNormSq = v0 * v0;
            for (int i = k + 1; i < m; i++)
            {
                vNormSq += a[i, k] * a[i, k];
            }

            if (vNormSq <= 0.0)
            {
                continue;
            }

            for (int j = k + 1; j < n; j++)
            {
                double dot = v0 * a[k, j];
                for (int i = k + 1; i < m; i++)
                {
                    dot += a[i, k] * a[i, j];
                }

                double factor = 2.0 * dot / vNormSq;
                a[k, j] -= factor * v0;
                for (int i = k + 1; i < m; i++)
                {
                    a[i, j] -= factor * a[i, k];
                }
            }

            double bDot = v0 * b[k];
            for (int i = k + 1; i < m; i++)
            {
                bDot += a[i, k] * b[i];
            }

            double bFactor = 2.0 * bDot / vNormSq;
            b[k] -= bFactor * v0;
            for (int i = k + 1; i < m; i++)
            {
                b[i] -= bFactor * a[i, k];
            }
        }

        for (int k = n - 1; k >= 0; k--)
        {
            double sum = b[k];
            for (int j = k + 1; j < n; j++)
            {
                sum -= a[k, j] * solution[j];
            }

            if (Math.Abs(a[k, k]) <= 1e-14)
            {
                return false;
            }

            solution[k] = sum / a[k, k];
        }

        for (int j = 0; j < n; j++)
        {
            solution[j] /= colScale[j];
            if (!double.IsFinite(solution[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static double[,] ChebyshevVandermonde(double[] x, int degree)
    {
        int m = x.Length;
        double[,] v = new double[m, degree + 1];
        for (int i = 0; i < m; i++)
        {
            v[i, 0] = 1.0;
            if (degree >= 1)
            {
                v[i, 1] = x[i];
            }

            for (int d = 2; d <= degree; d++)
            {
                v[i, d] = (2.0 * x[i] * v[i, d - 1]) - v[i, d - 2];
            }
        }

        return v;
    }

    private static bool ComputeOrthonormalFit(double[,] basis, double[] y, out double[] fit, out double[] hatDiagonal)
    {
        int m = basis.GetLength(0);
        int p = basis.GetLength(1);
        double[,] q = new double[m, p];

        // Modified Gram-Schmidt with one re-orthogonalization pass for full orthogonality.
        for (int j = 0; j < p; j++)
        {
            for (int i = 0; i < m; i++)
            {
                q[i, j] = basis[i, j];
            }

            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 0; k < j; k++)
                {
                    double dot = 0.0;
                    for (int i = 0; i < m; i++)
                    {
                        dot += q[i, k] * q[i, j];
                    }

                    for (int i = 0; i < m; i++)
                    {
                        q[i, j] -= dot * q[i, k];
                    }
                }
            }

            double normSq = 0.0;
            for (int i = 0; i < m; i++)
            {
                normSq += q[i, j] * q[i, j];
            }

            if (normSq <= 1e-24 || !double.IsFinite(normSq))
            {
                fit = [];
                hatDiagonal = [];
                return false;
            }

            double invNorm = 1.0 / Math.Sqrt(normSq);
            for (int i = 0; i < m; i++)
            {
                q[i, j] *= invNorm;
            }
        }

        double[] coeffs = new double[p];
        for (int j = 0; j < p; j++)
        {
            double dot = 0.0;
            for (int i = 0; i < m; i++)
            {
                dot += q[i, j] * y[i];
            }

            coeffs[j] = dot;
        }

        fit = new double[m];
        hatDiagonal = new double[m];
        for (int i = 0; i < m; i++)
        {
            double f = 0.0;
            double h = 0.0;
            for (int j = 0; j < p; j++)
            {
                double qij = q[i, j];
                f += qij * coeffs[j];
                h += qij * qij;
            }

            fit[i] = f;
            hatDiagonal[i] = h;
        }

        return true;
    }

    private static double MedianCopy(double[] values)
    {
        double[] copy = (double[])values.Clone();
        return Median(copy);
    }

    private static double Median(Span<double> values)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        values.Sort();
        int mid = values.Length / 2;
        return (values.Length & 1) == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }

    private static double PopulationStdDev(double[] values)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        double mean = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            mean += values[i];
        }

        mean /= values.Length;
        double sumSq = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sumSq += d * d;
        }

        return Math.Sqrt(sumSq / values.Length);
    }

    private static double Max(double[] values)
    {
        double max = double.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }
}
