using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Archival;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class CrossDiscParityCliAndViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis_xparity_int_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DiscDeviceManager _devices = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public CrossDiscParityCliAndViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _devices.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Cli_ArchiveWithParityDiscs_StagesVerifiesAndRebuildsMissingDisc()
    {
        string srcDir = Path.Combine(_root, "cli_src");
        string stageDir = Path.Combine(_root, "cli_stage");
        Directory.CreateDirectory(srcDir);

        byte[] payloadA = new byte[140_000];
        byte[] payloadB = new byte[95_111];
        new Random(42).NextBytes(payloadA);
        new Random(43).NextBytes(payloadB);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "part_a.bin"), payloadA);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "part_b.bin"), payloadB);

        // Run `archive --parity-discs 1` (1 data disc + 1 parity disc via CLI `archive`).
        var (stageExit, stageJson) = await RunCliAsync([
            "archive", "--input", srcDir, "--staging", stageDir,
            "--media", "cd", "--redundancy", "0", "--parity-discs", "1", "--json"
        ]);
        Assert.Equal(0, stageExit);
        Assert.True(stageJson.GetProperty("success").GetBoolean());
        Assert.Equal(1, stageJson.GetProperty("dataDiscCount").GetInt32());
        Assert.Equal(1, stageJson.GetProperty("parityDiscCount").GetInt32());
        Assert.Equal(2, stageJson.GetProperty("totalDiscs").GetInt32());

        var discsProp = stageJson.GetProperty("discs");
        Assert.Equal(2, discsProp.GetArrayLength());
        string disc1Path = discsProp[0].GetProperty("path").GetString()!;
        string disc2Path = discsProp[1].GetProperty("path").GetString()!;
        Assert.False(discsProp[0].GetProperty("isParityDisc").GetBoolean());
        Assert.True(discsProp[1].GetProperty("isParityDisc").GetBoolean());

        // `archive verify` validates the parity disc cleanly.
        var (verifyExit, verifyJson) = await RunCliAsync(["archive", "verify", "--path", disc2Path, "--json"]);
        Assert.Equal(0, verifyExit);
        Assert.True(verifyJson.GetProperty("success").GetBoolean());

        // Delete Disc 1 completely and rebuild via `archive rebuild`.
        Directory.Delete(disc1Path, recursive: true);
        string rebuiltOut = Path.Combine(_root, "cli_rebuilt_disc01");
        var (rebuildExit, rebuildJson) = await RunCliAsync([
            "archive", "rebuild", "--discs", disc2Path, "--disc", "1", "--out", rebuiltOut, "--json"
        ]);
        Assert.Equal(0, rebuildExit);
        Assert.True(rebuildJson.GetProperty("success").GetBoolean());
        Assert.Equal(1, rebuildJson.GetProperty("rebuiltDiscNumber").GetInt32());
        Assert.Equal(2, rebuildJson.GetProperty("filesRestored").GetInt32());
        Assert.Equal(SHA256.HashData(payloadA), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rebuiltOut, "part_a.bin"))));
        Assert.Equal(SHA256.HashData(payloadB), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rebuiltOut, "part_b.bin"))));
    }

    [Fact]
    public async Task Cli_ArchiveRebuild_ThreeDataPlusOneParity_RebuildsDeletedDisc2()
    {
        string srcDir = Path.Combine(_root, "cli_3p1_src");
        string stageDir = Path.Combine(_root, "cli_3p1_stage");
        Directory.CreateDirectory(srcDir);

        var rng = new Random(9001);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in new[] { "f1.bin", "f2.bin", "f3.bin" })
        {
            byte[] data = new byte[150_000];
            rng.NextBytes(data);
            await File.WriteAllBytesAsync(Path.Combine(srcDir, name), data);
            hashes[name] = SHA256.HashData(data);
        }

        var plan = ArchiveSpanPlanner.Plan(
            srcDir,
            DiscMediaType.CdR,
            redundancyPercent: 0,
            setId: "cli3p1",
            fillFreeSpace: false,
            ceilingSectorsOverride: 130,
            parityDiscCount: 1);
        Assert.Equal(3, plan.Discs.Count);

        var staged = await ArchiveStager.StageDiscsAsync(plan, stageDir, par2Path: null);
        Assert.Equal(4, staged.Count);

        Directory.Delete(staged[1].DiscRoot, recursive: true);

        string outDir = Path.Combine(_root, "cli_3p1_rebuilt2");
        string discsArg = string.Join(';', staged[0].DiscRoot, staged[2].DiscRoot, staged[3].DiscRoot);
        var (exit, json) = await RunCliAsync([
            "archive-rebuild", "--discs", discsArg, "--disc", "2", "--out", outDir, "--json"
        ]);
        Assert.Equal(0, exit);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(2, json.GetProperty("rebuiltDiscNumber").GetInt32());

        foreach (var file in plan.Discs[1].Files)
        {
            byte[] actual = await File.ReadAllBytesAsync(Path.Combine(outDir, file.RelativePath));
            Assert.Equal(hashes[file.RelativePath], SHA256.HashData(actual));
        }
    }

    [Fact]
    public async Task ArchiveViewModel_OptInCrossDiscParity_PlansStagesAndBurnsParityDisc()
    {
        string srcDir = Path.Combine(_root, "vm_src");
        string stageDir = Path.Combine(_root, "vm_stage");
        Directory.CreateDirectory(srcDir);

        byte[] data = new byte[100_000];
        new Random(77).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "photo.bin"), data);

        var vm = new ArchiveViewModel(_devices, _burnEngine)
        {
            SourceFolder = srcDir,
            StagingFolder = stageDir,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0
        };

        Assert.False(vm.UseCrossDiscParity);
        Assert.Equal(1, vm.ParityDiscCount);
        Assert.Equal(0, vm.EffectiveParityDiscCount);

        vm.UseCrossDiscParity = true;
        vm.ParityDiscCount = 2;
        Assert.Equal(2, vm.EffectiveParityDiscCount);

        var plan = await vm.PlanAsync();
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.ParityDiscCount);
        Assert.Equal(3, vm.Discs.Count); // 1 data + 2 parity
        Assert.False(vm.Discs[0].IsParityDisc);
        Assert.True(vm.Discs[1].IsParityDisc);
        Assert.True(vm.Discs[2].IsParityDisc);
        Assert.Contains("+ 2 parity", vm.PlanSummary);

        bool stagedOk = await vm.StageAsync();
        Assert.True(stagedOk, vm.StatusMessage);
        Assert.Equal("Staged", vm.Discs[0].Status);
        Assert.Equal("Staged (Parity)", vm.Discs[1].Status);
        Assert.Equal("Staged (Parity)", vm.Discs[2].Status);
        Assert.True(File.Exists(Path.Combine(vm.Discs[1].StagedPath!, ArchiveParitySet.ParitySetFileName)));
        Assert.True(File.Exists(Path.Combine(vm.Discs[2].StagedPath!, ArchiveParitySet.ParityPayloadFileName)));

        // Virtual burn of a parity disc row succeeds end-to-end.
        var vrec = vm.AvailableRecorders.OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        vrec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        vm.SelectedRecorder = vrec;
        vm.VerifyAfterBurn = true;

        var burnResult = await vm.BurnDiscAsync(vm.Discs[1]);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal("Burned", vm.Discs[1].Status);
    }

    private async Task<(int ExitCode, JsonElement Json)> RunCliAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        string raw = stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(raw), $"Expected JSON output on stdout, stderr was: {stderr}");
        using var doc = JsonDocument.Parse(raw);
        return (code, doc.RootElement.Clone());
    }
}
