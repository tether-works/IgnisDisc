using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class RosettaLadderCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-cli-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _deviceService;

    public RosettaLadderCliTests()
    {
        Directory.CreateDirectory(_tempDir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _deviceService = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RosettaLadder_PlanOnlyAndHelp_WriteManifestAndValidateArguments()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int helpCode = await CliCommandRunner.RunAsync(["rosetta", "ladder", "--help"], stdout, stderr);
        Assert.Equal(0, helpCode);
        Assert.Contains("rosetta ladder", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--media dvd|cd", stdout.ToString(), StringComparison.OrdinalIgnoreCase);

        string isoPath = Path.Combine(_tempDir, "ladder-plan.iso");
        stdout = new StringWriter();
        stderr = new StringWriter();
        int planCode = await CliCommandRunner.RunAsync(
            ["rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--plan-only", "--json"],
            stdout,
            stderr);

        Assert.Equal(0, planCode);
        Assert.False(File.Exists(isoPath));
        Assert.True(File.Exists(isoPath + ".json"));

        using (JsonDocument doc = JsonDocument.Parse(stdout.ToString()))
        {
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("planOnly").GetBoolean());
            Assert.Equal("DVD-R", root.GetProperty("media").GetString());
            Assert.Equal(24, root.GetProperty("rungCount").GetInt32());
            Assert.Equal(3, root.GetProperty("zoneCount").GetInt32());
        }

        // CD-R --plan-only with --media cd and --media cd-r.
        string wavPath = Path.Combine(_tempDir, "cd-ladder-plan.wav");
        stdout = new StringWriter();
        stderr = new StringWriter();
        int cdPlanCode = await CliCommandRunner.RunAsync(
            ["rosetta", "ladder", "--media", "cd", "--output", wavPath, "--start-angle", "15", "--plan-only", "--json"],
            stdout,
            stderr);

        Assert.Equal(0, cdPlanCode);
        Assert.False(File.Exists(wavPath));
        Assert.False(File.Exists(Path.ChangeExtension(wavPath, ".cue")));
        Assert.True(File.Exists(wavPath + ".json"));

        using (JsonDocument doc = JsonDocument.Parse(stdout.ToString()))
        {
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("planOnly").GetBoolean());
            Assert.Equal("CD-R", root.GetProperty("media").GetString());
            Assert.Equal("cd-80", root.GetProperty("discPresetId").GetString());
            Assert.Equal(15, root.GetProperty("rungCount").GetInt32());
            Assert.Equal(3, root.GetProperty("zoneCount").GetInt32());
            foreach (JsonElement rung in root.GetProperty("rungs").EnumerateArray())
            {
                Assert.True(rung.GetProperty("cellWidthUm").GetDouble() >= 50.0);
                Assert.True(rung.GetProperty("cellHeightUm").GetDouble() >= 75.0);
                Assert.True(rung.GetProperty("cdSupported").GetBoolean());
            }
        }

        // Reject unsupported --media, non-wav/raw output on CD, --no-interleave on DVD, missing --output, unknown --disc-preset, and 80 mm mini-DVD preset.
        foreach (string[] badArgs in new[]
        {
            new[] { "rosetta", "ladder", "--media", "bd", "--output", isoPath, "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "cd", "--output", isoPath, "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--no-interleave", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "cd", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--disc-preset", "no-such-preset", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "cd", "--output", wavPath, "--disc-preset", "no-such-preset", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--disc-preset", "dvd-5-80mm", "--plan-only" },
        })
        {
            var errOut = new StringWriter();
            int code = await CliCommandRunner.RunAsync(badArgs, new StringWriter(), errOut);
            Assert.Equal(1, code);
        }
    }

    [Fact]
    public async Task RosettaLadder_GeneratesFullDvdImageAndManifest_AndPassesSimulatedReadback()
    {
        string isoPath = Path.Combine(_tempDir, "ladder-full.iso");
        string jsonPath = isoPath + ".json";

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = await CliCommandRunner.RunAsync(
                ["rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--json"],
                stdout,
                stderr);

            Assert.Equal(0, code);
            Assert.True(File.Exists(isoPath));
            Assert.True(File.Exists(jsonPath));
            Assert.Equal(2_295_104L * 2048L, new FileInfo(isoPath).Length);

            using (JsonDocument doc = JsonDocument.Parse(stdout.ToString()))
            {
                JsonElement root = doc.RootElement;
                Assert.True(root.GetProperty("success").GetBoolean());
                Assert.False(root.GetProperty("planOnly").GetBoolean());
                Assert.Equal(2_295_104u, root.GetProperty("sectorsWritten").GetUInt32());
                Assert.Equal(24, root.GetProperty("rungCount").GetInt32());
            }

            RosettaLadderVerificationReport report = RosettaLadderReadback.VerifyIso(isoPath);
            Assert.True(report.AllRungsMatch, $"Readback matched {report.PassedRungs}/{report.TotalRungs} rungs.");
            Assert.Equal(24, report.PassedRungs);
        }
        finally
        {
            if (File.Exists(isoPath))
            {
                File.Delete(isoPath);
            }
        }
    }

    [Fact]
    public async Task RosettaLadder_GeneratesFullCd80WavCueAndManifest_AndPassesSimulatedReadback()
    {
        string wavPath = Path.Combine(_tempDir, "cd-ladder-80.wav");
        string cuePath = Path.ChangeExtension(wavPath, ".cue");
        string jsonPath = wavPath + ".json";

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = await CliCommandRunner.RunAsync(
                ["rosetta", "ladder", "--media", "cd-r", "--output", wavPath, "--json"],
                stdout,
                stderr);

            Assert.True(code == 0, stderr.ToString() + stdout.ToString());
            Assert.True(File.Exists(wavPath));
            Assert.True(File.Exists(cuePath));
            Assert.True(File.Exists(jsonPath));
            Assert.Equal(44L + (359_673L * 2352L), new FileInfo(wavPath).Length);
            Assert.Contains(Path.GetFileName(wavPath), File.ReadAllText(cuePath), StringComparison.Ordinal);

            using (JsonDocument doc = JsonDocument.Parse(stdout.ToString()))
            {
                JsonElement root = doc.RootElement;
                Assert.True(root.GetProperty("success").GetBoolean());
                Assert.False(root.GetProperty("planOnly").GetBoolean());
                Assert.Equal("CD-R", root.GetProperty("media").GetString());
                Assert.Equal("cd-80", root.GetProperty("discPresetId").GetString());
                Assert.Equal(cuePath, root.GetProperty("cueSheetPath").GetString());
                Assert.Equal(359_673u, root.GetProperty("sectorsWritten").GetUInt32());
                Assert.Equal(15, root.GetProperty("rungCount").GetInt32());
            }

            RosettaLadderVerificationReport report = RosettaLadderReadback.VerifyCdTrack(wavPath, jsonPath);
            Assert.True(report.AllRungsMatch, $"CD-80 readback matched {report.PassedRungs}/{report.TotalRungs} rungs.");
            Assert.Equal(15, report.PassedRungs);
        }
        finally
        {
            if (File.Exists(wavPath))
            {
                File.Delete(wavPath);
            }
        }
    }

    [Fact]
    public async Task RosettaLadder_CdR_BurnsThroughVirtualMmcDriveSao_VerifiesAndDecodesAll15Rungs()
    {
        string presetsPath = Path.Combine(_tempDir, "cd-presets.json");
        File.WriteAllText(presetsPath, """
            {
              "schemaVersion": 4,
              "cdPresets": [
                {
                  "id": "ladder-sao-test",
                  "displayName": "CD Ladder SAO Test",
                  "description": "Test preset for VirtualMmcDrive SAO ladder burn",
                  "sectors": 100000,
                  "innerRadiusMm": 24.825,
                  "outerRadiusMm": 46.5,
                  "linearVelocityMmPerSecond": 1200
                }
              ],
              "dvdPresets": [],
              "bluRayPresets": []
            }
            """);

        string wavPath = Path.Combine(_tempDir, "sao-ladder.wav");
        string cuePath = Path.ChangeExtension(wavPath, ".cue");
        string jsonPath = wavPath + ".json";
        string mediumBacking = Path.Combine(_tempDir, "virtual-cdr.bin");
        string logsDir = Path.Combine(_tempDir, "logs");

        var genOut = new StringWriter();
        var genErr = new StringWriter();
        int genCode = await CliCommandRunner.RunAsync(
            [
                "rosetta", "ladder",
                "--media", "cd",
                "--output", wavPath,
                "--disc-presets-file", presetsPath,
                "--disc-preset", "ladder-sao-test",
                "--start-angle", "22.5",
                "--json",
            ],
            genOut,
            genErr);

        Assert.True(genCode == 0, genErr.ToString() + genOut.ToString());
        Assert.True(File.Exists(wavPath));
        Assert.True(File.Exists(cuePath));
        Assert.True(File.Exists(jsonPath));

        using var medium = VirtualDiscMedium.CreateBlankCdR700(mediumBacking);
        _recorder.InsertMedia(medium);

        var burnOut = new StringWriter();
        var burnErr = new StringWriter();
        int burnCode = await CliCommandRunner.RunAsync(
            ["burn-audio", "--drive", "VDRV-01", "--tracks", cuePath, "--dao", "--native", "--log-dir", logsDir, "--json"],
            burnOut,
            burnErr,
            _deviceService,
            _burnEngine);

        Assert.True(burnCode == 0, burnErr.ToString() + burnOut.ToString());
        using (JsonDocument burnDoc = JsonDocument.Parse(burnOut.ToString()))
        {
            Assert.True(burnDoc.RootElement.GetProperty("verified").GetBoolean());
            Assert.True(burnDoc.RootElement.GetProperty("native").GetBoolean());
            Assert.Equal(1, burnDoc.RootElement.GetProperty("trackCount").GetInt32());
        }

        VirtualTrack burnedTrack = Assert.Single(Assert.Single(medium.Sessions).Tracks);
        Assert.True(burnedTrack.IsAudio);
        Assert.True(burnedTrack.SectorCount >= 100_000);

        var verifyOut = new StringWriter();
        var verifyErr = new StringWriter();
        int verifyCode = await CliCommandRunner.RunAsync(
            ["verify", "--drive", "VDRV-01", "--cue", cuePath, "--log-dir", logsDir, "--json"],
            verifyOut,
            verifyErr,
            _deviceService,
            _burnEngine);

        Assert.True(verifyCode == 0, verifyErr.ToString() + verifyOut.ToString());
        using (JsonDocument verifyDoc = JsonDocument.Parse(verifyOut.ToString()))
        {
            Assert.True(verifyDoc.RootElement.GetProperty("success").GetBoolean());
        }

        byte[] discAudio = new byte[100_000 * OpticalConstants.RawSectorSize];
        for (int i = 0; i < 100_000; i++)
        {
            medium.ReadSector(
                burnedTrack.StartLba + i,
                discAudio.AsSpan(i * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
                OpticalConstants.RawSectorSize);
        }

        Assert.Equal(File.ReadAllBytes(wavPath)[44..], discAudio);

        RosettaLadderManifest manifest = RosettaLadderDisc.LoadManifestJson(jsonPath);
        RosettaLadderVerificationReport readback = RosettaLadderReadback.VerifyCdStream(
            new MemoryStream(discAudio, writable: false),
            CdAudioByteOrder.LittleEndian,
            manifest);
        Assert.True(readback.AllRungsMatch, $"Burned SAO disc readback matched {readback.PassedRungs}/{readback.TotalRungs} rungs.");
        Assert.Equal(15, readback.PassedRungs);
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
