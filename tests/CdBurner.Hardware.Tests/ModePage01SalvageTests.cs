using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
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
/// Verifies Mode Page 0x01 (Read/Write Error Recovery Parameters) parameter encoding, exclusive-access
/// locking, unconditional finally restoration across read errors and cancellation, and bit-identical Mode 1
/// sector recovery through VirtualMmcDrive + SectorParityCorrector.
/// </summary>
public class ModePage01SalvageTests
{
    private sealed class MmcSalvageEngine(Func<IScsiTransport?> openTransport) : IBurnEngineBackend
    {
        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Expected BatchedSectorReader to open a salvage session.");

        public ISectorReadSession? OpenSalvageReadSession(IDiscRecorderBackend recorder, bool raw2352Mode, bool fastSalvage = true) =>
            PhysicalSectorReader.OpenSession(openTransport, raw2352Mode, DriveCacheBypass.None, null, null, _ => { }, fastSalvage);
    }

    private sealed class EventLoggingTransport(
        VirtualMmcDrive inner,
        List<string> events,
        bool failOnRestore = false,
        byte[]? customModePage01 = null) : IScsiTransport
    {
        private int _modeSelectCount;

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> data, byte direction, TimeSpan timeout)
        {
            byte op = cdb[0];
            if (op == MmcCommandBuilder.OpCodeModeSense10 && (cdb[2] & 0x3F) == ModePage01Parameters.PageCode)
            {
                events.Add("mode-sense-01");
                if (customModePage01 != null)
                {
                    byte[] resp = new byte[8 + customModePage01.Length];
                    BinaryPrimitives.WriteUInt16BigEndian(resp, (ushort)(resp.Length - 2));
                    customModePage01.CopyTo(resp, 8);
                    resp[8] |= 0x80; // PS bit set in MODE SENSE response
                    int n = Math.Min(resp.Length, data.Length);
                    resp.AsSpan(0, n).CopyTo(data);
                    return ScsiExecutionResult.Success((uint)n);
                }
            }
            else if (op == MmcCommandBuilder.OpCodeModeSelect10 && data.Length >= 10 && (data[8] & 0x3F) == ModePage01Parameters.PageCode)
            {
                _modeSelectCount++;
                byte flags = data[10];
                byte retries = data[11];
                events.Add($"mode-select-01:#{_modeSelectCount}:flags=0x{flags:X2}:retries={retries}");
                if (failOnRestore && _modeSelectCount >= 2)
                {
                    throw new IOException("Simulated transport failure while restoring Mode Page 0x01.");
                }
            }

            return inner.ExecuteCommand(cdb, data, direction, timeout);
        }

        public void Dispose() { }
    }

    private sealed class TrackingExclusiveLock(List<string> events) : IDisposable
    {
        public bool Disposed { get; private set; }

        public static Func<IDisposable?> Factory(List<string> events, List<TrackingExclusiveLock> created) => () =>
        {
            events.Add("lock-acquire");
            var lockObj = new TrackingExclusiveLock(events);
            created.Add(lockObj);
            return lockObj;
        };

        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                events.Add("lock-release");
            }
        }
    }

    private static byte[] BuildTestCdImage(int sectors, int seed = 101)
    {
        byte[] image = new byte[sectors * OpticalConstants.DataSectorSize];
        new Random(seed).NextBytes(image);
        return image;
    }

    [Fact]
    public void ModePage01Parameters_Mmc5OffsetsAndFlags_MatchSpecification()
    {
        // MMC-5 Read/Write Error Recovery Parameters Mode Page (0x01, length 0x0A = 10 bytes following -> 12 bytes total):
        // Byte 0: PS | PageCode (0x01), Byte 1: 0x0A, Byte 2: AWRE(7) ARRE(6) TB(5) RC(4) EER(3) PER(2) DTE(1) DCR(0), Byte 3: Read Retry Count.
        byte[] response = new byte[8 + 12];
        BinaryPrimitives.WriteUInt16BigEndian(response, (ushort)(response.Length - 2));
        response[8 + 0] = 0x81; // PS=1 | PageCode=0x01
        response[8 + 1] = 0x0A;
        response[8 + 2] = 0xD8; // AWRE(0x80) | ARRE(0x40) | RC(0x10) | EER(0x08)
        response[8 + 3] = 0x06; // Read Retry Count = 6
        response[8 + 8] = 0x04; // Write Retry Count = 4

        var original = ModePage01Parameters.FromModeSense10(response);
        Assert.NotNull(original);
        Assert.Equal((byte)0x06, original!.ReadRetryCount);
        Assert.True(original.ReadContinuous);
        Assert.True(original.EnableEarlyRecovery);

        var fastOnly = original.ForSalvage(readRetryCount: 1, rawDamagedSectorReturn: false);
        Assert.Equal((byte)0x01, fastOnly.ReadRetryCount);
        Assert.Equal((byte)0xD8, fastOnly.ErrorRecoveryBits);

        var rawSalvage = original.ForSalvage(readRetryCount: 1, rawDamagedSectorReturn: true);
        Assert.Equal((byte)0x01, rawSalvage.ReadRetryCount);
        // Preserves AWRE|ARRE (0xC0), sets TB(0x20)|PER(0x04)|DCR(0x01) = 0x25, clears RC/EER/DTE -> 0xE5.
        Assert.Equal((byte)0xE5, rawSalvage.ErrorRecoveryBits);
        Assert.True(rawSalvage.TransferBlock);
        Assert.True(rawSalvage.PostError);
        Assert.True(rawSalvage.DisableCorrection);
        Assert.False(rawSalvage.ReadContinuous);
        Assert.False(rawSalvage.EnableEarlyRecovery);
        Assert.False(rawSalvage.DisableTransferOnError);

        byte[] selectList = rawSalvage.ToModeSelect10ParameterList();
        Assert.Equal(20, selectList.Length);
        Assert.Equal((byte)0x01, selectList[8]); // PS cleared for MODE SELECT(10)
        Assert.Equal((byte)0xE5, selectList[10]);
        Assert.Equal((byte)0x01, selectList[11]);

        byte[] restoreList = original.ForRestore().ToModeSelect10ParameterList();
        Assert.Equal((byte)0x01, restoreList[8]); // PS cleared
        Assert.Equal((byte)0xD8, restoreList[10]);
        Assert.Equal((byte)0x06, restoreList[11]);
        Assert.Equal((byte)0x04, restoreList[16]);
    }

    [Fact]
    public async Task ModePage01Controller_FaultInjection_ReadError_AlwaysRestoresOriginalBytesAndReleasesLockInFinally()
    {
        var drive = new VirtualMmcDrive();
        // Set distinctive non-default original Mode Page 0x01 parameters on the drive.
        byte[] customInitial = [0x01, 0x0A, 0x48 /* ARRE | EER */, 0x09, 0, 0, 0, 0, 0x03, 0, 0, 0];
        byte[] initList = new byte[8 + customInitial.Length];
        customInitial.CopyTo(initList, 8);
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildModeSelect10Cdb((ushort)initList.Length), initList, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5)).IsSuccess);

        var events = new List<string>();
        var locks = new List<TrackingExclusiveLock>();
        var transport = new EventLoggingTransport(drive, events);
        using var controller = new ModePage01Controller(transport, TrackingExclusiveLock.Factory(events, locks));

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            controller.ExecuteWithSalvageAsync<int>(_ =>
            {
                events.Add("operation");
                Assert.Equal((byte)1, drive.ModePage01ReadRetryCount);
                Assert.True(drive.ModePage01RawDamagedReturnEnabled);
                throw new IOException("Simulated sector read I/O error");
            }, readRetryCount: 1, rawDamagedSectorReturn: true));

        Assert.Contains("Simulated sector read I/O error", ex.Message);
        Assert.True(controller.IsRestored);
        Assert.Null(controller.RestoreException);
        Assert.Single(locks);
        Assert.True(locks[0].Disposed);
        Assert.Equal(customInitial, drive.CurrentModePage01.ToArray());

        // Exclusive lock acquired before MODE SELECT apply, original restored BEFORE lock released.
        Assert.Equal(
            new[]
            {
                "mode-sense-01",
                "lock-acquire",
                "mode-select-01:#1:flags=0x65:retries=1",
                "operation",
                "mode-select-01:#2:flags=0x48:retries=9",
                "lock-release"
            },
            events);
    }

    [Fact]
    public async Task ModePage01Controller_FaultInjection_Cancellation_AlwaysRestoresOriginalBytesAndReleasesLockInFinally()
    {
        var drive = new VirtualMmcDrive();
        byte[] expectedOriginal = drive.CurrentModePage01.ToArray();
        var events = new List<string>();
        var locks = new List<TrackingExclusiveLock>();
        var transport = new EventLoggingTransport(drive, events);
        using var cts = new CancellationTokenSource();
        using var controller = new ModePage01Controller(transport, TrackingExclusiveLock.Factory(events, locks));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.ExecuteWithSalvageAsync<int>(ct =>
            {
                events.Add("operation-start");
                Assert.Equal((byte)1, drive.ModePage01ReadRetryCount);
                Assert.True(drive.ModePage01RawDamagedReturnEnabled);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }, readRetryCount: 1, rawDamagedSectorReturn: true, cancellationToken: cts.Token));

        Assert.True(controller.IsRestored);
        Assert.Null(controller.RestoreException);
        Assert.Single(locks);
        Assert.True(locks[0].Disposed);
        Assert.Equal(expectedOriginal, drive.CurrentModePage01.ToArray());
        Assert.Equal("lock-release", events[^1]);
        Assert.Equal("mode-select-01:#2:flags=0x00:retries=5", events[^2]);
    }

    [Fact]
    public void ModePage01Controller_FaultInjection_RestoreThrows_StillReleasesExclusiveLockAndRecordsException()
    {
        var drive = new VirtualMmcDrive();
        var events = new List<string>();
        var locks = new List<TrackingExclusiveLock>();
        var transport = new EventLoggingTransport(drive, events, failOnRestore: true);

        using (var controller = new ModePage01Controller(transport, TrackingExclusiveLock.Factory(events, locks)))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                controller.ExecuteWithSalvage<int>(() => throw new InvalidOperationException("Primary read failure"),
                    readRetryCount: 1, rawDamagedSectorReturn: true));

            Assert.Equal("Primary read failure", ex.Message);
            Assert.False(controller.IsRestored);
            Assert.IsType<IOException>(controller.RestoreException);
        }

        Assert.Single(locks);
        Assert.True(locks[0].Disposed);
        Assert.Equal("lock-release", events[^1]);
    }

    [Fact]
    public async Task VirtualMmcDrive_CorruptedMode1Sector_FailsCookedRead10_AndRecoversBitIdenticallyViaModePage01AndParity()
    {
        const int sectorCount = 32;
        const int damagedLba = 11;
        byte[] image = BuildTestCdImage(sectorCount, seed: 777);
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, DataDiscImage = image };
        byte[] originalPage01 = drive.CurrentModePage01.ToArray();

        // Corrupt one byte inside the Mode 1 user payload at LBA 11.
        drive.InjectCorruptedMode1Sector(damagedLba, rawByteOffset: 16 + 250, xorMask: 0xA5);

        // 1. Cooked READ(10) must fail on the corrupted sector.
        byte[] cookedBuf = new byte[2048];
        var cookedRes = drive.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(damagedLba, 1), cookedBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.False(cookedRes.IsSuccess);
        Assert.Equal((byte)0x03, cookedRes.SenseInfo?.SenseKey);
        Assert.Equal((byte)0x11, cookedRes.SenseInfo?.Asc);

        // 2. Raw READ CD without Mode Page 0x01 TB=1 & DCR=1 must also fail.
        byte[] rawBuf = new byte[2352];
        var rawWithoutMode01 = drive.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(damagedLba, 1, 0, 0xF8), rawBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.False(rawWithoutMode01.IsSuccess);
        Assert.Equal((byte)0x03, rawWithoutMode01.SenseInfo?.SenseKey);

        // 3. Salvage read through BatchedSectorReader + PhysicalSectorReader + ModePage01Controller + SectorParityCorrector.
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new MmcSalvageEngine(() => drive);

        byte[] recoveredImage = new byte[image.Length];
        var unreadable = new List<int>();
        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: sectorCount,
            raw2352Mode: false,
            maxRetriesPerSector: 1,
            onSector: (lba, data) =>
            {
                data.Span.CopyTo(recoveredImage.AsSpan(lba * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadable.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: 16,
            rawSectorSalvage: true);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.RawRecoveredSectors);
        Assert.Empty(unreadable);
        Assert.Equal(image, recoveredImage);

        // Verify MODE SENSE(10), MODE SELECT(10), READ CD, and restoration of original Mode Page 0x01 bytes.
        Assert.Contains(MmcCommandBuilder.OpCodeModeSense10, drive.CommandsReceived);
        Assert.Contains(MmcCommandBuilder.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.Contains(MmcCommandBuilder.OpCodeReadCd, drive.CommandsReceived);
        Assert.Equal(originalPage01, drive.CurrentModePage01.ToArray());
    }

    [Fact]
    public async Task VirtualMmcDrive_UncorrectableMode1Sector_IsNotAcceptedAsGood_AndFallsBackToUnreadable()
    {
        const int sectorCount = 16;
        const int destroyedLba = 5;
        byte[] image = BuildTestCdImage(sectorCount, seed: 888);
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, DataDiscImage = image };
        byte[] originalPage01 = drive.CurrentModePage01.ToArray();

        // Corrupt 200 consecutive bytes (> 86-byte P-interleave and Q capacity): beyond ECMA-130 P/Q parity correction.
        byte[] raw = RawSectorCodec.Convert2048To2352Mode1(image.AsSpan(destroyedLba * 2048, 2048), destroyedLba);
        for (int i = 0; i < 200; i++)
        {
            raw[24 + i] ^= 0xFF;
        }
        drive.CorruptedRawSectors[destroyedLba] = raw;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new MmcSalvageEngine(() => drive);

        var unreadable = new List<int>();
        var delivered = new HashSet<int>();
        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: sectorCount,
            raw2352Mode: false,
            maxRetriesPerSector: 1,
            onSector: (lba, _) =>
            {
                delivered.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadable.Add(lba);
                return ValueTask.FromResult(true);
            },
            rawSectorSalvage: true);

        Assert.True(outcome.Completed);
        Assert.Equal(0, outcome.RawRecoveredSectors);
        Assert.Equal(new[] { destroyedLba }, unreadable);
        Assert.DoesNotContain(destroyedLba, delivered);
        Assert.Equal(originalPage01, drive.CurrentModePage01.ToArray());
    }

    [Fact]
    public async Task PhysicalSectorReader_SalvageSession_CancellationDuringRead_RestoresOriginalModePage01()
    {
        const int sectorCount = 64;
        byte[] image = BuildTestCdImage(sectorCount, seed: 999);
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, DataDiscImage = image };
        byte[] originalPage01 = drive.CurrentModePage01.ToArray();

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new MmcSalvageEngine(() => drive);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: 0,
                sectorCount: sectorCount,
                raw2352Mode: false,
                maxRetriesPerSector: 1,
                onSector: (lba, _) =>
                {
                    if (lba == 10)
                    {
                        // Mode Page 0x01 fast salvage should be active during the read pass.
                        Assert.Equal((byte)1, drive.ModePage01ReadRetryCount);
                        cts.Cancel();
                    }
                    return ValueTask.CompletedTask;
                },
                onUnreadable: _ => ValueTask.FromResult(true),
                batchSectors: 8,
                cancellationToken: cts.Token,
                rawSectorSalvage: true));

        Assert.Equal(originalPage01, drive.CurrentModePage01.ToArray());
    }

    [Fact]
    public async Task ArchiveRecoveryService_VirtualMmcDrive_CorruptedMode1Sector_RecoversViaModePage01BeforePar2()
    {
        string root = Path.Combine(Path.GetTempPath(), "ignis-mp01-hw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string srcDir = Path.Combine(root, "source");
            Directory.CreateDirectory(srcDir);
            byte[] payload = new byte[16_384];
            new Random(2026).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(srcDir, "critical.bin"), payload);

            var plan = ArchiveSpanPlanner.Plan(srcDir, DiscMediaType.CdR, redundancyPercent: 0, setId: "mp01hw", ceilingSectorsOverride: 2_000);
            var staged = (await ArchiveStager.StageDiscsAsync(plan, Path.Combine(root, "staging"), par2Path: null)).Single();

            var tree = CompilationFolderImporter.ImportAsRoot(staged.DiscRoot, strict: true);
            byte[] isoBytes = IsoUdfImageBuilder.BuildToMemoryStream(
                tree,
                new IsoBuildOptions(VolumeLabel: "ARCHIVE01", IncludeUdf: true, IncludeJoliet: false)).ToArray();

            int fileStartLba;
            using (var isoStream = new MemoryStream(isoBytes))
            {
                fileStartLba = UdfTreeReader.TryRead(isoStream)!
                    .Files.Single(f => f.Name.Equals("critical.bin", StringComparison.OrdinalIgnoreCase)).StartLba;
            }

            var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, DataDiscImage = isoBytes };
            byte[] originalPage01 = drive.CurrentModePage01.ToArray();
            drive.InjectCorruptedMode1Sector(fileStartLba + 1, rawByteOffset: 16 + 500, xorMask: 0x3C);

            using var recorder = new VirtualDiscRecorder();
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            medium.UsedSectors = isoBytes.Length / OpticalConstants.DataSectorSize;
            recorder.InsertMedia(medium);

            string outDir = Path.Combine(root, "recovered");
            var report = await ArchiveRecoveryService.RecoverFromDiscAsync(
                recorder,
                new MmcSalvageEngine(() => drive),
                outDir,
                par2Path: null,
                maxRetriesPerSector: 1);

            Assert.True(report.FullyRecovered, report.Problem);
            Assert.Empty(report.UnreadableSectors);
            Assert.Equal(1, report.RawRecoveredSectors);
            Assert.False(report.RepairAttempted);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(outDir, "critical.bin")));
            Assert.Equal(originalPage01, drive.CurrentModePage01.ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
