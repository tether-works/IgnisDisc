using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CdBurner.App.ViewModels;
using CdBurner.Core.Subtitles;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The DVD-Video screen's "Generate subtitles" option: the 1-click offer appears only when the option is on and the
/// tool is missing, and goes away once the (fake) download is installed.
/// </summary>
public sealed class DvdSubtitleOptionViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_SubtitleVm_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task InstallOffer_ShowsOnlyWhenAskedForAndMissing_AndHidesAfterInstall()
    {
        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine())
        {
            SubtitleToolInstallRoot = Path.Combine(_tempDir, "whisper.cpp")
        };
        vm.RefreshSubtitleToolStatus();

        Assert.False(vm.GenerateSubtitles);
        Assert.False(vm.IsSubtitleToolInstalled);
        Assert.False(vm.IsSubtitleToolOfferVisible);
        Assert.Equal("Subtitle tool not installed", vm.SubtitleToolStatusText);
        Assert.Contains("SHA-256", vm.SubtitleToolOfferText);
        Assert.Contains("mishear", vm.SubtitleToolOfferText);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.GenerateSubtitles = true;
        Assert.True(vm.IsSubtitleToolOfferVisible);
        Assert.Contains(nameof(DvdVideoViewModel.IsSubtitleToolOfferVisible), changed);

        byte[] zip = CreateZip(("Release/whisper-cli.exe", "cli"), ("Release/whisper.dll", "lib"));
        byte[] model = Encoding.ASCII.GetBytes("weights");
        var plan = new WhisperInstallPlan(
            WhisperBuildKind.CpuX64,
            new WhisperDownload("prog.zip", "https://example.test/prog.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, zip.Length),
            new WhisperDownload("ggml-base.bin", "https://example.test/ggml-base.bin", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, model.Length),
            "test")
        {
            Vad = new WhisperDownload("vad.bin", "https://example.test/vad.bin", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, model.Length)
        };
        using var client = new HttpClient(new FakeDownloads(("prog.zip", zip), ("ggml-base.bin", model), ("vad.bin", model)));

        WhisperInstallResult result = await vm.InstallSubtitleToolAsync(plan, client);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(vm.IsSubtitleToolInstalled);
        Assert.False(vm.IsSubtitleToolOfferVisible);
        Assert.False(vm.IsBusy);
        Assert.Contains("ggml-base.bin", vm.SubtitleToolStatusText);
        Assert.Contains("processor", vm.SubtitleToolStatusText);
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private sealed class FakeDownloads(params (string FileName, byte[] Body)[] files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string name = request.RequestUri!.Segments[^1];
            foreach (var (fileName, body) in files)
            {
                if (fileName == name)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
