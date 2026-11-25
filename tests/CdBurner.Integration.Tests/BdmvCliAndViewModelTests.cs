using System.IO;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.BluRayVideo;
using CdBurner.Core.Subtitles;
using CdBurner.Core.Tests;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The <c>author-bdmv</c> command and the "Blu-ray Video (BDMV)" mode of the DVD-Video screen. FFmpeg is replaced by a
/// runner that writes a hand-built transport stream, so the tests need no encoder and no disc drive; the authored files
/// are read back with <see cref="BdmvStructuralValidator"/>.
/// </summary>
public sealed class BdmvCliAndViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BdmvInt_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _devices = new(enablePhysicalEnumeration: false);

    public BdmvCliAndViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _devices.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeFfmpeg : IExternalToolRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onOutputLine, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToList());
            if (arguments.Contains("-encoders"))
            {
                onOutputLine?.Invoke(" V....D libx264              libx264 H.264 / AVC");
                onOutputLine?.Invoke(" A....D ac3                  ATSC A/52A (AC-3)");
                onOutputLine?.Invoke(" A....D pcm_bluray           PCM for Blu-ray");
                return Task.FromResult(0);
            }
            File.WriteAllBytes(arguments[^1], SyntheticTransportStream.Create(96));
            return Task.FromResult(0);
        }
    }

    private string Movie(string name = "movie.mp4")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    private string FakeExe()
    {
        string path = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private static Dictionary<string, byte[]> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private async Task<(int Exit, string Out, string Err)> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, new VirtualBurnEngine());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ----- command line -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthorBdmv_Folder_WritesAValidTreeAndReportsStructuralVerificationOnly()
    {
        CliCommandRunner.BdmvToolRunnerOverride.Value = new FakeFfmpeg();
        string srt = Path.Combine(_dir, "movie.srt");
        File.WriteAllText(srt, "1\n00:00:01,500 --> 00:00:02,500\nHello\n");
        string outDir = Path.Combine(_dir, "bd");

        var (exit, stdout, stderr) = await RunCliAsync(
            "author-bdmv", "--input", Movie(), "--output", outDir, "--ffmpeg", FakeExe(),
            "--subtitle-track", $"eng:{srt}", "--chapters", "none", "--json");

        Assert.True(exit == 0, stderr + stdout);
        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("folder", root.GetProperty("outputKind").GetString());
        Assert.Equal("bluray", root.GetProperty("target").GetString());
        Assert.Equal("structural", root.GetProperty("verification").GetString());
        Assert.False(root.GetProperty("playerTested").GetBoolean());
        Assert.Equal(1, root.GetProperty("clips")[0].GetProperty("subtitles").GetInt32());

        var validation = BdmvStructuralValidator.Validate(ReadTree(outDir), avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
        Assert.Contains(validation.Scans["00000"].Segments, s => s.Type == 0x15);
    }

    [Fact]
    public async Task AuthorBdmv_Iso_WritesAUdf250ImageThatHoldsTheTree()
    {
        CliCommandRunner.BdmvToolRunnerOverride.Value = new FakeFfmpeg();
        string iso = Path.Combine(_dir, "movie.iso");

        var (exit, stdout, stderr) = await RunCliAsync(
            "author-bdmv", "--input", Movie(), "--output", iso, "--ffmpeg", FakeExe(), "--label", "MY_BD");

        Assert.True(exit == 0, stderr + stdout);
        Assert.Contains("not tested on a player", stdout);
        var udf = new UdfTestWalker(File.ReadAllBytes(iso));
        Assert.Equal(0x0250, udf.Revision);
        Assert.Contains("/BDMV/index.bdmv", udf.Files.Keys);
        Assert.Contains("/BDMV/STREAM/00000.m2ts", udf.Files.Keys);
        Assert.Contains("/CERTIFICATE/id.bdmv", udf.Files.Keys);
        var fromImage = BdmvStructuralValidator.Validate(udf.Files.ToDictionary(kv => kv.Key.TrimStart('/'), kv => kv.Value), avchd: false);
        Assert.True(fromImage.Errors.Count == 0, string.Join(Environment.NewLine, fromImage.Errors));
    }

    [Fact]
    public async Task AuthorBdmv_Avchd_UsesTheAvchdNames()
    {
        CliCommandRunner.BdmvToolRunnerOverride.Value = new FakeFfmpeg();
        string outDir = Path.Combine(_dir, "avchd");
        var (exit, stdout, stderr) = await RunCliAsync(
            "author-bdmv", "--input", Movie(), "--output", outDir, "--ffmpeg", FakeExe(), "--target", "avchd", "--json");
        Assert.True(exit == 0, stderr + stdout);
        Assert.True(File.Exists(Path.Combine(outDir, "BDMV", "STREAM", "00000.MTS")));
        Assert.True(File.Exists(Path.Combine(outDir, "BDMV", "PLAYLIST", "00000.MPL")));
        Assert.Equal("avchd", JsonDocument.Parse(stdout).RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task AuthorBdmv_RefusesToOverwriteWithoutTheFlag_AndRejectsBadValues()
    {
        CliCommandRunner.BdmvToolRunnerOverride.Value = new FakeFfmpeg();
        string outDir = Path.Combine(_dir, "twice");
        string movie = Movie();
        Assert.Equal(0, (await RunCliAsync("author-bdmv", "--input", movie, "--output", outDir, "--ffmpeg", FakeExe())).Exit);

        var again = await RunCliAsync("author-bdmv", "--input", movie, "--output", outDir, "--ffmpeg", FakeExe());
        Assert.NotEqual(0, again.Exit);
        Assert.Contains("--overwrite", again.Err);
        Assert.Equal(0, (await RunCliAsync("author-bdmv", "--input", movie, "--output", outDir, "--ffmpeg", FakeExe(), "--overwrite")).Exit);

        var badRate = await RunCliAsync("author-bdmv", "--input", movie, "--output", Path.Combine(_dir, "x"), "--video-format", "1080p", "--frame-rate", "25");
        Assert.NotEqual(0, badRate.Exit);
        Assert.Contains("frame rate", badRate.Err);

        var badTarget = await RunCliAsync("author-bdmv", "--input", movie, "--output", Path.Combine(_dir, "y"), "--target", "hddvd");
        Assert.NotEqual(0, badTarget.Exit);
        Assert.Contains("--target", badTarget.Err);

        var missing = await RunCliAsync("author-bdmv", "--output", Path.Combine(_dir, "z"));
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains("--input", missing.Err);

        var unknown = await RunCliAsync("author-bdmv", "--input", movie, "--output", Path.Combine(_dir, "w"), "--no-such-option", "1");
        Assert.NotEqual(0, unknown.Exit);
    }

    [Fact]
    public async Task AuthorBdmv_HelpListsTheCommandAndStatesTheLimit()
    {
        var (exit, stdout, _) = await RunCliAsync("author-bdmv", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("author-bdmv", stdout);
        Assert.Contains("not been tested on a Blu-ray player", stdout);
    }

    // ----- screen mode ------------------------------------------------------------------------------------------

    [Fact]
    public async Task BluRayMode_BuildsAUdf250ImageFromTheMovieList()
    {
        var vm = new DvdVideoViewModel(_devices, new VirtualBurnEngine())
        {
            BdmvToolRunner = new FakeFfmpeg(),
            CustomFfmpegPath = FakeExe(),
        };
        Assert.True(vm.IsDvdMode);
        Assert.Equal(new[] { "DVD-Video", "Blu-ray Video (BDMV)" }, vm.AuthoringModes);
        Assert.False(vm.BuildBdmvImageCommand.CanExecute(null));   // not in Blu-ray mode, and no movie yet

        vm.SelectedAuthoringMode = DvdVideoViewModel.BluRayModeName;
        Assert.True(vm.IsBluRayMode);
        Assert.False(vm.IsDvdMode);
        Assert.False(vm.BuildBdmvImageCommand.CanExecute(null));   // still no movie

        vm.VolumeLabel = "UI_BD";
        vm.SourceMediaFiles.Add(Movie());
        Assert.True(vm.BuildBdmvImageCommand.CanExecute(null));
        Assert.Contains("1 title", vm.BdmvSourceSummary);

        string iso = Path.Combine(_dir, "ui.iso");
        string built = await vm.BuildBdmvImageAsync(iso);

        Assert.Equal(iso, built);
        Assert.Equal(iso, vm.LastBuiltIsoPath);
        Assert.Contains("not claimed", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.Equal(100, vm.BurnProgress);
        var udf = new UdfTestWalker(File.ReadAllBytes(iso));
        Assert.Equal(0x0250, udf.Revision);
        var tree = udf.Files.ToDictionary(kv => kv.Key.TrimStart('/'), kv => kv.Value);
        var validation = BdmvStructuralValidator.Validate(tree, avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task BluRayMode_UsesTheChosenFormatSubtitlesAndSpeechRecognition()
    {
        string srt = Path.Combine(_dir, "chosen.srt");
        File.WriteAllText(srt, "1\n00:00:01,500 --> 00:00:02,500\nChosen\n");
        string made = Path.Combine(_dir, "made.srt");
        File.WriteAllText(made, "1\n00:00:03,000 --> 00:00:03,900\nMade\n");
        var runner = new FakeFfmpeg();
        var vm = new DvdVideoViewModel(_devices, new VirtualBurnEngine())
        {
            BdmvToolRunner = runner,
            CustomFfmpegPath = FakeExe(),
            BdmvSubtitleGenerator = (movie, progress, ct) =>
                Task.FromResult(new SubtitleGenerationResult(true, made, 1, TimeSpan.Zero, null, false, "test", null)),
        };
        vm.SelectedAuthoringMode = DvdVideoViewModel.BluRayModeName;
        vm.BdmvTarget = BdmvDiscTarget.Avchd;
        Assert.Equal(BdmvVideoFormat.Hd1080i, vm.BdmvVideoFormat);   // AVCHD defaults to interlaced HD
        vm.BdmvAudioCodec = BdmvAudioCodec.Lpcm;
        vm.GenerateSubtitles = true;
        Assert.True(vm.AddBdmvSubtitleFile(srt));
        Assert.False(vm.AddBdmvSubtitleFile(Path.Combine(_dir, "missing.srt")));
        Assert.Single(vm.BdmvSubtitleFiles);
        vm.SourceMediaFiles.Add(Movie());

        var options = vm.CreateBdmvOptions();
        Assert.Equal(BdmvDiscTarget.Avchd, options.Target);
        Assert.Equal(BdmvFrameRate.Fps25, options.FrameRate);
        Assert.Equal(2, options.Subtitles.Count);
        Assert.True(options.Subtitles[1].GenerateWithWhisper);

        string built = await vm.BuildBdmvImageAsync(Path.Combine(_dir, "avchd.iso"));
        Assert.NotEqual(string.Empty, built);
        var args = string.Join(' ', runner.Calls.Single(c => !c.Contains("-encoders")));
        Assert.Contains("pcm_bluray", args);
        var udf = new UdfTestWalker(File.ReadAllBytes(built));
        Assert.Contains("/BDMV/STREAM/00000.MTS", udf.Files.Keys);
        var validation = BdmvStructuralValidator.Validate(udf.Files.ToDictionary(kv => kv.Key.TrimStart('/'), kv => kv.Value), avchd: true);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(2, validation.Scans["00000"].Segments.Count(s => s.Type == 0x15));   // chosen + generated subtitle pictures
    }

    [Fact]
    public async Task BluRayMode_FailureLeavesAClearStatusAndNoImage()
    {
        var vm = new DvdVideoViewModel(_devices, new VirtualBurnEngine()) { BdmvToolRunner = new FakeFfmpeg(), CustomFfmpegPath = FakeExe() };
        vm.SelectedAuthoringMode = DvdVideoViewModel.BluRayModeName;

        Assert.Equal(string.Empty, await vm.BuildBdmvImageAsync(Path.Combine(_dir, "none.iso")));
        Assert.Contains("Add at least one movie", vm.StatusMessage);

        vm.SourceMediaFiles.Add(Path.Combine(_dir, "gone.mp4"));
        string iso = Path.Combine(_dir, "gone.iso");
        Assert.Equal(string.Empty, await vm.BuildBdmvImageAsync(iso));
        Assert.Contains("not found", vm.StatusMessage);
        Assert.False(File.Exists(iso));
        Assert.False(vm.IsBusy);
    }
}
