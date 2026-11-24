using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for BatchedSectorReader.
/// Verifies batch reading of sectors with fault isolation, retry handling, cancellation,
/// raw 2352 mode, and invalid argument guards.
/// </summary>
public class BatchedSectorReaderTests
{
    #region Test Doubles

    private sealed class CallCountingEngine : IBurnEngineBackend
    {
        private readonly IBurnEngineBackend _inner;
        public List<(int StartLba, int SectorCount)> Calls { get; } = new();

        public CallCountingEngine(IBurnEngineBackend inner)
        {
            _inner = inner;
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            Calls.Add((startLba, sectorCount));
            return _inner.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);
        }
    }

    private sealed class FlakyEngine : IBurnEngineBackend
    {
        private readonly IBurnEngineBackend _inner;
        private readonly int _flakyLba;
        private int _failuresRemaining;

        public FlakyEngine(IBurnEngineBackend inner, int flakyLba, int failuresBeforeSuccess)
        {
            _inner = inner;
            _flakyLba = flakyLba;
            _failuresRemaining = failuresBeforeSuccess;
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            bool containsFlakyLba = _flakyLba >= startLba && _flakyLba < startLba + sectorCount;
            if (containsFlakyLba && _failuresRemaining > 0)
            {
                _failuresRemaining--;
                int sectorsBefore = _flakyLba - startLba;
                if (sectorsBefore > 0)
                {
                    await _inner.ReadSectorsAsync(recorder, startLba, sectorsBefore, raw2352Mode, sectorConsumer, progress, cancellationToken);
                }

                return new RipOperationResult
                {
                    Success = false,
                    ErrorMessage = "simulated transient error",
                    SectorsRead = sectorsBefore,
                    BadSectorsCount = 1,
                    BadSectorsList = new[] { _flakyLba }
                };
            }

            return await _inner.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);
        }
    }

    #endregion

    #region Test 1: All Sectors Good

    [Fact]
    public async Task ReadRange_AllSectorsGood_DeliversEachSectorOnceInOrder_UsingBatches()
    {
        const int totalSectors = 1300;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] writeBuffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(writeBuffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, writeBuffer);
        }

        var baseEngine = new VirtualBurnEngine();
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var deliveredData = new List<byte[]>();
        bool onUnreadableCalled = false;
        var progressReports = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                deliveredData.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ =>
            {
                onUnreadableCalled = true;
                return ValueTask.FromResult(true);
            },
            onProgress: p => progressReports.Add(p),
            batchSectors: 512,
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(0, outcome.TotalRetries);
        Assert.Equal(Enumerable.Range(0, totalSectors), deliveredLbas);
        Assert.Equal(totalSectors, deliveredData.Count);

        for (int lba = 0; lba < totalSectors; lba++)
        {
            byte expectedByte = (byte)(lba % 251 + 1);
            Assert.Equal(OpticalConstants.DataSectorSize, deliveredData[lba].Length);
            Assert.All(deliveredData[lba], b => Assert.Equal(expectedByte, b));
        }

        Assert.Equal(new (int, int)[] { (0, 512), (512, 512), (1024, 276) }, countingEngine.Calls);
        Assert.False(onUnreadableCalled);
        Assert.NotEmpty(progressReports);
        Assert.Equal(totalSectors, progressReports.Last());

        for (int i = 1; i < progressReports.Count; i++)
        {
            Assert.True(progressReports[i] > progressReports[i - 1]);
        }
    }

    #endregion

    #region Test 2: Permanent Bad Sector - Continue Past It

    [Fact]
    public async Task ReadRange_PermanentBadSector_ContinuePastIt_RetriesAndReportsOnce()
    {
        const int totalSectors = 64;
        const int badLba = 20;
        const int batchSectors = 16;
        const int maxRetries = 3;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var baseEngine = new VirtualBurnEngine();
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var unreadableCalls = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: maxRetries,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: batchSectors,
            cancellationToken: CancellationToken.None);

        Assert.Single(unreadableCalls);
        Assert.Equal(badLba, unreadableCalls[0]);
        Assert.Equal(Enumerable.Range(0, totalSectors).Where(lba => lba != badLba), deliveredLbas);
        Assert.Equal(maxRetries, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(3, countingEngine.Calls.Count(c => c == (badLba, 1)));
    }

    #endregion

    #region Test 3: Permanent Bad Sector - Stop Requested

    [Fact]
    public async Task ReadRange_PermanentBadSector_StopRequested_ReturnsStoppedAtLba()
    {
        const int totalSectors = 64;
        const int badLba = 20;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var engine = new VirtualBurnEngine();
        var deliveredLbas = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ => ValueTask.FromResult(false),
            cancellationToken: CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(badLba, outcome.StoppedAtLba);
        Assert.Equal(Enumerable.Range(0, badLba), deliveredLbas);
    }

    #endregion

    #region Test 4: Transient Bad Sector - Recovers On Retry

    [Fact]
    public async Task ReadRange_TransientBadSector_RecoversOnRetry_WithoutReportingUnreadable()
    {
        const int totalSectors = 64;
        const int flakyLba = 10;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }

        var flakyEngine = new FlakyEngine(new VirtualBurnEngine(), flakyLba: flakyLba, failuresBeforeSuccess: 2);

        var deliveredLbas = new List<int>();
        var deliveredData = new List<byte[]>();
        bool onUnreadableCalled = false;

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            flakyEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                deliveredData.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ =>
            {
                onUnreadableCalled = true;
                return ValueTask.FromResult(true);
            },
            cancellationToken: CancellationToken.None);

        // the batch read consumes failure 1, single-sector retry #1 consumes failure 2, retry #2 succeeds
        Assert.False(onUnreadableCalled);
        Assert.Equal(Enumerable.Range(0, totalSectors), deliveredLbas);
        Assert.Equal(totalSectors, deliveredData.Count);

        for (int lba = 0; lba < totalSectors; lba++)
        {
            byte expectedByte = (byte)(lba % 251 + 1);
            Assert.All(deliveredData[lba], b => Assert.Equal(expectedByte, b));
        }

        Assert.Equal(2, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
    }

    #endregion

    #region Test 5: Engine Throws On Bad Sector

    [Fact]
    public async Task ReadRange_EngineThrowsOnBadSector_IsTreatedLikeAFailedRead()
    {
        const int totalSectors = 64;
        const int badLba = 20;
        const int batchSectors = 16;
        const int maxRetries = 3;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var baseEngine = new VirtualBurnEngine { ThrowOnReadError = true };
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var unreadableCalls = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: maxRetries,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: batchSectors,
            cancellationToken: CancellationToken.None);

        Assert.Single(unreadableCalls);
        Assert.Equal(badLba, unreadableCalls[0]);
        Assert.Equal(Enumerable.Range(0, totalSectors).Where(lba => lba != badLba), deliveredLbas);
        Assert.Equal(maxRetries, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(3, countingEngine.Calls.Count(c => c == (badLba, 1)));
    }

    #endregion

    #region Test 6: Cancelled Range Read

    [Fact]
    public async Task ReadRange_Cancelled_ThrowsOperationCanceledException()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: 0,
                sectorCount: 16,
                raw2352Mode: false,
                maxRetriesPerSector: 3,
                onSector: (_, _) => ValueTask.CompletedTask,
                onUnreadable: _ => ValueTask.FromResult(true),
                cancellationToken: cts.Token));
    }

    #endregion

    #region Test 7: Raw 2352 Mode

    [Fact]
    public async Task ReadRange_Raw2352Mode_DeliversRawSizedSectors()
    {
        const int sectorCount = 8;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.Raw2352Mode = true;
        recorder.InsertMedia(medium);

        var writtenBuffers = new List<byte[]>();
        for (int lba = 0; lba < sectorCount; lba++)
        {
            byte[] rawBuffer = new byte[OpticalConstants.RawSectorSize];
            Array.Fill(rawBuffer, (byte)(lba * 17 + 1));
            medium.WriteSector(lba, rawBuffer, OpticalConstants.RawSectorSize);
            writtenBuffers.Add(rawBuffer);
        }

        var engine = new VirtualBurnEngine();
        var deliveredBuffers = new List<byte[]>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: sectorCount,
            raw2352Mode: true,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredBuffers.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ => ValueTask.FromResult(true),
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(sectorCount, deliveredBuffers.Count);
        Assert.All(deliveredBuffers, buf => Assert.Equal(OpticalConstants.RawSectorSize, buf.Length));

        for (int i = 0; i < sectorCount; i++)
        {
            Assert.Equal(writtenBuffers[i], deliveredBuffers[i]);
        }
    }

    #endregion

    #region Test 8: Invalid Batch Size Guards

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ReadRange_InvalidBatchSize_Throws(int batchSectors)
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: 0,
                sectorCount: 10,
                raw2352Mode: false,
                maxRetriesPerSector: 3,
                onSector: (_, _) => ValueTask.CompletedTask,
                onUnreadable: _ => ValueTask.FromResult(true),
                batchSectors: batchSectors));
    }

    #endregion

    #region Test 9: Overflow Guard

    [Fact]
    public async Task ReadRange_StartLbaPlusSectorCountOverflow_ThrowsArgumentOutOfRangeException()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: int.MaxValue - 10,
                sectorCount: 20,
                raw2352Mode: false,
                maxRetriesPerSector: 1,
                onSector: (_, _) => ValueTask.CompletedTask,
                onUnreadable: _ => ValueTask.FromResult(true)));
    }

    #endregion

    #region Test 10: Adaptive Salvage & Single-Handle Session Reuse

    private sealed class SessionCountingEngine : IBurnEngineBackend
    {
        private readonly Func<IScsiTransport?> _openTransport;
        public int OpenSessionCalls { get; private set; }

        public SessionCountingEngine(Func<IScsiTransport?> openTransport) => _openTransport = openTransport;

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException("Expected BatchedSectorReader to use OpenReadSession.");

        public ISectorReadSession? OpenReadSession(
            IDiscRecorderBackend recorder,
            bool raw2352Mode,
            bool readFromDisc = false,
            bool strict = false,
            SectorRange? writtenRange = null)
        {
            OpenSessionCalls++;
            return PhysicalSectorReader.OpenSession(
                _openTransport,
                raw2352Mode,
                readFromDisc ? (strict ? DriveCacheBypass.EveryRead : DriveCacheBypass.RecentlyWritten) : DriveCacheBypass.None,
                writtenRange?.Start,
                writtenRange?.Count);
        }
    }

    private sealed class ProbeAndBurstTransport : IScsiTransport
    {
        private readonly byte[] _image;
        private readonly HashSet<int> _badLbas;
        public int ProbeCount { get; private set; }
        public int Read10Count { get; private set; }
        public bool Disposed { get; private set; }

        public ProbeAndBurstTransport(byte[] image, IEnumerable<int> badLbas)
        {
            _image = image;
            _badLbas = new HashSet<int>(badLbas);
        }

        public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> data, byte direction, TimeSpan timeout)
        {
            if (cdb[0] == 0x00) // TEST UNIT READY
            {
                ProbeCount++;
                return ScsiExecutionResult.Success(0);
            }
            if (cdb[0] == 0x28) // READ (10)
            {
                Read10Count++;
                int lba = (cdb[2] << 24) | (cdb[3] << 16) | (cdb[4] << 8) | cdb[5];
                int count = (cdb[7] << 8) | cdb[8];
                for (int i = 0; i < count; i++)
                {
                    if (_badLbas.Contains(lba + i))
                    {
                        byte[] sense = new byte[18];
                        sense[0] = 0x70;
                        sense[2] = 0x03; // Medium Error
                        sense[12] = 0x11;
                        return ScsiExecutionResult.Failed(1, "Unrecovered read error", sense);
                    }
                }
                int bytes = count * OpticalConstants.DataSectorSize;
                _image.AsSpan(lba * OpticalConstants.DataSectorSize, bytes).CopyTo(data);
                return ScsiExecutionResult.Success((uint)bytes);
            }
            return ScsiExecutionResult.Failed(5, "Unsupported command");
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ReadRange_AdaptiveSalvage_RecoversAllReadableSectorsAroundBursts_AndReadsHealthyZonesFirst()
    {
        const int totalSectors = 256;
        var badBurst = Enumerable.Range(20, 25).Concat(new[] { 180, 181 }).ToHashSet();

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        foreach (int bad in badBurst)
        {
            medium.SimulatedBadLbas.Add(bad);
        }

        var countingEngine = new CallCountingEngine(new VirtualBurnEngine());
        var delivered = new Dictionary<int, byte[]>();
        var deliveryOrder = new List<int>();
        var unreadable = new HashSet<int>();
        var progress = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 1,
            onSector: (lba, data) =>
            {
                Assert.False(delivered.ContainsKey(lba), $"Duplicate delivery of LBA {lba}");
                delivered[lba] = data.ToArray();
                deliveryOrder.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                Assert.True(unreadable.Add(lba), $"Duplicate unreadable report for LBA {lba}");
                return ValueTask.FromResult(true);
            },
            onProgress: p => progress.Add(p),
            batchSectors: 16,
            cancellationToken: CancellationToken.None,
            adaptiveSalvage: true);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(badBurst, unreadable);
        Assert.Equal(totalSectors - badBurst.Count, delivered.Count);
        foreach (var (lba, data) in delivered)
        {
            byte expectedByte = (byte)(lba % 251 + 1);
            Assert.All(data, b => Assert.Equal(expectedByte, b));
        }

        // Healthy outer half (>= 138) was salvaged before zooming back to the edge of the burst (45).
        int firstOuterIndex = deliveryOrder.FindIndex(lba => lba >= 200);
        int nearBurstEndIndex = deliveryOrder.FindIndex(lba => lba == 45);
        Assert.True(firstOuterIndex >= 0 && nearBurstEndIndex >= 0 && firstOuterIndex < nearBurstEndIndex,
            $"Expected outer zone (>=200) to be read before zooming to LBA 45 (outer idx={firstOuterIndex}, nearBurst idx={nearBurstEndIndex}).");
        Assert.Equal(totalSectors, progress.Last());
    }

    [Fact]
    public async Task ReadRange_WithPhysicalSession_ReusesSingleOpenHandleAcrossBatchesAndAdaptiveSalvage()
    {
        const int totalSectors = 1300; // > 2 batches of 512 sectors
        var badBurst = Enumerable.Range(100, 12).Concat(new[] { 900 }).ToHashSet();
        byte[] image = new byte[totalSectors * OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(image, (byte)(lba % 251 + 1), lba * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize);
        }

        int transportOpened = 0;
        ProbeAndBurstTransport? createdTransport = null;
        var engine = new SessionCountingEngine(() =>
        {
            transportOpened++;
            createdTransport = new ProbeAndBurstTransport(image, badBurst);
            return createdTransport;
        });

        using var recorder = new VirtualDiscRecorder();
        var delivered = new Dictionary<int, byte>();
        var unreadable = new HashSet<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 2,
            onSector: (lba, data) =>
            {
                delivered[lba] = data.Span[0];
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadable.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: 512,
            cancellationToken: CancellationToken.None,
            adaptiveSalvage: true);

        Assert.True(outcome.Completed);
        Assert.Equal(1, engine.OpenSessionCalls);
        Assert.Equal(1, transportOpened);
        Assert.NotNull(createdTransport);
        Assert.Equal(1, createdTransport!.ProbeCount);
        Assert.True(createdTransport.Disposed);
        Assert.Equal(badBurst, unreadable);
        Assert.Equal(totalSectors - badBurst.Count, delivered.Count);
        foreach (var (lba, firstByte) in delivered)
        {
            Assert.Equal((byte)(lba % 251 + 1), firstByte);
        }
    }

    #endregion
}
