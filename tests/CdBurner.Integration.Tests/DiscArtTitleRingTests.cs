using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The title ring: the title and date in capitals around the outer edge of a DVD picture's drawn area, readable without
/// a microscope. Expected sectors come from the spiral itself: with a constant track pitch the sector at radius r is
/// N (r^2 - r0^2) / (R^2 - r0^2) on a disc of N sectors drawn from r0 to R, and the ring's inner edge rounds outward to a
/// whole 16-sector ECC block (ECMA-267). Expected letters come from the Spleen 5x8 font, laid out by the ring's stated
/// rules: 0.25 mm blank at each edge, the 8 font rows filling the rest, square font pixels at the middle of the text,
/// the text centred on the top of the disc (270 degrees clockwise from the right, seen from the read side), top towards
/// the rim, reading clockwise.
/// </summary>
public sealed class DiscArtTitleRingTests : IDisposable
{
    private const int Sector = 2048;
    private const double DegreesToRadians = Math.PI / 180.0;

    // A short simulated DVD: the track runs from 50 to 58 mm with turns about 8.8 um apart (a DVD's are 0.74 um), so a
    // 3 mm ring is about 23,500 sectors (47 MB). Angles and radii depend on the geometry in the same way at either spacing.
    private const int N = 60_000;
    private const double R0 = 50;
    private const double R1 = 58;
    private static readonly CalibrationParameters ShortDvd = new(CalibrationDiscKind.Dvd, R0, R1, N, 133.3);
    private static readonly DateOnly Date = new(2026, 11, 17);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-title-ring-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public DiscArtTitleRingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>An all-black picture: everywhere it is drawn, the read-back is dark.</summary>
    private string WriteBlackPicture()
    {
        string path = P("black.png");
        if (File.Exists(path)) return path;
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        PngRaster.SaveBgra32(path, new BgraRaster(size, size, pixels));
        return path;
    }

    private string WriteFiles()
    {
        string folder = P("files");
        if (Directory.Exists(folder)) return folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.TXT"), "files inside, title ring outside");
        return folder;
    }

    private static double SectorAtRadius(double r) => N * (r * r - R0 * R0) / (R1 * R1 - R0 * R0);

    private static double RadiusAtSector(long lba) => Math.Sqrt(R0 * R0 + lba / (double)N * (R1 * R1 - R0 * R0));

    private static long AlignUp(double lba) => (long)Math.Ceiling(Math.Ceiling(lba) / 16) * 16;

    private static bool AllZero(byte[] image, long first, long end)
        => image.AsSpan(checked((int)(first * Sector)), checked((int)((end - first) * Sector))).IndexOfAnyExcept((byte)0) < 0;

    private static bool SameBytes(byte[] a, byte[] b, long first, long end)
    {
        int offset = checked((int)(first * Sector));
        int length = checked((int)((end - first) * Sector));
        return a.AsSpan(offset, length).SequenceEqual(b.AsSpan(offset, length));
    }

    private static (double X, double YDown) Polar(double radiusMm, double clockwiseDegrees)
        => (radiusMm * Math.Cos(clockwiseDegrees * DegreesToRadians), radiusMm * Math.Sin(clockwiseDegrees * DegreesToRadians));

    /// <summary>Majority of the 3x3 read-back pixels around a point; the preview spans [-R1, R1] mm on both axes.</summary>
    private static bool IsDarkAt(BgraRaster picture, double radiusMm, double clockwiseDegrees)
    {
        (double x, double yDown) = Polar(radiusMm, clockwiseDegrees);
        double centre = (picture.Width - 1) / 2.0;
        int px = (int)Math.Round(centre + (centre * x / R1));
        int py = (int)Math.Round(centre + (centre * yDown / R1));
        int dark = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (picture.Pixels[(((py + dy) * picture.Width) + px + dx) * 4] < 128) dark++;
        return dark >= 5;
    }

    private DiscArtRequest Request(string output, string? picture = null, DiscArtTitleRingRequest? ring = null,
        string? files = null, double? artInner = null, double? artOuter = null)
        => new(DiscArtMedium.Dvd, picture ?? string.Empty, ShortDvd, output, FilesFolder: files,
            ArtInnerRadiusMm: artInner, ArtOuterRadiusMm: artOuter, TitleRing: ring);

    [Fact]
    public void Read_back_preview_of_a_ring_alone_shows_the_title_and_date_letter_for_letter()
    {
        // 55 mm (58 - 3) lies at sector 36,458.3; outward to a block gives 36,464.
        long expectedStart = AlignUp(SectorAtRadius(55));
        Assert.Equal(36_464L, expectedStart);

        var stopwatch = Stopwatch.StartNew();
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(P("ring.iso"),
            ring: new DiscArtTitleRingRequest("Summer trip", Date)));
        _output.WriteLine($"Generated {result.BytesWritten:N0} bytes in {stopwatch.Elapsed.TotalSeconds:0.0} s.");

        DiscArtTitleRingPlan ring = result.Plan.TitleRing!;
        Assert.Equal("SUMMER TRIP  2026-11-17", ring.Text);
        Assert.False(ring.WithPicture);
        Assert.Equal((expectedStart, (long)N), (ring.StartLba, ring.EndLba));
        Assert.Equal((expectedStart, (long)N, (long)N), (result.Plan.PictureStartLba, result.Plan.PictureEndLba, result.Plan.TotalSectors));
        Assert.Equal(RadiusAtSector(expectedStart), ring.InnerRadiusMm, 2);
        Assert.Equal(R1, ring.OuterRadiusMm, 6);

        byte[] image = File.ReadAllBytes(P("ring.iso"));
        Assert.Equal((long)N * Sector, image.Length);
        Assert.True(AllZero(image, 0, expectedStart), "no picture: zeros up to the ring");

        stopwatch.Restart();
        DiscArtImageBuilder.Preview(P("ring.iso"), P("ring.png"), 2048);
        _output.WriteLine($"Preview in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        BgraRaster readback = PngRaster.Load(P("ring.png"));

        // The ring's layout from its stated rules and its radii (not from the code that draws it).
        double pixelMm = (ring.OuterRadiusMm - ring.InnerRadiusMm - 0.5) / 8;
        double textTopMm = ring.OuterRadiusMm - 0.25;
        double midMm = textTopMm - (4 * pixelMm);
        double pixelDegrees = pixelMm / midMm / DegreesToRadians;
        double startDegrees = 270 - (ring.Text.Length * 5 * pixelDegrees / 2);
        Assert.Equal(pixelMm, ring.GlyphPixelMm, 9);
        Assert.Equal(6 * pixelMm, ring.CapitalHeightMm, 9);
        Assert.InRange(ring.CapitalHeightMm, 1.7, 2.0);  // a 3 mm ring: capitals just under 2 mm
        Assert.Equal(startDegrees, ring.TextStartAngleDegrees, 9);

        Spleen5x8Font font = Spleen5x8Font.BuiltIn;
        int compared = 0;
        int ink = 0;
        for (int column = 0; column < ring.Text.Length; column++)
        {
            for (int px = 0; px < 5; px++)
            {
                double degrees = startDegrees + (((column * 5) + px + 0.5) * pixelDegrees);
                for (int py = 0; py < 8; py++)
                {
                    bool expected = font.IsPixelSet(ring.Text[column], px, py);
                    bool actual = IsDarkAt(readback, textTopMm - ((py + 0.5) * pixelMm), degrees);
                    Assert.True(expected == actual,
                        $"Column {column} ('{ring.Text[column]}'), font pixel ({px},{py}) at {degrees:F2} degrees: expected {(expected ? "ink" : "blank")}.");
                    compared++;
                    if (expected) ink++;
                }
            }
        }

        _output.WriteLine($"{compared} font pixels match, {ink} of them ink; {pixelMm:F3} mm per font pixel.");
        Assert.True(ink > 200, "the text has strokes");

        // Blank everywhere else in the ring: the two edge gaps all around, and the arc the text does not cover.
        for (double degrees = 0; degrees < 360; degrees += 0.5)
        {
            Assert.False(IsDarkAt(readback, ring.OuterRadiusMm - 0.12, degrees), $"rim-side gap at {degrees} degrees");
            Assert.False(IsDarkAt(readback, ring.InnerRadiusMm + 0.12, degrees), $"inner gap at {degrees} degrees");
        }

        double endDegrees = startDegrees + (ring.Text.Length * 5 * pixelDegrees);
        for (double degrees = endDegrees + 1; degrees < startDegrees + 359; degrees += 0.5)
        {
            Assert.False(IsDarkAt(readback, textTopMm - (3.5 * pixelMm), degrees), $"no text at {degrees % 360:F1} degrees");
        }
    }

    [Fact]
    public void Picture_stops_where_the_ring_starts_and_neither_changes_the_other()
    {
        string picture = WriteBlackPicture();
        var titleRing = new DiscArtTitleRingRequest("Holiday", Date);
        DiscArtResult both = DiscArtImageBuilder.Generate(Request(P("both.iso"), picture, titleRing));
        DiscArtTitleRingPlan ring = both.Plan.TitleRing!;
        long ringStart = ring.StartLba;
        Assert.True(ring.WithPicture);
        Assert.Equal(AlignUp(SectorAtRadius(55)), ringStart);
        Assert.Equal((0L, (long)N), (both.Plan.PictureStartLba, both.Plan.PictureEndLba));

        // The ring's sectors are the same bytes as a ring alone: the picture never reaches into them.
        DiscArtImageBuilder.Generate(Request(P("ring.iso"), ring: titleRing));
        // The picture's sectors are the same bytes as the picture alone in a band that ends where the ring starts.
        DiscArtResult pictureOnly = DiscArtImageBuilder.Generate(Request(P("picture.iso"), picture, artOuter: ring.InnerRadiusMm));
        Assert.Equal(ringStart, pictureOnly.Plan.PictureEndLba);

        byte[] a = File.ReadAllBytes(P("both.iso"));
        byte[] r = File.ReadAllBytes(P("ring.iso"));
        byte[] p = File.ReadAllBytes(P("picture.iso"));
        Assert.Equal(ringStart * Sector, p.Length);
        Assert.True(SameBytes(a, r, ringStart, N), "ring sectors equal the ring alone");
        Assert.True(SameBytes(a, p, 0, ringStart), "picture sectors equal the picture alone");
        Assert.False(SameBytes(a, r, ringStart - 16, ringStart), "the block before the ring is picture, not blank");

        // Read back: dark picture right up to the ring, blank edge gaps all around, letters in the ring.
        DiscArtImageBuilder.Preview(P("both.iso"), P("both.png"), 2048);
        BgraRaster readback = PngRaster.Load(P("both.png"));
        int darkInside = 0;
        int samples = 0;
        for (double degrees = 0; degrees < 360; degrees += 1)
        {
            samples++;
            if (IsDarkAt(readback, ring.InnerRadiusMm - 0.3, degrees)) darkInside++;
            Assert.False(IsDarkAt(readback, ring.InnerRadiusMm + 0.12, degrees), $"inner gap at {degrees} degrees");
            Assert.False(IsDarkAt(readback, ring.OuterRadiusMm - 0.12, degrees), $"rim-side gap at {degrees} degrees");
        }

        Assert.True(darkInside >= samples - 2, $"the black picture reaches the ring ({darkInside} of {samples} dark)");
        // 'H', the first letter, has its left stroke in font column 0 on rows 1 to 6.
        double pixelMm = ring.GlyphPixelMm;
        double midMm = ring.OuterRadiusMm - 0.25 - (4 * pixelMm);
        double firstStroke = ring.TextStartAngleDegrees + (0.5 * pixelMm / midMm / DegreesToRadians);
        Assert.True(Spleen5x8Font.BuiltIn.IsPixelSet('H', 0, 3));
        Assert.True(IsDarkAt(readback, ring.OuterRadiusMm - 0.25 - (3.5 * pixelMm), firstStroke));
    }

    [Fact]
    public void Ring_with_inner_files_and_an_art_band_keeps_the_files_and_ends_at_the_band()
    {
        string picture = WriteBlackPicture();
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(P("files.iso"), picture,
            new DiscArtTitleRingRequest("Files", Date, HeightMm: 2.5), WriteFiles(), artInner: 52, artOuter: 57));
        DiscArtPlan plan = result.Plan;
        DiscArtTitleRingPlan ring = plan.TitleRing!;

        long bandEnd = (long)Math.Floor(SectorAtRadius(57)) / 16 * 16;
        Assert.Equal(bandEnd, ring.EndLba);
        Assert.Equal(bandEnd, plan.TotalSectors);
        Assert.Equal(AlignUp(SectorAtRadius(RadiusAtSector(bandEnd) - 2.5)), ring.StartLba);
        Assert.True(plan.PictureStartLba < ring.StartLba, "room for the picture between the files and the ring");
        Assert.True(plan.FilesLayout!.GapEndLba <= plan.PictureStartLba);
        Assert.Equal(bandEnd * Sector, new FileInfo(P("files.iso")).Length);

        using IsoImageReader reader = IsoImageReader.FromFile(P("files.iso"));
        Assert.Equal("files inside, title ring outside", System.Text.Encoding.ASCII.GetString(reader.ReadFileBytes("/README.TXT")));

        // A ring alone with the files: zeros from the files' gap to the ring.
        DiscArtResult alone = DiscArtImageBuilder.Generate(Request(P("files-ring.iso"), null,
            new DiscArtTitleRingRequest("Files", Date), WriteFiles()));
        PictureDiscLayout layout = alone.Plan.FilesLayout!;
        Assert.Equal(alone.Plan.TitleRing!.StartLba, layout.PictureStartLba);
        Assert.True(AllZero(File.ReadAllBytes(P("files-ring.iso")), layout.VolumeEndLba, layout.PictureStartLba));
    }

    [Fact]
    public void With_outer_files_the_ring_ends_where_the_picture_zone_ends_before_the_gap()
    {
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(P("outer.iso"), WriteBlackPicture(),
            new DiscArtTitleRingRequest("Outer", Date), WriteFiles()) with { FilesPlacement = PictureDiscFilesPlacement.OuterFiles });
        DiscArtPlan plan = result.Plan;
        PictureDiscLayout layout = plan.FilesLayout!;
        DiscArtTitleRingPlan ring = plan.TitleRing!;

        Assert.Equal(layout.PictureEndLba, ring.EndLba);
        Assert.True(ring.EndLba <= layout.GapStartLba, "the ring ends before the gap and the files");
        Assert.Equal(AlignUp(SectorAtRadius(RadiusAtSector(ring.EndLba) - 3)), ring.StartLba);
        Assert.Equal(0, plan.PictureStartLba);
        Assert.Equal((long)layout.TotalSectors * Sector, new FileInfo(P("outer.iso")).Length);

        // The ring's sectors match a ring alone with the same outer files: the ring alone writes zeros before it.
        DiscArtResult alone = DiscArtImageBuilder.Generate(Request(P("outer-ring.iso"), null,
            new DiscArtTitleRingRequest("Outer", Date), WriteFiles()) with { FilesPlacement = PictureDiscFilesPlacement.OuterFiles });
        Assert.Equal(ring.StartLba, alone.Plan.TitleRing!.StartLba);
        byte[] a = File.ReadAllBytes(P("outer.iso"));
        byte[] b = File.ReadAllBytes(P("outer-ring.iso"));
        Assert.True(SameBytes(a, b, ring.StartLba, ring.EndLba));
        Assert.True(AllZero(b, 0, ring.StartLba));
    }

    [Fact]
    public void Title_ring_is_refused_with_a_reason_where_it_cannot_be_drawn()
    {
        string picture = WriteBlackPicture();
        var ring = new DiscArtTitleRingRequest("X", Date);
        Assert.Contains("DVD picture discs only", Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(
            new DiscArtRequest(DiscArtMedium.BluRay, picture, new CalibrationParameters(CalibrationDiscKind.BluRay, R0, R1, N,
                CalibrationParameters.BluRayChannelBitLengthNm), P("x.iso"), TitleRing: ring))).Message);
        Assert.Contains("DVD picture discs only", Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(
            new DiscArtRequest(DiscArtMedium.Cd, picture, new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 6000, 1200),
                P("x.wav"), TitleRing: ring))).Message);

        foreach (double height in new[] { 1.9, 10.1, double.NaN })
        {
            Assert.Contains("height must be 2 to 10 mm", Assert.Throws<ArgumentException>(() =>
                DiscArtImageBuilder.Plan(Request(P("x.iso"), picture, ring with { HeightMm = height }))).Message);
        }

        // A 3 mm ring reaches below a band that starts at 55.2 or 55.5 mm.
        Assert.Contains("leaves no room for the picture", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(P("x.iso"), picture, ring, artInner: 55.2))).Message);
        Assert.Contains("would reach below the drawn area", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(P("x.iso"), null, ring, artInner: 55.5))).Message);

        Assert.Contains("no text", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(P("x.iso"), picture, new DiscArtTitleRingRequest("   ", null)))).Message);
        Assert.Contains("The picture was not found", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(P("x.iso")))).Message);
        Assert.Contains("no picture to place", Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(
            Request(P("x.iso"), ring: ring) with { Transform = new DiscArtTransform(Scale: 2) })).Message);
        Assert.False(File.Exists(P("x.iso")));
    }

    [Fact]
    public void A_long_title_is_cut_short_and_the_date_is_kept()
    {
        string title = string.Concat(Enumerable.Repeat("A very long title ", 40));
        DiscArtTitleRingPlan ring = DiscArtImageBuilder.Plan(Request(P("x.iso"), ring: new DiscArtTitleRingRequest(title, Date))).TitleRing!;
        Assert.EndsWith("  2026-11-17", ring.Text);
        Assert.StartsWith("A VERY LONG TITLE A VERY", ring.Text);
        // The text stays inside one turn.
        Assert.InRange(ring.TextSpanDegrees, 340, 360);
    }

    // Command line ------------------------------------------------------------------------------------------------

    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 4,
              "cdPresets": [],
              "dvdPresets": [
                { "id": "short-dvd", "displayName": "Short DVD", "description": "test", "totalSectors": {{N}},
                  "innerRadiusMm": {{R0}}, "outerRadiusMm": {{R1}}, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": []
            }
            """);
        return path;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, new SingleDeviceService(recorder), new VirtualBurnEngine(recorder));
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Cli_plans_and_writes_a_title_ring_without_a_picture()
    {
        string[] common = ["disc-art", "generate", "--media", "dvd", "--output", P("cli.iso"),
            "--disc-presets-file", WritePresets(), "--disc-preset", "short-dvd", "--title-ring", "Summer trip",
            "--title-ring-date", "2026-11-17"];

        (int code, string json, string err) = await RunAsync([.. common, "--plan-only", "--json"]);
        Assert.True(code == 0, err + json);
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement ring = doc.RootElement.GetProperty("plan").GetProperty("titleRing");
            Assert.Equal("SUMMER TRIP  2026-11-17", ring.GetProperty("text").GetString());
            Assert.Equal(36_464, ring.GetProperty("startLba").GetInt64());
            Assert.Equal(N, ring.GetProperty("endLba").GetInt64());
            Assert.False(ring.GetProperty("withPicture").GetBoolean());
        }

        (int textCode, string text, _) = await RunAsync([.. common, "--plan-only", "--title-ring-height-mm", "2.5", "--title-ring-date", "none"]);
        Assert.Equal(0, textCode);
        Assert.Contains("Title ring  : \"SUMMER TRIP\"", text);
        Assert.Contains("no picture", text);
        Assert.False(File.Exists(P("cli.iso")));

        (int genCode, string genOut, string genErr) = await RunAsync(common);
        Assert.True(genCode == 0, genErr + genOut);
        Assert.Equal((long)N * Sector, new FileInfo(P("cli.iso")).Length);

        (int badDate, string badOut, string badErr) = await RunAsync([.. common, "--plan-only", "--title-ring-date", "17/11/2026"]);
        Assert.NotEqual(0, badDate);
        Assert.Contains("yyyy-MM-dd", badOut + badErr);

        (int orphan, string orphanOut, string orphanErr) = await RunAsync("disc-art", "generate", "--media", "dvd", "--picture",
            WriteBlackPicture(), "--output", P("o.iso"), "--disc-presets-file", WritePresets(), "--disc-preset", "short-dvd",
            "--title-ring-height-mm", "3", "--plan-only");
        Assert.NotEqual(0, orphan);
        Assert.Contains("need --title-ring", orphanOut + orphanErr);

        (int noPicture, string noPictureOut, string noPictureErr) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--output", P("o.iso"), "--disc-presets-file", WritePresets(), "--disc-preset", "short-dvd", "--plan-only");
        Assert.NotEqual(0, noPicture);
        Assert.Contains("--picture", noPictureOut + noPictureErr);
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
