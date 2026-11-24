using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Opt-in CD-R overburning at the two user-facing layers: the command line (<c>--allow-overburn</c>,
/// <c>--max-overburn-minutes</c>) and the Audio CD screen's switch with its confirmation warning.
/// </summary>
public sealed class OverburnCliAndWpfTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_overburn_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public OverburnCliAndWpfTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    // ---- Command line ----

    [Theory]
    [InlineData("allow-overburn")]
    public void AllowOverburn_IsAKnownFlag_ThatDoesNotConsumeTheNextToken(string flag)
    {
        var parsed = CliCommandRunner.CommandLineArgs.Parse([$"--{flag}", "burn-audio", "--drive", "VDRV-01"]);
        Assert.Equal("burn-audio", parsed.Subcommand);
        Assert.True(parsed.HasFlag(flag));
        Assert.Equal("VDRV-01", parsed.GetOption("drive"));
    }

    [Fact]
    public void MaxOverburnMinutes_IsAValueOption()
    {
        var parsed = CliCommandRunner.CommandLineArgs.Parse(["burn-audio", "--allow-overburn", "--max-overburn-minutes", "2.5", "--drive", "VDRV-01"]);
        Assert.True(parsed.HasFlag("allow-overburn"));
        Assert.Equal("2.5", parsed.GetOption("max-overburn-minutes"));
        Assert.Equal("VDRV-01", parsed.GetOption("drive"));
    }

    private string WriteAlbum(int sectorsPerTrack = 300)
    {
        for (int i = 1; i <= 2; i++)
        {
            byte[] pcm = new byte[sectorsPerTrack * OpticalConstants.RawSectorSize];
            new Random(i).NextBytes(pcm);
            WavFileCodec.WriteWavFile(Path.Combine(_tempDir, $"{i:D2}.wav"), pcm);
        }
        string cue = Path.Combine(_tempDir, "album.cue");
        File.WriteAllLines(cue,
        [
            "PERFORMER \"Band\"", "TITLE \"Album\"",
            "FILE \"01.wav\" WAVE", "  TRACK 01 AUDIO", "    TITLE \"One\"", "    INDEX 01 00:00:00",
            "FILE \"02.wav\" WAVE", "  TRACK 02 AUDIO", "    TITLE \"Two\"", "    INDEX 01 00:00:00"
        ]);
        return cue;
    }

    private async Task<(int Exit, string Out, string Err)> Cli(VirtualDiscRecorder recorder, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, new OneDevice(recorder), new VirtualBurnEngine(recorder));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private (VirtualDiscRecorder Recorder, string Cue) CdRAndAlbum()
    {
        var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        recorder.InsertMedia(new VirtualDiscMedium(DiscMediaType.CdR, 20_000, isBlank: true));
        return (recorder, WriteAlbum());
    }

    [Theory]
    [InlineData(new[] { "--allow-overburn", "--dao" }, "--native")]
    [InlineData(new[] { "--allow-overburn", "--native" }, "--dao")]
    [InlineData(new[] { "--max-overburn-minutes", "3", "--native", "--dao" }, "--allow-overburn")]
    [InlineData(new[] { "--allow-overburn", "--native", "--dao", "--max-overburn-minutes", "0" }, "--max-overburn-minutes")]
    [InlineData(new[] { "--allow-overburn", "--native", "--dao", "--max-overburn-minutes", "21" }, "--max-overburn-minutes")]
    [InlineData(new[] { "--allow-overburn", "--native", "--dao", "--max-overburn-minutes", "abc" }, "--max-overburn-minutes")]
    public async Task BurnAudio_RefusesOverburnOptionsThatDoNotMakeSense_BeforeTouchingTheDisc(string[] options, string mentioned)
    {
        var (recorder, cue) = CdRAndAlbum();
        using (recorder)
        {
            var result = await Cli(recorder, ["burn-audio", "--drive", "VDRV-01", "--tracks", cue, .. options]);

            Assert.NotEqual(0, result.Exit);
            Assert.Contains(mentioned, result.Out + result.Err);
            Assert.True(recorder.CurrentMedium!.IsBlank);
        }
    }

    [Fact]
    public async Task BurnAudio_WithTheFullOverburnOptions_IsAcceptedAndBurnsAProgramThatFits()
    {
        var (recorder, cue) = CdRAndAlbum();
        using (recorder)
        {
            var result = await Cli(recorder, "burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--allow-overburn", "--max-overburn-minutes", "3", "--log-dir", Path.Combine(_tempDir, "logs"), "--json");

            Assert.True(result.Exit == 0, result.Out + result.Err);
            Assert.False(recorder.CurrentMedium!.IsBlank);
        }
    }

    [Fact]
    public async Task BurnAudio_ProgramPastTheDisc_IsRefusedWithoutTheFlag_AndStillRefusedByTheSimulatedDriveWithIt()
    {
        // The disc's rated size is 500 sectors; the album needs 600.
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        recorder.InsertMedia(new VirtualDiscMedium(DiscMediaType.CdR, 500, isBlank: true));
        string cue = WriteAlbum();
        string logs = Path.Combine(_tempDir, "logs");

        var off = await Cli(recorder, "burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--log-dir", logs, "--json");
        Assert.NotEqual(0, off.Exit);
        Assert.True((off.Out + off.Err).Contains("exceeding media free capacity"), off.Out + off.Err);
        Assert.True(recorder.CurrentMedium!.IsBlank);

        // The simulated drive refuses a cue sheet past the ATIP (like most real drives), so nothing is written either way.
        var on = await Cli(recorder, "burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--allow-overburn", "--log-dir", logs, "--json");
        Assert.NotEqual(0, on.Exit);
        Assert.DoesNotContain("Overburning is off", on.Out + on.Err);
        Assert.True(recorder.CurrentMedium!.IsBlank);
    }

    // ---- Audio CD screen ----

    private AudioBurnViewModel CreateViewModel(int trackSectors = 300)
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Overburn Album",
            AlbumPerformer = "Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "vmlogs")
        };
        var files = new List<string>();
        for (int i = 1; i <= 2; i++)
        {
            byte[] pcm = new byte[trackSectors * OpticalConstants.RawSectorSize];
            new Random(i).NextBytes(pcm);
            string wav = Path.Combine(_tempDir, $"Track{i:D2}.wav");
            WavFileCodec.WriteWavFile(wav, pcm);
            files.Add(wav);
        }
        vm.AddAudioFilesCommand.Execute(files.ToArray());
        Assert.Equal(2, vm.Tracks.Count);
        return vm;
    }

    private VirtualDiscRecorder VirtualRecorder() =>
        _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");

    /// <summary>Puts a CD-R of 500 sectors in the virtual drive: the 2 x 300 sector album needs 600.</summary>
    private VirtualDiscMedium InsertSmallCdR()
    {
        var medium = new VirtualDiscMedium(DiscMediaType.CdR, 500, isBlank: true);
        VirtualRecorder().InsertMedia(medium);
        return medium;
    }

    [Fact]
    public void OverburnSwitch_IsOffByDefault_AndNeedsDirectDriveCommands()
    {
        var vm = CreateViewModel();
        Assert.False(vm.AllowOverburn);
        Assert.Equal(3.0, vm.OverburnMaxMinutes);

        vm.AllowOverburn = true;
        Assert.False(vm.AllowOverburn); // direct drive commands are off

        vm.UseDirectDriveCommands = true;
        vm.AllowOverburn = true;
        Assert.True(vm.AllowOverburn);

        vm.UseDirectDriveCommands = false;
        Assert.False(vm.AllowOverburn);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(100.0, 20.0)]
    [InlineData(4.0, 4.0)]
    public void OverburnMaxMinutes_IsKeptInTheSupportedRange(double asked, double kept)
    {
        var vm = CreateViewModel();
        vm.OverburnMaxMinutes = asked;
        Assert.Equal(kept, vm.OverburnMaxMinutes);
    }

    [Fact]
    public async Task WithoutTheSwitch_TooLongAProgramIsNeverConfirmedAndNeverWritten()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        var medium = InsertSmallCdR();
        int asked = 0;
        vm.ConfirmOverburn = _ => { asked++; return true; };

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success, vm.StatusMessage);
        Assert.Equal(0, asked);
        Assert.Contains("exceeding media free capacity", result.ErrorMessage);
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task WithTheSwitch_TheWarningShowsCapacityImageSizeExcessAndRisk_AndDecliningWritesNothing()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        vm.AllowOverburn = true;
        var medium = InsertSmallCdR();
        CdOverburnWarning? shown = null;
        vm.ConfirmOverburn = w => { shown = w; return false; };

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Cancelled, result.Status);
        Assert.True(medium.IsBlank);
        Assert.NotNull(shown);
        Assert.Equal(500, shown.NominalCapacitySectors);
        Assert.Equal(600, shown.ProgramSectors); // two 300-sector tracks, no gap between them in Disc-at-Once mode
        Assert.Equal(100, shown.ExcessSectors);
        Assert.True(shown.NeedsOverburn);
        Assert.False(shown.ExceedsLimit);

        string text = shown.Message;
        Assert.Contains("Rated disc capacity: 00:07 (500 sectors, 1.0 MB of data)", text);
        Assert.Contains("Image size: 00:08 (600 sectors, 1.2 MB of data)", text);
        Assert.Contains("Overburn excess: 100 sectors = 0.2 MB or 00:02 (mm:ss)", text);
        Assert.Contains("Overburn limit: 13,500 sectors (03:00)", text);
        Assert.Contains("99:59:74", text);
        Assert.Contains("Risk:", text);
        Assert.Contains("refuse", text); // a drive may refuse
        Assert.Contains("cannot be read", text);
    }

    [Fact]
    public async Task AcceptedWarning_PassesTheOptInToTheDirectWriter()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        vm.AllowOverburn = true;
        var medium = InsertSmallCdR();
        vm.ConfirmOverburn = _ => true;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        // The simulated drive, like most real ones, refuses a cue sheet past the ATIP: the refusal comes from the
        // drive's cue sheet check, not from the host's "overburning is off" check, so the opt-in reached the writer.
        Assert.False(result.Success, vm.StatusMessage);
        Assert.DoesNotContain("Overburning is off", result.ErrorMessage);
        Assert.Contains("cue sheet", result.ErrorMessage);
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task ProgramThatFits_NeedsNoConfirmation()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        vm.AllowOverburn = true;
        int asked = 0;
        vm.ConfirmOverburn = _ => { asked++; return false; };

        var result = await vm.StartAudioBurnAsync("VDRV-01"); // the default blank 80-minute CD-R

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task OverburnOnDvd_IsRefusedByTheScreen()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        vm.AllowOverburn = true;
        var dvd = VirtualDiscMedium.CreateBlankDvdR47();
        VirtualRecorder().InsertMedia(dvd);
        vm.ConfirmOverburn = _ => true;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.Contains("only allowed on a CD-R", result.ErrorMessage);
        Assert.True(dvd.IsBlank);
    }

    [Fact]
    public void Warning_ForAProgramPastTheLimit_SaysItWillBeRefused()
    {
        var warning = new CdOverburnWarning(359_850, 359_850 + 13_501, new CdOverburnPolicy(true));
        Assert.True(warning.ExceedsLimit);
        Assert.Contains("will be refused", warning.Message);
        Assert.Contains("Overburn excess: 13,501 sectors", warning.Message);
    }

    private sealed class OneDevice(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
