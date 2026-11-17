using System.Text;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>
/// <see cref="RosettaBook.GetTile"/> names the tile each page is drawn on (used by the vault's page index). Checked
/// against what the page itself prints: the header of the page at that tile starts with its own Rnn-Smm and page number.
/// </summary>
public class RosettaBookTileTests
{
    [Theory]
    [InlineData(false)] // one band
    [InlineData(true)]  // front matter and body at different cell sizes
    public void EveryPage_HeaderAtItsTile_ShowsThatPage(bool twoBands)
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        var text = new StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            text.Append("Line ").Append(i).Append(" of a text long enough to need several rings of pages.\n");
        }

        RosettaBook book = RosettaBook.Build(
            [new RosettaDocument("LONG", text.ToString())],
            new RosettaArchiveOptions(
                ArchiveTitle: "TILES",
                ArchiveDate: new DateOnly(2026, 11, 17),
                BodyPreset: twoBands ? catalog.HighDensity : catalog.Hobbyist,
                FrontMatterPreset: twoBands ? catalog.Hobbyist : null,
                InnerRadiusMm: 38.0,
                OuterRadiusMm: 58.0));
        Assert.Equal(twoBands ? 2 : 1, book.Bands.Count);
        Assert.True(book.TotalPages > book.Bands[0].Layout.SectorsInRing(0), "the text should reach a second ring");

        var seen = new HashSet<(int, int)>();
        for (int page = 1; page <= book.TotalPages; page++)
        {
            (int ring, int sector) = book.GetTile(page);
            Assert.True(seen.Add((ring, sector)), $"page {page} shares tile R{ring:D2}-S{sector:D2}");
            string header = book.RenderPageTextSnapshot(ring, sector).Split('\n')[0];
            Assert.StartsWith($" +  R{ring:D2}-S{sector:D2}  p {page:D4}/{book.TotalPages:D4}", header, StringComparison.Ordinal);
        }

        Assert.Equal((0, 0), book.GetTile(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.GetTile(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.GetTile(book.TotalPages + 1));
    }
}
