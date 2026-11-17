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
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The art band: a picture drawn only between two radii, with zero sectors before it and an image that ends at its outer
/// edge. Expected sectors come from the spiral itself: with a constant track pitch the track length inside radius r grows
/// with the ring's area, so the sector at radius r is N (r^2 - r0^2) / (R^2 - r0^2) on a disc of N sectors drawn from r0
/// to R. The band rounds inward to whole ECC blocks (DVD, 16 sectors, ECMA-267) or clusters (BD, 32 sectors).
/// </summary>
public sealed class DiscArtBandTests : IDisposable
{
    private const int Sector = 2048;
    private const int N = 4096;
    private const double R0 = 24;
    private const double R1 = 58;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-band-" + Guid.NewGuid().ToString("N"));

    private static readonly CalibrationParameters TinyDvd = new(CalibrationDiscKind.Dvd, R0, R1, N, 133.3);
    private static readonly CalibrationParameters TinyBd = new(CalibrationDiscKind.BluRay, R0, R1, N,
        CalibrationParameters.BluRayChannelBitLengthNm);
    private static readonly CalibrationParameters TinyCd = new(CalibrationDiscKind.Cd, 24.5, 56.8, 6000, 1200);

    public DiscArtBandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string WritePicture()
    {
        string path = P("picture.png");
        if (File.Exists(path)) return path;
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < size / 2 ? 0 : 255);
                pixels[o + 3] = 255;
            }

        PngRaster.SaveBgra32(path, new BgraRaster(size, size, pixels));
        return path;
    }

    private string WriteFiles()
    {
        string folder = P("files");
        if (Directory.Exists(folder)) return folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.TXT"), "files inside, picture band outside");
        byte[] data = new byte[90_000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(Path.Combine(folder, "DATA.BIN"), data);
        return folder;
    }

    private DiscArtRequest Request(DiscArtMedium medium, string output, double? inner = null, double? outer = null,
        string? files = null, PictureDiscFilesPlacement placement = PictureDiscFilesPlacement.InnerFiles) => new(
        medium, WritePicture(), medium switch
        {
            DiscArtMedium.Cd => TinyCd,
            DiscArtMedium.Dvd => TinyDvd,
            _ => TinyBd,
        }, output, FilesFolder: files, FilesPlacement: placement, ArtInnerRadiusMm: inner, ArtOuterRadiusMm: outer);

    /// <summary>Sector at radius r on a constant-pitch spiral of N sectors from R0 to R1 (fractional).</summary>
    private static double SectorAtRadius(double r) => N * (r * r - R0 * R0) / (R1 * R1 - R0 * R0);

    private static double RadiusAtSector(long lba) => Math.Sqrt(R0 * R0 + lba / (double)N * (R1 * R1 - R0 * R0));

    private static long AlignUp(double lba, int align) => (long)Math.Ceiling(Math.Ceiling(lba) / align) * align;

    private static long AlignDown(double lba, int align) => (long)Math.Floor(lba) / align * align;

    private static bool AllZero(byte[] image, long first, long end)
        => image.AsSpan(checked((int)(first * Sector)), checked((int)((end - first) * Sector))).IndexOfAnyExcept((byte)0) < 0;

    private static bool SameBytes(byte[] a, byte[] b, long first, long end)
    {
        int offset = checked((int)(first * Sector));
        int length = checked((int)((end - first) * Sector));
        return a.AsSpan(offset, length).SequenceEqual(b.AsSpan(offset, length));
    }

    [Fact]
    public void Dvd_band_without_files_has_zeros_before_it_the_full_disc_picture_in_it_and_ends_at_its_outer_edge()
    {
        // 35 mm lies at sector 953.4, 48 mm at 2538.7: inward to blocks gives 960 and 2528.
        long expectedStart = AlignUp(SectorAtRadius(35), 16);
        long expectedEnd = AlignDown(SectorAtRadius(48), 16);
        Assert.Equal((960L, 2528L), (expectedStart, expectedEnd));

        DiscArtResult full = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, P("full.iso")));
        DiscArtResult band = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, P("band.iso"), 35, 48));

        Assert.Equal(expectedStart, band.Plan.PictureStartLba);
        Assert.Equal(expectedEnd, band.Plan.PictureEndLba);
        Assert.Equal(expectedEnd, band.Plan.TotalSectors);
        Assert.Equal(expectedEnd * Sector, new FileInfo(P("band.iso")).Length);

        byte[] a = File.ReadAllBytes(P("full.iso"));
        byte[] b = File.ReadAllBytes(P("band.iso"));
        Assert.True(AllZero(b, 0, expectedStart), "sectors before the band are zero");
        Assert.True(SameBytes(a, b, expectedStart, expectedEnd), "the band holds the full-disc picture at the same sectors");
        Assert.False(AllZero(a, 0, expectedStart), "the full disc draws there, so the zeros are the band's doing");

        DiscArtBand info = band.Plan.Band!;
        Assert.Equal(35, info.RequestedInnerRadiusMm);
        Assert.Equal(48, info.RequestedOuterRadiusMm);
        Assert.Equal(RadiusAtSector(expectedStart), info.InnerRadiusMm, 2);
        Assert.Equal(RadiusAtSector(expectedEnd), info.OuterRadiusMm, 2);
        Assert.InRange(info.InnerRadiusMm, 35, 35.2);
        Assert.InRange(info.OuterRadiusMm, 47.8, 48);
        Assert.Equal(N, info.FullImageSectors);
        Assert.Equal(N - expectedEnd, info.SavedSectors);
        // DVD 1x is 1,385,000 bytes of user data a second (ECMA-267).
        Assert.Equal((N - expectedEnd) * 2048.0 / 1_385_000, info.SavedSecondsAt1x, 9);
        Assert.Null(full.Plan.Band);

        // The description keeps the band and the read-back preview of the shorter image still renders.
        Assert.Contains("\"band\"", File.ReadAllText(band.SidecarPath));
        DiscArtImageBuilder.Preview(P("band.iso"), P("band.png"), 128);
        Assert.True(File.Exists(P("band.png")));
    }

    [Fact]
    public void Dvd_band_with_inner_files_keeps_the_files_and_zeros_up_to_the_band()
    {
        // 41 mm at sector 1623.4 -> 1632; 50 mm at 2826.6 -> 2816.
        long expectedStart = AlignUp(SectorAtRadius(41), 16);
        long expectedEnd = AlignDown(SectorAtRadius(50), 16);
        Assert.Equal((1632L, 2816L), (expectedStart, expectedEnd));

        DiscArtResult full = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, P("full.iso"), files: WriteFiles()));
        DiscArtResult band = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, P("band.iso"), 41, 50, WriteFiles()));
        PictureDiscLayout layout = band.Plan.FilesLayout!;

        Assert.Equal(expectedEnd * Sector, new FileInfo(P("band.iso")).Length);
        Assert.Equal((expectedStart, expectedEnd, expectedEnd),
            ((long)layout.PictureStartLba, (long)layout.PictureEndLba, (long)layout.TotalSectors));
        Assert.True(layout.GapSectorCount >= PictureDiscLayoutPlanner.MinimumGapSectors);

        byte[] a = File.ReadAllBytes(P("full.iso"));
        byte[] b = File.ReadAllBytes(P("band.iso"));
        // The file system is the same size as without a band (its bytes carry the build time, so they are not compared).
        Assert.Equal(full.Plan.FilesLayout!.VolumeEndLba, layout.VolumeEndLba);
        Assert.True(AllZero(b, layout.VolumeEndLba, expectedStart), "zeros from the end of the files to the band");
        Assert.True(SameBytes(a, b, expectedStart, expectedEnd), "the band holds the full-disc picture at the same sectors");
        Assert.True(full.Plan.PictureStartLba < expectedStart);

        using IsoImageReader reader = IsoImageReader.FromFile(P("band.iso"));
        Assert.Equal("files inside, picture band outside", System.Text.Encoding.ASCII.GetString(reader.ReadFileBytes("/README.TXT")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(WriteFiles(), "DATA.BIN")), reader.ReadFileBytes("/DATA.BIN"));
    }

    [Fact]
    public void BluRay_band_ends_at_a_cluster_and_matches_the_full_disc_picture()
    {
        DiscArtResult full = DiscArtImageBuilder.Generate(Request(DiscArtMedium.BluRay, P("full.iso")));
        DiscArtResult band = DiscArtImageBuilder.Generate(Request(DiscArtMedium.BluRay, P("band.iso"), 35, 48));
        long start = band.Plan.PictureStartLba;
        long end = band.Plan.PictureEndLba;

        Assert.Equal(0, start % 32);
        Assert.Equal(0, end % 32);
        // The BD spiral counts run-in/run-out spacing per cluster, so allow one cluster against the area formula.
        Assert.InRange(start, AlignUp(SectorAtRadius(35), 32) - 32, AlignUp(SectorAtRadius(35), 32) + 32);
        Assert.InRange(end, AlignDown(SectorAtRadius(48), 32) - 32, AlignDown(SectorAtRadius(48), 32) + 32);
        Assert.InRange(band.Plan.Band!.InnerRadiusMm, 35, 36);
        Assert.InRange(band.Plan.Band!.OuterRadiusMm, 47, 48);
        Assert.Equal(end * Sector, new FileInfo(P("band.iso")).Length);
        Assert.Equal((N - end) * 2048.0 / 4_495_500, band.Plan.Band!.SavedSecondsAt1x, 9);

        byte[] a = File.ReadAllBytes(P("full.iso"));
        byte[] b = File.ReadAllBytes(P("band.iso"));
        Assert.True(AllZero(b, 0, start));
        Assert.True(SameBytes(a, b, start, end));
        _ = full;
    }

    [Fact]
    public void Only_an_outer_radius_draws_from_the_start_and_only_an_inner_radius_runs_to_the_end()
    {
        DiscArtPlan outerOnly = DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("o.iso"), outer: 48));
        Assert.Equal((0L, 2528L, 2528L), (outerOnly.PictureStartLba, outerOnly.PictureEndLba, outerOnly.TotalSectors));

        DiscArtPlan innerOnly = DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("i.iso"), inner: 35));
        Assert.Equal((960L, (long)N, (long)N), (innerOnly.PictureStartLba, innerOnly.PictureEndLba, innerOnly.TotalSectors));
        Assert.Equal(0, innerOnly.Band!.SavedSectors);
    }

    [Fact]
    public void Bands_over_the_files_or_too_narrow_are_refused_with_a_reason()
    {
        string files = WriteFiles();
        var overlap = Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 30, 50, files)));
        Assert.Contains("overlaps the files and the 2 MiB gap", overlap.Message);

        var overlapOuter = Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), outer: 30, files: files)));
        Assert.Contains("overlaps the files", overlapOuter.Message);

        // 40 to 40.1 mm holds about 7 sectors of a 4,096-sector disc: less than one 16-sector block.
        Assert.True(SectorAtRadius(40.1) - SectorAtRadius(40) < 16);
        var narrow = Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 40, 40.1)));
        Assert.Contains("narrower than one 16-sector ECC block", narrow.Message);

        var narrowBd = Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.BluRay, P("x.iso"), 40, 40.1)));
        Assert.Contains("narrower than one 32-sector cluster", narrowBd.Message);

        Assert.Contains("inner radius must be smaller", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 50, 40))).Message);
        Assert.Contains("must lie on the drawn disc", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 20, 40))).Message);
        Assert.Contains("must lie on the drawn disc", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 30, double.NaN))).Message);
        Assert.Contains("DVD and BD", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Cd, P("x.wav"), 30, 40))).Message);
        Assert.Contains("outer end", Assert.Throws<ArgumentException>(() =>
            DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, P("x.iso"), 30, 40, files, PictureDiscFilesPlacement.OuterFiles))).Message);
        Assert.False(File.Exists(P("x.iso")));
    }

    // Command line ------------------------------------------------------------------------------------------------

    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 4,
              "cdPresets": [],
              "dvdPresets": [
                { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": []
            }
            """);
        return path;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, new SingleDeviceService(recorder), new VirtualBurnEngine(recorder));
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Cli_plan_reports_the_band_radii_and_the_burn_saved_and_refuses_a_bad_band()
    {
        string[] common = ["disc-art", "generate", "--media", "dvd", "--picture", WritePicture(), "--output", P("cli.iso"),
            "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd", "--plan-only"];

        (int code, string json, string err) = await RunAsync([.. common, "--art-inner-radius-mm", "35", "--art-outer-radius-mm", "48", "--json"]);
        Assert.True(code == 0, err + json);
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            JsonElement band = doc.RootElement.GetProperty("plan").GetProperty("band");
            Assert.Equal(960, band.GetProperty("startLba").GetInt64());
            Assert.Equal(2528, band.GetProperty("endLba").GetInt64());
            Assert.Equal(N - 2528, band.GetProperty("savedSectors").GetInt64());
            Assert.Equal(2528, doc.RootElement.GetProperty("plan").GetProperty("totalSectors").GetInt64());
        }

        (int textCode, string text, _) = await RunAsync([.. common, "--art-outer-radius-mm", "48"]);
        Assert.Equal(0, textCode);
        Assert.Contains("Art band", text);
        Assert.Contains("Burn saved  : 1,568 of 4,096 sectors not written", text);
        Assert.False(File.Exists(P("cli.iso")));

        (int badCode, string badOut, string badErr) = await RunAsync([.. common, "--art-inner-radius-mm", "40", "--art-outer-radius-mm", "40.1"]);
        Assert.NotEqual(0, badCode);
        Assert.Contains("narrower than one 16-sector ECC block", badOut + badErr);

        (int nanCode, _, _) = await RunAsync([.. common, "--art-outer-radius-mm", "abc"]);
        Assert.NotEqual(0, nanCode);
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
