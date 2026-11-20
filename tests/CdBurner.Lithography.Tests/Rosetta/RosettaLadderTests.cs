using System.Text;
using System.Text.Json;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Rosetta;

public sealed class RosettaLadderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-ladder-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public RosettaLadderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CreateLayout_CoversAllEightBuiltInLadderSizesAcrossInnerMiddleOuterWithoutOverlap()
    {
        RosettaPresetCatalog catalog = RosettaPresetCatalog.BuiltIn;
        RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(catalog: catalog);
        RosettaLadderManifest manifest = layout.Manifest;

        Assert.Equal(3, manifest.Zones.Count);
        Assert.Equal(["inner", "middle", "outer"], manifest.Zones.Select(z => z.ZoneId).ToArray());
        Assert.Equal(24, manifest.Rungs.Count);
        Assert.Equal(catalog.Ladder.Count * 3, manifest.Rungs.Count);

        for (int z = 0; z < 3; z++)
        {
            List<RosettaLadderRung> zoneRungs = manifest.Rungs.Where(r => r.ZoneIndex == z).ToList();
            Assert.Equal(catalog.Ladder.Count, zoneRungs.Count);

            for (int k = 0; k < catalog.Ladder.Count; k++)
            {
                RosettaPagePreset expectedPreset = catalog.Ladder[k];
                RosettaLadderRung rung = zoneRungs[k];
                Assert.Equal(expectedPreset.CellSize.WidthUm, rung.CellWidthUm);
                Assert.Equal(expectedPreset.CellSize.HeightUm, rung.CellHeightUm);
                Assert.Equal(250.0, rung.Label.CellWidthUm);
                Assert.Equal(375.0, rung.Label.CellHeightUm);
                Assert.Equal(rung.Page.OuterRadiusMm, rung.Label.Bounds.OuterRadiusMm, precision: 6);
                Assert.Equal(manifest.Zones[z].OuterRadiusMm, rung.Page.OuterRadiusMm, precision: 6);
                Assert.True(BoxesDisjoint(rung.Label.Bounds, rung.Page));
            }
        }

        // All 48 polar boxes (24 labels + 24 pages) must be pairwise disjoint.
        var allBoxes = manifest.Rungs
            .SelectMany(r => new[] { r.Label.Bounds, r.Page })
            .ToArray();
        for (int i = 0; i < allBoxes.Length; i++)
        {
            for (int j = i + 1; j < allBoxes.Length; j++)
            {
                Assert.True(BoxesDisjoint(allBoxes[i], allBoxes[j]), $"Polar box {i} overlaps box {j}.");
            }
        }

        // Fixed test text covers A-Z, a-z, 0-9, and confusable sets 0O 1Il 5S 8B.
        string allText = string.Join("\n", manifest.TestTextLines);
        for (char c = 'A'; c <= 'Z'; c++) Assert.Contains(c, allText);
        for (char c = 'a'; c <= 'z'; c++) Assert.Contains(c, allText);
        for (char c = '0'; c <= '9'; c++) Assert.Contains(c, allText);
        foreach (string pair in new[] { "0O", "1Il", "5S", "8B" })
        {
            Assert.Contains(pair, allText, StringComparison.Ordinal);
        }

        // Round-trip JSON manifest and check IsoReadbackRenderer sidecar fields.
        string jsonPath = Path.Combine(_tempDir, "ladder.iso.json");
        string json = RosettaLadderDisc.SerializeManifestJson(manifest);
        File.WriteAllText(jsonPath, json);

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal(0u, root.GetProperty("startLba").GetUInt32());
            Assert.Equal(manifest.FillSectors, root.GetProperty("fillSectors").GetUInt32());
            Assert.Equal(manifest.TotalSectors, root.GetProperty("totalSectors").GetUInt32());
            Assert.Equal(DvdReadCompatibilityModes.Current, root.GetProperty("readCompatibility").GetString());
        }

        RosettaLadderManifest reloaded = RosettaLadderDisc.LoadManifestJson(jsonPath);
        Assert.Equal(manifest.Rungs.Count, reloaded.Rungs.Count);
        Assert.Equal(manifest.Zones.Count, reloaded.Zones.Count);
        Assert.Equal(manifest.DiscPresetId, reloaded.DiscPresetId);
    }

    [Fact]
    public void CreateLayout_Rejects80MmMiniDvdPresetWithClearMessage()
    {
        DvdDiscPresetDefinition miniDvd = DiscPresetJsonStore.LoadBuiltIn().DvdPresets.Single(p => p.Id == "dvd-5-80mm");
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RosettaLadderDisc.CreateLayout(RosettaLadderOptions.FromDvdPreset(miniDvd)));
        Assert.Contains("radial span", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cd-80")]
    [InlineData("cd-74")]
    [InlineData("verbatim-azo-43438")]
    [InlineData("ritek-medical-aqua")]
    public void CreateLayout_CdR_IncludesOnlyRungsAtLeast50x75UmWithoutOverlapAndAvoidsCircEdgeZones(string cdPresetId)
    {
        CdDiscPresetDefinition cdPreset = DiscPresetJsonStore.LoadBuiltIn().CdPresets.Single(p => p.Id == cdPresetId);
        RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(RosettaLadderOptions.FromCdPreset(cdPreset, startAngleDegrees: 12.5));
        RosettaLadderManifest manifest = layout.Manifest;

        Assert.Equal("CD-R", manifest.Media);
        Assert.Equal(cdPresetId, manifest.DiscPresetId);
        Assert.Equal(3, manifest.Zones.Count);
        Assert.Equal(["inner", "middle", "outer"], manifest.Zones.Select(z => z.ZoneId).ToArray());
        Assert.Equal(15, manifest.Rungs.Count);
        Assert.True(manifest.CdInterleave);
        Assert.Equal(CdCircEdgeZones.InnerEdgeZoneBytes, manifest.CircEdgeZoneBytes);

        (double w, double h)[] expectedSizes =
        [
            (250.0, 375.0),
            (150.0, 225.0),
            (100.0, 150.0),
            (75.0,  113.0),
            (50.0,  75.0),
        ];

        for (int z = 0; z < 3; z++)
        {
            List<RosettaLadderRung> zoneRungs = manifest.Rungs.Where(r => r.ZoneIndex == z).ToList();
            Assert.Equal(5, zoneRungs.Count);
            for (int k = 0; k < expectedSizes.Length; k++)
            {
                RosettaLadderRung rung = zoneRungs[k];
                Assert.Equal(expectedSizes[k].w, rung.CellWidthUm);
                Assert.Equal(expectedSizes[k].h, rung.CellHeightUm);
                Assert.True(rung.CdSupported);
                Assert.True(rung.CellWidthUm >= 50.0 && rung.CellHeightUm >= 75.0);
                Assert.True(BoxesDisjoint(rung.Label.Bounds, rung.Page));
            }
        }

        // All 30 polar boxes (15 labels + 15 pages) must be pairwise disjoint.
        RosettaPolarBox[] allBoxes = manifest.Rungs.SelectMany(r => new[] { r.Label.Bounds, r.Page }).ToArray();
        for (int i = 0; i < allBoxes.Length; i++)
        {
            for (int j = i + 1; j < allBoxes.Length; j++)
            {
                Assert.True(BoxesDisjoint(allBoxes[i], allBoxes[j]), $"CD polar box {i} overlaps box {j}.");
            }
        }

        // Every zone and rung byte range must stay strictly outside both ~18 mm CIRC edge zones.
        long totalBytes = checked((long)manifest.FillSectors * CdDiscParameters.BytesPerSector);
        long drawableEndByte = CdCircEdgeZones.LogicalDrawableEndByte(totalBytes);
        CdDiscParameters cdParams = layout.CdParameters!;
        double innerEdgeRadius = cdParams.RadiusFromByte(CdCircEdgeZones.InnerEdgeZoneBytes);
        double outerEdgeRadius = cdParams.RadiusFromByte(drawableEndByte);

        Assert.All(manifest.Zones, zone =>
        {
            Assert.True(zone.InnerRadiusMm > innerEdgeRadius);
            Assert.True(zone.OuterRadiusMm < outerEdgeRadius);
            Assert.True((long)zone.StartLba * CdDiscParameters.BytesPerSector >= CdCircEdgeZones.InnerEdgeZoneBytes);
            Assert.True((long)zone.EndLba * CdDiscParameters.BytesPerSector <= drawableEndByte);
        });
        Assert.All(manifest.Rungs, rung =>
        {
            Assert.True((long)rung.StartLba * CdDiscParameters.BytesPerSector >= CdCircEdgeZones.InnerEdgeZoneBytes);
            Assert.True((long)rung.EndLba * CdDiscParameters.BytesPerSector <= drawableEndByte);
        });

        string json = RosettaLadderDisc.SerializeManifestJson(manifest);
        string jsonPath = Path.Combine(_tempDir, $"{cdPresetId}.json");
        File.WriteAllText(jsonPath, json);
        RosettaLadderManifest reloaded = RosettaLadderDisc.LoadManifestJson(jsonPath);
        Assert.Equal("CD-R", reloaded.Media);
        Assert.Equal(15, reloaded.Rungs.Count);
        Assert.Equal(CdCircEdgeZones.InnerEdgeZoneBytes, reloaded.CircEdgeZoneBytes);
    }

    [Fact]
    public void CreateLayout_CdR_ExcludesSub50x75RungsAndRefusesCatalogWithoutCompatibleRungs()
    {
        const string mixedCatalogJson = """
        {
          "schemaVersion": 1,
          "presets": [
            { "id": "hobbyist", "name": "Hobbyist", "targetMedia": "DVD-R", "cellWidthUm": 100, "cellHeightUm": 150, "columns": 30, "rows": 13, "radialGutterUm": 200, "tangentialGutterUm": 200 },
            { "id": "high-density", "name": "High-density", "targetMedia": "DVD-R", "cellWidthUm": 26, "cellHeightUm": 34, "columns": 30, "rows": 17, "radialGutterUm": 60, "tangentialGutterUm": 60 }
          ],
          "ladder": [
            { "cellWidthUm": 60, "cellHeightUm": 90, "columns": 30, "rows": 13, "radialGutterUm": 120, "tangentialGutterUm": 120, "cdSupported": true },
            { "cellWidthUm": 40, "cellHeightUm": 60, "columns": 30, "rows": 15, "radialGutterUm": 90, "tangentialGutterUm": 90, "cdSupported": true },
            { "cellWidthUm": 34, "cellHeightUm": 51, "columns": 30, "rows": 17, "radialGutterUm": 80, "tangentialGutterUm": 80, "cdSupported": false }
          ]
        }
        """;

        CdDiscPresetDefinition cd80 = DiscPresetJsonStore.LoadBuiltIn().CdPresets.Single(p => p.Id == "cd-80");
        using var mixedMs = new MemoryStream(Encoding.UTF8.GetBytes(mixedCatalogJson));
        RosettaPresetCatalog mixedCatalog = RosettaPresetCatalog.Load(mixedMs);
        RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(RosettaLadderOptions.FromCdPreset(cd80), mixedCatalog);
        Assert.Equal(3, layout.Manifest.Rungs.Count);
        Assert.All(layout.Manifest.Rungs, r => Assert.Equal(60.0, r.CellWidthUm));

        const string tooSmallOnlyJson = """
        {
          "schemaVersion": 1,
          "presets": [
            { "id": "hobbyist", "name": "Hobbyist", "targetMedia": "DVD-R", "cellWidthUm": 100, "cellHeightUm": 150, "columns": 30, "rows": 13, "radialGutterUm": 200, "tangentialGutterUm": 200 },
            { "id": "high-density", "name": "High-density", "targetMedia": "DVD-R", "cellWidthUm": 26, "cellHeightUm": 34, "columns": 30, "rows": 17, "radialGutterUm": 60, "tangentialGutterUm": 60 }
          ],
          "ladder": [
            { "cellWidthUm": 40, "cellHeightUm": 60, "columns": 30, "rows": 15, "radialGutterUm": 90, "tangentialGutterUm": 90, "cdSupported": true },
            { "cellWidthUm": 26, "cellHeightUm": 34, "columns": 30, "rows": 17, "radialGutterUm": 60, "tangentialGutterUm": 60, "cdSupported": false }
          ]
        }
        """;

        using var smallMs = new MemoryStream(Encoding.UTF8.GetBytes(tooSmallOnlyJson));
        RosettaPresetCatalog smallCatalog = RosettaPresetCatalog.Load(smallMs);
        Assert.Throws<ArgumentException>(() =>
            RosettaLadderDisc.CreateLayout(RosettaLadderOptions.FromCdPreset(cd80), smallCatalog));
    }

    [Fact]
    public void CreateLayout_CdR_RejectsTinySectorCountOrNarrowRadialSpan()
    {
        var tinySectors = new RosettaLadderOptions(
            DiscPresetId: "tiny-sectors",
            TotalSectors: 2,
            InnerRadiusMm: 24.825,
            OuterRadiusMm: 58.0,
            Medium: RosettaLadderMedium.Cd,
            CdInterleave: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => RosettaLadderDisc.CreateLayout(tinySectors));

        var narrowSpan = new RosettaLadderOptions(
            DiscPresetId: "narrow-cd",
            TotalSectors: 100_000,
            InnerRadiusMm: 24.0,
            OuterRadiusMm: 38.0,
            Medium: RosettaLadderMedium.Cd,
            CdInterleave: true);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RosettaLadderDisc.CreateLayout(narrowSpan));
        Assert.Contains("radial span", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(false, 19.5)]
    public void CdR_WriteCdFile_And_VerifyCdTrack_MatchAll15RungsAndLabels(bool interleave, double startAngleDegrees)
    {
        var cdPreset = new CdDiscPresetDefinition(
            Id: "cd-ladder-slice",
            DisplayName: "CD ladder slice",
            Description: "Dense radial span for fast readback verification",
            Sectors: 100_000,
            InnerRadiusMm: 24.825,
            OuterRadiusMm: 46.5,
            LinearVelocityMmPerSecond: 1200.0);

        RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(
            RosettaLadderOptions.FromCdPreset(cdPreset, startAngleDegrees: startAngleDegrees, interleave: interleave));

        string trackPath = Path.Combine(_tempDir, interleave ? "cd-ladder.wav" : "cd-ladder.raw");
        string jsonPath = trackPath + ".json";
        CdGenerationSummary summary = RosettaLadderDisc.WriteCdFile(layout, trackPath, jsonPath);

        Assert.True(File.Exists(trackPath));
        Assert.True(File.Exists(jsonPath));
        if (interleave)
        {
            Assert.NotNull(summary.CueSheetPath);
            Assert.True(File.Exists(summary.CueSheetPath!));
            Assert.Equal(44L + (100_000L * CdDiscParameters.BytesPerSector), new FileInfo(trackPath).Length);
        }
        else
        {
            Assert.Null(summary.CueSheetPath);
            Assert.Equal(100_000L * CdDiscParameters.BytesPerSector, new FileInfo(trackPath).Length);
        }

        RosettaLadderVerificationReport report = RosettaLadderReadback.VerifyCdTrack(trackPath, jsonPath);
        _output.WriteLine($"CD-R interleave={interleave}, angle={startAngleDegrees}: Passed={report.PassedRungs}/{report.TotalRungs}, Sectors={report.InspectedEccBlocks}, Classified={report.ClassifiedPayloadBytes:N0}");
        foreach (RosettaRungVerification r in report.Rungs.Where(x => !x.AllGlyphBitmapsMatch))
        {
            _output.WriteLine($"  FAIL {r.RungId}: lblMis={r.LabelMismatchedSubpixels} (uns={r.LabelUnsampledSubpixels}), pgMis={r.PageMismatchedSubpixels} (uns={r.PageUnsampledSubpixels}), cellMis={r.MismatchedGlyphCells}, minCov={r.MinSampledPixelsPerGlyphCell}, cross={r.CrosshairsAndRuleMatch}");
        }

        Assert.True(report.AllRungsMatch, $"CD-R readback matched {report.PassedRungs}/{report.TotalRungs} rungs.");
        Assert.Equal(15, report.PassedRungs);
        Assert.All(report.Rungs, rung =>
        {
            Assert.Equal(0, rung.LabelMismatchedSubpixels);
            Assert.Equal(0, rung.PageMismatchedSubpixels);
            Assert.Equal(0, rung.MismatchedGlyphCells);
            Assert.True(rung.CrosshairsAndRuleMatch, $"Crosshairs/rule failed on {rung.RungId}");
            Assert.True(rung.MinSampledPixelsPerGlyphCell >= 28, $"Low sample coverage on {rung.RungId}: {rung.MinSampledPixelsPerGlyphCell}");
        });
    }

    private static bool SamplePngBoxSubpixel(
        BgraRaster png,
        double centre,
        double previewOuterMm,
        RosettaPolarBox box,
        int gridW,
        int gridH,
        int gx,
        int gy)
    {
        double uMm = ((gx + 0.5) / gridW) * box.WidthMm;
        double yMm = ((gy + 0.5) / gridH) * box.HeightMm;
        double rMm = box.OuterRadiusMm - yMm;
        double cwAngle = box.StartAngleRadians + (uMm / box.MidRadiusMm);
        double xMm = rMm * Math.Cos(cwAngle);
        double yDownMm = rMm * Math.Sin(cwAngle);

        int imgX = (int)Math.Round(centre + (centre * xMm / previewOuterMm));
        int imgY = (int)Math.Round(centre + (centre * yDownMm / previewOuterMm));
        byte lum = png.Pixels[((imgY * png.Width) + imgX) * 4];
        return lum < 128;
    }

    private static bool BoxesDisjoint(RosettaPolarBox a, RosettaPolarBox b)
    {
        if (a.OuterRadiusMm <= b.InnerRadiusMm || b.OuterRadiusMm <= a.InnerRadiusMm)
        {
            return true;
        }

        double diffAB = PolarPageLayout.WrapAngle(b.StartAngleRadians - a.StartAngleRadians);
        double diffBA = PolarPageLayout.WrapAngle(a.StartAngleRadians - b.StartAngleRadians);
        return diffAB >= a.AngularSpanRadians && diffBA >= b.AngularSpanRadians;
    }

    /// <summary>Own class so xUnit runs it beside the other tests of this file.</summary>
    public sealed class AllRungsReadback
    {
        private readonly ITestOutputHelper _output;

        public AllRungsReadback(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void SimulatedReadback_All24RungsAndCoarseLabels_MatchGlyphBitmapsOnStandardAndCalibratedPresets()
        {
            DiscPresetJsonDocument presets = DiscPresetJsonStore.LoadBuiltIn();
            DvdDiscPresetDefinition standard = presets.DvdPresets.Single(p => p.Id == "dvd-5-120mm");
            DvdDiscPresetDefinition calibrated = presets.DvdPresets.Single(p => p.Id == "verbatim-dvd-r-azo-43533");

            // Test constant-pitch standard DVD and cubic-pitch calibrated DVD geometry across all 24 rungs
            // (full 2,295,104-sector 4.70 GB end-to-end generation + VerifyIso is covered in RosettaLadderCliTests).
            foreach ((DvdDiscPresetDefinition preset, double angle) in new[] { (standard, 0.0), (calibrated, 17.5) })
            {
                RosettaLadderOptions baseOpts = RosettaLadderOptions.FromDvdPreset(preset, startAngleDegrees: angle);
                RosettaLadderOptions testOpts = baseOpts with
                {
                    TotalSectors = 740_000,
                    UsableSectors = 740_000,
                    InnerRadiusMm = 12.0,
                    OuterRadiusMm = 32.5,
                };
                RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(testOpts);
                RosettaLadderVerificationReport report = RosettaLadderReadback.VerifyInMemory(layout);

                _output.WriteLine($"Preset={preset.Id}, Angle={angle}, Passed={report.PassedRungs}/{report.TotalRungs}, Blocks={report.InspectedEccBlocks}, PayloadBytes={report.ClassifiedPayloadBytes:N0}");
                foreach (RosettaRungVerification r in report.Rungs.Where(x => !x.AllGlyphBitmapsMatch))
                {
                    _output.WriteLine($"  FAIL {r.RungId}: lblMis={r.LabelMismatchedSubpixels} (uns={r.LabelUnsampledSubpixels}), pgMis={r.PageMismatchedSubpixels} (uns={r.PageUnsampledSubpixels}), cellMis={r.MismatchedGlyphCells}, minCov={r.MinSampledPixelsPerGlyphCell}, cross={r.CrosshairsAndRuleMatch}");
                    foreach (RosettaCellReadback fc in r.FailedCells.Take(10))
                    {
                        _output.WriteLine($"    cell ({fc.Row},{fc.Column}) '{fc.ExpectedChar}': cov={fc.SampledPixelCount}, mis={fc.MismatchedSampledPixels}");
                    }
                }

                Assert.True(report.AllRungsMatch, $"Preset {preset.Id} failed readback: {report.PassedRungs}/{report.TotalRungs}");
                Assert.Equal(24, report.PassedRungs);

                foreach (RosettaRungVerification rung in report.Rungs)
                {
                    Assert.Equal(0, rung.LabelMismatchedSubpixels);
                    Assert.Equal(0, rung.PageMismatchedSubpixels);
                    Assert.Equal(0, rung.MismatchedGlyphCells);
                    Assert.True(rung.CrosshairsAndRuleMatch, $"Crosshairs/rule failed on {rung.RungId}");
                    Assert.True(rung.MinSampledPixelsPerGlyphCell >= 28, $"Low sample coverage on {rung.RungId}: {rung.MinSampledPixelsPerGlyphCell}");
                }
            }
        }
    }

    /// <summary>Own class so xUnit runs it beside the other tests of this file.</summary>
    public sealed class CoarseRungCrossCheck : IDisposable
    {
        private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-coarserungcrosscheck-" + Guid.NewGuid().ToString("N"));

        public CoarseRungCrossCheck()
        {
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public void CoarseRung_CrossChecksAgainstUnmodifiedIsoReadbackRenderer()
        {
            var options = new RosettaLadderOptions(
                DiscPresetId: "tight-slice",
                TotalSectors: 300_000,
                InnerRadiusMm: 12.0,
                OuterRadiusMm: 32.5,
                StartAngleDegrees: 27.0);

            RosettaLadderLayout layout = RosettaLadderDisc.CreateLayout(options);
            RosettaLadderZone innerZone = layout.Manifest.Zones[0];
            uint sliceSectors = innerZone.EndLba;

            string isoPath = Path.Combine(_tempDir, "coarse-slice.iso");
            string pngPath = Path.Combine(_tempDir, "coarse-slice.png");

            Func<DvdCodewordSample, byte> sampler = RosettaLadderDisc.CreateCodewordGrayscaleSampler(layout);
            MultiBlockSolveOptions spacerOptions = RosettaLadderDisc.BuildSegmentSolveOptions(layout, 0, innerZone.StartLba);
            MultiBlockSolveOptions innerOptions = RosettaLadderDisc.BuildSegmentSolveOptions(layout, innerZone.StartLba, sliceSectors - innerZone.StartLba);

            using (var fs = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                if (innerZone.StartLba > 0)
                {
                    FastDispersionImageWriter.WriteSectorPatternStream(static _ => RosettaDvdEncoder.PaperLuminance, fs, spacerOptions);
                }
                FastDispersionImageWriter.WriteCodewordPatternStream(sampler, fs, innerOptions);
            }

            var sidecar = new
            {
                startLba = 0u,
                fillSectors = sliceSectors,
                totalSectors = options.TotalSectors,
                readCompatibility = DvdReadCompatibilityModes.Current,
            };
            File.WriteAllText(isoPath + ".json", JsonSerializer.Serialize(sidecar));

            double previewOuterMm = innerZone.OuterRadiusMm + 0.1;
            IsoReadbackRenderer.Render(isoPath, pngPath, new IsoReadbackOptions(
                TotalSectors: options.TotalSectors,
                StartLba: 0,
                FillSectors: sliceSectors,
                PsnOffset: options.PsnOffset,
                InnerRadiusMm: options.InnerRadiusMm,
                OuterRadiusMm: options.OuterRadiusMm,
                ChannelBitLengthNm: options.ChannelBitLengthNm,
                Clockwise: true,
                PreviewSize: 6144,
                SamplesPerSector: 2048,
                PreviewOuterRadiusMm: previewOuterMm));

            BgraRaster png = PngRaster.Load(pngPath);
            Spleen5x8Font font = Spleen5x8Font.BuiltIn;
            double centre = (png.Width - 1) / 2.0;

            // Verify coarse 250x375 label and 150x225 / 100x150 rung pages pixel-for-pixel from the PNG.
            foreach (int k in new[] { 0, 1, 2 })
            {
                RosettaLadderRung rung = layout.Manifest.Rungs[k];
                RosettaPolarBox labelBox = rung.Label.Bounds;
                int labelGridW = RosettaLadderDisc.LabelColumns * Spleen5x8Font.CellPixelWidth;
                int labelGridH = RosettaLadderDisc.LabelRows * Spleen5x8Font.CellPixelHeight;
                int labelExpectedInk = 0;
                int labelFalseInk = 0;
                int labelMissedInk = 0;

                for (int gy = 0; gy < labelGridH; gy++)
                {
                    for (int gx = 0; gx < labelGridW; gx++)
                    {
                        bool actual = SamplePngBoxSubpixel(png, centre, previewOuterMm, labelBox, labelGridW, labelGridH, gx, gy);
                        bool expected = RosettaLadderDisc.IsExpectedLabelSubpixelInk(rung, gx, gy, font);
                        if (expected)
                        {
                            labelExpectedInk++;
                        }
                        if (expected != actual)
                        {
                            if (actual && !expected)
                            {
                                labelFalseInk++;
                            }
                            else
                            {
                                labelMissedInk++;
                            }
                        }
                    }
                }

                Assert.Equal(0, labelFalseInk);
                Assert.True(
                    labelMissedInk <= Math.Max(2, labelExpectedInk / 100),
                    $"Label PNG missed too many ink subpixels rung={rung.RungId}: missed={labelMissedInk}/{labelExpectedInk}");

                if (k >= 1)
                {
                    int pageGridW = rung.Columns * Spleen5x8Font.CellPixelWidth;
                    int pageGridH = (rung.Rows * Spleen5x8Font.CellPixelHeight) + 3;
                    int expectedInkCount = 0;
                    int falseInk = 0;
                    int missedInk = 0;
                    int[] cellMismatches = new int[rung.Columns * rung.Rows];

                    for (int gy = 0; gy < pageGridH; gy++)
                    {
                        int cellRow = gy / Spleen5x8Font.CellPixelHeight;
                        for (int gx = 0; gx < pageGridW; gx++)
                        {
                            bool actual = SamplePngBoxSubpixel(png, centre, previewOuterMm, rung.Page, pageGridW, pageGridH, gx, gy);
                            bool expected = RosettaLadderDisc.IsExpectedPageSubpixelInk(rung, layout.Manifest.TestTextLines, gx, gy, font);
                            if (expected)
                            {
                                expectedInkCount++;
                            }
                            if (expected != actual)
                            {
                                if (actual && !expected)
                                {
                                    falseInk++;
                                }
                                else
                                {
                                    missedInk++;
                                }

                                if (cellRow < rung.Rows)
                                {
                                    int cellCol = gx / Spleen5x8Font.CellPixelWidth;
                                    cellMismatches[(cellRow * rung.Columns) + cellCol]++;
                                }
                            }
                        }
                    }

                    // IsoReadbackRenderer maps discrete payload samples onto an 8192x8192 PNG; at PO-parity
                    // integer-sector/turn resonances a 0.46 mm radial band has zero payload samples (white pixels).
                    Assert.Equal(0, falseInk);
                    Assert.True(
                        missedInk <= Math.Max(2, expectedInkCount / 100),
                        $"Too many missed ink subpixels in {rung.RungId} PNG: missed={missedInk}/{expectedInkCount}");
                    Assert.True(
                        cellMismatches.Max() <= 6,
                        $"Single cell lost too many subpixels in {rung.RungId} PNG: maxCellMismatch={cellMismatches.Max()}");
                }
            }
        }
    }
}
