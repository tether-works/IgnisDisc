using System.Text;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.Tests.Presets;

public sealed class DiscPresetJsonStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_presets_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuiltInCatalog_LoadsSchema4WithAll17PresetsAndExactCoefficients()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();

        Assert.Equal(DiscPresetJsonStore.CurrentSchemaVersion, builtIn.SchemaVersion);
        Assert.Equal("zh-CN", builtIn.FallbackLanguage);
        Assert.Equal(
            ["cd-80", "ritek-medical-aqua", "verbatim-azo-43438", "cd-74"],
            builtIn.CdPresets.Select(p => p.Id).ToArray());
        Assert.Equal(
            [
                "dvd-5-120mm",
                "verbatim-dvd-r-azo-43533",
                "verbatim-dvd-r-azo-43523",
                "verbatim-dvd-plus-r-azo-43498",
                "philips-dvd-plus-r",
                "ritek-dvd-plus-r-e-series",
                "ritek-dvd-r-e-series",
                "dvd-5-80mm",
            ],
            builtIn.DvdPresets.Select(p => p.Id).ToArray());
        Assert.NotNull(builtIn.BluRayPresets);
        Assert.Equal(
            [
                "bd-r-sl-25gb",
                "bd-r-ritek-10x",
                "bd-r-ritek-12x",
                "bd-r-verbatim-64099",
                "bd-r-verbatim-64293",
            ],
            builtIn.BluRayPresets!.Select(p => p.Id).ToArray());

        DvdDiscPresetDefinition verbatim = builtIn.DvdPresets.Single(p => p.Id == "verbatim-dvd-r-azo-43533");
        Assert.Equal(2_297_888u, verbatim.TotalSectors);
        Assert.Equal(-3.9256854319227684E-05, verbatim.PitchLinear);
        Assert.Equal(6.964795915429256E-05, verbatim.PitchQuadratic);
        Assert.Equal(-4.670918950137523E-05, verbatim.PitchCubic);

        BluRayDiscPresetDefinition verbatimBd = builtIn.BluRayPresets.Single(p => p.Id == "bd-r-verbatim-64293");
        Assert.Equal("bczhc", verbatimBd.Contributor);
        Assert.Equal(12_219_392u, verbatimBd.TotalSectors);
    }

    [Fact]
    public void GetDefaultPath_TargetsAppDataIgnisDisc()
    {
        string path = DiscPresetJsonStore.GetDefaultPath();
        string expectedSuffix = Path.Combine("IgnisDisc", DiscPresetJsonStore.FileName);
        Assert.EndsWith(expectedSuffix, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadOrCreate_SaveLoadRoundTrip_AndStreamLoad()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, DiscPresetJsonStore.FileName);
        DiscPresetJsonDocument initial = DiscPresetJsonStore.CreateInitialUserDocument();

        DiscPresetJsonDocument created = DiscPresetJsonStore.LoadOrCreate(path, initial);
        Assert.True(File.Exists(path));
        Assert.Single(created.CdPresets);
        Assert.Single(created.DvdPresets);
        Assert.Single(created.BluRayPresets!);
        Assert.Equal("user-custom-cd", created.CdPresets[0].Id);
        Assert.Equal("user-custom-dvd", created.DvdPresets[0].Id);
        Assert.Equal("user-custom-bd", created.BluRayPresets![0].Id);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(File.ReadAllText(path)));
        DiscPresetJsonDocument fromStream = DiscPresetJsonStore.Load(stream, "memory.json");
        Assert.Equal(created.CdPresets[0], fromStream.CdPresets[0]);
        Assert.Equal(created.DvdPresets[0], fromStream.DvdPresets[0]);
        Assert.Equal(created.BluRayPresets![0], fromStream.BluRayPresets![0]);
    }

    [Fact]
    public void Merge_OverridesAppendsAndRespectsDisabledIds()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument user = DiscPresetJsonStore.CreateInitialUserDocument() with
        {
            CdPresets =
            [
                new CdDiscPresetDefinition("user-custom-cd", "Custom CD", "User CD", 359_849, 24.5, 56.8),
                new CdDiscPresetDefinition("cd-80", "Override CD 80", "Overridden", 350_000, 24.6, 57.0),
            ],
            DisabledDvdPresetIds = ["dvd-5-80mm"],
            DisabledBluRayPresetIds = ["bd-r-ritek-10x"],
        };

        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(builtIn, user);
        Assert.Equal(5, merged.CdPresets.Count);
        Assert.Equal(8, merged.DvdPresets.Count);
        Assert.Equal(5, merged.BluRayPresets!.Count);
        Assert.Equal(350_000L, merged.CdPresets.Single(p => p.Id == "cd-80").Sectors);
        Assert.Contains(merged.CdPresets, p => p.Id == "user-custom-cd");
        Assert.Contains(merged.DvdPresets, p => p.Id == "user-custom-dvd");
        Assert.Contains(merged.BluRayPresets, p => p.Id == "user-custom-bd");
        Assert.DoesNotContain(merged.DvdPresets, p => p.Id == "dvd-5-80mm");
        Assert.DoesNotContain(merged.BluRayPresets, p => p.Id == "bd-r-ritek-10x");
    }

    [Fact]
    public void LegacySchema2AndInterimSchema3_LoadAndMigrateToSchema4OnSave()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, DiscPresetJsonStore.FileName);

        File.WriteAllText(
            path,
            """
            {
              // Comments and trailing commas are allowed.
              "schemaVersion": 2,
              "cdPresets": [
                {
                  "id": "legacy-cd",
                  "displayName": "Legacy CD",
                  "description": "Schema 2 entry.",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0,
                  "linearVelocityMmPerSecond": 1400
                }
              ],
              "dvdPresets": [
                {
                  "id": "legacy-dvd",
                  "displayName": "Legacy DVD",
                  "description": "Schema 2 entry.",
                  "totalSectors": 2295104,
                  "innerRadiusMm": 24.0,
                  "outerRadiusMm": 58.0,
                  "channelBitLengthNm": 133.33
                }
              ],
            }
            """);

        DiscPresetJsonDocument legacy = DiscPresetJsonStore.Load(path);
        Assert.Equal(2, legacy.SchemaVersion);
        Assert.Equal(1_400.0, legacy.CdPresets[0].LinearVelocityMmPerSecond);
        Assert.Equal(133.33, legacy.DvdPresets[0].ChannelBitLengthNm);

        DiscPresetJsonStore.Save(path, legacy);
        DiscPresetJsonDocument migrated = DiscPresetJsonStore.Load(path);
        Assert.Equal(4, migrated.SchemaVersion);
        Assert.Empty(migrated.BluRayPresets!);
        Assert.Equal(1_400.0, migrated.CdPresets[0].LinearVelocityMmPerSecond);

        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 3,
              "cdPresets": [
                {
                  "id": "interim-cd",
                  "displayName": "Interim CD",
                  "description": "Schema 3 entry.",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0
                }
              ],
              "dvdPresets": [
                {
                  "id": "interim-dvd",
                  "displayName": "Interim DVD",
                  "description": "Schema 3 entry.",
                  "totalSectors": 2295104,
                  "innerRadiusMm": 24.0,
                  "outerRadiusMm": 58.0
                }
              ]
            }
            """);

        DiscPresetJsonDocument interim = DiscPresetJsonStore.Load(path);
        Assert.Equal(1_200.0, interim.CdPresets[0].LinearVelocityMmPerSecond);
        Assert.Equal(133.3, interim.DvdPresets[0].ChannelBitLengthNm);
        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(DiscPresetJsonStore.LoadBuiltIn(), interim);
        Assert.Equal(5, merged.BluRayPresets!.Count);
    }

    [Fact]
    public void InvalidDocumentsAndJson_AreRejected()
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, DiscPresetJsonStore.FileName);

        // Unmapped JSON property
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 4,
              "cdPresets": [
                {
                  "id": "extra-field",
                  "displayName": "Extra",
                  "description": "Unmapped field.",
                  "sectors": 350000,
                  "innerRadiusMm": 24.5,
                  "outerRadiusMm": 57.0,
                  "imageOuterRadiusMm": 58.0
                }
              ],
              "dvdPresets": [],
              "bluRayPresets": []
            }
            """);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Load(path));

        // Schema 4 missing bluRayPresets
        File.WriteAllText(path, """{ "schemaVersion": 4, "cdPresets": [], "dvdPresets": [] }""");
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Load(path));

        // Unsupported schema version
        File.WriteAllText(path, """{ "schemaVersion": 1, "cdPresets": [], "dvdPresets": [] }""");
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Load(path));

        // Reserved __manual__ ID
        DiscPresetJsonDocument reserved = new(
            4,
            [new CdDiscPresetDefinition("__manual__", "Manual", "Reserved", 350_000, 24.5, 57.0)],
            [],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(reserved));

        // Duplicate IDs
        DiscPresetJsonDocument duplicate = new(
            4,
            [
                new CdDiscPresetDefinition("dup", "A", "First", 350_000, 24.5, 57.0),
                new CdDiscPresetDefinition("dup", "B", "Second", 350_000, 24.5, 57.0),
            ],
            [],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(duplicate));

        // Misaligned usableSectors: CD (<=0 or >total), DVD (% 16), BD (% 32)
        DiscPresetJsonDocument badCdUsable = new(
            4,
            [new CdDiscPresetDefinition("cd-bad-usable", "CD", "Bad usable", 350_000, 24.5, 57.0, UsableSectors: 350_001)],
            [],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badCdUsable));

        DiscPresetJsonDocument badDvdUsable = new(
            4,
            [],
            [new DvdDiscPresetDefinition("dvd-bad-usable", "DVD", "Bad usable", 2_295_104, 24.0, 58.0, UsableSectors: 17)],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badDvdUsable));

        DiscPresetJsonDocument badBdUsable = new(
            4,
            [],
            [],
            BluRayPresets: [new BluRayDiscPresetDefinition("bd-bad-usable", "BD", "Bad usable", 12_219_392, 24.0, 58.0, UsableSectors: 33)]);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badBdUsable));

        // Misaligned BD totalSectors and outerRadiusMm > 60
        DiscPresetJsonDocument badBdTotal = new(
            4,
            [],
            [],
            BluRayPresets: [new BluRayDiscPresetDefinition("bd-bad-total", "BD", "Unaligned", 1000, 24.0, 58.0)]);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badBdTotal));

        DiscPresetJsonDocument badBdOuter = new(
            4,
            [],
            [],
            BluRayPresets: [new BluRayDiscPresetDefinition("bd-bad-outer", "BD", "Too wide", 12_219_392, 24.0, 60.1)]);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badBdOuter));

        // Non-positive cubic pitch polynomials: at boundary (x=1) and at interior minimum (x=0.5)
        DiscPresetJsonDocument badPitchBoundary = new(
            4,
            [],
            [new DvdDiscPresetDefinition("dvd-bad-pitch-end", "DVD", "Neg at x=1", 2_295_104, 24.0, 58.0, PitchLinear: -1.0)],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badPitchBoundary));

        // 1 - 4.1*x + 4.1*x^2 = 1 at x=0 and x=1, and -0.025 at x=0.5
        DiscPresetJsonDocument badPitchInterior = new(
            4,
            [],
            [new DvdDiscPresetDefinition("dvd-bad-pitch-mid", "DVD", "Neg at x=0.5", 2_295_104, 24.0, 58.0, PitchLinear: -4.1, PitchQuadratic: 4.1)],
            BluRayPresets: []);
        Assert.Throws<InvalidDataException>(() => DiscPresetJsonStore.Validate(badPitchInterior));
    }
}
