using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Geometry;
using Xunit;

namespace CdBurner.Lithography.Tests.Geometry;

/// <summary>
/// <see cref="MeasuredTurnCorrection"/> and its use in <see cref="CdDiscParameters.PolarFromByte"/>. Expected values come
/// from closed formulas written here: a thin-track spiral with radius² = I² + k·sector makes
/// turns = s·(r − I) / (π·k), s being the track length of one sector.
/// </summary>
public class MeasuredTurnCorrectionTests
{
    private const double SectorLengthMm = 16.0;          // 1200 mm/s over 75 sectors per second
    private const double Inner = 24.75;
    private const double PerSector = 7.74e-3;             // mm² of radius² per sector
    private const long Sectors = 360_000;

    private static double[] Stations(int count = 115, double first = 2_000, double spacing = 3_000)
        => [.. Enumerable.Range(0, count).Select(i => first + (i * spacing))];

    private static double SpiralTurns(double inner, double perSector, double sector)
        => SectorLengthMm * (Math.Sqrt((inner * inner) + (perSector * sector)) - inner) / (Math.PI * perSector);

    private static CdDiscParameters Disc(MeasuredTurnCorrection? correction)
        => new(Inner, Math.Sqrt((Inner * Inner) + (PerSector * Sectors)), Sectors) { TurnCorrection = correction };

    [Fact]
    public void A_quadratic_table_is_reproduced_between_stations()
    {
        static double F(double x) => 0.3 - (2.0e-6 * x) + (7.5e-12 * x * x);
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, [.. stations.Select(F)]);

        foreach (double sector in new[] { 2_000.0, 2_001.0, 3_499.5, 100_000.0, 250_123.25, 343_999.0, 344_000.0 })
        {
            Assert.Equal(F(sector), table.TurnsAt(sector), 10);
        }
    }

    [Fact]
    public void Outside_the_measured_range_the_end_slope_continues_in_a_straight_line()
    {
        static double F(double x) => 0.3 - (2.0e-6 * x) + (7.5e-12 * x * x);
        static double Slope(double x) => -2.0e-6 + (15.0e-12 * x);
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, [.. stations.Select(F)]);

        Assert.Equal(F(2_000) + (Slope(2_000) * -2_000), table.TurnsAt(0), 12);
        Assert.Equal(F(344_000) + (Slope(344_000) * 16_000), table.TurnsAt(360_000), 12);
    }

    [Fact]
    public void A_smooth_curve_the_size_of_a_real_deviation_is_interpolated_to_a_third_of_a_degree()
    {
        // About 0.8 turn of swing across the disc, the size measured on a real CD-R.
        static double F(double x) => 0.8 * Math.Sin(x / 60_000.0) + (0.2 * Math.Cos(x / 25_000.0));
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, [.. stations.Select(F)]);

        double worst = 0;
        for (double sector = 2_000; sector <= 344_000; sector += 137.0)
        {
            worst = Math.Max(worst, Math.Abs(table.TurnsAt(sector) - F(sector)));
        }

        Assert.True(worst < 0.001, $"worst interpolation error {worst} turn");
    }

    [Fact]
    public void A_zero_table_leaves_radius_and_angle_as_they_were()
    {
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, new double[stations.Length]);
        CdDiscParameters plain = Disc(null);
        CdDiscParameters measured = Disc(table);

        foreach (long sector in new long[] { 0, 1, 2_000, 123_456, 359_999 })
        {
            long globalByte = sector * CdDiscParameters.BytesPerSector + 777;
            (double r0, double t0) = plain.PolarFromByte(globalByte);
            (double r1, double t1) = measured.PolarFromByte(globalByte);
            Assert.Equal(r0, r1, 12);
            Assert.Equal(t0, t1, 6);   // angles reach 130,000 rad; 1e-6 rad is far below anything visible
        }
    }

    [Fact]
    public void A_quarter_turn_in_the_table_turns_every_byte_by_ninety_degrees()
    {
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations,
            [.. stations.Select(_ => 0.25)]);
        CdDiscParameters plain = Disc(null);
        CdDiscParameters measured = Disc(table);

        foreach (long sector in new long[] { 0, 50_000, 200_000, 359_000 })
        {
            long globalByte = sector * CdDiscParameters.BytesPerSector;
            Assert.Equal(plain.PolarFromByte(globalByte).ThetaRadians + (Math.PI / 2),
                measured.PolarFromByte(globalByte).ThetaRadians, 6);
            Assert.Equal(plain.PolarFromByte(globalByte).RadiusMm, measured.PolarFromByte(globalByte).RadiusMm, 12);
        }
    }

    [Fact]
    public void The_address_offset_asks_for_the_angle_of_a_later_address()
    {
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, new double[stations.Length],
            addressOffsetSectors: 2.0);
        CdDiscParameters plain = Disc(null);
        CdDiscParameters measured = Disc(table);

        long globalByte = 150_000L * CdDiscParameters.BytesPerSector;
        long twoSectorsOn = globalByte + (2 * CdDiscParameters.BytesPerSector);
        Assert.Equal(plain.PolarFromByte(twoSectorsOn).ThetaRadians, measured.PolarFromByte(globalByte).ThetaRadians, 6);
        Assert.Equal(plain.PolarFromByte(globalByte).RadiusMm, measured.PolarFromByte(globalByte).RadiusMm, 12);
    }

    [Fact]
    public void A_disc_on_another_spiral_gets_that_spirals_turn_count()
    {
        // The real disc follows a spiral with a different start radius and growth; the table holds the difference
        // at the stations. Every byte must then come out at the real spiral's turn count.
        const double realInner = 24.81;
        const double realPerSector = 7.70e-3;
        double[] stations = Stations();
        double[] difference = [.. stations.Select(a => SpiralTurns(realInner, realPerSector, a) - SpiralTurns(Inner, PerSector, a))];
        CdDiscParameters measured = Disc(new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, difference));

        // 16·(57.134 − 24.81)/(π·0.00770) ≈ 21,380 turns against 16·(57.229 − 24.75)/(π·0.00774) ≈ 21,371.
        Assert.True(Math.Abs(difference[^1] - difference[0]) > 5, "the two spirals should differ by several turns");
        foreach (double sector in new[] { 2_000.0, 17_333.0, 99_999.5, 222_222.0, 343_000.0 })
        {
            long globalByte = (long)(sector * CdDiscParameters.BytesPerSector);
            double turns = measured.PolarFromByte(globalByte).ThetaRadians / (2 * Math.PI);
            double expected = SpiralTurns(realInner, realPerSector, (double)globalByte / CdDiscParameters.BytesPerSector);
            Assert.True(Math.Abs(turns - expected) < 0.001, $"sector {sector}: {turns} turns, expected {expected}");
        }
    }

    [Fact]
    public void A_table_and_pitch_coefficients_together_are_refused()
    {
        double[] stations = Stations();
        var table = new MeasuredTurnCorrection(Inner, PerSector, SectorLengthMm, stations, new double[stations.Length]);
        CdDiscParameters both = Disc(table) with { PitchLinear = 0.01 };

        Assert.Throws<ArgumentException>(both.Validate);
    }

    [Fact]
    public void Stations_out_of_order_or_too_few_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new MeasuredTurnCorrection(
            Inner, PerSector, SectorLengthMm, [0, 10, 10, 30], [0, 0, 0, 0]));
        Assert.Throws<ArgumentException>(() => new MeasuredTurnCorrection(
            Inner, PerSector, SectorLengthMm, [0, 10, 20], [0, 0, 0]));
        Assert.Throws<ArgumentException>(() => new MeasuredTurnCorrection(
            Inner, PerSector, SectorLengthMm, [0, 10, 20, 30], [0, 0, 0]));
    }

    [Fact]
    public void A_turn_map_file_is_read_and_a_wrong_file_is_refused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ignisdisc-turnmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string good = Path.Combine(folder, "good.turnmap.json");
            File.WriteAllText(good,
                """
                { "innerRadiusMm": 24.75, "radiusSquaredPerSectorMm2": 0.00774, "sectorLengthMm": 16.0,
                  "stationSectors": [0, 1000, 2000, 3000], "correctionTurns": [0.0, 0.5, 1.0, 1.5] }
                """);
            MeasuredTurnCorrection table = MeasuredTurnCorrection.Load(good);
            Assert.Equal(4, table.StationCount);
            Assert.Equal(0.75, table.TurnsAt(1_500), 12);
            Assert.Equal(24.75, table.InnerRadiusMm);

            string bad = Path.Combine(folder, "bad.json");
            File.WriteAllText(bad, """{ "innerRadiusMm": 24.75 }""");
            Assert.Throws<InvalidDataException>(() => MeasuredTurnCorrection.Load(bad));

            string malformed = Path.Combine(folder, "malformed.json");
            File.WriteAllText(malformed, "{ not valid json");
            Assert.Throws<InvalidDataException>(() => MeasuredTurnCorrection.Load(malformed));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
