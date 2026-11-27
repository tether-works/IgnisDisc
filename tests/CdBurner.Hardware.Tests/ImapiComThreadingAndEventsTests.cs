using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;
using Xunit;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace CdBurner.Hardware.Tests;

public class ImapiComThreadingAndEventsTests
{
    #region Imapi2BurnEngine MTA Isolation, Exclusive Lock, and Teardown Order Tests

    [Fact]
    public async Task Imapi2BurnEngine_BurnDataStream_CreatesFreshMtaRecorder_WithoutTouchingStaRecorder()
    {
        var events = new List<string>();
        var staRecorder = new TrackingComRecorder("STA", events);
        var mtaRecorder = new TrackingComRecorder("MTA", events);
        var provider = new TrackingImapiComProvider(mtaRecorder, events);

        using var recorder = new ImapiDiscRecorder(staRecorder, "SIM-MTA-DATA", provider);
        var engine = new Imapi2BurnEngine(provider);

        using var iso = new MemoryStream(new byte[4 * OpticalConstants.DataSectorSize]);
        var result = await engine.BurnDataStreamAsync(
            recorder,
            iso,
            new DataBurnOptions { EjectAfterBurn = true },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, staRecorder.CallCount);
        Assert.Equal(ApartmentState.MTA, mtaRecorder.InitializeApartment);
        Assert.Equal("SIM-MTA-DATA", mtaRecorder.InitializedUniqueId);
        Assert.Same(mtaRecorder, provider.FormatData.AttachedRecorder);
        Assert.True(mtaRecorder.AcquireCount == 1);
        Assert.True(mtaRecorder.EjectCount == 1);
        Assert.True(mtaRecorder.ReleaseCount == 1);

        int releaseFormatIdx = events.IndexOf("Release:FakeFormat2Data");
        int releaseLockIdx = events.IndexOf("Recorder:MTA:ReleaseExclusiveAccess");
        int releaseComRecIdx = events.IndexOf("Release:Recorder:MTA");
        Assert.True(releaseFormatIdx >= 0);
        Assert.True(releaseLockIdx > releaseFormatIdx);
        Assert.True(releaseComRecIdx > releaseLockIdx);
    }

    [Fact]
    public async Task Imapi2BurnEngine_BurnDataStream_Audio_And_Erase_FailFastWhenExclusiveLockDenied()
    {
        // Case 1: Custom backend returns false from AcquireExclusiveAccess
        var deniedBackend = new LockDeniedRecorderBackend();
        var provider1 = new TrackingImapiComProvider(new TrackingComRecorder("MTA1"), new List<string>());
        var engine1 = new Imapi2BurnEngine(provider1) { ExclusiveAccessRetryDelay = TimeSpan.Zero };

        using var iso = new MemoryStream(new byte[2 * OpticalConstants.DataSectorSize]);
        var dataRes1 = await engine1.BurnDataStreamAsync(deniedBackend, iso, new DataBurnOptions(), null, CancellationToken.None);
        Assert.False(dataRes1.Success);
        Assert.Contains("exclusive access", dataRes1.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider1.FormatData.AttachedRecorder);
        Assert.False(provider1.FormatData.WriteCalled);

        // Case 2: ImapiDiscRecorder whose MTA IDiscRecorder2 throws on AcquireExclusiveAccess
        var staRecorder = new TrackingComRecorder("STA");
        var mtaRecorder = new TrackingComRecorder("MTA2") { ThrowOnAcquireExclusive = true };
        var provider2 = new TrackingImapiComProvider(mtaRecorder, new List<string>());
        using var imapiRec = new ImapiDiscRecorder(staRecorder, "SIM-LOCKED", provider2);
        var engine2 = new Imapi2BurnEngine(provider2) { ExclusiveAccessRetryDelay = TimeSpan.Zero };

        var dataRes2 = await engine2.BurnDataStreamAsync(imapiRec, iso, new DataBurnOptions(), null, CancellationToken.None);
        Assert.Equal(engine2.ExclusiveAccessAttempts, mtaRecorder.AcquireAttempts); // asked again before giving up
        Assert.False(dataRes2.Success);
        Assert.Contains("exclusive access", dataRes2.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider2.FormatData.AttachedRecorder);
        Assert.Equal(0, mtaRecorder.ReleaseCount);
        Assert.Equal(0, staRecorder.CallCount);

        var tracks = new[]
        {
            new AudioTrackSource
            {
                TrackNumber = 1,
                Title = "Track 1",
                TotalPcmBytes = 300 * OpticalConstants.RawSectorSize,
                PcmStreamFactory = () => new MemoryStream(new byte[300 * OpticalConstants.RawSectorSize])
            }
        };
        var audioRes = await engine2.BurnAudioTracksAsync(imapiRec, tracks, new AudioBurnOptions { UseDiscAtOnce = true }, null, CancellationToken.None);
        Assert.False(audioRes.Success);
        Assert.Contains("exclusive access", audioRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider2.FormatRawCd.AttachedRecorder);
        Assert.Null(provider2.FormatTao.AttachedRecorder);
        Assert.Equal(0, mtaRecorder.ReleaseCount);

        var eraseRes = await engine2.EraseMediaAsync(imapiRec, fullErase: false, null, CancellationToken.None);
        Assert.False(eraseRes.Success);
        Assert.Contains("exclusive access", eraseRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(provider2.FormatErase.AttachedRecorder);
        Assert.Equal(0, mtaRecorder.ReleaseCount);
    }

    [Fact]
    public async Task Imapi2BurnEngine_Erase_WaitsOutALockAnotherProgramHoldsBriefly()
    {
        // Measured 2026-11-26: an erase straight after a Blu-ray burn was refused (Windows was still looking at the new
        // disc), and the same erase 10 s later worked. Two refusals, then the lock is granted.
        var staRecorder = new TrackingComRecorder("STA");
        var mtaRecorder = new TrackingComRecorder("MTA") { RefuseAcquireTimes = 2 };
        var provider = new TrackingImapiComProvider(mtaRecorder, new List<string>());
        using var imapiRec = new ImapiDiscRecorder(staRecorder, "SIM-BUSY", provider);
        var engine = new Imapi2BurnEngine(provider) { ExclusiveAccessRetryDelay = TimeSpan.Zero };

        var result = await engine.EraseMediaAsync(imapiRec, fullErase: false, null, CancellationToken.None);

        Assert.DoesNotContain("exclusive access", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, mtaRecorder.AcquireAttempts);
        Assert.Equal(1, mtaRecorder.AcquireCount);
        Assert.Equal(1, mtaRecorder.ReleaseCount);
    }

    [Fact]
    public async Task Imapi2BurnEngine_BurnAudioTracks_Dao_ReleasesRawCdAndRawCreator_BeforeDisposingManagedComStreams()
    {
        var events = new List<string>();
        var staRecorder = new TrackingComRecorder("STA", events);
        var mtaRecorder = new TrackingComRecorder("MTA", events);
        var provider = new TrackingImapiComProvider(mtaRecorder, events);

        using var recorder = new ImapiDiscRecorder(staRecorder, "SIM-DAO-ORDER", provider);
        var engine = new Imapi2BurnEngine(provider);

        var trackStream = new DisposeTrackingMemoryStream(new byte[300 * OpticalConstants.RawSectorSize], events, "Track1Stream");
        var tracks = new[]
        {
            new AudioTrackSource
            {
                TrackNumber = 1,
                Title = "Track 1",
                TotalPcmBytes = trackStream.Length,
                PcmStreamFactory = () => trackStream
            }
        };

        var result = await engine.BurnAudioTracksAsync(
            recorder,
            tracks,
            new AudioBurnOptions { UseDiscAtOnce = true, EjectWhenDone = true },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, staRecorder.CallCount);
        Assert.Equal(ApartmentState.MTA, mtaRecorder.InitializeApartment);
        Assert.True(mtaRecorder.EjectCount == 1);
        Assert.True(mtaRecorder.ReleaseCount == 1);

        int releaseRawCdIdx = events.IndexOf("Release:FakeFormat2RawCD");
        int releaseRawCreatorIdx = events.IndexOf("Release:FakeRawCDImageCreator");
        int disposeTrackStreamIdx = events.IndexOf("Dispose:Track1Stream");

        Assert.True(releaseRawCdIdx >= 0);
        Assert.True(releaseRawCreatorIdx > releaseRawCdIdx);
        Assert.True(disposeTrackStreamIdx > releaseRawCreatorIdx,
            $"Track stream must be disposed after rawCd/rawCreator release. Events: {string.Join(" -> ", events)}");
        Assert.False(provider.RawCreator.ObservedDisposedTrackStreamOnRelease);
    }

    [Fact]
    public async Task Imapi2BurnEngine_EraseMediaAsync_ConnectsEraseSink_StreamsProgress_AndReleasesInOrder()
    {
        var events = new List<string>();
        var staRecorder = new TrackingComRecorder("STA", events);
        var mtaRecorder = new TrackingComRecorder("MTA", events);
        var provider = new TrackingImapiComProvider(mtaRecorder, events);

        using var recorder = new ImapiDiscRecorder(staRecorder, "SIM-ERASE", provider);
        var engine = new Imapi2BurnEngine(provider);
        var reported = new List<BurnProgressSnapshot>();
        var progress = new ImmediateProgress<BurnProgressSnapshot>(s => reported.Add(s));

        var result = await engine.EraseMediaAsync(recorder, fullErase: true, progress, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, staRecorder.CallCount);
        Assert.Equal(ApartmentState.MTA, mtaRecorder.InitializeApartment);
        Assert.True(provider.FormatErase.Advised);
        Assert.True(provider.FormatErase.Unadvised);

        Assert.Contains(reported, s => s.CurrentActionDescription == "FullErasing" && s.CurrentLba == 15 && s.TotalLba == 60);
        Assert.Contains(reported, s => s.CurrentActionDescription == "Completed" && s.CurrentLba == 100);

        int unadviseIdx = events.IndexOf("Unadvise:FakeFormat2Erase");
        int releaseEraseIdx = events.IndexOf("Release:FakeFormat2Erase");
        int releaseLockIdx = events.IndexOf("Recorder:MTA:ReleaseExclusiveAccess");
        int releaseRecIdx = events.IndexOf("Release:Recorder:MTA");
        Assert.True(unadviseIdx >= 0);
        Assert.True(releaseEraseIdx > unadviseIdx);
        Assert.True(releaseLockIdx > releaseEraseIdx);
        Assert.True(releaseRecIdx > releaseLockIdx);
    }

    #endregion

    #region DiscFormatDataEventsSink & DiscFormatEraseEventsSink Tests

    [Fact]
    public void DiscFormatDataEventsSink_Update_CatchesCallbackExceptionsCleanly()
    {
        var throwingProgress = new ThrowingProgress();
        bool secondEventRan = false;

        using var sink = new DiscFormatDataEventsSink(
            throwingProgress,
            onSnapshot: _ => throw new InvalidOperationException("onSnapshot boom"));
        sink.ProgressReported += (_, _) => throw new ApplicationException("event boom");
        sink.ProgressReported += (_, _) => secondEventRan = true;

        var args = new DuckTypedProgressArgs
        {
            ElapsedTime = 5,
            EstimatedRemainingTime = 25,
            StartLba = 0,
            SectorCount = 1_000,
            LastReadLba = 200,
            LastWrittenLba = 199,
            TotalSystemBuffer = 4_000_000,
            UsedSystemBuffer = 2_000_000,
            FreeSystemBuffer = 2_000_000,
            CurrentAction = (int)ImapiFormat2DataWriteAction.WritingData
        };

        var ex = Record.Exception(() => sink.Update(sender: null!, progress: args));
        Assert.Null(ex);
        Assert.False(secondEventRan); // first event handler threw inside event invocation, safely caught in Update
    }

    [Theory]
    [InlineData(ImapiFormat2DataWriteAction.ValidatingMedia)]
    [InlineData(ImapiFormat2DataWriteAction.FormattingMedia)]
    [InlineData(ImapiFormat2DataWriteAction.InitializingHardware)]
    [InlineData(ImapiFormat2DataWriteAction.CalibratingPower)]
    public void DiscFormatDataEventsSink_CreateSnapshot_EmitsRelativeLbaZero_PreWrite(ImapiFormat2DataWriteAction preWriteAction)
    {
        var snapshot = DiscFormatDataEventsSink.CreateSnapshot(
            elapsedTimeSec: 3,
            remainingTimeSec: 120,
            startLba: 0,
            sectorCount: 10_000,
            lastReadLba: 0,
            lastWrittenLba: 0,
            totalSystemBuffer: 8_388_608,
            usedSystemBuffer: 0,
            freeSystemBuffer: 8_388_608,
            currentAction: preWriteAction);

        Assert.Equal(0, snapshot.CurrentLba);
        Assert.Equal(0.0, snapshot.ProgressPercentage);
        Assert.Equal(0, snapshot.CurrentBytesPerSecond);
    }

    [Fact]
    public void DiscFormatDataEventsSink_CreateSnapshot_MultiSessionBeforeFirstWrite_EmitsRelativeLbaZero()
    {
        var snapshot = DiscFormatDataEventsSink.CreateSnapshot(
            elapsedTimeSec: 2,
            remainingTimeSec: 60,
            startLba: 13_630,
            sectorCount: 5_000,
            lastReadLba: 13_630,
            lastWrittenLba: 13_629,
            totalSystemBuffer: 8_388_608,
            usedSystemBuffer: 0,
            freeSystemBuffer: 8_388_608,
            currentAction: ImapiFormat2DataWriteAction.WritingData);

        Assert.Equal(0, snapshot.CurrentLba);
    }

    [Fact]
    public void DiscFormatDataEventsSink_ExtractSnapshot_CoversDuckTypedReadIntPropFallback()
    {
        Assert.Null(DiscFormatDataEventsSink.ExtractSnapshot(null, null));

        var direct = new BurnProgressSnapshot { CurrentLba = 42, TotalLba = 100 };
        Assert.Same(direct, DiscFormatDataEventsSink.ExtractSnapshot(null, direct));

        var duck = new DuckTypedProgressArgs
        {
            ElapsedTime = 10,
            EstimatedRemainingTime = 30,
            StartLba = 100,
            SectorCount = 1_000,
            LastReadLba = 360,
            LastWrittenLba = 349,
            TotalSystemBuffer = 1_000,
            UsedSystemBuffer = 500,
            FreeSystemBuffer = 500,
            CurrentAction = (int)ImapiFormat2DataWriteAction.WritingData
        };

        var snap = DiscFormatDataEventsSink.ExtractSnapshot(sender: null, progress: duck);
        Assert.NotNull(snap);
        Assert.Equal(TimeSpan.FromSeconds(10), snap!.ElapsedTime);
        Assert.Equal(TimeSpan.FromSeconds(30), snap.RemainingTime);
        Assert.Equal(250, snap.CurrentLba);
        Assert.Equal(1_000, snap.TotalLba);
        Assert.Equal(50.0, snap.BufferLevelPercent, precision: 1);
        Assert.Equal("WritingData", snap.CurrentActionDescription);

        Assert.Null(DiscFormatDataEventsSink.ExtractSnapshot(null, new ThrowingProgressArgs()));
    }

    [Fact]
    public void DiscFormatEraseEventsSink_ConnectUpdateDisconnect_AndCallbackExceptionSafety()
    {
        var container = new FakeEraseFormatterWithConnectionPoint(new List<string>());
        var snapshots = new List<BurnProgressSnapshot>();
        using var sink = new DiscFormatEraseEventsSink(
            new ImmediateProgress<BurnProgressSnapshot>(s => snapshots.Add(s)),
            "QuickErasing",
            onSnapshot: _ => throw new InvalidOperationException("callback boom"));

        sink.Connect(container);
        Assert.True(sink.IsConnected);

        var ex = Record.Exception(() => sink.Update(container, elapsedSeconds: 12, estimatedTotalSeconds: 40));
        Assert.Null(ex);
        var snap = Assert.Single(snapshots);
        Assert.Equal(12, snap.CurrentLba);
        Assert.Equal(40, snap.TotalLba);
        Assert.Equal(TimeSpan.FromSeconds(12), snap.ElapsedTime);
        Assert.Equal(TimeSpan.FromSeconds(28), snap.RemainingTime);
        Assert.Equal("QuickErasing", snap.CurrentActionDescription);

        sink.Disconnect();
        Assert.False(sink.IsConnected);
    }

    #endregion

    #region DiscDeviceManager Lock & RCW Leak + ImapiDiscRecorder Disposal Tests

    [Fact]
    public async Task DiscDeviceManager_RefreshPhysicalRecorders_ReleasesRcwOnInitializeThrow_AndDoesNotHoldLockDuringComActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var initEntered = new ManualResetEventSlim(false);
        using var releaseInit = new ManualResetEventSlim(false);
        var events = new List<string>();
        var provider = new DeviceManagerFakeComProvider(events, initEntered, releaseInit);

        // Start manager with virtual-only first so constructor returns immediately, then trigger refresh via task.
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: false, changeMonitor: null, comProvider: provider);
        var refreshTask = Task.Run(() => manager.EnablePhysicalDiscovery(true));

        Assert.True(initEntered.Wait(TimeSpan.FromSeconds(5)));

        // While COM InitializeDiscRecorder is blocked inside RefreshPhysicalRecorders,
        // GetRecorders and AddVirtualRecorder must NOT block on _lock.
        var nonBlockingReadTask = Task.Run(() =>
        {
            var list = manager.GetRecorders(includeVirtual: true);
            Assert.NotEmpty(list);
            return list.Count;
        });

        var completed = await Task.WhenAny(nonBlockingReadTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(nonBlockingReadTask, completed);
        Assert.Equal(1, await nonBlockingReadTask);

        releaseInit.Set();
        await refreshTask;

        // Device 0 threw in InitializeDiscRecorder and must be released; Device 1 succeeded.
        Assert.Contains("Release:DiscRecorder:BAD-ID", events);
        Assert.Contains("Release:DiscMaster", events);

        var physical = manager.GetRecorders(includeVirtual: false);
        var good = Assert.Single(physical);
        Assert.Equal("GOOD-ID", good.UniqueId);
    }

    [Fact]
    public void ImapiDiscRecorder_Dispose_IsThreadSafe_AndFinalizerDoesNotRunReleaseComObject()
    {
        var events = new List<string>();
        var comRec = new TrackingComRecorder("REC1", events);
        var provider = new TrackingImapiComProvider(comRec, events);

        var rec1 = new ImapiDiscRecorder(comRec, "REC1", provider);
        // Simulate concurrent Dispose() calls: ReleaseComObject must run once.
        Parallel.Invoke(
            () => rec1.Dispose(),
            () => rec1.Dispose(),
            () => rec1.Dispose());

        Assert.Equal(1, events.Count(e => e == "Release:Recorder:REC1"));

        // Verify ~ImapiDiscRecorder() / Dispose(false) does NOT invoke ReleaseComObject on finalizer thread.
        events.Clear();
        var rec2 = new FinalizableImapiDiscRecorder(comRec, "REC2", provider);
        rec2.InvokeFinalizerDispose();
        Assert.DoesNotContain("Release:Recorder:REC2", events);
    }

    #endregion

    #region Fakes & Test Doubles

    private sealed class FinalizableImapiDiscRecorder : ImapiDiscRecorder
    {
        public FinalizableImapiDiscRecorder(IDiscRecorder2 recorder, string uniqueId, IImapiComProvider provider)
            : base(recorder, uniqueId, provider)
        {
        }

        public void InvokeFinalizerDispose() => Dispose(false);
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public ImmediateProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class ThrowingProgress : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => throw new InvalidOperationException("progress sink error");
    }

    private sealed class DuckTypedProgressArgs
    {
        public int ElapsedTime { get; set; }
        public int EstimatedRemainingTime { get; set; }
        public int StartLba { get; set; }
        public int SectorCount { get; set; }
        public int LastReadLba { get; set; }
        public int LastWrittenLba { get; set; }
        public int TotalSystemBuffer { get; set; }
        public int UsedSystemBuffer { get; set; }
        public int FreeSystemBuffer { get; set; }
        public int CurrentAction { get; set; }
    }

    private sealed class ThrowingProgressArgs
    {
        public int ElapsedTime => throw new InvalidOperationException("getter failed");
    }

    private sealed class DisposeTrackingMemoryStream : MemoryStream
    {
        private readonly List<string> _events;
        private readonly string _name;

        public bool WasDisposed { get; private set; }

        public DisposeTrackingMemoryStream(byte[] buffer, List<string> events, string name)
            : base(buffer)
        {
            _events = events;
            _name = name;
        }

        protected override void Dispose(bool disposing)
        {
            if (!WasDisposed)
            {
                WasDisposed = true;
                _events.Add($"Dispose:{_name}");
            }
            base.Dispose(disposing);
        }
    }

    private sealed class LockDeniedRecorderBackend : IDiscRecorderBackend
    {
        public string UniqueId => "DENIED-01";
        public string VendorId => "VENDOR";
        public string ProductId => "DRIVE";
        public string ProductRevision => "1.0";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => false;

        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => new();
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => false;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class TrackingComRecorder : IDiscRecorder2
    {
        private readonly string _tag;
        private readonly List<string>? _events;
        public string Tag { get; }
        public int CallCount { get; private set; }
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int EjectCount { get; private set; }
        public bool ThrowOnAcquireExclusive { get; set; }
        public bool ThrowOnInitialize { get; set; }
        public string? InitializedUniqueId { get; private set; }
        public ApartmentState InitializeApartment { get; private set; } = ApartmentState.Unknown;

        public TrackingComRecorder(string tag, List<string>? events = null)
        {
            Tag = tag;
            _tag = tag;
            _events = events;
        }

        public void EjectMedia() { CallCount++; EjectCount++; _events?.Add($"Recorder:{_tag}:EjectMedia"); }
        public void CloseTray() { CallCount++; }
        /// <summary>Refuse this many requests for exclusive access, then grant them (a lock another program holds briefly).</summary>
        public int RefuseAcquireTimes { get; set; }
        public int AcquireAttempts { get; private set; }

        public void AcquireExclusiveAccess(bool force, string client)
        {
            CallCount++;
            AcquireAttempts++;
            if (ThrowOnAcquireExclusive || AcquireAttempts <= RefuseAcquireTimes)
            {
                throw new COMException("Locked by another app", unchecked((int)0xC0AA0207));
            }
            AcquireCount++;
            _events?.Add($"Recorder:{_tag}:AcquireExclusiveAccess");
        }
        public void ReleaseExclusiveAccess() { CallCount++; ReleaseCount++; _events?.Add($"Recorder:{_tag}:ReleaseExclusiveAccess"); }
        public void DisableMcn() { CallCount++; }
        public void EnableMcn() { CallCount++; }
        public void InitializeDiscRecorder(string recorderUniqueId)
        {
            CallCount++;
            InitializeApartment = Thread.CurrentThread.GetApartmentState();
            InitializedUniqueId = recorderUniqueId;
            if (ThrowOnInitialize)
            {
                throw new COMException("Init failed", unchecked((int)0x80004005));
            }
        }
        public string ActiveDiscRecorder { get { CallCount++; return InitializedUniqueId ?? _tag; } }
        public string VendorId { get { CallCount++; return "TRACKING"; } }
        public string ProductId { get { CallCount++; return "DRIVE"; } }
        public string ProductRevision { get { CallCount++; return "1.0"; } }
        public string VolumeName { get { CallCount++; return @"\\?\Volume{1}\"; } }
        public Array VolumePathNames { get { CallCount++; return new object[] { @"Z:\" }; } }
        public bool DeviceCanLoadMedia { get { CallCount++; return true; } }
        public int LegacyDeviceNumber { get { CallCount++; return 0; } }
        public Array SupportedFeaturePages => Array.Empty<object>();
        public Array CurrentFeaturePages => Array.Empty<object>();
        public Array SupportedProfiles => Array.Empty<object>();
        public Array CurrentProfiles => Array.Empty<object>();
        public Array SupportedModePages => Array.Empty<object>();
        public string ExclusiveAccessOwner => string.Empty;
    }

    private sealed class FakeFormat2Data : IDiscFormat2Data
    {
        public IDiscRecorder2? AttachedRecorder { get; private set; }
        public bool WriteCalled { get; private set; }
        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => true;
        public bool MediaHeuristicallyBlank => true;
        public Array SupportedMediaTypes => Array.Empty<object>();
        public void put_Recorder(IDiscRecorder2? value) => AttachedRecorder = value;
        public IDiscRecorder2? get_Recorder() => AttachedRecorder;
        public void put_BufferUnderrunFreeDisabled(bool value) { }
        public bool get_BufferUnderrunFreeDisabled() => false;
        public void put_PostgapAlreadyInImage(bool value) { }
        public bool get_PostgapAlreadyInImage() => false;
        public ImapiFormat2DataMediaState CurrentMediaStatus => ImapiFormat2DataMediaState.Blank;
        public ImapiMediaWriteProtectState WriteProtectStatus => ImapiMediaWriteProtectState.None;
        public int TotalSectorsOnMedia => 359_847;
        public int FreeSectorsOnMedia => 359_847;
        public int NextWritableAddress => 0;
        public int StartAddressOfPreviousSession => 0;
        public int LastWrittenAddressOfPreviousSession => 0;
        public void put_ForceMediaToBeClosed(bool value) { }
        public bool get_ForceMediaToBeClosed() => true;
        public void put_DisableConsumerDvdCompatibilityMode(bool value) { }
        public bool get_DisableConsumerDvdCompatibilityMode() => false;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdR;
        public void put_ClientName(string value) { }
        public string get_ClientName() => "CdBurner";
        public int RequestedWriteSpeed => 1200;
        public bool RequestedRotationTypeIsPureCAV => false;
        public int CurrentWriteSpeed => 1200;
        public bool CurrentRotationTypeIsPureCAV => false;
        public Array SupportedWriteSpeeds => new object[] { 1200 };
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
        public void put_ForceOverwrite(bool value) { }
        public bool get_ForceOverwrite() => false;
        public Array MultisessionInterfaces => Array.Empty<object>();
        public void Write(ComIStream data)
        {
            WriteCalled = true;
            byte[] buf = new byte[4096];
            IntPtr pcbRead = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                while (true)
                {
                    data.Read(buf, buf.Length, pcbRead);
                    if (Marshal.ReadInt32(pcbRead) <= 0) break;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pcbRead);
            }
        }
        public void CancelWrite() { }
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV) { }
    }

    private sealed class FakeFormat2RawCD : IDiscFormat2RawCD
    {
        public IDiscRecorder2? AttachedRecorder { get; private set; }
        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => true;
        public bool MediaHeuristicallyBlank => true;
        public Array SupportedMediaTypes => Array.Empty<object>();
        public void PrepareMedia() { }
        public void WriteMedia(ComIStream data)
        {
            byte[] buf = new byte[4096];
            IntPtr pcbRead = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                while (true)
                {
                    data.Read(buf, buf.Length, pcbRead);
                    if (Marshal.ReadInt32(pcbRead) <= 0) break;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pcbRead);
            }
        }
        public void WriteMedia2(ComIStream data, int streamLeadInSectors) => WriteMedia(data);
        public void CancelWrite() { }
        public void ReleaseMedia() { }
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV) { }
        public void put_Recorder(IDiscRecorder2? value) => AttachedRecorder = value;
        public IDiscRecorder2? get_Recorder() => AttachedRecorder;
        public void put_BufferUnderrunFreeDisabled(bool value) { }
        public bool get_BufferUnderrunFreeDisabled() => false;
        public int StartOfNextSession => 0;
        public int LastPossibleStartOfLeadout => 359_847;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdR;
        public Array SupportedSectorTypes => Array.Empty<object>();
        public void put_RequestedSectorType(ImapiFormat2RawCdDataType value) { }
        public ImapiFormat2RawCdDataType get_RequestedSectorType() => ImapiFormat2RawCdDataType.SubcodeIsRaw;
        public void put_ClientName(string value) { }
        public string get_ClientName() => "CdBurner";
        public int RequestedWriteSpeed => 1200;
        public bool RequestedRotationTypeIsPureCAV => false;
        public int CurrentWriteSpeed => 1200;
        public bool CurrentRotationTypeIsPureCAV => false;
        public Array SupportedWriteSpeeds => new object[] { 1200 };
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
    }

    private sealed class FakeRawCDImageCreator : IRawCDImageCreator
    {
        private readonly List<ComIStream> _trackStreams = new();
        public bool ObservedDisposedTrackStreamOnRelease { get; private set; }

        public ComIStream CreateResultImage() => new ManagedComStream(new MemoryStream(new byte[300 * OpticalConstants.RawSectorSize]), leaveOpen: false);

        public int AddTrack(int dataType, ComIStream? data)
        {
            if (data != null)
            {
                _trackStreams.Add(data);
                byte[] buf = new byte[4096];
                IntPtr pcbRead = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        data.Read(buf, buf.Length, pcbRead);
                        if (Marshal.ReadInt32(pcbRead) <= 0) break;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pcbRead);
                }
            }
            return _trackStreams.Count;
        }

        public void VerifyTrackStreamsStillOpen()
        {
            foreach (var s in _trackStreams)
            {
                if (s is ManagedComStream mcs && mcs.IsDisposed)
                {
                    ObservedDisposedTrackStreamOnRelease = true;
                }
            }
        }

        public void AddSpecialPregap(ComIStream data) { }
        public void AddSubcodeRWGenerator(ComIStream subcode) { }
        public void put_ResultingImageType(ImapiFormat2RawCdDataType value) { }
        public ImapiFormat2RawCdDataType get_ResultingImageType() => ImapiFormat2RawCdDataType.SubcodeIsRaw;
        public int StartOfLeadout => 300;
        public void put_StartOfLeadoutLimit(int value) { }
        public int get_StartOfLeadoutLimit() => 359_847;
        public void put_DisableGaplessAudio(bool value) { }
        public bool get_DisableGaplessAudio() => false;
        public void put_MediaCatalogNumber(string value) { }
        public string get_MediaCatalogNumber() => string.Empty;
        public void put_StartingTrackNumber(int value) { }
        public int get_StartingTrackNumber() => 1;
    }

    private sealed class FakeFormat2Tao : IDiscFormat2TrackAtOnce
    {
        public IDiscRecorder2? AttachedRecorder { get; private set; }
        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => true;
        public bool MediaHeuristicallyBlank => true;
        public Array SupportedMediaTypes => Array.Empty<object>();
        public void PrepareMedia() { }
        public void AddAudioTrack(ComIStream data) { }
        public void CancelAddTrack() { }
        public void ReleaseMedia() { }
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV) { }
        public void put_Recorder(IDiscRecorder2? value) => AttachedRecorder = value;
        public IDiscRecorder2? get_Recorder() => AttachedRecorder;
        public void put_BufferUnderrunFreeDisabled(bool value) { }
        public bool get_BufferUnderrunFreeDisabled() => false;
        public int NumberOfExistingTracks => 0;
        public int TotalSectorsOnMedia => 359_847;
        public int FreeSectorsOnMedia => 359_847;
        public int UsedSectorsOnMedia => 0;
        public void put_DoNotFinalizeMedia(bool value) { }
        public bool get_DoNotFinalizeMedia() => false;
        public Array ExpectedTableOfContents => Array.Empty<object>();
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdR;
        public void put_ClientName(string value) { }
        public string get_ClientName() => "CdBurner";
        public int RequestedWriteSpeed => 1200;
        public bool RequestedRotationTypeIsPureCAV => false;
        public int CurrentWriteSpeed => 1200;
        public bool CurrentRotationTypeIsPureCAV => false;
        public Array SupportedWriteSpeeds => new object[] { 1200 };
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
    }

    private sealed class FakeEraseFormatterWithConnectionPoint : IDiscFormat2Erase, IConnectionPointContainer, IConnectionPoint
    {
        private readonly List<string> _events;
        private DDiscFormat2EraseEvents? _sink;
        private int _cookie;

        public FakeEraseFormatterWithConnectionPoint(List<string> events) => _events = events;

        public IDiscRecorder2? AttachedRecorder { get; private set; }
        public bool Advised { get; private set; }
        public bool Unadvised { get; private set; }

        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => false;
        public bool MediaHeuristicallyBlank => false;
        public Array SupportedMediaTypes => Array.Empty<object>();
        public void put_Recorder(IDiscRecorder2? value) => AttachedRecorder = value;
        public IDiscRecorder2? get_Recorder() => AttachedRecorder;
        public void put_FullErase(bool value) { }
        public bool get_FullErase() => true;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdRw;
        public void put_ClientName(string value) { }
        public string get_ClientName() => "CdBurner";
        public void EraseMedia()
        {
            _sink?.Update(this, 15, 60);
        }

        public void EnumConnectionPoints(out IEnumConnectionPoints ppEnum) => throw new NotImplementedException();
        public void FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP)
        {
            ppCP = riid == Imapi2Guids.IidDDiscFormat2EraseEvents ? this : null!;
        }

        public void GetConnectionInterface(out Guid pIID) => pIID = Imapi2Guids.IidDDiscFormat2EraseEvents;
        public void GetConnectionPointContainer(out IConnectionPointContainer ppCPC) => ppCPC = this;
        public void Advise(object pUnkSink, out int pdwCookie)
        {
            _sink = (DDiscFormat2EraseEvents)pUnkSink;
            _cookie = 77;
            pdwCookie = _cookie;
            Advised = true;
        }
        public void Unadvise(int dwCookie)
        {
            if (dwCookie == _cookie)
            {
                _sink = null;
                Unadvised = true;
                _events.Add("Unadvise:FakeFormat2Erase");
            }
        }
        public void EnumConnections(out IEnumConnections ppEnum) => throw new NotImplementedException();
    }

    private sealed class TrackingImapiComProvider : IImapiComProvider
    {
        private readonly TrackingComRecorder _mtaRecorder;
        private readonly List<string> _events;

        public FakeFormat2Data FormatData { get; } = new();
        public FakeFormat2RawCD FormatRawCd { get; } = new();
        public FakeRawCDImageCreator RawCreator { get; } = new();
        public FakeFormat2Tao FormatTao { get; } = new();
        public FakeEraseFormatterWithConnectionPoint FormatErase { get; }

        public TrackingImapiComProvider(TrackingComRecorder mtaRecorder, List<string> events)
        {
            _mtaRecorder = mtaRecorder;
            _events = events;
            FormatErase = new FakeEraseFormatterWithConnectionPoint(events);
        }

        public IDiscMaster2? CreateDiscMaster2() => null;
        public IDiscRecorder2? CreateDiscRecorder2() => _mtaRecorder;
        public IDiscFormat2Data? CreateDiscFormat2Data() => FormatData;
        public IDiscFormat2TrackAtOnce? CreateDiscFormat2TrackAtOnce() => FormatTao;
        public IDiscFormat2RawCD? CreateDiscFormat2RawCD() => FormatRawCd;
        public IDiscFormat2Erase? CreateDiscFormat2Erase() => FormatErase;
        public IRawCDImageCreator? CreateRawCDImageCreator() => RawCreator;
        public int? ReadCapacity(string volumePath) => null;
        public int? ReadSessionCount(string volumePath) => null;
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents(string volumePath) => Array.Empty<DiscTocTrack>();
        public byte[]? ReadCdTextPacks(string volumePath) => null;
        public int? TryGetMountedVolumeSectors(string? volumePath) => null;
        public int? ReadFileSystemEndSector(string volumePath) => null;
        public bool TrySpinDown(string? volumePath) => true;

        public void ReleaseComObject<T>(ref T? comObject) where T : class
        {
            if (comObject == null) return;
            if (ReferenceEquals(comObject, RawCreator))
            {
                RawCreator.VerifyTrackStreamsStillOpen();
            }

            string label = comObject switch
            {
                TrackingComRecorder t => $"Recorder:{t.Tag}",
                FakeFormat2Data => "FakeFormat2Data",
                FakeFormat2RawCD => "FakeFormat2RawCD",
                FakeRawCDImageCreator => "FakeRawCDImageCreator",
                FakeEraseFormatterWithConnectionPoint => "FakeFormat2Erase",
                _ => comObject.GetType().Name
            };
            _events.Add($"Release:{label}");
            comObject = null;
        }
    }

    private sealed class FakeDiscMaster2 : IDiscMaster2
    {
        private readonly string[] _ids;
        public FakeDiscMaster2(params string[] ids) => _ids = ids;
        public object _NewEnum => _ids;
        public string this[int index] => _ids[index];
        public int Count => _ids.Length;
        public bool IsSupportedEnvironment => true;
    }

    private sealed class BlockingInitComRecorder : IDiscRecorder2
    {
        private readonly ManualResetEventSlim? _entered;
        private readonly ManualResetEventSlim? _release;
        private readonly bool _throwOnInit;
        public string Id { get; private set; } = string.Empty;

        public BlockingInitComRecorder(ManualResetEventSlim? entered, ManualResetEventSlim? release, bool throwOnInit)
        {
            _entered = entered;
            _release = release;
            _throwOnInit = throwOnInit;
        }

        public void InitializeDiscRecorder(string recorderUniqueId)
        {
            Id = recorderUniqueId;
            if (_entered != null && _release != null)
            {
                _entered.Set();
                _release.Wait();
            }
            if (_throwOnInit)
            {
                throw new COMException("Device init failure", unchecked((int)0x80004005));
            }
        }

        public void EjectMedia() { }
        public void CloseTray() { }
        public void AcquireExclusiveAccess(bool force, string client) { }
        public void ReleaseExclusiveAccess() { }
        public void DisableMcn() { }
        public void EnableMcn() { }
        public string ActiveDiscRecorder => Id;
        public string VendorId => "FAKE";
        public string ProductId => "DRIVE";
        public string ProductRevision => "1.0";
        public string VolumeName => @"\\?\Volume{0}\";
        public Array VolumePathNames => new object[] { @"Z:\" };
        public bool DeviceCanLoadMedia => true;
        public int LegacyDeviceNumber => 0;
        public Array SupportedFeaturePages => Array.Empty<object>();
        public Array CurrentFeaturePages => Array.Empty<object>();
        public Array SupportedProfiles => Array.Empty<object>();
        public Array CurrentProfiles => Array.Empty<object>();
        public Array SupportedModePages => Array.Empty<object>();
        public string ExclusiveAccessOwner => string.Empty;
    }

    private sealed class DeviceManagerFakeComProvider : IImapiComProvider
    {
        private readonly List<string> _events;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _createRecorderCall;

        public DeviceManagerFakeComProvider(List<string> events, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _events = events;
            _entered = entered;
            _release = release;
        }

        public IDiscMaster2? CreateDiscMaster2() => new FakeDiscMaster2("BAD-ID", "GOOD-ID");

        public IDiscRecorder2? CreateDiscRecorder2()
        {
            int idx = Interlocked.Increment(ref _createRecorderCall);
            return idx == 1
                ? new BlockingInitComRecorder(_entered, _release, throwOnInit: true)
                : new BlockingInitComRecorder(null, null, throwOnInit: false);
        }

        public IDiscFormat2Data? CreateDiscFormat2Data() => null;
        public IDiscFormat2TrackAtOnce? CreateDiscFormat2TrackAtOnce() => null;
        public IDiscFormat2RawCD? CreateDiscFormat2RawCD() => null;
        public IDiscFormat2Erase? CreateDiscFormat2Erase() => null;
        public IRawCDImageCreator? CreateRawCDImageCreator() => null;
        public int? ReadCapacity(string volumePath) => null;
        public int? ReadSessionCount(string volumePath) => null;
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents(string volumePath) => Array.Empty<DiscTocTrack>();
        public byte[]? ReadCdTextPacks(string volumePath) => null;
        public int? TryGetMountedVolumeSectors(string? volumePath) => null;
        public int? ReadFileSystemEndSector(string volumePath) => null;
        public bool TrySpinDown(string? volumePath) => false;

        public void ReleaseComObject<T>(ref T? comObject) where T : class
        {
            if (comObject == null) return;
            string label = comObject switch
            {
                FakeDiscMaster2 => "DiscMaster",
                BlockingInitComRecorder b => $"DiscRecorder:{b.Id}",
                _ => comObject.GetType().Name
            };
            lock (_events)
            {
                _events.Add($"Release:{label}");
            }
            comObject = null;
        }
    }

    #endregion
}
