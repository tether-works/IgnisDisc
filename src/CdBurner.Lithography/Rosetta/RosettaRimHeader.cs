// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;
using System.Text;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

/// <summary>What the rim header says and where the band ends.</summary>
/// <param name="Title">Archive title. Shown in capitals; cut short when the rim has no room for all of it.</param>
/// <param name="Date">Archive date, shown as yyyy-MM-dd.</param>
/// <param name="Sha256Prefix">The first 8 to 64 hexadecimal digits of the archive's SHA-256.</param>
/// <param name="OuterRadiusMm">Outer edge of the band: the outer edge of the drawable area.</param>
public sealed record RosettaRimHeaderOptions(
    string Title,
    DateOnly Date,
    string Sha256Prefix,
    double OuterRadiusMm = 58.0);

public enum RosettaRimHeaderItemKind
{
    Text,
    Diagram,
}

/// <summary>
/// One piece of the header strip. A text item is a row of glyph cells; a diagram item is a bitmap whose rows are
/// strings of '#' (ink) and '.' (blank), top row nearest the rim. Angles are clockwise seen from the read side.
/// </summary>
public sealed record RosettaRimHeaderItem(
    RosettaRimHeaderItemKind Kind,
    string Text,
    IReadOnlyList<string> BitmapRows,
    double StartAngleDegrees,
    double AngularSpanDegrees,
    int Columns);

/// <summary>
/// Band 0 of a Rosetta disc: a header in the outer 3.8 mm that can be read without a microscope.
/// <para>
/// From the rim inward: a ring of angle ticks every 5 degrees (0.88 mm), a blank gap (0.20 mm) and a strip
/// (2.72 mm) holding the title, the date, the SHA-256 prefix, and two small diagrams with a caption each that show
/// how to read the pages further in. Capital letters and digits are 2.04 mm tall.
/// </para>
/// <para>
/// Angles are clockwise seen from the read (shiny) side, the same frame as the pages. Tick k is centred on
/// k x 5 degrees. The tick at 0 degrees is three times as wide as the others, ticks at multiples of 30 degrees
/// are longest, other multiples of 10 degrees are medium, the rest are short. The text starts just after
/// 0 degrees, has its top towards the rim and reads clockwise.
/// </para>
/// <para>
/// Every stroke and every gap inside a mark is at least <see cref="MinimumFeatureMm"/> wide, so a laser pointer
/// shone through the disc can project the header as a shadow.
/// </para>
/// </summary>
public sealed class RosettaRimHeader
{
    /// <summary>Smallest stroke or gap inside a mark, in millimetres.</summary>
    public const double MinimumFeatureMm = 0.150;

    /// <summary>Radial height of the whole band.</summary>
    public const double BandHeightMm = TickZoneHeightMm + TickToTextGapMm + TextHeightMm;

    public const int TickCount = 72;
    public const double TickStepDegrees = 360.0 / TickCount;
    public const double TickZoneHeightMm = 0.88;

    /// <summary>Tick width at the rim; the sides are radial, so a tick is about 1.5 % narrower at its inner end.</summary>
    public const double TickWidthMm = 0.30;
    public const double ZeroTickWidthMm = 0.90;
    public const double LongTickLengthMm = TickZoneHeightMm;
    public const double MediumTickLengthMm = 0.60;
    public const double ShortTickLengthMm = 0.40;
    public const double TickToTextGapMm = 0.20;

    public const double GlyphPixelWidthMm = 0.35;
    public const double GlyphPixelHeightMm = 0.34;
    public const double GlyphCellWidthMm = GlyphPixelWidthMm * Spleen5x8Font.CellPixelWidth;
    public const double TextHeightMm = GlyphPixelHeightMm * Spleen5x8Font.CellPixelHeight;

    /// <summary>Height of a capital letter or digit: 6 of the 8 font rows.</summary>
    public const double CapitalHeightMm = GlyphPixelHeightMm * 6;

    public const int DiagramRows = 16;
    public const double DiagramPixelMm = TextHeightMm / DiagramRows;

    /// <summary>Blank radial space the pages must leave below the band.</summary>
    public const double PageGutterMm = 0.20;

    public const int MinimumSha256PrefixLength = 8;
    public const int MaximumSha256PrefixLength = 64;

    public const string ReflectedLightCaption = "MICROSCOPE ON SHINY SIDE, LIGHT FROM LENS SIDE";
    public const string GlassPlateCaption = "BD-R: GLASS PLATE AT 45°, LAMP AT SIDE";

    private const double TwoPi = 2.0 * Math.PI;
    private const double ItemGapMm = 2 * GlyphCellWidthMm;
    private const double LeadMm = GlyphCellWidthMm;
    private const int MinimumTitleColumns = 4;

    /// <summary>
    /// CD and DVD: the microscope looks straight down at the shiny side; a lamp beside the lens lights the spot
    /// it looks at. Top: microscope (left) and lamp (right). Bottom: the disc.
    /// </summary>
    private static readonly string[] ReflectedLightBitmap =
    [
        "........#######.....................",
        "........#######.....###.............",
        "........#######....#####............",
        "........#######....#####............",
        "........#######....#####............",
        "........#######.....###.............",
        ".........#####.....#................",
        "..........###.....#.................",
        ".................#..................",
        "...........#....#...................",
        "...........#...#....................",
        "...........#..#.....................",
        "...........#.#......................",
        "....................................",
        "####################################",
        "####################################",
    ];

    /// <summary>
    /// BD-R: a thin glass plate at 45 degrees between lens and disc; the lamp shines from the side and the plate
    /// turns part of the light down along the viewing direction.
    /// </summary>
    private static readonly string[] GlassPlateBitmap =
    [
        "........#######.....................",
        "........#######.....................",
        "........#######.....................",
        ".........#####......................",
        "..........###.......................",
        "....................................",
        "..............##....................",
        ".............##..........###........",
        "............##..........#####.......",
        "...........##...#######.#####.......",
        "..........##............#####.......",
        ".........##..............###........",
        "........##..........................",
        "....................................",
        "####################################",
        "####################################",
    ];

    private readonly Spleen5x8Font _font;
    private readonly Segment[] _segments;

    public RosettaRimHeader(RosettaRimHeaderOptions options, Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Title);
        ArgumentNullException.ThrowIfNull(options.Sha256Prefix);
        if (!double.IsFinite(options.OuterRadiusMm) || options.OuterRadiusMm <= BandHeightMm)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm,
                "OuterRadiusMm must be finite and larger than the height of the rim band.");
        }

        string sha = options.Sha256Prefix.Trim();
        if (sha.Length < MinimumSha256PrefixLength || sha.Length > MaximumSha256PrefixLength
            || !sha.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                $"Sha256Prefix must be {MinimumSha256PrefixLength} to {MaximumSha256PrefixLength} hexadecimal digits.",
                nameof(options));
        }

        _font = font ?? Spleen5x8Font.BuiltIn;
        OuterRadiusMm = options.OuterRadiusMm;
        InnerRadiusMm = options.OuterRadiusMm - BandHeightMm;
        TextOuterRadiusMm = options.OuterRadiusMm - TickZoneHeightMm - TickToTextGapMm;
        TextMidRadiusMm = TextOuterRadiusMm - (0.5 * TextHeightMm);
        Date = options.Date;
        Sha256Prefix = sha.ToUpperInvariant();

        string reflectedCaption = ToHeaderText(ReflectedLightCaption, _font);
        string glassCaption = ToHeaderText(GlassPlateCaption, _font);
        string tail = string.Create(CultureInfo.InvariantCulture, $"{options.Date:yyyy-MM-dd}  SHA-256 {Sha256Prefix}");

        // Everything except the title has a fixed length; the title gets what is left of the circle.
        double circumferenceMm = TwoPi * TextMidRadiusMm;
        double fixedMm = LeadMm
            + (tail.Length * GlyphCellWidthMm) + ItemGapMm
            + (reflectedCaption.Length * GlyphCellWidthMm) + ItemGapMm
            + (ReflectedLightBitmap[0].Length * DiagramPixelMm) + ItemGapMm
            + (glassCaption.Length * GlyphCellWidthMm) + ItemGapMm
            + (GlassPlateBitmap[0].Length * DiagramPixelMm) + ItemGapMm;
        int titleColumns = (int)Math.Floor((circumferenceMm - fixedMm) / GlyphCellWidthMm) - 2;
        if (titleColumns < MinimumTitleColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm,
                "The rim at this radius is too short for the header text and diagrams.");
        }

        string title = ToHeaderText(options.Title, _font).Trim();
        if (title.Length > titleColumns)
        {
            title = title[..titleColumns].TrimEnd();
        }
        Title = title;
        HeaderText = title.Length == 0 ? tail : title + "  " + tail;

        var segments = new List<Segment>(5);
        var items = new List<RosettaRimHeaderItem>(5);
        double cursorMm = LeadMm;
        AddText(HeaderText);
        AddText(reflectedCaption);
        AddDiagram(ReflectedLightBitmap);
        AddText(glassCaption);
        AddDiagram(GlassPlateBitmap);
        _segments = [.. segments];
        Items = items;

        void AddText(string text)
        {
            double widthMm = text.Length * GlyphCellWidthMm;
            segments.Add(new Segment(cursorMm, widthMm, text, null));
            items.Add(new RosettaRimHeaderItem(RosettaRimHeaderItemKind.Text, text, [],
                ArcToDegrees(cursorMm), ArcToDegrees(widthMm), text.Length));
            cursorMm += widthMm + ItemGapMm;
        }

        void AddDiagram(string[] bitmap)
        {
            double widthMm = bitmap[0].Length * DiagramPixelMm;
            segments.Add(new Segment(cursorMm, widthMm, null, bitmap));
            items.Add(new RosettaRimHeaderItem(RosettaRimHeaderItemKind.Diagram, string.Empty, bitmap,
                ArcToDegrees(cursorMm), ArcToDegrees(widthMm), bitmap[0].Length));
            cursorMm += widthMm + ItemGapMm;
        }
    }

    /// <summary>Outer edge of the band (the rim side of the ticks), in millimetres.</summary>
    public double OuterRadiusMm { get; }

    /// <summary>Inner edge of the band (the bottom of the text), in millimetres.</summary>
    public double InnerRadiusMm { get; }

    /// <summary>Radius of the top of the text strip, in millimetres.</summary>
    public double TextOuterRadiusMm { get; }

    /// <summary>Radius at which glyph cells have their stated width, in millimetres.</summary>
    public double TextMidRadiusMm { get; }

    /// <summary>The title as shown: capitals, possibly cut short.</summary>
    public string Title { get; }

    public DateOnly Date { get; }

    /// <summary>The SHA-256 prefix as shown: capitals.</summary>
    public string Sha256Prefix { get; }

    /// <summary>The first text item: title, date and SHA-256 prefix.</summary>
    public string HeaderText { get; }

    /// <summary>The strip's items in clockwise order: header text, caption, diagram, caption, diagram.</summary>
    public IReadOnlyList<RosettaRimHeaderItem> Items { get; }

    /// <summary>Length in millimetres of tick <paramref name="tickIndex"/> (0 to 71), measured from the rim inward.</summary>
    public static double TickLengthMm(int tickIndex)
    {
        if ((uint)tickIndex >= TickCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tickIndex), tickIndex, "Tick index must be 0 to 71.");
        }
        return tickIndex % 6 == 0 ? LongTickLengthMm
            : tickIndex % 2 == 0 ? MediumTickLengthMm
            : ShortTickLengthMm;
    }

    /// <summary>
    /// True when the point is ink. Does not allocate and may be called from several threads at once.
    /// </summary>
    /// <param name="radiusMm">Distance from the disc centre, in millimetres.</param>
    /// <param name="clockwiseAngleRadians">Angle clockwise seen from the read side, in radians; any finite value.</param>
    public bool IsInk(double radiusMm, double clockwiseAngleRadians)
    {
        if (!double.IsFinite(radiusMm) || !double.IsFinite(clockwiseAngleRadians)
            || radiusMm < InnerRadiusMm || radiusMm > OuterRadiusMm)
        {
            return false;
        }

        double angle = clockwiseAngleRadians - (TwoPi * Math.Floor(clockwiseAngleRadians / TwoPi));
        if (angle < 0.0 || angle >= TwoPi)
        {
            angle = 0.0;
        }

        double depthMm = OuterRadiusMm - radiusMm;
        if (depthMm <= TickZoneHeightMm)
        {
            const double stepRadians = TwoPi / TickCount;
            int tick = (int)Math.Round(angle / stepRadians);
            double offsetRadians = Math.Abs(angle - (tick * stepRadians));
            tick %= TickCount;

            // The sides of a tick are radial lines, so its width is an angle: the stated width at the rim.
            double halfWidthRadians = 0.5 * (tick == 0 ? ZeroTickWidthMm : TickWidthMm) / OuterRadiusMm;
            double lengthMm = tick % 6 == 0 ? LongTickLengthMm
                : tick % 2 == 0 ? MediumTickLengthMm
                : ShortTickLengthMm;
            return offsetRadians <= halfWidthRadians && depthMm <= lengthMm;
        }

        double yMm = depthMm - (TickZoneHeightMm + TickToTextGapMm);
        if (yMm < 0.0 || yMm >= TextHeightMm)
        {
            return false;
        }

        double uMm = angle * TextMidRadiusMm;
        for (int i = 0; i < _segments.Length; i++)
        {
            ref readonly Segment segment = ref _segments[i];
            double xMm = uMm - segment.StartMm;
            if (xMm < 0.0)
            {
                return false; // segments are in clockwise order
            }
            if (xMm >= segment.WidthMm)
            {
                continue;
            }

            if (segment.Text is { } text)
            {
                int column = Math.Min((int)(xMm / GlyphCellWidthMm), text.Length - 1);
                double xInCellMm = xMm - (column * GlyphCellWidthMm);
                int px = Math.Min((int)(xInCellMm / GlyphPixelWidthMm), Spleen5x8Font.CellPixelWidth - 1);
                int py = Math.Min((int)(yMm / GlyphPixelHeightMm), Spleen5x8Font.CellPixelHeight - 1);
                return _font.IsPixelSet(text[column], px, py);
            }

            string[] bitmap = segment.Bitmap!;
            int bx = Math.Min((int)(xMm / DiagramPixelMm), bitmap[0].Length - 1);
            int by = Math.Min((int)(yMm / DiagramPixelMm), DiagramRows - 1);
            return bitmap[by][bx] == '#';
        }

        return false;
    }

    private double ArcToDegrees(double arcMm) => arcMm / TextMidRadiusMm * (180.0 / Math.PI);

    /// <summary>
    /// Capitals only, so every letter is the full 2 mm; characters the font cannot draw become '?'.
    /// </summary>
    private static string ToHeaderText(string text, Spleen5x8Font font)
    {
        var builder = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
            {
                builder.Append(' ');
                continue;
            }

            Rune upper = Rune.ToUpperInvariant(rune);
            builder.Append(upper.IsBmp && font.HasGlyph(upper.Value) ? (char)upper.Value : '?');
        }
        return builder.ToString();
    }

    private readonly record struct Segment(double StartMm, double WidthMm, string? Text, string[]? Bitmap);
}
