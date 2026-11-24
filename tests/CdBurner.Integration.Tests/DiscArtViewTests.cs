using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
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
public sealed class DiscArtViewTests : IClassFixture<DiscArtViewTests.SharedFixtures>, IDisposable
{
    public sealed class SharedFixtures : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-view-shared-" + Guid.NewGuid().ToString("N"));
        public string PresetsPath { get; }
        public string RimPresetsPath { get; }
        public string PicturePath { get; }
        public string SampleFilesDir { get; }
        public string AlbumCuePath { get; }

        public SharedFixtures()
        {
            Directory.CreateDirectory(Dir);

            PresetsPath = Path.Combine(Dir, "presets.json");
            File.WriteAllText(PresetsPath, """
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
                      "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 },
                    { "id": "medium-dvd", "displayName": "Medium DVD", "description": "test", "totalSectors": 96000,
                      "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
                  ],
                  "bluRayPresets": [
                    { "id": "tiny-bd", "displayName": "Tiny BD", "description": "test", "totalSectors": 4096,
                      "innerRadiusMm": 24, "outerRadiusMm": 58 },
                    { "id": "small-bd", "displayName": "Small BD", "description": "test", "totalSectors": 32768,
                      "innerRadiusMm": 24, "outerRadiusMm": 58 }
                  ]
                }
                """);

            RimPresetsPath = Path.Combine(Dir, "rim-presets.json");
            File.WriteAllText(RimPresetsPath, """
                {
                  "schemaVersion": 4,
                  "cdPresets": [
                    { "id": "ring-cd", "displayName": "Ring CD", "description": "test", "sectors": 10900,
                      "innerRadiusMm": 53, "outerRadiusMm": 58, "linearVelocityMmPerSecond": 1200 },
                    { "id": "narrow-cd", "displayName": "Narrow CD", "description": "test", "sectors": 1422,
                      "innerRadiusMm": 57.9, "outerRadiusMm": 58, "linearVelocityMmPerSecond": 1200 }
                  ],
                  "dvdPresets": [
                    { "id": "ring-dvd", "displayName": "Ring DVD", "description": "test", "totalSectors": 16896,
                      "innerRadiusMm": 53, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 },
                    { "id": "narrow-dvd", "displayName": "Narrow DVD", "description": "test", "totalSectors": 2048,
                      "innerRadiusMm": 57.96, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
                  ],
                  "bluRayPresets": [
                    { "id": "ring-bd", "displayName": "Ring BD", "description": "test", "totalSectors": 17600,
                      "innerRadiusMm": 53.5, "outerRadiusMm": 58 },
                    { "id": "narrow-bd", "displayName": "Narrow BD", "description": "test", "totalSectors": 10176,
                      "innerRadiusMm": 57.98, "outerRadiusMm": 58 }
                  ]
                }
                """);

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

            SampleFilesDir = Path.Combine(Dir, "files");
            Directory.CreateDirectory(SampleFilesDir);
            File.WriteAllText(Path.Combine(SampleFilesDir, "README.TXT"), "picture disc with outer files");
            File.WriteAllBytes(Path.Combine(SampleFilesDir, "DATA.BIN"), Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 7)).ToArray());

            string albumDir = Path.Combine(Dir, "album");
            Directory.CreateDirectory(albumDir);
            byte[] pcm = new byte[700 * OpticalConstants.RawSectorSize];
            new Random(21).NextBytes(pcm);
            using (var wav = new FileStream(Path.Combine(albumDir, "album.wav"), FileMode.Create))
            {
                var w = new BinaryWriter(wav);
                w.Write("RIFF"u8);
                w.Write(36 + pcm.Length);
                w.Write("WAVEfmt "u8);
                w.Write(16);
                w.Write((short)1);
                w.Write((short)2);
                w.Write(44100);
                w.Write(176400);
                w.Write((short)4);
                w.Write((short)16);
                w.Write("data"u8);
                w.Write(pcm.Length);
                w.Write(pcm);
            }

            AlbumCuePath = Path.Combine(albumDir, "album.cue");
            File.WriteAllLines(AlbumCuePath, new[]
            {
                "TITLE \"Album\"",
                "PERFORMER \"Artist\"",
                "FILE \"album.wav\" WAVE",
                "  TRACK 01 AUDIO",
                "    TITLE \"One\"",
                "    INDEX 01 00:00:00",
                "  TRACK 02 AUDIO",
                "    TITLE \"Two\"",
                "    INDEX 01 00:05:00",
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-view-" + Guid.NewGuid().ToString("N"));
    private readonly SharedFixtures _shared;

    public DiscArtViewTests(SharedFixtures shared)
    {
        _shared = shared;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>One tiny disc of each type (the same sizes the command-line tests use).</summary>
    private string WritePresets() => _shared.PresetsPath;

    /// <summary>64 x 64: left half black, right half white.</summary>
    private string WritePicture() => _shared.PicturePath;

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
        Assert.False(studio.IsChecksumMatch);
        Assert.Contains("picture area was not compared", studio.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // A CD picture track goes through the direct writer on the command line.
        burned = null;
        page.Medium = DiscArtMedium.Cd;
        Assert.True(page.SelectPreset("tiny-cd"));
        await GenerateFromPage(page, P("track.wav"));
        Assert.False((await page.BurnAsync()).Success);
        Assert.Contains("disc-art burn", page.StatusMessage);
        Assert.Null(burned);
    }

    private string WriteSampleFiles() => _shared.SampleFilesDir;

    private DiscArtViewModel NewPageWithDrive(DiscDeviceManager devices, IBurnEngineBackend engine)
        => new(null, WritePresets(), P("no-calibrations.json"),
            deviceService: devices,
            burnEngine: engine,
            directBurnLogDirectory: P("logs"));

    [Theory]
    [InlineData(DiscMediaType.DvdPlusR, "small-dvd", 32_768, PictureDiscTwoSessionBurner.DvdPlusRSessionGapSectors)]
    [InlineData(DiscMediaType.DvdMinusR, "medium-dvd", 96_000, PictureDiscTwoSessionBurner.DvdMinusRFirstSessionGapSectors)]
    public async Task Outer_files_dvd_burns_two_sessions_verifies_both_and_reports_progress_for_both(
        DiscMediaType mediaType, string presetId, int capacity, int expectedGap)
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = Assert.IsType<VirtualDiscRecorder>(devices.GetRecorderById("VDRV-01"));
        using var medium = new VirtualDiscMedium(mediaType, capacity);
        recorder.InsertMedia(medium);

        DiscArtViewModel page = NewPageWithDrive(devices, new VirtualBurnEngine());
        var statuses = new List<string>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscArtViewModel.StatusMessage) && !string.IsNullOrEmpty(page.StatusMessage))
            {
                statuses.Add(page.StatusMessage);
            }
        };

        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset(presetId));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        string filesDir = WriteSampleFiles();
        page.FilesFolder = filesDir;
        page.FilesOuter = true;
        page.VolumeLabel = "OUTER_UI";

        await page.PlacementTask;
        Assert.DoesNotContain("cannot be burned yet", page.PlanSummary, StringComparison.OrdinalIgnoreCase);

        DiscArtResult outerImage = await GenerateFromPage(page, P($"outer-{mediaType}.iso"));
        PictureDiscLayout layout = Assert.IsType<PictureDiscLayout>(outerImage.Plan.FilesLayout);
        Assert.Equal(PictureDiscFilesPlacement.OuterFiles, layout.Placement);

        BurnOperationResult burn = await page.BurnAsync();
        Assert.True(burn.Success, burn.ErrorMessage + " / " + page.StatusMessage);
        Assert.Equal(100, page.Progress);

        PictureDiscTwoSessionResult twoSession = Assert.IsType<PictureDiscTwoSessionResult>(page.LastTwoSessionResult);
        Assert.True(twoSession.Success, twoSession.ErrorMessage);
        Assert.False(twoSession.PictureSession?.Verified);
        Assert.False(twoSession.PictureSession?.PictureAreaCompared);
        Assert.True(twoSession.FilesSession?.Verified);
        Assert.True(twoSession.FilesSession?.FilesCompared);

        int expectedStart = layout.PictureEndLba + expectedGap;
        Assert.Equal(expectedStart, twoSession.FilesSessionStartLba);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.Equal(0, medium.Sessions[0].StartLba);
        Assert.Equal(layout.PictureEndLba, medium.Sessions[0].SectorCount);
        Assert.Equal(expectedStart, medium.Sessions[1].StartLba);
        Assert.True(medium.IsFinalized);

        // Progress updates named both sessions while burning and verifying.
        Assert.Contains(statuses, s => s.Contains("Session 1 of 2 (picture)", StringComparison.Ordinal));
        Assert.Contains(statuses, s => s.Contains("Session 2 of 2 (files)", StringComparison.Ordinal));
        if (mediaType == DiscMediaType.DvdMinusR)
        {
            Assert.Contains(DataBurnService.AddedSessionHint, page.StatusMessage);
        }

        // Session 2 holds the files intact and Session 1 holds the exact picture bytes.
        using (var reader = IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: expectedStart))
        {
            Assert.Equal("OUTER_UI", reader.VolumeLabel);
            Assert.Equal(File.ReadAllBytes(Path.Combine(filesDir, "README.TXT")), reader.ReadFileBytes("/README.TXT"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(filesDir, "DATA.BIN")), reader.ReadFileBytes("/DATA.BIN"));
        }

        byte[] expectedPicture = new byte[layout.PictureEndLba * OpticalConstants.DataSectorSize];
        using (var source = File.OpenRead(outerImage.OutputPath))
        {
            source.ReadExactly(expectedPicture);
        }
        byte[] discPicture = new byte[expectedPicture.Length];
        medium.ReadSectors(0, layout.PictureEndLba, discPicture, OpticalConstants.DataSectorSize);
        Assert.True(expectedPicture.AsSpan().SequenceEqual(discPicture));
    }

    [Theory]
    [InlineData(DiscMediaType.DvdMinusRw)]
    [InlineData(DiscMediaType.DvdPlusRw)]
    [InlineData(DiscMediaType.CdR)]
    [InlineData(DiscMediaType.BdR)]
    public async Task Outer_files_burn_refuses_unsupported_media_with_cli_reason_and_leaves_disc_blank(DiscMediaType mediaType)
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = Assert.IsType<VirtualDiscRecorder>(devices.GetRecorderById("VDRV-01"));
        using var medium = new VirtualDiscMedium(mediaType, 32_768);
        recorder.InsertMedia(medium);

        DiscArtViewModel page = NewPageWithDrive(devices, new VirtualBurnEngine());
        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("small-dvd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.FilesFolder = WriteSampleFiles();
        page.FilesOuter = true;
        await GenerateFromPage(page, P($"outer-refuse-{mediaType}.iso"));

        BurnOperationResult burn = await page.BurnAsync();
        Assert.False(burn.Success);
        string expectedReason = PictureDiscTwoSessionBurner.WhyNotSupported(mediaType)!;
        Assert.Contains(expectedReason, page.StatusMessage);
        Assert.Contains("Nothing was written", page.StatusMessage);
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);
    }

    [Fact]
    public async Task Outer_files_bd_image_is_refused_with_cli_reason_before_touching_drive()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = Assert.IsType<VirtualDiscRecorder>(devices.GetRecorderById("VDRV-01"));
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 32_768);
        recorder.InsertMedia(medium);

        DiscArtViewModel page = NewPageWithDrive(devices, new VirtualBurnEngine());
        page.Medium = DiscArtMedium.BluRay;
        Assert.True(page.SelectPreset("small-bd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.FilesFolder = WriteSampleFiles();
        page.FilesOuter = true;
        DiscArtResult bdResult = await GenerateFromPage(page, P("outer-bd.iso"));

        BurnOperationResult burn = await page.BurnAsync();
        Assert.False(burn.Success);
        Assert.Equal(PictureDiscTwoSessionService.WhyLayoutNotSupported(bdResult.Plan.FilesLayout!), page.StatusMessage);
        Assert.Contains("BD image", page.StatusMessage);
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);
    }

    [Fact]
    public async Task MainViewModel_disc_art_page_burns_outer_files_image_on_simulated_dvd_plus_r()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = Assert.IsType<VirtualDiscRecorder>(devices.GetRecorderById("VDRV-01"));
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 32_768);
        recorder.InsertMedia(medium);

        using var main = new MainViewModel(devices, new VirtualBurnEngine());
        DiscArtViewModel page = new(
            burnImage: null,
            userPresetsPath: WritePresets(),
            calibrationStorePath: P("no-calibrations.json"),
            deviceService: devices,
            burnEngine: new VirtualBurnEngine(),
            getSelectedRecorder: () => main.SelectedRecorder,
            directBurnLogDirectory: P("logs"));

        // Smoke-check the MainViewModel-owned DiscArt instance too (without writing to %LOCALAPPDATA% presets).
        Assert.True(main.DiscArt.BurnCommand.CanExecute(null));
        Assert.Same(recorder, main.SelectedRecorder);

        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("small-dvd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.FilesFolder = WriteSampleFiles();
        page.FilesOuter = true;
        await GenerateFromPage(page, P("main-outer.iso"));

        BurnOperationResult burn = await page.BurnAsync();
        Assert.True(burn.Success, burn.ErrorMessage + " / " + page.StatusMessage);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public void DiscArtView_checkbox_does_not_say_cannot_be_burned_yet()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DiscArtViewModel page = NewPage();
                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();
                var checkboxes = FindVisualChildren<CheckBox>(view).ToList();
                var outerBox = Assert.Single(checkboxes, cb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(cb),
                    "Files at the outer edge",
                    StringComparison.Ordinal));
                string content = outerBox.Content?.ToString() ?? string.Empty;
                Assert.DoesNotContain("cannot be burned yet", content, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("View check failed: " + failure, failure);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
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

    private string WriteAlbumCue() => _shared.AlbumCuePath;

    [Fact]
    public async Task Dvd_title_ring_from_page_equals_the_cli_bytes()
    {
        DiscArtViewModel page = NewPage();
        page.Medium = DiscArtMedium.Dvd;
        Assert.True(page.SelectPreset("small-dvd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.TitleRingText = "Summer trip";
        page.TitleRingDateText = "2026-11-17";

        await page.PlacementTask;
        Assert.NotNull(page.PlacementPreviewPng);
        Assert.Contains("Title ring:", page.PlanSummary);

        DiscArtResult fromPage = await GenerateFromPage(page, P("page-ring.iso"));
        Assert.NotNull(fromPage.Plan.TitleRing);

        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", P("cli-ring.iso"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "small-dvd", "--title-ring", "Summer trip", "--title-ring-date", "2026-11-17");
        Assert.True(code == 0, error + output);

        Assert.Equal(Sha(P("cli-ring.iso")), Sha(P("page-ring.iso")));
        Assert.NotNull(page.ReadbackPreviewPng);

        // Title ring alone (without a picture) and with no date ("none") also matches the CLI.
        DiscArtViewModel ringOnlyPage = NewPage();
        ringOnlyPage.Medium = DiscArtMedium.Dvd;
        Assert.True(ringOnlyPage.SelectPreset("small-dvd"));
        ringOnlyPage.TitleRingText = "Summer trip";
        ringOnlyPage.TitleRingDateText = string.Empty;
        ringOnlyPage.TitleRingHeightText = "2.5";

        await ringOnlyPage.PlacementTask;
        Assert.NotNull(ringOnlyPage.PlacementPreviewPng);
        await GenerateFromPage(ringOnlyPage, P("page-ring-only.iso"));

        (int ringCode, string ringOut, string ringErr) = await Cli("disc-art", "generate", "--media", "dvd",
            "--output", P("cli-ring-only.iso"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "small-dvd", "--title-ring", "Summer trip", "--title-ring-date", "none",
            "--title-ring-height-mm", "2.5");
        Assert.True(ringCode == 0, ringErr + ringOut);

        Assert.Equal(Sha(P("cli-ring-only.iso")), Sha(P("page-ring-only.iso")));
    }

    [Fact]
    public async Task Cd_pre_gap_picture_from_page_equals_the_cli_bytes()
    {
        string baseCue = WriteAlbumCue();
        DiscArtViewModel page = NewPage();
        Assert.False(page.HidePictureInPreGap);

        page.Medium = DiscArtMedium.Cd;
        Assert.True(page.SelectPreset("tiny-cd"));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.BaseCuePath = baseCue;
        page.HidePictureInPreGap = true;
        page.PreGapSecondsText = "60";
        Assert.True(page.FitToRing);

        await page.PlacementTask;
        Assert.NotNull(page.PlacementPreviewPng);
        Assert.Contains("Experimental", page.PlanSummary);

        string pageOut = P(Path.Combine("page", "pregap.wav"));
        DiscArtResult fromPage = await GenerateFromPage(page, pageOut);
        Assert.Equal(60 * 75, fromPage.Plan.CdPreGapSectors);
        Assert.NotNull(fromPage.CueSheetPath);

        string cliOut = P(Path.Combine("cli", "pregap.wav"));
        (int code, string output, string error) = await Cli("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", cliOut, "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-cd", "--pregap-art", baseCue, "--pregap-seconds", "60");
        Assert.True(code == 0, error + output);

        Assert.Equal(Sha(cliOut), Sha(pageOut));
        Assert.Equal(
            Sha(P(Path.Combine("cli", "album-with-pregap-picture.cue"))),
            Sha(fromPage.CueSheetPath!));
        Assert.NotNull(page.ReadbackPreviewPng);
    }

    [Fact]
    public void Pre_gap_option_is_off_by_default_and_carries_the_experimental_warning()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DiscArtViewModel page = NewPage();
                Assert.False(page.HidePictureInPreGap);

                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                var checkboxes = FindVisualChildren<CheckBox>(view).ToList();
                var preGapBox = Assert.Single(checkboxes, cb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(cb),
                    "Hide the picture in the pre-gap (experimental)",
                    StringComparison.Ordinal));
                Assert.False(preGapBox.IsChecked);
                Assert.Contains("experimental", preGapBox.Content?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                var warningBlock = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Pre-gap picture experimental warning",
                    StringComparison.Ordinal));
                Assert.Equal(DiscArtImageBuilder.CdPreGapWarning, warningBlock.Text);
                Assert.Contains("noise", warningBlock.Text, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The pre-gap view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("Pre-gap view check failed: " + failure, failure);
    }

    [Fact]
    public void Title_box_is_disabled_with_a_reason_on_media_the_cli_refuses()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DiscArtViewModel page = NewPage();
                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                var textBoxes = FindVisualChildren<TextBox>(view).ToList();
                var titleBox = Assert.Single(textBoxes, tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Title ring text",
                    StringComparison.Ordinal));
                var dateBox = Assert.Single(textBoxes, tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Title ring date",
                    StringComparison.Ordinal));
                var reasonBlock = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Title ring unavailable reason",
                    StringComparison.Ordinal));

                // Supported on DVD.
                page.Medium = DiscArtMedium.Dvd;
                view.UpdateLayout();
                Assert.True(titleBox.IsEnabled);
                Assert.True(dateBox.IsEnabled);
                Assert.NotEqual(Visibility.Visible, reasonBlock.Visibility);

                // Refused on CD and BD with the CLI reason.
                foreach (DiscArtMedium unsupported in new[] { DiscArtMedium.Cd, DiscArtMedium.BluRay })
                {
                    page.Medium = unsupported;
                    view.UpdateLayout();
                    Assert.False(titleBox.IsEnabled);
                    Assert.False(dateBox.IsEnabled);
                    Assert.Equal(Visibility.Visible, reasonBlock.Visibility);
                    Assert.Contains("DVD picture discs only", reasonBlock.Text);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The title ring view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("Title ring view check failed: " + failure, failure);
    }

    private string WriteRimHeaderPresets() => _shared.RimPresetsPath;

    [Fact]
    public void Rim_header_checkbox_is_off_by_default_and_available_on_cd_dvd_and_bd()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DiscArtViewModel page = new(null, WriteRimHeaderPresets(), P("no-calibrations.json"));
                Assert.False(page.IncludeRimHeader);

                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                var rimBox = Assert.Single(FindVisualChildren<CheckBox>(view), cb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(cb),
                    "Include Band 0 rim header",
                    StringComparison.Ordinal));
                var planSummary = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Picture disc plan summary",
                    StringComparison.Ordinal));
                var rimValidation = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Band 0 rim header validation",
                    StringComparison.Ordinal));

                Assert.False(rimBox.IsChecked);
                Assert.NotNull(planSummary);
                Assert.NotEqual(Visibility.Visible, rimValidation.Visibility);

                foreach (DiscArtMedium medium in new[] { DiscArtMedium.Cd, DiscArtMedium.Dvd, DiscArtMedium.BluRay })
                {
                    page.Medium = medium;
                    view.UpdateLayout();
                    Assert.True(rimBox.IsEnabled);
                }

                rimBox.IsChecked = true;
                view.UpdateLayout();
                Assert.True(page.IncludeRimHeader);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The rim header view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("Rim header view check failed: " + failure, failure);
    }

    [Theory]
    [InlineData(DiscArtMedium.Cd, "cd", "ring-cd", "page-rim.wav", "cli-rim.wav", "DISC_ART", null)]
    [InlineData(DiscArtMedium.Dvd, "dvd", "ring-dvd", "page-rim.iso", "cli-rim.iso", "DISC_ART", null)]
    [InlineData(DiscArtMedium.Dvd, "dvd", "ring-dvd", "page-rim-lbl.iso", "cli-rim-lbl.iso", "Holiday", "Holiday")]
    [InlineData(DiscArtMedium.BluRay, "bd", "ring-bd", "page-rim-bd.iso", "cli-rim-bd.iso", "DISC_ART", null)]
    public async Task Rim_header_requests_and_images_match_cli_and_preview_shows_rim_band(
        DiscArtMedium medium, string cliMedia, string presetId, string pageFile, string cliFile,
        string volumeLabel, string? cliLabel)
    {
        string presets = WriteRimHeaderPresets();
        DiscArtViewModel page = new(null, presets, P("no-calibrations.json"));
        page.Medium = medium;
        Assert.True(page.SelectPreset(presetId));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);
        page.VolumeLabel = volumeLabel;

        // Off by default: request omits RimHeader and the outer-left edge of the placement preview shows the black picture.
        Assert.Null(page.BuildRequest(P(pageFile)).RimHeader);
        await page.PlacementTask;
        Assert.NotNull(page.PlacementPreviewPng);
        BgraRaster withoutRim = PngRaster.Read(new MemoryStream(page.PlacementPreviewPng!));
        (int pxLeft, int pyLeft) = PreviewPixel(-55.5, 0);
        int leftOffset = (pyLeft * withoutRim.Width + pxLeft) * 4;
        Assert.Equal(0, withoutRim.Pixels[leftOffset]);

        // Enabled: request matches CLI --rim-header, placement preview renders the rim-header band, and image bytes match.
        page.IncludeRimHeader = true;
        DiscArtRequest req = page.BuildRequest(P(pageFile));
        DiscArtRimHeader rim = Assert.IsType<DiscArtRimHeader>(req.RimHeader);
        string expectedTitle = cliLabel ?? Path.GetFileNameWithoutExtension(WritePicture());
        Assert.Equal(expectedTitle, rim.Title);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), rim.Date);

        await page.PlacementTask;
        Assert.False(page.HasPlanError);
        Assert.False(page.HasRimHeaderValidationMessage);
        Assert.Contains("Rim header:", page.PlanSummary);
        Assert.NotNull(page.PlacementPreviewPng);

        BgraRaster withRim = PngRaster.Read(new MemoryStream(page.PlacementPreviewPng!));
        // Inside the rim band at (-55.5, 0) the white rim paper replaces the black picture; at (57.8, 0) the 0-deg tick is dark.
        Assert.Equal(255, withRim.Pixels[leftOffset]);
        (int pxZero, int pyZero) = PreviewPixel(57.8, 0);
        int zeroOffset = (pyZero * withRim.Width + pxZero) * 4;
        Assert.Equal(0, withRim.Pixels[zeroOffset]);

        await GenerateFromPage(page, P(pageFile));

        var cliArgs = new List<string>
        {
            "disc-art", "generate", "--media", cliMedia,
            "--picture", WritePicture(), "--output", P(cliFile),
            "--disc-presets-file", presets, "--disc-preset", presetId, "--rim-header",
        };
        if (cliLabel is not null)
        {
            cliArgs.Add("--label");
            cliArgs.Add(cliLabel);
        }

        (int code, string output, string error) = await Cli([.. cliArgs]);
        Assert.True(code == 0, error + output);
        Assert.Equal(Sha(P(cliFile)), Sha(P(pageFile)));
    }

    [Theory]
    [InlineData(DiscArtMedium.Cd, "narrow-cd", "50 x 75 um")]
    [InlineData(DiscArtMedium.Dvd, "narrow-dvd", "26 x 34 um")]
    [InlineData(DiscArtMedium.BluRay, "narrow-bd", "26 x 34 um")]
    public async Task Rim_header_narrow_ring_refusal_surfaces_before_generation_and_in_visual_tree(
        DiscArtMedium medium, string presetId, string expectedCellFloor)
    {
        string presets = WriteRimHeaderPresets();
        DiscArtViewModel page = new(null, presets, P("no-calibrations.json"));
        page.Medium = medium;
        Assert.True(page.SelectPreset(presetId));
        Assert.True(page.OpenPicture(WritePicture()), page.StatusMessage);

        page.IncludeRimHeader = true;
        await page.PlacementTask;

        Assert.True(page.HasPlanError);
        Assert.True(page.HasRimHeaderValidationMessage);
        Assert.Null(page.PlacementPreviewPng);
        Assert.Contains("rim header does not fit", page.PlanSummary);
        Assert.Contains(expectedCellFloor, page.PlanSummary);
        Assert.Equal(page.PlanSummary, page.RimHeaderValidationMessage);

        // Generation also refuses cleanly and leaves no output file behind.
        string outFile = P($"refused-{presetId}" + (medium == DiscArtMedium.Cd ? ".wav" : ".iso"));
        page.OutputPath = outFile;
        await page.GenerateAsync();
        Assert.Null(page.LastResult);
        Assert.False(File.Exists(outFile));
        Assert.Contains("rim header does not fit", page.StatusMessage);

        // Visual tree surfaces the refusal in both the plan summary and rim-header validation TextBlocks.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                var planSummary = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Picture disc plan summary",
                    StringComparison.Ordinal));
                var rimValidation = Assert.Single(FindVisualChildren<TextBlock>(view), tb => string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(tb),
                    "Band 0 rim header validation",
                    StringComparison.Ordinal));

                Assert.Equal(Visibility.Visible, rimValidation.Visibility);
                Assert.Contains("rim header does not fit", rimValidation.Text);
                Assert.Contains(expectedCellFloor, planSummary.Text);
                view.DataContext = null;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The narrow-ring view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("Narrow-ring view check failed: " + failure, failure);
    }
}
