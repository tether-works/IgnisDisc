using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Covers CLI flag parsing regardless of position, per-command --keep-image handling,
/// unknown-option rejection, scan --block-size / --json status, --help --json, and rim-header title fallback.
/// </summary>
public sealed class CliValidationAndFlagsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliValidationAndFlagsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_CliVal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
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

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class TestDeviceService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sha256sums")]
    [InlineData("blake3sums")]
    [InlineData("native")]
    [InlineData("test-write")]
    [InlineData("test-first")]
    [InlineData("recovery-session")]
    [InlineData("blue-book")]
    [InlineData("allow-suspect")]
    [InlineData("repair")]
    [InlineData("repair-from-recovery")]
    [InlineData("no-replaygain")]
    [InlineData("health-certificate")]
    [InlineData("files-outer")]
    [InlineData("read-compat")]
    public void MissingBooleanFlags_BeforeSubcommandOrPositional_DoNotConsumeNextToken(string flag)
    {
        var beforeSubcommand = CliCommandRunner.CommandLineArgs.Parse([$"--{flag}", "burn-iso", "--drive", "VDRV-01"]);
        Assert.Equal("burn-iso", beforeSubcommand.Subcommand);
        Assert.True(beforeSubcommand.HasFlag(flag));

        var beforePositional = CliCommandRunner.CommandLineArgs.Parse(["disc-art", $"--{flag}", "burn", "--drive", "VDRV-01"]);
        Assert.Equal("disc-art", beforePositional.Subcommand);
        Assert.Equal(["burn"], beforePositional.Positionals);
        Assert.True(beforePositional.HasFlag(flag));
    }

    [Fact]
    public void KeepImage_ResolvesAsFlagOnRecover_AndValueOptionOnCopyDisc()
    {
        // On recover: boolean flag even when placed before subcommand or before --drive.
        var recoverBeforeSub = CliCommandRunner.CommandLineArgs.Parse(["--keep-image", "recover", "--drive", "VDRV-01", "--out", "out"]);
        Assert.Equal("recover", recoverBeforeSub.Subcommand);
        Assert.True(recoverBeforeSub.HasFlag("keep-image"));
        Assert.Equal("VDRV-01", recoverBeforeSub.GetOption("drive"));

        var recoverMid = CliCommandRunner.CommandLineArgs.Parse(["recover", "--keep-image", "--drive", "VDRV-01", "--out", "out"]);
        Assert.True(recoverMid.HasFlag("keep-image"));
        Assert.Equal("VDRV-01", recoverMid.GetOption("drive"));

        // On copy-disc: value option regardless of position.
        var copyAfter = CliCommandRunner.CommandLineArgs.Parse(["copy-disc", "--source", "VDRV-01", "--keep-image", "staged_dir"]);
        Assert.Equal("copy-disc", copyAfter.Subcommand);
        Assert.Equal("staged_dir", copyAfter.GetOption("keep-image"));

        var copyBefore = CliCommandRunner.CommandLineArgs.Parse(["--keep-image", "staged_dir", "copy-disc", "--source", "VDRV-01"]);
        Assert.Equal("copy-disc", copyBefore.Subcommand);
        Assert.Equal("staged_dir", copyBefore.GetOption("keep-image"));
    }

    [Fact]
    public async Task CopyDisc_KeepImageWithoutFolder_FailsCleanly()
    {
        var (code, stdout, _) = await RunAsync("copy-disc", "--source", "VDRV-01", "--keep-image", "--json");
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("--keep-image needs a folder", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CopyDisc_KeepImageWithFolder_PreservesStagedImage()
    {
        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        byte[] pcm = new byte[300 * OpticalConstants.RawSectorSize];
        new Random(42).NextBytes(pcm);
        string wav = Path.Combine(_tempDir, "t1.wav");
        WavFileCodec.WriteWavFile(wav, pcm);
        compilation.AddTrack(new AudioCdTrackItem(1, wav, "Track 1", "Artist"));

        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var burned = await new AudioCdBurnService(_burnEngine).BurnAsync(_recorder, compilation, new AudioBurnOptions { UseDiscAtOnce = true });
        Assert.True(burned.Success, burned.ErrorMessage);

        string stagedFolder = Path.Combine(_tempDir, "kept");
        var (code, stdout, err) = await RunAsync("copy-disc", "--source", "VDRV-01", "--keep-image", stagedFolder, "--json");
        Assert.True(code == 0, stdout + err);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        string stagedPath = doc.RootElement.GetProperty("stagedImage").GetString()!;
        Assert.StartsWith(stagedFolder, stagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(stagedPath));
    }

    [Theory]
    [InlineData(new[] { "drives", "--bogus-flag", "--json" }, "bogus-flag")]
    [InlineData(new[] { "--bogus-flag", "drives", "--json" }, "bogus-flag")]
    [InlineData(new[] { "--bogus-flag", "--json" }, "bogus-flag")]
    [InlineData(new[] { "drives", "--bogus-opt=123", "--json" }, "bogus-opt")]
    [InlineData(new[] { "drives", "--native", "--json" }, "native")]
    [InlineData(new[] { "disc-art", "--bogus-flag", "burn", "--drive", "VDRV-01", "--image", "a.iso", "--json" }, "bogus-flag")]
    public async Task UnknownOrMalformedOptions_FailWithNonZeroExitAndJsonError(string[] args, string expectedOptionName)
    {
        var (code, stdout, _) = await RunAsync(args);
        Assert.NotEqual(0, code);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains(expectedOptionName, root.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BooleanFlagWithInlineValue_FailsWithNonZeroExit()
    {
        var (code, _, stderr) = await RunAsync("drives", "--json=yes");
        Assert.Equal(1, code);
        Assert.Contains("does not take a value", stderr);
    }

    [Fact]
    public async Task UnknownOption_WithoutJson_WritesErrorToStderrAndReturnsOne()
    {
        var (code, stdout, stderr) = await RunAsync("drives", "--bogus-flag");
        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("--bogus-flag", stderr);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-16")]
    [InlineData("")]
    public async Task Scan_InvalidBlockSize_FailsWithJsonError(string badValue)
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        var (code, stdout, _) = await RunAsync("scan", "--drive", "VDRV-01", "--block-size", badValue, "--json");
        Assert.Equal(1, code);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("--block-size", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scan_Json_WithDamagedBlocks_ReturnsExitOneAndSuccessFalse()
    {
        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, 1024, isBlank: false)
        {
            UsedSectors = 1024
        };
        medium.SimulatedBadLbas.Add(500);
        _recorder.InsertMedia(medium);

        var (code, stdout, err) = await RunAsync("scan", "--drive", "VDRV-01", "--block-size", "256", "--json");
        Assert.Equal(1, code);
        Assert.Empty(err);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("damagedBlocks").GetInt32());
        Assert.Equal(3, root.GetProperty("goodBlocks").GetInt32());
    }

    [Fact]
    public async Task Help_WithJson_EmitsValidJsonForTopLevelAndSubcommand()
    {
        var (topCode, topOut, topErr) = await RunAsync("--help", "--json");
        Assert.Equal(0, topCode);
        Assert.Empty(topErr);
        using (var topDoc = JsonDocument.Parse(topOut))
        {
            var root = topDoc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(LegalBanner.VersionNotice, root.GetProperty("notice").GetString());
            Assert.Contains("drives", root.GetProperty("commands").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains("Usage:", root.GetProperty("help").GetString());
        }

        var (subCode, subOut, subErr) = await RunAsync("drives", "--help", "--json");
        Assert.Equal(0, subCode);
        Assert.Empty(subErr);
        using (var subDoc = JsonDocument.Parse(subOut))
        {
            var root = subDoc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("drives", root.GetProperty("command").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("help").GetString()));
        }
    }

    [Fact]
    public async Task DiscArt_RimHeader_WithoutLabelAndEmptyStemPicture_UsesNonEmptyFallbackTitle()
    {
        // A dotfile PNG (".png") has an empty filename stem; without --label the rim header still gets a non-empty title.
        string dotPng = Path.Combine(_tempDir, ".png");
        const int size = 32;
        byte[] pixels = new byte[size * size * 4];
        PngRaster.SaveBgra32(dotPng, new BgraRaster(size, size, pixels));

        string wav = Path.Combine(_tempDir, "rim.wav");
        var (code, stdout, err) = await RunAsync(
            "disc-art", "generate", "--media", "cd", "--picture", dotPng, "--output", wav,
            "--disc-preset", "cd-80", "--rim-header", "--plan-only", "--json");

        Assert.True(code == 0, stdout + err);
        using var doc = JsonDocument.Parse(stdout);
        string headerText = doc.RootElement.GetProperty("plan").GetProperty("rimHeader").GetProperty("text").GetString()!;
        Assert.StartsWith("DISC_ART  ", headerText, StringComparison.Ordinal);
    }
}
