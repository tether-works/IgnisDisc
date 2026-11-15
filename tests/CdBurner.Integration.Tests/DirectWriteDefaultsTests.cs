using System.IO;
using System.Runtime.InteropServices;
using CdBurner.App.ViewModels;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Which writer each screen picks when the user has not chosen: direct drive commands for audio CDs (the only way
/// to write CD-Text), falling back to the Windows writer on a drive without Session-at-Once; on a DVD-Video screen
/// direct only while the movies need a dual-layer disc (layer break on a cell boundary).
/// </summary>
public sealed class DirectWriteDefaultsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_DirectDefaults_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public DirectWriteDefaultsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private VirtualDiscRecorder Drive() =>
        _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");

    private AudioBurnViewModel AudioScreen()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Album",
            AlbumPerformer = "Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };
        byte[] pcm = new byte[300 * OpticalConstants.RawSectorSize];
        new Random(7).NextBytes(pcm);
        string wav = Path.Combine(_tempDir, "Track01.wav");
        WavFileCodec.WriteWavFile(wav, pcm);
        vm.AddAudioFilesCommand.Execute(new[] { wav });
        return vm;
    }

    [Fact]
    public void TheApp_StartsTheAudioScreenWithDirectDriveCommands_AndTheOtherScreensWithout()
    {
        using var main = new MainViewModel(new DiscDeviceManager(enablePhysicalEnumeration: false), new VirtualBurnEngine());

        Assert.True(main.AudioBurn.UseDirectDriveCommands);
        Assert.False(main.AudioBurn.TestWriteFirst);
        Assert.False(main.AudioBurn.IncludeRecoverySession);
        Assert.False(main.DvdVideo.UseDirectDriveCommands); // nothing loaded yet: a single-layer default
    }

    [Fact]
    public async Task AudioDefault_OnADriveWithoutSessionAtOnce_BurnsWithTheWindowsWriter_AndSaysSo()
    {
        var vm = AudioScreen();
        vm.UseDirectDriveCommandsByDefault();
        Assert.True(vm.UseDirectDriveCommands);
        Drive().SupportsSessionAtOnce = false;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(vm.LastDirectBurnResult);
        Assert.Contains("does not report Session-at-Once", vm.StatusMessage);
        Assert.Contains("Windows writer burned the disc", vm.StatusMessage);
        Assert.False(Drive().CurrentMedium!.IsBlank);
    }

    [Fact]
    public async Task AudioDefault_OnADriveWithSessionAtOnce_BurnsDirect_WithCdText()
    {
        var vm = AudioScreen();
        vm.UseDirectDriveCommandsByDefault();

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(vm.LastDirectBurnResult);
        Assert.Equal("Album", CdTextPackParser.ParsePacks(Drive().CurrentMedium!.CdTextBytes!).AlbumTitle);
    }

    [Fact]
    public async Task DirectTickedByTheUser_OnADriveWithoutSessionAtOnce_IsStillRefused_WithTheDiscUntouched()
    {
        var vm = AudioScreen();
        vm.UseDirectDriveCommandsByDefault();
        vm.UseDirectDriveCommands = false;
        vm.UseDirectDriveCommands = true; // the user's own tick: they asked for CD-Text, so no silent fallback
        Drive().SupportsSessionAtOnce = false;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.Contains("Session-at-Once", vm.StatusMessage);
        Assert.True(Drive().CurrentMedium!.IsBlank);
    }

    // ---- DVD-Video ----

    private string VideoTs(string name, long movieBytes)
    {
        string root = Path.Combine(_tempDir, name);
        string videoTs = Path.Combine(root, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        byte[] vmg = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vts = DvdVideoValidator.CreateSyntheticVtsIfo();
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO"), vmg);
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTs, "VIDEO_TS.BUP"), (byte[])vmg.Clone());
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.IFO"), vts);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.BUP"), (byte[])vts.Clone());

        // The movie as 1 GB pieces, like a real VIDEO_TS; sparse, so they take no space on the test machine.
        int part = 1;
        for (long left = movieBytes; left > 0; left -= 1_073_739_776, part++)
        {
            SparseFile(Path.Combine(videoTs, $"VTS_01_{part}.VOB"), Math.Min(left, 1_073_739_776));
        }
        return root;
    }

    private static void SparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException($"Could not make {path} sparse (error {Marshal.GetLastWin32Error()}).");
        }
        stream.SetLength(length);
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [Fact]
    public void DvdDefault_TurnsDirectOnForADualLayerDisc_AndOffAgainForASingleLayerOne()
    {
        string big = VideoTs("Big", 5_000_000_000);
        string small = VideoTs("Small", 100_000_000);
        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine);
        Assert.False(vm.UseDirectDriveCommands);

        vm.VideoTsFolderPath = big;
        Assert.True(vm.IsDualLayerRequired);
        Assert.True(vm.UseDirectDriveCommands);

        vm.VideoTsFolderPath = small;
        Assert.False(vm.IsDualLayerRequired);
        Assert.False(vm.UseDirectDriveCommands);
    }

    [Fact]
    public void DvdDefault_NeverOverridesTheUsersOwnChoice()
    {
        string big = VideoTs("Big", 5_000_000_000);
        string small = VideoTs("Small", 100_000_000);

        // Switched off by the user on a dual-layer disc: stays off when the next plan is dual-layer again.
        var off = new DvdVideoViewModel(_deviceManager, _burnEngine) { VideoTsFolderPath = big };
        Assert.True(off.UseDirectDriveCommands);
        off.UseDirectDriveCommands = false;
        off.VideoTsFolderPath = small;
        off.VideoTsFolderPath = big;
        Assert.False(off.UseDirectDriveCommands);

        // Switched on by the user for a single-layer disc: stays on.
        var on = new DvdVideoViewModel(_deviceManager, _burnEngine) { UseDirectDriveCommands = true };
        on.VideoTsFolderPath = big;
        on.VideoTsFolderPath = small;
        Assert.True(on.UseDirectDriveCommands);
    }
}
