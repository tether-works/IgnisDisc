using System.Buffers.Binary;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// The BD Disc Information a drive returns for READ DISC STRUCTURE with media type 1 and format code 00h (MMC-6 6.22.3.3.1,
/// Tables 441 to 443): a 4-byte header (data length 4,098 on a full reply, two reserved bytes) followed by 112-byte DI
/// units. Within a unit: bytes 0-1 "DI", byte 2 the DI format, byte 3 the number of layers the unit applies to, bytes 8-10
/// the disc type ("BDR" for BD-R, "BDW" for BD-RE, "BDO" for BD-ROM), bytes 100-105 the disc manufacturer ID, 106-108 the
/// media type ID and 111 the product revision. The layer count is read from byte 3 as the table names it; it is
/// advisory, so the writer checks it against the capacity the drive reports instead of trusting it alone.
/// </summary>
public sealed record BdDiscInformation(string DiscType, int LayerCount, int UnitCount, string ManufacturerId, string MediaTypeId)
{
    /// <summary>Bytes before the first DI unit.</summary>
    public const int HeaderLength = 4;

    /// <summary>Bytes in one DI unit.</summary>
    public const int UnitLength = 112;

    /// <summary>Length of a full reply: header plus 4,096 bytes of DI units and emergency brake data.</summary>
    public const int FullReplyLength = 4100;

    /// <summary>The most layers any Blu-ray disc has (BDXL quad layer).</summary>
    public const int MaxLayers = 4;

    public bool IsRecordable => DiscType == "BDR";
    public bool IsRewritable => DiscType == "BDW";

    /// <summary>Parses a reply; null when no DI unit with the "DI" identifier is in it.</summary>
    public static BdDiscInformation? Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength + UnitLength)
        {
            return null;
        }
        // The length field counts what follows it; never read past what was actually transferred.
        int end = Math.Min(response.Length, HeaderLength - 2 + BinaryPrimitives.ReadUInt16BigEndian(response));
        string? type = null;
        string manufacturer = string.Empty;
        string mediaType = string.Empty;
        int layers = 0;
        int units = 0;
        for (int at = HeaderLength; at + UnitLength <= end; at += UnitLength)
        {
            var unit = response.Slice(at, UnitLength);
            if (unit[0] != (byte)'D' || unit[1] != (byte)'I')
            {
                continue;
            }
            units++;
            if (type is null)
            {
                type = Ascii(unit.Slice(8, 3));
                manufacturer = Ascii(unit.Slice(100, 6));
                mediaType = Ascii(unit.Slice(106, 3));
            }
            if (unit[3] is >= 1 and <= MaxLayers)
            {
                layers = Math.Max(layers, unit[3]);
            }
        }
        return type is null ? null : new BdDiscInformation(type, layers, units, manufacturer, mediaType);
    }

    /// <summary>
    /// The capacity split into one figure per layer, as 64-bit sectors. Blu-ray layers are equal in size (BD-R DL
    /// 2 x 12,219,392; BDXL 3 x 16,292,864 and 4 x 15,625,216 sectors), so each gets the same whole number of 32-sector
    /// clusters and the last takes any remainder.
    /// </summary>
    public static IReadOnlyList<long> SplitCapacity(long totalSectors, int layers, int clusterSectors)
    {
        if (layers < 1 || totalSectors <= 0 || clusterSectors < 1)
        {
            return Array.Empty<long>();
        }
        long each = totalSectors / layers / clusterSectors * clusterSectors;
        var result = new long[layers];
        for (int i = 0; i < layers; i++)
        {
            result[i] = each;
        }
        result[^1] += totalSectors - each * layers;
        return result;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : ' ';
        }
        return new string(chars).Trim();
    }
}
