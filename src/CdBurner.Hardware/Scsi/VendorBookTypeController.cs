using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Hardware DVD Book Type controller reading current media Book Type from MMC READ DISC STRUCTURE (0xAD) format 0x00
/// and updating bitsetting via standard MMC SEND DISC STRUCTURE (0xBF) or strictly <see cref="BookTypeDriveAllowList"/>-gated
/// vendor hooks for Lite-On/MediaTek (0xDF 0x0F) and LG/HL-DT-ST (0xFA / 0xFC +RTB).
/// Unlisted drives never receive vendor-specific CDBs.
/// </summary>
public sealed class VendorBookTypeController : IBookTypeController
{
    public const byte OpCodeLiteOnBookType = 0xDF;
    public const byte SubCommandLiteOnBookType = 0x0F;
    public const byte OpCodeLgBookTypeQuery = 0xFA;
    public const byte OpCodeLgBookTypeSet = 0xFC;
    public const int PhysicalFormatReadLength = 36;
    public const int LiteOnQueryLength = 0x80;
    public const int LgVectorLength = 4;

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IDiscRecorderBackend, IScsiTransport?>? _transportFactory;
    private readonly TimeSpan _commandTimeout;

    public VendorBookTypeController(
        Func<IDiscRecorderBackend, IScsiTransport?>? transportFactory = null,
        TimeSpan? commandTimeout = null)
    {
        _transportFactory = transportFactory;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public static byte[] BuildReadPhysicalFormatCdb(ushort allocationLength = PhysicalFormatReadLength) =>
        MmcCommandBuilder.BuildReadDiscStructureCdb(
            BookTypeService.FormatCodePhysicalFormatInformation,
            allocationLength,
            mediaType: 0);

    public static byte[] BuildLiteOnQueryBookTypeCdb()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLiteOnBookType;
        cdb[2] = SubCommandLiteOnBookType;
        cdb[7] = LiteOnQueryLength;
        return cdb;
    }

    public static byte[] BuildLiteOnSetBookTypeCdb(bool dvdRom)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeLiteOnBookType;
        cdb[2] = SubCommandLiteOnBookType;
        cdb[3] = 0x01;
        cdb[4] = (byte)(dvdRom ? 0x01 : 0x00);
        return cdb;
    }

    public static byte[] BuildLgQueryBookTypeCdb()
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeLgBookTypeQuery;
        cdb[8] = LgVectorLength;
        return cdb;
    }

    public static byte[] BuildLgSetBookTypeCdb()
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeLgBookTypeSet;
        cdb[2] = (byte)'+';
        cdb[3] = (byte)'R';
        cdb[4] = (byte)'T';
        cdb[5] = (byte)'B';
        cdb[8] = LgVectorLength;
        return cdb;
    }

    public static byte[] BuildLgSetBookTypePayload(bool dvdRom) =>
        dvdRom
            ? [0x00, 0x00, 0x00, 0x00]
            : [0xA0, 0xE0, 0x00, 0x00];

    public static bool TryParseLiteOnBookTypeSetting(
        ReadOnlySpan<byte> response,
        DiscMediaType mediaType,
        out DvdBookType bookType)
    {
        bookType = default;
        if (response.Length < 1)
        {
            return false;
        }

        bookType = response[0] switch
        {
            0x01 => DvdBookType.DvdRom,
            0x00 => BookTypeService.GetDefaultBookType(mediaType),
            0x02 => DiscMediaType.DvdPlusRw == mediaType ? DvdBookType.DvdPlusRw : BookTypeService.GetDefaultBookType(mediaType),
            _ => default
        };
        return response[0] is 0x00 or 0x01 or 0x02;
    }

    public static bool TryParseLgBookTypeSetting(
        ReadOnlySpan<byte> response,
        DiscMediaType mediaType,
        out DvdBookType bookType)
    {
        bookType = default;
        if (response.Length < 2)
        {
            return false;
        }

        if (mediaType == DiscMediaType.DvdPlusRDualLayer)
        {
            bookType = (response[1] & 0xF0) == 0 ? DvdBookType.DvdRom : DvdBookType.DvdPlusRDualLayer;
            return true;
        }

        if (mediaType == DiscMediaType.DvdPlusR || mediaType == DiscMediaType.Unknown)
        {
            bookType = (response[0] & 0xF0) == 0 ? DvdBookType.DvdRom : DvdBookType.DvdPlusR;
            return true;
        }

        return false;
    }

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

        if (!TryOpenTransport(recorder, out IScsiTransport? transport, out string? openError))
        {
            DvdBookType fallback = status.BookType ?? BookTypeService.GetDefaultBookType(status.MediaType);
            return new BookTypeQueryResult(
                Success: false,
                MediaBookType: fallback,
                PartVersion: null,
                DriveSettingBookType: null,
                DriveFamily: BookTypeDriveFamily.Unknown,
                IsAllowListed: BookTypeDriveAllowList.IsSupported(recorder.VendorId, recorder.ProductId),
                BitsettingSupportedOnMedia: BookTypeService.IsBitsettingSupported(status.MediaType),
                DiagnosticMessage: openError ?? "Unable to open drive for SCSI Book Type query.");
        }

        using (transport!)
        {
            return QueryBookType(transport, status.MediaType);
        }
    }

    public BurnOperationResult SetBookType(IDiscRecorderBackend recorder, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        if (!BookTypeService.IsBitsettingSupported(status.MediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{status.MediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        if (!TryOpenTransport(recorder, out IScsiTransport? transport, out string? openError))
        {
            return BurnOperationResult.Failed(openError ?? "Unable to open drive for SCSI Book Type bitsetting.");
        }

        using (transport!)
        {
            return SetBookType(transport, status.MediaType, targetBookType);
        }
    }

    public (bool Success, DvdBookType? BookType, byte? PartVersion, string? Diagnostic) ReadCurrentBookType(IScsiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        byte[] buf = new byte[PhysicalFormatReadLength];
        var res = transport.ExecuteCommand(
            BuildReadPhysicalFormatCdb((ushort)buf.Length),
            buf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        if (!res.IsSuccess)
        {
            return (false, null, null, $"READ DISC STRUCTURE (0xAD) format 0x00 failed: {DescribeFailure(res)}");
        }

        int len = (int)Math.Min((uint)buf.Length, res.DataTransferLength);
        if (BookTypeService.TryParsePhysicalFormatBookType(buf.AsSpan(0, len), out DvdBookType bookType, out byte partVersion))
        {
            return (true, bookType, partVersion, null);
        }

        return (false, null, null, $"READ DISC STRUCTURE (0xAD) format 0x00 returned an invalid or short response ({len} bytes).");
    }

    public BookTypeQueryResult QueryBookType(IScsiTransport transport, DiscMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(transport);

        bool supportedOnMedia = BookTypeService.IsBitsettingSupported(mediaType);
        if (!TryInquiry(transport, out MmcInquiryResponse? inquiry, out string? inquiryError))
        {
            return new BookTypeQueryResult(
                Success: false,
                MediaBookType: mediaType == DiscMediaType.Unknown ? null : BookTypeService.GetDefaultBookType(mediaType),
                PartVersion: null,
                DriveSettingBookType: null,
                DriveFamily: BookTypeDriveFamily.Unknown,
                IsAllowListed: false,
                BitsettingSupportedOnMedia: supportedOnMedia,
                DiagnosticMessage: inquiryError);
        }

        BookTypeAllowListMatch? allowMatch = BookTypeDriveAllowList.Evaluate(inquiry!.VendorId, inquiry.ProductId);
        BookTypeDriveFamily family = allowMatch?.Family ?? BookTypeDriveFamily.StandardMmc;

        DvdBookType? mediaBookType = null;
        byte? partVersion = null;
        string? readDiag = null;

        if (mediaType.IsDvd() || mediaType == DiscMediaType.Unknown)
        {
            var readback = ReadCurrentBookType(transport);
            if (readback.Success)
            {
                mediaBookType = readback.BookType;
                partVersion = readback.PartVersion;
            }
            else
            {
                readDiag = readback.Diagnostic;
            }
        }
        else
        {
            mediaBookType = BookTypeService.GetDefaultBookType(mediaType);
            readDiag = $"{mediaType.GetDisplayName()} is not DVD media.";
        }

        DvdBookType? driveSetting = null;
        string? vendorDiag = null;
        if (allowMatch != null && (mediaType == DiscMediaType.Unknown || BookTypeService.IsBitsettingSupported(mediaType)))
        {
            (driveSetting, vendorDiag) = QueryVendorUnitSetting(transport, allowMatch.Family, mediaType);
        }

        string summary = BuildQuerySummary(inquiry, allowMatch, mediaBookType, partVersion, driveSetting, readDiag ?? vendorDiag);
        return new BookTypeQueryResult(
            Success: mediaBookType.HasValue || driveSetting.HasValue,
            MediaBookType: mediaBookType ?? (mediaType == DiscMediaType.Unknown ? null : BookTypeService.GetDefaultBookType(mediaType)),
            PartVersion: partVersion,
            DriveSettingBookType: driveSetting,
            DriveFamily: family,
            IsAllowListed: allowMatch != null,
            BitsettingSupportedOnMedia: supportedOnMedia,
            DiagnosticMessage: summary);
    }

    public BurnOperationResult SetBookType(IScsiTransport transport, DiscMediaType mediaType, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (!BookTypeService.IsBitsettingSupported(mediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{mediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        // 1. Mandatory INQUIRY gate before any MMC bitsetting or vendor CDB.
        if (!TryInquiry(transport, out MmcInquiryResponse? inquiry, out string? inquiryError))
        {
            return BurnOperationResult.Failed(inquiryError ?? "SCSI INQUIRY failed.");
        }

        BookTypeAllowListMatch? allowMatch = BookTypeDriveAllowList.Evaluate(inquiry!.VendorId, inquiry.ProductId);

        // 2. Try standard MMC SEND DISC STRUCTURE (0xBF) format 0x00 first.
        var (cdb, payload) = BookTypeService.BuildBookTypeCommand(targetBookType);
        var mmcRes = transport.ExecuteCommand(cdb, payload, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, _commandTimeout);
        if (mmcRes.IsSuccess)
        {
            var readback = ReadCurrentBookType(transport);
            if (!readback.Success || readback.BookType == targetBookType)
            {
                return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
            }

            if (allowMatch == null)
            {
                return BurnOperationResult.Failed(
                    $"Drive '{inquiry.VendorId} {inquiry.ProductId}' accepted MMC SEND DISC STRUCTURE (0xBF) but still reports Book Type {readback.BookType}.");
            }
        }

        // 3. Strict allow-list gate: unlisted drives NEVER receive vendor CDBs.
        if (allowMatch == null)
        {
            return BurnOperationResult.Failed(
                $"Drive '{inquiry.VendorId} {inquiry.ProductId}' does not support MMC SEND DISC STRUCTURE (0xBF) bitsetting ({DescribeFailure(mmcRes)}) and is not on the Book Type allow-list; no vendor commands were sent.");
        }

        return allowMatch.Family switch
        {
            BookTypeDriveFamily.LiteOnMediaTek => SetLiteOnBookType(transport, inquiry, mediaType, targetBookType),
            BookTypeDriveFamily.LgHlDtSt => SetLgBookType(transport, inquiry, mediaType, targetBookType),
            _ => BurnOperationResult.Failed(
                $"Drive '{inquiry.VendorId} {inquiry.ProductId}' has no vendor bitsetting handler for family {allowMatch.Family}.")
        };
    }

    private (DvdBookType? Setting, string? Diagnostic) QueryVendorUnitSetting(
        IScsiTransport transport,
        BookTypeDriveFamily family,
        DiscMediaType mediaType)
    {
        if (family == BookTypeDriveFamily.LiteOnMediaTek)
        {
            byte[] buf = new byte[LiteOnQueryLength];
            var res = transport.ExecuteCommand(
                BuildLiteOnQueryBookTypeCdb(),
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            int len = (int)Math.Min((uint)buf.Length, res.DataTransferLength);
            if (res.IsSuccess && len >= 1 &&
                TryParseLiteOnBookTypeSetting(buf.AsSpan(0, len), mediaType, out DvdBookType parsed))
            {
                return (parsed, null);
            }
            return (null, $"Lite-On bitsetting query failed: {DescribeFailure(res)}");
        }

        if (family == BookTypeDriveFamily.LgHlDtSt)
        {
            if (mediaType == DiscMediaType.DvdPlusRw)
            {
                return (null, "LG/HL-DT-ST vendor bitsetting supports DVD+R and DVD+R DL only (not DVD+RW).");
            }

            byte[] buf = new byte[LgVectorLength];
            var res = transport.ExecuteCommand(
                BuildLgQueryBookTypeCdb(),
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            int len = (int)Math.Min((uint)buf.Length, res.DataTransferLength);
            if (res.IsSuccess && len >= 2 &&
                TryParseLgBookTypeSetting(buf.AsSpan(0, len), mediaType, out DvdBookType parsed))
            {
                return (parsed, null);
            }
            return (null, $"LG/HL-DT-ST bitsetting query failed: {DescribeFailure(res)}");
        }

        return (null, null);
    }

    private BurnOperationResult SetLiteOnBookType(
        IScsiTransport transport,
        MmcInquiryResponse inquiry,
        DiscMediaType mediaType,
        DvdBookType targetBookType)
    {
        DvdBookType nativeDefault = BookTypeService.GetDefaultBookType(mediaType);
        if (targetBookType != DvdBookType.DvdRom && targetBookType != nativeDefault)
        {
            return BurnOperationResult.Failed(
                $"Lite-On/MediaTek drives can set DVD+ media to DVD-ROM or native {nativeDefault}, not {targetBookType}.");
        }

        bool setDvdRom = targetBookType == DvdBookType.DvdRom;
        var setRes = transport.ExecuteCommand(
            BuildLiteOnSetBookTypeCdb(setDvdRom),
            Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
            _commandTimeout);

        if (!setRes.IsSuccess)
        {
            return BurnOperationResult.Failed(
                $"Lite-On/MediaTek drive '{inquiry.VendorId} {inquiry.ProductId}' rejected Book Type command (0xDF 0x0F): {DescribeFailure(setRes)}");
        }

        byte[] queryBuf = new byte[LiteOnQueryLength];
        var queryRes = transport.ExecuteCommand(
            BuildLiteOnQueryBookTypeCdb(),
            queryBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        int len = (int)Math.Min((uint)queryBuf.Length, queryRes.DataTransferLength);
        if (queryRes.IsSuccess && len >= 1 &&
            TryParseLiteOnBookTypeSetting(queryBuf.AsSpan(0, len), mediaType, out DvdBookType confirmed) &&
            confirmed != targetBookType)
        {
            return BurnOperationResult.Failed(
                $"Lite-On/MediaTek drive '{inquiry.VendorId} {inquiry.ProductId}' still reports Book Type setting {confirmed} after 0xDF 0x0F.");
        }

        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    private BurnOperationResult SetLgBookType(
        IScsiTransport transport,
        MmcInquiryResponse inquiry,
        DiscMediaType mediaType,
        DvdBookType targetBookType)
    {
        if (mediaType == DiscMediaType.DvdPlusRw)
        {
            return BurnOperationResult.Failed(
                $"LG/HL-DT-ST drive '{inquiry.VendorId} {inquiry.ProductId}' supports Book Type bitsetting for DVD+R and DVD+R DL only, not DVD+RW.");
        }

        DvdBookType nativeDefault = BookTypeService.GetDefaultBookType(mediaType);
        if (targetBookType != DvdBookType.DvdRom && targetBookType != nativeDefault)
        {
            return BurnOperationResult.Failed(
                $"LG/HL-DT-ST drives can set DVD+R/+R DL to DVD-ROM or native {nativeDefault}, not {targetBookType}.");
        }

        bool setDvdRom = targetBookType == DvdBookType.DvdRom;
        byte[] payload = BuildLgSetBookTypePayload(setDvdRom);
        var setRes = transport.ExecuteCommand(
            BuildLgSetBookTypeCdb(),
            payload,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT,
            _commandTimeout);

        if (!setRes.IsSuccess)
        {
            return BurnOperationResult.Failed(
                $"LG/HL-DT-ST drive '{inquiry.VendorId} {inquiry.ProductId}' rejected Book Type command (0xFC +RTB): {DescribeFailure(setRes)}");
        }

        byte[] queryBuf = new byte[LgVectorLength];
        var queryRes = transport.ExecuteCommand(
            BuildLgQueryBookTypeCdb(),
            queryBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        int len = (int)Math.Min((uint)queryBuf.Length, queryRes.DataTransferLength);
        if (queryRes.IsSuccess && len >= 2 &&
            TryParseLgBookTypeSetting(queryBuf.AsSpan(0, len), mediaType, out DvdBookType confirmed) &&
            confirmed != targetBookType)
        {
            return BurnOperationResult.Failed(
                $"LG/HL-DT-ST drive '{inquiry.VendorId} {inquiry.ProductId}' still reports Book Type setting {confirmed} after 0xFC +RTB.");
        }

        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    private bool TryInquiry(IScsiTransport transport, out MmcInquiryResponse? inquiry, out string? error)
    {
        byte[] inquiryBuf = new byte[36];
        var inquiryRes = transport.ExecuteCommand(
            MmcCommandBuilder.BuildInquiryCdb(36),
            inquiryBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        if (!inquiryRes.IsSuccess || inquiryRes.DataTransferLength < 36)
        {
            inquiry = null;
            error = $"SCSI INQUIRY failed: {DescribeFailure(inquiryRes)}.";
            return false;
        }

        int len = (int)Math.Min(36u, inquiryRes.DataTransferLength);
        inquiry = MmcResponseParser.ParseInquiry(inquiryBuf.AsSpan(0, len));
        error = null;
        return true;
    }

    private bool TryOpenTransport(IDiscRecorderBackend recorder, [NotNullWhen(true)] out IScsiTransport? transport, out string? error)
    {
        if (_transportFactory != null)
        {
            transport = _transportFactory(recorder);
            if (transport == null)
            {
                error = $"Could not open SCSI transport for drive '{recorder.UniqueId}'.";
                return false;
            }
            error = null;
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            transport = null;
            error = "Physical drive Book Type commands require Windows SCSI pass-through.";
            return false;
        }

        string devicePath = NativeAudioBurnEngine.DevicePathFor(recorder);
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            transport = null;
            error = $"Drive '{recorder.UniqueId}' has no device path for SCSI pass-through.";
            return false;
        }

        try
        {
            transport = Win32ScsiTransport.Open(devicePath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            transport = null;
            error = $"Could not open drive '{recorder.UniqueId}' for Book Type command: {ex.Message}";
            return false;
        }
    }

    private static string BuildQuerySummary(
        MmcInquiryResponse inquiry,
        BookTypeAllowListMatch? allowMatch,
        DvdBookType? mediaBookType,
        byte? partVersion,
        DvdBookType? driveSetting,
        string? diagnostic)
    {
        string mediaPart = mediaBookType is { } bt
            ? $"Media Book Type: {bt} (0x{(byte)bt:X2}{(partVersion is { } pv ? $", rev {pv}" : string.Empty)})"
            : "Media Book Type: unavailable";
        string drivePart = allowMatch != null
            ? $"Allow-listed ({allowMatch.Family}: {inquiry.VendorId} {inquiry.ProductId})"
            : $"Unlisted drive ({inquiry.VendorId} {inquiry.ProductId}; standard MMC only, vendor CDBs blocked)";
        string settingPart = driveSetting is { } ds
            ? $"; drive setting: {ds}"
            : string.Empty;
        string diagPart = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $"; {diagnostic}";
        return $"{mediaPart}. {drivePart}{settingPart}{diagPart}.";
    }

    private static string DescribeFailure(ScsiExecutionResult res)
    {
        if (res.SenseInfo != null)
        {
            return res.SenseInfo.ToString();
        }
        if (res.Win32Error != 0)
        {
            return $"Win32 {res.Win32Error}: {res.ErrorMessage}";
        }
        return res.ErrorMessage ?? $"SCSI status 0x{res.ScsiStatus:X2}";
    }
}
