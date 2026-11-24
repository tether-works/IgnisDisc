using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// A CD-ROM Mode 1 or Mode 2 XA data track from a CUE sheet's BINARY file: track 1 of a mixed-mode CD (data, then audio).
/// Mode 1 files hold 2,048-byte or 2,352-byte sectors (sending 2,048 bytes per sector to the writer); Mode 2 XA files
/// hold 2,336-byte or 2,352-byte sectors (sending 2,336 bytes per sector: 8-byte XA sub-header + 2,328 payload bytes).
/// </summary>
/// <param name="FilePath">The BINARY file.</param>
/// <param name="OffsetBytes">Where the track's INDEX 01 starts in the file.</param>
/// <param name="SectorCount">Sectors of the track kept from the file.</param>
/// <param name="SectorSize">2,048, 2,336, or 2,352 bytes a sector in the file.</param>
/// <param name="Mode">CD-ROM sector mode: 1 (Mode 1) or 2 (Mode 2 XA).</param>
public sealed record DataTrackImage(string FilePath, long OffsetBytes, int SectorCount, int SectorSize, int Mode = 1)
{
    public const int UserDataSize = 2048;
    public const int Mode2XaPayloadSize = 2336;
    private const int RawHeaderSize = 16; // sync (12) + header (4)
    private const int Mode2SubheaderSize = 8;

    /// <summary>Bytes of user data (2,048 per sector).</summary>
    public long UserDataBytes => (long)SectorCount * UserDataSize;

    /// <summary>Bytes per sector sent to the MMC writer (2,048 for Mode 1, 2,336 for Mode 2 XA).</summary>
    public int WriterSectorSize => Mode == 2 ? Mode2XaPayloadSize : UserDataSize;

    /// <summary>Total bytes sent to the MMC writer.</summary>
    public long WriterPayloadBytes => (long)SectorCount * WriterSectorSize;

    /// <summary>
    /// Describes the data track at <paramref name="offsetBytes"/> in <paramref name="filePath"/>, checking that the file
    /// is long enough and, for raw sectors, that the first one matches <paramref name="mode"/>.
    /// </summary>
    public static DataTrackImage Open(string filePath, long offsetBytes, int sectorCount, int sectorSize, int mode = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (mode is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Data track mode must be 1 (Mode 1) or 2 (Mode 2 XA).");
        }
        if (mode == 1 && sectorSize is not (UserDataSize or OpticalConstants.RawSectorSize))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "A Mode 1 data track has 2,048- or 2,352-byte sectors.");
        }
        if (mode == 2 && sectorSize is not (Mode2XaPayloadSize or OpticalConstants.RawSectorSize))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "A Mode 2 XA data track has 2,336- or 2,352-byte sectors.");
        }
        if (sectorCount <= 0)
        {
            throw new InvalidDataException($"The data track in '{Path.GetFileName(filePath)}' is empty.");
        }

        long length = new FileInfo(filePath).Length;
        if (offsetBytes < 0 || offsetBytes + (long)sectorCount * sectorSize > length)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(filePath)}' ends before its data track does ({sectorCount:N0} sectors of {sectorSize} bytes from byte {offsetBytes:N0}).");
        }

        if (sectorSize == OpticalConstants.RawSectorSize)
        {
            byte[] first = new byte[RawHeaderSize];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = offsetBytes;
            fs.ReadExactly(first);
            bool sync = first[0] == 0 && first[11] == 0 && first.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0;
            if (!sync)
            {
                throw new InvalidDataException(
                    $"The data track in '{Path.GetFileName(filePath)}' does not start with a CD-ROM sector: the cue sheet's MODE{mode}/2352 does not match the file.");
            }
            if (first[15] != mode)
            {
                throw new InvalidDataException(
                    $"The data track in '{Path.GetFileName(filePath)}' holds Mode {first[15]} sectors, not Mode {mode}.");
            }
        }

        return new DataTrackImage(filePath, offsetBytes, sectorCount, sectorSize, mode);
    }

    /// <summary>
    /// How many sectors at the end of the range do not match <see cref="Mode"/> (no sync pattern and matching mode header),
    /// up to <paramref name="max"/>. In a cue sheet without INDEX 00 the range of track 1 runs to track 2's INDEX 01, so
    /// it ends with track 2's audio pre-gap. Only for raw 2,352-byte sectors; 0 otherwise.
    /// </summary>
    public int CountTrailingNonDataSectors(int max)
    {
        if (SectorSize != OpticalConstants.RawSectorSize)
        {
            return 0;
        }
        byte[] header = new byte[RawHeaderSize];
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int count = 0;
        while (count < max && count < SectorCount - 1)
        {
            fs.Position = OffsetBytes + (long)(SectorCount - 1 - count) * SectorSize;
            fs.ReadExactly(header);
            bool matchesMode = header[0] == 0 && header[11] == 0 && header.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0 && header[15] == Mode;
            if (matchesMode)
            {
                break;
            }
            count++;
        }
        return count;
    }

    /// <summary>Opens the track's user data: <see cref="UserDataSize"/> bytes per sector, in order.</summary>
    public Stream OpenUserData()
    {
        int sliceOffset = (Mode, SectorSize) switch
        {
            (1, UserDataSize) => 0,
            (1, _) => RawHeaderSize,
            (2, Mode2XaPayloadSize) => Mode2SubheaderSize,
            _ => RawHeaderSize + Mode2SubheaderSize,
        };
        return new SectorSliceStream(this, sliceOffset, UserDataSize);
    }

    /// <summary>
    /// Opens the track's writer payload: 2,048 bytes per sector for Mode 1, or 2,336 bytes per sector (8-byte XA
    /// sub-header + 2,328 payload bytes) for Mode 2 XA.
    /// </summary>
    public Stream OpenWriterData()
    {
        if (Mode == 1)
        {
            return OpenUserData();
        }
        int sliceOffset = SectorSize == Mode2XaPayloadSize ? 0 : RawHeaderSize;
        return new SectorSliceStream(this, sliceOffset, Mode2XaPayloadSize);
    }

    /// <summary>
    /// How many sectors at the end of the track hold only empty gap content, up to <paramref name="max"/>. A rip of a
    /// mixed-mode CD keeps the data track's two-second post-gap at its end; the writer adds that gap itself.
    /// </summary>
    public int CountTrailingZeroSectors(int max)
    {
        byte[] sector = new byte[SectorSize];
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int zeros = 0;
        while (zeros < max && zeros < SectorCount)
        {
            fs.Position = OffsetBytes + (long)(SectorCount - 1 - zeros) * SectorSize;
            fs.ReadExactly(sector);
            if (Mode == 1)
            {
                int start = SectorSize == UserDataSize ? 0 : RawHeaderSize;
                if (sector.AsSpan(start, UserDataSize).IndexOfAnyExcept((byte)0) >= 0)
                {
                    break;
                }
            }
            else
            {
                int subStart = SectorSize == Mode2XaPayloadSize ? 0 : RawHeaderSize;
                if (!sector.AsSpan(subStart, Mode2SubheaderSize).SequenceEqual(Images.RawSectorCodec.Mode2XaEmptyForm1Subheader) ||
                    sector.AsSpan(subStart + Mode2SubheaderSize, UserDataSize).IndexOfAnyExcept((byte)0) >= 0)
                {
                    break;
                }
            }
            zeros++;
        }
        return zeros;
    }

    /// <summary>Reads a fixed byte slice from each sector in turn (forward only).</summary>
    private sealed class SectorSliceStream : Stream
    {
        private readonly DataTrackImage _image;
        private readonly int _sliceOffset;
        private readonly int _sliceSize;
        private readonly FileStream _file;
        private readonly byte[] _sector;
        private int _sectorIndex;
        private int _posInSector;
        private long _delivered;

        public SectorSliceStream(DataTrackImage image, int sliceOffset, int sliceSize)
        {
            _image = image;
            _sliceOffset = sliceOffset;
            _sliceSize = sliceSize;
            _posInSector = sliceSize; // nothing buffered yet
            _file = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _file.Position = image.OffsetBytes;
            _sector = new byte[image.SectorSize];
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                if (_posInSector == _sliceSize)
                {
                    if (_sectorIndex == _image.SectorCount)
                    {
                        break;
                    }
                    _file.ReadExactly(_sector);
                    _sectorIndex++;
                    _posInSector = 0;
                }
                int n = Math.Min(buffer.Length - total, _sliceSize - _posInSector);
                _sector.AsSpan(_sliceOffset + _posInSector, n).CopyTo(buffer.Slice(total));
                _posInSector += n;
                total += n;
            }
            _delivered += total;
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => (long)_image.SectorCount * _sliceSize;
        public override long Position
        {
            get => _delivered;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Audio straight from a CUE sheet's BINARY (little-endian) or MOTOROLA (big-endian) file: 16-bit stereo samples at
/// 44.1 kHz with no header, as a CD stores them. No decoder is involved; a track is a range of bytes in the file.
/// </summary>
public static class RawCdAudioFile
{
    /// <summary>The CUE sheet FILE types whose bytes are raw CD sectors.</summary>
    public static bool IsRawType(string fileType) =>
        string.Equals(fileType, "BINARY", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileType, "MOTOROLA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A factory for the audio from <paramref name="startFrame"/> for <paramref name="frameCount"/> frames (to the end of
    /// the file when null), as little-endian Red Book PCM.
    /// </summary>
    public static Func<Stream> CreateSliceFactory(string filePath, int startFrame, int? frameCount, bool bigEndian) => () =>
    {
        long fileFrames = new FileInfo(filePath).Length / OpticalConstants.RawSectorSize;
        long start = Math.Clamp(startFrame, 0, fileFrames);
        long frames = frameCount.HasValue ? Math.Clamp(frameCount.Value, 0, fileFrames - start) : fileFrames - start;
        return new FileSliceStream(filePath, start * OpticalConstants.RawSectorSize, frames * OpticalConstants.RawSectorSize, bigEndian);
    };

    /// <summary>Whole frames in the file.</summary>
    public static int FrameCount(string filePath) =>
        (int)Math.Min(int.MaxValue, new FileInfo(filePath).Length / OpticalConstants.RawSectorSize);

    /// <summary>A read-only, seekable window onto part of a file, with 16-bit samples byte-swapped when asked.</summary>
    private sealed class FileSliceStream : Stream
    {
        private readonly FileStream _file;
        private readonly long _start;
        private readonly long _length;
        private readonly bool _swap;
        private long _position;

        public FileSliceStream(string path, long start, long length, bool swap)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _start = start;
            _length = length;
            _swap = swap;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            long left = _length - _position;
            if (left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer.Slice(0, (int)left);

            if (!_swap)
            {
                _file.Position = _start + _position;
                int n = _file.Read(buffer);
                _position += n;
                return n;
            }

            int total = 0;

            // If starting on an odd offset, deliver the high byte of the enclosing big-endian 16-bit sample first.
            if ((_position & 1) != 0)
            {
                _file.Position = _start + _position - 1;
                int b = _file.ReadByte();
                if (b < 0) return 0;
                buffer[0] = (byte)b;
                _position++;
                total++;
                buffer = buffer.Slice(1);
                if (buffer.IsEmpty) return total;
            }

            // _position is now even: swap aligned 2-byte pairs in place.
            int evenLen = buffer.Length & ~1;
            if (evenLen > 0)
            {
                _file.Position = _start + _position;
                int n = _file.Read(buffer.Slice(0, evenLen));
                if (n == 0) return total;

                if ((n & 1) != 0)
                {
                    int extra = _file.ReadByte();
                    if (extra >= 0)
                    {
                        buffer[n] = (byte)extra;
                        n++;
                    }
                    else
                    {
                        n &= ~1;
                    }
                }

                for (int i = 0; i + 1 < n; i += 2)
                {
                    (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
                }

                _position += n;
                total += n;
                if (n < evenLen || buffer.Length == evenLen) return total;
            }

            // One trailing byte requested at an even _position: read its 16-bit pair and emit the low byte.
            if (_position + 1 < _length)
            {
                Span<byte> pair = stackalloc byte[2];
                _file.Position = _start + _position;
                int pairRead = _file.Read(pair);
                if (pairRead == 2)
                {
                    buffer[^1] = pair[1];
                    _position++;
                    total++;
                }
                else if (pairRead == 1)
                {
                    buffer[^1] = pair[0];
                    _position++;
                    total++;
                }
            }
            else
            {
                _file.Position = _start + _position;
                int b = _file.ReadByte();
                if (b >= 0)
                {
                    buffer[^1] = (byte)b;
                    _position++;
                    total++;
                }
            }

            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
