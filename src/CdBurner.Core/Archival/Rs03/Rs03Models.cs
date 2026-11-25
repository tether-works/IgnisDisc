using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Storage mode for RS03 raw-sector parity.
/// </summary>
public enum Rs03Mode
{
    /// <summary>Standalone <c>.ecc</c> parity file paired with any ISO/UDF image, safe on write-once and rewritable media.</summary>
    EccFile = 0,

    /// <summary>Augmented write-once ISO/UDF image padded to a CD/DVD/BD capacity ceiling with inline CRC and ECC layers.</summary>
    AugmentedImage = 1
}

/// <summary>
/// Status of the fingerprint sector (LBA 16) comparison against the RS03 header.
/// </summary>
public enum Rs03FingerprintStatus
{
    Match = 0,
    MissingOrZeroed = 1,
    NotChecked = 2,
    Mismatch = 3
}

/// <summary>
/// 255-layer RS03 geometry for either a standalone <c>.ecc</c> file or an augmented ISO/UDF image.
/// </summary>
public sealed record Rs03Layout(
    Rs03Mode Mode,
    long DataSectors,
    int InLastBytes,
    long DataPaddingSectors,
    long SectorsPerLayer,
    long TotalVirtualSectors,
    long EccHeaderSector,
    long FirstCrcSector,
    long FirstEccSector,
    int DataLayerCount,
    int ParityRootCount,
    double RedundancyPercent,
    long MediumCapacitySectors)
{
    /// <summary>Number of data layers (excluding the CRC layer).</summary>
    public int PayloadDataLayers => DataLayerCount - 1;

    /// <summary>Total sectors written to the <c>.ecc</c> file (header + CRC + ECC), or 0 for augmented images.</summary>
    public long EccFileTotalSectors => Mode == Rs03Mode.EccFile
        ? Rs03Constants.EccHeaderSectors + (ParityRootCount + 1L) * SectorsPerLayer
        : 0;

    /// <summary>Maps a layer index [0..254] and column offset [0..SectorsPerLayer-1] to the on-disk sector index.</summary>
    public long GetDiskSectorIndex(int layer, long column)
    {
        if (Mode == Rs03Mode.AugmentedImage)
        {
            return layer * SectorsPerLayer + column;
        }

        if (layer < DataLayerCount - 1)
        {
            return layer * SectorsPerLayer + column;
        }
        if (layer == DataLayerCount - 1)
        {
            return FirstCrcSector + column;
        }
        return FirstEccSector + (layer - DataLayerCount) * SectorsPerLayer + column;
    }

    public static Rs03Layout ForEccFile(long dataSectors, int inLastBytes, int parityRootCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataSectors);
        if (inLastBytes is < 1 or > Rs03Constants.SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(inLastBytes));
        }
        if (parityRootCount is < Rs03Constants.MinRoots or > Rs03Constants.MaxRoots)
        {
            throw new ArgumentOutOfRangeException(nameof(parityRootCount));
        }

        int ndata = Rs03Constants.FieldMax - parityRootCount;
        int payloadLayers = ndata - 1;
        long sectorsPerLayer = (dataSectors + payloadLayers - 1) / payloadLayers;
        long totalVirtualSectors = (long)Rs03Constants.FieldMax * sectorsPerLayer;
        double redundancy = parityRootCount * 100.0 / ndata;

        return new Rs03Layout(
            Mode: Rs03Mode.EccFile,
            DataSectors: dataSectors,
            InLastBytes: inLastBytes,
            DataPaddingSectors: 0,
            SectorsPerLayer: sectorsPerLayer,
            TotalVirtualSectors: totalVirtualSectors,
            EccHeaderSector: 0,
            FirstCrcSector: Rs03Constants.EccHeaderSectors,
            FirstEccSector: Rs03Constants.EccHeaderSectors + sectorsPerLayer,
            DataLayerCount: ndata,
            ParityRootCount: parityRootCount,
            RedundancyPercent: redundancy,
            MediumCapacitySectors: 0);
    }

    public static Rs03Layout ForAugmentedImage(long dataSectors, int inLastBytes, long mediumCapacitySectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataSectors);
        if (inLastBytes is < 1 or > Rs03Constants.SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(inLastBytes));
        }
        if (mediumCapacitySectors < Rs03Constants.FieldMax)
        {
            throw new ArgumentOutOfRangeException(nameof(mediumCapacitySectors));
        }

        long sectorsPerLayer = mediumCapacitySectors / Rs03Constants.FieldMax;
        if (dataSectors >= mediumCapacitySectors)
        {
            throw new InvalidOperationException(
                $"Medium capacity ({mediumCapacitySectors:N0} sectors) is smaller than image ({dataSectors:N0} sectors).");
        }

        long payloadLayers = (dataSectors + Rs03Constants.EccHeaderSectors + sectorsPerLayer - 1) / sectorsPerLayer;
        if (payloadLayers < 84)
        {
            payloadLayers = 84;
        }

        int ndata = (int)payloadLayers + 1;
        int nroots = Rs03Constants.FieldMax - ndata;
        if (nroots < Rs03Constants.MinRoots)
        {
            throw new InvalidOperationException(
                $"Not enough space for RS03 parity on {mediumCapacitySectors:N0}-sector medium for {dataSectors:N0}-sector image (at least {Rs03Constants.MinRoots} parity roots required).");
        }

        long dataPadding = payloadLayers * sectorsPerLayer - dataSectors - Rs03Constants.EccHeaderSectors;
        long totalSectors = (long)Rs03Constants.FieldMax * sectorsPerLayer;
        long firstCrcPos = payloadLayers * sectorsPerLayer;
        long firstEccPos = firstCrcPos + sectorsPerLayer;
        double redundancy = nroots * 100.0 / ndata;

        return new Rs03Layout(
            Mode: Rs03Mode.AugmentedImage,
            DataSectors: dataSectors,
            InLastBytes: inLastBytes,
            DataPaddingSectors: dataPadding,
            SectorsPerLayer: sectorsPerLayer,
            TotalVirtualSectors: totalSectors,
            EccHeaderSector: dataSectors,
            FirstCrcSector: firstCrcPos,
            FirstEccSector: firstEccPos,
            DataLayerCount: ndata,
            ParityRootCount: nroots,
            RedundancyPercent: redundancy,
            MediumCapacitySectors: mediumCapacitySectors);
    }

    public static Rs03Layout FromHeader(Rs03EccHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.IsEccFile)
        {
            return ForEccFile(header.DataSectors, header.InLastBytes, header.EccBytes);
        }

        long mediumCap = header.SectorsPerLayer * Rs03Constants.FieldMax;
        return ForAugmentedImage(header.DataSectors, header.InLastBytes, mediumCap);
    }

    /// <summary>Converts a percentage (3.2 to 200.0) into a clamped RS03 parity root count [8..170].</summary>
    public static int RootsFromRedundancyPercent(double redundancyPercent)
    {
        double clamped = Math.Clamp(redundancyPercent, 3.2, 200.0);
        int roots = (int)Math.Round(Rs03Constants.FieldMax * clamped / (100.0 + clamped), MidpointRounding.AwayFromZero);
        return Math.Clamp(roots, Rs03Constants.MinRoots, Rs03Constants.MaxRoots);
    }
}

/// <summary>
/// Rewritable-media safety guard for RS03 augmented-image mode.
/// </summary>
public sealed record Rs03MediaGuardResult(
    bool IsAllowed,
    bool IsRewritableMedia,
    string? WarningMessage,
    string? BlockingReason);

public static class Rs03MediaGuard
{
    public const string RewritableAugmentedWarning =
        "RS03 augmented images append raw parity sectors past the ISO/UDF descriptor boundary, which can be truncated on rewritable media (DVD+RW, DVD-RAM, BD-RE, CD-RW) and displace the closing UDF anchor. Use standalone .ecc file mode for rewritable discs, or target write-once media (CD-R, DVD±R, BD-R).";

    public static Rs03MediaGuardResult Evaluate(
        DiscMediaType mediaType,
        Rs03Mode mode,
        bool allowRewritableAugmented = false)
    {
        if (mode == Rs03Mode.EccFile)
        {
            return new Rs03MediaGuardResult(IsAllowed: true, IsRewritableMedia: mediaType.IsRewritable(), WarningMessage: null, BlockingReason: null);
        }

        if (mediaType.IsRewritable())
        {
            return allowRewritableAugmented
                ? new Rs03MediaGuardResult(
                    IsAllowed: true,
                    IsRewritableMedia: true,
                    WarningMessage: RewritableAugmentedWarning,
                    BlockingReason: null)
                : new Rs03MediaGuardResult(
                    IsAllowed: false,
                    IsRewritableMedia: true,
                    WarningMessage: RewritableAugmentedWarning,
                    BlockingReason: RewritableAugmentedWarning);
        }

        return new Rs03MediaGuardResult(IsAllowed: true, IsRewritableMedia: false, WarningMessage: null, BlockingReason: null);
    }
}

public sealed record Rs03CreateOptions
{
    public Rs03Mode Mode { get; init; } = Rs03Mode.EccFile;
    public string? EccFilePath { get; init; }
    public int? ParityRoots { get; init; }
    public double? RedundancyPercent { get; init; }
    public DiscMediaType? MediaType { get; init; }
    public long? MediumCapacitySectors { get; init; }
    public bool NoBdrDefectManagement { get; init; }
    public bool AllowRewritableAugmented { get; init; }
    public bool Overwrite { get; init; }
}

public sealed record Rs03CreateResult(
    bool Success,
    Rs03Mode Mode,
    string ImagePath,
    string? EccFilePath,
    Rs03Layout Layout,
    byte[] MediumFingerprint,
    byte[] DataMd5,
    string? Warning,
    string Summary);

public sealed record Rs03VerifyOptions
{
    public bool QuickHeaderOnly { get; init; }
}

public sealed record Rs03VerifyResult(
    bool IsValid,
    Rs03Mode Mode,
    Rs03Layout Layout,
    bool HeaderReconstructed,
    Rs03FingerprintStatus FingerprintStatus,
    bool DataMd5Checked,
    bool DataMd5Matches,
    int MissingDataSectors,
    int CrcErrorSectors,
    int MissingCrcSectors,
    int CrcSignatureErrors,
    int MissingEccSectors,
    int SyndromeErrorColumns,
    int MaxErasuresInAnyColumn,
    bool IsRecoverable,
    string Summary);

public sealed record Rs03RepairOptions
{
    public IReadOnlyCollection<int>? UnreadableSectors { get; init; }
    public bool AllowFingerprintMismatch { get; init; }
    public bool VerifyAfterRepair { get; init; } = true;
}

public sealed record Rs03RepairResult(
    bool Success,
    Rs03Mode Mode,
    Rs03Layout Layout,
    bool HeaderReconstructed,
    int RepairedDataSectors,
    int RepairedCrcSectors,
    int RepairedEccSectors,
    int UnrepairedColumns,
    IReadOnlyList<int> UnrepairedSectors,
    bool VerifiedAfterRepair,
    string Summary,
    string? Warning = null);
