using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Parsed standard SCSI INQUIRY response data.
/// </summary>
public sealed record MmcInquiryResponse(
    byte PeripheralDeviceType,
    bool IsRemovable,
    string VendorId,
    string ProductId,
    string ProductRevision,
    bool IsCdDvdDevice)
{
    public override string ToString() => $"{VendorId} {ProductId} (Rev {ProductRevision})";
}

/// <summary>
/// Descriptor representing a single track or lead-out entry within a READ TOC Format 0x00 response.
/// </summary>
public sealed record TocTrackDescriptor(byte TrackNumber, byte AdrControl, int StartLba)
{
    /// <summary>
    /// ADR nibble (bits 4..7) indicating the type of subchannel Q data.
    /// </summary>
    public byte Adr => (byte)((AdrControl >> 4) & 0x0F);

    /// <summary>
    /// Control nibble (bits 0..3) specifying track attributes (audio vs. data, pre-emphasis, copy permit).
    /// </summary>
    public byte Control => (byte)(AdrControl & 0x0F);

    /// <summary>
    /// True if bit 2 of Control is set (Data track: Mode 1, Mode 2, or DVD data).
    /// </summary>
    public bool IsDataTrack => (Control & 0x04) != 0;

    /// <summary>
    /// True if bit 2 of Control is 0 (Red Book CD-DA audio track).
    /// </summary>
    public bool IsAudioTrack => !IsDataTrack;

    /// <summary>
    /// True if this descriptor represents the Lead-Out track (TrackNumber == 0xAA).
    /// </summary>
    public bool IsLeadOut => TrackNumber == 0xAA;

    public override string ToString() =>
        $"Track {(IsLeadOut ? "AA (Lead-Out)" : TrackNumber.ToString("D2"))}: Start LBA={StartLba}, Control=0x{Control:X1} ({(IsDataTrack ? "Data" : "Audio")})";
}

/// <summary>
/// Full TOC response including track range and track descriptors.
/// </summary>
public sealed record TocResponse(
    byte FirstTrack,
    byte LastTrack,
    IReadOnlyList<TocTrackDescriptor> Tracks);

/// <summary>
/// Parsers for SCSI / MMC-5 command responses including INQUIRY and READ TOC.
/// </summary>
public static class MmcResponseParser
{
    /// <summary>
    /// Standard SCSI Peripheral Device Type code for CD/DVD optical devices (0x05).
    /// </summary>
    public const byte DeviceTypeCdDvd = 0x05;

    /// <summary>
    /// Parses standard SCSI INQUIRY response bytes.
    /// </summary>
    /// <param name="buffer">Byte buffer returned by INQUIRY command (minimum 36 bytes for full identification).</param>
    /// <returns>Structured <see cref="MmcInquiryResponse"/> containing vendor, product, and revision strings.</returns>
    public static MmcInquiryResponse ParseInquiry(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 36)
        {
            throw new ArgumentException("INQUIRY response buffer must be at least 36 bytes.", nameof(buffer));
        }

        byte devType = (byte)(buffer[0] & 0x1F);
        bool isRemovable = (buffer[1] & 0x80) != 0;

        string vendor = Encoding.ASCII.GetString(buffer.Slice(8, 8)).Trim(' ', '\0');
        string product = Encoding.ASCII.GetString(buffer.Slice(16, 16)).Trim(' ', '\0');
        string revision = Encoding.ASCII.GetString(buffer.Slice(32, 4)).Trim(' ', '\0');

        bool isCdDvd = devType == DeviceTypeCdDvd;

        return new MmcInquiryResponse(devType, isRemovable, vendor, product, revision, isCdDvd);
    }

    /// <summary>
    /// Parses READ TOC Format 0x00 response bytes into a list of <see cref="TocTrackDescriptor"/>.
    /// </summary>
    /// <param name="buffer">Byte buffer returned by READ TOC Format 0x00 command.</param>
    /// <param name="isMsf">True if addresses in buffer are encoded as MSF (MM:SS:FF) timecodes; false if 32-bit big-endian LBA.</param>
    /// <returns>List of track descriptors including regular tracks and Lead-Out (0xAA).</returns>
    public static IReadOnlyList<TocTrackDescriptor> ParseToc(ReadOnlySpan<byte> buffer, bool isMsf = false)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("TOC buffer must be at least 4 bytes (header length).", nameof(buffer));
        }

        ushort tocDataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));

        var tracks = new List<TocTrackDescriptor>();

        // If tocDataLength is populated (> 0), data ends at Math.Min(buffer.Length, tocDataLength + 2).
        // Otherwise, parse up to buffer.Length.
        int availableData = tocDataLength > 0 ? Math.Min(buffer.Length, tocDataLength + 2) : buffer.Length;

        // Descriptors begin at offset 4 and are 8 bytes each
        for (int offset = 4; offset + 8 <= availableData; offset += 8)
        {
            byte adrControl = buffer[offset + 1];
            byte trackNumber = buffer[offset + 2];

            int startLba;
            if (isMsf)
            {
                byte m = buffer[offset + 5];
                byte s = buffer[offset + 6];
                byte f = buffer[offset + 7];
                startLba = new MsfAddress(m, s, f).ToLba();
            }
            else
            {
                startLba = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(offset + 4, 4));
            }

            tracks.Add(new TocTrackDescriptor(trackNumber, adrControl, startLba));
        }

        return tracks;
    }

    /// <summary>
    /// Parses full READ TOC Format 0x00 response into <see cref="TocResponse"/> containing first/last track numbers and descriptors.
    /// </summary>
    public static TocResponse ParseTocResponse(ReadOnlySpan<byte> buffer, bool isMsf = false)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("TOC buffer must be at least 4 bytes.", nameof(buffer));
        }

        byte firstTrack = buffer[2];
        byte lastTrack = buffer[3];
        var tracks = ParseToc(buffer, isMsf);

        return new TocResponse(firstTrack, lastTrack, tracks);
    }

    /// <summary>
    /// Parses the 16-byte Formatted Q sub-channel block returned by READ CD (sub-channel selection 0x02).
    /// </summary>
    public static bool TryParseReadCdFormattedQ(ReadOnlySpan<byte> q16, bool bcdFields, out QSubChannelFrame frame)
    {
        frame = default;
        if (q16.Length < 10)
        {
            return false;
        }

        byte control = (byte)((q16[0] >> 4) & 0x0F);
        byte adr = (byte)(q16[0] & 0x0F);
        if (adr != 1)
        {
            frame = new QSubChannelFrame(adr, control, 0, 0, 0, 0, 0, 0, 0, 0);
            return true;
        }

        if (!TryDecodeField(q16[1], bcdFields, out int track) ||
            !TryDecodeField(q16[2], bcdFields, out int index) ||
            !TryDecodeField(q16[3], bcdFields, out int relM) ||
            !TryDecodeField(q16[4], bcdFields, out int relS) ||
            !TryDecodeField(q16[5], bcdFields, out int relF) ||
            !TryDecodeField(q16[7], bcdFields, out int absM) ||
            !TryDecodeField(q16[8], bcdFields, out int absS) ||
            !TryDecodeField(q16[9], bcdFields, out int absF))
        {
            return false;
        }

        frame = new QSubChannelFrame(adr, control, track, index, relM, relS, relF, absM, absS, absF);
        return true;
    }

    /// <summary>
    /// Parses a READ SUB-CHANNEL (0x42) Format 0x01 (Current Position) response buffer.
    /// </summary>
    public static bool TryParseSubChannelPosition(ReadOnlySpan<byte> response, bool msf, out QSubChannelFrame frame)
    {
        frame = default;
        if (response.Length < 16 || response[4] != MmcCommandBuilder.SubChannelFormatCurrentPosition)
        {
            return false;
        }

        byte adr = (byte)((response[5] >> 4) & 0x0F);
        byte control = (byte)(response[5] & 0x0F);
        int track = response[6];
        int index = response[7];

        if (msf)
        {
            frame = new QSubChannelFrame(
                adr,
                control,
                track,
                index,
                response[13],
                response[14],
                response[15],
                response[9],
                response[10],
                response[11]);
            return true;
        }

        int absLba = BinaryPrimitives.ReadInt32BigEndian(response.Slice(8, 4));
        int relLba = BinaryPrimitives.ReadInt32BigEndian(response.Slice(12, 4));
        if (absLba < -OpticalConstants.CdLeadInFrames)
        {
            return false;
        }

        MsfAddress abs = MsfAddress.FromLba(absLba);
        MsfAddress rel = MsfAddress.FromFrameCount(Math.Abs(relLba));
        frame = new QSubChannelFrame(
            adr,
            control,
            track,
            index,
            rel.Minutes,
            rel.Seconds,
            rel.Frames,
            abs.Minutes,
            abs.Seconds,
            abs.Frames);
        return true;
    }

    /// <summary>
    /// Parses a READ SUB-CHANNEL (0x42) Format 0x02 (Media Catalog Number) response buffer.
    /// </summary>
    public static bool TryParseSubChannelMediaCatalogNumber(ReadOnlySpan<byte> response, out string mcn)
    {
        mcn = string.Empty;
        if (response.Length < 22 ||
            response[4] != MmcCommandBuilder.SubChannelFormatMediaCatalogNumber ||
            (response[8] & 0x80) == 0)
        {
            return false;
        }

        Span<char> chars = stackalloc char[13];
        bool anyNonZero = false;
        for (int i = 0; i < 13; i++)
        {
            byte b = response[9 + i];
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
            if (b != (byte)'0')
            {
                anyNonZero = true;
            }
            chars[i] = (char)b;
        }

        if (!anyNonZero)
        {
            return false;
        }

        mcn = new string(chars);
        return true;
    }

    /// <summary>
    /// Parses a READ SUB-CHANNEL (0x42) Format 0x03 (Track ISRC) response buffer.
    /// </summary>
    public static bool TryParseSubChannelIsrc(ReadOnlySpan<byte> response, out string isrc)
    {
        isrc = string.Empty;
        if (response.Length < 21 ||
            response[4] != MmcCommandBuilder.SubChannelFormatTrackIsrc ||
            (response[8] & 0x80) == 0)
        {
            return false;
        }

        Span<char> chars = stackalloc char[12];
        for (int i = 0; i < 12; i++)
        {
            byte b = response[9 + i];
            char ch = char.ToUpperInvariant((char)b);
            bool valid = i switch
            {
                < 2 => ch is >= 'A' and <= 'Z',
                < 5 => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9'),
                _ => ch is >= '0' and <= '9'
            };
            if (!valid)
            {
                return false;
            }
            chars[i] = ch;
        }

        isrc = new string(chars);
        return true;
    }

    private static bool TryDecodeField(byte raw, bool bcd, out int value)
    {
        if (!bcd)
        {
            value = raw;
            return true;
        }

        int hi = (raw >> 4) & 0x0F;
        int lo = raw & 0x0F;
        if (hi > 9 || lo > 9)
        {
            value = 0;
            return false;
        }

        value = (hi * 10) + lo;
        return true;
    }
}
