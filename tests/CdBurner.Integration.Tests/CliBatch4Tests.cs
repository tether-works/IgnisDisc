using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class CliBatch4Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliBatch4Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_B4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> BuildTestIsoAsync(string name, string fileContent)
    {
        string inputDir = Path.Combine(_tempDir, "in_" + name);
        Directory.CreateDirectory(inputDir);
        File.WriteAllText(Path.Combine(inputDir, $"{name}.txt"), fileContent, Encoding.UTF8);

        string isoPath = Path.Combine(_tempDir, $"{name}.iso");
        var (code, _, err) = await RunCliAsync("build-iso", "--input", inputDir, "--output", isoPath, "--label", name.ToUpperInvariant());
        Assert.True(code == 0, err);
        return isoPath;
    }

    private string CreateStandInPar2Script(byte[] expectedPayload)
    {
        string ps1Path = Path.Combine(_tempDir, "standin-par2.ps1");
        string cmdPath = Path.Combine(_tempDir, "standin-par2.cmd");
        string expectedB64 = Convert.ToBase64String(expectedPayload);

        File.WriteAllText(ps1Path, $$"""
            $ErrorActionPreference = 'Stop'
            $mode = $args[0]
            if ($mode -ne 'repair') {
                Write-Error "Unexpected mode: $mode"
                exit 1
            }
            $root = (Get-Location).Path
            $target = Join-Path $root 'payload.bin'
            if (Test-Path -LiteralPath $target) {
                Move-Item -LiteralPath $target -Destination ($target + '.1') -Force
            }
            $bytes = [System.Convert]::FromBase64String('{{expectedB64}}')
            [System.IO.File]::WriteAllBytes($target, $bytes)
            exit 0
            """);
        File.WriteAllText(cmdPath, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\" %*\r\n");
        return cmdPath;
    }

    private sealed class TestDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public TestDeviceService(params IDiscRecorderBackend[] recorders) => _recorders = recorders;

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => _recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BurnQueue_SequentialIsos_SucceedsAndRejectsCueOrMissingArgs()
    {
        string iso1 = await BuildTestIsoAsync("queue1", "first queue disc payload");
        string iso2 = await BuildTestIsoAsync("queue2", "second queue disc payload");
        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (code, outText, errText) = await RunCliAsync(
            "burn-queue", "--drive", "VDRV-01",
            "--images", $"{iso1},{iso2}",
            "--no-verify", "--post-action", "eject", "--json");
        Assert.True(code == 0, errText);
        Assert.True(_recorder.TrayOpen);

        using (var doc = JsonDocument.Parse(outText))
        {
            var root = doc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(2, root.GetProperty("totalItems").GetInt32());
            Assert.Equal(2, root.GetProperty("completedItems").GetInt32());
            Assert.Equal(0, root.GetProperty("failedItems").GetInt32());
            Assert.Equal("Eject", root.GetProperty("postAction").GetString());
            Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        }

        // CUE sheets cannot be queued.
        string cuePath = Path.Combine(_tempDir, "audio.cue");
        File.WriteAllText(cuePath, "FILE \"track.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n");
        var (cueCode, _, _) = await RunCliAsync("burn-queue", "--drive", "VDRV-01", "--images", cuePath);
        Assert.NotEqual(0, cueCode);

        // Missing required --images option.
        var (badArgsCode, _, _) = await RunCliAsync("burn-queue", "--drive", "VDRV-01");
        Assert.NotEqual(0, badArgsCode);
    }

    [Fact]
    public async Task NoBurnFreeAndPostAction_LogBufeFalse_EjectTray_AndGuardShutdown()
    {
        string isoPath = await BuildTestIsoAsync("bufe", "buffer underrun setting check");
        string logDir = Path.Combine(_tempDir, "native_logs");
        Directory.CreateDirectory(logDir);
        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (code, outText, errText) = await RunCliAsync(
            "burn-iso", "--drive", "VDRV-01", "--iso", isoPath,
            "--no-verify", "--no-burnfree", "--post-action", "eject",
            "--native", "--log-dir", logDir, "--json");
        Assert.True(code == 0, errText);
        Assert.True(_recorder.TrayOpen);

        using (var doc = JsonDocument.Parse(outText))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        }

        string[] logFiles = Directory.GetFiles(logDir, "*.log");
        Assert.NotEmpty(logFiles);
        string logText = File.ReadAllText(logFiles[0]);
        Assert.Contains("BUFE False", logText, StringComparison.Ordinal);

        // Invalid --post-action value.
        var (badPostCode, _, badPostErr) = await RunCliAsync(
            "burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--post-action", "invalid-action");
        Assert.NotEqual(0, badPostCode);
        Assert.Contains("Invalid --post-action", badPostErr, StringComparison.OrdinalIgnoreCase);

        // shutdown without --confirm-shutdown must be rejected cleanly.
        var (shutCode, _, shutErr) = await RunCliAsync(
            "burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--post-action", "shutdown");
        Assert.NotEqual(0, shutCode);
        Assert.Contains("--confirm-shutdown", shutErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BurnData_AppendMultiSession_RequiresOpenSessionAndAppendsSecondSession()
    {
        string dir1 = Path.Combine(_tempDir, "session1");
        string dir2 = Path.Combine(_tempDir, "session2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir1, "s1.txt"), "session one");
        File.WriteAllText(Path.Combine(dir2, "s2.txt"), "session two");

        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        // Cannot append onto a blank disc.
        var (blankAppendCode, _, blankAppendErr) = await RunCliAsync(
            "burn-data", "--drive", "VDRV-01", "--input", dir1, "--append", "--no-verify");
        Assert.NotEqual(0, blankAppendCode);
        Assert.Contains("blank", blankAppendErr, StringComparison.OrdinalIgnoreCase);

        // Session 1 with --no-close leaves disc appendable.
        var (s1Code, _, s1Err) = await RunCliAsync(
            "burn-data", "--drive", "VDRV-01", "--input", dir1, "--no-verify", "--no-close", "--json");
        Assert.True(s1Code == 0, s1Err);
        Assert.Equal(1, _recorder.QueryMediaStatus().SessionCount);

        // Session 2 with --append closes disc afterwards (no --no-close).
        var (s2Code, s2Out, s2Err) = await RunCliAsync(
            "burn-data", "--drive", "VDRV-01", "--input", dir2, "--append", "--no-verify", "--json");
        Assert.True(s2Code == 0, s2Err);
        Assert.Equal(2, _recorder.QueryMediaStatus().SessionCount);
        using (var doc = JsonDocument.Parse(s2Out))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        }

        // Session 3 cannot append after session 2 closed the disc.
        var (closedAppendCode, _, closedAppendErr) = await RunCliAsync(
            "burn-data", "--drive", "VDRV-01", "--input", dir2, "--append", "--no-verify");
        Assert.NotEqual(0, closedAppendCode);
        Assert.Contains("closed", closedAppendErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BookType_GetsAndSetsDvdPlusR_AndRejectsCdOrInvalidType()
    {
        _recorder.InsertMedia(new VirtualDiscMedium(DiscMediaType.DvdPlusR, OpticalConstants.DvdPlusR5Sectors, isBlank: true));

        var (getCode, getOut, getErr) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--json");
        Assert.True(getCode == 0, getErr);
        using (var doc = JsonDocument.Parse(getOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("bitsettingSupported").GetBoolean());
            Assert.Equal("DvdPlusR", doc.RootElement.GetProperty("bookType").GetString());
        }

        var (setCode, setOut, setErr) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--set", "dvd-rom", "--json");
        Assert.True(setCode == 0, setErr);
        using (var doc = JsonDocument.Parse(setOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("DvdPlusR", doc.RootElement.GetProperty("previousBookType").GetString());
            Assert.Equal("DvdRom", doc.RootElement.GetProperty("bookType").GetString());
            Assert.Equal("0x00", doc.RootElement.GetProperty("bookTypeCode").GetString());
        }

        // Invalid book type keyword.
        var (badTypeCode, _, _) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--set", "not-a-booktype");
        Assert.NotEqual(0, badTypeCode);

        // Bitsetting on CD-R fails cleanly.
        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var (cdCode, cdOut, _) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--set", "dvd-rom", "--json");
        Assert.Equal(1, cdCode);
        using (var doc = JsonDocument.Parse(cdOut))
        {
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("bitsettingSupported").GetBoolean());
        }
    }

    [Fact]
    public async Task RepairPar2_RepairsCorruptedPayload_ManagesBackups_AndHandlesErrors()
    {
        byte[] cleanBytes = Encoding.UTF8.GetBytes("Expected clean archival payload bytes 0123456789ABCDEF.");
        byte[] damagedBytes = Encoding.UTF8.GetBytes("Damaged! clean archival payload bytes 0123456789ABCDEF.");
        string standInCmd = CreateStandInPar2Script(cleanBytes);

        // Case 1: repair keeps .1 backup by default.
        string dirKeep = Path.Combine(_tempDir, "par2_keep");
        Directory.CreateDirectory(Path.Combine(dirKeep, ArchiveManifest.RecoveryFolderName));
        File.WriteAllBytes(Path.Combine(dirKeep, ArchiveManifest.RecoveryFolderName, "recovery.par2"), new byte[64]);
        string payloadKeep = Path.Combine(dirKeep, "payload.bin");
        File.WriteAllBytes(payloadKeep, damagedBytes);

        var (keepCode, keepOut, keepErr) = await RunCliAsync(
            "repair-par2", "--path", dirKeep, "--par2", standInCmd, "--json");
        Assert.True(keepCode == 0, keepErr);
        Assert.Equal(cleanBytes, File.ReadAllBytes(payloadKeep));
        Assert.True(File.Exists(payloadKeep + ".1"));

        using (var doc = JsonDocument.Parse(keepOut))
        {
            var root = doc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Single(root.GetProperty("backupFiles").EnumerateArray());
            Assert.Empty(root.GetProperty("deletedBackups").EnumerateArray());
        }

        // Case 2: --delete-backups removes the .1 backup after repair.
        string dirDel = Path.Combine(_tempDir, "par2_delete");
        Directory.CreateDirectory(Path.Combine(dirDel, ArchiveManifest.RecoveryFolderName));
        File.WriteAllBytes(Path.Combine(dirDel, ArchiveManifest.RecoveryFolderName, "recovery.par2"), new byte[64]);
        string payloadDel = Path.Combine(dirDel, "payload.bin");
        File.WriteAllBytes(payloadDel, damagedBytes);

        var (delCode, delOut, delErr) = await RunCliAsync(
            "repair-par2", "--path", dirDel, "--par2", standInCmd, "--delete-backups", "--json");
        Assert.True(delCode == 0, delErr);
        Assert.Equal(cleanBytes, File.ReadAllBytes(payloadDel));
        Assert.False(File.Exists(payloadDel + ".1"));

        using (var doc = JsonDocument.Parse(delOut))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Single(root.GetProperty("deletedBackups").EnumerateArray());
        }

        // Missing directory fails cleanly.
        var (missingDirCode, _, _) = await RunCliAsync(
            "repair-par2", "--path", Path.Combine(_tempDir, "nonexistent"), "--par2", standInCmd);
        Assert.NotEqual(0, missingDirCode);

        // Missing --path fails cleanly.
        var (missingArgCode, _, _) = await RunCliAsync("repair-par2");
        Assert.NotEqual(0, missingArgCode);
    }
}
