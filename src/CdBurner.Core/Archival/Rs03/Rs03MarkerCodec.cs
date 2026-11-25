using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CdBurner.Core.Images;

namespace CdBurner.Core.Archival.Rs03;

/// <summary>
/// Binary serializer/deserializer for RS03 <c>EccHeader</c> (4,096 B), <c>CrcBlock</c> (2,048 B),
/// padding sectors, and dead-sector markers.
/// </summary>
public sealed class Rs03EccHeader
{
    public byte[] MethodFlags { get; init; } = new byte[4];
    public byte[] MediumFingerprint { get; init; } = new byte[16];
    public byte[] MediumSum { get; init; } = new byte[16];
    public byte[] EccSum { get; init; } = new byte[16];
    public byte[] CrcSum { get; init; } = new byte[16];
    public long DataSectors { get; init; }
    public int DataBytes { get; init; }
    public int EccBytes { get; init; }
    public int CreatorVersion { get; init; } = Rs03Constants.CreatorVersion;
    public int NeededVersion { get; init; } = Rs03Constants.NeededVersion;
    public int FingerprintSector { get; init; } = Rs03Constants.FingerprintSector;
    public uint SelfCrc { get; private set; }
    public int InLastBytes { get; init; } = Rs03Constants.SectorSize;
    public long SectorsPerLayer { get; init; }
    public long SectorsAddedByEcc { get; init; }

    public bool HasDataMd5 => (MethodFlags[0] & Rs03Constants.MethodFlagDataMd5) != 0;
    public bool IsEccFile => (MethodFlags[0] & Rs03Constants.MethodFlagEccFile) != 0;

    public byte[] ToBytes()
    {
        byte[] buf = new byte[Rs03Constants.EccHeaderBytes];
        Rs03Constants.MagicCookie.CopyTo(buf.AsSpan(0, 12));
        Rs03Constants.MethodName.CopyTo(buf.AsSpan(12, 4));
        MethodFlags.AsSpan(0, 4).CopyTo(buf.AsSpan(16, 4));
        MediumFingerprint.AsSpan(0, 16).CopyTo(buf.AsSpan(20, 16));
        MediumSum.AsSpan(0, 16).CopyTo(buf.AsSpan(36, 16));
        EccSum.AsSpan(0, 16).CopyTo(buf.AsSpan(52, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(68, 8), (ulong)DataSectors);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(76, 4), DataBytes);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(80, 4), EccBytes);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(84, 4), CreatorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(88, 4), NeededVersion);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(92, 4), FingerprintSector);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(96, 4), Rs03Constants.SelfCrcSeed);
        CrcSum.AsSpan(0, 16).CopyTo(buf.AsSpan(100, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(116, 4), InLastBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(120, 8), (ulong)SectorsPerLayer);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(128, 8), (ulong)SectorsAddedByEcc);

        uint crc = Crc32Algorithm.ComputeUncomplemented(buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(96, 4), crc);
        SelfCrc = crc;
        return buf;
    }

    public static bool TryParse(ReadOnlySpan<byte> buffer, out Rs03EccHeader? header)
    {
        header = null;
        if (buffer.Length < Rs03Constants.EccHeaderBytes)
        {
            return false;
        }

        if (!buffer.Slice(0, 12).SequenceEqual(Rs03Constants.MagicCookie) ||
            !buffer.Slice(12, 4).SequenceEqual(Rs03Constants.MethodName))
        {
            return false;
        }

        uint recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(96, 4));
        byte[] copy = buffer.Slice(0, Rs03Constants.EccHeaderBytes).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(96, 4), Rs03Constants.SelfCrcSeed);
        uint computedCrc = Crc32Algorithm.ComputeUncomplemented(copy);
        if (computedCrc != recordedCrc)
        {
            return false;
        }

        ulong rawSectors = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(68, 8));
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(76, 4));
        int eccBytes = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(80, 4));
        int creatorVersion = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(84, 4));
        int neededVersion = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(88, 4));
        int fpSector = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(92, 4));
        int inLast = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(116, 4));
        ulong spl = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(120, 8));
        ulong added = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(128, 8));

        if (!IsPlausible(copy[16], rawSectors, dataBytes, eccBytes, inLast, spl))
        {
            return false;
        }

        header = new Rs03EccHeader
        {
            MethodFlags = copy.AsSpan(16, 4).ToArray(),
            MediumFingerprint = copy.AsSpan(20, 16).ToArray(),
            MediumSum = copy.AsSpan(36, 16).ToArray(),
            EccSum = copy.AsSpan(52, 16).ToArray(),
            CrcSum = copy.AsSpan(100, 16).ToArray(),
            DataSectors = (long)rawSectors,
            DataBytes = dataBytes,
            EccBytes = eccBytes,
            CreatorVersion = creatorVersion,
            NeededVersion = neededVersion,
            FingerprintSector = fpSector,
            SelfCrc = recordedCrc,
            InLastBytes = inLast,
            SectorsPerLayer = (long)spl,
            SectorsAddedByEcc = (long)added
        };
        return true;
    }

    internal static bool IsPlausible(
        byte methodFlag0,
        ulong rawDataSectors,
        int dataBytes,
        int eccBytes,
        int inLast,
        ulong sectorsPerLayer)
    {
        if (rawDataSectors == 0 || rawDataSectors > int.MaxValue)
        {
            return false;
        }
        if (inLast is < 1 or > Rs03Constants.SectorSize)
        {
            return false;
        }
        if (eccBytes is < Rs03Constants.MinRoots or > Rs03Constants.MaxRoots)
        {
            return false;
        }
        if (dataBytes != Rs03Constants.FieldMax - eccBytes)
        {
            return false;
        }

        bool isEccFile = (methodFlag0 & Rs03Constants.MethodFlagEccFile) != 0;
        if (isEccFile)
        {
            ulong expectedSpl = (rawDataSectors + (ulong)(Rs03Constants.FieldMax - eccBytes - 2)) / (ulong)(Rs03Constants.FieldMax - eccBytes - 1);
            if (sectorsPerLayer != expectedSpl)
            {
                return false;
            }
        }
        else
        {
            if (sectorsPerLayer == 0 || sectorsPerLayer > int.MaxValue / (ulong)Rs03Constants.FieldMax)
            {
                return false;
            }
            if (rawDataSectors + Rs03Constants.EccHeaderSectors > (ulong)(Rs03Constants.FieldMax - 9) * sectorsPerLayer)
            {
                return false;
            }
        }

        return sectorsPerLayer > 0 && sectorsPerLayer <= int.MaxValue / (ulong)Rs03Constants.FieldMax;
    }
}

public sealed class Rs03CrcBlock
{
    public uint[] Crc { get; init; } = new uint[256];
    public byte[] MethodFlags { get; init; } = new byte[4];
    public int CreatorVersion { get; init; } = Rs03Constants.CreatorVersion;
    public int NeededVersion { get; init; } = Rs03Constants.NeededVersion;
    public int FingerprintSector { get; init; } = Rs03Constants.FingerprintSector;
    public byte[] MediumFingerprint { get; init; } = new byte[16];
    public byte[] MediumSum { get; init; } = new byte[16];
    public long DataSectors { get; init; }
    public int InLastBytes { get; init; } = Rs03Constants.SectorSize;
    public int DataBytes { get; init; }
    public int EccBytes { get; init; }
    public long SectorsPerLayer { get; init; }
    public uint SelfCrc { get; private set; }

    public bool IsEccFile => (MethodFlags[0] & Rs03Constants.MethodFlagEccFile) != 0;

    public void WriteTo(Span<byte> sectorBuffer)
    {
        if (sectorBuffer.Length < Rs03Constants.SectorSize)
        {
            throw new ArgumentException("CRC block buffer must be at least 2,048 bytes.", nameof(sectorBuffer));
        }

        sectorBuffer.Slice(0, Rs03Constants.SectorSize).Clear();
        for (int i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sectorBuffer.Slice(i * 4, 4), Crc[i]);
        }

        // Fixed offsets matching C struct _CrcBlock with 8-byte alignment at SectorsPerLayer (offset 1112).
        Rs03Constants.MagicCookie.CopyTo(sectorBuffer.Slice(1024, 12));
        Rs03Constants.MethodName.CopyTo(sectorBuffer.Slice(1036, 4));
        MethodFlags.AsSpan(0, 4).CopyTo(sectorBuffer.Slice(1040, 4));
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1044, 4), CreatorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1048, 4), NeededVersion);
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1052, 4), FingerprintSector);
        MediumFingerprint.AsSpan(0, 16).CopyTo(sectorBuffer.Slice(1056, 16));
        MediumSum.AsSpan(0, 16).CopyTo(sectorBuffer.Slice(1072, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(sectorBuffer.Slice(1088, 8), (ulong)DataSectors);
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1096, 4), InLastBytes);
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1100, 4), DataBytes);
        BinaryPrimitives.WriteInt32LittleEndian(sectorBuffer.Slice(1104, 4), EccBytes);
        // Bytes 1108..1111 are 4-byte alignment padding before aligned uint64 sectorsPerLayer.
        BinaryPrimitives.WriteUInt64LittleEndian(sectorBuffer.Slice(1112, 8), (ulong)SectorsPerLayer);
        BinaryPrimitives.WriteUInt32LittleEndian(sectorBuffer.Slice(1120, 4), Rs03Constants.SelfCrcSeed);

        uint crc = Crc32Algorithm.ComputeUncomplemented(sectorBuffer.Slice(0, Rs03Constants.SectorSize));
        BinaryPrimitives.WriteUInt32LittleEndian(sectorBuffer.Slice(1120, 4), crc);
        SelfCrc = crc;
    }

    public static bool TryParse(ReadOnlySpan<byte> sectorBuffer, out Rs03CrcBlock? block)
    {
        block = null;
        if (sectorBuffer.Length < Rs03Constants.SectorSize)
        {
            return false;
        }

        if (!sectorBuffer.Slice(1024, 12).SequenceEqual(Rs03Constants.MagicCookie) ||
            !sectorBuffer.Slice(1036, 4).SequenceEqual(Rs03Constants.MethodName))
        {
            return false;
        }

        uint recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuffer.Slice(1120, 4));
        byte[] copy = sectorBuffer.Slice(0, Rs03Constants.SectorSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(1120, 4), Rs03Constants.SelfCrcSeed);
        uint computedCrc = Crc32Algorithm.ComputeUncomplemented(copy);
        if (computedCrc != recordedCrc)
        {
            return false;
        }

        ulong dataSectors = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(1088, 8));
        int inLast = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1096, 4));
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1100, 4));
        int eccBytes = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1104, 4));
        ulong spl = BinaryPrimitives.ReadUInt64LittleEndian(copy.AsSpan(1112, 8));

        if (!Rs03EccHeader.IsPlausible(copy[1040], dataSectors, dataBytes, eccBytes, inLast, spl))
        {
            return false;
        }

        uint[] crcs = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            crcs[i] = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(i * 4, 4));
        }

        block = new Rs03CrcBlock
        {
            Crc = crcs,
            MethodFlags = copy.AsSpan(1040, 4).ToArray(),
            CreatorVersion = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1044, 4)),
            NeededVersion = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1048, 4)),
            FingerprintSector = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(1052, 4)),
            MediumFingerprint = copy.AsSpan(1056, 16).ToArray(),
            MediumSum = copy.AsSpan(1072, 16).ToArray(),
            DataSectors = (long)dataSectors,
            InLastBytes = inLast,
            DataBytes = dataBytes,
            EccBytes = eccBytes,
            SectorsPerLayer = (long)spl,
            SelfCrc = recordedCrc
        };
        return true;
    }

    public Rs03EccHeader ReconstructHeader()
    {
        var header = new Rs03EccHeader
        {
            MethodFlags = (byte[])MethodFlags.Clone(),
            MediumFingerprint = (byte[])MediumFingerprint.Clone(),
            MediumSum = (byte[])MediumSum.Clone(),
            DataSectors = DataSectors,
            DataBytes = DataBytes,
            EccBytes = EccBytes,
            CreatorVersion = CreatorVersion,
            NeededVersion = NeededVersion,
            FingerprintSector = FingerprintSector,
            InLastBytes = InLastBytes,
            SectorsPerLayer = SectorsPerLayer,
            SectorsAddedByEcc = 0
        };
        _ = header.ToBytes(); // computes SelfCrc
        return header;
    }
}

/// <summary>
/// Builds and detects RS03 padding sectors and dead-sector markers.
/// </summary>
public static class Rs03MarkerCodec
{
    private const string MarkerVersion = "1.00";
    private static readonly byte[] DeadPrefix = Encoding.ASCII.GetBytes(
        "dvdisaster dead sector marker\n" +
        "This sector could not be read from the image.\n" +
        "Its contents have been substituted by the dvdisaster read routine.\n");
    private static readonly byte[] DeadEndMarker = Encoding.ASCII.GetBytes("dvdisaster dead sector end marker\n");

    private static readonly byte[] PaddingBody = Encoding.ASCII.GetBytes(
        "dvdisaster padding sector       " +
        "This is a padding sector needed for augmenting the image " +
        "with error correction data.");
    private static readonly byte[] PaddingEndMarker = Encoding.ASCII.GetBytes("dvdisaster padding sector end marker");

    public static void WritePaddingSector(Span<byte> buffer, long sectorNumber, ReadOnlySpan<byte> mediumFingerprint, int fingerprintSector)
    {
        if (buffer.Length < Rs03Constants.SectorSize)
        {
            throw new ArgumentException("Sector buffer must be at least 2,048 bytes.", nameof(buffer));
        }

        Span<byte> sector = buffer.Slice(0, Rs03Constants.SectorSize);
        sector.Clear();

        PaddingBody.CopyTo(sector);
        PaddingEndMarker.CopyTo(sector.Slice(2047 - PaddingEndMarker.Length));

        WriteAsciiZ(sector.Slice(0x100), "Padding sector marker version");
        WriteAsciiZ(sector.Slice(0x120), MarkerVersion);
        WriteAsciiZ(sector.Slice(0x140), "Padding sector number");
        WriteAsciiZ(sector.Slice(0x160), sectorNumber.ToString(CultureInfo.InvariantCulture));
        WriteAsciiZ(sector.Slice(0x180), "Medium fingerprint");
        if (mediumFingerprint.Length >= 16)
        {
            mediumFingerprint.Slice(0, 16).CopyTo(sector.Slice(0x1A0, 16));
        }
        else
        {
            Encoding.ASCII.GetBytes("none").CopyTo(sector.Slice(0x1B0, 4));
        }
        WriteAsciiZ(sector.Slice(0x1C0), "Medium fingerprint sector");
        WriteAsciiZ(sector.Slice(0x1E0), fingerprintSector.ToString(CultureInfo.InvariantCulture));
    }

    public static void WriteDeadSector(
        Span<byte> buffer,
        long sectorNumber,
        ReadOnlySpan<byte> mediumFingerprint,
        int fingerprintSector,
        string? volumeLabel = null)
    {
        if (buffer.Length < Rs03Constants.SectorSize)
        {
            throw new ArgumentException("Sector buffer must be at least 2,048 bytes.", nameof(buffer));
        }

        Span<byte> sector = buffer.Slice(0, Rs03Constants.SectorSize);
        sector.Clear();

        DeadPrefix.CopyTo(sector);
        DeadEndMarker.CopyTo(sector.Slice(2046 - DeadEndMarker.Length));

        WriteAsciiZ(sector.Slice(0x100), "Dead sector marker version");
        WriteAsciiZ(sector.Slice(0x120), MarkerVersion);
        WriteAsciiZ(sector.Slice(0x140), "Dead sector number");
        WriteAsciiZ(sector.Slice(0x160), sectorNumber.ToString(CultureInfo.InvariantCulture));
        WriteAsciiZ(sector.Slice(0x180), "Medium fingerprint");
        if (mediumFingerprint.Length >= 16)
        {
            mediumFingerprint.Slice(0, 16).CopyTo(sector.Slice(0x1A0, 16));
        }
        else
        {
            Encoding.ASCII.GetBytes("none").CopyTo(sector.Slice(0x1B0, 4));
        }
        WriteAsciiZ(sector.Slice(0x1C0), "Medium fingerprint sector");
        WriteAsciiZ(sector.Slice(0x1E0), fingerprintSector.ToString(CultureInfo.InvariantCulture));
        WriteAsciiZ(sector.Slice(0x200), "Volume label (if any)");
        WriteAsciiZ(sector.Slice(0x220), string.IsNullOrEmpty(volumeLabel) ? "none" : volumeLabel);
    }

    public static bool IsDeadSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < Rs03Constants.SectorSize)
        {
            return false;
        }

        return sector.Slice(0, DeadPrefix.Length).SequenceEqual(DeadPrefix) &&
               sector.Slice(2046 - DeadEndMarker.Length, DeadEndMarker.Length).SequenceEqual(DeadEndMarker);
    }

    private static void WriteAsciiZ(Span<byte> dest, string value)
    {
        int written = Encoding.ASCII.GetBytes(value, dest);
        if (written < dest.Length)
        {
            dest[written] = 0;
        }
    }
}
