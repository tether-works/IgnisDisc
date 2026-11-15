using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace CdBurner.Core.Tests;

/// <summary>
/// Files and a picture on one disc. The file system must end where the files end (ISO 9660 volume space size,
/// UDF partition), at least 2 MiB of zero sectors must follow before the picture, and the picture zone must sit on
/// DVD ECC block (16 sectors, ECMA-267) or BD cluster (32 sectors) boundaries. Field offsets come from ECMA-119
/// (volume space size at byte 80 of the Primary Volume Descriptor) and ECMA-167 (Anchor at LBA 256 and at the last
/// sector of the volume, Partition Descriptor tag 5 with start at byte 188 and length at byte 192). Files are read
/// back with DiscUtils, an independent reader.
/// </summary>
public class PictureDiscLayoutTests
{
    private const int Sector = 2048;

    // Small disc so the whole image fits in memory: 12,000 sectors (about 24.6 MB).
    private const int DiscCapacity = 12_000;

    private static readonly IsoBuildOptions Options = new(
        VolumeLabel: "PICTURE_DISC",
        FixedTimestampUtc: new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

    private static Dictionary<string, byte[]> SourceFiles()
    {
        static byte[] Random(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        return new Dictionary<string, byte[]>
        {
            ["readme.txt"] = "files and a picture on one disc"u8.ToArray(),
            ["empty.bin"] = Array.Empty<byte>(),
            [@"photos\one.bin"] = Random(300_000, 1),
            [@"photos\deep\two.bin"] = Random(1_000_001, 2),
        };
    }

    private static CompilationDirectory Compile(Dictionary<string, byte[]> files)
    {
        var compilation = new DataCompilation(Options.VolumeLabel);
        foreach (var (path, content) in files)
        {
            compilation.AddFile("/" + path.Replace('\\', '/'), content);
        }
        return compilation.Root;
    }

    /// <summary>Fills each picture sector with 0xA5 and stamps its LBA in the first 8 bytes.</summary>
    private static void PictureBlock(long firstLba, Span<byte> block)
    {
        block.Fill(0xA5);
        for (int s = 0; s < block.Length / Sector; s++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(block.Slice(s * Sector, 8), firstLba + s);
        }
    }

    private static (PictureDiscLayout Layout, byte[] Image) Compose(
        PictureDiscFilesPlacement placement, PictureDiscMedium medium, List<long>? blockCalls = null)
    {
        var root = Compile(SourceFiles());
        var layout = PictureDiscLayoutPlanner.Plan(root, Options, placement, medium, DiscCapacity);
        using var output = new MemoryStream();
        PictureDiscImageComposer.Compose(root, Options, layout, (lba, block) =>
        {
            blockCalls?.Add(lba);
            PictureBlock(lba, block);
        }, output);
        return (layout, output.ToArray());
    }

    private static ReadOnlySpan<byte> SectorAt(byte[] image, long lba) => image.AsSpan(checked((int)(lba * Sector)), Sector);

    public static TheoryData<PictureDiscFilesPlacement, PictureDiscMedium> AllLayouts => new()
    {
        { PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd },
        { PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.BluRay },
        { PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd },
        { PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.BluRay },
    };

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void PictureZoneIsAlignedAndSeparatedFromTheVolumeByTwoMebibytesOfZeros(
        PictureDiscFilesPlacement placement, PictureDiscMedium medium)
    {
        var calls = new List<long>();
        var (layout, image) = Compose(placement, medium, calls);
        int align = medium == PictureDiscMedium.Dvd ? 16 : 32;

        Assert.Equal((long)layout.TotalSectors * Sector, image.Length);
        Assert.True(layout.TotalSectors <= DiscCapacity);
        Assert.Equal(0, layout.PictureStartLba % align);
        Assert.Equal(0, layout.PictureEndLba % align);
        Assert.Equal(0, layout.FilesSessionStartLba % align);
        Assert.True(layout.PictureSectorCount >= align);

        // 2 MiB = 1024 sectors of 2048 bytes, all zero.
        Assert.True(layout.GapSectorCount >= 1024, $"gap is {layout.GapSectorCount} sectors");
        for (long lba = layout.GapStartLba; lba < layout.GapEndLba; lba++)
        {
            Assert.True(SectorAt(image, lba).IndexOfAnyExcept((byte)0) < 0, $"gap sector {lba} is not zero");
        }

        // The picture is written block by block at the planned sectors, and only there.
        Assert.Equal(layout.PictureSectorCount / align, calls.Count);
        Assert.All(calls, lba => Assert.Equal(0, lba % align));
        for (long lba = layout.PictureStartLba; lba < layout.PictureEndLba; lba++)
        {
            var sector = SectorAt(image, lba);
            Assert.Equal(lba, BinaryPrimitives.ReadInt64LittleEndian(sector));
            Assert.True(sector[8..].IndexOfAnyExcept((byte)0xA5) < 0);
        }

        if (placement == PictureDiscFilesPlacement.InnerFiles)
        {
            Assert.Equal(0, layout.FilesSessionStartLba);
            Assert.True(layout.PictureStartLba >= layout.VolumeEndLba + 1024);
            Assert.Equal(AlignUp(layout.VolumeEndLba + 1024, align), layout.PictureStartLba);
            Assert.Equal(DiscCapacity / align * align, layout.PictureEndLba);
        }
        else
        {
            Assert.Equal(0, layout.PictureStartLba);
            Assert.True(layout.FilesSessionStartLba >= layout.PictureEndLba + 1024);
            Assert.Equal(layout.VolumeEndLba, layout.TotalSectors);
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void IsoVolumeSpaceSizeEndsAtTheFiles(PictureDiscFilesPlacement placement, PictureDiscMedium medium)
    {
        var (layout, image) = Compose(placement, medium);

        // ECMA-119 8.4: Primary Volume Descriptor at session start + 16, type 1, "CD001", volume space size as a
        // both-byte-order 32-bit number at bytes 80-87.
        var pvd = SectorAt(image, layout.FilesSessionStartLba + 16);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd.Slice(1, 5)));
        uint volumeSpaceSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd[80..]);
        Assert.Equal(volumeSpaceSize, BinaryPrimitives.ReadUInt32BigEndian(pvd[84..]));
        Assert.Equal((uint)layout.VolumeEndLba, volumeSpaceSize);

        // The picture lies entirely outside the session's sectors [session start, volume end).
        bool overlaps = layout.PictureStartLba < layout.VolumeEndLba && layout.FilesSessionStartLba < layout.PictureEndLba;
        Assert.False(overlaps);
    }

    [Fact]
    public void UdfPartitionAndClosingAnchorEndAtTheFilesOnInnerFilesDiscs()
    {
        var (layout, image) = Compose(PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd);

        // ECMA-167 3/10.2: Anchor Volume Descriptor Pointer (tag 2) at LBA 256; main VDS extent at bytes 16-23.
        var avdp = SectorAt(image, 256);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(avdp));
        uint vdsLength = BinaryPrimitives.ReadUInt32LittleEndian(avdp[16..]);
        uint vdsLocation = BinaryPrimitives.ReadUInt32LittleEndian(avdp[20..]);

        uint? partitionStart = null, partitionLength = null;
        for (uint lba = vdsLocation; lba < vdsLocation + vdsLength / Sector; lba++)
        {
            var d = SectorAt(image, lba);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (tag == 5)
            {
                // ECMA-167 3/10.5: Partition Starting Location at byte 188, Partition Length at byte 192.
                partitionStart = BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
                partitionLength = BinaryPrimitives.ReadUInt32LittleEndian(d[192..]);
            }
            if (tag == 8)
            {
                break; // Terminating Descriptor
            }
        }

        Assert.NotNull(partitionStart);
        long partitionEnd = (long)partitionStart!.Value + partitionLength!.Value;
        Assert.True(partitionEnd <= layout.VolumeEndLba, $"UDF partition ends at {partitionEnd}, volume at {layout.VolumeEndLba}");
        Assert.True(partitionEnd + 1024 <= layout.PictureStartLba);

        // ECMA-167 3/8.4.2.1: an anchor at the last sector of the volume space (N), which must be the volume end - 1,
        // not the last sector of the picture.
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(SectorAt(image, layout.VolumeEndLba - 1)));
    }

    [Fact]
    public void DiscUtilsReadsVolumeSizeAndEveryFileOnInnerFilesDisc()
    {
        var (layout, image) = Compose(PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd);
        var files = SourceFiles();

        foreach (bool joliet in new[] { false, true })
        {
            using var stream = new MemoryStream(image, writable: false);
            using var iso = new CDReader(stream, joliet);
            Assert.Equal((long)layout.VolumeEndLba * Sector, iso.Size);
            AssertEveryFile(iso, files, joliet, layout);
        }

        using (var stream = new MemoryStream(image, writable: false))
        using (var udf = new UdfReader(stream))
        {
            AssertEveryFileContent(udf, files);
        }
    }

    [Theory]
    [InlineData(PictureDiscMedium.Dvd)]
    [InlineData(PictureDiscMedium.BluRay)]
    public void DiscUtilsReadsVolumeSizeAndEveryFileOnOuterFilesDisc(PictureDiscMedium medium)
    {
        var (layout, image) = Compose(PictureDiscFilesPlacement.OuterFiles, medium);
        var files = SourceFiles();

        // A multisession-aware reader takes the volume descriptors from the last session (here the files session);
        // DiscUtils always reads them at LBA 16, so present the files session's descriptors there.
        foreach (bool joliet in new[] { false, true })
        {
            using var stream = new LastSessionDescriptorStream(image, layout.FilesSessionStartLba);
            using var iso = new CDReader(stream, joliet);
            Assert.Equal((long)layout.VolumeEndLba * Sector, iso.Size);
            AssertEveryFile(iso, files, joliet, layout);
        }

        // Our own reader with the session start, as the app reads a multisession disc.
        using var plain = new MemoryStream(image, writable: false);
        using var reader = IsoImageReader.FromStream(plain, leaveOpen: true, sessionStartLba: layout.FilesSessionStartLba);
        Assert.Equal(files.Count, reader.AllFiles.Count);
    }

    private static void AssertEveryFile(CDReader iso, Dictionary<string, byte[]> files, bool fullNames, PictureDiscLayout layout)
    {
        var onDisc = iso.GetFiles("", "*.*", SearchOption.AllDirectories).ToList();
        Assert.Equal(files.Count, onDisc.Count);

        foreach (string path in onDisc)
        {
            // Every extent lies inside the files session and outside the picture zone.
            foreach (var range in iso.PathToClusters(path))
            {
                long first = range.Offset;
                long end = range.Offset + range.Count;
                Assert.True(first >= layout.FilesSessionStartLba && end <= layout.VolumeEndLba,
                    $"{path} extent [{first}, {end}) is outside the volume");
                Assert.True(end <= layout.PictureStartLba || first >= layout.PictureEndLba,
                    $"{path} extent [{first}, {end}) overlaps the picture");
            }
        }

        if (fullNames)
        {
            AssertEveryFileContent(iso, files);
        }
        else
        {
            // Plain ISO 9660 names are shortened; check contents by size and bytes instead of by name.
            var expected = files.Values.Select(Convert.ToHexString).OrderBy(x => x).ToList();
            var actual = onDisc.Select(p => Convert.ToHexString(ReadAll(iso, p))).OrderBy(x => x).ToList();
            Assert.Equal(expected, actual);
        }
    }

    private static void AssertEveryFileContent(DiscFileSystem fs, Dictionary<string, byte[]> files)
    {
        Assert.Equal(files.Count, fs.GetFiles("", "*.*", SearchOption.AllDirectories).Count());
        foreach (var (path, content) in files)
        {
            Assert.True(fs.FileExists(path), $"missing on disc: {path}");
            Assert.Equal(content, ReadAll(fs, path));
        }
    }

    private static byte[] ReadAll(DiscFileSystem fs, string path)
    {
        using var s = fs.OpenFile(path, FileMode.Open);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void PlanFailsWhenFilesGapAndOnePictureBlockDoNotFit()
    {
        var root = Compile(SourceFiles());
        // Inner files: the gap ends on a block boundary, but the disc ends 15 sectors later, short of one block.
        int volume = IsoUdfImageBuilder.CalculateTotalSectors(root, Options);
        int innerTooSmall = AlignUp(volume + 1024, 16) + 15;
        Assert.Throws<InvalidOperationException>(() => PictureDiscLayoutPlanner.Plan(
            root, Options, PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd, innerTooSmall));
        Assert.NotNull(PictureDiscLayoutPlanner.Plan(
            root, Options, PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd, innerTooSmall + 1));

        // Outer files (no UDF in an appended session): the session plus the gap leave less than one block in front.
        int session = IsoUdfImageBuilder.CalculateTotalSectors(root, Options, sessionStartLba: 1600) - 1600;
        int outerTooSmall = session + 1024 + 15;
        Assert.Throws<InvalidOperationException>(() => PictureDiscLayoutPlanner.Plan(
            root, Options, PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, outerTooSmall));
    }

    [Fact]
    public void PlanRejectsFilesFromAnEarlierSession()
    {
        var compilation = new DataCompilation("OLD");
        compilation.AddFile("/old.bin", string.Empty, 4096, importedStartLba: 400);
        Assert.Throws<ArgumentException>(() => PictureDiscLayoutPlanner.Plan(
            compilation.Root, Options, PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd, DiscCapacity));
    }

    [Fact]
    public void ComposeRefusesALayoutPlannedForDifferentFiles()
    {
        var root = Compile(SourceFiles());
        var layout = PictureDiscLayoutPlanner.Plan(root, Options, PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd, DiscCapacity);

        var bigger = SourceFiles();
        bigger["extra.bin"] = new byte[200_000];
        using var output = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            PictureDiscImageComposer.Compose(Compile(bigger), Options, layout, PictureBlock, output));
    }

    [Fact]
    public void SingleLayerDvdPlanFillsTheDiscToTheLastWholeEccBlock()
    {
        // Single-layer DVD+R: 2,295,104 sectors. The picture runs to the last whole 16-sector block.
        const int dvdPlusR = 2_295_104;
        var root = Compile(SourceFiles());
        var layout = PictureDiscLayoutPlanner.Plan(root, Options, PictureDiscFilesPlacement.InnerFiles, PictureDiscMedium.Dvd, dvdPlusR);
        Assert.Equal(dvdPlusR / 16 * 16, layout.PictureEndLba);
        Assert.Equal(layout.PictureEndLba, layout.TotalSectors);

        var outer = PictureDiscLayoutPlanner.Plan(root, Options, PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, dvdPlusR);
        Assert.True(outer.VolumeEndLba <= dvdPlusR);
        Assert.True(dvdPlusR - outer.VolumeEndLba < 16, "the files session should sit at the end of the disc");
    }

    private static int AlignUp(int value, int align) => (value + align - 1) / align * align;

    /// <summary>
    /// Read-only view of a disc image that returns the volume descriptors of the session starting at
    /// <c>sessionStart</c> when LBA 16 onward is read, up to and including the Volume Descriptor Set Terminator
    /// (type 255, ECMA-119 8.3). Every other sector is the image as it is, so absolute LBAs in the descriptors and
    /// directories resolve to the files session.
    /// </summary>
    private sealed class LastSessionDescriptorStream : Stream
    {
        private readonly byte[] _image;
        private readonly long _sessionStart;
        private readonly int _descriptorSectors;
        private long _position;

        public LastSessionDescriptorStream(byte[] image, long sessionStart)
        {
            _image = image;
            _sessionStart = sessionStart;
            int count = 0;
            while (true)
            {
                byte type = image[(sessionStart + 16 + count) * Sector];
                count++;
                if (type == 255)
                {
                    break;
                }
                Assert.True(count < 32, "no volume descriptor set terminator");
            }
            _descriptorSectors = count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _image.Length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && _position < _image.Length)
            {
                long lba = _position / Sector;
                int inSector = (int)(_position % Sector);
                int n = Math.Min(count, Sector - inSector);
                long source = lba >= 16 && lba < 16 + _descriptorSectors
                    ? (lba + _sessionStart) * Sector + inSector
                    : _position;
                Array.Copy(_image, source, buffer, offset, n);
                _position += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _image.Length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
