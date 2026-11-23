using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Metadata inspected from a legacy or third-party optical disc image container
/// (.nrg Nero, .mdf/.mds Alcohol 120%, .ccd/.img CloneCD, .cdi DiscJuggler).
/// </summary>
public sealed record LegacyDiscImageInfo(
    string SourceFilePath,
    string BinaryPayloadPath,
    string FormatName,
    long PayloadOffsetBytes,
    long PayloadLengthBytes,
    int SectorSizeBytes,
    int SectorCount,
    bool IsRaw2352,
    bool IsAudioDisc,
    int TrackCount
);

/// <summary>
/// Inspects and converts legacy proprietary optical disc image formats
/// (Nero .nrg, Alcohol 120% .mds/.mdf, CloneCD .ccd/.img) into canonical ISO 9660 (.iso)
/// or CDRWIN (.bin + .cue) streams for burning and verification.
/// </summary>
public static class LegacyDiscImageConverter
{
    private const int SectorSize2336 = 2336;
    private const int SectorSize2448 = 2448;
    private const int StandardPregapSectors = 150;
    private static readonly byte[] IsoCd001Magic = "CD001"u8.ToArray();
    private static readonly byte[] MdsSignature = "MEDIA DESCRIPTOR"u8.ToArray();

    /// <summary>
    /// Returns true if the file path has a supported legacy or raw binary disc image extension (.bin, .nrg, .mdf, .mds, .ccd, .img, .cdi).
    /// </summary>
    public static bool IsLegacyImageFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".bin" or ".nrg" or ".mdf" or ".mds" or ".ccd" or ".img" or ".cdi";
    }

    /// <summary>
    /// Inspects a legacy disc image (.nrg, .mdf, .mds, .ccd, .img) and returns its geometry and sector layout.
    /// </summary>
    public static LegacyDiscImageInfo Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{filePath}'.", filePath);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".iso" => InspectIso(filePath),
            ".bin" => InspectRawBin(filePath),
            ".nrg" => InspectNrg(filePath),
            ".cdi" => InspectDiscJuggler(filePath),
            ".ccd" => InspectCloneCdCcd(filePath),
            ".img" => InspectCloneCdImg(filePath),
            ".mds" => InspectAlcoholMds(filePath),
            ".mdf" => InspectAlcoholMdf(filePath),
            _ => throw new NotSupportedException($"Unsupported legacy image extension '{ext}'.")
        };
    }

    /// <summary>
    /// Converts any supported legacy disc image (.nrg, .mdf/.mds, .ccd/.img, or raw 2352-byte Mode 1 .bin)
    /// into a standard 2,048-byte Mode 1 ISO 9660 file (.iso).
    /// </summary>
    public static async Task<int> ConvertToIsoAsync(
        string sourceImagePath,
        string outputIsoPath,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        var info = Inspect(sourceImagePath);
        if (info.IsAudioDisc)
        {
            throw new InvalidOperationException(
                $"Cannot convert Red Book Audio image '{Path.GetFileName(sourceImagePath)}' to a 2048-byte data ISO. Use BIN/CUE or burn directly.");
        }

        string? outDir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        await using var outStream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        return await ExtractMode1IsoStreamAsync(info, outStream, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a disc image (.iso, .nrg, .mdf/.mds, .ccd/.img) into a companion CDRWIN (.bin + .cue) pair.
    /// Streams 2,048-byte user data sectors out to <paramref name="outputBinPath"/> (or preserves raw 2,352-byte
    /// sectors when the source image is already raw) and writes the companion .cue sheet.
    /// </summary>
    public static async Task<int> ConvertImageToBinCueAsync(
        string sourcePath,
        string outputBinPath,
        string? outputCuePath = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBinPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source disc image file not found: '{sourcePath}'.", sourcePath);
        }

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".iso" or ".nrg" or ".cdi" or ".mdf" or ".mds" or ".ccd" or ".img" or ".bin"))
        {
            throw new NotSupportedException($"Unsupported disc image extension '{ext}' for BIN/CUE conversion.");
        }

        var info = Inspect(sourcePath);
        if (info.IsAudioDisc)
        {
            throw new InvalidOperationException(
                $"Cannot convert Red Book Audio image '{Path.GetFileName(sourcePath)}' to a data BIN/CUE image.");
        }

        if (info.TrackCount > 1)
        {
            // The output cue describes one data track. Writing a multi-track (mixed-mode) image under it would label
            // audio sectors as data, so refuse rather than produce a broken image.
            throw new NotSupportedException(
                $"'{Path.GetFileName(sourcePath)}' has {info.TrackCount} tracks. Only single-track data images can be converted to BIN/CUE.");
        }

        outputCuePath ??= Path.ChangeExtension(outputBinPath, ".cue");
        if (string.Equals(outputBinPath, outputCuePath, StringComparison.OrdinalIgnoreCase))
        {
            outputBinPath = Path.ChangeExtension(outputBinPath, ".bin");
        }

        string fullSource = Path.GetFullPath(sourcePath);
        string fullPayload = Path.GetFullPath(info.BinaryPayloadPath);
        foreach (string output in new[] { outputBinPath, outputCuePath })
        {
            string fullOutput = Path.GetFullPath(output);
            if (string.Equals(fullOutput, fullSource, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullOutput, fullPayload, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The output file '{output}' would overwrite the source image. Choose another name.");
            }
        }

        string? binDir = Path.GetDirectoryName(outputBinPath);
        if (!string.IsNullOrEmpty(binDir) && !Directory.Exists(binDir))
        {
            Directory.CreateDirectory(binDir);
        }

        string? cueDir = Path.GetDirectoryName(outputCuePath);
        if (!string.IsNullOrEmpty(cueDir) && !Directory.Exists(cueDir))
        {
            Directory.CreateDirectory(cueDir);
        }

        string trackMode = "MODE1/2048";
        int srcSectorSize = info.SectorSizeBytes;
        int outSectorSize = OpticalConstants.DataSectorSize;

        if (info.SectorSizeBytes == SectorSize2336)
        {
            trackMode = "MODE2/2336";
            outSectorSize = SectorSize2336;
        }
        else if (info.IsRaw2352 || info.SectorSizeBytes is OpticalConstants.RawSectorSize or SectorSize2448)
        {
            outSectorSize = OpticalConstants.RawSectorSize;
            await using (var probeFs = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                probeFs.Position = info.PayloadOffsetBytes;
                byte[] probeBuf = new byte[32];
                int r = await probeFs.ReadAsync(probeBuf.AsMemory(0, 32), cancellationToken).ConfigureAwait(false);
                bool isMode2 = r > 15 && probeBuf[15] == 2;
                trackMode = isMode2 ? "MODE2/2352" : "MODE1/2352";
            }
        }
        else
        {
            outSectorSize = OpticalConstants.DataSectorSize;
            trackMode = "MODE1/2048";
        }

        string binFileName = Path.GetFileName(outputBinPath);
        string cueContent = $"""
            FILE "{binFileName}" BINARY
              TRACK 01 {trackMode}
                INDEX 01 00:00:00

            """;

        // No byte-order mark: CDRWIN-era tools and many burners reject a .cue that starts with one.
        await File.WriteAllTextAsync(outputCuePath, cueContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

        await using var src = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        src.Position = Math.Clamp(info.PayloadOffsetBytes, 0, src.Length);

        await using var dest = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        int totalSectors = info.SectorCount;
        byte[] sectorBuf = new byte[Math.Max(srcSectorSize, outSectorSize)];
        int writtenSectors = 0;

        while (writtenSectors < totalSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readTotal = 0;
            while (readTotal < srcSectorSize)
            {
                int r = await src.ReadAsync(sectorBuf.AsMemory(readTotal, srcSectorSize - readTotal), cancellationToken).ConfigureAwait(false);
                if (r == 0) break;
                readTotal += r;
            }

            if (readTotal == 0) break;
            if (readTotal < srcSectorSize)
            {
                Array.Clear(sectorBuf, readTotal, srcSectorSize - readTotal);
            }

            await dest.WriteAsync(sectorBuf.AsMemory(0, outSectorSize), cancellationToken).ConfigureAwait(false);
            writtenSectors++;

            if (progress != null && (writtenSectors % 64 == 0 || writtenSectors == totalSectors))
            {
                progress.Report(Math.Min(1.0, (double)writtenSectors / totalSectors));
            }
        }

        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return writtenSectors;
    }

    /// <summary>
    /// Converts a CDRWIN (.bin + .cue) image pair into a standard 2,048-byte Mode 1 / Mode 2 Form 1 ISO 9660 file (.iso).
    /// Extracts the first data track from the CUE sheet, streaming 2,048-byte user data sectors and stripping headers/trailers.
    /// </summary>
    private static readonly byte[] RawCdSyncPattern =
    [
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    ];

    private static LegacyDiscImageInfo InspectCueDataTrack(string cuePath)
    {
        if (!File.Exists(cuePath))
        {
            throw new FileNotFoundException($"CUE sheet file not found: '{cuePath}'.", cuePath);
        }

        var cueSheet = CueSheetParser.ParseFile(cuePath);

        CueFileEntry? dataFile = null;
        CueTrack? dataTrack = null;
        foreach (var file in cueSheet.Files)
        {
            foreach (var track in file.Tracks)
            {
                if (!track.IsAudio)
                {
                    dataFile = file;
                    dataTrack = track;
                    break;
                }
            }
            if (dataTrack != null) break;
        }

        if (dataTrack == null || dataFile == null)
        {
            throw new InvalidDataException($"CUE sheet '{Path.GetFileName(cuePath)}' contains only audio tracks and has no data track to convert to ISO.");
        }

        string binPath = dataFile.FileName;
        if (!Path.IsPathRooted(binPath))
        {
            string cueDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
            binPath = Path.Combine(cueDir, binPath);
        }

        if (!File.Exists(binPath))
        {
            throw new FileNotFoundException($"Binary payload file referenced in CUE sheet not found: '{binPath}'.", binPath);
        }

        long fileLen = new FileInfo(binPath).Length;
        if (fileLen == 0)
        {
            throw new InvalidDataException($"Binary payload file referenced in CUE sheet is empty (0 bytes): '{binPath}'.");
        }

        int sectorSize = dataTrack.DataType switch
        {
            CueTrackDataType.Mode1_2048 => OpticalConstants.DataSectorSize,
            CueTrackDataType.Mode2_2336 => SectorSize2336,
            CueTrackDataType.Mode1_2352 or CueTrackDataType.Mode2_2352 => OpticalConstants.RawSectorSize,
            _ => OpticalConstants.DataSectorSize
        };

        int startFrame = 0;
        var idx01 = dataTrack.Indices.FirstOrDefault(i => i.IndexNumber == 1);
        if (idx01 != null)
        {
            startFrame = idx01.Position.ToFrameCount();
        }

        if (sectorSize == OpticalConstants.RawSectorSize && fileLen % SectorSize2448 == 0)
        {
            using var probeFs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long offset2448 = (long)startFrame * SectorSize2448;
            long offset2352 = (long)startFrame * OpticalConstants.RawSectorSize;
            bool sync2448 = HasRawCdSyncPatternAtStride(probeFs, SectorSize2448, offset2448);
            bool sync2352 = HasRawCdSyncPatternAtStride(probeFs, OpticalConstants.RawSectorSize, offset2352);

            if (sync2448 && !sync2352)
            {
                sectorSize = SectorSize2448;
            }
            else if (!sync2352 &&
                     offset2448 < fileLen &&
                     TryProbeIsoGeometry(probeFs, offset2448, fileLen - offset2448, out int probedSize, out _) &&
                     probedSize == SectorSize2448)
            {
                sectorSize = SectorSize2448;
            }
            else if (fileLen % OpticalConstants.RawSectorSize != 0)
            {
                sectorSize = SectorSize2448;
            }
        }

        int trackIndex = -1;
        for (int i = 0; i < dataFile.Tracks.Count; i++)
        {
            if (ReferenceEquals(dataFile.Tracks[i], dataTrack) || dataFile.Tracks[i].TrackNumber == dataTrack.TrackNumber)
            {
                trackIndex = i;
                break;
            }
        }

        int sectorCount;
        if (trackIndex >= 0 && trackIndex + 1 < dataFile.Tracks.Count)
        {
            var nextTrack = dataFile.Tracks[trackIndex + 1];
            var nextIdx = nextTrack.Indices.OrderBy(idx => idx.IndexNumber).FirstOrDefault();
            if (nextIdx != null && nextIdx.Position.ToFrameCount() > startFrame)
            {
                sectorCount = nextIdx.Position.ToFrameCount() - startFrame;
            }
            else
            {
                int totalFrames = (int)(fileLen / sectorSize);
                sectorCount = Math.Max(0, totalFrames - startFrame);
            }
        }
        else
        {
            int totalFrames = (int)(fileLen / sectorSize);
            sectorCount = Math.Max(0, totalFrames - startFrame);
        }

        if (sectorCount <= 0)
        {
            throw new InvalidDataException($"CUE data track in '{Path.GetFileName(binPath)}' has no complete {sectorSize}-byte sectors.");
        }

        long payloadOffset = (long)startFrame * sectorSize;
        long payloadLength = (long)sectorCount * sectorSize;
        return new LegacyDiscImageInfo(
            SourceFilePath: cuePath,
            BinaryPayloadPath: binPath,
            FormatName: $"CDRWIN CUE Data Track ({dataTrack.DataType})",
            PayloadOffsetBytes: payloadOffset,
            PayloadLengthBytes: payloadLength,
            SectorSizeBytes: sectorSize,
            SectorCount: sectorCount,
            IsRaw2352: sectorSize != OpticalConstants.DataSectorSize,
            IsAudioDisc: false,
            TrackCount: dataFile.Tracks.Count);
    }

    public static async Task<int> ConvertBinCueToIsoAsync(
        string cuePath,
        string outputIsoPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        var info = InspectCueDataTrack(cuePath);

        string? outDir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        await using var src = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        src.Position = info.PayloadOffsetBytes;

        await using var dest = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        int sectorSize = info.SectorSizeBytes;
        int sectorCount = info.SectorCount;
        byte[] sectorBuf = new byte[sectorSize];
        int writtenSectors = 0;

        while (writtenSectors < sectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readTotal = 0;
            while (readTotal < sectorSize)
            {
                int r = await src.ReadAsync(sectorBuf.AsMemory(readTotal, sectorSize - readTotal), cancellationToken).ConfigureAwait(false);
                if (r == 0) break;
                readTotal += r;
            }

            if (readTotal == 0) break;
            if (readTotal < sectorSize)
            {
                Array.Clear(sectorBuf, readTotal, sectorSize - readTotal);
            }

            if (IsMode2Form2Sector(sectorBuf, sectorSize))
            {
                throw new InvalidDataException($"'{Path.GetFileName(cuePath)}' contains Mode 2 Form 2 sectors (2,324 bytes/sector), which cannot be converted to a 2,048-byte ISO.");
            }

            int userOffset = GetUserDataOffset(sectorBuf, sectorSize);
            await dest.WriteAsync(sectorBuf.AsMemory(userOffset, OpticalConstants.DataSectorSize), cancellationToken).ConfigureAwait(false);
            writtenSectors++;

            if (progress != null && (writtenSectors % 64 == 0 || writtenSectors == sectorCount))
            {
                progress.Report(Math.Min(1.0, (double)writtenSectors / sectorCount));
            }
        }

        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return writtenSectors;
    }

    /// <summary>
    /// Opens a read-only, seekable 2,048-byte user-data view of the first data track in a CUE sheet,
    /// stripping raw 2,352 / 2,336 / 2,448-byte headers and ECC trailers and refusing Mode 2 Form 2 or audio tracks.
    /// </summary>
    public static Stream OpenCueIsoUserDataStream(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);
        return OpenIsoUserDataStream(InspectCueDataTrack(cuePath));
    }

    /// <summary>
    /// Opens a read-only, seekable view of the image's 2,048-byte user data (exactly what an .iso of the disc would contain),
    /// transforming raw sectors on the fly. Nothing is buffered beyond one sector, so 4.7 GB / 8.5 GB DVD images burn and
    /// verify without hitting the 2 GB <see cref="MemoryStream"/> limit or needing an intermediate temp file.
    /// </summary>
    public static Stream OpenIsoUserDataStream(LegacyDiscImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsAudioDisc)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(info.SourceFilePath)}' is a Red Book Audio image; it has no 2048-byte data sectors to burn as an ISO.");
        }

        EnsureNoMode2Form2Sectors(info);
        return new LegacyIsoUserDataStream(info);
    }

    /// <summary>
    /// Inspects <paramref name="filePath"/> and opens its 2,048-byte user-data view.
    /// </summary>
    public static Stream OpenIsoUserDataStream(string filePath) => OpenIsoUserDataStream(Inspect(filePath));

    internal static bool IsMode2Form2Sector(ReadOnlySpan<byte> sector, int sectorSize)
    {
        if (sectorSize == SectorSize2336)
        {
            return sector.Length >= 8 && (sector[2] & 0x20) != 0 && sector[2] == sector[6];
        }
        if (sectorSize is OpticalConstants.RawSectorSize or SectorSize2448)
        {
            return sector.Length >= 24 && sector[15] == 2 && (sector[18] & 0x20) != 0 && sector[18] == sector[22];
        }
        return false;
    }

    private static void EnsureNoMode2Form2Sectors(LegacyDiscImageInfo info)
    {
        if (info.SectorSizeBytes is not (SectorSize2336 or OpticalConstants.RawSectorSize or SectorSize2448) || info.SectorCount <= 0)
        {
            return;
        }

        int size = info.SectorSizeBytes;
        const int batchSectors = 64;
        byte[] buffer = new byte[size * batchSectors];
        using var fs = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        fs.Position = Math.Clamp(info.PayloadOffsetBytes, 0, fs.Length);

        int remaining = info.SectorCount;
        while (remaining > 0)
        {
            int toReadSectors = Math.Min(batchSectors, remaining);
            int targetBytes = toReadSectors * size;
            int got = 0;
            while (got < targetBytes)
            {
                int r = fs.Read(buffer, got, targetBytes - got);
                if (r <= 0) break;
                got += r;
            }

            int completeSectors = got / size;
            if (completeSectors == 0) break;

            for (int i = 0; i < completeSectors; i++)
            {
                if (IsMode2Form2Sector(buffer.AsSpan(i * size, size), size))
                {
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(info.SourceFilePath)}' contains Mode 2 Form 2 sectors (2,324 bytes/sector), which cannot be burned as a 2,048-byte ISO stream.");
                }
            }

            remaining -= completeSectors;
            if (got < targetBytes) break;
        }
    }

    /// <summary>
    /// Returns where the 2,048-byte user payload starts inside one stored sector:
    /// 2048 = 0; 2336 (Mode 2 formless, header stripped) = 8 (after the subheader);
    /// 2352 / 2448 (raw, optionally + 96-byte subchannel) = 16 for Mode 1, 24 for Mode 2 / CD-ROM XA Form 1
    /// (PlayStation, VCD, Photo CD), where an 8-byte subheader follows the 4-byte header.
    /// </summary>
    internal static int GetUserDataOffset(ReadOnlySpan<byte> sector, int sectorSize)
    {
        if (sectorSize == OpticalConstants.DataSectorSize) return 0;
        if (sectorSize == SectorSize2336)
        {
            if (IsMode2Form2Sector(sector, sectorSize))
            {
                throw new InvalidDataException("Mode 2 Form 2 sectors (2,324 bytes/sector) cannot be converted to a 2,048-byte ISO.");
            }
            return 8;
        }

        if (sector.Length < 24 || !sector.Slice(0, 12).SequenceEqual(RawCdSyncPattern))
        {
            throw new InvalidDataException($"Raw {sectorSize}-byte CD sector is missing the 12-byte sync pattern.");
        }

        if (IsMode2Form2Sector(sector, sectorSize))
        {
            throw new InvalidDataException("Mode 2 Form 2 sectors (2,324 bytes/sector) cannot be converted to a 2,048-byte ISO.");
        }

        return sector[15] switch
        {
            1 => 16,
            2 => 24,
            _ => throw new InvalidDataException($"Unsupported raw CD sector mode 0x{sector[15]:X2} (expected Mode 1 or Mode 2).")
        };
    }

    /// <summary>
    /// Extracts 2,048-byte user data sectors from a legacy disc image into a writable destination stream.
    /// Raw sectors have their sync/header (plus the Mode 2 subheader) and EDC/ECC trailer stripped.
    /// </summary>
    public static async Task<int> ExtractMode1IsoStreamAsync(
        LegacyDiscImageInfo info,
        Stream destinationIsoStream,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(destinationIsoStream);

        await using var src = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        src.Position = Math.Clamp(info.PayloadOffsetBytes, 0, src.Length);

        int totalSectors = info.SectorCount;
        int srcSectorSize = info.SectorSizeBytes;
        byte[] sectorBuf = new byte[srcSectorSize];
        int writtenSectors = 0;

        while (writtenSectors < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            int readTotal = 0;
            while (readTotal < srcSectorSize)
            {
                int r = await src.ReadAsync(sectorBuf.AsMemory(readTotal, srcSectorSize - readTotal), ct).ConfigureAwait(false);
                if (r == 0) break;
                readTotal += r;
            }

            if (readTotal == 0)
            {
                break;
            }

            if (readTotal < srcSectorSize)
            {
                Array.Clear(sectorBuf, readTotal, srcSectorSize - readTotal);
            }

            if (srcSectorSize == OpticalConstants.DataSectorSize)
            {
                int writeLen = Math.Min(OpticalConstants.DataSectorSize, readTotal);
                await destinationIsoStream.WriteAsync(sectorBuf.AsMemory(0, writeLen), ct).ConfigureAwait(false);
            }
            else
            {
                if (IsMode2Form2Sector(sectorBuf, srcSectorSize))
                {
                    throw new InvalidDataException(
                        $"'{Path.GetFileName(info.SourceFilePath)}' contains Mode 2 Form 2 sectors (2,324 bytes/sector), which cannot be converted to a 2,048-byte ISO.");
                }
                // Raw sector: strip sync/header (+ Mode 2 subheader) and EDC/ECC, keeping the 2048-byte user payload.
                int userOffset = GetUserDataOffset(sectorBuf, srcSectorSize);
                await destinationIsoStream.WriteAsync(sectorBuf.AsMemory(userOffset, OpticalConstants.DataSectorSize), ct).ConfigureAwait(false);
            }

            writtenSectors++;

            if (progress != null && (writtenSectors % 64 == 0 || writtenSectors == totalSectors))
            {
                progress.Report(new BurnProgressSnapshot
                {
                    CurrentLba = writtenSectors,
                    TotalLba = totalSectors,
                    BufferLevelPercent = 100.0,
                    CurrentActionDescription = $"Converting {info.FormatName} to ISO"
                });
            }
        }

        await destinationIsoStream.FlushAsync(ct).ConfigureAwait(false);
        return writtenSectors;
    }

    #region Synthetic Builders for Deterministic Testing

    /// <summary>
    /// Creates a valid Nero 5.5+ (.nrg) image file wrapping a 2,048-byte ISO payload and appending a canonical NER5 trailer
    /// (ETN2 + END! chunks + "NER5" + 64-bit big-endian trailer offset).
    /// </summary>
    public static void CreateNrgFile(string nrgFilePath, ReadOnlySpan<byte> isoPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nrgFilePath);
        string? dir = Path.GetDirectoryName(nrgFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(nrgFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(isoPayloadBytes);

        ulong trailerOffset = (ulong)fs.Position;

        // Write ETN2 chunk (32 bytes body):
        // [0..7] uint64 BE offset = 0
        // [8..15] uint64 BE size = isoPayloadBytes.Length
        // [16..19] uint32 BE mode = 0 (2048-byte Mode 1)
        // [20..23] uint32 BE startLba = 0
        // [24..31] uint64 BE lengthSectors
        Span<byte> etn2 = stackalloc byte[8 + 32];
        "ETN2"u8.CopyTo(etn2.Slice(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(4, 4), 32);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(8, 8), 0UL);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(16, 8), (ulong)isoPayloadBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(24, 4), 0U); // Mode 0 = 2048-byte sectors
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(28, 4), 0U);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(32, 8), (ulong)(isoPayloadBytes.Length / OpticalConstants.DataSectorSize));
        fs.Write(etn2);

        // Write END! chunk + NER5 12-byte footer
        Span<byte> footer = stackalloc byte[8 + 12];
        "END!"u8.CopyTo(footer.Slice(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(4, 4), 0U);
        "NER5"u8.CopyTo(footer.Slice(8, 4));
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), trailerOffset);
        fs.Write(footer);
    }

    /// <summary>
    /// Creates a CloneCD (.ccd + .img) image pair from 2,048-byte Mode 1 sector data (wrapping each sector into a 2,352-byte Mode 1 raw sector).
    /// </summary>
    public static void CreateCloneCdImage(string ccdFilePath, ReadOnlySpan<byte> mode1IsoBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ccdFilePath);
        string imgFilePath = Path.ChangeExtension(ccdFilePath, ".img");
        int sectorCount = OpticalConstants.BytesToSectors(mode1IsoBytes.Length, OpticalConstants.DataSectorSize);

        using (var imgStream = new FileStream(imgFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> rawSector = stackalloc byte[OpticalConstants.RawSectorSize];
            for (int lba = 0; lba < sectorCount; lba++)
            {
                rawSector.Clear();
                // 12-byte CD-ROM sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
                rawSector[0] = 0x00;
                rawSector.Slice(1, 10).Fill(0xFF);
                rawSector[11] = 0x00;
                // Mode 1 byte at offset 15
                rawSector[15] = 0x01;

                int srcOffset = lba * OpticalConstants.DataSectorSize;
                int copyLen = Math.Min(OpticalConstants.DataSectorSize, mode1IsoBytes.Length - srcOffset);
                if (copyLen > 0)
                {
                    mode1IsoBytes.Slice(srcOffset, copyLen).CopyTo(rawSector.Slice(16, copyLen));
                }

                imgStream.Write(rawSector);
            }
        }

        string ccdText = $"""
            [CloneCD]
            Version=3
            [Disc]
            TocEntries=1
            Sessions=1
            DataTracksScrambled=0
            [Track 1]
            MODE=1
            INDEX 1=0
            """;
        File.WriteAllText(ccdFilePath, ccdText, Encoding.ASCII);
    }

    /// <summary>
    /// Creates an Alcohol 120% (.mds + .mdf) image pair from a 2,048-byte ISO payload.
    /// </summary>
    public static void CreateAlcoholMdsMdfImage(string mdsFilePath, ReadOnlySpan<byte> isoPayloadBytes) =>
        CreateAlcoholMdsMdfImage(mdsFilePath, isoPayloadBytes, OpticalConstants.DataSectorSize, audio: false);

    /// <summary>
    /// Creates an Alcohol 120% (.mds + .mdf) image pair with explicit sector geometry and track mode.
    /// </summary>
    public static void CreateAlcoholMdsMdfImage(string mdsFilePath, ReadOnlySpan<byte> payloadBytes, int sectorSize, bool audio = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mdsFilePath);
        if (sectorSize <= 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));

        string? dir = Path.GetDirectoryName(mdsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string mdfFilePath = Path.ChangeExtension(mdsFilePath, ".mdf");
        File.WriteAllBytes(mdfFilePath, payloadBytes.ToArray());

        uint sectors = (uint)(payloadBytes.Length / sectorSize);
        byte[] mdfNameBytes = Encoding.ASCII.GetBytes(Path.GetFileName(mdfFilePath) + "\0");

        const int headerSize = 88;
        const int sessionSize = 24;
        const int trackSize = 80;
        const int extraSize = 8;
        const int footerSize = 16;
        const int sessionOffset = headerSize;
        const int trackOffset = sessionOffset + sessionSize;
        const int extraOffset = trackOffset + trackSize;
        const int footerOffset = extraOffset + extraSize;
        const int filenameOffset = footerOffset + footerSize;

        byte[] mdsBytes = new byte[filenameOffset + mdfNameBytes.Length];
        Span<byte> span = mdsBytes;

        // 88-byte Alcohol 120% v1.2 MEDIA DESCRIPTOR header
        MdsSignature.CopyTo(span.Slice(0, 16));
        span[16] = 1; // Version 1.2
        span[17] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0); // Medium = CD-ROM
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), 1); // 1 session
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(80, 4), sessionOffset);

        // 24-byte SessionBlock
        Span<byte> sess = span.Slice(sessionOffset, sessionSize);
        BinaryPrimitives.WriteInt32LittleEndian(sess.Slice(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(sess.Slice(4, 4), (int)sectors);
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(8, 2), 1); // session number
        sess[10] = 1; // num_all_blocks
        sess[11] = 0; // num_nontrack_blocks
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(12, 2), 1); // first_track
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(14, 2), 1); // last_track
        BinaryPrimitives.WriteUInt32LittleEndian(sess.Slice(20, 4), trackOffset);

        // 80-byte TrackBlock
        Span<byte> trk = span.Slice(trackOffset, trackSize);
        trk[0] = audio ? (byte)0xA9 : (sectorSize == SectorSize2336 ? (byte)0xAB : (byte)0xAA);
        trk[1] = sectorSize == SectorSize2448 ? (byte)0x08 : (byte)0x00;
        trk[2] = audio ? (byte)0x01 : (byte)0x14; // ADR/CTL
        trk[3] = 0;
        trk[4] = 1; // point = track 1
        BinaryPrimitives.WriteUInt32LittleEndian(trk.Slice(12, 4), extraOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(trk.Slice(16, 2), (ushort)sectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(trk.Slice(36, 4), 0); // start_sector
        BinaryPrimitives.WriteUInt64LittleEndian(trk.Slice(40, 8), 0); // start_offset
        BinaryPrimitives.WriteUInt32LittleEndian(trk.Slice(48, 4), 1); // number_of_files
        BinaryPrimitives.WriteUInt32LittleEndian(trk.Slice(52, 4), footerOffset);

        // 8-byte TrackExtraBlock
        Span<byte> extra = span.Slice(extraOffset, extraSize);
        BinaryPrimitives.WriteUInt32LittleEndian(extra.Slice(0, 4), 0); // pregap
        BinaryPrimitives.WriteUInt32LittleEndian(extra.Slice(4, 4), sectors);

        // 16-byte Footer + ASCII filename
        Span<byte> footer = span.Slice(footerOffset, footerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(0, 4), filenameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(4, 4), 0); // ASCII
        mdfNameBytes.CopyTo(span.Slice(filenameOffset));

        File.WriteAllBytes(mdsFilePath, mdsBytes);
    }

    /// <summary>
    /// Creates a single-session, single-track DiscJuggler (.cdi) image for tests. Layout follows the open-source
    /// cdirip reader (GPL-2.0, https://github.com/jozip/cdirip): track data first, then a descriptor block, then the
    /// trailer (uint32 LE version, uint32 LE descriptor position). <paramref name="version"/> is 2, 3 or 35 (for 3.5).
    /// <paramref name="sectorData"/> holds whole stored sectors of <paramref name="sectorSize"/> bytes (2048, 2336 or 2352).
    /// </summary>
    public static void CreateDiscJugglerFile(string cdiFilePath, ReadOnlySpan<byte> sectorData, int sectorSize, int version = 3, bool audio = false, int pregapSectors = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cdiFilePath);
        uint versionCode = version switch { 2 => CdiVersion2, 3 => CdiVersion3, 35 => CdiVersion35, _ => throw new ArgumentOutOfRangeException(nameof(version)) };
        uint sizeCode = sectorSize switch { 2048 => 0u, SectorSize2336 => 1u, 2352 => 2u, _ => throw new ArgumentOutOfRangeException(nameof(sectorSize)) };
        uint mode = audio ? 0u : (sectorSize == 2352 && sectorData.Length > 15 && sectorData[15] == 2 ? 2u : 1u);
        uint userSectors = (uint)(sectorData.Length / sectorSize);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(sectorData.ToArray());
        // Pregap sectors are stored in the track's data area ahead of the user sectors.
        long headerPos = ms.Position;

        w.Write((ushort)1); // sessions
        w.Write((ushort)1); // tracks in session 1
        w.Write(0u);        // extra-data flag (0 = none)
        w.Write(CdiStartMark); w.Write(CdiStartMark);
        w.Write(new byte[4]);
        w.Write((byte)0);   // filename length
        w.Write(new byte[11 + 4 + 4]);
        w.Write(0u);        // 0x80000000 would add 8 bytes
        w.Write(new byte[2]);
        w.Write((uint)pregapSectors);
        w.Write(userSectors - (uint)pregapSectors);
        w.Write(new byte[6]);
        w.Write(mode);
        w.Write(new byte[12]);
        w.Write(0u);        // start LBA
        w.Write(userSectors);
        w.Write(new byte[16]);
        w.Write(sizeCode);
        w.Write(new byte[29]);
        if (version != 2)
        {
            w.Write(new byte[5]);
            w.Write(0u);    // 0xFFFFFFFF would add 78 bytes
        }

        w.Write(new byte[12 + (version == 2 ? 0 : 1)]); // session trailer
        long headerLen = ms.Position - headerPos;
        w.Write(versionCode);
        // v2 stores the descriptor position from the start of the file; v3/v3.5 store the distance from the end.
        w.Write(version == 2 ? (uint)headerPos : (uint)(headerLen + 8));
        string? dir = Path.GetDirectoryName(cdiFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(cdiFilePath, ms.ToArray());
    }

    #endregion

    #region Format Inspectors

    private const uint CdiVersion2 = 0x80000004;
    private const uint CdiVersion3 = 0x80000005;
    private const uint CdiVersion35 = 0x80000006;
    private static readonly byte[] CdiStartMark = { 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// Reads a DiscJuggler .cdi (v2, v3, v3.5). Layout per the open-source cdirip reader (GPL-2.0,
    /// https://github.com/jozip/cdirip), re-implemented here from its format description: the file ends with
    /// uint32 LE version + uint32 LE descriptor position, track data is stored back to back from byte 0.
    /// The first data track becomes the payload; an image whose tracks are all audio is flagged as audio.
    /// </summary>
    private static LegacyDiscImageInfo InspectDiscJuggler(string cdiPath)
    {
        using var fs = new FileStream(cdiPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;
        if (fileLen < 8) throw new InvalidDataException("DiscJuggler image is too small to contain a trailer.");

        using var r = new BinaryReader(fs);
        fs.Position = fileLen - 8;
        uint versionCode = r.ReadUInt32();
        uint headerValue = r.ReadUInt32();
        if (versionCode is not (CdiVersion2 or CdiVersion3 or CdiVersion35))
            throw new InvalidDataException($"'{Path.GetFileName(cdiPath)}' is not a DiscJuggler image (unknown version 0x{versionCode:X8}).");
        if (headerValue == 0) throw new InvalidDataException("DiscJuggler descriptor position is zero.");

        long headerPos = versionCode == CdiVersion2 ? headerValue : fileLen - headerValue;
        if (headerPos < 0 || headerPos >= fileLen - 8)
            throw new InvalidDataException("DiscJuggler descriptor position is outside the file.");
        string versionName = versionCode switch { CdiVersion2 => "2", CdiVersion3 => "3", _ => "3.5" };
        long descriptorEnd = fileLen - 8;

        void EnsureAvailable(long n)
        {
            if (n < 0 || fs.Position + n > descriptorEnd)
                throw new InvalidDataException("DiscJuggler descriptor is truncated.");
        }

        void Skip(long n)
        {
            EnsureAvailable(n);
            fs.Position += n;
        }

        byte ReadByteChecked() { EnsureAvailable(1); return r.ReadByte(); }
        ushort ReadUInt16Checked() { EnsureAvailable(2); return r.ReadUInt16(); }
        uint ReadUInt32Checked() { EnsureAvailable(4); return r.ReadUInt32(); }
        byte[] ReadBytesChecked(int count) { EnsureAvailable(count); return r.ReadBytes(count); }

        try
        {
            fs.Position = headerPos;
            int sessions = ReadUInt16Checked();
            if (sessions is < 1 or > 99) throw new InvalidDataException($"DiscJuggler image reports {sessions} sessions.");

            long dataCursor = 0;
            int trackCount = 0;
            bool anyData = false;
            long payloadOffset = 0, payloadLength = 0;
            int sectorSize = OpticalConstants.DataSectorSize;
            int dataSectors = 0;

            bool firstAudioSeen = false;
            long firstAudioOffset = 0;
            int firstAudioSectorSize = OpticalConstants.RawSectorSize;
            ulong totalAudioSectors = 0;

            for (int s = 0; s < sessions; s++)
            {
                int tracks = ReadUInt16Checked();
                if (tracks is < 1 or > 99) throw new InvalidDataException($"DiscJuggler session {s + 1} reports {tracks} tracks.");

                for (int t = 0; t < tracks; t++)
                {
                    if (ReadUInt32Checked() != 0) Skip(8);
                    byte[] mark1 = ReadBytesChecked(10);
                    byte[] mark2 = ReadBytesChecked(10);
                    if (!mark1.AsSpan().SequenceEqual(CdiStartMark) || !mark2.AsSpan().SequenceEqual(CdiStartMark))
                        throw new InvalidDataException("DiscJuggler track descriptor is corrupt (start marker mismatch).");
                    Skip(4);
                    int nameLen = ReadByteChecked();
                    Skip(nameLen);
                    Skip(11 + 4 + 4);
                    if (ReadUInt32Checked() == 0x80000000) Skip(8);
                    Skip(2);
                    uint pregap = ReadUInt32Checked();
                    uint length = ReadUInt32Checked();
                    Skip(6);
                    uint mode = ReadUInt32Checked();
                    if (mode > 2) throw new InvalidDataException($"DiscJuggler track mode {mode} is not supported.");
                    Skip(12);
                    ReadUInt32Checked(); // start LBA (unused: tracks are stored back to back)
                    uint totalLength = ReadUInt32Checked();
                    Skip(16);
                    uint sizeCode = ReadUInt32Checked();
                    int size = sizeCode switch
                    {
                        0 => OpticalConstants.DataSectorSize,
                        1 => SectorSize2336,
                        2 => OpticalConstants.RawSectorSize,
                        _ => throw new InvalidDataException($"DiscJuggler sector size code {sizeCode} is not supported.")
                    };
                    Skip(29);
                    if (versionCode != CdiVersion2)
                    {
                        Skip(5);
                        if (ReadUInt32Checked() == 0xFFFFFFFF) Skip(78);
                    }

                    if (length > int.MaxValue)
                        throw new InvalidDataException($"DiscJuggler track length {length} exceeds maximum supported sectors.");

                    ulong headerLimit = (ulong)headerPos;
                    ulong uCursor = (ulong)dataCursor;
                    ulong uPregapBytes = (ulong)pregap * (ulong)size;
                    ulong uTrackBytes = (ulong)length * (ulong)size;
                    ulong uTotalBytes = (ulong)totalLength * (ulong)size;

                    if (uCursor > headerLimit ||
                        uPregapBytes > headerLimit - uCursor ||
                        uTrackBytes > headerLimit - uCursor - uPregapBytes ||
                        uTotalBytes > headerLimit - uCursor)
                    {
                        throw new InvalidDataException("DiscJuggler track extends past the end of the data area.");
                    }

                    ulong trackOffset = uCursor + uPregapBytes;
                    trackCount++;

                    if (mode != 0 && !anyData)
                    {
                        anyData = true;
                        payloadOffset = (long)trackOffset;
                        sectorSize = size;
                        dataSectors = (int)length;
                        payloadLength = (long)uTrackBytes;
                    }
                    else if (mode == 0)
                    {
                        if (!firstAudioSeen)
                        {
                            firstAudioSeen = true;
                            firstAudioOffset = (long)trackOffset;
                            firstAudioSectorSize = size;
                        }
                        totalAudioSectors += length;
                    }

                    dataCursor = (long)(uCursor + uTotalBytes);
                }

                // Session trailer: 4 + 8 bytes, plus 1 more from v3 onward (cdirip CDI_skip_next_session).
                Skip(12 + (versionCode == CdiVersion2 ? 0 : 1));
            }

            if (trackCount == 0) throw new InvalidDataException("DiscJuggler image has no tracks.");

            if (!anyData)
            {
                if (totalAudioSectors == 0 || totalAudioSectors > int.MaxValue)
                    throw new InvalidDataException("DiscJuggler audio image has invalid sector count.");

                ulong audioBytes = totalAudioSectors * (ulong)firstAudioSectorSize;
                if (audioBytes > (ulong)long.MaxValue)
                    throw new InvalidDataException("DiscJuggler audio image size exceeds supported bounds.");

                payloadOffset = firstAudioOffset;
                sectorSize = firstAudioSectorSize;
                dataSectors = (int)totalAudioSectors;
                payloadLength = (long)audioBytes;
            }

            return new LegacyDiscImageInfo(
                SourceFilePath: cdiPath,
                BinaryPayloadPath: cdiPath,
                FormatName: $"DiscJuggler v{versionName} (.cdi)",
                PayloadOffsetBytes: payloadOffset,
                PayloadLengthBytes: payloadLength,
                SectorSizeBytes: sectorSize,
                SectorCount: dataSectors,
                IsRaw2352: sectorSize == OpticalConstants.RawSectorSize,
                IsAudioDisc: !anyData,
                TrackCount: trackCount);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("DiscJuggler descriptor is truncated or corrupt.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("DiscJuggler descriptor contains out-of-range numeric values.", ex);
        }
    }

    private static LegacyDiscImageInfo InspectIso(string isoPath)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long len = fs.Length;
        if (len == 0)
        {
            throw new InvalidDataException($"ISO disc image '{Path.GetFileName(isoPath)}' is empty (0 bytes).");
        }

        int sectors = OpticalConstants.BytesToSectors(len, OpticalConstants.DataSectorSize);
        return new LegacyDiscImageInfo(
            SourceFilePath: isoPath,
            BinaryPayloadPath: isoPath,
            FormatName: "Standard ISO 9660 Image (.iso)",
            PayloadOffsetBytes: 0,
            PayloadLengthBytes: len,
            SectorSizeBytes: OpticalConstants.DataSectorSize,
            SectorCount: sectors,
            IsRaw2352: false,
            IsAudioDisc: false,
            TrackCount: 1);
    }

    private static LegacyDiscImageInfo InspectRawBin(string binPath)
    {
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length == 0)
        {
            throw new InvalidDataException($"Binary disc image '{Path.GetFileName(binPath)}' is empty (0 bytes).");
        }

        bool hasIsoVolume = TryProbeIsoGeometry(fs, 0, fs.Length, out _, out _);
        var (sectorSize, offset, len) = ProbeOrGuessGeometry(fs);
        bool isRaw = sectorSize != OpticalConstants.DataSectorSize;
        int sectors = OpticalConstants.BytesToSectors(len, sectorSize);

        // A raw 2,352-byte .bin with no ISO 9660 volume descriptor is almost always CD-DA audio. Treat it as audio so
        // it is never burned or converted as if its sectors were data.
        bool looksLikeAudio = isRaw && !hasIsoVolume;

        return new LegacyDiscImageInfo(
            SourceFilePath: binPath,
            BinaryPayloadPath: binPath,
            FormatName: isRaw ? $"Raw Sector Binary Image (.bin, {sectorSize} B)" : "Raw ISO Sector Image (.bin, 2048 B)",
            PayloadOffsetBytes: offset,
            PayloadLengthBytes: len,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: isRaw,
            IsAudioDisc: looksLikeAudio,
            TrackCount: 1);
    }

    private static LegacyDiscImageInfo InspectNrg(string nrgPath)
    {
        using var fs = new FileStream(nrgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;
        if (fileLen < 16)
        {
            throw new InvalidDataException($"Nero disc image '{Path.GetFileName(nrgPath)}' is too small to contain a valid NRG trailer.");
        }

        Span<byte> tail12 = stackalloc byte[12];
        fs.Position = fileLen - 12;
        if (fs.Read(tail12) != 12)
        {
            throw new InvalidDataException($"Failed to read Nero footer from '{Path.GetFileName(nrgPath)}'.");
        }

        long trailerOffset;
        long footerEnd;
        string versionTag;

        if (tail12.Slice(0, 4).SequenceEqual("NER5"u8))
        {
            ulong off = BinaryPrimitives.ReadUInt64BigEndian(tail12.Slice(4, 8));
            footerEnd = fileLen - 12;
            if (off == 0 || off >= (ulong)footerEnd)
            {
                throw new InvalidDataException($"Nero NER5 trailer offset 0x{off:X} is out of bounds for file length {fileLen}.");
            }
            trailerOffset = (long)off;
            versionTag = "Nero Burning ROM v5.5+ (NER5)";
        }
        else if (tail12.Slice(4, 4).SequenceEqual("NERO"u8))
        {
            uint off = BinaryPrimitives.ReadUInt32BigEndian(tail12.Slice(8, 4));
            footerEnd = fileLen - 8;
            if (off == 0 || (ulong)off >= (ulong)footerEnd)
            {
                throw new InvalidDataException($"Nero NERO trailer offset 0x{off:X} is out of bounds for file length {fileLen}.");
            }
            trailerOffset = off;
            versionTag = "Nero Burning ROM Legacy (NERO)";
        }
        else
        {
            throw new InvalidDataException($"'{Path.GetFileName(nrgPath)}' is not a valid Nero (.nrg) image (missing NER5/NERO footer signature).");
        }

        ulong trailerLimit = (ulong)trailerOffset;
        var tracks = new List<(ulong Offset, ulong Size, int SectorSize, bool IsAudio)>();
        bool seenEnd = false;

        fs.Position = trailerOffset;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> etn2 = stackalloc byte[32];
        Span<byte> etnf = stackalloc byte[20];
        Span<byte> daoHeader = stackalloc byte[22];
        Span<byte> daox = stackalloc byte[42];
        Span<byte> daoi = stackalloc byte[30];

        while (fs.Position < footerEnd)
        {
            if (fs.Position + 8 > footerEnd || fs.Read(chunkHeader) != 8)
            {
                throw new InvalidDataException("Nero trailer chunk header is truncated.");
            }

            foreach (byte b in chunkHeader.Slice(0, 4))
            {
                if (b < 0x20 || b > 0x7E)
                {
                    throw new InvalidDataException("Nero trailer contains an invalid chunk identifier.");
                }
            }

            string id = Encoding.ASCII.GetString(chunkHeader.Slice(0, 4));
            long chunkLen = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.Slice(4, 4));
            long chunkDataStart = fs.Position;
            long nextChunkPos = chunkDataStart + chunkLen;
            if (nextChunkPos > footerEnd)
            {
                throw new InvalidDataException($"Nero '{id}' chunk length ({chunkLen}) extends past trailer end.");
            }

            if (id == "END!")
            {
                seenEnd = true;
                break;
            }

            if (id == "ETN2")
            {
                if (chunkLen < 32 || chunkLen % 32 != 0)
                {
                    throw new InvalidDataException($"Nero ETN2 chunk has invalid length {chunkLen}.");
                }

                long recordCount = chunkLen / 32;
                for (long i = 0; i < recordCount; i++)
                {
                    if (fs.Read(etn2) != 32)
                    {
                        throw new InvalidDataException("Nero ETN2 record is truncated.");
                    }

                    ulong off = BinaryPrimitives.ReadUInt64BigEndian(etn2.Slice(0, 8));
                    ulong sz = BinaryPrimitives.ReadUInt64BigEndian(etn2.Slice(8, 8));
                    uint mode = BinaryPrimitives.ReadUInt32BigEndian(etn2.Slice(16, 4));

                    if (sz == 0 || off > trailerLimit || sz > trailerLimit - off)
                    {
                        throw new InvalidDataException($"Nero ETN2 track range (offset={off}, size={sz}) is invalid or exceeds trailer offset {trailerOffset}.");
                    }

                    int trkSectorSize = NrgModeToSectorSize(mode, -1);
                    if (trkSectorSize <= 0)
                    {
                        throw new InvalidDataException($"Nero ETN2 track mode {mode} is not supported.");
                    }

                    tracks.Add((off, sz, trkSectorSize, mode is 7 or 16));
                }
            }
            else if (id == "ETNF")
            {
                if (chunkLen < 20 || chunkLen % 20 != 0)
                {
                    throw new InvalidDataException($"Nero ETNF chunk has invalid length {chunkLen}.");
                }

                long recordCount = chunkLen / 20;
                for (long i = 0; i < recordCount; i++)
                {
                    if (fs.Read(etnf) != 20)
                    {
                        throw new InvalidDataException("Nero ETNF record is truncated.");
                    }

                    uint off = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(0, 4));
                    uint sz = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(4, 4));
                    uint mode = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(8, 4));

                    if (sz == 0 || (ulong)off > trailerLimit || (ulong)sz > trailerLimit - off)
                    {
                        throw new InvalidDataException($"Nero ETNF track range (offset={off}, size={sz}) is invalid or exceeds trailer offset {trailerOffset}.");
                    }

                    int trkSectorSize = NrgModeToSectorSize(mode, -1);
                    if (trkSectorSize <= 0)
                    {
                        throw new InvalidDataException($"Nero ETNF track mode {mode} is not supported.");
                    }

                    tracks.Add((off, sz, trkSectorSize, mode is 7 or 16));
                }
            }
            else if (id == "DAOX")
            {
                if (chunkLen < 22 + 42 || (chunkLen - 22) % 42 != 0)
                {
                    throw new InvalidDataException($"Nero DAOX chunk has invalid length {chunkLen}.");
                }
                if (fs.Read(daoHeader) != 22)
                {
                    throw new InvalidDataException("Nero DAOX header is truncated.");
                }

                long recordCount = (chunkLen - 22) / 42;
                for (long i = 0; i < recordCount; i++)
                {
                    if (fs.Read(daox) != 42)
                    {
                        throw new InvalidDataException("Nero DAOX track record is truncated.");
                    }

                    ushort declaredSectorSize = BinaryPrimitives.ReadUInt16BigEndian(daox.Slice(12, 2));
                    byte mode = daox[14];
                    ulong pregapOff = BinaryPrimitives.ReadUInt64BigEndian(daox.Slice(18, 8));
                    ulong startOff = BinaryPrimitives.ReadUInt64BigEndian(daox.Slice(26, 8));
                    ulong endOff = BinaryPrimitives.ReadUInt64BigEndian(daox.Slice(34, 8));

                    if (endOff <= startOff || pregapOff > startOff || endOff > trailerLimit)
                    {
                        throw new InvalidDataException($"Nero DAOX track offsets ({pregapOff}..{startOff}..{endOff}) are out of bounds.");
                    }

                    int trkSectorSize = NrgModeToSectorSize(mode, declaredSectorSize);
                    if (trkSectorSize <= 0)
                    {
                        throw new InvalidDataException($"Nero DAOX track mode {mode} is not supported.");
                    }

                    tracks.Add((startOff, endOff - startOff, trkSectorSize, mode is 7 or 16));
                }
            }
            else if (id == "DAOI")
            {
                if (chunkLen < 22 + 30 || (chunkLen - 22) % 30 != 0)
                {
                    throw new InvalidDataException($"Nero DAOI chunk has invalid length {chunkLen}.");
                }
                if (fs.Read(daoHeader) != 22)
                {
                    throw new InvalidDataException("Nero DAOI header is truncated.");
                }

                long recordCount = (chunkLen - 22) / 30;
                for (long i = 0; i < recordCount; i++)
                {
                    if (fs.Read(daoi) != 30)
                    {
                        throw new InvalidDataException("Nero DAOI track record is truncated.");
                    }

                    ushort declaredSectorSize = BinaryPrimitives.ReadUInt16BigEndian(daoi.Slice(12, 2));
                    byte mode = daoi[14];
                    uint pregapOff = BinaryPrimitives.ReadUInt32BigEndian(daoi.Slice(18, 4));
                    uint startOff = BinaryPrimitives.ReadUInt32BigEndian(daoi.Slice(22, 4));
                    uint endOff = BinaryPrimitives.ReadUInt32BigEndian(daoi.Slice(26, 4));

                    if (endOff <= startOff || pregapOff > startOff || (ulong)endOff > trailerLimit)
                    {
                        throw new InvalidDataException($"Nero DAOI track offsets ({pregapOff}..{startOff}..{endOff}) are out of bounds.");
                    }

                    int trkSectorSize = NrgModeToSectorSize(mode, declaredSectorSize);
                    if (trkSectorSize <= 0)
                    {
                        throw new InvalidDataException($"Nero DAOI track mode {mode} is not supported.");
                    }

                    tracks.Add((startOff, endOff - startOff, trkSectorSize, mode is 7 or 16));
                }
            }

            fs.Position = nextChunkPos;
        }

        if (tracks.Count == 0 || !seenEnd)
        {
            throw new InvalidDataException($"Nero image '{Path.GetFileName(nrgPath)}' trailer is missing track descriptors or END! marker.");
        }

        int dataTrackIdx = tracks.FindIndex(t => !t.IsAudio);
        bool isAudio = dataTrackIdx < 0;
        long payloadOffset;
        long payloadLength;
        int sectorSize;

        if (!isAudio)
        {
            var dataTrack = tracks[dataTrackIdx];
            payloadOffset = (long)dataTrack.Offset;
            payloadLength = (long)dataTrack.Size;
            sectorSize = dataTrack.SectorSize;

            // Locate the ISO 9660 Primary Volume Descriptor to confirm the sector geometry and skip a stored
            // 150-sector pre-gap (Nero DAO images often keep track 1's 2-second pre-gap in front of the data).
            if (TryProbeIsoGeometry(fs, payloadOffset, payloadLength, out int probedSize, out long pregapBytes))
            {
                sectorSize = probedSize;
                payloadOffset += pregapBytes;
                payloadLength -= pregapBytes;
            }
        }
        else
        {
            payloadOffset = (long)tracks[0].Offset;
            sectorSize = tracks[0].SectorSize;
            ulong totalAudioBytes = 0;
            foreach (var trk in tracks)
            {
                if (totalAudioBytes > (ulong)long.MaxValue - trk.Size)
                {
                    throw new InvalidDataException("Nero audio track sizes overflow supported bounds.");
                }
                totalAudioBytes += trk.Size;
            }
            payloadLength = (long)totalAudioBytes;
        }

        int sectors = OpticalConstants.BytesToSectors(payloadLength, sectorSize);
        return new LegacyDiscImageInfo(
            SourceFilePath: nrgPath,
            BinaryPayloadPath: nrgPath,
            FormatName: versionTag,
            PayloadOffsetBytes: payloadOffset,
            PayloadLengthBytes: payloadLength,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: sectorSize == OpticalConstants.RawSectorSize,
            IsAudioDisc: isAudio,
            TrackCount: tracks.Count);
    }

    private static LegacyDiscImageInfo InspectCloneCdCcd(string ccdPath)
    {
        string dir = Path.GetDirectoryName(ccdPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(ccdPath);
        string imgPath = Path.Combine(dir, baseName + ".img");
        if (!File.Exists(imgPath))
        {
            string binCandidate = Path.Combine(dir, baseName + ".bin");
            if (File.Exists(binCandidate)) imgPath = binCandidate;
        }

        if (!File.Exists(imgPath))
        {
            throw new FileNotFoundException(
                $"CloneCD payload image not found for '{Path.GetFileName(ccdPath)}' (expected '{baseName}.img' or '{baseName}.bin').",
                imgPath);
        }

        bool hasCloneCdHeader = false;
        int trackCount = 0;
        var trackModes = new List<int>();
        foreach (string rawLine in File.ReadLines(ccdPath))
        {
            string line = rawLine.Trim();
            if (line.Equals("[CloneCD]", StringComparison.OrdinalIgnoreCase))
            {
                hasCloneCdHeader = true;
            }
            else if (line.StartsWith("[Track ", StringComparison.OrdinalIgnoreCase))
            {
                trackCount++;
            }
            else if (line.StartsWith("MODE=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(line.AsSpan(5).Trim(), out int parsedMode))
            {
                trackModes.Add(parsedMode);
            }
        }

        if (!hasCloneCdHeader && trackCount == 0)
        {
            throw new InvalidDataException($"'{Path.GetFileName(ccdPath)}' is not a valid CloneCD (.ccd) control file.");
        }

        if (trackCount == 0) trackCount = 1;

        long payloadLen = new FileInfo(imgPath).Length;
        if (payloadLen == 0)
        {
            throw new InvalidDataException($"CloneCD payload image '{Path.GetFileName(imgPath)}' is empty (0 bytes).");
        }

        // Only a disc whose tracks are ALL audio (MODE=0) is an Audio CD. Mixed-mode / Enhanced CDs
        // (data track + audio tracks) still carry a convertible data track.
        bool isAudio = trackModes.Count > 0 && trackModes.All(m => m == 0);

        long payloadOffset = 0;
        int sectorSize = OpticalConstants.RawSectorSize;
        if (!isAudio)
        {
            using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (TryProbeIsoGeometry(fs, 0, payloadLen, out int probedSize, out long pregapBytes))
            {
                sectorSize = probedSize;
                payloadOffset = pregapBytes;
                payloadLen -= pregapBytes;
            }
        }

        int sectors = OpticalConstants.BytesToSectors(payloadLen, sectorSize);
        return new LegacyDiscImageInfo(
            SourceFilePath: ccdPath,
            BinaryPayloadPath: imgPath,
            FormatName: "CloneCD Control Image (.ccd/.img)",
            PayloadOffsetBytes: payloadOffset,
            PayloadLengthBytes: payloadLen,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: sectorSize == OpticalConstants.RawSectorSize,
            IsAudioDisc: isAudio,
            TrackCount: trackCount);
    }

    private static LegacyDiscImageInfo InspectCloneCdImg(string imgPath)
    {
        string ccdCandidate = Path.ChangeExtension(imgPath, ".ccd");
        if (File.Exists(ccdCandidate))
        {
            var ccdInfo = InspectCloneCdCcd(ccdCandidate);
            return ccdInfo with { SourceFilePath = imgPath, BinaryPayloadPath = imgPath };
        }

        using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length == 0)
        {
            throw new InvalidDataException($"Disc image '{Path.GetFileName(imgPath)}' is empty (0 bytes).");
        }

        var (sectorSize, offset, len) = ProbeOrGuessGeometry(fs);
        bool isRaw = sectorSize != OpticalConstants.DataSectorSize;
        int sectors = OpticalConstants.BytesToSectors(len, sectorSize);

        return new LegacyDiscImageInfo(
            SourceFilePath: imgPath,
            BinaryPayloadPath: imgPath,
            FormatName: isRaw ? $"CloneCD / Raw Sector Image (.img, {sectorSize} B)" : "Raw ISO Sector Image (.img, 2048 B)",
            PayloadOffsetBytes: offset,
            PayloadLengthBytes: len,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: isRaw,
            IsAudioDisc: false,
            TrackCount: 1);
    }

    private static LegacyDiscImageInfo InspectAlcoholMds(string mdsPath)
    {
        long mdsFileLen = new FileInfo(mdsPath).Length;
        const int MdsHeaderSize = 88;
        const int MdsSessionSize = 24;
        const int MdsTrackSize = 80;
        const int MdsExtraSize = 8;
        const int MdsFooterSize = 16;

        if (mdsFileLen < MdsHeaderSize || mdsFileLen > 64L * 1024 * 1024)
        {
            throw new InvalidDataException($"'{Path.GetFileName(mdsPath)}' is not a valid Alcohol 120% MDS descriptor (invalid file size {mdsFileLen} bytes).");
        }

        byte[] mds = File.ReadAllBytes(mdsPath);
        ReadOnlySpan<byte> span = mds;
        if (!span.Slice(0, 16).SequenceEqual(MdsSignature))
        {
            throw new InvalidDataException($"'{Path.GetFileName(mdsPath)}' is not a valid Alcohol 120% MDS file (missing MEDIA DESCRIPTOR signature).");
        }

        byte majorVersion = span[16];
        if (majorVersion != 1)
        {
            throw new InvalidDataException($"Alcohol 120% MDS version {majorVersion}.{span[17]} is not supported.");
        }

        ushort mediumType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(18, 2));
        ushort numSessions = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(20, 2));
        uint sessionsOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(80, 4));

        if (numSessions is < 1 or > 99)
        {
            throw new InvalidDataException($"Alcohol 120% MDS reports invalid session count {numSessions}.");
        }

        if (sessionsOffset < MdsHeaderSize || (long)sessionsOffset + (long)numSessions * MdsSessionSize > mds.Length)
        {
            throw new InvalidDataException("Alcohol 120% MDS session table offset is out of bounds.");
        }

        bool isCd = mediumType < 0x10;
        var parsedTracks = new List<(bool IsAudio, int SectorSize, long StartOffset, uint TrackSectors, string? DeclaredFile)>();

        for (int s = 0; s < numSessions; s++)
        {
            int sessPos = (int)sessionsOffset + (s * MdsSessionSize);
            byte numAllBlocks = span[sessPos + 10];
            uint tracksOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(sessPos + 20, 4));

            if (numAllBlocks == 0 || tracksOffset < MdsHeaderSize || (long)tracksOffset + (long)numAllBlocks * MdsTrackSize > mds.Length)
            {
                throw new InvalidDataException($"Alcohol 120% MDS session {s + 1} track table is out of bounds.");
            }

            for (int t = 0; t < numAllBlocks; t++)
            {
                int trkPos = (int)tracksOffset + (t * MdsTrackSize);
                byte mode = span[trkPos + 0];
                byte point = span[trkPos + 4];
                uint extraOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(trkPos + 12, 4));
                ushort sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(trkPos + 16, 2));
                ulong startOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(trkPos + 40, 8));
                uint numFiles = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(trkPos + 48, 4));
                uint footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(trkPos + 52, 4));

                if (point is < 1 or > 98)
                {
                    continue;
                }

                if ((mode & 0x07) == 0x06)
                {
                    throw new InvalidDataException($"Alcohol 120% MDS track {point} has unsupported mode 0x{mode:X2}.");
                }

                if (sectorSize is not (OpticalConstants.DataSectorSize or 2324 or 2328 or SectorSize2336 or OpticalConstants.RawSectorSize or SectorSize2448))
                {
                    throw new InvalidDataException($"Alcohol 120% MDS track {point} has unsupported sector size {sectorSize}.");
                }

                if (startOffset > (ulong)long.MaxValue)
                {
                    throw new InvalidDataException($"Alcohol 120% MDS track {point} start offset exceeds supported bounds.");
                }

                uint trackSectors = 0;
                if (isCd && extraOffset != 0)
                {
                    if (extraOffset < MdsHeaderSize || (long)extraOffset + MdsExtraSize > mds.Length)
                    {
                        throw new InvalidDataException($"Alcohol 120% MDS track {point} extra block offset is out of bounds.");
                    }
                    trackSectors = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)extraOffset + 4, 4));
                }

                string? declaredFile = null;
                if (footerOffset != 0 && numFiles > 0)
                {
                    if (footerOffset < MdsHeaderSize || (long)footerOffset + (long)numFiles * MdsFooterSize > mds.Length)
                    {
                        throw new InvalidDataException($"Alcohol 120% MDS track {point} footer offset is out of bounds.");
                    }

                    uint filenameOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)footerOffset, 4));
                    uint widechar = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)footerOffset + 4, 4));
                    if (filenameOffset > 0 && filenameOffset < (uint)mds.Length)
                    {
                        declaredFile = ReadNullTerminatedMdsFilename(span.Slice((int)filenameOffset), widechar != 0);
                    }
                }

                bool trkIsAudio = mode != 0 && (mode & 0x07) == 0x01;
                parsedTracks.Add((trkIsAudio, sectorSize, (long)startOffset, trackSectors, declaredFile));
            }
        }

        if (parsedTracks.Count == 0)
        {
            throw new InvalidDataException($"Alcohol 120% MDS '{Path.GetFileName(mdsPath)}' contains no tracks.");
        }

        int dataTrackIndex = parsedTracks.FindIndex(t => !t.IsAudio);
        bool isAudioDisc = dataTrackIndex < 0;
        var primary = isAudioDisc ? parsedTracks[0] : parsedTracks[dataTrackIndex];

        string mdfPath = ResolveMdsPayloadPath(mdsPath, primary.DeclaredFile);
        if (!File.Exists(mdfPath))
        {
            throw new FileNotFoundException($"Alcohol 120% payload file not found: '{mdfPath}'.", mdfPath);
        }

        long mdfLen = new FileInfo(mdfPath).Length;
        if (mdfLen == 0)
        {
            throw new InvalidDataException($"Alcohol 120% payload file '{Path.GetFileName(mdfPath)}' is empty (0 bytes).");
        }

        if (primary.StartOffset < 0 || primary.StartOffset >= mdfLen)
        {
            throw new InvalidDataException($"Alcohol 120% track start offset {primary.StartOffset} is outside '{Path.GetFileName(mdfPath)}' ({mdfLen} bytes).");
        }

        long payloadLength;
        int sectors;
        if (!isAudioDisc)
        {
            if (primary.TrackSectors > 0)
            {
                ulong trackBytes = (ulong)primary.TrackSectors * (ulong)primary.SectorSize;
                if (trackBytes > (ulong)(mdfLen - primary.StartOffset))
                {
                    throw new InvalidDataException($"Alcohol 120% track extends past the end of '{Path.GetFileName(mdfPath)}'.");
                }
                payloadLength = (long)trackBytes;
                sectors = checked((int)primary.TrackSectors);
            }
            else
            {
                payloadLength = mdfLen - primary.StartOffset;
                sectors = OpticalConstants.BytesToSectors(payloadLength, primary.SectorSize);
            }
        }
        else
        {
            ulong audioSectorsSum = 0;
            foreach (var trk in parsedTracks)
            {
                if (trk.TrackSectors > 0)
                {
                    audioSectorsSum += trk.TrackSectors;
                }
            }

            if (audioSectorsSum > 0)
            {
                ulong audioBytes = audioSectorsSum * (ulong)primary.SectorSize;
                if (audioBytes > (ulong)(mdfLen - primary.StartOffset) || audioSectorsSum > int.MaxValue)
                {
                    throw new InvalidDataException($"Alcohol 120% audio tracks extend past the end of '{Path.GetFileName(mdfPath)}'.");
                }
                payloadLength = (long)audioBytes;
                sectors = (int)audioSectorsSum;
            }
            else
            {
                payloadLength = mdfLen - primary.StartOffset;
                sectors = OpticalConstants.BytesToSectors(payloadLength, primary.SectorSize);
            }
        }

        if (sectors <= 0)
        {
            throw new InvalidDataException($"Alcohol 120% image '{Path.GetFileName(mdsPath)}' contains 0 sectors.");
        }

        return new LegacyDiscImageInfo(
            SourceFilePath: mdsPath,
            BinaryPayloadPath: mdfPath,
            FormatName: "Alcohol 120% Media Descriptor (.mds/.mdf)",
            PayloadOffsetBytes: primary.StartOffset,
            PayloadLengthBytes: payloadLength,
            SectorSizeBytes: primary.SectorSize,
            SectorCount: sectors,
            IsRaw2352: primary.SectorSize == OpticalConstants.RawSectorSize,
            IsAudioDisc: isAudioDisc,
            TrackCount: parsedTracks.Count);
    }

    private static string? ReadNullTerminatedMdsFilename(ReadOnlySpan<byte> slice, bool wideChar)
    {
        if (wideChar)
        {
            int byteLen = 0;
            while (byteLen + 1 < slice.Length && (slice[byteLen] != 0 || slice[byteLen + 1] != 0))
            {
                byteLen += 2;
            }
            return byteLen == 0 ? null : Encoding.Unicode.GetString(slice.Slice(0, byteLen)).Trim();
        }

        int nul = slice.IndexOf((byte)0);
        ReadOnlySpan<byte> strBytes = nul >= 0 ? slice.Slice(0, nul) : slice;
        return strBytes.IsEmpty ? null : Encoding.UTF8.GetString(strBytes).Trim();
    }

    private static string ResolveMdsPayloadPath(string mdsPath, string? declaredFile)
    {
        string defaultMdf = Path.ChangeExtension(mdsPath, ".mdf");
        if (string.IsNullOrWhiteSpace(declaredFile))
        {
            return defaultMdf;
        }

        if (declaredFile.StartsWith("*.", StringComparison.Ordinal))
        {
            return Path.ChangeExtension(mdsPath, declaredFile.Substring(1));
        }

        string mdsDir = Path.GetDirectoryName(mdsPath) ?? string.Empty;
        string candidate = Path.IsPathRooted(declaredFile)
            ? declaredFile
            : Path.Combine(mdsDir, declaredFile);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        string fileNameOnly = Path.Combine(mdsDir, Path.GetFileName(declaredFile));
        return File.Exists(fileNameOnly) ? fileNameOnly : candidate;
    }

    private static LegacyDiscImageInfo InspectAlcoholMdf(string mdfPath)
    {
        using var fs = new FileStream(mdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length == 0)
        {
            throw new InvalidDataException($"Alcohol 120% payload image '{Path.GetFileName(mdfPath)}' is empty (0 bytes).");
        }

        var (sectorSize, offset, len) = ProbeOrGuessGeometry(fs);
        bool isRaw = sectorSize != OpticalConstants.DataSectorSize;
        int sectors = OpticalConstants.BytesToSectors(len, sectorSize);

        return new LegacyDiscImageInfo(
            SourceFilePath: mdfPath,
            BinaryPayloadPath: mdfPath,
            FormatName: isRaw ? $"Alcohol 120% Raw Image (.mdf, {sectorSize} B)" : "Alcohol 120% ISO Image (.mdf, 2048 B)",
            PayloadOffsetBytes: offset,
            PayloadLengthBytes: len,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: isRaw,
            IsAudioDisc: false,
            TrackCount: 1);
    }

    /// <summary>
    /// Maps a Nero ETN2/ETNF track mode code to its stored sector size:
    /// 0/2 = 2048 (Mode 1 / Mode 2 Form 1 cooked), 3 = 2336 (Mode 2 formless), 5/6/7 = 2352 raw (Mode 1 / Mode 2 / Audio),
    /// 15/16/17 = 2448 (2352 raw + 96-byte subchannel).
    /// </summary>
    private static int NrgModeToSectorSize(uint mode, int fallback) => mode switch
    {
        0 or 2 => OpticalConstants.DataSectorSize,
        3 => SectorSize2336,
        5 or 6 or 7 => OpticalConstants.RawSectorSize,
        15 or 16 or 17 => SectorSize2448,
        _ => fallback
    };

    /// <summary>
    /// Finds the ISO 9660 Primary Volume Descriptor ("CD001" at user-data byte 1 of sector 16) by trying every stored sector
    /// geometry (2048; 2352 Mode 1 @16; 2352 Mode 2 XA @24; 2336 @8; 2448 with subchannel) with and without a stored
    /// 150-sector pre-gap. Returns the sector size and how many pre-gap bytes precede sector 0 of the data track.
    /// </summary>
    private static bool TryProbeIsoGeometry(Stream stream, long baseOffset, long payloadLength, out int sectorSize, out long pregapBytes)
    {
        (int Size, int UserOffset)[] geometries =
        {
            (OpticalConstants.DataSectorSize, 0),
            (OpticalConstants.RawSectorSize, 16),
            (OpticalConstants.RawSectorSize, 24),
            (SectorSize2336, 8),
            (SectorSize2448, 16),
            (SectorSize2448, 24),
        };

        Span<byte> buf = stackalloc byte[5];
        foreach (int lead in new[] { 0, StandardPregapSectors })
        {
            foreach (var (size, userOffset) in geometries)
            {
                long pvd = baseOffset + ((lead + 16L) * size) + userOffset + 1;
                if (pvd + 5 > baseOffset + payloadLength || pvd + 5 > stream.Length) continue;
                stream.Position = pvd;
                if (stream.Read(buf) == 5 && buf.SequenceEqual(IsoCd001Magic))
                {
                    sectorSize = size;
                    pregapBytes = (long)lead * size;
                    return true;
                }
            }
        }

        sectorSize = 0;
        pregapBytes = 0;
        return false;
    }

    /// <summary>
    /// Probes the PVD geometry; without an ISO 9660 descriptor (e.g. pure UDF) falls back to raw CD sync check or size-divisibility guess.
    /// </summary>
    private static (int SectorSize, long Offset, long Length) ProbeOrGuessGeometry(FileStream fs)
    {
        long len = fs.Length;
        if (TryProbeIsoGeometry(fs, 0, len, out int size, out long pregap))
        {
            return (size, pregap, len - pregap);
        }

        if (len >= OpticalConstants.RawSectorSize && len % OpticalConstants.RawSectorSize == 0 && HasRawCdSyncPatternAtStride(fs, OpticalConstants.RawSectorSize))
        {
            return (OpticalConstants.RawSectorSize, 0, len);
        }

        if (len >= SectorSize2448 && len % SectorSize2448 == 0 && HasRawCdSyncPatternAtStride(fs, SectorSize2448))
        {
            return (SectorSize2448, 0, len);
        }

        bool isRaw = len % OpticalConstants.RawSectorSize == 0 && len % OpticalConstants.DataSectorSize != 0;
        return (isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize, 0, len);
    }

    /// <summary>
    /// Returns true when the seekable stream begins with the 12-byte CD-ROM raw sector sync header
    /// (and a second sync header at byte 2,352 when the stream is at least two raw sectors long).
    /// </summary>
    public static bool HasRawCdSyncPattern(Stream stream) =>
        HasRawCdSyncPatternAtStride(stream, OpticalConstants.RawSectorSize);

    private static bool HasRawCdSyncPatternAtStride(Stream stream, int stride, long baseOffset = 0)
    {
        if (!stream.CanSeek || baseOffset < 0 || stream.Length < baseOffset + stride)
        {
            return false;
        }

        long savedPos = stream.Position;
        try
        {
            Span<byte> sync = stackalloc byte[12];
            stream.Position = baseOffset;
            if (stream.Read(sync) != 12 || !sync.SequenceEqual(RawCdSyncPattern))
            {
                return false;
            }

            if (stream.Length >= baseOffset + (stride * 2L))
            {
                stream.Position = baseOffset + stride;
                if (stream.Read(sync) != 12 || !sync.SequenceEqual(RawCdSyncPattern))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            stream.Position = savedPos;
        }
    }

    /// <summary>
    /// Read-only seekable stream exposing the 2,048-byte user data of every sector of a legacy image, one sector at a time.
    /// </summary>
    private sealed class LegacyIsoUserDataStream : Stream
    {
        private readonly LegacyDiscImageInfo _info;
        private readonly FileStream _source;
        private readonly byte[] _sector;
        private readonly long _length;
        private long _position;
        private long _cachedSector = -1;

        public LegacyIsoUserDataStream(LegacyDiscImageInfo info)
        {
            _info = info;
            _source = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            _sector = new byte[Math.Max(info.SectorSizeBytes, OpticalConstants.DataSectorSize)];
            _length = (long)info.SectorCount * OpticalConstants.DataSectorSize;
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

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                long sectorIndex = _position / OpticalConstants.DataSectorSize;
                int within = (int)(_position % OpticalConstants.DataSectorSize);
                ReadOnlySpan<byte> user = LoadUserData(sectorIndex);
                int n = Math.Min(buffer.Length, OpticalConstants.DataSectorSize - within);
                user.Slice(within, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private ReadOnlySpan<byte> LoadUserData(long sectorIndex)
        {
            int size = _info.SectorSizeBytes;
            if (_cachedSector != sectorIndex)
            {
                _source.Position = _info.PayloadOffsetBytes + (sectorIndex * size);
                int got = 0;
                while (got < size)
                {
                    int r = _source.Read(_sector, got, size - got);
                    if (r <= 0) break;
                    got += r;
                }

                if (got < size)
                {
                    Array.Clear(_sector, got, size - got); // truncated image tail reads as zeros
                }

                _cachedSector = sectorIndex;
            }

            if (IsMode2Form2Sector(_sector.AsSpan(0, size), size))
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(_info.SourceFilePath)}' contains Mode 2 Form 2 sectors (2,324 bytes/sector), which cannot be burned as a 2,048-byte ISO stream.");
            }

            int userOffset = GetUserDataOffset(_sector.AsSpan(0, size), size);
            return _sector.AsSpan(userOffset, OpticalConstants.DataSectorSize);
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

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #endregion
}
