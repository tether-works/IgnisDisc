using System.Collections.Concurrent;
using System.IO;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class MultiDriveBurnCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cdburner-multidrive-" + Guid.NewGuid().ToString("N"));
    private readonly string _logDir;

    public MultiDriveBurnCoordinatorTests()
    {
        Directory.CreateDirectory(_tempDir);
        _logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static VirtualDiscRecorder[] CreateThreeVirtualDrives(Func<VirtualDiscMedium> mediumFactory)
    {
        var drives = new[]
        {
            new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL A", @"V:\"),
            new VirtualDiscRecorder("VDRV-02", "IGNIS", "VIRTUAL B", @"W:\"),
            new VirtualDiscRecorder("VDRV-03", "IGNIS", "VIRTUAL C", @"X:\")
        };
        foreach (var drive in drives)
        {
            drive.InsertMedia(mediumFactory());
        }
        return drives;
    }

    private async Task<(string IsoPath, byte[] Payload)> CreateSampleIsoAsync(string fileName = "sample.iso", int payloadBytes = 200_000)
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[payloadBytes];
        new Random(42).NextBytes(payload);
        root.AddFile("payload.bin", payload);

        string isoPath = Path.Combine(_tempDir, fileName);
        await using var fs = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "MULTI_ISO"));
        return (isoPath, payload);
    }

    private AudioCdCompilation CreateSampleAudioCompilation(int trackCount = 2)
    {
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "MultiDrive Album",
            AlbumPerformer = "Test Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        for (int i = 0; i < trackCount; i++)
        {
            byte[] pcm = new byte[(300 + i * 25) * OpticalConstants.RawSectorSize];
            new Random(100 + i).NextBytes(pcm);
            string wavPath = Path.Combine(_tempDir, $"track{i + 1}.wav");
            WavFileCodec.WriteWavFile(wavPath, pcm);
            compilation.AddTrack(new AudioCdTrackItem(i + 1, wavPath, $"Track {i + 1}", "Test Artist"));
        }

        return compilation;
    }

    [Fact]
    public async Task BurnImageAsync_AcrossThreeSimultaneousVirtualMmcDrives_CompletesAndVerifiesAllWithZeroSharedState()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankDvdR47());
        var (isoPath, expectedPayload) = await CreateSampleIsoAsync("multi3.iso", 250_000);
        var mmcInstances = new ConcurrentDictionary<string, VirtualMmcDrive>(StringComparer.OrdinalIgnoreCase);
        var exclusiveHandles = new ConcurrentDictionary<string, TrackingExclusiveHandle>(StringComparer.OrdinalIgnoreCase);
        var progressUpdates = new ConcurrentBag<MultiDriveProgressUpdate>();

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            configureVirtualDrive: _ => { },
            wait: _ => { },
            exclusiveAccessFactory: rec =>
            {
                var handle = new TrackingExclusiveHandle(rec.UniqueId);
                Assert.True(exclusiveHandles.TryAdd(rec.UniqueId, handle));
                return handle;
            });

        foreach (var rec in drives)
        {
            string id = rec.UniqueId;
            rec.ConfigureVirtualDrive = mmc => mmcInstances[id] = mmc;
        }

        var result = await coordinator.BurnImageAsync(
            drives,
            isoPath,
            new DataBurnOptions { CloseDisc = true, VerifyAfterBurn = true },
            new SyncProgress<MultiDriveProgressUpdate>(u => progressUpdates.Add(u)));

        Assert.True(result.AllSucceeded, result.SummaryMessage);
        Assert.Equal(3, result.TotalDrives);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, mmcInstances.Count);
        Assert.Equal(3, mmcInstances.Values.Distinct().Count());

        var logPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var engines = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var telemetries = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var target in result.Results)
        {
            Assert.True(target.Success, target.ErrorMessage);
            Assert.Equal(MultiDriveBurnStatus.Succeeded, target.Status);
            Assert.True(target.ExclusiveAccessAcquired);
            Assert.True(exclusiveHandles[target.DriveId].Disposed);
            Assert.NotNull(target.DataBurnResult);
            Assert.True(target.DataBurnResult!.Verified, $"Drive {target.DriveId} was not verified.");
            Assert.True(target.Telemetry.Count > 0);
            Assert.True(telemetries.Add(target.Telemetry));
            Assert.True(engines.Add(target.EngineUsed!));

            var composite = Assert.IsType<MultiDriveBurnEngineFactory.PerDriveNativeCompositeEngine>(target.EngineUsed);
            Assert.NotNull(composite.DataEngine.LastLogPath);
            Assert.True(File.Exists(composite.DataEngine.LastLogPath));
            Assert.True(logPaths.Add(composite.DataEngine.LastLogPath!));

            var vRec = (VirtualDiscRecorder)target.Recorder;
            Assert.False(vRec.CurrentMedium!.IsBlank);
            Assert.True(vRec.CurrentMedium.IsFinalized);
            Assert.Contains(progressUpdates, u => u.DriveId == target.DriveId && u.Status == MultiDriveBurnStatus.Running);
            Assert.Contains(progressUpdates, u => u.DriveId == target.DriveId && u.Status == MultiDriveBurnStatus.Succeeded);

            // Verify ISO payload directly from virtual medium
            using var reader = IsoImageReader.FromStream(OpenVirtualMediumStream(vRec.CurrentMedium), leaveOpen: false);
            byte[] extracted = reader.ReadFileBytes("/payload.bin");
            Assert.Equal(expectedPayload, extracted);
        }
    }

    [Fact]
    public async Task BurnAudioAsync_AcrossThreeSimultaneousVirtualMmcDrives_CompletesAndVerifiesAll()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankCdR700());
        var compilation = CreateSampleAudioCompilation(trackCount: 2);

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            wait: _ => { });

        var result = await coordinator.BurnAudioAsync(
            drives,
            compilation,
            new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                KeepIndexMarks = true
            });

        Assert.True(result.AllSucceeded, result.SummaryMessage);
        Assert.Equal(3, result.SucceededCount);

        var logPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in result.Results)
        {
            Assert.True(target.Success, target.ErrorMessage);
            Assert.True(target.BurnResult!.TocChecked);
            var composite = Assert.IsType<MultiDriveBurnEngineFactory.PerDriveNativeCompositeEngine>(target.EngineUsed);
            Assert.NotNull(composite.AudioEngine.LastResult);
            Assert.True(composite.AudioEngine.LastResult!.Verification!.Passed);
            Assert.True(logPaths.Add(composite.AudioEngine.LastLogPath!));

            var medium = ((VirtualDiscRecorder)target.Recorder).CurrentMedium!;
            Assert.True(medium.Raw2352Mode);
            Assert.Equal(2, Assert.Single(medium.Sessions).Tracks.Count);
        }
    }

    [Fact]
    public async Task BurnAudioAsync_WithSharedSingleFileAlbumSource_ClonesSourcePerDriveWithoutCollision()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankCdR700());

        byte[] albumPcm = new byte[650 * OpticalConstants.RawSectorSize];
        new Random(77).NextBytes(albumPcm);
        string albumWav = Path.Combine(_tempDir, "album.wav");
        WavFileCodec.WriteWavFile(albumWav, albumPcm);
        string cuePath = Path.Combine(_tempDir, "album.cue");
        File.WriteAllLines(cuePath,
        [
            "TITLE \"Shared Album\"",
            "PERFORMER \"Shared Artist\"",
            "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO",
            "    TITLE \"Part 1\"",
            "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO",
            "    TITLE \"Part 2\"",
            "    INDEX 01 00:04:10"
        ]);

        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(cuePath);

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            wait: _ => { });

        var result = await coordinator.BurnAudioAsync(
            drives,
            compilation,
            new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = true, KeepIndexMarks = true });

        Assert.True(result.AllSucceeded, result.SummaryMessage);
        Assert.Equal(3, result.SucceededCount);
    }

    [Fact]
    public async Task BurnArchiveAsync_AcrossThreeSimultaneousVirtualMmcDrives_CompletesAndVerifiesAll()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankCdR700());
        string sourceDir = Path.Combine(_tempDir, "archive_src");
        string stagingDir = Path.Combine(_tempDir, "archive_stage");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "doc1.txt"), "Archive data 1"u8.ToArray());
        File.WriteAllBytes(Path.Combine(sourceDir, "doc2.bin"), new byte[32_768]);

        var plan = ArchiveSpanPlanner.Plan(sourceDir, DiscMediaType.CdR, redundancyPercent: 0);
        var staged = await ArchiveStager.StageDiscsAsync(plan, stagingDir, par2Path: null);
        Assert.Single(staged);

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            wait: _ => { });

        var result = await coordinator.BurnArchiveAsync(
            drives,
            staged[0].DiscRoot,
            new DataBurnOptions { VerifyAfterBurn = true, CloseDisc = true });

        Assert.True(result.AllSucceeded, result.SummaryMessage);
        Assert.Equal(3, result.SucceededCount);
        foreach (var target in result.Results)
        {
            Assert.True(target.DataBurnResult!.Verified);
        }
    }

    [Fact]
    public async Task BurnImageAsync_WhenDrive2FailsMidBurn_Drives1And3StillCompleteAndVerify()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankDvdR47());
        var (isoPath, _) = await CreateSampleIsoAsync("fault.iso", 250_000);

        // Inject a mid-burn write failure on Drive 2 (VDRV-02) only
        drives[1].ConfigureVirtualDrive = mmc => mmc.WriteErrorAtLba = 16;

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            wait: _ => { });

        var result = await coordinator.BurnImageAsync(
            drives,
            isoPath,
            new DataBurnOptions { CloseDisc = true, VerifyAfterBurn = true });

        Assert.False(result.AllSucceeded);
        Assert.True(result.AnySucceeded);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.CancelledCount);

        var d1 = result.Results.Single(r => r.DriveId == "VDRV-01");
        var d2 = result.Results.Single(r => r.DriveId == "VDRV-02");
        var d3 = result.Results.Single(r => r.DriveId == "VDRV-03");

        Assert.True(d1.Success, d1.ErrorMessage);
        Assert.True(d1.DataBurnResult!.Verified);
        Assert.False(d2.Success);
        Assert.Equal(MultiDriveBurnStatus.Failed, d2.Status);
        Assert.NotNull(d2.ErrorMessage);
        Assert.True(d3.Success, d3.ErrorMessage);
        Assert.True(d3.DataBurnResult!.Verified);
    }

    [Fact]
    public async Task BurnImageAsync_PerDriveCancellation_CancelsOnlyTargetDrive()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankDvdR47());
        var (isoPath, _) = await CreateSampleIsoAsync("cancel.iso", 200_000);

        using var ctsDrive2 = new CancellationTokenSource();
        ctsDrive2.Cancel();

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            new VirtualBurnEngine(),
            useDirectDriveCommands: true,
            logDirectory: _logDir,
            wait: _ => { });

        var perDriveTokens = new Dictionary<string, CancellationToken>
        {
            ["VDRV-02"] = ctsDrive2.Token
        };

        var result = await coordinator.BurnImageAsync(
            drives,
            isoPath,
            new DataBurnOptions { CloseDisc = true, VerifyAfterBurn = true },
            perDriveCancellationTokens: perDriveTokens);

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(MultiDriveBurnStatus.Succeeded, result.Results.Single(r => r.DriveId == "VDRV-01").Status);
        Assert.Equal(MultiDriveBurnStatus.Cancelled, result.Results.Single(r => r.DriveId == "VDRV-02").Status);
        Assert.Equal(MultiDriveBurnStatus.Succeeded, result.Results.Single(r => r.DriveId == "VDRV-03").Status);
    }

    [Fact]
    public async Task BurnImageAsync_DuplicateTargetDrives_IsRejected()
    {
        var drives = CreateThreeVirtualDrives(() => VirtualDiscMedium.CreateBlankCdR700());
        var (isoPath, _) = await CreateSampleIsoAsync("dup.iso", 32_768);
        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(new VirtualBurnEngine());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.BurnImageAsync(new[] { drives[0], drives[0] }, isoPath));
    }

    private static MemoryStream OpenVirtualMediumStream(VirtualDiscMedium medium)
    {
        var ms = new MemoryStream(medium.UsedSectors * OpticalConstants.DataSectorSize);
        byte[] sec = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < medium.UsedSectors; lba++)
        {
            medium.ReadSector(lba, sec, OpticalConstants.DataSectorSize);
            ms.Write(sec, 0, sec.Length);
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class TrackingExclusiveHandle(string driveId) : IDisposable
    {
        public string DriveId { get; } = driveId;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
