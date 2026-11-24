using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Subtitles;

namespace CdBurner.Core.Tests;

/// <summary>
/// Process-tree termination, timeout enforcement, partial output cleanup, and installer rollback behavior
/// across external tool invocations.
/// </summary>
public sealed class ExternalToolCleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_toolcleanup_" + Guid.NewGuid().ToString("N"));

    public ExternalToolCleanupTests()
    {
        Directory.CreateDirectory(_tempDir);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitForFlagAsync(string flagPath, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(flagPath))
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Timed out waiting for marker file '{flagPath}'.");
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task DvdStillMenu_CancelledWhileFfmpegRunning_KillsProcessAndDeletesTempWorkDir()
    {
        string startedFlag = Path.Combine(_tempDir, "menu_started.flag");
        string workDirLog = Path.Combine(_tempDir, "menu_workdir.txt");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_menu.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            $"echo %LASTARG%> \"{workDirLog}\"",
            $"echo started> \"{startedFlag}\"",
            "(ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "exit /b 0"));

        using var cts = new CancellationTokenSource();
        var titles = new[] { new DvdMenuTitle("Title One", null, false) };

        Task<DvdStillMenuResult?> buildTask = DvdStillMenu.BuildAsync(
            "Menu",
            titles,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            fakeFfmpeg,
            _ => new byte[8],
            cts.Token);

        await WaitForFlagAsync(startedFlag);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask);

        string vobPath = File.ReadAllText(workDirLog).Trim();
        string workDir = Path.GetDirectoryName(vobPath)!;
        Assert.False(Directory.Exists(workDir), $"Temp menu work directory leaked: {workDir}");
    }

    [Fact]
    public async Task DvdVideoAuthoring_CancelledDuringStillExtract_KillsFfmpegAndDeletesStillsDir()
    {
        string movie = Path.Combine(_tempDir, "Movie.mp4");
        File.WriteAllBytes(movie, new byte[4096]);
        string outputDir = Path.Combine(_tempDir, "dvd_out_still");
        string startedFlag = Path.Combine(_tempDir, "still_started.flag");
        string stillPathLog = Path.Combine(_tempDir, "still_path.txt");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_still.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_STILL=0\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-frames:v\" set \"IS_STILL=1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_STILL%\"==\"1\" (",
            $"  echo %LASTARG%> \"{stillPathLog}\"",
            $"  echo started> \"{startedFlag}\"",
            "  (ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "  exit /b 0",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        using var cts = new CancellationTokenSource();
        var service = new DvdVideoAuthoringService();
        var options = new DvdAuthoringOptions
        {
            AutoCreatePlayMenu = true,
            UseFfmpegWhenAvailable = true,
            CustomFfmpegPath = fakeFfmpeg
        };

        Task<DvdAuthoringResult> authorTask = service.AuthorAsync(movie, outputDir, options, cancellationToken: cts.Token);

        await WaitForFlagAsync(startedFlag);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorTask);

        string stillPng = File.ReadAllText(stillPathLog).Trim();
        string stillsDir = Path.GetDirectoryName(stillPng)!;
        Assert.False(Directory.Exists(stillsDir), $"Temp stills directory leaked: {stillsDir}");
    }

    [Fact]
    public async Task DvdVideoAuthoring_CancelledDuringTranscode_KillsFfmpegCleansTempVobAndSubtitlesWithoutWatchdogRace()
    {
        string movie = Path.Combine(_tempDir, "Film.mp4");
        File.WriteAllBytes(movie, new byte[4096]);
        string srt = Path.Combine(_tempDir, "Film.srt");
        File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:03,000\r\nHello world\r\n\r\n");

        string outputDir = Path.Combine(_tempDir, "dvd_out_transcode");
        string startedFlag = Path.Combine(_tempDir, "transcode_started.flag");
        string pathsLog = Path.Combine(_tempDir, "transcode_paths.txt");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_transcode.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_TRANSCODE=0\"",
            "set \"SUBIDX=\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-target\" set \"IS_TRANSCODE=1\"",
            "if /i \"%~x1\"==\".idx\" set \"SUBIDX=%~1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_TRANSCODE%\"==\"1\" (",
            $"  > \"{pathsLog}\" (",
            "    echo(%LASTARG%",
            "    echo(%SUBIDX%",
            "  )",
            $"  echo started> \"{startedFlag}\"",
            "  (ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "  exit /b 0",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        DvdVideoAuthoringService.FfmpegNoOutputTimeoutOverride.Value = TimeSpan.FromMilliseconds(600);
        try
        {
            using var cts = new CancellationTokenSource();
            var service = new DvdVideoAuthoringService();
            var options = new DvdAuthoringOptions
            {
                AutoCreatePlayMenu = false,
                UseFfmpegWhenAvailable = true,
                CustomFfmpegPath = fakeFfmpeg
            };

            Task<DvdAuthoringResult> authorTask = service.AuthorAsync(movie, outputDir, options, cancellationToken: cts.Token);

            await WaitForFlagAsync(startedFlag);
            cts.Cancel();

            Exception ex = await Record.ExceptionAsync(() => authorTask);
            Assert.IsAssignableFrom<OperationCanceledException>(ex);

            string[] logged = File.ReadAllLines(pathsLog);
            string tempVob = logged[0].Trim();
            string subIdx = logged[1].Trim();
            string subDir = Path.GetDirectoryName(subIdx)!;

            Assert.False(File.Exists(tempVob), $"Temp VOB leaked: {tempVob}");
            Assert.False(Directory.Exists(subDir), $"Temp subtitle dir leaked: {subDir}");
        }
        finally
        {
            DvdVideoAuthoringService.FfmpegNoOutputTimeoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task DvdVideoAuthoring_WatchdogStall_KillsFfmpegAndCleansTempVobAndSubtitles()
    {
        string movie = Path.Combine(_tempDir, "StallFilm.mp4");
        File.WriteAllBytes(movie, new byte[4096]);
        string srt = Path.Combine(_tempDir, "StallFilm.srt");
        File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:03,000\r\nHello world\r\n\r\n");

        string outputDir = Path.Combine(_tempDir, "dvd_out_stall");
        string pathsLog = Path.Combine(_tempDir, "stall_paths.txt");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_stall.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_TRANSCODE=0\"",
            "set \"SUBIDX=\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-target\" set \"IS_TRANSCODE=1\"",
            "if /i \"%~x1\"==\".idx\" set \"SUBIDX=%~1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_TRANSCODE%\"==\"1\" (",
            $"  >> \"{pathsLog}\" echo(%LASTARG%^|%SUBIDX%",
            "  (ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "  exit /b 0",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        DvdVideoAuthoringService.FfmpegNoOutputTimeoutOverride.Value = TimeSpan.FromMilliseconds(150);
        try
        {
            var service = new DvdVideoAuthoringService();
            var options = new DvdAuthoringOptions
            {
                AutoCreatePlayMenu = false,
                UseFfmpegWhenAvailable = true,
                CustomFfmpegPath = fakeFfmpeg
            };

            DvdAuthoringResult result = await service.AuthorAsync(movie, outputDir, options);

            Assert.False(result.Success);
            Assert.Contains(result.LogNotes, n => n.Contains("printed nothing", StringComparison.OrdinalIgnoreCase));

            foreach (string line in File.ReadAllLines(pathsLog))
            {
                string[] parts = line.Split('|');
                Assert.False(File.Exists(parts[0]), $"Temp VOB leaked: {parts[0]}");
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    Assert.False(Directory.Exists(Path.GetDirectoryName(parts[1])!), $"Temp subtitle dir leaked: {parts[1]}");
                }
            }
        }
        finally
        {
            DvdVideoAuthoringService.FfmpegNoOutputTimeoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task FfmpegAudioDecoder_Cancelled_KillsProcessTree()
    {
        string input = Path.Combine(_tempDir, "track.wav");
        File.WriteAllBytes(input, new byte[64]);
        string startedFlag = Path.Combine(_tempDir, "audio_cancel_started.flag");
        string lockFile = Path.Combine(_tempDir, "audio_cancel.lock");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_audio_cancel.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            $"echo started> \"{startedFlag}\"",
            $"(ping -n 30 127.0.0.1 >nul) > \"{lockFile}\""));

        using var cts = new CancellationTokenSource();
        Task<byte[]> decodeTask = Task.Run(() => FfmpegAudioDecoder.DecodeToRedBookPcm(input, fakeFfmpeg, cts.Token));

        await WaitForFlagAsync(startedFlag);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decodeTask);
        // If the process tree were still alive, deleting the locked file would fail with IOException.
        File.Delete(lockFile);
    }

    [Fact]
    public async Task FfmpegAudioDecoder_TimeoutAndStreamError_KillProcessTree()
    {
        string input = Path.Combine(_tempDir, "track2.wav");
        File.WriteAllBytes(input, new byte[64]);
        string lockFile = Path.Combine(_tempDir, "audio_timeout.lock");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_audio_timeout.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            $"(ping -n 30 127.0.0.1 >nul) > \"{lockFile}\""));

        FfmpegAudioDecoder.ProcessTimeoutOverride.Value = TimeSpan.FromMilliseconds(120);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                Task.Run(() => FfmpegAudioDecoder.DecodeToRedBookPcm(input, fakeFfmpeg, CancellationToken.None)));
            File.Delete(lockFile);

            FfmpegAudioDecoder.ProcessTimeoutOverride.Value = TimeSpan.FromSeconds(15);
            FfmpegAudioDecoder.CopyStdoutOverride.Value = static (_, _) => throw new OutOfMemoryException("simulated OOM");
            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                Task.Run(() => FfmpegAudioDecoder.DecodeToRedBookPcm(input, fakeFfmpeg, CancellationToken.None)));
            File.Delete(lockFile);
        }
        finally
        {
            FfmpegAudioDecoder.ProcessTimeoutOverride.Value = null;
            FfmpegAudioDecoder.CopyStdoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task SubtitleGeneration_MoveFailure_DeletesTempSrtFile()
    {
        string movie = Path.Combine(_tempDir, "Film.mp4");
        File.WriteAllBytes(movie, new byte[1024]);
        string cacheRoot = Path.Combine(_tempDir, "sub_cache");
        string tools = Path.Combine(_tempDir, "sub_tools");
        Directory.CreateDirectory(tools);
        string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        string cli = Path.Combine(tools, "whisper-cli.exe");
        string model = Path.Combine(tools, "ggml-base.bin");
        File.WriteAllText(ffmpeg, "fake");
        File.WriteAllText(cli, "fake");
        File.WriteAllText(model, "fake");

        var whisper = new WhisperInstallation(cli, model, WhisperBuildKind.CpuX64);
        var options = new SubtitleGenerationOptions
        {
            Language = "en",
            FfmpegPath = ffmpeg,
            Whisper = whisper,
            CacheRoot = cacheRoot,
            Threads = 2
        };

        string key = SubtitleGenerationService.ComputeCacheKey(new FileInfo(movie), whisper.ModelName, "en", "-mc 0");
        string entryDir = Path.Combine(cacheRoot, key);
        Directory.CreateDirectory(entryDir);
        string srtPath = Path.Combine(entryDir, "Film.srt");
        // Creating a directory at srtPath makes File.Move(tempSrt, srtPath, overwrite: true) fail.
        Directory.CreateDirectory(srtPath);

        var runner = new StubSubtitleRunner(ffmpeg, cli);
        SubtitleGenerationResult result = await new SubtitleGenerationService(runner).GenerateAsync(movie, options);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(cacheRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WhisperToolInstaller_StagingMoveFails_PreservesExistingBinDirectory()
    {
        byte[] cudaZip = CreateZip(("Release/whisper-cli.exe", "cuda cli"), ("Release/ggml-cuda.dll", "cuda dll"));
        byte[] cpuZip = CreateZip(("Release/whisper-cli.exe", "cpu cli"));
        byte[] model = Encoding.ASCII.GetBytes("weights");
        byte[] vad = Encoding.ASCII.GetBytes("vad weights");
        string root = Path.Combine(_tempDir, "whisper_root");

        WhisperInstallPlan MakePlan(WhisperBuildKind build, byte[] zip) => new(
            build,
            new WhisperDownload("prog.zip", "https://example.test/prog.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, zip.Length),
            new WhisperDownload("ggml-base.bin", "https://example.test/ggml-base.bin", Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, model.Length),
            "test")
        {
            Vad = new WhisperDownload("vad.bin", "https://example.test/vad.bin", Convert.ToHexStringLower(SHA256.HashData(vad)), vad.Length, vad.Length)
        };

        using (var c1 = new HttpClient(new StubZipHandler(("vad.bin", vad), ("prog.zip", cudaZip), ("ggml-base.bin", model))))
        {
            WhisperInstallResult initial = await WhisperToolInstaller.InstallAsync(MakePlan(WhisperBuildKind.CudaX64, cudaZip), root, c1);
            Assert.True(initial.Success, initial.ErrorMessage);
        }

        WhisperToolInstaller.MoveDirectoryOverride.Value = static (src, dst) =>
        {
            if (src.EndsWith(".new", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated staging move failure.");
            }
            Directory.Move(src, dst);
        };
        try
        {
            using var c2 = new HttpClient(new StubZipHandler(("vad.bin", vad), ("prog.zip", cpuZip), ("ggml-base.bin", model)));
            WhisperInstallResult upgrade = await WhisperToolInstaller.InstallAsync(MakePlan(WhisperBuildKind.CpuX64, cpuZip), root, c2);

            Assert.False(upgrade.Success);
            string binDir = WhisperLocator.GetBinDirectory(root);
            Assert.True(Directory.Exists(binDir));
            Assert.Equal("cuda cli", File.ReadAllText(Path.Combine(binDir, "whisper-cli.exe")));
            Assert.True(File.Exists(Path.Combine(binDir, "ggml-cuda.dll")));
            Assert.Equal(WhisperBuildKind.CudaX64, WhisperLocator.ReadBuildMarker(root));
            Assert.DoesNotContain(Directory.GetDirectories(root), d => Path.GetFileName(d)!.StartsWith("bin.", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            WhisperToolInstaller.MoveDirectoryOverride.Value = null;
        }
    }

    [Fact]
    public async Task Par2Runner_CreateTimeout_KillsProcessAndCleansPartialPar2Files()
    {
        string discRoot = Path.Combine(_tempDir, "par2_create_disc");
        string scratch = Path.Combine(_tempDir, "par2_create_scratch");
        Directory.CreateDirectory(discRoot);
        File.WriteAllText(Path.Combine(discRoot, "payload.bin"), "data");

        string fakePar2 = Path.Combine(_tempDir, "fake_par2_hang.cmd");
        File.WriteAllText(fakePar2, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"OUT=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "echo \"%~1\" | findstr /i \"recovery.par2\" >nul && set \"OUT=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "echo partial > \"%OUT%\"",
            "ping -n 30 127.0.0.1 >nul"));

        Par2Runner.ProcessTimeoutOverride.Value = TimeSpan.FromMilliseconds(150);
        try
        {
            var layout = new Par2RecoveryLayout(2048, 1, 1, 1, 4);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                Par2Runner.CreateAsync(fakePar2, discRoot, layout, scratch));

            Assert.Empty(Directory.GetFiles(scratch, "*.par2"));
        }
        finally
        {
            Par2Runner.ProcessTimeoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task Par2Runner_RepairTimeoutAndPartialOriginal_RestoresOriginalOverwritingPartial()
    {
        string folder = Path.Combine(_tempDir, "par2_repair_partial");
        string recovery = Path.Combine(folder, ArchiveManifest.RecoveryFolderName);
        Directory.CreateDirectory(recovery);

        const string originalDamaged = "original damaged bytes";
        File.WriteAllText(Path.Combine(folder, "data.bin"), originalDamaged);
        File.WriteAllText(Path.Combine(recovery, "recovery.par2"), "index");

        string fakePar2 = Path.Combine(_tempDir, "fake_par2_partial.cmd");
        File.WriteAllText(fakePar2, string.Join("\r\n",
            "@echo off",
            "ren data.bin data.bin.1",
            "echo half-written > data.bin",
            "ping -n 30 127.0.0.1 >nul"));

        Par2Runner.ProcessTimeoutOverride.Value = TimeSpan.FromMilliseconds(150);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                Par2Runner.RepairAsync(fakePar2, folder));

            Assert.Equal(originalDamaged, File.ReadAllText(Path.Combine(folder, "data.bin")));
            Assert.False(File.Exists(Path.Combine(folder, "data.bin.1")));
        }
        finally
        {
            Par2Runner.ProcessTimeoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task Par2Runner_RetryCancelWithMissingHeldVolume_DoesNotMaskOperationCanceledException()
    {
        string folder = Path.Combine(_tempDir, "par2_retry_cancel");
        string recovery = Path.Combine(folder, ArchiveManifest.RecoveryFolderName);
        Directory.CreateDirectory(recovery);

        File.WriteAllText(Path.Combine(folder, "data.bin"), "damaged");
        File.WriteAllText(Path.Combine(recovery, "recovery.par2"), "index");
        File.WriteAllText(Path.Combine(recovery, "recovery.vol000+01.par2"), "vol0");

        string retryStarted = Path.Combine(_tempDir, "retry_started.flag");
        string fakePar2 = Path.Combine(_tempDir, "fake_par2_retry_cancel.cmd");
        File.WriteAllText(fakePar2, string.Join("\r\n",
            "@echo off",
            "if exist \"RECOVERY\\recovery.vol000+01.par2\" (",
            "  echo RS computation error. 1>&2",
            "  exit /b 6",
            ")",
            // Create a conflicting file at the original volume path so finally cannot blindly overwrite.
            "echo conflict > \"RECOVERY\\recovery.vol000+01.par2\"",
            $"echo started> \"{retryStarted}\"",
            "ping -n 30 127.0.0.1 >nul"));

        using var cts = new CancellationTokenSource();
        Task<Par2RepairResult> repairTask = Par2Runner.RepairAsync(fakePar2, folder, cts.Token);

        await WaitForFlagAsync(retryStarted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repairTask);
    }

    [Fact]
    public void ShortPathAlias_LongTempPathAndHungCmd_DoesNotUseDriveRootAndKillsHungProcess()
    {
        string longTemp = Path.Combine(_tempDir, "LongUserTempFolderNameOverFortyChars");
        Directory.CreateDirectory(longTemp);
        string targetFolder = Path.Combine(_tempDir, new string('d', 120));
        Directory.CreateDirectory(targetFolder);
        string longFile = Path.Combine(targetFolder, new string('a', 50) + ".bin");

        ShortPathAlias.TempPathOverride.Value = longTemp;
        try
        {
            using ShortPathAlias? alias = ShortPathAlias.CreateIfNeeded(targetFolder, [longFile]);
            Assert.NotNull(alias);
            Assert.StartsWith(longTemp, alias!.Path, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetPathRoot(targetFolder), Path.GetDirectoryName(alias.Path));
        }
        finally
        {
            ShortPathAlias.TempPathOverride.Value = null;
        }

        // Hung cmd.exe: must be killed when JunctionTimeoutMsOverride elapses and return null.
        string lockFile = Path.Combine(_tempDir, "mklink_hang.lock");
        ShortPathAlias.TempPathOverride.Value = longTemp;
        ShortPathAlias.JunctionTimeoutMsOverride.Value = 120;
        ShortPathAlias.JunctionProcessStartInfoOverride.Value = (_, _) =>
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"(ping -n 30 127.0.0.1 >nul) > \"{lockFile}\"");
            return psi;
        };
        try
        {
            using ShortPathAlias? hungAlias = ShortPathAlias.CreateIfNeeded(targetFolder, [longFile]);
            Assert.Null(hungAlias);
            File.Delete(lockFile);
        }
        finally
        {
            ShortPathAlias.TempPathOverride.Value = null;
            ShortPathAlias.JunctionTimeoutMsOverride.Value = null;
            ShortPathAlias.JunctionProcessStartInfoOverride.Value = null;
        }
    }

    private sealed class StubSubtitleRunner(string ffmpeg, string cli) : IExternalToolRunner
    {
        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory,
            Action<string>? onOutputLine, CancellationToken cancellationToken)
        {
            if (executablePath == ffmpeg)
            {
                File.WriteAllBytes(Path.Combine(workingDirectory, arguments[^1]), new byte[512]);
                return Task.FromResult(0);
            }

            if (executablePath == cli)
            {
                File.WriteAllText(
                    Path.Combine(workingDirectory, "subtitles.srt"),
                    "1\n00:00:00,000 --> 00:00:02,000\nLine one\n\n");
                return Task.FromResult(0);
            }

            return Task.FromResult(1);
        }
    }

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private sealed class StubZipHandler(params (string FileName, byte[] Body)[] files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string name = request.RequestUri!.Segments[^1];
            foreach (var (fileName, body) in files)
            {
                if (fileName == name)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
