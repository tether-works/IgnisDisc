using System.Buffers.Binary;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// The production UDF reader used by <c>recover</c>: it must list exactly what the strict test walker reads, with
/// full names, and still find the files when a descriptor is damaged, by using the backups UDF keeps.
/// </summary>
public class UdfTreeReaderTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<ushort> Revisions => new() { 0x0102, 0x0150, 0x0200, 0x0201, 0x0250, 0x0260 };

    private static (byte[] Image, Dictionary<string, byte[]> Expected) Build(ushort revision, bool joliet = false)
    {
        var rng = new Random(revision);
        var root = CompilationDirectory.CreateRoot();
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sub = root.AddDirectory("Schaffer, Riley, and the Double Ice Backfire - Hold Person (2026)");
        for (int f = 0; f < 40; f++)
        {
            byte[] data = new byte[f % 7 == 0 ? 0 : rng.Next(1, 90_000)];
            rng.NextBytes(data);
            // Names past Joliet's 64-character limit, with characters outside Latin-1.
            string name = $"{f:D2} A track title that is a good deal longer than sixty-four characters ( ͡° ͜ʖ ͡°).mp3";
            (f % 2 == 0 ? root : sub).AddFile(name, data);
            expected[(f % 2 == 0 ? "" : "/" + sub.Name) + "/" + name] = data;
        }
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(
            IncludeJoliet: joliet, IncludeUdf: true, UdfRevision: revision, FixedTimestampUtc: FixedTime)).ToArray();
        return (image, expected);
    }

    private static Dictionary<string, byte[]> ReadAll(byte[] image, List<string>? skipped = null)
    {
        using var stream = new MemoryStream(image, writable: false);
        var root = UdfTreeReader.TryRead(stream, skipped);
        Assert.NotNull(root);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(IsoDirectoryEntry dir)
        {
            foreach (var file in dir.Files)
            {
                files[file.VirtualPath] = image.AsSpan(file.StartLba * 2048, (int)file.SizeBytes).ToArray();
            }
            foreach (var d in dir.SubDirectories) Walk(d);
        }
        Walk(root!);
        return files;
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EveryRevision_ListsEveryFileWithItsFullName(ushort revision)
    {
        var (image, expected) = Build(revision);

        var files = ReadAll(image);

        Assert.Equal(expected.Keys.Order(), files.Keys.Order());
        foreach (var (path, data) in expected)
        {
            Assert.True(data.AsSpan().SequenceEqual(files[path]), path);
        }
        // Agrees with the strict walker.
        var walker = new UdfTestWalker(image);
        Assert.Equal(walker.Files.Keys.Order(), files.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void DamagedFirstAnchor_UsesTheOneAtTheEnd(ushort revision)
    {
        var (image, expected) = Build(revision);
        Array.Clear(image, 256 * 2048, 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void DamagedMainDescriptorSequence_UsesTheReserveCopy(ushort revision)
    {
        var (image, expected) = Build(revision);
        uint mainVds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        Array.Clear(image, (int)mainVds * 2048, 6 * 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Theory]
    [InlineData((ushort)0x0250)]
    [InlineData((ushort)0x0260)]
    public void DamagedMetadataFile_UsesTheMirror(ushort revision)
    {
        var (image, expected) = Build(revision);
        var (partitionStart, metadataFileLbn) = MetadataFileLocation(image);
        Array.Clear(image, (partitionStart + (int)metadataFileLbn) * 2048, 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Fact]
    public void DamagedFileEntry_SkipsThatFileOnly()
    {
        var (image, expected) = Build(0x0102);
        // The FSD sits at the start of the partition; the root directory's entry follows it, then files' entries.
        // Damage the last file entry before the first file's data by zeroing the sector right before that data.
        var walkerFiles = ReadAll(image);
        using var stream = new MemoryStream(image, writable: false);
        var root = UdfTreeReader.TryRead(stream)!;
        int firstData = root.Files.Where(f => f.SizeBytes > 0).Min(f => f.StartLba);
        Array.Clear(image, (firstData - 1) * 2048, 2048);

        var skipped = new List<string>();
        var files = ReadAll(image, skipped);

        Assert.Equal(expected.Count - 1, files.Count);
        Assert.Single(skipped);
        Assert.Equal(walkerFiles.Count, expected.Count);
    }

    [Fact]
    public void ImageWithoutUdf_GivesNull()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", new byte[100]);
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false)).ToArray();
        using var stream = new MemoryStream(image);
        Assert.Null(UdfTreeReader.TryRead(stream));
        using var empty = new MemoryStream(new byte[300 * 2048]);
        Assert.Null(UdfTreeReader.TryRead(empty));
    }

    /// <summary>Partition start and the Metadata File entry's block, from the Partition and Logical Volume Descriptors.</summary>
    private static (int PartitionStart, uint MetadataFileLbn) MetadataFileLocation(byte[] image)
    {
        uint vds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        int partitionStart = -1;
        uint fileLbn = 0;
        for (uint i = 0; i < 16; i++)
        {
            var d = image.AsSpan((int)(vds + i) * 2048, 2048);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (id == 5) partitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
            if (id == 6) fileLbn = BinaryPrimitives.ReadUInt32LittleEndian(d[(440 + 6 + 40)..]); // after the 6-byte type 1 map
            if (id == 8) break;
        }
        return (partitionStart, fileLbn);
    }

    [Fact]
    public void CyclicDirectoryEntry_IsRejectedWithoutInfiniteRecursion()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("CycleSub");
        root.AddFile("good.txt", "ok"u8.ToArray());
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root, new IsoBuildOptions(IncludeIso9660: false, IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0102, FixedTimestampUtc: FixedTime)).ToArray();

        // Locate FSD -> root ICB -> root extent sector and rewrite CycleSub's FID ICB to point back to root ICB
        var (partStart, rootIcbLbn, rootExtLbn) = LocateUdfRoot(image);
        int dirOffset = (partStart + (int)rootExtLbn) * 2048;
        int pos = 0;
        bool patched = false;
        while (pos + 38 <= 2048)
        {
            var fid = image.AsSpan(dirOffset + pos);
            if (BinaryPrimitives.ReadUInt16LittleEndian(fid) != 257) break;
            byte chars = fid[18];
            int nameLen = fid[19];
            int iuLen = BinaryPrimitives.ReadUInt16LittleEndian(fid[36..]);
            int recLen = (38 + iuLen + nameLen + 3) & ~3;

            if ((chars & 0x02) != 0 && (chars & 0x08) == 0 && nameLen > 0)
            {
                // Rewrite child ICB LBN to rootIcbLbn to form root -> CycleSub -> root cycle
                BinaryPrimitives.WriteUInt32LittleEndian(fid.Slice(24, 4), rootIcbLbn);
                UpdateEcma167Tag(fid[..recLen]);
                patched = true;
                break;
            }
            pos += recLen;
        }
        Assert.True(patched);

        using var stream = new MemoryStream(image, writable: false);
        var skipped = new List<string>();
        var parsedRoot = UdfTreeReader.TryRead(stream, skipped);

        Assert.NotNull(parsedRoot);
        Assert.Single(parsedRoot!.Files);
        Assert.Equal("good.txt", parsedRoot.Files.First().Name);
        Assert.Empty(parsedRoot.SubDirectories);
        Assert.NotEmpty(skipped);
    }

    [Fact]
    public void PathTraversalAndSeparatorCharactersInFidName_AreSanitizedOrRejected()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("1234567890.txt", "a"u8.ToArray());
        root.AddFile("abcdefghij.txt", "b"u8.ToArray());
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root, new IsoBuildOptions(IncludeIso9660: false, IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0102, FixedTimestampUtc: FixedTime)).ToArray();

        var (partStart, _, rootExtLbn) = LocateUdfRoot(image);
        int dirOffset = (partStart + (int)rootExtLbn) * 2048;
        int pos = 0;
        int fileSeen = 0;
        while (pos + 38 <= 2048)
        {
            var fid = image.AsSpan(dirOffset + pos);
            if (BinaryPrimitives.ReadUInt16LittleEndian(fid) != 257) break;
            byte chars = fid[18];
            int nameLen = fid[19];
            int iuLen = BinaryPrimitives.ReadUInt16LittleEndian(fid[36..]);
            int recLen = (38 + iuLen + nameLen + 3) & ~3;

            if ((chars & 0x08) == 0 && nameLen >= 12)
            {
                var nameSpan = fid.Slice(38 + iuLen, nameLen);
                nameSpan[0] = 8; // Latin-1 compression ID
                if (fileSeen == 0)
                {
                    byte[] evil = System.Text.Encoding.Latin1.GetBytes("../../evil.txt");
                    evil[..Math.Min(evil.Length, nameLen - 1)].CopyTo(nameSpan[1..]);
                    // Pad remaining name bytes with NUL (stripped by DecodeName)
                    nameSpan[(1 + Math.Min(evil.Length, nameLen - 1))..].Clear();
                }
                else
                {
                    byte[] sep = System.Text.Encoding.Latin1.GetBytes("sub/name\\x.txt");
                    sep[..Math.Min(sep.Length, nameLen - 1)].CopyTo(nameSpan[1..]);
                    nameSpan[(1 + Math.Min(sep.Length, nameLen - 1))..].Clear();
                }
                UpdateEcma167Tag(fid[..recLen]);
                fileSeen++;
            }
            pos += recLen;
        }
        Assert.Equal(2, fileSeen);

        using var stream = new MemoryStream(image, writable: false);
        var parsedRoot = UdfTreeReader.TryRead(stream);
        Assert.NotNull(parsedRoot);
        Assert.Single(parsedRoot!.Files);
        string sanitized = parsedRoot.Files.First().Name;
        Assert.DoesNotContain("/", sanitized);
        Assert.DoesNotContain("\\", sanitized);
        Assert.DoesNotContain("..", sanitized);
    }

    [Fact]
    public void TryRead_WithSessionStartLba_ReadsShiftedUdfAvdp()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("shifted.txt", "session-offset-udf"u8.ToArray());
        byte[] baseImage = IsoUdfImageBuilder.BuildToMemoryStream(
            root, new IsoBuildOptions(IncludeIso9660: false, IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0102, FixedTimestampUtc: FixedTime)).ToArray();

        const int sessionStart = 5000; // past VdsScanSectors (4096) so sessionStartLba=0 scan cannot stumble upon it
        byte[] shifted = new byte[sessionStart * 2048 + baseImage.Length];
        baseImage.CopyTo(shifted, sessionStart * 2048);

        // Shift absolute sector references in AVDP (tag location + VDS pointers) and VDS descriptors (PVD, PD, LVD, FSD, etc.)
        // so that AVDP sits at sessionStart + 256 with matching ECMA-167 tag location.
        int avdpOff = (sessionStart + 256) * 2048;
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(avdpOff + 12, 4), (uint)(sessionStart + 256));
        uint oldMainVds = BinaryPrimitives.ReadUInt32LittleEndian(shifted.AsSpan(avdpOff + 20, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(avdpOff + 20, 4), oldMainVds + sessionStart);
        uint oldResVds = BinaryPrimitives.ReadUInt32LittleEndian(shifted.AsSpan(avdpOff + 28, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(avdpOff + 28, 4), oldResVds + sessionStart);
        UpdateEcma167Tag(shifted.AsSpan(avdpOff, 512));

        for (uint i = 0; i < 16; i++)
        {
            int secOff = (int)(sessionStart + oldMainVds + i) * 2048;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(shifted.AsSpan(secOff, 2));
            if (id == 0) break;
            BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(secOff + 12, 4), sessionStart + oldMainVds + i);
            if (id == 5)
            {
                uint oldPartStart = BinaryPrimitives.ReadUInt32LittleEndian(shifted.AsSpan(secOff + 188, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(secOff + 188, 4), oldPartStart + sessionStart);
            }
            UpdateEcma167Tag(shifted.AsSpan(secOff, 512));
            if (id == 8) break;
        }

        using var stream = new MemoryStream(shifted, writable: false);
        Assert.Null(UdfTreeReader.TryRead(stream, sessionStartLba: 0));
        var parsedRoot = UdfTreeReader.TryRead(stream, sessionStartLba: sessionStart);
        Assert.NotNull(parsedRoot);
        Assert.Single(parsedRoot!.Files);
        Assert.Equal("shifted.txt", parsedRoot.Files.First().Name);
    }

    private static (int PartitionStart, uint RootIcbLbn, uint RootExtentLbn) LocateUdfRoot(byte[] image)
    {
        uint vds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        int partitionStart = -1;
        uint fsdLbn = 0;
        for (uint i = 0; i < 16; i++)
        {
            var d = image.AsSpan((int)(vds + i) * 2048, 2048);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (id == 5) partitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
            if (id == 6) fsdLbn = BinaryPrimitives.ReadUInt32LittleEndian(d[252..]);
            if (id == 8) break;
        }
        var fsd = image.AsSpan((partitionStart + (int)fsdLbn) * 2048, 2048);
        uint rootIcbLbn = BinaryPrimitives.ReadUInt32LittleEndian(fsd[404..]);
        var rootFe = image.AsSpan((partitionStart + (int)rootIcbLbn) * 2048, 2048);
        uint rootExtLbn = BinaryPrimitives.ReadUInt32LittleEndian(rootFe[(176 + 4)..]);
        return (partitionStart, rootIcbLbn, rootExtLbn);
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
}
