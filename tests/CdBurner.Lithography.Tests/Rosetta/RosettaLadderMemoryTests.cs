using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>Heap-growth check for the ladder writer; runs alone because it reads the process-wide heap size.</summary>
[Collection(HeapMeasurementCollection.Name)]
public sealed class RosettaLadderMemoryTests
{
    [Fact]
    public void WriteStream_StaysWithinMemoryBudget()
    {
        _ = RosettaPresetCatalog.BuiltIn;
        _ = Spleen5x8Font.BuiltIn;

        var warmupOptions = new RosettaLadderOptions(
            DiscPresetId: "budget-warmup",
            TotalSectors: 32_000,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0);
        RosettaLadderDisc.WriteStream(RosettaLadderDisc.CreateLayout(warmupOptions), Stream.Null);

        var options = new RosettaLadderOptions(
            DiscPresetId: "budget-check",
            TotalSectors: 80_000,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0);
        RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(options);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peakDelta = 0;

        FastDvdStreamSummary summary = RosettaLadderDisc.WriteStream(layout, Stream.Null, progress: _ =>
        {
            long delta = GC.GetTotalMemory(false) - baseline;
            if (delta > peakDelta)
            {
                peakDelta = delta;
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long retainedDelta = GC.GetTotalMemory(true) - baseline;

        const long maxBudget = 32L * 1024 * 1024;
        Assert.Equal(options.OutputSectors, summary.SectorsWritten);
        Assert.True(retainedDelta < maxBudget, $"Retained delta {retainedDelta:N0} exceeded 32 MB.");
        Assert.True(peakDelta < maxBudget, $"Peak delta {peakDelta:N0} exceeded 32 MB.");
    }
}
