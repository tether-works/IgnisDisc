using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The Disc Art screen: an image generated from the screen must be byte for byte the image
/// <c>ignisdisc-cli disc-art generate</c> makes from the same choices; the placement preview must put the picture where
/// <see cref="DiscArtTransform"/> says (x right, y down, positive rotation clockwise); a generated DVD image burns and
/// verifies on the simulated drive. Tiny discs (a few thousand sectors) keep each test to seconds. No physical drive.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class DiscArtViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-view-" + Guid.NewGuid().ToString("N"));

    public DiscArtViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>One tiny disc of each type (the same sizes the command-line tests use).</summary>
    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 4,
              "cdPresets": [
                { "id": "tiny-cd", "displayName": "Tiny CD", "description": "test", "sectors": 6000,
                  "innerRadiusMm": 24.5, "outerRadiusMm": 56.8, "linearVelocityMmPerSecond": 1200 }
              ],
              "dvdPresets": [
                { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 },
                { "id": "small-dvd", "displayName": "Small DVD", "description": "test", "totalSectors": 32768,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": [
                { "id": "tiny-bd", "displayName": "Tiny BD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58 }
              ]
            }
            """);
        return path;
    }

    /// <summary>64 x 64: left half black, right half white.</summary>
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

    private DiscArtViewModel NewPage(Func<string, Task<BurnOperationResult>>? burn = null, string? store = null)
        => new(burn, WritePresets(), store ?? P("no-calibrations.json"));

    private static async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // Only "generate" runs here: no drive is opened. The virtual device list is there because the runner needs one.
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, devices, new VirtualBurnEngine());
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<DiscArtResult> GenerateFromPage(DiscArtViewModel page, string output)
    {
        page.OutputPath = output;
        await page.GenerateAsync();
        Assert.True(page.LastResult is not null, page.StatusMessage);
        return page.LastResult!;
    }

    [Fact]
    public async Task Dvd_placed_picture_in_an_art_band_equals_the_cli_bytes()
    {
        DiscArtViewModel page = NewPage();
        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("tiny-dvd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.Scale = 0.8;
        page.OffsetXMm = 5;
        page.OffsetYMm = -3;
        page.RotationDegrees = 30;
        page.FitToRing = true;
        page.ArtInnerRadiusText = "30";
        page.ArtOuterRadiusText = "50";
        page.ApplyTone(ToneProfile.Photo);
        DiscArtResult fromPage = await GenerateFromPage(page, P("page.iso"));

        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", P("cli.iso"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-dvd", "--tone", "photo", "--scale", "0.8", "--offset-x-mm", "5",
            "--offset-y-mm", "-3", "--rotate", "30", "--fit", "ring",
            "--art-inner-radius-mm", "30", "--art-outer-radius-mm", "50");
        Assert.True(code == 0, error + output);

        Assert.NotNull(fromPage.Plan.Band);
        Assert.Equal(new FileInfo(P("cli.iso")).Length, new FileInfo(P("page.iso")).Length);
        Assert.Equal(Sha(P("cli.iso")), Sha(P("page.iso")));
        // The read-back preview was drawn from the generated image.
        Assert.NotNull(page.ReadbackPreviewPng);
        Assert.Equal(DiscArtViewModel.ReadbackPixels, PngRaster.Read(new MemoryStream(page.ReadbackPreviewPng!)).Width);
    }

    [Fact]
    public async Task Cd_black_and_white_rotated_picture_track_equals_the_cli_bytes()
    {
        DiscArtViewModel page = NewPage();
        page.Medium = DiscArtMedium.Cd;
        Assert.True(page.SelectPreset("tiny-cd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.ApplyTone(ToneProfile.LegacyBinary);
        page.RotationDegrees = 45;
        DiscArtResult fromPage = await GenerateFromPage(page, P("page.wav"));
        Assert.NotNull(fromPage.CueSheetPath);

        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", P("cli.wav"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-cd", "--tone", "binary", "--rotate", "45");
        Assert.True(code == 0, error + output);

        Assert.Equal(Sha(P("cli.wav")), Sha(P("page.wav")));
        Assert.NotNull(page.ReadbackPreviewPng);
    }

    [Fact]
    public async Task Bd_picture_equals_the_cli_bytes_and_says_read_back_is_not_available()
    {
        DiscArtViewModel page = NewPage();
        page.Medium = DiscArtMedium.BluRay;
        Assert.True(page.SelectPreset("tiny-bd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.OffsetXMm = -10;
        await GenerateFromPage(page, P("page.iso"));

        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "bd",
            "--picture", WritePicture(), "--output", P("cli.iso"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-bd", "--offset-x-mm", "-10");
        Assert.True(code == 0, error + output);

        Assert.Equal(Sha(P("cli.iso")), Sha(P("page.iso")));
        Assert.Null(page.ReadbackPreviewPng);
        Assert.Contains("not available for BD", page.StatusMessage);
    }

    [Fact]
    public async Task Saved_calibration_is_offered_and_gives_the_cli_bytes()
    {
        string store = P("calibrations.json");
        // A tiny DVD with a fitted pitch, as a calibration fit would save it.
        var fitted = new CalibrationParameters(CalibrationDiscKind.Dvd, 24.2, 57.9, 4096, 133.3, PitchLinear: 0.002);
        CalibrationStore.Save(store, new CalibrationStoreEntry("TEST DRIVE", "TESTMEDIA-001", fitted,
            DateTimeOffset.UnixEpoch, "target-1", "fine", 0.05));

        DiscArtViewModel page = NewPage(store: store);
        page.Medium = DiscArtMedium.Dvd;
        DiscArtDiscChoice saved = Assert.Single(page.DiscChoices, c => c.Text.Contains("TEST DRIVE / TESTMEDIA-001"));
        page.SelectedDisc = saved;
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        await GenerateFromPage(page, P("page.iso"));

        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", P("cli.iso"), "--drive-model", "TEST DRIVE",
            "--media-id", "TESTMEDIA-001", "--store", store);
        Assert.True(code == 0, error + output);
        Assert.Contains("saved calibration", output);

        Assert.Equal(Sha(P("cli.iso")), Sha(P("page.iso")));
        // The CD list does not offer a DVD calibration.
        page.Medium = DiscArtMedium.Cd;
        Assert.DoesNotContain(page.DiscChoices, c => c.Text.Contains("TEST DRIVE"));
    }

    [Fact]
    public async Task Generated_dvd_burns_and_verifies_on_the_simulated_drive_and_unburnable_images_are_refused()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var engine = new VirtualBurnEngine();
        var studio = new ImageStudioViewModel(devices, engine) { VerifyAfterBurn = true };
        string? burned = null;
        DiscArtViewModel page = NewPage(path =>
        {
            burned = path;
            studio.SourceImagePath = path;
            return studio.BurnImageToDiscAsync("VDRV-01");
        });

        // Nothing generated yet.
        Assert.False((await page.BurnAsync()).Success);
        Assert.Null(burned);

        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("tiny-dvd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        DiscArtResult result = await GenerateFromPage(page, P("burn.iso"));
        BurnOperationResult burn = await page.BurnAsync();
        Assert.True(burn.Success, burn.ErrorMessage + " / " + page.StatusMessage);
        Assert.Equal(result.OutputPath, burned);
        Assert.True(studio.IsChecksumMatch, studio.VerificationStatusText);

        // Files at the outer edge need a two-session burn, which the command line does: refused here before any drive
        // is touched. (A disc larger than the tiny one: outer files keep room for the drive's gap between sessions.)
        string files = P("files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "README.TXT"), "picture disc with files");
        Assert.True(page.SelectPreset("small-dvd"));
        page.FilesFolder = files;
        page.FilesOuter = true;
        DiscArtResult outerImage = await GenerateFromPage(page, P("outer.iso"));
        burned = null;
        BurnOperationResult outer = await page.BurnAsync();
        Assert.False(outer.Success);
        Assert.Contains("two sessions", page.StatusMessage);
        Assert.Contains($"disc-art burn --drive <id> --image \"{outerImage.OutputPath}\"", page.StatusMessage);
        Assert.Null(burned);

        // A CD picture track goes through the direct writer on the command line.
        page.Medium = DiscArtMedium.Cd;
        Assert.True(page.SelectPreset("tiny-cd"));
        await GenerateFromPage(page, P("track.wav"));
        Assert.False((await page.BurnAsync()).Success);
        Assert.Contains("disc-art burn", page.StatusMessage);
        Assert.Null(burned);
    }

    [Fact]
    public async Task Missing_or_bad_choices_are_explained_and_nothing_is_written()
    {
        DiscArtViewModel page = NewPage();
        Assert.Throws<ArgumentException>(() => page.BuildRequest(P("x.iso")));
        File.WriteAllText(P("not-a-png.png"), "text");
        Assert.False(page.OpenPicture(P("not-a-png.png")));
        Assert.Contains("PNG", page.StatusMessage);

        Assert.True(page.OpenPicture(WritePicture()));
        page.ArtInnerRadiusText = "abc";
        ArgumentException ex = Assert.Throws<ArgumentException>(() => page.BuildRequest(P("x.iso")));
        Assert.Contains("not a number", ex.Message);
        page.OutputPath = P("x.iso");
        await page.GenerateAsync();
        Assert.Contains("not a number", page.StatusMessage);
        Assert.Null(page.LastResult);
        Assert.False(File.Exists(P("x.iso")));
    }

    // Preview pixel of a point given in disc millimetres (x right, y down from the centre).
    private static (int X, int Y) PreviewPixel(double xMm, double yMm)
    {
        double pixelsPerMm = DiscArtViewModel.PreviewPixels / (2 * DiscArtViewModel.PreviewDiscRadiusMm);
        return ((int)Math.Floor(DiscArtViewModel.PreviewPixels / 2.0 + xMm * pixelsPerMm),
            (int)Math.Floor(DiscArtViewModel.PreviewPixels / 2.0 + yMm * pixelsPerMm));
    }

    [Fact]
    public async Task Rendered_page_shows_the_picture_where_the_placement_puts_it()
    {
        DiscArtViewModel page = NewPage();
        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("tiny-dvd")); // drawn ring 24 to 58 mm
        Assert.True(page.OpenPicture(WritePicture()));

        async Task<Func<double, double, Color>> Snapshot()
        {
            await page.PlacementTask;
            Assert.True(page.PlacementPreviewPng is not null, page.PlanSummary);
            Func<double, double, Color>? pixel = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var view = new DiscArtView { DataContext = page };
                    view.Measure(new Size(1400, 900));
                    view.Arrange(new Rect(0, 0, 1400, 900));
                    view.UpdateLayout();
                    var surface = (FrameworkElement)view.FindName("DiscSurface");
                    Assert.Equal(DiscArtViewModel.PreviewPixels, surface.ActualWidth);
                    var bitmap = new RenderTargetBitmap(DiscArtViewModel.PreviewPixels, DiscArtViewModel.PreviewPixels,
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    bitmap.Freeze();
                    pixel = (x, y) =>
                    {
                        (int px, int py) = PreviewPixel(x, y);
                        byte[] bgra = new byte[4];
                        bitmap.CopyPixels(new Int32Rect(px, py, 1, 1), bgra, 4, 0);
                        return Color.FromArgb(bgra[3], bgra[2], bgra[1], bgra[0]);
                    };
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The view did not render.");
            if (failure is not null) throw new InvalidOperationException("Disc Art view failed: " + failure, failure);
            return pixel!;
        }

        Color black = Colors.Black, white = Colors.White;
        Color blank = Color.FromRgb(0x8A, 0x8A, 0x85);
        Color clear = Color.FromArgb(0, 0, 0, 0);

        // Unplaced: the picture's left (black) half is left of the centre, the right (white) half right of it.
        Func<double, double, Color> at = await Snapshot();
        Assert.Equal(black, at(-40, 0));
        Assert.Equal(white, at(40, 0));
        Assert.Equal(clear, at(0, 0));        // centre hole
        Assert.Equal(blank, at(16, 0));       // inside the drawn ring: the disc stays blank
        Assert.Equal(blank, at(0, 59));       // beyond the 58 mm ring the disc is blank
        Assert.Equal(clear, at(50, 50));      // past the 60 mm edge there is no disc

        // Turned 90 degrees clockwise (y down): the left half moves to the top.
        page.RotationDegrees = 90;
        at = await Snapshot();
        Assert.Equal(black, at(0, -40));
        Assert.Equal(white, at(0, 40));

        // A quarter size moved 40 mm right: the picture sits around (40, 0); the rest of the ring is white.
        page.RotationDegrees = 0;
        page.Scale = 0.25;
        page.OffsetXMm = 40;
        at = await Snapshot();
        Assert.Equal(black, at(36, 0));
        Assert.Equal(white, at(44, 0));
        Assert.Equal(white, at(-40, 0));
        Assert.Equal(white, at(40, -30));

        // A drag of 30 preview pixels to the left moves the picture 10 mm (a 120 mm disc over 360 pixels).
        page.MoveByPreviewPixels(-30, 0);
        Assert.Equal(30, page.OffsetXMm, 6);
    }

    [Fact]
    public void Page_opens_from_the_navigation_rail()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
                using var main = new MainViewModel(devices, new VirtualBurnEngine());
                main.NavigateCommand.Execute(NavigationTab.DiscArt);
                var workspace = Assert.IsType<WorkspaceViewModel>(main.CurrentWorkspaceViewModel);
                Assert.Same(main.DiscArt, workspace.Content);
                Assert.True(main.DiscArt.BurnCommand.CanExecute(null));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The navigation test did not finish.");
        if (failure is not null) throw new InvalidOperationException("Navigation failed: " + failure, failure);
    }
}
