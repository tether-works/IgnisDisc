using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Selects which optical disc filesystem tree to browse or extract from.
/// </summary>
public enum DiscImageFileSystem
{
    Auto = 0,
    Iso9660 = 1,
    Joliet = 2,
    Udf = 3
}

/// <summary>
/// Metadata describing a single file or directory entry inside an optical disc image.
/// </summary>
public sealed record DiscImageEntryInfo(
    string Name,
    string RelativePath,
    string VirtualPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime TimestampUtc,
    FileAttributes Attributes,
    string FlagsSummary,
    int StartLba,
    string FileSystemSource,
    bool IsSymlink = false);

/// <summary>
/// Hierarchical directory tree node returned by <see cref="DiscImageBrowserService.Browse"/>.
/// </summary>
public sealed class DiscImageTreeNode
{
    public required DiscImageEntryInfo Entry { get; init; }
    public List<DiscImageTreeNode> Children { get; } = new();
}

/// <summary>
/// Complete read-only inspection result for a disc image's filesystem hierarchy.
/// </summary>
public sealed record DiscImageBrowseResult(
    string ImagePath,
    string FormatName,
    string VolumeLabel,
    string ActiveFileSystem,
    bool HasIso9660,
    bool HasJoliet,
    bool HasUdf,
    long ImageSizeBytes,
    int SectorCount,
    int DirectoryCount,
    int FileCount,
    DiscImageTreeNode Root,
    IReadOnlyList<DiscImageEntryInfo> Entries,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Options controlling safe file extraction from a disc image.
/// </summary>
public sealed record DiscImageExtractionOptions(
    string OutputDirectory,
    IReadOnlyList<string>? EntryPaths = null,
    bool OverwriteExisting = false,
    DiscImageFileSystem PreferredFileSystem = DiscImageFileSystem.Auto);

/// <summary>
/// Progress snapshot emitted while streaming extracted files from a disc image.
/// </summary>
public sealed record DiscImageExtractionProgress(
    int FilesCompleted,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes,
    string CurrentEntryPath);

/// <summary>
/// Summary of a completed disc image extraction operation.
/// </summary>
public sealed record DiscImageExtractionResult(
    string ImagePath,
    string OutputDirectory,
    string FileSystemUsed,
    int ExtractedFileCount,
    int CreatedDirectoryCount,
    long ExtractedBytes,
    IReadOnlyList<string> ExtractedFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only browser and bounded-memory streaming extractor over ISO 9660, Joliet, UDF, and legacy
/// disc images (.iso, .udf, .nrg, .mds/.mdf, .ccd/.img, .cdi, and .bin/.cue data tracks).
/// </summary>
public sealed class DiscImageBrowserService
{
    private const int StreamCopyBufferSize = 65_536;
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Returns true when <paramref name="filePath"/> has an image extension supported by the browser.
    /// </summary>
    public static bool IsSupportedImageFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".iso" or ".udf" or ".cue" or ".bin" or ".nrg" or ".mds" or ".mdf" or ".ccd" or ".img" or ".cdi";
    }

    /// <summary>
    /// Opens a disc image read-only and returns its directory tree and entry metadata.
    /// </summary>
    public DiscImageBrowseResult Browse(
        string imagePath,
        DiscImageFileSystem preferredFileSystem = DiscImageFileSystem.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{fullPath}'.", fullPath);
        }

        using var opened = OpenImageSession(fullPath, preferredFileSystem);
        return opened.BuildBrowseResult();
    }

    /// <summary>
    /// Attempts to browse <paramref name="imagePath"/> without throwing on non-ISO/UDF or audio images.
    /// </summary>
    public bool TryBrowse(
        string imagePath,
        out DiscImageBrowseResult? result,
        DiscImageFileSystem preferredFileSystem = DiscImageFileSystem.Auto)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !IsSupportedImageFile(imagePath))
        {
            return false;
        }

        try
        {
            result = Browse(imagePath, preferredFileSystem);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Safely extracts all entries or selected <see cref="DiscImageExtractionOptions.EntryPaths"/> from
    /// <paramref name="imagePath"/> using bounded streaming RAM and strict extraction path validation.
    /// </summary>
    public async Task<DiscImageExtractionResult> ExtractAsync(
        string imagePath,
        DiscImageExtractionOptions options,
        IProgress<DiscImageExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);

        string fullImagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullImagePath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{fullImagePath}'.", fullImagePath);
        }

        string fullOutputRoot = ValidateOutputDirectoryRoot(options.OutputDirectory);

        List<string>? normalizedSelections = null;
        if (options.EntryPaths is { Count: > 0 })
        {
            normalizedSelections = new List<string>(options.EntryPaths.Count);
            foreach (string rawEntry in options.EntryPaths)
            {
                normalizedSelections.Add(NormalizeAndValidateRequestedEntryPath(rawEntry));
            }
        }

        using var opened = OpenImageSession(fullImagePath, options.PreferredFileSystem);
        var browse = opened.BuildBrowseResult();

        var directoriesToCreate = new List<DiscImageEntryInfo>();
        var filesToExtract = new List<(DiscImageEntryInfo Entry, IsoFileEntry SourceEntry)>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (normalizedSelections == null || normalizedSelections.Count == 0)
        {
            foreach (var entry in browse.Entries)
            {
                if (entry.IsDirectory)
                {
                    if (seenDirs.Add(entry.RelativePath))
                    {
                        directoriesToCreate.Add(entry);
                    }
                }
                else
                {
                    if (seenFiles.Add(entry.RelativePath) && opened.SourceFilesByRelativePath.TryGetValue(entry.RelativePath, out var isoEntry))
                    {
                        filesToExtract.Add((entry, isoEntry));
                    }
                }
            }
        }
        else
        {
            var dirLookup = browse.Entries
                .Where(e => e.IsDirectory)
                .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            var fileLookup = browse.Entries
                .Where(e => !e.IsDirectory)
                .ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

            foreach (string requested in normalizedSelections)
            {
                if (fileLookup.TryGetValue(requested, out var matchedFile))
                {
                    if (seenFiles.Add(matchedFile.RelativePath) &&
                        opened.SourceFilesByRelativePath.TryGetValue(matchedFile.RelativePath, out var isoFile))
                    {
                        filesToExtract.Add((matchedFile, isoFile));
                    }
                    continue;
                }

                if (dirLookup.TryGetValue(requested, out var matchedDir))
                {
                    if (seenDirs.Add(matchedDir.RelativePath))
                    {
                        directoriesToCreate.Add(matchedDir);
                    }

                    string prefix = matchedDir.RelativePath + "/";
                    foreach (var entry in browse.Entries)
                    {
                        if (!entry.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (entry.IsDirectory)
                        {
                            if (seenDirs.Add(entry.RelativePath))
                            {
                                directoriesToCreate.Add(entry);
                            }
                        }
                        else if (seenFiles.Add(entry.RelativePath) &&
                                 opened.SourceFilesByRelativePath.TryGetValue(entry.RelativePath, out var isoChild))
                        {
                            filesToExtract.Add((entry, isoChild));
                        }
                    }
                    continue;
                }

                throw new FileNotFoundException(
                    $"Entry '{requested}' was not found in disc image '{Path.GetFileName(fullImagePath)}'.",
                    requested);
            }
        }

        // Validate every target directory and file path before creating or writing anything on disk.
        var validatedDirectories = new List<string>(directoriesToCreate.Count);
        foreach (var dirEntry in directoriesToCreate)
        {
            validatedDirectories.Add(ValidateExtractionPath(fullOutputRoot, dirEntry.RelativePath, dirEntry.IsSymlink));
        }

        var validatedFiles = new List<(DiscImageEntryInfo Entry, IsoFileEntry SourceEntry, string DestinationPath)>(filesToExtract.Count);
        foreach (var (entry, sourceEntry) in filesToExtract)
        {
            string destinationPath = ValidateExtractionPath(fullOutputRoot, entry.RelativePath, entry.IsSymlink);
            validatedFiles.Add((entry, sourceEntry, destinationPath));
        }

        Directory.CreateDirectory(fullOutputRoot);
        int createdDirCount = 0;
        foreach (string dirPath in validatedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dirPath))
            {
                EnsureSafeDirectoryChain(fullOutputRoot, dirPath);
                createdDirCount++;
            }
        }

        long totalBytes = validatedFiles.Sum(f => Math.Max(0L, f.Entry.SizeBytes));
        long bytesCopied = 0;
        int filesCompleted = 0;
        var extractedPaths = new List<string>(validatedFiles.Count);

        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(StreamCopyBufferSize);
        try
        {
            foreach (var (entry, sourceEntry, destinationPath) in validatedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string parentDir = Path.GetDirectoryName(destinationPath)!;
                if (!Directory.Exists(parentDir))
                {
                    EnsureSafeDirectoryChain(fullOutputRoot, parentDir);
                }

                // Re-validate immediately before opening the output file in case a directory link exists.
                ValidateExtractionPath(fullOutputRoot, entry.RelativePath, entry.IsSymlink);

                long fileBytesCopied = await ExtractSingleFileAsync(
                    opened,
                    entry,
                    sourceEntry,
                    destinationPath,
                    options.OverwriteExisting,
                    copyBuffer,
                    delta =>
                    {
                        bytesCopied += delta;
                        progress?.Report(new DiscImageExtractionProgress(
                            filesCompleted,
                            validatedFiles.Count,
                            bytesCopied,
                            totalBytes,
                            entry.RelativePath));
                    },
                    cancellationToken).ConfigureAwait(false);

                if (entry.TimestampUtc > DateTime.MinValue && entry.TimestampUtc.Year >= 1980)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(destinationPath, entry.TimestampUtc);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }
                }

                filesCompleted++;
                extractedPaths.Add(entry.RelativePath);
                progress?.Report(new DiscImageExtractionProgress(
                    filesCompleted,
                    validatedFiles.Count,
                    bytesCopied,
                    totalBytes,
                    entry.RelativePath));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer, clearArray: true);
        }

        return new DiscImageExtractionResult(
            ImagePath: fullImagePath,
            OutputDirectory: fullOutputRoot,
            FileSystemUsed: browse.ActiveFileSystem,
            ExtractedFileCount: filesCompleted,
            CreatedDirectoryCount: createdDirCount,
            ExtractedBytes: bytesCopied,
            ExtractedFiles: extractedPaths,
            Warnings: browse.Warnings);
    }

    /// <summary>
    /// Validates a relative entry path for extraction inside <paramref name="outputDirectory"/>, rejecting
    /// '..' traversal, rooted paths, drive-letter prefixes, reserved Windows device names, invalid characters,
    /// and symlink/reparse point escapes. Returns the resolved full destination path.
    /// </summary>
    public static string ValidateExtractionPath(
        string outputDirectory,
        string entryRelativePath,
        bool isSymlink = false)
    {
        string fullRoot = ValidateOutputDirectoryRoot(outputDirectory);
        string fullRootWithSep = EnsureTrailingSeparator(fullRoot);

        if (isSymlink)
        {
            throw new InvalidDataException($"Refusing to extract symbolic link entry '{entryRelativePath}'.");
        }

        string[] segments = ParseAndValidateRelativeSegments(entryRelativePath, allowSingleLeadingSlash: false);

        string current = fullRoot;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            string fullCurrent = Path.GetFullPath(current);
            if (!IsPathStrictlyInsideRoot(fullRootWithSep, fullCurrent))
            {
                throw new InvalidDataException(
                    $"Extraction path '{entryRelativePath}' resolves outside output directory '{fullRoot}'.");
            }

            bool isLast = i == segments.Length - 1;
            if (!isLast)
            {
                if (Directory.Exists(fullCurrent))
                {
                    if ((File.GetAttributes(fullCurrent) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            $"Refusing to traverse symlink or reparse point directory '{fullCurrent}' while extracting '{entryRelativePath}'.");
                    }
                }
                else if (File.Exists(fullCurrent))
                {
                    throw new InvalidDataException(
                        $"Cannot extract '{entryRelativePath}': intermediate path '{fullCurrent}' already exists as a file.");
                }
            }
            else
            {
                if (File.Exists(fullCurrent) && (File.GetAttributes(fullCurrent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Refusing to write over symlink or reparse point file '{fullCurrent}'.");
                }

                if (Directory.Exists(fullCurrent) && (File.GetAttributes(fullCurrent) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Refusing symlink or reparse point directory '{fullCurrent}'.");
                }
            }
        }

        return Path.GetFullPath(current);
    }

    private static string ValidateOutputDirectoryRoot(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string fullRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullRoot) && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Output directory '{outputDirectory}' is a symlink or reparse point and cannot be used for extraction.");
        }
        if (File.Exists(fullRoot))
        {
            throw new IOException($"Output path '{outputDirectory}' already exists as a file.");
        }
        return fullRoot;
    }

    private static string NormalizeAndValidateRequestedEntryPath(string rawEntry)
    {
        string[] segments = ParseAndValidateRelativeSegments(rawEntry, allowSingleLeadingSlash: true);
        return string.Join('/', segments);
    }

    private static string[] ParseAndValidateRelativeSegments(string? entryPath, bool allowSingleLeadingSlash)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new InvalidDataException("Image entry path cannot be empty.");
        }

        foreach (char ch in entryPath)
        {
            if (ch == '\0' || ch < 0x20)
            {
                throw new InvalidDataException($"Image entry path '{entryPath}' contains illegal control characters.");
            }
        }

        if (entryPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            entryPath.StartsWith("//", StringComparison.Ordinal) ||
            entryPath.StartsWith('\\'))
        {
            throw new InvalidDataException($"Refusing rooted or UNC extraction path '{entryPath}'.");
        }

        string working = entryPath;
        if (working.StartsWith('/'))
        {
            if (!allowSingleLeadingSlash)
            {
                throw new InvalidDataException($"Refusing rooted extraction path '{entryPath}'.");
            }
            working = working[1..];
            if (working.Length == 0)
            {
                throw new InvalidDataException("Image entry path cannot be the bare root '/'.");
            }
        }

        if (Path.IsPathRooted(working) ||
            (working.Length >= 2 && char.IsLetter(working[0]) && working[1] == ':') ||
            working.Contains(':'))
        {
            throw new InvalidDataException($"Refusing drive-letter or rooted extraction path '{entryPath}'.");
        }

        string[] segments = working.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Length == 0)
        {
            throw new InvalidDataException($"Image entry path '{entryPath}' contains no valid path segments.");
        }

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (string.IsNullOrEmpty(segment))
            {
                throw new InvalidDataException($"Image entry path '{entryPath}' contains an empty path segment.");
            }

            if (segment is "." or ".." || string.IsNullOrWhiteSpace(segment))
            {
                throw new InvalidDataException($"Refusing path traversal segment '{segment}' in '{entryPath}'.");
            }

            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                throw new InvalidDataException($"Image entry path '{entryPath}' contains invalid filename characters in '{segment}'.");
            }

            if (IsReservedWindowsDeviceName(segment))
            {
                throw new InvalidDataException($"Refusing reserved Windows device name '{segment}' in '{entryPath}'.");
            }
        }

        return segments;
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        string trimmed = segment.TrimEnd(' ', '.');
        if (trimmed.Length == 0)
        {
            return true;
        }

        int dotIndex = trimmed.IndexOf('.');
        string stem = (dotIndex >= 0 ? trimmed[..dotIndex] : trimmed).TrimEnd(' ');
        return ReservedWindowsDeviceNames.Contains(stem);
    }

    private static string EnsureTrailingSeparator(string fullPath) =>
        fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;

    private static bool IsPathStrictlyInsideRoot(string fullRootWithSep, string fullCandidate) =>
        fullCandidate.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafeDirectoryChain(string fullRoot, string targetDirFullPath)
    {
        string fullRootWithSep = EnsureTrailingSeparator(fullRoot);
        string relative = Path.GetRelativePath(fullRoot, targetDirFullPath);
        if (relative == ".")
        {
            Directory.CreateDirectory(fullRoot);
            return;
        }

        string current = fullRoot;
        foreach (string part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.GetFullPath(Path.Combine(current, part));
            if (!IsPathStrictlyInsideRoot(fullRootWithSep, current))
            {
                throw new InvalidDataException($"Directory creation target '{current}' escapes output root '{fullRoot}'.");
            }

            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Refusing to traverse existing symlink or reparse point directory '{current}'.");
                }
            }
            else if (File.Exists(current))
            {
                throw new IOException($"Cannot create directory '{current}' because a file already exists at that path.");
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static async Task<long> ExtractSingleFileAsync(
        OpenedImageSession session,
        DiscImageEntryInfo entry,
        IsoFileEntry sourceEntry,
        string destinationPath,
        bool overwriteExisting,
        byte[] copyBuffer,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        FileMode mode = overwriteExisting ? FileMode.Create : FileMode.CreateNew;
        await using var output = new FileStream(
            destinationPath,
            mode,
            FileAccess.Write,
            FileShare.None,
            StreamCopyBufferSize,
            useAsync: true);

        if (entry.SizeBytes == 0)
        {
            return 0;
        }

        if (entry.StartLba < 0)
        {
            throw new InvalidDataException($"File '{entry.RelativePath}' has invalid negative start LBA {entry.StartLba}.");
        }

        if (session.IsoReader != null &&
            !string.Equals(entry.FileSystemSource, "UDF", StringComparison.OrdinalIgnoreCase) &&
            sourceEntry.Extents is { Count: > 1 })
        {
            await using Stream extentStream = session.IsoReader.OpenFileStream(sourceEntry);
            return await CopyBoundedAsync(extentStream, entry.SizeBytes, output, copyBuffer, onBytesCopied, cancellationToken).ConfigureAwait(false);
        }

        long startOffset = (long)entry.StartLba * OpticalConstants.DataSectorSize;
        if (startOffset < 0 || startOffset > session.DataStream.Length - entry.SizeBytes)
        {
            throw new InvalidDataException(
                $"File '{entry.RelativePath}' extent (LBA {entry.StartLba}, size {entry.SizeBytes} B) exceeds image bounds ({session.DataStream.Length} B).");
        }

        session.DataStream.Position = startOffset;
        return await CopyBoundedAsync(session.DataStream, entry.SizeBytes, output, copyBuffer, onBytesCopied, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        long bytesToCopy,
        Stream destination,
        byte[] buffer,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        long remaining = bytesToCopy;
        long totalCopied = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of image stream while reading file data ({remaining} bytes remaining).");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            totalCopied += read;
            onBytesCopied(read);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return totalCopied;
    }

    private static OpenedImageSession OpenImageSession(string fullPath, DiscImageFileSystem preferredFileSystem)
    {
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!IsSupportedImageFile(fullPath))
        {
            throw new NotSupportedException(
                $"Unsupported disc image format '{ext}'. Supported formats: .iso, .udf, .cue, .bin, .nrg, .mds/.mdf, .ccd/.img, .cdi.");
        }

        Stream dataStream;
        string formatName;
        long imageSizeBytes;
        int sectorCount;

        if (ext is ".iso" or ".udf")
        {
            var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamCopyBufferSize, useAsync: false);
            if (fs.Length == 0)
            {
                fs.Dispose();
                throw new InvalidDataException($"Disc image '{Path.GetFileName(fullPath)}' is empty (0 bytes).");
            }
            dataStream = fs;
            formatName = ext == ".udf" ? "UDF Disc Image (.udf)" : "ISO Disc Image (.iso)";
            imageSizeBytes = fs.Length;
            sectorCount = OpticalConstants.BytesToSectors(fs.Length, OpticalConstants.DataSectorSize);
        }
        else if (ext == ".cue")
        {
            dataStream = LegacyDiscImageConverter.OpenCueIsoUserDataStream(fullPath);
            formatName = "CDRWIN CUE Data Track (.cue)";
            imageSizeBytes = dataStream.Length;
            sectorCount = OpticalConstants.BytesToSectors(dataStream.Length, OpticalConstants.DataSectorSize);
        }
        else
        {
            var info = LegacyDiscImageConverter.Inspect(fullPath);
            dataStream = LegacyDiscImageConverter.OpenIsoUserDataStream(info);
            formatName = info.FormatName;
            imageSizeBytes = info.PayloadLengthBytes;
            sectorCount = info.SectorCount;
        }

        try
        {
            IsoImageReader? isoReader = null;
            if (HasIso9660Pvd(dataStream))
            {
                try
                {
                    isoReader = IsoImageReader.FromStream(dataStream, leaveOpen: true);
                }
                catch (InvalidDataException)
                {
                    isoReader = null;
                }
            }

            var udfWarnings = new List<string>();
            dataStream.Position = 0;
            IsoDirectoryEntry? udfRoot = UdfTreeReader.TryRead(dataStream, udfWarnings);
            string? udfLabel = udfRoot != null ? UdfTreeReader.TryReadVolumeLabel(dataStream) : null;

            bool hasIso = isoReader?.HasIso9660 == true;
            bool hasJoliet = isoReader?.HasJoliet == true;
            bool hasUdf = udfRoot != null;

            if (!hasIso && !hasJoliet && !hasUdf)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(fullPath)}' does not contain a readable ISO 9660, Joliet, or UDF filesystem.");
            }

            (IsoDirectoryEntry chosenRoot, string fsName, string volumeLabel) = SelectTree(
                fullPath,
                ext,
                isoReader,
                udfRoot,
                udfLabel,
                udfWarnings,
                preferredFileSystem);

            if (ext == ".iso")
            {
                formatName = (hasIso, hasJoliet, hasUdf) switch
                {
                    (true, true, true) => "ISO 9660 / Joliet / UDF Bridge (.iso)",
                    (true, true, false) => "ISO 9660 / Joliet (.iso)",
                    (true, false, true) => "ISO 9660 / UDF Bridge (.iso)",
                    (false, false, true) => "UDF Disc Image (.iso)",
                    _ => "ISO 9660 Image (.iso)"
                };
            }

            return new OpenedImageSession(
                fullPath,
                formatName,
                volumeLabel,
                fsName,
                hasIso,
                hasJoliet,
                hasUdf,
                imageSizeBytes,
                sectorCount,
                dataStream,
                isoReader,
                chosenRoot,
                udfWarnings);
        }
        catch
        {
            dataStream.Dispose();
            throw;
        }
    }

    private static bool HasIso9660Pvd(Stream stream)
    {
        if (stream.Length < 17L * OpticalConstants.DataSectorSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[6];
        long savedPos = stream.Position;
        try
        {
            stream.Position = 16L * OpticalConstants.DataSectorSize;
            int read = stream.Read(header);
            return read == 6 && header[0] == 0x01 && header.Slice(1, 5).SequenceEqual("CD001"u8);
        }
        finally
        {
            stream.Position = savedPos;
        }
    }

    private static (IsoDirectoryEntry Root, string FsName, string VolumeLabel) SelectTree(
        string fullPath,
        string ext,
        IsoImageReader? isoReader,
        IsoDirectoryEntry? udfRoot,
        string? udfLabel,
        IReadOnlyList<string> udfWarnings,
        DiscImageFileSystem preferred)
    {
        int udfFileCount = udfRoot != null ? CountFiles(udfRoot) : 0;
        int udfSymlinkCount = udfWarnings.Count(w => w.Contains("symbolic link", StringComparison.OrdinalIgnoreCase));
        int isoFileCount = isoReader?.AllFiles.Count ?? 0;

        string fallbackLabel = !string.IsNullOrWhiteSpace(udfLabel)
            ? udfLabel
            : (!string.IsNullOrWhiteSpace(isoReader?.VolumeLabel) ? isoReader!.VolumeLabel : string.Empty);

        return preferred switch
        {
            DiscImageFileSystem.Udf => udfRoot != null
                ? (udfRoot, "UDF", !string.IsNullOrWhiteSpace(udfLabel) ? udfLabel : fallbackLabel)
                : throw new InvalidDataException($"'{Path.GetFileName(fullPath)}' does not contain a readable UDF tree."),

            DiscImageFileSystem.Joliet => isoReader?.JolietRootDirectory != null
                ? (isoReader.JolietRootDirectory, "Joliet", isoReader.JolietVolumeLabel)
                : throw new InvalidDataException($"'{Path.GetFileName(fullPath)}' does not contain a Joliet tree."),

            DiscImageFileSystem.Iso9660 => isoReader?.IsoRootDirectory != null
                ? (isoReader.IsoRootDirectory, "ISO 9660", isoReader.PvdVolumeLabel)
                : throw new InvalidDataException($"'{Path.GetFileName(fullPath)}' does not contain an ISO 9660 tree."),

            _ => SelectBestAutoTree(ext, isoReader, udfRoot, udfFileCount + udfSymlinkCount, isoFileCount, fallbackLabel)
        };
    }

    private static (IsoDirectoryEntry Root, string FsName, string VolumeLabel) SelectBestAutoTree(
        string ext,
        IsoImageReader? isoReader,
        IsoDirectoryEntry? udfRoot,
        int effectiveUdfFileCount,
        int isoFileCount,
        string fallbackLabel)
    {
        if (udfRoot != null && (ext == ".udf" || isoReader == null || effectiveUdfFileCount >= isoFileCount))
        {
            return (udfRoot, "UDF", fallbackLabel);
        }

        if (isoReader?.HasJoliet == true && isoReader.JolietRootDirectory != null)
        {
            return (isoReader.JolietRootDirectory, "Joliet", isoReader.VolumeLabel);
        }

        if (isoReader?.IsoRootDirectory != null)
        {
            return (isoReader.IsoRootDirectory, "ISO 9660", isoReader.PvdVolumeLabel);
        }

        if (udfRoot != null)
        {
            return (udfRoot, "UDF", fallbackLabel);
        }

        throw new InvalidDataException("No readable filesystem tree was found in the disc image.");
    }

    private static int CountFiles(IsoDirectoryEntry directory)
    {
        int count = directory.Files.Count();
        foreach (var sub in directory.SubDirectories)
        {
            count += CountFiles(sub);
        }
        return count;
    }

    private sealed class OpenedImageSession : IDisposable
    {
        public string ImagePath { get; }
        public string FormatName { get; }
        public string VolumeLabel { get; }
        public string ActiveFileSystem { get; }
        public bool HasIso9660 { get; }
        public bool HasJoliet { get; }
        public bool HasUdf { get; }
        public long ImageSizeBytes { get; }
        public int SectorCount { get; }
        public Stream DataStream { get; }
        public IsoImageReader? IsoReader { get; }
        public IsoDirectoryEntry ChosenRoot { get; }
        public IReadOnlyList<string> Warnings { get; }
        public Dictionary<string, IsoFileEntry> SourceFilesByRelativePath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public OpenedImageSession(
            string imagePath,
            string formatName,
            string volumeLabel,
            string activeFileSystem,
            bool hasIso9660,
            bool hasJoliet,
            bool hasUdf,
            long imageSizeBytes,
            int sectorCount,
            Stream dataStream,
            IsoImageReader? isoReader,
            IsoDirectoryEntry chosenRoot,
            IReadOnlyList<string> warnings)
        {
            ImagePath = imagePath;
            FormatName = formatName;
            VolumeLabel = volumeLabel;
            ActiveFileSystem = activeFileSystem;
            HasIso9660 = hasIso9660;
            HasJoliet = hasJoliet;
            HasUdf = hasUdf;
            ImageSizeBytes = imageSizeBytes;
            SectorCount = sectorCount;
            DataStream = dataStream;
            IsoReader = isoReader;
            ChosenRoot = chosenRoot;
            Warnings = warnings;
        }

        public DiscImageBrowseResult BuildBrowseResult()
        {
            var flatEntries = new List<DiscImageEntryInfo>();
            SourceFilesByRelativePath.Clear();

            var rootInfo = new DiscImageEntryInfo(
                Name: string.IsNullOrWhiteSpace(VolumeLabel) ? Path.GetFileName(ImagePath) : VolumeLabel,
                RelativePath: string.Empty,
                VirtualPath: "/",
                IsDirectory: true,
                SizeBytes: 0,
                TimestampUtc: ChosenRoot.RecordingTimeUtc,
                Attributes: FileAttributes.Directory | FileAttributes.ReadOnly,
                FlagsSummary: "Directory, ReadOnly",
                StartLba: ChosenRoot.StartLba,
                FileSystemSource: ActiveFileSystem);

            var rootNode = new DiscImageTreeNode { Entry = rootInfo };
            int dirCount = 0;
            int fileCount = 0;

            void Walk(IsoDirectoryEntry sourceDir, DiscImageTreeNode parentNode, string parentRelativePath)
            {
                var orderedDirs = sourceDir.SubDirectories
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var orderedFiles = sourceDir.Files
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var subDir in orderedDirs)
                {
                    string rel = string.IsNullOrEmpty(parentRelativePath) ? subDir.Name : $"{parentRelativePath}/{subDir.Name}";
                    var dirInfo = CreateEntryInfo(subDir, rel, isDirectory: true, ActiveFileSystem);
                    var dirNode = new DiscImageTreeNode { Entry = dirInfo };
                    parentNode.Children.Add(dirNode);
                    flatEntries.Add(dirInfo);
                    dirCount++;
                    Walk(subDir, dirNode, rel);
                }

                foreach (var file in orderedFiles)
                {
                    string rel = string.IsNullOrEmpty(parentRelativePath) ? file.Name : $"{parentRelativePath}/{file.Name}";
                    var fileInfo = CreateEntryInfo(file, rel, isDirectory: false, ActiveFileSystem);
                    parentNode.Children.Add(new DiscImageTreeNode { Entry = fileInfo });
                    flatEntries.Add(fileInfo);
                    SourceFilesByRelativePath[rel] = file;
                    fileCount++;
                }
            }

            Walk(ChosenRoot, rootNode, string.Empty);

            return new DiscImageBrowseResult(
                ImagePath: ImagePath,
                FormatName: FormatName,
                VolumeLabel: VolumeLabel,
                ActiveFileSystem: ActiveFileSystem,
                HasIso9660: HasIso9660,
                HasJoliet: HasJoliet,
                HasUdf: HasUdf,
                ImageSizeBytes: ImageSizeBytes,
                SectorCount: SectorCount,
                DirectoryCount: dirCount,
                FileCount: fileCount,
                Root: rootNode,
                Entries: flatEntries,
                Warnings: Warnings);
        }

        private static DiscImageEntryInfo CreateEntryInfo(IsoEntry source, string relativePath, bool isDirectory, string fsSource)
        {
            FileAttributes attrs = FileAttributes.ReadOnly;
            if (isDirectory)
            {
                attrs |= FileAttributes.Directory;
            }
            if (source.IsHidden)
            {
                attrs |= FileAttributes.Hidden;
            }

            var flags = new List<string>(3)
            {
                isDirectory ? "Directory" : "File"
            };
            if (source.IsHidden)
            {
                flags.Add("Hidden");
            }
            flags.Add("ReadOnly");

            return new DiscImageEntryInfo(
                Name: source.Name,
                RelativePath: relativePath,
                VirtualPath: "/" + relativePath,
                IsDirectory: isDirectory,
                SizeBytes: isDirectory ? 0L : source.SizeBytes,
                TimestampUtc: source.RecordingTimeUtc,
                Attributes: attrs,
                FlagsSummary: string.Join(", ", flags),
                StartLba: source.StartLba,
                FileSystemSource: fsSource);
        }

        public void Dispose()
        {
            IsoReader?.Dispose();
            DataStream.Dispose();
        }
    }
}
