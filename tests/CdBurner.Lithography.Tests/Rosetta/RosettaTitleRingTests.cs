using CdBurner.Lithography.Dvd;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>
/// The title ring as specified: 0.25 mm blank at each edge, the 8 rows of the Spleen 5x8 font filling the rest with
/// square font pixels, capitals, the text centred on 270 degrees (the top of the disc seen from the read side), and no
/// stored bitmap: drawing allocates nothing and the memory used does not grow with the disc.
/// </summary>
public sealed class RosettaTitleRingTests
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private static readonly DateOnly Date = new(2026, 11, 17);
    private readonly ITestOutputHelper _output;

    public RosettaTitleRingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Layout_follows_the_ring_height_and_centres_the_text_on_the_top()
    {
        var ring = new RosettaTitleRing("Summer trip", Date, 55.0, 58.0);

        Assert.Equal("SUMMER TRIP  2026-11-17", ring.Text);
        Assert.Equal(2.5 / 8, ring.GlyphPixelMm, 12);
        Assert.Equal(57.75, ring.TextOuterRadiusMm, 12);
        Assert.Equal(57.75 - 1.25, ring.TextMidRadiusMm, 12);
        Assert.Equal(5 * 2.5 / 8, ring.CellWidthMm, 12);
        double span = ring.Text.Length * ring.CellWidthMm / ring.TextMidRadiusMm / DegreesToRadians;
        Assert.Equal(span, ring.TextSpanDegrees, 9);
        Assert.Equal(270 - (span / 2), ring.TextStartAngleDegrees, 9);

        // 'S' row 1 (Spleen: " ####") at the first column, sampled at the centre of each font pixel.
        double rowRadius = ring.TextOuterRadiusMm - (1.5 * ring.GlyphPixelMm);
        for (int px = 0; px < 5; px++)
        {
            double angle = (ring.TextStartAngleDegrees + ((px + 0.5) * ring.GlyphPixelMm / ring.TextMidRadiusMm / DegreesToRadians)) * DegreesToRadians;
            Assert.Equal(Spleen5x8Font.BuiltIn.IsPixelSet('S', px, 1), ring.IsInk(rowRadius, angle));
        }

        // Nothing outside the text strip, in the edge gaps or after the text.
        for (double degrees = 0; degrees < 360; degrees += 0.1)
        {
            double a = degrees * DegreesToRadians;
            Assert.False(ring.IsInk(57.76, a));
            Assert.False(ring.IsInk(55.24, a));
            Assert.False(ring.IsInk(54.0, a));
            Assert.False(ring.IsInk(58.5, a));
        }

        Assert.False(ring.IsInk(57.0, (ring.TextStartAngleDegrees - 0.5) * DegreesToRadians));
        Assert.False(ring.IsInk(57.0, (ring.TextStartAngleDegrees + span + 0.5) * DegreesToRadians));
        Assert.False(ring.IsInk(double.NaN, 0));
    }

    [Fact]
    public void Lower_case_and_unknown_characters_become_capitals_and_question_marks_and_the_date_may_be_left_out()
    {
        var ring = new RosettaTitleRing("café 中", null, 55.0, 58.0);
        Assert.Equal(Spleen5x8Font.BuiltIn.HasGlyph('É') ? "CAFÉ ?" : "CAF? ?", ring.Text);
        Assert.Null(ring.Date);
        Assert.Throws<ArgumentException>(() => new RosettaTitleRing("  ", null, 55.0, 58.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaTitleRing("X", Date, 55.0, 55.0 + RosettaTitleRing.MinimumHeightMm - 0.01));
        _ = new RosettaTitleRing("X", Date, 55.0, 55.0 + RosettaTitleRing.MinimumHeightMm);
        Assert.Equal(1.7, RosettaTitleRing.MinimumHeightMm, 12);
    }

    [Fact]
    public void Drawing_a_full_size_dvd_ring_allocates_nothing_per_point_and_stays_under_32_mb()
    {
        var ring = new RosettaTitleRing("Memory check", Date, 55.0, 58.0);
        Func<double, double, byte> sampler = ring.CreateReadSideSampler();
        _ = sampler(57.0, 0.0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int ink = 0;
        for (int i = 0; i < 200_000; i++)
        {
            double a = i * (2 * Math.PI / 200_000);
            double r = 55.0 + (3.0 * ((i * 7919) % 1000) / 1000.0);
            if (sampler(r * Math.Cos(a), r * Math.Sin(a)) == RosettaDvdEncoder.InkLuminance) ink++;
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(ink > 0);

        // A nominal DVD (2,295,104 sectors from 24 to 58 mm): stream 32,000 sectors from LBA 2,100,000 (r = 55.9 mm).
        var options = new DvdStreamingOptions(2_295_104, 24.0, 58.0, Tone: ToneProfile.LegacyBinary, ReadCompatibility: true);
        MultiBlockSolveOptions solve = DvdStreamingGenerator.CreateSolveOptions(options, 2_100_000, 32_000);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peak = 0;
        FastDvdStreamSummary summary = FastDispersionImageWriter.WritePatternStream(sampler, Stream.Null, solve, _ =>
        {
            peak = Math.Max(peak, GC.GetTotalMemory(false) - baseline);
        });

        Assert.Equal(32_000L * 2048, summary.BytesWritten);
        _output.WriteLine($"Peak heap growth {peak / 1048576.0:F2} MB for {summary.BytesWritten / 1048576.0:F0} MB written.");
        Assert.True(peak < 32L * 1024 * 1024, $"Peak heap growth {peak:N0} bytes exceeded 32 MB.");
    }
}
