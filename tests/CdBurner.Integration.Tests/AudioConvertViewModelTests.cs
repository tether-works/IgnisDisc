using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The "Convert Audio" tab's view model. The conversion tests need an ffmpeg with the MP3 encoder (the optional
/// download, or the path in IGNISDISC_TEST_FFMPEG) and are skipped where there is none.
/// </summary>
public sealed class AudioConvertViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public AudioConvertViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_ConvertVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup of a temp folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string?> FindFfmpegWithMp3Async()
    {
        string? path = FfmpegLocator.FindFfmpegExecutable(Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG"));
        if (path == null)
        {
            return null;
        }

        try
        {
            return await FfmpegProcess.HasEncoderAsync(path, "libmp3lame") ? path : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private AudioConvertViewModel CreateViewModel(string ffmpeg)
    {
        var service = new AppSettingsService(Path.Combine(_tempDir, "settings.json"));
        service.CurrentSettings = new AppSettings { FfmpegExecutablePath = ffmpeg };
        return new AudioConvertViewModel(service);
    }

    private static void WriteTestWav(string path)
    {
        byte[] pcm = new byte[20 * OpticalConstants.RawSectorSize];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)((i * 7) % 253);
        WavFileCodec.WriteWavFile(path, pcm);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var vm = new AudioConvertViewModel();

        Assert.Empty(vm.Inputs);
        Assert.False(vm.ConvertToFlac);
        Assert.False(vm.ConvertToMp3);
        Assert.False(vm.ConvertToOpus);
        Assert.False(vm.ConvertToAac);
        Assert.False(vm.Overwrite);
        Assert.True(vm.Recursive);
        Assert.Equal("320 kbit/s", vm.SelectedMp3Quality);
        Assert.Equal(6, vm.Mp3QualityChoices.Count);
        Assert.False(vm.Mp3Selected);
        Assert.False(vm.BitrateApplies);
        Assert.Null(vm.NamingProblem);
    }

    [Fact]
    public void FormatChoices_EnableTheMatchingBoxes()
    {
        var vm = new AudioConvertViewModel();

        vm.ConvertToMp3 = true;
        Assert.True(vm.Mp3Selected);
        Assert.False(vm.BitrateApplies);

        vm.ConvertToOpus = true;
        Assert.True(vm.BitrateApplies);
        vm.ConvertToOpus = false;
        vm.ConvertToAac = true;
        Assert.True(vm.BitrateApplies);
    }

    [Fact]
    public void AddInputs_SkipsBlanksAndRepeats_AndRemoveAndClearWork()
    {
        var vm = new AudioConvertViewModel();
        string a = Path.Combine(_tempDir, "a.wav");
        string b = Path.Combine(_tempDir, "b.wav");

        vm.AddInputs([a, " ", a.ToUpperInvariant(), b]);

        Assert.Equal([a, b], vm.Inputs);
        vm.SelectedInput = a;
        vm.RemoveSelectedCommand.Execute(null);
        Assert.Equal([b], vm.Inputs);
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Inputs);
    }

    [Fact]
    public void NamingProblem_IsShownOnlyForANonEmptyBadTemplate()
    {
        var vm = new AudioConvertViewModel { NamingTemplate = "{bogus}" };
        Assert.Contains("Unknown token", vm.NamingProblem);

        vm.NamingTemplate = string.Empty;
        Assert.Null(vm.NamingProblem);
    }

    [Fact]
    public async Task Convert_WithNothingChosen_SaysWhatIsMissingAndWritesNothing()
    {
        var vm = new AudioConvertViewModel { OutputFolder = Path.Combine(_tempDir, "out-none") };

        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.Contains("at least one output format", vm.StatusMessage);
        Assert.Contains("Conversion not started", vm.Report);

        vm.ConvertToFlac = true;
        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.Contains("Add files or a folder", vm.StatusMessage);
        Assert.False(Directory.Exists(vm.OutputFolder));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Convert_WithABadBitrate_IsRefusedBeforeAnyFileIsTouched()
    {
        var vm = new AudioConvertViewModel { OutputFolder = Path.Combine(_tempDir, "out-bad"), ConvertToOpus = true, BitrateKbps = "abc" };
        vm.AddInputs([Path.Combine(_tempDir, "x.wav")]);

        await vm.ConvertCommand.ExecuteAsync(null);

        Assert.Contains("Invalid bitrate", vm.StatusMessage);
        Assert.False(Directory.Exists(vm.OutputFolder));
    }

    [Fact]
    public async Task Convert_TwoWavFilesToMp3_WritesTwoFilesAndTwoOkLines()
    {
        string? ffmpeg = await FindFfmpegWithMp3Async();
        if (ffmpeg == null) return;

        string source = Path.Combine(_tempDir, "source");
        string output = Path.Combine(_tempDir, "converted");
        Directory.CreateDirectory(source);
        WriteTestWav(Path.Combine(source, "one.wav"));
        WriteTestWav(Path.Combine(source, "two.wav"));
        var vm = CreateViewModel(ffmpeg);
        vm.AddInputs([source]);
        vm.ConvertToMp3 = true;
        vm.SelectedMp3Quality = "128 kbit/s";
        vm.OutputFolder = output;

        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ConvertTask);
        await vm.ConvertTask!;

        Assert.Equal(2, Directory.GetFiles(output, "*.mp3", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(output, "one.mp3")));
        Assert.True(File.Exists(Path.Combine(output, "two.mp3")));
        Assert.Equal(2, vm.Report.Split('\n').Count(line => line.TrimStart().StartsWith("OK ", StringComparison.Ordinal)));
        Assert.Contains("Converted 2 file(s); 0 skipped, 0 failed.", vm.StatusMessage);
        Assert.True(File.Exists(Path.Combine(source, "one.wav")), "the original must stay");
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Convert_AnInputThatIsTheOutputPath_FailsAloneAndTheOtherFileIsStillConverted()
    {
        string? ffmpeg = await FindFfmpegWithMp3Async();
        if (ffmpeg == null) return;

        string work = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(work);
        WriteTestWav(Path.Combine(work, "c.wav"));
        WriteTestWav(Path.Combine(work, "d.wav"));
        var vm = CreateViewModel(ffmpeg);
        vm.ConvertToMp3 = true;
        vm.OutputFolder = work;

        // First pass makes work\c.mp3 next to its source.
        vm.AddInputs([Path.Combine(work, "c.wav")]);
        await vm.ConvertCommand.ExecuteAsync(null);
        await vm.ConvertTask!;
        Assert.True(File.Exists(Path.Combine(work, "c.mp3")), vm.Report);

        // Second pass: c.mp3 would be written over itself, d.wav is a normal file.
        vm.Inputs.Clear();
        vm.AddInputs([Path.Combine(work, "c.mp3"), Path.Combine(work, "d.wav")]);
        vm.Overwrite = true;
        await vm.ConvertCommand.ExecuteAsync(null);
        await vm.ConvertTask!;

        Assert.True(File.Exists(Path.Combine(work, "d.mp3")), vm.Report);
        string[] lines = vm.Report.Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Single(lines, l => l.StartsWith("failed ", StringComparison.Ordinal) && l.Contains("c.mp3", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith("OK ", StringComparison.Ordinal) && l.Contains("d.mp3", StringComparison.Ordinal));
        Assert.Contains("replace an input file", vm.Report);
        Assert.Contains("1 failed", vm.StatusMessage);
    }
}
