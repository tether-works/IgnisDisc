using CdBurner.Lithography.Geometry;
using Xunit;

namespace CdBurner.Lithography.Tests.Geometry;

/// <summary>
/// <see cref="SpiralTimingAnalyzer"/> against primary-source physical spirals (constant pitch and a 300-degree smooth
/// departure) with simulated CAV read timing and noise, plus replay tests on recorded drive captures.
/// </summary>
public sealed class SpiralTimingAnalyzerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-spiral-timing-" + Guid.NewGuid().ToString("N"));

    public SpiralTimingAnalyzerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DriveTiming", name);

    private static double ConstantSpiralTurns(double innerRadiusMm, double perSectorMm2, double sectorLengthMm, double sector)
        => sectorLengthMm * (Math.Sqrt((innerRadiusMm * innerRadiusMm) + (perSectorMm2 * sector)) - innerRadiusMm) / (Math.PI * perSectorMm2);

    /// <summary>
    /// Builds a synthetic CAV read-timing capture directly from a physical turn function <paramref name="trueTurns"/>,
    /// including seeks, rotational arrival modulo the spin period, read-ahead cache hits, and Gaussian noise.
    /// </summary>
    private static List<TimingCaptureSample> BuildSyntheticCapture(
        Func<double, double> trueTurns,
        double periodUs = 28_701.6,
        int anchor = 170_000,
        int from = 2_000,
        int to = 344_000,
        int spacing = 3_000,
        double jitterDeg = 0.5,
        int seed = 42,
        double unsteadyFraction = 0.0)
    {
        var rng = new Random(seed);
        var plan = new List<int>();
        for (int s = from; s <= to; s += spacing)
        {
            plan.Add(anchor);
            plan.Add(s);
            for (int off = 1; off <= 512; off *= 2)
            {
                plan.Add(anchor);
                plan.Add(s + off);
            }
        }

        plan.Add(anchor);

        static double Mod1(double v) => v - Math.Floor(v);
        double clockUs = 0.0;
        double initialPhase = 0.23;
        int headLba = anchor;
        int cacheStart = -1;
        int cacheEnd = -1;
        double jitterUs = (jitterDeg / 360.0) * periodUs;

        var samples = new List<TimingCaptureSample>(plan.Count);
        double originUs = 0.0;
        for (int i = 0; i < plan.Count; i++)
        {
            int lba = plan[i];
            double tStart = clockUs;
            if (lba >= cacheStart && lba < cacheEnd)
            {
                clockUs += 6_000.0; // cache hit (< 60 ms)
            }
            else
            {
                double readyUs = clockUs + 68_000.0 + (0.25 * Math.Abs(lba - headLba));
                double targetPhase = Mod1(initialPhase + trueTurns(lba));
                double readyPhase = Mod1(readyUs / periodUs);
                double delta = targetPhase - readyPhase;
                if (delta <= 1e-9)
                {
                    delta += 1.0;
                }

                double arrivalUs = readyUs + (delta * periodUs);
                if (unsteadyFraction > 0.0 && rng.NextDouble() < unsteadyFraction)
                {
                    arrivalUs += 0.07 * periodUs;
                }

                if (jitterUs > 0.0)
                {
                    double u1 = Math.Max(1e-12, 1.0 - rng.NextDouble());
                    double u2 = rng.NextDouble();
                    double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                    arrivalUs += z * jitterUs;
                }

                clockUs = Math.Max(tStart + 100.0, arrivalUs);
                headLba = lba;
                cacheStart = lba;
                cacheEnd = lba + 32;
            }

            if (i == 0)
            {
                originUs = clockUs;
            }

            samples.Add(new TimingCaptureSample(i, lba, clockUs - originUs, clockUs - tStart, Ok: true));
        }

        return samples;
    }

    private static void AssertMatchesTrueSpiral(
        SpiralTimingMeasurementResult result,
        Func<double, double> trueTurns,
        double from,
        double to)
    {
        Assert.True(result.Success, result.RefusalReason);
        Assert.NotNull(result.TurnCorrection);
        MeasuredTurnCorrection table = result.TurnCorrection!;

        double expectedTotal = trueTurns(to) - trueTurns(from);
        double measuredTotal = result.TurnsBetween(from, to);
        Assert.True(
            Math.Abs(measuredTotal - expectedTotal) < 0.003,
            $"Turn count {measuredTotal:F4} differs from truth {expectedTotal:F4} by {Math.Abs(measuredTotal - expectedTotal):F5} turns (T={result.RotationPeriodMicroseconds:F2}, kept={result.KeptReadings}/{result.TotalReadings}, unsteady={result.UnsteadyReadings}, margin={result.ChainMarginTurns:F4}).");

        // Remove the arbitrary global phase constant (mean offset across the measured span) and check error everywhere.
        var samples = new List<double>();
        var errs = new List<double>();
        for (double a = from; a <= to; a += 500.0)
        {
            double pred = ConstantSpiralTurns(table.InnerRadiusMm, table.RadiusSquaredPerSectorMm2, table.SectorLengthMm, a)
                + table.TurnsAt(a);
            errs.Add(pred - trueTurns(a));
            samples.Add(a);
        }

        double meanOffset = errs.Average();
        double worstDeg = errs.Max(e => Math.Abs(e - meanOffset) * 360.0);
        Assert.True(worstDeg < 1.0, $"Worst table error across the disc was {worstDeg:F3} deg (limit 1.0 deg).");
    }

    [Fact]
    public void Constant_pitch_synthetic_capture_recovers_turn_count_and_table_within_one_degree()
    {
        const double r0 = 24.825;
        const double k = 0.007639437; // 1.5 um pitch * 16 mm / pi
        double TrueTurns(double a) => ConstantSpiralTurns(r0, k, 16.0, a);

        List<TimingCaptureSample> capture = BuildSyntheticCapture(TrueTurns, jitterDeg: 0.5, seed: 101);
        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(capture);

        AssertMatchesTrueSpiral(result, TrueTurns, 2_000, 344_000);
        Assert.True(result.ChainMarginTurns < 0.1, $"Unexpectedly high chain margin {result.ChainMarginTurns}");
        Assert.True(result.CacheHitReadings > 0, "Expected anchor-station collisions to be filtered as cache hits.");
    }

    [Fact]
    public void Three_hundred_degree_departure_synthetic_capture_recovers_turn_count_and_table_within_one_degree()
    {
        const double r0 = 24.75;
        const double k = 0.00774;
        double TrueTurns(double a)
        {
            double u = (a - 2_000.0) / (344_000.0 - 2_000.0);
            // Smooth departure spanning ~300 degrees (0.833 turn peak-to-peak).
            double departure = (0.36 * Math.Sin(2.0 * Math.PI * u)) - (0.14 * Math.Cos(3.0 * Math.PI * u));
            return ConstantSpiralTurns(r0, k, 16.0, a) + departure;
        }

        List<TimingCaptureSample> capture = BuildSyntheticCapture(TrueTurns, jitterDeg: 0.5, seed: 202);
        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(capture);

        AssertMatchesTrueSpiral(result, TrueTurns, 2_000, 344_000);
        Assert.True(result.SmoothingDegree >= 3, $"Expected smooth correction polynomial, got degree {result.SmoothingDegree}.");

        string jsonPath = Path.Combine(_tempDir, "departure.turnmap.json");
        SpiralTimingAnalyzer.WriteTurnMapJson(jsonPath, result.TurnCorrection!);
        MeasuredTurnCorrection loaded = MeasuredTurnCorrection.Load(jsonPath);
        Assert.Equal(result.StationCount, loaded.StationCount);
        Assert.Equal(result.TurnCorrection!.TurnsAt(123_456.0), loaded.TurnsAt(123_456.0), 8);
    }

    [Fact]
    public void Replay_map1_real_capture_reproduces_recorded_turn_count_and_low_margin()
    {
        IReadOnlyList<TimingCaptureSample> samples = SpiralTimingAnalyzer.ReadCaptureCsv(FixturePath("map1.csv"));
        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(samples);

        Assert.True(result.Success, result.RefusalReason);
        Assert.NotNull(result.TurnCorrection);
        Assert.Equal(2_000.0, result.FirstStationSector);
        Assert.Equal(344_000.0, result.LastStationSector);
        Assert.InRange(result.TurnCount, 21_167.322, 21_167.325); // 21,167.3234
        Assert.True(result.ChainMarginTurns < 0.1, $"Expected chain margin < 0.1, got {result.ChainMarginTurns:F4}");
        Assert.InRange(result.RotationPeriodMicroseconds, 28_695.0, 28_710.0);
    }

    [Fact]
    public void Replay_cdr_b1_real_capture_reproduces_other_kind_turn_count_and_low_margin()
    {
        IReadOnlyList<TimingCaptureSample> allSamples = SpiralTimingAnalyzer.ReadCaptureCsv(FixturePath("cdr_b1.csv"));
        SpiralTimingMeasurementResult fullResult = SpiralTimingAnalyzer.Analyze(allSamples);

        Assert.True(fullResult.Success, fullResult.RefusalReason);
        Assert.True(fullResult.ChainMarginTurns < 0.1, $"Expected chain margin < 0.1, got {fullResult.ChainMarginTurns:F4}");
        double span344 = fullResult.TurnsBetween(2_000, 344_000);
        Assert.InRange(span344, 21_269.25, 21_269.29); // 21,269.27

        // Truncating at station 344,000 reproduces the exact unsmoothed 2,000..344,000 count (21,269.27).
        int endRow = allSamples.TakeWhile(s => s.Lba != 344_512).Count() + 2; // include 344512 and its trailing anchor
        SpiralTimingMeasurementResult truncatedResult = SpiralTimingAnalyzer.Analyze(allSamples.Take(endRow).ToList());
        Assert.True(truncatedResult.Success, truncatedResult.RefusalReason);
        Assert.InRange(truncatedResult.TurnCount, 21_269.265, 21_269.275);
    }

    [Fact]
    public void Unsteady_real_capture_is_refused_with_reason_and_no_table()
    {
        IReadOnlyList<TimingCaptureSample> samples = SpiralTimingAnalyzer.ReadCaptureCsv(FixturePath("rw_c_fast.csv"));
        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(samples);

        Assert.False(result.Success);
        Assert.Null(result.TurnCorrection);
        Assert.False(string.IsNullOrWhiteSpace(result.RefusalReason));
        Assert.Contains("steady", result.RefusalReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_with_chain_slip_is_refused_with_reason_and_no_table()
    {
        const double r0 = 24.80;
        const double k = 0.007639437;
        double TrueTurns(double a) => ConstantSpiralTurns(r0, k, 16.0, a);

        // Inject a localized 0.40-turn jump in the middle of the disc so chaining hits a margin > 0.25 turn.
        List<TimingCaptureSample> capture = BuildSyntheticCapture(
            TrueTurns,
            from: 2_000,
            to: 344_000,
            spacing: 3_000,
            jitterDeg: 0.5,
            seed: 303);

        double periodUs = 28_701.6;
        double shiftUs = 0.40 * periodUs;
        for (int i = 0; i < capture.Count; i++)
        {
            TimingCaptureSample s = capture[i];
            if (s.Lba >= 164_000 && s.Lba != 170_000)
            {
                capture[i] = s with { DoneMicroseconds = s.DoneMicroseconds + shiftUs };
            }
        }

        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(capture);

        Assert.False(result.Success);
        Assert.Null(result.TurnCorrection);
        Assert.False(string.IsNullOrWhiteSpace(result.RefusalReason));
        Assert.True(
            result.ChainMarginTurns > SpiralTimingAnalyzer.MaxChainMarginTurns
            || result.RefusalReason!.Contains("chain", StringComparison.OrdinalIgnoreCase)
            || result.RefusalReason!.Contains("converge", StringComparison.OrdinalIgnoreCase)
            || result.RefusalReason!.Contains("steady", StringComparison.OrdinalIgnoreCase),
            $"Unexpected refusal reason: {result.RefusalReason}");
    }
}
