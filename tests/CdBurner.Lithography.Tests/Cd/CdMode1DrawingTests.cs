using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Cd;

/// <summary>Mode 1 drawing sectors: user data chosen so that the bytes after the drive's scrambler are palette bytes.</summary>
public sealed class CdMode1DrawingTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");
    private static CdDiscParameters Geometry => new(24.5, 56.8, 1200);

    // First 32 bytes of the ECMA-130 Annex B scrambler output (x^15 + x + 1, register preset to 1), applied from byte
    // 12 of the 2,352-byte sector, i.e. from the start of the header. Published as the scramble table in cdrdao and
    // in libedc_ecc (yellowbook_scrambler). User data starts at sector byte 16, i.e. table index 4.
    private static readonly byte[] PublishedScrambleStart =
    [
        0x01, 0x80, 0x00, 0x60, 0x00, 0x28, 0x00, 0x1E, 0x80, 0x08, 0x60, 0x06, 0xA8, 0x02, 0xFE, 0x81,
        0x80, 0x60, 0x60, 0x28, 0x28, 0x1E, 0x9E, 0x88, 0x68, 0x66, 0xAE, 0xAA, 0xFC, 0x7F, 0x01, 0xE0,
    ];

    private static BgraRaster Solid(byte level)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = level; pixels[i + 3] = 255; }
        return new BgraRaster(16, 16, pixels);
    }

    [Theory]
    [InlineData(0, 0x0D)]
    [InlineData(255, 0xAA)]
    public void Scrambled_user_data_of_a_solid_picture_is_the_palette_byte(byte level, byte palette)
    {
        // A 16 px picture over 57.5 mm covers the whole ring, so every sampled byte has this level.
        using MemoryStream output = new();
        CdMode1Drawing.WriteSectors(Solid(level), output, Geometry, 100, 103);
        byte[] data = output.ToArray();
        Assert.Equal(3 * 2048, data.Length);

        for (int sector = 0; sector < 3; sector++)
            for (int offset = 0; offset < PublishedScrambleStart.Length - 4; offset++)
                Assert.Equal(palette, (byte)(data[sector * 2048 + offset] ^ PublishedScrambleStart[offset + 4]));
    }

    [Fact]
    public void Payload_bytes_of_a_sector_range_land_on_distinct_disc_bytes()
    {
        HashSet<long> seen = new();
        for (long lba = 10; lba < 20; lba++)
            for (int offset = 0; offset < 2048; offset++)
                Assert.True(seen.Add(CdMode1Drawing.PhysicalByteForPayload(lba, offset)));
    }

    [Fact]
    public void Payload_bytes_stay_inside_their_own_sector_window_after_the_header()
    {
        // ECMA-130: user data follows the 12-byte sync and 4-byte header, so with no delay the first payload byte
        // would sit at sector byte 16 (17 after the pair swap). The CIRC delay moves a byte at most one delay cycle.
        long physical = CdMode1Drawing.PhysicalByteForPayload(5, 0);
        Assert.InRange(physical - 5 * 2352, 16 - 112 * 24, 2352);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Read_back_of_drawing_sectors_reproduces_the_picture(bool simd, bool parallel)
    {
        BgraRaster picture = PngRaster.Load(Path.Combine(FixtureDir, "quadrants-64.png"));
        using MemoryStream sectors = new();
        CdMode1Drawing.WriteSectors(picture, sectors, Geometry, 0, 1200, useSimdSampling: simd, useParallelSampling: parallel);
        sectors.Position = 0;

        byte[] pixels = CdMode1Drawing.RenderSectors(sectors, 0, 1200, Geometry, 64, byteStep: 1);

        int checkedPixels = CdPictureTrackTests.AssertMatchesThresholdedPattern(new BgraRaster(64, 64, pixels), Geometry);
        Assert.True(checkedPixels > 1500);
    }

    [Fact]
    public void Drawing_range_outside_the_geometry_is_refused()
    {
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => CdMode1Drawing.WriteSectors(Solid(0), output, Geometry, 1100, 1201));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdMode1Drawing.WriteSectors(Solid(0), output, Geometry, 50, 50));
    }
}
