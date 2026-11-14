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
/// Minimal PNG reader and writer (ISO/IEC 15948, W3C PNG 2nd edition): reads non-interlaced 8-bit greyscale, grey +
/// alpha, RGB, RGBA and palette images and writes 8-bit RGBA. Enough for picture input and read-back previews without
/// a desktop-UI dependency in this library. Interlaced and 1/2/4/16-bit images are refused with a clear message.
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
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file (signature missing).");

        int width = 0, height = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using MemoryStream compressed = new();
        Span<byte> header = stackalloc byte[8];
        Span<byte> crcBytes = stackalloc byte[4];
        bool sawHeader = false, sawEnd = false;
        while (!sawEnd)
        {
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > int.MaxValue) throw new InvalidDataException("PNG chunk length is invalid.");
            byte[] typeAndData = new byte[4 + (int)length];
            header[4..].CopyTo(typeAndData);
            stream.ReadExactly(typeAndData.AsSpan(4));
            stream.ReadExactly(crcBytes);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes) != Crc32(typeAndData))
                throw new InvalidDataException("PNG chunk CRC does not match (damaged file).");
            string type = System.Text.Encoding.ASCII.GetString(typeAndData, 0, 4);
            ReadOnlySpan<byte> data = typeAndData.AsSpan(4);
            switch (type)
            {
                case "IHDR":
                    if (data.Length != 13) throw new InvalidDataException("PNG header chunk has the wrong size.");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    int bitDepth = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0)
                        throw new InvalidDataException("PNG uses an unknown compression or filter method.");
                    if (data[12] != 0)
                        throw new InvalidDataException("Interlaced PNG files are not supported; save the picture without interlacing.");
                    if (bitDepth != 8 || colorType is not (0 or 2 or 3 or 4 or 6))
                        throw new InvalidDataException("Only 8-bit greyscale, RGB, palette or RGBA PNG files are supported.");
                    if (width <= 0 || height <= 0 || (long)width * height > BgraRaster.MaxPixels)
                        throw new InvalidDataException("PNG picture size is out of range.");
                    sawHeader = true;
                    break;
                case "PLTE":
                    if (data.Length % 3 != 0 || data.Length == 0) throw new InvalidDataException("PNG palette is malformed.");
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    paletteAlpha = data.ToArray();
                    break;
                case "IDAT":
                    if (!sawHeader) throw new InvalidDataException("PNG image data comes before its header.");
                    compressed.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                default:
                    if ((typeAndData[0] & 0x20) == 0)
                        throw new InvalidDataException($"PNG contains an unknown critical chunk '{type}'.");
                    break;
            }
        }

        if (!sawHeader) throw new InvalidDataException("PNG has no header chunk.");
        if (colorType == 3 && palette is null) throw new InvalidDataException("Palette PNG has no palette.");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int stride = checked(width * channels);
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        byte[] pixels = new byte[checked(width * height * 4)];
        compressed.Position = 0;
        using ZLibStream inflater = new(compressed, CompressionMode.Decompress);
        for (int y = 0; y < height; y++)
        {
            int filter = inflater.ReadByte();
            if (filter < 0) throw new InvalidDataException("PNG image data ends early.");
            inflater.ReadExactly(current);
            Unfilter(filter, current, previous, channels);
            int outRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0: r = g = b = current[x]; break;
                    case 4: r = g = b = current[2 * x]; a = current[2 * x + 1]; break;
                    case 2: r = current[3 * x]; g = current[3 * x + 1]; b = current[3 * x + 2]; break;
                    case 3:
                        int entry = current[x];
                        if (entry * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range.");
                        r = palette[entry * 3]; g = palette[entry * 3 + 1]; b = palette[entry * 3 + 2];
                        if (paletteAlpha is not null && entry < paletteAlpha.Length) a = paletteAlpha[entry];
                        break;
                    default: r = current[4 * x]; g = current[4 * x + 1]; b = current[4 * x + 2]; a = current[4 * x + 3]; break;
                }
                int o = outRow + x * 4;
                pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = a;
            }
            (previous, current) = (current, previous);
        }
        return new BgraRaster(width, height, pixels);
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0: return;
            case 1:
                for (int i = bytesPerPixel; i < row.Length; i++) row[i] += row[i - bytesPerPixel];
                return;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] += previous[i];
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    row[i] += (byte)((left + previous[i]) >> 1);
                }
                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int b = previous[i];
                    int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return;
            default:
                throw new InvalidDataException($"PNG row uses unknown filter type {filter}.");
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
