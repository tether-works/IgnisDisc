using System.Diagnostics;
using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Maps a file in a disc compilation or filesystem image to its allocated sector extent (Start LBA, sector count, and byte size).
/// </summary>
public class LbaFileExtentMap
{
    /// <summary>
    /// Virtual path of the file within the disc compilation (e.g. "/Bin/app.bin" or "/README.TXT").
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Starting logical block address (LBA) on media.
    /// </summary>
    public int StartLba { get; set; }

    /// <summary>
    /// Total number of logical sectors allocated for this file.
    /// </summary>
    public int SectorCount { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Ending logical block address (inclusive).
    /// </summary>
    public int EndLba => StartLba + (SectorCount > 0 ? SectorCount - 1 : 0);

    public LbaFileExtentMap() { }

    public LbaFileExtentMap(string filePath, int startLba, int sectorCount, long fileSizeBytes = 0)
    {
        FilePath = filePath;
        StartLba = startLba;
        SectorCount = sectorCount;
        FileSizeBytes = fileSizeBytes;
    }

    /// <summary>
    /// Returns true if the specified LBA address falls within this file's allocated sector extent.
    /// </summary>
    public bool ContainsLba(int lba)
    {
        if (SectorCount <= 0)
        {
            return lba == StartLba;
        }
        return lba >= StartLba && lba < StartLba + SectorCount;
    }

    /// <summary>
    /// Builds a list of LBA file extents from a disc image stream: from its UDF file system when it has one, plus any
    /// ISO 9660 / Joliet file UDF did not list.
    /// <para>
    /// UDF comes first because it is the only complete description on many discs: a Blu-ray image may have no
    /// ISO 9660 tree at all, and where it has one, a file of 4 GiB or more is recorded there with its size cut to
    /// just under 4 GiB, so a sector further into the file would map to no file.
    /// </para>
    /// Throws when the stream holds neither a readable UDF tree nor an ISO 9660 volume.
    /// </summary>
    public static IReadOnlyList<LbaFileExtentMap> FromIsoStream(Stream isoStream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(isoStream);
        long originalPos = isoStream.CanSeek ? isoStream.Position : 0;
        try
        {
            var map = new List<LbaFileExtentMap>();
            var udfRoot = isoStream.CanSeek ? UdfTreeReader.TryRead(isoStream, skipped: null, sessionStartLba) : null;
            if (udfRoot != null)
            {
                AddFiles(udfRoot, map);
            }

            IReadOnlyList<LbaFileExtentMap> isoMap;
            try
            {
                if (isoStream.CanSeek)
                {
                    isoStream.Position = originalPos;
                }
                using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true, sessionStartLba);
                isoMap = FromIsoReader(reader);
            }
            catch (Exception ex) when (map.Count > 0 && ex is InvalidDataException or IOException or ArgumentException or OverflowException)
            {
                // UDF-only image (no ISO 9660 descriptor) or a damaged ISO 9660 tree: the UDF map is the answer.
                return map;
            }

            if (map.Count == 0)
            {
                return isoMap;
            }

            // On a bridge disc both trees name the same data; keep UDF's entry (full name, true size) and add only
            // ISO 9660 files whose data UDF did not list (for example an entry UDF could not read).
            var udfStarts = new HashSet<int>(map.Select(e => e.StartLba));
            map.AddRange(isoMap.Where(e => !udfStarts.Contains(e.StartLba)));
            return map;
        }
        finally
        {
            if (isoStream.CanSeek)
            {
                isoStream.Position = originalPos;
            }
        }
    }

    /// <summary>
    /// Builds a list of LBA file extents from an <see cref="IsoImageReader"/>'s ISO 9660 / Joliet tree. A file stored
    /// as several ISO 9660 extents (multi-extent, used for files of 4 GiB or more) gets one entry per extent, so
    /// the gap between extents belongs to no file. Empty files own no sectors and are left out.
    /// </summary>
    public static IReadOnlyList<LbaFileExtentMap> FromIsoReader(IsoImageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var map = new List<LbaFileExtentMap>();
        foreach (var file in reader.AllFiles)
        {
            AddFile(file, map);
        }
        return map;
    }

    private static void AddFiles(IsoDirectoryEntry directory, List<LbaFileExtentMap> map)
    {
        foreach (var file in directory.Files)
        {
            AddFile(file, map);
        }
        foreach (var sub in directory.SubDirectories)
        {
            AddFiles(sub, map);
        }
    }

    private static void AddFile(IsoFileEntry file, List<LbaFileExtentMap> map)
    {
        if (file.SizeBytes <= 0)
        {
            return; // an empty file has no data sectors; its recorded LBA may be the next file's
        }

        if (file.Extents is { Count: > 1 } extents)
        {
            foreach (var (startLba, sizeBytes) in extents)
            {
                int extentSectors = OpticalConstants.BytesToSectors(sizeBytes, OpticalConstants.DataSectorSize);
                map.Add(new LbaFileExtentMap(file.VirtualPath, startLba, extentSectors, file.SizeBytes));
            }
            return;
        }

        int sectors = OpticalConstants.BytesToSectors(file.SizeBytes, OpticalConstants.DataSectorSize);
        map.Add(new LbaFileExtentMap(file.VirtualPath, file.StartLba, sectors, file.SizeBytes));
    }

    public override string ToString() => $"{FilePath} [LBA {StartLba}..{EndLba}, {SectorCount} sectors, {FileSizeBytes} B]";
}

/// <summary>
/// Detailed result of a disc verification scan comparing physical/virtual disc sectors against an expected image stream.
/// </summary>
public class VerificationResult
{
    /// <summary>
    /// Indicates whether every sector on the disc matched the expected stream bit-for-bit without read errors.
    /// </summary>
    public bool IsMatch { get; init; }

    /// <summary>
    /// Synonym for <see cref="IsMatch"/>.
    /// </summary>
    public bool Success => IsMatch;

    /// <summary>
    /// True when verification was not attempted because the source cannot be compared sector-for-sector
    /// (e.g. an Audio CD burned from compressed .flac/.wav files). <see cref="ErrorMessage"/> explains why.
    /// A skipped result is neither a pass nor a failure.
    /// </summary>
    public bool IsSkipped { get; init; }

    /// <summary>
    /// Total number of logical sectors verified.
    /// </summary>
    public int TotalSectorsVerified { get; init; }

    /// <summary>
    /// Every LBA that failed verification, in LBA order: <see cref="UnreadableLbas"/> and <see cref="DifferingLbas"/>
    /// together. A sector that failed the first read but read correctly on the second (see
    /// <see cref="RecoveredOnRereadLbas"/>) is not in this list.
    /// </summary>
    public IReadOnlyList<int> MismatchedLbas { get; init; } = Array.Empty<int>();

    /// <summary>
    /// LBAs the drive could not read at all (an unrecovered read error), on the first read and again on the second.
    /// The verifier compared zeros for these, so they say nothing about what the disc holds there.
    /// </summary>
    public IReadOnlyList<int> UnreadableLbas { get; init; } = Array.Empty<int>();

    /// <summary>
    /// LBAs the drive read without error but whose bytes differ from the image, after a second read.
    /// </summary>
    public IReadOnlyList<int> DifferingLbas { get; init; } = Array.Empty<int>();

    /// <summary>
    /// LBAs that were unreadable or different on the first read but matched the image when read again from the disc
    /// surface after moving the drive's read head away: a passing read hiccup, not bad data. They do not fail the
    /// verification.
    /// </summary>
    public IReadOnlyList<int> RecoveredOnRereadLbas { get; init; } = Array.Empty<int>();

    /// <summary>Number of sectors that failed the first read but matched on the second (<see cref="RecoveredOnRereadLbas"/>).</summary>
    public int TransientSectorCount => RecoveredOnRereadLbas.Count;

    /// <summary>
    /// Image file paths that own a sector in <see cref="MismatchedLbas"/>.
    /// </summary>
    public IReadOnlyList<string> CorruptedFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Hexadecimal CRC32 checksum of the expected source stream.
    /// </summary>
    public string ExpectedCrc32 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal CRC32 checksum computed from sectors read back from disc.
    /// </summary>
    public string ActualCrc32 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal MD5 hash of the expected source stream.
    /// </summary>
    public string ExpectedMd5 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal MD5 hash computed from sectors read back from disc.
    /// </summary>
    public string ActualMd5 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal SHA-256 hash of the expected source stream.
    /// </summary>
    public string ExpectedSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal SHA-256 hash computed from sectors read back from disc.
    /// </summary>
    public string ActualSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Numeric CRC32 value of expected stream.
    /// </summary>
    public uint ExpectedCrc32Value { get; init; }

    /// <summary>
    /// Numeric CRC32 value of read-back disc stream.
    /// </summary>
    public uint ActualCrc32Value { get; init; }

    /// <summary>
    /// Total execution duration of verification.
    /// </summary>
    public TimeSpan TotalTime { get; init; }

    /// <summary>
    /// Error message describing mismatches or read failures, or null if matched.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True when the underlying sector reads enforced media-surface readback (SCSI FUA for 2,048-byte data reads,
    /// or SYNCHRONIZE CACHE (10) plus seek-away / tray-reload cache invalidation for raw 2,352-byte reads);
    /// false when readback could have been served from a drive or OS buffer.
    /// </summary>
    public bool MediaSurfaceReadbackEnforced { get; init; }

    /// <summary>
    /// Optional note explaining how media-surface readback was enforced or why it could not be guaranteed.
    /// </summary>
    public string? ReadbackNote { get; init; }
}

/// <summary>
/// IEEE 802.3 32-bit Cyclic Redundancy Check incremental calculator.
/// </summary>
internal sealed class Crc32Algorithm
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (crc >> 1) ^ 0xEDB88320u;
                }
                else
                {
                    crc >>= 1;
                }
            }
            table[i] = crc;
        }
        return table;
    }

    private uint _crc = 0xFFFFFFFFu;

    public void Append(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            _crc = Table[(_crc ^ data[i]) & 0xFF] ^ (_crc >> 8);
        }
    }

    public uint GetCurrentHashAsUInt32() => ~_crc;

    /// <summary>Computes reflected IEEE 802.3 CRC-32 with init 0xFFFFFFFF and no final complement.</summary>
    public static uint ComputeUncomplemented(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    public string GetCurrentHashAsHexString() => GetCurrentHashAsUInt32().ToString("X8");

    /// <summary>
    /// Combines two IEEE 802.3 CRC-32 values (<paramref name="crc1"/> over the first chunk and
    /// <paramref name="crc2"/> over a following chunk of <paramref name="length2"/> bytes) into
    /// the CRC-32 of their concatenation using GF(2) matrix squaring.
    /// </summary>
    public static uint Combine(uint crc1, uint crc2, long length2)
    {
        if (length2 <= 0)
        {
            return crc1;
        }

        Span<uint> even = stackalloc uint[32];
        Span<uint> odd = stackalloc uint[32];
        odd[0] = 0xEDB88320u;
        uint row = 1u;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Gf2MatrixSquare(even, odd);
        Gf2MatrixSquare(odd, even);

        ulong len = (ulong)length2;
        do
        {
            Gf2MatrixSquare(even, odd);
            if ((len & 1UL) != 0)
            {
                crc1 = Gf2MatrixTimes(even, crc1);
            }
            len >>= 1;
            if (len == 0)
            {
                break;
            }

            Gf2MatrixSquare(odd, even);
            if ((len & 1UL) != 0)
            {
                crc1 = Gf2MatrixTimes(odd, crc1);
            }
            len >>= 1;
        } while (len != 0);

        return crc1 ^ crc2;
    }

    private static uint Gf2MatrixTimes(ReadOnlySpan<uint> matrix, uint vec)
    {
        uint sum = 0;
        int idx = 0;
        while (vec != 0)
        {
            if ((vec & 1u) != 0)
            {
                sum ^= matrix[idx];
            }
            vec >>= 1;
            idx++;
        }
        return sum;
    }

    private static void Gf2MatrixSquare(Span<uint> square, ReadOnlySpan<uint> mat)
    {
        for (int n = 0; n < 32; n++)
        {
            square[n] = Gf2MatrixTimes(mat, mat[n]);
        }
    }
}

/// <summary>
/// Sector-by-sector verification service for optical media, validating recorded discs against
/// source image streams or compilations with CRC32, MD5, and SHA-256 checksums and file defect mapping.
/// </summary>
public class DiscVerificationService
{
    /// <summary>
    /// At most this many failed sectors get a second read (each keeps its expected 2 KiB in memory until then); past
    /// it the disc is badly damaged and the rest are reported from the first read.
    /// </summary>
    public const int MaxSectorsToReread = SectorRereadHelper.MaxSectorsToReread;

    /// <summary>
    /// Verifies a recorded disc sector-by-sector against an expected image stream.
    /// <paramref name="strict"/> forces every read from the disc; null follows <see cref="VerifyPreferences.StrictByDefault"/>.
    /// Without it only the stretches that can still sit in the drive's memory are forced.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstStreamAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        Stream expectedStream,
        bool raw2352Mode,
        IReadOnlyList<LbaFileExtentMap>? fileMap = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default,
        bool? strict = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(expectedStream);

        ct.ThrowIfCancellationRequested();

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return new VerificationResult
            {
                IsMatch = false,
                ErrorMessage = "No optical media present in recorder for verification."
            };
        }

        int sectorSize = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

        // Auto-extract LBA file map from ISO stream if not provided
        if ((fileMap == null || fileMap.Count == 0) && !raw2352Mode && expectedStream.CanSeek && expectedStream.Length >= 17 * OpticalConstants.DataSectorSize)
        {
            try
            {
                fileMap = LbaFileExtentMap.FromIsoStream(expectedStream);
            }
            catch
            {
                // Not an ISO 9660 stream or parsing failed; proceed without file mapping
                fileMap = Array.Empty<LbaFileExtentMap>();
            }
        }

        if (expectedStream.CanSeek)
        {
            expectedStream.Position = 0;
        }

        long streamLength = expectedStream.CanSeek ? expectedStream.Length : -1;
        int totalSectors = streamLength >= 0
            ? OpticalConstants.BytesToSectors(streamLength, sectorSize)
            : (mediaStatus.UsedSectors > 0 ? mediaStatus.UsedSectors : 0);

        if (totalSectors <= 0)
        {
            return new VerificationResult
            {
                IsMatch = false,
                ErrorMessage = "Cannot determine sector count to verify from empty or unseekable stream."
            };
        }

        var sw = Stopwatch.StartNew();

        // Sectors that failed the first read, in LBA order. Raw 2,352-byte reads are not read a second time: their
        // cache flush can reload the tray, and a second read would repeat it for every run of failed sectors.
        var failed = new List<SectorRereadHelper.FailedSector>();
        bool secondReadEnabled = !raw2352Mode;

        var expectedCrc = new Crc32Algorithm();
        var actualCrc = new Crc32Algorithm();

        using var expectedMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var actualMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        using var expectedSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actualSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] expectedBuffer = new byte[sectorSize];
        byte[] zeroBuffer = new byte[sectorSize];

        // Disc sectors arrive strictly in LBA order (a retried sector is always the next one), so the expected
        // image is consumed in lockstep with them.
        async ValueTask ReadNextExpectedSectorAsync()
        {
            int bytesRead = 0;
            while (bytesRead < sectorSize)
            {
                int read = await expectedStream.ReadAsync(expectedBuffer.AsMemory(bytesRead, sectorSize - bytesRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                bytesRead += read;
            }
            if (bytesRead < sectorSize)
            {
                Array.Clear(expectedBuffer, bytesRead, sectorSize - bytesRead);
            }

            expectedCrc.Append(expectedBuffer);
            expectedMd5.AppendData(expectedBuffer);
            expectedSha.AppendData(expectedBuffer);
        }

        void AppendActual(ReadOnlySpan<byte> actual)
        {
            actualCrc.Append(actual);
            actualMd5.AppendData(actual);
            actualSha.AppendData(actual);
        }

        void RecordFailure(int lba, ReadOnlySpan<byte> read, bool unreadable)
        {
            bool keep = secondReadEnabled && failed.Count < MaxSectorsToReread;
            failed.Add(new SectorRereadHelper.FailedSector(
                lba,
                unreadable,
                keep ? expectedBuffer.ToArray() : null,
                keep && !unreadable ? read.ToArray() : null));
        }

        var readOutcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            totalSectors,
            raw2352Mode,
            maxRetriesPerSector: 1,
            onSector: async (lba, data) =>
            {
                await ReadNextExpectedSectorAsync().ConfigureAwait(false);
                if (!data.Span.SequenceEqual(expectedBuffer))
                {
                    RecordFailure(lba, data.Span, unreadable: false);
                }
                AppendActual(data.Span);
            },
            onUnreadable: async lba =>
            {
                await ReadNextExpectedSectorAsync().ConfigureAwait(false);
                RecordFailure(lba, ReadOnlySpan<byte>.Empty, unreadable: true);
                AppendActual(zeroBuffer);
                return true;
            },
            onProgress: done => progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                CurrentLba = done,
                TotalLba = totalSectors,
                CurrentActionDescription = "VerifyingSectors",
                BufferLevelPercent = 100.0,
                CurrentSpeedMultiplier = 24.0,
                CurrentBytesPerSecond = 24 * (raw2352Mode ? OpticalConstants.CdAudio1XBytesPerSec : OpticalConstants.CdData1XBytesPerSec)
            }),
            cancellationToken: ct,
            // Always verify from media surface: 2,048-byte uses SCSI FUA; raw 2,352-byte uses SYNCHRONIZE CACHE + seek-away / tray-reload.
            readFromDisc: true,
            strict: strict ?? VerifyPreferences.StrictByDefault).ConfigureAwait(false);

        // Second read of every failed sector, from the disc surface, before anything is called a failure.
        var secondReadErrors = new List<string>();
        var toReread = failed.Where(f => f.Expected != null).ToList();
        if (toReread.Count > 0)
        {
            await SectorRereadHelper.RereadFailedSectorsAsync(
                recorder,
                engine,
                firstVerifiedLba: 0,
                lastVerifiedLba: totalSectors - 1,
                toReread,
                secondReadErrors,
                (done, total) => progress?.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = sw.Elapsed,
                    CurrentLba = done,
                    TotalLba = total,
                    CurrentActionDescription = "RereadingFailedSectors"
                }),
                ct).ConfigureAwait(false);
        }

        progress?.Report(new BurnProgressSnapshot
        {
            ElapsedTime = sw.Elapsed,
            CurrentLba = totalSectors,
            TotalLba = totalSectors,
            CurrentActionDescription = "Completed"
        });

        var stillFailed = failed.Where(f => !f.Recovered).ToList();
        var mismatchedLbas = stillFailed.Select(f => f.Lba).ToList();
        var unreadableLbas = stillFailed.Where(f => f.Unreadable).Select(f => f.Lba).ToList();
        var differingLbas = stillFailed.Where(f => !f.Unreadable).Select(f => f.Lba).ToList();
        var recoveredLbas = failed.Where(f => f.Recovered).Select(f => f.Lba).ToList();
        int notReread = failed.Count(f => !f.Reread);
        var corruptedFiles = SectorRereadHelper.MapCorruptedFiles(mismatchedLbas, fileMap);

        uint expCrcVal = expectedCrc.GetCurrentHashAsUInt32();
        uint actCrcVal = actualCrc.GetCurrentHashAsUInt32();

        string expCrc = expectedCrc.GetCurrentHashAsHexString();
        string actCrc = actualCrc.GetCurrentHashAsHexString();

        string expMd5 = Convert.ToHexString(expectedMd5.GetHashAndReset());
        string actMd5 = Convert.ToHexString(actualMd5.GetHashAndReset());

        string expSha = Convert.ToHexString(expectedSha.GetHashAndReset());
        string actSha = Convert.ToHexString(actualSha.GetHashAndReset());

        // The "actual" hashes above describe the first read. When the second read changed what the disc gave, they
        // are worked out again from the final reads.
        if (failed.Count > 0 && stillFailed.Count == 0)
        {
            // Every sector finally read back exactly as the image: the disc's hashes are the image's.
            (actCrcVal, actCrc, actMd5, actSha) = (expCrcVal, expCrc, expMd5, expSha);
        }
        else if (failed.Any(f => f.ChangedOnReread) && failed.All(f => f.Expected != null) && expectedStream.CanSeek)
        {
            (actCrcVal, actCrc, actMd5, actSha) = await HashFinalReadsAsync(expectedStream, totalSectors, sectorSize, stillFailed, ct).ConfigureAwait(false);
        }

        bool isMatch = mismatchedLbas.Count == 0 && string.Equals(expSha, actSha, StringComparison.OrdinalIgnoreCase);

        return new VerificationResult
        {
            IsMatch = isMatch,
            TotalSectorsVerified = totalSectors,
            MismatchedLbas = mismatchedLbas,
            UnreadableLbas = unreadableLbas,
            DifferingLbas = differingLbas,
            RecoveredOnRereadLbas = recoveredLbas,
            CorruptedFiles = corruptedFiles,
            ExpectedCrc32 = expCrc,
            ActualCrc32 = actCrc,
            ExpectedMd5 = expMd5,
            ActualMd5 = actMd5,
            ExpectedSha256 = expSha,
            ActualSha256 = actSha,
            ExpectedCrc32Value = expCrcVal,
            ActualCrc32Value = actCrcVal,
            TotalTime = sw.Elapsed,
            ErrorMessage = isMatch
                ? null
                : SectorRereadHelper.DescribeFailure(mismatchedLbas.Count, unreadableLbas, differingLbas, recoveredLbas.Count,
                    notReread, secondReadEnabled, fileMap, secondReadErrors),
            MediaSurfaceReadbackEnforced = readOutcome.MediaSurfaceReadbackEnforced,
            ReadbackNote = readOutcome.ReadbackNote
        };
    }

    /// <summary>
    /// Verifies a recorded data disc against an .iso file on disk.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstIsoFileAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string isoFilePath,
        IReadOnlyList<LbaFileExtentMap>? fileMap = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default,
        bool? strict = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoFilePath);
        if (!File.Exists(isoFilePath))
        {
            throw new FileNotFoundException("ISO image file not found.", isoFilePath);
        }

        await using var fs = new FileStream(isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return await VerifyDiscAgainstStreamAsync(recorder, engine, fs, raw2352Mode: false, fileMap, progress, ct, strict).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a recorded disc against a virtual filesystem compilation tree.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstCompilationAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        CompilationDirectory rootDirectory,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);

        long estimatedPayloadBytes = rootDirectory.GetAllFilesRecursive().Sum(f => f.SizeBytes);
        Stream isoStream = estimatedPayloadBytes > 128L * 1024 * 1024
            ? new FileStream(
                Path.Combine(Path.GetTempPath(), $"cdburner_verify_{Guid.NewGuid():N}.tmp"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 65536,
                options: FileOptions.DeleteOnClose | FileOptions.SequentialScan | FileOptions.Asynchronous)
            : new MemoryStream();

        await using (isoStream.ConfigureAwait(false))
        {
            await IsoUdfImageBuilder.BuildAsync(rootDirectory, isoStream, buildOptions, null, ct).ConfigureAwait(false);
            isoStream.Position = 0;
            var fileMap = LbaFileExtentMap.FromIsoStream(isoStream);
            isoStream.Position = 0;
            return await VerifyDiscAgainstStreamAsync(recorder, engine, isoStream, raw2352Mode: false, fileMap, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// CRC-32, MD5 and SHA-256 of what the disc finally gave: the image's bytes, except for the sectors that still
    /// failed (zeros where unreadable, the bytes read where they differ).
    /// </summary>
    private static async Task<(uint Crc, string CrcHex, string Md5, string Sha256)> HashFinalReadsAsync(
        Stream expectedStream,
        int totalSectors,
        int sectorSize,
        List<SectorRereadHelper.FailedSector> stillFailed,
        CancellationToken ct)
    {
        var finalBytes = stillFailed.ToDictionary(f => f.Lba, f => f.Unreadable ? null : f.Read);
        var crc = new Crc32Algorithm();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] sector = new byte[sectorSize];
        byte[] zeros = new byte[sectorSize];

        expectedStream.Position = 0;
        for (int lba = 0; lba < totalSectors; lba++)
        {
            int filled = 0;
            while (filled < sectorSize)
            {
                int read = await expectedStream.ReadAsync(sector.AsMemory(filled, sectorSize - filled), ct).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
            }
            if (filled < sectorSize)
            {
                Array.Clear(sector, filled, sectorSize - filled);
            }

            ReadOnlySpan<byte> bytes = finalBytes.TryGetValue(lba, out var final) ? (final ?? zeros) : sector;
            crc.Append(bytes);
            md5.AppendData(bytes);
            sha.AppendData(bytes);
        }

        return (crc.GetCurrentHashAsUInt32(), crc.GetCurrentHashAsHexString(),
            Convert.ToHexString(md5.GetHashAndReset()), Convert.ToHexString(sha.GetHashAndReset()));
    }
}
