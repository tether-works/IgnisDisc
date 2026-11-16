using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using CdBurner.Cli;
using CdBurner.Hardware.Simulation;
using CdBurner.Hardware.Devices;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The <c>disc-art calibrate</c> commands end to end: target files, fits of upstream's published records, and the
/// store per drive model and media ID. No drive is used; the commands only read and write files.
/// </summary>
public sealed class DiscArtCalibrateCliTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamCalibration");
    private const string CoarseRecord = "verbatim-azo-20261104--calibration-4f0228e6.calibration.json";
    private const string BoundaryRecord = "verbatim-azo-20261104--calibration-3f2ce8c2.calibration.json";
    private const string FineRecord = "verbatim-azo-20261104--calibration-14580aac.calibration.json";
    private const string EmptyRecord = "dvd-calibration--calibration-f2f851f7.calibration.json";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-calibrate-cli-" + Guid.NewGuid().ToString("N"));

    public DiscArtCalibrateCliTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string StorePath => Path.Combine(_tempDir, "store", "disc-calibrations.json");

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, devices, new VirtualBurnEngine());
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Help_lists_the_three_calibrate_commands()
    {
        (int code, string output, _) = await RunAsync("disc-art", "--help");

        Assert.Equal(0, code);
        Assert.Contains("disc-art calibrate target", output);
        Assert.Contains("disc-art calibrate fit --points", output);
        Assert.Contains("disc-art calibrate list", output);

        (int general, string all, _) = await RunAsync("--help");
        Assert.Equal(0, general);
        Assert.Contains("disc-art calibrate target", all);
    }

    [Fact]
    public async Task Target_command_writes_the_bytes_of_the_upstream_golden_target_and_its_record()
    {
        string iso = Path.Combine(_tempDir, "boundary.iso");

        (int code, string output, string error) = await RunAsync("disc-art", "calibrate", "target", "--media", "dvd",
            "--stage", "boundary", "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--sectors", "1600",
            "--output", iso, "--json");

        Assert.True(code == 0, error + output);
        // dvd-calibration.iso in tests/CdBurner.Lithography.Tests/Fixtures/UpstreamGoldens/upstream-goldens.json:
        // upstream's own output for "calibration-target dvd WavyBoundary 24 58 1600".
        Assert.Equal("81e94f84fe3715d4839400e733adab457d26930f6fa36e86fa76e00e9791fea8",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(iso))).ToLowerInvariant());
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("boundary", root.GetProperty("stage").GetString());
        Assert.Equal("WavyBoundary", root.GetProperty("artwork").GetString());
        Assert.Equal(1600L * 2048, root.GetProperty("bytesWritten").GetInt64());
        string recordPath = root.GetProperty("recordPath").GetString()!;
        Assert.Equal(Path.Combine(_tempDir, "boundary.calibration.json"), recordPath);
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(recordPath));
        Assert.Equal(16, record.RootElement.GetProperty("target").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(root.GetProperty("targetId").GetString(),
            record.RootElement.GetProperty("target").GetProperty("id").GetString());

        // A second run must not replace the target someone may already have burned.
        (int again, _, string againError) = await RunAsync("disc-art", "calibrate", "target", "--media", "dvd",
            "--stage", "boundary", "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--sectors", "1600",
            "--output", iso);
        Assert.Equal(1, again);
        Assert.Contains("already exists", againError);
    }

    [Fact]
    public async Task Target_command_rejects_bad_arguments()
    {
        string iso = Path.Combine(_tempDir, "bad.iso");
        foreach (string[] args in new[]
        {
            new[] { "disc-art" },
            new[] { "disc-art", "calibrate", "draw" },
            new[] { "disc-art", "calibrate", "target", "--stage", "fine", "--output", iso },
            new[] { "disc-art", "calibrate", "target", "--media", "hd-dvd", "--stage", "fine", "--output", iso },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "finest", "--output", iso },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine" },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine", "--output", Path.Combine(_tempDir, "bad.wav"), "--sectors", "1600" },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine", "--output", iso, "--disc-preset", "no-such-preset" },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine", "--output", iso, "--sectors", "1601" },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine", "--output", iso, "--sectors", "1600", "--inner-radius-mm", "abc" },
            new[] { "disc-art", "calibrate", "target", "--media", "dvd", "--stage", "fine", "--output", iso, "--sectors", "1600", "--drive-model", "Drive A" },
        })
        {
            (int code, _, string error) = await RunAsync(args);
            Assert.True(code == 1, string.Join(' ', args));
            Assert.StartsWith("Error:", error);
        }

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task Fine_fit_reports_the_published_parameters_saves_them_and_the_next_target_starts_from_them()
    {
        (int code, string output, string error) = await RunAsync("disc-art", "calibrate", "fit",
            "--points", Path.Combine(FixtureDir, FineRecord),
            "--drive-model", "Example Drive DVR-1", "--media-id", "MCC 03RG20", "--store", StorePath, "--json");

        Assert.True(code == 0, error + output);
        using (JsonDocument doc = JsonDocument.Parse(output))
        {
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("fitSucceeded").GetBoolean());
            Assert.Equal("fine", root.GetProperty("stage").GetString());
            Assert.Equal(33, root.GetProperty("pointCount").GetInt32());
            Assert.Equal(1, root.GetProperty("chosenCandidate").GetInt32());
            Assert.True(root.GetProperty("saved").GetBoolean());
            // Upstream docs/CALIBRATION_TUTORIAL.md: the disc burned after this fit and its coefficient set B.
            JsonElement result = root.GetProperty("result");
            Assert.Equal(23.99144094121467, result.GetProperty("innerRadiusMm").GetDouble(), 9);
            Assert.Equal(57.964899248503315, result.GetProperty("outerRadiusMm").GetDouble(), 9);
            Assert.Equal(-7.937249285949148E-05, result.GetProperty("pitchLinear").GetDouble(), 12);
            Assert.Equal(0.00014593344294674726, result.GetProperty("pitchQuadratic").GetDouble(), 12);
            Assert.Equal(-8.817669151956023E-05, result.GetProperty("pitchCubic").GetDouble(), 12);
        }

        (int listCode, string listed, _) = await RunAsync("disc-art", "calibrate", "list", "--store", StorePath, "--json");
        Assert.Equal(0, listCode);
        using (JsonDocument doc = JsonDocument.Parse(listed))
        {
            JsonElement entry = Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal("Example Drive DVR-1", entry.GetProperty("driveModel").GetString());
            Assert.Equal("MCC 03RG20", entry.GetProperty("mediaId").GetString());
            Assert.Equal("DVD", entry.GetProperty("media").GetString());
            Assert.Equal(23.99144094121467, entry.GetProperty("parameters").GetProperty("innerRadiusMm").GetDouble(), 9);
        }

        // The key is not case-sensitive; a small sector count keeps the image small.
        string iso = Path.Combine(_tempDir, "next.iso");
        (int targetCode, string targetOut, string targetError) = await RunAsync("disc-art", "calibrate", "target",
            "--media", "dvd", "--stage", "boundary", "--drive-model", "example drive dvr-1", "--media-id", "mcc 03rg20",
            "--store", StorePath, "--sectors", "1600", "--output", iso, "--json");
        Assert.True(targetCode == 0, targetError + targetOut);
        using (JsonDocument doc = JsonDocument.Parse(targetOut))
        {
            Assert.Contains("saved calibration", doc.RootElement.GetProperty("parametersFrom").GetString());
            JsonElement parameters = doc.RootElement.GetProperty("parameters");
            Assert.Equal(23.99144094121467, parameters.GetProperty("innerRadiusMm").GetDouble(), 9);
            Assert.Equal(-8.817669151956023E-05, parameters.GetProperty("pitchCubic").GetDouble(), 12);
            Assert.Equal(1600, parameters.GetProperty("sectors").GetInt64());
        }

        // Another blank disc in the same drive has no saved calibration and falls back to the preset.
        string other = Path.Combine(_tempDir, "other.iso");
        (int otherCode, string otherOut, _) = await RunAsync("disc-art", "calibrate", "target",
            "--media", "dvd", "--stage", "boundary", "--drive-model", "Example Drive DVR-1", "--media-id", "RITEKF1",
            "--store", StorePath, "--sectors", "1600", "--output", other, "--json");
        Assert.Equal(0, otherCode);
        using (JsonDocument doc = JsonDocument.Parse(otherOut))
        {
            Assert.Contains("disc preset 'dvd-5-120mm'", doc.RootElement.GetProperty("parametersFrom").GetString());
        }
    }

    [Fact]
    public async Task Coarse_fit_prints_the_published_wide_band_radii_and_saves_nothing_without_a_drive_and_media()
    {
        (int code, string output, string error) = await RunAsync("disc-art", "calibrate", "fit",
            "--points", Path.Combine(FixtureDir, CoarseRecord), "--store", StorePath);

        Assert.True(code == 0, error + output);
        // Upstream docs/CALIBRATION_TUTORIAL.md, wide-band estimate of the coarse record.
        Assert.Contains("inner 24.016692871476", output);
        Assert.Contains("outer 57.709242244500", output);
        Assert.Contains("band edges, 6 point(s)", output);
        Assert.Contains("Not saved", output);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Flagged_boundary_fit_is_not_applied_or_saved_until_a_candidate_is_named()
    {
        (int code, string output, string error) = await RunAsync("disc-art", "calibrate", "fit",
            "--points", Path.Combine(FixtureDir, BoundaryRecord),
            "--drive-model", "Example Drive DVR-1", "--media-id", "MCC 03RG20", "--store", StorePath, "--json");

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, error);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("fitSucceeded").GetBoolean());
        Assert.False(root.GetProperty("saved").GetBoolean());
        Assert.Equal(154, root.GetProperty("pointCount").GetInt32());
        Assert.NotEqual(0, root.GetProperty("warnings").GetArrayLength());
        // No applied result: the property is left out (or null).
        Assert.True(!root.TryGetProperty("result", out JsonElement applied) || applied.ValueKind == JsonValueKind.Null);
        Assert.Contains("--candidate", root.GetProperty("error").GetString());
        // The candidates are still listed so the person can compare them; the first is the one upstream's
        // tutorial applied (the radii of its next disc).
        JsonElement first = root.GetProperty("candidates").EnumerateArray().First();
        Assert.Equal(1, first.GetProperty("number").GetInt32());
        Assert.Equal(23.990847220268247, first.GetProperty("parameters").GetProperty("innerRadiusMm").GetDouble(), 9);
        Assert.Equal(57.96535646048506, first.GetProperty("parameters").GetProperty("outerRadiusMm").GetDouble(), 9);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Fit_command_rejects_bad_arguments_and_records_without_points()
    {
        string fine = Path.Combine(FixtureDir, FineRecord);
        string broken = Path.Combine(_tempDir, "broken.calibration.json");
        File.WriteAllText(broken, "{ \"target\": 5 }");
        foreach (string[] args in new[]
        {
            new[] { "disc-art", "calibrate", "fit" },
            new[] { "disc-art", "calibrate", "fit", "--points", Path.Combine(_tempDir, "missing.calibration.json") },
            new[] { "disc-art", "calibrate", "fit", "--points", broken },
            new[] { "disc-art", "calibrate", "fit", "--points", Path.Combine(FixtureDir, EmptyRecord) },
            new[] { "disc-art", "calibrate", "fit", "--points", fine, "--drive-model", "Example Drive DVR-1" },
            new[] { "disc-art", "calibrate", "fit", "--points", fine, "--candidate", "0" },
            new[] { "disc-art", "calibrate", "fit", "--points", fine, "--candidate", "99", "--store", StorePath,
                "--drive-model", "Example Drive DVR-1", "--media-id", "MCC 03RG20" },
        })
        {
            (int code, _, string error) = await RunAsync(args);
            Assert.True(code == 1, string.Join(' ', args));
            Assert.StartsWith("Error:", error);
        }

        Assert.False(File.Exists(StorePath));
    }
}
