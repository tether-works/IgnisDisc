// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;
using System.Text;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

/// <summary>
/// A ring of large text around a picture disc (title and date) that can be read without a microscope. The letters
/// are Spleen 5x8 glyphs scaled up so that the 8 font rows fill the ring between two blank edges; each font pixel is a
/// square of <see cref="GlyphPixelMm"/> millimetres (its width measured at the middle of the text). Ink and blank are
/// strictly two levels, so the strokes are drawn without dither.
/// <para>
/// Angles are clockwise seen from the read (shiny) side, the frame the pictures and the Rosetta pages use: with
/// x to the right and y down, 0 degrees points right and 270 degrees points up. The text is centred on 270 degrees,
/// has its top towards the rim and reads clockwise, so it reads left to right across the top of the disc.
/// </para>
/// <para>
/// Nothing is rasterised: <see cref="IsInk"/> works out the glyph pixel at a point from the font, so the memory used
/// does not depend on the disc or the ring size.
/// </para>
/// </summary>
public sealed class RosettaTitleRing
{
    /// <summary>Blank radial space at the rim side and at the inner side of the ring, in millimetres.</summary>
    public const double EdgeGapMm = 0.25;

    /// <summary>Smallest font pixel, in millimetres; the same limit as the rim header's strokes.</summary>
    public const double MinimumGlyphPixelMm = RosettaRimHeader.MinimumFeatureMm;

    /// <summary>Smallest ring that leaves room for the edge gaps and 8 font rows of the minimum pixel size.</summary>
    public const double MinimumHeightMm = (2 * EdgeGapMm) + (Spleen5x8Font.CellPixelHeight * MinimumGlyphPixelMm);

    /// <summary>Clockwise angle of the middle of the text: the top of the disc seen from the read side.</summary>
    public const double CentreAngleDegrees = 270.0;

    /// <summary>Blank columns kept free between the end and the start of the text when it is cut to fit.</summary>
    private const int ClosingGapColumns = 4;

    private const double TwoPi = 2.0 * Math.PI;

    private readonly Spleen5x8Font _font;
    private readonly double _startAngleRadians;
    private readonly double _spanRadians;

    /// <param name="title">The title; shown in capitals and cut short when the ring has no room for all of it.</param>
    /// <param name="date">The date, shown as yyyy-MM-dd after the title; null shows the title alone.</param>
    /// <param name="innerRadiusMm">Inner edge of the ring.</param>
    /// <param name="outerRadiusMm">Outer edge of the ring.</param>
    /// <param name="font">Font; null uses the built-in Spleen 5x8.</param>
    public RosettaTitleRing(string title, DateOnly? date, double innerRadiusMm, double outerRadiusMm, Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!double.IsFinite(innerRadiusMm) || !double.IsFinite(outerRadiusMm) || innerRadiusMm <= 0 || outerRadiusMm <= innerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), outerRadiusMm,
                "The title ring's radii must be finite, positive, and the outer radius larger than the inner one.");
        }

        double heightMm = outerRadiusMm - innerRadiusMm;
        if (heightMm < MinimumHeightMm - 1e-9)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), heightMm, string.Create(CultureInfo.InvariantCulture,
                $"The title ring is {heightMm:0.###} mm high; it needs at least {MinimumHeightMm:0.###} mm for letters whose strokes are {MinimumGlyphPixelMm:0.###} mm wide."));
        }

        _font = font ?? Spleen5x8Font.BuiltIn;
        InnerRadiusMm = innerRadiusMm;
        OuterRadiusMm = outerRadiusMm;
        TextOuterRadiusMm = outerRadiusMm - EdgeGapMm;
        GlyphPixelMm = (heightMm - (2 * EdgeGapMm)) / Spleen5x8Font.CellPixelHeight;
        TextHeightMm = GlyphPixelMm * Spleen5x8Font.CellPixelHeight;
        TextMidRadiusMm = TextOuterRadiusMm - (0.5 * TextHeightMm);
        CellWidthMm = GlyphPixelMm * Spleen5x8Font.CellPixelWidth;

        string tail = date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        int columns = (int)Math.Floor(TwoPi * TextMidRadiusMm / CellWidthMm) - ClosingGapColumns;
        int titleColumns = columns - (tail.Length == 0 ? 0 : tail.Length + 2);
        string shownTitle = ToRingText(title, _font).Trim();
        if (titleColumns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), outerRadiusMm,
                "The ring at this radius is too short for the date; make the ring lower or move it outward.");
        }

        if (shownTitle.Length > titleColumns)
        {
            shownTitle = shownTitle[..titleColumns].TrimEnd();
        }

        Title = shownTitle;
        Date = date;
        Text = shownTitle.Length == 0 ? tail : tail.Length == 0 ? shownTitle : shownTitle + "  " + tail;
        if (Text.Length == 0)
        {
            throw new ArgumentException("The title ring has no text: give a title or a date.", nameof(title));
        }

        _spanRadians = Text.Length * CellWidthMm / TextMidRadiusMm;
        _startAngleRadians = (CentreAngleDegrees * (Math.PI / 180.0)) - (0.5 * _spanRadians);
    }

    /// <summary>Inner edge of the ring, in millimetres.</summary>
    public double InnerRadiusMm { get; }

    /// <summary>Outer edge of the ring, in millimetres.</summary>
    public double OuterRadiusMm { get; }

    /// <summary>Radius of the top of the text (the rim side), in millimetres.</summary>
    public double TextOuterRadiusMm { get; }

    /// <summary>Radius at which a font pixel is <see cref="GlyphPixelMm"/> wide.</summary>
    public double TextMidRadiusMm { get; }

    /// <summary>Height of the 8 font rows, in millimetres.</summary>
    public double TextHeightMm { get; }

    /// <summary>Side of one font pixel, in millimetres.</summary>
    public double GlyphPixelMm { get; }

    /// <summary>Height of a capital letter or digit (6 of the 8 font rows), in millimetres.</summary>
    public double CapitalHeightMm => GlyphPixelMm * 6;

    /// <summary>Width of one glyph cell at <see cref="TextMidRadiusMm"/>, in millimetres.</summary>
    public double CellWidthMm { get; }

    /// <summary>The title as shown: capitals, possibly cut short.</summary>
    public string Title { get; }

    public DateOnly? Date { get; }

    /// <summary>Everything the ring says: the title, two spaces and the date.</summary>
    public string Text { get; }

    /// <summary>Clockwise angle where the first glyph cell starts, in degrees (0 to 360).</summary>
    public double TextStartAngleDegrees => Wrap(_startAngleRadians) * (180.0 / Math.PI);

    /// <summary>Angle the text covers, in degrees.</summary>
    public double TextSpanDegrees => _spanRadians * (180.0 / Math.PI);

    /// <summary>True when the point is ink. Does not allocate and may be called from several threads at once.</summary>
    /// <param name="radiusMm">Distance from the disc centre, in millimetres.</param>
    /// <param name="clockwiseAngleRadians">Angle clockwise seen from the read side, in radians; any finite value.</param>
    public bool IsInk(double radiusMm, double clockwiseAngleRadians)
    {
        if (!double.IsFinite(radiusMm) || !double.IsFinite(clockwiseAngleRadians))
        {
            return false;
        }

        double yMm = TextOuterRadiusMm - radiusMm;
        if (yMm < 0.0 || yMm >= TextHeightMm)
        {
            return false;
        }

        double angle = Wrap(clockwiseAngleRadians - _startAngleRadians);
        if (angle >= _spanRadians)
        {
            return false;
        }

        double xMm = angle * TextMidRadiusMm;
        int column = Math.Min((int)(xMm / CellWidthMm), Text.Length - 1);
        double xInCellMm = xMm - (column * CellWidthMm);
        int px = Math.Min((int)(xInCellMm / GlyphPixelMm), Spleen5x8Font.CellPixelWidth - 1);
        int py = Math.Min((int)(yMm / GlyphPixelMm), Spleen5x8Font.CellPixelHeight - 1);
        return _font.IsPixelSet(Text[column], px, py);
    }

    /// <summary>
    /// Read-side grey sampler for the DVD pattern writer: millimetres from the disc centre, x right and y down; 0 is
    /// ink and 255 blank. The writer applies the disc's start angle itself.
    /// </summary>
    public Func<double, double, byte> CreateReadSideSampler()
        => (xMm, yDownMm) => IsInk(Math.Sqrt((xMm * xMm) + (yDownMm * yDownMm)), Math.Atan2(yDownMm, xMm))
            ? RosettaDvdEncoder.InkLuminance
            : RosettaDvdEncoder.PaperLuminance;

    private static double Wrap(double radians)
    {
        double wrapped = radians - (TwoPi * Math.Floor(radians / TwoPi));
        return wrapped is >= 0.0 and < TwoPi ? wrapped : 0.0;
    }

    /// <summary>Capitals only, so every letter is full height; characters the font cannot draw become '?'.</summary>
    private static string ToRingText(string text, Spleen5x8Font font)
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
}
