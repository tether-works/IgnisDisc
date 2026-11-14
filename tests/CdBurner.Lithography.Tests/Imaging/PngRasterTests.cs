using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Imaging;

public sealed class PngRasterTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");

    [Fact]
    public void Reads_the_fixture_pattern_as_scripted()
    {
        BgraRaster raster = PngRaster.Load(Path.Combine(FixtureDir, "quadrants-64.png"));

        Assert.Equal(64, raster.Width);
        Assert.Equal(64, raster.Height);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int o = (y * 64 + x) * 4;
                int level = Cd.CdPictureTrackTests.QuadrantLevel(x, y);
                Assert.Equal(new byte[] { (byte)level, (byte)level, (byte)level, 255 }, raster.Pixels[o..(o + 4)]);
            }
    }

    [Fact]
    public void Written_png_reads_back_identical()
    {
        byte[] pixels = new byte[7 * 5 * 4];
        new Random(3).NextBytes(pixels);
        BgraRaster raster = new(7, 5, pixels);
        using MemoryStream stream = new();

        PngRaster.Write(stream, raster);
        stream.Position = 0;

        Assert.Equal(pixels, PngRaster.Read(stream).Pixels);
    }

    [Fact]
    public void A_damaged_chunk_is_refused()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(FixtureDir, "rings-64.png"));
        png[40] ^= 0xFF; // inside the first IDAT chunk's data
        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream(png)));
    }

    [Fact]
    public void A_file_that_is_not_png_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream("GIF89a.."u8.ToArray())));
    }
}
