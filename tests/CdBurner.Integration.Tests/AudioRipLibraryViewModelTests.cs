using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
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
/// The ripper screen's format checkboxes, library folder, file-name template, saved settings and MusicBrainz edition
/// choice in <see cref="AudioBurnViewModel"/>. Network answers come from stub handlers; no test contacts the internet.
/// </summary>
public sealed class AudioRipLibraryViewModelTests : IDisposable
{
    private const string DeReleaseId = "0d3e1c3a-7a4b-4b1a-9c55-0a8f6b6a1e01";
    private const string CaReleaseId = "5b8d2a4c-1f3e-4d6a-8b7c-2e9f0a1b3c02";
    private const string UsReleaseId = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c03";

    private static readonly byte[] TinyPng =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01];

    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioRipLibraryViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipLibraryTests_" + Guid.NewGuid().ToString("N"));
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
            // Best effort cleanup of a temp folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A virtual audio CD with three tracks, like the disc the command-line tests rip.</summary>
    private async Task<AudioBurnViewModel> CreateVmWithThreeTrackDiscAsync(AppSettingsService? settings = null)
    {
        var wavs = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string wav = Path.Combine(_tempDir, $"Track0{i}.wav");
            byte[] pcm = new byte[(60 + i * 15) * OpticalConstants.RawSectorSize];
            for (int b = 0; b < pcm.Length; b++) pcm[b] = (byte)((b * (i + 1)) % 251);
            WavFileCodec.WriteWavFile(wav, pcm);
            wavs.Add(wav);
        }

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settings)
        {
            AlbumTitle = "Rip Library Album",
            AlbumPerformer = "Test Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = false
        };
        vm.AddAudioFilesCommand.Execute(wavs.ToArray());
        var burn = await vm.StartAudioBurnAsync("VDRV-01");
        Assert.True(burn.Success, $"Burn failed: {burn.ErrorMessage}");
        vm.VerifyAccurateRip = false;
        vm.VerifyCtdb = false;
        return vm;
    }

    /// <summary>
    /// Three releases that share the disc ID: tracks 1 and 2 agree, track 3 (the virtual disc's last track) is titled
    /// differently on the German release.
    /// </summary>
    private static string ThreeReleasesJson()
    {
        static string Release(string id, string date, string country, string track3) => $$"""
            {
              "id": "{{id}}", "title": "Sehnsucht", "date": "{{date}}", "country": "{{country}}",
              "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ],
              "media": [ { "tracks": [
                { "position": 1, "title": "Spieluhr", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] },
                { "position": 2, "title": "Bück dich", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] },
                { "position": 3, "title": "{{track3}}", "artist-credit": [ { "name": "Rammstein", "joinphrase": "" } ] }
              ] } ]
            }
            """;

        return "{ \"releases\": [" +
            Release(DeReleaseId, "1998-01-13", "DE", "Du riechst so gut '98") + "," +
            Release(CaReleaseId, "1997", "CA", "Stripped") + "," +
            Release(UsReleaseId, "1997", "US", "Stripped") + "] }";
    }

    private static SpyHttpHandler MusicBrainzAndCoverStub() => new(request =>
    {
        if (request.RequestUri!.Host.Contains("coverartarchive", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyPng) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ThreeReleasesJson(), Encoding.UTF8, "application/json")
        };
    });

    [Fact]
    public void Settings_FromAnOldFileWithoutTheNewKeys_LoadWithTheDefaults()
    {
        string path = Path.Combine(_tempDir, "old-settings.json");
        AppSettingsService.SaveToFile(new AppSettings { AudioReadOffsetSamples = 12, SecureAudioRipByDefault = true }, path);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string[] newKeys = ["RipFormats", "Mp3Quality", "RipLibraryFolder", "RipNamingTemplate", "FetchCoverArtByDefault", "WriteFolderImage"];
        int removed = 0;
        foreach (string key in node.Select(p => p.Key).Where(k => newKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
            removed++;
        }

        Assert.Equal(newKeys.Length, removed);
        File.WriteAllText(path, node.ToJsonString());

        var service = new AppSettingsService(path);
        service.Load();
        Assert.Equal(12, service.CurrentSettings.AudioReadOffsetSamples);
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: service);

        Assert.True(vm.RipAsWav);
        Assert.False(vm.RipAsFlac);
        Assert.False(vm.RipAsMp3);
        Assert.False(vm.RipAsOpus);
        Assert.False(vm.RipAsAac);
        Assert.Equal("320 kbit/s", vm.SelectedMp3Quality);
        Assert.Equal(string.Empty, vm.RipLibraryFolder);
        Assert.Equal(AudioNamingTemplate.Default, vm.RipNamingTemplate);
        Assert.True(vm.FetchCoverArt);
        Assert.True(vm.WriteFolderImage);
        Assert.False(vm.OverwriteExistingFiles);
    }

    [Fact]
    public void Settings_ChangesToFormatsLibraryAndTemplateAreSaved()
    {
        string path = Path.Combine(_tempDir, "saved-settings.json");
        var service = new AppSettingsService(path);
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: service);
        string library = Path.Combine(_tempDir, "Music Library");

        vm.RipAsMp3 = true;
        vm.SelectedMp3Quality = "VBR V2 (about 190 kbit/s)";
        vm.RipLibraryFolder = library;
        vm.RipNamingTemplate = AudioNamingTemplate.Flat;
        vm.FetchCoverArt = false;
        vm.WriteFolderImage = false;

        var saved = service.CurrentSettings;
        Assert.Equal("wav,mp3", saved.RipFormats);
        Assert.Equal("VBR V2 (about 190 kbit/s)", saved.Mp3Quality);
        Assert.Equal(library, saved.RipLibraryFolder);
        Assert.Equal(AudioNamingTemplate.Flat, saved.RipNamingTemplate);
        Assert.False(saved.FetchCoverArtByDefault);
        Assert.False(saved.WriteFolderImage);

        // The file on disk carries them too: a new run starts with the same choices.
        var reloaded = new AppSettingsService(path);
        reloaded.Load();
        var vm2 = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: reloaded);
        Assert.True(vm2.RipAsWav);
        Assert.True(vm2.RipAsMp3);
        Assert.False(vm2.RipAsFlac);
        Assert.Equal("VBR V2 (about 190 kbit/s)", vm2.SelectedMp3Quality);
        Assert.Equal(library, vm2.RipLibraryFolder);
        Assert.Equal(AudioNamingTemplate.Flat, vm2.RipNamingTemplate);
        Assert.False(vm2.FetchCoverArt);
        Assert.False(vm2.WriteFolderImage);
    }

    [Fact]
    public void SelectedRipFormat_StaysCompatibleWithTheCheckboxes()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);
        Assert.Equal(AudioRipFormat.Wav, vm.SelectedRipFormat);

        vm.SelectedRipFormat = AudioRipFormat.Flac;
        Assert.True(vm.RipAsFlac);
        Assert.False(vm.RipAsWav);
        Assert.False(vm.RipAsMp3);
        Assert.False(vm.RipAsOpus);
        Assert.False(vm.RipAsAac);

        vm.RipAsMp3 = true;
        Assert.Equal(AudioRipFormat.Flac, vm.SelectedRipFormat);
        Assert.True(vm.RipAsMp3);

        vm.RipAsFlac = false;
        Assert.Equal(AudioRipFormat.Mp3, vm.SelectedRipFormat);
    }

    [Fact]
    public void NamingTemplate_ShowsProblemAndPreview()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        Assert.Null(vm.RipNamingProblem);
        string expected = AudioNamingTemplate.Render(
            AudioNamingTemplate.Default, AudioBurnViewModel.NamingPreviewSample(AudioRipFormat.Wav), ".wav");
        Assert.Equal(expected, vm.RipNamingPreview);
        Assert.Equal(Path.Combine("Rammstein", "Sehnsucht (1997)", "12 - Stripped.wav"), vm.RipNamingPreview);

        vm.RipAsMp3 = true;
        vm.RipAsWav = false;
        Assert.EndsWith("12 - Stripped.mp3", vm.RipNamingPreview, StringComparison.Ordinal);

        vm.RipNamingTemplate = "{bogus}";
        Assert.Contains("Unknown token", vm.RipNamingProblem);
        Assert.Equal(string.Empty, vm.RipNamingPreview);
    }

    [Fact]
    public async Task Rip_WithNoFormatChecked_FailsBeforeReadingTheDisc()
    {
        var vm = await CreateVmWithThreeTrackDiscAsync();
        string outDir = Path.Combine(_tempDir, "no-format");
        vm.LookupMetadata = false;
        vm.RipAsWav = false;

        var result = await vm.RipAudioCdAsync("VDRV-01", outDir);

        Assert.False(result.Success);
        Assert.Contains("at least one output format", vm.RipReport);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task Rip_WithABadTemplateAndALibraryFolder_FailsBeforeReadingTheDisc()
    {
        var vm = await CreateVmWithThreeTrackDiscAsync();
        string outDir = Path.Combine(_tempDir, "bad-template");
        vm.LookupMetadata = false;
        vm.RipLibraryFolder = Path.Combine(_tempDir, "lib-bad");
        vm.RipNamingTemplate = "{bogus}";

        var result = await vm.RipAudioCdAsync("VDRV-01", outDir);

        Assert.False(result.Success);
        Assert.Contains("Unknown token", vm.RipReport);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task Rip_WithDifferingEditions_StopsBeforeReadingTheDiscThenFilesTheChosenEdition()
    {
        var vm = await CreateVmWithThreeTrackDiscAsync();
        var spy = MusicBrainzAndCoverStub();
        using var http = new HttpClient(spy);
        string outDir = Path.Combine(_tempDir, "rip-out");
        string library = Path.Combine(_tempDir, "library");
        vm.LookupMetadata = true;
        vm.RipAsWav = true;

        var stopped = await vm.RipAudioCdAsync("VDRV-01", outDir, httpClient: http);

        Assert.False(stopped.Success);
        Assert.Contains("Choose yours, then start the rip again.", vm.StatusMessage);
        Assert.Equal(3, vm.ReleaseChoices.Count);
        Assert.True(vm.HasReleaseChoice);
        Assert.Equal(-1, vm.SelectedReleaseIndex);
        Assert.Contains("track 3", vm.ReleaseDifferenceNote);
        Assert.Contains("Du riechst so gut '98", vm.ReleaseDifferenceNote);
        Assert.Contains("Stripped", vm.ReleaseDifferenceNote);
        Assert.Contains("Choose the one on your CD.", vm.ReleaseDifferenceNote);
        Assert.True(!Directory.Exists(outDir) || Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length == 0);
        Assert.All(spy.Requests, r => Assert.Contains("musicbrainz", r.Host, StringComparison.Ordinal));

        vm.SelectedReleaseIndex = 2;
        vm.RipLibraryFolder = library;
        var rip = await vm.RipAudioCdAsync("VDRV-01", outDir, httpClient: http);

        Assert.True(rip.Success, vm.RipReport);
        string albumDir = Path.Combine(library, "Rammstein", "Sehnsucht (1997)");
        Assert.True(File.Exists(Path.Combine(albumDir, "03 - Stripped.wav")), vm.RipReport);
        Assert.True(File.Exists(Path.Combine(albumDir, "01 - Spieluhr.wav")));
        Assert.Equal(TinyPng, File.ReadAllBytes(Path.Combine(albumDir, "folder.png")));
        Assert.Contains($"Edition:          US, 1997 (MusicBrainz {UsReleaseId})", vm.RipReport);
        Assert.Contains("Cover:            Cover Art Archive", vm.RipReport);
        Assert.Contains(spy.Requests, r => r.AbsolutePath == $"/release/{UsReleaseId}/front-500");
        Assert.Equal(3, rip.RippedFilePaths.Count);
    }

    [Fact]
    public async Task Rip_WithTheCoverOptionOffOrACoverFile_DoesNotContactTheCoverArtArchive()
    {
        var vm = await CreateVmWithThreeTrackDiscAsync();
        var spy = MusicBrainzAndCoverStub();
        using var http = new HttpClient(spy);
        vm.LookupMetadata = true;
        vm.RipLibraryFolder = Path.Combine(_tempDir, "library-nocover");
        vm.FetchCoverArt = false;

        var stopped = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "out-nocover"), httpClient: http);
        Assert.False(stopped.Success);
        vm.SelectedReleaseIndex = 0;
        var rip = await vm.RipAudioCdAsync("VDRV-01", Path.Combine(_tempDir, "out-nocover"), httpClient: http);

        Assert.True(rip.Success, vm.RipReport);
        Assert.DoesNotContain(spy.Requests, r => r.Host.Contains("coverartarchive", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_tempDir, "library-nocover", "Rammstein", "Sehnsucht (1998)", "03 - Du riechst so gut '98.wav")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_tempDir, "library-nocover"), "folder.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LookUpReleases_FillsTheChoicesWithoutReadingAudio()
    {
        var vm = await CreateVmWithThreeTrackDiscAsync();
        using var http = new HttpClient(MusicBrainzAndCoverStub());

        await vm.LookUpReleasesAsync(http);

        Assert.Equal(3, vm.ReleaseChoices.Count);
        Assert.True(vm.HasReleaseChoice);
        Assert.Equal(-1, vm.SelectedReleaseIndex);
        Assert.Contains("Choose yours", vm.StatusMessage);
        Assert.False(vm.IsBusy);
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
