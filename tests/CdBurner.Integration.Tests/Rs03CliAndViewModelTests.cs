using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Archival;
using CdBurner.Core.Archival.Rs03;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class Rs03CliAndViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis_rs03_int_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DiscDeviceManager _devices = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public Rs03CliAndViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _devices.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cli_EccRs03_CreateVerifyRepair_Json_RoundTripsEccAndAugmentedModes()
    {
        string isoEcc = BuildArchiveIso("cli_ecc", out string srcFolder);
        byte[] originalIsoHash = SHA256.HashData(File.ReadAllBytes(isoEcc));
        string eccPath = isoEcc + ".ecc";

        // 1. Standalone .ecc create -> wipe LBA 0-64 + scattered bursts -> verify -> repair -> verify
        var (cExit, cJson) = await RunCliAsync(["ecc-rs03", "create", "--image", isoEcc, "--ecc", eccPath, "--roots", "32", "--json"]);
        Assert.Equal(0, cExit);
        Assert.True(cJson.GetProperty("success").GetBoolean());
        Assert.Equal("EccFile", cJson.GetProperty("mode").GetString());

        using (var fs = new FileStream(isoEcc, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ZeroSectors(fs, 0, 65);
            ZeroSectors(fs, 120, 4);
        }

        var (vBadExit, vBadJson) = await RunCliAsync(["ecc-rs03", "verify", "--image", isoEcc, "--ecc", eccPath, "--json"]);
        Assert.Equal(2, vBadExit);
        Assert.False(vBadJson.GetProperty("isValid").GetBoolean());
        Assert.True(vBadJson.GetProperty("isRecoverable").GetBoolean());

        var (rExit, rJson) = await RunCliAsync(["ecc-rs03", "repair", "--image", isoEcc, "--ecc", eccPath, "--json"]);
        Assert.Equal(0, rExit);
        Assert.True(rJson.GetProperty("success").GetBoolean());
        Assert.True(rJson.GetProperty("verifiedAfterRepair").GetBoolean());
        Assert.Equal(originalIsoHash, SHA256.HashData(File.ReadAllBytes(isoEcc)));

        // 2. Augmented mode rewritable guard + create + repair
        string isoAug = BuildArchiveIso("cli_aug", out _);
        byte[] augOrig = File.ReadAllBytes(isoAug);

        var (guardExit, guardJson) = await RunCliAsync(["ecc-rs03", "create", "--image", isoAug, "--mode", "augmented", "--media", "dvd+rw", "--medium-sectors", "15300", "--json"]);
        Assert.Equal(1, guardExit);
        Assert.False(guardJson.GetProperty("success").GetBoolean());
        Assert.Contains("rewritable", guardJson.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var (augCreateExit, augCreateJson) = await RunCliAsync(["ecc-rs03", "create", "--image", isoAug, "--mode", "augmented", "--media", "cd", "--medium-sectors", "15300", "--json"]);
        Assert.Equal(0, augCreateExit);
        Assert.True(augCreateJson.GetProperty("success").GetBoolean());
        Assert.Equal("AugmentedImage", augCreateJson.GetProperty("mode").GetString());

        using (var fs = new FileStream(isoAug, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ZeroSectors(fs, 0, 65);
            ZeroSectors(fs, 200, 3);
        }
        var (augRepExit, augRepJson) = await RunCliAsync(["ecc-rs03", "repair", "--image", isoAug, "--json"]);
        Assert.Equal(0, augRepExit);
        Assert.True(augRepJson.GetProperty("success").GetBoolean());
        Assert.Equal(SHA256.HashData(augOrig), SHA256.HashData(File.ReadAllBytes(isoAug).AsSpan(0, augOrig.Length)));

        // Suppress unused warning on srcFolder helper
        Assert.True(Directory.Exists(srcFolder));
    }

    [Fact]
    public async Task ArchiveRecoveryService_RecoversAllFiles_FromDamagedAugmentedVirtualDvdPlusRwAndImage()
    {
        string isoPath = BuildArchiveIso("rec_aug", out string srcFolder);
        await Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.AugmentedImage,
            MediumCapacitySectors = 60 * 255,
            MediaType = DiscMediaType.CdR,
            Overwrite = true
        });

        // Burn to a simulated DVD+RW and damage LBA 0-64 + scattered sectors on the virtual disc.
        var recorder = Assert.IsType<VirtualDiscRecorder>(_devices.GetRecorders(includeVirtual: true).First(r => r.IsVirtual));
        var medium = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        recorder.InsertMedia(medium);

        using (var isoFs = File.OpenRead(isoPath))
        {
            var burn = await _burnEngine.BurnDataStreamAsync(recorder, isoFs, new CdBurner.Core.Abstractions.DataBurnOptions
            {
                VolumeLabel = "REC_AUG",
                OverwriteExistingData = true
            }, progress: null, CancellationToken.None);
            Assert.True(burn.Success);
        }

        // Verify RecordedVolumeExtent sees the full RS03 augmented capacity on overwrite-only DVD+RW.
        int? endSector = RecordedVolumeExtent.FindEndSector(lba =>
        {
            if (lba < 0 || lba >= medium.UsedSectors) return null;
            byte[] buf = new byte[2048];
            medium.ReadSector(lba, buf);
            return buf;
        });
        Assert.Equal(60 * 255, endSector);

        // Inject unreadable LBA 0..64 + scattered bad sectors on the virtual medium.
        for (int lba = 0; lba < 65; lba++)
        {
            medium.SimulatedBadLbas.Add(lba);
        }
        medium.SimulatedBadLbas.Add(150);
        medium.SimulatedBadLbas.Add(256);

        string outDir = Path.Combine(_root, "recovered_out");
        var report = await ArchiveRecoveryService.RecoverFromDiscAsync(recorder, _burnEngine, outDir, par2Path: null);
        Assert.True(report.FullyRecovered, report.Problem);
        Assert.True(report.Rs03RecoveredSectors >= 5);
        foreach (string src in Directory.EnumerateFiles(srcFolder))
        {
            string dst = Path.Combine(outDir, Path.GetFileName(src));
            Assert.True(File.Exists(dst));
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(src)), SHA256.HashData(File.ReadAllBytes(dst)));
        }
    }

    [Fact]
    public async Task ArchiveViewModel_OptInRs03_EnforcesRewritableGuard_AndStagesEccFiles()
    {
        string srcDir = Path.Combine(_root, "vm_src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, "alpha.bin"), new byte[12_000]);
        File.WriteAllBytes(Path.Combine(srcDir, "beta.bin"), new byte[18_000]);

        string staging = Path.Combine(_root, "vm_stage");
        var vm = new ArchiveViewModel(_devices, _burnEngine)
        {
            SourceFolder = srcDir,
            StagingFolder = staging,
            RedundancyPercent = 0,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR)
        };

        Assert.False(vm.UseRs03Parity);
        Assert.False(vm.UseRs03AugmentedMode);
        Assert.Null(vm.Rs03RewritableWarning);

        // Selecting rewritable media + augmented mode triggers explicit guard warning and blocks staging.
        vm.SelectedMedia = new ArchiveMediaOption("DVD+RW", DiscMediaType.DvdPlusRw);
        vm.UseRs03Parity = true;
        vm.UseRs03AugmentedMode = true;
        Assert.NotNull(vm.Rs03RewritableWarning);
        Assert.False(await vm.StageAsync());
        Assert.Contains("rewritable", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Standalone .ecc mode on CD-R works cleanly alongside mounted archive staging.
        vm.SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR);
        vm.UseRs03AugmentedMode = false;
        Assert.Null(vm.Rs03RewritableWarning);

        bool staged = await vm.StageAsync();
        Assert.True(staged, vm.StatusMessage);
        Assert.Single(vm.Discs);
        Assert.NotNull(vm.Discs[0].StagedIsoPath);
        Assert.NotNull(vm.Discs[0].StagedEccPath);
        Assert.True(File.Exists(vm.Discs[0].StagedIsoPath!));
        Assert.True(File.Exists(vm.Discs[0].StagedEccPath!));
    }

    [Fact]
    public async Task ImageStudioViewModel_OptInRs03_EnforcesRewritableGuard_AndCreatesParity()
    {
        string isoPath = BuildArchiveIso("img_vm", out _);
        var recorder = Assert.IsType<VirtualDiscRecorder>(_devices.GetRecorders(includeVirtual: true).First(r => r.IsVirtual));
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdPlusRw47());

        using var vm = new ImageStudioViewModel(_devices, _burnEngine)
        {
            SelectedRecorder = recorder,
            SourceImagePath = isoPath,
            VerifyAfterBurn = false
        };

        Assert.False(vm.CreateRs03Parity);
        Assert.False(vm.UseRs03AugmentedMode);
        Assert.Null(vm.Rs03RewritableWarning);

        // Opting into augmented mode on DVD+RW shows warning and blocks parity creation until allowed or switched to .ecc.
        vm.CreateRs03Parity = true;
        vm.UseRs03AugmentedMode = true;
        Assert.NotNull(vm.Rs03RewritableWarning);
        Assert.Null(await vm.CreateRs03ParityForSourceAsync());
        Assert.Contains("rewritable", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Switching to standalone .ecc mode clears the warning and creates the .ecc file safely on DVD+RW.
        vm.UseRs03AugmentedMode = false;
        Assert.Null(vm.Rs03RewritableWarning);
        var created = await vm.CreateRs03ParityForSourceAsync();
        Assert.NotNull(created);
        Assert.Equal(Rs03Mode.EccFile, created!.Mode);
        Assert.True(File.Exists(created.EccFilePath!));

        var burn = await vm.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.True(burn.Success);
    }

    private string BuildArchiveIso(string name, out string sourceFolder)
    {
        sourceFolder = Path.Combine(_root, name + "_src");
        string stageFolder = Path.Combine(_root, name + "_stage");
        Directory.CreateDirectory(sourceFolder);
        var rng = new Random(123);
        for (int i = 0; i < 6; i++)
        {
            byte[] data = new byte[12_000 + i * 2048];
            rng.NextBytes(data);
            File.WriteAllBytes(Path.Combine(sourceFolder, $"item_{i:D2}.dat"), data);
        }

        var plan = ArchiveSpanPlanner.Plan(sourceFolder, DiscMediaType.CdR, redundancyPercent: 0, fillFreeSpace: false);
        var staged = ArchiveStager.StageAsync(plan, stageFolder, par2Path: null).GetAwaiter().GetResult();
        string isoPath = Path.Combine(_root, name + ".iso");
        var root = CompilationFolderImporter.ImportAsRoot(staged[0], strict: true);
        IsoUdfImageBuilder.BuildToFile(root, isoPath, new IsoBuildOptions(
            VolumeLabel: ArchiveManifest.VolumeLabel(plan.SetId, 1),
            IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true));
        return isoPath;
    }

    private async Task<(int ExitCode, JsonElement Json)> RunCliAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        string raw = stdout.ToString().Trim();
        using var doc = JsonDocument.Parse(raw);
        return (exit, doc.RootElement.Clone());
    }

    private static void ZeroSectors(FileStream fs, int startLba, int count)
    {
        byte[] zeros = new byte[count * 2048];
        fs.Position = (long)startLba * 2048;
        fs.Write(zeros, 0, zeros.Length);
    }
}
