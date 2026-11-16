using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class EnhancedCdUiAndCliTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _source;
    private readonly VirtualDiscRecorder _target;
    private readonly TwoDriveService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public EnhancedCdUiAndCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_enhcd_ui_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _source = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL A", @"V:\");
        _target = new VirtualDiscRecorder("VDRV-02", "IGNIS", "VIRTUAL B", @"W:\");
        _deviceService = new TwoDriveService(_source, _target);
        _burnEngine = new VirtualBurnEngine(_source);
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] CreateNoisePcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    [Fact]
    [Trait("Category", "UI")]
    public void ImageStudioView_DiscCopyTab_ExplainsWhatIsCopiedForEachDiscKind()
    {
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();

                var vm = new ImageStudioViewModel(_deviceService, _burnEngine);
                var view = new ImageStudioView { DataContext = vm };

                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var tabs = FindVisualChildren<TabControl>(view).First();
                var copyTab = tabs.Items.OfType<TabItem>().First(t => (t.Header?.ToString() ?? string.Empty).Contains("Disc Copy", StringComparison.OrdinalIgnoreCase));
                tabs.SelectedItem = copyTab;

                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var summaryBlock = FindVisualChildren<TextBlock>(view)
                    .FirstOrDefault(tb => string.Equals(AutomationProperties.GetName(tb), "What will be copied", StringComparison.Ordinal));
                Assert.NotNull(summaryBlock);

                var directCheckBox = FindVisualChildren<CheckBox>(view)
                    .FirstOrDefault(cb => string.Equals(AutomationProperties.GetName(cb), "Copy with direct drive commands", StringComparison.Ordinal));
                Assert.NotNull(directCheckBox);

                // 1. Blank disc
                _source.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                Assert.Contains("blank", vm.CopySourceSummary, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(vm.CopySourceSummary, summaryBlock!.Text);

                // 2. Audio CD
                using var audioMedium = VirtualDiscMedium.CreateBlankCdR700();
                audioMedium.IsBlank = false;
                audioMedium.IsFinalized = true;
                audioMedium.UsedSectors = 600;
                var audioSession = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 600 };
                audioSession.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 300, IsAudio = true });
                audioSession.Tracks.Add(new VirtualTrack { TrackNumber = 2, StartLba = 300, SectorCount = 300, IsAudio = true });
                audioMedium.Sessions.Add(audioSession);
                _source.InsertMedia(audioMedium);
                Assert.Contains("Audio CD (2 track(s))", vm.CopySourceSummary);

                // 3. Data disc
                using var dataMedium = VirtualDiscMedium.CreateBlankCdR700();
                dataMedium.IsBlank = false;
                dataMedium.IsFinalized = true;
                dataMedium.UsedSectors = 400;
                var dataSession = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 400 };
                dataSession.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 400, IsAudio = false });
                dataMedium.Sessions.Add(dataSession);
                _source.InsertMedia(dataMedium);
                Assert.Contains("Data", vm.CopySourceSummary);
                Assert.Contains("400 sectors", vm.CopySourceSummary);

                // 4. Mixed-mode CD with direct drive commands off vs on
                using var mixedMedium = VirtualDiscMedium.CreateBlankCdR700();
                mixedMedium.IsBlank = false;
                mixedMedium.IsFinalized = true;
                mixedMedium.UsedSectors = 750;
                var mixedSession = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 750 };
                mixedSession.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 300, IsAudio = false });
                mixedSession.Tracks.Add(new VirtualTrack { TrackNumber = 2, StartLba = 450, SectorCount = 300, IsAudio = true });
                mixedMedium.Sessions.Add(mixedSession);
                _source.InsertMedia(mixedMedium);

                vm.UseDirectDriveCommands = false;
                Assert.Contains("Track 1 data will be skipped", vm.CopySourceSummary);
                vm.UseDirectDriveCommands = true;
                Assert.Contains("track 1 data and all audio tracks with CD-Text will be copied together", vm.CopySourceSummary);

                // 5. Enhanced CD with direct drive commands off vs on
                using var enhMedium = VirtualDiscMedium.CreateBlankCdR700();
                enhMedium.IsBlank = false;
                enhMedium.IsFinalized = true;
                enhMedium.UsedSectors = 12_350;
                var s1 = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 600 };
                s1.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 300, IsAudio = true });
                s1.Tracks.Add(new VirtualTrack { TrackNumber = 2, StartLba = 300, SectorCount = 300, IsAudio = true });
                var s2 = new VirtualSession { SessionNumber = 2, StartLba = 12_000, SectorCount = 350 };
                s2.Tracks.Add(new VirtualTrack { TrackNumber = 3, StartLba = 12_000, SectorCount = 350, IsAudio = false });
                enhMedium.Sessions.Add(s1);
                enhMedium.Sessions.Add(s2);
                _source.InsertMedia(enhMedium);

                vm.UseDirectDriveCommands = false;
                Assert.Contains("Session 2 data will be skipped", vm.CopySourceSummary);
                vm.UseDirectDriveCommands = true;
                Assert.Contains("session 2 data files will be read and rewritten at the new disc's next writable address", vm.CopySourceSummary);
                Assert.Equal(vm.CopySourceSummary, summaryBlock.Text);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException != null)
        {
            throw new Xunit.Sdk.XunitException($"STA WPF verification failed: {staException}");
        }
    }

    [Fact]
    public async Task AudioBurnViewModel_DataSessionFolder_CalculatesCapacityAndBurnsEnhancedCd()
    {
        string wav1 = Path.Combine(_tempDir, "t1.wav");
        string wav2 = Path.Combine(_tempDir, "t2.wav");
        WavFileCodec.WriteWavFile(wav1, CreateNoisePcm(1, 300));
        WavFileCodec.WriteWavFile(wav2, CreateNoisePcm(2, 300));

        string bonusDir = Path.Combine(_tempDir, "bonus");
        Directory.CreateDirectory(bonusDir);
        byte[] bonusBytes = new byte[4096];
        new Random(7).NextBytes(bonusBytes);
        File.WriteAllBytes(Path.Combine(bonusDir, "notes.txt"), bonusBytes);

        _source.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var vm = new AudioBurnViewModel(_deviceService, _burnEngine)
        {
            SelectedRecorder = _source,
            AlbumTitle = "Live Concert",
            AlbumPerformer = "Ensemble",
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };

        vm.AddAudioFiles(new[] { wav1, wav2 });
        double audioOnlyUsage = vm.DiscUsagePercent;

        // Mutual exclusion: DataSessionFolderPath turns on UseDirectDriveCommands and clears IncludeRecoverySession.
        vm.IncludeRecoverySession = true;
        Assert.True(vm.IncludeRecoverySession);

        vm.DataSessionFolderPath = bonusDir;
        Assert.False(vm.IncludeRecoverySession);
        Assert.True(vm.UseDirectDriveCommands);
        Assert.True(vm.HasDataSessionFolder);

        var plan = AudioCdBurnService.PlanDataSessionFolder(bonusDir, vm.DataSessionVolumeLabel, 600);
        double expectedUsage = (double)(600 + 11_400 + plan.PaddedTrackSectors + 2) / OpticalConstants.Cd700MbSectors * 100.0;
        Assert.True(vm.DiscUsagePercent > audioOnlyUsage);
        Assert.InRange(vm.DiscUsagePercent, expectedUsage - 0.01, expectedUsage + 0.01);
        Assert.Contains("11,400-sector session gap", vm.DataSessionSummaryText);

        // Enabling IncludeRecoverySession clears DataSessionFolderPath.
        vm.IncludeRecoverySession = true;
        Assert.False(vm.HasDataSessionFolder);
        Assert.Equal(string.Empty, vm.DataSessionFolderPath);

        // Set folder back and burn end-to-end.
        vm.DataSessionFolderPath = bonusDir;
        var burnResult = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Contains("Enhanced CD burned", vm.StatusMessage);
        Assert.Equal(2, _source.CurrentMedium!.Sessions.Count);
        Assert.True(_source.CurrentMedium.IsFinalized);
    }

    [Fact]
    public async Task Cli_BurnAudioDataFolder_And_CopyDiscNativeJson_RoundTrip()
    {
        byte[] pcm1 = CreateNoisePcm(11, 300);
        byte[] pcm2 = CreateNoisePcm(22, 300);
        string wav1 = Path.Combine(_tempDir, "a1.wav");
        string wav2 = Path.Combine(_tempDir, "a2.wav");
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        string dataDir = Path.Combine(_tempDir, "extra_files");
        Directory.CreateDirectory(Path.Combine(dataDir, "Docs"));
        byte[] docBytes = new byte[3000];
        new Random(55).NextBytes(docBytes);
        File.WriteAllBytes(Path.Combine(dataDir, "Docs", "Info.bin"), docBytes);

        _source.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        _target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        // 1. burn-audio with --data-folder and --data-label
        var burnOut = new StringWriter();
        var burnErr = new StringWriter();
        int burnExit = await CliCommandRunner.RunAsync(
            new[]
            {
                "burn-audio",
                "--drive", "VDRV-01",
                "--tracks", $"{wav1},{wav2}",
                "--title", "CLI Enhanced",
                "--artist", "CLI Artist",
                "--data-folder", dataDir,
                "--data-label", "BONUS_DATA",
                "--native",
                "--log-dir", Path.Combine(_tempDir, "cli_logs"),
                "--json"
            },
            burnOut,
            burnErr,
            _deviceService,
            _burnEngine);

        Assert.True(burnExit == 0, burnOut.ToString() + burnErr.ToString());
        using var burnJson = ParseTrailingJson(burnOut.ToString());
        Assert.True(burnJson.RootElement.GetProperty("success").GetBoolean());
        Assert.True(burnJson.RootElement.GetProperty("dataSessionWritten").GetBoolean());
        Assert.True(burnJson.RootElement.GetProperty("dataSessionSectors").GetInt32() > 0);

        // 2. copy-disc --native --json
        var copyOut = new StringWriter();
        var copyErr = new StringWriter();
        int copyExit = await CliCommandRunner.RunAsync(
            new[]
            {
                "copy-disc",
                "--source", "VDRV-01",
                "--target", "VDRV-02",
                "--native",
                "--log-dir", Path.Combine(_tempDir, "copy_logs"),
                "--json"
            },
            copyOut,
            copyErr,
            _deviceService,
            _burnEngine);

        Assert.True(copyExit == 0, copyOut.ToString() + copyErr.ToString());
        using var copyJson = ParseTrailingJson(copyOut.ToString());
        Assert.True(copyJson.RootElement.GetProperty("success").GetBoolean());
        Assert.True(copyJson.RootElement.GetProperty("enhancedCd").GetBoolean());
        Assert.True(copyJson.RootElement.GetProperty("copiedDataSession").GetBoolean());

        var targetMedium = _target.CurrentMedium!;
        Assert.Equal(2, targetMedium.Sessions.Count);
        Assert.True(targetMedium.IsFinalized);

        // Verify Session 1 audio + track layout on VDRV-02
        var sourceTracks = _source.CurrentMedium!.Sessions[0].Tracks;
        var targetTracks = targetMedium.Sessions[0].Tracks;
        Assert.Equal(sourceTracks.Count, targetTracks.Count);
        for (int i = 0; i < sourceTracks.Count; i++)
        {
            Assert.Equal(sourceTracks[i].StartLba, targetTracks[i].StartLba);
            Assert.Equal(sourceTracks[i].SectorCount, targetTracks[i].SectorCount);
            Assert.Equal(sourceTracks[i].Index00Lba, targetTracks[i].Index00Lba);
        }

        var rip = await new AudioCdRipperService().RipAudioCdAsync(
            _target,
            new VirtualBurnEngine(_target),
            Path.Combine(_tempDir, "rip_vdrv02"),
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = true });
        Assert.True(rip.Success, rip.ErrorMessage);
        byte[] ripped1 = File.ReadAllBytes(rip.RippedFilePaths[0])[44..];
        Assert.Equal(pcm1, ripped1[..pcm1.Length]);
        Assert.All(ripped1[pcm1.Length..], b => Assert.Equal(0, b));
        Assert.Equal(pcm2, File.ReadAllBytes(rip.RippedFilePaths[1])[44..]);
        Assert.Equal("CLI Enhanced", rip.CdText?.AlbumTitle);
        Assert.Equal("CLI Artist", rip.CdText?.AlbumPerformer);

        // Verify Session 2 ISO on VDRV-02
        int s2Start = targetMedium.Sessions[1].StartLba;
        int s2Count = targetMedium.Sessions[1].SectorCount;
        using var isoStream = new MemoryStream((s2Start + s2Count) * OpticalConstants.DataSectorSize);
        isoStream.SetLength((long)(s2Start + s2Count) * OpticalConstants.DataSectorSize);
        byte[] sec = new byte[OpticalConstants.DataSectorSize];
        for (int i = 0; i < s2Count; i++)
        {
            targetMedium.ReadSector(s2Start + i, sec, OpticalConstants.DataSectorSize);
            isoStream.Position = (long)(s2Start + i) * OpticalConstants.DataSectorSize;
            isoStream.Write(sec);
        }
        isoStream.Position = 0;

        using var iso = IsoImageReader.FromStream(isoStream, leaveOpen: true, sessionStartLba: s2Start);
        Assert.Equal("BONUS_DATA", iso.VolumeLabel);
        var infoEntry = iso.FindFile("/Docs/Info.bin");
        Assert.NotNull(infoEntry);
        Assert.Equal(docBytes, iso.ReadFileBytes(infoEntry!));
    }

    private static JsonDocument ParseTrailingJson(string text)
    {
        int start = text.IndexOf('{');
        Assert.True(start >= 0, $"Expected JSON output, got: {text}");
        return JsonDocument.Parse(text[start..]);
    }

    private static void EnsureAppResources()
    {
        if (System.Windows.Application.Current == null)
        {
            var app = new CdBurner.App.App();
            app.InitializeComponent();
        }
        else if (!System.Windows.Application.Current.Resources.Contains("AccentButtonStyle"))
        {
            var appResources = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(appResources);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class TwoDriveService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }
}
