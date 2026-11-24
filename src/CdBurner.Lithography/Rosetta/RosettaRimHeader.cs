// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;
using System.Text;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

/// <summary>What the rim header says and where the band ends.</summary>
/// <param name="Title">Archive title. Shown in capitals; cut short when the rim has no room for all of it.</param>
/// <param name="Date">Archive date, shown as yyyy-MM-dd.</param>
/// <param name="Sha256Prefix">The first 8 to 64 hexadecimal digits of the archive's SHA-256.</param>
/// <param name="OuterRadiusMm">Outer edge of the band: the outer edge of the drawable area.</param>
/// <param name="Scale">
/// Size of the header against its full size (1): every length in the band is multiplied by it. Greater than 0, at
/// most 1. <see cref="RosettaRimHeader.FitToBand"/> picks it for a ring of a given width.
/// </param>
/// <param name="TrackPitchMm">
/// Track pitch of the disc in millimetres, or 0. When given, a glyph row is a whole, even number of track turns
/// (rounded down), so every row of a letter is drawn by the same number of turns.
/// </param>
public sealed record RosettaRimHeaderOptions(
    string Title,
    DateOnly Date,
    string Sha256Prefix,
    double OuterRadiusMm = 58.0,
    double Scale = 1.0,
    double TrackPitchMm = 0.0);

/// <summary>
/// Smallest glyph cell that still reads on one disc family, from the resolution report: CD 50 x 75 um (a CD
/// symbol is 4.7 um long and 27 % of each frame is parity), DVD and BD 26 x 34 um.
/// </summary>
public sealed record RosettaRimHeaderFormat(string Name, double MinimumGlyphCellWidthMm, double MinimumGlyphCellHeightMm)
{
    public static RosettaRimHeaderFormat Cd { get; } = new("a CD", 0.050, 0.075);
    public static RosettaRimHeaderFormat Dvd { get; } = new("a DVD", 0.026, 0.034);
    public static RosettaRimHeaderFormat BluRay { get; } = new("a BD", 0.026, 0.034);
}

/// <summary>Every length of one header, in millimetres. <see cref="FullSize"/> is the header at scale 1.</summary>
public sealed record RosettaRimHeaderMetrics(
    double Scale,
    double TickZoneHeightMm,
    double TickWidthMm,
    double ZeroTickWidthMm,
    double MediumTickLengthMm,
    double ShortTickLengthMm,
    double TickToTextGapMm,
    double GlyphPixelWidthMm,
    double GlyphPixelHeightMm,
    double PageGutterMm,
    double MinimumFeatureMm,
    int? TurnsPerGlyphRow)
{
    public static RosettaRimHeaderMetrics FullSize { get; } = Create(1.0, 0.0);

    public double GlyphCellWidthMm => GlyphPixelWidthMm * Spleen5x8Font.CellPixelWidth;
    public double TextHeightMm => GlyphPixelHeightMm * Spleen5x8Font.CellPixelHeight;
    public double CapitalHeightMm => GlyphPixelHeightMm * 6;
    public double DiagramPixelMm => TextHeightMm / RosettaRimHeader.DiagramRows;
    public double LongTickLengthMm => TickZoneHeightMm;
    public double BandHeightMm => TickZoneHeightMm + TickToTextGapMm + TextHeightMm;

    /// <summary>
    /// The lengths at <paramref name="scale"/>. With a track pitch the glyph row is rounded down to a whole, even
    /// number of turns, so a diagram row (half a glyph row) is whole turns too. At scale 1 without a pitch the
    /// lengths are exactly the full-size constants of <see cref="RosettaRimHeader"/>.
    /// </summary>
    public static RosettaRimHeaderMetrics Create(double scale, double trackPitchMm)
    {
        if (!double.IsFinite(scale) || scale <= 0.0 || scale > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The header scale must be greater than 0 and at most 1.");
        }
        if (!double.IsFinite(trackPitchMm) || trackPitchMm < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackPitchMm), trackPitchMm, "The track pitch must be 0 or a positive number.");
        }

        double rowMm = RosettaRimHeader.GlyphPixelHeightMm * scale;
        int? turns = null;
        if (trackPitchMm > 0.0)
        {
            double halfTurns = Math.Floor((rowMm / (2.0 * trackPitchMm)) + 1e-9);
            if (!double.IsFinite(halfTurns) || halfTurns > int.MaxValue / 2)
            {
                throw new ArgumentOutOfRangeException(nameof(trackPitchMm), trackPitchMm, string.Create(CultureInfo.InvariantCulture,
                    $"The track pitch ({trackPitchMm * 1000.0:0.###} um) is too fine for glyph rows of {rowMm * 1000.0:0.#} um."));
            }
            int even = 2 * (int)halfTurns;
            if (even < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(trackPitchMm), trackPitchMm, string.Create(CultureInfo.InvariantCulture,
                    $"The track pitch ({trackPitchMm * 1000.0:0.###} um) is too coarse for glyph rows of {rowMm * 1000.0:0.#} um: a row needs at least two turns."));
            }
            turns = even;
            rowMm = even * trackPitchMm;
        }

        return new RosettaRimHeaderMetrics(
            scale,
            RosettaRimHeader.TickZoneHeightMm * scale,
            RosettaRimHeader.TickWidthMm * scale,
            RosettaRimHeader.ZeroTickWidthMm * scale,
            RosettaRimHeader.MediumTickLengthMm * scale,
            RosettaRimHeader.ShortTickLengthMm * scale,
            RosettaRimHeader.TickToTextGapMm * scale,
            RosettaRimHeader.GlyphPixelWidthMm * scale,
            rowMm,
            RosettaRimHeader.PageGutterMm * scale,
            RosettaRimHeader.MinimumFeatureMm * scale,
            turns);
    }
}

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
/// At full size every stroke and every gap inside a mark is at least <see cref="MinimumFeatureMm"/> wide, so a
/// laser pointer shone through the disc can project the header as a shadow. A header made smaller for a narrow
/// ring (<see cref="RosettaRimHeaderOptions.Scale"/>, <see cref="FitToBand"/>) keeps the same layout with every
/// length scaled and then needs a magnifier or a microscope. The constants below are the full-size lengths;
/// <see cref="Metrics"/> holds the lengths of this header.
/// </para>
/// </summary>
public sealed class RosettaRimHeader
{
    /// <summary>Smallest stroke or gap inside a mark at full size, in millimetres.</summary>
    public const double MinimumFeatureMm = 0.150;

    /// <summary>Radial height of the whole band at full size.</summary>
    public const double BandHeightMm = TickZoneHeightMm + TickToTextGapMm + TextHeightMm;

    /// <summary>Radial depth of band plus gutter at full size: the ring <see cref="FitToBand"/> needs for scale 1.</summary>
    public const double FullDepthMm = BandHeightMm + PageGutterMm;

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

    /// <summary>Height of a capital letter or digit at full size: 6 of the 8 font rows.</summary>
    public const double CapitalHeightMm = GlyphPixelHeightMm * 6;

    public const int DiagramRows = 16;
    public const double DiagramPixelMm = TextHeightMm / DiagramRows;

    /// <summary>Blank radial space the pages (or a picture) must leave below the band at full size.</summary>
    public const double PageGutterMm = 0.20;

    public const int MinimumSha256PrefixLength = 8;
    public const int MaximumSha256PrefixLength = 64;

    public const string ReflectedLightCaption = "MICROSCOPE ON SHINY SIDE, LIGHT FROM LENS SIDE";
    public const string GlassPlateCaption = "BD-R: GLASS PLATE AT 45°, LAMP AT SIDE";

    private const double TwoPi = 2.0 * Math.PI;
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
    private readonly RosettaRimHeaderMetrics _m;

    public RosettaRimHeader(RosettaRimHeaderOptions options, Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Title);
        ArgumentNullException.ThrowIfNull(options.Sha256Prefix);
        RosettaRimHeaderMetrics metrics = RosettaRimHeaderMetrics.Create(options.Scale, options.TrackPitchMm);
        if (!double.IsFinite(options.OuterRadiusMm) || options.OuterRadiusMm <= metrics.BandHeightMm)
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
        _m = metrics;
        OuterRadiusMm = options.OuterRadiusMm;
        InnerRadiusMm = options.OuterRadiusMm - metrics.BandHeightMm;
        TextOuterRadiusMm = options.OuterRadiusMm - metrics.TickZoneHeightMm - metrics.TickToTextGapMm;
        TextMidRadiusMm = TextOuterRadiusMm - (0.5 * metrics.TextHeightMm);
        Date = options.Date;
        Sha256Prefix = sha.ToUpperInvariant();

        double cellMm = metrics.GlyphCellWidthMm;
        double itemGapMm = 2 * cellMm;
        double leadMm = cellMm;
        double diagramPixelMm = metrics.DiagramPixelMm;
        string reflectedCaption = ToHeaderText(ReflectedLightCaption, _font);
        string glassCaption = ToHeaderText(GlassPlateCaption, _font);
        string tail = string.Create(CultureInfo.InvariantCulture, $"{options.Date:yyyy-MM-dd}  SHA-256 {Sha256Prefix}");

        // Everything except the title has a fixed length; the title gets what is left of the circle.
        double circumferenceMm = TwoPi * TextMidRadiusMm;
        double fixedMm = leadMm
            + (tail.Length * cellMm) + itemGapMm
            + (reflectedCaption.Length * cellMm) + itemGapMm
            + (ReflectedLightBitmap[0].Length * diagramPixelMm) + itemGapMm
            + (glassCaption.Length * cellMm) + itemGapMm
            + (GlassPlateBitmap[0].Length * diagramPixelMm) + itemGapMm;
        int titleColumns = (int)Math.Floor((circumferenceMm - fixedMm) / cellMm) - 2;
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
        double cursorMm = leadMm;
        AddText(HeaderText);
        AddText(reflectedCaption);
        AddDiagram(ReflectedLightBitmap);
        AddText(glassCaption);
        AddDiagram(GlassPlateBitmap);
        _segments = [.. segments];
        Items = items;

        void AddText(string text)
        {
            double widthMm = text.Length * cellMm;
            segments.Add(new Segment(cursorMm, widthMm, text, null));
            items.Add(new RosettaRimHeaderItem(RosettaRimHeaderItemKind.Text, text, [],
                ArcToDegrees(cursorMm), ArcToDegrees(widthMm), text.Length));
            cursorMm += widthMm + itemGapMm;
        }

        void AddDiagram(string[] bitmap)
        {
            double widthMm = bitmap[0].Length * diagramPixelMm;
            segments.Add(new Segment(cursorMm, widthMm, null, bitmap));
            items.Add(new RosettaRimHeaderItem(RosettaRimHeaderItemKind.Diagram, string.Empty, bitmap,
                ArcToDegrees(cursorMm), ArcToDegrees(widthMm), bitmap[0].Length));
            cursorMm += widthMm + itemGapMm;
        }
    }

    /// <summary>
    /// The largest header, up to full size, that fits with its gutter in the ring from
    /// <paramref name="ringInnerRadiusMm"/> to <paramref name="ringOuterRadiusMm"/>, with glyph rows of whole track
    /// turns. Refuses, with the reason, when the ring is too narrow for glyph cells of the format's minimum size
    /// (<see cref="RosettaRimHeaderFormat"/>) or for two track turns per glyph row.
    /// </summary>
    /// <param name="trackPitchMm">Track pitch of the disc near the rim, in millimetres (greater than 0).</param>
    /// <exception cref="ArgumentException">The ring is too narrow for readable text; the message says why.</exception>
    public static RosettaRimHeader FitToBand(
        string title,
        DateOnly date,
        string sha256Prefix,
        RosettaRimHeaderFormat format,
        double ringInnerRadiusMm,
        double ringOuterRadiusMm,
        double trackPitchMm,
        Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (!double.IsFinite(ringInnerRadiusMm) || !double.IsFinite(ringOuterRadiusMm)
            || ringInnerRadiusMm < 0.0 || ringOuterRadiusMm <= ringInnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(ringOuterRadiusMm), ringOuterRadiusMm,
                "The ring for the rim header must have an outer radius larger than its inner radius.");
        }
        if (!double.IsFinite(trackPitchMm) || trackPitchMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackPitchMm), trackPitchMm, "The track pitch must be positive.");
        }

        // Band plus gutter is FullDepthMm at scale 1; rounding rows to whole turns only ever makes the band shallower.
        double availableMm = ringOuterRadiusMm - ringInnerRadiusMm;
        double scale = Math.Min(1.0, availableMm / FullDepthMm);

        // Smallest scale whose glyph cells reach the format's minimum after rows are rounded down to even turns.
        double minimumRowMm = format.MinimumGlyphCellHeightMm / Spleen5x8Font.CellPixelHeight;
        double rowFloorMm = 2.0 * trackPitchMm * Math.Max(1.0, Math.Ceiling((minimumRowMm / (2.0 * trackPitchMm)) - 1e-9));
        double widthScale = format.MinimumGlyphCellWidthMm / GlyphCellWidthMm;
        double rowScale = rowFloorMm / GlyphPixelHeightMm;
        double minimumScale = Math.Max(widthScale, rowScale);
        if (minimumScale > 1.0 + 1e-9)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The track pitch of this disc ({trackPitchMm * 1000.0:0.###} um between turns) is too coarse for the rim header: "
                + $"a glyph row of the full-size header ({GlyphPixelHeightMm:0.00} mm) must span at least two turns. Leave the rim header off for this disc."));
        }
        if (scale < minimumScale * (1.0 - 1e-9))
        {
            double neededMm = Math.Min(1.0, minimumScale) * FullDepthMm;
            string rows = rowScale > widthScale && rowFloorMm > minimumRowMm * (1.0 + 1e-6)
                ? string.Create(CultureInfo.InvariantCulture, $" and glyph rows of at least two track turns ({trackPitchMm * 1000.0:0.###} um pitch)")
                : string.Empty;
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The rim header does not fit: the ring for it is only {availableMm:0.000} mm wide (r = {ringInnerRadiusMm:0.000} to {ringOuterRadiusMm:0.000} mm). "
                + $"Readable text on {format.Name} needs glyph cells of at least {format.MinimumGlyphCellWidthMm * 1000.0:0} x {format.MinimumGlyphCellHeightMm * 1000.0:0} um{rows}, "
                + $"so a ring of at least {neededMm:0.000} mm. Leave more room for the picture at the rim, or leave the rim header off."));
        }

        return new RosettaRimHeader(
            new RosettaRimHeaderOptions(title, date, sha256Prefix, ringOuterRadiusMm, Math.Min(1.0, Math.Max(scale, minimumScale)), trackPitchMm),
            font);
    }

    /// <summary>Every length of this header (scaled and fitted to the track pitch).</summary>
    public RosettaRimHeaderMetrics Metrics => _m;

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

    /// <summary>Full-size length in millimetres of tick <paramref name="tickIndex"/> (0 to 71), measured from the rim inward.</summary>
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

        RosettaRimHeaderMetrics m = _m;
        double depthMm = OuterRadiusMm - radiusMm;
        if (depthMm <= m.TickZoneHeightMm)
        {
            const double stepRadians = TwoPi / TickCount;
            int tick = (int)Math.Round(angle / stepRadians);
            double offsetRadians = Math.Abs(angle - (tick * stepRadians));
            tick %= TickCount;

            // The sides of a tick are radial lines, so its width is an angle: the stated width at the rim.
            double halfWidthRadians = 0.5 * (tick == 0 ? m.ZeroTickWidthMm : m.TickWidthMm) / OuterRadiusMm;
            double lengthMm = tick % 6 == 0 ? m.LongTickLengthMm
                : tick % 2 == 0 ? m.MediumTickLengthMm
                : m.ShortTickLengthMm;
            return offsetRadians <= halfWidthRadians && depthMm <= lengthMm;
        }

        double yMm = depthMm - (m.TickZoneHeightMm + m.TickToTextGapMm);
        if (yMm < 0.0 || yMm >= m.TextHeightMm)
        {
            return false;
        }

        double cellMm = m.GlyphCellWidthMm;
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
                int column = Math.Min((int)(xMm / cellMm), text.Length - 1);
                double xInCellMm = xMm - (column * cellMm);
                int px = Math.Min((int)(xInCellMm / m.GlyphPixelWidthMm), Spleen5x8Font.CellPixelWidth - 1);
                int py = Math.Min((int)(yMm / m.GlyphPixelHeightMm), Spleen5x8Font.CellPixelHeight - 1);
                return _font.IsPixelSet(text[column], px, py);
            }

            string[] bitmap = segment.Bitmap!;
            double diagramPixelMm = m.DiagramPixelMm;
            int bx = Math.Min((int)(xMm / diagramPixelMm), bitmap[0].Length - 1);
            int by = Math.Min((int)(yMm / diagramPixelMm), DiagramRows - 1);
            return bitmap[by][bx] == '#';
        }

        return false;
    }

    private double ArcToDegrees(double arcMm) => arcMm / TextMidRadiusMm * (180.0 / Math.PI);

    /// <summary>
    /// Capitals only, so every letter is the full cell height; characters the font cannot draw become '?'.
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

/// <summary>
/// The rim header drawn over a picture disc. Inside the band and its gutter the header replaces the picture (ink
/// dark, everything else light), so the text reads on any picture. Coordinates are the picture's (x right, y down):
/// angle 0 of the header is the picture's right-hand side and the text runs clockwise as the picture is seen.
/// </summary>
public sealed class RosettaRimHeaderOverlay : IPictureOverlay
{
    public RosettaRimHeaderOverlay(RosettaRimHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
        OuterRadiusMm = header.OuterRadiusMm;
        InnerRadiusMm = Math.Max(0.0, header.InnerRadiusMm - header.Metrics.PageGutterMm);
    }

    public RosettaRimHeader Header { get; }

    public double InnerRadiusMm { get; }

    public double OuterRadiusMm { get; }

    public bool TrySample(double xMm, double yDownMm, out byte gray)
    {
        double radiusMm = Math.Sqrt((xMm * xMm) + (yDownMm * yDownMm));
        if (!(radiusMm >= InnerRadiusMm && radiusMm <= OuterRadiusMm))
        {
            gray = RosettaDvdEncoder.PaperLuminance;
            return false;
        }

        gray = Header.IsInk(radiusMm, Math.Atan2(yDownMm, xMm))
            ? RosettaDvdEncoder.InkLuminance
            : RosettaDvdEncoder.PaperLuminance;
        return true;
    }
}
