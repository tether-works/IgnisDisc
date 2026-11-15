using System.Globalization;
using System.Reflection;

namespace CdBurner.Lithography.Rosetta.Fonts;

/// <summary>
/// Bitmap parser for Adobe BDF 2.1 Spleen 5x8 glyphs, preserving exact BDF baseline positioning.
/// </summary>
public sealed class Spleen5x8Font
{
    public const int CellPixelWidth = 5;
    public const int CellPixelHeight = 8;

    private const string FontResourceName = "CdBurner.Lithography.Fonts.Spleen.spleen-5x8.bdf";
    private const string LicenseResourceName = "CdBurner.Lithography.Fonts.Spleen.LICENSE";
    private static readonly Lazy<Spleen5x8Font> BuiltInLazy = new(LoadBuiltIn);

    private readonly ulong[] _glyphs;
    private readonly bool[] _hasGlyph;

    private Spleen5x8Font(ulong[] glyphs, bool[] hasGlyph)
    {
        _glyphs = glyphs;
        _hasGlyph = hasGlyph;
    }

    public static Spleen5x8Font BuiltIn => BuiltInLazy.Value;

    public bool HasGlyph(int codePoint)
        => (uint)codePoint < 256u && _hasGlyph[codePoint];

    public ulong GetPackedGlyph(int codePoint)
        => HasGlyph(codePoint) ? _glyphs[codePoint] : 0UL;

    public bool IsPixelSet(int codePoint, int pixelCol, int pixelRow)
    {
        if ((uint)pixelCol >= CellPixelWidth || (uint)pixelRow >= CellPixelHeight || !HasGlyph(codePoint))
        {
            return false;
        }

        return ((_glyphs[codePoint] >> ((pixelRow * 8) + pixelCol)) & 1UL) != 0;
    }

    public static string ReadEmbeddedLicenseText()
    {
        using Stream stream = OpenEmbeddedResource(LicenseResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Loads a 5x8 BDF font from a stream.</summary>
    public static Spleen5x8Font Load(Stream bdfStream)
    {
        ArgumentNullException.ThrowIfNull(bdfStream);

        var glyphs = new ulong[256];
        var hasGlyph = new bool[256];

        using var reader = new StreamReader(bdfStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        bool sawFbb = false;
        int fbbOffX = 0;
        int fbbOffY = 0;
        const int fbbH = CellPixelHeight;

        bool inChar = false;
        int encoding = -1;
        bool sawBbx = false;
        int bbxW = 0, bbxH = 0, bbxOffX = 0, bbxOffY = 0;
        bool inBitmap = false;
        int bitmapRow = 0;
        ulong packed = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty)
            {
                continue;
            }

            if (!inChar)
            {
                if (StartsWithKeyword(span, "FONTBOUNDINGBOX"))
                {
                    ReadOnlySpan<char> args = span["FONTBOUNDINGBOX".Length..].Trim();
                    int w = ReadNextInt(ref args, "FONTBOUNDINGBOX width");
                    int h = ReadNextInt(ref args, "FONTBOUNDINGBOX height");
                    fbbOffX = ReadNextInt(ref args, "FONTBOUNDINGBOX x-offset");
                    fbbOffY = ReadNextInt(ref args, "FONTBOUNDINGBOX y-offset");
                    if (w != CellPixelWidth || h != CellPixelHeight)
                    {
                        throw new InvalidDataException($"Expected 5x8 FONTBOUNDINGBOX, got {w}x{h}.");
                    }
                    sawFbb = true;
                }
                else if (StartsWithKeyword(span, "STARTCHAR"))
                {
                    if (!sawFbb)
                    {
                        throw new InvalidDataException("BDF STARTCHAR encountered before FONTBOUNDINGBOX.");
                    }
                    inChar = true;
                    encoding = -1;
                    sawBbx = false;
                    inBitmap = false;
                    bitmapRow = 0;
                    packed = 0;
                }
                continue;
            }

            if (StartsWithKeyword(span, "ENDCHAR"))
            {
                if (inBitmap && bitmapRow != bbxH)
                {
                    throw new InvalidDataException($"BDF glyph ENCODING {encoding} ended after {bitmapRow} of {bbxH} bitmap rows.");
                }
                if ((uint)encoding < 256u)
                {
                    glyphs[encoding] = packed;
                    hasGlyph[encoding] = true;
                }
                inChar = false;
                inBitmap = false;
                continue;
            }

            if (inBitmap)
            {
                if (bitmapRow < bbxH)
                {
                    if ((uint)encoding < 256u)
                    {
                        AccumulateBitmapRow(span, bitmapRow, bbxW, bbxH, bbxOffX, bbxOffY, fbbOffX, fbbOffY, fbbH, ref packed);
                    }
                    bitmapRow++;
                }
                continue;
            }

            if (StartsWithKeyword(span, "ENCODING"))
            {
                ReadOnlySpan<char> args = span["ENCODING".Length..].Trim();
                encoding = ReadNextInt(ref args, "ENCODING");
            }
            else if (StartsWithKeyword(span, "BBX"))
            {
                ReadOnlySpan<char> args = span["BBX".Length..].Trim();
                bbxW = ReadNextInt(ref args, "BBX width");
                bbxH = ReadNextInt(ref args, "BBX height");
                bbxOffX = ReadNextInt(ref args, "BBX x-offset");
                bbxOffY = ReadNextInt(ref args, "BBX y-offset");
                if (bbxW < 0 || bbxH < 0)
                {
                    throw new InvalidDataException($"Invalid negative BDF BBX dimensions {bbxW}x{bbxH}.");
                }
                sawBbx = true;
            }
            else if (span.Equals("BITMAP", StringComparison.Ordinal))
            {
                if (!sawBbx)
                {
                    throw new InvalidDataException($"BDF glyph ENCODING {encoding} missing BBX before BITMAP.");
                }
                inBitmap = true;
                bitmapRow = 0;
            }
        }

        if (!sawFbb)
        {
            throw new InvalidDataException("BDF stream is missing FONTBOUNDINGBOX.");
        }

        return new Spleen5x8Font(glyphs, hasGlyph);
    }

    private static void AccumulateBitmapRow(
        ReadOnlySpan<char> hexRow,
        int r,
        int bbxW,
        int bbxH,
        int bbxOffX,
        int bbxOffY,
        int fbbOffX,
        int fbbOffY,
        int fbbH,
        ref ulong packed)
    {
        int expectedBytes = (bbxW + 7) >> 3;
        if (hexRow.Length < expectedBytes * 2)
        {
            throw new InvalidDataException($"BDF bitmap row '{hexRow}' is shorter than {expectedBytes} hex bytes.");
        }

        int cellRow = (fbbOffY + fbbH) - (bbxOffY + bbxH) + r;
        if ((uint)cellRow >= CellPixelHeight)
        {
            return;
        }

        for (int b = 0; b < bbxW; b++)
        {
            int byteIdx = b >> 3;
            int hexIdx = byteIdx * 2;
            int byteVal = (ParseHexNibble(hexRow[hexIdx]) << 4) | ParseHexNibble(hexRow[hexIdx + 1]);
            bool set = ((byteVal >> (7 - (b & 7))) & 1) != 0;
            if (!set)
            {
                continue;
            }

            int cellCol = (bbxOffX - fbbOffX) + b;
            if ((uint)cellCol < CellPixelWidth)
            {
                packed |= 1UL << ((cellRow * 8) + cellCol);
            }
        }
    }

    private static int ParseHexNibble(char c)
    {
        if (c is >= '0' and <= '9') return c - '0';
        if (c is >= 'A' and <= 'F') return c - 'A' + 10;
        if (c is >= 'a' and <= 'f') return c - 'a' + 10;
        throw new InvalidDataException($"Invalid hex character '{c}' in BDF bitmap.");
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> line, string keyword)
        => line.StartsWith(keyword, StringComparison.Ordinal)
           && (line.Length == keyword.Length || char.IsWhiteSpace(line[keyword.Length]));

    private static int ReadNextInt(ref ReadOnlySpan<char> span, string field)
    {
        span = span.TrimStart();
        if (span.IsEmpty)
        {
            throw new InvalidDataException($"Missing integer for {field}.");
        }

        int end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        if (!int.TryParse(span[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException($"Invalid integer '{span[..end]}' for {field}.");
        }

        span = span[end..];
        return value;
    }

    private static Spleen5x8Font LoadBuiltIn()
    {
        using Stream stream = OpenEmbeddedResource(FontResourceName);
        return Load(stream);
    }

    private static Stream OpenEmbeddedResource(string name)
    {
        Assembly assembly = typeof(Spleen5x8Font).Assembly;
        return assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource '{name}'.");
    }
}
