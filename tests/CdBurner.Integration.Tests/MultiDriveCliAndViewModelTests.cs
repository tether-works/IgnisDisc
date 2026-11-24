using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;
using ThemeMode = CdBurner.Core.Persistence.ThemeMode;

namespace CdBurner.Integration.Tests;

public sealed class MultiDriveCliAndViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cdburner-multidrive-int-" + Guid.NewGuid().ToString("N"));

    public MultiDriveCliAndViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<string> CreateTestIsoAsync(string name = "test.iso")
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] data = new byte[120_000];
        new Random(11).NextBytes(data);
        root.AddFile("data.bin", data);

        string isoPath = Path.Combine(_tempDir, name);
        await using var fs = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "MULTI_CLI"));
        return isoPath;
    }

    private string CreateTestWav(string fileName, int frames = 320, int seed = 5)
    {
        byte[] pcm = new byte[frames * OpticalConstants.RawSectorSize];
        new Random(seed).NextBytes(pcm);
        string path = Path.Combine(_tempDir, fileName);
        WavFileCodec.WriteWavFile(path, pcm);
        return path;
    }

    private static DiscDeviceManager CreateThreeDriveManager(Func<VirtualDiscMedium>? mediaFactory = null)
    {
        var dm = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var d1 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-01")!;
        var d2 = new VirtualDiscRecorder("VDRV-02", "IGNIS", "VIRTUAL B", @"W:\");
        var d3 = new VirtualDiscRecorder("VDRV-03", "IGNIS", "VIRTUAL C", @"X:\");
        dm.AddVirtualRecorder(d2);
        dm.AddVirtualRecorder(d3);

        if (mediaFactory != null)
        {
            d1.InsertMedia(mediaFactory());
            d2.InsertMedia(mediaFactory());
            d3.InsertMedia(mediaFactory());
        }
        return dm;
    }

    private static async Task<(int Exit, JsonDocument? Json, string Out, string Err)> RunCliAsync(
        IDiscDeviceService devices,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, devices, new VirtualBurnEngine());
        string text = stdout.ToString();
        int idx = text.IndexOf('{');
        JsonDocument? doc = args.Contains("--json") && idx >= 0 ? JsonDocument.Parse(text[idx..]) : null;
        return (exit, doc, text, stderr.ToString());
    }

    [Fact]
    public async Task Cli_BurnIso_Drives_BurnsThreeVirtualDrivesSimultaneously_WithPerDriveTelemetry()
    {
        using var dm = CreateThreeDriveManager(() => VirtualDiscMedium.CreateBlankDvdR47());
        string isoPath = await CreateTestIsoAsync("cli3.iso");
        string telemetryBase = Path.Combine(_tempDir, "telemetry.svg");

        var (exit, json, outText, err) = await RunCliAsync(
            dm,
            "burn-iso",
            "--drives", "VDRV-01,VDRV-02,VDRV-03",
            "--iso", isoPath,
            "--native",
            "--log-dir", Path.Combine(_tempDir, "logs"),
            "--telemetry-out", telemetryBase,
            "--json");

        Assert.True(exit == 0, outText + err);
        var root = json!.RootElement;
        Assert.True(root.GetProperty("allSucceeded").GetBoolean());
        Assert.True(root.GetProperty("multiDrive").GetBoolean());
        Assert.Equal(3, root.GetProperty("drivesRequested").GetInt32());
        Assert.Equal(3, root.GetProperty("succeededCount").GetInt32());
        Assert.Equal(0, root.GetProperty("failedCount").GetInt32());

        foreach (var item in root.GetProperty("results").EnumerateArray())
        {
            Assert.True(item.GetProperty("success").GetBoolean());
            Assert.True(item.GetProperty("verified").GetBoolean());
            string? telPath = item.GetProperty("telemetryOut").GetString();
            Assert.False(string.IsNullOrWhiteSpace(telPath));
            Assert.True(File.Exists(telPath));
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "telemetry-VDRV-01.svg")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "telemetry-VDRV-02.svg")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "telemetry-VDRV-03.svg")));
    }

    [Fact]
    public async Task Cli_BurnIso_Drives_IsolatesFaultWhenOneDriveFails()
    {
        using var dm = CreateThreeDriveManager(() => VirtualDiscMedium.CreateBlankCdR700());
        // Make VDRV-02 finalized so it fails before writing while VDRV-01 and VDRV-03 succeed
        var d2 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-02")!;
        d2.CurrentMedium!.IsBlank = false;
        d2.CurrentMedium!.IsFinalized = true;

        string isoPath = await CreateTestIsoAsync("fault_cli.iso");
        var (exit, json, outText, err) = await RunCliAsync(
            dm,
            "burn-iso",
            "--drives", "VDRV-01,VDRV-02,VDRV-03",
            "--iso", isoPath,
            "--json");

        Assert.Equal(1, exit);
        var root = json!.RootElement;
        Assert.False(root.GetProperty("allSucceeded").GetBoolean());
        Assert.True(root.GetProperty("anySucceeded").GetBoolean());
        Assert.Equal(2, root.GetProperty("succeededCount").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
    }

    [Fact]
    public async Task Cli_Duplicate_FromSourceDataDisc_BurnsToTwoDrivesSimultaneously()
    {
        using var dm = CreateThreeDriveManager();
        var d1 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-01")!;
        var d2 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-02")!;
        var d3 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-03")!;

        string sourceIso = await CreateTestIsoAsync("source_data.iso");
        d1.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
        var burnSrc = await new DataBurnService(new VirtualBurnEngine(d1)).BurnIsoFileAsync(d1, sourceIso);
        Assert.True(burnSrc.Success, burnSrc.ErrorMessage);

        d2.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
        d3.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());

        var (exit, json, outText, err) = await RunCliAsync(
            dm,
            "duplicate",
            "--source", "VDRV-01",
            "--drives", "VDRV-02,VDRV-03",
            "--json");

        Assert.True(exit == 0, outText + err);
        var root = json!.RootElement;
        Assert.True(root.GetProperty("allSucceeded").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("sourceDriveId").GetString());
        Assert.Equal(2, root.GetProperty("succeededCount").GetInt32());
        Assert.False(d2.CurrentMedium!.IsBlank);
        Assert.False(d3.CurrentMedium!.IsBlank);
        Assert.True(d2.CurrentMedium.IsFinalized);
        Assert.True(d3.CurrentMedium.IsFinalized);
    }

    [Fact]
    public async Task Cli_Duplicate_FromSourceAudioCd_BurnsToTwoDrivesSimultaneously()
    {
        using var dm = CreateThreeDriveManager();
        var d1 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-01")!;
        var d2 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-02")!;
        var d3 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-03")!;

        string wav1 = CreateTestWav("src1.wav", 300, seed: 1);
        string wav2 = CreateTestWav("src2.wav", 320, seed: 2);
        var comp = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        comp.AddTrack(new AudioCdTrackItem(1, wav1, "Track 1", "Artist"));
        comp.AddTrack(new AudioCdTrackItem(2, wav2, "Track 2", "Artist"));

        d1.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var burned = await new AudioCdBurnService(new VirtualBurnEngine(d1)).BurnAsync(d1, comp);
        Assert.True(burned.Success, burned.ErrorMessage);

        d2.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        d3.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (exit, json, outText, err) = await RunCliAsync(
            dm,
            "duplicate",
            "--source", "VDRV-01",
            "--drives", "VDRV-02,VDRV-03",
            "--json");

        Assert.True(exit == 0, outText + err);
        Assert.True(json!.RootElement.GetProperty("allSucceeded").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("succeededCount").GetInt32());
        Assert.Equal(2, Assert.Single(d2.CurrentMedium!.Sessions).Tracks.Count);
        Assert.Equal(2, Assert.Single(d3.CurrentMedium!.Sessions).Tracks.Count);
    }

    [Fact]
    public async Task Cli_MultiDriveValidation_RejectsInvalidCombinations()
    {
        using var dm = CreateThreeDriveManager(() => VirtualDiscMedium.CreateBlankCdR700());
        string isoPath = await CreateTestIsoAsync("val.iso");

        var (exit1, _, _, err1) = await RunCliAsync(dm, "burn-iso", "--drive", "VDRV-01", "--drives", "VDRV-01,VDRV-02", "--iso", isoPath);
        Assert.Equal(1, exit1);
        Assert.Contains("Cannot combine --drive and --drives", err1);

        var (exit2, _, _, err2) = await RunCliAsync(dm, "burn-iso", "--drives", "VDRV-01,VDRV-01", "--iso", isoPath);
        Assert.Equal(1, exit2);
        Assert.Contains("Duplicate drive", err2);

        var (exit3, _, _, err3) = await RunCliAsync(dm, "duplicate", "--source", "VDRV-01", "--drives", "VDRV-01,VDRV-02");
        Assert.Equal(1, exit3);
        Assert.Contains("cannot also be in --drives", err3);
    }

    [Fact]
    public async Task ImageStudioViewModel_MultiDriveSelection_BurnsAndUpdatesPerDriveRows()
    {
        using var dm = CreateThreeDriveManager(() => VirtualDiscMedium.CreateBlankDvdR47());
        string isoPath = await CreateTestIsoAsync("vm_img.iso");

        var vm = new ImageStudioViewModel(dm, new VirtualBurnEngine())
        {
            SourceImagePath = isoPath,
            UseDirectDriveCommands = true,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "vm-img-logs")
        };

        Assert.Equal(3, vm.MultiDriveTargets.Count);
        vm.MultiDriveTargets[0].IsSelected = true;
        vm.MultiDriveTargets[1].IsSelected = true;
        vm.MultiDriveTargets[2].IsSelected = true;
        Assert.True(vm.HasMultiDriveSelection);

        var res = await vm.BurnImageToDiscAsync();

        Assert.True(res.Success, res.ErrorMessage);
        Assert.NotNull(vm.LastMultiDriveResult);
        Assert.True(vm.LastMultiDriveResult!.AllSucceeded);
        Assert.Equal(3, vm.LastMultiDriveResult.SucceededCount);
        Assert.All(vm.MultiDriveTargets, t =>
        {
            Assert.Equal(MultiDriveBurnStatus.Succeeded, t.Status);
            Assert.Equal(100.0, t.ProgressPercent);
            Assert.False(t.IsBusy);
        });
    }

    [Fact]
    public async Task AudioBurnViewModel_MultiDriveSelection_BurnsAndUpdatesPerDriveRows_WithFaultIsolation()
    {
        using var dm = CreateThreeDriveManager(() => VirtualDiscMedium.CreateBlankCdR700());
        var d2 = (VirtualDiscRecorder)dm.GetRecorderById("VDRV-02")!;
        d2.ConfigureVirtualDrive = mmc => mmc.WriteErrorAtLba = 10;

        string wav1 = CreateTestWav("vm_a1.wav", 300, seed: 3);
        string wav2 = CreateTestWav("vm_a2.wav", 310, seed: 4);

        var vm = new AudioBurnViewModel(dm, new VirtualBurnEngine())
        {
            AlbumTitle = "Multi Album",
            AlbumPerformer = "Multi Artist",
            UseDirectDriveCommands = true,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "vm-audio-logs")
        };
        vm.AddAudioFiles(new[] { wav1, wav2 });

        Assert.Equal(3, vm.MultiDriveTargets.Count);
        foreach (var t in vm.MultiDriveTargets)
        {
            t.IsSelected = true;
        }

        var res = await vm.StartAudioBurnAsync();

        Assert.False(res.Success);
        Assert.NotNull(vm.LastMultiDriveResult);
        Assert.Equal(2, vm.LastMultiDriveResult!.SucceededCount);
        Assert.Equal(1, vm.LastMultiDriveResult.FailedCount);
        Assert.Equal(MultiDriveBurnStatus.Succeeded, vm.MultiDriveTargets[0].Status);
        Assert.Equal(MultiDriveBurnStatus.Failed, vm.MultiDriveTargets[1].Status);
        Assert.Equal(MultiDriveBurnStatus.Succeeded, vm.MultiDriveTargets[2].Status);
    }
}

[Collection(WpfApplicationCollection.Name)]
public sealed class MultiDriveWpfViewTests
{
    [Fact]
    public void ImageStudioView_And_AudioBurnView_RenderMultiDriveTargets_WithoutErrors()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }
                ThemeManager.ApplyTheme(ThemeMode.Dark);

                using var dm = new DiscDeviceManager(enablePhysicalEnumeration: false);
                dm.AddVirtualRecorder(new VirtualDiscRecorder("VDRV-02", "IGNIS", "VIRTUAL B", @"W:\"));

                var imgVm = new ImageStudioViewModel(dm, new VirtualBurnEngine()) { IsMultiDriveExpanded = true };
                var imgView = new ImageStudioView { DataContext = imgVm };
                imgView.Measure(new Size(1280, 760));
                imgView.Arrange(new Rect(0, 0, 1280, 760));
                imgView.UpdateLayout();

                var audioVm = new AudioBurnViewModel(dm, new VirtualBurnEngine()) { IsMultiDriveExpanded = true };
                var audioView = new AudioBurnView { DataContext = audioVm };
                audioView.Measure(new Size(1280, 760));
                audioView.Arrange(new Rect(0, 0, 1280, 760));
                audioView.UpdateLayout();

                Assert.Equal(2, imgVm.MultiDriveTargets.Count);
                Assert.Equal(2, audioVm.MultiDriveTargets.Count);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw new Exception("STA WPF multi-drive test failed.", failure);
        }
    }
}
