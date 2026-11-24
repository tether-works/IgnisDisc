using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class Ecma119AndUdfConformanceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_ecma119_" + Guid.NewGuid().ToString("N"));

    public Ecma119AndUdfConformanceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static List<(string Ident, byte Flags, uint ExtentLba, uint DataLen)> ReadDirectoryRecords(
        byte[] image, uint dirLba, uint dirLength, bool isJoliet)
    {
        var result = new List<(string, byte, uint, uint)>();
        int baseOffset = checked((int)(dirLba * 2048));
        int pos = 0;

        while (pos < dirLength)
        {
            int sectorPos = pos % 2048;
            byte recLen = image[baseOffset + pos];
            if (recLen == 0)
            {
                pos += 2048 - sectorPos;
                continue;
            }

            ReadOnlySpan<byte> rec = image.AsSpan(baseOffset + pos, recLen);
            uint extentLba = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4));
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));
            byte flags = rec[25];
            byte identLen = rec[32];
            ReadOnlySpan<byte> identBytes = rec.Slice(33, identLen);

            string ident = identLen == 1 && identBytes[0] == 0x00 ? "." :
                identLen == 1 && identBytes[0] == 0x01 ? ".." :
                isJoliet ? Encoding.BigEndianUnicode.GetString(identBytes) : Encoding.ASCII.GetString(identBytes);

            result.Add((ident, flags, extentLba, dataLen));
            pos += recLen;
        }

        return result;
    }

    private static List<(ushort ParentDirNumber, uint ExtentLba, string Name)> ReadPathTable(
        byte[] image, uint tableLba, uint tableBytesLength, bool isJoliet, bool bigEndian)
    {
        var entries = new List<(ushort, uint, string)>();
        int baseOffset = checked((int)(tableLba * 2048));
        int offset = 0;

        while (offset < tableBytesLength)
        {
            byte lenDi = image[baseOffset + offset];
            uint extentLba = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(baseOffset + offset + 2, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(baseOffset + offset + 2, 4));
            ushort parent = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(baseOffset + offset + 6, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(baseOffset + offset + 6, 2));

            ReadOnlySpan<byte> idSpan = image.AsSpan(baseOffset + offset + 8, lenDi);
            string name = entries.Count == 0 && lenDi == 1 && idSpan[0] == 0
                ? string.Empty
                : isJoliet ? Encoding.BigEndianUnicode.GetString(idSpan) : Encoding.ASCII.GetString(idSpan);

            entries.Add((parent, extentLba, name));
            offset += 8 + lenDi + (lenDi % 2);
        }

        return entries;
    }

    [Fact]
    public void DirectoryRecords_CombineDirsAndFilesInAscendingEncodedIdentifierOrder_InIsoAndJoliet()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("b_dir").AddFile("inner.txt", "1"u8.ToArray());
        root.AddDirectory("d_dir").AddFile("inner2.txt", "2"u8.ToArray());
        root.AddFile("a_file.txt", "a"u8.ToArray());
        root.AddFile("c_file.txt", "c"u8.ToArray());

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false)).ToArray();

        // ISO 9660 root directory at LBA 16
        uint isoRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 158, 4));
        uint isoRootLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 166, 4));
        var isoRecords = ReadDirectoryRecords(image, isoRootLba, isoRootLen, isJoliet: false);

        Assert.Equal([".", "..", "A_FILE.TXT;1", "B_DIR", "C_FILE.TXT;1", "D_DIR"],
            isoRecords.Select(r => r.Ident).ToArray());

        // Joliet root directory at LBA 17
        uint jolietRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * 2048 + 158, 4));
        uint jolietRootLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * 2048 + 166, 4));
        var jolietRecords = ReadDirectoryRecords(image, jolietRootLba, jolietRootLen, isJoliet: true);

        Assert.Equal([".", "..", "a_file.txt", "b_dir", "c_file.txt", "d_dir"],
            jolietRecords.Select(r => r.Ident).ToArray());
    }

    [Fact]
    public void PathTable_FollowsEncodedParentAndDirectoryIdentifierOrder_WhenIsoAndJolietOrdersDiffer()
    {
        // "ab" vs "a-z":
        // In ISO 9660: "AB" (0x41 0x42) < "A_Z" (0x41 0x5F) because 'B' (0x42) < '_' (0x5F).
        // In Joliet: "a-z" (0x0061 0x002D) < "ab" (0x0061 0x0062) because '-' (0x2D) < 'b' (0x62).
        var root = CompilationDirectory.CreateRoot();
        var dirAz = root.AddDirectory("a-z");
        dirAz.AddDirectory("child_az");
        var dirAb = root.AddDirectory("ab");
        dirAb.AddDirectory("child_ab");

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false)).ToArray();

        // Check ISO Path Table (Type L and Type M)
        uint isoPtSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 132, 4));
        uint isoPtL = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 140, 4));
        uint isoPtM = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16 * 2048 + 148, 4));

        var isoTableL = ReadPathTable(image, isoPtL, isoPtSize, isJoliet: false, bigEndian: false);
        var isoTableM = ReadPathTable(image, isoPtM, isoPtSize, isJoliet: false, bigEndian: true);
        Assert.Equal(isoTableL, isoTableM);
        Assert.Equal(["", "AB", "A_Z", "CHILD_AB", "CHILD_AZ"], isoTableL.Select(e => e.Name).ToArray());
        Assert.Equal([(ushort)1, (ushort)1, (ushort)1, (ushort)2, (ushort)3], isoTableL.Select(e => e.ParentDirNumber).ToArray());

        // Check Joliet Path Table (Type L and Type M)
        uint jPtSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * 2048 + 132, 4));
        uint jPtL = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * 2048 + 140, 4));
        uint jPtM = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(17 * 2048 + 148, 4));

        var jTableL = ReadPathTable(image, jPtL, jPtSize, isJoliet: true, bigEndian: false);
        var jTableM = ReadPathTable(image, jPtM, jPtSize, isJoliet: true, bigEndian: true);
        Assert.Equal(jTableL, jTableM);
        Assert.Equal(["", "a-z", "ab", "child_az", "child_ab"], jTableL.Select(e => e.Name).ToArray());
        Assert.Equal([(ushort)1, (ushort)1, (ushort)1, (ushort)2, (ushort)3], jTableL.Select(e => e.ParentDirNumber).ToArray());
    }

    [Fact]
    public void VideoTs_DirectoryRecordsAreAlphabetical_WhileFileExtentsPreserveDvdVideoLbaOrder()
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTs = root.AddDirectory("VIDEO_TS");
        videoTs.AddFile("VIDEO_TS.BUP", new byte[2048]);
        videoTs.AddFile("VIDEO_TS.IFO", new byte[2048]);
        videoTs.AddFile("VIDEO_TS.VOB", new byte[2048]);
        videoTs.AddFile("VTS_01_0.BUP", new byte[2048]);
        videoTs.AddFile("VTS_01_0.IFO", new byte[2048]);
        videoTs.AddFile("VTS_01_1.VOB", new byte[2048]);

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true)).ToArray();

        uint isoRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 158, 4));
        uint isoRootLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 166, 4));
        var rootEntry = ReadDirectoryRecords(image, isoRootLba, isoRootLen, isJoliet: false)
            .Single(e => e.Ident == "VIDEO_TS");

        var isoVideoTs = ReadDirectoryRecords(image, rootEntry.ExtentLba, rootEntry.DataLen, isJoliet: false);
        var fileIdents = isoVideoTs.Skip(2).Select(e => e.Ident).ToArray();
        Assert.Equal([
            "VIDEO_TS.BUP;1",
            "VIDEO_TS.IFO;1",
            "VIDEO_TS.VOB;1",
            "VTS_01_0.BUP;1",
            "VTS_01_0.IFO;1",
            "VTS_01_1.VOB;1"
        ], fileIdents);

        // Verify physical extent LBAs still follow DVD-Video playback/ECC order
        uint ifoLba = isoVideoTs.Single(e => e.Ident == "VIDEO_TS.IFO;1").ExtentLba;
        uint vobLba = isoVideoTs.Single(e => e.Ident == "VIDEO_TS.VOB;1").ExtentLba;
        uint bupLba = isoVideoTs.Single(e => e.Ident == "VIDEO_TS.BUP;1").ExtentLba;
        Assert.Equal(0u, ifoLba % OpticalConstants.DvdEccBlockSectors);
        Assert.True(ifoLba < vobLba);
        Assert.True(vobLba < bupLba);
        Assert.True(bupLba - (ifoLba + 1) >= OpticalConstants.DvdEccBlockSectors);

        uint vtsIfoLba = isoVideoTs.Single(e => e.Ident == "VTS_01_0.IFO;1").ExtentLba;
        uint vtsVobLba = isoVideoTs.Single(e => e.Ident == "VTS_01_1.VOB;1").ExtentLba;
        uint vtsBupLba = isoVideoTs.Single(e => e.Ident == "VTS_01_0.BUP;1").ExtentLba;
        Assert.True(bupLba < vtsIfoLba);
        Assert.True(vtsIfoLba < vtsVobLba);
        Assert.True(vtsVobLba < vtsBupLba);
        Assert.True(vtsBupLba - (vtsIfoLba + 1) >= OpticalConstants.DvdEccBlockSectors);
    }

    [Fact]
    public void Level1DirectoryCollisions_StayWithin8DCharacters_WithoutTilde()
    {
        var root = CompilationDirectory.CreateRoot();
        for (int i = 0; i < 12; i++)
        {
            root.AddDirectory($"LongDirectoryName_{i:D2}").AddFile("f.txt", [(byte)i]);
        }

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeIso9660: true, IncludeJoliet: false, IncludeUdf: false)).ToArray();

        uint isoRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 158, 4));
        uint isoRootLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 166, 4));
        var dirIdents = ReadDirectoryRecords(image, isoRootLba, isoRootLen, isJoliet: false)
            .Skip(2)
            .Select(e => e.Ident)
            .ToList();

        Assert.Equal(12, dirIdents.Count);
        Assert.Equal(12, dirIdents.Distinct(StringComparer.Ordinal).Count());
        foreach (string ident in dirIdents)
        {
            Assert.Matches(@"^[A-Z0-9_]{1,8}$", ident);
            Assert.DoesNotContain('~', ident);
        }

        uint ptSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 132, 4));
        uint ptL = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 140, 4));
        var pt = ReadPathTable(image, ptL, ptSize, isJoliet: false, bigEndian: false);
        Assert.Equal(dirIdents, pt.Skip(1).Select(e => e.Name).ToList());
    }

    [Fact]
    public void PathTable_ThrowsWhenParentDirNumberExceedsUInt16Max()
    {
        var root = CompilationDirectory.CreateRoot();
        CompilationDirectory? lastLeaf = null;
        for (int a = 0; a < 16; a++)
        {
            var l1 = root.AddDirectory($"A{a:D2}");
            for (int b = 0; b < 64; b++)
            {
                var l2 = l1.AddDirectory($"B{b:D2}");
                for (int c = 0; c < 64; c++)
                {
                    lastLeaf = l2.AddDirectory($"C{c:D2}");
                }
            }
        }
        // Total directories so far: 1 + 16 + 1024 + 65536 = 66577; lastLeaf has DirNumber 66577 > 65535.
        lastLeaf!.AddDirectory("OVERFLOW");

        var ex = Assert.Throws<NotSupportedException>(() =>
            IsoUdfImageBuilder.CalculateTotalSectors(root, new IsoBuildOptions(IncludeJoliet: false, IncludeUdf: false)));
        Assert.Contains("65535", ex.Message);
    }

    [Fact]
    public void JolietSvd_DoesNotSplitUtf16SurrogatePairsInVolumeLabelOrPublisher()
    {
        // 15 BMP chars + 1 surrogate pair (total length 17 UTF-16 code units, exceeds 16)
        string label = new string('A', 15) + "\U0001F600";
        // 63 BMP chars + 1 surrogate pair (total length 65 UTF-16 code units, exceeds 64)
        string publisher = new string('P', 63) + "\U0001F680";

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", "hi"u8.ToArray());

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(
                VolumeLabel: label,
                PublisherId: publisher,
                IncludeIso9660: true,
                IncludeJoliet: true,
                IncludeUdf: false)).ToArray();

        ReadOnlySpan<byte> svd = image.AsSpan(17 * 2048, 2048);
        string decodedLabel = Encoding.BigEndianUnicode.GetString(svd.Slice(40, 32));
        string decodedPub = Encoding.BigEndianUnicode.GetString(svd.Slice(318, 128));

        Assert.Equal(new string('A', 15) + " ", decodedLabel);
        Assert.DoesNotContain(decodedLabel, char.IsSurrogate);
        Assert.Equal(new string('P', 63) + " ", decodedPub);
        Assert.DoesNotContain(decodedPub, char.IsSurrogate);
    }

    [Fact]
    public void Timestamps_LocalConvertedToUtc_AndPre1980PreservedInUdf()
    {
        var localTime = new DateTime(2015, 6, 15, 14, 30, 40, DateTimeKind.Local);
        var expectedUtc = localTime.ToUniversalTime();
        var pre1980Utc = new DateTime(1975, 4, 20, 8, 9, 10, DateTimeKind.Utc);
        var fixedBuildUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var root = CompilationDirectory.CreateRoot();
        var localFile = root.AddFile("local.bin", "local"u8.ToArray())!;
        localFile.LastWriteTimeUtc = localTime;
        var oldFile = root.AddFile("old1975.bin", "1975"u8.ToArray())!;
        oldFile.LastWriteTimeUtc = pre1980Utc;

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(
                IncludeIso9660: true,
                IncludeJoliet: false,
                IncludeUdf: true,
                UdfRevision: 0x0102,
                FixedTimestampUtc: fixedBuildUtc)).ToArray();

        // Check ISO 9660 directory record recording timestamp (UTC)
        uint isoRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16 * 2048 + 158, 4));
        int rootOffset = (int)(isoRootLba * 2048);
        int pos = 0;
        DateTime? parsedIsoLocalFileUtc = null;
        while (pos < 2048 && image[rootOffset + pos] > 0)
        {
            byte len = image[rootOffset + pos];
            var rec = image.AsSpan(rootOffset + pos, len);
            string name = Encoding.ASCII.GetString(rec.Slice(33, rec[32]));
            if (name == "LOCAL.BIN;1")
            {
                parsedIsoLocalFileUtc = new DateTime(1900 + rec[18], rec[19], rec[20], rec[21], rec[22], rec[23], DateTimeKind.Utc);
            }
            pos += len;
        }
        Assert.Equal(expectedUtc, parsedIsoLocalFileUtc);

        // Check UDF File Entries for pre-1980 year (1975) and converted local UTC time
        var yearsFound = new List<(int Year, int Hour)>();
        for (int lba = 289; lba < image.Length / 2048; lba++)
        {
            var s = image.AsSpan(lba * 2048, 2048);
            if (BinaryPrimitives.ReadUInt16LittleEndian(s) == 0x0105 && s[27] == 5)
            {
                short year = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(84 + 2, 2)); // modification timestamp
                byte hour = s[84 + 6];
                yearsFound.Add((year, hour));
            }
        }
        Assert.Contains((1975, 8), yearsFound);
        Assert.Contains((expectedUtc.Year, expectedUtc.Hour), yearsFound);
    }

    [Fact]
    public void CompilationFolderImporter_PreservesTimestampsAndHiddenAttributes()
    {
        string subPath = Path.Combine(_tempDir, "HiddenSub");
        Directory.CreateDirectory(subPath);
        string filePath = Path.Combine(subPath, "secret.dat");
        File.WriteAllBytes(filePath, [1, 2, 3]);

        var fileTime = new DateTime(2019, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        var dirTime = new DateTime(2018, 2, 7, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, fileTime);
        File.SetAttributes(filePath, FileAttributes.Hidden | FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(subPath, dirTime);
        File.SetAttributes(subPath, FileAttributes.Hidden | FileAttributes.Directory);

        var root = CompilationFolderImporter.ImportAsRoot(_tempDir, strict: true);
        var importedSub = Assert.Single(root.Children.OfType<CompilationDirectory>());
        var importedFile = Assert.Single(importedSub.Children.OfType<CompilationFile>());

        Assert.Equal(dirTime, importedSub.LastWriteTimeUtc);
        Assert.True(importedSub.Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Equal(fileTime, importedFile.LastWriteTimeUtc);
        Assert.True(importedFile.Attributes.HasFlag(FileAttributes.Hidden));

        // Verify hidden flags propagate into ISO 9660 image
        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false));
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        var dirEntry = reader.FindDirectory("/HiddenSub");
        var fileEntry = reader.FindFile("/HiddenSub/secret.dat");
        Assert.NotNull(dirEntry);
        Assert.NotNull(fileEntry);
        Assert.True(dirEntry.IsHidden);
        Assert.True(fileEntry.IsHidden);
    }

    [Fact]
    public void CalculateTotalSectors_DoesNotMutateImportedStartLba()
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTs = root.AddDirectory("VIDEO_TS");
        var ifo = videoTs.AddFile("VIDEO_TS.IFO", new byte[2048])!;
        var bup = videoTs.AddFile("VIDEO_TS.BUP", new byte[2048])!;

        new DvdVideoLayoutPlanner().ApplyToCompilation(videoTs, startLba: 100);
        int ifoBefore = ifo.ImportedStartLba!.Value;
        int bupBefore = bup.ImportedStartLba!.Value;

        int total = IsoUdfImageBuilder.CalculateTotalSectors(root);
        Assert.True(total > 256);
        Assert.Equal(ifoBefore, ifo.ImportedStartLba);
        Assert.Equal(bupBefore, bup.ImportedStartLba);
    }

    [Fact]
    public void CompilationCapacityCalculator_ClampsLargeSectorSums_AndCountsUdfFileEntries()
    {
        // Two 3 TiB files: each ~1.61B sectors, sum ~3.22B > int.MaxValue (2.14B)
        var hugeFiles = new[]
        {
            new CompilationFile("part1.bin", 3L * 1024 * 1024 * 1024 * 1024),
            new CompilationFile("part2.bin", 3L * 1024 * 1024 * 1024 * 1024),
        };

        var assessment = CompilationCapacityCalculator.Calculate(hugeFiles, mediaType: DiscMediaType.BdxlRQuadLayer);
        Assert.Equal(int.MaxValue, assessment.TotalFileDataSectors);
        Assert.Equal(int.MaxValue, assessment.TotalSectors);
        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, assessment.Status);

        // UDF per-file FE overhead: 500 files add 500 FE sectors when includeUdf = true
        var manyFiles = Enumerable.Range(0, 500)
            .Select(i => new CompilationFile($"f{i}.txt", 1))
            .ToList();
        var withUdf = CompilationCapacityCalculator.Calculate(manyFiles, includeUdf: true);
        var withoutUdf = CompilationCapacityCalculator.Calculate(manyFiles, includeUdf: false);
        Assert.Equal(withoutUdf.FileSystemOverheadSectors + 32 + 500, withUdf.FileSystemOverheadSectors);
    }

    [Fact]
    public void ElTorito_LargeUefiImageOver32MiB_EmitsSectorCountZero_AndMoreThan31SectionsThrows()
    {
        // 33 MiB = 16,896 ISO sectors = 67,584 512-byte blocks (> 65,535)
        byte[] largeUefi = new byte[33 * 1024 * 1024];
        largeUefi[0] = 0xEB;
        largeUefi[^1] = 0x55;

        Assert.Equal((ushort)0, ElToritoCatalogBuilder.ComputeSectorCount(BootPlatformId.Efi, largeUefi.LongLength));
        Assert.Equal(ushort.MaxValue, ElToritoCatalogBuilder.ComputeSectorCount(BootPlatformId.X86Bios, largeUefi.LongLength));

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("README.TXT", "uefi"u8.ToArray());

        var bootConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                Platform: BootPlatformId.Efi,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 0,
                BootImageStreamFactory: () => new MemoryStream(largeUefi),
                Name: "bootx64.efi"));

        using var iso = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeUdf: false, BootConfiguration: bootConfig));
        var parsed = ElToritoCatalogInspector.ParseBootInfo(iso);
        Assert.Equal(BootPlatformId.Efi, parsed.DefaultEntry.Platform);
        Assert.Equal((ushort)0, parsed.DefaultEntry.SectorCount);

        // > 31 additional sections must fail validation
        var tooManySections = Enumerable.Range(0, 32)
            .Select(i => new BootCatalogEntrySpec(
                BootPlatformId.Efi,
                BootEmulationType.NoEmulation,
                0,
                4,
                () => new MemoryStream(new byte[2048]),
                $"sec{i}.efi"))
            .ToList();
        var invalidConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                BootPlatformId.X86Bios,
                BootEmulationType.NoEmulation,
                0,
                4,
                () => new MemoryStream(new byte[2048])),
            AdditionalSectionEntries: tooManySections);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElToritoCatalogBuilder.BuildBootCatalogSector(invalidConfig, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false, BootConfiguration: invalidConfig)));
    }
}
