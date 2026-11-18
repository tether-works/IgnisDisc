using CdBurner.Lithography.Dvd;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Rosetta;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>Allocation and heap-growth check for the title ring; runs alone because it reads the process-wide heap size.</summary>
[Collection(HeapMeasurementCollection.Name)]
public sealed class RosettaTitleRingMemoryTests
{
    private static readonly DateOnly Date = new(2026, 11, 17);
    private readonly ITestOutputHelper _output;

    public RosettaTitleRingMemoryTests(ITestOutputHelper output) => _output = output;

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
