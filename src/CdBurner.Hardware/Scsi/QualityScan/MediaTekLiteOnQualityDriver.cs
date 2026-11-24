using System.Buffers.Binary;
using System.Diagnostics;
using CdBurner.Core.Verification;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Hardware.Scsi.QualityScan;

/// <summary>
/// Clean-room MediaTek / Lite-On optical disc quality scanner (<c>0xF3 0x0E</c> with single <c>0xDF</c> fallback on CD).
/// Gated strictly on <see cref="QualityScanDriveAllowList"/> via standard <c>INQUIRY (0x12)</c>; unlisted drives
/// never receive vendor CDBs. Executes exclusive access locking, 10-second per-command timeouts, immediate sense/Win32
/// abort, and unconditional <c>finally</c> teardown.
/// </summary>
public sealed class MediaTekLiteOnQualityDriver : IQualityScanDriver, IDisposable
{
    private const int MaxConsecutiveStalePolls = 64;

    private readonly IScsiTransport _transport;
    private readonly bool _ownsTransport;
    private readonly Func<IDisposable?>? _acquireExclusiveAccess;
    private readonly string? _mediaId;
    private readonly TimeSpan _commandTimeout;

    public string DriverName => nameof(MediaTekLiteOnQualityDriver);
    public bool IsExperimentalHardware => true;

    public MediaTekLiteOnQualityDriver(
        IScsiTransport transport,
        bool ownsTransport = false,
        Func<IDisposable?>? acquireExclusiveAccess = null,
        string? mediaId = null,
        TimeSpan? commandTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        _acquireExclusiveAccess = acquireExclusiveAccess ?? (transport is Win32ScsiTransport win32
            ? () => CdromExclusiveAccess.Acquire(win32.Handle, "IgnisDisc")
            : null);
        _mediaId = mediaId;
        _commandTimeout = commandTimeout ?? MediaTekQualityCommands.DefaultCommandTimeout;
    }

    public Task<QualityScanReport> ScanAsync(
        QualityScanOptions options,
        IProgress<QualityScanProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => ExecuteScan(options, progress, cancellationToken), cancellationToken);
    }

    private QualityScanReport ExecuteScan(
        QualityScanOptions options,
        IProgress<QualityScanProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        // 1. Mandatory INQUIRY gate before any other command or vendor CDB.
        byte[] inquiryBuf = new byte[36];
        var inquiryRes = _transport.ExecuteCommand(
            MmcCommandBuilder.BuildInquiryCdb(36),
            inquiryBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        if (!inquiryRes.IsSuccess || inquiryRes.DataTransferLength < 36)
        {
            return QualityScanReport.CreateUnsupported(
                "UNKNOWN",
                "UNKNOWN",
                string.Empty,
                $"SCSI INQUIRY failed: {inquiryRes.ErrorMessage ?? "short response"}.");
        }

        var inquiry = MmcResponseParser.ParseInquiry(inquiryBuf.AsSpan(0, (int)Math.Min(36u, inquiryRes.DataTransferLength)));
        var allowMatch = QualityScanDriveAllowList.Evaluate(inquiry.VendorId, inquiry.ProductId);
        if (allowMatch == null)
        {
            return QualityScanReport.CreateUnsupported(
                inquiry.VendorId,
                inquiry.ProductId,
                inquiry.ProductRevision,
                $"Drive '{inquiry.VendorId} {inquiry.ProductId}' is not on the MediaTek/Lite-On quality-scan allow-list; no vendor commands were sent.");
        }

        // 2. Exclusive access + standard MMC media/capacity probe.
        using IDisposable? exclusiveLock = _acquireExclusiveAccess?.Invoke();

        var readyRes = ExecuteTestUnitReadyHandlingUnitAttention();
        if (!readyRes.IsSuccess)
        {
            return BuildNonScanResult(
                QualityScanStatus.UnsupportedMedia,
                inquiry,
                QualityScanMediaKind.Cd,
                null,
                0,
                $"Drive not ready: {readyRes.ErrorMessage}",
                clock.Elapsed);
        }

        byte[] cfgBuf = new byte[64];
        var cfgRes = _transport.ExecuteCommand(
            MmcCommandBuilder.BuildGetConfigurationCdb(0, (ushort)cfgBuf.Length),
            cfgBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);

        ushort profile = cfgRes.IsSuccess && cfgRes.DataTransferLength >= 8
            ? BinaryPrimitives.ReadUInt16BigEndian(cfgBuf.AsSpan(6, 2))
            : (ushort)0;

        QualityScanMediaKind? maybeKind = MapProfileToMediaKind(profile);
        if (maybeKind is not QualityScanMediaKind mediaKind)
        {
            return BuildNonScanResult(
                QualityScanStatus.UnsupportedMedia,
                inquiry,
                QualityScanMediaKind.Cd,
                profile == 0 ? null : $"0x{profile:X4}",
                0,
                $"Inserted medium profile 0x{profile:X4} is not a supported CD/DVD/BD medium.",
                clock.Elapsed);
        }

        if (IsDiscBlank())
        {
            return BuildNonScanResult(
                QualityScanStatus.UnsupportedMedia,
                inquiry,
                mediaKind,
                $"0x{profile:X4}",
                0,
                "Disc is blank; quality scanning requires recorded media.",
                clock.Elapsed);
        }

        int totalDiscSectors = QueryTotalSectors(mediaKind);
        if (totalDiscSectors <= 0)
        {
            return BuildNonScanResult(
                QualityScanStatus.UnsupportedMedia,
                inquiry,
                mediaKind,
                $"0x{profile:X4}",
                0,
                "Could not determine recorded disc sector capacity.",
                clock.Elapsed);
        }

        int startLba = Math.Clamp(options.StartLba, 0, Math.Max(0, totalDiscSectors - 1));
        int endLba = options.EndLba.HasValue
            ? Math.Clamp(options.EndLba.Value, startLba + 1, totalDiscSectors)
            : totalDiscSectors;

        // 3. Optional read-speed setting (non-fatal if drive keeps its own speed).
        if (options.ReadSpeedMultiplier is int reqSpeed && reqSpeed > 0)
        {
            double speed1XKb = OneXKilobytesPerSec(mediaKind);
            ushort kb = (ushort)Math.Clamp((int)Math.Round(reqSpeed * speed1XKb), 1, ushort.MaxValue);
            _ = _transport.ExecuteCommand(
                MediaTekQualityCommands.BuildSetCdSpeedCdb(kb),
                Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                _commandTimeout);
        }

        // 4. Start, poll, and unconditionally stop in finally.
        var samples = new List<QualityScanIntervalSample>();
        QualityScanProtocol protocol = QualityScanProtocol.None;
        bool scanEntered = false;
        bool useLegacyDf = false;
        string? failureReason = null;

        try
        {
            var seekRes = _transport.ExecuteCommand(
                MediaTekQualityCommands.BuildSeek10Cdb(startLba),
                Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                _commandTimeout);
            if (!seekRes.IsSuccess)
            {
                return BuildNonScanResult(
                    QualityScanStatus.Failed,
                    inquiry,
                    mediaKind,
                    $"0x{profile:X4}",
                    totalDiscSectors,
                    $"SEEK(10) failed before scan start: {seekRes.ErrorMessage}",
                    clock.Elapsed);
            }

            if (mediaKind == QualityScanMediaKind.Cd && options.ForceLegacyProtocol)
            {
                useLegacyDf = true;
                scanEntered = true;
                protocol = QualityScanProtocol.MediaTekDf;
                if (!TryInitLegacyDf(out failureReason))
                {
                    return BuildNonScanResult(QualityScanStatus.Failed, inquiry, mediaKind, $"0x{profile:X4}", totalDiscSectors, failureReason!, clock.Elapsed, protocol);
                }
            }
            else
            {
                byte[] probeBuf = new byte[MediaTekQualityCommands.NewScanReplyLength];
                var f3Init = _transport.ExecuteCommand(
                    MediaTekQualityCommands.BuildF3ErrcCdb(mediaKind),
                    probeBuf,
                    ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                    _commandTimeout);

                if (f3Init.IsSuccess)
                {
                    scanEntered = true;
                    protocol = QualityScanProtocol.MediaTekF3;
                }
                else if (mediaKind == QualityScanMediaKind.Cd && IsInvalidCommandSense(f3Init))
                {
                    useLegacyDf = true;
                    scanEntered = true;
                    protocol = QualityScanProtocol.MediaTekDf;
                    if (!TryInitLegacyDf(out failureReason))
                    {
                        return BuildNonScanResult(QualityScanStatus.Failed, inquiry, mediaKind, $"0x{profile:X4}", totalDiscSectors, failureReason!, clock.Elapsed, protocol);
                    }
                }
                else
                {
                    return BuildNonScanResult(
                        QualityScanStatus.Failed,
                        inquiry,
                        mediaKind,
                        $"0x{profile:X4}",
                        totalDiscSectors,
                        $"MediaTek 0xF3 scan init failed: {f3Init.ErrorMessage}",
                        clock.Elapsed,
                        QualityScanProtocol.MediaTekF3);
                }
            }

            if (useLegacyDf)
            {
                RunLegacyDfLoop(startLba, endLba, samples, progress, ref failureReason, cancellationToken);
            }
            else
            {
                RunNewF3Loop(mediaKind, startLba, endLba, samples, progress, ref failureReason, cancellationToken);
            }
        }
        finally
        {
            if (scanEntered)
            {
                RunScanExitTeardown(useLegacyDf);
            }
        }

        int sectorsScanned = samples.Count > 0 ? Math.Max(0, samples[^1].EndLba - startLba) : 0;
        var summary = QualityScanSummary.FromSamples(mediaKind, sectorsScanned, samples);
        QualityScanStatus status = failureReason == null ? QualityScanStatus.Completed : QualityScanStatus.Failed;
        string statusMsg = failureReason ?? "Quality scan completed (experimental hardware scan; verify live results against QPxTool).";

        return new QualityScanReport(
            SchemaVersion: QualityScanReport.CurrentSchemaVersion,
            CreatedUtc: DateTimeOffset.UtcNow,
            IsSupported: true,
            Status: status,
            IsExperimentalHardwareScan: true,
            StatusMessage: statusMsg,
            DriveVendor: inquiry.VendorId,
            DriveProduct: inquiry.ProductId,
            DriveRevision: inquiry.ProductRevision,
            DriverName: DriverName,
            Protocol: protocol,
            MediaKind: mediaKind,
            MediaType: $"0x{profile:X4}",
            MediaId: _mediaId,
            TotalDiscSectors: totalDiscSectors,
            Summary: summary,
            Samples: samples,
            Elapsed: clock.Elapsed);
    }

    private bool TryInitLegacyDf(out string? failureReason)
    {
        byte[] buf = new byte[MediaTekQualityCommands.LegacyScanReplyLength];
        byte[][] initCdbs =
        [
            MediaTekQualityCommands.BuildDfInitACdb(),
            MediaTekQualityCommands.BuildDfInitArmCdb(0x02),
            MediaTekQualityCommands.BuildDfInitArmCdb(0x00),
            MediaTekQualityCommands.BuildDfInitArmCdb(0x04),
            MediaTekQualityCommands.BuildDfInitArmCdb(0x02),
            MediaTekQualityCommands.BuildDfResetIntervalCdb()
        ];

        foreach (byte[] cdb in initCdbs)
        {
            var r = _transport.ExecuteCommand(cdb, buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, _commandTimeout);
            if (!r.IsSuccess)
            {
                failureReason = $"MediaTek 0xDF scan init failed on CDB {cdb[0]:X2} {cdb[1]:X2}: {r.ErrorMessage}";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private void RunLegacyDfLoop(
        int startLba,
        int endLba,
        List<QualityScanIntervalSample> samples,
        IProgress<QualityScanProgressSnapshot>? progress,
        ref string? failureReason,
        CancellationToken cancellationToken)
    {
        byte[] buf = new byte[MediaTekQualityCommands.LegacyScanReplyLength];
        int currentLba = startLba;
        int totalToScan = Math.Max(1, endLba - startLba);
        var intervalClock = Stopwatch.StartNew();

        while (currentLba < endLba)
        {
            cancellationToken.ThrowIfCancellationRequested();
            intervalClock.Restart();

            var readRes = _transport.ExecuteCommand(
                MediaTekQualityCommands.BuildDfReadIntervalCdb(),
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            if (!readRes.IsSuccess)
            {
                failureReason = $"0xDF interval read failed at LBA {currentLba}: {readRes.ErrorMessage}";
                return;
            }

            var dataRes = _transport.ExecuteCommand(
                MediaTekQualityCommands.BuildDfGetDataCdb(),
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            if (!dataRes.IsSuccess)
            {
                failureReason = $"0xDF counter readout failed at LBA {currentLba}: {dataRes.ErrorMessage}";
                return;
            }

            if (!MediaTekQualityCommands.TryParseDfCdCounters(buf.AsSpan(0, (int)Math.Min((uint)buf.Length, dataRes.DataTransferLength)), out int c1, out int c2, out int cu))
            {
                failureReason = $"0xDF counter reply too short ({dataRes.DataTransferLength} bytes) at LBA {currentLba}.";
                return;
            }

            int nextLba = Math.Min(endLba, currentLba + 75);
            int sectorCount = nextLba - currentLba;

            var rstRes = _transport.ExecuteCommand(
                MediaTekQualityCommands.BuildDfResetIntervalCdb(),
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            if (!rstRes.IsSuccess)
            {
                failureReason = $"0xDF interval reset failed at LBA {currentLba}: {rstRes.ErrorMessage}";
                return;
            }

            (double speedX, double speedKb) = ComputeSpeed(QualityScanMediaKind.Cd, sectorCount, intervalClock.Elapsed);
            var sample = new QualityScanIntervalSample(
                StartLba: currentLba,
                EndLba: nextLba,
                SectorCount: sectorCount,
                ReadSpeedX: speedX,
                ReadSpeedKbPerSec: speedKb,
                C1: c1,
                C2: c2,
                Cu: cu);
            samples.Add(sample);

            currentLba = nextLba;
            double pct = Math.Clamp(100.0 * (currentLba - startLba) / totalToScan, 0.0, 100.0);
            progress?.Report(new QualityScanProgressSnapshot(pct, currentLba, endLba, sample, $"Scanning CD (0xDF): {pct:F0}% (LBA {currentLba:N0})"));
        }
    }

    private void RunNewF3Loop(
        QualityScanMediaKind mediaKind,
        int startLba,
        int endLba,
        List<QualityScanIntervalSample> samples,
        IProgress<QualityScanProgressSnapshot>? progress,
        ref string? failureReason,
        CancellationToken cancellationToken)
    {
        byte[] buf = new byte[MediaTekQualityCommands.NewScanReplyLength];
        byte[] pollCdb = MediaTekQualityCommands.BuildF3ErrcCdb(mediaKind);
        int prevLba = startLba;
        int staleCount = 0;
        bool bdZeroRetried = false;
        int totalToScan = Math.Max(1, endLba - startLba);
        var intervalClock = Stopwatch.StartNew();

        while (prevLba < endLba)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mediaKind == QualityScanMediaKind.BluRay && prevLba == 0 && !bdZeroRetried)
            {
                _ = _transport.ExecuteCommand(
                    MediaTekQualityCommands.BuildSeek10Cdb(0),
                    Span<byte>.Empty,
                    ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                    _commandTimeout);
            }

            intervalClock.Restart();
            var pollRes = _transport.ExecuteCommand(
                pollCdb,
                buf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);

            if (!pollRes.IsSuccess)
            {
                failureReason = $"0xF3 poll failed at LBA {prevLba}: {pollRes.ErrorMessage}";
                return;
            }

            if (!MediaTekQualityCommands.TryParseF3Reply(
                    buf.AsSpan(0, (int)Math.Min((uint)buf.Length, pollRes.DataTransferLength)),
                    mediaKind,
                    out int lba,
                    out int primaryErr,
                    out int secondaryErr))
            {
                failureReason = $"0xF3 reply invalid or truncated ({pollRes.DataTransferLength} bytes) at LBA {prevLba}.";
                return;
            }

            if (mediaKind == QualityScanMediaKind.BluRay && lba == 0 && !bdZeroRetried)
            {
                bdZeroRetried = true;
                continue;
            }

            if (lba <= prevLba)
            {
                if (++staleCount >= MaxConsecutiveStalePolls)
                {
                    failureReason = $"Drive stopped advancing during 0xF3 quality scan at LBA {prevLba}.";
                    return;
                }
                continue;
            }

            staleCount = 0;
            int clampedEnd = Math.Min(endLba, lba);
            int sectorCount = Math.Max(1, clampedEnd - prevLba);
            (double speedX, double speedKb) = ComputeSpeed(mediaKind, sectorCount, intervalClock.Elapsed);

            QualityScanIntervalSample sample = mediaKind switch
            {
                QualityScanMediaKind.Dvd => new QualityScanIntervalSample(prevLba, clampedEnd, sectorCount, speedX, speedKb, Pie: primaryErr, Pif: secondaryErr, Poe: 0, Pof: 0),
                QualityScanMediaKind.BluRay => new QualityScanIntervalSample(prevLba, clampedEnd, sectorCount, speedX, speedKb, Ldc: primaryErr, Bis: secondaryErr),
                _ => new QualityScanIntervalSample(prevLba, clampedEnd, sectorCount, speedX, speedKb, C1: primaryErr, C2: secondaryErr, Cu: 0)
            };

            samples.Add(sample);
            prevLba = clampedEnd;

            double pct = Math.Clamp(100.0 * (prevLba - startLba) / totalToScan, 0.0, 100.0);
            progress?.Report(new QualityScanProgressSnapshot(pct, prevLba, endLba, sample, $"Scanning {mediaKind} (0xF3): {pct:F0}% (LBA {prevLba:N0})"));
        }
    }

    private void RunScanExitTeardown(bool useLegacyDf)
    {
        try
        {
            if (useLegacyDf)
            {
                byte[] stopBuf = new byte[MediaTekQualityCommands.LegacyScanReplyLength];
                _ = _transport.ExecuteCommand(
                    MediaTekQualityCommands.BuildDfEndCdb(),
                    stopBuf,
                    ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                    _commandTimeout);
            }
            else
            {
                _ = _transport.ExecuteCommand(
                    MediaTekQualityCommands.BuildSeek10Cdb(0),
                    Span<byte>.Empty,
                    ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                    _commandTimeout);
            }
        }
        catch
        {
            // Never mask the scan outcome or cancellation exception during mandatory teardown.
        }

        try
        {
            _ = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildTestUnitReadyCdb(),
                Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                _commandTimeout);
        }
        catch
        {
        }
    }

    private ScsiExecutionResult ExecuteTestUnitReadyHandlingUnitAttention()
    {
        var tur = _transport.ExecuteCommand(
            MmcCommandBuilder.BuildTestUnitReadyCdb(),
            Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
            _commandTimeout);
        if (!tur.IsSuccess && tur.SenseInfo is { SenseKey: 0x06, Asc: 0x28 })
        {
            tur = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildTestUnitReadyCdb(),
                Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED,
                _commandTimeout);
        }
        return tur;
    }

    private bool IsDiscBlank()
    {
        byte[] discInfo = new byte[34];
        byte[] cdb = new byte[10];
        cdb[0] = MmcCommandBuilder.OpCodeReadDiscInformation;
        cdb[8] = (byte)discInfo.Length;
        var res = _transport.ExecuteCommand(cdb, discInfo, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, _commandTimeout);
        if (!res.IsSuccess || res.DataTransferLength < 3)
        {
            return false;
        }
        int discStatus = discInfo[2] & 0x03;
        return discStatus == 0;
    }

    private int QueryTotalSectors(QualityScanMediaKind mediaKind)
    {
        if (mediaKind == QualityScanMediaKind.Cd)
        {
            byte[] tocBuf = new byte[4 + (8 * 100)];
            var tocRes = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildReadTocPmaAtipCdb(MmcCommandBuilder.TocFormatFormattedToc, 0, (ushort)tocBuf.Length),
                tocBuf,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                _commandTimeout);
            if (tocRes.IsSuccess && tocRes.DataTransferLength >= 12)
            {
                var tracks = MmcResponseParser.ParseToc(tocBuf.AsSpan(0, (int)Math.Min((uint)tocBuf.Length, tocRes.DataTransferLength)));
                var leadOut = tracks.LastOrDefault(t => t.IsLeadOut);
                if (leadOut != null && leadOut.StartLba > 0)
                {
                    return leadOut.StartLba;
                }
            }
        }

        byte[] capBuf = new byte[8];
        var capRes = _transport.ExecuteCommand(
            MediaTekQualityCommands.BuildReadCapacity10Cdb(),
            capBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            _commandTimeout);
        if (capRes.IsSuccess && capRes.DataTransferLength >= 4)
        {
            uint lastLba = BinaryPrimitives.ReadUInt32BigEndian(capBuf.AsSpan(0, 4));
            if (lastLba < int.MaxValue - 1)
            {
                return (int)lastLba + 1;
            }
        }

        return 0;
    }

    private static (double SpeedX, double SpeedKbPerSec) ComputeSpeed(
        QualityScanMediaKind mediaKind,
        int sectorCount,
        TimeSpan elapsed)
    {
        double oneXKb = OneXKilobytesPerSec(mediaKind);
        double sec = elapsed.TotalSeconds;
        if (sec <= 0.0005)
        {
            double defaultX = mediaKind switch
            {
                QualityScanMediaKind.Dvd => 8.0,
                QualityScanMediaKind.BluRay => 4.0,
                _ => 24.0
            };
            return (defaultX, Math.Round(defaultX * oneXKb, 1));
        }

        double sectorKb = mediaKind == QualityScanMediaKind.Cd ? 2.352 : 2.048;
        double kbPerSec = (sectorCount * sectorKb) / sec;
        double speedX = Math.Clamp(kbPerSec / oneXKb, 0.1, 100.0);
        return (Math.Round(speedX, 2), Math.Round(kbPerSec, 1));
    }

    private static double OneXKilobytesPerSec(QualityScanMediaKind kind) => kind switch
    {
        QualityScanMediaKind.Dvd => 1385.0,
        QualityScanMediaKind.BluRay => 4500.0,
        _ => 150.0
    };

    private static QualityScanMediaKind? MapProfileToMediaKind(ushort profile) => profile switch
    {
        >= 0x0008 and <= 0x000A => QualityScanMediaKind.Cd,
        >= 0x0010 and <= 0x001B => QualityScanMediaKind.Dvd,
        >= 0x0040 and <= 0x0043 => QualityScanMediaKind.BluRay,
        _ => null
    };

    private static bool IsInvalidCommandSense(ScsiExecutionResult res) =>
        res.SenseInfo is { SenseKey: 0x05, Asc: 0x20 or 0x24 };

    private QualityScanReport BuildNonScanResult(
        QualityScanStatus status,
        MmcInquiryResponse inquiry,
        QualityScanMediaKind mediaKind,
        string? mediaType,
        int totalSectors,
        string message,
        TimeSpan elapsed,
        QualityScanProtocol protocol = QualityScanProtocol.None) => new(
        SchemaVersion: QualityScanReport.CurrentSchemaVersion,
        CreatedUtc: DateTimeOffset.UtcNow,
        IsSupported: status != QualityScanStatus.UnsupportedDrive,
        Status: status,
        IsExperimentalHardwareScan: true,
        StatusMessage: message,
        DriveVendor: inquiry.VendorId,
        DriveProduct: inquiry.ProductId,
        DriveRevision: inquiry.ProductRevision,
        DriverName: DriverName,
        Protocol: protocol,
        MediaKind: mediaKind,
        MediaType: mediaType,
        MediaId: _mediaId,
        TotalDiscSectors: totalSectors,
        Summary: QualityScanSummary.Empty(QualityScanGrade.NotEvaluated, message),
        Samples: Array.Empty<QualityScanIntervalSample>(),
        Elapsed: elapsed);

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
