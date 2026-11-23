using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Read-only, seekable stream bounded to a contiguous byte range inside an underlying seekable stream.
/// Coordinates reads under the base stream's monitor so multiple open file streams can read concurrently.
/// </summary>
internal sealed class BoundedSegmentStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _startOffset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;
    private bool _disposed;

    public BoundedSegmentStream(Stream baseStream, long startOffset, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (!baseStream.CanRead || !baseStream.CanSeek)
        {
            throw new ArgumentException("The base stream must be readable and seekable.", nameof(baseStream));
        }

        _baseStream = baseStream;
        _startOffset = startOffset;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && _baseStream.CanSeek;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _length - _position);
        int read;
        lock (_baseStream)
        {
            long previousPosition = _baseStream.Position;
            try
            {
                _baseStream.Position = _startOffset + _position;
                read = _baseStream.Read(buffer[..toRead]);
            }
            finally
            {
                _baseStream.Position = previousPosition;
            }
        }

        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
            {
                _baseStream.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Read-only, seekable stream spanning a sequence of sector-addressed file extents inside an ISO 9660 / Joliet volume.
/// </summary>
internal sealed class MultiExtentSegmentStream : Stream
{
    private readonly Stream _baseStream;
    private readonly IReadOnlyList<(int StartLba, long SizeBytes)> _extents;
    private readonly long[] _cumulativeOffsets;
    private readonly long _totalLength;
    private long _position;
    private bool _disposed;

    public MultiExtentSegmentStream(Stream baseStream, IReadOnlyList<(int StartLba, long SizeBytes)> extents)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(extents);
        if (!baseStream.CanRead || !baseStream.CanSeek)
        {
            throw new ArgumentException("The base stream must be readable and seekable.", nameof(baseStream));
        }

        _baseStream = baseStream;
        _extents = extents;
        _cumulativeOffsets = new long[extents.Count + 1];
        long total = 0;
        for (int i = 0; i < extents.Count; i++)
        {
            _cumulativeOffsets[i] = total;
            total += extents[i].SizeBytes;
        }
        _cumulativeOffsets[extents.Count] = total;
        _totalLength = total;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && _baseStream.CanSeek;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _totalLength;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _totalLength)
        {
            return 0;
        }

        int totalRead = 0;
        while (totalRead < buffer.Length && _position < _totalLength)
        {
            int idx = FindExtentIndex(_position);
            if (idx < 0 || idx >= _extents.Count)
            {
                break;
            }

            long offsetInExtent = _position - _cumulativeOffsets[idx];
            long extentRemaining = _extents[idx].SizeBytes - offsetInExtent;
            int toRead = (int)Math.Min(buffer.Length - totalRead, extentRemaining);
            if (toRead <= 0)
            {
                break;
            }

            long streamOffset = (long)_extents[idx].StartLba * OpticalConstants.DataSectorSize + offsetInExtent;
            int read;
            lock (_baseStream)
            {
                long previousPosition = _baseStream.Position;
                try
                {
                    _baseStream.Position = streamOffset;
                    read = _baseStream.Read(buffer.Slice(totalRead, toRead));
                }
                finally
                {
                    _baseStream.Position = previousPosition;
                }
            }

            if (read == 0)
            {
                break;
            }

            _position += read;
            totalRead += read;
        }

        return totalRead;
    }

    private int FindExtentIndex(long position)
    {
        int lo = 0;
        int hi = _extents.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (position < _cumulativeOffsets[mid])
            {
                hi = mid - 1;
            }
            else if (position >= _cumulativeOffsets[mid + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }
        return -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _totalLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
