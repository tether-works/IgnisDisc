using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.BluRay.Encoding;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The rim header on picture discs. Each disc is a 4 to 5 mm ring at the rim with a coarse track pitch (10 to 40 um),
/// so an image stays small, yet the full-size header (2 mm capitals) fits. The header is read back from what a drive
/// would return: the CD and DVD read-back previews, and for BD the image's own bytes descrambled and placed at their
/// physical positions. The text is then read glyph by glyph against the font, without using the encoder's ink test.
/// </summary>
public sealed class DiscArtRimHeaderTests : IClassFixture<DiscArtRimHeaderTests.SharedFixtures>, IDisposable
{
    public sealed class SharedFixtures : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ignisdisc-rimheader-shared-" + Guid.NewGuid().ToString("N"));
        public string PicturePath { get; }
        public string PictureSha16 { get; }
        public string PresetsPath { get; }

        public SharedFixtures()
        {
            Directory.CreateDirectory(Dir);

            PicturePath = Path.Combine(Dir, "picture.png");
            const int size = 64;
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = (y * size + x) * 4;
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < size / 2 ? 0 : 255);
                    pixels[o + 3] = 255;
                }
            PngRaster.SaveBgra32(PicturePath, new BgraRaster(size, size, pixels));
            PictureSha16 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PicturePath)))[..16];

            PresetsPath = Path.Combine(Dir, "presets.json");
            File.WriteAllText(PresetsPath, """
                {
                  "schemaVersion": 4,
                  "cdPresets": [
                    { "id": "narrow-cd", "displayName": "Narrow CD", "description": "test", "sectors": 1422,
                      "innerRadiusMm": 57.9, "outerRadiusMm": 58, "linearVelocityMmPerSecond": 1200 }
                  ],
                  "dvdPresets": [
                    { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": 4096,
                      "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
                  ],
                  "bluRayPresets": [
                    { "id": "tiny-bd", "displayName": "Tiny BD", "description": "test", "totalSectors": 4096,
                      "innerRadiusMm": 24, "outerRadiusMm": 58 }
                  ]
                }
                """);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private const string Title = "Family Photos";
    private static readonly DateOnly Date = new(2026, 11, 18);

    // About 10 um (CD), 20 um (DVD) and 40 um (BD) between turns.
    private static readonly CalibrationParameters RingCd = new(CalibrationDiscKind.Cd, 53, 58, 10_900, 1200);
    private static readonly CalibrationParameters RingDvd = new(CalibrationDiscKind.Dvd, 53, 58, 16_896, 133.3);
    private static readonly CalibrationParameters RingBd = new(CalibrationDiscKind.BluRay, 53.5, 58, 17_600,
        CalibrationParameters.BluRayChannelBitLengthNm);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-rimheader-" + Guid.NewGuid().ToString("N"));
    private readonly SharedFixtures _shared;

    public DiscArtRimHeaderTests(SharedFixtures shared)
    {
        _shared = shared;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>64 x 64 picture, left half black, right half white.</summary>
    private string WritePicture() => _shared.PicturePath;

    private string PictureSha16() => _shared.PictureSha16;

    private DiscArtRequest Request(DiscArtMedium medium, CalibrationParameters disc, string output, bool header = true)
        => new(medium, WritePicture(), disc, output, RimHeader: header ? new DiscArtRimHeader(Title, Date) : null);

    /// <summary>The header as the plan describes it, rebuilt from the plan's numbers alone.</summary>
    private RosettaRimHeader HeaderFromPlan(DiscArtRimHeaderPlan plan)
        => new(new RosettaRimHeaderOptions(Title, Date, PictureSha16(), plan.OuterRadiusMm, plan.Scale, plan.TrackPitchUm / 1000.0));

    // ---------------------------------------------------------------------------------------------------------------
    // Reading the header back
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Ink test on a read-back image: the 3 x 3 pixels around the point, darker than mid-grey on average.</summary>
    private static Func<double, double, bool> InkFromPreview(BgraRaster image, double outerRadiusMm, bool centreOnPixelEdge)
    {
        double centre = centreOnPixelEdge ? image.Width / 2.0 : (image.Width - 1) / 2.0;
        double scale = centreOnPixelEdge ? image.Width / 2.0 : (image.Width - 1) / 2.0;
        return (xMm, yDownMm) =>
        {
            int cx = (int)Math.Round(centre + (scale * xMm / outerRadiusMm));
            int cy = (int)Math.Round(centre + (scale * yDownMm / outerRadiusMm));
            int sum = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height) continue;
                    sum += image.Pixels[((y * image.Width) + x) * 4];
                    n++;
                }

            return n > 0 && sum < 128 * n;
        };
    }

    /// <summary>
    /// Reads the first text item cell by cell: samples the 5 x 8 pixel centres of each glyph cell, then takes the
    /// printable character whose font bitmap differs in the fewest pixels.
    /// </summary>
    private static string DecodeHeaderText(RosettaRimHeader header, Func<double, double, bool> inkAt)
    {
        Spleen5x8Font font = Spleen5x8Font.BuiltIn;
        RosettaRimHeaderMetrics m = header.Metrics;
        RosettaRimHeaderItem item = header.Items[0];
        double startArcMm = item.StartAngleDegrees * Math.PI / 180.0 * header.TextMidRadiusMm;
        char[] candidates = Enumerable.Range(0x20, 0x5F).Select(c => (char)c).Where(c => font.HasGlyph(c)).ToArray();
        var text = new StringBuilder(item.Columns);
        for (int column = 0; column < item.Columns; column++)
        {
            var bits = new bool[Spleen5x8Font.CellPixelWidth, Spleen5x8Font.CellPixelHeight];
            for (int py = 0; py < Spleen5x8Font.CellPixelHeight; py++)
            {
                double r = header.TextOuterRadiusMm - ((py + 0.5) * m.GlyphPixelHeightMm);
                for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
                {
                    double u = startArcMm + (column * m.GlyphCellWidthMm) + ((px + 0.5) * m.GlyphPixelWidthMm);
                    double angle = u / header.TextMidRadiusMm;
                    bits[px, py] = inkAt(r * Math.Cos(angle), r * Math.Sin(angle));
                }
            }

            char best = '?';
            int bestDistance = int.MaxValue;
            foreach (char c in candidates)
            {
                int distance = 0;
                for (int py = 0; py < Spleen5x8Font.CellPixelHeight; py++)
                    for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
                        if (font.IsPixelSet(c, px, py) != bits[px, py]) distance++;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = c;
                }
            }

            text.Append(best);
        }

        return text.ToString();
    }

    /// <summary>
    /// The angle ticks and the picture around the header: the wide 0-degree tick is ink at 0 degrees, blank at
    /// 2.5 degrees, the short 5-degree tick is ink; below the header the picture shows (left half black, right white).
    /// </summary>
    private static void AssertTicksAndPicture(RosettaRimHeader header, Func<double, double, bool> inkAt, double pictureRadiusMm)
    {
        RosettaRimHeaderMetrics m = header.Metrics;
        double r = header.OuterRadiusMm - (0.25 * m.TickZoneHeightMm);
        static (double, double) At(double radius, double degrees)
            => (radius * Math.Cos(degrees * Math.PI / 180.0), radius * Math.Sin(degrees * Math.PI / 180.0));
        (double x0, double y0) = At(r, 0);
        (double xGap, double yGap) = At(r, 2.5);
        (double x5, double y5) = At(r, 5);
        Assert.True(inkAt(x0, y0), "0-degree tick");
        Assert.False(inkAt(xGap, yGap), "blank between ticks");
        Assert.True(inkAt(x5, y5), "5-degree tick");
        Assert.True(inkAt(-pictureRadiusMm, 0), "picture's black half below the header");
        Assert.False(inkAt(pictureRadiusMm, 0), "picture's white half below the header");
    }

    private void AssertHeaderPlan(DiscArtPlan plan, double turnsPitchUm)
    {
        DiscArtRimHeaderPlan header = Assert.IsType<DiscArtRimHeaderPlan>(plan.RimHeader);
        Assert.Equal($"FAMILY PHOTOS  2026-11-18  SHA-256 {PictureSha16()}", header.HeaderText);
        Assert.Equal(PictureSha16(), header.Sha256Prefix);
        Assert.Equal(1.0, header.Scale);
        Assert.Equal(plan.PictureOuterRadiusMm, header.OuterRadiusMm, 9);
        Assert.Equal(turnsPitchUm, header.TrackPitchUm, 1);
        // Glyph rows are a whole, even number of turns, rounded down from the full-size 0.34 mm.
        Assert.Equal(0, header.TurnsPerGlyphRow % 2);
        Assert.Equal(header.TurnsPerGlyphRow * header.TrackPitchUm / 1000.0 * 8, header.GlyphCellHeightMm, 9);
        Assert.InRange(header.GlyphCellHeightMm, 2.72 - (2 * 8 * header.TrackPitchUm / 1000.0), 2.72);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // CD, BD and DVD: header present, read back as text
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cd_PictureTrack_HeaderReadsBackAsText()
    {
        string wav = P("ring.wav");
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Cd, RingCd, wav));
        AssertHeaderPlan(result.Plan, 10.0);
        Assert.Equal(34, result.Plan.RimHeader!.TurnsPerGlyphRow);

        string png = DiscArtImageBuilder.Preview(wav, P("ring-cd.png"), size: 2048);
        Func<double, double, bool> ink = InkFromPreview(PngRaster.Load(png), RingCd.OuterRadiusMm, centreOnPixelEdge: true);
        RosettaRimHeader header = HeaderFromPlan(result.Plan.RimHeader);

        Assert.Equal(result.Plan.RimHeader.HeaderText, header.HeaderText);
        Assert.Equal(header.HeaderText, DecodeHeaderText(header, ink));
        AssertTicksAndPicture(header, ink, pictureRadiusMm: 53.5);
    }

    [Fact]
    public void Bd_Picture_HeaderReadsBackAsText()
    {
        string iso = P("ring.iso");
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.BluRay, RingBd, iso));
        AssertHeaderPlan(result.Plan, 40.0);
        Assert.Equal(8, result.Plan.RimHeader!.TurnsPerGlyphRow);

        Func<double, double, bool> ink = InkFromBluRayImage(iso, RingBd, size: 2048);
        RosettaRimHeader header = HeaderFromPlan(result.Plan.RimHeader);

        Assert.Equal(header.HeaderText, DecodeHeaderText(header, ink));
        AssertTicksAndPicture(header, ink, pictureRadiusMm: 53.8);
    }

    [Fact]
    public void Dvd_Picture_HeaderReadsBackAsText()
    {
        string iso = P("ring-dvd.iso");
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, RingDvd, iso));
        AssertHeaderPlan(result.Plan, 20.0);
        Assert.Equal(16, result.Plan.RimHeader!.TurnsPerGlyphRow);

        string png = DiscArtImageBuilder.Preview(iso, P("ring-dvd.png"), size: 2048);
        Func<double, double, bool> ink = InkFromPreview(PngRaster.Load(png), RingDvd.OuterRadiusMm, centreOnPixelEdge: false);
        RosettaRimHeader header = HeaderFromPlan(result.Plan.RimHeader);

        Assert.Equal(header.HeaderText, DecodeHeaderText(header, ink));
        AssertTicksAndPicture(header, ink, pictureRadiusMm: 53.5);
    }

    /// <summary>
    /// What a drive reads from the BD image: each user byte XOR the scrambler gives the recorded payload byte, 0xAA
    /// (3T runs, dark) or 0x44 (6T runs, light), placed at the byte's physical position on the spiral.
    /// </summary>
    private static Func<double, double, bool> InkFromBluRayImage(string isoPath, CalibrationParameters parameters, int size)
    {
        BluRayDiscParameters disc = CalibrationTargetFile.BluRayParameters(parameters);
        var positions = new BluRayImagePayloadMapping(disc, new BluRayGray8Raster(1, 1, [255]), new BluRayRunLengthMapping());
        byte[] image = File.ReadAllBytes(isoPath);
        var dark = new int[size * size];
        var seen = new int[size * size];
        double half = size / 2.0;
        double outer = parameters.OuterRadiusMm;
        for (uint sector = 0; sector < disc.OutputSectors; sector++)
        {
            byte[] mask = BluRayScrambler.CreateMask(disc.GetPhysicalSectorNumber(sector), BluRayDiscParameters.BytesPerSector);
            int baseOffset = checked((int)sector * BluRayDiscParameters.BytesPerSector);
            for (int offset = 0; offset < BluRayDiscParameters.BytesPerSector; offset += 2)
            {
                byte payload = (byte)(image[baseOffset + offset] ^ mask[offset]);
                if (payload is not (BluRayRunLengthMapping.ThreeTByte or BluRayRunLengthMapping.SixTByte)) continue;
                BluRayTrackPoint point = positions.GetPosition(sector, offset).TrackPoint;
                int x = (int)(half + (half * point.Xmm / outer));
                int y = (int)(half + (half * -point.Ymm / outer)); // picture frame: y down
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                int index = (y * size) + x;
                seen[index]++;
                if (payload == BluRayRunLengthMapping.ThreeTByte) dark[index]++;
            }
        }

        return (xMm, yDownMm) =>
        {
            int cx = (int)(half + (half * xMm / outer));
            int cy = (int)(half + (half * yDownMm / outer));
            int d = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                    d += dark[(y * size) + x];
                    n += seen[(y * size) + x];
                }

            return n > 0 && 2 * d > n;
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ring width: smaller header, or refused with the reason
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cd_NarrowRing_GetsSmallerHeader_WithReadableCells()
    {
        // 57 to 58 mm at a real CD pitch (1.6 um): a quarter of the full depth.
        var disc = new CalibrationParameters(CalibrationDiscKind.Cd, 57, 58, 14_112, 1200);
        DiscArtPlan plan = DiscArtImageBuilder.Plan(Request(DiscArtMedium.Cd, disc, P("narrow.wav")));

        DiscArtRimHeaderPlan header = Assert.IsType<DiscArtRimHeaderPlan>(plan.RimHeader);
        Assert.Equal(0.25, header.Scale, 3);
        Assert.InRange(header.InnerRadiusMm, 57.0, 57.05);
        Assert.Equal(58.0, header.OuterRadiusMm, 6);
        Assert.True(header.GlyphCellWidthMm >= 0.050 && header.GlyphCellHeightMm >= 0.075);
        Assert.Equal(0, header.TurnsPerGlyphRow % 2);
        Assert.Equal(header.TurnsPerGlyphRow * header.TrackPitchUm * 8 / 1000.0, header.GlyphCellHeightMm, 9);
    }

    [Fact]
    public void Cd_RingTooNarrow_IsRefusedWithTheReason()
    {
        // 0.1 mm at the rim: a header with 50 x 75 um glyph cells needs about 0.114 mm.
        var disc = new CalibrationParameters(CalibrationDiscKind.Cd, 57.9, 58, 1_422, 1200);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DiscArtImageBuilder.Plan(Request(DiscArtMedium.Cd, disc, P("too-narrow.wav"))));

        Assert.Contains("rim header does not fit", ex.Message);
        Assert.Contains("0.100 mm wide", ex.Message);
        Assert.Contains("a CD needs glyph cells of at least 50 x 75 um", ex.Message);
        Assert.Contains("leave the rim header off", ex.Message);
        // Without the header the same disc is fine.
        Assert.Null(DiscArtImageBuilder.Plan(Request(DiscArtMedium.Cd, disc, P("too-narrow.wav"), header: false)).RimHeader);
    }

    [Fact]
    public void Bd_RingTooNarrow_IsRefusedWithTheReason()
    {
        // 0.02 mm at the rim at the BD pitch (0.32 um): 26 x 34 um cells need about 0.059 mm.
        var disc = new CalibrationParameters(CalibrationDiscKind.BluRay, 57.98, 58, 10_176,
            CalibrationParameters.BluRayChannelBitLengthNm);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DiscArtImageBuilder.Plan(Request(DiscArtMedium.BluRay, disc, P("too-narrow.iso"))));

        Assert.Contains("rim header does not fit", ex.Message);
        Assert.Contains("a BD needs glyph cells of at least 26 x 34 um", ex.Message);
    }

    [Fact]
    public void Coarse_track_pitch_is_refused_because_a_glyph_row_needs_two_turns()
    {
        // 3,000 sectors spread from 24.5 to 56.8 mm: 172 um between turns, more than half a 0.34 mm glyph row.
        var disc = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 3000, 1200);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => DiscArtImageBuilder.Plan(Request(DiscArtMedium.Cd, disc, P("coarse.wav"))));

        Assert.Contains("too coarse for the rim header", ex.Message);
        Assert.Contains("at least two turns", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Command line
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        var engine = new VirtualBurnEngine(recorder);
        var devices = new SingleDeviceService(recorder);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, devices, engine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WritePresets() => _shared.PresetsPath;

    [Fact]
    public async Task Cli_RimHeader_Switch_PlansTheHeader_OnABuiltInCd()
    {
        string wav = P("cli.wav");

        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", wav, "--disc-preset", "cd-80", "--rim-header", "--plan-only", "--json");

        Assert.True(code == 0, error + output);
        Assert.False(File.Exists(wav));
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement header = doc.RootElement.GetProperty("plan").GetProperty("rimHeader");
        // Title from the picture's name, today's date, the picture's SHA-256 prefix.
        Assert.Matches($@"^PICTURE  \d{{4}}-\d{{2}}-\d{{2}}  SHA-256 {PictureSha16()}$", header.GetProperty("text").GetString());
        Assert.Equal(1.0, header.GetProperty("scale").GetDouble());
        Assert.Equal(2.04, header.GetProperty("capitalHeightMm").GetDouble(), 1);
        Assert.True(header.GetProperty("turnsPerGlyphRow").GetInt32() >= 200);

        // Without the switch there is no header.
        (int plainCode, string plain, _) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", wav, "--disc-preset", "cd-80", "--plan-only", "--json");
        Assert.Equal(0, plainCode);
        using JsonDocument plainDoc = JsonDocument.Parse(plain);
        Assert.False(plainDoc.RootElement.GetProperty("plan").TryGetProperty("rimHeader", out JsonElement absent)
            && absent.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Cli_RimHeader_TooNarrow_IsRefusedWithTheReason()
    {
        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", P("narrow.wav"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "narrow-cd", "--rim-header", "--label", "Holiday", "--plan-only");

        Assert.NotEqual(0, code);
        Assert.Contains("rim header does not fit", error + output);
        Assert.Contains("50 x 75 um", error + output);
    }

    private sealed class SingleDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
