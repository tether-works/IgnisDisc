using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Format constants, finite-field parameters, and nominal medium capacities for RS03 raw-sector parity.
/// </summary>
public static class Rs03Constants
{
    public const int SectorSize = OpticalConstants.DataSectorSize; // 2048
    public const int EccHeaderBytes = 4096;
    public const int EccHeaderSectors = 2;

    public static ReadOnlySpan<byte> MagicCookie => "*dvdisaster*"u8;
    public static ReadOnlySpan<byte> MethodName => "RS03"u8;

    public const byte MethodFlagDataMd5 = 0x01;
    public const byte MethodFlagEccFile = 0x02;

    public const int FingerprintSector = 16;
    public const int CreatorVersion = 7910;
    public const int NeededVersion = 7900;
    public const uint SelfCrcSeed = 0x004C5047u;

    // GF(2^8) and CCSDS Reed-Solomon parameters.
    public const int FieldSize = 256;
    public const int FieldMax = 255;
    public const int AlphaZero = 255;
    public const int FieldGeneratorPoly = 0x187;
    public const int FirstConsecutiveRoot = 112;
    public const int PrimitiveElement = 11;
    public const int PrimthRoot = 116;

    public const int MinRoots = 8;
    public const int MaxRoots = 170;
    public const int DefaultNormalRoots = 32;
    public const int DefaultHighRoots = 64;

    // Standard medium capacities (2,048-byte sectors) used by RS03 augmented-image layouts.
    public const long CdRMediumSectors = 359_424;           // 351 * 1024
    public const long DvdSlMediumSectors = 2_295_104;
    public const long DvdDlMediumSectors = 4_171_712;
    public const long BdSlMediumSectors = 11_826_176;
    public const long BdSlNoDmMediumSectors = 12_219_392;
    public const long BdDlMediumSectors = 23_652_352;
    public const long BdDlNoDmMediumSectors = 24_438_784;
    public const long BdxlTlMediumSectors = 47_305_728;
    public const long BdxlTlNoDmMediumSectors = 48_878_592;
    public const long BdxlQlMediumSectors = 60_403_712;
    public const long BdxlQlNoDmMediumSectors = 62_500_864;

    public static readonly long[] StandardMediumCapacities =
    [
        CdRMediumSectors,
        DvdSlMediumSectors,
        DvdDlMediumSectors,
        BdSlMediumSectors,
        BdSlNoDmMediumSectors,
        BdDlMediumSectors,
        BdDlNoDmMediumSectors,
        BdxlTlMediumSectors,
        BdxlTlNoDmMediumSectors,
        BdxlQlMediumSectors,
        BdxlQlNoDmMediumSectors
    ];

    /// <summary>
    /// Resolves the RS03 target medium sector capacity for an augmented image from a <see cref="DiscMediaType"/>.
    /// </summary>
    public static long ResolveMediumCapacitySectors(DiscMediaType mediaType, bool noBdrDefectManagement = false) =>
        mediaType switch
        {
            DiscMediaType.CdRom or DiscMediaType.CdR or DiscMediaType.CdRw => CdRMediumSectors,
            DiscMediaType.DvdRom or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw
                or DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam => DvdSlMediumSectors,
            DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer => DvdDlMediumSectors,
            DiscMediaType.BdRom or DiscMediaType.BdR or DiscMediaType.BdRe =>
                noBdrDefectManagement ? BdSlNoDmMediumSectors : BdSlMediumSectors,
            DiscMediaType.BdRDualLayer or DiscMediaType.BdReDualLayer =>
                noBdrDefectManagement ? BdDlNoDmMediumSectors : BdDlMediumSectors,
            DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlReTripleLayer =>
                noBdrDefectManagement ? BdxlTlNoDmMediumSectors : BdxlTlMediumSectors,
            DiscMediaType.BdxlRQuadLayer =>
                noBdrDefectManagement ? BdxlQlNoDmMediumSectors : BdxlQlMediumSectors,
            _ => throw new ArgumentException($"Unsupported optical media type for RS03 augmented layout: {mediaType}.", nameof(mediaType))
        };

    /// <summary>
    /// Selects the smallest standard medium capacity that fits <paramref name="dataSectors"/> with at least
    /// <see cref="MinRoots"/> parity layers.
    /// </summary>
    public static long SelectSmallestMediumCapacity(long dataSectors, bool noBdrDefectManagement = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataSectors);

        ReadOnlySpan<long> candidates =
        [
            CdRMediumSectors,
            DvdSlMediumSectors,
            DvdDlMediumSectors,
            noBdrDefectManagement ? BdSlNoDmMediumSectors : BdSlMediumSectors,
            noBdrDefectManagement ? BdDlNoDmMediumSectors : BdDlMediumSectors,
            noBdrDefectManagement ? BdxlTlNoDmMediumSectors : BdxlTlMediumSectors,
            noBdrDefectManagement ? BdxlQlNoDmMediumSectors : BdxlQlMediumSectors
        ];

        foreach (long capacity in candidates)
        {
            long sectorsPerLayer = capacity / FieldMax;
            if (sectorsPerLayer <= 0)
            {
                continue;
            }
            long dataLayers = (dataSectors + EccHeaderSectors + sectorsPerLayer - 1) / sectorsPerLayer;
            int roots = FieldMax - (int)dataLayers - 1;
            if (roots >= MinRoots)
            {
                return capacity;
            }
        }

        throw new InvalidOperationException(
            $"Image ({dataSectors:N0} sectors) exceeds the largest standard RS03 medium capacity with {MinRoots} parity roots.");
    }
}
