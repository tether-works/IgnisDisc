using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

public class RosettaPageFormattingSnapshotTests
{
    [Fact]
    public void Build_GeneratesContentsReaderInstructionsCrosshairsAndRuleSnapshot()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        RosettaDocument[] docs =
        [
            new("LATIN", "Hello Rosetta!\nLine two."),
            new("GREEK_CJK", "Greek Ω Cyrillic Ж CJK 永")
        ];

        var options = new RosettaArchiveOptions(
            ArchiveTitle: "SAMPLE ARCHIVE",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: catalog.Hobbyist,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0);

        RosettaBook book = RosettaBook.Build(docs, options);

        // Page 1 (R00-S00): Contents; Pages 2..4 (R00-S01..S03): Reader Instructions; Docs start at 0005.
        Assert.Equal(6, book.TotalPages);
        Assert.Equal(2, book.DocumentRanges.Count);
        Assert.Equal(new RosettaDocumentRange(0, "LATIN", 5, 5), book.DocumentRanges[0]);
        Assert.Equal(new RosettaDocumentRange(1, "GREEK_CJK", 6, 6), book.DocumentRanges[1]);

        string expectedShaPrefix = RosettaBook.ComputeTextSha256Hex(docs)[..12];
        Assert.Equal(expectedShaPrefix, book.TextSha256Prefix);

        string expectedContents = string.Join('\n',
            " +  R00-S00  p 0001/0006  CONT",
            "    --------------------------",
            "+++ SAMPLE ARCHIVE",
            " +  DATE 2026-11-15",
            $"SHA256 {expectedShaPrefix}",
            "",
            "0005-0005  LATIN",
            "0006-0006  GREEK_CJK",
            "",
            "",
            "",
            "                            +",
            "                           +++",
            "                            +");

        string actualContents = book.RenderPageTextSnapshot(0, 0);
        // Normalize trailing blank lines that have no bottom-right '+' (lines 8..10 in 12-row body).
        string[] actualLines = actualContents.Split('\n');
        Assert.Equal(14, actualLines.Length);
        Assert.Equal(" +  R00-S00  p 0001/0006  CONT", actualLines[0]);
        Assert.Equal("    --------------------------", actualLines[1]);
        Assert.Equal("+++ SAMPLE ARCHIVE", actualLines[2]);
        Assert.Equal(" +  DATE 2026-11-15", actualLines[3]);
        Assert.Equal($"SHA256 {expectedShaPrefix}", actualLines[4]);
        Assert.Equal("", actualLines[5]);
        Assert.Equal("0005-0005  LATIN", actualLines[6]);
        Assert.Equal("0006-0006  GREEK_CJK", actualLines[7]);
        Assert.Equal("", actualLines[8]);
        Assert.Equal("                            +", actualLines[11]);
        Assert.Equal("                           +++", actualLines[12]);
        Assert.Equal("                            +", actualLines[13]);
        Assert.Equal(expectedContents, actualContents);

        // Verify R00-S01 (Reader Instructions) uses Unifont for both header and body.
        RosettaPagePixelSource source = book.Bands[0].PixelSource;
        Assert.True(source.TryResolveGlyphCell(0, 1, cellRow: 0, cellCol: 4, out int hCp, out bool hUnifont, out _));
        Assert.Equal('R', hCp);
        Assert.True(hUnifont);
        Assert.True(source.TryResolveGlyphCell(0, 1, cellRow: 1, cellCol: 4, out int bCp, out bool bUnifont, out _));
        Assert.Equal('T', bCp);
        Assert.True(bUnifont);

        string readerSnapshot = book.RenderPageTextSnapshot(0, 1);
        Assert.StartsWith(" +  R00-S01  p 0002/0006  READ", readerSnapshot, StringComparison.Ordinal);
        Assert.Contains("THIS DISC HOLDS TEXT YOU", readerSnapshot, StringComparison.Ordinal);

        // Verify 3x3 '+' corner crosshairs (5 inked cells, 4 blank corners) and 1-pixel horizontal rule via IsInk.
        RosettaPagePreset preset = catalog.Hobbyist;
        double cellW = preset.CellSize.WidthMeters;
        double cellH = preset.CellSize.HeightMeters;
        double pageH = preset.PageRadialHeightMeters;
        double pageW = preset.PageTangentialWidthMeters;

        int tlInked = 0;
        for (int cy = 0; cy < 3; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                double u = (cx + 0.5) * cellW;
                double v = pageH - ((cy + 0.5) * cellH);
                bool inked = source.IsInk(0, 0, u, v);
                bool expected = cx == 1 || cy == 1;
                Assert.Equal(expected, inked);
                if (inked) tlInked++;
            }
        }
        Assert.Equal(5, tlInked);

        int brInked = 0;
        for (int cy = 0; cy < 3; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                double u = (pageW - 3 * cellW) + ((cx + 0.5) * cellW);
                double v = pageH - ((pageH - 3 * cellH) + ((cy + 0.5) * cellH));
                bool inked = source.IsInk(0, 0, u, v);
                bool expected = cx == 1 || cy == 1;
                Assert.Equal(expected, inked);
                if (inked) brInked++;
            }
        }
        Assert.Equal(5, brInked);

        // 1-pixel rule sits at y = cellH + 0.5 * (ruleBandH - onePxH) for col >= 4, and blank for col < 4.
        double ruleY = cellH + (0.5 * preset.RuleBandHeightMeters);
        Assert.False(source.IsInk(0, 0, 3.5 * cellW, pageH - ruleY));
        Assert.True(source.IsInk(0, 0, 4.5 * cellW, pageH - ruleY));
    }

    [Fact]
    public void Build_MixedBands_PlacesFrontMatterAtHobbyistAndBodyAtHighDensity()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        RosettaDocument[] docs = [new("DOC_A", "Sample high-density body text.")];

        var options = new RosettaArchiveOptions(
            ArchiveTitle: "MIXED",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: catalog.HighDensity,
            FrontMatterPreset: catalog.Hobbyist,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 30.0);

        RosettaBook book = RosettaBook.Build(docs, options);

        Assert.Equal(2, book.Bands.Count);
        Assert.Equal(0, book.Bands[0].RingOffset);
        Assert.Equal(catalog.Hobbyist, book.Bands[0].Preset);
        Assert.Equal(4, book.Bands[0].PageCount);

        Assert.Equal(1, book.Bands[1].RingOffset);
        Assert.Equal(catalog.HighDensity, book.Bands[1].Preset);
        Assert.Equal(1, book.Bands[1].PageCount);
        Assert.Equal(5, book.TotalPages);

        // R01-S00 is page 0005/0005 in the high-density body band (26 columns wraps lines >26 chars).
        string bodySnapshot = book.RenderPageTextSnapshot(1, 0);
        Assert.StartsWith(" +  R01-S00  p 0005/0005  DOC_", bodySnapshot, StringComparison.Ordinal);
        Assert.Contains("Sample high-density body", bodySnapshot, StringComparison.Ordinal);
        Assert.Contains("text.", bodySnapshot, StringComparison.Ordinal);

        // FrontMatterPreset smaller than BodyPreset must be rejected.
        var invalidOptions = options with
        {
            BodyPreset = catalog.Hobbyist,
            FrontMatterPreset = catalog.HighDensity
        };
        Assert.Throws<ArgumentException>(() => RosettaBook.Build(docs, invalidOptions));
    }

    [Fact]
    public void Build_ContentsOverflow_KeepsR00S00ContentsAndR00S01ReaderInstructions()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        var docs = Enumerable.Range(1, 10)
            .Select(i => new RosettaDocument($"DOC_{i:D2}", $"Document {i} body"))
            .ToArray();

        RosettaBook book = RosettaBook.Build(docs, new RosettaArchiveOptions(
            ArchiveTitle: "OVERFLOW",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: catalog.Hobbyist,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 58.0));

        // 2 Contents pages + 3 Reader Instruction pages + 10 doc pages = 15 pages.
        Assert.Equal(15, book.TotalPages);
        Assert.StartsWith(" +  R00-S00  p 0001/0015  CONT", book.RenderPageTextSnapshot(0, 0), StringComparison.Ordinal);
        Assert.StartsWith(" +  R00-S01  p 0002/0015  READ", book.RenderPageTextSnapshot(0, 1), StringComparison.Ordinal);
        Assert.StartsWith(" +  R00-S04  p 0005/0015  CONT", book.RenderPageTextSnapshot(0, 4), StringComparison.Ordinal);
        Assert.StartsWith(" +  R00-S05  p 0006/0015  DOC_", book.RenderPageTextSnapshot(0, 5), StringComparison.Ordinal);
        Assert.Equal(6, book.DocumentRanges[0].FirstPageNumber);
    }
}
