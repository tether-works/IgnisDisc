using System.Buffers.Binary;
using CdBurner.Core.Verification;

namespace CdBurner.Hardware.Scsi.QualityScan;

/// <summary>
/// Clean-room SCSI CDB builders and reply parsers for MediaTek / Lite-On optical quality scanning
/// (<c>0xF3 0x0E</c> and legacy <c>0xDF</c> error-correction counter commands).
/// Command layouts and reply field offsets match QPxTool's <c>plugins/liteon/qscan_cmd.cpp</c>.
/// </summary>
public static class MediaTekQualityCommands
{
    public const byte OpCodeNewScan = 0xF3;
    public const byte SubCommandNewErrc = 0x0E;
    public const byte OpCodeLegacyScan = 0xDF;
    public const byte OpCodeSeek10 = 0x2B;
    public const byte OpCodeReadCapacity10 = 0x25;
    public const byte OpCodeSetCdSpeed = 0xBB;

    public const int NewScanReplyLength = 0x10;
    public const int LegacyScanReplyLength = 256;

    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    public static byte[] BuildSeek10Cdb(int lba)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeSeek10;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2, 4), (uint)Math.Max(0, lba));
        return cdb;
    }

    public static byte[] BuildReadCapacity10Cdb()
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReadCapacity10;
        return cdb;
    }

    public static byte[] BuildSetCdSpeedCdb(ushort readKbPerSec, ushort writeKbPerSec = 0xFFFF)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSetCdSpeed;
        cdb[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2, 2), readKbPerSec);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4, 2), writeKbPerSec);
        return cdb;
    }

    /// <summary>
    /// Builds the 12-byte <c>0xF3 0x0E</c> ERRC probe/poll CDB (<c>cdb[8] = 0x10</c> for DVD, <c>0x00</c> for CD/BD).
    /// </summary>
    public static byte[] BuildF3ErrcCdb(QualityScanMediaKind mediaKind)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeNewScan;
        cdb[1] = SubCommandNewErrc;
        cdb[8] = mediaKind == QualityScanMediaKind.Dvd ? (byte)0x10 : (byte)0x00;
        cdb[11] = 0x00;
        return cdb;
    }

    public static byte[] BuildDfInitACdb()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0xA3;
        return cdb;
    }

    public static byte[] BuildDfInitArmCdb(byte modeByte)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0xA0;
        cdb[4] = modeByte;
        return cdb;
    }

    public static byte[] BuildDfReadIntervalCdb()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0x82;
        cdb[2] = 0x09;
        return cdb;
    }

    public static byte[] BuildDfGetDataCdb()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0x82;
        cdb[2] = 0x05;
        return cdb;
    }

    public static byte[] BuildDfResetIntervalCdb()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0x97;
        return cdb;
    }

    public static byte[] BuildDfEndCdb()
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeLegacyScan;
        cdb[1] = 0xA3;
        cdb[2] = 0x01;
        return cdb;
    }

    /// <summary>
    /// Parses a 16-byte <c>0xF3 0x0E</c> reply into <c>(Lba, PrimaryError, SecondaryError)</c>.
    /// </summary>
    public static bool TryParseF3Reply(
        ReadOnlySpan<byte> buffer,
        QualityScanMediaKind mediaKind,
        out int lba,
        out int primaryError,
        out int secondaryError)
    {
        lba = 0;
        primaryError = 0;
        secondaryError = 0;

        if (buffer.Length < 8)
        {
            return false;
        }

        if (mediaKind == QualityScanMediaKind.Cd)
        {
            byte m = buffer[1];
            byte s = buffer[2];
            byte f = buffer[3];
            if (s >= 60 || f >= 75)
            {
                return false;
            }
            lba = (m * 60 * 75) + (s * 75) + f;
            primaryError = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(4, 2)); // BLER (C1)
            secondaryError = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(6, 2)); // E22 (C2)
            return true;
        }

        uint rawLba = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
        lba = (int)Math.Min(int.MaxValue, rawLba);
        primaryError = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(4, 2));
        secondaryError = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(6, 2));

        if (mediaKind == QualityScanMediaKind.BluRay && (primaryError > 9700 || secondaryError >= 768))
        {
            primaryError = 0;
            secondaryError = 0;
        }

        return true;
    }

    /// <summary>
    /// Parses a 256-byte <c>0xDF 0x82 0x05</c> reply into CD <c>(C1, C2, CU)</c>.
    /// </summary>
    public static bool TryParseDfCdCounters(
        ReadOnlySpan<byte> buffer,
        out int c1,
        out int c2,
        out int cu)
    {
        c1 = 0;
        c2 = 0;
        cu = 0;
        if (buffer.Length < 5)
        {
            return false;
        }

        c1 = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        c2 = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        cu = buffer[4];
        return true;
    }
}
