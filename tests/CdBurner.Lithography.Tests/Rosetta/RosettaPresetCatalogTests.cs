using System.Text;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

public class RosettaPresetCatalogTests
{
    [Fact]
    public void BuiltIn_Presets_MatchExpectedMicroscopeFieldDimensions()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;

        RosettaPagePreset hobbyist = catalog.Hobbyist;
        Assert.Equal("hobbyist", hobbyist.Id);
        Assert.Equal("Hobbyist", hobbyist.Name);
        Assert.Equal("DVD-R", hobbyist.TargetMedia);
        Assert.Equal(100.0, hobbyist.CellSize.WidthUm);
        Assert.Equal(150.0, hobbyist.CellSize.HeightUm);
        Assert.Equal(30, hobbyist.Columns);
        Assert.Equal(13, hobbyist.Rows);
        Assert.Equal(3.000, hobbyist.PageTangentialWidthMeters * 1e3, 6);
        Assert.Equal(2.00625, hobbyist.PageRadialHeightMeters * 1e3, 6);

        RosettaPagePreset highDensity = catalog.HighDensity;
        Assert.Equal("high-density", highDensity.Id);
        Assert.Equal("High-density", highDensity.Name);
        Assert.Equal("DVD-R", highDensity.TargetMedia);
        Assert.Equal(26.0, highDensity.CellSize.WidthUm);
        Assert.Equal(34.0, highDensity.CellSize.HeightUm);
        Assert.Equal(30, highDensity.Columns);
        Assert.Equal(17, highDensity.Rows);
        Assert.Equal(0.780, highDensity.PageTangentialWidthMeters * 1e3, 6);
        Assert.Equal(0.59075, highDensity.PageRadialHeightMeters * 1e3, 6);

        Assert.Same(hobbyist, catalog.GetPreset("Hobbyist"));
        Assert.Same(highDensity, catalog.GetPreset("high-density"));
    }

    [Fact]
    public void BuiltIn_Ladder_HasAllEightRungsAndCdCompatibilityBoundary()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        Assert.Equal(8, catalog.Ladder.Count);

        (double w, double h, bool cd)[] expected =
        [
            (250.0, 375.0, true),
            (150.0, 225.0, true),
            (100.0, 150.0, true),
            (75.0,  113.0, true),
            (50.0,  75.0,  true),
            (34.0,  51.0,  false),
            (26.0,  34.0,  false),
            (13.0,  17.0,  false),
        ];

        for (int i = 0; i < expected.Length; i++)
        {
            RosettaPagePreset rung = catalog.Ladder[i];
            Assert.Equal(expected[i].w, rung.CellSize.WidthUm);
            Assert.Equal(expected[i].h, rung.CellSize.HeightUm);
            Assert.Equal(expected[i].cd, rung.CdSupported);
            Assert.Equal(expected[i].w >= 50.0 && expected[i].h >= 75.0, rung.CellSize.IsCdCompatible);
        }
    }

    [Fact]
    public void Load_CustomJson_UsesDataNotConstants_AndRejectsInvalidGrid()
    {
        const string customJson = """
        {
          "schemaVersion": 1,
          "presets": [
            { "id": "hobbyist", "name": "Hobbyist", "targetMedia": "DVD-R", "cellWidthUm": 120, "cellHeightUm": 180, "columns": 24, "rows": 12, "radialGutterUm": 150, "tangentialGutterUm": 150 },
            { "id": "high-density", "name": "High-density", "targetMedia": "DVD-R", "cellWidthUm": 30, "cellHeightUm": 40, "columns": 24, "rows": 14, "radialGutterUm": 50, "tangentialGutterUm": 50 }
          ],
          "ladder": [
            { "cellWidthUm": 120, "cellHeightUm": 180, "columns": 24, "rows": 12, "radialGutterUm": 150, "tangentialGutterUm": 150, "cdSupported": true }
          ]
        }
        """;

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(customJson));
        RosettaPresetCatalog custom = RosettaPresetCatalog.Load(ms);
        Assert.Equal(120.0, custom.Hobbyist.CellSize.WidthUm);
        Assert.Equal(24, custom.Hobbyist.Columns);
        Assert.Single(custom.Ladder);

        string tooFewCols = customJson.Replace("\"columns\": 24", "\"columns\": 9");
        using var badCols = new MemoryStream(Encoding.UTF8.GetBytes(tooFewCols));
        Assert.Throws<InvalidDataException>(() => RosettaPresetCatalog.Load(badCols));

        string tooFewRows = customJson.Replace("\"rows\": 12", "\"rows\": 7");
        using var badRows = new MemoryStream(Encoding.UTF8.GetBytes(tooFewRows));
        Assert.Throws<InvalidDataException>(() => RosettaPresetCatalog.Load(badRows));

        string tooManyCols = customJson.Replace("\"columns\": 24", $"\"columns\": {RosettaPagePreset.MaxColumns + 1}");
        using var hugeCols = new MemoryStream(Encoding.UTF8.GetBytes(tooManyCols));
        Assert.Throws<InvalidDataException>(() => RosettaPresetCatalog.Load(hugeCols));

        string tooManyRows = customJson.Replace("\"rows\": 12", $"\"rows\": {RosettaPagePreset.MaxRows + 1}");
        using var hugeRows = new MemoryStream(Encoding.UTF8.GetBytes(tooManyRows));
        Assert.Throws<InvalidDataException>(() => RosettaPresetCatalog.Load(hugeRows));
    }

    [Fact]
    public void RosettaPagePreset_RejectsInvalidSmallAndHugeDimensions()
    {
        RosettaPagePreset valid = RosettaPresetCatalog.BuiltIn.Hobbyist;
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Columns = 9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Columns = RosettaPagePreset.MaxColumns + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Rows = 7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => valid with { Rows = RosettaPagePreset.MaxRows + 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaPagePreset(
            "bad", "Bad", "DVD-R", valid.CellSize, 40_000, 12, 100, 100, false));
    }

    [Fact]
    public void TryResolveHeaderCell_PreservesSurrogatePairsAndHandlesWideColumns()
    {
        // Title with 300 non-BMP surrogate pairs after 'A' (601 chars) tests both surrogate-pair preservation and Columns > 256.
        string pair = "\U0001F600";
        string wideTitle = "A" + string.Concat(Enumerable.Repeat(pair, 300));
        RosettaPagePreset widePreset = RosettaPresetCatalog.BuiltIn.Hobbyist with { Columns = 300, Rows = 12 };
        RosettaBook book = RosettaBook.Build(
            [new RosettaDocument(wideTitle, "Body text")],
            new RosettaArchiveOptions("ARCHIVE", new DateOnly(2026, 11, 23), widePreset, InnerRadiusMm: 24.0, OuterRadiusMm: 58.0));

        RosettaPagePixelSource src = book.Bands[0].PixelSource;
        // Doc 0 starts on page 3 (0-based localPageIndex 2 in single band, R00-S02): header prefix is 22 chars at cols 4..25, 'A' at 26, pairs start at 27.
        int targetCp = char.ConvertToUtf32(pair, 0);
        Assert.True(src.TryResolveGlyphCell(0, 2, cellRow: 0, cellCol: 27, out int firstCp, out _, out _));
        Assert.Equal(targetCp, firstCp);

        // Near char index 255/256 and past column 256 on the 300-column preset, every header glyph must resolve to targetCp (never U+FFFD).
        Assert.True(src.TryResolveGlyphCell(0, 2, cellRow: 0, cellCol: 259, out int boundaryCp, out _, out _));
        Assert.Equal(targetCp, boundaryCp);

        Assert.True(src.TryResolveGlyphCell(0, 2, cellRow: 0, cellCol: 295, out int lateCp, out _, out _));
        Assert.Equal(targetCp, lateCp);

        // Also exercise the stackalloc (<= 256 chars) path when Columns = 128 and title fits within 256 chars with a surrogate pair near the end.
        string fitTitle = "A" + string.Concat(Enumerable.Repeat(pair, 110));
        RosettaPagePreset mediumPreset = RosettaPresetCatalog.BuiltIn.Hobbyist with { Columns = 128, Rows = 12 };
        RosettaBook mediumBook = RosettaBook.Build(
            [new RosettaDocument(fitTitle, "Body text")],
            new RosettaArchiveOptions("ARCHIVE", new DateOnly(2026, 11, 23), mediumPreset, InnerRadiusMm: 24.0, OuterRadiusMm: 58.0));
        Assert.True(mediumBook.Bands[0].PixelSource.TryResolveGlyphCell(0, 2, cellRow: 0, cellCol: 127, out int endCp, out _, out _));
        Assert.Equal(targetCp, endCp);
    }

    [Fact]
    public void PlanLayout_ComputesDvdAndCdCapacityWithoutTextFolder_AndRejectsSub50x75OnCd()
    {
        DiscPresetJsonDocument presets = DiscPresetJsonStore.LoadBuiltIn();
        CalibrationParameters dvd = DiscArtPresets.ToParameters(presets.DvdPresets.First(p => p.Id == "dvd-5-120mm"));
        CalibrationParameters cd = DiscArtPresets.ToParameters(presets.CdPresets.First());
        CalibrationParameters bd = DiscArtPresets.ToParameters(presets.BluRayPresets!.First());

        RosettaHybridPlan hobbyist = RosettaHybridDisc.PlanLayout(dvd, RosettaPresetCatalog.BuiltIn.Hobbyist);
        RosettaHybridPlan highDensity = RosettaHybridDisc.PlanLayout(dvd, RosettaPresetCatalog.BuiltIn.HighDensity);

        Assert.True(hobbyist.PageCapacity > 0);
        Assert.Equal(hobbyist.PageCapacity - hobbyist.PagesUsed, hobbyist.PagesFree);
        Assert.True(highDensity.PageCapacity > hobbyist.PageCapacity);
        Assert.Equal(hobbyist.VaultCeilingSectors, highDensity.VaultCeilingSectors);

        RosettaHybridPlan cdHobbyist = RosettaHybridDisc.PlanLayout(cd, RosettaPresetCatalog.BuiltIn.Hobbyist);
        RosettaHybridPlan cd50x75 = RosettaHybridDisc.PlanLayout(cd, RosettaPresetCatalog.BuiltIn.GetPreset("ladder-50x75"));
        Assert.Equal(CalibrationDiscKind.Cd, cdHobbyist.Medium);
        Assert.True(cdHobbyist.PageCapacity > 0);
        Assert.True(cd50x75.PageCapacity > cdHobbyist.PageCapacity);
        Assert.Equal(cdHobbyist.VaultCeilingSectors, cd50x75.VaultCeilingSectors);

        Assert.Throws<ArgumentException>(() => RosettaHybridDisc.PlanLayout(cd, RosettaPresetCatalog.BuiltIn.HighDensity));
        Assert.Throws<ArgumentException>(() => RosettaHybridDisc.PlanLayout(bd, RosettaPresetCatalog.BuiltIn.Hobbyist));
    }
}
