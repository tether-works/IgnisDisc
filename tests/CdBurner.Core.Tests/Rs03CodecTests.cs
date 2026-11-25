using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Archival.Rs03;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Tests;

public sealed class Rs03CodecTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignis_rs03_core_" + Guid.NewGuid().ToString("N")[..8]);

    public Rs03CodecTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void EccHeader_And_CrcBlock_MatchPrimaryBinarySpecOffsets()
    {
        byte[] fp = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] sum = Enumerable.Range(21, 16).Select(i => (byte)i).ToArray();
        var header = new Rs03EccHeader
        {
            MethodFlags = [Rs03Constants.MethodFlagDataMd5 | Rs03Constants.MethodFlagEccFile, 0, 0, 0],
            MediumFingerprint = fp,
            MediumSum = sum,
            DataSectors = 1234,
            DataBytes = 223,
            EccBytes = 32,
            InLastBytes = 1500,
            SectorsPerLayer = 6
        };

        byte[] rawHdr = header.ToBytes();
        Assert.Equal(4096, rawHdr.Length);
        Assert.Equal("*dvdisaster*", Encoding.ASCII.GetString(rawHdr, 0, 12));
        Assert.Equal("RS03", Encoding.ASCII.GetString(rawHdr, 12, 4));
        Assert.Equal(3, rawHdr[16]);
        Assert.Equal(1234UL, BinaryPrimitives.ReadUInt64LittleEndian(rawHdr.AsSpan(68, 8)));
        Assert.Equal(223, BinaryPrimitives.ReadInt32LittleEndian(rawHdr.AsSpan(76, 4)));
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(rawHdr.AsSpan(80, 4)));
        Assert.Equal(7900, BinaryPrimitives.ReadInt32LittleEndian(rawHdr.AsSpan(88, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(rawHdr.AsSpan(92, 4)));
        Assert.Equal(1500, BinaryPrimitives.ReadInt32LittleEndian(rawHdr.AsSpan(116, 4)));
        Assert.Equal(6UL, BinaryPrimitives.ReadUInt64LittleEndian(rawHdr.AsSpan(120, 8)));

        // Verify selfCRC uses seed 0x004C5047 ("LPG\0") at offset 96 with uncomplemented IEEE CRC32.
        byte[] crcCheck = (byte[])rawHdr.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(crcCheck.AsSpan(96, 4), 0x004C5047u);
        Assert.Equal(Crc32Algorithm.ComputeUncomplemented(crcCheck), BinaryPrimitives.ReadUInt32LittleEndian(rawHdr.AsSpan(96, 4)));
        Assert.True(Rs03EccHeader.TryParse(rawHdr, out var parsedHdr));
        Assert.NotNull(parsedHdr);
        Assert.Equal(1234, parsedHdr!.DataSectors);
        Assert.Equal(6, parsedHdr.SectorsPerLayer);

        // Verify CrcBlock offsets including 4-byte alignment padding at 1108..1111 before SectorsPerLayer (1112).
        var cb = new Rs03CrcBlock
        {
            MethodFlags = [Rs03Constants.MethodFlagDataMd5, 0, 0, 0],
            MediumFingerprint = fp,
            MediumSum = sum,
            DataSectors = 1234,
            InLastBytes = 2048,
            DataBytes = 223,
            EccBytes = 32,
            SectorsPerLayer = 12
        };
        cb.Crc[0] = 0x11223344u;
        cb.Crc[221] = 0xAABBCCDDu;

        byte[] rawCb = new byte[2048];
        cb.WriteTo(rawCb);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(rawCb.AsSpan(0, 4)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(rawCb.AsSpan(221 * 4, 4)));
        Assert.Equal("*dvdisaster*", Encoding.ASCII.GetString(rawCb, 1024, 12));
        Assert.Equal("RS03", Encoding.ASCII.GetString(rawCb, 1036, 4));
        Assert.Equal(1234UL, BinaryPrimitives.ReadUInt64LittleEndian(rawCb.AsSpan(1088, 8)));
        Assert.Equal(2048, BinaryPrimitives.ReadInt32LittleEndian(rawCb.AsSpan(1096, 4)));
        Assert.Equal(223, BinaryPrimitives.ReadInt32LittleEndian(rawCb.AsSpan(1100, 4)));
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(rawCb.AsSpan(1104, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(rawCb.AsSpan(1108, 4))); // 8-byte alignment gap
        Assert.Equal(12UL, BinaryPrimitives.ReadUInt64LittleEndian(rawCb.AsSpan(1112, 8)));

        byte[] cbCrcCheck = (byte[])rawCb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(cbCrcCheck.AsSpan(1120, 4), 0x004C5047u);
        Assert.Equal(Crc32Algorithm.ComputeUncomplemented(cbCrcCheck), BinaryPrimitives.ReadUInt32LittleEndian(rawCb.AsSpan(1120, 4)));
        Assert.True(Rs03CrcBlock.TryParse(rawCb, out var parsedCb));
        Assert.NotNull(parsedCb);
        Assert.Equal(12, parsedCb!.SectorsPerLayer);
        Assert.Equal(0xAABBCCDDu, parsedCb.Crc[221]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(170)]
    public void ReedSolomon_EncodesZeroSyndromes_AndRecoversMaxErasuresAndUnknownErrors(int nroots)
    {
        var rs = new Rs03ReedSolomon(nroots);
        int ndata = rs.DataSymbolCount;
        var rng = new Random(1000 + nroots);

        byte[][] allLayers = new byte[255][];
        for (int i = 0; i < 255; i++)
        {
            allLayers[i] = new byte[2048];
        }
        for (int i = 0; i < ndata; i++)
        {
            rng.NextBytes(allLayers[i]);
        }

        byte[][] original = allLayers.Select(b => (byte[])b.Clone()).ToArray();
        rs.EncodeLayers(allLayers.AsSpan(0, ndata), allLayers.AsSpan(ndata, nroots), 2048);
        for (int i = ndata; i < 255; i++)
        {
            original[i] = (byte[])allLayers[i].Clone();
        }

        byte[] syn = new byte[nroots * 2048];
        rs.ComputeSyndromes(allLayers, 0, 2048, syn);
        Assert.All(syn, b => Assert.Equal(0, b));

        // Erase exactly nroots layers across data, CRC, and parity.
        int[] erasures = Enumerable.Range(0, nroots).Select(i => (i * 3 + 1) % 255).Distinct().ToArray();
        foreach (int e in erasures)
        {
            Array.Clear(allLayers[e]);
        }

        bool ok = rs.TryDecodeColumn(allLayers, 0, 2048, erasures, out int unexpected);
        Assert.True(ok);
        Assert.Equal(0, unexpected);
        for (int i = 0; i < 255; i++)
        {
            Assert.Equal(original[i], allLayers[i]);
        }

        // Also verify unknown error correction (without erasure positions) up to nroots / 2.
        int unknownCount = Math.Min(nroots / 2, 4);
        for (int i = 0; i < unknownCount; i++)
        {
            int targetLayer = (i * 19 + 5) % 255;
            allLayers[targetLayer][i * 17] ^= 0xA5;
        }
        bool okUnknown = rs.TryDecodeColumn(allLayers, 0, 2048, ReadOnlySpan<int>.Empty, out int foundUnexpected);
        Assert.True(okUnknown);
        Assert.Equal(unknownCount, foundUnexpected);
        for (int i = 0; i < 255; i++)
        {
            Assert.Equal(original[i], allLayers[i]);
        }
    }

    [Fact]
    public async Task EccFileMode_ReconstructsBitIdenticalIso_AfterWipingLba0To64_AndScatteredBursts_AndCorruptedEccHeader()
    {
        string isoPath = BuildSampleIso("ecc_test.iso", fileCount: 18, fileSize: 28_000);
        byte[] originalIsoHash = SHA256.HashData(File.ReadAllBytes(isoPath));

        var create = await Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.EccFile,
            ParityRoots = 32,
            Overwrite = true
        });
        Assert.True(create.Success);
        Assert.NotNull(create.EccFilePath);
        byte[] originalEccHash = SHA256.HashData(File.ReadAllBytes(create.EccFilePath!));

        var cleanVerify = await Rs03Codec.VerifyAsync(isoPath, create.EccFilePath);
        Assert.True(cleanVerify.IsValid);
        Assert.True(cleanVerify.DataMd5Matches);

        // Wipe LBA 0..64 (ISO/UDF volume descriptors + root directory) + scattered multi-track bursts in image + ECC header + some CRC/ECC sectors.
        int dataSectors = (int)create.Layout.DataSectors;
        using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ZeroSectors(fs, 0, Math.Min(65, dataSectors));
            for (int lba = 80; lba < dataSectors; lba += 95)
            {
                ZeroSectors(fs, lba, Math.Min(3, dataSectors - lba));
            }
        }
        using (var eccFs = new FileStream(create.EccFilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ZeroSectors(eccFs, 0, 2); // Wipe EccHeader in .ecc file
            ZeroSectors(eccFs, 2, 1); // Wipe first CrcBlock
        }

        var damagedVerify = await Rs03Codec.VerifyAsync(isoPath, create.EccFilePath);
        Assert.False(damagedVerify.IsValid);
        Assert.True(damagedVerify.HeaderReconstructed);
        Assert.True(damagedVerify.IsRecoverable);

        var repair = await Rs03Codec.RepairAsync(isoPath, create.EccFilePath);
        Assert.True(repair.Success);
        Assert.True(repair.HeaderReconstructed);
        Assert.True(repair.VerifiedAfterRepair);
        Assert.True(repair.RepairedDataSectors >= 5);
        Assert.Equal(originalIsoHash, SHA256.HashData(File.ReadAllBytes(isoPath)));
        Assert.Equal(originalEccHash, SHA256.HashData(File.ReadAllBytes(create.EccFilePath!)));
    }

    [Fact]
    public async Task AugmentedMode_ReconstructsBitIdenticalIso_AfterWipingLba0To64_Anchor256_EccHeader_AndBursts()
    {
        string isoPath = BuildSampleIso("aug_test.iso", fileCount: 16, fileSize: 32_000);
        byte[] originalPrefix = File.ReadAllBytes(isoPath);
        byte[] originalPrefixHash = SHA256.HashData(originalPrefix);
        long origSectors = originalPrefix.Length / 2048;

        // Custom write-once capacity ~15,300 sectors (60 sectors/layer * 255 = 15,300) keeps unit test fast.
        const long mediumCap = 60 * 255;
        var create = await Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.AugmentedImage,
            MediumCapacitySectors = mediumCap,
            MediaType = DiscMediaType.CdR,
            Overwrite = true
        });
        Assert.True(create.Success);
        Assert.Null(create.Warning);
        Assert.Equal(mediumCap * 2048, new FileInfo(isoPath).Length);
        byte[] augmentedHash = SHA256.HashData(File.ReadAllBytes(isoPath));

        // Wipe LBA 0..64, UDF anchor at 256 (if within image), EccHeader at ehSector, plus scattered multi-track bursts.
        using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ZeroSectors(fs, 0, Math.Min(65, (int)origSectors));
            if (256 < origSectors)
            {
                ZeroSectors(fs, 256, 1);
            }
            ZeroSectors(fs, (int)create.Layout.EccHeaderSector, 2);
            for (long lba = create.Layout.FirstCrcSector; lba < create.Layout.TotalVirtualSectors; lba += 180)
            {
                ZeroSectors(fs, (int)lba, 2);
            }
        }

        var damagedVerify = await Rs03Codec.VerifyAsync(isoPath);
        Assert.False(damagedVerify.IsValid);
        Assert.True(damagedVerify.HeaderReconstructed);
        Assert.True(damagedVerify.IsRecoverable);

        var repair = await Rs03Codec.RepairAsync(isoPath);
        Assert.True(repair.Success);
        Assert.True(repair.VerifiedAfterRepair);
        Assert.True(repair.RepairedDataSectors >= 65);

        byte[] repairedAll = File.ReadAllBytes(isoPath);
        Assert.Equal(augmentedHash, SHA256.HashData(repairedAll));
        Assert.Equal(originalPrefixHash, SHA256.HashData(repairedAll.AsSpan(0, originalPrefix.Length)));
    }

    [Theory]
    [InlineData(DiscMediaType.CdRw)]
    [InlineData(DiscMediaType.DvdMinusRw)]
    [InlineData(DiscMediaType.DvdPlusRw)]
    [InlineData(DiscMediaType.DvdRam)]
    [InlineData(DiscMediaType.BdRe)]
    public async Task RewritableMediaGuard_BlocksAugmentedModeUnlessExplicitlyAllowed_WhileAllowingEccFile(DiscMediaType rewritable)
    {
        var eccGuard = Rs03MediaGuard.Evaluate(rewritable, Rs03Mode.EccFile);
        Assert.True(eccGuard.IsAllowed);
        Assert.Null(eccGuard.WarningMessage);

        var blockedGuard = Rs03MediaGuard.Evaluate(rewritable, Rs03Mode.AugmentedImage, allowRewritableAugmented: false);
        Assert.False(blockedGuard.IsAllowed);
        Assert.True(blockedGuard.IsRewritableMedia);
        Assert.NotNull(blockedGuard.BlockingReason);

        string isoPath = BuildSampleIso($"rw_guard_{rewritable}.iso", fileCount: 4, fileSize: 4096);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.AugmentedImage,
            MediaType = rewritable,
            MediumCapacitySectors = 60 * 255,
            AllowRewritableAugmented = false
        }));
        Assert.Contains("rewritable", ex.Message, StringComparison.OrdinalIgnoreCase);

        var allowed = await Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.AugmentedImage,
            MediaType = rewritable,
            MediumCapacitySectors = 60 * 255,
            AllowRewritableAugmented = true
        });
        Assert.True(allowed.Success);
        Assert.NotNull(allowed.Warning);
    }

    [Fact]
    public async Task RecordedVolumeExtent_ExpandsForValidAugmentedRs03_AndIgnoresStaleOverwrittenTrailer()
    {
        string isoPath = BuildSampleIso("extent_rs03.iso", fileCount: 5, fileSize: 8192);
        long origSectors = new FileInfo(isoPath).Length / 2048;
        const long mediumCap = 60 * 255;

        await Rs03Codec.CreateAsync(isoPath, new Rs03CreateOptions
        {
            Mode = Rs03Mode.AugmentedImage,
            MediaType = DiscMediaType.DvdPlusR,
            MediumCapacitySectors = mediumCap,
            Overwrite = true
        });

        byte[] augBytes = File.ReadAllBytes(isoPath);
        int? totalDetected = RecordedVolumeExtent.FindEndSector(lba =>
            lba < 0 || (lba + 1L) * 2048 > augBytes.Length ? null : augBytes.AsSpan(lba * 2048, 2048).ToArray());
        Assert.Equal((int)mediumCap, totalDetected);
        Assert.Equal((int)origSectors, RecordedVolumeExtent.FindFileSystemEndSector(lba =>
            lba < 0 || (lba + 1L) * 2048 > augBytes.Length ? null : augBytes.AsSpan(lba * 2048, 2048).ToArray()));

        // Simulate overwriting the start of a DVD+RW with a smaller plain ISO: stale RS03 layers further out must NOT expand extent.
        string smallerIso = BuildSampleIso("smaller_plain.iso", fileCount: 2, fileSize: 2048);
        byte[] smallBytes = File.ReadAllBytes(smallerIso);
        int smallSectors = smallBytes.Length / 2048;
        Assert.True(smallSectors < origSectors);

        byte[] reusedDisc = (byte[])augBytes.Clone();
        smallBytes.CopyTo(reusedDisc, 0);

        int? reusedDetected = RecordedVolumeExtent.FindEndSector(lba =>
            lba < 0 || (lba + 1L) * 2048 > reusedDisc.Length ? null : reusedDisc.AsSpan(lba * 2048, 2048).ToArray());
        Assert.Equal(smallSectors, reusedDetected);
    }

    private string BuildSampleIso(string fileName, int fileCount, int fileSize)
    {
        string srcDir = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(fileName) + "_src");
        Directory.CreateDirectory(srcDir);
        var comp = new DataCompilation("RS03_TEST");
        var rng = new Random(42 + fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            string f = Path.Combine(srcDir, $"doc_{i:D2}.bin");
            byte[] data = new byte[fileSize + i * 137];
            rng.NextBytes(data);
            File.WriteAllBytes(f, data);
            comp.AddFile($"/doc_{i:D2}.bin", f, data.Length);
        }

        string isoPath = Path.Combine(_tempDir, fileName);
        IsoUdfImageBuilder.BuildToFile(comp.Root, isoPath, new IsoBuildOptions(
            VolumeLabel: "RS03_TEST", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true));
        return isoPath;
    }

    private static void ZeroSectors(FileStream fs, int startLba, int count)
    {
        if (count <= 0) return;
        byte[] zeros = new byte[count * 2048];
        fs.Position = (long)startLba * 2048;
        fs.Write(zeros, 0, zeros.Length);
    }
}
