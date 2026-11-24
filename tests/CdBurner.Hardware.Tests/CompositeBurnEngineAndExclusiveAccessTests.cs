using System.ComponentModel;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// <see cref="CompositeBurnEngine"/> sends every call to the simulator for a virtual drive and to the Windows engine for
/// a real one; both are replaced by recording stand-ins here, so no drive is touched. Also covers the argument checks of
/// <see cref="CdromExclusiveAccess"/>, which is given an ordinary file handle (never a drive).
/// </summary>
public sealed class CompositeBurnEngineAndExclusiveAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-composite-" + Guid.NewGuid().ToString("N"));

    public CompositeBurnEngineAndExclusiveAccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class RecordingEngine : IBurnEngineBackend, ISecureAudioReader
    {
        public RecordingEngine(string name) => Name = name;

        public string Name { get; }
        public List<string> Calls { get; } = new();
        public bool C2Supported { get; init; }
        public IDiscRecorderBackend? LastRecorder { get; private set; }
        public bool? LastStrict { get; private set; }
        public SectorRange? LastWrittenRange { get; private set; }
        public bool? LastRaw { get; private set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            Record("data", recorder);
            return Task.FromResult(BurnOperationResult.Succeeded(11, TimeSpan.Zero));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            Record("audio", recorder);
            return Task.FromResult(BurnOperationResult.Succeeded(22, TimeSpan.Zero));
        }

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            Record(fullErase ? "erase-full" : "erase-quick", recorder);
            return Task.FromResult(BurnOperationResult.Succeeded(33, TimeSpan.Zero));
        }

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            Record("read", recorder);
            LastRaw = raw2352Mode;
            return Task.FromResult(RipOperationResult.Succeeded(sectorCount, TimeSpan.Zero));
        }

        public Task<RipOperationResult> ReadSectorsForVerifyAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken, bool strict = false, SectorRange? writtenRange = null)
        {
            Record("verify-read", recorder);
            LastStrict = strict;
            LastWrittenRange = writtenRange;
            return Task.FromResult(RipOperationResult.Succeeded(sectorCount, TimeSpan.Zero));
        }

        public ISectorReadSession? OpenReadSession(IDiscRecorderBackend recorder, bool raw2352Mode, bool readFromDisc = false, bool strict = false, SectorRange? writtenRange = null)
        {
            Record("open-session", recorder);
            return null;
        }

        public bool SupportsC2ErrorPointers(IDiscRecorderBackend recorder, int probeLba)
        {
            Record("c2-probe", recorder);
            return C2Supported;
        }

        public Task<RipOperationResult> ReadAudioSectorsWithC2Async(IDiscRecorderBackend recorder, int startLba, int sectorCount, Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer, CancellationToken cancellationToken)
        {
            Record("c2-read", recorder);
            return Task.FromResult(RipOperationResult.Succeeded(sectorCount, TimeSpan.Zero));
        }

        public Task FlushReadCacheAsync(IDiscRecorderBackend recorder, int nearLba, int audioEndLba, CancellationToken cancellationToken)
        {
            Record("flush", recorder);
            return Task.CompletedTask;
        }

        private void Record(string call, IDiscRecorderBackend recorder)
        {
            Calls.Add(call);
            LastRecorder = recorder;
        }
    }

    /// <summary>An engine that is not a secure audio reader, like the plain Windows engine.</summary>
    private sealed class PlainEngine : IBurnEngineBackend
    {
        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A recorder that reports itself as a real drive; none of its members is ever used by the engine under test.</summary>
    private sealed class PhysicalStandInRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "PHYS-STAND-IN";
        public string VendorId => "TEST";
        public string ProductId => "STAND-IN";
        public string ProductRevision => "0";
        public string VolumePath => @"X:\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => throw new NotSupportedException();
        public MediaStatusDto QueryMediaStatus() => throw new NotSupportedException();
        public void EjectMedia() => throw new NotSupportedException();
        public void CloseTray() => throw new NotSupportedException();
        public bool AcquireExclusiveAccess(string clientName) => throw new NotSupportedException();
        public void ReleaseExclusiveAccess() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static readonly Func<int, ReadOnlyMemory<byte>, ValueTask> NoSink = (_, _) => ValueTask.CompletedTask;

    private static async Task<(RecordingEngine Virtual, RecordingEngine Physical, CompositeBurnEngine Composite)> RunAllCalls(IDiscRecorderBackend recorder, bool c2 = false)
    {
        var virt = new RecordingEngine("virtual") { C2Supported = c2 };
        var phys = new RecordingEngine("physical") { C2Supported = c2 };
        var composite = new CompositeBurnEngine(virt, phys);
        var progress = (IProgress<BurnProgressSnapshot>?)null;

        await composite.BurnDataStreamAsync(recorder, new MemoryStream(), new DataBurnOptions(), progress, default);
        await composite.BurnAudioTracksAsync(recorder, Array.Empty<AudioTrackSource>(), new AudioBurnOptions(), progress, default);
        await composite.EraseMediaAsync(recorder, true, progress, default);
        await composite.EraseMediaAsync(recorder, false, progress, default);
        await composite.ReadSectorsAsync(recorder, 0, 4, true, NoSink, progress, default);
        await composite.ReadSectorsForVerifyAsync(recorder, 8, 4, NoSink, progress, default, strict: true, writtenRange: new SectorRange(8, 100));
        composite.OpenReadSession(recorder, raw2352Mode: false);
        composite.SupportsC2ErrorPointers(recorder, 16);
        await composite.ReadAudioSectorsWithC2Async(recorder, 0, 4, (_, _, _) => ValueTask.CompletedTask, default);
        await composite.FlushReadCacheAsync(recorder, 10, 20, default);
        return (virt, phys, composite);
    }

    private static readonly string[] AllCalls =
    {
        "data", "audio", "erase-full", "erase-quick", "read", "verify-read", "open-session", "c2-probe", "c2-read", "flush"
    };

    [Fact]
    public async Task VirtualDrive_SendsEveryCallToTheVirtualEngineOnly()
    {
        using var recorder = new VirtualDiscRecorder();
        Assert.True(recorder.IsVirtual);

        var (virt, phys, _) = await RunAllCalls(recorder);

        Assert.Equal(AllCalls, virt.Calls);
        Assert.Empty(phys.Calls);
        Assert.Same(recorder, virt.LastRecorder);
    }

    [Fact]
    public async Task RealDrive_SendsEveryCallToThePhysicalEngineOnly()
    {
        var recorder = new PhysicalStandInRecorder();

        var (virt, phys, _) = await RunAllCalls(recorder);

        Assert.Equal(AllCalls, phys.Calls);
        Assert.Empty(virt.Calls);
        Assert.Same(recorder, phys.LastRecorder);
    }

    [Fact]
    public async Task VerifyRead_PassesStrictAndWrittenRangeThrough()
    {
        var (_, phys, _) = await RunAllCalls(new PhysicalStandInRecorder());

        Assert.True(phys.LastStrict);
        Assert.Equal(new SectorRange(8, 100), phys.LastWrittenRange);
    }

    [Fact]
    public async Task ResultsComeBackFromTheChosenEngine()
    {
        var virt = new RecordingEngine("virtual");
        var phys = new RecordingEngine("physical");
        var composite = new CompositeBurnEngine(virt, phys);
        using var virtualRecorder = new VirtualDiscRecorder();

        var fromVirtual = await composite.BurnDataStreamAsync(virtualRecorder, new MemoryStream(), new DataBurnOptions(), null, default);
        var fromPhysical = await composite.BurnDataStreamAsync(new PhysicalStandInRecorder(), new MemoryStream(), new DataBurnOptions(), null, default);

        Assert.Equal(11, fromVirtual.SectorsWritten);
        Assert.Equal(11, fromPhysical.SectorsWritten);
        Assert.Single(virt.Calls);
        Assert.Single(phys.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C2Support_IsWhatTheChosenEngineReports(bool supported)
    {
        var phys = new RecordingEngine("physical") { C2Supported = supported };
        var composite = new CompositeBurnEngine(new RecordingEngine("virtual"), phys);

        Assert.Equal(supported, composite.SupportsC2ErrorPointers(new PhysicalStandInRecorder(), 16));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EngineWithoutSecureAudioReading_ReportsNoC2_FailsC2Reads_AndIgnoresCacheFlush()
    {
        var composite = new CompositeBurnEngine(new RecordingEngine("virtual"), new PlainEngine());
        var recorder = new PhysicalStandInRecorder();

        Assert.False(composite.SupportsC2ErrorPointers(recorder, 16));

        var read = await composite.ReadAudioSectorsWithC2Async(recorder, 0, 4, (_, _, _) => ValueTask.CompletedTask, default);
        Assert.False(read.Success);
        Assert.Contains("C2", read.ErrorMessage);

        await composite.FlushReadCacheAsync(recorder, 10, 20, default); // must complete without error
    }

    [Fact]
    public void Constructor_UsesTheGivenEngines_AndDefaultsToTheRealOnes()
    {
        var virt = new RecordingEngine("virtual");
        var phys = new RecordingEngine("physical");
        var composite = new CompositeBurnEngine(virt, phys);
        Assert.Same(virt, composite.VirtualEngine);
        Assert.Same(phys, composite.PhysicalEngine);

        var defaults = new CompositeBurnEngine();
        Assert.IsType<VirtualBurnEngine>(defaults.VirtualEngine);
        Assert.IsType<Imapi2BurnEngine>(defaults.PhysicalEngine);
    }

    [Fact]
    public async Task NullRecorder_IsRejectedByEveryCall()
    {
        var composite = new CompositeBurnEngine(new RecordingEngine("virtual"), new RecordingEngine("physical"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.BurnDataStreamAsync(null!, new MemoryStream(), new DataBurnOptions(), null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.BurnAudioTracksAsync(null!, Array.Empty<AudioTrackSource>(), new AudioBurnOptions(), null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.EraseMediaAsync(null!, false, null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.ReadSectorsAsync(null!, 0, 1, false, NoSink, null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.ReadSectorsForVerifyAsync(null!, 0, 1, NoSink, null, default));
        Assert.Throws<ArgumentNullException>(() => composite.OpenReadSession(null!, false));
        Assert.Throws<ArgumentNullException>(() => composite.SupportsC2ErrorPointers(null!, 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.ReadAudioSectorsWithC2Async(null!, 0, 1, (_, _, _) => ValueTask.CompletedTask, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.FlushReadCacheAsync(null!, 0, 1, default));
    }

    // ---- CdromExclusiveAccess ----------------------------------------------------------------------------------

    [Fact]
    public void ExclusiveAccess_IoctlCodeIsCtlCodeOfNtddcdrm()
    {
        // CTL_CODE(IOCTL_CDROM_BASE = 0x02, function 0x17, METHOD_BUFFERED = 0, FILE_READ_ACCESS | FILE_WRITE_ACCESS = 3)
        uint expected = (0x02u << 16) | (3u << 14) | (0x17u << 2) | 0u;
        Assert.Equal(0x0002C05Cu, expected);
        Assert.True(expected == CdromExclusiveAccess.IoctlCdromExclusiveAccess);
    }

    [Fact]
    public void ExclusiveAccess_RejectsNullHandle()
    {
        Assert.Throws<ArgumentNullException>(() => CdromExclusiveAccess.Acquire(null!, "IgnisDisc"));
    }

    [Fact]
    public void ExclusiveAccess_RejectsNullCallerName()
    {
        using var file = File.Create(Path.Combine(_dir, "not-a-drive.bin"));
        var ex = Assert.Throws<ArgumentNullException>(() => CdromExclusiveAccess.Acquire(file.SafeFileHandle, null!));
        Assert.Equal("callerName", ex.ParamName);
    }

    [Fact]
    public void ExclusiveAccess_OnAnOrdinaryFile_FailsWithAWindowsError()
    {
        using var file = File.Create(Path.Combine(_dir, "not-a-drive.bin"));
        var ex = Assert.Throws<Win32Exception>(() => CdromExclusiveAccess.Acquire(file.SafeFileHandle, "IgnisDisc test"));
        Assert.NotEqual(0, ex.NativeErrorCode);
        Assert.Contains("exclusive access", ex.Message);
    }
}
