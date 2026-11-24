using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// "Fill empty space with a picture" on audio CDs: the music session as before, then a second session with a small
/// ISO 9660 volume and Mode 1 picture sectors to the end of the disc, burned on the simulated drive through
/// <c>burn-audio --native</c>, checked with <c>verify --cue</c>, and read back.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class AudioCdFillPictureTests : IDisposable
{
    // A small CD-R keeps the simulated disc in memory; the picture is drawn where these sectors lie on a real 80-min disc.
    private const int DiscSectors = 20_000;
    private const int Size = 64;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_cdfill_" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _deviceService;

    public AudioCdFillPictureTests()
    {
        Directory.CreateDirectory(_tempDir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _deviceService = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Left half black, right half white.</summary>
    private static int Level(int x) => x < Size / 2 ? 0 : 255;

    private static byte[] HalfPng(int size = Size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < size / 2 ? 0 : 255);
                pixels[o + 3] = 255;
            }
        using var ms = new MemoryStream();
        string tmp = Path.Combine(Path.GetTempPath(), $"cdfill_{Guid.NewGuid():N}.png");
        try
        {
            PngRaster.SaveBgra32(tmp, new BgraRaster(size, size, pixels));
            return File.ReadAllBytes(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private string WritePicture(string name, byte[] bytes)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Two noise tracks as WAV files and a CUE sheet naming them.</summary>
    private string WriteAlbum(string folder = "album")
    {
        string dir = Path.Combine(_tempDir, folder);
        Directory.CreateDirectory(dir);
        var random = new Random(7);
        foreach (var (name, sectors) in new[] { ("01.wav", 300), ("02.wav", 320) })
        {
            byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
            random.NextBytes(pcm);
            WavFileCodec.WriteWavFile(Path.Combine(dir, name), pcm);
        }
        string cue = Path.Combine(dir, "album.cue");
        File.WriteAllLines(cue, new[]
        {
            "PERFORMER \"Studio Band\"", "TITLE \"Picture Album\"",
            "FILE \"01.wav\" WAVE", "  TRACK 01 AUDIO", "    TITLE \"One\"", "    INDEX 01 00:00:00",
            "FILE \"02.wav\" WAVE", "  TRACK 02 AUDIO", "    TITLE \"Two\"", "    INDEX 01 00:00:00",
        });
        return cue;
    }

    private async Task<(int Exit, string Out, string Err)> Cli(HttpClient? http, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine, http);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Task<(int Exit, string Out, string Err)> Cli(params string[] args) => Cli(null, args);

    private static byte[] ReadSectors(VirtualDiscMedium medium, long first, long count)
    {
        byte[] data = new byte[count * OpticalConstants.DataSectorSize];
        for (long i = 0; i < count; i++)
        {
            medium.ReadSector((int)(first + i), data.AsSpan((int)(i * OpticalConstants.DataSectorSize), OpticalConstants.DataSectorSize),
                OpticalConstants.DataSectorSize);
        }
        return data;
    }

    private static (int Number, int Start, int Count, bool Audio, string? Title)[] TrackList(VirtualSession session)
        => session.Tracks.Select(t => (t.TrackNumber, t.StartLba, t.SectorCount, t.IsAudio, t.Title)).ToArray();

    [Fact]
    public async Task Picture_session_follows_the_music_verifies_reads_back_and_fills_the_disc()
    {
        using var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(medium);
        string cue = WriteAlbum();
        byte[] png = HalfPng();
        string picture = WritePicture("art/picture.png", png);
        string logs = Path.Combine(_tempDir, "logs");

        var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
            "--fill-picture", picture, "--log-dir", logs, "--json");

        Assert.True(burn.Exit == 0, burn.Out + burn.Err);
        long pictureStart, pictureEnd;
        int volumeSectors;
        using (JsonDocument doc = JsonDocument.Parse(burn.Out.Trim()))
        {
            JsonElement fill = doc.RootElement.GetProperty("fillPicture");
            Assert.Equal("chosen", fill.GetProperty("source").GetString());
            pictureStart = fill.GetProperty("pictureStartLba").GetInt64();
            pictureEnd = fill.GetProperty("pictureEndLba").GetInt64();
            volumeSectors = fill.GetProperty("volumeSectors").GetInt32();
            Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        }

        // Two sessions, the disc closed. Session 2 starts after session 1's lead-out (6,750), its own lead-in (4,500) and
        // the first track's pre-gap (150): Orange Book multisession layout, 11,400 sectors.
        Assert.Equal(2, medium.Sessions.Count);
        Assert.True(medium.IsFinalized);
        int nwa = medium.Sessions[1].StartLba;
        Assert.Equal(medium.Sessions[0].SectorCount + 11_400, nwa);
        Assert.All(medium.Sessions[0].Tracks, t => Assert.True(t.IsAudio));
        VirtualTrack data = Assert.Single(medium.Sessions[1].Tracks);
        Assert.False(data.IsAudio);

        // The session runs to the disc's end, less the 2 run-out blocks of a Track-at-Once track and the margin kept.
        Assert.Equal(DiscSectors - AudioCdBurnService.SessionTwoRunOutSectors - AudioCdFillPicture.EndMarginSectors, pictureEnd);
        Assert.Equal(pictureEnd - nwa, data.SectorCount);

        // The music session checks out against the CUE it was burned from.
        var verify = await Cli("verify", "--drive", "VDRV-01", "--cue", cue, "--log-dir", logs, "--json");
        Assert.True(verify.Exit == 0, verify.Out + verify.Err);

        // The volume reads at the session start: the Blue Book folders, INFO.CDP at session sector 75, and the picture file.
        byte[] volume = ReadSectors(medium, nwa, volumeSectors);
        // The reader takes the disc from LBA 0 (the file system's addresses are absolute): zeros up to the session start.
        byte[] fromZero = new byte[(long)(nwa + volumeSectors) * 2048];
        volume.CopyTo(fromZero, (long)nwa * 2048);
        using (var iso = IsoImageReader.FromStream(new MemoryStream(fromZero), leaveOpen: false, sessionStartLba: nwa))
        {
            Assert.True(iso.HasJoliet);
            Assert.False(iso.HasUdf);
            IsoFileEntry? info = iso.FindFile("/CDPLUS/INFO.CDP");
            Assert.NotNull(info);
            Assert.Equal(nwa + 75, info!.StartLba);
            IsoFileEntry? cover = iso.FindFile("/PICTURES/COVER.PNG");
            Assert.NotNull(cover);
            Assert.Equal(png, ReadSectors(medium, cover!.StartLba, (cover.SizeBytes + 2047) / 2048)[..(int)cover.SizeBytes]);
        }
        Assert.Equal((byte)'C', volume[75 * 2048]);
        Assert.Equal((byte)'D', volume[75 * 2048 + 1]);

        // ECMA-119 8.4.8: the volume space size (PVD bytes 80-83, little-endian, counted from LBA 0 on a multisession
        // disc) ends before the picture; the sectors between are zero.
        int volumeSpace = BinaryPrimitives.ReadInt32LittleEndian(volume.AsSpan(16 * 2048 + 80, 4));
        Assert.Equal(1, volume[16 * 2048]); // primary volume descriptor
        Assert.Equal(nwa + volumeSectors, volumeSpace);
        Assert.True(volumeSpace <= pictureStart - AudioCdFillPicture.GapSectors);
        Assert.All(ReadSectors(medium, volumeSpace, pictureStart - volumeSpace), b => Assert.Equal(0, b));

        // Read the picture back the way the disc shows it (CD drawing geometry, absolute time 0 = LBA -150).
        CdDiscParameters disc = AudioCdFillPicture.DefaultDisc();
        const int render = 1024;
        byte[] sectors = ReadSectors(medium, pictureStart, pictureEnd - pictureStart);
        byte[] pixels = CdMode1Drawing.RenderSectors(new MemoryStream(sectors), pictureStart + 150, pictureEnd + 150, disc, render, byteStep: 1);
        double inner = disc.RadiusFromByte((pictureStart + 150) * CdDiscParameters.BytesPerSector);
        double outer = disc.RadiusFromByte((pictureEnd + 150) * CdDiscParameters.BytesPerSector);
        double mmPerPixel = disc.PreviewImageOuterRadiusMm / (render / 2.0);
        int checkedPixels = 0;
        for (int y = 0; y < render; y++)
            for (int x = 0; x < render; x++)
            {
                double dx = x - render / 2.0, dy = y - render / 2.0;
                double r = Math.Sqrt(dx * dx + dy * dy) * mmPerPixel;
                // Skip the seam: within two source pixels of the black/white edge the drawing blends the two levels.
                if (r < inner + 2 * mmPerPixel || r > outer - 2 * mmPerPixel || Math.Abs(dx) < 2.0 * render / Size) continue;
                Assert.Equal(dx < 0 ? 0 : 255, pixels[(y * render + x) * 4]);
                checkedPixels++;
            }
        Assert.True(checkedPixels > 1000, $"only {checkedPixels} pixels checked");
    }

    [Fact]
    public async Task Player_visible_track_list_is_the_same_with_and_without_the_picture()
    {
        string cue = WriteAlbum();
        string picture = WritePicture("picture.png", HalfPng());

        using var plain = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(plain);
        var first = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--json");
        Assert.True(first.Exit == 0, first.Out + first.Err);

        using var filled = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(filled);
        var second = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--fill-picture", picture, "--json");
        Assert.True(second.Exit == 0, second.Out + second.Err);

        // A CD player reads the first session's table of contents only: same tracks, same places, same text and codes.
        Assert.Single(plain.Sessions);
        Assert.Equal(2, filled.Sessions.Count);
        Assert.Equal(TrackList(plain.Sessions[0]), TrackList(filled.Sessions[0]));
        Assert.Equal(plain.Sessions[0].SectorCount, filled.Sessions[0].SectorCount);
        Assert.Equal(plain.CdTextBytes, filled.CdTextBytes);
        Assert.Equal(plain.MediaCatalogNumber, filled.MediaCatalogNumber);
        // No extra audio track anywhere: the picture is a data track.
        Assert.Equal(2, filled.Sessions.SelectMany(s => s.Tracks).Count(t => t.IsAudio));
        Assert.All(filled.Sessions[1].Tracks, t => Assert.False(t.IsAudio));
    }

    [Fact]
    public async Task Option_is_off_by_default_in_the_burn_options_and_the_cli()
    {
        Assert.Null(new AudioBurnOptions().SessionTwoImage);

        using var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(medium);
        // A cover.jpg next to the files is not used unless the option is given.
        string cue = WriteAlbum();
        WritePicture("album/cover.png", HalfPng());
        var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--json");

        Assert.True(burn.Exit == 0, burn.Out + burn.Err);
        Assert.Single(medium.Sessions);
        using JsonDocument doc = JsonDocument.Parse(burn.Out.Trim());
        Assert.False(doc.RootElement.TryGetProperty("fillPicture", out _));
        Assert.False(doc.RootElement.GetProperty("dataSessionWritten").GetBoolean());
    }

    [Fact]
    public async Task Refusals_write_nothing()
    {
        string cue = WriteAlbum();
        string picture = WritePicture("picture.png", HalfPng());

        using (var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(medium);
            var noNative = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--fill-picture", picture);
            Assert.NotEqual(0, noNative.Exit);
            Assert.Contains("--native", noNative.Err);

            var withFolder = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--fill-picture", picture, "--data-folder", _tempDir);
            Assert.NotEqual(0, withFolder.Exit);

            string notPicture = WritePicture("notes.png", Encoding.ASCII.GetBytes("not a picture"));
            var bad = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--fill-picture", notPicture);
            Assert.NotEqual(0, bad.Exit);
            Assert.Contains("Nothing was written", bad.Err);
            Assert.True(medium.IsBlank);
            Assert.Empty(medium.Sessions);
        }

        // A disc with no room for one minute of picture after the music is refused before the audio is written.
        using var small = new VirtualDiscMedium(DiscMediaType.CdR, 14_000);
        _recorder.InsertMedia(small);
        var full = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--fill-picture", picture, "--json");
        Assert.NotEqual(0, full.Exit);
        Assert.Contains("Nothing was written", full.Out);
        Assert.Empty(small.Sessions);
    }

    [Fact]
    public void Auto_picture_takes_embedded_art_first_then_cover_then_folder()
    {
        byte[] embedded = HalfPng(40);
        byte[] cover = HalfPng(48);
        byte[] folder = HalfPng(56);
        string dir = Path.Combine(_tempDir, "flacs");
        Directory.CreateDirectory(dir);
        string flac = Path.Combine(dir, "01.flac");
        File.WriteAllBytes(flac, FlacFileCodec.CreateFlacBytes(new byte[44_100 * 4],
            tags: new AudioTrackTags("One", "Band", "Album", 1, 1, CoverArtBytes: embedded, CoverArtMimeType: "image/png")));
        string plainFlac = Path.Combine(dir, "02.flac");
        File.WriteAllBytes(plainFlac, FlacFileCodec.CreateFlacBytes(new byte[44_100 * 4]));
        File.WriteAllBytes(Path.Combine(dir, "cover.png"), cover);
        File.WriteAllBytes(Path.Combine(dir, "folder.jpg"), folder); // PNG bytes under a .jpg name still decode

        AudioCdCompilation Album(params string[] files)
        {
            var c = new AudioCdCompilation { AlbumTitle = "Album", AlbumPerformer = "Band" };
            for (int i = 0; i < files.Length; i++) c.AddTrack(new AudioCdTrackItem(i + 1, files[i], $"T{i + 1}", "Band"));
            return c;
        }

        AudioCdPicture? withArt = AudioCdFillPicture.FindLocalPicture(Album(plainFlac, flac), null);
        Assert.Equal("embedded", withArt?.Source);
        Assert.Equal(embedded, withArt!.Bytes);

        AudioCdPicture? withCover = AudioCdFillPicture.FindLocalPicture(Album(plainFlac), null);
        Assert.Equal("file", withCover?.Source);
        Assert.Equal(cover, withCover!.Bytes);

        File.Delete(Path.Combine(dir, "cover.png"));
        AudioCdPicture? withFolder = AudioCdFillPicture.FindLocalPicture(Album(plainFlac), null);
        Assert.Equal(folder, withFolder!.Bytes);

        File.Delete(Path.Combine(dir, "folder.jpg"));
        Assert.Null(AudioCdFillPicture.FindLocalPicture(Album(plainFlac), null));
    }

    [Fact]
    public async Task Online_cover_lookup_runs_only_with_lookup_and_uses_the_album_tags()
    {
        string cue = WriteAlbum(); // no art next to it
        const string release = "6a5e4f2b-1c3d-4e5f-8a9b-0c1d2e3f4a5b";
        var handler = new CoverServer(release, HalfPng());
        using var http = new HttpClient(handler);

        // Without --lookup nothing is asked online, and the disc is burned without the picture.
        using (var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(medium);
            var burn = await Cli(http, "burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--fill-picture", "auto");
            Assert.True(burn.Exit == 0, burn.Out + burn.Err);
            Assert.Contains("No picture fill", burn.Err);
            Assert.Empty(handler.Requests);
            Assert.Single(medium.Sessions);
        }

        // With --lookup: MusicBrainz release search by album title and artist, then the Cover Art Archive front cover.
        using (var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(medium);
            var burn = await Cli(http, "burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--fill-picture", "auto", "--lookup", "--json");
            Assert.True(burn.Exit == 0, burn.Out + burn.Err);
            using JsonDocument doc = JsonDocument.Parse(burn.Out.Trim());
            Assert.Equal("coverartarchive", doc.RootElement.GetProperty("fillPicture").GetProperty("source").GetString());
            Assert.Equal(2, medium.Sessions.Count);
        }

        Uri search = handler.Requests[0];
        Assert.Equal("musicbrainz.org", search.Host);
        Assert.Equal("/ws/2/release/", search.AbsolutePath);
        string query = Uri.UnescapeDataString(search.Query);
        Assert.Contains("release:\"Picture Album\"", query);
        Assert.Contains("artist:\"Studio Band\"", query);
        Assert.Equal($"https://coverartarchive.org/release/{release}/front-500", handler.Requests[1].ToString());
    }

    [Fact]
    public async Task Audio_screen_option_is_off_by_default_excludes_other_session_two_uses_and_burns_the_picture()
    {
        string cue = WriteAlbum();
        string dir = Path.GetDirectoryName(cue)!;
        WritePicture("album/cover.png", HalfPng());
        using var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(medium);
        var vm = new CdBurner.App.ViewModels.AudioBurnViewModel(_deviceService, _burnEngine)
        {
            SelectedRecorder = _recorder,
            AlbumTitle = "Picture Album",
            AlbumPerformer = "Studio Band",
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };

        Assert.False(vm.FillWithPicture);
        Assert.False(vm.FillPictureLookupOnline);
        Assert.Equal(string.Empty, vm.FillPicturePath);

        vm.AddAudioFiles(new[] { Path.Combine(dir, "01.wav"), Path.Combine(dir, "02.wav") });
        vm.IncludeRecoverySession = true;
        vm.FillWithPicture = true;
        Assert.False(vm.IncludeRecoverySession);
        Assert.True(vm.UseDirectDriveCommands);
        vm.DataSessionFolderPath = dir;
        Assert.False(vm.FillWithPicture);
        vm.FillWithPicture = true;
        Assert.False(vm.HasDataSessionFolder);

        // Automatic picture: cover.png next to the files.
        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage + " / " + vm.StatusMessage);
        Assert.Contains("the picture fills the rest of the disc", vm.StatusMessage);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.True(medium.IsFinalized);
        Assert.All(medium.Sessions[0].Tracks, t => Assert.True(t.IsAudio));
    }

    [Fact]
    public async Task Temp_space_is_checked_from_the_real_layout_before_any_write_and_names_missing_megabytes()
    {
        string cue = WriteAlbum();
        string dir = Path.GetDirectoryName(cue)!;
        byte[] png = HalfPng();
        string picture = WritePicture("picture.png", png);
        string tempRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        // Measure the exact session-2 image sectors from a real layout on this disc (far below 700 MB).
        var compilation = new AudioCdCompilation { AlbumTitle = "Picture Album", AlbumPerformer = "Studio Band" };
        compilation.ImportCueSheet(cue);
        var session = new AudioCdPictureSession(AudioCdFillPicture.LoadChosen(picture), BlueBookDiscInfo.From(compilation),
            tempFolder: _tempDir);
        int predictedNwa = 620 + (int)CdBurner.Core.Archival.RecoverySessionBudget.FixedSessionSectors;
        long predictedAvailable = AudioCdBurnService.SessionTwoSectorsAvailable(predictedNwa, DiscSectors, 0);
        Assert.Null(session.PreflightError(predictedNwa, predictedAvailable));

        // Build once into an isolated medium to know the exact session-2 image size.
        long exactImageSectors;
        using (var probe = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(probe);
            var probeBurn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--fill-picture", picture, "--json");
            Assert.True(probeBurn.Exit == 0, probeBurn.Out + probeBurn.Err);
            using JsonDocument doc = JsonDocument.Parse(probeBurn.Out.Trim());
            JsonElement fill = doc.RootElement.GetProperty("fillPicture");
            exactImageSectors = fill.GetProperty("pictureEndLba").GetInt64() - fill.GetProperty("sessionStartLba").GetInt64();
        }
        long exactNeededBytes = exactImageSectors * IsoUdfImageBuilder.SectorSize + 64L * 1024 * 1024;
        Assert.True(exactNeededBytes < 120L * 1024 * 1024);

        try
        {
            // 10 MB short of the real layout size: refused before anything is written on CLI and screen, naming drive + missing MB.
            DiscFillPicture.AvailableFreeSpaceOverride.Value = _ => exactNeededBytes - 10L * 1048576L;

            using (var untouched = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
            {
                _recorder.InsertMedia(untouched);
                var refused = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                    "--fill-picture", picture, "--json");
                Assert.NotEqual(0, refused.Exit);
                Assert.True(untouched.IsBlank);
                Assert.Empty(untouched.Sessions);
                Assert.Equal(0, untouched.UsedSectors);
                Assert.Contains(tempRoot, refused.Out);
                Assert.Contains("10 MB missing", refused.Out);
                Assert.Contains("Nothing was written", refused.Out);
            }

            using (var vmMedium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
            {
                _recorder.InsertMedia(vmMedium);
                var vm = new CdBurner.App.ViewModels.AudioBurnViewModel(_deviceService, _burnEngine)
                {
                    SelectedRecorder = _recorder,
                    AlbumTitle = "Picture Album",
                    AlbumPerformer = "Studio Band",
                    FillWithPicture = true,
                    FillPicturePath = picture,
                    CalibrationStorePath = Path.Combine(_tempDir, "missing-store.json")
                };
                vm.AddAudioFiles(new[] { Path.Combine(dir, "01.wav"), Path.Combine(dir, "02.wav") });
                var vmResult = await vm.StartAudioBurnAsync("VDRV-01");
                Assert.False(vmResult.Success);
                Assert.True(vmMedium.IsBlank);
                Assert.Empty(vmMedium.Sessions);
                Assert.Contains(tempRoot, vm.StatusMessage);
                Assert.Contains("10 MB missing", vm.StatusMessage);
                Assert.Contains("Nothing was written", vm.StatusMessage);
            }

            // Exact free space for the real layout (~80 MB, well under 700 MB): accepted and written cleanly.
            DiscFillPicture.AvailableFreeSpaceOverride.Value = _ => exactNeededBytes;
            using (var fits = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
            {
                _recorder.InsertMedia(fits);
                var ok = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                    "--fill-picture", picture, "--json");
                Assert.True(ok.Exit == 0, ok.Out + ok.Err);
                Assert.Equal(2, fits.Sessions.Count);
                Assert.True(fits.IsFinalized);
            }
        }
        finally
        {
            DiscFillPicture.AvailableFreeSpaceOverride.Value = null;
        }
    }

    [Fact]
    public async Task Saved_calibration_is_used_for_the_picture_session_and_defaults_stay_unchanged_without_one()
    {
        string cue = WriteAlbum();
        string dir = Path.GetDirectoryName(cue)!;
        string picture = WritePicture("picture.png", HalfPng());
        string store = Path.Combine(_tempDir, "store", CalibrationStore.FileName);

        // Without a saved calibration, the default cd-80 preset is used and matches a direct default session byte-for-byte.
        byte[] defaultPictureSectors;
        long pictureStart, pictureEnd;
        using (var uncalibrated = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(uncalibrated);
            var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--fill-picture", picture, "--store", store, "--json");
            Assert.True(burn.Exit == 0, burn.Out + burn.Err);
            using JsonDocument doc = JsonDocument.Parse(burn.Out.Trim());
            JsonElement fill = doc.RootElement.GetProperty("fillPicture");
            Assert.Equal("disc preset 'cd-80'", fill.GetProperty("discFrom").GetString());
            pictureStart = fill.GetProperty("pictureStartLba").GetInt64();
            pictureEnd = fill.GetProperty("pictureEndLba").GetInt64();
            defaultPictureSectors = ReadSectors(uncalibrated, pictureStart, pictureEnd - pictureStart);
        }

        // Save a CD calibration for this drive and media with shifted radii and non-zero pitch.
        var calibrated = new CalibrationParameters(CalibrationDiscKind.Cd, 25.5, 56.5, 360_000, 1200.0, PitchLinear: 0.002);
        CalibrationStore.Save(store, new CalibrationStoreEntry(
            "IGNIS VIRTUAL DVD/CD-RW", "VIRTUAL-OPTICAL-01", calibrated, DateTimeOffset.UnixEpoch, "target-cd", "fine", 0.04));

        byte[] cliCalibratedSectors;
        using (var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(medium);
            var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
                "--fill-picture", picture, "--store", store, "--json");
            Assert.True(burn.Exit == 0, burn.Out + burn.Err);
            using JsonDocument doc = JsonDocument.Parse(burn.Out.Trim());
            JsonElement fill = doc.RootElement.GetProperty("fillPicture");
            Assert.Contains("saved calibration", fill.GetProperty("discFrom").GetString());
            Assert.Equal(pictureStart, fill.GetProperty("pictureStartLba").GetInt64());
            Assert.Equal(pictureEnd, fill.GetProperty("pictureEndLba").GetInt64());
            cliCalibratedSectors = ReadSectors(medium, pictureStart, pictureEnd - pictureStart);
        }

        Assert.False(defaultPictureSectors.AsSpan().SequenceEqual(cliCalibratedSectors));

        // Read the calibrated sectors back with the calibrated geometry: the half-black / half-white picture matches.
        CdDiscParameters calibratedCd = AudioCdFillPicture.ToCdDiscParameters(calibrated);
        const int render = 1024;
        byte[] pixels = CdMode1Drawing.RenderSectors(new MemoryStream(cliCalibratedSectors),
            pictureStart + AudioCdFillPicture.GeometryLbaOffset, pictureEnd + AudioCdFillPicture.GeometryLbaOffset,
            calibratedCd, render, byteStep: 1);
        double inner = calibratedCd.RadiusFromByte((pictureStart + AudioCdFillPicture.GeometryLbaOffset) * CdDiscParameters.BytesPerSector);
        double outer = calibratedCd.RadiusFromByte((pictureEnd + AudioCdFillPicture.GeometryLbaOffset) * CdDiscParameters.BytesPerSector);
        double mmPerPixel = calibratedCd.PreviewImageOuterRadiusMm / (render / 2.0);
        int checkedPixels = 0;
        for (int y = 0; y < render; y++)
            for (int x = 0; x < render; x++)
            {
                double dx = x - render / 2.0, dy = y - render / 2.0;
                double r = Math.Sqrt(dx * dx + dy * dy) * mmPerPixel;
                if (r < inner + 2 * mmPerPixel || r > outer - 2 * mmPerPixel || Math.Abs(dx) < 2.0 * render / Size) continue;
                Assert.Equal(dx < 0 ? 0 : 255, pixels[(y * render + x) * 4]);
                checkedPixels++;
            }
        Assert.True(checkedPixels > 1000, $"only {checkedPixels} pixels checked");

        // Audio CD screen uses the same saved calibration for the drive and media and writes the exact same bytes.
        using (var vmMedium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors))
        {
            _recorder.InsertMedia(vmMedium);
            var vm = new CdBurner.App.ViewModels.AudioBurnViewModel(_deviceService, _burnEngine)
            {
                SelectedRecorder = _recorder,
                AlbumTitle = "Picture Album",
                AlbumPerformer = "Studio Band",
                FillWithPicture = true,
                FillPicturePath = picture,
                CalibrationStorePath = store
            };
            vm.AddAudioFiles(new[] { Path.Combine(dir, "01.wav"), Path.Combine(dir, "02.wav") });
            var result = await vm.StartAudioBurnAsync("VDRV-01");
            Assert.True(result.Success, result.ErrorMessage + " / " + vm.StatusMessage);
            byte[] vmSectors = ReadSectors(vmMedium, pictureStart, pictureEnd - pictureStart);
            Assert.Equal(cliCalibratedSectors, vmSectors);
        }

        // Raw ATIP prefix fallback (as media-info appends the manufacturer name) and non-CD filtering.
        var rawAtipCalibrated = new CalibrationParameters(CalibrationDiscKind.Cd, 25.0, 57.0, 360_000, 1200.0);
        CalibrationStore.Save(store, new CalibrationStoreEntry(
            "HL-DT-ST DVDRAM GP60NB50", "97m26s00f", rawAtipCalibrated, DateTimeOffset.UnixEpoch, "target-atip", "coarse", 0.1));
        CalibrationStore.Save(store, new CalibrationStoreEntry(
            "HL-DT-ST DVDRAM GP60NB50", "MCC 03RG20", new CalibrationParameters(CalibrationDiscKind.Dvd, 24.0, 58.0, 2_295_104, 133.3),
            DateTimeOffset.UnixEpoch, "target-dvd", "coarse", 0.1));

        CdDiscParameters resolvedAtip = AudioCdFillPicture.ResolveDisc(
            "HL-DT-ST DVDRAM GP60NB50", "97m26s00f (FORNET - Long Strategy (Cyanine / AZO))", store, out string? atipSource);
        Assert.Contains("97m26s00f", atipSource);
        Assert.Equal(25.0, resolvedAtip.InnerRadiusMm);

        CdDiscParameters ignoredDvd = AudioCdFillPicture.ResolveDisc(
            "HL-DT-ST DVDRAM GP60NB50", "MCC 03RG20", store, out string? dvdSource);
        Assert.Null(dvdSource);
        Assert.Equal(AudioCdFillPicture.DefaultDisc(), ignoredDvd);
    }

    [Fact]
    public async Task Corrupt_calibration_store_does_not_break_picture_resolution_or_burn()
    {
        string cue = WriteAlbum();
        string picture = WritePicture("picture.png", HalfPng());
        string store = Path.Combine(_tempDir, "corrupt", CalibrationStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "{ corrupt json");

        CdDiscParameters fallback = AudioCdFillPicture.ResolveDisc(
            "IGNIS VIRTUAL DVD/CD-RW", "VIRTUAL-OPTICAL-01", store, out string? source);
        Assert.Null(source);
        Assert.Equal(AudioCdFillPicture.DefaultDisc(), fallback);

        using var medium = new VirtualDiscMedium(DiscMediaType.CdR, DiscSectors);
        _recorder.InsertMedia(medium);
        var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native",
            "--fill-picture", picture, "--store", store, "--json");
        Assert.True(burn.Exit == 0, burn.Out + burn.Err);
        Assert.Equal(2, medium.Sessions.Count);
    }

    /// <summary>Answers a MusicBrainz release search with one release and the Cover Art Archive with a PNG.</summary>
    private sealed class CoverServer(string releaseId, byte[] png) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri);
            HttpResponseMessage response;
            if (uri.Host == "musicbrainz.org")
            {
                string json = $$"""{"created":"2026-11-17T00:00:00Z","count":1,"offset":0,"releases":[{"id":"{{releaseId}}","score":100,"title":"Picture Album","artist-credit":[{"name":"Studio Band"}]}]}""";
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            else if (uri.Host == "coverartarchive.org" && uri.AbsolutePath == $"/release/{releaseId}/front-500")
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Task.FromResult(response);
        }
    }

    private sealed class SingleDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
