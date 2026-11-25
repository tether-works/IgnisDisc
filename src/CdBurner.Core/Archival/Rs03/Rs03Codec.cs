using System.Buffers;
using System.Security.Cryptography;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Clean-room RS03 raw-sector ECC codec supporting standalone <c>.ecc</c> files and augmented ISO/UDF images.
/// </summary>
public static class Rs03Codec
{
    private const int ChunkSectors = 256;

    /// <summary>
    /// Creates RS03 parity for <paramref name="imagePath"/> either as a standalone <c>.ecc</c> file or by
    /// augmenting the image in place up to the target medium capacity.
    /// </summary>
    public static async Task<Rs03CreateResult> CreateAsync(
        string imagePath,
        Rs03CreateOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Source image file not found: '{imagePath}'.", imagePath);
        }

        string? guardWarning = null;
        if (options.Mode == Rs03Mode.AugmentedImage && options.MediaType is { } mt)
        {
            var guard = Rs03MediaGuard.Evaluate(mt, options.Mode, options.AllowRewritableAugmented);
            if (!guard.IsAllowed)
            {
                throw new InvalidOperationException(guard.BlockingReason);
            }
            guardWarning = guard.WarningMessage;
        }

        long fileLength = new FileInfo(imagePath).Length;
        if (fileLength <= 0)
        {
            throw new InvalidOperationException("Cannot create RS03 parity for an empty image.");
        }

        long dataSectors = (fileLength + Rs03Constants.SectorSize - 1) / Rs03Constants.SectorSize;
        int inLast = (int)(fileLength % Rs03Constants.SectorSize);
        if (inLast == 0)
        {
            inLast = Rs03Constants.SectorSize;
        }

        Rs03Layout layout;
        string? eccFilePath = null;
        if (options.Mode == Rs03Mode.EccFile)
        {
            int nroots = options.ParityRoots
                ?? (options.RedundancyPercent.HasValue
                    ? Rs03Layout.RootsFromRedundancyPercent(options.RedundancyPercent.Value)
                    : Rs03Constants.DefaultNormalRoots);
            layout = Rs03Layout.ForEccFile(dataSectors, inLast, nroots);
            eccFilePath = string.IsNullOrWhiteSpace(options.EccFilePath) ? imagePath + ".ecc" : options.EccFilePath;

            if (File.Exists(eccFilePath) && !options.Overwrite)
            {
                throw new IOException($"Target ECC file already exists: '{eccFilePath}'.");
            }
        }
        else
        {
            await using (var probeFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (TryReadEccHeaderAtSector(probeFs, dataSectors, out _) != null)
                {
                    throw new InvalidOperationException("Image already contains an RS03 parity trailer.");
                }
                if (TryFindAugmentedHeader(probeFs, exhaustiveScan: false, out var existing, out _) && existing != null)
                {
                    throw new InvalidOperationException("Image already contains an RS03 parity layout.");
                }
            }

            long mediumCap = options.MediumCapacitySectors
                ?? (options.MediaType.HasValue
                    ? Rs03Constants.ResolveMediumCapacitySectors(options.MediaType.Value, options.NoBdrDefectManagement)
                    : Rs03Constants.SelectSmallestMediumCapacity(dataSectors, options.NoBdrDefectManagement));

            layout = Rs03Layout.ForAugmentedImage(dataSectors, inLast, mediumCap);
        }

        // Compute payload MD5 and fingerprint sector (LBA 16).
        (byte[] mediumFp, byte[] dataMd5) = await ComputeFingerprintAndMd5Async(imagePath, fileLength, dataSectors, cancellationToken)
            .ConfigureAwait(false);

        byte[] flags = new byte[4];
        flags[0] = Rs03Constants.MethodFlagDataMd5;
        if (options.Mode == Rs03Mode.EccFile)
        {
            flags[0] |= Rs03Constants.MethodFlagEccFile;
        }

        var header = new Rs03EccHeader
        {
            MethodFlags = flags,
            MediumFingerprint = mediumFp,
            MediumSum = dataMd5,
            DataSectors = layout.DataSectors,
            DataBytes = layout.DataLayerCount,
            EccBytes = layout.ParityRootCount,
            InLastBytes = layout.InLastBytes,
            SectorsPerLayer = layout.SectorsPerLayer
        };
        byte[] headerBytes = header.ToBytes();

        if (options.Mode == Rs03Mode.EccFile)
        {
            string? eccDir = Path.GetDirectoryName(Path.GetFullPath(eccFilePath!));
            if (!string.IsNullOrEmpty(eccDir))
            {
                Directory.CreateDirectory(eccDir);
            }

            await using var imageFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            await using var eccFs = new FileStream(eccFilePath!, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
            await eccFs.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            eccFs.SetLength(layout.EccFileTotalSectors * Rs03Constants.SectorSize);

            await EncodeAllColumnsAsync(imageFs, eccFs, layout, header, progress, cancellationToken).ConfigureAwait(false);
            await eccFs.FlushAsync(cancellationToken).ConfigureAwait(false);

            return new Rs03CreateResult(
                Success: true,
                Mode: Rs03Mode.EccFile,
                ImagePath: imagePath,
                EccFilePath: eccFilePath,
                Layout: layout,
                MediumFingerprint: mediumFp,
                DataMd5: dataMd5,
                Warning: null,
                Summary: $"Created RS03 .ecc file ({layout.ParityRootCount} parity roots, {layout.RedundancyPercent:F1}% redundancy, {layout.EccFileTotalSectors:N0} sectors).");
        }
        else
        {
            await using var imageFs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);

            // Pad incomplete last data sector to sector boundary before writing EccHeader.
            if (inLast < Rs03Constants.SectorSize)
            {
                imageFs.Position = fileLength;
                byte[] zeroTail = new byte[Rs03Constants.SectorSize - inLast];
                await imageFs.WriteAsync(zeroTail, cancellationToken).ConfigureAwait(false);
            }

            // Write EccHeader at DataSectors, followed by data padding sectors up to FirstCrcSector.
            imageFs.Position = layout.EccHeaderSector * Rs03Constants.SectorSize;
            await imageFs.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            byte[] padSector = new byte[Rs03Constants.SectorSize];
            for (long p = 0; p < layout.DataPaddingSectors; p++)
            {
                long lba = layout.DataSectors + Rs03Constants.EccHeaderSectors + p;
                Rs03MarkerCodec.WritePaddingSector(padSector, lba, mediumFp, Rs03Constants.FingerprintSector);
                await imageFs.WriteAsync(padSector, cancellationToken).ConfigureAwait(false);
            }

            imageFs.SetLength(layout.TotalVirtualSectors * Rs03Constants.SectorSize);
            await EncodeAllColumnsAsync(imageFs, imageFs, layout, header, progress, cancellationToken).ConfigureAwait(false);
            await imageFs.FlushAsync(cancellationToken).ConfigureAwait(false);

            return new Rs03CreateResult(
                Success: true,
                Mode: Rs03Mode.AugmentedImage,
                ImagePath: imagePath,
                EccFilePath: null,
                Layout: layout,
                MediumFingerprint: mediumFp,
                DataMd5: dataMd5,
                Warning: guardWarning,
                Summary: $"Augmented image with RS03 parity ({layout.DataSectors:N0} data -> {layout.TotalVirtualSectors:N0} total sectors, {layout.ParityRootCount} parity roots, {layout.RedundancyPercent:F1}% redundancy).");
        }
    }

    /// <summary>
    /// Verifies RS03 parity for <paramref name="imagePath"/> (standalone <paramref name="eccFilePath"/> or augmented image).
    /// </summary>
    public static async Task<Rs03VerifyResult> VerifyAsync(
        string imagePath,
        string? eccFilePath = null,
        Rs03VerifyOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: '{imagePath}'.", imagePath);
        }

        options ??= new Rs03VerifyOptions();

        await using var imageFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        FileStream? eccFs = null;
        try
        {
            Rs03EccHeader header;
            bool headerReconstructed;
            Rs03Mode mode;

            if (!string.IsNullOrWhiteSpace(eccFilePath))
            {
                if (!File.Exists(eccFilePath))
                {
                    throw new FileNotFoundException($"ECC file not found: '{eccFilePath}'.", eccFilePath);
                }
                eccFs = new FileStream(eccFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                if (!TryResolveEccFileHeader(eccFs, out header!, out headerReconstructed))
                {
                    throw new InvalidDataException($"'{eccFilePath}' is not a valid RS03 .ecc file.");
                }
                mode = Rs03Mode.EccFile;
            }
            else
            {
                if (!TryFindAugmentedHeader(imageFs, exhaustiveScan: true, out header!, out headerReconstructed))
                {
                    throw new InvalidDataException($"No RS03 parity layout found in '{imagePath}'.");
                }
                mode = Rs03Mode.AugmentedImage;
            }

            var layout = Rs03Layout.FromHeader(header);
            var fpStatus = CheckFingerprint(imageFs, header);

            if (options.QuickHeaderOnly)
            {
                return new Rs03VerifyResult(
                    IsValid: fpStatus != Rs03FingerprintStatus.Mismatch,
                    Mode: mode,
                    Layout: layout,
                    HeaderReconstructed: headerReconstructed,
                    FingerprintStatus: fpStatus,
                    DataMd5Checked: false,
                    DataMd5Matches: false,
                    MissingDataSectors: 0,
                    CrcErrorSectors: 0,
                    MissingCrcSectors: 0,
                    CrcSignatureErrors: 0,
                    MissingEccSectors: 0,
                    SyndromeErrorColumns: 0,
                    MaxErasuresInAnyColumn: 0,
                    IsRecoverable: true,
                    Summary: $"RS03 header valid ({mode}, {layout.ParityRootCount} roots, fingerprint {fpStatus}).");
            }

            FileStream crcEccStream = mode == Rs03Mode.EccFile ? eccFs! : imageFs;
            var rs = new Rs03ReedSolomon(layout.ParityRootCount);

            long spl = layout.SectorsPerLayer;
            int ndata = layout.DataLayerCount;
            int nroots = layout.ParityRootCount;
            int payloadLayers = ndata - 1;

            int missingData = 0, crcErrors = 0, missingCrc = 0, crcSigErrors = 0, missingEcc = 0, synBadCols = 0;
            int maxErasures = 0;

            byte[][] layerBufs = AllocateLayerBuffers(Rs03Constants.FieldMax, ChunkSectors);
            byte[] synBuf = new byte[nroots * Rs03Constants.SectorSize];
            byte[] prevCrcSector = new byte[Rs03Constants.SectorSize];

            // Preload last CRC block for wrap-around to column 0.
            ReadVirtualSector(imageFs, crcEccStream, layout, header, ndata - 1, spl - 1, prevCrcSector);
            bool prevCrcValid = Rs03CrcBlock.TryParse(prevCrcSector, out var prevCrcBlock) &&
                                prevCrcBlock != null &&
                                !Rs03MarkerCodec.IsDeadSector(prevCrcSector);

            for (long chunkStart = 0; chunkStart < spl; chunkStart += ChunkSectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunkCount = (int)Math.Min(ChunkSectors, spl - chunkStart);

                ReadChunkLayers(imageFs, crcEccStream, layout, header, chunkStart, chunkCount, layerBufs);

                for (int c = 0; c < chunkCount; c++)
                {
                    int colOffset = c * Rs03Constants.SectorSize;
                    int colErasures = 0;

                    for (int l = 0; l < payloadLayers; l++)
                    {
                        var sec = layerBufs[l].AsSpan(colOffset, Rs03Constants.SectorSize);
                        long lba = layout.GetDiskSectorIndex(l, chunkStart + c);
                        bool isDead = IsMissingSectorOnDisk(mode, imageFs.Length, lba, sec);
                        if (isDead)
                        {
                            missingData++;
                            colErasures++;
                        }
                        else if (prevCrcValid && prevCrcBlock != null)
                        {
                            uint actualCrc = Crc32Algorithm.ComputeUncomplemented(sec);
                            if (actualCrc != prevCrcBlock.Crc[l])
                            {
                                crcErrors++;
                                colErasures++;
                            }
                        }
                    }

                    var crcSec = layerBufs[ndata - 1].AsSpan(colOffset, Rs03Constants.SectorSize);
                    bool crcDead = Rs03MarkerCodec.IsDeadSector(crcSec) ||
                                   (mode == Rs03Mode.EccFile && (layout.FirstCrcSector + chunkStart + c + 1) * Rs03Constants.SectorSize > crcEccStream.Length) ||
                                   (mode == Rs03Mode.AugmentedImage && (layout.FirstCrcSector + chunkStart + c + 1) * Rs03Constants.SectorSize > imageFs.Length);
                    Rs03CrcBlock? curCrcBlock = null;
                    bool curCrcValid = false;
                    if (crcDead)
                    {
                        missingCrc++;
                        colErasures++;
                    }
                    else if (!Rs03CrcBlock.TryParse(crcSec, out curCrcBlock) || curCrcBlock == null)
                    {
                        crcSigErrors++;
                        colErasures++;
                    }
                    else
                    {
                        curCrcValid = true;
                    }

                    for (int r = 0; r < nroots; r++)
                    {
                        int l = ndata + r;
                        var eccSec = layerBufs[l].AsSpan(colOffset, Rs03Constants.SectorSize);
                        long eccLba = layout.GetDiskSectorIndex(l, chunkStart + c);
                        bool eccMissing = Rs03MarkerCodec.IsDeadSector(eccSec) ||
                                          (eccLba + 1) * Rs03Constants.SectorSize > crcEccStream.Length;
                        if (eccMissing)
                        {
                            missingEcc++;
                            colErasures++;
                        }
                    }

                    if (colErasures > maxErasures)
                    {
                        maxErasures = colErasures;
                    }

                    // Build 255-layer view at colOffset for ComputeSyndromes.
                    Span<byte[]> singleColumn = layerBufs;
                    rs.ComputeSyndromes(singleColumn, colOffset, Rs03Constants.SectorSize, synBuf);
                    bool synBad = false;
                    for (int i = 0; i < synBuf.Length; i++)
                    {
                        if (synBuf[i] != 0)
                        {
                            synBad = true;
                            break;
                        }
                    }
                    if (synBad)
                    {
                        synBadCols++;
                    }

                    prevCrcBlock = curCrcBlock;
                    prevCrcValid = curCrcValid;
                }

                progress?.Report(Math.Clamp((double)(chunkStart + chunkCount) / spl, 0.0, 1.0));
            }

            bool md5Checked = header.HasDataMd5 && missingData == 0;
            bool md5Match = false;
            if (md5Checked)
            {
                long payloadBytes = (layout.DataSectors - 1) * Rs03Constants.SectorSize + layout.InLastBytes;
                if (imageFs.Length >= payloadBytes)
                {
                    byte[] actualMd5 = await ComputeStreamMd5Async(imageFs, payloadBytes, cancellationToken).ConfigureAwait(false);
                    md5Match = actualMd5.AsSpan().SequenceEqual(header.MediumSum);
                }
            }

            bool isValid = missingData == 0 && crcErrors == 0 && missingCrc == 0 && crcSigErrors == 0 &&
                           missingEcc == 0 && synBadCols == 0 && (!md5Checked || md5Match) &&
                           fpStatus != Rs03FingerprintStatus.Mismatch;
            bool isRecoverable = maxErasures <= nroots && (!headerReconstructed || crcSigErrors < spl);

            string summary = isValid
                ? $"RS03 verify passed ({mode}, {layout.DataSectors:N0} data sectors, {nroots} parity roots)."
                : $"RS03 verify found defects (missing data={missingData}, CRC errors={crcErrors}, bad CRC blocks={missingCrc + crcSigErrors}, missing ECC={missingEcc}, syndrome errors={synBadCols}, max erasures/col={maxErasures}/{nroots}).";

            return new Rs03VerifyResult(
                IsValid: isValid,
                Mode: mode,
                Layout: layout,
                HeaderReconstructed: headerReconstructed,
                FingerprintStatus: fpStatus,
                DataMd5Checked: md5Checked,
                DataMd5Matches: md5Match,
                MissingDataSectors: missingData,
                CrcErrorSectors: crcErrors,
                MissingCrcSectors: missingCrc,
                CrcSignatureErrors: crcSigErrors,
                MissingEccSectors: missingEcc,
                SyndromeErrorColumns: synBadCols,
                MaxErasuresInAnyColumn: maxErasures,
                IsRecoverable: isRecoverable,
                Summary: summary);
        }
        finally
        {
            if (eccFs != null)
            {
                await eccFs.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Repairs damaged sectors in <paramref name="imagePath"/> (and in <paramref name="eccFilePath"/> for standalone mode)
    /// using RS03 parity. Returns detailed counts and verifies the repaired image when <see cref="Rs03RepairOptions.VerifyAfterRepair"/> is true.
    /// </summary>
    public static async Task<Rs03RepairResult> RepairAsync(
        string imagePath,
        string? eccFilePath = null,
        Rs03RepairOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: '{imagePath}'.", imagePath);
        }

        options ??= new Rs03RepairOptions();
        HashSet<long> callerBadSectors = options.UnreadableSectors is { Count: > 0 } u
            ? u.Select(x => (long)x).ToHashSet()
            : new HashSet<long>();

        Rs03EccHeader header;
        bool headerReconstructed;
        Rs03Mode mode;
        Rs03Layout layout;
        int repairedData = 0, repairedCrc = 0, repairedEcc = 0, unrepairedCols = 0;
        var unrepairedSectors = new List<int>();

        await using (var imageFs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
        {
            FileStream? eccFs = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(eccFilePath))
                {
                    if (!File.Exists(eccFilePath))
                    {
                        throw new FileNotFoundException($"ECC file not found: '{eccFilePath}'.", eccFilePath);
                    }
                    eccFs = new FileStream(eccFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
                    if (!TryResolveEccFileHeader(eccFs, out header!, out headerReconstructed))
                    {
                        throw new InvalidDataException($"'{eccFilePath}' is not a valid RS03 .ecc file.");
                    }
                    mode = Rs03Mode.EccFile;
                }
                else
                {
                    if (!TryFindAugmentedHeader(imageFs, exhaustiveScan: true, out header!, out headerReconstructed))
                    {
                        throw new InvalidDataException($"No RS03 parity layout found in '{imagePath}'.");
                    }
                    mode = Rs03Mode.AugmentedImage;
                }

                layout = Rs03Layout.FromHeader(header);
                var fpStatus = CheckFingerprint(imageFs, header);
                if (fpStatus == Rs03FingerprintStatus.Mismatch && !options.AllowFingerprintMismatch)
                {
                    return new Rs03RepairResult(
                        Success: false,
                        Mode: mode,
                        Layout: layout,
                        HeaderReconstructed: headerReconstructed,
                        RepairedDataSectors: 0,
                        RepairedCrcSectors: 0,
                        RepairedEccSectors: 0,
                        UnrepairedColumns: 0,
                        UnrepairedSectors: Array.Empty<int>(),
                        VerifiedAfterRepair: false,
                        Summary: "Fingerprint mismatch at sector 16: the RS03 parity does not match this image (possibly leftover ECC on rewritable media).");
                }

                // Expand truncated files before repairing.
                long expectedDataBytes = (layout.DataSectors - 1) * Rs03Constants.SectorSize + layout.InLastBytes;
                if (mode == Rs03Mode.EccFile)
                {
                    if (imageFs.Length < expectedDataBytes)
                    {
                        imageFs.SetLength(expectedDataBytes);
                    }
                    long expectedEccBytes = layout.EccFileTotalSectors * Rs03Constants.SectorSize;
                    if (eccFs!.Length < expectedEccBytes)
                    {
                        eccFs.SetLength(expectedEccBytes);
                    }
                    if (headerReconstructed)
                    {
                        eccFs.Position = 0;
                        await eccFs.WriteAsync(header.ToBytes(), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    long expectedImgBytes = layout.TotalVirtualSectors * Rs03Constants.SectorSize;
                    if (imageFs.Length < expectedImgBytes)
                    {
                        imageFs.SetLength(expectedImgBytes);
                    }
                    if (headerReconstructed)
                    {
                        imageFs.Position = layout.EccHeaderSector * Rs03Constants.SectorSize;
                        await imageFs.WriteAsync(header.ToBytes(), cancellationToken).ConfigureAwait(false);
                    }
                }

                FileStream crcEccStream = mode == Rs03Mode.EccFile ? eccFs! : imageFs;
                var rs = new Rs03ReedSolomon(layout.ParityRootCount);
                long spl = layout.SectorsPerLayer;
                int ndata = layout.DataLayerCount;
                int nroots = layout.ParityRootCount;
                int payloadLayers = ndata - 1;

                byte[][] layerBufs = AllocateLayerBuffers(Rs03Constants.FieldMax, ChunkSectors);
                byte[] prevCrcSector = new byte[Rs03Constants.SectorSize];
                ReadVirtualSector(imageFs, crcEccStream, layout, header, ndata - 1, spl - 1, prevCrcSector);
                bool lastCrcWasInitiallyValid = Rs03CrcBlock.TryParse(prevCrcSector, out var prevCrcBlock) &&
                                                prevCrcBlock != null &&
                                                !Rs03MarkerCodec.IsDeadSector(prevCrcSector);
                bool prevCrcValid = lastCrcWasInitiallyValid;

                var erasures = new List<int>(nroots);
                byte[] synProbe = new byte[nroots * Rs03Constants.SectorSize];

                int passes = lastCrcWasInitiallyValid ? 1 : 2;
                for (int pass = 0; pass < passes; pass++)
                {
                    long colLimit = pass == 0 ? spl : Math.Min(spl, 1);
                    if (pass == 1)
                    {
                        ReadVirtualSector(imageFs, crcEccStream, layout, header, ndata - 1, spl - 1, prevCrcSector);
                        prevCrcValid = Rs03CrcBlock.TryParse(prevCrcSector, out prevCrcBlock) &&
                                       prevCrcBlock != null &&
                                       !Rs03MarkerCodec.IsDeadSector(prevCrcSector);
                    }

                    for (long chunkStart = 0; chunkStart < colLimit; chunkStart += ChunkSectors)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int chunkCount = (int)Math.Min(ChunkSectors, colLimit - chunkStart);
                        ReadChunkLayers(imageFs, crcEccStream, layout, header, chunkStart, chunkCount, layerBufs);

                        for (int c = 0; c < chunkCount; c++)
                        {
                            long col = chunkStart + c;
                            int colOffset = c * Rs03Constants.SectorSize;
                            erasures.Clear();

                            for (int l = 0; l < payloadLayers; l++)
                            {
                                var sec = layerBufs[l].AsSpan(colOffset, Rs03Constants.SectorSize);
                                long lba = layout.GetDiskSectorIndex(l, col);
                                bool isBad = callerBadSectors.Contains(lba) || Rs03MarkerCodec.IsDeadSector(sec);
                                if (!isBad && prevCrcValid && prevCrcBlock != null)
                                {
                                    uint crc = Crc32Algorithm.ComputeUncomplemented(sec);
                                    if (crc != prevCrcBlock.Crc[l])
                                    {
                                        isBad = true;
                                    }
                                }
                                if (isBad)
                                {
                                    erasures.Add(l);
                                }
                            }

                            var crcSec = layerBufs[ndata - 1].AsSpan(colOffset, Rs03Constants.SectorSize);
                            long crcDiskLba = layout.GetDiskSectorIndex(ndata - 1, col);
                            Rs03CrcBlock? curCrcBlock = null;
                            bool crcBad = callerBadSectors.Contains(crcDiskLba) ||
                                          Rs03MarkerCodec.IsDeadSector(crcSec) ||
                                          !Rs03CrcBlock.TryParse(crcSec, out curCrcBlock) ||
                                          curCrcBlock == null;
                            if (crcBad)
                            {
                                erasures.Add(ndata - 1);
                            }

                            for (int r = 0; r < nroots; r++)
                            {
                                int l = ndata + r;
                                var eccSec = layerBufs[l].AsSpan(colOffset, Rs03Constants.SectorSize);
                                long eccLba = layout.GetDiskSectorIndex(l, col);
                                if (callerBadSectors.Contains(eccLba) || Rs03MarkerCodec.IsDeadSector(eccSec))
                                {
                                    erasures.Add(l);
                                }
                            }

                            bool needsDecode = erasures.Count > 0;
                            if (!needsDecode)
                            {
                                rs.ComputeSyndromes(layerBufs, colOffset, Rs03Constants.SectorSize, synProbe);
                                for (int i = 0; i < synProbe.Length; i++)
                                {
                                    if (synProbe[i] != 0)
                                    {
                                        needsDecode = true;
                                        break;
                                    }
                                }
                            }

                            if (needsDecode)
                            {
                                byte[][] beforeSnapshots = new byte[erasures.Count][];
                                if (erasures.Count <= nroots)
                                {
                                    for (int i = 0; i < erasures.Count; i++)
                                    {
                                        beforeSnapshots[i] = layerBufs[erasures[i]].AsSpan(colOffset, Rs03Constants.SectorSize).ToArray();
                                    }
                                }

                                bool decoded = rs.TryDecodeColumn(
                                    layerBufs,
                                    colOffset,
                                    Rs03Constants.SectorSize,
                                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(erasures),
                                    out int unexpectedCount);

                                if (!decoded)
                                {
                                    if (pass == passes - 1)
                                    {
                                        unrepairedCols++;
                                        foreach (int layerIdx in erasures)
                                        {
                                            if (layerIdx < payloadLayers)
                                            {
                                                long badLba = layout.GetDiskSectorIndex(layerIdx, col);
                                                if (badLba < layout.DataSectors && badLba <= int.MaxValue)
                                                {
                                                    unrepairedSectors.Add((int)badLba);
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Write back any layer that changed (known erasures or unexpected error corrections).
                                    var repairedSet = new HashSet<int>();
                                    for (int i = 0; i < erasures.Count; i++)
                                    {
                                        int l = erasures[i];
                                        if (!layerBufs[l].AsSpan(colOffset, Rs03Constants.SectorSize).SequenceEqual(beforeSnapshots[i]))
                                        {
                                            repairedSet.Add(l);
                                        }
                                    }
                                    if (unexpectedCount > 0)
                                    {
                                        for (int l = 0; l < Rs03Constants.FieldMax; l++)
                                        {
                                            repairedSet.Add(l);
                                        }
                                    }

                                    foreach (int l in repairedSet)
                                    {
                                        long secIdx = layout.GetDiskSectorIndex(l, col);
                                        if (l < payloadLayers)
                                        {
                                            if (mode == Rs03Mode.EccFile && secIdx >= layout.DataSectors)
                                            {
                                                continue;
                                            }
                                            int len = secIdx == layout.DataSectors - 1 ? layout.InLastBytes : Rs03Constants.SectorSize;
                                            imageFs.Position = secIdx * Rs03Constants.SectorSize;
                                            await imageFs.WriteAsync(layerBufs[l].AsMemory(colOffset, len), cancellationToken).ConfigureAwait(false);
                                            repairedData++;
                                        }
                                        else if (l == ndata - 1)
                                        {
                                            crcEccStream.Position = secIdx * Rs03Constants.SectorSize;
                                            await crcEccStream.WriteAsync(layerBufs[l].AsMemory(colOffset, Rs03Constants.SectorSize), cancellationToken).ConfigureAwait(false);
                                            repairedCrc++;
                                        }
                                        else
                                        {
                                            crcEccStream.Position = secIdx * Rs03Constants.SectorSize;
                                            await crcEccStream.WriteAsync(layerBufs[l].AsMemory(colOffset, Rs03Constants.SectorSize), cancellationToken).ConfigureAwait(false);
                                            repairedEcc++;
                                        }
                                    }

                                    // Update curCrcBlock from the newly repaired CRC sector.
                                    curCrcBlock = null;
                                    prevCrcValid = Rs03CrcBlock.TryParse(
                                        layerBufs[ndata - 1].AsSpan(colOffset, Rs03Constants.SectorSize),
                                        out curCrcBlock) && curCrcBlock != null;
                                    prevCrcBlock = curCrcBlock;
                                    continue;
                                }
                            }

                            prevCrcBlock = curCrcBlock;
                            prevCrcValid = !crcBad && curCrcBlock != null;
                        }

                        if (pass == 0)
                        {
                            progress?.Report(Math.Clamp((double)(chunkStart + chunkCount) / spl, 0.0, 1.0));
                        }
                    }
                }

                // Enforce exact byte length on repaired payload in EccFile mode.
                if (mode == Rs03Mode.EccFile && imageFs.Length != expectedDataBytes)
                {
                    imageFs.SetLength(expectedDataBytes);
                }

                await imageFs.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (eccFs != null)
                {
                    await eccFs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (eccFs != null)
                {
                    await eccFs.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        bool verified = false;
        if (options.VerifyAfterRepair && unrepairedSectors.Count == 0 && unrepairedCols == 0)
        {
            var verify = await VerifyAsync(imagePath, eccFilePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            verified = verify.IsValid;
        }

        bool ok = unrepairedCols == 0 && (!options.VerifyAfterRepair || verified);
        string summary = ok
            ? $"RS03 repair succeeded ({repairedData} data, {repairedCrc} CRC, {repairedEcc} ECC sectors repaired)."
            : $"RS03 repair incomplete ({unrepairedCols} unrepaired columns, {unrepairedSectors.Count} unrepaired data sectors).";

        return new Rs03RepairResult(
            Success: ok,
            Mode: mode,
            Layout: layout,
            HeaderReconstructed: headerReconstructed,
            RepairedDataSectors: repairedData,
            RepairedCrcSectors: repairedCrc,
            RepairedEccSectors: repairedEcc,
            UnrepairedColumns: unrepairedCols,
            UnrepairedSectors: unrepairedSectors,
            VerifiedAfterRepair: verified,
            Summary: summary);
    }

    /// <summary>
    /// Detects whether <paramref name="imagePath"/> carries an RS03 augmented layout and returns its header/layout if present.
    /// </summary>
    public static bool TryInspectAugmentedImage(string imagePath, out Rs03EccHeader? header, out Rs03Layout? layout)
    {
        header = null;
        layout = null;
        if (!File.Exists(imagePath))
        {
            return false;
        }

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!TryFindAugmentedHeader(fs, exhaustiveScan: false, out header, out _) || header == null)
        {
            return false;
        }
        layout = Rs03Layout.FromHeader(header);
        return true;
    }

    /// <summary>
    /// Probes sector <paramref name="candidateHeaderLba"/> in <paramref name="readSector"/> for a valid non-file RS03
    /// <c>EccHeader</c> whose <c>DataSectors</c> equals <paramref name="candidateHeaderLba"/>. Used to discover the
    /// full RS03-augmented length behind an ISO/UDF volume on rewritable media.
    /// </summary>
    public static long? TryReadAugmentedTotalSectorsAtBoundary(Func<int, byte[]?> readSector, int candidateHeaderLba)
    {
        ArgumentNullException.ThrowIfNull(readSector);
        if (candidateHeaderLba <= 0)
        {
            return null;
        }

        byte[]? s0 = readSector(candidateHeaderLba);
        byte[]? s1 = readSector(candidateHeaderLba + 1);
        if (s0 is not { Length: >= Rs03Constants.SectorSize } || s1 is not { Length: >= Rs03Constants.SectorSize })
        {
            return null;
        }

        byte[] combined = new byte[Rs03Constants.EccHeaderBytes];
        s0.AsSpan(0, Rs03Constants.SectorSize).CopyTo(combined.AsSpan(0, Rs03Constants.SectorSize));
        s1.AsSpan(0, Rs03Constants.SectorSize).CopyTo(combined.AsSpan(Rs03Constants.SectorSize, Rs03Constants.SectorSize));
        if (!Rs03EccHeader.TryParse(combined, out var header) || header == null || header.IsEccFile)
        {
            return null;
        }

        if (header.DataSectors != candidateHeaderLba)
        {
            return null;
        }

        // Guard against stale ECC left over on reused media when sector 16 belongs to a different burn.
        byte[]? fpSector = readSector(header.FingerprintSector);
        if (fpSector is { Length: >= Rs03Constants.SectorSize } && !IsAllZero(fpSector.AsSpan(0, Rs03Constants.SectorSize)))
        {
            byte[] fp = MD5.HashData(fpSector.AsSpan(0, Rs03Constants.SectorSize));
            if (!IsAllZero(header.MediumFingerprint) && !fp.AsSpan().SequenceEqual(header.MediumFingerprint))
            {
                return null;
            }
        }

        return header.SectorsPerLayer * Rs03Constants.FieldMax;
    }

    private static async Task EncodeAllColumnsAsync(
        FileStream imageFs,
        FileStream crcEccStream,
        Rs03Layout layout,
        Rs03EccHeader header,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var rs = new Rs03ReedSolomon(layout.ParityRootCount);
        long spl = layout.SectorsPerLayer;
        int ndata = layout.DataLayerCount;
        int nroots = layout.ParityRootCount;
        int payloadLayers = ndata - 1;

        byte[][] dataAndCrcLayers = AllocateLayerBuffers(ndata, ChunkSectors);
        byte[][] parityLayers = AllocateLayerBuffers(nroots, ChunkSectors);

        // Compute CRC32 of column 0 sectors once for the wrap-around into column spl - 1.
        uint[] firstColCrcs = new uint[payloadLayers];
        byte[] singleSec = new byte[Rs03Constants.SectorSize];
        for (int l = 0; l < payloadLayers; l++)
        {
            ReadPayloadLayerSector(imageFs, layout, header, l, 0, singleSec);
            firstColCrcs[l] = Crc32Algorithm.ComputeUncomplemented(singleSec);
        }

        var crcTemplate = new Rs03CrcBlock
        {
            MethodFlags = (byte[])header.MethodFlags.Clone(),
            CreatorVersion = header.CreatorVersion,
            NeededVersion = header.NeededVersion,
            FingerprintSector = header.FingerprintSector,
            MediumFingerprint = (byte[])header.MediumFingerprint.Clone(),
            MediumSum = (byte[])header.MediumSum.Clone(),
            DataSectors = layout.DataSectors,
            InLastBytes = layout.InLastBytes,
            DataBytes = layout.DataLayerCount,
            EccBytes = layout.ParityRootCount,
            SectorsPerLayer = layout.SectorsPerLayer
        };

        for (long chunkStart = 0; chunkStart < spl; chunkStart += ChunkSectors)
        {
            ct.ThrowIfCancellationRequested();
            int chunkCount = (int)Math.Min(ChunkSectors, spl - chunkStart);
            int chunkBytes = chunkCount * Rs03Constants.SectorSize;

            // Read payload layers 0 .. ndata - 2 for [chunkStart .. chunkStart + chunkCount - 1].
            for (int l = 0; l < payloadLayers; l++)
            {
                for (int c = 0; c < chunkCount; c++)
                {
                    ReadPayloadLayerSector(
                        imageFs,
                        layout,
                        header,
                        l,
                        chunkStart + c,
                        dataAndCrcLayers[l].AsSpan(c * Rs03Constants.SectorSize, Rs03Constants.SectorSize));
                }

                // Populate CRC32s in CrcBlock of column c from sector at column c + 1.
                for (int c = 0; c < chunkCount; c++)
                {
                    uint crc;
                    if (c + 1 < chunkCount)
                    {
                        crc = Crc32Algorithm.ComputeUncomplemented(dataAndCrcLayers[l].AsSpan((c + 1) * Rs03Constants.SectorSize, Rs03Constants.SectorSize));
                    }
                    else if (chunkStart + c + 1 < spl)
                    {
                        ReadPayloadLayerSector(imageFs, layout, header, l, chunkStart + c + 1, singleSec);
                        crc = Crc32Algorithm.ComputeUncomplemented(singleSec);
                    }
                    else
                    {
                        crc = firstColCrcs[l];
                    }

                    // Write into temporary slot in crcTemplate.Crc? No, write directly to CrcBlock buffer per column below.
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        dataAndCrcLayers[ndata - 1].AsSpan(c * Rs03Constants.SectorSize + l * 4, 4),
                        crc);
                }
            }

            // Finalize each CrcBlock in layer ndata - 1 (headers + selfCRC).
            for (int c = 0; c < chunkCount; c++)
            {
                Span<byte> crcSec = dataAndCrcLayers[ndata - 1].AsSpan(c * Rs03Constants.SectorSize, Rs03Constants.SectorSize);
                Array.Clear(crcTemplate.Crc);
                for (int l = 0; l < payloadLayers; l++)
                {
                    crcTemplate.Crc[l] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(crcSec.Slice(l * 4, 4));
                }
                crcTemplate.WriteTo(crcSec);
            }

            rs.EncodeLayers(dataAndCrcLayers, parityLayers, chunkBytes);

            // Write CRC layer and ECC parity layers to output.
            crcEccStream.Position = layout.FirstCrcSector * Rs03Constants.SectorSize + chunkStart * Rs03Constants.SectorSize;
            await crcEccStream.WriteAsync(dataAndCrcLayers[ndata - 1].AsMemory(0, chunkBytes), ct).ConfigureAwait(false);

            for (int r = 0; r < nroots; r++)
            {
                long layerStart = layout.FirstEccSector + r * spl + chunkStart;
                crcEccStream.Position = layerStart * Rs03Constants.SectorSize;
                await crcEccStream.WriteAsync(parityLayers[r].AsMemory(0, chunkBytes), ct).ConfigureAwait(false);
            }

            progress?.Report(Math.Clamp((double)(chunkStart + chunkCount) / spl, 0.0, 1.0));
        }
    }

    private static void ReadChunkLayers(
        FileStream imageFs,
        FileStream crcEccStream,
        Rs03Layout layout,
        Rs03EccHeader header,
        long chunkStart,
        int chunkCount,
        byte[][] layerBufs)
    {
        int payloadLayers = layout.PayloadDataLayers;
        for (int l = 0; l < payloadLayers; l++)
        {
            for (int c = 0; c < chunkCount; c++)
            {
                ReadVirtualSector(
                    imageFs,
                    crcEccStream,
                    layout,
                    header,
                    l,
                    chunkStart + c,
                    layerBufs[l].AsSpan(c * Rs03Constants.SectorSize, Rs03Constants.SectorSize));
            }
        }

        for (int l = payloadLayers; l < Rs03Constants.FieldMax; l++)
        {
            for (int c = 0; c < chunkCount; c++)
            {
                ReadVirtualSector(
                    imageFs,
                    crcEccStream,
                    layout,
                    header,
                    l,
                    chunkStart + c,
                    layerBufs[l].AsSpan(c * Rs03Constants.SectorSize, Rs03Constants.SectorSize));
            }
        }
    }

    private static void ReadPayloadLayerSector(
        FileStream imageFs,
        Rs03Layout layout,
        Rs03EccHeader header,
        int layer,
        long col,
        Span<byte> dest)
    {
        long sectorIndex = layer * layout.SectorsPerLayer + col;
        if (layout.Mode == Rs03Mode.EccFile && sectorIndex >= layout.DataSectors)
        {
            Rs03MarkerCodec.WritePaddingSector(dest, sectorIndex, header.MediumFingerprint, header.FingerprintSector);
            return;
        }

        dest.Clear();
        long byteOffset = sectorIndex * Rs03Constants.SectorSize;
        if (byteOffset < imageFs.Length)
        {
            imageFs.Position = byteOffset;
            int toRead = (int)Math.Min(Rs03Constants.SectorSize, imageFs.Length - byteOffset);
            ReadExactOrPartial(imageFs, dest.Slice(0, toRead));
        }
    }

    private static void ReadVirtualSector(
        FileStream imageFs,
        FileStream crcEccStream,
        Rs03Layout layout,
        Rs03EccHeader header,
        int layer,
        long col,
        Span<byte> dest)
    {
        if (layer < layout.PayloadDataLayers)
        {
            long sectorIndex = layer * layout.SectorsPerLayer + col;
            if (layout.Mode == Rs03Mode.EccFile && sectorIndex >= layout.DataSectors)
            {
                Rs03MarkerCodec.WritePaddingSector(dest, sectorIndex, header.MediumFingerprint, header.FingerprintSector);
                return;
            }

            dest.Clear();
            long byteOffset = sectorIndex * Rs03Constants.SectorSize;
            if (byteOffset < imageFs.Length)
            {
                imageFs.Position = byteOffset;
                int toRead = (int)Math.Min(Rs03Constants.SectorSize, imageFs.Length - byteOffset);
                if (layout.Mode == Rs03Mode.EccFile && sectorIndex == layout.DataSectors - 1 && toRead > layout.InLastBytes)
                {
                    toRead = layout.InLastBytes;
                }
                ReadExactOrPartial(imageFs, dest.Slice(0, toRead));
            }
            return;
        }

        long diskLba = layout.GetDiskSectorIndex(layer, col);
        dest.Clear();
        long offset = diskLba * Rs03Constants.SectorSize;
        if (offset < crcEccStream.Length)
        {
            crcEccStream.Position = offset;
            int toRead = (int)Math.Min(Rs03Constants.SectorSize, crcEccStream.Length - offset);
            ReadExactOrPartial(crcEccStream, dest.Slice(0, toRead));
        }
    }

    private static bool IsMissingSectorOnDisk(Rs03Mode mode, long imageLength, long lba, ReadOnlySpan<byte> sec)
    {
        if (Rs03MarkerCodec.IsDeadSector(sec))
        {
            return true;
        }

        return mode == Rs03Mode.AugmentedImage && (lba + 1) * Rs03Constants.SectorSize > imageLength;
    }

    private static bool TryResolveEccFileHeader(FileStream eccFs, out Rs03EccHeader? header, out bool reconstructed)
    {
        reconstructed = false;
        header = TryReadEccHeaderAtSector(eccFs, 0, out _);
        if (header != null && header.IsEccFile)
        {
            return true;
        }

        header = null;
        byte[] sec = new byte[Rs03Constants.SectorSize];
        eccFs.Position = Rs03Constants.EccHeaderBytes;
        while (eccFs.Position + Rs03Constants.SectorSize <= eccFs.Length)
        {
            if (ReadExactOrPartial(eccFs, sec) != Rs03Constants.SectorSize)
            {
                break;
            }
            if (Rs03CrcBlock.TryParse(sec, out var cb) && cb != null && cb.IsEccFile)
            {
                header = cb.ReconstructHeader();
                reconstructed = true;
                return true;
            }
        }
        return false;
    }

    private static bool TryFindAugmentedHeader(FileStream imageFs, bool exhaustiveScan, out Rs03EccHeader? header, out bool reconstructed)
    {
        reconstructed = false;
        header = null;

        // 1. Check right after ISO/UDF filesystem boundary when descriptors are intact.
        imageFs.Position = 0;
        long imageSectors = imageFs.Length / Rs03Constants.SectorSize;
        int? fsEnd = RecordedVolumeExtent.FindFileSystemEndSector(lba =>
        {
            if (lba < 0 || (long)(lba + 1) * Rs03Constants.SectorSize > imageFs.Length)
            {
                return null;
            }
            byte[] buf = new byte[Rs03Constants.SectorSize];
            imageFs.Position = (long)lba * Rs03Constants.SectorSize;
            return ReadExactOrPartial(imageFs, buf) == Rs03Constants.SectorSize ? buf : null;
        });

        if (fsEnd is > 0)
        {
            if (fsEnd.Value >= imageSectors)
            {
                return false;
            }

            var direct = TryReadEccHeaderAtSector(imageFs, fsEnd.Value, out _);
            if (direct != null && !direct.IsEccFile && direct.DataSectors == fsEnd.Value)
            {
                header = direct;
                return true;
            }
        }

        // 2. Probe candidate layer sizes from image size and standard capacities (handles wiped LBA 0-64 / EccHeader).
        var candidateSpls = new List<long>();
        long derived = imageSectors / Rs03Constants.FieldMax;
        for (long delta = -1; delta <= 1; delta++)
        {
            if (derived + delta > 0)
            {
                candidateSpls.Add(derived + delta);
            }
        }
        foreach (long cap in Rs03Constants.StandardMediumCapacities)
        {
            long spl = cap / Rs03Constants.FieldMax;
            if (spl > 0 && !candidateSpls.Contains(spl))
            {
                candidateSpls.Add(spl);
            }
        }

        byte[] secBuf = new byte[Rs03Constants.SectorSize];
        foreach (long spl in candidateSpls)
        {
            if (spl * Rs03Constants.FieldMax > imageSectors + 255)
            {
                continue;
            }
            for (int nroots = Rs03Constants.MinRoots; nroots <= Rs03Constants.MaxRoots; nroots++)
            {
                long firstCrc = (Rs03Constants.FieldMax - nroots - 1L) * spl;
                long probeLimit = Math.Min(spl, 16);
                for (long offset = 0; offset < probeLimit; offset++)
                {
                    long probeLba = firstCrc + offset;
                    if ((probeLba + 1) * Rs03Constants.SectorSize > imageFs.Length)
                    {
                        break;
                    }
                    imageFs.Position = probeLba * Rs03Constants.SectorSize;
                    if (ReadExactOrPartial(imageFs, secBuf) == Rs03Constants.SectorSize &&
                        Rs03CrcBlock.TryParse(secBuf, out var cb) &&
                        cb != null &&
                        !cb.IsEccFile &&
                        cb.SectorsPerLayer == spl &&
                        cb.EccBytes == nroots)
                    {
                        var directEh = TryReadEccHeaderAtSector(imageFs, cb.DataSectors, out _);
                        if (directEh != null && !directEh.IsEccFile)
                        {
                            header = directEh;
                            reconstructed = false;
                        }
                        else
                        {
                            header = cb.ReconstructHeader();
                            reconstructed = true;
                        }
                        return true;
                    }
                }
            }
        }

        if (!exhaustiveScan)
        {
            return false;
        }

        // 3. Exhaustive scan of every sector for a valid non-file CrcBlock positioned inside its declared CRC layer.
        imageFs.Position = 0;
        for (long lba = 0; lba < imageSectors; lba++)
        {
            imageFs.Position = lba * Rs03Constants.SectorSize;
            if (ReadExactOrPartial(imageFs, secBuf) != Rs03Constants.SectorSize)
            {
                break;
            }
            if (Rs03CrcBlock.TryParse(secBuf, out var cb) && cb != null && !cb.IsEccFile)
            {
                long firstCrc = (Rs03Constants.FieldMax - cb.EccBytes - 1L) * cb.SectorsPerLayer;
                if (lba >= firstCrc && lba < firstCrc + cb.SectorsPerLayer)
                {
                    var directEh = TryReadEccHeaderAtSector(imageFs, cb.DataSectors, out _);
                    if (directEh != null && !directEh.IsEccFile)
                    {
                        header = directEh;
                        reconstructed = false;
                    }
                    else
                    {
                        header = cb.ReconstructHeader();
                        reconstructed = true;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private static Rs03EccHeader? TryReadEccHeaderAtSector(FileStream fs, long startSector, out bool presentButInvalid)
    {
        presentButInvalid = false;
        long byteOffset = startSector * Rs03Constants.SectorSize;
        if (byteOffset < 0 || byteOffset + Rs03Constants.EccHeaderBytes > fs.Length)
        {
            return null;
        }

        byte[] buf = new byte[Rs03Constants.EccHeaderBytes];
        fs.Position = byteOffset;
        if (ReadExactOrPartial(fs, buf) != Rs03Constants.EccHeaderBytes)
        {
            return null;
        }

        if (Rs03EccHeader.TryParse(buf, out var header))
        {
            return header;
        }

        presentButInvalid = true;
        return null;
    }

    private static Rs03FingerprintStatus CheckFingerprint(FileStream imageFs, Rs03EccHeader header)
    {
        if (header.FingerprintSector < 0 || header.FingerprintSector >= header.DataSectors)
        {
            return Rs03FingerprintStatus.NotChecked;
        }
        if (IsAllZero(header.MediumFingerprint))
        {
            return Rs03FingerprintStatus.NotChecked;
        }

        long fpOffset = (long)header.FingerprintSector * Rs03Constants.SectorSize;
        if (fpOffset + Rs03Constants.SectorSize > imageFs.Length)
        {
            return Rs03FingerprintStatus.MissingOrZeroed;
        }

        byte[] sector = new byte[Rs03Constants.SectorSize];
        imageFs.Position = fpOffset;
        if (ReadExactOrPartial(imageFs, sector) != Rs03Constants.SectorSize ||
            Rs03MarkerCodec.IsDeadSector(sector) ||
            IsAllZero(sector))
        {
            return Rs03FingerprintStatus.MissingOrZeroed;
        }

        byte[] actualFp = MD5.HashData(sector);
        return actualFp.AsSpan().SequenceEqual(header.MediumFingerprint)
            ? Rs03FingerprintStatus.Match
            : Rs03FingerprintStatus.Mismatch;
    }

    private static async Task<(byte[] MediumFp, byte[] DataMd5)> ComputeFingerprintAndMd5Async(
        string imagePath,
        long fileLength,
        long dataSectors,
        CancellationToken ct)
    {
        byte[] fp = new byte[16];
        await using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (dataSectors > Rs03Constants.FingerprintSector)
        {
            byte[] fpBuf = new byte[Rs03Constants.SectorSize];
            fs.Position = (long)Rs03Constants.FingerprintSector * Rs03Constants.SectorSize;
            int read = await ReadAsyncFull(fs, fpBuf, ct).ConfigureAwait(false);
            if (read == Rs03Constants.SectorSize)
            {
                fp = MD5.HashData(fpBuf);
            }
        }

        byte[] md5 = await ComputeStreamMd5Async(fs, fileLength, ct).ConfigureAwait(false);
        return (fp, md5);
    }

    private static async Task<byte[]> ComputeStreamMd5Async(FileStream fs, long byteCount, CancellationToken ct)
    {
        fs.Position = 0;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long remaining = byteCount;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remaining);
                int read = await fs.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return md5.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<int> ReadAsyncFull(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static int ReadExactOrPartial(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static byte[][] AllocateLayerBuffers(int layerCount, int sectorsPerChunk)
    {
        int size = sectorsPerChunk * Rs03Constants.SectorSize;
        var bufs = new byte[layerCount][];
        for (int i = 0; i < layerCount; i++)
        {
            bufs[i] = new byte[size];
        }
        return bufs;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b != 0) return false;
        }
        return true;
    }
}
