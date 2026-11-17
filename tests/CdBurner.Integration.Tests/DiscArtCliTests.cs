using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// <c>disc-art generate | preview | burn</c> through the command-line runner, on tiny discs (a few thousand sectors) and
/// the simulated drive. No physical drive is touched.
/// </summary>
public sealed class DiscArtCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-cli-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _devices;

    public DiscArtCliTests()
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

    /// <summary>Presets file with one tiny disc of each type (sector counts chosen so every encoder accepts them).</summary>
    private string WritePresets()
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
              "bluRayPresets": [
                { "id": "tiny-bd", "displayName": "Tiny BD", "description": "test", "totalSectors": 4096,
                  "innerRadiusMm": 24, "outerRadiusMm": 58 }
              ]
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
        File.WriteAllText(Path.Combine(folder, "README.TXT"), "picture disc with files");
        byte[] data = new byte[60_000];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(Path.Combine(folder, "DATA.BIN"), data);
        return folder;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Generate_placement_options_default_to_the_unplaced_image_and_otherwise_change_it()
    {
        string presets = WritePresets();
        string[] common = ["disc-art", "generate", "--media", "dvd", "--picture", WritePicture(),
            "--disc-presets-file", presets, "--disc-preset", "tiny-dvd"];

        (int c1, string o1, string e1) = await RunAsync([.. common, "--output", P("a.iso")]);
        (int c2, string o2, string e2) = await RunAsync([.. common, "--output", P("b.iso"),
            "--scale", "1", "--offset-x-mm", "0", "--offset-y-mm", "0", "--rotate", "0", "--fit", "disc"]);
        (int c3, string o3, string e3) = await RunAsync([.. common, "--output", P("c.iso"), "--rotate", "90", "--fit", "ring"]);

        Assert.True(c1 == 0 && c2 == 0 && c3 == 0, e1 + o1 + e2 + o2 + e3 + o3);
        Assert.Equal(Sha(P("a.iso")), Sha(P("b.iso")));
        Assert.NotEqual(Sha(P("a.iso")), Sha(P("c.iso")));
        Assert.Contains("\"rotationDegrees\": 90", File.ReadAllText(P("c.iso.disc-art.json")));
    }

    [Theory]
    [InlineData("--fit", "square")]
    [InlineData("--scale", "abc")]
    [InlineData("--scale", "0")]
    [InlineData("--offset-x-mm", "9999")]
    [InlineData("--rotate", "x")]
    public async Task Generate_bad_placement_values_are_refused(string option, string value)
    {
        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", P("bad.iso"), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-dvd", "--plan-only", option, value);

        Assert.NotEqual(0, code);
        Assert.False(File.Exists(P("bad.iso")));
    }

    // 11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Generate_PlanOnly_WritesNothing_AndPrintsPlan()
    {
        string iso = P("plan.iso");

        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd",
            "--files", WriteFiles(), "--plan-only", "--json");

        Assert.True(code == 0, error + output);
        Assert.False(File.Exists(iso));
        Assert.False(File.Exists(iso + ".disc-art.json"));
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("planOnly").GetBoolean());
        Assert.Equal("DVD", root.GetProperty("media").GetString());
        JsonElement plan = root.GetProperty("plan");
        Assert.True(plan.GetProperty("pictureStartLba").GetInt64() > 1024);
        Assert.Equal(0, plan.GetProperty("pictureStartLba").GetInt64() % 16);
        Assert.Equal(4096, plan.GetProperty("pictureEndLba").GetInt64());
        Assert.Equal("inner", plan.GetProperty("files").GetProperty("placement").GetString());
        Assert.True(plan.GetProperty("files").GetProperty("volumeEndLba").GetInt64() > 0);

        // The text form says the same in words.
        (int textCode, string text, _) = await RunAsync("disc-art", "generate", "--media", "dvd", "--picture", WritePicture(),
            "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd", "--plan-only");
        Assert.Equal(0, textCode);
        Assert.Contains("nothing was written", text);
        Assert.Contains("Picture", text);
        Assert.False(File.Exists(iso));
    }

    // 12 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Generate_Dvd_WithFiles_ThenPreview()
    {
        string iso = P("art.iso");
        string presets = WritePresets();

        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", presets, "--disc-preset", "tiny-dvd",
            "--files", WriteFiles(), "--label", "ART_DISC", "--json");

        Assert.True(code == 0, error + output);
        using (JsonDocument doc = JsonDocument.Parse(output))
        {
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(iso, root.GetProperty("outputPath").GetString());
            Assert.Equal(iso + ".disc-art.json", root.GetProperty("sidecarPath").GetString());
            Assert.Equal(new FileInfo(iso).Length, root.GetProperty("bytesWritten").GetInt64());
            Assert.Equal(iso, root.GetProperty("burnImage").GetString());
            Assert.Equal("inner", root.GetProperty("plan").GetProperty("files").GetProperty("placement").GetString());
        }

        // Without --overwrite a second run is refused and the image is untouched.
        long length = new FileInfo(iso).Length;
        (int again, _, string againError) = await RunAsync("disc-art", "generate", "--media", "dvd", "--picture", WritePicture(),
            "--output", iso, "--disc-presets-file", presets, "--disc-preset", "tiny-dvd");
        Assert.Equal(1, again);
        Assert.Contains("already exists", againError);
        Assert.Equal(length, new FileInfo(iso).Length);

        string png = P("art.png");
        (int previewCode, string previewOut, string previewErr) = await RunAsync("disc-art", "preview",
            "--image", iso, "--output", png, "--size", "128", "--json");

        Assert.True(previewCode == 0, previewErr + previewOut);
        using (JsonDocument doc = JsonDocument.Parse(previewOut))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(128, doc.RootElement.GetProperty("size").GetInt32());
        }

        BgraRaster raster = PngRaster.Load(png);
        Assert.Equal(128, raster.Width);
        Assert.Equal(128, raster.Height);

        // The text form prints the burn command for the person to copy.
        string second = P("art2.iso");
        (int textCode, string text, string textErr) = await RunAsync("disc-art", "generate", "--media", "dvd", "--picture", WritePicture(),
            "--output", second, "--disc-presets-file", presets, "--disc-preset", "tiny-dvd", "--files", WriteFiles(), "--files-outer");
        Assert.True(textCode == 0, textErr + text);
        Assert.Contains($"Burn with: ignisdisc-cli disc-art burn --drive <id> --image \"{second}\"", text);
        Assert.Contains("(outer session)", text);
        Assert.Contains("two-session burn", text);

        // Burned in one pass the files would sit in no session a computer reads, so the burn is refused before any
        // drive is touched.
        using var medium = VirtualDiscMedium.CreateBlankDvdRw47();
        _recorder.InsertMedia(medium);
        (int burnCode, string burnOut, string burnErr) = await RunAsync("disc-art", "burn", "--drive", "VDRV-01", "--image", second);
        Assert.True(burnCode == 1, burnErr + burnOut);
        Assert.Contains("two sessions", burnErr);
    }

    [Fact]
    public async Task Preview_of_a_bd_image_says_it_is_not_available_yet_and_bad_arguments_are_refused()
    {
        string iso = P("bd.iso");
        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "bd", "--picture", WritePicture(),
            "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-bd");
        Assert.True(code == 0, error + output);
        Assert.Equal(4096L * 2048, new FileInfo(iso).Length);

        (int previewCode, _, string previewError) = await RunAsync("disc-art", "preview", "--image", iso, "--output", P("bd.png"));
        Assert.Equal(1, previewCode);
        Assert.Contains("not available for BD", previewError);
        Assert.False(File.Exists(P("bd.png")));

        foreach (string[] args in new[]
        {
            new[] { "disc-art", "generate", "--picture", WritePicture(), "--output", P("x.iso") },
            new[] { "disc-art", "generate", "--media", "dvd", "--output", P("x.iso") },
            new[] { "disc-art", "generate", "--media", "dvd", "--picture", WritePicture() },
            new[] { "disc-art", "generate", "--media", "dvd", "--picture", WritePicture(), "--output", P("x.iso"), "--files-outer" },
            new[] { "disc-art", "generate", "--media", "dvd", "--picture", WritePicture(), "--output", P("x.iso"), "--tone", "sepia" },
            new[] { "disc-art", "generate", "--media", "cd", "--picture", WritePicture(), "--output", P("x.iso") },
            new[] { "disc-art", "preview", "--image", P("missing.iso"), "--output", P("x.png") },
            new[] { "disc-art", "preview", "--image", iso, "--output", P("x.png"), "--size", "10" },
            new[] { "disc-art", "burn", "--drive", "VDRV-01", "--image", P("x.wav") },
            new[] { "disc-art", "burn", "--drive", "VDRV-01", "--image", P("x.bin") },
        })
        {
            (int refused, _, string refusedError) = await RunAsync(args);
            Assert.True(refused == 1, string.Join(' ', args));
            Assert.StartsWith("Error:", refusedError);
        }

        Assert.False(File.Exists(P("x.iso")));
    }

    // 13 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Burn_Dvd_OnSimulatedDrive_Verifies()
    {
        string iso = P("burn.iso");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd",
            "--files", WriteFiles());
        Assert.True(made == 0, madeErr + madeOut);
        using var medium = VirtualDiscMedium.CreateBlankDvdRw47();
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync("disc-art", "burn", "--drive", "VDRV-01", "--image", iso,
            "--log-dir", P("logs"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("sectorsWritten").GetInt64() >= 1500);
    }

    [Fact]
    public async Task Burn_CdCue_OnSimulatedDrive_Verifies()
    {
        string wav = P("cd.wav");
        (int made, string madeOut, string madeErr) = await RunAsync("disc-art", "generate", "--media", "cd",
            "--picture", WritePicture(), "--output", wav, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-cd");
        Assert.True(made == 0, madeErr + madeOut);
        Assert.Contains($"--image \"{P("cd.cue")}\"", madeOut);
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync("disc-art", "burn", "--drive", "VDRV-01", "--image", P("cd.cue"),
            "--log-dir", P("logs"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("native").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("trackCount").GetInt32());
        VirtualTrack track = Assert.Single(Assert.Single(medium.Sessions).Tracks);
        Assert.True(track.IsAudio);
        Assert.True(track.SectorCount >= 6000);
    }

    // 14 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Help_ListsGenerateePreviewBurn()
    {
        (int code, string output, _) = await RunAsync("disc-art", "--help");

        Assert.Equal(0, code);
        Assert.Contains("disc-art generate", output);
        Assert.Contains("disc-art preview", output);
        Assert.Contains("disc-art burn", output);
        Assert.Contains("disc-art calibrate target", output);

        (int general, string all, _) = await RunAsync("--help");
        Assert.Equal(0, general);
        Assert.Contains("disc-art generate", all);
        Assert.Contains("disc-art burn", all);
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
