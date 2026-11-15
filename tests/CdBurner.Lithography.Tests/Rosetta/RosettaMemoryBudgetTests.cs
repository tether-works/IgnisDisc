using System.Text;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

// Disable test parallelization in this assembly so process-wide GC.GetTotalMemory peak checks
// are not skewed by concurrent large-raster round-trip tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CdBurner.Lithography.Tests.Rosetta;

public class RosettaMemoryBudgetTests
{
    private const long MaxBudgetBytes = 32L * 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public RosettaMemoryBudgetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FullDiscHighDensityRun_StaysUnder32MbAndRasterizerAllocatesZeroBytes()
    {
        // Warm up singletons and JIT paths before measuring deltas.
        _ = RosettaPresetCatalog.BuiltIn;
        _ = Spleen5x8Font.BuiltIn;
        _ = UnifontBitmapFont.BuiltIn;

        RosettaPagePreset highDensity = RosettaPresetCatalog.BuiltIn.HighDensity;
        var probeLayout = highDensity.CreateLayout(24.0e-3, 58.0e-3);
        Assert.True(probeLayout.TotalPages > 15_000, $"Expected ~15,900 tiles, got {probeLayout.TotalPages}");

        // Build ~15,900 pages worth of text using a compact shared document template.
        var lineBuilder = new StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            lineBuilder.Append("Rosetta high-density optical archive line ").Append(i).Append(".\n");
        }
        string chunkText = lineBuilder.ToString();
        int docCount = 1950;
        var docs = new RosettaDocument[docCount];
        for (int i = 0; i < docCount; i++)
        {
            docs[i] = new RosettaDocument($"D{i:D4}", chunkText);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baselineBytes = GC.GetTotalMemory(true);

        RosettaBook book = RosettaBook.Build(docs, new RosettaArchiveOptions(
            ArchiveTitle: "FULL_DISC",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: highDensity,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0));

        Assert.True(book.TotalPages > 15_000, $"Expected full-disc book (>15,000 pages), got {book.TotalPages}");

        // 1. Verify PolarStreamRasterizer allocates 0 bytes across all rings.
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        var rasterizer = new PolarStreamRasterizer(geometry, book.PrimaryLayout, book.PrimaryPixelSource, samplesPerSector: 64);
        byte[] sectorBuf = new byte[64 * 128];

        // Warm up rasterizer JIT before measuring allocation.
        rasterizer.RasterizeRange(0, 128, sectorBuf);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int ring = 0; ring < book.PrimaryLayout.RingCount; ring++)
        {
            double midR = book.PrimaryLayout.GetBounds(ring, 0).MidRadiusMeters;
            long lba = Math.Max(0L, geometry.LastLbaInside(midR) - 64);
            rasterizer.RasterizeRange(lba, 128, sectorBuf);
        }
        long rasterizerAllocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Assert.Equal(0L, rasterizerAllocated);

        // 2. Stream RosettaDvdEncoder.WriteStream into Stream.Null across the disc while sampling GC.GetTotalMemory(false).
        long peakDeltaBytes = GC.GetTotalMemory(false) - baselineBytes;
        var encodeOptions = new RosettaDvdEncodeOptions(
            TotalSectors: 2_295_104,
            StartLba: 0,
            FillSectors: 32_000,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0,
            ReadCompatibility: true);

        RosettaDvdEncoder.WriteStream(book, Stream.Null, encodeOptions, progress: _ =>
        {
            long delta = GC.GetTotalMemory(false) - baselineBytes;
            if (delta > peakDeltaBytes)
            {
                peakDeltaBytes = delta;
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long retainedDeltaBytes = GC.GetTotalMemory(true) - baselineBytes;

        _output.WriteLine($"Pages={book.TotalPages}, RetainedDelta={retainedDeltaBytes / (1024.0 * 1024.0):F2} MB, PeakDelta={peakDeltaBytes / (1024.0 * 1024.0):F2} MB");
        Assert.True(retainedDeltaBytes < MaxBudgetBytes, $"Retained heap delta {retainedDeltaBytes:N0} exceeded 32 MB.");
        Assert.True(peakDeltaBytes < MaxBudgetBytes, $"Peak heap delta {peakDeltaBytes:N0} exceeded 32 MB.");
    }
}
