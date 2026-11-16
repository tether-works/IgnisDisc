using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.BluRay.Encoding;

namespace CdBurner.Lithography.Tests.BluRay;

/// <summary>
/// The ported BD-R picture generator against the upstream golden hashes (Fixtures/UpstreamGoldens, recorded from
/// DiscImageStudio at the pinned commit). Each test builds the request upstream's command module builds for the
/// recorded command line (BluRayDiscModule.cs at the pinned commit: bd-generate and bd-encode).
/// </summary>
public sealed class BluRayPictureGoldenTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-bdpicture-" + Guid.NewGuid().ToString("N"));

    public BluRayPictureGoldenTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static (string Sha256, long Bytes) Golden(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "upstream-goldens.json")));
        JsonElement output = document.RootElement.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("outputs").EnumerateArray())
            .Single(o => o.GetProperty("file").GetString() == file);
        return (output.GetProperty("sha256").GetString()!, output.GetProperty("bytes").GetInt64());
    }

    private static void AssertGolden(string goldenFile, byte[] actual)
    {
        (string sha, long bytes) = Golden(goldenFile);
        Assert.Equal(bytes, actual.LongLength);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant());
    }

    // bd-generate --total-sectors N --r0 24 --r1 58; the other values are the command defaults: preset bd-r-sl-25gb
    // (one layer, start angle 0, constant pitch, no usable-sector limit), two tone levels without dither, brightness
    // or sRGB linearization, and dark mapped to 3T.
    private byte[] Generate(string fixturePng, string name, uint totalSectors)
    {
        string iso = Path.Combine(_tempDir, name);
        BluRayImageRequest request = new(Path.Combine(FixtureDir, fixturePng), iso,
            new BluRayDiscParameters(totalSectors, 1, 24, 58), new ToneProfile(0, ToneProfile.TwoLevels, 0, false));
        new BluRayImageGenerator(BluRayImageLoader.Load).Generate(request);
        return File.ReadAllBytes(iso);
    }

    [Fact]
    public void Eight_cluster_picture_image_matches_the_upstream_golden_hash()
    {
        AssertGolden("bd-picture.iso", Generate("quadrants-64.png", "bd-picture.iso", 256));
    }

    [Fact]
    public void One_cluster_picture_image_matches_the_upstream_golden_hash()
    {
        AssertGolden("bd-cluster.iso", Generate("rings-64.png", "bd-cluster.iso", 32));
    }

    [Fact]
    public void Ecc_cluster_of_the_one_cluster_image_matches_the_upstream_golden_hash()
    {
        // bd-encode --psn 0x100000: EDC, scrambler and LDC/BIS Reed-Solomon of exactly one 65,536-byte cluster.
        byte[] cluster = Generate("rings-64.png", "bd-cluster.iso", 32);
        AssertGolden("bd-cluster.ecc", BluRayEccEncoder.Encode(cluster, 0x100000));
    }

    [Fact]
    public void Picture_image_holds_only_the_two_run_length_bytes_after_scrambling()
    {
        // The drive scrambles each 2,048-byte sector with the sequence of ISO/IEC 30190 clause 13.4 (16-bit shift
        // register, preset from the cluster's physical sector number, taps 16/15/13/4). The picture bytes are chosen
        // so that the scrambled result is 0xAA (dark, 3T runs) or 0x44 (light, 6T runs). The register is stepped
        // here bit by bit, apart from the encoder's own mask code.
        byte[] image = Generate("quadrants-64.png", "bd-picture.iso", 256);
        int dark = 0, light = 0;
        for (int sector = 0; sector < 256; sector++)
        {
            uint psn = 0x100000u + (uint)sector;
            uint state = 0x8000u | ((psn >> 5) & 0x7FFF);
            for (int index = 0; index < 2048; index++)
            {
                byte scrambled = (byte)(image[sector * 2048 + index] ^ (byte)state);
                if (scrambled == 0xAA) dark++;
                else if (scrambled == 0x44) light++;
                else Assert.Fail($"Sector {sector}, byte {index}: scrambled value 0x{scrambled:X2} is neither 0xAA nor 0x44.");
                for (int bit = 0; bit < 8; bit++)
                {
                    uint feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                    state = ((state << 1) | feedback) & 0xFFFF;
                }
            }
        }

        // The fixture has a black quadrant, a white one, a ramp and a checkerboard, so both values must occur.
        Assert.True(dark > 0 && light > 0, $"dark {dark}, light {light}");
    }

    [Fact]
    public void Generation_writes_a_sidecar_that_reopens_with_the_same_geometry()
    {
        Generate("quadrants-64.png", "bd-picture.iso", 256);
        BluRayIsoMetadata? sidecar = BluRayIsoMetadata.TryLoad(Path.Combine(_tempDir, "bd-picture.iso"));
        Assert.NotNull(sidecar);
        Assert.Equal(BluRayIsoMetadata.FormatName, sidecar.Format);
        Assert.Equal(256u, sidecar.TotalSectors);
        Assert.Equal(256L * 2048, sidecar.IsoBytes);
        Assert.Equal(24.0, sidecar.InnerRadiusMm);
        Assert.Equal(58.0, sidecar.OuterRadiusMm);
        Assert.Equal(0u, sidecar.DrawingStartSector);
        Assert.Equal(256u, sidecar.DrawingEndSectorExclusive);
        Assert.False(sidecar.InvertMapping);
        Assert.Null(sidecar.DataDirectory);
    }

    [Theory]
    [InlineData(0u)]     // nothing to write
    [InlineData(31u)]    // one sector short of a cluster
    [InlineData(33u)]    // one sector past a cluster
    [InlineData(100u)]
    public void Disc_size_must_be_a_whole_number_of_32_sector_clusters(uint totalSectors)
    {
        // A BD-R is written in clusters of 32 user sectors (ISO/IEC 30190 clause 13.1: 32 data frames per LDC block).
        Assert.Throws<ArgumentOutOfRangeException>(() => new BluRayDiscParameters(totalSectors).Validate());
    }

    [Theory]
    [InlineData(16u, 32u)]   // start inside a cluster
    [InlineData(0u, 48u)]    // length ends inside a cluster
    [InlineData(0u, 0u)]
    [InlineData(224u, 64u)]  // runs past the end of a 256-sector disc
    public void Stream_range_must_start_and_end_on_cluster_boundaries_inside_the_disc(uint start, uint count)
    {
        BluRayStreamingOptions options = new(new BluRayDiscParameters(256), start, count);
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void A_picture_image_that_also_holds_files_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => BluRayIsoGenerator.PrepareDrawing(new BluRayDiscParameters(256), _tempDir));
    }
}
