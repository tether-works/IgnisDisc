using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class IsoImageReaderTests
{
    [Fact]
    public void OpenFileStream_StreamsWithoutFullArrayAllocation_AndSupportsSeeking()
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[65_536];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }
        root.AddFile("streamed.bin", payload);

        using var iso = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false));
        using var reader = IsoImageReader.FromStream(iso, leaveOpen: true);

        using Stream fileStream = reader.OpenFileStream("/streamed.bin");
        Assert.Equal(payload.Length, fileStream.Length);
        Assert.True(fileStream.CanRead);
        Assert.True(fileStream.CanSeek);

        fileStream.Position = 12_345;
        Assert.Equal(payload[12_345], fileStream.ReadByte());

        fileStream.Seek(-10, SeekOrigin.End);
        byte[] tail = new byte[10];
        fileStream.ReadExactly(tail);
        Assert.Equal(payload[^10..], tail);

        fileStream.Position = 0;
        byte[] full = new byte[payload.Length];
        fileStream.ReadExactly(full);
        Assert.Equal(payload, full);
    }

    [Fact]
    public void Joliet_PreservesSemicolonAndTrailingDotInFilename()
    {
        // Construct a minimal ISO 9660 + Joliet SVD image where the Joliet directory entry is literally "a;b.txt"
        // and another is "trailing." to verify Joliet names do not undergo ISO 9660 ";1" stripping.
        using var stream = new SparseSectorStream(32 * 2048);

        byte[] pvd = new byte[2048];
        pvd[0] = 0x01;
        "CD001"u8.CopyTo(pvd.AsSpan(1, 5));
        pvd[6] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(pvd.AsSpan(80, 4), 32);
        WriteRootRecord(pvd.AsSpan(156, 34), rootLba: 19, rootLen: 2048);
        stream.WriteSector(16, pvd);

        byte[] svd = new byte[2048];
        svd[0] = 0x02;
        "CD001"u8.CopyTo(svd.AsSpan(1, 5));
        svd[6] = 0x01;
        svd[88] = 0x25;
        svd[89] = 0x2F;
        svd[90] = 0x45;
        BinaryPrimitives.WriteUInt32LittleEndian(svd.AsSpan(80, 4), 32);
        WriteRootRecord(svd.AsSpan(156, 34), rootLba: 20, rootLen: 2048);
        stream.WriteSector(17, svd);

        byte[] term = new byte[2048];
        term[0] = 0xFF;
        "CD001"u8.CopyTo(term.AsSpan(1, 5));
        term[6] = 0x01;
        stream.WriteSector(18, term);

        // ISO 9660 root directory at LBA 19 with ;1 version suffix
        byte[] isoDir = new byte[2048];
        int p = WriteDirRecord(isoDir.AsSpan(0), 19, 2048, 0x02, [0x00]);
        p += WriteDirRecord(isoDir.AsSpan(p), 19, 2048, 0x02, [0x01]);
        WriteDirRecord(isoDir.AsSpan(p), 21, 5, 0x00, "AB.TXT;1"u8.ToArray());
        stream.WriteSector(19, isoDir);

        // Joliet root directory at LBA 20 with "a;b.txt" and "note."
        byte[] jolietDir = new byte[2048];
        int jp = WriteDirRecord(jolietDir.AsSpan(0), 20, 2048, 0x02, [0x00]);
        jp += WriteDirRecord(jolietDir.AsSpan(jp), 20, 2048, 0x02, [0x01]);
        jp += WriteDirRecord(jolietDir.AsSpan(jp), 21, 5, 0x00, Encoding.BigEndianUnicode.GetBytes("a;b.txt"));
        WriteDirRecord(jolietDir.AsSpan(jp), 21, 5, 0x00, Encoding.BigEndianUnicode.GetBytes("note."));
        stream.WriteSector(20, jolietDir);

        using var reader = IsoImageReader.FromStream(stream, leaveOpen: true);
        Assert.NotNull(reader.FindFile("/a;b.txt"));
        Assert.NotNull(reader.FindFile("/note."));
        Assert.Contains(reader.AllFiles, f => f.Name == "a;b.txt");
        Assert.Contains(reader.AllFiles, f => f.Name == "note.");
        Assert.Contains(reader.IsoRootDirectory!.Files, f => f.Name == "AB.TXT");
    }

    [Fact]
    public void MultiExtent_CoalescesOver4GiBFileIntoSingleEntry_AndStreamsAcrossExtentBoundary()
    {
        const uint extent1Size = 0xFFFFF800u; // 4,294,965,248 bytes (2,097,151 sectors)
        const uint extent2Size = 705_034_752u; // total = 5,000,000,000 bytes (~4.66 GiB)
        const long totalFileSize = (long)extent1Size + extent2Size;
        const int extent1Lba = 100;
        const int extent2Lba = 2_200_000; // non-contiguous to exercise MultiExtentSegmentStream as well
        const int totalVolumeSectors = 2_600_000;

        using var stream = new SparseSectorStream((long)totalVolumeSectors * 2048);

        byte[] pvd = new byte[2048];
        pvd[0] = 0x01;
        "CD001"u8.CopyTo(pvd.AsSpan(1, 5));
        pvd[6] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(pvd.AsSpan(80, 4), totalVolumeSectors);
        WriteRootRecord(pvd.AsSpan(156, 34), rootLba: 18, rootLen: 2048);
        stream.WriteSector(16, pvd);

        byte[] term = new byte[2048];
        term[0] = 0xFF;
        "CD001"u8.CopyTo(term.AsSpan(1, 5));
        term[6] = 0x01;
        stream.WriteSector(17, term);

        byte[] rootDir = new byte[2048];
        int offset = WriteDirRecord(rootDir.AsSpan(0), 18, 2048, 0x02, [0x00]);
        offset += WriteDirRecord(rootDir.AsSpan(offset), 18, 2048, 0x02, [0x01]);
        byte[] fileIdent = "HUGE.BIN;1"u8.ToArray();
        // First extent: ECMA-119 multi-extent flag (0x80) set
        offset += WriteDirRecord(rootDir.AsSpan(offset), extent1Lba, extent1Size, 0x80, fileIdent);
        // Second (final) extent: multi-extent flag clear
        WriteDirRecord(rootDir.AsSpan(offset), extent2Lba, extent2Size, 0x00, fileIdent);
        stream.WriteSector(18, rootDir);

        // Place sentinel bytes at the end of extent 1 and start of extent 2
        byte[] ext1LastSector = new byte[2048];
        ext1LastSector[2047] = 0xA1;
        stream.WriteSector(extent1Lba + (int)(extent1Size / 2048) - 1, ext1LastSector);

        byte[] ext2FirstSector = new byte[2048];
        ext2FirstSector[0] = 0xB2;
        ext2FirstSector[1] = 0xC3;
        stream.WriteSector(extent2Lba, ext2FirstSector);

        using var reader = IsoImageReader.FromStream(stream, leaveOpen: true);

        Assert.Single(reader.AllFiles);
        var entry = reader.AllFiles[0];
        Assert.Equal("HUGE.BIN", entry.Name);
        Assert.Equal(extent1Lba, entry.StartLba);
        Assert.Equal(totalFileSize, entry.SizeBytes);

        using Stream fileStream = reader.OpenFileStream(entry);
        Assert.Equal(totalFileSize, fileStream.Length);

        fileStream.Position = extent1Size - 1;
        byte[] crossBoundary = new byte[3];
        fileStream.ReadExactly(crossBoundary);
        Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3 }, crossBoundary);

        // The sector-to-file map covers both extents and not the gap between them.
        var map = Images.LbaFileExtentMap.FromIsoReader(reader);
        Assert.Equal(2, map.Count);
        Assert.All(map, e => Assert.Equal("/HUGE.BIN", e.FilePath));
        Assert.Contains(map, e => e.ContainsLba(extent1Lba) && e.ContainsLba(extent1Lba + (int)(extent1Size / 2048) - 1));
        Assert.Contains(map, e => e.ContainsLba(extent2Lba + 100_000));
        Assert.DoesNotContain(map, e => e.ContainsLba(extent1Lba + (int)(extent1Size / 2048) + 10));
    }

    [Fact]
    public void HasUdf_RejectsSpoofedAvdpWithBadCrcOrLocation()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", "data"u8.ToArray());
        byte[] baseIso = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false)).ToArray();

        // Extend beyond LBA 256 and place a spoofed 0x0002 tag at LBA 256 without valid CRC/location
        byte[] spoofed = new byte[260 * 2048];
        baseIso.CopyTo(spoofed, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(spoofed.AsSpan(256 * 2048, 2), 0x0002);

        using var ms = new MemoryStream(spoofed, writable: false);
        using var reader = IsoImageReader.FromStream(ms, leaveOpen: true);
        Assert.False(reader.HasUdf);
    }

    [Fact]
    public void Pvd_NegativeOrOverflowTotalSectors_ThrowsInvalidDataException()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", "data"u8.ToArray());
        byte[] iso = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false)).ToArray();

        // Set VolumeSpaceSize to 0x80000001 (> int.MaxValue, negative as signed int32)
        BinaryPrimitives.WriteUInt32LittleEndian(iso.AsSpan(16 * 2048 + 80, 4), 0x80000001u);

        using var ms = new MemoryStream(iso, writable: false);
        Assert.Throws<InvalidDataException>(() => IsoImageReader.FromStream(ms, leaveOpen: true));
    }

    private static void WriteRootRecord(Span<byte> dest, uint rootLba, uint rootLen)
    {
        dest[0] = 34;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(2, 4), rootLba);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(10, 4), rootLen);
        dest[25] = 0x02;
        dest[32] = 1;
        dest[33] = 0x00;
    }

    private static int WriteDirRecord(Span<byte> dest, uint lba, uint size, byte flags, byte[] ident)
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

    private sealed class SparseSectorStream : Stream
    {
        private readonly Dictionary<int, byte[]> _sectors = new();
        private readonly long _length;
        private long _position;

        public SparseSectorStream(long length) => _length = length;

        public void WriteSector(int lba, byte[] data) => _sectors[lba] = data;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.IsEmpty) return 0;
            int total = (int)Math.Min(buffer.Length, _length - _position);
            int read = 0;
            while (read < total)
            {
                int lba = (int)(_position / 2048);
                int off = (int)(_position % 2048);
                int n = Math.Min(2048 - off, total - read);
                if (_sectors.TryGetValue(lba, out var sec))
                {
                    sec.AsSpan(off, n).CopyTo(buffer.Slice(read, n));
                }
                else
                {
                    buffer.Slice(read, n).Clear();
                }
                _position += n;
                read += n;
            }
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
