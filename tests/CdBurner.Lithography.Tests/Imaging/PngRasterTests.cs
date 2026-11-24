using System.Buffers.Binary;
using System.IO.Compression;
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

    [Fact]
    public void Adam7_interlaced_8bit_rgba_png_decodes_accurately()
    {
        const int width = 9;
        const int height = 7;
        byte[] expectedBgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                expectedBgra[o] = (byte)(x * 23 + y * 11);       // B
                expectedBgra[o + 1] = (byte)(x * 13 + y * 29);   // G
                expectedBgra[o + 2] = (byte)(x * 19 + y * 17);   // R
                expectedBgra[o + 3] = (byte)(128 + ((x + y) * 7) % 127); // A
            }
        }

        int[] x0 = [0, 4, 0, 2, 0, 1, 0];
        int[] dx = [8, 8, 4, 4, 2, 2, 1];
        int[] y0 = [0, 0, 4, 0, 2, 0, 1];
        int[] dy = [8, 8, 8, 4, 4, 2, 2];

        using MemoryStream raw = new();
        for (int pass = 0; pass < 7; pass++)
        {
            if (x0[pass] >= width || y0[pass] >= height)
            {
                continue;
            }
            for (int y = y0[pass]; y < height; y += dy[pass])
            {
                raw.WriteByte(0); // filter 0
                for (int x = x0[pass]; x < width; x += dx[pass])
                {
                    int o = (y * width + x) * 4;
                    raw.WriteByte(expectedBgra[o + 2]); // R
                    raw.WriteByte(expectedBgra[o + 1]); // G
                    raw.WriteByte(expectedBgra[o]);     // B
                    raw.WriteByte(expectedBgra[o + 3]); // A
                }
            }
        }

        byte[] png = BuildPng(width, height, bitDepth: 8, colorType: 6, interlace: 1, raw.ToArray());
        BgraRaster decoded = PngRaster.Read(new MemoryStream(png));

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(expectedBgra, decoded.Pixels);
    }

    [Fact]
    public void SixteenBit_greyscale_alpha_png_preserves_straight_bgra_under_alpha()
    {
        const int width = 2;
        const int height = 1;
        // Pixel 0: gray=0xAB00, alpha=0x0000 (transparent; straight BGRA must keep 0xAB, not premultiply to 0)
        // Pixel 1: gray=0x8080, alpha=0x4040
        byte[] raw =
        [
            0, // filter 0
            0xAB, 0x00, 0x00, 0x00,
            0x80, 0x80, 0x40, 0x40,
        ];

        byte[] png = BuildPng(width, height, bitDepth: 16, colorType: 4, interlace: 0, raw);
        BgraRaster decoded = PngRaster.Read(new MemoryStream(png));

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(
            new byte[]
            {
                0xAB, 0xAB, 0xAB, 0x00,
                0x80, 0x80, 0x80, 0x40,
            },
            decoded.Pixels);
    }

    [Fact]
    public void FourBit_palette_png_with_trns_alpha_decodes_to_bgra()
    {
        const int width = 3;
        const int height = 2;
        byte[] plte =
        [
            10, 20, 30,   // index 0
            40, 50, 60,   // index 1
            70, 80, 90,   // index 2
            100, 110, 120 // index 3
        ];
        byte[] trns = [255, 180, 90, 0];
        // Row 0: indices [0, 1, 2] -> 0x01, 0x20
        // Row 1: indices [3, 2, 1] -> 0x32, 0x10
        byte[] raw =
        [
            0, 0x01, 0x20,
            0, 0x32, 0x10,
        ];

        byte[] png = BuildPng(width, height, bitDepth: 4, colorType: 3, interlace: 0, raw, plte, trns);
        BgraRaster decoded = PngRaster.Read(new MemoryStream(png));

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(
            new byte[]
            {
                30, 20, 10, 255,  60, 50, 40, 180,   90, 80, 70, 90,
                120, 110, 100, 0, 90, 80, 70, 90,    60, 50, 40, 180,
            },
            decoded.Pixels);
    }

    [Fact]
    public void MaxPixels_buffer_fits_within_array_max_length()
    {
        Assert.True(BgraRaster.MaxPixels * 4 <= Array.MaxLength);
        Assert.True(1 + BgraRaster.MaxPixels * 4 <= Array.MaxLength);
    }

    [Theory]
    [InlineData(536_870_900, 1)]
    [InlineData(20_000, 20_000)]
    public void Crafted_oversized_png_headers_throw_InvalidDataException_without_OOM(int width, int height)
    {
        byte[] png = BuildPng(width, height, bitDepth: 8, colorType: 6, interlace: 0, [0, 0, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream(png)));
    }

    [Fact]
    public void Png_without_ihdr_first_is_refused()
    {
        using MemoryStream output = new();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "tEXt", "Comment\0hi"u8.ToArray());
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", [0x78, 0x9C, 0x63, 0x60, 0x60, 0x60, 0x60, 0x00, 0x00, 0x00, 0x05, 0x00, 0x01]);
        WriteChunk(output, "IEND", []);

        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream(output.ToArray())));
    }

    [Fact]
    public void Png_with_duplicate_or_trailing_plte_is_refused()
    {
        byte[] plte = [0, 0, 0, 255, 255, 255];
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;
        ihdr[9] = 3;

        byte[] compressed;
        using (MemoryStream ms = new())
        {
            using (ZLibStream z = new(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write([0, 0]);
            }
            compressed = ms.ToArray();
        }

        using MemoryStream dup = new();
        dup.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(dup, "IHDR", ihdr);
        WriteChunk(dup, "PLTE", plte);
        WriteChunk(dup, "PLTE", plte);
        WriteChunk(dup, "IDAT", compressed);
        WriteChunk(dup, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream(dup.ToArray())));

        using MemoryStream trailing = new();
        trailing.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(trailing, "IHDR", ihdr);
        WriteChunk(trailing, "IDAT", compressed);
        WriteChunk(trailing, "PLTE", plte);
        WriteChunk(trailing, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngRaster.Read(new MemoryStream(trailing.ToArray())));
    }

    private static byte[] BuildPng(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte interlace,
        byte[] rawScanlines,
        byte[]? plte = null,
        byte[]? trns = null)
    {
        using MemoryStream output = new();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlace;
        WriteChunk(output, "IHDR", ihdr);

        if (plte is not null)
        {
            WriteChunk(output, "PLTE", plte);
        }
        if (trns is not null)
        {
            WriteChunk(output, "tRNS", trns);
        }

        using MemoryStream compressed = new();
        using (ZLibStream deflater = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(rawScanlines);
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeAndData = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);

        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
        output.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ uint.MaxValue;
    }
}
