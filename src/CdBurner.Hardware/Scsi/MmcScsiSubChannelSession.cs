using CdBurner.Core.Abstractions;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Reads Q sub-channel position, Media Catalog Number (MCN), and track ISRC via SCSI MMC commands
/// (<c>READ CD</c> sub-channel 0x02 with automatic fallback to <c>READ SUB-CHANNEL</c> 0x42).
/// </summary>
public sealed class MmcScsiSubChannelSession : IAudioSubChannelSession
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private readonly IScsiTransport _transport;
    private readonly bool _ownsTransport;
    private readonly IDisposable? _extraDisposable;
    private readonly byte[] _sectorWithQ = new byte[2352 + 16];
    private readonly byte[] _subBuf = new byte[24];
    private bool _readCdQRejected;
    private bool _disposed;

    public MmcScsiSubChannelSession(
        IScsiTransport transport,
        bool ownsTransport,
        IDisposable? extraDisposable = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _extraDisposable = extraDisposable;
    }

    /// <summary>
    /// When true, decodes track, index, and MSF fields in <c>READ CD</c> Formatted Q responses as BCD instead of binary.
    /// </summary>
    public bool UseBcdInReadCdQ { get; set; }

    public bool TryReadQSubChannel(int lba, out QSubChannelFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        if (lba < 0)
        {
            return false;
        }

        if (!_readCdQRejected)
        {
            byte[] cdb = MmcCommandBuilder.BuildReadCdCdb(
                lba,
                1,
                expectedSectorType: 1,
                mainChannelSelectionBits: 0x10,
                subChannelSelection: MmcCommandBuilder.ReadCdSubChannelFormattedQ);
            var res = _transport.ExecuteCommand(cdb, _sectorWithQ, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
            if (res.IsSuccess && res.DataTransferLength >= 2368)
            {
                return MmcResponseParser.TryParseReadCdFormattedQ(_sectorWithQ.AsSpan(2352, 16), UseBcdInReadCdQ, out frame);
            }

            if (res.SenseInfo is { SenseKey: 0x05, Asc: 0x24 or 0x20 })
            {
                _readCdQRejected = true;
            }
            else
            {
                return false;
            }
        }

        byte[] seekCdb = MmcCommandBuilder.BuildReadCdCdb(
            lba,
            1,
            expectedSectorType: 1,
            mainChannelSelectionBits: 0x10,
            subChannelSelection: 0);
        var seekRes = _transport.ExecuteCommand(seekCdb, _sectorWithQ.AsSpan(0, 2352), ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (!seekRes.IsSuccess)
        {
            return false;
        }

        byte[] subCdb = MmcCommandBuilder.BuildReadSubChannelCdb(
            MmcCommandBuilder.SubChannelFormatCurrentPosition,
            trackNumber: 0,
            allocationLength: 16,
            msf: true);
        var subSpan = _subBuf.AsSpan(0, 16);
        subSpan.Clear();
        var subRes = _transport.ExecuteCommand(subCdb, subSpan, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (!subRes.IsSuccess || subRes.DataTransferLength < 16)
        {
            return false;
        }

        return MmcResponseParser.TryParseSubChannelPosition(subSpan, msf: true, out frame);
    }

    public string? ReadMediaCatalogNumber()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] cdb = MmcCommandBuilder.BuildReadSubChannelCdb(
            MmcCommandBuilder.SubChannelFormatMediaCatalogNumber,
            trackNumber: 0,
            allocationLength: 24,
            msf: true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            _subBuf.AsSpan().Clear();
            var res = _transport.ExecuteCommand(cdb, _subBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
            if (!res.IsSuccess)
            {
                if (res.SenseInfo is { SenseKey: 0x05, Asc: 0x20 or 0x24 })
                {
                    return null;
                }
                continue;
            }

            if (MmcResponseParser.TryParseSubChannelMediaCatalogNumber(_subBuf, out string mcn))
            {
                return mcn;
            }
        }

        return null;
    }

    public string? ReadTrackIsrc(int trackNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (trackNumber is < 1 or > 99)
        {
            return null;
        }

        byte[] cdb = MmcCommandBuilder.BuildReadSubChannelCdb(
            MmcCommandBuilder.SubChannelFormatTrackIsrc,
            trackNumber: (byte)trackNumber,
            allocationLength: 24,
            msf: true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            _subBuf.AsSpan().Clear();
            var res = _transport.ExecuteCommand(cdb, _subBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
            if (!res.IsSuccess)
            {
                if (res.SenseInfo is { SenseKey: 0x05, Asc: 0x20 or 0x24 })
                {
                    return null;
                }
                continue;
            }

            if (MmcResponseParser.TryParseSubChannelIsrc(_subBuf, out string isrc))
            {
                return isrc;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsTransport)
            {
                _transport.Dispose();
            }
            _extraDisposable?.Dispose();
        }
    }
}
