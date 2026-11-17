using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Experimental <c>disc-art generate --pregap-art</c>: an audio CD picture hidden in track 1's pre-gap (INDEX 00 from
/// LBA 0 to track 1's INDEX 01), burned Session-at-Once on the simulated drive, checked with <c>verify --cue</c> and
/// read back into the picture with the whole-disc geometry.
/// </summary>
public sealed class DiscArtPreGapPictureTests : IDisposable
{
    private const int Size = 64;
    private const int PreGapSeconds = 60;
    private const int PreGapSectors = PreGapSeconds * 75; // CD: 75 sectors per second (ECMA-130)

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CdBurner_pregapart_" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _deviceService;

    public DiscArtPreGapPictureTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "album"));
        _burnEngine = new VirtualBurnEngine(_recorder);
        _deviceService = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Left half black, right half white.</summary>
    private static int Level(int x) => x < Size / 2 ? 0 : 255;

    private string WritePicturePng()
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int o = (y * Size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)Level(x);
                pixels[o + 3] = 255;
            }
        string path = Path.Combine(_dir, "picture.png");
        PngRaster.SaveBgra32(path, new BgraRaster(Size, Size, pixels));
        return path;
    }

    /// <summary>A single-file album: one noise WAV of 700 sectors, track 2 from 00:05:00 (375 sectors).</summary>
    private string WriteAlbumCue()
    {
        byte[] pcm = new byte[700 * OpticalConstants.RawSectorSize];
        new Random(21).NextBytes(pcm);
        using (var wav = new FileStream(Path.Combine(_dir, "album", "album.wav"), FileMode.Create))
        {
            CdWaveHeader(wav, pcm.Length);
            wav.Write(pcm);
        }
        string cue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(cue, new[]
        {
            "TITLE \"Album\"", "PERFORMER \"Artist\"", "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO", "    TITLE \"One\"", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    TITLE \"Two\"", "    INDEX 01 00:05:00",
        });
        return cue;
    }

    private static void CdWaveHeader(Stream s, int dataBytes)
    {
        var w = new BinaryWriter(s);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(44100); w.Write(176400); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(dataBytes);
        w.Flush();
    }

    private async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Task<(int Exit, string Out, string Err)> Generate(string baseCue, params string[] extra)
        => Cli(new[]
        {
            "disc-art", "generate", "--media", "cd", "--disc-preset", "cd-80", "--picture", WritePicturePng(),
            "--pregap-art", baseCue, "--tone", "binary", "--output", Path.Combine(_dir, "art", "pregap.wav"), "--json",
        }.Concat(extra).ToArray());

    [Fact]
    public async Task Pre_gap_picture_burns_before_track_one_verifies_against_the_cue_and_reads_back_as_the_picture()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        var generated = await Generate(WriteAlbumCue(), "--pregap-seconds", PreGapSeconds.ToString());
        Assert.True(generated.Exit == 0, generated.Out + generated.Err);
        string cue;
        string wav;
        using (JsonDocument doc = JsonDocument.Parse(generated.Out.Trim()))
        {
            JsonElement plan = doc.RootElement.GetProperty("plan");
            Assert.Equal(0, plan.GetProperty("pictureStartLba").GetInt64());
            Assert.Equal(PreGapSectors, plan.GetProperty("pictureEndLba").GetInt64());
            JsonElement preGap = plan.GetProperty("preGap");
            Assert.True(preGap.GetProperty("experimental").GetBoolean());
            Assert.Equal(PreGapSectors, preGap.GetProperty("track1Index01Lba").GetInt64());
            Assert.Contains("noise", preGap.GetProperty("warning").GetString());
            cue = doc.RootElement.GetProperty("cueSheetPath").GetString()!;
            wav = doc.RootElement.GetProperty("outputPath").GetString()!;
        }
        Assert.EndsWith("album-with-pregap-picture.cue", cue);
        byte[] picturePcm = File.ReadAllBytes(wav)[44..];
        Assert.Equal(PreGapSectors * OpticalConstants.RawSectorSize, picturePcm.Length);
        string logs = Path.Combine(_dir, "logs");

        var burn = await Cli("disc-art", "burn", "--drive", "VDRV-01", "--image", cue, "--log-dir", logs, "--json");

        Assert.True(burn.Exit == 0, burn.Out + burn.Err);
        using (JsonDocument doc = JsonDocument.Parse(burn.Out.Trim()))
        {
            Assert.True(doc.RootElement.GetProperty("native").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
        }

        // Track 1 starts after the picture: LBA -150..-1 silence, the picture from LBA 0, track 1's INDEX 01 after it.
        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { PreGapSectors, PreGapSectors + 375 }, tracks.Select(t => t.StartLba));
        Assert.All(tracks, t => Assert.True(t.IsAudio));

        var verify = await Cli("verify", "--drive", "VDRV-01", "--cue", cue, "--title", "Album", "--artist", "Artist",
            "--log-dir", logs, "--json");
        Assert.True(verify.Exit == 0, verify.Out + verify.Err);
        using (JsonDocument doc = JsonDocument.Parse(verify.Out.Trim()))
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        // The pre-gap read back as a drive returns CD-DA (2,352 bytes, little-endian samples) equals the picture file.
        byte[] disc = new byte[PreGapSectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < PreGapSectors; i++)
            medium.ReadSector(i, disc.AsSpan(i * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
                OpticalConstants.RawSectorSize);
        Assert.Equal(picturePcm, disc);

        // Rendered on the whole-disc spiral (byte 0 = absolute time 00:00:00 = LBA -150, which holds silence), the
        // pre-gap shows the picture at the inner edge, from the radius of LBA 0 to the radius of track 1's start.
        CdDiscParameters wholeDisc = AudioCdFillPicture.DefaultDisc();
        byte[] fromDiscStart = new byte[150 * OpticalConstants.RawSectorSize + disc.Length];
        disc.CopyTo(fromDiscStart, 150 * OpticalConstants.RawSectorSize);
        const int render = 2048;
        byte[] pixels = CdTrackGenerator.RenderTrack(new MemoryStream(fromDiscStart), CdAudioByteOrder.LittleEndian,
            wholeDisc, render, byteStep: 1, deinterleave: true);
        double innerMm = wholeDisc.RadiusFromByte(150L * CdDiscParameters.BytesPerSector);
        double outerMm = wholeDisc.RadiusFromByte((150L + PreGapSectors) * CdDiscParameters.BytesPerSector);
        Assert.InRange(innerMm, 24.8, 24.9);   // the inner edge of the program area
        Assert.InRange(outerMm - innerMm, 0.5, 0.9); // one minute of disc there is about 0.7 mm
        double mmPerPixel = wholeDisc.PreviewImageOuterRadiusMm / (render / 2.0);
        int dark = 0, light = 0;
        for (int y = 0; y < render; y++)
            for (int x = 0; x < render; x++)
            {
                double dx = (x + 0.5 - render / 2.0) * mmPerPixel;
                double dy = (y + 0.5 - render / 2.0) * mmPerPixel;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < innerMm + 3 * mmPerPixel || r > outerMm - 3 * mmPerPixel || Math.Abs(dx) < 1.5) continue;
                byte gray = pixels[(y * render + x) * 4];
                if (dx < 0) { Assert.True(gray < 128, $"({x},{y}) r={r:0.000} should be dark, is {gray}"); dark++; }
                else { Assert.True(gray >= 128, $"({x},{y}) r={r:0.000} should be light, is {gray}"); light++; }
            }
        Assert.True(dark > 2000 && light > 2000, $"only {dark} dark and {light} light pixels checked");
    }

    [Fact]
    public async Task Pre_gap_picture_is_off_by_default_and_marked_experimental_in_help()
    {
        var (code, help, _) = await Cli("disc-art", "--help");
        Assert.Equal(0, code);
        string line = Assert.Single(help.Split('\n'), l => l.Contains("--pregap-art <base.cue>"));
        Assert.Contains("EXPERIMENTAL, off by default", line);
        Assert.Contains("noise", help);
        var (general, all, _) = await Cli("--help");
        Assert.Equal(0, general);
        Assert.Contains("--pregap-art (experimental, off by default)", all);

        Assert.Null(new DiscArtRequest(DiscArtMedium.Cd, "p.png", null!, "o.wav").CdPreGapSeconds);

        // Without --pregap-art a CD picture with a base CUE is still the last track and track 1 keeps no INDEX 00.
        string baseCue = WriteAlbumCue();
        var last = await Cli("disc-art", "generate", "--media", "cd", "--disc-preset", "cd-80", "--picture", WritePicturePng(),
            "--append-to", baseCue, "--output", Path.Combine(_dir, "last", "picture.wav"), "--plan-only", "--json");
        Assert.True(last.Exit == 0, last.Out + last.Err);
        using JsonDocument doc = JsonDocument.Parse(last.Out.Trim());
        Assert.False(doc.RootElement.GetProperty("plan").TryGetProperty("preGap", out JsonElement preGap) && preGap.ValueKind != JsonValueKind.Null);
        Assert.True(doc.RootElement.GetProperty("plan").GetProperty("pictureStartLba").GetInt64() > 0);
    }

    [Fact]
    public async Task Plan_only_prints_the_warning_and_writes_nothing()
    {
        var plan = await Cli("disc-art", "generate", "--media", "cd", "--disc-preset", "cd-80", "--picture", WritePicturePng(),
            "--pregap-art", WriteAlbumCue(), "--output", Path.Combine(_dir, "art", "pregap.wav"), "--plan-only");

        Assert.True(plan.Exit == 0, plan.Out + plan.Err);
        Assert.Contains($"track 1 starts at LBA {DiscArtImageBuilder.CdPreGapDefaultSeconds * 75:N0}", plan.Out);
        Assert.Contains("Experimental", plan.Out);
        Assert.Contains("noise", plan.Out);
        Assert.False(Directory.Exists(Path.Combine(_dir, "art")) && Directory.EnumerateFiles(Path.Combine(_dir, "art")).Any());
    }

    [Theory]
    [InlineData("--pregap-seconds", "59")]
    [InlineData("--pregap-seconds", "1201")]
    [InlineData("--pregap-seconds", "1.5")]
    [InlineData("--append-to", "album")]
    [InlineData("--media", "dvd")]
    public async Task Bad_pre_gap_options_are_refused_and_nothing_is_written(string option, string value)
    {
        string baseCue = WriteAlbumCue();
        if (value == "album") value = baseCue;
        var args = new List<string>
        {
            "disc-art", "generate", "--picture", WritePicturePng(), "--pregap-art", baseCue,
            "--output", Path.Combine(_dir, "art", option == "--media" ? "pregap.iso" : "pregap.wav"),
        };
        if (option != "--media") args.AddRange(new[] { "--media", "cd" });
        args.AddRange(new[] { option, value });

        var result = await Cli(args.ToArray());

        Assert.NotEqual(0, result.Exit);
        Assert.False(Directory.Exists(Path.Combine(_dir, "art")) && Directory.EnumerateFiles(Path.Combine(_dir, "art")).Any());
    }

    [Fact]
    public async Task Pre_gap_seconds_without_pre_gap_art_is_refused()
    {
        var result = await Cli("disc-art", "generate", "--media", "cd", "--picture", WritePicturePng(),
            "--pregap-seconds", "60", "--output", Path.Combine(_dir, "art", "pregap.wav"));
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("--pregap-art", result.Err);
    }

    [Fact]
    public async Task A_base_cue_with_audio_already_before_track_one_is_refused_before_drawing()
    {
        string cue = WriteAlbumCue();
        File.WriteAllLines(cue, new[]
        {
            "FILE \"album.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 00 00:00:00", "    INDEX 01 00:02:00",
            "  TRACK 02 AUDIO", "    INDEX 01 00:05:00",
        });

        var result = await Generate(cue);

        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Track 1 already has a pre-gap", result.Out + result.Err);
        Assert.False(Directory.Exists(Path.Combine(_dir, "art")) && Directory.EnumerateFiles(Path.Combine(_dir, "art")).Any());
    }

    [Fact]
    public async Task A_pre_gap_that_does_not_fit_on_the_disc_is_refused()
    {
        // An 80-minute disc: 78 minutes of music leave no room for a 5-minute pre-gap picture.
        byte[] pcm = new byte[78 * 60 * 75 * OpticalConstants.RawSectorSize];
        using (var wav = new FileStream(Path.Combine(_dir, "album", "long.wav"), FileMode.Create))
        {
            CdWaveHeader(wav, pcm.Length);
            wav.Write(pcm);
        }
        string cue = Path.Combine(_dir, "album", "long.cue");
        File.WriteAllLines(cue, new[] { "FILE \"long.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 01 00:00:00" });

        var result = await Generate(cue);

        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Choose a shorter pre-gap", result.Out + result.Err);
    }

    [Fact]
    public async Task Burning_a_pre_gap_picture_with_the_windows_writer_is_refused_and_the_disc_stays_blank()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        var generated = await Generate(WriteAlbumCue(), "--pregap-seconds", "60");
        Assert.True(generated.Exit == 0, generated.Out + generated.Err);
        string cue;
        using (JsonDocument doc = JsonDocument.Parse(generated.Out.Trim()))
            cue = doc.RootElement.GetProperty("cueSheetPath").GetString()!;

        var burn = await Cli("disc-art", "burn", "--drive", "VDRV-01", "--image", cue, "--windows-writer");

        Assert.NotEqual(0, burn.Exit);
        Assert.Contains("pre-gap", burn.Err);
        Assert.Empty(medium.Sessions);
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
