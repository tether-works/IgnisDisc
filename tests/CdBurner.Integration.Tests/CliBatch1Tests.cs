using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class CliBatch1Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliBatch1Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_B1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(recorder);
        _burnEngine = new VirtualBurnEngine(recorder);
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
        catch
        {
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static byte[] CreateToolZipBytes(string exeName, byte[] exeContent, string licenseText = "GPL")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exeEntry = zip.CreateEntry($"bin/{exeName}");
            using (var s = exeEntry.Open())
            {
                s.Write(exeContent, 0, exeContent.Length);
            }

            var licEntry = zip.CreateEntry("LICENSE.txt");
            using var ls = new StreamWriter(licEntry.Open(), Encoding.UTF8);
            ls.Write(licenseText);
        }
        return ms.ToArray();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private sealed class TestDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public TestDeviceService(params IDiscRecorderBackend[] recorders) => _recorders = recorders;

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => _recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Settings_GetSetReset_WorksAcrossJsonAndErrors()
    {
        string settingsFile = Path.Combine(_tempDir, "settings.json");

        var (getCode, getOut, getErr) = await RunCliAsync("settings", "get", "--settings-file", settingsFile, "--json");
        Assert.True(getCode == 0, getErr);
        using (var doc = JsonDocument.Parse(getOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(WriteSpeedPolicy.Recommended, doc.RootElement.GetProperty("settings").GetProperty("defaultWriteSpeedMultiplier").GetInt32());
        }

        // Negative integer offset positional & speed policy keyword & enum
        var (setOffsetCode, setOffsetOut, setOffsetErr) = await RunCliAsync("settings", "set", "audioReadOffsetSamples", "-12", "--settings-file", settingsFile, "--json");
        Assert.True(setOffsetCode == 0, setOffsetErr);
        using (var doc = JsonDocument.Parse(setOffsetOut))
        {
            Assert.Equal("audioReadOffsetSamples", doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(-12, doc.RootElement.GetProperty("value").GetInt32());
        }

        var (setSpeedCode, _, _) = await RunCliAsync("settings", "set", "--key", "defaultWriteSpeedMultiplier", "--value", "slowest", "--settings-file", settingsFile, "--json");
        Assert.Equal(0, setSpeedCode);

        var (setThemeCode, _, _) = await RunCliAsync("settings", "set", "themeMode", "Light", "--settings-file", settingsFile, "--json");
        Assert.Equal(0, setThemeCode);

        var (singleGetCode, singleGetOut, _) = await RunCliAsync("settings", "get", "defaultWriteSpeedMultiplier", "--settings-file", settingsFile, "--json");
        Assert.Equal(0, singleGetCode);
        using (var doc = JsonDocument.Parse(singleGetOut))
        {
            Assert.Equal(WriteSpeedPolicy.Slowest, doc.RootElement.GetProperty("value").GetInt32());
        }

        // Bad key & bad value errors
        var (badKeyCode, _, badKeyErr) = await RunCliAsync("settings", "get", "nonExistentKey", "--settings-file", settingsFile);
        Assert.NotEqual(0, badKeyCode);
        Assert.Contains("Unknown setting", badKeyErr);

        var (badValCode, _, badValErr) = await RunCliAsync("settings", "set", "logRingBufferCapacity", "2", "--settings-file", settingsFile);
        Assert.NotEqual(0, badValCode);
        Assert.Contains("between 10 and 100000", badValErr);

        // Reset restores defaults
        var (resetCode, resetOut, resetErr) = await RunCliAsync("settings", "reset", "--settings-file", settingsFile, "--json");
        Assert.True(resetCode == 0, resetErr);
        using (var doc = JsonDocument.Parse(resetOut))
        {
            Assert.Equal(WriteSpeedPolicy.Recommended, doc.RootElement.GetProperty("settings").GetProperty("defaultWriteSpeedMultiplier").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("settings").GetProperty("audioReadOffsetSamples").GetInt32());
        }
    }

    [Fact]
    public async Task Tools_ReportsAvailabilityAndJsonShape()
    {
        string fakeFfmpeg = Path.Combine(_tempDir, "ffmpeg.exe");
        await File.WriteAllBytesAsync(fakeFfmpeg, [0x4D, 0x5A]);
        string missingPar2 = Path.Combine(_tempDir, "missing-par2.exe");

        var (code, stdout, stderr) = await RunCliAsync("tools", "--ffmpeg", fakeFfmpeg, "--par2", missingPar2, "--json");
        Assert.True(code == 0, stderr);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("ffmpeg").GetProperty("available").GetBoolean());
        Assert.Equal(Path.GetFullPath(fakeFfmpeg), doc.RootElement.GetProperty("ffmpeg").GetProperty("path").GetString());
        Assert.True(doc.RootElement.TryGetProperty("par2", out _));
        Assert.True(doc.RootElement.TryGetProperty("subtitleTool", out _));
    }

    [Fact]
    public async Task InstallFfmpeg_And_InstallPar2_PlanOnly_And_MockedHttp()
    {
        string ffmpegDir = Path.Combine(_tempDir, "ffmpeg-install");
        string par2Dir = Path.Combine(_tempDir, "par2-install");

        // Plan-only via RunAsync (no network)
        var (planCode, planOut, planErr) = await RunCliAsync("install-ffmpeg", "--dir", ffmpegDir, "--plan-only", "--json");
        Assert.True(planCode == 0, planErr);
        using (var doc = JsonDocument.Parse(planOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("planOnly").GetBoolean());
            Assert.Equal("ffmpeg", doc.RootElement.GetProperty("tool").GetString());
        }

        // Mocked FFmpeg install
        byte[] ffmpegZip = CreateToolZipBytes("ffmpeg.exe", "fake-ffmpeg"u8.ToArray(), "LGPL");
        using (var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ffmpegZip)
        })))
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            var cmd = CliCommandRunner.CommandLineArgs.Parse(["install-ffmpeg", "--dir", ffmpegDir, "--json"]);
            int code = await CliCommandRunner.HandleInstallFfmpegAsync(cmd, outW, errW, http, "https://example.invalid/ffmpeg.zip");
            Assert.True(code == 0, errW.ToString());
            using var doc = JsonDocument.Parse(outW.ToString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe")));

            // Re-installing without --overwrite is refused cleanly
            var cmdNoOverwrite = CliCommandRunner.CommandLineArgs.Parse(["install-ffmpeg", "--dir", ffmpegDir]);
            await Assert.ThrowsAsync<CommandLineException>(() =>
                CliCommandRunner.HandleInstallFfmpegAsync(cmdNoOverwrite, new StringWriter(), new StringWriter(), http, "https://example.invalid/ffmpeg.zip"));
        }

        // Mocked PAR2 install with SHA-256 check
        byte[] par2Zip = CreateToolZipBytes("par2.exe", "fake-par2"u8.ToArray(), "GPL");
        string par2Hash = Convert.ToHexStringLower(SHA256.HashData(par2Zip));
        using (var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(par2Zip)
        })))
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            var cmd = CliCommandRunner.CommandLineArgs.Parse(["install-par2", "--dir", par2Dir, "--json"]);
            int code = await CliCommandRunner.HandleInstallPar2Async(cmd, outW, errW, http, "https://example.invalid/par2.zip", par2Hash);
            Assert.True(code == 0, errW.ToString());
            using var doc = JsonDocument.Parse(outW.ToString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(Path.Combine(par2Dir, "par2.exe")));

            // Bad hash returns exit code 1 with error in JSON
            var badOut = new StringWriter();
            var cmdBad = CliCommandRunner.CommandLineArgs.Parse(["install-par2", "--dir", Path.Combine(_tempDir, "par2-bad"), "--json"]);
            int badCode = await CliCommandRunner.HandleInstallPar2Async(
                cmdBad, badOut, new StringWriter(), http, "https://example.invalid/par2.zip", new string('0', 64));
            Assert.Equal(1, badCode);
            using var badDoc = JsonDocument.Parse(badOut.ToString());
            Assert.False(badDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task CheckUpdate_WithMockedGitHubApi_ReportsUpdateAndFailure()
    {
        const string releaseJson = """
            {
              "tag_name": "v99.0.0",
              "html_url": "https://github.com/tether-works/IgnisDisc/releases/tag/v99.0.0"
            }
            """;

        using (var http = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
        })))
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            var cmd = CliCommandRunner.CommandLineArgs.Parse(["check-update", "--json"]);
            int code = await CliCommandRunner.HandleCheckUpdateAsync(cmd, outW, errW, http, "https://api.github.com/repos/tether-works/IgnisDisc/releases/latest", "1.11.0");
            Assert.True(code == 0, errW.ToString());

            using var doc = JsonDocument.Parse(outW.ToString());
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("isUpdateAvailable").GetBoolean());
            Assert.Equal("v99.0.0", doc.RootElement.GetProperty("latestVersion").GetString());
        }

        using (var failHttp = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            var outW = new StringWriter();
            var errW = new StringWriter();
            var cmd = CliCommandRunner.CommandLineArgs.Parse(["check-update", "--json"]);
            int failCode = await CliCommandRunner.HandleCheckUpdateAsync(cmd, outW, errW, failHttp, "https://api.github.com/repos/tether-works/IgnisDisc/releases/latest", "1.11.0");
            Assert.Equal(1, failCode);
            using var doc = JsonDocument.Parse(outW.ToString());
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        }
    }
}
