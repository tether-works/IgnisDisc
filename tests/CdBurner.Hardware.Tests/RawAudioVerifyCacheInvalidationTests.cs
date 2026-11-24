using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class RawAudioVerifyCacheInvalidationTests
{
    private sealed class MmcRawAudioEngine(VirtualMmcDrive drive) : IBurnEngineBackend
    {
        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            raw2352Mode
                ? PhysicalSectorReader.ReadRawSectorsForVerifyAsync(drive, startLba, sectorCount, DriveCacheBypass.None, sectorConsumer, progress, cancellationToken, sleep: _ => { })
                : PhysicalSectorReader.ReadDataSectorsAsync(drive, startLba, sectorCount, DriveCacheBypass.None, sectorConsumer, progress, cancellationToken, bypassRangeStart: null, bypassRangeCount: null, sleep: _ => { });

        public ISectorReadSession? OpenReadSession(IDiscRecorderBackend recorder, bool raw2352Mode, bool readFromDisc = false, bool strict = false,
            SectorRange? writtenRange = null)
        {
            DriveCacheBypass bypass = readFromDisc
                ? (strict ? DriveCacheBypass.EveryRead : DriveCacheBypass.RecentlyWritten)
                : DriveCacheBypass.None;
            return PhysicalSectorReader.OpenSession(
                () => drive,
                raw2352Mode,
                bypass,
                writtenRange?.Start,
                writtenRange?.Count,
                sleep: _ => { });
        }
    }

    private static (VirtualMmcDrive Drive, byte[] ExpectedRawStream) BurnSimulatedAudioDisc(
        int sectors,
        int? badMediaLba = null,
        bool populateStaleCacheAtBadLba = false,
        bool populateEntireStaleCache = false,
        bool canLoadTray = false,
        int bufferCapacityBytes = 1024 * 1024,
        int readCacheCapacitySectors = 1024)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(99).NextBytes(pcm);

        var drive = new VirtualMmcDrive
        {
            Profile = NativeSaoAudioWriter.ProfileCdR,
            CanLoadTray = canLoadTray,
            BufferCapacityBytes = bufferCapacityBytes,
            ReadCacheCapacitySectors = readCacheCapacitySectors,
            ReadOffsetSamples = 0,
            WriteOffsetSamples = 0
        };

        var tracks = new[] { new SaoTrack(1, sectors, OpticalConstants.CdLeadInFrames) };
        var writeResult = new NativeSaoAudioWriter(drive, sleep: _ => { })
            .Write(new NativeSaoWriteRequest(tracks, i => i == 0 ? new MemoryStream(pcm, writable: false) : Stream.Null, CdTextPacks: null, MediaCatalogNumber: null, TestWrite: false));
        Assert.True(writeResult.Success, writeResult.Message);

        if (badMediaLba is int bad)
        {
            drive.ReadBackDifferenceLbas.Add(bad);
            if (populateStaleCacheAtBadLba)
            {
                drive.StaleRawReadCache[bad] = pcm.AsSpan(bad * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize).ToArray();
            }
        }

        if (populateEntireStaleCache)
        {
            for (int lba = 0; lba < sectors; lba++)
            {
                drive.StaleRawReadCache[lba] = pcm.AsSpan(lba * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize).ToArray();
            }
        }

        return (drive, pcm);
    }

    [Fact]
    public async Task Raw2352Verify_SpringTray_RunsSyncCacheAndSeekAway_CatchesBufferMismatch_AndReportsEnforced()
    {
        const int totalSectors = 2000;
        const int badLba = 12;
        var (drive, expected) = BurnSimulatedAudioDisc(
            totalSectors,
            badMediaLba: badLba,
            populateStaleCacheAtBadLba: true,
            canLoadTray: false,
            bufferCapacityBytes: 1024 * 1024, // 2 * 1 MB / 2048 = 1024 guard sectors
            readCacheCapacitySectors: 1024);

        // Plain uncached-bypass read is fooled by the stale RAM write buffer.
        byte[]? plainSector = null;
        await PhysicalSectorReader.ReadRawSectorsForVerifyAsync(drive, badLba, 1, DriveCacheBypass.None,
            (_, d) => { plainSector = d.ToArray(); return ValueTask.CompletedTask; }, null, default);
        Assert.Equal(expected.AsSpan(badLba * 2352, 2352).ToArray(), plainSector);

        int syncBeforeVerify = drive.SynchronizeCacheCount;
        drive.ReadCdLog.Clear();

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var verify = await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder,
            new MmcRawAudioEngine(drive),
            new MemoryStream(expected),
            raw2352Mode: true);

        Assert.False(verify.IsMatch);
        Assert.Contains(badLba, verify.MismatchedLbas);
        Assert.True(verify.MediaSurfaceReadbackEnforced);
        Assert.True(drive.SynchronizeCacheCount > syncBeforeVerify);
        Assert.Contains(drive.ReadCdLog, r => r.Lba >= totalSectors / 2 - 64);
        Assert.NotNull(verify.ReadbackNote);
        Assert.Contains("SYNCHRONIZE CACHE", verify.ReadbackNote);
    }

    [Fact]
    public async Task Raw2352Verify_MotorizedTray_ReloadsDisc_CatchesFullWriteBufferMismatch_AndReportsEnforced()
    {
        const int totalSectors = 320;
        const int badLba = 300;
        var (drive, expected) = BurnSimulatedAudioDisc(
            totalSectors,
            badMediaLba: badLba,
            populateEntireStaleCache: true,
            canLoadTray: true,
            bufferCapacityBytes: 4 * 1024 * 1024,
            readCacheCapacitySectors: int.MaxValue);

        int syncBefore = drive.SynchronizeCacheCount;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var verify = await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder,
            new MmcRawAudioEngine(drive),
            new MemoryStream(expected),
            raw2352Mode: true);

        Assert.False(verify.IsMatch);
        Assert.Contains(badLba, verify.MismatchedLbas);
        Assert.True(verify.MediaSurfaceReadbackEnforced);
        Assert.True(drive.SynchronizeCacheCount > syncBefore);
        Assert.Equal(1, drive.TrayEjects);
        Assert.Equal(1, drive.TrayLoads);
        Assert.Contains("reloaded", verify.ReadbackNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Raw2352Verify_HealthyDiscWithAdequateSeekAway_PassesAndReportsEnforced()
    {
        const int totalSectors = 2000;
        var (drive, expected) = BurnSimulatedAudioDisc(
            totalSectors,
            canLoadTray: false,
            bufferCapacityBytes: 1024 * 1024,
            readCacheCapacitySectors: 1024);

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var verify = await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder,
            new MmcRawAudioEngine(drive),
            new MemoryStream(expected),
            raw2352Mode: true);

        Assert.True(verify.IsMatch, verify.ErrorMessage);
        Assert.True(verify.MediaSurfaceReadbackEnforced);
        Assert.NotNull(verify.ReadbackNote);
    }

    [Fact]
    public async Task Raw2352Verify_WhenSyncCacheRefusedOrDiscTooSmallForBuffer_ReportsEnforcedFalseAccurately()
    {
        // Case 1: SYNCHRONIZE CACHE refused on spring-tray drive
        var (refusedDrive, expectedLarge) = BurnSimulatedAudioDisc(
            2000,
            canLoadTray: false,
            bufferCapacityBytes: 1024 * 1024);
        refusedDrive.RejectsSynchronizeCache = true;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var refusedResult = await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder,
            new MmcRawAudioEngine(refusedDrive),
            new MemoryStream(expectedLarge),
            raw2352Mode: true);

        Assert.True(refusedResult.IsMatch, refusedResult.ErrorMessage);
        Assert.False(refusedResult.MediaSurfaceReadbackEnforced);
        Assert.Contains("SYNCHRONIZE CACHE", refusedResult.ReadbackNote);

        // Case 2: Disc smaller than 2x drive buffer on spring-tray drive
        var (smallDrive, expectedSmall) = BurnSimulatedAudioDisc(
            320,
            canLoadTray: false,
            bufferCapacityBytes: 4 * 1024 * 1024);

        var smallResult = await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder,
            new MmcRawAudioEngine(smallDrive),
            new MemoryStream(expectedSmall),
            raw2352Mode: true);

        Assert.True(smallResult.IsMatch, smallResult.ErrorMessage);
        Assert.False(smallResult.MediaSurfaceReadbackEnforced);
        Assert.Contains("fewer than twice the drive buffer", smallResult.ReadbackNote);
    }

    [Fact]
    public void Imapi2BurnEngine_OpenReadSession_CreatesSessionForRaw2352Verify()
    {
        var engine = new Imapi2BurnEngine();
        using var recorder = new VirtualDiscRecorder("V0", "TEST", "DRIVE", @"V:\");
        using var session = engine.OpenReadSession(recorder, raw2352Mode: true, readFromDisc: true, strict: false, new SectorRange(0, 300));
        Assert.NotNull(session);
    }
}
