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
    public void SubtitleRows_SayWhichSrtWasFound_AndTheUserCanChooseOrLeaveItOff()
    {
        Directory.CreateDirectory(_tempDir);
        // Named like a typical download: dots and square brackets in the name, .srt beside the .mp4.
        string movie = Path.Combine(_tempDir, "Some.Film.1994.1080p.BluRay.x264.AAC-[GRP].mp4");
        string other = Path.Combine(_tempDir, "Other Film.mkv");
        File.WriteAllBytes(movie, new byte[16]);
        File.WriteAllBytes(other, new byte[16]);
        File.WriteAllText(Path.ChangeExtension(movie, ".srt"),
            "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nWorld\r\n");
        string picked = Path.Combine(_tempDir, "picked", "english.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(picked)!);
        File.WriteAllText(picked, "1\r\n00:00:01,000 --> 00:00:02,000\r\nOne line\r\n");

        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine()) { UseFfmpegWhenAvailable = false };
        Assert.False(vm.HasSubtitleRows);
        vm.AddMovieFiles([movie, other]);

        Assert.True(vm.HasSubtitleRows);
        Assert.Equal(2, vm.SubtitleRows.Count);
        var found = vm.SubtitleRows[0];
        Assert.Equal(DvdSubtitleSource.FoundBesideMovie, found.Source);
        Assert.Equal(Path.ChangeExtension(movie, ".srt"), found.SubtitlePath);
        Assert.Contains("Found beside the movie", found.StatusText);
        Assert.Contains("(2 subtitles)", found.StatusText);
        Assert.True(found.HasSubtitleFile);

        var none = vm.SubtitleRows[1];
        Assert.Equal(DvdSubtitleSource.None, none.Source);
        Assert.Null(none.SubtitlePath);
        Assert.Contains("no subtitles", none.StatusText);

        // Generate subtitles on: the movie without a file says it will get generated ones; the found file still wins.
        vm.GenerateSubtitles = true;
        Assert.Equal(DvdSubtitleSource.FoundBesideMovie, vm.SubtitleRows[0].Source);
        Assert.Equal(DvdSubtitleSource.WillBeGenerated, vm.SubtitleRows[1].Source);
        Assert.Contains("speech recognition", vm.SubtitleRows[1].StatusText);

        // Choosing a file for the second movie; leaving the first movie's file off.
        vm.ChooseSubtitleFileCommand.Execute(null); // no row: nothing happens, no dialog
        vm.ChooseSubtitleFile(other, picked);
        vm.ClearSubtitleFileCommand.Execute(vm.SubtitleRows[0]);
        Assert.Equal(DvdSubtitleSource.WillBeGenerated, vm.SubtitleRows[0].Source);
        Assert.Contains("left off", vm.SubtitleRows[0].StatusText);
        Assert.False(vm.SubtitleRows[0].HasSubtitleFile);
        Assert.Equal(DvdSubtitleSource.Chosen, vm.SubtitleRows[1].Source);
        Assert.Equal(picked, vm.SubtitleRows[1].SubtitlePath);
        Assert.Contains("english.srt (1 subtitle)", vm.SubtitleRows[1].StatusText);

        // An unreadable pick says so instead of pretending.
        string empty = Path.Combine(_tempDir, "empty.srt");
        File.WriteAllText(empty, "");
        vm.ChooseSubtitleFile(other, empty);
        Assert.Equal(DvdSubtitleSource.Unreadable, vm.SubtitleRows[1].Source);
        Assert.Contains("cannot be used", vm.SubtitleRows[1].StatusText);

        vm.ClearSource();
        Assert.Empty(vm.SubtitleRows);
        Assert.False(vm.HasSubtitleRows);
        vm.AddMovieFiles([movie]);
        Assert.Equal(DvdSubtitleSource.FoundBesideMovie, vm.SubtitleRows[0].Source); // picks forgotten with the list
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
