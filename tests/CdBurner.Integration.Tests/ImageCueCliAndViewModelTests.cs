using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ImageCueCliAndViewModelTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly VirtualDiscMedium _medium;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;
    private readonly byte[] _track1Pcm;
    private readonly byte[] _track2Pcm;

    public ImageCueCliAndViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_ImageCueInt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(_medium);
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);

        _track1Pcm = SinePcm(440.0, 75);
        _track2Pcm = SinePcm(880.0, 150);
    }

    public async Task InitializeAsync()
    {
        string wav1 = Path.Combine(_tempDir, "in1.wav");
        string wav2 = Path.Combine(_tempDir, "in2.wav");
        WavFileCodec.WriteWavFile(wav1, _track1Pcm);
        WavFileCodec.WriteWavFile(wav2, _track2Pcm);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Image Mode Album",
            AlbumPerformer = "Integration Band",
            Songwriter = "Band Writer",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };
        compilation.AddTrack(new AudioCdTrackItem(1, wav1, "Opening", "Integration Band", "USAA12600201", PreGapFrames: 150));
        compilation.AddTrack(new AudioCdTrackItem(2, wav2, "Closing", "Integration Band", "USAA12600202", PreGapFrames: 0));

        using var decoder = new MediaFoundationAudioDecoder();
        var burnResult = await new AudioCdBurnService(_burnEngine, decoder).BurnCompilationAsync(_recorder, compilation);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _recorder.Dispose();
        _medium.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static byte[] SinePcm(double frequency, int frameCount)
    {
        int samples = frameCount * OpticalConstants.CdAudioSamplesPerFrame;
        byte[] pcm = new byte[samples * 4];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)Math.Round(12000 * Math.Sin(2.0 * Math.PI * frequency * i / OpticalConstants.CdAudioSampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }
        return pcm;
    }

    [Fact]
    public async Task Cli_RipAudio_ModeImageCue_Flac_Json_WritesSingleImageCueAndValidatesLog()
    {
        string outDir = Path.Combine(_tempDir, "cli-image-cue-flac");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--mode", "image-cue", "--format", "flac", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.True(exit == 0, $"Expected exit 0, got {exit}. stdout={stdout} stderr={stderr}");
        Assert.Empty(stderr.ToString());

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("image-cue", root.GetProperty("mode").GetString());
        Assert.Equal("FLAC", root.GetProperty("format").GetString());

        string imagePath = root.GetProperty("imageFile").GetString()!;
        string cuePath = root.GetProperty("cueSheet").GetString()!;
        string logPath = root.GetProperty("ripLog").GetString()!;

        Assert.True(File.Exists(imagePath));
        Assert.True(File.Exists(cuePath));
        Assert.True(File.Exists(logPath));
        Assert.EndsWith(".flac", imagePath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(outDir, "*.flac"));

        var reloaded = new AudioCdCompilation();
        Assert.Equal(2, reloaded.ImportCueSheet(cuePath));
        using var s1 = reloaded.Tracks[0].CustomPcmStreamFactory!();
        using var s2 = reloaded.Tracks[1].CustomPcmStreamFactory!();
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        s1.CopyTo(ms1);
        s2.CopyTo(ms2);
        Assert.Equal(_track1Pcm, ms1.ToArray());
        Assert.Equal(_track2Pcm, ms2.ToArray());

        Assert.True(RipLogValidator.ValidateFile(logPath).IsValid);
    }

    [Theory]
    [InlineData("--format", "mp3")]
    [InlineData("--format", "wav,flac")]
    [InlineData("--tracks", "1")]
    [InlineData("--mode", "bogus")]
    public async Task Cli_RipAudio_ModeImageCue_InvalidArguments_ReturnsExitOne(string flag, string value)
    {
        string outDir = Path.Combine(_tempDir, "cli-invalid-" + Guid.NewGuid().ToString("N"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--mode", "image-cue", flag, value },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exit);
        Assert.False(string.IsNullOrWhiteSpace(stderr.ToString()));
    }

    [Fact]
    public async Task ViewModel_ImageCueMode_PersistsSetting_RipsFlacAndLog()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);

        var vm = new AudioBurnViewModel(_deviceService, _burnEngine, settingsService: settingsService)
        {
            LookupMetadata = false,
            VerifyAccurateRip = false,
            SecureRip = false,
            RipMode = AudioRipMode.ImageCue,
            RipAsFlac = true
        };

        Assert.True(vm.IsImageCueMode);
        Assert.False(vm.IsTrackByTrackMode);
        Assert.False(vm.RipAsWav);
        Assert.True(vm.RipAsFlac);
        Assert.False(vm.RipBitrateApplies);
        Assert.False(vm.RipMp3Selected);
        Assert.Equal("image-cue", settingsService.CurrentSettings.RipMode);

        // Reloading settings restores ImageCue mode
        var reloadedSettings = new AppSettingsService(settingsPath);
        reloadedSettings.Load();
        var vmReloaded = new AudioBurnViewModel(_deviceService, _burnEngine, settingsService: reloadedSettings);
        Assert.True(vmReloaded.IsImageCueMode);
        Assert.True(vmReloaded.RipAsFlac);

        string outDir = Path.Combine(_tempDir, "vm-image-cue");
        var ripResult = await vm.RipAudioCdAsync("VDRV-01", outDir);
        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(AudioRipMode.ImageCue, ripResult.Mode);
        Assert.NotNull(ripResult.ImageFilePath);
        Assert.True(File.Exists(ripResult.ImageFilePath));
        Assert.NotNull(ripResult.RipLogPath);
        Assert.True(File.Exists(ripResult.RipLogPath));
        Assert.True(RipLogValidator.ValidateFile(ripResult.RipLogPath!).IsValid);
        Assert.Contains("Rip Log:", vm.RipReport);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void AudioBurnView_RipModeToggle_SwitchesModesWithoutBindingErrors()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                using var deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
                var vm = new AudioBurnViewModel(deviceManager, new VirtualBurnEngine());
                var view = new AudioBurnView { DataContext = vm };
                view.Measure(new Size(1100, 900));
                view.Arrange(new Rect(0, 0, 1100, 900));
                view.UpdateLayout();

                var tabs = FindVisualChildren<TabControl>(view).First();
                var ripTab = tabs.Items.OfType<TabItem>()
                    .First(t => (t.Header?.ToString() ?? string.Empty).Contains("Audio CD Ripper", StringComparison.OrdinalIgnoreCase));
                tabs.SelectedItem = ripTab;
                view.UpdateLayout();

                var trackRadio = FindVisualChildren<RadioButton>(view)
                    .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Track-by-track", StringComparison.Ordinal));
                var imageRadio = FindVisualChildren<RadioButton>(view)
                    .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Single-file Image + CUE", StringComparison.Ordinal));
                var mp3Check = FindVisualChildren<CheckBox>(view)
                    .FirstOrDefault(c => string.Equals(AutomationProperties.GetName(c), "Save as MP3", StringComparison.Ordinal));

                Assert.NotNull(trackRadio);
                Assert.NotNull(imageRadio);
                Assert.NotNull(mp3Check);
                Assert.True(trackRadio!.IsChecked);
                Assert.True(mp3Check!.IsEnabled);

                imageRadio!.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                view.UpdateLayout();

                Assert.True(vm.IsImageCueMode);
                Assert.True(imageRadio.IsChecked);
                Assert.False(trackRadio.IsChecked);
                Assert.False(mp3Check.IsEnabled);

                trackRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                view.UpdateLayout();

                Assert.False(vm.IsImageCueMode);
                Assert.True(trackRadio.IsChecked);
                Assert.True(mp3Check.IsEnabled);
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
            throw new AggregateException(failure);
        }
    }

    private static void EnsureAppResources()
    {
        if (Application.Current == null)
        {
            var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
        }
        else if (!Application.Current.Resources.Contains("AccentButtonStyle"))
        {
            var appResources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
            Application.Current.Resources.MergedDictionaries.Add(appResources);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class TestDeviceService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) =>
            includeVirtual ? recorders : recorders.Where(r => !r.IsVirtual).ToList();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }
}
