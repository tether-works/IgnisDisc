using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Cli;

/// <summary>
/// Batch queue burning, post-burn action helpers, DVD book-type bitsetting, and PAR2 archive repair CLI commands:
/// <c>burn-queue</c>, <c>book-type</c>, and <c>repair-par2</c>.
/// </summary>
public static partial class CliCommandRunner
{
    internal static PostBurnAction ParsePostBurnAction(CommandLineArgs cmd)
    {
        string? raw = cmd.GetOption("post-action");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PostBurnAction.None;
        }

        PostBurnAction action = raw.Trim().ToLowerInvariant() switch
        {
            "none" => PostBurnAction.None,
            "eject" => PostBurnAction.Eject,
            "sound" or "playsound" or "play-sound" => PostBurnAction.PlaySound,
            "eject-sound" or "ejectandplaysound" or "eject-and-play-sound" => PostBurnAction.EjectAndPlaySound,
            "shutdown" or "shut-down" => PostBurnAction.ShutDown,
            _ => throw new CommandLineException(
                $"Invalid --post-action '{raw}'. Use none, eject, sound, eject-sound, or shutdown.")
        };

        if (action == PostBurnAction.ShutDown && !cmd.HasFlag("confirm-shutdown"))
        {
            throw new CommandLineException(
                "--post-action shutdown schedules a host computer shutdown after burning; pass --confirm-shutdown to allow it.");
        }

        return action;
    }

    internal static void ExecutePostBurnActionIfSucceeded(
        PostBurnAction action,
        bool burnSucceeded,
        IDiscRecorderBackend recorder)
    {
        if (!burnSucceeded || action == PostBurnAction.None)
        {
            return;
        }

        PostBurnActionExecutor.Execute(action, recorder);
    }

    private static async Task<int> HandleBurnQueueAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string imagesRaw = cmd.GetRequiredOption("images", "--images <file1,file2,...>");
        string? speedStr = cmd.GetOption("speed");
        bool strictVerify = cmd.HasFlag("strict-verify");
        bool verify = strictVerify || !cmd.HasFlag("no-verify");
        bool closeDisc = !cmd.HasFlag("no-close");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        bool overwrite = cmd.HasFlag("overwrite");
        bool continueOnError = cmd.HasFlag("continue-on-error");
        PostBurnAction postAction = ParsePostBurnAction(cmd);

        int waitMinutes = 30;
        if (cmd.GetOption("wait-minutes") is { } waitRaw)
        {
            if (!int.TryParse(waitRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out waitMinutes) ||
                waitMinutes < 1 || waitMinutes > 1440)
            {
                throw new CommandLineException("--wait-minutes takes an integer from 1 to 1440.");
            }
        }

        var imagePaths = imagesRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (imagePaths.Length == 0)
        {
            throw new CommandLineException("No disc image files specified in --images.");
        }

        var resolvedPaths = new List<string>(imagePaths.Length);
        foreach (string rawPath in imagePaths)
        {
            string fullPath = Path.GetFullPath(rawPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Queue disc image file not found: '{fullPath}'.", fullPath);
            }
            if (!BurnQueueService.IsQueueableImage(fullPath, out string? reason))
            {
                throw new CommandLineException(reason ?? $"Image '{fullPath}' cannot be burned in a queue.");
            }
            resolvedPaths.Add(fullPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        MediaStatusDto initialStatus = recorder.QueryMediaStatus();
        DiscMediaType referenceMediaType = initialStatus.MediaPresent ? initialStatus.MediaType : DiscMediaType.DvdMinusR;
        double? requestedMultiplier = null;
        if (!string.IsNullOrWhiteSpace(speedStr))
        {
            var speedDesc = ParseSpeed(speedStr, recorder, referenceMediaType);
            requestedMultiplier = speedDesc?.Multiplier;
        }

        var queueItems = resolvedPaths.Select(p => new BurnQueueItem(
            ImagePath: p,
            WriteSpeedMultiplier: requestedMultiplier,
            VerifyAfterBurn: verify,
            CloseDisc: closeDisc)
        {
            DisableBufferUnderrunFree = disableBufe,
            StrictVerify = strictVerify,
            OverwriteExistingData = overwrite
        }).ToList();

        async Task<bool> PromptForNextDiscAsync(int itemIndex, BurnQueueItem nextItem, CancellationToken ct)
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                VirtualDiscMedium nextBlank = initialStatus.MediaType.IsDvd()
                    ? VirtualDiscMedium.CreateBlankDvdR47()
                    : VirtualDiscMedium.CreateBlankCdR700();
                virtualRecorder.InsertMedia(nextBlank);
                return true;
            }

            stderr.WriteLine(
                $"Queue item {itemIndex + 1} of {queueItems.Count} ({Path.GetFileName(nextItem.ImagePath)}): " +
                $"insert a blank disc in {recorder.VolumePath ?? recorder.UniqueId} and close the tray (waiting up to {waitMinutes} min).");

            var deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var status = recorder.QueryMediaStatus();
                    if (status.MediaPresent && (status.IsBlank || (overwrite && status.IsRewritable)))
                    {
                        stderr.WriteLine($"Ready {status.MediaType} found; burning next queue item.");
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            stderr.WriteLine($"No blank disc inserted within {waitMinutes} min.");
            return false;
        }

        var progress = cmd.IsJson ? null : new Progress<BurnProgressSnapshot>(snap =>
        {
            if (!string.IsNullOrWhiteSpace(snap.CurrentActionDescription))
            {
                stderr.Write($"\r  {snap.CurrentActionDescription,-60}");
            }
        });

        var queueService = new BurnQueueService(burnEngine);
        var result = await queueService.ExecuteQueueAsync(
            recorder,
            queueItems,
            promptForNextDiscAsync: PromptForNextDiscAsync,
            progress: progress,
            postBurnAction: postAction,
            continueOnError: continueOnError).ConfigureAwait(false);

        if (!cmd.IsJson)
        {
            stderr.WriteLine();
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                totalItems = result.TotalItems,
                completedItems = result.CompletedItems,
                failedItems = result.FailedItems,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                postAction = postAction.ToString(),
                error = result.ErrorMessage,
                items = result.Items.Select(it => new
                {
                    imagePath = it.ImagePath,
                    status = it.Status.ToString(),
                    sectorsWritten = it.BurnResult?.SectorsWritten ?? 0,
                    error = it.ErrorMessage
                })
            });
            return result.Success ? 0 : 1;
        }

        stdout.WriteLine($"Burn queue finished: {result.CompletedItems}/{result.TotalItems} completed, {result.FailedItems} failed ({result.TotalTime.TotalSeconds:F1} s).");
        foreach (var item in result.Items)
        {
            stdout.WriteLine($"  [{item.Status}] {Path.GetFileName(item.ImagePath)}" +
                             (string.IsNullOrWhiteSpace(item.ErrorMessage) ? string.Empty : $" - {item.ErrorMessage}"));
        }
        return result.Success ? 0 : 1;
    }

    private static int HandleBookTypeCommand(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string? targetRaw = cmd.GetOption("set");

        var recorder = FindRecorder(deviceService, driveId);
        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            throw new CommandLineException($"No optical disc is inserted in drive '{driveId}'.");
        }

        var service = new BookTypeService();
        DvdBookType previous = service.GetBookType(recorder);
        bool bitsettingSupported = BookTypeService.IsBitsettingSupported(status.MediaType);

        if (string.IsNullOrWhiteSpace(targetRaw))
        {
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    driveId = recorder.UniqueId,
                    mediaType = status.MediaType.ToString(),
                    bitsettingSupported,
                    bookType = previous.ToString(),
                    bookTypeCode = $"0x{(byte)previous:X2}"
                });
                return 0;
            }

            stdout.WriteLine($"Drive:                {recorder.UniqueId}");
            stdout.WriteLine($"Media Type:           {status.MediaType.GetDisplayName()}");
            stdout.WriteLine($"Book Type:            {previous} (0x{(byte)previous:X2})");
            stdout.WriteLine($"Bitsetting Supported: {(bitsettingSupported ? "Yes" : "No")}");
            return 0;
        }

        DvdBookType requested = ParseDvdBookType(targetRaw);
        var setResult = service.SetBookType(recorder, requested);
        DvdBookType current = service.GetBookType(recorder);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = setResult.Success,
                driveId = recorder.UniqueId,
                mediaType = status.MediaType.ToString(),
                bitsettingSupported,
                previousBookType = previous.ToString(),
                requestedBookType = requested.ToString(),
                bookType = current.ToString(),
                bookTypeCode = $"0x{(byte)current:X2}",
                error = setResult.ErrorMessage
            });
            return setResult.Success ? 0 : 1;
        }

        if (!setResult.Success)
        {
            stderr.WriteLine($"Book type change failed: {setResult.ErrorMessage}");
            return 1;
        }

        stdout.WriteLine($"Book type updated: {previous} -> {current} on {recorder.UniqueId} ({status.MediaType.GetDisplayName()}).");
        return 0;
    }

    private static DvdBookType ParseDvdBookType(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "dvd-rom" or "dvdrom" or "rom" => DvdBookType.DvdRom,
            "dvd+r" or "dvdplusr" => DvdBookType.DvdPlusR,
            "dvd+rw" or "dvdplusrw" => DvdBookType.DvdPlusRw,
            "dvd+r-dl" or "dvd+rdl" or "dvdplusrdl" => DvdBookType.DvdPlusRDualLayer,
            "dvd-r" or "dvdminusr" => DvdBookType.DvdMinusR,
            "dvd-rw" or "dvdminusrw" => DvdBookType.DvdMinusRw,
            "dvd-ram" or "dvdram" => DvdBookType.DvdRam,
            _ => throw new CommandLineException(
                $"Unknown book type '{raw}'. Use dvd-rom, dvd+r, dvd+rw, dvd+r-dl, dvd-r, dvd-rw, or dvd-ram.")
        };
    }

    internal static async Task<int> HandleRepairPar2Async(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        string? par2ExecutableOverride = null)
    {
        string rawPath = cmd.GetRequiredOption("path", "--path <folder>");
        string folderPath = Path.GetFullPath(rawPath);
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Directory to repair does not exist: '{folderPath}'.");
        }

        bool deleteBackups = cmd.HasFlag("delete-backups");
        string? par2Path = Par2Runner.FindPar2Executable(par2ExecutableOverride ?? cmd.GetOption("par2"));
        if (string.IsNullOrWhiteSpace(par2Path))
        {
            throw new CommandLineException("PAR2 tool (par2.exe) was not found. Install it with: ignisdisc-cli install-par2");
        }

        var result = await Par2Runner.RepairAsync(par2Path, folderPath).ConfigureAwait(false);
        bool succeeded = result.ExitCode == 0;
        var deletedBackups = new List<string>();

        if (succeeded && deleteBackups)
        {
            foreach (string backup in result.BackupFiles)
            {
                try
                {
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                        deletedBackups.Add(backup);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"Warning: could not remove backup '{backup}': {ex.Message}");
                }
            }
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = succeeded,
                path = folderPath,
                par2Executable = par2Path,
                exitCode = result.ExitCode,
                attempts = result.Attempts,
                backupFiles = result.BackupFiles,
                deletedBackups,
                error = string.IsNullOrWhiteSpace(result.ErrorOutput) ? null : result.ErrorOutput
            });
            return succeeded ? 0 : 1;
        }

        if (!succeeded)
        {
            stderr.WriteLine($"PAR2 repair failed (exit code {result.ExitCode}): {result.ErrorOutput}");
            return 1;
        }

        stdout.WriteLine($"PAR2 verification/repair succeeded in {folderPath} ({result.Attempts} run(s)).");
        if (result.BackupFiles.Count > 0)
        {
            stdout.WriteLine(deleteBackups
                ? $"  Deleted {deletedBackups.Count} backup copy file(s)."
                : $"  Backup copies kept ({result.BackupFiles.Count}); pass --delete-backups to remove them.");
        }
        return 0;
    }
}
