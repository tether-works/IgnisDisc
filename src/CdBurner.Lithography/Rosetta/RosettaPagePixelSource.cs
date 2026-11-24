using System.Buffers;
using System.Globalization;
using System.Text;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

/// <summary>
/// Allocation-free, thread-safe <see cref="IPagePixelSource"/> for a band of a <see cref="RosettaBook"/>.
/// </summary>
public sealed class RosettaPagePixelSource : IPagePixelSource
{
    private readonly string[] _sources;
    private readonly string[] _titles;
    private readonly LineSlice[] _lines;
    private readonly PageEntry[] _pages;
    private readonly Spleen5x8Font _spleen;
    private readonly UnifontBitmapFont _unifont;
    private readonly int _ringOffset;
    private readonly int _firstPageIndex;
    private readonly int _pageCount;
    private readonly int _totalPages;
    private readonly int[] _ringFirstPageIndex;

    private readonly double _cellW;
    private readonly double _cellH;
    private readonly double _ruleBandH;
    private readonly double _pageW;
    private readonly double _pageH;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _bodyRows;

    internal RosettaPagePixelSource(
        string[] sources,
        string[] titles,
        LineSlice[] lines,
        PageEntry[] pages,
        Spleen5x8Font spleen,
        UnifontBitmapFont unifont,
        RosettaPagePreset preset,
        PolarPageLayout layout,
        int ringOffset,
        int firstPageIndex,
        int pageCount,
        int totalPages)
    {
        _sources = sources;
        _titles = titles;
        _lines = lines;
        _pages = pages;
        _spleen = spleen;
        _unifont = unifont;
        _ringOffset = ringOffset;
        _firstPageIndex = firstPageIndex;
        _pageCount = pageCount;
        _totalPages = totalPages;

        _cellW = preset.CellSize.WidthMeters;
        _cellH = preset.CellSize.HeightMeters;
        _ruleBandH = preset.RuleBandHeightMeters;
        _pageW = preset.PageTangentialWidthMeters;
        _pageH = preset.PageRadialHeightMeters;
        _columns = preset.Columns;
        _rows = preset.Rows;
        _bodyRows = preset.BodyRows;

        _ringFirstPageIndex = new int[layout.RingCount];
        int running = 0;
        for (int r = 0; r < layout.RingCount; r++)
        {
            _ringFirstPageIndex[r] = running;
            running += layout.SectorsInRing(r);
        }
    }

    internal int GetLocalPageIndex(int ring, int sector)
    {
        if ((uint)ring >= (uint)_ringFirstPageIndex.Length || sector < 0)
        {
            return -1;
        }
        int nextRingStart = ring + 1 < _ringFirstPageIndex.Length ? _ringFirstPageIndex[ring + 1] : int.MaxValue;
        int local = _ringFirstPageIndex[ring] + sector;
        return local < nextRingStart ? local : -1;
    }

    internal int GetGlyphCellWidth(int codePoint, bool forceUnifont)
        => (forceUnifont || !_spleen.HasGlyph(codePoint)) ? _unifont.GetCellWidth(codePoint) : 1;

    /// <inheritdoc />
    public bool IsInk(int ring, int sector, double uMeters, double vMeters)
    {
        int localPageIndex = GetLocalPageIndex(ring, sector);
        if ((uint)localPageIndex >= (uint)_pageCount)
        {
            return false;
        }

        if (!double.IsFinite(uMeters) || !double.IsFinite(vMeters)
            || uMeters < 0.0 || uMeters > _pageW
            || vMeters < 0.0 || vMeters > _pageH)
        {
            return false;
        }

        double xMeters = Math.Clamp(uMeters, 0.0, Math.BitDecrement(_pageW));
        double yMeters = Math.Clamp(_pageH - vMeters, 0.0, Math.BitDecrement(_pageH));

        // 1. Corner '+' crosshairs (3x3 cells wide).
        if (xMeters < 3.0 * _cellW && yMeters < 3.0 * _cellH)
        {
            int cx = (int)(xMeters / _cellW);
            int cy = (int)(yMeters / _cellH);
            return cx == 1 || cy == 1;
        }

        if (xMeters >= _pageW - (3.0 * _cellW) && yMeters >= _pageH - (3.0 * _cellH))
        {
            int cx = Math.Clamp((int)((xMeters - (_pageW - (3.0 * _cellW))) / _cellW), 0, 2);
            int cy = Math.Clamp((int)((yMeters - (_pageH - (3.0 * _cellH))) / _cellH), 0, 2);
            return cx == 1 || cy == 1;
        }

        int col = Math.Clamp((int)(xMeters / _cellW), 0, _columns - 1);
        double xInCell = xMeters - (col * _cellW);

        // 2. Header row (yMeters < cellH).
        if (yMeters < _cellH)
        {
            if (col < 4)
            {
                return false;
            }
            if (!TryResolveHeaderCell(ring, sector, localPageIndex, col, out int codePoint, out bool useUnifont, out int wideHalf)
                || codePoint == ' ')
            {
                return false;
            }
            return SampleGlyphPixel(codePoint, useUnifont, wideHalf, xInCell, yMeters);
        }

        // 3. 1-pixel horizontal rule (cellH <= yMeters < cellH + ruleBandH).
        if (yMeters < _cellH + _ruleBandH)
        {
            if (xMeters < 4.0 * _cellW)
            {
                return false;
            }
            ref readonly PageEntry page = ref _pages[_firstPageIndex + localPageIndex];
            bool pageForcesUnifont = (page.Flags & RosettaBook.PageFlagForceUnifont) != 0;
            double onePxH = pageForcesUnifont ? (_cellH / 16.0) : (_cellH / 8.0);
            double yRule0 = _cellH + (0.5 * (_ruleBandH - onePxH));
            return yMeters >= yRule0 && yMeters < yRule0 + onePxH;
        }

        // 4. Body rows (yMeters >= cellH + ruleBandH).
        double yBody = yMeters - (_cellH + _ruleBandH);
        int bodyRow = Math.Clamp((int)(yBody / _cellH), 0, _bodyRows - 1);
        double yInCell = yBody - (bodyRow * _cellH);

        if ((bodyRow < 2 && col < 4) || (bodyRow >= _bodyRows - 3 && col >= _columns - 4))
        {
            return false;
        }

        if (!TryResolveBodyCell(localPageIndex, bodyRow, col, out int bodyCodePoint, out bool bodyUseUnifont, out int bodyWideHalf)
            || bodyCodePoint == ' ')
        {
            return false;
        }

        return SampleGlyphPixel(bodyCodePoint, bodyUseUnifont, bodyWideHalf, xInCell, yInCell);
    }

    /// <summary>
    /// Resolves the Unicode code point and font assignment at cell (<paramref name="cellRow"/>, <paramref name="cellCol"/>),
    /// where <paramref name="cellRow"/> = 0 is the header row and 1..Rows-1 are body rows.
    /// </summary>
    public bool TryResolveGlyphCell(
        int ring,
        int sector,
        int cellRow,
        int cellCol,
        out int codePoint,
        out bool useUnifont,
        out int wideHalf)
    {
        codePoint = 0;
        useUnifont = false;
        wideHalf = 0;

        int localPageIndex = GetLocalPageIndex(ring, sector);
        if ((uint)localPageIndex >= (uint)_pageCount
            || (uint)cellRow >= (uint)_rows
            || (uint)cellCol >= (uint)_columns)
        {
            return false;
        }

        if (cellRow == 0)
        {
            return cellCol >= 4
                && TryResolveHeaderCell(ring, sector, localPageIndex, cellCol, out codePoint, out useUnifont, out wideHalf);
        }

        int bodyRow = cellRow - 1;
        if ((bodyRow < 2 && cellCol < 4) || (bodyRow >= _bodyRows - 3 && cellCol >= _columns - 4))
        {
            return false;
        }

        return TryResolveBodyCell(localPageIndex, bodyRow, cellCol, out codePoint, out useUnifont, out wideHalf);
    }

    private bool SampleGlyphPixel(int codePoint, bool useUnifont, int wideHalf, double xInCell, double yInCell)
    {
        if (!useUnifont)
        {
            int px = Math.Clamp((int)((xInCell / _cellW) * Spleen5x8Font.CellPixelWidth), 0, Spleen5x8Font.CellPixelWidth - 1);
            int py = Math.Clamp((int)((yInCell / _cellH) * Spleen5x8Font.CellPixelHeight), 0, Spleen5x8Font.CellPixelHeight - 1);
            return _spleen.IsPixelSet(codePoint, px, py);
        }

        int uy = Math.Clamp((int)((yInCell / _cellH) * UnifontBitmapFont.CellPixelHeight), 0, UnifontBitmapFont.CellPixelHeight - 1);
        int ux = (wideHalf * UnifontBitmapFont.NarrowPixelWidth)
            + Math.Clamp((int)((xInCell / _cellW) * UnifontBitmapFont.NarrowPixelWidth), 0, UnifontBitmapFont.NarrowPixelWidth - 1);
        return _unifont.IsPixelSet(codePoint, ux, uy);
    }

    private bool TryResolveHeaderCell(
        int ring,
        int sector,
        int localPageIndex,
        int targetCol,
        out int codePoint,
        out bool useUnifont,
        out int wideHalf)
    {
        codePoint = 0;
        useUnifont = false;
        wideHalf = 0;

        ref readonly PageEntry page = ref _pages[_firstPageIndex + localPageIndex];
        bool pageForcesUnifont = (page.Flags & RosettaBook.PageFlagForceUnifont) != 0;

        Span<char> prefix = stackalloc char[64];
        int pos = 0;
        int globalRing = _ringOffset + ring;
        int pageNumber1Based = _firstPageIndex + localPageIndex + 1;

        prefix[pos++] = 'R';
        if (!globalRing.TryFormat(prefix[pos..], out int written, "D2", CultureInfo.InvariantCulture)) return false;
        pos += written;
        prefix[pos++] = '-';
        prefix[pos++] = 'S';
        if (!sector.TryFormat(prefix[pos..], out written, "D2", CultureInfo.InvariantCulture)) return false;
        pos += written;
        prefix[pos++] = ' ';
        prefix[pos++] = ' ';
        prefix[pos++] = 'p';
        prefix[pos++] = ' ';
        if (!pageNumber1Based.TryFormat(prefix[pos..], out written, "D4", CultureInfo.InvariantCulture)) return false;
        pos += written;
        prefix[pos++] = '/';
        if (!_totalPages.TryFormat(prefix[pos..], out written, "D4", CultureInfo.InvariantCulture)) return false;
        pos += written;
        prefix[pos++] = ' ';
        prefix[pos++] = ' ';

        ReadOnlySpan<char> title = _titles[page.TitleSourceIndex].AsSpan();
        int maxTitleChars = Math.Min(title.Length, 2 * Math.Max(0, _columns - 4));
        if (maxTitleChars > 0 && maxTitleChars < title.Length
            && char.IsSurrogatePair(title[maxTitleChars - 1], title[maxTitleChars]))
        {
            maxTitleChars++;
        }

        int totalLen = pos + maxTitleChars;
        if (totalLen <= 256)
        {
            Span<char> header = stackalloc char[256];
            prefix[..pos].CopyTo(header);
            title[..maxTitleChars].CopyTo(header[pos..]);
            return TryScanLineForColumn(
                header[..totalLen],
                startCol: 4,
                endColExclusive: _columns,
                targetCol,
                pageForcesUnifont,
                out codePoint,
                out useUnifont,
                out wideHalf);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(totalLen);
        try
        {
            prefix[..pos].CopyTo(rented);
            title[..maxTitleChars].CopyTo(rented.AsSpan(pos));
            return TryScanLineForColumn(
                rented.AsSpan(0, totalLen),
                startCol: 4,
                endColExclusive: _columns,
                targetCol,
                pageForcesUnifont,
                out codePoint,
                out useUnifont,
                out wideHalf);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool TryResolveBodyCell(
        int localPageIndex,
        int bodyRow,
        int targetCol,
        out int codePoint,
        out bool useUnifont,
        out int wideHalf)
    {
        codePoint = 0;
        useUnifont = false;
        wideHalf = 0;

        ref readonly PageEntry page = ref _pages[_firstPageIndex + localPageIndex];
        if (bodyRow >= page.LineCount)
        {
            return false;
        }

        LineSlice slice = _lines[page.FirstLineIndex + bodyRow];
        ReadOnlySpan<char> text = _sources[slice.SourceIndex].AsSpan(slice.CharStart, slice.CharLength);
        bool pageForcesUnifont = (page.Flags & RosettaBook.PageFlagForceUnifont) != 0;
        int startCol = bodyRow < 2 ? 4 : 0;
        int endColExclusive = bodyRow >= _bodyRows - 3 ? _columns - 4 : _columns;

        return TryScanLineForColumn(
            text,
            startCol,
            endColExclusive,
            targetCol,
            pageForcesUnifont,
            out codePoint,
            out useUnifont,
            out wideHalf);
    }

    private bool TryScanLineForColumn(
        ReadOnlySpan<char> text,
        int startCol,
        int endColExclusive,
        int targetCol,
        bool pageForcesUnifont,
        out int codePoint,
        out bool useUnifont,
        out int wideHalf)
    {
        codePoint = 0;
        useUnifont = false;
        wideHalf = 0;

        int curCol = startCol;
        int idx = 0;
        while (idx < text.Length && curCol < endColExclusive)
        {
            if (Rune.DecodeFromUtf16(text[idx..], out Rune rune, out int consumed) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            bool runeUnifont = pageForcesUnifont || !_spleen.HasGlyph(rune.Value);
            int span = runeUnifont ? _unifont.GetCellWidth(rune.Value) : 1;
            if (curCol + span > endColExclusive)
            {
                break;
            }

            if (targetCol >= curCol && targetCol < curCol + span)
            {
                codePoint = rune.Value;
                useUnifont = runeUnifont;
                wideHalf = targetCol - curCol;
                return true;
            }

            curCol += span;
            idx += consumed;
        }

        return false;
    }
}
