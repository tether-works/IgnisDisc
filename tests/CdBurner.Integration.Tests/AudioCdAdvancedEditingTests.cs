using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class AudioCdAdvancedEditingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioCdAdvancedEditingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_AudioAdvUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

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
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] MakeSinePcm(double freqHz, int frames, double amplitude = 0.5)
    {
        int samples = frames * 588;
        byte[] pcm = new byte[samples * 4];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)Math.Round(amplitude * short.MaxValue * Math.Sin(2.0 * Math.PI * freqHz * i / OpticalConstants.CdAudioSampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }
        return pcm;
    }

    private static byte[] CreateValidJpegBytes()
    {
        var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr24, null, new byte[8 * 8 * 3], 8 * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] CreateValidPngBytes()
    {
        var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task AudioBurnViewModel_InlineMetadata_Htoa_CoverArt_And_ProjectSerializer_RoundTrip()
    {
        string wav1 = Path.Combine(_tempDir, "01.wav");
        string wav2 = Path.Combine(_tempDir, "02.wav");
        string htoaWav = Path.Combine(_tempDir, "htoa.wav");
        string coverJpg = Path.Combine(_tempDir, "front.jpg");

        byte[] htoaPcm = MakeSinePcm(330.0, 150);
        WavFileCodec.WriteWavFile(wav1, MakeSinePcm(440.0, 300));
        WavFileCodec.WriteWavFile(wav2, MakeSinePcm(880.0, 300));
        WavFileCodec.WriteWavFile(htoaWav, htoaPcm);
        byte[] jpgBytes = CreateValidJpegBytes();
        await File.WriteAllBytesAsync(coverJpg, jpgBytes);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);
        vm.AddAudioFiles([wav1, wav2]);
        Assert.Equal(2, vm.Tracks.Count);

        // Edit per-track inline metadata, pre-gap, and pre-emphasis flag + badge
        var row1 = vm.Tracks[0];
        row1.Title = "Edited One";
        row1.Performer = "Edited Performer";
        row1.Album = "Custom Track Album";
        row1.Songwriter = "Track Writer";
        row1.Isrc = "USAA12600001";
        row1.PreGapSeconds = 3.0; // 225 frames
        row1.PreEmphasis = true;

        Assert.Equal(225, row1.PreGapFrames);
        Assert.True(row1.HasPreEmphasis);
        Assert.Equal("PRE", row1.PreEmphasisBadgeText);

        // Attach and preview HTOA
        Assert.True(vm.AttachHtoaFile(htoaWav));
        Assert.True(vm.HasHtoa);
        Assert.Equal(150, vm.HtoaFrames);
        Assert.Contains("htoa.wav", vm.HtoaStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.UseDirectDriveCommands);
        await vm.PreviewHtoaAsync();
        Assert.True(vm.IsPreviewPlaying);
        vm.StopPreview();
        Assert.False(vm.IsPreviewPlaying);

        // Pick local cover art and export Jewel Case + share to Disc Art
        Assert.True(vm.SetCoverArtFile(coverJpg));
        Assert.True(vm.HasCoverArt);
        Assert.Equal("image/jpeg", vm.CoverArtMimeType);

        string jewelHtml = Path.Combine(_tempDir, "jewel.html");
        vm.ExportJewelCaseCoverHtml(jewelHtml);
        string htmlContent = await File.ReadAllTextAsync(jewelHtml);
        Assert.Contains("data:image/jpeg;base64,", htmlContent, StringComparison.Ordinal);
        Assert.Contains("Hidden Track One Audio (HTOA)", htmlContent, StringComparison.Ordinal);
        Assert.Contains("[PRE]", htmlContent, StringComparison.Ordinal);

        var discArt = new DiscArtViewModel(calibrationStorePath: Path.Combine(_tempDir, "no-cal.json"));
        string exportedPng = Path.Combine(_tempDir, "shared-disc-art.png");
        string? sharedPath = vm.ShareCoverArtWithDiscArt(discArt, exportedPng);
        Assert.Equal(exportedPng, sharedPath);
        Assert.True(discArt.HasPicture);
        Assert.Equal(exportedPng, vm.FillPicturePath);

        // Round-trip compilation through ProjectSerializer into a fresh AudioBurnViewModel
        var doc = ProjectSerializer.FromAudioCdCompilation(vm.BuildCurrentCompilation(), "RoundTrip");
        string json = ProjectSerializer.ToJson(doc);
        var reloadedComp = ProjectSerializer.ToAudioCdCompilation(ProjectSerializer.FromJson(json));

        var vm2 = new AudioBurnViewModel(_deviceManager, _burnEngine);
        vm2.LoadCompilation(reloadedComp);

        Assert.Equal("Edited One", vm2.Tracks[0].Title);
        Assert.Equal("Edited Performer", vm2.Tracks[0].Performer);
        Assert.Equal("Custom Track Album", vm2.Tracks[0].Album);
        Assert.Equal("Track Writer", vm2.Tracks[0].Songwriter);
        Assert.Equal("USAA12600001", vm2.Tracks[0].Isrc);
        Assert.Equal(225, vm2.Tracks[0].PreGapFrames);
        Assert.True(vm2.Tracks[0].PreEmphasis);
        Assert.True(vm2.HasHtoa);
        Assert.Equal(150, vm2.HtoaFrames);
        Assert.Equal(htoaWav, vm2.HtoaFilePath);
        Assert.True(vm2.HasCoverArt);
        Assert.Equal(jpgBytes, vm2.CoverArtBytes);
    }

    [Fact]
    public async Task AudioBurnViewModel_FetchCoverArtFromArchive_AndRipWithHtoaBadgeAndDeEmphasis()
    {
        const string releaseId = "11111111-2222-3333-4444-555555555555";
        byte[] pngCover = CreateValidPngBytes();

        var handler = new FakeHttpHandler(req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("musicbrainz.org/ws/2/release/?query=", StringComparison.OrdinalIgnoreCase))
            {
                string body = $$"""{"releases":[{"id":"{{releaseId}}","title":"Neon Echoes","score":100}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains($"release/{releaseId}/front-500", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(pngCover)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Neon Echoes",
            AlbumPerformer = "Signal Band"
        };

        bool fetched = await vm.FetchCoverArtFromArchiveAsync(http);
        Assert.True(fetched);
        Assert.True(vm.HasCoverArt);
        Assert.Equal("image/png", vm.CoverArtMimeType);
        Assert.Equal(pngCover, vm.CoverArtBytes);

        // Cover Art Archive bytes survive ProjectSerializer even when no local CoverArtPath exists
        var doc = ProjectSerializer.FromAudioCdCompilation(vm.BuildCurrentCompilation());
        var reloaded = ProjectSerializer.ToAudioCdCompilation(ProjectSerializer.FromJson(ProjectSerializer.ToJson(doc)));
        Assert.Equal(pngCover, reloaded.CoverArtBytes);
        Assert.Equal("image/png", reloaded.CoverArtMimeType);

        // Now burn a virtual disc with HTOA + PreEmphasis via NativeAudioBurnEngine, then check HTOA badge + de-emphasis rip
        byte[] htoaPcm = MakeSinePcm(220.0, 150);
        byte[] t1Pcm = MakeSinePcm(10000.0, 300);
        var recorder = (VirtualDiscRecorder)_deviceManager.GetRecorders().First(r => r.UniqueId == "VDRV-01");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var nativeEngine = new NativeAudioBurnEngine(_tempDir) { Wait = _ => { } };
        var burnRes = await nativeEngine.BurnAudioTracksAsync(
            recorder,
            [
                new AudioTrackSource
                {
                    TrackNumber = 1,
                    Title = "Emphasized Opening",
                    Performer = "Signal Band",
                    PreGapFrames = 150,
                    PreGapAudioFrames = 150,
                    PreGapPcmStreamFactory = () => new MemoryStream(htoaPcm),
                    PreEmphasis = true,
                    TotalPcmBytes = t1Pcm.Length,
                    PcmStreamFactory = () => new MemoryStream(t1Pcm)
                }
            ],
            new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = true },
            progress: null,
            CancellationToken.None);
        Assert.True(burnRes.Success, burnRes.ErrorMessage);

        vm.SelectedRecorder = recorder;
        vm.RefreshRipHtoaStatus();
        Assert.True(vm.HasRipHtoa);
        Assert.Contains("HTOA", vm.RipHtoaBadgeText, StringComparison.Ordinal);

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.DeEmphasizePreEmphasisTracks = true;
        string ripOut = Path.Combine(_tempDir, "rip-htoa-deemph");
        var ripRes = await vm.RipAudioCdAsync("VDRV-01", ripOut);

        Assert.True(ripRes.Success, ripRes.ErrorMessage);
        Assert.Equal(150, ripRes.HiddenTrackOneAudioFrames);
        Assert.True(vm.HasRipHtoa);
        Assert.Contains("HTOA Extracted", vm.RipHtoaBadgeText, StringComparison.Ordinal);
        Assert.True(ripRes.TrackInfos[0].PreEmphasis);
        Assert.True(ripRes.TrackInfos[0].DeEmphasisApplied);
    }

    [Fact]
    public async Task Rip_ToOpus_EmbedsCoverArt_WhenFfmpegHasLibOpus()
    {
        string? ffmpeg = FfmpegLocator.FindFfmpegExecutable(Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG"));
        if (ffmpeg == null || !await FfmpegProcess.HasEncoderAsync(ffmpeg, "libopus"))
        {
            return;
        }

        string settingsPath = Path.Combine(_tempDir, "settings-opus.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.CurrentSettings = new AppSettings { FfmpegExecutablePath = ffmpeg };

        string wav1 = Path.Combine(_tempDir, "t1.wav");
        WavFileCodec.WriteWavFile(wav1, MakeSinePcm(440.0, 150));

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settingsService)
        {
            AlbumTitle = "Opus Cover Album",
            AlbumPerformer = "Test Artist"
        };
        vm.AddAudioFile(wav1);
        Assert.True((await vm.StartAudioBurnAsync("VDRV-01")).Success);

        byte[] png = CreateValidPngBytes();
        string coverPath = Path.Combine(_tempDir, "cover.png");
        await File.WriteAllBytesAsync(coverPath, png);
        Assert.True(vm.SetCoverArtFile(coverPath));

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.SelectedRipFormat = AudioRipFormat.Opus;
        string ripOut = Path.Combine(_tempDir, "rip-opus");
        var ripRes = await vm.RipAudioCdAsync("VDRV-01", ripOut);
        Assert.True(ripRes.Success, vm.RipReport);

        string opusFile = Directory.GetFiles(ripOut, "*.opus").Single();
        using var decoder = new MediaFoundationAudioDecoder(ffmpeg);
        var meta = decoder.ProbeAudioFile(opusFile);
        Assert.True(meta.HasEmbeddedCoverArt);
        Assert.Equal("image/png", meta.CoverArtMimeType);
        Assert.Equal(png, meta.CoverArtBytes);
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
