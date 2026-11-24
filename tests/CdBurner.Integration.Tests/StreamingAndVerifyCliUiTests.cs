using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class StreamingAndVerifyCliUiTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public StreamingAndVerifyCliUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_StreamCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL BD-RE", @"V:\");
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

    private sealed class TestDeviceService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }
        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => recorders;
        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void StreamingFlag_BeforeSubcommand_IsParsedAsBooleanFlag()
    {
        var parsed = CliCommandRunner.CommandLineArgs.Parse(["--streaming", "burn-iso", "--drive", "VDRV-01"]);
        Assert.Equal("burn-iso", parsed.Subcommand);
        Assert.True(parsed.HasFlag("streaming"));
        Assert.Equal("VDRV-01", parsed.GetOption("drive"));
    }

    [Fact]
    public async Task BurnIso_StreamingWithoutNative_IsRejectedCleanly()
    {
        string isoPath = Path.Combine(_tempDir, "test.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[32 * 2048]);
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        _recorder.InsertMedia(medium);

        var (code, _, stderr) = await RunAsync("burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--streaming");

        Assert.NotEqual(0, code);
        Assert.Contains("--streaming needs --native", stderr);
    }

    [Fact]
    public async Task BurnIso_NativeStreamingOnBdRe_SucceedsAndReportsStreamingWriteInJson()
    {
        string isoPath = Path.Combine(_tempDir, "bdre.iso");
        byte[] iso = new byte[64 * 2048];
        iso[16 * 2048] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, 16 * 2048 + 1);
        await File.WriteAllBytesAsync(isoPath, iso);

        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        _recorder.InsertMedia(medium);

        var (code, stdout, stderr) = await RunAsync(
            "burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--native", "--streaming", "--log-dir", _tempDir, "--json");

        Assert.True(code == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("directWrite").GetProperty("streamingWrite").GetBoolean());
    }

    [Fact]
    public async Task BurnData_NativeStreamingOnBdRe_SucceedsAndReportsStreamingWriteInJson()
    {
        string inputDir = Path.Combine(_tempDir, "payload");
        Directory.CreateDirectory(inputDir);
        await File.WriteAllBytesAsync(Path.Combine(inputDir, "data.bin"), new byte[8192]);

        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        _recorder.InsertMedia(medium);

        var (code, stdout, stderr) = await RunAsync(
            "burn-data", "--drive", "VDRV-01", "--input", inputDir, "--native", "--streaming", "--log-dir", _tempDir, "--json");

        Assert.True(code == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("directWrite").GetProperty("streamingWrite").GetBoolean());
    }

    [Fact]
    public void ViewModels_ExposeStreamingToggleOnlyForOverwritableBd_AndDefaultOff()
    {
        using var bdRe = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        using var cdR = VirtualDiscMedium.CreateBlankCdR700();

        var dataVm = new DataBurnViewModel(_deviceService, _burnEngine);
        var imageVm = new ImageStudioViewModel(_deviceService, _burnEngine);

        Assert.False(dataVm.StreamingWrite);
        Assert.False(imageVm.StreamingWrite);

        _recorder.InsertMedia(bdRe);
        dataVm.SelectedRecorder = null;
        dataVm.SelectedRecorder = _recorder;
        imageVm.SelectedRecorder = null;
        imageVm.SelectedRecorder = _recorder;
        Assert.True(dataVm.IsOverwritableBdMedia);
        Assert.True(imageVm.IsOverwritableBdMedia);

        _recorder.InsertMedia(cdR);
        dataVm.ApplyDirectDefaultForDisc(_recorder.QueryMediaStatus(), enabled: true);
        imageVm.ApplyDirectDefaultForDisc(_recorder.QueryMediaStatus(), enabled: true);
        Assert.False(dataVm.IsOverwritableBdMedia);
        Assert.False(imageVm.IsOverwritableBdMedia);
    }
}
