using System.Text;
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
    }
}
