using System.Buffers.Binary;
using System.IO.Compression;

namespace CdBurner.Lithography.Imaging;

/// <summary>A picture as straight (not premultiplied) BGRA, 4 bytes per pixel, rows top to bottom.</summary>
public sealed record BgraRaster(int Width, int Height, byte[] Pixels)
{
    /// <summary>Largest picture accepted, in pixels (the 4-byte buffer must stay below 2 GB).</summary>
    public const long MaxPixels = int.MaxValue / 4;

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || (long)Width * Height > MaxPixels)
            throw new ArgumentException($"A picture must have between 1 and {MaxPixels:N0} pixels.");
        if (Pixels is null || Pixels.Length != (long)Width * Height * 4)
            throw new ArgumentException("The pixel buffer does not hold Width x Height BGRA pixels.");
    }
}

/// <summary>
/// PNG reader and writer (ISO/IEC 15948, W3C PNG 2nd edition): validates chunk framing and CRC-32 checksums
/// and decodes PNGs (including interlaced, 1/2/4/8/16-bit, greyscale, RGB, RGBA, and palette with tRNS) to
/// straight 32-bit BGRA via Windows Imaging Component (<c>windowscodecs.dll</c>), and writes 8-bit RGBA PNGs.
/// </summary>
public static class PngRaster
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Largest PNG file read (256 MiB).</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public static BgraRaster Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes)
            throw new InvalidDataException($"'{fullPath}' is larger than {MaxFileBytes / (1024 * 1024)} MiB.");
        return Read(stream);
    }

    public static BgraRaster Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] pngBytes = ReadPngBytes(stream);
        ValidatePngFraming(pngBytes);
        return WicPngDecoder.Decode(pngBytes);
    }

    private static byte[] ReadPngBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining > MaxFileBytes)
            {
                throw new InvalidDataException($"PNG stream is larger than {MaxFileBytes / (1024 * 1024)} MiB.");
            }
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[64 * 1024];
        long totalRead = 0;
        while (true)
        {
            int maxToRead = (int)Math.Min(chunk.Length, (MaxFileBytes + 1) - totalRead);
            int read = stream.Read(chunk, 0, maxToRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
            if (totalRead > MaxFileBytes)
            {
                throw new InvalidDataException($"PNG stream is larger than {MaxFileBytes / (1024 * 1024)} MiB.");
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void ValidatePngFraming(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file (signature missing).");
        }

        int offset = 8;
        int colorType = -1;
        bool sawHeader = false;
        bool sawPalette = false;
        bool sawIdat = false;
        bool sawEnd = false;

        while (!sawEnd)
        {
            if (data.Length - offset < 12)
            {
                throw new InvalidDataException("PNG file ends early.");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > int.MaxValue || (long)offset + 12L + length > data.Length)
            {
                throw new InvalidDataException("PNG chunk length is invalid or truncated.");
            }

            ReadOnlySpan<byte> typeAndData = data.Slice(offset + 4, 4 + (int)length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int)length, 4));
            if (expectedCrc != Crc32(typeAndData))
            {
                throw new InvalidDataException("PNG chunk CRC does not match (damaged file).");
            }

            ReadOnlySpan<byte> type = typeAndData[..4];
            ReadOnlySpan<byte> chunkData = typeAndData[4..];

            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader)
                {
                    throw new InvalidDataException("PNG contains multiple header chunks.");
                }
                if (chunkData.Length != 13)
                {
                    throw new InvalidDataException("PNG header chunk has the wrong size.");
                }

                uint width = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
                uint height = BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]);
                int bitDepth = chunkData[8];
                colorType = chunkData[9];
                if (chunkData[10] != 0 || chunkData[11] != 0)
                {
                    throw new InvalidDataException("PNG uses an unknown compression or filter method.");
                }
                if (chunkData[12] is not (0 or 1))
                {
                    throw new InvalidDataException("PNG uses an unknown interlace method.");
                }
                if (colorType is not (0 or 2 or 3 or 4 or 6) || bitDepth is not (1 or 2 or 4 or 8 or 16))
                {
                    throw new InvalidDataException("PNG uses an unsupported colour type or bit depth.");
                }
                if (width == 0 || height == 0 || (long)width * height > BgraRaster.MaxPixels)
                {
                    throw new InvalidDataException("PNG picture size is out of range.");
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (chunkData.Length == 0 || chunkData.Length % 3 != 0 || chunkData.Length > 256 * 3)
                {
                    throw new InvalidDataException("PNG palette is malformed.");
                }
                sawPalette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader)
                {
                    throw new InvalidDataException("PNG image data comes before its header.");
                }
                sawIdat = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (chunkData.Length != 0)
                {
                    throw new InvalidDataException("PNG end chunk is malformed.");
                }
                sawEnd = true;
            }

            offset += 12 + (int)length;
        }

        if (!sawHeader)
        {
            throw new InvalidDataException("PNG has no header chunk.");
        }
        if (colorType == 3 && !sawPalette)
        {
            throw new InvalidDataException("Palette PNG has no palette.");
        }
        if (!sawIdat)
        {
            throw new InvalidDataException("PNG has no image data.");
        }
    }

    /// <summary>Writes an 8-bit RGBA PNG (filter 0 on every row).</summary>
    public static void SaveBgra32(string path, BgraRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        raster.Validate();
        using FileStream output = new(Path.GetFullPath(path), FileMode.Create, FileAccess.Write, FileShare.None);
        Write(output, raster);
    }

    public static void Write(Stream output, BgraRaster raster)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(raster);
        raster.Validate();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)raster.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)raster.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);
        using MemoryStream compressed = new();
        using (ZLibStream deflater = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] row = new byte[1 + raster.Width * 4];
            for (int y = 0; y < raster.Height; y++)
            {
                int inRow = y * raster.Width * 4;
                for (int x = 0; x < raster.Width; x++)
                {
                    int i = inRow + x * 4, o = 1 + x * 4;
                    row[o] = raster.Pixels[i + 2];
                    row[o + 1] = raster.Pixels[i + 1];
                    row[o + 2] = raster.Pixels[i];
                    row[o + 3] = raster.Pixels[i + 3];
                }
                deflater.Write(row);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
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

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }
}
