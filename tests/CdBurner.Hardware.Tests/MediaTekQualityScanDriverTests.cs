using CdBurner.Core.Primitives;
using CdBurner.Core.Verification;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Scsi.QualityScan;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class MediaTekQualityScanDriverTests
{
    [Fact]
    public void MediaTekCommands_MatchQPxToolCdbBytesAndReplyOffsets()
    {
        byte[] f3Cd = MediaTekQualityCommands.BuildF3ErrcCdb(QualityScanMediaKind.Cd);
        Assert.Equal(12, f3Cd.Length);
        Assert.Equal(0xF3, f3Cd[0]);
        Assert.Equal(0x0E, f3Cd[1]);
        Assert.Equal(0x00, f3Cd[8]);

        byte[] f3Dvd = MediaTekQualityCommands.BuildF3ErrcCdb(QualityScanMediaKind.Dvd);
        Assert.Equal(0xF3, f3Dvd[0]);
        Assert.Equal(0x0E, f3Dvd[1]);
        Assert.Equal(0x10, f3Dvd[8]);

        // CD 0xF3 reply: bytes 1..3 = M, S, F; bytes 4..5 = BLER (BE16); bytes 6..7 = E22 (BE16)
        byte[] cdReply = [0x00, 0x01, 0x02, 0x05, 0x00, 0x2A, 0x00, 0x03, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(MediaTekQualityCommands.TryParseF3Reply(cdReply, QualityScanMediaKind.Cd, out int cdLba, out int c1, out int c2));
        Assert.Equal((1 * 60 * 75) + (2 * 75) + 5, cdLba);
        Assert.Equal(42, c1);
        Assert.Equal(3, c2);

        // DVD 0xF3 reply: bytes 0..3 = LBA (BE32); bytes 4..5 = PIE (BE16); bytes 6..7 = PIF (BE16)
        byte[] dvdReply = [0x00, 0x00, 0x01, 0x80, 0x00, 0x55, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(MediaTekQualityCommands.TryParseF3Reply(dvdReply, QualityScanMediaKind.Dvd, out int dvdLba, out int pie, out int pif));
        Assert.Equal(0x180, dvdLba);
        Assert.Equal(85, pie);
        Assert.Equal(2, pif);

        // BD 0xF3 reply: bytes 0..3 = LBA (BE32); bytes 4..5 = LDC; bytes 6..7 = BIS; out-of-range clamp
        byte[] bdValid = [0x00, 0x00, 0x02, 0x00, 0x00, 0x7D, 0x00, 0x04, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(MediaTekQualityCommands.TryParseF3Reply(bdValid, QualityScanMediaKind.BluRay, out int bdLba, out int ldc, out int bis));
        Assert.Equal(0x200, bdLba);
        Assert.Equal(125, ldc);
        Assert.Equal(4, bis);

        byte[] bdClamp = [0x00, 0x00, 0x03, 0x00, 0x30, 0x00, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(MediaTekQualityCommands.TryParseF3Reply(bdClamp, QualityScanMediaKind.BluRay, out _, out int clampedLdc, out int clampedBis));
        Assert.Equal(0, clampedLdc);
        Assert.Equal(0, clampedBis);

        // Legacy 0xDF readout: bytes 0..1 = BLER, bytes 2..3 = E22, byte 4 = E32/CU
        byte[] dfReply = new byte[256];
        dfReply[0] = 0x01;
        dfReply[1] = 0x04;
        dfReply[2] = 0x00;
        dfReply[3] = 0x07;
        dfReply[4] = 0x02;
        Assert.True(MediaTekQualityCommands.TryParseDfCdCounters(dfReply, out int dfC1, out int dfC2, out int dfCu));
        Assert.Equal(260, dfC1);
        Assert.Equal(7, dfC2);
        Assert.Equal(2, dfCu);

        byte[] dfEnd = MediaTekQualityCommands.BuildDfEndCdb();
        Assert.Equal(10, dfEnd.Length);
        Assert.Equal(0xDF, dfEnd[0]);
        Assert.Equal(0xA3, dfEnd[1]);
        Assert.Equal(0x01, dfEnd[2]);
    }

    [Theory]
    [InlineData("HL-DT-ST", "DVDRAM GP60NB50")]
    [InlineData("HL-DT-ST", "BD-RE BP50NB40")]
    [InlineData("PIONEER", "DVD-RW  DVR-111")]
    [InlineData("IGNIS", "VIRTUAL DVD/CD-RW")]
    public async Task UnlistedDrive_ReceivesZeroVendorCdbs_AndReturnsCleanUnsupportedStatus(string vendor, string product)
    {
        var drive = new VirtualMmcDrive
        {
            InquiryVendorId = vendor,
            InquiryProductId = product,
            InquiryProductRevision = "RF02",
            DiscStatus = 2
        };

        bool lockAcquired = false;
        using var driver = new MediaTekLiteOnQualityDriver(
            drive,
            acquireExclusiveAccess: () => { lockAcquired = true; return null; });

        var service = new QualityScanService();
        var report = await service.RunScanAsync(driver);

        Assert.False(report.IsSupported);
        Assert.Equal(QualityScanStatus.UnsupportedDrive, report.Status);
        Assert.False(lockAcquired);
        Assert.Equal(new byte[] { MmcCommandBuilder.OpCodeInquiry }, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0xF3, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0xDF, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0xE9, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0xEB, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0x3B, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0x3C, drive.CommandsReceived);
        Assert.DoesNotContain((byte)0xF8, drive.CommandsReceived);
    }

    [Fact]
    public async Task AllowListedLiteOn_F3CdDvdBdScans_AcquireLock_EnforceTimeout_AndRunFinallyTeardown()
    {
        // 1. CD scan (0xF3)
        var cdDrive = BuildRecordedAllowListedDrive(NativeSaoAudioWriter.ProfileCdR, 300);
        cdDrive.MarginalLbas.Add(180);
        var spy = new SpyTransport(cdDrive);
        var lockTracker = new LockTracker();

        using (var driver = new MediaTekLiteOnQualityDriver(spy, acquireExclusiveAccess: lockTracker.Acquire))
        {
            var report = await new QualityScanService().RunScanAsync(driver);
            Assert.True(report.IsSupported);
            Assert.Equal(QualityScanStatus.Completed, report.Status);
            Assert.True(report.IsExperimentalHardwareScan);
            Assert.Equal(QualityScanProtocol.MediaTekF3, report.Protocol);
            Assert.Equal(QualityScanMediaKind.Cd, report.MediaKind);
            Assert.Equal(4, report.Samples.Count);
            Assert.True(report.Summary.TotalC1 > 0);
            Assert.True(report.Summary.TotalC2 > 0);
            Assert.True(lockTracker.Acquired);
            Assert.True(lockTracker.Disposed);
            Assert.True(cdDrive.QualityScanStopCommandCount >= 1);
            Assert.True(cdDrive.QualityScanTeardownReadyCount >= 1);
            Assert.All(spy.Timeouts, t => Assert.Equal(TimeSpan.FromSeconds(10), t));
        }

        // 2. DVD scan (0xF3 with cdb[8] == 0x10)
        var dvdDrive = BuildRecordedAllowListedDrive(NativeTaoDataWriter.ProfileDvdMinusR, 512);
        dvdDrive.MarginalLbas.Add(300);
        var dvdSpy = new SpyTransport(dvdDrive);
        using (var driver = new MediaTekLiteOnQualityDriver(dvdSpy))
        {
            var report = await new QualityScanService().RunScanAsync(driver);
            Assert.Equal(QualityScanStatus.Completed, report.Status);
            Assert.Equal(QualityScanMediaKind.Dvd, report.MediaKind);
            Assert.Contains(dvdSpy.Cdbs, c => c[0] == 0xF3 && c[1] == 0x0E && c[8] == 0x10);
            Assert.True(report.Summary.TotalPie > 0);
            Assert.True(report.Summary.TotalPif > 0);
        }

        // 3. BD scan (0xF3)
        var bdDrive = BuildRecordedAllowListedDrive(NativeTaoDataWriter.ProfileBdR, 128);
        bdDrive.MarginalLbas.Add(40);
        using (var driver = new MediaTekLiteOnQualityDriver(bdDrive))
        {
            var report = await new QualityScanService().RunScanAsync(driver);
            Assert.Equal(QualityScanStatus.Completed, report.Status);
            Assert.Equal(QualityScanMediaKind.BluRay, report.MediaKind);
            Assert.True(report.Summary.TotalLdc > 0);
            Assert.True(report.Summary.TotalBis > 0);
        }
    }

    [Fact]
    public async Task AllowListedCd_FallsBackToLegacyDf_WhenF3Rejected_AndRunsStopInFinally()
    {
        var drive = BuildRecordedAllowListedDrive(NativeSaoAudioWriter.ProfileCdR, 300);
        drive.RejectsQualityScanF3 = true;
        drive.MarginalLbas.Add(160);
        var spy = new SpyTransport(drive);

        using var driver = new MediaTekLiteOnQualityDriver(spy);
        var report = await new QualityScanService().RunScanAsync(driver);

        Assert.Equal(QualityScanStatus.Completed, report.Status);
        Assert.Equal(QualityScanProtocol.MediaTekDf, report.Protocol);
        Assert.Equal(4, report.Samples.Count);
        Assert.Contains(spy.Cdbs, c => c[0] == 0xDF && c[1] == 0xA3 && c[2] == 0x01);
        Assert.True(drive.QualityScanStopCommandCount >= 1);
        Assert.True(drive.QualityScanTeardownReadyCount >= 1);
    }

    [Fact]
    public async Task FaultInjection_And_Cancellation_AlwaysRunExitTeardownInFinally()
    {
        // 1. Sense error mid-scan on 0xDF: aborts immediately and sends DF A3 01 + TEST UNIT READY in finally.
        var senseDrive = BuildRecordedAllowListedDrive(NativeSaoAudioWriter.ProfileCdR, 300);
        senseDrive.RejectsQualityScanF3 = true;
        senseDrive.QualityScanErrorAtLba = 150;
        var senseSpy = new SpyTransport(senseDrive);
        var senseLock = new LockTracker();

        using (var driver = new MediaTekLiteOnQualityDriver(senseSpy, acquireExclusiveAccess: senseLock.Acquire))
        {
            var report = await new QualityScanService().RunScanAsync(driver);
            Assert.Equal(QualityScanStatus.Failed, report.Status);
            Assert.Contains(senseSpy.Cdbs, c => c[0] == 0xDF && c[1] == 0xA3 && c[2] == 0x01);
            Assert.Equal(0x00, senseSpy.Cdbs[^1][0]);
            Assert.True(senseLock.Disposed);
        }

        // 2. Win32 error mid-scan on 0xF3: aborts immediately and runs teardown in finally.
        var winErrDrive = BuildRecordedAllowListedDrive(NativeSaoAudioWriter.ProfileCdR, 300);
        winErrDrive.QualityScanWin32ErrorAtPoll = 2;
        var winSpy = new SpyTransport(winErrDrive);

        using (var driver = new MediaTekLiteOnQualityDriver(winSpy))
        {
            var report = await new QualityScanService().RunScanAsync(driver);
            Assert.Equal(QualityScanStatus.Failed, report.Status);
            Assert.Equal(1, winErrDrive.QualityScanStopCommandCount);
            Assert.Equal(0x00, winSpy.Cdbs[^1][0]);
        }

        // 3. Cancellation mid-scan on 0xDF: throws OperationCanceledException, still sends DF A3 01 + TEST UNIT READY,
        //    and leaves the drive ready for READ TOC and READ CD.
        var cancelDrive = BuildRecordedAllowListedDrive(NativeSaoAudioWriter.ProfileCdR, 450);
        cancelDrive.RejectsQualityScanF3 = true;
        var cancelSpy = new SpyTransport(cancelDrive);
        var cancelLock = new LockTracker();
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<QualityScanProgressSnapshot>(_ => cts.Cancel());
        using (var driver = new MediaTekLiteOnQualityDriver(cancelSpy, acquireExclusiveAccess: cancelLock.Acquire))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await new QualityScanService().RunScanAsync(driver, new QualityScanOptions(), progress, cts.Token));
        }

        Assert.True(cancelLock.Disposed);
        Assert.Contains(cancelSpy.Cdbs, c => c[0] == 0xDF && c[1] == 0xA3 && c[2] == 0x01);
        Assert.Equal(0x00, cancelSpy.Cdbs[^1][0]);

        byte[] tocBuf = new byte[128];
        var tocRes = cancelDrive.ExecuteCommand(
            MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 0, (ushort)tocBuf.Length),
            tocBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            TimeSpan.FromSeconds(5));
        Assert.True(tocRes.IsSuccess);

        byte[] rawCdBuf = new byte[2352];
        var readCdRes = cancelDrive.ExecuteCommand(
            MmcCommandBuilder.BuildReadCdCdb(0, 1, 0, 0xF8),
            rawCdBuf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            TimeSpan.FromSeconds(5));
        Assert.True(readCdRes.IsSuccess);

        // 4. Cancellation mid-scan on 0xF3 DVD: runs SEEK(0) + TEST UNIT READY in finally and leaves drive ready for READ(10).
        var dvdCancelDrive = BuildRecordedAllowListedDrive(NativeTaoDataWriter.ProfileDvdMinusR, 512);
        using var dvdCts = new CancellationTokenSource();
        var dvdProgress = new SyncProgress<QualityScanProgressSnapshot>(_ => dvdCts.Cancel());
        using (var driver = new MediaTekLiteOnQualityDriver(dvdCancelDrive))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await new QualityScanService().RunScanAsync(driver, new QualityScanOptions(), dvdProgress, dvdCts.Token));
        }
        Assert.True(dvdCancelDrive.QualityScanStopCommandCount >= 1);
        Assert.True(dvdCancelDrive.QualityScanTeardownReadyCount >= 1);

        byte[] read10Buf = new byte[2048];
        var read10Res = dvdCancelDrive.ExecuteCommand(
            MmcCommandBuilder.BuildRead10Cdb(0, 1),
            read10Buf,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            TimeSpan.FromSeconds(5));
        Assert.True(read10Res.IsSuccess);
    }

    [Fact]
    public async Task VirtualQualityScanDriver_ProducesDeterministicReportsForCdDvdAndBd()
    {
        using var recorder = new VirtualDiscRecorder();

        using var cd = new VirtualDiscMedium(DiscMediaType.CdR, 300, isBlank: false) { UsedSectors = 300 };
        cd.SimulatedBadLbas.Add(200);
        recorder.InsertMedia(cd);
        var cdReport = await new VirtualQualityScanDriver(recorder).ScanAsync(new QualityScanOptions());
        Assert.Equal(QualityScanStatus.Completed, cdReport.Status);
        Assert.Equal(QualityScanMediaKind.Cd, cdReport.MediaKind);
        Assert.True(cdReport.Summary.TotalC1 > 0);
        Assert.True(cdReport.Summary.TotalC2 > 0);

        using var dvd = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 512, isBlank: false) { UsedSectors = 512 };
        dvd.SimulatedBadLbas.Add(100);
        recorder.InsertMedia(dvd);
        var dvdReport = await new VirtualQualityScanDriver(recorder).ScanAsync(new QualityScanOptions());
        Assert.Equal(QualityScanStatus.Completed, dvdReport.Status);
        Assert.Equal(QualityScanMediaKind.Dvd, dvdReport.MediaKind);
        Assert.True(dvdReport.Summary.TotalPie > 0);
        Assert.True(dvdReport.Summary.TotalPif > 0);

        using var bd = new VirtualDiscMedium(DiscMediaType.BdR, 256, isBlank: false) { UsedSectors = 256 };
        bd.SimulatedBadLbas.Add(64);
        recorder.InsertMedia(bd);
        var bdReport = await new VirtualQualityScanDriver(recorder).ScanAsync(new QualityScanOptions());
        Assert.Equal(QualityScanStatus.Completed, bdReport.Status);
        Assert.Equal(QualityScanMediaKind.BluRay, bdReport.MediaKind);
        Assert.True(bdReport.Summary.TotalLdc > 0);
        Assert.True(bdReport.Summary.TotalBis > 0);
    }

    private static VirtualMmcDrive BuildRecordedAllowListedDrive(ushort profile, int sectorCount)
    {
        bool isCd = profile == NativeSaoAudioWriter.ProfileCdR;
        var drive = new VirtualMmcDrive
        {
            InquiryVendorId = "ATAPI",
            InquiryProductId = "iHAS124   F",
            InquiryProductRevision = "CL9E",
            Profile = profile,
            DiscStatus = isCd ? 0 : 2,
            CapacitySectors = sectorCount
        };
        if (isCd)
        {
            byte[] pcm = new byte[sectorCount * 2352];
            new Random(7).NextBytes(pcm);
            var req = new NativeSaoWriteRequest(
                [new SaoTrack(1, sectorCount)],
                _ => new MemoryStream(pcm),
                null,
                null,
                TestWrite: false);
            var res = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(req);
            Assert.True(res.Success, res.Message);
        }
        else
        {
            drive.DataDiscImage = new byte[sectorCount * 2048];
        }
        return drive;
    }

    private sealed class SpyTransport(IScsiTransport inner) : IScsiTransport
    {
        public List<byte[]> Cdbs { get; } = new();
        public List<TimeSpan> Timeouts { get; } = new();

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
        {
            Cdbs.Add(cdb.ToArray());
            Timeouts.Add(timeout);
            return inner.ExecuteCommand(cdb, dataBuffer, dataDirection, timeout);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class LockTracker
    {
        public bool Acquired { get; private set; }
        public bool Disposed { get; private set; }

        public IDisposable Acquire()
        {
            Acquired = true;
            return new Token(() => Disposed = true);
        }

        private sealed class Token(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
