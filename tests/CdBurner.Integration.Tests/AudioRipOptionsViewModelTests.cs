using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CdBurner.App.ViewModels;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration tests verifying the audio rip options, read offset clamping,
/// secure extraction C2 reporting, and rip report generation in <see cref="AudioBurnViewModel"/>.
/// </summary>
public sealed class AudioRipOptionsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioRipOptionsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipOptionsTests_" + Guid.NewGuid().ToString("N"));
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
        catch
        {
            // Best effort cleanup
        }
    }

    private async Task<AudioBurnViewModel> CreateAndBurnVirtualAudioCdAsync()
    {
        string wav1 = Path.Combine(_tempDir, "Track01.wav");
        string wav2 = Path.Combine(_tempDir, "Track02.wav");

        byte[] pcm1 = new byte[75 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize];

        for (int i = 0; i < pcm1.Length; i++) pcm1[i] = (byte)(i % 256);
        for (int i = 0; i < pcm2.Length; i++) pcm2[i] = (byte)((i * 3) % 256);

        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Rip Test Album",
            AlbumPerformer = "Test Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = false
        };

        vm.AddAudioFilesCommand.Execute(new[] { wav1, wav2 });
        var burnResult = await vm.StartAudioBurnAsync("VDRV-01");
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");

        return vm;
    }

    [Fact]
    public async Task Rip_PassesOptionsToRipper()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_1");

        vm.ReadOffsetSamples = 6;
        vm.SecureRip = true;
        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success, $"Rip failed: {vm.LastRipResult.ErrorMessage}");
        Assert.True(vm.LastRipResult.SecureModeUsed);
        Assert.Contains("+6", vm.RipReport);
    }

    [Fact]
    public async Task Rip_WithoutNetworkOptions_ReportsDiscIds()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_2");

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success, $"Rip failed: {vm.LastRipResult.ErrorMessage}");
        Assert.Contains("MusicBrainz ID", vm.RipReport);
    }

    [Fact]
    public void ReadOffsetSamples_IsClamped()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        vm.ReadOffsetSamples = 50000;
        Assert.Equal(10000, vm.ReadOffsetSamples);

        vm.ReadOffsetSamples = -50000;
        Assert.Equal(-10000, vm.ReadOffsetSamples);
    }

    [Fact]
    public async Task RipReport_IsClearedAtStartOfRip()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_4");

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        // A sentinel that cannot occur in a real report ("old" matched "Output Folder").
        const string staleReport = "STALE-REPORT-SENTINEL";
        vm.RipReport = staleReport;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success);
        Assert.DoesNotContain(staleReport, vm.RipReport);
    }

    // Smallest bytes the app accepts as a PNG: the 8-byte signature from the PNG specification, then filler.
    private static byte[] FakePng() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public async Task Rip_WithCoverThatIsNotAnImage_FailsBeforeReadingTheDisc()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_BadCover");
        string notAnImage = Path.Combine(_tempDir, "cover.txt");
        await File.WriteAllTextAsync(notAnImage, "this is not a picture");

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.SelectedRipFormat = AudioRipFormat.Wav;
        vm.RipCoverArtPath = notAnImage;

        var result = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.False(result.Success);
        Assert.Contains("not a JPEG, PNG, GIF or WebP image", vm.RipReport);
        Assert.False(Directory.Exists(ripOutput) && Directory.GetFiles(ripOutput).Length > 0);

        vm.RipCoverArtPath = Path.Combine(_tempDir, "missing.png");
        result = await vm.RipAudioCdAsync("VDRV-01", ripOutput);
        Assert.False(result.Success);
        Assert.Contains("Cover image could not be read", vm.RipReport);
    }

    [Fact]
    public void ClearRipCoverArt_EmptiesThePath()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine) { RipCoverArtPath = "x.png" };
        vm.ClearRipCoverArtCommand.Execute(null);
        Assert.Equal(string.Empty, vm.RipCoverArtPath);
    }

    [Fact]
    public async Task Rip_ToFlac_CarriesReplayGainAndCoverLikeTheCommandLine()
    {
        // Needs ffmpeg with the FLAC encoder (the optional download); skipped where it is not installed.
        string? ffmpeg = FfmpegLocator.FindFfmpegExecutable(Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG"));
        if (ffmpeg == null) return;

        string settingsPath = Path.Combine(_tempDir, "settings-flac.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.CurrentSettings = new AppSettings { FfmpegExecutablePath = ffmpeg };

        string wav1 = Path.Combine(_tempDir, "F1.wav");
        string wav2 = Path.Combine(_tempDir, "F2.wav");
        WavFileCodec.WriteWavFile(wav1, new byte[75 * OpticalConstants.RawSectorSize]);
        WavFileCodec.WriteWavFile(wav2, new byte[150 * OpticalConstants.RawSectorSize]);
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settingsService)
        {
            AlbumTitle = "Flac Tag Album",
            AlbumPerformer = "Test Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = false
        };
        vm.AddAudioFilesCommand.Execute(new[] { wav1, wav2 });
        Assert.True((await vm.StartAudioBurnAsync("VDRV-01")).Success);

        byte[] cover = FakePng();
        string coverPath = Path.Combine(_tempDir, "cover.png");
        await File.WriteAllBytesAsync(coverPath, cover);

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.SelectedRipFormat = AudioRipFormat.Flac;
        vm.RipCoverArtPath = coverPath;
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_Flac");

        var result = await vm.RipAudioCdAsync("VDRV-01", ripOutput);
        Assert.True(result.Success, vm.RipReport);

        var flacs = Directory.GetFiles(ripOutput, "*.flac");
        Assert.Equal(2, flacs.Length);
        foreach (string flac in flacs)
        {
            var meta = FlacFileCodec.ProbeMetadata(flac);
            Assert.True(meta.HasEmbeddedCoverArt);
            Assert.Equal(cover, meta.CoverArtBytes);
            Assert.Equal("image/png", meta.CoverArtMimeType);
            Assert.EndsWith(" dB", meta.ReplayGainTrackGain);
            Assert.EndsWith(" dB", meta.ReplayGainAlbumGain);
            Assert.False(string.IsNullOrEmpty(meta.ReplayGainTrackPeak));
            Assert.False(string.IsNullOrEmpty(meta.ReplayGainAlbumPeak));
        }
    }

    [Fact]
    public void AppSettings_InitializedAndSavedWhenPropertiesChange()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.CurrentSettings = new AppSettings
        {
            AudioReadOffsetSamples = 12,
            SecureAudioRipByDefault = true,
            LookupMusicBrainzByDefault = false,
            VerifyAccurateRipByDefault = false
        };

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settingsService);
        Assert.Equal(12, vm.ReadOffsetSamples);
        Assert.True(vm.SecureRip);
        Assert.False(vm.LookupMetadata);
        Assert.False(vm.VerifyAccurateRip);
        Assert.False(vm.VerifyCtdb);

        vm.ReadOffsetSamples = 30;
        Assert.Equal(30, settingsService.CurrentSettings.AudioReadOffsetSamples);
    }

    [Fact]
    public void VerifyCtdb_DefaultsToFalseWithOrWithoutSettings()
    {
        var vmWithoutSettings = new AudioBurnViewModel(_deviceManager, _burnEngine);
        Assert.False(vmWithoutSettings.VerifyCtdb);

        var settingsService = new AppSettingsService(Path.Combine(_tempDir, "ctdb-default.json"));
        var vmWithSettings = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settingsService);
        Assert.False(vmWithSettings.VerifyCtdb);
    }

    [Fact]
    public async Task Rip_MakesNoNetworkCallsWhenLookupsOff_AndNoCtdbCallsWhenAccurateRipOn()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        var handler = new SpyHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.VerifyCtdb = false;
        var ripOff = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_NetOff"), httpClient: client);
        Assert.True(ripOff.Success, ripOff.ErrorMessage);
        Assert.Empty(handler.Requests);

        vm.VerifyAccurateRip = true;
        vm.VerifyCtdb = false;
        var ripArOnly = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_ArOnly"), httpClient: client);
        Assert.True(ripArOnly.Success, ripArOnly.ErrorMessage);
        Assert.NotEmpty(handler.Requests);
        Assert.DoesNotContain(handler.Requests, u => string.Equals(u.Host, "db.cuetools.net", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rip_WhenVerifyCtdbEnabled_QueriesLookup2AndReportsMatch()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();

        // First rip offline to obtain the exact disc and track CTDB CRCs for the virtual medium
        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.VerifyCtdb = false;
        var baseline = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_CtdbBase"));
        Assert.True(baseline.Success);
        Assert.NotNull(baseline.CtdbDiscCrc);

        string trackCrcs = string.Join(' ', baseline.TrackInfos.Select(t => t.CtdbTrackCrc.ToString("x8")));
        string xml = $"""<ctdb><entry id="1" crc32="{baseline.CtdbDiscCrc.Value:x8}" confidence="4" trackcrcs="{trackCrcs}" /></ctdb>""";

        var handler = new SpyHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        });
        using var client = new HttpClient(handler);

        vm.VerifyCtdb = true;
        var rip = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_CtdbOn"), httpClient: client);
        Assert.True(rip.Success, rip.ErrorMessage);

        Assert.Single(handler.Requests);
        Assert.Equal("db.cuetools.net", handler.Requests[0].Host);
        Assert.Equal("/lookup2.php", handler.Requests[0].AbsolutePath);
        Assert.Contains("CTDB:             checked", vm.RipReport);
        Assert.Contains("accurate", vm.RipReport);
    }

    [Fact]
    public async Task Rip_WithVerifyCtdb_RepairsUnreadableSectorsAndConfirmsWithAccurateRip()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        vm.VerifyCtdb = false;
        var baseline = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_RepairBase"));
        Assert.True(baseline.Success, baseline.ErrorMessage);
        byte[] cleanPcm = baseline.TrackInfos.OrderBy(t => t.AudioTrackIndex)
            .SelectMany(t => WavFileCodec.DecodeToRedBookPcm(t.FilePath)).ToArray();
        var server = new FakeCtdbServer(
            cleanPcm,
            baseline.CtdbDiscCrc!.Value.ToString("x8"),
            baseline.TrackInfos.OrderBy(t => t.AudioTrackIndex).Select(t => t.AccurateRipV2).ToList());
        using var client = new HttpClient(server);

        // Two unreadable sectors in track 2 (LBA 75..224), zero-filled because "continue on unreadable" is on.
        var medium = _deviceManager.GetVirtualRecorders().Single(r => r.UniqueId == "VDRV-01").CurrentMedium!;
        medium.SimulatedBadLbas.Add(133);
        medium.SimulatedBadLbas.Add(134);
        vm.ContinueOnUnreadable = true;
        vm.VerifyCtdb = true;
        var rip = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "Rip_Repaired"), httpClient: client);

        Assert.True(rip.Success, rip.ErrorMessage);
        Assert.Equal(2, rip.BadSectorLbas.Count);
        Assert.Contains("CTDB Repair:      repaired", vm.RipReport);
        Assert.Contains("AccurateRip was contacted only to confirm this repair.", vm.RipReport);
        Assert.Equal(cleanPcm, rip.TrackInfos.OrderBy(t => t.AudioTrackIndex)
            .SelectMany(t => WavFileCodec.DecodeToRedBookPcm(t.FilePath)).ToArray());
        Assert.Equal(baseline.CtdbDiscCrc, rip.CtdbDiscCrc);
        Assert.Contains(server.Requests, u => u.Host == "p.cuetools.net");
    }

    private sealed class SpyHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != null)
            {
                Requests.Add(request.RequestUri);
            }
            return Task.FromResult(respond(request));
        }
    }
}
