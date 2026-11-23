using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Verifies picture-disc burn reporting and selective comparison across picture-only CDs/DVDs,
/// hybrid inner/outer-files DVDs, audio + picture CDs, and pre-gap picture CDs.
/// </summary>
public sealed class DiscArtPictureVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-picverify-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _devices;

    public DiscArtPictureVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _devices = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 4,
              "cdPresets": [
                { "id": "tiny-cd", "displayName": "Tiny CD", "description": "test", "sectors": 6000,
                  "innerRadiusMm": 24.5, "outerRadiusMm": 56.8, "linearVelocityMmPerSecond": 1200 },
                { "id": "cd-80", "displayName": "CD 80", "description": "test", "sectors": 360000,
                  "innerRadiusMm": 24.5, "outerRadiusMm": 58.0, "linearVelocityMmPerSecond": 1200 }
              ],
              "dvdPresets": [
                { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 },
                { "id": "small-dvd", "displayName": "Small DVD", "description": "test", "totalSectors": 32768,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": []
            }
            """);
        return path;
    }

    private string WritePicture()
    {
        string path = P("picture.png");
        if (File.Exists(path)) return path;
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < size / 2 ? 0 : 255);
                pixels[o + 3] = 255;
            }
        PngRaster.SaveBgra32(path, new BgraRaster(size, size, pixels));
        return path;
    }

    private string WriteFiles()
    {
        string folder = P("files");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.TXT"), "Hybrid disc payload text for sector comparison.\n", Encoding.UTF8);
        byte[] payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(folder, "DATA.BIN"), payload);
        return folder;
    }

    private static byte[] MakeNonZeroCdPcm(int sectors)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < pcm.Length; i += 4)
        {
            short l = (short)(8000 * Math.Sin((i / 4) * 0.13));
            short r = (short)(8000 * Math.Cos((i / 4) * 0.17));
            pcm[i] = (byte)(l & 0xFF);
            pcm[i + 1] = (byte)((l >> 8) & 0xFF);
            pcm[i + 2] = (byte)(r & 0xFF);
            pcm[i + 3] = (byte)((r >> 8) & 0xFF);
        }
        return pcm;
    }

    private static void WriteCdWav(string path, byte[] pcm)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(44100);
        w.Write(176400);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    [Fact]
    public async Task PictureOnlyDvd_WhenPictureSectorsReadWrongOrFail_SucceedsAndReportsNotCompared()
    {
        string iso = P("pic-only-dvd.iso");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd");
        Assert.True(made == 0, madeErr + madeOut);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096);
        medium.SimulatedBadLbas.Add(0);
        medium.SimulatedBadLbas.Add(1000);
        medium.SimulatedReadDifferenceLbas.Add(10);
        medium.SimulatedReadDifferenceLbas.Add(200);
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync(
            "disc-art", "burn", "--native", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-dvd-json"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("writesAccepted").GetBoolean());
        Assert.True(root.GetProperty("sessionClosed").GetBoolean());
        Assert.True(root.GetProperty("tocChecked").GetBoolean());
        Assert.False(root.GetProperty("filesCompared").GetBoolean());
        Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
        Assert.False(root.GetProperty("verified").GetBoolean());
        Assert.Contains("picture area was not compared", root.GetProperty("verificationNote").GetString(), StringComparison.OrdinalIgnoreCase);

        using var mediumText = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096);
        mediumText.SimulatedBadLbas.Add(5);
        _recorder.InsertMedia(mediumText);
        (int textCode, string textOut, string textErr) = await RunAsync(
            "disc-art", "burn", "--native", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-dvd-text"));
        Assert.True(textCode == 0, textErr + textOut);
        Assert.Contains("picture area was not compared", textOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PictureOnlyCdCue_WhenPictureSectorsReadWrongOrFail_SucceedsAndReportsNotCompared()
    {
        string wav = P("pic-only-cd.wav");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", wav, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-cd");
        Assert.True(made == 0, madeErr + madeOut);

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.SimulatedBadLbas.Add(0);
        medium.SimulatedBadLbas.Add(500);
        medium.SimulatedReadDifferenceLbas.Add(15);
        medium.SimulatedReadDifferenceLbas.Add(1200);
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync(
            "disc-art", "burn", "--drive", "VDRV-01", "--image", P("pic-only-cd.cue"), "--log-dir", P("logs-cd-json"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("writesAccepted").GetBoolean());
        Assert.True(root.GetProperty("sessionClosed").GetBoolean());
        Assert.True(root.GetProperty("tocChecked").GetBoolean());
        Assert.False(root.GetProperty("audioCompared").GetBoolean());
        Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
        Assert.False(root.GetProperty("verified").GetBoolean());
        Assert.Contains("picture area was not compared", root.GetProperty("verifyMessage").GetString(), StringComparison.OrdinalIgnoreCase);

        using var mediumText = VirtualDiscMedium.CreateBlankCdR700();
        mediumText.SimulatedReadDifferenceLbas.Add(42);
        _recorder.InsertMedia(mediumText);
        (int textCode, string textOut, string textErr) = await RunAsync(
            "disc-art", "burn", "--drive", "VDRV-01", "--image", P("pic-only-cd.cue"), "--log-dir", P("logs-cd-text"));
        Assert.True(textCode == 0, textErr + textOut);
        Assert.Contains("picture area was not compared", textOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HybridInnerFilesDvd_IgnoresWrongPictureSectors_StillFailsOnWrongFileSector()
    {
        string iso = P("hybrid-inner.iso");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd",
            "--files", WriteFiles(), "--json");
        Assert.True(made == 0, madeErr + madeOut);

        int picStart;
        using (JsonDocument gen = JsonDocument.Parse(madeOut))
        {
            picStart = gen.RootElement.GetProperty("plan").GetProperty("pictureStartLba").GetInt32();
        }
        int fileLba;
        using (var isoStream = File.OpenRead(iso))
        using (var reader = IsoImageReader.FromStream(isoStream))
        {
            fileLba = reader.FindFile("/DATA.BIN")!.StartLba;
        }

        // Wrong or unreadable sectors inside the picture zone must still pass.
        using (var mediumPicFault = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096))
        {
            mediumPicFault.SimulatedBadLbas.Add(picStart + 5);
            mediumPicFault.SimulatedReadDifferenceLbas.Add(picStart + 20);
            _recorder.InsertMedia(mediumPicFault);

            (int code, string output, string error) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-inner-ok"), "--json");
            Assert.True(code == 0, error + output);
            using JsonDocument burn = JsonDocument.Parse(output);
            JsonElement root = burn.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("filesCompared").GetBoolean());
            Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
            Assert.True(root.GetProperty("verified").GetBoolean());
            Assert.Contains("Picture area was not compared", root.GetProperty("verificationNote").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        // A wrong sector inside a file must still fail.
        using (var mediumFileFault = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 4096))
        {
            mediumFileFault.SimulatedReadDifferenceLbas.Add(fileLba);
            _recorder.InsertMedia(mediumFileFault);

            (int failCode, string failOut, _) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-inner-fail"), "--json");
            Assert.Equal(1, failCode);
            using JsonDocument failDoc = JsonDocument.Parse(failOut);
            Assert.False(failDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task HybridOuterFilesTwoSessionDvd_IgnoresWrongPictureSectors_StillFailsOnWrongFileSector()
    {
        string iso = P("hybrid-outer.iso");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "small-dvd",
            "--files", WriteFiles(), "--files-outer", "--label", "OUTER_TEST", "--json");
        Assert.True(made == 0, madeErr + madeOut);

        int picEnd;
        using (JsonDocument gen = JsonDocument.Parse(madeOut))
        {
            picEnd = gen.RootElement.GetProperty("plan").GetProperty("pictureEndLba").GetInt32();
        }

        // Session 1 picture defects must still succeed.
        using (var mediumPicFault = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 32_768))
        {
            mediumPicFault.SimulatedBadLbas.Add(5);
            mediumPicFault.SimulatedReadDifferenceLbas.Add(25);
            _recorder.InsertMedia(mediumPicFault);

            (int code, string output, string error) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-outer-ok"), "--json");
            Assert.True(code == 0, error + output);
            using JsonDocument burn = JsonDocument.Parse(output);
            JsonElement root = burn.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("filesCompared").GetBoolean());
            Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
        }

        // Session 2 file-session defect (on DVD+R session 2 starts at picEnd + 2048; PVD is at +16).
        using (var mediumFileFault = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 32_768))
        {
            mediumFileFault.SimulatedReadDifferenceLbas.Add(picEnd + 2048 + 16);
            _recorder.InsertMedia(mediumFileFault);

            (int failCode, string failOut, _) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", iso, "--log-dir", P("logs-outer-fail"), "--json");
            Assert.Equal(1, failCode);
            using JsonDocument failDoc = JsonDocument.Parse(failOut);
            Assert.False(failDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task AudioAndPictureTrackCd_IgnoresPictureTrack_StillFailsOnWrongMusicSample()
    {
        string musicWav = P("music.wav");
        WriteCdWav(musicWav, MakeNonZeroCdPcm(300));

        string picWav = P("pic-track.wav");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", picWav, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-cd");
        Assert.True(made == 0, madeErr + madeOut);

        string cue = P("mixed-music-pic.cue");
        File.WriteAllLines(cue, new[]
        {
            "TITLE \"Mixed Music and Picture\"",
            "PERFORMER \"Tester\"",
            $"FILE \"{Path.GetFileName(musicWav)}\" WAVE",
            "  TRACK 01 AUDIO",
            "    TITLE \"Music\"",
            "    INDEX 01 00:00:00",
            $"FILE \"{Path.GetFileName(picWav)}\" WAVE",
            "  TRACK 02 AUDIO",
            "    TITLE \"Picture\"",
            "    INDEX 01 00:00:00"
        });

        // Picture track defect at LBA >= 450 (track 1 is 300 sectors + 150 pregap = ends at 450).
        using (var mediumPicFault = VirtualDiscMedium.CreateBlankCdR700())
        {
            mediumPicFault.SimulatedBadLbas.Add(500);
            mediumPicFault.SimulatedReadDifferenceLbas.Add(600);
            _recorder.InsertMedia(mediumPicFault);

            (int code, string output, string error) = await RunAsync(
                "burn-audio", "--native", "--dao", "--drive", "VDRV-01", "--tracks", cue, "--log-dir", P("logs-audpic-ok"), "--json");
            Assert.True(code == 0, error + output);
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("audioCompared").GetBoolean());
            Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
            Assert.True(root.GetProperty("verified").GetBoolean());
            Assert.Contains("Picture area was not compared", root.GetProperty("verifyMessage").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        // One wrong music sample in track 1 (LBA 50) must still fail.
        using (var mediumMusicFault = VirtualDiscMedium.CreateBlankCdR700())
        {
            mediumMusicFault.SimulatedReadDifferenceLbas.Add(50);
            _recorder.InsertMedia(mediumMusicFault);

            (int failCode, string failOut, _) = await RunAsync(
                "burn-audio", "--native", "--dao", "--drive", "VDRV-01", "--tracks", cue, "--log-dir", P("logs-audpic-fail"), "--json");
            Assert.Equal(1, failCode);
            using JsonDocument failDoc = JsonDocument.Parse(failOut);
            Assert.False(failDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task PreGapPicturePlusMusicCd_IgnoresPreGapPicture_StillFailsOnWrongMusicSample()
    {
        string albumWav = P("album.wav");
        WriteCdWav(albumWav, MakeNonZeroCdPcm(300));
        string baseCue = P("album.cue");
        File.WriteAllLines(baseCue, new[]
        {
            "TITLE \"PreGap Test\"",
            "PERFORMER \"Artist\"",
            "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO",
            "    TITLE \"Song\"",
            "    INDEX 01 00:00:00"
        });

        string pregapWav = P("art\\pregap.wav");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--disc-preset", "cd-80", "--disc-presets-file", WritePresets(),
            "--picture", WritePicture(), "--pregap-art", baseCue, "--pregap-seconds", "60",
            "--tone", "binary", "--output", pregapWav, "--json");
        Assert.True(made == 0, madeErr + madeOut);

        string generatedCue;
        int track1Start;
        using (JsonDocument gen = JsonDocument.Parse(madeOut))
        {
            generatedCue = gen.RootElement.GetProperty("cueSheetPath").GetString()!;
            track1Start = gen.RootElement.GetProperty("plan").GetProperty("preGap").GetProperty("track1Index01Lba").GetInt32();
        }

        // Defect in pre-gap picture area (LBA 10, well before track1Start = 300) must still pass.
        using (var mediumPicFault = VirtualDiscMedium.CreateBlankCdR700())
        {
            mediumPicFault.SimulatedBadLbas.Add(10);
            mediumPicFault.SimulatedReadDifferenceLbas.Add(50);
            _recorder.InsertMedia(mediumPicFault);

            (int code, string output, string error) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", generatedCue, "--log-dir", P("logs-pg-ok"), "--json");
            Assert.True(code == 0, error + output);
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("audioCompared").GetBoolean());
            Assert.False(root.GetProperty("pictureAreaCompared").GetBoolean());
            Assert.True(root.GetProperty("verified").GetBoolean());
        }

        // Defect inside track 1 music after the pre-gap must still fail.
        using (var mediumMusicFault = VirtualDiscMedium.CreateBlankCdR700())
        {
            mediumMusicFault.SimulatedReadDifferenceLbas.Add(track1Start + 30);
            _recorder.InsertMedia(mediumMusicFault);

            (int failCode, string failOut, _) = await RunAsync(
                "disc-art", "burn", "--drive", "VDRV-01", "--image", generatedCue, "--log-dir", P("logs-pg-fail"), "--json");
            Assert.Equal(1, failCode);
            using JsonDocument failDoc = JsonDocument.Parse(failOut);
            Assert.False(failDoc.RootElement.GetProperty("success").GetBoolean());
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
