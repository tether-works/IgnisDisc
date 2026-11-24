using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Utilities;

/// <summary>
/// DVD Book Type specifications according to MMC-5 and DVD+R / DVD-ROM physical format specifications.
/// </summary>
public enum DvdBookType : byte
{
    /// <summary>
    /// DVD-ROM format (0x00) - Maximum legacy compatibility with standalone DVD players and game consoles.
    /// </summary>
    DvdRom = 0x00,

    /// <summary>
    /// DVD-RAM format (0x01).
    /// </summary>
    DvdRam = 0x01,

    /// <summary>
    /// DVD-R (recordable) format (0x02).
    /// </summary>
    DvdMinusR = 0x02,

    /// <summary>
    /// DVD-RW (rewritable) format (0x03).
    /// </summary>
    DvdMinusRw = 0x03,

    /// <summary>
    /// DVD+RW (rewritable) format (0x09).
    /// </summary>
    DvdPlusRw = 0x09,

    /// <summary>
    /// DVD+R (recordable single layer) format (0x0A).
    /// </summary>
    DvdPlusR = 0x0A,

    /// <summary>
    /// DVD+R DL (recordable dual layer) format (0x0E).
    /// </summary>
    DvdPlusRDualLayer = 0x0E
}

/// <summary>
/// Interface for mediums or optical drive targets that support reading and setting DVD Book Type.
/// </summary>
public interface IBookTypeTarget
{
    /// <summary>
    /// Physical optical medium classification.
    /// </summary>
    DiscMediaType MediaType { get; }

    /// <summary>
    /// The currently applied DVD Book Type.
    /// </summary>
    DvdBookType BookType { get; set; }
}

/// <summary>
/// Vendor bitsetting protocol family matched by <see cref="BookTypeDriveAllowList"/>.
/// </summary>
public enum BookTypeDriveFamily
{
    Unknown = 0,
    StandardMmc = 1,
    LiteOnMediaTek = 2,
    LgHlDtSt = 3
}

/// <summary>Allow-list entry matching INQUIRY Vendor ID and Product ID prefix for vendor Book Type commands.</summary>
public sealed record BookTypeAllowListEntry(
    BookTypeDriveFamily Family,
    string VendorId,
    string ProductPrefix,
    string Notes);

/// <summary>Matched allow-list result for an optical drive's SCSI INQUIRY identity.</summary>
public sealed record BookTypeAllowListMatch(
    BookTypeDriveFamily Family,
    string VendorId,
    string ProductId,
    string MatchedPrefix);

/// <summary>
/// Strict INQUIRY allow-list gating vendor-specific DVD+R / DVD+R DL Book Type bitsetting CDBs.
/// Unlisted drives must never receive vendor-specific opcodes (0xDF, 0xFA, 0xFC).
/// </summary>
public static class BookTypeDriveAllowList
{
    public static IReadOnlyList<BookTypeAllowListEntry> Entries { get; } =
    [
        // Lite-On / MediaTek (0xDF 0x0F unit bitsetting)
        new(BookTypeDriveFamily.LiteOnMediaTek, "ATAPI", "iHAS", "Lite-On SATA DVD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "ATAPI", "iHBS", "Lite-On SATA BD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "iHAS", "Lite-On DVD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "iHBS", "Lite-On BD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "LH-", "Lite-On LH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW LH-", "Lite-On LH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DH-", "Lite-On DH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW DH-", "Lite-On DH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "SOHW-", "Lite-On SOHW-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW SOHW-", "Lite-On SOHW-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVD+RW SOHW-", "Lite-On SOHW-series DVD+RW writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "SHW-", "Lite-On SHW-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW SHW-", "Lite-On SHW-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "SHM-", "Lite-On SHM-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW SHM-", "Lite-On SHM-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "SH-", "Lite-On SH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW SH-", "Lite-On SH-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "DVDRW LDW-", "Lite-On LDW-series DVD writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITE-ON", "LTR-", "Lite-On LTR-series writers"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITEON", "iHAS", "Lite-On DVD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "LITEON", "iHBS", "Lite-On BD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "PLDS", "iHAS", "PLDS/Lite-On DVD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "PLDS", "iHBS", "PLDS/Lite-On BD writers (MediaTek)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "ASUS", "DRW-24B", "ASUS DRW-24B* (MediaTek OEM)"),
        new(BookTypeDriveFamily.LiteOnMediaTek, "ASUS", "DRW-2014", "ASUS DRW-2014* (MediaTek OEM)"),

        // LG / Hitachi-LG Data Storage (0xFA query / 0xFC +RTB write)
        new(BookTypeDriveFamily.LgHlDtSt, "HL-DT-ST", "DVDRAM ", "Hitachi-LG DVD-RAM/DVD+R writers"),
        new(BookTypeDriveFamily.LgHlDtSt, "HL-DT-ST", "DVD-RAM ", "Hitachi-LG DVD-RAM/DVD+R writers"),
        new(BookTypeDriveFamily.LgHlDtSt, "HL-DT-ST", "BD-RE ", "Hitachi-LG BD-RE/DVD+R writers"),
        new(BookTypeDriveFamily.LgHlDtSt, "HL-DT-ST", "BD-RW ", "Hitachi-LG BD-RW/DVD+R writers"),
        new(BookTypeDriveFamily.LgHlDtSt, "LG", "DVDRAM ", "LG DVD-RAM/DVD+R writers"),
        new(BookTypeDriveFamily.LgHlDtSt, "LG", "BD-RE ", "LG BD-RE/DVD+R writers")
    ];

    /// <summary>
    /// Evaluates trimmed INQUIRY <paramref name="vendorId"/> and <paramref name="productId"/> against the allow-list.
    /// Returns null when the drive is not explicitly allow-listed.
    /// </summary>
    public static BookTypeAllowListMatch? Evaluate(string? vendorId, string? productId)
    {
        string vendor = NormalizeInquiryField(vendorId);
        string product = NormalizeInquiryField(productId);
        if (vendor.Length == 0 || product.Length == 0)
        {
            return null;
        }

        foreach (BookTypeAllowListEntry entry in Entries)
        {
            if (string.Equals(vendor, entry.VendorId, StringComparison.OrdinalIgnoreCase) &&
                product.StartsWith(entry.ProductPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new BookTypeAllowListMatch(entry.Family, vendor, product, entry.ProductPrefix);
            }
        }

        return null;
    }

    public static bool IsSupported(string? vendorId, string? productId) => Evaluate(vendorId, productId) != null;

    private static string NormalizeInquiryField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim(' ', '\0', '\t', '\r', '\n');
}

/// <summary>Result of querying the current media and drive Book Type state.</summary>
public sealed record BookTypeQueryResult(
    bool Success,
    DvdBookType? MediaBookType,
    byte? PartVersion,
    DvdBookType? DriveSettingBookType,
    BookTypeDriveFamily DriveFamily,
    bool IsAllowListed,
    bool BitsettingSupportedOnMedia,
    string? DiagnosticMessage);

/// <summary>Outcome of an opt-in or required pre-burn Book Type step.</summary>
public sealed record PreBurnBookTypeOutcome(
    bool ShouldProceed,
    bool Applied,
    string? Note,
    string? ErrorMessage,
    bool IsWarning);

/// <summary>
/// Hardware controller abstraction implemented in <c>CdBurner.Hardware</c> for querying and setting DVD Book Type
/// over SCSI MMC and allow-listed vendor hooks.
/// </summary>
public interface IBookTypeController
{
    BookTypeQueryResult QueryBookType(IDiscRecorderBackend recorder);
    BurnOperationResult SetBookType(IDiscRecorderBackend recorder, DvdBookType targetBookType);
}

/// <summary>
/// Service for inspecting and modifying DVD Book Type bitsetting (e.g. DVD+R / DVD+RW / DVD+R DL to DVD-ROM)
/// and constructing MMC SEND DISC STRUCTURE (0xBF) SCSI command payloads.
/// </summary>
public class BookTypeService
{
    private readonly IBookTypeController? _hardwareController;

    public BookTypeService(IBookTypeController? hardwareController = null)
    {
        _hardwareController = hardwareController;
    }

    /// <summary>
    /// SCSI / MMC-5 Operation Code for READ DISC STRUCTURE (0xAD).
    /// </summary>
    public const byte OpCodeReadDiscStructure = 0xAD;

    /// <summary>
    /// SCSI / MMC-5 Operation Code for SEND DISC STRUCTURE (0xBF).
    /// </summary>
    public const byte OpCodeSendDiscStructure = 0xBF;

    /// <summary>
    /// Format code for DVD Physical Format Information (0x00).
    /// </summary>
    public const byte FormatCodePhysicalFormatInformation = 0x00;

    /// <summary>
    /// Checks whether the specified physical medium type supports Book Type modification (bitsetting).
    /// Only DVD+R, DVD+RW, and DVD+R DL media support setting Book Type to DVD-ROM.
    /// </summary>
    public static bool IsBitsettingSupported(DiscMediaType mediaType) =>
        mediaType is DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or DiscMediaType.DvdPlusRDualLayer;

    /// <summary>
    /// Returns the standard default <see cref="DvdBookType"/> corresponding to the specified physical media type.
    /// </summary>
    public static DvdBookType GetDefaultBookType(DiscMediaType mediaType) => mediaType switch
    {
        DiscMediaType.DvdRom => DvdBookType.DvdRom,
        DiscMediaType.DvdRam => DvdBookType.DvdRam,
        DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRDualLayer => DvdBookType.DvdMinusR,
        DiscMediaType.DvdMinusRw => DvdBookType.DvdMinusRw,
        DiscMediaType.DvdPlusRw => DvdBookType.DvdPlusRw,
        DiscMediaType.DvdPlusR => DvdBookType.DvdPlusR,
        DiscMediaType.DvdPlusRDualLayer => DvdBookType.DvdPlusRDualLayer,
        _ => DvdBookType.DvdRom
    };

    /// <summary>
    /// Builds a 12-byte SCSI MMC SEND DISC STRUCTURE Command Descriptor Block (CDB) (OpCode 0xBF).
    /// </summary>
    /// <param name="formatCode">Structure format code (0x00 = Physical Format Information).</param>
    /// <param name="structureDataLength">Total length of the parameter list including the 4-byte header.</param>
    /// <returns>A 12-byte byte array containing the formatted CDB.</returns>
    public static byte[] BuildSendDiscStructureCdb(byte formatCode = FormatCodePhysicalFormatInformation, ushort structureDataLength = 16)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSendDiscStructure; // 0xBF
        cdb[1] = 0x00;
        cdb[2] = 0x00;
        cdb[3] = 0x00;
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = 0x00;
        cdb[7] = formatCode;
        cdb[8] = (byte)(structureDataLength >> 8);
        cdb[9] = (byte)(structureDataLength & 0xFF);
        cdb[10] = 0x00;
        cdb[11] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 4-byte MMC SEND DISC STRUCTURE parameter list header.
    /// </summary>
    /// <param name="dataLength">Length in bytes of the following structure data (big-endian).</param>
    /// <returns>A 4-byte byte array containing the structure header.</returns>
    public static byte[] BuildStructureHeader(ushort dataLength = 16)
    {
        return new byte[]
        {
            (byte)(dataLength >> 8),
            (byte)(dataLength & 0xFF),
            0x00,
            0x00
        };
    }

    /// <summary>
    /// Builds the complete MMC SEND DISC STRUCTURE parameter list (4-byte header + structure data)
    /// configured to change the Book Type of the media.
    /// </summary>
    /// <param name="bookType">The target DVD Book Type (e.g. <see cref="DvdBookType.DvdRom"/>).</param>
    /// <param name="dataLength">Length of structure data payload (default 16 bytes).</param>
    /// <returns>Byte array containing 4-byte header and data payload.</returns>
    public static byte[] BuildBookTypePayload(DvdBookType bookType = DvdBookType.DvdRom, ushort dataLength = 16)
    {
        if (dataLength < 1 || dataLength > ushort.MaxValue - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength), "Structure data length must be between 1 and 65531 bytes.");
        }

        byte[] payload = new byte[4 + dataLength];
        payload[0] = (byte)(dataLength >> 8);
        payload[1] = (byte)(dataLength & 0xFF);
        payload[2] = 0x00;
        payload[3] = 0x00;

        // Byte 4 is byte 0 of Physical Format Information:
        // Bits 7..4: Book Type
        // Bits 3..0: Part Version (default 0x01)
        payload[4] = (byte)(((byte)bookType << 4) | 0x01);

        return payload;
    }

    /// <summary>
    /// Builds both the 12-byte 0xBF CDB and the full parameter list payload (4-byte header + structure data encoding
    /// <paramref name="bookType"/>) for setting the DVD Book Type.
    /// </summary>
    public static (byte[] Cdb, byte[] StructureHeader) BuildBookTypeCommand(
        DvdBookType bookType = DvdBookType.DvdRom,
        byte formatCode = FormatCodePhysicalFormatInformation,
        ushort dataLength = 16)
    {
        if (dataLength < 1 || dataLength > ushort.MaxValue - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength), "Structure data length must be between 1 and 65531 bytes.");
        }

        byte[] payload = BuildBookTypePayload(bookType, dataLength);
        byte[] cdb = BuildSendDiscStructureCdb(formatCode, (ushort)payload.Length);
        return (cdb, payload);
    }

    /// <summary>
    /// Creates both the 12-byte 0xBF CDB and full parameter list payload for setting the DVD Book Type.
    /// </summary>
    public static (byte[] Cdb, byte[] Payload) CreateSetBookTypeCommand(
        DvdBookType bookType = DvdBookType.DvdRom,
        byte formatCode = FormatCodePhysicalFormatInformation,
        ushort dataLength = 16)
    {
        if (dataLength < 1 || dataLength > ushort.MaxValue - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(dataLength), "Structure data length must be between 1 and 65531 bytes.");
        }

        byte[] payload = BuildBookTypePayload(bookType, dataLength);
        byte[] cdb = BuildSendDiscStructureCdb(formatCode, (ushort)payload.Length);
        return (cdb, payload);
    }

    /// <summary>
    /// Instance helper to build a 12-byte SEND DISC STRUCTURE CDB.
    /// </summary>
    public byte[] BuildCdb(byte formatCode = FormatCodePhysicalFormatInformation, ushort structureDataLength = 16)
        => BuildSendDiscStructureCdb(formatCode, structureDataLength);

    /// <summary>
    /// Instance helper to build a 4-byte structure header.
    /// </summary>
    public byte[] BuildHeader(ushort dataLength = 16)
        => BuildStructureHeader(dataLength);

    /// <summary>
    /// Instance helper to build a full Book Type parameter list payload.
    /// </summary>
    public byte[] BuildPayload(DvdBookType bookType = DvdBookType.DvdRom, ushort dataLength = 16)
        => BuildBookTypePayload(bookType, dataLength);

    /// <summary>
    /// Parses an MMC-5 READ DISC STRUCTURE (0xAD) format 0x00 (DVD Physical Format Information) response buffer.
    /// Byte 4 bits 7..4 hold the Book Type nibble; bits 3..0 hold the Part Version.
    /// </summary>
    public static bool TryParsePhysicalFormatBookType(
        ReadOnlySpan<byte> response,
        out DvdBookType bookType,
        out byte partVersion)
    {
        bookType = default;
        partVersion = 0;

        if (response.Length < 5)
        {
            return false;
        }

        ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(0, 2));
        if (dataLength < 3)
        {
            return false;
        }

        byte pfi0 = response[4];
        byte bookNibble = (byte)((pfi0 >> 4) & 0x0F);
        partVersion = (byte)(pfi0 & 0x0F);
        if (!Enum.IsDefined(typeof(DvdBookType), bookNibble))
        {
            return false;
        }

        bookType = (DvdBookType)bookNibble;
        return true;
    }

    /// <summary>
    /// Queries the current DVD Book Type of the specified medium or target.
    /// </summary>
    public DvdBookType GetBookType(IBookTypeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.BookType;
    }

    /// <summary>
    /// Queries the current DVD Book Type and drive bitsetting state for the specified recorder.
    /// </summary>
    public BookTypeQueryResult QueryBookType(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            return new BookTypeQueryResult(
                Success: false,
                MediaBookType: null,
                PartVersion: null,
                DriveSettingBookType: null,
                DriveFamily: BookTypeDriveFamily.Unknown,
                IsAllowListed: false,
                BitsettingSupportedOnMedia: false,
                DiagnosticMessage: "No optical media present in recorder.");
        }

        bool supportedOnMedia = IsBitsettingSupported(status.MediaType);
        if (recorder.IsVirtual)
        {
            DvdBookType virtualType = ReadVirtualBookType(recorder, status);
            return new BookTypeQueryResult(
                Success: true,
                MediaBookType: virtualType,
                PartVersion: 1,
                DriveSettingBookType: virtualType,
                DriveFamily: BookTypeDriveFamily.StandardMmc,
                IsAllowListed: true,
                BitsettingSupportedOnMedia: supportedOnMedia,
                DiagnosticMessage: supportedOnMedia
                    ? $"Virtual simulator: Book Type {virtualType} (0x{(byte)virtualType:X2})."
                    : $"Virtual simulator: {status.MediaType.GetDisplayName()} (Book Type {virtualType}; bitsetting applies only to DVD+R, DVD+RW, and DVD+R DL).");
        }

        if (_hardwareController != null)
        {
            return _hardwareController.QueryBookType(recorder);
        }

        DvdBookType fallback = status.BookType ?? GetDefaultBookType(status.MediaType);
        return new BookTypeQueryResult(
            Success: true,
            MediaBookType: fallback,
            PartVersion: null,
            DriveSettingBookType: null,
            DriveFamily: BookTypeDriveFamily.Unknown,
            IsAllowListed: false,
            BitsettingSupportedOnMedia: supportedOnMedia,
            DiagnosticMessage: "Book type (bitsetting) controller is not attached; showing media default.");
    }

    /// <summary>
    /// Asynchronously queries the current DVD Book Type and drive bitsetting state for the specified recorder.
    /// </summary>
    public Task<BookTypeQueryResult> QueryBookTypeAsync(
        IDiscRecorderBackend recorder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        cancellationToken.ThrowIfCancellationRequested();
        return recorder.IsVirtual || _hardwareController == null
            ? Task.FromResult(QueryBookType(recorder))
            : Task.Run(() => QueryBookType(recorder), cancellationToken);
    }

    /// <summary>
    /// Queries the current DVD Book Type of the media inserted in the specified recorder.
    /// </summary>
    public DvdBookType GetBookType(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (recorder.IsVirtual)
        {
            var statusVirtual = recorder.QueryMediaStatus();
            return ReadVirtualBookType(recorder, statusVirtual);
        }

        if (_hardwareController != null)
        {
            var query = _hardwareController.QueryBookType(recorder);
            if (query.Success && query.MediaBookType is { } mediaBookType)
            {
                return mediaBookType;
            }
        }

        var status = recorder.QueryMediaStatus();
        return status.BookType ?? GetDefaultBookType(status.MediaType);
    }

    private static DvdBookType ReadVirtualBookType(IDiscRecorderBackend recorder, MediaStatusDto status)
    {
        var bookTypeProp = recorder.GetType().GetProperty("BookType");
        if (bookTypeProp != null)
        {
            var val = bookTypeProp.GetValue(recorder);
            if (val is DvdBookType dbt) return dbt;
        }

        var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
        if (mediumProp?.GetValue(recorder) is IBookTypeTarget mediumTarget)
        {
            return mediumTarget.BookType;
        }

        return status.BookType ?? GetDefaultBookType(status.MediaType);
    }

    /// <summary>
    /// Sets the DVD Book Type on the specified target medium (e.g. DVD+R DL to DVD-ROM).
    /// </summary>
    /// <param name="target">The target medium or drive implementing <see cref="IBookTypeTarget"/>.</param>
    /// <param name="targetBookType">Desired Book Type (e.g. <see cref="DvdBookType.DvdRom"/>).</param>
    /// <returns>A <see cref="BurnOperationResult"/> indicating success or failure.</returns>
    public BurnOperationResult SetBookType(IBookTypeTarget target, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!IsBitsettingSupported(target.MediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{target.MediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        target.BookType = targetBookType;
        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    /// <summary>
    /// Sets the DVD Book Type on the media currently inserted in the specified recorder.
    /// </summary>
    public BurnOperationResult SetBookType(IDiscRecorderBackend recorder, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        if (!IsBitsettingSupported(status.MediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{status.MediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        if (!recorder.IsVirtual)
        {
            if (_hardwareController != null)
            {
                return _hardwareController.SetBookType(recorder, targetBookType);
            }

            // Book type changes use drive-vendor-specific commands (they differ between LG, Pioneer, Lite-On, ASUS...).
            // Without an attached controller, report honestly instead of claiming success.
            return BurnOperationResult.Failed(
                "Book type (bitsetting) is not yet supported on physical drives. No change was made to the disc.");
        }

        {
            var bookTypeProp = recorder.GetType().GetProperty("BookType");
            if (bookTypeProp != null && bookTypeProp.CanWrite)
            {
                bookTypeProp.SetValue(recorder, targetBookType);
            }
            else
            {
                var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
                if (mediumProp?.GetValue(recorder) is IBookTypeTarget mediumTarget)
                {
                    mediumTarget.BookType = targetBookType;
                }
            }
        }

        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    /// <summary>
    /// Asynchronously sets the DVD Book Type on the specified target medium.
    /// </summary>
    public Task<BurnOperationResult> SetBookTypeAsync(
        IBookTypeTarget target,
        DvdBookType targetBookType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SetBookType(target, targetBookType));
    }

    /// <summary>
    /// Asynchronously sets the DVD Book Type on the media currently inserted in the specified recorder.
    /// </summary>
    public Task<BurnOperationResult> SetBookTypeAsync(
        IDiscRecorderBackend recorder,
        DvdBookType targetBookType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        cancellationToken.ThrowIfCancellationRequested();
        return recorder.IsVirtual || _hardwareController == null
            ? Task.FromResult(SetBookType(recorder, targetBookType))
            : Task.Run(() => SetBookType(recorder, targetBookType), cancellationToken);
    }

    /// <summary>
    /// Applies an opt-in or required pre-burn Book Type setting before writing begins.
    /// Unsupported drives or non-DVD+ media report cleanly without failing the burn unless
    /// <paramref name="requireBookType"/> is true.
    /// </summary>
    public PreBurnBookTypeOutcome ApplyPreBurnBookType(
        IDiscRecorderBackend recorder,
        DiscMediaType mediaType,
        DvdBookType? requestedBookType,
        bool requireBookType,
        bool isSimulate)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        if (requestedBookType is not { } target)
        {
            return new PreBurnBookTypeOutcome(ShouldProceed: true, Applied: false, Note: null, ErrorMessage: null, IsWarning: false);
        }

        if (isSimulate)
        {
            return new PreBurnBookTypeOutcome(
                ShouldProceed: true,
                Applied: false,
                Note: "Book type was not changed during test write.",
                ErrorMessage: null,
                IsWarning: false);
        }

        if (!IsBitsettingSupported(mediaType))
        {
            string unsupportedMsg =
                $"Book type was not set to {target}: {mediaType.GetDisplayName()} does not support bitsetting.";
            return requireBookType
                ? new PreBurnBookTypeOutcome(ShouldProceed: false, Applied: false, Note: null, ErrorMessage: unsupportedMsg, IsWarning: false)
                : new PreBurnBookTypeOutcome(ShouldProceed: true, Applied: false, Note: unsupportedMsg, ErrorMessage: null, IsWarning: true);
        }

        BurnOperationResult setResult = SetBookType(recorder, target);
        if (setResult.Success)
        {
            return new PreBurnBookTypeOutcome(
                ShouldProceed: true,
                Applied: true,
                Note: $"Book type set to {target} (0x{(byte)target:X2}).",
                ErrorMessage: null,
                IsWarning: false);
        }

        string reason = string.IsNullOrWhiteSpace(setResult.ErrorMessage)
            ? "not supported by this drive"
            : setResult.ErrorMessage.Trim();
        string skippedMsg = $"Book type was not set to {target}: {reason}";
        return requireBookType
            ? new PreBurnBookTypeOutcome(ShouldProceed: false, Applied: false, Note: null, ErrorMessage: setResult.ErrorMessage ?? skippedMsg, IsWarning: false)
            : new PreBurnBookTypeOutcome(ShouldProceed: true, Applied: false, Note: skippedMsg, ErrorMessage: null, IsWarning: true);
    }
}
