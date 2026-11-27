using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// The sector-to-file map used to name the file behind a bad sector: it must cover the whole of a file of 4 GiB or
/// more (ISO 9660 can only record such a file's first 4 GiB), and images with only a UDF file system.
/// </summary>
public class LbaFileExtentMapTests
{
    /// <summary>The settings IgnisDisc uses for a Blu-ray image (UDF 2.50 with a small ISO 9660 tree).</summary>
    private static IsoBuildOptions BluRayOptions() => new(
        VolumeLabel: "FILM", IncludeIso9660: true, IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0250);

    [Fact]
    public void FilmLargerThan4GiB_OnABluRayImage_IsMappedOverItsWholeLength()
    {
        const long size = 4L * 1024 * 1024 * 1024 + 300L * 1024 * 1024; // 4.3 GiB
        var root = CompilationDirectory.CreateRoot();
        var stream = root.AddDirectory("BDMV").AddDirectory("STREAM");
        stream.AddFile(new CompilationFile("00000.m2ts", size, () => new ZeroStream(size)));
        root.AddDirectory("CERTIFICATE").AddFile("id.bdmv", "BDID0200"u8.ToArray());

        using var image = new SparseStream();
        IsoUdfImageBuilder.Build(root, image, BluRayOptions());

        image.Position = 0;
        var map = LbaFileExtentMap.FromIsoStream(image);

        var film = Assert.Single(map, e => e.FilePath == "/BDMV/STREAM/00000.m2ts");
        Assert.Equal(size, film.FileSizeBytes);
        Assert.Equal((int)((size + 2047) / 2048), film.SectorCount);
        int pastFourGiB = film.StartLba + 2_200_000; // ~4.2 GiB into the file
        Assert.True(film.ContainsLba(pastFourGiB));
        Assert.True(film.ContainsLba(film.EndLba));
        Assert.Contains(map, e => e.FilePath == "/CERTIFICATE/id.bdmv");

        // What the map held before: the ISO 9660 tree alone stops just under 4 GiB into the film.
        image.Position = 0;
        using var reader = IsoImageReader.FromStream(image, leaveOpen: true);
        var isoOnly = LbaFileExtentMap.FromIsoReader(reader);
        Assert.DoesNotContain(isoOnly, e => e.ContainsLba(pastFourGiB));
    }

    [Fact]
    public void UdfOnlyImage_IsMapped_WithFullNames()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("BDMV").AddDirectory("STREAM").AddFile("00001.m2ts", new byte[100_000]);
        root.AddFile("a long file name that ISO 9660 could not hold.txt", "text"u8.ToArray());
        byte[] bytes = IsoUdfImageBuilder.BuildToMemoryStream(root, BluRayOptions()).ToArray();
        // Blank the ISO 9660 volume descriptors: a UDF-only image, as many Blu-ray authoring tools write.
        for (int lba = 16; lba < 32; lba++)
        {
            var sector = bytes.AsSpan(lba * 2048, 2048);
            if (sector.Slice(1, 5).SequenceEqual("CD001"u8))
            {
                sector.Clear();
            }
        }
        using var image = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => IsoImageReader.FromStream(image, leaveOpen: true));

        image.Position = 0;
        var map = LbaFileExtentMap.FromIsoStream(image);

        Assert.Contains(map, e => e.FilePath == "/BDMV/STREAM/00001.m2ts" && e.SectorCount == 49);
        Assert.Contains(map, e => e.FilePath == "/a long file name that ISO 9660 could not hold.txt" && e.SectorCount == 1);
        Assert.Equal(0, image.Position);
    }

    [Fact]
    public void BridgeImage_ListsEachFileOnce_AndLeavesEmptyFilesOut()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("data.bin", new byte[10_000]);
        root.AddFile("empty.txt", Array.Empty<byte>());
        root.AddDirectory("Docs").AddFile("Report.pdf", new byte[5_000]);
        using var image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions());

        var map = LbaFileExtentMap.FromIsoStream(image);

        Assert.Equal(2, map.Count);
        Assert.Contains(map, e => e.FilePath == "/data.bin" && e.SectorCount == 5);
        Assert.Contains(map, e => e.FilePath == "/Docs/Report.pdf" && e.SectorCount == 3);
    }

    [Fact]
    public void ImageWithNoFileSystem_StillThrows()
    {
        using var noise = new MemoryStream(new byte[300 * 2048]);
        Assert.Throws<InvalidDataException>(() => LbaFileExtentMap.FromIsoStream(noise));
    }

    /// <summary>Read-only stream of zeros with a fixed length (stands in for a huge source file).</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Seekable in-memory stream that only stores non-zero 2 KiB blocks, so multi-GiB images fit in RAM.</summary>
    private sealed class SparseStream : Stream
    {
        private const int Block = 2048;
        private readonly Dictionary<long, byte[]> _blocks = new();
        private long _length;
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                long blockIndex = _position / Block;
                int inBlock = (int)(_position % Block);
                int n = Math.Min(count, Block - inBlock);
                var span = buffer.AsSpan(offset, n);
                if (_blocks.TryGetValue(blockIndex, out var existing))
                {
                    span.CopyTo(existing.AsSpan(inBlock));
                }
                else if (span.ContainsAnyExcept((byte)0))
                {
                    var block = new byte[Block];
                    span.CopyTo(block.AsSpan(inBlock));
                    _blocks[blockIndex] = block;
                }
                _position += n;
                offset += n;
                count -= n;
                _length = Math.Max(_length, _position);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            count = (int)Math.Min(count, Math.Max(0, _length - _position));
            while (count > 0)
            {
                long blockIndex = _position / Block;
                int inBlock = (int)(_position % Block);
                int n = Math.Min(count, Block - inBlock);
                if (_blocks.TryGetValue(blockIndex, out var block))
                {
                    block.AsSpan(inBlock, n).CopyTo(buffer.AsSpan(offset, n));
                }
                else
                {
                    Array.Clear(buffer, offset, n);
                }
                _position += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => _length = value;
    }
}
