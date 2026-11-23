using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Manages multi-session optical disc mastering, session importing, and tree merging.
/// Allows importing previously recorded directory hierarchies, adding/modifying/removing files,
/// and generating new session images that point to existing file extents without duplicating data.
/// </summary>
public static class MultiSessionManager
{
    /// <summary>
    /// Imports the directory tree from a previously recorded optical session on disc or disc image stream.
    /// Files present in the previous session have <see cref="CompilationFile.ImportedStartLba"/> set to their
    /// on-disc LBA so they do not need to be re-written in subsequent sessions.
    /// </summary>
    /// <param name="previousSessionStream">Readable and seekable stream representing the disc medium or session image.</param>
    /// <param name="sessionStartLba">Starting LBA of the session to import (default is 0 for the first session).</param>
    /// <returns>A new <see cref="CompilationDirectory"/> containing the imported filesystem hierarchy.</returns>
    public static CompilationDirectory ImportSessionTree(Stream previousSessionStream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(previousSessionStream);

        using var reader = IsoImageReader.FromStream(previousSessionStream, leaveOpen: true, sessionStartLba);
        var root = CompilationDirectory.CreateRoot();

        PopulateDirectory(
            reader.RootDirectory,
            root,
            (fileStartLba, fileSizeBytes) => CreateImportedFileStream(previousSessionStream, fileStartLba, fileSizeBytes),
            new HashSet<IsoDirectoryEntry>(ReferenceEqualityComparer.Instance),
            new HashSet<int>());
        return root;
    }

    private static CompilationDirectory ImportSessionTreeCore(
        Stream previousSessionStream,
        int sessionStartLba,
        Func<int, long, Stream> streamFactoryProvider)
    {
        using var reader = IsoImageReader.FromStream(previousSessionStream, leaveOpen: true, sessionStartLba);
        var root = CompilationDirectory.CreateRoot();

        PopulateDirectory(
            reader.RootDirectory,
            root,
            streamFactoryProvider,
            new HashSet<IsoDirectoryEntry>(ReferenceEqualityComparer.Instance),
            new HashSet<int>());
        return root;
    }

    /// <summary>
    /// Asynchronously imports the active or specified session directory tree directly from an optical drive recorder backend.
    /// </summary>
    /// <param name="recorder">The optical recorder backend holding the medium.</param>
    /// <param name="engine">The burn engine backend capable of reading sectors from the recorder.</param>
    /// <param name="sessionIndex">Optional 0-based session index to import (defaults to the last session).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new <see cref="CompilationDirectory"/> containing the imported filesystem hierarchy.</returns>
    public static async Task<CompilationDirectory> ImportFromRecorderAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int? sessionIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);

        var status = MediaStatusForWriting.Describe(engine, recorder);
        if (!status.MediaPresent || status.IsBlank)
        {
            throw new InvalidOperationException("No recorded media present in the recorder to import from.");
        }

        // Fast-path: if the recorder provides a direct stream (e.g. VirtualDiscRecorder).
        // Keep the stream open on success because returned CompilationFile StreamFactory delegates capture it;
        // dispose only if importing throws.
        if (recorder.GetType().GetMethod("CreateReadStream")?.Invoke(recorder, null) is Stream fastStream)
        {
            try
            {
                int startLba = DetermineSessionStartLba(recorder, sessionIndex, status);
                return ImportSessionTree(fastStream, startLba);
            }
            catch
            {
                fastStream.Dispose();
                throw;
            }
        }

        // Hardware / generic fallback: read the session's Primary Volume Descriptor first (at +16 sectors)
        // so we bound the read to the session's exact ISO VolumeSpaceSize and never hit TAO run-out/gap sectors.
        int sessionStartLba = DetermineSessionStartLba(recorder, sessionIndex, status);
        int totalSessionSectors = status.UsedSectors > sessionStartLba ? status.UsedSectors - sessionStartLba : 512;

        const int HeaderSectors = 17;
        const int MaxBufferedSessionSectors = 65_536; // 128 MiB in-memory threshold before streaming on demand

        using var memoryStream = new MemoryStream();
        var headerResult = await engine.ReadSectorsAsync(
            recorder,
            startLba: sessionStartLba,
            sectorCount: HeaderSectors,
            raw2352Mode: false,
            sectorConsumer: async (_, memory) =>
            {
                await memoryStream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
            },
            progress: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!headerResult.Success)
        {
            throw new InvalidOperationException($"Failed to read session header sectors at LBA {sessionStartLba}: {headerResult.ErrorMessage}", headerResult.Exception);
        }

        if (memoryStream.Length >= HeaderSectors * OpticalConstants.DataSectorSize)
        {
            byte[] pvd = new byte[OpticalConstants.DataSectorSize];
            memoryStream.Position = 16L * OpticalConstants.DataSectorSize;
            memoryStream.ReadExactly(pvd);
            memoryStream.Position = memoryStream.Length;

            if (pvd[0] == 0x01 && System.Text.Encoding.ASCII.GetString(pvd.AsSpan(1, 5)) == "CD001")
            {
                uint rawVolumeEndLba = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80, 4));
                if (rawVolumeEndLba > (uint)sessionStartLba && rawVolumeEndLba <= int.MaxValue)
                {
                    totalSessionSectors = (int)rawVolumeEndLba - sessionStartLba;
                }
            }
        }

        if (totalSessionSectors <= MaxBufferedSessionSectors)
        {
            int remainingToRead = Math.Max(0, totalSessionSectors - HeaderSectors);
            if (remainingToRead > 0)
            {
                var ripResult = await engine.ReadSectorsAsync(
                    recorder,
                    startLba: sessionStartLba + HeaderSectors,
                    sectorCount: remainingToRead,
                    raw2352Mode: false,
                    sectorConsumer: async (_, memory) =>
                    {
                        await memoryStream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                    },
                    progress: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!ripResult.Success)
                {
                    throw new InvalidOperationException($"Failed to read sectors from media for session import: {ripResult.ErrorMessage}", ripResult.Exception);
                }
            }

            memoryStream.Position = 0;
            using SessionOffsetStream? offsetStream = sessionStartLba > 0
                ? new SessionOffsetStream(memoryStream, (long)sessionStartLba * OpticalConstants.DataSectorSize, leaveOpen: true)
                : null;
            Stream sessionStream = offsetStream ?? (Stream)memoryStream;

            return ImportSessionTreeCore(
                sessionStream,
                sessionStartLba,
                (fileStartLba, fileSizeBytes) => CreateRecorderFileStream(recorder, engine, fileStartLba, fileSizeBytes));
        }

        using var sectorStream = new Images.SectorReadStream(
            (relLba, count, buffer) => ReadRecorderSectorsSync(recorder, engine, sessionStartLba + relLba, count, buffer, cancellationToken),
            totalSessionSectors);
        using SessionOffsetStream? largeOffsetStream = sessionStartLba > 0
            ? new SessionOffsetStream(sectorStream, (long)sessionStartLba * OpticalConstants.DataSectorSize, leaveOpen: true)
            : null;
        Stream largeSessionStream = largeOffsetStream ?? (Stream)sectorStream;

        return ImportSessionTreeCore(
            largeSessionStream,
            sessionStartLba,
            (fileStartLba, fileSizeBytes) => CreateRecorderFileStream(recorder, engine, fileStartLba, fileSizeBytes));
    }

    /// <summary>
    /// Compiles an appended session image stream starting at <paramref name="nextWritableLba"/>.
    /// New volume descriptors and directory records are placed at <paramref name="nextWritableLba"/>,
    /// previously recorded files point directly to their <see cref="CompilationFile.ImportedStartLba"/>
    /// without re-writing their payload bytes, and new/modified files have their data extents allocated
    /// after the descriptors and written to the stream.
    /// </summary>
    /// <param name="mergedRoot">Root directory containing the merged compilation tree.</param>
    /// <param name="nextWritableLba">The starting LBA on media where the new session will be burned.</param>
    /// <param name="options">Optional ISO build options (defaults to ISO 9660 + Joliet, UDF disabled by default for appended sessions).</param>
    /// <returns>A seekable <see cref="MemoryStream"/> containing the new session's sectors, positioned at 0.</returns>
    public static MemoryStream BuildAppendedSessionStream(
        CompilationDirectory mergedRoot,
        int nextWritableLba,
        IsoBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mergedRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(nextWritableLba);

        options ??= new IsoBuildOptions(IncludeUdf: false);
        return IsoUdfImageBuilder.BuildToMemoryStream(mergedRoot, options, sessionStartLba: nextWritableLba);
    }

    /// <summary>
    /// Opens an <see cref="IsoImageReader"/> targeting a specific session on a stream.
    /// </summary>
    public static IsoImageReader OpenSessionReader(Stream stream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return IsoImageReader.FromStream(stream, leaveOpen: true, sessionStartLba);
    }

    #region Tree Traversal Helpers

    private static void PopulateDirectory(
        IsoDirectoryEntry isoDir,
        CompilationDirectory targetDir,
        Func<int, long, Stream> streamFactoryProvider,
        HashSet<IsoDirectoryEntry> visitedDirs,
        HashSet<int> visitedDirLbas)
    {
        if (!visitedDirs.Add(isoDir))
        {
            return;
        }

        if (isoDir.StartLba > 0 && isoDir.SizeBytes > 0 && !visitedDirLbas.Add(isoDir.StartLba))
        {
            return;
        }

        foreach (var child in isoDir.Children)
        {
            if (child is IsoDirectoryEntry subDir)
            {
                if (visitedDirs.Contains(subDir) || (subDir.StartLba > 0 && subDir.SizeBytes > 0 && visitedDirLbas.Contains(subDir.StartLba)))
                {
                    continue;
                }

                var newSubDir = targetDir.AddDirectory(subDir.Name);
                newSubDir.LastWriteTimeUtc = subDir.RecordingTimeUtc;
                if (subDir.IsHidden)
                {
                    newSubDir.Attributes |= FileAttributes.Hidden;
                }

                PopulateDirectory(subDir, newSubDir, streamFactoryProvider, visitedDirs, visitedDirLbas);
            }
            else if (child is IsoFileEntry file)
            {
                int fileStartLba = file.StartLba;
                long fileSizeBytes = file.SizeBytes;

                var compFile = new CompilationFile(
                    name: file.Name,
                    sourceFilePath: string.Empty,
                    sizeBytes: fileSizeBytes,
                    importedStartLba: fileStartLba,
                    streamFactory: () => streamFactoryProvider(fileStartLba, fileSizeBytes),
                    parent: targetDir,
                    lastWriteTimeUtc: file.RecordingTimeUtc,
                    attributes: file.IsHidden ? FileAttributes.Hidden : FileAttributes.Normal);

                targetDir.AddFile(compFile);
            }
        }
    }

    private static Stream CreateImportedFileStream(Stream stream, int startLba, long sizeBytes)
    {
        if (sizeBytes <= 0 || startLba < 0 || !stream.CanSeek)
        {
            return Stream.Null;
        }

        long startOffset = (long)startLba * OpticalConstants.DataSectorSize;
        return new BoundedSegmentStream(stream, startOffset, sizeBytes, leaveOpen: true);
    }

    private static Stream CreateRecorderFileStream(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int startLba,
        long sizeBytes)
    {
        if (sizeBytes <= 0 || startLba < 0)
        {
            return Stream.Null;
        }

        int sectorCount = OpticalConstants.BytesToSectors(sizeBytes, OpticalConstants.DataSectorSize);
        var sectorStream = new Images.SectorReadStream(
            (relLba, count, buffer) => ReadRecorderSectorsSync(recorder, engine, startLba + relLba, count, buffer, CancellationToken.None),
            sectorCount);
        return new BoundedSegmentStream(sectorStream, 0, sizeBytes, leaveOpen: false);
    }

    private static string? ReadRecorderSectorsSync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int startLba,
        int count,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int filled = 0;
        var result = engine.ReadSectorsAsync(
            recorder,
            startLba: startLba,
            sectorCount: count,
            raw2352Mode: false,
            sectorConsumer: (_, memory) =>
            {
                if (filled < count)
                {
                    memory.Span.CopyTo(buffer.AsSpan(filled * OpticalConstants.DataSectorSize));
                    filled++;
                }
                return ValueTask.CompletedTask;
            },
            progress: null,
            cancellationToken: cancellationToken).GetAwaiter().GetResult();

        return result.Success && filled == count ? null : result.ErrorMessage ?? "Failed to read sectors.";
    }

    private static int DetermineSessionStartLba(
        IDiscRecorderBackend recorder,
        int? sessionIndex,
        MediaStatusDto status)
    {
        // If VirtualDiscRecorder, inspect recorded session list
        var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
        if (mediumProp?.GetValue(recorder) is object mediumObj)
        {
            var sessionsProp = mediumObj.GetType().GetProperty("Sessions");
            if (sessionsProp?.GetValue(mediumObj) is System.Collections.IList sessions && sessions.Count > 0)
            {
                int idx = sessionIndex ?? (sessions.Count - 1);
                if (idx >= 0 && idx < sessions.Count)
                {
                    var sessionItem = sessions[idx];
                    var startLbaProp = sessionItem?.GetType().GetProperty("StartLba");
                    if (startLbaProp?.GetValue(sessionItem) is int sLba)
                    {
                        return sLba;
                    }
                }
            }
        }

        if (recorder is IDiscTocProvider tocProvider)
        {
            var dataTracks = tocProvider.ReadTableOfContents().Where(t => !t.IsAudio).ToList();
            if (dataTracks.Count > 0)
            {
                int idx = sessionIndex ?? (dataTracks.Count - 1);
                if (idx >= 0 && idx < dataTracks.Count)
                {
                    return Math.Max(0, dataTracks[idx].StartLba);
                }
            }
        }

        return 0;
    }

    private sealed class SessionOffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _baseOffset;
        private readonly bool _leaveOpen;
        private long _position;
        private bool _disposed;

        public SessionOffsetStream(Stream inner, long baseOffset, bool leaveOpen = false)
        {
            _inner = inner;
            _baseOffset = baseOffset;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _baseOffset + _inner.Length;
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

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty || _position >= Length)
            {
                return 0;
            }

            int totalRead = 0;
            if (_position < _baseOffset)
            {
                int zeroBytes = (int)Math.Min(buffer.Length, _baseOffset - _position);
                buffer.Slice(0, zeroBytes).Clear();
                _position += zeroBytes;
                totalRead += zeroBytes;
                buffer = buffer.Slice(zeroBytes);
            }

            if (!buffer.IsEmpty && _position < Length)
            {
                _inner.Position = _position - _baseOffset;
                int r = _inner.Read(buffer);
                _position += r;
                totalRead += r;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
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
                    _inner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    #endregion
}
