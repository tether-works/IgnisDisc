using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
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
    public void NewDvd_ClearsTheLastDisc_SoTheNextMoviesStartFresh()
    {
        Directory.CreateDirectory(_tempDir);
        string first = Path.Combine(_tempDir, "First Film.mp4");
        string second = Path.Combine(_tempDir, "Second Film.mp4");
        File.WriteAllBytes(first, new byte[16]);
        File.WriteAllBytes(second, new byte[16]);
        string picked = Path.Combine(_tempDir, "first.srt");
        File.WriteAllText(picked, "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n");

        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine()) { UseFfmpegWhenAvailable = false };
        vm.SelectedStandard = CdBurner.Core.DvdVideo.DvdVideoStandard.Pal; // a preference: must survive New DVD
        vm.AddMovieFiles([first]);
        vm.ChooseSubtitleFile(first, picked);
        string firstLabel = vm.VolumeLabel;
        string firstTitle = vm.MenuTitle;
        Assert.NotEqual("DVD_VIDEO", firstLabel); // filled in from the first movie's name
        Assert.True(vm.IsStructureValid);
        Assert.NotEmpty(vm.ExtentLayoutRows);
        Assert.True(vm.NewDvdCommand.CanExecute(null));

        vm.NewDvdCommand.Execute(null);

        Assert.Empty(vm.SourceMediaFiles);
        Assert.Equal(string.Empty, vm.VideoTsFolderPath);
        Assert.Empty(vm.SubtitleRows);
        Assert.Empty(vm.ExtentLayoutRows);
        Assert.Empty(vm.ValidationMessages);
        Assert.False(vm.IsStructureValid);
        Assert.Equal(0, vm.TotalSectors);
        Assert.Null(vm.LastBuiltIsoPath);
        Assert.Equal("DVD_VIDEO", vm.VolumeLabel);
        Assert.Equal("DVD Video", vm.MenuTitle);
        Assert.Contains("New DVD", vm.StatusMessage);
        Assert.Equal("No movies added yet", vm.ValidationStatusText); // not a red "Structure Invalid" on an empty screen
        Assert.Equal(CdBurner.Core.DvdVideo.DvdVideoStandard.Pal, vm.SelectedStandard);

        // The next disc gets its own name and title, not the first movie's, and no leftover subtitle pick.
        vm.AddMovieFiles([second]);
        Assert.Single(vm.SourceMediaFiles);
        Assert.NotEqual(firstLabel, vm.VolumeLabel);
        Assert.NotEqual(firstTitle, vm.MenuTitle);
        Assert.Equal(DvdSubtitleSource.None, vm.SubtitleRows[0].Source);

        // A typed VIDEO_TS folder path is cleared too.
        vm.VideoTsFolderPath = _tempDir;
        vm.StartNewDvd();
        Assert.Equal(string.Empty, vm.VideoTsFolderPath);
        Assert.False(vm.IsStructureValid);

        // While a burn runs the button is off and the call does nothing.
        vm.AddMovieFiles([second]);
        vm.IsBusy = true;
        Assert.False(vm.NewDvdCommand.CanExecute(null));
        vm.StartNewDvd();
        Assert.Single(vm.SourceMediaFiles);
        vm.IsBusy = false;
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

    [Fact]
    public async Task DvdVideoViewModel_AuthoringOptionsRoundTripIntoBuiltIso_AndInvalidateCache()
    {
        Directory.CreateDirectory(_tempDir);
        string m1 = Path.Combine(_tempDir, "movie_one.mp4");
        string m2 = Path.Combine(_tempDir, "movie_two.mp4");
        File.WriteAllBytes(m1, new byte[64 * 2048]);
        File.WriteAllBytes(m2, new byte[64 * 2048]);

        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine())
        {
            UseFfmpegWhenAvailable = false
        };
        vm.AddMovieFiles([m1, m2]);

        vm.DualLayer = true;
        vm.MenuAutoPlaySeconds = 15;
        vm.AfterTitleAction = DvdAfterTitleAction.ReturnToMenu;
        vm.ChapterIntervalMinutes = 0;
        vm.SubtitlesOnByDefault = true;
        vm.SubtitleSize = DvdSubtitleSize.Large;
        vm.SelectedQualityPreset = vm.AvailableQualityPresets.First(p => p.Id == "high");
        vm.SubtitleRows[0].MenuTitleName = "Opening Night";

        Assert.True(vm.IsDualLayerRequired);
        Assert.Equal("Opening Night", vm.GetTitleName(m1));
        Assert.False(vm.FitToDisc);
        Assert.Equal(8000, vm.VideoBitrateKbps);

        string iso1 = Path.Combine(_tempDir, "roundtrip1.iso");
        string built1 = await vm.BuildDvdIsoAsync(iso1);
        Assert.Equal(iso1, built1);
        Assert.NotNull(vm.LastAuthoredVideoTsPath);

        using (var reader = IsoImageReader.FromFile(iso1))
        {
            byte[] vmg = reader.ReadFileBytes("/VIDEO_TS/VIDEO_TS.IFO");
            byte[] vts1 = reader.ReadFileBytes("/VIDEO_TS/VTS_01_0.IFO");

            // First-Play PGC SetSTN ON (0xC0)
            int fpCmd = 0x400 + BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(0x400 + 0xE4));
            Assert.Equal(new byte[] { 0x51, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00 }, vmg.AsSpan(fpCmd + 8, 8).ToArray());

            // Menu still time == 15s and post == JumpTT 1
            int ut = (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC8)) * 2048;
            int pgcit = ut + (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(ut + 12));
            int menuPgc = pgcit + (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(pgcit + 12));
            int cell = menuPgc + BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(menuPgc + 0xE8));
            int menuCmd = menuPgc + BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(menuPgc + 0xE4));
            Assert.Equal(15, vmg[cell + 2]);
            Assert.Equal(new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 }, vmg.AsSpan(menuCmd + 8, 8).ToArray());

            // VTS_01 post == CallSS VMGM menu 2
            int vtsPgcit = (int)BinaryPrimitives.ReadUInt32BigEndian(vts1.AsSpan(0xCC)) * 2048;
            int vtsPgc = vtsPgcit + (int)BinaryPrimitives.ReadUInt32BigEndian(vts1.AsSpan(vtsPgcit + 12));
            int vtsCmd = vtsPgc + BinaryPrimitives.ReadUInt16BigEndian(vts1.AsSpan(vtsPgc + 0xE4));
            Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00 }, vts1.AsSpan(vtsCmd + 8, 8).ToArray());
        }

        // Changing an authoring option invalidates cached VIDEO_TS and unchecking menu resets ReturnToMenu -> PlayNextTitle
        vm.AutoCreatePlayMenu = false;
        Assert.Null(vm.LastAuthoredVideoTsPath);
        Assert.Equal(DvdAfterTitleAction.PlayNextTitle, vm.AfterTitleAction);
    }

    [Fact]
    public async Task CliAuthorDvd_ParsesAndAppliesAllConfigurableDvdOptions()
    {
        Directory.CreateDirectory(_tempDir);
        string m1 = Path.Combine(_tempDir, "clip1.mp4");
        string m2 = Path.Combine(_tempDir, "clip2.mp4");
        File.WriteAllBytes(m1, new byte[64 * 2048]);
        File.WriteAllBytes(m2, new byte[64 * 2048]);
        string outDir = Path.Combine(_tempDir, "CliAuthored");

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(
            [
                "author-dvd",
                "--input", $"{m1};{m2}",
                "--output", outDir,
                "--menu-autoplay", "10",
                "--after-title", "menu",
                "--chapters", "none",
                "--subtitles-default", "on",
                "--subtitle-size", "large",
                "--video-bitrate", "4500",
                "--dual-layer",
                "--title-names", "First Film;Second Film",
                "--no-ffmpeg",
                "--json"
            ],
            stdout,
            stderr,
            _deviceManager,
            new VirtualBurnEngine());

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(10, root.GetProperty("menuAutoPlaySeconds").GetInt32());
        Assert.Equal("ReturnToMenu", root.GetProperty("afterTitleAction").GetString());
        Assert.Equal(0, root.GetProperty("chapterIntervalSeconds").GetInt32());
        Assert.True(root.GetProperty("subtitlesOnByDefault").GetBoolean());
        Assert.Equal("Large", root.GetProperty("subtitleSize").GetString());
        Assert.False(root.GetProperty("fitToDisc").GetBoolean());
        Assert.Equal(4500, root.GetProperty("videoBitrateKbps").GetInt32());
        Assert.True(root.GetProperty("dualLayer").GetBoolean());

        byte[] vmg = File.ReadAllBytes(Path.Combine(outDir, "VIDEO_TS", "VIDEO_TS.IFO"));
        byte[] vts1 = File.ReadAllBytes(Path.Combine(outDir, "VIDEO_TS", "VTS_01_0.IFO"));
        int fpCmd = 0x400 + BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(0x400 + 0xE4));
        Assert.Equal(new byte[] { 0x51, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00 }, vmg.AsSpan(fpCmd + 8, 8).ToArray());

        int ut = (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC8)) * 2048;
        int pgcit = ut + (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(ut + 12));
        int menuPgc = pgcit + (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(pgcit + 12));
        int cell = menuPgc + BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(menuPgc + 0xE8));
        Assert.Equal(10, vmg[cell + 2]);

        int vtsPgcit = (int)BinaryPrimitives.ReadUInt32BigEndian(vts1.AsSpan(0xCC)) * 2048;
        int vtsPgc = vtsPgcit + (int)BinaryPrimitives.ReadUInt32BigEndian(vts1.AsSpan(vtsPgcit + 12));
        int vtsCmd = vtsPgc + BinaryPrimitives.ReadUInt16BigEndian(vts1.AsSpan(vtsPgc + 0xE4));
        Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00 }, vts1.AsSpan(vtsCmd + 8, 8).ToArray());
    }
}
