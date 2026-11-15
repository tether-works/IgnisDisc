using System.Buffers;
using System.IO.Compression;
using System.Reflection;

namespace CdBurner.Lithography.Rosetta.Fonts;

/// <summary>
/// Stream-parsed GNU Unifont BMP bitmap font (8x16 narrow and 16x16 wide glyphs).
/// </summary>
public sealed class UnifontBitmapFont
{
    public const int NarrowPixelWidth = 8;
    public const int WidePixelWidth = 16;
    public const int CellPixelHeight = 16;

    private const int BmpSize = 65536;
    private const int WideFlag = 0x4000_0000;
    private const int OffsetMask = 0x3FFF_FFFF;

    private const string FontResourceName = "CdBurner.Lithography.Fonts.Unifont.unifont.hex.gz";
    private const string LicenseResourceName = "CdBurner.Lithography.Fonts.Unifont.OFL.txt";
    private static readonly Lazy<UnifontBitmapFont> BuiltInLazy = new(LoadBuiltIn);

    private readonly int[] _bmpIndex;
    private readonly byte[] _slab;
    private readonly Dictionary<int, int>? _supplementary;
    private readonly int _fallbackEntry;

    private UnifontBitmapFont(int[] bmpIndex, byte[] slab, Dictionary<int, int>? supplementary)
    {
        _bmpIndex = bmpIndex;
        _slab = slab;
        _supplementary = supplementary;
        _fallbackEntry = TryGetRawEntry(0xFFFD, out int fffd)
            ? fffd
            : (TryGetRawEntry('?', out int q) ? q : -1);
    }

    public static UnifontBitmapFont BuiltIn => BuiltInLazy.Value;

    public bool HasGlyph(int codePoint) => TryGetRawEntry(codePoint, out _);

    /// <summary>Returns 1 for 8x16 narrow glyphs and 2 for 16x16 wide glyphs.</summary>
    public int GetCellWidth(int codePoint)
    {
        int entry = ResolveEntry(codePoint);
        return entry >= 0 && (entry & WideFlag) != 0 ? 2 : 1;
    }

    public bool IsPixelSet(int codePoint, int pixelCol, int pixelRow)
    {
        if ((uint)pixelRow >= CellPixelHeight)
        {
            return false;
        }

        int entry = ResolveEntry(codePoint);
        if (entry < 0)
        {
            return false;
        }

        int offset = entry & OffsetMask;
        if ((entry & WideFlag) != 0)
        {
            if ((uint)pixelCol >= WidePixelWidth)
            {
                return false;
            }
            byte rowByte = _slab[offset + (pixelRow * 2) + (pixelCol >> 3)];
            return ((rowByte >> (7 - (pixelCol & 7))) & 1) != 0;
        }

        if ((uint)pixelCol >= NarrowPixelWidth)
        {
            return false;
        }

        byte narrowByte = _slab[offset + pixelRow];
        return ((narrowByte >> (7 - pixelCol)) & 1) != 0;
    }

    public static string ReadEmbeddedLicenseText()
    {
        using Stream stream = OpenEmbeddedResource(LicenseResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static UnifontBitmapFont LoadGzip(Stream gzipHexStream)
    {
        ArgumentNullException.ThrowIfNull(gzipHexStream);
        using var gzip = new GZipStream(gzipHexStream, CompressionMode.Decompress, leaveOpen: true);
        return LoadHex(gzip);
    }

    /// <summary>
    /// Stream-parses a Unifont .hex stream without per-line string allocations.
    /// </summary>
    public static UnifontBitmapFont LoadHex(Stream hexStream)
    {
        ArgumentNullException.ThrowIfNull(hexStream);

        int[] bmpIndex = new int[BmpSize];
        Array.Fill(bmpIndex, -1);
        Dictionary<int, int>? supplementary = null;

        byte[] slab = new byte[1536 * 1024];
        int slabLen = 0;

        byte[] readBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        byte[] lineBuf = ArrayPool<byte>.Shared.Rent(256);
        int lineLen = 0;

        try
        {
            int bytesRead;
            while ((bytesRead = hexStream.Read(readBuf, 0, readBuf.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = readBuf[i];
                    if (b == (byte)'\n')
                    {
                        ProcessLine(lineBuf.AsSpan(0, lineLen), bmpIndex, ref supplementary, ref slab, ref slabLen);
                        lineLen = 0;
                    }
                    else if (b != (byte)'\r')
                    {
                        if (lineLen >= lineBuf.Length)
                        {
                            throw new InvalidDataException("Unifont hex line exceeds maximum length.");
                        }
                        lineBuf[lineLen++] = b;
                    }
                }
            }

            if (lineLen > 0)
            {
                ProcessLine(lineBuf.AsSpan(0, lineLen), bmpIndex, ref supplementary, ref slab, ref slabLen);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
            ArrayPool<byte>.Shared.Return(lineBuf);
        }

        if (slabLen != slab.Length)
        {
            Array.Resize(ref slab, slabLen);
        }

        return new UnifontBitmapFont(bmpIndex, slab, supplementary);
    }

    private static void ProcessLine(
        ReadOnlySpan<byte> line,
        int[] bmpIndex,
        ref Dictionary<int, int>? supplementary,
        ref byte[] slab,
        ref int slabLen)
    {
        line = TrimAsciiWhitespace(line);
        if (line.IsEmpty || line[0] == (byte)'#')
        {
            return;
        }

        int colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            throw new InvalidDataException("Unifont hex line is missing ':' separator.");
        }

        int codePoint = ParseHexInt(line[..colon]);
        if (codePoint < 0 || codePoint > 0x10FFFF)
        {
            throw new InvalidDataException($"Unifont code point 0x{codePoint:X} is out of Unicode range.");
        }

        ReadOnlySpan<byte> hexData = TrimAsciiWhitespace(line[(colon + 1)..]);
        if (hexData.Length is not (32 or 64))
        {
            throw new InvalidDataException(
                $"Unifont glyph U+{codePoint:X4} has {hexData.Length} hex digits; expected 32 or 64.");
        }

        bool isWide = hexData.Length == 64;
        int byteCount = hexData.Length >> 1;
        if (slabLen + byteCount > slab.Length)
        {
            int nextSize = Math.Max(slab.Length * 2, slabLen + byteCount);
            Array.Resize(ref slab, nextSize);
        }

        for (int i = 0; i < byteCount; i++)
        {
            int hi = ParseHexNibble(hexData[i * 2]);
            int lo = ParseHexNibble(hexData[(i * 2) + 1]);
            slab[slabLen + i] = (byte)((hi << 4) | lo);
        }

        int entry = (isWide ? WideFlag : 0) | slabLen;
        slabLen += byteCount;

        if (codePoint < BmpSize)
        {
            bmpIndex[codePoint] = entry;
        }
        else
        {
            supplementary ??= new Dictionary<int, int>();
            supplementary[codePoint] = entry;
        }
    }

    private bool TryGetRawEntry(int codePoint, out int entry)
    {
        if ((uint)codePoint < BmpSize)
        {
            entry = _bmpIndex[codePoint];
            return entry >= 0;
        }

        if (_supplementary is not null && _supplementary.TryGetValue(codePoint, out entry))
        {
            return true;
        }

        entry = -1;
        return false;
    }

    private int ResolveEntry(int codePoint)
        => TryGetRawEntry(codePoint, out int entry) ? entry : _fallbackEntry;

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && span[start] <= (byte)' ') start++;
        int end = span.Length;
        while (end > start && span[end - 1] <= (byte)' ') end--;
        return span[start..end];
    }

    private static int ParseHexInt(ReadOnlySpan<byte> hex)
    {
        if (hex.IsEmpty || hex.Length > 6)
        {
            throw new InvalidDataException("Invalid Unifont code point hex length.");
        }
        int value = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            value = (value << 4) | ParseHexNibble(hex[i]);
        }
        return value;
    }

    private static int ParseHexNibble(byte b)
    {
        if (b is >= (byte)'0' and <= (byte)'9') return b - (byte)'0';
        if (b is >= (byte)'A' and <= (byte)'F') return b - (byte)'A' + 10;
        if (b is >= (byte)'a' and <= (byte)'f') return b - (byte)'a' + 10;
        throw new InvalidDataException($"Invalid hex byte 0x{b:X2} in Unifont data.");
    }

    private static UnifontBitmapFont LoadBuiltIn()
    {
        using Stream stream = OpenEmbeddedResource(FontResourceName);
        return LoadGzip(stream);
    }

    private static Stream OpenEmbeddedResource(string name)
    {
        Assembly assembly = typeof(UnifontBitmapFont).Assembly;
        return assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource '{name}'.");
    }
}
