using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>One chunk of a striped parity payload on a parity disc.</summary>
public sealed record ParityPayloadChunk(string RelativePath, long SizeBytes, string Sha256);

/// <summary>Parity payload descriptor stored in <c>PARITY-SET.JSON</c>.</summary>
public sealed record ParityPayloadInfo(
    IReadOnlyList<ParityPayloadChunk> Chunks,
    long TotalSizeBytes,
    string Sha256);

/// <summary>File entry within a data disc's deterministic streaming container.</summary>
public sealed record ParitySetFileEntry(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    DateTime LastWriteUtc);

/// <summary>Ordered layout of one data disc's virtual container in a cross-disc parity set.</summary>
public sealed record ParitySetDataDisc(
    int DiscNumber,
    long DataStreamBytes,
    IReadOnlyList<ParitySetFileEntry> Files);

/// <summary>
/// Self-describing metadata file (<c>PARITY-SET.JSON</c>) written to every parity disc in a multi-disc archive span.
/// </summary>
public sealed record ParitySetDocument(
    int SchemaVersion,
    string SetId,
    string Algorithm,
    string MediaType,
    int DataDiscCount,
    int ParityDiscCount,
    int TotalDiscCount,
    int ThisDiscNumber,
    int ParitySlotIndex,
    int SectorSizeBytes,
    int StripeSizeBytes,
    long MaxDataStreamBytes,
    int RedundancyPercent,
    ParityPayloadInfo ParityPayload,
    IReadOnlyList<ParitySetDataDisc> DataDiscs);

/// <summary>
/// Helpers, schema serialization, and bounded O(1)-memory streaming container readers/writers for cross-disc
/// Reed-Solomon parity sets.
/// </summary>
public static class ArchiveParitySet
{
    public const int CurrentSchemaVersion = 1;
    public const string ParitySetFileName = "PARITY-SET.JSON";
    public const string ParityPayloadFileName = "PARITY.BIN";
    public const int DefaultStripeSizeBytes = 64 * 1024; // 32 * 2048-byte sectors
    public const long MaxParityChunkBytes = 1024L * 1024 * 1024; // 1 GiB (exact multiple of DefaultStripeSizeBytes)

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static long AlignUpToSector(long sizeBytes, int sectorSize = OpticalConstants.DataSectorSize) =>
        sizeBytes <= 0 ? 0L : ((sizeBytes + sectorSize - 1) / sectorSize) * sectorSize;

    public static long AlignUpToStripe(long sizeBytes, int stripeSizeBytes) =>
        sizeBytes <= 0 ? 0L : ((sizeBytes + stripeSizeBytes - 1) / stripeSizeBytes) * stripeSizeBytes;

    public static long ComputeDataDiscStreamBytes(IEnumerable<long> fileSizes, int sectorSize = OpticalConstants.DataSectorSize)
    {
        long total = 0;
        foreach (long size in fileSizes)
        {
            total += AlignUpToSector(size, sectorSize);
        }
        return total;
    }

    /// <summary>
    /// Upper-bound sector reservation on every disc when cross-disc parity is enabled so that <c>PARITY-SET.JSON</c>,
    /// parity payload metadata, and per-disc scripts fit on the parity disc within the capacity ceiling.
    /// </summary>
    public static long EstimateMetadataSectors(IEnumerable<ArchiveSourceFile> files, int parityDiscCount)
    {
        if (parityDiscCount <= 0)
        {
            return 0;
        }
        // PARITY-SET.JSON lists every file once with path, size, SHA-256, and timestamp (comparable to CATALOG.JSON),
        // plus per-disc manifests/scripts on the parity disc.
        return ArchiveCatalog.EstimateSectors(files) + 16;
    }

    public static ArchiveCatalogParitySet? CreateCatalogParitySet(
        ArchivePlan plan,
        int stripeSizeBytes = DefaultStripeSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ParityDiscCount <= 0 || plan.Discs.Count == 0)
        {
            return null;
        }

        long maxStream = 0;
        foreach (var disc in plan.Discs)
        {
            long bytes = ComputeDataDiscStreamBytes(disc.Files.Select(f => f.SizeBytes));
            if (bytes > maxStream)
            {
                maxStream = bytes;
            }
        }
        maxStream = AlignUpToStripe(maxStream, stripeSizeBytes);

        var parityNumbers = Enumerable.Range(plan.Discs.Count + 1, plan.ParityDiscCount).ToList();
        return new ArchiveCatalogParitySet(
            DataDiscCount: plan.Discs.Count,
            ParityDiscCount: plan.ParityDiscCount,
            TotalDiscCount: plan.Discs.Count + plan.ParityDiscCount,
            StripeSizeBytes: stripeSizeBytes,
            SectorSizeBytes: OpticalConstants.DataSectorSize,
            MaxDataStreamBytes: maxStream,
            ParitySetFileName: ParitySetFileName,
            ParityDiscNumbers: parityNumbers,
            Algorithm: CrossDiscReedSolomon.AlgorithmName);
    }

    public static IReadOnlyList<string> GetChunkRelativePaths(long totalParityBytes)
    {
        if (totalParityBytes <= MaxParityChunkBytes)
        {
            return [ParityPayloadFileName];
        }

        int count = (int)((totalParityBytes + MaxParityChunkBytes - 1) / MaxParityChunkBytes);
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            paths.Add($"PARITY.{i + 1:D3}");
        }
        return paths;
    }

    public static string ToJson(ParitySetDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions) + "\n";

    public static ParitySetDocument? ParseJson(string json) =>
        JsonSerializer.Deserialize<ParitySetDocument>(json, CaseInsensitiveJsonOptions);

    public static async Task WriteParitySetJsonAsync(
        string discRoot,
        ParitySetDocument document,
        CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(
            Path.Combine(discRoot, ParitySetFileName),
            ToJson(document),
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);
    }

    public static string BuildParityReadmeText(ParitySetDocument doc)
    {
        int parityNumber = doc.ParitySlotIndex + 1;
        string chunkDesc = doc.ParityPayload.Chunks.Count == 1
            ? doc.ParityPayload.Chunks[0].RelativePath
            : $"{doc.ParityPayload.Chunks.Count} chunks ({doc.ParityPayload.Chunks[0].RelativePath} .. {doc.ParityPayload.Chunks[^1].RelativePath})";

        return $"""
            ================================================================================
            IGNISDISC CROSS-DISC PARITY & DISASTER RECOVERY GUIDE
            Set ID:         {doc.SetId}
            This Disc:      Disc {doc.ThisDiscNumber} of {doc.TotalDiscCount} (Parity Disc {parityNumber} of {doc.ParityDiscCount})
            Protects:       Data Discs 1..{doc.DataDiscCount}
            Algorithm:      {doc.Algorithm} (tolerates up to {doc.ParityDiscCount} lost disc(s))
            Media Type:     {doc.MediaType}
            Stripe Size:    {doc.StripeSizeBytes:N0} bytes ({doc.SectorSizeBytes:N0}-byte aligned containers)
            Payload:        {chunkDesc} ({doc.ParityPayload.TotalSizeBytes:N0} bytes)
            Payload SHA256: {doc.ParityPayload.Sha256}
            ================================================================================

            This disc stores cross-disc Reed-Solomon parity computed across Data Discs
            1..{doc.DataDiscCount}. If any {doc.ParityDiscCount} disc(s) in this archive set are completely lost,
            scratched beyond PAR2 repair, or unreadable, the missing disc(s) can be rebuilt
            bit-for-bit from the remaining discs plus parity disc(s).

            1. VERIFYING THIS PARITY DISC (ZERO DEPENDENCIES)
               - Windows: double-click VERIFY.BAT (or run .\VERIFY.PS1 in PowerShell).
               - Linux/macOS: run sha256sum -c IGNISDISC-MANIFEST.sha256
               VERIFY.PS1 checks every file checksum AND validates PARITY-SET.JSON against
               the parity payload and CATALOG.JSON.

            2. REBUILDING A LOST DATA OR PARITY DISC
               - Copy or mount all surviving discs (at least {doc.DataDiscCount} discs total from 1..{doc.TotalDiscCount}).
               - Run IgnisDisc CLI:
                   ignisdisc-cli archive rebuild --discs "<disc1>;<disc3>;<parity>" --disc 2 --out "<rebuilt-disc02>"
               - Every restored file is verified against its SHA-256 digest in PARITY-SET.JSON
                 and CATALOG.JSON, and a fresh manifest + verification script set is written.
            """.Replace("\r\n", "\n") + "\n";
    }

    internal static ArchivePlan ToArchivePlan(ParitySetDocument doc)
    {
        if (!Enum.TryParse<DiscMediaType>(doc.MediaType, ignoreCase: true, out var mediaType))
        {
            mediaType = DiscMediaType.CdR;
        }

        var discs = new List<ArchiveDiscPlan>(doc.DataDiscs.Count);
        long totalBytes = 0;
        foreach (var d in doc.DataDiscs.OrderBy(x => x.DiscNumber))
        {
            var files = d.Files.Select(f => new ArchiveSourceFile(f.RelativePath, f.RelativePath, f.SizeBytes)).ToList();
            long discBytes = files.Sum(f => f.SizeBytes);
            int discSectors = (int)Math.Min(int.MaxValue, d.DataStreamBytes / Math.Max(1, doc.SectorSizeBytes));
            discs.Add(new ArchiveDiscPlan(d.DiscNumber, files, discBytes, discSectors));
            totalBytes += discBytes;
        }

        int ceiling = ArchiveSpanPlanner.CalculateCeilingSectors(mediaType);
        int usable = ArchiveSpanPlanner.CalculateUsableSectors(mediaType, doc.RedundancyPercent);
        return new ArchivePlan(
            SetId: doc.SetId,
            MediaType: mediaType,
            RedundancyPercent: doc.RedundancyPercent,
            UsableSectorsPerDisc: usable,
            Discs: discs,
            TotalBytes: totalBytes,
            CeilingSectorsPerDisc: ceiling,
            FillFreeSpace: true,
            PackingMode: ArchivePackingMode.FirstFitDecreasing,
            ParityDiscCount: doc.ParityDiscCount);
    }

    internal static IReadOnlyList<ArchiveCatalogEntry> ToCatalogEntries(ParitySetDocument doc)
    {
        var entries = new List<ArchiveCatalogEntry>();
        foreach (var d in doc.DataDiscs.OrderBy(x => x.DiscNumber))
        {
            foreach (var f in d.Files)
            {
                entries.Add(new ArchiveCatalogEntry(d.DiscNumber, f.RelativePath, f.SizeBytes, f.Sha256));
            }
        }
        return entries;
    }

    /// <summary>
    /// Abstraction over a staged/mounted directory or an ISO image file for reading disc files sequentially.
    /// </summary>
    internal interface IArchiveDiscSource : IDisposable
    {
        string SourcePath { get; }
        bool FileExists(string relativePath);
        long? GetFileLength(string relativePath);
        Stream OpenRead(string relativePath);
        Task<string?> ReadTextIfExistsAsync(string relativePath, CancellationToken cancellationToken);
    }

    internal static IArchiveDiscSource OpenDiscSource(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryDiscSource(path);
        }
        if (File.Exists(path))
        {
            return new IsoDiscSource(path);
        }
        throw new FileNotFoundException($"Archive disc source not found: '{path}'.", path);
    }

    private sealed class DirectoryDiscSource : IArchiveDiscSource
    {
        public string SourcePath { get; }

        public DirectoryDiscSource(string rootPath)
        {
            SourcePath = Path.GetFullPath(rootPath);
        }

        public bool FileExists(string relativePath)
        {
            string? resolved = ArchiveManifest.ResolveInsideRoot(SourcePath, relativePath);
            return resolved != null && File.Exists(resolved);
        }

        public long? GetFileLength(string relativePath)
        {
            string? resolved = ArchiveManifest.ResolveInsideRoot(SourcePath, relativePath);
            return resolved != null && File.Exists(resolved) ? new FileInfo(resolved).Length : null;
        }

        public Stream OpenRead(string relativePath)
        {
            string resolved = ArchiveManifest.ResolveInsideRoot(SourcePath, relativePath)
                ?? throw new InvalidOperationException($"Path '{relativePath}' resolves outside '{SourcePath}'.");
            return new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }

        public async Task<string?> ReadTextIfExistsAsync(string relativePath, CancellationToken cancellationToken)
        {
            string? resolved = ArchiveManifest.ResolveInsideRoot(SourcePath, relativePath);
            if (resolved == null || !File.Exists(resolved))
            {
                return null;
            }
            var info = new FileInfo(resolved);
            if (info.Length > ArchiveManifest.MaxMetadataFileSizeBytes)
            {
                throw new InvalidDataException($"Metadata file '{relativePath}' exceeds maximum readable size.");
            }
            return await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    private sealed class IsoDiscSource : IArchiveDiscSource
    {
        private readonly FileStream _isoStream;
        private readonly Dictionary<string, IsoFileEntry> _files;

        public string SourcePath { get; }

        public IsoDiscSource(string isoPath)
        {
            SourcePath = Path.GetFullPath(isoPath);
            _isoStream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false);
            var list = ArchiveRecoveryService.ReadFileList(_isoStream, out string? problem, out _);
            if (list == null)
            {
                _isoStream.Dispose();
                throw new InvalidDataException($"Could not read file system from ISO '{isoPath}': {problem ?? "unknown error"}.");
            }
            _files = new Dictionary<string, IsoFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list)
            {
                string key = entry.VirtualPath.TrimStart('/');
                _files[key] = entry;
            }
        }

        public bool FileExists(string relativePath) => _files.ContainsKey(relativePath);

        public long? GetFileLength(string relativePath) =>
            _files.TryGetValue(relativePath, out var entry) ? entry.SizeBytes : null;

        public Stream OpenRead(string relativePath)
        {
            if (!_files.TryGetValue(relativePath, out var entry))
            {
                throw new FileNotFoundException($"File '{relativePath}' not found inside ISO '{SourcePath}'.", relativePath);
            }
            if (entry.SizeBytes <= 0)
            {
                return Stream.Null;
            }
            if (entry.Extents is { Count: > 1 })
            {
                return new MultiExtentSegmentStream(_isoStream, entry.Extents);
            }
            return new BoundedSegmentStream(
                _isoStream,
                (long)entry.StartLba * OpticalConstants.DataSectorSize,
                entry.SizeBytes,
                leaveOpen: true);
        }

        public async Task<string?> ReadTextIfExistsAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(relativePath, out var entry))
            {
                return null;
            }
            if (entry.SizeBytes > ArchiveManifest.MaxMetadataFileSizeBytes)
            {
                throw new InvalidDataException($"Metadata file '{relativePath}' exceeds maximum readable size.");
            }
            await using var stream = OpenRead(relativePath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _isoStream.Dispose();
    }

    /// <summary>
    /// Bounded O(1)-memory sequential reader over a data disc's sector-aligned virtual file container.
    /// Keeps at most one open file stream at a time and computes per-file SHA-256 on the fly.
    /// </summary>
    internal sealed class DiscContainerReader : IAsyncDisposable
    {
        private readonly IArchiveDiscSource _source;
        private readonly IReadOnlyList<ParitySetFileEntry> _files;
        private readonly int _sectorSize;
        private readonly bool _verifySha256;
        private int _fileIndex;
        private long _fileBytesRead;
        private long _paddingRemaining;
        private Stream? _currentStream;
        private IncrementalHash? _currentHash;

        public string? HashMismatchPath { get; private set; }

        public DiscContainerReader(
            IArchiveDiscSource source,
            IReadOnlyList<ParitySetFileEntry> files,
            int sectorSize = OpticalConstants.DataSectorSize,
            bool verifySha256 = true)
        {
            _source = source;
            _files = files;
            _sectorSize = sectorSize;
            _verifySha256 = verifySha256;
        }

        public async Task ReadStripeAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            int filled = 0;
            while (filled < destination.Length)
            {
                if (_paddingRemaining > 0)
                {
                    int padBytes = (int)Math.Min(_paddingRemaining, destination.Length - filled);
                    destination.Span.Slice(filled, padBytes).Clear();
                    filled += padBytes;
                    _paddingRemaining -= padBytes;
                    continue;
                }

                if (_currentStream == null)
                {
                    if (!await AdvanceToNextFileAsync(cancellationToken).ConfigureAwait(false))
                    {
                        destination.Span[filled..].Clear();
                        return;
                    }
                    continue;
                }

                var currentFile = _files[_fileIndex];
                long remainingInFile = currentFile.SizeBytes - _fileBytesRead;
                if (remainingInFile <= 0)
                {
                    await CloseCurrentFileAsync(currentFile).ConfigureAwait(false);
                    _paddingRemaining = AlignUpToSector(currentFile.SizeBytes, _sectorSize) - currentFile.SizeBytes;
                    _fileIndex++;
                    continue;
                }

                int toRead = (int)Math.Min(remainingInFile, destination.Length - filled);
                int read = await _currentStream.ReadAsync(destination.Slice(filled, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Unexpected end of file '{currentFile.RelativePath}' after {_fileBytesRead} of {currentFile.SizeBytes} bytes.");
                }

                _currentHash?.AppendData(destination.Span.Slice(filled, read));
                filled += read;
                _fileBytesRead += read;

                if (_fileBytesRead >= currentFile.SizeBytes)
                {
                    await CloseCurrentFileAsync(currentFile).ConfigureAwait(false);
                    _paddingRemaining = AlignUpToSector(currentFile.SizeBytes, _sectorSize) - currentFile.SizeBytes;
                    _fileIndex++;
                }
            }
        }

        /// <summary>Finalizes any trailing zero-byte files after all stripes have been read.</summary>
        public async Task VerifyRemainingZeroByteFilesAsync(CancellationToken cancellationToken)
        {
            while (_fileIndex < _files.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var f = _files[_fileIndex];
                if (f.SizeBytes > 0)
                {
                    throw new InvalidDataException($"File '{f.RelativePath}' was not reached during container streaming.");
                }
                if (!_source.FileExists(f.RelativePath) || _source.GetFileLength(f.RelativePath) != 0)
                {
                    HashMismatchPath ??= f.RelativePath;
                }
                _fileIndex++;
            }
        }

        private async Task<bool> AdvanceToNextFileAsync(CancellationToken cancellationToken)
        {
            while (_fileIndex < _files.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = _files[_fileIndex];
                if (file.SizeBytes == 0)
                {
                    if (!_source.FileExists(file.RelativePath) || _source.GetFileLength(file.RelativePath) != 0)
                    {
                        HashMismatchPath ??= file.RelativePath;
                    }
                    _fileIndex++;
                    continue;
                }

                long? actualLen = _source.GetFileLength(file.RelativePath);
                if (actualLen != file.SizeBytes)
                {
                    HashMismatchPath ??= file.RelativePath;
                    throw new InvalidDataException(
                        $"File '{file.RelativePath}' has length {actualLen?.ToString() ?? "missing"} (expected {file.SizeBytes}).");
                }

                _currentStream = _source.OpenRead(file.RelativePath);
                _fileBytesRead = 0;
                _currentHash = _verifySha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                return true;
            }
            return false;
        }

        private async Task CloseCurrentFileAsync(ParitySetFileEntry file)
        {
            if (_currentHash != null)
            {
                string actualHex = Convert.ToHexStringLower(_currentHash.GetHashAndReset());
                if (!string.Equals(actualHex, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    HashMismatchPath ??= file.RelativePath;
                }
                _currentHash.Dispose();
                _currentHash = null;
            }
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync().ConfigureAwait(false);
                _currentStream = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _currentHash?.Dispose();
            _currentHash = null;
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync().ConfigureAwait(false);
                _currentStream = null;
            }
        }
    }

    /// <summary>
    /// Bounded O(1)-memory sequential writer reconstructing a missing data disc's files in canonical order.
    /// </summary>
    internal sealed class DiscContainerWriter : IAsyncDisposable
    {
        private readonly string _outputRoot;
        private readonly IReadOnlyList<ParitySetFileEntry> _files;
        private readonly int _sectorSize;
        private readonly List<string> _verifiedFiles = new();
        private readonly List<string> _failedFiles = new();

        private int _fileIndex;
        private long _fileBytesWritten;
        private long _paddingRemaining;
        private FileStream? _currentStream;
        private IncrementalHash? _currentHash;

        public long PayloadBytesRestored { get; private set; }
        public IReadOnlyList<string> VerifiedFiles => _verifiedFiles;
        public IReadOnlyList<string> FailedFiles => _failedFiles;

        public DiscContainerWriter(
            string outputRoot,
            IReadOnlyList<ParitySetFileEntry> files,
            int sectorSize = OpticalConstants.DataSectorSize)
        {
            _outputRoot = Path.GetFullPath(outputRoot);
            _files = files;
            _sectorSize = sectorSize;
        }

        public async Task WriteStripeAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < source.Length)
            {
                if (_paddingRemaining > 0)
                {
                    int skip = (int)Math.Min(_paddingRemaining, source.Length - offset);
                    offset += skip;
                    _paddingRemaining -= skip;
                    continue;
                }

                if (_currentStream == null)
                {
                    if (!AdvancePastZeroByteFiles())
                    {
                        return; // Trailing zero padding past end of this disc's stream.
                    }
                    OpenCurrentFile();
                }

                var currentFile = _files[_fileIndex];
                long remaining = currentFile.SizeBytes - _fileBytesWritten;
                int toWrite = (int)Math.Min(remaining, source.Length - offset);
                if (toWrite > 0)
                {
                    await _currentStream!.WriteAsync(source.Slice(offset, toWrite), cancellationToken).ConfigureAwait(false);
                    _currentHash!.AppendData(source.Span.Slice(offset, toWrite));
                    offset += toWrite;
                    _fileBytesWritten += toWrite;
                    PayloadBytesRestored += toWrite;
                }

                if (_fileBytesWritten >= currentFile.SizeBytes)
                {
                    await FinalizeCurrentFileAsync(currentFile).ConfigureAwait(false);
                    _paddingRemaining = AlignUpToSector(currentFile.SizeBytes, _sectorSize) - currentFile.SizeBytes;
                    _fileIndex++;
                }
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_currentStream != null)
            {
                var currentFile = _files[_fileIndex];
                await FinalizeCurrentFileAsync(currentFile).ConfigureAwait(false);
                _fileIndex++;
            }

            while (_fileIndex < _files.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = _files[_fileIndex];
                if (file.SizeBytes == 0)
                {
                    CreateZeroByteFile(file);
                }
                else
                {
                    _failedFiles.Add(file.RelativePath);
                }
                _fileIndex++;
            }
        }

        private bool AdvancePastZeroByteFiles()
        {
            while (_fileIndex < _files.Count)
            {
                var file = _files[_fileIndex];
                if (file.SizeBytes > 0)
                {
                    return true;
                }
                CreateZeroByteFile(file);
                _fileIndex++;
            }
            return false;
        }

        private void CreateZeroByteFile(ParitySetFileEntry file)
        {
            string destPath = ResolveSafeDestination(file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using (new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
            }
            ApplyTimestamp(destPath, file.LastWriteUtc);
            string emptySha = Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty));
            if (string.Equals(emptySha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _verifiedFiles.Add(file.RelativePath);
            }
            else
            {
                _failedFiles.Add(file.RelativePath);
            }
        }

        private void OpenCurrentFile()
        {
            var file = _files[_fileIndex];
            string destPath = ResolveSafeDestination(file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            _currentStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            _fileBytesWritten = 0;
            _currentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private async Task FinalizeCurrentFileAsync(ParitySetFileEntry file)
        {
            string destPath = ResolveSafeDestination(file.RelativePath);
            if (_currentStream != null)
            {
                await _currentStream.FlushAsync().ConfigureAwait(false);
                await _currentStream.DisposeAsync().ConfigureAwait(false);
                _currentStream = null;
            }
            ApplyTimestamp(destPath, file.LastWriteUtc);

            string actualHex = Convert.ToHexStringLower(_currentHash!.GetHashAndReset());
            _currentHash.Dispose();
            _currentHash = null;

            if (_fileBytesWritten == file.SizeBytes && string.Equals(actualHex, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _verifiedFiles.Add(file.RelativePath);
            }
            else
            {
                _failedFiles.Add(file.RelativePath);
            }
        }

        private string ResolveSafeDestination(string relativePath) =>
            ArchiveManifest.ResolveInsideRoot(_outputRoot, relativePath)
                ?? throw new InvalidOperationException($"Refusing to restore unsafe path '{relativePath}' outside '{_outputRoot}'.");

        private static void ApplyTimestamp(string path, DateTime lastWriteUtc)
        {
            if (lastWriteUtc != default)
            {
                File.SetLastWriteTimeUtc(path, lastWriteUtc);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _currentHash?.Dispose();
            _currentHash = null;
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync().ConfigureAwait(false);
                _currentStream = null;
            }
        }
    }
}
