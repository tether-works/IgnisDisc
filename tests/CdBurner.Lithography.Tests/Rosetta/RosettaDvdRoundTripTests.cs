using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Rosetta;

public sealed class RosettaDvdRoundTripTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public RosettaDvdRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Hobbyist_Spleen_RoundTripThroughDvdEncoderAndReadbackRenderer_HasZeroGlyphMismatches()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        RosettaPagePreset preset = catalog.Hobbyist; // 100x150 um (Spleen pixel 20x18.75 um)

        const double innerRadiusMm = 24.0;
        const double outerRadiusMm = 26.15; // 1 ring (2.00625 mm tall) -> ~6.38 um/px at 8192 PNG
        const uint totalSectors = 88_832;

        RosettaDocument[] docs =
        [
            new("ASYM_LATIN", "R0O1Il5S8Bp7+? Fqgy!"),
        ];

        RosettaBook book = RosettaBook.Build(docs, new RosettaArchiveOptions(
            ArchiveTitle: "SPLEEN_RT",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: preset,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm));

        string isoPath = Path.Combine(_tempDir, "spleen.iso");
        string pngPath = Path.Combine(_tempDir, "spleen.png");

        var encodeOptions = new RosettaDvdEncodeOptions(
            TotalSectors: totalSectors,
            StartLba: 0,
            FillSectors: totalSectors,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm,
            ChannelBitLengthNm: 133.3,
            StartAngleDegrees: 45.0,
            ReadCompatibility: true);

        RosettaDvdEncoder.WriteIsoFile(book, isoPath, encodeOptions);
        string sidecarJson = File.ReadAllText(isoPath + ".json");
        Assert.Contains($"\"readCompatibility\": \"{DvdReadCompatibilityModes.Current}\"", sidecarJson, StringComparison.Ordinal);

        IsoReadbackSummary summary = IsoReadbackRenderer.Render(isoPath, pngPath, new IsoReadbackOptions(
            TotalSectors: totalSectors,
            StartLba: 0,
            FillSectors: totalSectors,
            PsnOffset: encodeOptions.PsnOffset,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm,
            ChannelBitLengthNm: 133.3,
            Clockwise: true,
            PreviewSize: 8192,
            SamplesPerSector: 2048,
            PreviewOuterRadiusMm: outerRadiusMm));

        _output.WriteLine($"Summary: pitch={summary.TrackPitchMicrometres:F4} um, turns={summary.TotalTurns:F2}, classified={summary.ClassifiedSamples}/{summary.TotalSamples}");

        BgraRaster readback = PngRaster.Load(pngPath);
        RosettaBand band = book.Bands[0];
        Spleen5x8Font spleen = Spleen5x8Font.BuiltIn;

        // Verify R00-S00 (Contents) and R00-S04 (Spleen Latin doc): crosshairs, header, rule, and every body glyph.
        foreach (int sector in new[] { 0, 4 })
        {
            VerifyCornerCrosshairsAndRule(readback, outerRadiusMm, band.Layout, band.PixelSource, sector, preset, forceUnifont: false, startAngleDegrees: 45.0);

            for (int row = 0; row < preset.Rows; row++)
            {
                for (int col = 0; col < preset.Columns; col++)
                {
                    if (!band.PixelSource.TryResolveGlyphCell(0, sector, row, col, out int cp, out bool useUnifont, out _))
                    {
                        continue;
                    }
                    Assert.False(useUnifont);

                    double cellX0 = col * preset.CellSize.WidthMeters;
                    double cellY0 = row == 0 ? 0.0 : preset.CellSize.HeightMeters + preset.RuleBandHeightMeters + ((row - 1) * preset.CellSize.HeightMeters);

                    bool[,] actual = RosettaDvdEncoder.SampleCellBitmapFromReadback(
                        readback,
                        outerRadiusMm,
                        band.Layout,
                        ringInBand: 0,
                        sector: sector,
                        cellX0Meters: cellX0,
                        cellY0Meters: cellY0,
                        cellWidthMeters: preset.CellSize.WidthMeters,
                        cellHeightMeters: preset.CellSize.HeightMeters,
                        gridWidth: Spleen5x8Font.CellPixelWidth,
                        gridHeight: Spleen5x8Font.CellPixelHeight,
                        startAngleDegrees: 45.0);

                    int mismatches = 0;
                    for (int py = 0; py < Spleen5x8Font.CellPixelHeight; py++)
                    {
                        for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
                        {
                            bool expected = spleen.IsPixelSet(cp, px, py);
                            if (expected != actual[px, py])
                            {
                                mismatches++;
                                _output.WriteLine($"Spleen mismatch sector={sector} row={row} col={col} U+{cp:X4} px=({px},{py}): exp={expected} act={actual[px, py]}");
                            }
                        }
                    }
                    Assert.Equal(0, mismatches);
                }
            }
        }
    }

    [Fact]
    public void Ladder150x225_Unifont_RoundTripIncludingWideCjk_HasZeroGlyphMismatches()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        RosettaPagePreset preset = catalog.Ladder[1]; // 150x225 um (Unifont pixel 18.75x14.06 um)

        const double innerRadiusMm = 24.16;
        const double outerRadiusMm = 27.31; // 1 ring (~2.953 mm tall) -> ~6.67 um/px at 8192 PNG
        const uint totalSectors = 133_760;

        RosettaDocument[] docs =
        [
            new("MULTI", "R7p? Ω Ж 永", ForceUnifont: true),
        ];

        RosettaBook book = RosettaBook.Build(docs, new RosettaArchiveOptions(
            ArchiveTitle: "UNIFONT_RT",
            ArchiveDate: new DateOnly(2026, 11, 15),
            BodyPreset: preset,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm));

        string isoPath = Path.Combine(_tempDir, "unifont.iso");
        string pngPath = Path.Combine(_tempDir, "unifont.png");

        var encodeOptions = new RosettaDvdEncodeOptions(
            TotalSectors: totalSectors,
            StartLba: 0,
            FillSectors: totalSectors,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm,
            ChannelBitLengthNm: 133.3,
            StartAngleDegrees: 0.0,
            ReadCompatibility: true);

        RosettaDvdEncoder.WriteIsoFile(book, isoPath, encodeOptions);
        string sidecarJson = File.ReadAllText(isoPath + ".json");
        Assert.Contains($"\"readCompatibility\": \"{DvdReadCompatibilityModes.Current}\"", sidecarJson, StringComparison.Ordinal);

        IsoReadbackSummary summary = IsoReadbackRenderer.Render(isoPath, pngPath, new IsoReadbackOptions(
            TotalSectors: totalSectors,
            StartLba: 0,
            FillSectors: totalSectors,
            PsnOffset: encodeOptions.PsnOffset,
            InnerRadiusMm: innerRadiusMm,
            OuterRadiusMm: outerRadiusMm,
            ChannelBitLengthNm: 133.3,
            Clockwise: true,
            PreviewSize: 8192,
            SamplesPerSector: 2048,
            PreviewOuterRadiusMm: outerRadiusMm));

        _output.WriteLine($"Summary: pitch={summary.TrackPitchMicrometres:F4} um, turns={summary.TotalTurns:F2}, classified={summary.ClassifiedSamples}/{summary.TotalSamples}");

        BgraRaster readback = PngRaster.Load(pngPath);
        RosettaBand band = book.Bands[0];
        UnifontBitmapFont unifont = UnifontBitmapFont.BuiltIn;

        // Verify Reader Instructions (R00-S01) and Multilingual doc (last populated sector).
        int docSector = book.TotalPages - 1;
        foreach (int sector in new[] { 1, docSector })
        {
            VerifyCornerCrosshairsAndRule(readback, outerRadiusMm, band.Layout, band.PixelSource, sector, preset, forceUnifont: true, startAngleDegrees: 0.0);

            for (int row = 0; row < preset.Rows; row++)
            {
                for (int col = 0; col < preset.Columns; col++)
                {
                    if (!band.PixelSource.TryResolveGlyphCell(0, sector, row, col, out int cp, out bool useUnifont, out int wideHalf))
                    {
                        continue;
                    }
                    Assert.True(useUnifont);

                    double cellX0 = col * preset.CellSize.WidthMeters;
                    double cellY0 = row == 0 ? 0.0 : preset.CellSize.HeightMeters + preset.RuleBandHeightMeters + ((row - 1) * preset.CellSize.HeightMeters);

                    bool[,] actual = RosettaDvdEncoder.SampleCellBitmapFromReadback(
                        readback,
                        outerRadiusMm,
                        band.Layout,
                        ringInBand: 0,
                        sector: sector,
                        cellX0Meters: cellX0,
                        cellY0Meters: cellY0,
                        cellWidthMeters: preset.CellSize.WidthMeters,
                        cellHeightMeters: preset.CellSize.HeightMeters,
                        gridWidth: UnifontBitmapFont.NarrowPixelWidth,
                        gridHeight: UnifontBitmapFont.CellPixelHeight,
                        startAngleDegrees: 0.0);

                    for (int py = 0; py < UnifontBitmapFont.CellPixelHeight; py++)
                    {
                        for (int px = 0; px < UnifontBitmapFont.NarrowPixelWidth; px++)
                        {
                            bool expected = unifont.IsPixelSet(cp, (wideHalf * UnifontBitmapFont.NarrowPixelWidth) + px, py);
                            Assert.True(
                                expected == actual[px, py],
                                $"Mismatch at sector {sector} row {row} col {col} U+{cp:X4} half={wideHalf} px=({px},{py}): expected {expected}, got {actual[px, py]}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Encoder_RejectsNonBinaryOrDitheredToneProfiles()
    {
        RosettaBook book = RosettaBook.Build(
            [new("DOC", "Test")],
            new RosettaArchiveOptions("TEST", new DateOnly(2026, 11, 15), RosettaPresetCatalog.BuiltIn.Hobbyist, InnerRadiusMm: 24.0, OuterRadiusMm: 28.0));

        var baseOpts = new RosettaDvdEncodeOptions(TotalSectors: 1600, FillSectors: 1600, InnerRadiusMm: 24.0, OuterRadiusMm: 28.0);

        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.BuildSolveOptions(baseOpts with { Tone = ToneProfile.Photo }));
        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.BuildSolveOptions(baseOpts with { Tone = new ToneProfile(0.1, 2, 0) }));
        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.BuildSolveOptions(baseOpts with { Tone = new ToneProfile(0, 4, 0) }));
        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.BuildSolveOptions(baseOpts with { Tone = new ToneProfile(0, 2, 12) }));
        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.BuildSolveOptions(baseOpts with { Tone = new ToneProfile(0, 2, 0, SrgbLinearization: true) }));

        MultiBlockSolveOptions ditheredSolve = RosettaDvdEncoder.BuildSolveOptions(baseOpts);
        ditheredSolve = ditheredSolve with { ImageMapping = ditheredSolve.ImageMapping with { Tone = ToneProfile.Photo } };
        Assert.Throws<ArgumentException>(() => RosettaDvdEncoder.WriteStream(book, Stream.Null, ditheredSolve));
    }

    private static void VerifyCornerCrosshairsAndRule(
        BgraRaster readback,
        double previewOuterRadiusMm,
        PolarPageLayout layout,
        RosettaPagePixelSource source,
        int sector,
        RosettaPagePreset preset,
        bool forceUnifont,
        double startAngleDegrees = 0.0)
    {
        double cellW = preset.CellSize.WidthMeters;
        double cellH = preset.CellSize.HeightMeters;
        double pageW = preset.PageTangentialWidthMeters;
        double pageH = preset.PageRadialHeightMeters;

        bool[,] tl = RosettaDvdEncoder.SampleCellBitmapFromReadback(
            readback, previewOuterRadiusMm, layout, 0, sector,
            0.0, 0.0, 3.0 * cellW, 3.0 * cellH, 3, 3, startAngleDegrees);
        bool[,] br = RosettaDvdEncoder.SampleCellBitmapFromReadback(
            readback, previewOuterRadiusMm, layout, 0, sector,
            pageW - (3.0 * cellW), pageH - (3.0 * cellH), 3.0 * cellW, 3.0 * cellH, 3, 3, startAngleDegrees);

        for (int cy = 0; cy < 3; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                bool expected = cx == 1 || cy == 1;
                Assert.Equal(expected, tl[cx, cy]);
                Assert.Equal(expected, br[cx, cy]);
            }
        }

        // 1-pixel horizontal rule across columns 4..Columns-1.
        double onePxH = forceUnifont ? (cellH / 16.0) : (cellH / 8.0);
        double ruleY0 = cellH + (0.5 * (preset.RuleBandHeightMeters - onePxH));
        bool[,] rule = RosettaDvdEncoder.SampleCellBitmapFromReadback(
            readback, previewOuterRadiusMm, layout, 0, sector,
            4.0 * cellW, ruleY0, (preset.Columns - 4) * cellW, onePxH, preset.Columns - 4, 1, startAngleDegrees);

        for (int i = 0; i < preset.Columns - 4; i++)
        {
            Assert.True(rule[i, 0], $"Rule gap at sector {sector} col {4 + i}");
        }
        Assert.True(source.IsInk(0, sector, 4.5 * cellW, pageH - (ruleY0 + 0.5 * onePxH)));
    }
}
