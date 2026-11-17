using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

public sealed record RosettaDocument(string Title, string Text, bool ForceUnifont = false);

public sealed record RosettaArchiveOptions(
    string ArchiveTitle,
    DateOnly ArchiveDate,
    RosettaPagePreset BodyPreset,
    RosettaPagePreset? FrontMatterPreset = null,
    double InnerRadiusMm = 38.0,
    double OuterRadiusMm = 58.0,
    bool HasInnerFileVault = false,
    bool IncludeRimHeader = false);

public readonly record struct RosettaDocumentRange(
    int DocumentIndex,
    string Title,
    int FirstPageNumber,
    int LastPageNumber);

public readonly record struct RosettaBand(
    int BandIndex,
    int RingOffset,
    int FirstPageIndex,
    int PageCount,
    RosettaPagePreset Preset,
    PolarPageLayout Layout,
    RosettaPagePixelSource PixelSource);

internal readonly record struct LineSlice(int CharStart, ushort CharLength, ushort SourceIndex);

internal readonly record struct PageEntry(int FirstLineIndex, ushort LineCount, ushort TitleSourceIndex, byte Flags);

/// <summary>
/// Paginated Rosetta optical text archive with Contents (R00-S00), Reader Instructions (R00-S01+),
/// and memory-compact line/page indices across one or two concentric bands.
/// </summary>
public sealed class RosettaBook
{
    internal const byte PageFlagForceUnifont = 1 << 0;
    internal const byte PageFlagIsContents = 1 << 1;
    internal const byte PageFlagIsReaderInstructions = 1 << 2;

    private readonly string[] _sources;
    private readonly string[] _titles;
    private readonly LineSlice[] _lines;
    private readonly PageEntry[] _pages;

    private RosettaBook(
        string archiveTitle,
        DateOnly archiveDate,
        string textSha256Hex,
        int totalPages,
        long totalPhysicalTiles,
        IReadOnlyList<RosettaDocumentRange> documentRanges,
        IReadOnlyList<RosettaBand> bands,
        RosettaRimHeader? rimHeader,
        string[] sources,
        string[] titles,
        LineSlice[] lines,
        PageEntry[] pages)
    {
        RimHeader = rimHeader;
        ArchiveTitle = archiveTitle;
        ArchiveDate = archiveDate;
        TextSha256Hex = textSha256Hex;
        TotalPages = totalPages;
        TotalPhysicalTiles = totalPhysicalTiles;
        DocumentRanges = documentRanges;
        Bands = bands;
        _sources = sources;
        _titles = titles;
        _lines = lines;
        _pages = pages;
    }

    public string ArchiveTitle { get; }
    public DateOnly ArchiveDate { get; }
    public string TextSha256Hex { get; }
    public string TextSha256Prefix => TextSha256Hex[..12];
    public int TotalPages { get; }
    public long TotalPhysicalTiles { get; }
    public IReadOnlyList<RosettaDocumentRange> DocumentRanges { get; }
    public IReadOnlyList<RosettaBand> Bands { get; }

    /// <summary>
    /// The naked-eye header in the outer <see cref="RosettaRimHeader.BandHeightMm"/> of the disc, or null when
    /// <see cref="RosettaArchiveOptions.IncludeRimHeader"/> was off. The pages end below it.
    /// </summary>
    public RosettaRimHeader? RimHeader { get; }
    public PolarPageLayout PrimaryLayout => Bands[0].Layout;
    public IPagePixelSource PrimaryPixelSource => Bands[0].PixelSource;

    internal ReadOnlySpan<string> Sources => _sources;
    internal ReadOnlySpan<string> Titles => _titles;
    internal ReadOnlySpan<LineSlice> Lines => _lines;
    internal ReadOnlySpan<PageEntry> Pages => _pages;

    /// <summary>
    /// Builds a paginated archive from the given documents and layout options.
    /// </summary>
    public static RosettaBook Build(
        IReadOnlyList<RosettaDocument> documents,
        RosettaArchiveOptions options,
        Spleen5x8Font? spleen = null,
        UnifontBitmapFont? unifont = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ArchiveTitle);
        ArgumentNullException.ThrowIfNull(options.BodyPreset);
        if (!double.IsFinite(options.InnerRadiusMm) || options.InnerRadiusMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.InnerRadiusMm, "InnerRadiusMm must be finite and positive.");
        }
        if (!double.IsFinite(options.OuterRadiusMm) || options.OuterRadiusMm <= options.InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm, "OuterRadiusMm must exceed InnerRadiusMm.");
        }

        spleen ??= Spleen5x8Font.BuiltIn;
        unifont ??= UnifontBitmapFont.BuiltIn;

        RosettaPagePreset frontPreset = options.FrontMatterPreset ?? options.BodyPreset;
        if (frontPreset.CellSize.AreaUm2 < options.BodyPreset.CellSize.AreaUm2)
        {
            throw new ArgumentException(
                "FrontMatterPreset cell area must be at least as large as BodyPreset cell area so Page 1 uses the largest cell size on the disc.",
                nameof(options));
        }

        bool singleBand = frontPreset.Equals(options.BodyPreset);
        string sha256Hex = ComputeTextSha256Hex(documents);
        string sha256Prefix = sha256Hex[..12];

        int sourceCount = 2 + documents.Count;
        if (sourceCount > ushort.MaxValue)
        {
            throw new ArgumentException("Too many documents for 16-bit source indexing.", nameof(documents));
        }

        var sources = new string[sourceCount];
        var titles = new string[sourceCount];
        titles[0] = "CONTENTS";
        titles[1] = "READ THIS FIRST";
        sources[1] = BuildReaderInstructionText(options.HasInnerFileVault);

        for (int i = 0; i < documents.Count; i++)
        {
            RosettaDocument doc = documents[i] ?? throw new ArgumentNullException(nameof(documents), $"Document {i} is null.");
            titles[2 + i] = NormalizeSingleLineTitle(doc.Title);
            sources[2 + i] = NormalizeForRender(doc.Text);
        }

        // 1. Paginate Reader Instructions (always ForceUnifont = true) at frontPreset.
        var readerLines = new List<LineSlice>();
        var readerPages = new List<PageEntry>();
        PaginateSource(
            sourceIndex: 1,
            sourceText: sources[1],
            titleSourceIndex: 1,
            preset: frontPreset,
            forceUnifont: true,
            extraFlags: PageFlagIsReaderInstructions,
            spleen,
            unifont,
            readerLines,
            readerPages);

        // 2. Paginate all user documents at BodyPreset.
        var docLines = new List<LineSlice>();
        var docPages = new List<PageEntry>();
        var docRelativeStart = new int[documents.Count];
        var docPageCounts = new int[documents.Count];

        for (int i = 0; i < documents.Count; i++)
        {
            docRelativeStart[i] = docPages.Count;
            PaginateSource(
                sourceIndex: (ushort)(2 + i),
                sourceText: sources[2 + i],
                titleSourceIndex: (ushort)(2 + i),
                preset: options.BodyPreset,
                forceUnifont: documents[i].ForceUnifont,
                extraFlags: 0,
                spleen,
                unifont,
                docLines,
                docPages);
            docPageCounts[i] = docPages.Count - docRelativeStart[i];
        }

        // 3. Iteratively resolve Contents page count C (almost always converges in 1 pass).
        var contentsLines = new List<LineSlice>();
        var contentsPages = new List<PageEntry>();
        var docRanges = new RosettaDocumentRange[documents.Count];
        string contentsText = string.Empty;
        int cCount = 1;
        for (int pass = 0; pass < 5; pass++)
        {
            int docStartPage1Based = 1 + cCount + readerPages.Count;
            for (int i = 0; i < documents.Count; i++)
            {
                int first = docStartPage1Based + docRelativeStart[i];
                int last = first + docPageCounts[i] - 1;
                docRanges[i] = new RosettaDocumentRange(i, titles[2 + i], first, last);
            }

            contentsText = BuildContentsText(options.ArchiveTitle, options.ArchiveDate, sha256Prefix, docRanges);
            contentsLines.Clear();
            contentsPages.Clear();
            PaginateSource(
                sourceIndex: 0,
                sourceText: contentsText,
                titleSourceIndex: 0,
                preset: frontPreset,
                forceUnifont: false,
                extraFlags: PageFlagIsContents,
                spleen,
                unifont,
                contentsLines,
                contentsPages);

            if (contentsPages.Count == cCount)
            {
                break;
            }
            cCount = contentsPages.Count;
        }
        sources[0] = contentsText;

        // 4. Assemble combined line/page arrays in physical tile order:
        //    [0] = Contents p1, [1..R] = Reader Instructions, [1+R..C+R-1] = Contents overflow, [C+R..] = Documents.
        int frontMatterCount = contentsPages.Count + readerPages.Count;
        int totalPages = frontMatterCount + docPages.Count;
        int totalLinesCount = contentsLines.Count + readerLines.Count + docLines.Count;

        var allLines = new LineSlice[totalLinesCount];
        var allPages = new PageEntry[totalPages];

        int lineCursor = 0;
        int pageCursor = 0;

        AppendPageSlice(contentsPages[0], contentsLines, allLines, allPages, ref lineCursor, ref pageCursor);
        for (int i = 0; i < readerPages.Count; i++)
        {
            AppendPageSlice(readerPages[i], readerLines, allLines, allPages, ref lineCursor, ref pageCursor);
        }
        for (int i = 1; i < contentsPages.Count; i++)
        {
            AppendPageSlice(contentsPages[i], contentsLines, allLines, allPages, ref lineCursor, ref pageCursor);
        }
        for (int i = 0; i < docPages.Count; i++)
        {
            AppendPageSlice(docPages[i], docLines, allLines, allPages, ref lineCursor, ref pageCursor);
        }

        // 5. Build PolarPageLayout bands and check physical capacity.
        // The rim header takes the outermost band; the pages end one gutter below it.
        RosettaRimHeader? rimHeader = null;
        double pagesOuterRadiusMm = options.OuterRadiusMm;
        if (options.IncludeRimHeader)
        {
            rimHeader = new RosettaRimHeader(
                new RosettaRimHeaderOptions(options.ArchiveTitle, options.ArchiveDate, sha256Prefix, options.OuterRadiusMm),
                spleen);
            pagesOuterRadiusMm = rimHeader.InnerRadiusMm - RosettaRimHeader.PageGutterMm;
            if (pagesOuterRadiusMm <= options.InnerRadiusMm)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm,
                    "The rim header leaves no room for pages between InnerRadiusMm and OuterRadiusMm.");
            }
        }

        double innerMeters = options.InnerRadiusMm * 1.0e-3;
        double outerMeters = pagesOuterRadiusMm * 1.0e-3;

        RosettaBand[] bands;
        long totalPhysicalTiles;

        if (singleBand)
        {
            PolarPageLayout layout = frontPreset.CreateLayout(innerMeters, outerMeters);
            if (totalPages > layout.TotalPages)
            {
                throw new InvalidOperationException(
                    $"Archive requires {totalPages} pages, which exceeds single-band disc capacity ({layout.TotalPages} tiles).");
            }

            totalPhysicalTiles = layout.TotalPages;
            bands = new RosettaBand[1];
            var pixelSource = new RosettaPagePixelSource(
                sources, titles, allLines, allPages,
                spleen, unifont, frontPreset, layout,
                ringOffset: 0, firstPageIndex: 0, pageCount: totalPages, totalPages: totalPages);
            bands[0] = new RosettaBand(0, 0, 0, totalPages, frontPreset, layout, pixelSource);
        }
        else
        {
            PolarPageLayout probeFront = frontPreset.CreateLayout(innerMeters, outerMeters);
            if (frontMatterCount > probeFront.TotalPages)
            {
                throw new InvalidOperationException(
                    $"Front matter requires {frontMatterCount} pages, which exceeds disc capacity ({probeFront.TotalPages} tiles).");
            }

            int k = 0;
            long frontCapacity = 0;
            while (k < probeFront.RingCount && frontCapacity < frontMatterCount)
            {
                frontCapacity += probeFront.SectorsInRing(k);
                k++;
            }

            double band0OuterMeters = innerMeters
                + (k * frontPreset.PageRadialHeightMeters)
                + ((k - 1) * frontPreset.RadialGutterMeters);
            double band1InnerMeters = band0OuterMeters + frontPreset.RadialGutterMeters;

            if (band1InnerMeters + options.BodyPreset.PageRadialHeightMeters > outerMeters + 1.0e-12)
            {
                throw new InvalidOperationException(
                    "Front-matter rings leave insufficient radial space for a body band.");
            }

            PolarPageLayout band0Layout = frontPreset.CreateLayout(innerMeters, band0OuterMeters);
            PolarPageLayout band1Layout = options.BodyPreset.CreateLayout(band1InnerMeters, outerMeters);

            if (docPages.Count > band1Layout.TotalPages)
            {
                throw new InvalidOperationException(
                    $"Body documents require {docPages.Count} pages, which exceeds body band capacity ({band1Layout.TotalPages} tiles).");
            }

            totalPhysicalTiles = band0Layout.TotalPages + band1Layout.TotalPages;
            var source0 = new RosettaPagePixelSource(
                sources, titles, allLines, allPages,
                spleen, unifont, frontPreset, band0Layout,
                ringOffset: 0, firstPageIndex: 0, pageCount: frontMatterCount, totalPages: totalPages);
            var source1 = new RosettaPagePixelSource(
                sources, titles, allLines, allPages,
                spleen, unifont, options.BodyPreset, band1Layout,
                ringOffset: k, firstPageIndex: frontMatterCount, pageCount: docPages.Count, totalPages: totalPages);

            bands =
            [
                new RosettaBand(0, 0, 0, frontMatterCount, frontPreset, band0Layout, source0),
                new RosettaBand(1, k, frontMatterCount, docPages.Count, options.BodyPreset, band1Layout, source1)
            ];
        }

        return new RosettaBook(
            options.ArchiveTitle,
            options.ArchiveDate,
            sha256Hex,
            totalPages,
            totalPhysicalTiles,
            docRanges,
            bands,
            rimHeader,
            sources,
            titles,
            allLines,
            allPages);
    }

    /// <summary>
    /// The tile a page is drawn on: its ring counted over all bands (outward from the centre) and its sector in that
    /// ring. Pages fill each band ring by ring, sector by sector, in page order.
    /// </summary>
    /// <param name="pageNumber">1-based page number, as printed in the page header.</param>
    public (int GlobalRing, int Sector) GetTile(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > TotalPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, $"Pages are numbered 1 to {TotalPages}.");
        }

        int pageIndex = pageNumber - 1;
        foreach (RosettaBand band in Bands)
        {
            if (pageIndex < band.FirstPageIndex || pageIndex >= band.FirstPageIndex + band.PageCount)
            {
                continue;
            }

            int local = pageIndex - band.FirstPageIndex;
            for (int ring = 0; ring < band.Layout.RingCount; ring++)
            {
                int sectors = band.Layout.SectorsInRing(ring);
                if (local < sectors)
                {
                    return (band.RingOffset + ring, local);
                }

                local -= sectors;
            }
        }

        throw new InvalidOperationException($"Page {pageNumber} is not placed on any tile.");
    }

    /// <summary>Computes the lowercase SHA-256 hex digest of the canonical UTF-8 document stream.</summary>
    public static string ComputeTextSha256Hex(IReadOnlyList<RosettaDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] byteBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        ReadOnlySpan<byte> newline = "\n"u8;

        try
        {
            for (int i = 0; i < documents.Count; i++)
            {
                RosettaDocument doc = documents[i] ?? throw new ArgumentNullException(nameof(documents));
                AppendUtf8(hash, doc.Title.AsSpan(), byteBuf);
                hash.AppendData(newline);
                AppendNormalizedLfUtf8(hash, doc.Text.AsSpan(), byteBuf);
                hash.AppendData(newline);
            }

            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuf);
        }
    }

    /// <summary>Returns the canonical page 1 reader instruction text.</summary>
    public static string BuildReaderInstructionText(bool hasInnerFileVault)
    {
        string finalParagraph = hasInnerFileVault
            ? "The same text is stored as files in the inner part of the disc, readable by a CD/DVD drive; the SHA-256 number on the rim checks that the files are complete."
            : "This disc holds optical text pages across the data band without a separate computer file system; the SHA-256 prefix on page R00-S00 identifies the complete text set.";

        return string.Join('\n',
            "THIS DISC HOLDS TEXT YOU CAN READ WITHOUT A COMPUTER.",
            "Look at the shiny side with a microscope or a strong magnifier, 20x to 200x.",
            "Light the disc from the direction you look from (a lamp beside the lens, or a piece of glass at 45 degrees in front of the lens with a lamp to the side). Tilt the disc until the letters stand out.",
            "Pages are numbered Rnn-Smm: ring nn counts outward from the centre, sector mm counts clockwise as you look at the shiny side. Page R00-S00 lists every document and its pages.",
            "Each page has a + mark in two corners. Centre the top-left + and read left to right, top to bottom.",
            finalParagraph);
    }

    /// <summary>
    /// Renders an exact ASCII grid of one populated page with '+' corner crosshairs, header,
    /// horizontal '---' rule, and wrapped body rows for snapshot verification.
    /// </summary>
    public string RenderPageTextSnapshot(int globalRing, int sector)
    {
        RosettaBand? matched = null;
        for (int i = 0; i < Bands.Count; i++)
        {
            RosettaBand b = Bands[i];
            if (globalRing >= b.RingOffset && globalRing < b.RingOffset + b.Layout.RingCount)
            {
                matched = b;
                break;
            }
        }

        if (matched is not { } band)
        {
            throw new ArgumentOutOfRangeException(nameof(globalRing), globalRing, "Global ring does not belong to any band.");
        }

        int ringInBand = globalRing - band.RingOffset;
        int sectors = band.Layout.SectorsInRing(ringInBand);
        if (sector < 0 || sector >= sectors)
        {
            throw new ArgumentOutOfRangeException(nameof(sector), sector, $"Ring {globalRing} has sectors 0..{sectors - 1}.");
        }

        int localPageIndex = band.PixelSource.GetLocalPageIndex(ringInBand, sector);
        if (localPageIndex < 0 || localPageIndex >= band.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sector), sector, $"Tile R{globalRing:D2}-S{sector:D2} is unpopulated.");
        }

        int cols = band.Preset.Columns;
        int bodyRows = band.Preset.BodyRows;
        int pageNumber1Based = band.FirstPageIndex + localPageIndex + 1;
        ref readonly PageEntry page = ref _pages[band.FirstPageIndex + localPageIndex];
        string title = _titles[page.TitleSourceIndex];

        var sb = new StringBuilder();
        char[] rowChars = new char[cols];

        // Header row 0: top arm of top-left '+' at cols 0..2, header starting at col 4.
        Array.Fill(rowChars, ' ');
        rowChars[1] = '+';
        string header = string.Create(
            CultureInfo.InvariantCulture,
            $"R{globalRing:D2}-S{sector:D2}  p {pageNumber1Based:D4}/{TotalPages:D4}  {title}");
        int hCol = 4;
        foreach (Rune rune in header.EnumerateRunes())
        {
            int w = band.PixelSource.GetGlyphCellWidth(rune.Value, (page.Flags & PageFlagForceUnifont) != 0);
            if (hCol + w > cols)
            {
                break;
            }
            if (rune.Utf16SequenceLength == 1)
            {
                rowChars[hCol] = (char)rune.Value;
            }
            else
            {
                rowChars[hCol] = '?';
            }
            hCol += w;
        }
        sb.Append(new string(rowChars).TrimEnd()).Append('\n');

        // Rule line: 4 spaces + dashes from col 4 to cols - 1.
        sb.Append(' ', 4).Append('-', cols - 4).Append('\n');

        // Body rows 0 .. bodyRows - 1.
        for (int bodyRow = 0; bodyRow < bodyRows; bodyRow++)
        {
            Array.Fill(rowChars, ' ');
            if (bodyRow == 0)
            {
                rowChars[0] = '+';
                rowChars[1] = '+';
                rowChars[2] = '+';
            }
            else if (bodyRow == 1)
            {
                rowChars[1] = '+';
            }

            if (bodyRow == bodyRows - 3 || bodyRow == bodyRows - 1)
            {
                rowChars[cols - 2] = '+';
            }
            else if (bodyRow == bodyRows - 2)
            {
                rowChars[cols - 3] = '+';
                rowChars[cols - 2] = '+';
                rowChars[cols - 1] = '+';
            }

            if (bodyRow < page.LineCount)
            {
                LineSlice slice = _lines[page.FirstLineIndex + bodyRow];
                ReadOnlySpan<char> sliceSpan = _sources[slice.SourceIndex].AsSpan(slice.CharStart, slice.CharLength);
                int col = bodyRow < 2 ? 4 : 0;
                int endCol = bodyRow >= bodyRows - 3 ? cols - 4 : cols;
                bool forceUnifont = (page.Flags & PageFlagForceUnifont) != 0;

                foreach (Rune rune in sliceSpan.EnumerateRunes())
                {
                    int w = band.PixelSource.GetGlyphCellWidth(rune.Value, forceUnifont);
                    if (col + w > endCol)
                    {
                        break;
                    }
                    if (rune.Utf16SequenceLength == 1)
                    {
                        rowChars[col] = (char)rune.Value;
                    }
                    else
                    {
                        rowChars[col] = '?';
                    }
                    col += w;
                }
            }

            sb.Append(new string(rowChars).TrimEnd());
            if (bodyRow < bodyRows - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string BuildContentsText(
        string archiveTitle,
        DateOnly archiveDate,
        string sha256Prefix,
        IReadOnlyList<RosettaDocumentRange> docRanges)
    {
        var sb = new StringBuilder();
        sb.Append(NormalizeSingleLineTitle(archiveTitle)).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"DATE {archiveDate:yyyy-MM-dd}").Append('\n');
        sb.Append("SHA256 ").Append(sha256Prefix).Append('\n');
        if (docRanges.Count > 0)
        {
            sb.Append('\n');
            for (int i = 0; i < docRanges.Count; i++)
            {
                RosettaDocumentRange r = docRanges[i];
                sb.Append(CultureInfo.InvariantCulture, $"{r.FirstPageNumber:D4}-{r.LastPageNumber:D4}  {r.Title}");
                if (i + 1 < docRanges.Count)
                {
                    sb.Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    private static void PaginateSource(
        ushort sourceIndex,
        string sourceText,
        ushort titleSourceIndex,
        RosettaPagePreset preset,
        bool forceUnifont,
        byte extraFlags,
        Spleen5x8Font spleen,
        UnifontBitmapFont unifont,
        List<LineSlice> linesOut,
        List<PageEntry> pagesOut)
    {
        byte flags = (byte)(extraFlags | (forceUnifont ? PageFlagForceUnifont : 0));
        int cols = preset.Columns;
        int bodyRows = preset.BodyRows;
        ReadOnlySpan<char> span = sourceText.AsSpan();
        int charIndex = 0;

        do
        {
            int firstLine = linesOut.Count;
            int rowCount = 0;

            for (int bodyRow = 0; bodyRow < bodyRows && (charIndex < span.Length || rowCount == 0); bodyRow++)
            {
                if (charIndex >= span.Length)
                {
                    break;
                }

                int startCol = bodyRow < 2 ? 4 : 0;
                int endColExclusive = bodyRow >= bodyRows - 3 ? cols - 4 : cols;
                int rowCapacity = endColExclusive - startCol;

                if (span[charIndex] == '\n')
                {
                    linesOut.Add(new LineSlice(charIndex, 0, sourceIndex));
                    charIndex++;
                    rowCount++;
                    continue;
                }

                int lineStart = charIndex;
                int scan = charIndex;
                int cellsUsed = 0;
                int lastBreakEnd = -1;
                int lastBreakResume = -1;
                bool sawNonSpace = false;

                while (scan < span.Length && span[scan] != '\n')
                {
                    if (Rune.DecodeFromUtf16(span[scan..], out Rune rune, out int consumed) != OperationStatus.Done)
                    {
                        rune = Rune.ReplacementChar;
                        consumed = 1;
                    }

                    int runeWidth = (forceUnifont || !spleen.HasGlyph(rune.Value))
                        ? unifont.GetCellWidth(rune.Value)
                        : 1;

                    if (cellsUsed + runeWidth > rowCapacity)
                    {
                        if (rune.Value == ' ')
                        {
                            // Space fell right at the wrap boundary: consume it and break cleanly.
                            linesOut.Add(new LineSlice(lineStart, checked((ushort)(scan - lineStart)), sourceIndex));
                            charIndex = scan + consumed;
                        }
                        else if (lastBreakEnd >= 0)
                        {
                            linesOut.Add(new LineSlice(lineStart, checked((ushort)(lastBreakEnd - lineStart)), sourceIndex));
                            charIndex = lastBreakResume;
                        }
                        else
                        {
                            // Hard-wrap long word (also leaves endColExclusive - 1 blank if 2-cell glyph didn't fit).
                            if (scan == lineStart)
                            {
                                scan += consumed;
                            }
                            linesOut.Add(new LineSlice(lineStart, checked((ushort)(scan - lineStart)), sourceIndex));
                            charIndex = scan;
                        }
                        goto LineEmitted;
                    }

                    if (rune.Value == ' ')
                    {
                        if (sawNonSpace)
                        {
                            lastBreakEnd = scan;
                            lastBreakResume = scan + consumed;
                        }
                    }
                    else
                    {
                        sawNonSpace = true;
                    }

                    cellsUsed += runeWidth;
                    scan += consumed;
                }

                // Reached '\n' or end of string without overflowing rowCapacity.
                linesOut.Add(new LineSlice(lineStart, checked((ushort)(scan - lineStart)), sourceIndex));
                charIndex = (scan < span.Length && span[scan] == '\n') ? scan + 1 : scan;

            LineEmitted:
                rowCount++;
            }

            pagesOut.Add(new PageEntry(firstLine, checked((ushort)rowCount), titleSourceIndex, flags));
        }
        while (charIndex < span.Length);
    }

    private static void AppendPageSlice(
        in PageEntry srcPage,
        List<LineSlice> srcLines,
        LineSlice[] dstLines,
        PageEntry[] dstPages,
        ref int lineCursor,
        ref int pageCursor)
    {
        int dstFirstLine = lineCursor;
        for (int i = 0; i < srcPage.LineCount; i++)
        {
            dstLines[lineCursor++] = srcLines[srcPage.FirstLineIndex + i];
        }
        dstPages[pageCursor++] = new PageEntry(dstFirstLine, srcPage.LineCount, srcPage.TitleSourceIndex, srcPage.Flags);
    }

    private static string NormalizeSingleLineTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }
        if (!title.AsSpan().ContainsAny('\r', '\n', '\t'))
        {
            return title;
        }
        return title.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
    }

    private static string NormalizeForRender(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string lf = text.Contains('\r')
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;
        return lf.Contains('\t') ? lf.Replace("\t", "  ", StringComparison.Ordinal) : lf;
    }

    private static void AppendUtf8(IncrementalHash hash, ReadOnlySpan<char> chars, byte[] scratch)
    {
        int maxCharsPerChunk = scratch.Length / 4;
        while (!chars.IsEmpty)
        {
            int take = Math.Min(chars.Length, maxCharsPerChunk);
            // Avoid splitting a surrogate pair at chunk boundary.
            if (take < chars.Length && char.IsHighSurrogate(chars[take - 1]))
            {
                take--;
            }
            if (take == 0)
            {
                take = Math.Min(chars.Length, 2);
            }
            int bytes = System.Text.Encoding.UTF8.GetBytes(chars[..take], scratch);
            hash.AppendData(scratch.AsSpan(0, bytes));
            chars = chars[take..];
        }
    }

    private static void AppendNormalizedLfUtf8(IncrementalHash hash, ReadOnlySpan<char> chars, byte[] scratch)
    {
        ReadOnlySpan<byte> lf = "\n"u8;
        int i = 0;
        while (i < chars.Length)
        {
            int cr = chars[i..].IndexOf('\r');
            if (cr < 0)
            {
                AppendUtf8(hash, chars[i..], scratch);
                break;
            }

            if (cr > 0)
            {
                AppendUtf8(hash, chars.Slice(i, cr), scratch);
            }
            hash.AppendData(lf);
            i += cr + 1;
            if (i < chars.Length && chars[i] == '\n')
            {
                i++;
            }
        }
    }
}
