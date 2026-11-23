using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-end checks on the virtual drive for burn paths that could otherwise waste a disc or erase one:
/// raw-clone disc copy, and the unattended burn queue.
/// </summary>
public sealed class BurnSafetyRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BurnSafety_" + Guid.NewGuid().ToString("N"));

    public BurnSafetyRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task<byte[]> BuildIsoAsync()
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        root.AddFile("PAYLOAD.DAT", payload);
        using var ms = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, ms, new IsoBuildOptions(VolumeLabel: "SAFETY"));
        return ms.ToArray();
    }

    [Fact]
    public async Task DiscCopy_RawCloneMode_BurnsTheDataNotTheRawSectorBytes()
    {
        byte[] iso = await BuildIsoAsync();
        int sectors = iso.Length / OpticalConstants.DataSectorSize;

        using var source = new VirtualDiscRecorder("SRC-01", @"S:\");
        var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < sectors; i++)
        {
            sourceMedium.WriteSector(i, iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray(), OpticalConstants.DataSectorSize);
        }
        sourceMedium.UsedSectors = sectors;
        sourceMedium.IsFinalized = true;
        source.InsertMedia(sourceMedium);

        using var target = new VirtualDiscRecorder("DST-01", @"T:\");
        target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var result = await new DiscCopyService(new VirtualBurnEngine()).CopyDiscAsync(
            source,
            target,
            new DiscCopyOptions { RawCloneMode = true, VerifyEachCopy = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.WasAudioCd);
        for (int i = 0; i < sectors; i++)
        {
            byte[] written = target.CurrentMedium!.ReadSector(i, OpticalConstants.DataSectorSize);
            Assert.True(
                written.AsSpan().SequenceEqual(iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize)),
                $"Sector {i} of the copy differs from the source disc's data.");
        }
    }

    [Fact]
    public async Task BurnQueue_RefusesAudioBinAndCueSheets_WithoutTouchingTheDisc()
    {
        string audioBin = Path.Combine(_dir, "album.bin");
        byte[] raw = new byte[OpticalConstants.RawSectorSize * 400];
        new Random(3).NextBytes(raw);
        await File.WriteAllBytesAsync(audioBin, raw);
        string cue = Path.Combine(_dir, "album.cue");
        await File.WriteAllTextAsync(cue, "FILE \"album.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");

        using var recorder = new VirtualDiscRecorder("Q-01", @"Q:\");
        var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(audioBin), new BurnQueueItem(cue) },
            promptForNextDiscAsync: (_, _, _) => Task.FromResult(true),
            continueOnError: true);

        Assert.False(result.Success);
        Assert.Equal(2, result.FailedItems);
        Assert.Contains("CUE sheet", result.Items[1].ErrorMessage);
        Assert.True(medium.IsBlank, "A refused image must not be written to the disc.");
    }

    [Fact]
    public async Task BurnQueue_NeverReplacesDataOnARewritableDisc()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-02", @"R:\");
        var rewritable = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        byte[] existing = Enumerable.Repeat((byte)0xAB, OpticalConstants.DataSectorSize).ToArray();
        for (int i = 0; i < 64; i++)
        {
            rewritable.WriteSector(i, existing, OpticalConstants.DataSectorSize);
        }
        rewritable.UsedSectors = 64;
        rewritable.IsBlank = false;
        recorder.InsertMedia(rewritable);
        Assert.True(recorder.QueryMediaStatus().HasDataThatBurningReplaces);

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder, new[] { new BurnQueueItem(isoPath) });

        Assert.False(result.Success);
        Assert.Equal(existing, recorder.CurrentMedium!.ReadSector(0, OpticalConstants.DataSectorSize));
    }

    [Fact]
    public async Task BurnQueue_CancelledAtTheDiscSwap_DoesNotShutDown()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-03", @"U:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        int shutdowns = 0;

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(isoPath), new BurnQueueItem(isoPath) },
            promptForNextDiscAsync: (_, _, _) => Task.FromResult(false), // user clicks Cancel at the swap prompt
            postBurnAction: PostBurnAction.ShutDown,
            shutdownInitiator: () => shutdowns++);

        Assert.Equal(1, result.CompletedItems);
        Assert.Equal(BurnQueueItemStatus.Cancelled, result.Items[1].Status);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task BurnQueue_RunToTheEnd_RunsTheAfterBurnActionOnce()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-04", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        int shutdowns = 0;

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(isoPath), new BurnQueueItem(isoPath) },
            promptForNextDiscAsync: (_, _, _) =>
            {
                recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                return Task.FromResult(true);
            },
            postBurnAction: PostBurnAction.ShutDown,
            shutdownInitiator: () => shutdowns++);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, shutdowns);
    }

    private static byte[] EncodeRawMode1(byte[] iso)
    {
        int sectors = iso.Length / OpticalConstants.DataSectorSize;
        byte[] raw = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < sectors; i++)
        {
            RawSectorCodec.Convert2048To2352Mode1(
                iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize),
                i,
                raw.AsSpan(i * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize));
        }
        return raw;
    }

    private static byte[] EncodeRawMode2Xa(byte[] iso, byte submode = 0x08)
    {
        int sectors = iso.Length / OpticalConstants.DataSectorSize;
        byte[] raw = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < sectors; i++)
        {
            RawSectorCodec.Convert2048To2352Mode2Xa(
                iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize),
                i,
                raw.AsSpan(i * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
                submode: submode);
        }
        return raw;
    }

    private sealed class CapturingDeviceService : IDiscDeviceService
    {
        private readonly IDiscRecorderBackend _recorder;
        public CapturingDeviceService(IDiscRecorderBackend recorder) => _recorder = recorder;
        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => new[] { _recorder };
        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            string.Equals(uniqueId, _recorder.UniqueId, StringComparison.OrdinalIgnoreCase) ? _recorder : null;
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }
    }

    private sealed class CapturingRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "CAP-01";
        public string VendorId => "TEST";
        public string ProductId => "CAPTURING DRIVE";
        public string ProductRevision => "1.0";
        public string VolumePath => @"X:\";
        public bool IsVirtual => false;
        public int EjectCount { get; private set; }

        public MediaStatusDto MediaStatus { get; set; } = new()
        {
            MediaPresent = true,
            IsBlank = true,
            MediaType = DiscMediaType.CdR,
            TotalSectors = 350_000,
            FreeSectors = 350_000,
            TotalCapacityBytes = 350_000L * OpticalConstants.DataSectorSize,
            FreeCapacityBytes = 350_000L * OpticalConstants.DataSectorSize
        };

        public DriveCapabilitiesDto GetCapabilities() => new()
        {
            CanWriteCdR = true,
            CanWriteCdRw = true,
            CanWriteDvdR = true,
            SupportedMediaTypes = new[] { DiscMediaType.CdR, DiscMediaType.DvdMinusR },
            SupportedWriteSpeeds = new[] { new WriteSpeedDescriptor(16 * OpticalConstants.CdData1XBytesPerSec, DiscMediaType.CdR) }
        };

        public MediaStatusDto QueryMediaStatus() => MediaStatus;
        public void EjectMedia() => EjectCount++;
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class CapturingBurnEngine : IBurnEngineBackend
    {
        public int DataBurnCalls { get; private set; }
        public byte[]? CapturedDataStream { get; private set; }
        public bool FailVerifyRead { get; set; }
        public bool CorruptVerifyRead { get; set; }
        public bool CancelDuringVerifyRead { get; set; }
        public CancellationTokenSource? CancelOnVerifyCts { get; set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            DataBurnCalls++;
            using var ms = new MemoryStream();
            imageStream.CopyTo(ms);
            CapturedDataStream = ms.ToArray();
            int sectors = OpticalConstants.BytesToSectors(CapturedDataStream.Length, OpticalConstants.DataSectorSize);
            return Task.FromResult(BurnOperationResult.Succeeded(sectors, TimeSpan.FromMilliseconds(10), 16.0));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            if (CancelDuringVerifyRead)
            {
                CancelOnVerifyCts?.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
            if (FailVerifyRead)
            {
                return new RipOperationResult { Success = false, ErrorMessage = "Simulated read error during verification." };
            }

            byte[] data = CapturedDataStream ?? Array.Empty<byte>();
            int sectorSize = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
            for (int i = 0; i < sectorCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] sec = new byte[sectorSize];
                int off = (startLba + i) * sectorSize;
                if (off < data.Length)
                {
                    int copy = Math.Min(sectorSize, data.Length - off);
                    data.AsSpan(off, copy).CopyTo(sec);
                }
                if (CorruptVerifyRead && i == 0)
                {
                    sec[0] ^= 0xFF;
                }
                await sectorConsumer(startLba + i, sec);
            }

            return new RipOperationResult { Success = true, SectorsRead = sectorCount };
        }
    }

    [Fact]
    public async Task ImageStudio_Raw2352Bin_ConvertsTo2048UserData_AndRefusesAudioOrForm2BeforeBurnEngine()
    {
        byte[] iso = await BuildIsoAsync();
        string mode1Bin = Path.Combine(_dir, "mode1.bin");
        await File.WriteAllBytesAsync(mode1Bin, EncodeRawMode1(iso));

        var recorder = new CapturingRecorder();
        var devices = new CapturingDeviceService(recorder);
        var engine = new CapturingBurnEngine();
        var vm = new ImageStudioViewModel(devices, engine) { VerifyAfterBurn = false };

        vm.SourceImagePath = mode1Bin;
        var mode1Result = await vm.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.True(mode1Result.Success, mode1Result.ErrorMessage);
        Assert.Equal(1, engine.DataBurnCalls);
        Assert.Equal(iso, engine.CapturedDataStream);

        // Audio .bin (2352-byte sectors with no ISO 9660 volume) must be refused before BurnDataStreamAsync
        string audioBin = Path.Combine(_dir, "audio.bin");
        byte[] audio = new byte[OpticalConstants.RawSectorSize * 64];
        new Random(7).NextBytes(audio);
        await File.WriteAllBytesAsync(audioBin, audio);

        var engine2 = new CapturingBurnEngine();
        var vm2 = new ImageStudioViewModel(devices, engine2) { VerifyAfterBurn = false };
        vm2.SourceImagePath = audioBin;
        var audioResult = await vm2.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.False(audioResult.Success);
        Assert.Equal(0, engine2.DataBurnCalls);

        // Mode 2 Form 2 .bin (ISO header in sector 16, Form 2 sector with submode 0x20 afterwards) must be refused
        byte[] form2Raw = EncodeRawMode2Xa(iso, submode: 0x08);
        RawSectorCodec.Convert2048To2352Mode2Xa(
            iso.AsSpan(0, OpticalConstants.DataSectorSize),
            20,
            form2Raw.AsSpan(20 * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
            submode: 0x20);
        string form2Bin = Path.Combine(_dir, "vcd_form2.bin");
        await File.WriteAllBytesAsync(form2Bin, form2Raw);

        var engine3 = new CapturingBurnEngine();
        var vm3 = new ImageStudioViewModel(devices, engine3) { VerifyAfterBurn = false };
        vm3.SourceImagePath = form2Bin;
        var form2Result = await vm3.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.False(form2Result.Success);
        Assert.Equal(0, engine3.DataBurnCalls);
    }

    [Theory]
    [InlineData(CueTrackDataType.Mode1_2352)]
    [InlineData(CueTrackDataType.Mode2_2352)]
    public async Task ImageStudio_CuePhysicalFallback_Extracts2048UserData_AndRefusesForm2(CueTrackDataType dataType)
    {
        byte[] iso = await BuildIsoAsync();
        byte[] raw = dataType == CueTrackDataType.Mode2_2352 ? EncodeRawMode2Xa(iso) : EncodeRawMode1(iso);
        string binPath = Path.Combine(_dir, $"cue_{dataType}.bin");
        string cuePath = Path.Combine(_dir, $"cue_{dataType}.cue");
        await File.WriteAllBytesAsync(binPath, raw);
        string modeKeyword = dataType == CueTrackDataType.Mode2_2352 ? "MODE2/2352" : "MODE1/2352";
        await File.WriteAllTextAsync(cuePath, $"FILE \"{Path.GetFileName(binPath)}\" BINARY\n  TRACK 01 {modeKeyword}\n    INDEX 01 00:00:00\n");

        var recorder = new CapturingRecorder();
        var devices = new CapturingDeviceService(recorder);
        var engine = new CapturingBurnEngine();
        var vm = new ImageStudioViewModel(devices, engine) { VerifyAfterBurn = false };
        vm.SourceImagePath = cuePath;

        var result = await vm.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, engine.DataBurnCalls);
        Assert.Equal(iso, engine.CapturedDataStream);

        // Form 2 sector in Mode2_2352 CUE must be refused before BurnDataStreamAsync
        byte[] form2Raw = EncodeRawMode2Xa(iso, submode: 0x20);
        string form2Bin = Path.Combine(_dir, $"cue_form2_{dataType}.bin");
        string form2Cue = Path.Combine(_dir, $"cue_form2_{dataType}.cue");
        await File.WriteAllBytesAsync(form2Bin, form2Raw);
        await File.WriteAllTextAsync(form2Cue, $"FILE \"{Path.GetFileName(form2Bin)}\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

        var engine2 = new CapturingBurnEngine();
        var vm2 = new ImageStudioViewModel(devices, engine2) { VerifyAfterBurn = false };
        vm2.SourceImagePath = form2Cue;
        var form2Res = await vm2.BurnImageToDiscAsync(recorder.UniqueId);
        Assert.False(form2Res.Success);
        Assert.Equal(0, engine2.DataBurnCalls);
    }

    [Fact]
    public async Task CliBurnIso_OnRaw2352Bin_ConvertsTo2048UserData_AndRefusesAudioAndForm2()
    {
        byte[] iso = await BuildIsoAsync();
        string mode1Bin = Path.Combine(_dir, "cli_mode1.bin");
        await File.WriteAllBytesAsync(mode1Bin, EncodeRawMode1(iso));

        var recorder = new CapturingRecorder();
        var devices = new CapturingDeviceService(recorder);
        var engine = new CapturingBurnEngine();

        var out1 = new StringWriter();
        var err1 = new StringWriter();
        int rc1 = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", recorder.UniqueId, "--iso", mode1Bin, "--no-verify", "--windows-writer", "--json" },
            out1, err1, devices, engine);

        Assert.Equal(0, rc1);
        Assert.Equal(1, engine.DataBurnCalls);
        Assert.Equal(iso, engine.CapturedDataStream);

        string audioBin = Path.Combine(_dir, "cli_audio.bin");
        byte[] audio = new byte[OpticalConstants.RawSectorSize * 64];
        new Random(9).NextBytes(audio);
        await File.WriteAllBytesAsync(audioBin, audio);

        var engine2 = new CapturingBurnEngine();
        int rc2 = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", recorder.UniqueId, "--iso", audioBin, "--no-verify", "--windows-writer", "--json" },
            new StringWriter(), new StringWriter(), devices, engine2);
        Assert.NotEqual(0, rc2);
        Assert.Equal(0, engine2.DataBurnCalls);

        byte[] form2Raw = EncodeRawMode2Xa(iso, submode: 0x08);
        RawSectorCodec.Convert2048To2352Mode2Xa(
            iso.AsSpan(0, OpticalConstants.DataSectorSize),
            20,
            form2Raw.AsSpan(20 * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
            submode: 0x20);
        string form2Bin = Path.Combine(_dir, "cli_form2.bin");
        await File.WriteAllBytesAsync(form2Bin, form2Raw);

        var engine3 = new CapturingBurnEngine();
        int rc3 = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", recorder.UniqueId, "--iso", form2Bin, "--no-verify", "--windows-writer", "--json" },
            new StringWriter(), new StringWriter(), devices, engine3);
        Assert.NotEqual(0, rc3);
        Assert.Equal(0, engine3.DataBurnCalls);
    }

    [Fact]
    public async Task DataBurnService_WhenVerificationFails_NeverEjectsMedia()
    {
        byte[] iso = await BuildIsoAsync();
        var recorder = new CapturingRecorder();
        var engine = new CapturingBurnEngine { CorruptVerifyRead = true };
        var service = new DataBurnService(engine);

        var result = await service.BurnStreamAsync(
            recorder,
            new MemoryStream(iso),
            new DataBurnOptions { VerifyAfterBurn = true, EjectAfterBurn = true, EjectWhenDone = true });

        Assert.False(result.Success);
        Assert.Equal(0, recorder.EjectCount);
    }

    [Fact]
    public async Task ImageStudio_WhenVerificationFailsOrIsCancelled_NeverRunsPostBurnActions()
    {
        byte[] iso = await BuildIsoAsync();
        string isoPath = Path.Combine(_dir, "verify_fail.iso");
        await File.WriteAllBytesAsync(isoPath, iso);

        // 1. Verification mismatch: ShutDown and Eject must never run
        var recFail = new CapturingRecorder();
        var engFail = new CapturingBurnEngine { CorruptVerifyRead = true };
        int shutdownsFail = 0;
        var vmFail = new ImageStudioViewModel(new CapturingDeviceService(recFail), engFail, () => shutdownsFail++)
        {
            SourceImagePath = isoPath,
            VerifyAfterBurn = true,
            SelectedPostBurnAction = PostBurnAction.ShutDown
        };
        var failRes = await vmFail.BurnImageToDiscAsync(recFail.UniqueId);
        Assert.False(failRes.Success);
        Assert.Equal(0, shutdownsFail);
        Assert.Equal(0, recFail.EjectCount);

        vmFail.SelectedPostBurnAction = PostBurnAction.EjectAndPlaySound;
        await vmFail.BurnImageToDiscAsync(recFail.UniqueId);
        Assert.Equal(0, recFail.EjectCount);

        // 2. Verification cancelled: ShutDown and Eject must never run
        var recCancel = new CapturingRecorder();
        using var cts = new CancellationTokenSource();
        var engCancel = new CapturingBurnEngine { CancelDuringVerifyRead = true, CancelOnVerifyCts = cts };
        int shutdownsCancel = 0;
        var vmCancel = new ImageStudioViewModel(new CapturingDeviceService(recCancel), engCancel, () => shutdownsCancel++)
        {
            SourceImagePath = isoPath,
            VerifyAfterBurn = true,
            SelectedPostBurnAction = PostBurnAction.ShutDown
        };
        var cancelRes = await vmCancel.BurnImageToDiscAsync(recCancel.UniqueId, cts.Token);
        Assert.Equal(BurnStatus.Cancelled, cancelRes.Status);
        Assert.Equal(0, shutdownsCancel);
        Assert.Equal(0, recCancel.EjectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BurnQueue_WhenAnyItemFails_NeverRunsPostBurnAction(bool continueOnError)
    {
        string isoPath = Path.Combine(_dir, "ok.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());
        string missingPath = Path.Combine(_dir, "missing.iso");

        using var recorder = new VirtualDiscRecorder("Q-FAIL", @"Y:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        int shutdowns = 0;

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(isoPath), new BurnQueueItem(missingPath) },
            promptForNextDiscAsync: (_, _, _) =>
            {
                recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                return Task.FromResult(true);
            },
            postBurnAction: PostBurnAction.ShutDown,
            shutdownInitiator: () => shutdowns++,
            continueOnError: continueOnError);

        Assert.False(result.Success);
        Assert.True(result.FailedItems > 0);
        Assert.Equal(0, shutdowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscCopy_AllowOverwriteDefaultsFalse_RefusesUsedRewritable_AndOverwritesWhenOptedIn(bool rawCloneMode)
    {
        Assert.False(new DiscCopyOptions().AllowOverwrite);

        byte[] iso = await BuildIsoAsync();
        int sectors = iso.Length / OpticalConstants.DataSectorSize;

        using var source = new VirtualDiscRecorder("CP-SRC", @"S:\");
        var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < sectors; i++)
        {
            sourceMedium.WriteSector(i, iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray(), OpticalConstants.DataSectorSize);
        }
        sourceMedium.UsedSectors = sectors;
        sourceMedium.IsFinalized = true;
        source.InsertMedia(sourceMedium);

        using var target = new VirtualDiscRecorder("CP-DST", @"T:\");
        var rewritable = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        byte[] existing = Enumerable.Repeat((byte)0xCD, OpticalConstants.DataSectorSize).ToArray();
        for (int i = 0; i < 64; i++)
        {
            rewritable.WriteSector(i, existing, OpticalConstants.DataSectorSize);
        }
        rewritable.UsedSectors = 64;
        rewritable.IsBlank = false;
        target.InsertMedia(rewritable);

        var copyService = new DiscCopyService(new VirtualBurnEngine());
        var refused = await copyService.CopyDiscAsync(
            source,
            target,
            new DiscCopyOptions { RawCloneMode = rawCloneMode, VerifyEachCopy = false });

        Assert.False(refused.Success);
        Assert.Equal(existing, rewritable.ReadSector(0, OpticalConstants.DataSectorSize));

        var allowed = await copyService.CopyDiscAsync(
            source,
            target,
            new DiscCopyOptions { RawCloneMode = rawCloneMode, VerifyEachCopy = false, AllowOverwrite = true });

        Assert.True(allowed.Success, allowed.ErrorMessage);
        Assert.Equal(iso.AsSpan(0, OpticalConstants.DataSectorSize).ToArray(), rewritable.ReadSector(0, OpticalConstants.DataSectorSize));
    }
}
