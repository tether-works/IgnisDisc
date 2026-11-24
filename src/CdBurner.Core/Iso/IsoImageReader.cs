using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Abstract base class for all file and directory entries parsed from an ISO 9660 or Joliet volume image.
/// </summary>
public abstract class IsoEntry
{
    public string Name { get; init; } = string.Empty;
    public string VirtualPath { get; init; } = string.Empty;
    public int StartLba { get; init; }
    public long SizeBytes { get; init; }
    public DateTime RecordingTimeUtc { get; set; }
    public bool IsDirectory { get; init; }
    public bool IsHidden { get; set; }

    public override string ToString() => $"[{(IsDirectory ? "Dir" : "File")}] {VirtualPath} (LBA: {StartLba}, Size: {SizeBytes} B)";
}

/// <summary>
/// Represents a file entry in an ISO 9660 / Joliet filesystem image.
/// </summary>
public class IsoFileEntry : IsoEntry
{
    internal IReadOnlyList<(int StartLba, long SizeBytes)>? Extents { get; init; }
}

/// <summary>
/// Represents a directory entry containing child files and subdirectories.
/// </summary>
public class IsoDirectoryEntry : IsoEntry
{
    public List<IsoEntry> Children { get; } = new();

    public IEnumerable<IsoFileEntry> Files => Children.OfType<IsoFileEntry>();
    public IEnumerable<IsoDirectoryEntry> SubDirectories => Children.OfType<IsoDirectoryEntry>();
}

/// <summary>
/// Reads, inspects, and verifies optical disc images (.iso) complying with ISO 9660, Microsoft Joliet,
/// and ECMA-167 / UDF Bridge formats.
/// </summary>
public sealed class IsoImageReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;
    private bool _udfLoaded;
    private Dictionary<string, IsoFileEntry>? _udfFilesExact;
    private Dictionary<string, IsoFileEntry>? _udfFilesIgnoreCase;
    private List<string> _udfSkipped = new();

    /// <summary>
    /// Gets the starting LBA on the optical medium of the session being read.
    /// </summary>
    public int SessionStartLba { get; }

    /// <summary>
    /// Gets the primary volume label of the image (prefers Joliet Unicode label if present).
    /// </summary>
    public string VolumeLabel { get; }

    /// <summary>
    /// Gets the volume label stored in the Primary Volume Descriptor (ASCII).
    /// </summary>
    public string PvdVolumeLabel { get; } = string.Empty;

    /// <summary>
    /// Gets the volume label stored in the Joliet Supplementary Volume Descriptor (UCS-2BE), if present.
    /// </summary>
    public string JolietVolumeLabel { get; } = string.Empty;

    /// <summary>
    /// Gets whether the ISO image contains a valid ISO 9660 Primary Volume Descriptor at LBA 16.
    /// </summary>
    public bool HasIso9660 { get; }

    /// <summary>
    /// Gets whether the ISO image contains a valid Microsoft Joliet Supplementary Volume Descriptor.
    /// </summary>
    public bool HasJoliet { get; }

    /// <summary>
    /// Gets whether the ISO image contains ECMA-167 / UDF bridge descriptors (such as AVDP at LBA 256).
    /// </summary>
    public bool HasUdf { get; }

    /// <summary>
    /// Total number of 2048-byte logical sectors recorded in the volume space.
    /// </summary>
    public int TotalSectors { get; }

    /// <summary>
    /// Total byte size of the volume space (TotalSectors * 2048).
    /// </summary>
    public long TotalBytes => (long)TotalSectors * OpticalConstants.DataSectorSize;

    /// <summary>
    /// Root directory of the image (Joliet directory if present, otherwise ISO 9660 directory).
    /// </summary>
    public IsoDirectoryEntry RootDirectory { get; }

    /// <summary>
    /// ISO 9660 root directory tree parsed from the Primary Volume Descriptor.
    /// </summary>
    public IsoDirectoryEntry? IsoRootDirectory { get; }

    /// <summary>
    /// Joliet root directory tree parsed from the Supplementary Volume Descriptor (if present).
    /// </summary>
    public IsoDirectoryEntry? JolietRootDirectory { get; }

    /// <summary>
    /// Flat list of all files in the active root directory tree.
    /// </summary>
    public IReadOnlyList<IsoFileEntry> AllFiles { get; }

    /// <summary>
    /// Flat list of all directories in the active root directory tree.
    /// </summary>
    public IReadOnlyList<IsoDirectoryEntry> AllDirectories { get; }

    /// <summary>
    /// Opens and parses an ISO image from a file on disk.
    /// </summary>
    public static IsoImageReader FromFile(string isoPath, int sessionStartLba = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: '{isoPath}'", isoPath);
        }

        var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new IsoImageReader(stream, leaveOpen: false, sessionStartLba);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens and parses an ISO image from a stream.
    /// </summary>
    public static IsoImageReader FromStream(Stream stream, bool leaveOpen = false, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new IsoImageReader(stream, leaveOpen, sessionStartLba);
    }

    /// <summary>
    /// Opens an image that may be damaged: when LBA 16 holds no Primary Volume Descriptor, a copy written by
    /// IgnisDisc's archive layout (see <see cref="PrimaryVolumeDescriptorCopy"/>) is used instead, and
    /// <see cref="DescriptorCopyLba"/> says where it was found. First session only.
    /// </summary>
    public static IsoImageReader FromDamagedStream(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new IsoImageReader(stream, leaveOpen, 0, allowDescriptorCopy: true);
    }

    /// <summary>
    /// The sector the Primary Volume Descriptor was read from when LBA 16 was unreadable (only with
    /// <see cref="FromDamagedStream"/>); null when LBA 16 was used.
    /// </summary>
    public int? DescriptorCopyLba { get; }

    private IsoImageReader(Stream stream, bool leaveOpen, int sessionStartLba = 0, bool allowDescriptorCopy = false)
    {
        _leaveOpen = leaveOpen;
        SessionStartLba = sessionStartLba;

        if (!stream.CanRead)
        {
            throw new ArgumentException("The ISO image stream must be readable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            // Buffer into seekable MemoryStream if stream cannot seek
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            _stream = ms;
            _leaveOpen = false;
        }
        else
        {
            _stream = stream;
        }

        try
        {
            if (_stream.Length < (long)(sessionStartLba + 17) * OpticalConstants.DataSectorSize)
            {
                throw new InvalidDataException("The stream is too short to be a valid ISO 9660 optical image.");
            }

            // 1. Read Primary Volume Descriptor at sessionStartLba + 16
            byte[] pvd = new byte[OpticalConstants.DataSectorSize];
            _stream.Position = (long)(sessionStartLba + 16) * OpticalConstants.DataSectorSize;
            _stream.ReadExactly(pvd);

            bool atSixteen = pvd[0] == 0x01 && Encoding.ASCII.GetString(pvd.AsSpan(1, 5)) == "CD001";
            if (!atSixteen && allowDescriptorCopy && PrimaryVolumeDescriptorCopy.Find(_stream) is { } copy)
            {
                pvd = copy.Descriptor;
                DescriptorCopyLba = copy.Lba;
            }

            if (pvd[0] == 0x01 && Encoding.ASCII.GetString(pvd.AsSpan(1, 5)) == "CD001")
            {
                HasIso9660 = true;
                PvdVolumeLabel = Encoding.ASCII.GetString(pvd.AsSpan(40, 32)).Trim();
                uint rawTotalSectors = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80, 4));
                if (rawTotalSectors > int.MaxValue)
                {
                    throw new InvalidDataException($"Invalid ISO 9660 volume space size: {rawTotalSectors} sectors.");
                }
                TotalSectors = (int)rawTotalSectors;

                uint isoRootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2, 4));
                uint isoRootLength = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10, 4));

                var visitedIso = new HashSet<uint>();
                IsoRootDirectory = ParseDirectoryExtent(
                    isoRootLba,
                    isoRootLength,
                    dirName: string.Empty,
                    virtualPath: "/",
                    isJoliet: false,
                    visitedIso);
            }
            else
            {
                throw new InvalidDataException($"Missing ISO 9660 Primary Volume Descriptor (PVD) at LBA {sessionStartLba + 16}.");
            }

            // 2. Scan for Joliet Supplementary Volume Descriptor and UDF descriptors
            int maxScanLba = Math.Min(sessionStartLba + 32, (int)(_stream.Length / OpticalConstants.DataSectorSize));
            for (int lba = sessionStartLba + 17; lba < maxScanLba; lba++)
            {
                byte[] sector = new byte[OpticalConstants.DataSectorSize];
                _stream.Position = (long)lba * OpticalConstants.DataSectorSize;
                _stream.ReadExactly(sector);

                if (sector[0] == 0x02 && Encoding.ASCII.GetString(sector.AsSpan(1, 5)) == "CD001")
                {
                    // Supplementary Volume Descriptor - check for Joliet UCS-2 escape sequence at 88..90
                    if (sector[88] == 0x25 && sector[89] == 0x2F)
                    {
                        HasJoliet = true;
                        JolietVolumeLabel = Encoding.BigEndianUnicode.GetString(sector.AsSpan(40, 32)).Trim('\0', ' ');

                        uint jolietRootLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 2, 4));
                        uint jolietRootLength = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 10, 4));

                        var visitedJoliet = new HashSet<uint>();
                        JolietRootDirectory = ParseDirectoryExtent(
                            jolietRootLba,
                            jolietRootLength,
                            dirName: string.Empty,
                            virtualPath: "/",
                            isJoliet: true,
                            visitedJoliet);
                    }
                }
                else if (sector[0] == 0xFF && Encoding.ASCII.GetString(sector.AsSpan(1, 5)) == "CD001")
                {
                    // Volume Descriptor Set Terminator
                }
                else if (sector[0] == 0x00)
                {
                    string id = Encoding.ASCII.GetString(sector.AsSpan(1, 5));
                    if (id is "BEA01" or "NSR02" or "NSR03" or "TEA01")
                    {
                        // UDF descriptor present
                    }
                }
            }

            // 3. Check for UDF Anchor Volume Descriptor Pointer (AVDP) at LBA 256 or sessionStartLba + 256
            long avdpLba = sessionStartLba > 0 ? (sessionStartLba + 256) : 256;
            if (_stream.Length >= (avdpLba + 1) * OpticalConstants.DataSectorSize)
            {
                byte[] avdpSector = new byte[OpticalConstants.DataSectorSize];
                _stream.Position = avdpLba * OpticalConstants.DataSectorSize;
                _stream.ReadExactly(avdpSector);

                if (IsValidEcma167Avdp(avdpSector, (uint)avdpLba))
                {
                    HasUdf = true;
                }
            }

            // Configure active root directory and label
            RootDirectory = JolietRootDirectory ?? IsoRootDirectory ?? new IsoDirectoryEntry
            {
                Name = "/",
                VirtualPath = "/",
                IsDirectory = true
            };

            VolumeLabel = HasJoliet && !string.IsNullOrWhiteSpace(JolietVolumeLabel)
                ? JolietVolumeLabel
                : PvdVolumeLabel;

            // Flatten files and directories for quick lookup
            var allFilesList = new List<IsoFileEntry>();
            var allDirsList = new List<IsoDirectoryEntry>();
            CollectEntries(RootDirectory, allFilesList, allDirsList);
            AllFiles = allFilesList.AsReadOnly();
            AllDirectories = allDirsList.AsReadOnly();
        }
        catch
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Finds a file entry by its virtual path (e.g. "/Docs/Manual.txt" or "Docs/Manual.txt").
    /// </summary>
    public IsoFileEntry? FindFile(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return null;
        }

        string normalized = NormalizeVirtualPath(virtualPath);

        // Search primary root tree (Joliet or ISO)
        var file = AllFiles.FirstOrDefault(f => string.Equals(f.VirtualPath, normalized, StringComparison.OrdinalIgnoreCase));
        if (file != null)
        {
            return file;
        }

        // Also check ISO root tree if different from primary
        if (IsoRootDirectory != null && !ReferenceEquals(IsoRootDirectory, RootDirectory))
        {
            var isoFiles = new List<IsoFileEntry>();
            var isoDirs = new List<IsoDirectoryEntry>();
            CollectEntries(IsoRootDirectory, isoFiles, isoDirs);
            file = isoFiles.FirstOrDefault(f => string.Equals(f.VirtualPath, normalized, StringComparison.OrdinalIgnoreCase));
            if (file != null)
            {
                return file;
            }
        }

        return null;
    }

    internal IsoFileEntry? TryFindUdfFile(string virtualPath, out bool udfReadable, out IReadOnlyList<string> udfSkipped)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || !HasUdf || _disposed)
        {
            udfReadable = false;
            udfSkipped = _udfSkipped;
            return null;
        }

        if (!_udfLoaded)
        {
            _udfLoaded = true;
            long savedPos = _stream.CanSeek ? _stream.Position : 0;
            try
            {
                var skipped = new List<string>();
                var udfRoot = UdfTreeReader.TryRead(_stream, skipped, SessionStartLba);
                _udfSkipped = skipped;
                if (udfRoot != null)
                {
                    var exact = new Dictionary<string, IsoFileEntry>(StringComparer.Ordinal);
                    var ignoreCase = new Dictionary<string, IsoFileEntry>(StringComparer.OrdinalIgnoreCase);
                    void Collect(IsoDirectoryEntry dir)
                    {
                        foreach (var entry in dir.Files)
                        {
                            exact.TryAdd(entry.VirtualPath, entry);
                            ignoreCase.TryAdd(entry.VirtualPath, entry);
                        }
                        foreach (var sub in dir.SubDirectories)
                        {
                            Collect(sub);
                        }
                    }
                    Collect(udfRoot);
                    _udfFilesExact = exact;
                    _udfFilesIgnoreCase = ignoreCase;
                }
            }
            finally
            {
                if (_stream.CanSeek)
                {
                    _stream.Position = savedPos;
                }
            }
        }

        udfReadable = _udfFilesExact != null;
        udfSkipped = _udfSkipped;
        if (_udfFilesExact == null)
        {
            return null;
        }

        string normalized = NormalizeVirtualPath(virtualPath);
        if (_udfFilesExact.TryGetValue(normalized, out var hit))
        {
            return hit;
        }
        return _udfFilesIgnoreCase != null && _udfFilesIgnoreCase.TryGetValue(normalized, out var ciHit) ? ciHit : null;
    }

    /// <summary>
    /// Finds a directory entry by its virtual path (e.g. "/Docs" or "Docs").
    /// </summary>
    public IsoDirectoryEntry? FindDirectory(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || virtualPath == "/")
        {
            return RootDirectory;
        }

        string normalized = NormalizeVirtualPath(virtualPath);
        return AllDirectories.FirstOrDefault(d => string.Equals(d.VirtualPath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads and returns the complete file payload bytes for the specified virtual path.
    /// </summary>
    public byte[] ReadFileBytes(string virtualPath)
    {
        var file = FindFile(virtualPath);
        if (file == null)
        {
            throw new FileNotFoundException($"File not found in ISO image: '{virtualPath}'", virtualPath);
        }

        return ReadFileBytes(file);
    }

    /// <summary>
    /// Reads and returns the complete file payload bytes for a given file entry.
    /// </summary>
    public byte[] ReadFileBytes(IsoFileEntry fileEntry)
    {
        ArgumentNullException.ThrowIfNull(fileEntry);

        if (fileEntry.SizeBytes == 0)
        {
            return Array.Empty<byte>();
        }

        if (fileEntry.SizeBytes < 0 || fileEntry.SizeBytes > Array.MaxLength)
        {
            throw new InvalidDataException(
                $"File entry '{fileEntry.VirtualPath}' extent (LBA {fileEntry.StartLba}, size {fileEntry.SizeBytes}) exceeds maximum readable byte array bounds.");
        }

        using Stream stream = OpenFileStream(fileEntry);
        byte[] data = new byte[fileEntry.SizeBytes];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Opens a readable stream for the specified virtual file path.
    /// </summary>
    public Stream OpenFileStream(string virtualPath)
    {
        var file = FindFile(virtualPath);
        if (file == null)
        {
            throw new FileNotFoundException($"File not found in ISO image: '{virtualPath}'", virtualPath);
        }

        return OpenFileStream(file);
    }

    /// <summary>
    /// Opens a readable, seekable streaming handle for a given file entry without buffering the file in memory.
    /// </summary>
    public Stream OpenFileStream(IsoFileEntry fileEntry)
    {
        ArgumentNullException.ThrowIfNull(fileEntry);

        if (fileEntry.SizeBytes == 0)
        {
            return Stream.Null;
        }

        if (fileEntry.Extents is { Count: > 1 } extents)
        {
            bool contiguous = true;
            long expectedNextOffset = (long)extents[0].StartLba * OpticalConstants.DataSectorSize;
            for (int i = 0; i < extents.Count; i++)
            {
                var (extLba, extSize) = extents[i];
                long extOffset = (long)extLba * OpticalConstants.DataSectorSize;
                if (extSize < 0 || extLba < 0 || extOffset > _stream.Length - extSize)
                {
                    throw new InvalidDataException(
                        $"File entry '{fileEntry.VirtualPath}' extent (LBA {extLba}, size {extSize}) exceeds ISO stream bounds ({_stream.Length} bytes).");
                }

                if (i > 0 && extOffset != expectedNextOffset)
                {
                    contiguous = false;
                }
                expectedNextOffset = extOffset + extSize;
            }

            if (!contiguous)
            {
                return new MultiExtentSegmentStream(_stream, extents);
            }
        }

        long startOffset = (long)fileEntry.StartLba * OpticalConstants.DataSectorSize;
        if (fileEntry.SizeBytes < 0 ||
            fileEntry.StartLba < 0 ||
            startOffset > _stream.Length - fileEntry.SizeBytes)
        {
            throw new InvalidDataException(
                $"File entry '{fileEntry.VirtualPath}' extent (LBA {fileEntry.StartLba}, size {fileEntry.SizeBytes}) exceeds ISO stream bounds ({_stream.Length} bytes).");
        }

        return new BoundedSegmentStream(_stream, startOffset, fileEntry.SizeBytes, leaveOpen: true);
    }

    #region Internal Parsing Helpers

    private IsoDirectoryEntry ParseDirectoryExtent(
        uint extentLba,
        uint extentLength,
        string dirName,
        string virtualPath,
        bool isJoliet,
        HashSet<uint> visited,
        int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("ISO directory hierarchy exceeds maximum supported recursion depth (64).");
        }

        if (!visited.Add(extentLba))
        {
            return new IsoDirectoryEntry
            {
                Name = dirName,
                VirtualPath = virtualPath,
                StartLba = (int)extentLba,
                SizeBytes = extentLength,
                IsDirectory = true
            };
        }

        var dirEntry = new IsoDirectoryEntry
        {
            Name = dirName,
            VirtualPath = virtualPath,
            StartLba = (int)extentLba,
            SizeBytes = extentLength,
            IsDirectory = true
        };

        if (extentLength == 0)
        {
            return dirEntry;
        }

        const uint MaxDirectoryExtentBytes = 16 * 1024 * 1024; // 16 MB cap
        long extentOffset = (long)extentLba * OpticalConstants.DataSectorSize;
        if (extentLength > MaxDirectoryExtentBytes || extentOffset < 0 || extentOffset + extentLength > _stream.Length)
        {
            throw new InvalidDataException(
                $"Directory extent for '{virtualPath}' (LBA {extentLba}, length {extentLength}) is out of bounds or exceeds maximum directory extent limit.");
        }

        byte[] buffer = new byte[extentLength];
        lock (_stream)
        {
            _stream.Position = extentOffset;
            _stream.ReadExactly(buffer);
        }

        int offset = 0;
        int bytesRead = buffer.Length;
        PendingMultiExtent? pendingMultiExtent = null;

        void FlushPendingMultiExtent()
        {
            if (pendingMultiExtent == null)
            {
                return;
            }

            dirEntry.Children.Add(new IsoFileEntry
            {
                Name = pendingMultiExtent.Name,
                VirtualPath = pendingMultiExtent.VirtualPath,
                StartLba = pendingMultiExtent.FirstStartLba,
                SizeBytes = pendingMultiExtent.TotalSizeBytes,
                RecordingTimeUtc = pendingMultiExtent.RecordingTimeUtc,
                IsDirectory = false,
                IsHidden = pendingMultiExtent.IsHidden,
                Extents = pendingMultiExtent.Extents.Count > 1 ? pendingMultiExtent.Extents : null
            });
            pendingMultiExtent = null;
        }

        while (offset < bytesRead)
        {
            byte recordLen = buffer[offset];
            if (recordLen == 0)
            {
                // Advance to start of next 2048-byte sector
                int nextSectorOffset = ((offset / OpticalConstants.DataSectorSize) + 1) * OpticalConstants.DataSectorSize;
                if (nextSectorOffset <= offset)
                {
                    break;
                }
                offset = nextSectorOffset;
                continue;
            }

            if (offset + recordLen > bytesRead)
            {
                break;
            }

            var recordSpan = buffer.AsSpan(offset, recordLen);
            uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(recordSpan.Slice(2, 4));
            uint childSize = BinaryPrimitives.ReadUInt32LittleEndian(recordSpan.Slice(10, 4));
            DateTime recTime = ParseIsoDate(recordSpan.Slice(18, 7));
            byte flags = recordSpan[25];
            bool isDir = (flags & 0x02) != 0;
            bool isHidden = (flags & 0x01) != 0;
            bool isMultiExtent = (flags & 0x80) != 0;
            byte identLen = recordSpan[32];

            if (identLen > 0 && offset + 33 + identLen <= bytesRead)
            {
                var identSpan = recordSpan.Slice(33, identLen);

                if (identLen == 1 && identSpan[0] == 0x00)
                {
                    // '.' current directory
                    dirEntry.RecordingTimeUtc = recTime;
                }
                else if (identLen == 1 && identSpan[0] == 0x01)
                {
                    // '..' parent directory
                }
                else
                {
                    string entryName = isJoliet
                        ? Encoding.BigEndianUnicode.GetString(identSpan)
                        : Encoding.ASCII.GetString(identSpan);

                    // Strip version suffix (;1) and trailing padding dots only for ISO 9660 names;
                    // Joliet filenames can legitimately contain ';' and trailing '.'.
                    if (!isJoliet)
                    {
                        int semi = entryName.LastIndexOf(';');
                        if (semi >= 0)
                        {
                            entryName = entryName[..semi].TrimEnd('.');
                        }
                    }

                    entryName = entryName.Replace("/", string.Empty).Replace("\\", string.Empty).Replace("\0", string.Empty);
                    if (string.IsNullOrEmpty(entryName) || entryName is "." or "..")
                    {
                        offset += recordLen;
                        continue;
                    }

                    string childPath = virtualPath == "/" ? $"/{entryName}" : $"{virtualPath}/{entryName}";

                    if (isDir)
                    {
                        FlushPendingMultiExtent();
                        var subDir = ParseDirectoryExtent(
                            childLba,
                            childSize,
                            entryName,
                            childPath,
                            isJoliet,
                            visited,
                            depth + 1);

                        subDir.RecordingTimeUtc = recTime;
                        subDir.IsHidden = isHidden;
                        dirEntry.Children.Add(subDir);
                    }
                    else if (pendingMultiExtent != null && string.Equals(pendingMultiExtent.Name, entryName, StringComparison.Ordinal))
                    {
                        pendingMultiExtent.AddExtent((int)childLba, childSize);
                        if (!isMultiExtent)
                        {
                            FlushPendingMultiExtent();
                        }
                    }
                    else
                    {
                        FlushPendingMultiExtent();
                        if (isMultiExtent)
                        {
                            pendingMultiExtent = new PendingMultiExtent(entryName, childPath, recTime, isHidden, (int)childLba, childSize);
                        }
                        else
                        {
                            var file = new IsoFileEntry
                            {
                                Name = entryName,
                                VirtualPath = childPath,
                                StartLba = (int)childLba,
                                SizeBytes = childSize,
                                RecordingTimeUtc = recTime,
                                IsDirectory = false,
                                IsHidden = isHidden
                            };
                            dirEntry.Children.Add(file);
                        }
                    }
                }
            }

            offset += recordLen;
        }

        FlushPendingMultiExtent();
        return dirEntry;
    }

    private sealed class PendingMultiExtent
    {
        public string Name { get; }
        public string VirtualPath { get; }
        public DateTime RecordingTimeUtc { get; }
        public bool IsHidden { get; }
        public int FirstStartLba { get; }
        public long TotalSizeBytes { get; private set; }
        public List<(int StartLba, long SizeBytes)> Extents { get; } = new();

        public PendingMultiExtent(string name, string virtualPath, DateTime recordingTimeUtc, bool isHidden, int startLba, long sizeBytes)
        {
            Name = name;
            VirtualPath = virtualPath;
            RecordingTimeUtc = recordingTimeUtc;
            IsHidden = isHidden;
            FirstStartLba = startLba;
            AddExtent(startLba, sizeBytes);
        }

        public void AddExtent(int startLba, long sizeBytes)
        {
            Extents.Add((startLba, sizeBytes));
            TotalSizeBytes += sizeBytes;
        }
    }

    private static bool IsValidEcma167Avdp(ReadOnlySpan<byte> sector, uint expectedLocation)
    {
        if (sector.Length < 512)
        {
            return false;
        }

        ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0, 2));
        if (tagId != 0x0002) // TAG_AVDP
        {
            return false;
        }

        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum = (byte)(sum + sector[i]);
            }
        }
        if (sum != sector[4])
        {
            return false;
        }

        uint tagLocation = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(12, 4));
        if (tagLocation != expectedLocation)
        {
            return false;
        }

        ushort expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(8, 2));
        ushort crcLength = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(10, 2));
        if (crcLength == 0 || 16 + crcLength > sector.Length)
        {
            return false;
        }

        return CalculateEcma167Crc(sector.Slice(16, crcLength)) == expectedCrc;
    }

    private static ushort CalculateEcma167Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static DateTime ParseIsoDate(ReadOnlySpan<byte> span)
    {
        try
        {
            int year = 1900 + span[0];
            int month = Math.Clamp((int)span[1], 1, 12);
            int day = Math.Clamp((int)span[2], 1, 31);
            int hour = Math.Clamp((int)span[3], 0, 23);
            int minute = Math.Clamp((int)span[4], 0, 59);
            int second = Math.Clamp((int)span[5], 0, 59);
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static void CollectEntries(IsoDirectoryEntry dir, List<IsoFileEntry> files, List<IsoDirectoryEntry> dirs)
    {
        dirs.Add(dir);
        foreach (var child in dir.Children)
        {
            if (child is IsoFileEntry file)
            {
                files.Add(file);
            }
            else if (child is IsoDirectoryEntry subDir)
            {
                CollectEntries(subDir, files, dirs);
            }
        }
    }

    private static string NormalizeVirtualPath(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }
        return p;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
