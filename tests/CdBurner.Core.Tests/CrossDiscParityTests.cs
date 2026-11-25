using System.Diagnostics;
using System.Security.Cryptography;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Tests;

public sealed class CrossDiscParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis_xparity_core_" + Guid.NewGuid().ToString("N")[..8]);

    public CrossDiscParityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void CrossDiscReedSolomon_RoundTripsAllErasureCombinations(int dataCount, int parityCount)
    {
        var codec = new CrossDiscReedSolomon(dataCount, parityCount);
        const int stripeBytes = 4096;
        var rng = new Random(dataCount * 100 + parityCount);

        byte[][] data = new byte[dataCount][];
        for (int j = 0; j < dataCount; j++)
        {
            data[j] = new byte[stripeBytes];
            rng.NextBytes(data[j]);
        }

        byte[][] parity = new byte[parityCount][];
        for (int p = 0; p < parityCount; p++)
        {
            parity[p] = new byte[stripeBytes];
            for (int j = 0; j < dataCount; j++)
            {
                CrossDiscReedSolomon.MultiplyXor(codec.GetCoefficient(p, j), data[j], parity[p]);
            }
        }

        // Parity 0 must be exact bitwise XOR of all data stripes.
        byte[] expectedXor = new byte[stripeBytes];
        for (int j = 0; j < dataCount; j++)
        {
            for (int b = 0; b < stripeBytes; b++)
            {
                expectedXor[b] ^= data[j][b];
            }
        }
        Assert.Equal(expectedXor, parity[0]);

        // Test all subsets of missing data discs of size 1..min(dataCount, parityCount).
        int maxMissing = Math.Min(dataCount, parityCount);
        for (int mask = 1; mask < (1 << dataCount); mask++)
        {
            int missingCount = System.Numerics.BitOperations.PopCount((uint)mask);
            if (missingCount > maxMissing)
            {
                continue;
            }

            var missingData = new List<int>();
            var survivingData = new List<int>();
            for (int j = 0; j < dataCount; j++)
            {
                if ((mask & (1 << j)) != 0) missingData.Add(j);
                else survivingData.Add(j);
            }

            // Pick the last `missingCount` parity discs (to test non-trivial Cauchy rows too).
            var selectedParity = Enumerable.Range(parityCount - missingCount, missingCount).ToList();
            byte[,] inv = codec.BuildRecoveryMatrix(missingData, selectedParity);

            byte[][] residuals = new byte[missingCount][];
            for (int r = 0; r < missingCount; r++)
            {
                residuals[r] = (byte[])parity[selectedParity[r]].Clone();
                foreach (int j in survivingData)
                {
                    CrossDiscReedSolomon.MultiplyXor(codec.GetCoefficient(selectedParity[r], j), data[j], residuals[r]);
                }
            }

            for (int c = 0; c < missingCount; c++)
            {
                byte[] rebuilt = new byte[stripeBytes];
                for (int r = 0; r < missingCount; r++)
                {
                    CrossDiscReedSolomon.MultiplyXor(inv[c, r], residuals[r], rebuilt);
                }
                Assert.Equal(data[missingData[c]], rebuilt);
            }
        }
    }

    [Fact]
    public async Task StreamingE2E_ThreeDataPlusOneParity_DeletesDisc2Completely_RebuildsAndVerifiesSha256()
    {
        var (srcDir, stagingDir, plan, staged, sourceHashes) = await BuildAndStageSpanAsync("e2e_3p1", parityDiscCount: 1);

        Assert.Equal(3, plan.Discs.Count);
        Assert.Equal(1, plan.ParityDiscCount);
        Assert.Equal(4, plan.TotalDiscCount);
        Assert.Equal(4, staged.Count);
        Assert.False(staged[0].IsParityDisc);
        Assert.False(staged[1].IsParityDisc);
        Assert.False(staged[2].IsParityDisc);
        Assert.True(staged[3].IsParityDisc);
        Assert.Equal(4, staged[3].DiscNumber);

        string disc1 = staged[0].DiscRoot;
        string disc2 = staged[1].DiscRoot;
        string disc3 = staged[2].DiscRoot;
        string disc4 = staged[3].DiscRoot;

        // Every disc carries identical CATALOG.JSON with parity-set metadata.
        string cat1 = await File.ReadAllTextAsync(Path.Combine(disc1, ArchiveCatalog.CatalogFileName));
        foreach (var s in staged)
        {
            Assert.Equal(cat1, await File.ReadAllTextAsync(Path.Combine(s.DiscRoot, ArchiveCatalog.CatalogFileName)));
        }

        var catDoc = ArchiveCatalog.ParseJson(cat1)!;
        Assert.Equal(4, catDoc.DiscCount);
        Assert.NotNull(catDoc.ParitySet);
        Assert.Equal(3, catDoc.ParitySet!.DataDiscCount);
        Assert.Equal(1, catDoc.ParitySet.ParityDiscCount);
        Assert.Equal(4, catDoc.ParitySet.TotalDiscCount);
        Assert.Equal([4], catDoc.ParitySet.ParityDiscNumbers);
        Assert.Equal(ArchiveParitySet.ParitySetFileName, catDoc.ParitySet.ParitySetFileName);

        // Parity Disc 4 has PARITY-SET.JSON, PARITY.BIN, recovery instructions, VERIFY.PS1, and verifies intact.
        Assert.True(File.Exists(Path.Combine(disc4, ArchiveParitySet.ParitySetFileName)));
        Assert.True(File.Exists(Path.Combine(disc4, ArchiveParitySet.ParityPayloadFileName)));
        Assert.True(File.Exists(Path.Combine(disc4, ArchiveManifest.RecoveryReadmeFileName)));
        Assert.True(File.Exists(Path.Combine(disc4, ArchiveManifest.VerifyPowerShellFileName)));
        var verifyParity = await ArchiveManifest.VerifyAsync(disc4);
        Assert.True(verifyParity.IsIntact);

        // Capture expected file hashes on Disc 2 before deleting it completely.
        var disc2Expected = plan.Discs[1].Files.ToDictionary(
            f => f.RelativePath,
            f => sourceHashes[f.RelativePath],
            StringComparer.Ordinal);
        Assert.NotEmpty(disc2Expected);

        // Completely delete Disc 2!
        Directory.Delete(disc2, recursive: true);
        Assert.False(Directory.Exists(disc2));

        string rebuiltDisc2 = Path.Combine(_root, "rebuilt_disc02");
        var report = await ArchiveRecoveryService.RebuildMissingDiscAsync([disc1, disc3, disc4], 2, rebuiltDisc2);

        Assert.True(report.Success, report.Problem);
        Assert.False(report.IsParityDisc);
        Assert.Equal(plan.SetId, report.SetId);
        Assert.Equal(2, report.RebuiltDiscNumber);
        Assert.Equal(disc2Expected.Count, report.FilesRestored);
        Assert.Empty(report.FailedFiles);
        Assert.Equal([1, 3, 4], report.SourceDiscNumbersUsed);

        foreach (var (rel, expectedHash) in disc2Expected)
        {
            string restoredFile = Path.Combine(rebuiltDisc2, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(restoredFile), $"Missing restored file: {rel}");
            byte[] actualBytes = await File.ReadAllBytesAsync(restoredFile);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(actualBytes));
            Assert.Equal(expectedHash, actualHash);
        }

        // Rebuilt Disc 2 also verifies cleanly with ArchiveManifest.VerifyAsync!
        var verifyRebuilt = await ArchiveManifest.VerifyAsync(rebuiltDisc2);
        Assert.True(verifyRebuilt.IsIntact);
        Assert.Equal(disc2Expected.Count + 2, verifyRebuilt.FilesChecked);

        // Suppress unused warning on srcDir/stagingDir
        Assert.True(Directory.Exists(srcDir));
        Assert.True(Directory.Exists(stagingDir));
    }

    [Fact]
    public async Task StreamingE2E_ThreeDataPlusTwoDualParity_RebuildsDeletedDisc2_MultipleDiscs_CorruptSurvivor_AndParityDisc()
    {
        var (_, _, plan, staged, sourceHashes) = await BuildAndStageSpanAsync("e2e_3p2", parityDiscCount: 2);

        Assert.Equal(3, plan.Discs.Count);
        Assert.Equal(2, plan.ParityDiscCount);
        Assert.Equal(5, staged.Count);

        string disc1 = staged[0].DiscRoot;
        string disc2 = staged[1].DiscRoot;
        string disc3 = staged[2].DiscRoot;
        string disc4 = staged[3].DiscRoot;
        string disc5 = staged[4].DiscRoot;

        byte[] origParity4 = await File.ReadAllBytesAsync(Path.Combine(disc4, ArchiveParitySet.ParityPayloadFileName));

        // 1. Delete Disc 2 completely and rebuild from remaining discs + dual parity.
        Directory.Delete(disc2, recursive: true);
        string outSingle = Path.Combine(_root, "rebuilt_3p2_single");
        var rSingle = await ArchiveRecoveryService.RebuildMissingDiscAsync([disc1, disc3, disc4, disc5], 2, outSingle);
        Assert.True(rSingle.Success, rSingle.Problem);
        AssertFilesMatchExpected(outSingle, plan.Discs[1].Files, sourceHashes);

        // 2. Delete BOTH Disc 2 and Disc 3 completely, rebuild both simultaneously using Disc 1 + Parity 4 + Parity 5!
        Directory.Delete(disc3, recursive: true);
        string outBoth2 = Path.Combine(_root, "rebuilt_3p2_both2");
        string outBoth3 = Path.Combine(_root, "rebuilt_3p2_both3");
        var rBoth = await ArchiveRecoveryService.RebuildMissingDiscsAsync(
            [disc1, disc4, disc5],
            new Dictionary<int, string> { [2] = outBoth2, [3] = outBoth3 });
        Assert.Equal(2, rBoth.Count);
        Assert.All(rBoth, r => Assert.True(r.Success, r.Problem));
        AssertFilesMatchExpected(outBoth2, plan.Discs[1].Files, sourceHashes);
        AssertFilesMatchExpected(outBoth3, plan.Discs[2].Files, sourceHashes);

        // 3. Silently corrupt a file on surviving Disc 1 while Disc 2 is missing -> detects Disc 1 mismatch and recovers Disc 2 via [outBoth3, disc4, disc5]!
        string disc1Copy = Path.Combine(_root, "disc01_tampered");
        CopyDirectory(disc1, disc1Copy);
        string firstNonEmptyOnDisc1 = plan.Discs[0].Files.First(f => f.SizeBytes > 0).RelativePath;
        string corruptTarget = Path.Combine(disc1Copy, firstNonEmptyOnDisc1.Replace('/', Path.DirectorySeparatorChar));
        byte[] tampered = await File.ReadAllBytesAsync(corruptTarget);
        tampered[0] ^= 0x5A;
        await File.WriteAllBytesAsync(corruptTarget, tampered);

        string outWithCorruptSurvivor = Path.Combine(_root, "rebuilt_3p2_corrupt_survivor");
        var rCorruptSurvivor = await ArchiveRecoveryService.RebuildMissingDiscAsync([disc1Copy, outBoth3, disc4, disc5], 2, outWithCorruptSurvivor);
        Assert.True(rCorruptSurvivor.Success, rCorruptSurvivor.Problem);
        Assert.DoesNotContain(1, rCorruptSurvivor.SourceDiscNumbersUsed);
        AssertFilesMatchExpected(outWithCorruptSurvivor, plan.Discs[1].Files, sourceHashes);

        // 4. Delete Parity Disc 4 and rebuild Disc 4 from the 3 data discs.
        Directory.Delete(disc4, recursive: true);
        string outParity4 = Path.Combine(_root, "rebuilt_disc04_parity");
        var rParity = await ArchiveRecoveryService.RebuildMissingDiscAsync([disc1, outBoth2, outBoth3, disc5], 4, outParity4);
        Assert.True(rParity.Success, rParity.Problem);
        Assert.True(rParity.IsParityDisc);
        Assert.Equal(
            SHA256.HashData(origParity4),
            SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(outParity4, ArchiveParitySet.ParityPayloadFileName))));
        Assert.True((await ArchiveManifest.VerifyAsync(outParity4)).IsIntact);
    }

    [Fact]
    public async Task VerifyPs1_OnParityDisc_PassesWhenIntact_AndFailsWhenParitySetOrPayloadTampered()
    {
        var (_, _, _, staged, _) = await BuildAndStageSpanAsync("ps1_parity", parityDiscCount: 1);
        string parityDisc = staged[3].DiscRoot;
        string ps1Path = Path.Combine(parityDisc, ArchiveManifest.VerifyPowerShellFileName);

        Assert.Equal(0, await RunPowerShellVerifyAsync(ps1Path));

        // Tamper PARITY.BIN -> VERIFY.PS1 fails.
        string payloadPath = Path.Combine(parityDisc, ArchiveParitySet.ParityPayloadFileName);
        byte[] origPayload = await File.ReadAllBytesAsync(payloadPath);
        byte[] badPayload = (byte[])origPayload.Clone();
        badPayload[0] ^= 0xFF;
        await File.WriteAllBytesAsync(payloadPath, badPayload);
        Assert.NotEqual(0, await RunPowerShellVerifyAsync(ps1Path));

        // Restore payload, tamper PARITY-SET.JSON -> VERIFY.PS1 fails.
        await File.WriteAllBytesAsync(payloadPath, origPayload);
        Assert.Equal(0, await RunPowerShellVerifyAsync(ps1Path));
        string paritySetPath = Path.Combine(parityDisc, ArchiveParitySet.ParitySetFileName);
        string psJson = await File.ReadAllTextAsync(paritySetPath);
        await File.WriteAllTextAsync(paritySetPath, psJson.Replace("\"dataDiscCount\": 3", "\"dataDiscCount\": 99"));
        Assert.NotEqual(0, await RunPowerShellVerifyAsync(ps1Path));
    }

    [Fact]
    public async Task RebuildMissingDisc_FromIsoImages_ReconstructsDeletedDisc()
    {
        var (_, _, plan, staged, sourceHashes) = await BuildAndStageSpanAsync("iso_rebuild", parityDiscCount: 1);

        string iso1 = BuildIsoFromStaged(staged[0].DiscRoot, plan.SetId, 1, "disc01.iso");
        string iso3 = BuildIsoFromStaged(staged[2].DiscRoot, plan.SetId, 3, "disc03.iso");
        string iso4 = BuildIsoFromStaged(staged[3].DiscRoot, plan.SetId, 4, "disc04.iso");

        Directory.Delete(staged[1].DiscRoot, recursive: true);

        string outDir = Path.Combine(_root, "rebuilt_from_iso");
        var report = await ArchiveRecoveryService.RebuildMissingDiscAsync([iso1, iso3, iso4], 2, outDir);

        Assert.True(report.Success, report.Problem);
        AssertFilesMatchExpected(outDir, plan.Discs[1].Files, sourceHashes);
    }

    [Fact]
    public async Task RebuildMissingDisc_TooManyDiscsLost_FailsCleanly()
    {
        var (_, _, _, staged, _) = await BuildAndStageSpanAsync("too_many_lost", parityDiscCount: 1);

        Directory.Delete(staged[1].DiscRoot, recursive: true);
        Directory.Delete(staged[2].DiscRoot, recursive: true);

        string outDir = Path.Combine(_root, "rebuilt_impossible");
        var report = await ArchiveRecoveryService.RebuildMissingDiscAsync([staged[0].DiscRoot, staged[3].DiscRoot], 2, outDir);

        Assert.False(report.Success);
        Assert.NotNull(report.Problem);
        Assert.Contains("unavailable", report.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string SrcDir, string StagingDir, ArchivePlan Plan, IReadOnlyList<ArchiveStagedDisc> Staged, Dictionary<string, string> Hashes)>
        BuildAndStageSpanAsync(string name, int parityDiscCount)
    {
        string srcDir = Path.Combine(_root, name + "_src");
        string stagingDir = Path.Combine(_root, name + "_stage");
        Directory.CreateDirectory(srcDir);

        var rng = new Random(2026_11_24 + parityDiscCount);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        // Create 6 files spanning 3 discs under ceiling=300 sectors (~600 KB per disc), with multi-stripe files (>64 KiB),
        // non-sector-aligned sizes, nested subfolders, and a 0-byte empty file.
        var specs = new (string Rel, int Size)[]
        {
            ("alpha/big_a.bin", 180_123),
            ("alpha/tiny_a.txt", 317),
            ("beta/big_b.bin", 195_777),
            ("beta/empty.dat", 0),
            ("gamma/big_c.bin", 172_555),
            ("gamma/sub/odd.bin", 67_001)
        };

        foreach (var (rel, size) in specs)
        {
            string full = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            byte[] data = new byte[size];
            if (size > 0)
            {
                rng.NextBytes(data);
            }
            await File.WriteAllBytesAsync(full, data);
            hashes[rel] = Convert.ToHexStringLower(SHA256.HashData(data));
        }

        // Ceiling 140 sectors (~280 KB) per disc -> after catalog+parity metadata (~30 sectors), ~110 sectors (~220 KB) usable per disc:
        // alpha (180 KB + 317 B) -> Disc 1, beta (195 KB + 0 B) -> Disc 2, gamma (172 KB + 67 KB = 239 KB? Wait: 172+67=239 > 220 -> check!)
        // Let's use ceiling 165 sectors (~330 KB) -> packCapacity ~135 sectors (~270 KB):
        // beta = 96+0=96, alpha = 88+1=89, gamma = 85+33=118 -> each > half of 135, so 3 discs!
        var plan = ArchiveSpanPlanner.Plan(
            srcDir,
            DiscMediaType.CdR,
            redundancyPercent: 0,
            setId: "xp" + name[..Math.Min(4, name.Length)],
            fillFreeSpace: false,
            ceilingSectorsOverride: 165,
            packing: ArchivePackingMode.FirstFitDecreasing,
            parityDiscCount: parityDiscCount);

        var staged = await ArchiveStager.StageDiscsAsync(plan, stagingDir, par2Path: null);
        return (srcDir, stagingDir, plan, staged, hashes);
    }

    private string BuildIsoFromStaged(string discRoot, string setId, int discNumber, string fileName)
    {
        string isoPath = Path.Combine(_root, fileName);
        var root = CompilationFolderImporter.ImportAsRoot(discRoot, strict: true);
        IsoUdfImageBuilder.BuildToFile(root, isoPath, new IsoBuildOptions(
            VolumeLabel: ArchiveManifest.VolumeLabel(setId, discNumber),
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true));
        return isoPath;
    }

    private static void AssertFilesMatchExpected(
        string restoredRoot,
        IReadOnlyList<ArchiveSourceFile> expectedFiles,
        IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var file in expectedFiles)
        {
            string full = Path.Combine(restoredRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"Restored file missing: {file.RelativePath}");
            byte[] actual = File.ReadAllBytes(full);
            Assert.Equal(file.SizeBytes, actual.LongLength);
            Assert.Equal(hashes[file.RelativePath], Convert.ToHexStringLower(SHA256.HashData(actual)));
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            File.Copy(file, Path.Combine(dst, rel), overwrite: true);
        }
    }

    private static async Task<int> RunPowerShellVerifyAsync(string ps1Path)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ps1Path);

        using var proc = Process.Start(psi)!;
        _ = await proc.StandardOutput.ReadToEndAsync();
        _ = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
