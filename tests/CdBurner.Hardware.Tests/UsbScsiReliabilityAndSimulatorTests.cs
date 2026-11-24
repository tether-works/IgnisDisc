using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Direct-writer reliability and simulator fidelity tests: DVD+RW partly-formatted restart,
/// cancel during FORMAT UNIT without flush, bounded transient USB SCSI retries, 0x54 SEND OPC,
/// LastSessionState == 1 close-session recovery, 64 KiB mixed-mode shrink, Mode Page 0x05 header
/// disambiguation, and VirtualMmcDrive / VirtualBurnEngine fidelity checks.
/// </summary>
public sealed class UsbScsiReliabilityAndSimulatorTests
{
    private const int DvdCapacity = OpticalConstants.DvdPlusR5Sectors;
    private const int Speed4x = 5_540;

    private static byte[] DvdSpeeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], DvdCapacity - 1);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static byte[] Bytes(int length, int seed = 42)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] IsoImage(int sectors, int seed = 7, int sessionStartLba = 0)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("DATA.BIN", () => new MemoryStream(Bytes(Math.Max(1, sectors - 40) * 2048, seed))));
        using var ms = new MemoryStream();
        IsoUdfImageBuilder.Build(root, ms, new IsoBuildOptions(VolumeLabel: "TEST", IncludeUdf: false), sessionStartLba);
        return ms.ToArray();
    }

    // ---- 1. DVD+RW partly-formatted restart ----

    [Fact]
    public void VirtualMmcDrive_DvdPlusRw_BackgroundFormat1_RefusesWriteUntilRestartFormatRuns()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdPlusRw,
            CapacitySectors = DvdCapacity,
            BackgroundFormatStatus = 1,
            FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(DvdCapacity, formatted: true)
        };

        byte[] sector = new byte[2048];
        var writeBeforeFormat = drive.ExecuteCommand(
            NativeWriteCommands.BuildWrite10(0, 1), sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));
        Assert.False(writeBeforeFormat.IsSuccess);
        Assert.Equal((byte)0x05, writeBeforeFormat.SenseInfo?.SenseKey);
        Assert.Equal((byte)0x30, writeBeforeFormat.SenseInfo?.Asc);
        Assert.Equal((byte)0x10, writeBeforeFormat.SenseInfo?.Ascq);

        var formatNoRestart = drive.ExecuteCommand(
            NativeWriteCommands.BuildFormatUnit(),
            NativeWriteCommands.BuildDvdPlusRwFormatParameters(restart: false),
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT,
            TimeSpan.FromSeconds(5));
        Assert.False(formatNoRestart.IsSuccess);
        Assert.Equal((byte)0x26, formatNoRestart.SenseInfo?.Asc);

        var formatRestart = drive.ExecuteCommand(
            NativeWriteCommands.BuildFormatUnit(),
            NativeWriteCommands.BuildDvdPlusRwFormatParameters(restart: true),
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT,
            TimeSpan.FromSeconds(5));
        Assert.True(formatRestart.IsSuccess, formatRestart.SenseInfo?.ToString());
        Assert.Equal(2, drive.BackgroundFormatStatus);

        var tur = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        Assert.False(tur.IsSuccess); // 2/04/01 once from Immed
        var tur2 = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        Assert.True(tur2.IsSuccess);

        var writeAfterFormat = drive.ExecuteCommand(
            NativeWriteCommands.BuildWrite10(0, 1), sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));
        Assert.True(writeAfterFormat.IsSuccess, writeAfterFormat.SenseInfo?.ToString());
    }

    [Fact]
    public void NativeTaoDataWriter_DvdPlusRw_PartlyFormattedWithExistingFs_StillRequiresOverwriteFlag()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdPlusRw,
            CapacitySectors = DvdCapacity,
            BackgroundFormatStatus = 1,
            FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(DvdCapacity, formatted: true),
            WriteSpeedPerformanceResponse = DvdSpeeds(Speed4x)
        };
        byte[] old = new byte[400 * 2048];
        old[16 * 2048] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(old, 16 * 2048 + 1);
        drive.DataDiscImage = (byte[])old.Clone();

        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        byte[] next = Bytes(320 * 2048, seed: 3);

        var refused = writer.Write(new NativeDataWriteRequest(new MemoryStream(next), 0, next.Length, TestWrite: false, OverwriteExistingData: false));
        Assert.False(refused.Success);
        Assert.True(refused.StoppedBeforeWriting);
        Assert.Equal(0, drive.FormatUnitCount);

        var allowed = writer.Write(new NativeDataWriteRequest(new MemoryStream(next), 0, next.Length, TestWrite: false, OverwriteExistingData: true));
        Assert.True(allowed.Success, allowed.Message);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal((byte)1, drive.LastFormatParameters![11]);
    }

    // ---- 2. Cancel during FORMAT UNIT does not issue SYNCHRONIZE CACHE ----

    [Fact]
    public void CancelDuringFormatUnit_DoesNotSendSynchronizeCache()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdRam,
            CapacitySectors = 2_295_104,
            RamFormatted = false,
            RamFormatBusyPolls = 4,
            WriteSpeedPerformanceResponse = DvdSpeeds(Speed4x)
        };
        using var cts = new CancellationTokenSource();
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        byte[] image = Bytes(320 * 2048, seed: 9);

        var progress = new CallbackProgress(snap =>
        {
            if (snap.CurrentActionDescription.StartsWith("Formatting", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        });

        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false), progress, cts.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(result.StoppedBeforeWriting);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.DoesNotContain(MmcCommandBuilder.OpCodeSynchronizeCache10, drive.CommandsReceived);
    }

    // ---- 3. Bounded transient USB SCSI retries ----

    [Theory]
    [InlineData(0x02, 0x04, 0x01)] // Logical Unit Is in Process of Becoming Ready
    [InlineData(0x0B, 0x08, 0x00)] // Logical Unit Communication Failure
    [InlineData(0x0B, 0x08, 0x03)] // Logical Unit Communication CRC Error
    [InlineData(0x03, 0x02, 0x00)] // No Seek Complete
    [InlineData(0x06, 0x29, 0x00)] // Power On, Reset, or Bus Device Reset Occurred
    public void NativeTaoDataWriter_RetriesTransientUsbWriteErrors_AndSucceeds(byte key, byte asc, byte ascq)
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        var fault = new TransientWriteFaultTransport(drive, key, asc, ascq, failuresToInject: 2);
        byte[] image = Bytes(350 * 2048, seed: 14);

        var result = new NativeTaoDataWriter(fault, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false));

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, fault.InjectedCount);
        Assert.Equal(image, drive.DataDiscImage!.AsSpan(0, image.Length).ToArray());
    }

    [Theory]
    [InlineData(0x02, 0x04, 0x01)]
    [InlineData(0x0B, 0x08, 0x00)]
    [InlineData(0x06, 0x29, 0x00)]
    public void NativeSaoAudioWriter_RetriesTransientUsbWriteErrors_AndSucceeds(byte key, byte asc, byte ascq)
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        var fault = new TransientWriteFaultTransport(drive, key, asc, ascq, failuresToInject: 2);
        byte[] pcm = Bytes(300 * 2352, seed: 22);

        var result = new NativeSaoAudioWriter(fault, sleep: _ => { }).Write(
            new NativeSaoWriteRequest(new[] { new SaoTrack(1, 300) }, _ => new MemoryStream(pcm), null, null, TestWrite: false));

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, fault.InjectedCount);
    }

    [Fact]
    public void PersistentTransientError_StopsAfterBoundedRetries_AndRespectsCancellation()
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        var fault = new TransientWriteFaultTransport(drive, 0x0B, 0x08, 0x00, failuresToInject: 100);
        byte[] image = Bytes(350 * 2048, seed: 15);

        var failed = new NativeTaoDataWriter(fault, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false));

        Assert.False(failed.Success);
        Assert.Equal(1 + NativeWriteCommands.MaxTransientRetries, fault.InjectedCount);

        using var cts = new CancellationTokenSource();
        var drive2 = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        var cancelFault = new TransientWriteFaultTransport(drive2, 0x0B, 0x08, 0x00, failuresToInject: 100, onInject: () => cts.Cancel());
        var cancelled = new NativeTaoDataWriter(cancelFault, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false), null, cts.Token);

        Assert.False(cancelled.Success);
        Assert.True(cancelled.Cancelled);
        Assert.Equal(1, cancelFault.InjectedCount);
    }

    // ---- 4. 0x54 SEND OPC INFORMATION ----

    [Fact]
    public void BuildSendOpcInformationCdb_MatchesMmc5Layout()
    {
        // MMC-5 6.36: 10-byte CDB, byte 0 = 0x54, byte 1 bit 0 = DoOpc, bytes 7..8 = Parameter List Length.
        byte[] doOpc = MmcCommandBuilder.BuildSendOpcInformationCdb(doOpc: true);
        Assert.Equal(new byte[] { 0x54, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, doOpc);

        byte[] table = MmcCommandBuilder.BuildSendOpcInformationCdb(doOpc: false, parameterListLength: 0x0120);
        Assert.Equal(new byte[] { 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x20, 0x00 }, table);
    }

    [Fact]
    public void PreWriteOpc_RunsOnRealWriteForCdAndDvdMinusR_SkipsTestWriteAndPlusR_AndToleratesRefusal()
    {
        // CD-R real write sends 0x54 (even if drive rejects it); test write does not.
        var cdTest = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        Assert.True(new NativeSaoAudioWriter(cdTest, sleep: _ => { }).Write(
            new NativeSaoWriteRequest(new[] { new SaoTrack(1, 300) }, _ => new MemoryStream(Bytes(300 * 2352)), null, null, TestWrite: true)).Success);
        Assert.Equal(0, cdTest.SendOpcCount);
        Assert.DoesNotContain(MmcCommandBuilder.OpCodeSendOpcInformation, cdTest.CommandsReceived);

        var cdReal = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, RejectsSendOpc = true };
        Assert.True(new NativeSaoAudioWriter(cdReal, sleep: _ => { }).Write(
            new NativeSaoWriteRequest(new[] { new SaoTrack(1, 300) }, _ => new MemoryStream(Bytes(300 * 2352)), null, null, TestWrite: false)).Success);
        Assert.Contains(MmcCommandBuilder.OpCodeSendOpcInformation, cdReal.CommandsReceived);
        Assert.Equal(0, cdReal.SendOpcCount); // refused non-fatally

        // DVD-R real write sends 0x54; DVD+R does not.
        var dvdMinusR = new VirtualMmcDrive { Profile = NativeTaoDataWriter.ProfileDvdMinusR, CapacitySectors = DvdCapacity, WriteSpeedPerformanceResponse = DvdSpeeds(Speed4x) };
        byte[] dvdImg = Bytes(320 * 2048);
        Assert.True(new NativeTaoDataWriter(dvdMinusR, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(dvdImg), 0, dvdImg.Length, TestWrite: false)).Success);
        Assert.Equal(1, dvdMinusR.SendOpcCount);

        var dvdPlusR = new VirtualMmcDrive { Profile = NativeTaoDataWriter.ProfileDvdPlusR, CapacitySectors = DvdCapacity, WriteSpeedPerformanceResponse = DvdSpeeds(Speed4x) };
        Assert.True(new NativeTaoDataWriter(dvdPlusR, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(dvdImg), 0, dvdImg.Length, TestWrite: false)).Success);
        Assert.Equal(0, dvdPlusR.SendOpcCount);
    }

    // ---- 5. LastSessionState == 1 close-session recovery ----

    [Fact]
    public void LastSessionState1_IsRecoveredWithCloseSession_OnStandaloneRecoveryAndSessionAppend()
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };
        byte[] session1 = IsoImage(320, seed: 5, sessionStartLba: 0);

        // Leave session 1 flushed (SYNCHRONIZE CACHE) with multi-session 11b, but omit CLOSE SESSION.
        var modePage = WriteParametersPage.FromPageBytes(drive.CurrentModePage05.ToArray())
            .ForTrackAtOnceData(testWrite: false, bufferUnderrunFree: true, closeDisc: false);
        Assert.True(drive.ExecuteCommand(NativeWriteCommands.BuildModeSelect10((ushort)modePage.ToModeSelect10ParameterList().Length),
            modePage.ToModeSelect10ParameterList(), ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5)).IsSuccess);
        int s1Sectors = session1.Length / 2048;
        for (int lba = 0; lba < s1Sectors; lba += 16)
        {
            int n = Math.Min(16, s1Sectors - lba);
            Assert.True(drive.ExecuteCommand(NativeWriteCommands.BuildWrite10(lba, (ushort)n),
                session1.AsSpan(lba * 2048, n * 2048), ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5)).IsSuccess);
        }
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildSynchronizeCacheCdb(), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5)).IsSuccess);

        // Verify READ DISC INFORMATION reports Status = 1, LastSessionState = 1.
        byte[] discInfo = new byte[34];
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), discInfo,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5)).IsSuccess);
        Assert.Equal(1, discInfo[2] & 0x03);
        Assert.Equal(1, (discInfo[2] >> 2) & 0x03);

        int expectedNextStart = s1Sectors + 2 + VirtualMmcDrive.CdFirstSessionGapSectors;
        byte[] session2 = IsoImage(320, seed: 6, sessionStartLba: expectedNextStart);
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });

        // Test write must not mutate an incomplete session.
        var testRefused = writer.Write(new NativeDataWriteRequest(new MemoryStream(session2), 0, session2.Length, TestWrite: true, SessionStartLba: expectedNextStart));
        Assert.False(testRefused.Success);
        Assert.True(testRefused.StoppedBeforeWriting);

        // Real append closes the incomplete session first, discovers the new NWA, and writes session 2.
        var recoveredAppend = writer.Write(new NativeDataWriteRequest(new MemoryStream(session2), 0, session2.Length, TestWrite: false, CloseDisc: true, SessionStartLba: expectedNextStart));
        Assert.True(recoveredAppend.Success, recoveredAppend.Message);
        Assert.Equal(2, drive.DataSessions.Count);
        Assert.Equal(expectedNextStart, drive.DataSessions[1].TrackStartLba);
    }

    [Fact]
    public void CloseIncompleteSession_ClosesUnfinishedDvdSession()
    {
        var drive = new VirtualMmcDrive { Profile = NativeTaoDataWriter.ProfileDvdMinusR, CapacitySectors = DvdCapacity };
        byte[] session1 = IsoImage(320, seed: 11, sessionStartLba: 0);
        int s1Sectors = (session1.Length / 2048 + 15) / 16 * 16;
        byte[] padded = new byte[s1Sectors * 2048];
        session1.CopyTo(padded, 0);

        var modePage = WriteParametersPage.FromPageBytes(drive.CurrentModePage05.ToArray())
            .ForDvdIncremental(testWrite: false, bufferUnderrunFree: true, closeDisc: false, WriteParametersPage.TrackModeDvd);
        Assert.True(drive.ExecuteCommand(NativeWriteCommands.BuildModeSelect10((ushort)modePage.ToModeSelect10ParameterList().Length),
            modePage.ToModeSelect10ParameterList(), ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5)).IsSuccess);
        for (int lba = 0; lba < s1Sectors; lba += 16)
        {
            Assert.True(drive.ExecuteCommand(NativeWriteCommands.BuildWrite10(lba, 16),
                padded.AsSpan(lba * 2048, 16 * 2048), ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5)).IsSuccess);
        }
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildSynchronizeCacheCdb(), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5)).IsSuccess);

        var res = new NativeTaoDataWriter(drive, sleep: _ => { }).CloseIncompleteSession();
        Assert.True(res.Success, res.Message);
        Assert.True(res.SessionClosed);
        Assert.Equal(1, drive.DiscStatus);
        Assert.Single(drive.DataSessions);
    }

    // ---- 6. 64 KiB mixed-mode audio shrink + FirstWriteTimeout ----

    [Fact]
    public void NativeSaoAudioWriter_MixedModeDataTrack_ShrinksOnError87_AndUsesFirstWriteTimeout()
    {
        // Cap MaxTransferBytes below 65,536 B (32 Mode 1 sectors = 65,536 B) and inject 70 s first-write calibration.
        var drive = new VirtualMmcDrive
        {
            Profile = NativeSaoAudioWriter.ProfileCdRw,
            MaxTransferBytesOverride = 65_535,
            FirstWriteCalibration = TimeSpan.FromSeconds(70)
        };
        byte[] userData = Bytes(320 * 2048, seed: 31);
        byte[] audio = Bytes(300 * 2352, seed: 32);
        var tracks = new[]
        {
            new SaoTrack(1, 320, PreGapSectors: 150, IsData: true, DataMode: SaoDataMode.Mode1),
            new SaoTrack(2, 300, PreGapSectors: 150)
        };
        var request = new NativeSaoWriteRequest(
            tracks,
            i => i == 0 ? new MemoryStream(userData) : new MemoryStream(audio),
            CdTextPacks: null,
            MediaCatalogNumber: null,
            TestWrite: false);

        var result = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        // After shrinking from 32 to 16 sectors (32,768 B) on error 87, the first accepted WRITE gets FirstWriteTimeout (4 min),
        // and all subsequent WRITEs get WriteTimeout (60 s).
        Assert.True(drive.CommandsReceived.Count(op => op == NativeWriteCommands.OpCodeWrite10) > drive.WriteTimeouts.Count);
        Assert.True(drive.WriteTimeouts[0] >= TimeSpan.FromSeconds(70));
        Assert.All(drive.WriteTimeouts.Skip(1), t => Assert.Equal(TimeSpan.FromSeconds(60), t));
    }

    // ---- 7. Mode Page 0x05 byte-0 false positive ----

    [Fact]
    public void ParseWriteParametersModePage_DoesNotFalsePositiveOnModeDataLengthByte0EqualTo05()
    {
        // Full MODE SENSE (10) response for Page 0x2A whose ModeDataLength MSB happens to be 0x05 (0x0520).
        byte[] page2AWithLengthMsb05 = new byte[8 + 16];
        BinaryPrimitives.WriteUInt16BigEndian(page2AWithLengthMsb05.AsSpan(0, 2), 0x0520);
        page2AWithLengthMsb05[2] = 0x52; // Media Type byte that previously looked like Page 0x05 byte 2!
        page2AWithLengthMsb05[8] = 0x2A; // Actual mode page at offset 8 is Page 0x2A, not 0x05
        page2AWithLengthMsb05[9] = 14;

        var notPresent = MmcCommandBuilder.ParseWriteParametersModePage(page2AWithLengthMsb05);
        Assert.False(notPresent.ModePage05Present);

        // Full MODE SENSE (10) response with ModeDataLength MSB = 0x05 and actual Page 0x05 at offset 8.
        byte[] page05WithLengthMsb05 = new byte[8 + 12];
        BinaryPrimitives.WriteUInt16BigEndian(page05WithLengthMsb05.AsSpan(0, 2), 0x0512);
        page05WithLengthMsb05[2] = 0x00;
        page05WithLengthMsb05[8] = 0x05;
        page05WithLengthMsb05[9] = 10;
        page05WithLengthMsb05[10] = 0x52; // BUFE=1, TestWrite=1, WriteType=2 (SAO)

        var present = MmcCommandBuilder.ParseWriteParametersModePage(page05WithLengthMsb05);
        Assert.True(present.ModePage05Present);
        Assert.True(present.ModePage05BufferUnderrunFreeEnabled);
        Assert.True(present.ModePage05TestWriteEnabled);
        Assert.Equal(0x02, present.ModePage05WriteType);
    }

    // ---- 8. VirtualMmcDrive & VirtualBurnEngine fidelity ----

    [Fact]
    public void VirtualMmcDrive_TrayReload_Emits62800_AndSetsErasableBitAndChecksReadCdBounds()
    {
        var drive = new VirtualMmcDrive { CanLoadTray = true };
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: false, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5)).IsSuccess);
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: true, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5)).IsSuccess);

        // 2x 2/04/01 spin-up, then 1x 6/28/00 media changed, then ready.
        var r1 = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        var r2 = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        var r3 = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        var r4 = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        Assert.Equal("02/04/01", r1.SenseInfo?.AscAscqHex);
        Assert.Equal("02/04/01", r2.SenseInfo?.AscAscqHex);
        Assert.Equal("06/28/00", r3.SenseInfo?.AscAscqHex);
        Assert.True(r4.IsSuccess);

        // Erasable bit (0x10 in READ DISC INFORMATION byte 2) on all rewritable media, clear on write-once.
        foreach (ushort p in new[] { NativeTaoDataWriter.ProfileDvdMinusRwRestricted, NativeTaoDataWriter.ProfileDvdPlusRw, NativeTaoDataWriter.ProfileDvdRam, NativeTaoDataWriter.ProfileBdRe })
        {
            var d = new VirtualMmcDrive { Profile = p };
            byte[] buf = new byte[34];
            Assert.True(d.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5)).IsSuccess);
            Assert.True((buf[2] & 0x10) != 0, $"Profile 0x{p:X4} should set Erasable bit.");
        }
        foreach (ushort p in new[] { NativeTaoDataWriter.ProfileDvdMinusR, NativeTaoDataWriter.ProfileDvdPlusR, NativeSaoAudioWriter.ProfileCdR, NativeTaoDataWriter.ProfileBdR })
        {
            var d = new VirtualMmcDrive { Profile = p };
            byte[] buf = new byte[34];
            Assert.True(d.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5)).IsSuccess);
            Assert.True((buf[2] & 0x10) == 0, $"Profile 0x{p:X4} should not set Erasable bit.");
        }

        // ReadCd bounds check: LoadOpenAudioSession with a data track and empty ProgramAreaWritten returns SCSI sense instead of ArgumentOutOfRangeException.
        var mixedDrive = new VirtualMmcDrive();
        mixedDrive.LoadOpenAudioSession(new[] { ((byte)1, 0, (byte)0x40), ((byte)2, 400, (byte)0x00) }, 700, nextSessionAddress: null);
        byte[] readBuf = new byte[2352 * 4];
        var oob = mixedDrive.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(startLba: 698, transferBlocks: 4), readBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.False(oob.IsSuccess);
        Assert.NotNull(oob.SenseInfo);
    }

    [Fact]
    public async Task VirtualBurnEngine_HonorsDaoLeaveSessionOpen_RedBookMinimumAndGaps_AndRejectsRaw2352Data()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder) { EnforceRedBookTrackMinimumAndGaps = true };

        // 1. DAO with LeaveSessionOpen = true leaves medium unfinalized and sets NextWritableLba past 11,400 gap;
        //    EnforceRedBookTrackMinimumAndGaps pads short track (50 frames) to 300 frames.
        var daoTracks = new[]
        {
            new AudioTrackSource { TrackNumber = 1, TotalPcmBytes = 50 * 2352, PcmStreamFactory = () => new MemoryStream(Bytes(50 * 2352, 7)) }
        };
        var daoOpen = await engine.BurnAudioTracksAsync(recorder, daoTracks, new AudioBurnOptions { UseDiscAtOnce = true, LeaveSessionOpen = true }, null, CancellationToken.None);
        Assert.True(daoOpen.Success, daoOpen.ErrorMessage);
        Assert.Equal(300, daoOpen.SectorsWritten);
        Assert.False(medium.IsFinalized);
        Assert.Equal(300 + VirtualMmcDrive.CdFirstSessionGapSectors, medium.NextWritableLba);

        // 2. TAO with EnforceRedBookTrackMinimumAndGaps inserts 150-frame gap between tracks.
        medium.Erase(fullErase: false);
        var taoTracks = new[]
        {
            new AudioTrackSource { TrackNumber = 1, TotalPcmBytes = 300 * 2352, PcmStreamFactory = () => new MemoryStream(Bytes(300 * 2352, 8)) },
            new AudioTrackSource { TrackNumber = 2, TotalPcmBytes = 300 * 2352, PcmStreamFactory = () => new MemoryStream(Bytes(300 * 2352, 9)) }
        };
        var taoRes = await engine.BurnAudioTracksAsync(recorder, taoTracks, new AudioBurnOptions { UseDiscAtOnce = false }, null, CancellationToken.None);
        Assert.True(taoRes.Success, taoRes.ErrorMessage);
        Assert.Equal(300 + 150 + 300, taoRes.SectorsWritten);
        Assert.Equal(450, medium.Sessions[0].Tracks[1].StartLba);

        // 3. BurnDataStreamAsync rejects raw 2,352-byte Mode 1 streams.
        medium.Erase(fullErase: false);
        byte[] iso = IsoImage(40, seed: 10);
        int isoSectors = iso.Length / 2048;
        byte[] raw2352 = new byte[isoSectors * 2352];
        for (int i = 0; i < isoSectors; i++)
        {
            RawSectorCodec.Convert2048To2352Mode1(iso.AsSpan(i * 2048, 2048), i, raw2352.AsSpan(i * 2352, 2352));
        }
        var rawReject = await engine.BurnDataStreamAsync(recorder, new MemoryStream(raw2352), new DataBurnOptions(), null, CancellationToken.None);
        Assert.False(rawReject.Success);
        Assert.Contains("2,352", rawReject.ErrorMessage);
        Assert.True(medium.IsBlank);
    }

    private sealed class CallbackProgress(Action<BurnProgressSnapshot> onReport) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => onReport(value);
    }

    private sealed class TransientWriteFaultTransport(
        IScsiTransport inner,
        byte senseKey,
        byte asc,
        byte ascq,
        int failuresToInject,
        Action? onInject = null) : IScsiTransport
    {
        public int InjectedCount { get; private set; }

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
        {
            if (cdb.Length > 0 && cdb[0] == NativeWriteCommands.OpCodeWrite10 && InjectedCount < failuresToInject)
            {
                InjectedCount++;
                onInject?.Invoke();
                byte[] sense = new byte[18];
                sense[0] = 0x70;
                sense[2] = senseKey;
                sense[7] = 10;
                sense[12] = asc;
                sense[13] = ascq;
                return ScsiExecutionResult.CheckCondition(0x02, 0, sense);
            }
            return inner.ExecuteCommand(cdb, dataBuffer, dataDirection, timeout);
        }

        public void Dispose() { }
    }
}
