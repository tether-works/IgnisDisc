using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Lithography.Dvd;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Dvd;

/// <summary>
/// The ported DVD picture solver against the upstream golden hashes (Fixtures/UpstreamGoldens, recorded from
/// DiscImageStudio at the pinned commit), and the readback renderer against the fixture picture itself.
/// Each test builds the options upstream's command line builds for the recorded command (Program.cs at the pinned
/// commit: SolveMultipleBlocks, ReadImageMappingOptions, ReadBoundary, Encode).
/// </summary>
public sealed class DvdPictureGoldenTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-dvdpicture-" + Guid.NewGuid().ToString("N"));

    public DvdPictureGoldenTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Rings => Path.Combine(FixtureDir, "rings-64.png");

    // --total-sectors 1600 --inner-radius-mm 24 --outer-radius-mm 58 --channel-bit-nm 133.3; the other values are the
    // command-line defaults: start angle 0, clockwise spiral, luminance threshold 128, alpha threshold 1, and
    // constraint step 64 for a sector range.
    internal static ImageMappingOptions GoldenMapping => new(
        TotalSectors: 1600, InnerRadiusMm: 24, OuterRadiusMm: 58, ChannelBitLengthNm: 133.3, StartAngleDegrees: 0,
        Clockwise: true, LuminanceThreshold: 128, AlphaThreshold: 1, SampleEveryChannelBits: 64);

    // --lba 0 --psn-offset 0x30000 --algorithm dispersion --initial zero; iterations 1 is the dispersion default.
    internal static MultiBlockSolveOptions GoldenOptions(uint fillSectors, bool readCompatibility = false,
        int fastParallelism = 0, uint startLba = 0) => new(
        StartLba: startLba, FillSectors: fillSectors, PsnOffset: 0x30000, IterationsPerBlock: 1, MutationBytes: 4,
        RandomSeed: 1, InitialTemperature: 64.0, FinalTemperature: 0.25, ZeroInitialPayload: true,
        ImageMapping: GoldenMapping, Algorithm: SolverAlgorithm.Dispersion, FastParallelism: fastParallelism,
        ReadCompatibility: readCompatibility);

    // --initial-state 1 --initial-dsv 0 --initial-level land (the command-line defaults).
    private static ModulationBoundary GoldenBoundary => ModulationBoundary.Validate(1, 0, isLand: true);

    private static (string Sha256, long Bytes) Golden(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "upstream-goldens.json")));
        JsonElement output = document.RootElement.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("outputs").EnumerateArray())
            .Single(o => o.GetProperty("file").GetString() == file);
        return (output.GetProperty("sha256").GetString()!, output.GetProperty("bytes").GetInt64());
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void AssertGolden(string goldenFile, byte[] actual)
    {
        (string sha, long bytes) = Golden(goldenFile);
        Assert.Equal(bytes, actual.LongLength);
        Assert.Equal(sha, Sha256Hex(actual));
    }

    private byte[] WriteFast(string name, MultiBlockSolveOptions options)
    {
        string iso = Path.Combine(_tempDir, name);
        FastDvdImageFileWriter.WritePrepared(Rings, iso, options);
        return File.ReadAllBytes(iso);
    }

    [Fact]
    public void Fast_picture_iso_matches_the_upstream_golden_hash()
    {
        AssertGolden("dvd-picture.iso", WriteFast("dvd-picture.iso", GoldenOptions(1600)));
    }

    [Fact]
    public void Exact_solver_path_gives_the_same_golden_iso()
    {
        string iso = Path.Combine(_tempDir, "dvd-picture-exact.iso");

        MultiBlockSolveResult result = MultiBlockImageSolver.Solve(Rings, iso, GoldenBoundary, GoldenOptions(1600), null);

        Assert.Equal(100, result.CompletedBlocks);
        AssertGolden("dvd-picture-exact.iso", File.ReadAllBytes(iso));
    }

    [Fact]
    public void Read_compatibility_iso_matches_the_upstream_golden_hash()
    {
        AssertGolden("dvd-picture-compat.iso", WriteFast("dvd-picture-compat.iso", GoldenOptions(1600, readCompatibility: true)));
    }

    [Fact]
    public void First_ecc_block_and_its_channel_bits_match_the_upstream_golden_hashes()
    {
        byte[] block = WriteFast("dvd-block0.iso", GoldenOptions(16));
        AssertGolden("dvd-block0.iso", block);

        EncodedEccBlock encoded = new DvdEccBlockEncoder().Encode(block, 0, 0x30000, GoldenBoundary);

        // ECMA-267: a sector is 26 sync frames of 1,488 channel bits; an ECC block is 16 sectors.
        Assert.Equal(16 * 26 * 1488, encoded.ChannelLevels.Length);
        AssertGolden("dvd-block0.nrzi", BitPacking.PackMsbFirst(encoded.ChannelLevels));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Output_does_not_depend_on_the_worker_count(bool readCompatibility)
    {
        byte[] reference = WriteFast("workers-auto.iso", GoldenOptions(1600, readCompatibility));
        foreach (int workers in new[] { 1, 3, 16 })
        {
            byte[] actual = WriteFast($"workers-{workers}.iso", GoldenOptions(1600, readCompatibility, workers));
            Assert.True(reference.AsSpan().SequenceEqual(actual), $"{workers} worker(s) changed the output");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Output_does_not_depend_on_how_the_sector_range_is_split(bool readCompatibility)
    {
        byte[] whole = WriteFast("whole.iso", GoldenOptions(1600, readCompatibility));

        // The same 1,600 sectors as three separate streams of 16, 784 and 800 sectors.
        using MemoryStream pieces = new();
        foreach ((uint start, uint count) in new[] { (0u, 16u), (16u, 784u), (800u, 800u) })
        {
            FastDvdStreamSummary summary = FastDispersionImageWriter.WriteStream(
                Rings, pieces, GoldenOptions(count, readCompatibility, fastParallelism: 2, startLba: start));
            Assert.Equal(count, summary.SectorsWritten);
        }

        Assert.True(whole.AsSpan().SequenceEqual(pieces.ToArray()), "splitting the sector range changed the output");
    }

    [Fact]
    public void Readback_renderer_reproduces_the_fixture_picture()
    {
        string iso = Path.Combine(_tempDir, "readback.iso");
        FastDvdImageFileWriter.WritePrepared(Rings, iso, GoldenOptions(1600));
        string png = Path.Combine(_tempDir, "readback.png");

        // A 64-pixel preview has the fixture's own pixel grid: the picture's shorter side spans the outer diameter.
        IsoReadbackSummary summary = IsoReadbackRenderer.Render(iso, png, new IsoReadbackOptions(
            TotalSectors: 1600, StartLba: 0, FillSectors: 1600, PsnOffset: 0x30000, InnerRadiusMm: 24,
            OuterRadiusMm: 58, ChannelBitLengthNm: 133.3, Clockwise: true, PreviewSize: 64, SamplesPerSector: 2048));

        // Every sampled byte is one of the two picture bytes.
        Assert.Equal(1600L * 2048, summary.TotalSamples);
        Assert.Equal(summary.TotalSamples, summary.ClassifiedSamples);

        BgraRaster fixture = PngRaster.Load(Rings);
        BgraRaster readback = PngRaster.Load(png);
        Assert.Equal((64, 64), (readback.Width, readback.Height));

        // Compare every pixel that lies wholly inside the drawn band (24 to 58 mm, one pixel of margin).
        const double pixelsPerMm = 31.5 / 58.0;
        int compared = 0, dark = 0, mismatches = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                double radiusPixels = Math.Sqrt(((x - 31.5) * (x - 31.5)) + ((y - 31.5) * (y - 31.5)));
                if (radiusPixels < (24 * pixelsPerMm) + 1 || radiusPixels > (58 * pixelsPerMm) - 1)
                    continue;
                bool expectedDark = fixture.Pixels[((y * 64) + x) * 4] < 128;
                bool actualDark = readback.Pixels[((y * 64) + x) * 4] < 128;
                compared++;
                if (expectedDark) dark++;
                if (expectedDark != actualDark) mismatches++;
            }
        }

        // The band holds both ring colours, so an all-white or all-black readback cannot pass.
        Assert.True(compared > 1500, $"only {compared} pixels compared");
        Assert.InRange(dark, compared / 4, compared * 3 / 4);
        Assert.Equal(0, mismatches);
    }
}
