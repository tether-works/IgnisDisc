using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class DiscImageBrowserServiceTests : IDisposable
{
    private static readonly DateTime FixedTime = new(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
    private readonly string _tempDir;
    private readonly DiscImageBrowserService _service = new();

    public DiscImageBrowserServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BrowserTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData(false, false, ".iso", "ISO 9660")]
    [InlineData(true, false, ".iso", "Joliet")]
    [InlineData(true, true, ".iso", "UDF")]
    [InlineData(false, true, ".udf", "UDF")]
    public async Task BrowseAndExtract_IsoJolietAndUdf_VerifiesMetadataAndByteExactSha256(
        bool includeJoliet,
        bool includeUdf,
        string extension,
        string expectedFs)
    {
        byte[] readmeBytes = "IgnisDisc browser verification text\r\n"u8.ToArray();
        byte[] binaryBytes = new byte[150_000];
        new Random(172).NextBytes(binaryBytes);

        var root = CompilationDirectory.CreateRoot();
        root.LastWriteTimeUtc = FixedTime;
        var docs = root.AddDirectory("Docs");
        docs.LastWriteTimeUtc = FixedTime;
        var readmeFile = docs.AddFile("README.TXT", readmeBytes)!;
        readmeFile.LastWriteTimeUtc = FixedTime;

        string longOrHiddenName = includeUdf
            ? "01 Long Unicode Archive Entry Name Exceeding Joliet Sixty Four Characters.bin"
            : "DATA_BIN.BIN";
        var sub = root.AddDirectory("Nested");
        sub.LastWriteTimeUtc = FixedTime;
        var dataFile = sub.AddFile(longOrHiddenName, binaryBytes)!;
        dataFile.LastWriteTimeUtc = FixedTime;
        dataFile.Attributes = FileAttributes.Hidden;
        var emptyDir = root.AddDirectory("EmptyDir");
        emptyDir.LastWriteTimeUtc = FixedTime;

        string imagePath = Path.Combine(_tempDir, $"sample{extension}");
        var buildOpts = new IsoBuildOptions(
            VolumeLabel: "BROWSE_TEST",
            IncludeIso9660: extension != ".udf",
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            UdfRevision: 0x0250,
            FixedTimestampUtc: FixedTime);
        IsoUdfImageBuilder.BuildToFile(root, imagePath, buildOpts);

        DiscImageBrowseResult browse = _service.Browse(imagePath);
        Assert.Equal(expectedFs, browse.ActiveFileSystem);
        Assert.Equal("BROWSE_TEST", browse.VolumeLabel);
        Assert.Equal(3, browse.DirectoryCount);
        Assert.Equal(2, browse.FileCount);

        string expectedNested = expectedFs == "ISO 9660" ? "NESTED" : "Nested";
        string expectedDocs = expectedFs == "ISO 9660" ? "DOCS" : "Docs";
        string expectedEmpty = expectedFs == "ISO 9660" ? "EMPTYDIR" : "EmptyDir";

        var dataEntry = Assert.Single(browse.Entries, e => !e.IsDirectory && e.Name == longOrHiddenName);
        Assert.Equal($"{expectedNested}/{longOrHiddenName}", dataEntry.RelativePath);
        Assert.Equal(binaryBytes.Length, dataEntry.SizeBytes);
        Assert.Equal(expectedFs, dataEntry.FileSystemSource);
        Assert.True(dataEntry.StartLba > 0);
        Assert.Equal(FixedTime, dataEntry.TimestampUtc);
        Assert.True(dataEntry.Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Contains("Hidden", dataEntry.FlagsSummary);

        string extractDir = Path.Combine(_tempDir, $"out_{expectedFs}_{extension.TrimStart('.')}");
        DiscImageExtractionResult extracted = await _service.ExtractAsync(
            imagePath,
            new DiscImageExtractionOptions(extractDir));

        Assert.Equal(2, extracted.ExtractedFileCount);
        Assert.True(Directory.Exists(Path.Combine(extractDir, expectedEmpty)));
        Assert.Equal(Sha256Hex(readmeBytes), Sha256File(Path.Combine(extractDir, expectedDocs, "README.TXT")));
        Assert.Equal(Sha256Hex(binaryBytes), Sha256File(Path.Combine(extractDir, expectedNested, longOrHiddenName)));
    }

    [Theory]
    [InlineData("nrg")]
    [InlineData("mds")]
    [InlineData("ccd")]
    [InlineData("cdi")]
    [InlineData("cue-mode1")]
    [InlineData("cue-mode2")]
    public async Task BrowseAndExtract_LegacyAndBinCueImages_VerifiesByteExactSha256(string format)
    {
        byte[] payload = new byte[48_000];
        new Random(format.GetHashCode(StringComparison.Ordinal)).NextBytes(payload);
        byte[] small = "legacy image check"u8.ToArray();

        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("Tracks").AddFile("DATA.BIN", payload)!.LastWriteTimeUtc = FixedTime;
        root.AddFile("NOTE.TXT", small)!.LastWriteTimeUtc = FixedTime;
        byte[] isoBytes = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(VolumeLabel: "LEGACY_TEST", IncludeJoliet: true, IncludeUdf: false, FixedTimestampUtc: FixedTime)).ToArray();

        string imagePath;
        switch (format)
        {
            case "nrg":
                imagePath = Path.Combine(_tempDir, "disc.nrg");
                LegacyDiscImageConverter.CreateNrgFile(imagePath, isoBytes);
                break;
            case "mds":
                imagePath = Path.Combine(_tempDir, "disc.mds");
                LegacyDiscImageConverter.CreateAlcoholMdsMdfImage(imagePath, WrapRaw2352Mode1(isoBytes), OpticalConstants.RawSectorSize, audio: false);
                break;
            case "ccd":
                imagePath = Path.Combine(_tempDir, "disc.ccd");
                LegacyDiscImageConverter.CreateCloneCdImage(imagePath, isoBytes);
                break;
            case "cdi":
                imagePath = Path.Combine(_tempDir, "disc.cdi");
                LegacyDiscImageConverter.CreateDiscJugglerFile(imagePath, WrapRaw2352Mode1(isoBytes), OpticalConstants.RawSectorSize, version: 3);
                break;
            case "cue-mode1":
            {
                string binPath = Path.Combine(_tempDir, "mode1.bin");
                imagePath = Path.Combine(_tempDir, "mode1.cue");
                File.WriteAllBytes(binPath, WrapRaw2352Mode1(isoBytes));
                await CueSheetWriter.WriteToFileAsync(
                    new CueSheet(Files: [new CueFileEntry("mode1.bin", "BINARY", [new CueTrack(1, CueTrackDataType.Mode1_2352, Indices: [new CueIndex(1, new MsfAddress(0, 0, 0))])])]),
                    imagePath);
                break;
            }
            case "cue-mode2":
            {
                string binPath = Path.Combine(_tempDir, "mode2.bin");
                imagePath = Path.Combine(_tempDir, "mode2.cue");
                File.WriteAllBytes(binPath, WrapRaw2352Mode2XaForm1(isoBytes));
                await CueSheetWriter.WriteToFileAsync(
                    new CueSheet(Files: [new CueFileEntry("mode2.bin", "BINARY", [new CueTrack(1, CueTrackDataType.Mode2_2352, Indices: [new CueIndex(1, new MsfAddress(0, 0, 0))])])]),
                    imagePath);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        DiscImageBrowseResult browse = _service.Browse(imagePath);
        Assert.Equal(2, browse.FileCount);

        string outDir = Path.Combine(_tempDir, "out_" + format);
        DiscImageExtractionResult extracted = await _service.ExtractAsync(imagePath, new DiscImageExtractionOptions(outDir));

        Assert.Equal(2, extracted.ExtractedFileCount);
        Assert.Equal(Sha256Hex(payload), Sha256File(Path.Combine(outDir, "Tracks", "DATA.BIN")));
        Assert.Equal(Sha256Hex(small), Sha256File(Path.Combine(outDir, "NOTE.TXT")));
    }

    [Fact]
    public async Task ExtractAsync_SelectiveFolderAndFile_AndOverwriteBehavior()
    {
        byte[] a = "alpha"u8.ToArray();
        byte[] b = "beta"u8.ToArray();
        byte[] c = "gamma"u8.ToArray();

        var root = CompilationDirectory.CreateRoot();
        var sub = root.AddDirectory("Keep");
        sub.AddFile("a.txt", a);
        sub.AddFile("b.txt", b);
        root.AddDirectory("Skip").AddFile("c.txt", c);

        string isoPath = Path.Combine(_tempDir, "selective.iso");
        IsoUdfImageBuilder.BuildToFile(root, isoPath, new IsoBuildOptions(IncludeUdf: true, FixedTimestampUtc: FixedTime));

        string outDir = Path.Combine(_tempDir, "selective_out");
        var res = await _service.ExtractAsync(isoPath, new DiscImageExtractionOptions(outDir, EntryPaths: ["/Keep"]));
        Assert.Equal(2, res.ExtractedFileCount);
        Assert.True(File.Exists(Path.Combine(outDir, "Keep", "a.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "Keep", "b.txt")));
        Assert.False(File.Exists(Path.Combine(outDir, "Skip", "c.txt")));

        await Assert.ThrowsAsync<IOException>(() =>
            _service.ExtractAsync(isoPath, new DiscImageExtractionOptions(outDir, EntryPaths: ["Keep/a.txt"], OverwriteExisting: false)));

        var overwriteRes = await _service.ExtractAsync(
            isoPath,
            new DiscImageExtractionOptions(outDir, EntryPaths: ["Keep/a.txt"], OverwriteExisting: true));
        Assert.Equal(1, overwriteRes.ExtractedFileCount);
        Assert.Equal(Sha256Hex(a), Sha256File(Path.Combine(outDir, "Keep", "a.txt")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.ExtractAsync(isoPath, new DiscImageExtractionOptions(outDir, EntryPaths: ["Keep/missing.txt"])));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("dir/../../escape.txt")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("C:evil.txt")]
    [InlineData(@"C:\evil.txt")]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("sub/con.txt")]
    [InlineData("sub/NUL.")]
    [InlineData("COM2 .log")]
    public void ValidateExtractionPath_RejectsAdversarialPaths(string badPath)
    {
        string outDir = Path.Combine(_tempDir, "safe_root");
        Directory.CreateDirectory(outDir);

        Assert.Throws<InvalidDataException>(() => DiscImageBrowserService.ValidateExtractionPath(outDir, badPath));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN.TXT")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("C:EVIL.TXT")]
    public async Task ExtractAsync_RejectsReservedDeviceNamesAndDrivePrefixesInIsoImage(string adversarialEntryName)
    {
        byte[] isoBytes = BuildSingleFileRawIso(adversarialEntryName, "bad"u8.ToArray());
        string isoPath = Path.Combine(_tempDir, $"adv_{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(isoPath, isoBytes);

        string outDir = Path.Combine(_tempDir, $"adv_out_{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.ExtractAsync(isoPath, new DiscImageExtractionOptions(outDir)));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDirectoryJunctionSymlinkEscape()
    {
        string outsideDir = Path.Combine(_tempDir, "outside_secret");
        Directory.CreateDirectory(outsideDir);

        string outDir = Path.Combine(_tempDir, "extract_jail");
        Directory.CreateDirectory(outDir);
        string junctionDir = Path.Combine(outDir, "EscapeDir");

        CreateNtfsJunction(junctionDir, outsideDir);

        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("EscapeDir").AddFile("pwn.txt", "escape"u8.ToArray());
        string isoPath = Path.Combine(_tempDir, "junction_test.iso");
        IsoUdfImageBuilder.BuildToFile(root, isoPath, new IsoBuildOptions(IncludeUdf: false, IncludeJoliet: true));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.ExtractAsync(isoPath, new DiscImageExtractionOptions(outDir)));

        Assert.False(File.Exists(Path.Combine(outsideDir, "pwn.txt")));
    }

    [Fact]
    public async Task BrowseAndExtract_UdfSymbolicLinkEntry_IsSkippedWithWarningAndNotExtracted()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("symlink_target.txt", "target"u8.ToArray());
        root.AddFile("normal.txt", "ok"u8.ToArray());
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeIso9660: false, IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0102, FixedTimestampUtc: FixedTime)).ToArray();

        // Patch the first file's UDF File Entry ICB FileType (byte 27) from 5 (regular file) to 12 (symbolic link).
        uint vds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        int partStart = -1;
        uint fsdLbn = 0;
        for (uint i = 0; i < 16; i++)
        {
            var d = image.AsSpan((int)(vds + i) * 2048, 2048);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (id == 5) partStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
            if (id == 6) fsdLbn = BinaryPrimitives.ReadUInt32LittleEndian(d[252..]);
            if (id == 8) break;
        }
        var fsd = image.AsSpan((partStart + (int)fsdLbn) * 2048, 2048);
        uint rootIcb = BinaryPrimitives.ReadUInt32LittleEndian(fsd[404..]);
        var rootFe = image.AsSpan((partStart + (int)rootIcb) * 2048, 2048);
        uint rootExt = BinaryPrimitives.ReadUInt32LittleEndian(rootFe[180..]);
        int dirOff = (partStart + (int)rootExt) * 2048;

        int pos = 0;
        bool patched = false;
        while (pos + 38 <= 2048)
        {
            var fid = image.AsSpan(dirOff + pos);
            if (BinaryPrimitives.ReadUInt16LittleEndian(fid) != 257) break;
            byte chars = fid[18];
            int nameLen = fid[19];
            int iuLen = BinaryPrimitives.ReadUInt16LittleEndian(fid[36..]);
            int recLen = (38 + iuLen + nameLen + 3) & ~3;
            if ((chars & 0x0A) == 0 && nameLen > 1)
            {
                var nameBytes = fid.Slice(38 + iuLen, nameLen);
                string fidName = nameBytes[0] == 8
                    ? Encoding.Latin1.GetString(nameBytes[1..])
                    : Encoding.BigEndianUnicode.GetString(nameBytes[1..]);
                if (fidName.Contains("symlink_target", StringComparison.OrdinalIgnoreCase))
                {
                    uint childIcb = BinaryPrimitives.ReadUInt32LittleEndian(fid[24..]);
                    var childFe = image.AsSpan((partStart + (int)childIcb) * 2048, 2048);
                    childFe[27] = 12; // ECMA-167 ICB File Type 12 = symbolic link
                    UpdateEcma167Tag(childFe);
                    patched = true;
                    break;
                }
            }
            pos += recLen;
        }
        Assert.True(patched);

        string udfPath = Path.Combine(_tempDir, "symlink.udf");
        File.WriteAllBytes(udfPath, image);

        var browse = _service.Browse(udfPath);
        Assert.Equal(1, browse.FileCount);
        Assert.Contains(browse.Warnings, w => w.Contains("symbolic link", StringComparison.OrdinalIgnoreCase));

        string outDir = Path.Combine(_tempDir, "symlink_out");
        var extracted = await _service.ExtractAsync(udfPath, new DiscImageExtractionOptions(outDir));
        Assert.Equal(1, extracted.ExtractedFileCount);
        Assert.True(File.Exists(Path.Combine(outDir, "normal.txt")));
        Assert.False(File.Exists(Path.Combine(outDir, "symlink_target.txt")));
    }

    private static void CreateNtfsJunction(string junctionPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(10_000);
        Assert.Equal(0, proc.ExitCode);
        Assert.True((File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0);
    }

    private static byte[] WrapRaw2352Mode1(byte[] isoBytes)
    {
        int sectors = OpticalConstants.BytesToSectors(isoBytes.Length, OpticalConstants.DataSectorSize);
        byte[] raw = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int lba = 0; lba < sectors; lba++)
        {
            Span<byte> sec = raw.AsSpan(lba * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize);
            sec[0] = 0x00;
            sec.Slice(1, 10).Fill(0xFF);
            sec[11] = 0x00;
            sec[15] = 0x01;
            int srcOff = lba * OpticalConstants.DataSectorSize;
            int copy = Math.Min(OpticalConstants.DataSectorSize, isoBytes.Length - srcOff);
            isoBytes.AsSpan(srcOff, copy).CopyTo(sec.Slice(16, copy));
        }
        return raw;
    }

    private static byte[] WrapRaw2352Mode2XaForm1(byte[] isoBytes)
    {
        int sectors = OpticalConstants.BytesToSectors(isoBytes.Length, OpticalConstants.DataSectorSize);
        byte[] raw = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int lba = 0; lba < sectors; lba++)
        {
            Span<byte> sec = raw.AsSpan(lba * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize);
            sec[0] = 0x00;
            sec.Slice(1, 10).Fill(0xFF);
            sec[11] = 0x00;
            sec[15] = 0x02; // Mode 2 XA Form 1 (user data at offset 24)
            int srcOff = lba * OpticalConstants.DataSectorSize;
            int copy = Math.Min(OpticalConstants.DataSectorSize, isoBytes.Length - srcOff);
            isoBytes.AsSpan(srcOff, copy).CopyTo(sec.Slice(24, copy));
        }
        return raw;
    }

    private static byte[] BuildSingleFileRawIso(string fileName, byte[] fileData)
    {
        byte[] iso = new byte[20 * 2048];
        Span<byte> pvd = iso.AsSpan(16 * 2048, 2048);
        pvd[0] = 0x01;
        "CD001"u8.CopyTo(pvd.Slice(1, 5));
        pvd[6] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(pvd.Slice(80, 4), 20);
        WriteIsoDirRecord(pvd.Slice(156, 34), 18, 2048, 0x02, [0x00]);

        Span<byte> term = iso.AsSpan(17 * 2048, 2048);
        term[0] = 0xFF;
        "CD001"u8.CopyTo(term.Slice(1, 5));
        term[6] = 0x01;

        Span<byte> dir = iso.AsSpan(18 * 2048, 2048);
        int off = WriteIsoDirRecord(dir, 18, 2048, 0x02, [0x00]);
        off += WriteIsoDirRecord(dir[off..], 18, 2048, 0x02, [0x01]);
        WriteIsoDirRecord(dir[off..], 19, (uint)fileData.Length, 0x00, Encoding.ASCII.GetBytes(fileName));

        fileData.CopyTo(iso.AsSpan(19 * 2048));
        return iso;
    }

    private static int WriteIsoDirRecord(Span<byte> dest, uint lba, uint size, byte flags, byte[] ident)
    {
        int len = 33 + ident.Length;
        if ((len & 1) != 0) len++;
        dest[0] = (byte)len;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(2, 4), lba);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(10, 4), size);
        dest[25] = flags;
        dest[32] = (byte)ident.Length;
        ident.CopyTo(dest.Slice(33));
        return len;
    }

    private static void UpdateEcma167Tag(Span<byte> descriptor)
    {
        ushort crcLen = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(10, 2));
        if (crcLen > 0 && 16 + crcLen <= descriptor.Length)
        {
            ushort crc = 0;
            foreach (byte b in descriptor.Slice(16, crcLen))
            {
                crc ^= (ushort)(b << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.Slice(8, 2), crc);
        }

        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4) sum = (byte)(sum + descriptor[i]);
        }
        descriptor[4] = sum;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
