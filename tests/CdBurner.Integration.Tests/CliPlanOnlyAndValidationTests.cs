using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Fast, drive-free checks of the command-line handlers for <c>rosetta</c>, <c>rosetta build/burn</c>,
/// <c>disc-art calibrate</c>, <c>burn-audio --fill-picture</c> and the subtitle commands: what each prints for
/// <c>--plan-only</c>, and which wrong or missing options it refuses (always with a non-zero exit and a message that
/// names the option). Every run uses a temporary folder and the simulated drive; nothing touches a real drive and no
/// download is started.
/// </summary>
public sealed class CliPlanOnlyAndValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-cli-plan-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _engine;
    private readonly SingleRecorderService _devices;

    public CliPlanOnlyAndValidationTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new VirtualBurnEngine(_recorder);
        _devices = new SingleRecorderService(_recorder);
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
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _engine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Asserts a refusal: non-zero exit and the given text somewhere in what the user sees.</summary>
    private async Task Refused(string expectedText, params string[] args)
    {
        (int code, string output, string error) = await RunAsync(args);
        Assert.True(code != 0, $"expected a refusal for: {string.Join(' ', args)}\n{output}{error}");
        Assert.True((output + error).Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            $"expected '{expectedText}' in:\n{output}{error}");
    }

    private string TextFolder()
    {
        string folder = P("text");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "chapter1.txt"), "In the beginning the archive was small.");
        File.WriteAllText(Path.Combine(folder, "chapter2.txt"), "Later it grew.");
        return folder;
    }

    private string Presets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 4,
              "cdPresets": [
                { "id": "tiny-cd", "displayName": "Tiny CD", "description": "test", "sectors": 6000,
                  "innerRadiusMm": 24.5, "outerRadiusMm": 56.8, "linearVelocityMmPerSecond": 1200 }
              ],
              "dvdPresets": [
                { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": []
            }
            """);
        return path;
    }

    // ---------- rosetta (ladder) ----------

    [Fact]
    public async Task Rosetta_WithoutAVerb_ShowsUsage()
    {
        await Refused("Usage", "rosetta");
        await Refused("Usage", "rosetta", "frobnicate");
    }

    [Fact]
    public async Task RosettaLadder_Dvd_PlanOnly_WritesOnlyTheManifest()
    {
        string iso = P("ladder.iso");
        (int code, string output, string error) = await RunAsync("rosetta", "ladder", "--media", "dvd", "--output", iso,
            "--disc-presets-file", Presets(), "--disc-preset", "tiny-dvd", "--plan-only", "--json");

        Assert.True(code == 0, output + error);
        Assert.False(File.Exists(iso));
        Assert.True(File.Exists(iso + ".json"));
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("planOnly").GetBoolean());
        Assert.Equal("tiny-dvd", root.GetProperty("discPresetId").GetString());
        Assert.True(root.GetProperty("totalSectors").GetInt64() > 0);
        Assert.True(root.GetProperty("zoneCount").GetInt32() > 0);
        Assert.True(root.GetProperty("rungCount").GetInt32() > 0);
    }

    [Fact]
    public async Task RosettaLadder_Cd_PlanOnly_PrintsThePlanInWords()
    {
        string wav = P("ladder.wav");
        (int code, string output, string error) = await RunAsync("rosetta-ladder", "--media", "cd", "--output", wav,
            "--disc-presets-file", Presets(), "--disc-preset", "tiny-cd", "--start-angle", "45.5", "--plan-only");

        Assert.True(code == 0, output + error);
        Assert.False(File.Exists(wav));
        Assert.Contains("Plan only", output);
        Assert.Contains("Zones / Rungs", output);
        Assert.Contains("tiny-cd", output);
        Assert.True(File.Exists(wav + ".json"));
    }

    [Fact]
    public async Task RosettaLadder_RefusesBadOptions()
    {
        string presets = Presets();
        await Refused("Unsupported --media", "rosetta", "ladder", "--media", "bd", "--output", P("a.iso"));
        await Refused("--no-interleave", "rosetta", "ladder", "--media", "dvd", "--no-interleave", "--output", P("a.iso"));
        await Refused("--output", "rosetta", "ladder", "--media", "dvd");
        await Refused("--output", "rosetta", "ladder", "--media", "cd");
        await Refused(".wav", "rosetta", "ladder", "--media", "cd", "--output", P("a.iso"));
        await Refused("--start-angle", "rosetta", "ladder", "--media", "dvd", "--output", P("a.iso"), "--start-angle", "north");
        await Refused("Unknown DVD disc preset", "rosetta", "ladder", "--media", "dvd", "--output", P("a.iso"),
            "--disc-presets-file", presets, "--disc-preset", "nope");
        await Refused("Unknown CD disc preset", "rosetta", "ladder", "--media", "cd", "--output", P("a.wav"),
            "--disc-presets-file", presets, "--disc-preset", "nope");
        await Refused("not found", "rosetta", "ladder", "--media", "dvd", "--output", P("a.iso"),
            "--disc-presets-file", P("missing.json"));
    }

    // ---------- rosetta build / burn ----------

    [Fact]
    public async Task RosettaBuild_Dvd_PlanOnly_DescribesTheDiscAndWritesNothing()
    {
        string iso = P("vault.iso");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--text", TextFolder(), "--output", iso,
            "--title", "Test Archive", "--date", "2026-01-31", "--redundancy", "0", "--plan-only", "--json");

        Assert.True(code == 0, output + error);
        Assert.False(File.Exists(iso));
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("planOnly").GetBoolean());
        Assert.Equal("DVD-R", doc.RootElement.GetProperty("media").GetString());
        Assert.True(doc.RootElement.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task RosettaBuild_Cd_PlanOnly_TextFormMentionsNothingWasWritten()
    {
        string cue = P("vault.cue");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--media", "cd", "--text", TextFolder(),
            "--output", cue, "--redundancy", "0", "--plan-only", "--no-interleave");

        Assert.True(code == 0, output + error);
        Assert.Contains("hybrid Rosetta CD-R", output);
        Assert.Contains("nothing was written", output);
        Assert.False(File.Exists(cue));
    }

    [Fact]
    public async Task RosettaBuild_RefusesBadOptions()
    {
        string text = TextFolder();
        await Refused("Unsupported --media", "rosetta", "build", "--media", "bd", "--text", text, "--output", P("v.iso"));
        await Refused("--no-interleave", "rosetta", "build", "--text", text, "--output", P("v.iso"), "--no-interleave");
        await Refused("Usage", "rosetta", "build", "--output", P("v.iso"));
        await Refused("--output", "rosetta", "build", "--text", text);
        await Refused("--output", "rosetta", "build", "--media", "cd", "--text", text);
        await Refused("Invalid --date", "rosetta", "build", "--text", text, "--output", P("v.iso"), "--date", "31/01/2026");
        await Refused("Unknown --cells", "rosetta", "build", "--text", text, "--output", P("v.iso"), "--cells", "huge");
        await Refused("Invalid --redundancy", "rosetta", "build", "--text", text, "--output", P("v.iso"), "--redundancy", "101");
        await Refused("Invalid --redundancy", "rosetta", "build", "--text", text, "--output", P("v.iso"), "--redundancy", "-5");
    }

    [Fact]
    public async Task RosettaBuild_MissingTextFolder_IsRefusedBeforeWriting()
    {
        string iso = P("vault.iso");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--text", P("no-such-folder"),
            "--output", iso, "--redundancy", "0", "--plan-only");

        Assert.NotEqual(0, code);
        Assert.False(File.Exists(iso), output + error);
    }

    [Fact]
    public async Task RosettaBurn_RefusesImagesThatBuildDidNotMake()
    {
        await Refused("--image", "rosetta", "burn", "--drive", "VDRV-01");
        await Refused("--drive", "rosetta", "burn", "--image", P("x.iso"));
        await Refused("CUE sheet", "rosetta", "burn", "--drive", "VDRV-01", "--image", P("x.bin"));
        await Refused("Cannot burn", "rosetta", "burn", "--drive", "VDRV-01", "--image", P("x.img"));

        string plain = P("plain.iso");
        File.WriteAllBytes(plain, new byte[2048]);
        await Refused("rosetta build", "rosetta", "burn", "--drive", "VDRV-01", "--image", plain);
    }

    // ---------- disc-art verbs and calibrate ----------

    [Fact]
    public async Task DiscArt_UnknownVerb_ShowsUsage()
    {
        await Refused("disc-art generate", "disc-art", "frobnicate");
        await Refused("disc-art generate", "disc-art");
    }

    [Fact]
    public async Task DiscArtCalibrate_NeedsAKnownSubcommand()
    {
        await Refused("calibrate", "disc-art", "calibrate");
        await Refused("calibrate", "disc-art", "calibrate", "frobnicate");
    }

    [Fact]
    public async Task DiscArtCalibrateTarget_RefusesBadOptions()
    {
        await Refused("--media", "disc-art", "calibrate", "target", "--stage", "coarse", "--output", P("t.iso"));
        await Refused("--stage", "disc-art", "calibrate", "target", "--media", "dvd", "--output", P("t.iso"));
        await Refused("Unknown --media", "disc-art", "calibrate", "target", "--media", "floppy", "--stage", "coarse", "--output", P("t.iso"));
        await Refused("stage", "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "ultra", "--output", P("t.iso"));
        await Refused("--output", "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "coarse");
        await Refused("--output", "disc-art", "calibrate", "target", "--media", "cd", "--stage", "coarse");
        Assert.False(File.Exists(P("t.iso")));
    }

    [Fact]
    public async Task DiscArtCalibrateFit_RefusesBadOptions()
    {
        await Refused("--points", "disc-art", "calibrate", "fit");
        await Refused("not found", "disc-art", "calibrate", "fit", "--points", P("missing.calibration.json"));

        string record = P("garbage.calibration.json");
        File.WriteAllText(record, "this is not a calibration record");
        await Refused("garbage.calibration.json", "disc-art", "calibrate", "fit", "--points", record);
        await Refused("--drive-model", "disc-art", "calibrate", "fit", "--points", record, "--drive-model", "Some Drive");
    }

    [Fact]
    public async Task DiscArtCalibrateList_OnAnEmptyStore_SaysSo()
    {
        (int code, string output, string error) = await RunAsync("disc-art", "calibrate", "list", "--store", P("empty-store.json"));

        Assert.True(code == 0, output + error);
        Assert.False(string.IsNullOrWhiteSpace(output + error));
    }

    // ---------- burn-audio --fill-picture ----------

    private string OneTrackWav()
    {
        string path = P("track.wav");
        // Five seconds of silence: 44,100 Hz x 2 channels x 2 bytes x 5 s.
        WavFileCodec.WriteWavFile(path, new byte[44_100 * 4 * 5]);
        return path;
    }

    [Fact]
    public async Task BurnAudio_FillPicture_RefusesWithoutNativeWriter()
    {
        await Refused("--native", "burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao",
            "--fill-picture", P("p.png"));
    }

    [Fact]
    public async Task BurnAudio_FillPicture_RefusesCombinationWithDataFolder()
    {
        await Refused("--fill-picture", "burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao", "--native",
            "--fill-picture", P("p.png"), "--data-folder", _dir);
    }

    [Fact]
    public async Task BurnAudio_FillPicture_NeedsAPictureOrAuto()
    {
        await Refused("--fill-picture needs", "burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao", "--native",
            "--fill-picture", "");
    }

    [Fact]
    public async Task BurnAudio_FillPicture_DriveModelAndMediaIdComeTogether()
    {
        string picture = P("p.png");
        File.WriteAllBytes(picture, new byte[16]);
        await Refused("both --drive-model", "burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao", "--native",
            "--fill-picture", picture, "--drive-model", "Some Drive");
        await Refused("both --drive-model", "burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao", "--native",
            "--fill-picture", picture, "--media-id", "RITEK-R04");
    }

    [Fact]
    public async Task BurnAudio_FillPicture_MissingOrUnreadablePicture_WritesNothing()
    {
        (int missing, string o1, string e1) = await RunAsync("burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao",
            "--native", "--fill-picture", P("not-there.png"));
        Assert.NotEqual(0, missing);
        Assert.Contains("Nothing was written", o1 + e1);

        string notPicture = P("notes.png");
        File.WriteAllText(notPicture, "this is text, not a picture");
        (int bad, string o2, string e2) = await RunAsync("burn-audio", "--drive", "VDRV-01", "--tracks", OneTrackWav(), "--dao",
            "--native", "--fill-picture", notPicture);
        Assert.NotEqual(0, bad);
        Assert.Contains("Nothing was written", o2 + e2);
        Assert.True(_recorder.QueryMediaStatus().IsBlank, "the disc must still be blank after a refused picture");
    }

    // ---------- subtitles ----------

    [Fact]
    public async Task InstallSubtitleTool_PlanOnly_ReportsTheDownloadWithoutDownloading()
    {
        (int code, string output, string error) = await RunAsync("install-subtitle-tool", "--plan-only", "--json");

        Assert.True(code == 0, output + error);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.StartsWith("https://", root.GetProperty("program").GetString());
        Assert.StartsWith("https://", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("downloadBytes").GetInt64() > 1_000_000);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task InstallSubtitleTool_PlanOnly_TextForm()
    {
        (int code, string output, string error) = await RunAsync("install-subtitle-tool", "--plan-only");

        Assert.True(code == 0, output + error);
        Assert.Contains("Subtitle tool for this PC", output);
        Assert.Contains("Program:", output);
        Assert.Contains("Model:", output);
        Assert.Contains("Installed now:", output);
    }

    [Fact]
    public async Task GenerateSubtitles_RefusesBadInput()
    {
        string movie = P("movie.mp4");
        File.WriteAllBytes(movie, new byte[64]);

        await Refused("--input", "generate-subtitles");
        await Refused("--language must be", "generate-subtitles", "--input", movie, "--language", "english");
        await Refused("--language must be", "generate-subtitles", "--input", movie, "--language", "e1");
        await Refused("Movie file not found", "generate-subtitles", "--input", P("absent.mp4"));
    }

    [Fact]
    public async Task BurnDvd_GenerateSubtitles_ChecksTheLanguageBeforeAnythingIsAuthored()
    {
        string source = P("dvd-source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "movie.mp4"), new byte[64]);

        await Refused("--subtitle-language must be", "burn-dvd", "--drive", "VDRV-01", "--video-ts", source,
            "--generate-subtitles", "--subtitle-language", "klingon");
    }

    private sealed class SingleRecorderService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
