using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CdBurner.Cli;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class RosettaLadderCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-cli-" + Guid.NewGuid().ToString("N"));

    public RosettaLadderCliTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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
            Assert.Equal(24, root.GetProperty("rungCount").GetInt32());
            Assert.Equal(3, root.GetProperty("zoneCount").GetInt32());
        }

        // Reject --media cd, missing --output, unknown --disc-preset, and 80 mm mini-DVD preset.
        foreach (string[] badArgs in new[]
        {
            new[] { "rosetta", "ladder", "--media", "cd", "--output", isoPath, "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--plan-only" },
            new[] { "rosetta", "ladder", "--media", "dvd", "--output", isoPath, "--disc-preset", "no-such-preset", "--plan-only" },
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
}
