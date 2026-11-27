using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Core.Verification;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Scsi.QualityScan;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Cli;

/// <summary>
/// Exception thrown for command-line syntax or argument validation errors.
/// </summary>
public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message) { }
}

/// <summary>
/// Command-line interface orchestration engine for optical drive inspection, ISO mastering,
/// burning, ripping, verification, erasing, and surface scanning.
/// </summary>
public static partial class CliCommandRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "udf", "joliet", "verify", "close", "no-close", "raw", "full", "dao", "help", "h", "version", "v",
        "lookup", "keep-wav", "continue-on-error", "verify-accuraterip", "verify-ctdb", "no-ctdb-repair", "secure", "plan-only",
        "overwrite", "strict-verify", "no-verify", "no-secure", "allow-damaged", "no-fill", "keep-folders",
        "generate-subtitles", "no-menu", "dual-layer", "fit-to-disc", "no-ffmpeg", "no-cd-text", "normalize", "peak-normalize", "rms-level",
        "no-burnfree", "append", "delete-backups", "confirm-shutdown", "windows-writer", "no-interleave",
        "no-cover-lookup", "no-folder-image", "recursive", "no-rim", "rim-header",
        "sha256sums", "blake3sums", "native", "test-write", "test-first", "recovery-session", "blue-book", "streaming",
        "allow-suspect", "repair", "repair-from-recovery", "no-replaygain", "health-certificate", "files-outer", "read-compat", "force-old",
        "get", "require-book-type", "allow-overburn", "refresh-offsets",
        "allow-rewritable", "no-defect-management", "allow-fingerprint-mismatch", "quick"
    };

    private static void WriteJsonResult(TextWriter stdout, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions);
        if (node is JsonObject obj)
        {
            var withEngine = new JsonObject
            {
                ["engine"] = LegalBanner.JsonEngine
            };
            foreach (var kvp in obj)
            {
                if (kvp.Key != "engine")
                {
                    withEngine[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            stdout.WriteLine(JsonSerializer.Serialize(withEngine, JsonOptions));
            return;
        }

        stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Executes a command-line request asynchronously and returns the process exit code (0 = success, non-zero = failure).
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService? customDeviceService = null,
        IBurnEngineBackend? customBurnEngine = null,
        HttpClient? customHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var parsed = CommandLineArgs.Parse(args);

        // General help / version flags without subcommand
        if (string.IsNullOrEmpty(parsed.Subcommand))
        {
            if (parsed.HasFlag("version") || parsed.HasFlag("v"))
            {
                string? version = typeof(CliCommandRunner).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion?.Split('+')[0];

                if (parsed.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        version = version ?? "unknown",
                        notice = LegalBanner.VersionNotice
                    });
                    return 0;
                }

                stdout.WriteLine(LegalBanner.VersionNotice);
                stdout.WriteLine($"IgnisDisc (CdBurner CLI) v{version ?? "dev"} (.NET {Environment.Version.ToString(2)} Windows)");
                return 0;
            }

            if (parsed.HasFlag("help") || parsed.HasFlag("h") || args.Length == 0)
            {
                if (parsed.IsJson)
                {
                    using var sw = new StringWriter();
                    PrintHelp(sw);
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        usage = "ignisdisc-cli <command> [options]",
                        notice = LegalBanner.VersionNotice,
                        commands = SupportedCommandNames,
                        help = sw.ToString()
                    });
                    return 0;
                }

                PrintHelp(stdout);
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                ValidateTopLevelOptions(parsed);
            }
            catch (CommandLineException cle)
            {
                return HandleError(parsed, stdout, stderr, cle.Message);
            }
        }

        if (parsed.HasFlag("help") || parsed.HasFlag("h"))
        {
            if (parsed.IsJson)
            {
                using var sw = new StringWriter();
                PrintSubcommandHelp(parsed.Subcommand, sw);
                WriteJsonResult(stdout, new
                {
                    success = true,
                    command = parsed.Subcommand,
                    notice = LegalBanner.VersionNotice,
                    help = sw.ToString()
                });
                return 0;
            }

            PrintSubcommandHelp(parsed.Subcommand, stdout);
            return 0;
        }

        bool ownsDeviceService = customDeviceService == null;
        bool skipPhysical = ShouldSkipPhysicalEnumeration(parsed);
        var deviceService = customDeviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: !skipPhysical && OperatingSystem.IsWindows());
        var burnEngine = customBurnEngine ?? new CompositeBurnEngine();
        VirtualAutomationContext? virtualContext = null;

        try
        {
            string subcommand = parsed.Subcommand?.ToLowerInvariant() ?? string.Empty;
            ValidateCommandOptions(parsed, subcommand);
            virtualContext = PrepareVirtualAutomation(parsed, deviceService, ownsDeviceService);
            int exitCode = await DispatchCommandAsync(parsed, stdout, stderr, deviceService, burnEngine, customHttpClient, virtualContext).ConfigureAwait(false);
            SaveVirtualAutomationState(virtualContext, deviceService, subcommand);
            return exitCode;
        }
        catch (CommandLineException cle)
        {
            return HandleError(parsed, stdout, stderr, cle.Message);
        }
        catch (FileNotFoundException fnfe)
        {
            return HandleError(parsed, stdout, stderr, fnfe.Message);
        }
        catch (DirectoryNotFoundException dnfe)
        {
            return HandleError(parsed, stdout, stderr, dnfe.Message);
        }
        catch (Exception ex)
        {
            return HandleError(parsed, stdout, stderr, ex.Message);
        }
        finally
        {
            virtualContext?.Dispose();
            if (ownsDeviceService && deviceService is IDisposable disp)
            {
                disp.Dispose();
            }
        }
    }

    private static int HandleError(CommandLineArgs parsed, TextWriter stdout, TextWriter stderr, string errorMessage)
    {
        if (parsed.IsJson)
        {
            var errObj = new { success = false, error = errorMessage };
            WriteJsonResult(stdout, errObj);
        }
        else
        {
            stderr.WriteLine($"Error: {errorMessage}");
        }
        return 1;
    }

    private static async Task<int> DispatchCommandAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine,
        HttpClient? customHttpClient = null,
        VirtualAutomationContext? virtualContext = null)
    {
        string subcommand = cmd.Subcommand?.ToLowerInvariant() ?? string.Empty;
        ValidateCommandOptions(cmd, subcommand);

        return subcommand switch
        {
            "drives" or "list-drives" => HandleDrives(cmd, stdout, deviceService),
            "media-info" or "mediainfo" or "info" => HandleMediaInfo(cmd, stdout, deviceService),
            "virtual-mount" or "virtualmount" => HandleVirtualMount(cmd, stdout, deviceService, virtualContext ?? new VirtualAutomationContext()),
            "virtual-eject" or "virtualeject" => HandleVirtualEject(cmd, stdout, deviceService, virtualContext ?? new VirtualAutomationContext()),
            "virtual-status" or "virtualstatus" => HandleVirtualStatus(cmd, stdout, deviceService, virtualContext ?? new VirtualAutomationContext()),
            "build-iso" or "buildiso" => await HandleBuildIsoAsync(cmd, stdout).ConfigureAwait(false),
            "burn-iso" or "burniso" => await HandleBurnIsoAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "burn-data" or "burndata" => await HandleBurnDataAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "burn-audio" or "burnaudio" => await HandleBurnAudioAsync(cmd, stdout, stderr, deviceService, burnEngine, customHttpClient).ConfigureAwait(false),
            "burn-dvd" or "burndvd" => await HandleBurnDvdAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "rip" or "rip-disc" => await HandleRipAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "rip-audio" or "ripaudio" => await HandleRipAudioAsync(cmd, stdout, stderr, deviceService, burnEngine, customHttpClient).ConfigureAwait(false),
            "convert-audio" or "convertaudio" => await HandleConvertAudioAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "detect-offset" or "detectoffset" => await HandleDetectOffsetAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "check-disc" or "checkdisc" => HandleCheckDisc(cmd, stdout, deviceService),
            "copy-disc" or "copydisc" or "copy" => await HandleCopyDiscAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "duplicate" => await HandleDuplicateAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "archive" => await HandleArchiveCommandAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "archive-stage" or "archivestage" => await HandleArchiveStageAsync(cmd, stdout).ConfigureAwait(false),
            "archive-burn" or "archiveburn" => await HandleArchiveBurnAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "archive-verify" or "archiveverify" => await HandleArchiveVerifyAsync(cmd, stdout).ConfigureAwait(false),
            "archive-rebuild" or "archiverebuild" => await HandleArchiveRebuildAsync(cmd, stdout).ConfigureAwait(false),
            "recover" or "archive-recover" => await HandleRecoverAsync(cmd, stdout, deviceService, burnEngine).ConfigureAwait(false),
            "verify" or "verify-disc" => await HandleVerifyAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "erase" or "erase-disc" => await HandleEraseAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "scan" or "scan-disc" => await HandleScanAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "quality-scan" or "qualityscan" => await HandleQualityScanAsync(cmd, stdout, stderr, deviceService).ConfigureAwait(false),
            "drive-report" or "drivereport" => HandleDriveReport(cmd, stdout, deviceService),
            "install-subtitle-tool" => await HandleInstallSubtitleToolAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "generate-subtitles" => await HandleGenerateSubtitlesAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "eject" => RunTrayCommand(cmd, stdout, stderr, deviceService, load: false),
            "load" => RunTrayCommand(cmd, stdout, stderr, deviceService, load: true),
            "settings" => HandleSettingsCommand(cmd, stdout),
            "tools" => HandleToolsCommand(cmd, stdout),
            "install-ffmpeg" => await HandleInstallFfmpegAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "install-par2" => await HandleInstallPar2Async(cmd, stdout, stderr).ConfigureAwait(false),
            "check-update" => await HandleCheckUpdateAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "image-info" or "imageinfo" => HandleImageInfo(cmd, stdout),
            "browse-image" or "browseimage" or "browse" => await HandleBrowseImageAsync(cmd, stdout, deviceService, burnEngine).ConfigureAwait(false),
            "extract-image" or "extractimage" => await HandleExtractImageAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "convert-image" or "convertimage" => await HandleConvertImageAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "author-dvd" or "authordvd" => await HandleAuthorDvdAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "author-bdmv" or "authorbdmv" => await HandleAuthorBdmvAsync(cmd, stdout, stderr).ConfigureAwait(false),
            "jewel-cover" or "jewelcover" => HandleJewelCover(cmd, stdout, stderr),
            "audio-plan" or "audioplan" => HandleAudioPlan(cmd, stdout, stderr),
            "burn-queue" or "burnqueue" => await HandleBurnQueueAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "book-type" or "booktype" => HandleBookTypeCommand(cmd, stdout, stderr, deviceService),
            "repair-par2" or "repairpar2" => await HandleRepairPar2Async(cmd, stdout, stderr).ConfigureAwait(false),
            "ecc-rs03" or "rs03" => await HandleEccRs03Async(cmd, stdout, stderr).ConfigureAwait(false),
            "rosetta" or "rosetta-ladder" or "rosettaladder" => await HandleRosettaCommandAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "disc-art" or "discart" => await HandleDiscArtCommandAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            _ => throw new CommandLineException($"Unknown command '{cmd.Subcommand}'. Use --help for usage information.")
        };
    }

    #region Subcommand Handlers

    private static int HandleDrives(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        var recorders = deviceService.GetRecorders(includeVirtual: true);

        if (cmd.IsJson)
        {
            var driveDtos = recorders.Select(r =>
            {
                var caps = r.GetCapabilities();
                bool canWriteCd = caps.CanWriteCdR || caps.CanWriteCdRw;
                bool canWriteDvd = caps.CanWriteDvdR || caps.CanWriteDvdRw || caps.CanWriteDvdPlusR || caps.CanWriteDvdPlusRw || caps.CanWriteDvdRam;
                bool canWriteDl = caps.CanWriteDvdPlusRDL || caps.CanWriteDvdMinusRDL;
                bool canReadCd = caps.SupportedMediaTypes.Any(m => m.IsCd());
                bool canReadDvd = caps.SupportedMediaTypes.Any(m => m.IsDvd());

                return new
                {
                    uniqueId = r.UniqueId,
                    vendorId = r.VendorId,
                    productId = r.ProductId,
                    productRevision = r.ProductRevision,
                    volumePath = r.VolumePath,
                    isVirtual = r.IsVirtual,
                    capabilities = new
                    {
                        canWriteCd = canWriteCd,
                        canWriteDvd = canWriteDvd,
                        canWriteDualLayer = canWriteDl,
                        canReadCd = canReadCd,
                        canReadDvd = canReadDvd,
                        supportedMediaTypes = caps.SupportedMediaTypes.Select(m => m.ToString()).ToList(),
                        mmcFeatures = caps.MmcFeatureSummary,
                        supportsNativeTao = caps.SupportsNativeTao,
                        supportsNativeSao = caps.SupportsNativeSao,
                        supportsNativeCdText = caps.SupportsNativeCdText,
                        supportsNativeRawDao = caps.SupportsNativeRawDao,
                        supportsTestWrite = caps.SupportsTestWrite,
                        supportsBufferUnderrunFree = caps.SupportsBufferUnderrunFree,
                        supportsLayerJump = caps.SupportsLayerJump,
                        supportedWriteSpeeds = caps.SupportedWriteSpeeds.Select(s => new
                        {
                            multiplier = s.Multiplier,
                            bytesPerSecond = s.BytesPerSecond,
                            mediaType = s.MediaType.ToString()
                        }).ToList()
                    }
                };
            }).ToList();

            var result = new { success = true, drives = driveDtos };
            WriteJsonResult(stdout, result);
            return 0;
        }

        stdout.WriteLine($"Found {recorders.Count} optical drive(s):");
        stdout.WriteLine(new string('-', 85));
        stdout.WriteLine($"{"Drive ID",-12} {"Vendor",-10} {"Product",-24} {"Type",-10} {"Path",-8} {"Write Capabilities",-20}");
        stdout.WriteLine(new string('-', 85));

        foreach (var r in recorders)
        {
            var caps = r.GetCapabilities();
            var writeModes = new List<string>(3);
            if (caps.CanWriteCdR || caps.CanWriteCdRw) writeModes.Add("CD");
            if (caps.CanWriteDvdR || caps.CanWriteDvdRw || caps.CanWriteDvdPlusR || caps.CanWriteDvdPlusRw || caps.CanWriteDvdRam) writeModes.Add("DVD");
            if (caps.CanWriteDvdPlusRDL || caps.CanWriteDvdMinusRDL) writeModes.Add("DL");
            string writeCapStr = writeModes.Count > 0 ? string.Join("/", writeModes) : "Read-Only";

            stdout.WriteLine($"{r.UniqueId,-12} {r.VendorId,-10} {r.ProductId,-24} {(r.IsVirtual ? "Virtual" : "Physical"),-10} {r.VolumePath,-8} {writeCapStr,-20}");
        }

        return 0;
    }

    private static int HandleDriveReport(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = ResolveTargetDriveId(cmd);
        var recorder = FindRecorder(deviceService, driveId);

        var caps = recorder.GetCapabilities();
        var media = recorder.WithManufacturerId(recorder.QueryMediaStatus());
        string report = DriveCompatibilityReportBuilder.BuildMarkdownReport(recorder, caps, media);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                driveId = recorder.UniqueId,
                report = report
            };
            WriteJsonResult(stdout, result);
            return 0;
        }

        stdout.WriteLine(report);
        return 0;
    }

    /// <summary>
    /// eject / load --drive &lt;id&gt; [--json]: opens or closes the tray with START STOP UNIT. The drive's Removable
    /// Medium feature is read first; a load on a drive that cannot close its own tray sends nothing and says to push
    /// the tray in by hand (exit code 1). A virtual drive goes through the simulated drive and its tray follows what
    /// that drive accepted.
    /// </summary>
    private static int RunTrayCommand(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr, IDiscDeviceService deviceService, bool load)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        TrayCommandResult result;
        if (recorder is VirtualDiscRecorder virtualRecorder)
        {
            var drive = NativeAudioBurnEngine.CreateVirtualDrive(virtualRecorder);
            result = load ? TrayCommands.Load(drive, _ => { }, CancellationToken.None) : TrayCommands.Eject(drive);
            if (result.Success && load)
            {
                virtualRecorder.CloseTray();
            }
            else if (result.Success)
            {
                virtualRecorder.EjectMedia();
            }
        }
        else if (recorder.IsVirtual)
        {
            throw new CommandLineException($"Drive '{driveId}' is a virtual drive without a tray.");
        }
        else
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new CommandLineException("Opening and closing the tray of a physical drive needs Windows.");
            }
            try
            {
                using var transport = Win32ScsiTransport.Open(NativeAudioBurnEngine.DevicePathFor(recorder));
                result = load ? TrayCommands.Load(transport, Thread.Sleep, CancellationToken.None) : TrayCommands.Eject(transport);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                throw new CommandLineException($"Could not open the drive for drive commands: {ex.Message}");
            }
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                action = load ? "load" : "eject",
                commandSent = result.CommandSent,
                needsManualClose = result.NeedsManualClose,
                discPresent = result.DiscPresent,
                message = result.Message,
                loadingMechanism = result.Mechanism?.LoadingMechanism,
                canLoad = result.Mechanism?.CanLoad,
                canEject = result.Mechanism?.CanEject
            });
        }
        else
        {
            (result.Success ? stdout : stderr).WriteLine(result.Message);
        }
        return result.Success ? 0 : 1;
    }

    private static int HandleMediaInfo(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = ResolveTargetDriveId(cmd);
        var recorder = FindRecorder(deviceService, driveId);

        var status = recorder.WithManufacturerId(recorder.QueryMediaStatus());
        var caps = recorder.GetCapabilities();
        var speedCap = status.MediaPresent ? MediaSpeedCaps.Find(status.MediaManufacturerId, status.MediaType) : null;
        string? recommendedSpeed = null;
        if (status.MediaPresent)
        {
            var forDisc = WriteSpeedPolicy.ForDisc(caps.SupportedWriteSpeeds, status.MediaType);
            var capped = WriteSpeedPolicy.ChooseForMedia(forDisc, WriteSpeedPolicy.Recommended, status.MediaManufacturerId, status.MediaType);
            recommendedSpeed = WriteSpeedPolicy.DescribeCappedChoice(capped.Speed, speedCap)
                ?? WriteSpeedPolicy.ExplainRecommended(forDisc);
        }
        // Straight from the drive (read-only): formatted size and the write speeds it offers for this disc.
        var driveReport = status.MediaPresent && recorder is IDriveMediaReportProvider reportProvider
            ? reportProvider.ReadDriveMediaReport()
            : DriveMediaReport.Empty;
        // Only where formatting decides the usable size; on a CD-RW the drive just echoes the recorded data.
        var formatted = status.MediaType.IsFormattable() ? driveReport.FormattedCapacity : null;
        // Overwrite-only media (DVD+RW, DVD-RAM) are never "finalized": each burn rewrites them from the start.
        bool isFinalized = status.MediaPresent && !status.IsLoading && !status.IsBlank && !status.IsAppendable && !status.IsOverwriteOnly;
        long usedBytes = status.UsedSectors > 0
            ? (long)status.UsedSectors * OpticalConstants.DataSectorSize
            : Math.Max(0L, status.TotalCapacityBytes - status.FreeCapacityBytes);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                driveId = recorder.UniqueId,
                vendorId = recorder.VendorId,
                productId = recorder.ProductId,
                productRevision = recorder.ProductRevision,
                volumePath = recorder.VolumePath,
                isVirtual = recorder.IsVirtual,
                mediaPresent = status.MediaPresent,
                mediaType = status.MediaType.ToString(),
                mediaTypeName = status.MediaType.GetDisplayName(),
                mediaManufacturerId = status.MediaManufacturerId,
                mmcFeatures = caps.MmcFeatureSummary,
                isLoading = status.IsLoading,
                isBlank = status.IsBlank,
                isRewritable = status.IsRewritable,
                isAppendable = status.IsAppendable,
                isFinalized = isFinalized,
                isOverwriteOnly = status.IsOverwriteOnly,
                totalCapacityBytes = status.TotalCapacityBytes,
                freeCapacityBytes = status.FreeCapacityBytes,
                usedCapacityBytes = usedBytes,
                totalSectors = status.TotalSectors,
                freeSectors = status.FreeSectors,
                usedSectors = status.UsedSectors,
                sessionCount = status.SessionCount,
                lastRecordedLba = status.LastRecordedLba,
                nextWritableLba = status.NextWritableLba,
                supportedWriteSpeeds = caps.SupportedWriteSpeeds.Select(s => new
                {
                    multiplier = s.Multiplier,
                    bytesPerSecond = s.BytesPerSecond,
                    mediaType = s.MediaType.ToString()
                }).ToList(),
                formattedCapacity = formatted == null ? null : new
                {
                    state = formatted.StateName,
                    blocks = formatted.Blocks,
                    blockLength = formatted.BlockLength,
                    reportedBlockLength = formatted.ReportedBlockLength,
                    bytes = formatted.Bytes,
                    formats = formatted.Formats.Select(f => new
                    {
                        formatType = $"0x{f.FormatType:X2}",
                        blocks = f.Blocks,
                        typeParameter = f.TypeParameter
                    }).ToList()
                },
                recommendedWriteSpeed = recommendedSpeed,
                speedLimit = speedCap == null ? null : new { maxMultiplier = speedCap.MaxMultiplier, reason = speedCap.Reason },
                writeSpeedsFromDrive = driveReport.WriteSpeeds.Select(s => new
                {
                    multiplier = s.Multiplier(status.MediaType),
                    kilobytesPerSecond = s.KilobytesPerSecond,
                    bytesPerSecond = s.BytesPerSecond,
                    endLba = s.EndLba,
                    exact = s.Exact,
                    rotation = s.Rotation.ToString().ToUpperInvariant()
                }).ToList()
            };

            WriteJsonResult(stdout, result);
            return 0;
        }

        stdout.WriteLine($"Drive '{recorder.UniqueId}' ({recorder.VendorId} {recorder.ProductId}):");
        if (caps.MmcFeatureSummary.Count > 0)
        {
            stdout.WriteLine($"  MMC Features:          {string.Join(", ", caps.MmcFeatureSummary)}");
        }
        stdout.WriteLine($"  Media Present:         {(status.MediaPresent ? "Yes" : "No")}");
        if (status.MediaPresent)
        {
            stdout.WriteLine($"  Media Type:            {status.MediaType.GetDisplayName()}");
            if (!string.IsNullOrWhiteSpace(status.MediaManufacturerId))
            {
                stdout.WriteLine($"  Media ID / ATIP:       {status.MediaManufacturerId}");
            }
            if (status.IsLoading)
            {
                stdout.WriteLine("  State:                 Still loading; ask again in a few seconds");
            }
            stdout.WriteLine($"  Blank:                 {(status.IsBlank ? "Yes" : "No")}");
            stdout.WriteLine($"  Rewritable:            {(status.IsRewritable ? "Yes" : "No")}");
            stdout.WriteLine($"  Appendable:            {(status.IsAppendable ? "Yes" : "No")}");
            stdout.WriteLine(status.IsOverwriteOnly
                ? "  Finalized:             No (rewritten in place; a burn replaces everything on it)"
                : $"  Finalized:             {(isFinalized ? "Yes" : "No")}");
            stdout.WriteLine($"  Total Capacity:        {FormatBytes(status.TotalCapacityBytes)} ({status.TotalSectors:N0} sectors)");
            stdout.WriteLine(status.HasDataThatBurningReplaces
                ? $"  Free Capacity:         {FormatBytes(status.FreeCapacityBytes)} after replacing the current files (--overwrite)"
                : $"  Free Capacity:         {FormatBytes(status.FreeCapacityBytes)} ({status.FreeSectors:N0} sectors)");
            stdout.WriteLine($"  Used Capacity:         {FormatBytes(usedBytes)} ({status.UsedSectors:N0} sectors)");
            stdout.WriteLine($"  Sessions:              {status.SessionCount}");
            stdout.WriteLine($"  Next Writable LBA:     {status.NextWritableLba:N0}");
            if (formatted != null)
            {
                stdout.WriteLine(formatted.State == FormattedCapacityState.Formatted
                    ? $"  Formatted Capacity:    {FormatBytes(formatted.Bytes)} ({formatted.Blocks:N0} blocks of {formatted.BlockLength})"
                    : $"  Formattable Up To:     {FormatBytes(formatted.Bytes)} ({formatted.Blocks:N0} blocks, not formatted)");
            }
            if (driveReport.WriteSpeeds.Count > 0)
            {
                stdout.WriteLine($"  Drive's Write Speeds:  {string.Join(", ", driveReport.WriteSpeeds.Select(s => $"{s.Multiplier(status.MediaType):0.#}x{(s.Rotation == WriteRotation.Unknown ? "" : " " + s.Rotation.ToString().ToUpperInvariant())}"))}");
            }
            if (recommendedSpeed != null)
            {
                stdout.WriteLine($"  Default Write Speed:   {recommendedSpeed}");
            }
            if (speedCap != null)
            {
                stdout.WriteLine($"  Speed Limit:           {speedCap.MaxMultiplier:0.#}x - {speedCap.Reason}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads <c>--fixed-time</c> (Unix seconds or an ISO 8601 date/time; a time without an offset is taken as UTC),
    /// falling back to the <c>SOURCE_DATE_EPOCH</c> environment variable used by reproducible-build tooling.
    /// Returns null when neither is given, which keeps the normal build-time stamps.
    /// </summary>
    internal static DateTime? ResolveFixedTimestamp(CommandLineArgs cmd)
    {
        string? flagValue = cmd.GetOption("fixed-time");
        string? value = flagValue ?? Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return ParseFixedTimestamp(value.Trim(), flagValue != null ? "--fixed-time" : "SOURCE_DATE_EPOCH");
    }

    /// <summary>Parses Unix seconds or an ISO 8601 date/time into a UTC build time an image can record.</summary>
    public static DateTime ParseFixedTimestamp(string value, string source = "--fixed-time")
    {
        var earliest = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2155, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        DateTime result;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds > 253402300799L)
            {
                throw new ArgumentException($"{source}: {value} is not a valid Unix time in seconds.");
            }
            result = DateTime.UnixEpoch.AddSeconds(seconds);
        }
        else if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            result = parsed.UtcDateTime;
        }
        else
        {
            throw new ArgumentException($"{source}: '{value}' is not Unix seconds or an ISO 8601 date/time (e.g. 2026-01-01T00:00:00Z).");
        }

        // ISO 9660 dates cover 1900-2155; 1980 is the floor the builder already uses for file times.
        if (result < earliest || result > latest)
        {
            throw new ArgumentException($"{source}: {result:yyyy-MM-dd} is outside the range a disc image can record (1980-2155).");
        }
        return DateTime.SpecifyKind(result, DateTimeKind.Utc);
    }

    /// <summary>The UDF revision from <c>--udf-rev</c> (e.g. "2.50"), or 1.02 when it is not given.</summary>
    internal static ushort ResolveUdfRevision(CommandLineArgs cmd) =>
        cmd.GetOption("udf-rev") is { } value ? ParseUdfRevision(value) : (ushort)0x0102;

    /// <summary>Parses "2.50", "250" or "0x0250" into the BCD revision the builder takes.</summary>
    public static ushort ParseUdfRevision(string value)
    {
        string digits = value.Trim().Replace(".", "", StringComparison.Ordinal);
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }
        if (digits.Length is >= 3 and <= 4 && ushort.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out ushort bcd)
            && IsoUdfImageBuilder.SupportedUdfRevisions.Contains(bcd))
        {
            return bcd;
        }
        throw new ArgumentException($"--udf-rev: '{value}' is not a supported UDF revision. Use 1.02, 1.50, 2.00, 2.01, 2.50 or 2.60.");
    }

    private static async Task<int> HandleBuildIsoAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string inputFolder = cmd.GetRequiredOption("input", "--input <folder>");
        string outputIso = cmd.GetRequiredOption("output", "--output <file.iso>");
        string? volumeLabel = cmd.GetOption("label");
        string? bootBiosPath = cmd.GetOption("boot-bios");
        string? bootUefiPath = cmd.GetOption("boot-uefi");

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        bool hasUdfFlag = cmd.HasFlag("udf") || cmd.GetOption("udf-rev") != null;
        bool hasJolietFlag = cmd.HasFlag("joliet");
        bool includeUdf = true;
        bool includeJoliet = true;

        if (hasUdfFlag || hasJolietFlag)
        {
            includeUdf = hasUdfFlag;
            includeJoliet = hasJolietFlag;
        }

        var bootConfig = BuildBootConfiguration(bootBiosPath, bootUefiPath, volumeLabel);

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "CDBURNER_ISO";
        var isoOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            UdfRevision: ResolveUdfRevision(cmd),
            BootConfiguration: bootConfig,
            FixedTimestampUtc: ResolveFixedTimestamp(cmd),
            WriteSha256Manifest: cmd.HasFlag("sha256sums"),
            WriteBlake3Manifest: cmd.HasFlag("blake3sums"));

        var root = CompilationDirectory.CreateRoot();
        PopulateDirectoryFromDisk(root, inputFolder);

        await IsoUdfImageBuilder.BuildToFileAsync(root, outputIso, isoOptions).ConfigureAwait(false);

        var fileInfo = new FileInfo(outputIso);
        int totalSectors = OpticalConstants.BytesToSectors(fileInfo.Length, OpticalConstants.DataSectorSize);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                outputPath = Path.GetFullPath(outputIso),
                totalBytes = fileInfo.Length,
                totalSectors = totalSectors,
                volumeLabel = effectiveLabel,
                hasIso9660 = true,
                hasJoliet = includeJoliet,
                hasUdf = includeUdf,
                isBootable = bootConfig != null
            };

            WriteJsonResult(stdout, result);
            return 0;
        }

        stdout.WriteLine("ISO image built successfully:");
        stdout.WriteLine($"  Output Path:   {Path.GetFullPath(outputIso)}");
        stdout.WriteLine($"  Size:          {FormatBytes(fileInfo.Length)} ({totalSectors:N0} sectors)");
        stdout.WriteLine($"  Volume Label:  {effectiveLabel}");
        stdout.WriteLine($"  Joliet:        {includeJoliet}");
        stdout.WriteLine($"  UDF:           {includeUdf}");
        stdout.WriteLine($"  Bootable:      {bootConfig != null}");

        return 0;
    }

    private static async Task<int> HandleBurnIsoAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        if (HasMultiDriveOption(cmd))
        {
            return await HandleBurnIsoMultiDriveAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false);
        }

        string driveId = ResolveTargetDriveId(cmd);
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso>");
        string? speedStr = cmd.GetOption("speed");
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine, hasDirectDefault: true);
        bool strictVerify = cmd.HasFlag("strict-verify"); // every read forced from the disc; implies --verify
        // Read back by default (--no-verify skips it; --verify is still accepted). A test write records nothing to read,
        // and a direct burn is always read back.
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null));
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        DvdBookType? requestedBookType = ParseOptionalBookType(cmd);
        bool requireBookType = cmd.HasFlag("require-book-type");
        var postAction = ParsePostBurnAction(cmd);

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var mediaStatus = recorder.QueryMediaStatus();
        nativeEngine = ApplyDirectWritingDefault(cmd, nativeEngine, burnEngine, mediaStatus, stdout);
        bool streamingWrite = ParseStreamingWrite(cmd, nativeEngine);
        verify |= nativeEngine != null && !testWrite; // a direct burn is always read back

        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType);

        var burnOptions = new DataBurnOptions
        {
            Speed = speedDesc,
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite,
            LayerBreakSectors = ParseLayerBreak(cmd, nativeEngine, mediaStatus),
            DisableBufferUnderrunFree = disableBufe,
            StreamingWrite = streamingWrite,
            RequestedBookType = requestedBookType,
            RequireBookType = requireBookType
        };

        string? telemetryOut = ParseTelemetryOutPath(cmd);
        var burnService = CreateDataBurnService(nativeEngine ?? burnEngine);
        var telemetry = CreateTelemetrySeries(telemetryOut, innerProgress: null, $"burn-iso ({Path.GetFileName(isoPath)})", recorder, mediaStatus.MediaType);
        DataBurnResult result;
        if (LegacyDiscImageConverter.IsLegacyImageFile(isoPath))
        {
            // .bin / .nrg / .mdf / .mds / .ccd / .img / .cdi: stream the 2048-byte user data directly (no temp .iso, no 2 GB limit).
            await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(isoPath);
            result = await burnService.BurnStreamAsync(recorder, legacyStream, burnOptions, telemetry).ConfigureAwait(false);
        }
        else
        {
            result = await burnService.BurnIsoFileAsync(recorder, isoPath, burnOptions, telemetry).ConfigureAwait(false);
        }

        IReadOnlyList<string> telemetryPaths = SaveTelemetryIfRequested(telemetryOut, telemetry);
        ExecutePostBurnActionIfSucceeded(postAction, result.Success, recorder);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                isoPath = Path.GetFullPath(isoPath),
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                writesAccepted = result.WritesAccepted,
                sessionClosed = result.SessionClosed,
                tocChecked = result.TocChecked,
                filesCompared = result.FilesCompared,
                pictureAreaCompared = result.HasPictureZone ? result.PictureAreaCompared : (bool?)null,
                verified = result.Verified,
                verificationNote = result.VerificationNote,
                bookTypeNote = result.BookTypeNote,
                warning = result.Warning,
                directWrite = NativeDataJson(nativeEngine),
                telemetryOut = FormatTelemetryJsonValue(telemetryPaths),
                telemetryPoints = telemetry?.Count,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            stdout.WriteLine($"  Average Speed:  {result.AverageSpeedMultiplier:F1}x");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "Passed" : "No")}");
            if (result.VerificationNote != null) stdout.WriteLine($"  Verify Note:    {result.VerificationNote}");
            if (result.BookTypeNote != null) stdout.WriteLine($"  Book Type:      {result.BookTypeNote}");
            WriteNativeDataSummary(stdout, nativeEngine);
            WriteTelemetryOutSummary(stdout, telemetryPaths);
            return 0;
        }

        stderr.WriteLine($"Burn failed: {result.ErrorMessage}");
        WriteNativeDataSummary(stderr, nativeEngine);
        WriteTelemetryOutSummary(stderr, telemetryPaths);
        return 1;
    }

    private static async Task<int> HandleBurnDataAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        string inputFolder = cmd.GetRequiredOption("input", "--input <folder>");
        string? volumeLabel = cmd.GetOption("label");
        string? speedStr = cmd.GetOption("speed");
        string? bootBiosPath = cmd.GetOption("boot-bios");
        string? bootUefiPath = cmd.GetOption("boot-uefi");
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine, hasDirectDefault: true);
        bool strictVerify = cmd.HasFlag("strict-verify"); // every read forced from the disc; implies --verify
        // Read back by default (--no-verify skips it; --verify is still accepted). A test write records nothing to read,
        // and a direct burn is always read back.
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null));
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");
        bool explicitAppend = cmd.HasFlag("append");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        DvdBookType? requestedBookType = ParseOptionalBookType(cmd);
        bool requireBookType = cmd.HasFlag("require-book-type");
        var postAction = ParsePostBurnAction(cmd);
        string? fillPicture = ParseFillPicture(cmd, autoAllowed: false); // off unless --fill-picture is given

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        bool hasUdfFlag = cmd.HasFlag("udf") || cmd.GetOption("udf-rev") != null;
        bool hasJolietFlag = cmd.HasFlag("joliet");
        bool includeUdf = true;
        bool includeJoliet = true;

        if (hasUdfFlag || hasJolietFlag)
        {
            includeUdf = hasUdfFlag;
            includeJoliet = hasJolietFlag;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var windowsStatus = recorder.QueryMediaStatus();
        nativeEngine = ApplyDirectWritingDefault(cmd, nativeEngine, burnEngine, windowsStatus, stdout);
        bool streamingWrite = ParseStreamingWrite(cmd, nativeEngine);
        verify |= nativeEngine != null && !testWrite; // a direct burn is always read back
        // As the writer that will burn sees the disc: direct writing finds an open DVD-RW that Windows calls overwrite-only.
        var mediaStatus = MediaStatusForWriting.Describe(nativeEngine ?? burnEngine, recorder, windowsStatus);
        // A filled disc is written from the start in one pass and closed: refused for append, --no-close and used discs
        // before anything is read or drawn.
        var fill = fillPicture is null ? ((DiscArtMedium Medium, CalibrationParameters Disc, string Source)?)null
            : PrepareFill(cmd, mediaStatus, closeDisc, explicitAppend);
        if (explicitAppend)
        {
            if (!mediaStatus.MediaPresent)
            {
                throw new CommandLineException("No disc is inserted in the drive to append a session to.");
            }
            if (mediaStatus.IsBlank)
            {
                throw new CommandLineException("The inserted disc is blank; omit --append to write the first session (use --no-close to keep it open for later --append).");
            }
            if (!mediaStatus.IsAppendable || mediaStatus.IsOverwriteOnly)
            {
                throw new CommandLineException($"Cannot append a session to {mediaStatus.MediaType.GetDisplayName()}: the disc is closed or overwrite-only.");
            }
        }
        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType);

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "DATA_DISC";
        var bootConfig = BuildBootConfiguration(bootBiosPath, bootUefiPath, effectiveLabel);

        var root = CompilationDirectory.CreateRoot();
        if (fill is null && !mediaStatus.IsBlank && mediaStatus.IsAppendable && !mediaStatus.IsOverwriteOnly)
        {
            try
            {
                root = await MultiSessionManager.ImportFromRecorderAsync(recorder, nativeEngine ?? burnEngine).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !explicitAppend)
            {
                stderr.WriteLine($"Warning: Could not import previous session ({ex.Message}); writing new session without merging.");
            }
        }

        PopulateDirectoryFromDisk(root, inputFolder);

        // Leaving a disc open for later sessions requires omitting UDF 1.02 (unless explicitly requested with --udf or
        // required by a >= 4 GiB file), because Windows mounts UDF 1.02 at fixed LBA 256 and hides later Joliet sessions.
        if (!closeDisc && !hasUdfFlag && !root.GetAllFilesRecursive().Any(f => f.SizeBytes > uint.MaxValue))
        {
            includeUdf = false;
        }

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = effectiveLabel,
            Speed = speedDesc,
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite,
            LayerBreakSectors = ParseLayerBreak(cmd, nativeEngine, mediaStatus),
            DisableBufferUnderrunFree = disableBufe,
            StreamingWrite = streamingWrite,
            RequestedBookType = requestedBookType,
            RequireBookType = requireBookType
        };

        var buildOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            UdfRevision: ResolveUdfRevision(cmd),
            BootConfiguration: bootConfig,
            FixedTimestampUtc: ResolveFixedTimestamp(cmd),
            WriteSha256Manifest: cmd.HasFlag("sha256sums"),
            WriteBlake3Manifest: cmd.HasFlag("blake3sums"));

        var phases = new PhaseTimingProgress(cmd.IsJson ? null : stdout);
        string? telemetryOut = ParseTelemetryOutPath(cmd);
        var telemetry = CreateTelemetrySeries(telemetryOut, phases, $"burn-data ({effectiveLabel})", recorder, mediaStatus.MediaType);
        IProgress<BurnProgressSnapshot> progress = (IProgress<BurnProgressSnapshot>?)telemetry ?? phases;
        var burnService = CreateDataBurnService(nativeEngine ?? burnEngine);
        DataBurnResult result;
        FillOutcome? fillOutcome = null;
        if (fill is { } f)
        {
            (result, fillOutcome) = await BurnFilledAsync(burnService, recorder, root, buildOptions, burnOptions, fillPicture!,
                f.Medium, f.Disc, f.Source, cmd.IsJson ? null : stderr, progress).ConfigureAwait(false);
        }
        else
        {
            result = await burnService.BurnCompilationAsync(recorder, root, burnOptions, buildOptions, progress).ConfigureAwait(false);
        }
        phases.Complete();
        IReadOnlyList<string> telemetryPaths = SaveTelemetryIfRequested(telemetryOut, telemetry);
        ExecutePostBurnActionIfSucceeded(postAction, result.Success, recorder);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                inputFolder = Path.GetFullPath(inputFolder),
                volumeLabel = effectiveLabel,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                writesAccepted = result.WritesAccepted,
                sessionClosed = result.SessionClosed,
                tocChecked = result.TocChecked,
                filesCompared = result.FilesCompared,
                pictureAreaCompared = result.HasPictureZone ? result.PictureAreaCompared : (bool?)null,
                verified = result.Verified,
                verificationNote = result.VerificationNote,
                bookTypeNote = result.BookTypeNote,
                warning = result.Warning,
                fillPicture = FillJson(fillOutcome, null),
                phases = phases.Phases.Select(p => new { phase = p.Name, seconds = Math.Round(p.Duration.TotalSeconds, 1) }),
                directWrite = NativeDataJson(nativeEngine),
                telemetryOut = FormatTelemetryJsonValue(telemetryPaths),
                telemetryPoints = telemetry?.Count,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Data burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {effectiveLabel}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "Passed" : "No")}");
            if (result.VerificationNote != null) stdout.WriteLine($"  Verify Note:    {result.VerificationNote}");
            if (result.BookTypeNote != null) stdout.WriteLine($"  Book Type:      {result.BookTypeNote}");
            if (result.Warning != null) stdout.WriteLine($"  Warning:        {result.Warning}");
            WriteFillSummary(stdout, fillOutcome, null);
            WriteNativeDataSummary(stdout, nativeEngine);
            WriteTelemetryOutSummary(stdout, telemetryPaths);
            phases.WriteSummary(stdout);
            return 0;
        }

        stderr.WriteLine($"Data burn failed: {result.ErrorMessage}");
        WriteNativeDataSummary(stderr, nativeEngine);
        WriteTelemetryOutSummary(stderr, telemetryPaths);
        phases.WriteSummary(stderr);
        return 1;
    }

    /// <summary>
    /// <c>--native [--test-write | --test-first] [--log-dir]</c> for data burns: the direct writer, with the given
    /// engine kept for discs it does not cover yet, reading and erasing. Null without <c>--native</c>.
    /// </summary>
    /// <param name="hasDirectDefault">True for commands where <see cref="ApplyDirectWritingDefault"/> may pick the direct
    /// writer without <c>--native</c> (burn-data, burn-iso); there <c>--log-dir</c> is accepted on its own.</param>
    private static DataBurnService CreateDataBurnService(IBurnEngineBackend engine) =>
        new(engine, new BookTypeService(new CdBurner.Hardware.Scsi.VendorBookTypeController()));

    private static DvdBookType? ParseOptionalBookType(CommandLineArgs cmd)
    {
        if (!cmd.Options.TryGetValue("book-type", out string? raw))
        {
            if (cmd.HasFlag("require-book-type"))
            {
                throw new CommandLineException("--require-book-type needs --book-type <dvd-rom|dvd+r|dvd+rw|dvd+r-dl>.");
            }
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new CommandLineException("--book-type requires a value (e.g. --book-type dvd-rom).");
        }

        return ParseDvdBookType(raw);
    }

    private static (NativeDataBurnEngine? Engine, bool TestWrite) ResolveNativeDataEngine(CommandLineArgs cmd, IBurnEngineBackend fallback,
        bool hasDirectDefault = false)
    {
        bool native = cmd.HasFlag("native");
        bool testWrite = cmd.HasFlag("test-write");
        bool testFirst = cmd.HasFlag("test-first");
        if ((testWrite || testFirst) && !native)
        {
            throw new CommandLineException("--test-write and --test-first need --native.");
        }
        if (cmd.GetOption("log-dir") != null && !native && !hasDirectDefault)
        {
            throw new CommandLineException("--log-dir needs --native.");
        }
        if (testFirst && testWrite)
        {
            throw new CommandLineException("--test-first only applies to a real --native burn (without --test-write).");
        }
        return native
            ? (new NativeDataBurnEngine(fallback, cmd.GetOption("log-dir")) { TestWriteFirst = testFirst }, testWrite)
            : (null, false);
    }

    /// <summary>
    /// The writer for the disc in the drive: <paramref name="chosen"/> when <c>--native</c> was given; otherwise direct
    /// drive commands for a disc they suit better (a DVD-RW, <see cref="DirectWritingDefault"/>) unless
    /// <c>--windows-writer</c>; otherwise null, the Windows writer. Says so on <paramref name="note"/> when the default
    /// picked the direct writer.
    /// </summary>
    private static NativeDataBurnEngine? ApplyDirectWritingDefault(CommandLineArgs cmd, NativeDataBurnEngine? chosen, IBurnEngineBackend fallback,
        MediaStatusDto status, TextWriter note)
    {
        bool windowsWriter = cmd.HasFlag("windows-writer");
        if (chosen != null)
        {
            if (windowsWriter)
            {
                throw new CommandLineException("--native and --windows-writer cannot be used together.");
            }
            return chosen;
        }
        if (windowsWriter || !DirectWritingDefault.Prefers(status))
        {
            return null;
        }
        if (!cmd.IsJson)
        {
            note.WriteLine(DirectWritingDefault.Note + " (--windows-writer uses Windows' writer.)");
        }
        return new NativeDataBurnEngine(fallback, cmd.GetOption("log-dir"));
    }

    /// <summary>
    /// <c>--layer-break &lt;sectors&gt;</c>: how many sectors go on the first layer of a dual-layer DVD (a positive multiple
    /// of 16). Only the direct writer sets the break, so it needs <c>--native</c> and a dual-layer disc in the drive; the
    /// writer itself then checks it against the image and the layer's capacity before the first WRITE. Null when not given.
    /// </summary>
    private static int? ParseLayerBreak(CommandLineArgs cmd, NativeDataBurnEngine? nativeEngine, MediaStatusDto mediaStatus)
    {
        string? value = cmd.GetOption("layer-break");
        if (value == null)
        {
            return null;
        }
        if (nativeEngine == null)
        {
            throw new CommandLineException("--layer-break needs --native: only the direct writer sets the layer break.");
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int sectors) || sectors <= 0 ||
            sectors % OpticalConstants.DvdEccBlockSectors != 0)
        {
            throw new CommandLineException($"--layer-break takes the number of sectors on the first layer, a positive multiple of 16; '{value}' is not.");
        }
        if (mediaStatus.MediaType is not (DiscMediaType.DvdPlusRDualLayer or DiscMediaType.DvdMinusRDualLayer))
        {
            throw new CommandLineException($"--layer-break needs a blank dual-layer DVD+R or DVD-R in the drive; it holds {mediaStatus.MediaType}. Nothing was written.");
        }
        return sectors;
    }

    private static bool ParseStreamingWrite(CommandLineArgs cmd, NativeDataBurnEngine? nativeEngine)
    {
        if (!cmd.HasFlag("streaming"))
        {
            return false;
        }
        if (cmd.HasFlag("windows-writer"))
        {
            throw new CommandLineException("--streaming cannot be used with --windows-writer: only the direct writer sends streaming WRITE(12) commands.");
        }
        if (nativeEngine == null)
        {
            throw new CommandLineException("--streaming needs --native: only the direct writer sends streaming WRITE(12) commands.");
        }
        return true;
    }

    private static object? NativeDataJson(NativeDataBurnEngine? engine)
    {
        if (engine == null)
        {
            return null;
        }
        var r = engine.LastResult;
        return new
        {
            writer = engine.LastFallbackReason != null ? "windows" : "direct",
            fallbackReason = engine.LastFallbackReason,
            testWritePassedFirst = engine.LastTestWritePassed,
            writeMode = r?.WriteMode,
            streamingWrite = r?.StreamingWriteUsed,
            speedSetKiloBytesPerSecond = r?.SpeedSetKiloBytesPerSecond,
            driveSpeedsKiloBytesPerSecond = r?.DriveSpeedsKiloBytesPerSecond,
            dataSectors = r?.DataSectorsWritten,
            layer0Sectors = r?.Layer0Sectors,
            paddingSectors = r?.PaddingSectors,
            writingSeconds = r == null ? (double?)null : Math.Round(r.WritingTime.TotalSeconds, 1),
            writerSeconds = r == null ? (double?)null : Math.Round(r.Elapsed.TotalSeconds, 1),
            writerMessage = r?.Message,
            commandLog = engine.LastLogPath
        };
    }

    private static void WriteNativeDataSummary(TextWriter output, NativeDataBurnEngine? engine)
    {
        if (engine == null)
        {
            return;
        }
        if (engine.LastFallbackReason != null)
        {
            output.WriteLine($"  Writer:         {engine.LastFallbackReason}");
            return;
        }
        var r = engine.LastResult;
        output.WriteLine($"  Writer:         direct drive commands ({r?.WriteMode ?? "not started"})");
        if (engine.TestWriteFirst) output.WriteLine($"  Test write:     {(engine.LastTestWritePassed ? "passed first" : "not passed")}");
        if (r != null)
        {
            double oneX = r.OneXKiloBytesPerSecond;
            string speed = r.SpeedSetKiloBytesPerSecond is int kb ? $"{kb} kB/s ({kb / oneX:0.#}x)" : "drive's own";
            output.WriteLine($"  Speed set:      {speed}; drive offers {(r.DriveSpeedsKiloBytesPerSecond.Count > 0 ? string.Join(", ", r.DriveSpeedsKiloBytesPerSecond.Select(k => $"{k / oneX:0.#}x")) : "no list")}");
            output.WriteLine($"  Writing time:   {r.WritingTime.TotalSeconds:F0} s for the data ({r.AverageWriteMultiplier:F1}x average), {r.Elapsed.TotalSeconds:F0} s with closing");
            output.WriteLine($"  Drive reports:  {r.Message}");
        }
        if (engine.LastLogPath != null) output.WriteLine($"  Command log:    {engine.LastLogPath}");
    }

    /// <summary>
    /// Records how long each burn phase (IMAPI write action or service step) took, printing each phase as it starts.
    /// Implemented synchronously rather than with <see cref="Progress{T}"/>, so reports are never reordered.
    /// </summary>
    private sealed class PhaseTimingProgress : IProgress<BurnProgressSnapshot>
    {
        private readonly TextWriter? _live;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<(string Name, TimeSpan Start)> _starts = new();
        private readonly object _lock = new();
        private TimeSpan? _end;

        public PhaseTimingProgress(TextWriter? live) => _live = live;

        public void Report(BurnProgressSnapshot value)
        {
            string name = string.IsNullOrWhiteSpace(value.CurrentActionDescription) ? "Working" : value.CurrentActionDescription;
            lock (_lock)
            {
                if (_end != null || (_starts.Count > 0 && _starts[^1].Name == name)) return;
                _starts.Add((name, _clock.Elapsed));
                _live?.WriteLine($"  [{_clock.Elapsed:mm\\:ss}] {name}");
            }
        }

        public void Complete()
        {
            lock (_lock) { _end ??= _clock.Elapsed; }
        }

        public IReadOnlyList<(string Name, TimeSpan Duration)> Phases
        {
            get
            {
                lock (_lock)
                {
                    var end = _end ?? _clock.Elapsed;
                    return _starts.Select((s, i) => (s.Name, (i + 1 < _starts.Count ? _starts[i + 1].Start : end) - s.Start)).ToList();
                }
            }
        }

        public void WriteSummary(TextWriter writer)
        {
            var phases = Phases;
            if (phases.Count == 0) return;
            writer.WriteLine("  Phase timing:");
            foreach (var (name, duration) in phases)
            {
                writer.WriteLine($"    {name,-28} {duration:mm\\:ss\\.f}");
            }
        }
    }

    private static async Task<int> HandleBurnAudioAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine,
        HttpClient? customHttpClient = null)
    {
        bool planOnly = cmd.HasFlag("plan-only");
        bool dao = cmd.HasFlag("dao");
        bool native = cmd.HasFlag("native");
        bool testWrite = cmd.HasFlag("test-write");
        bool testFirst = cmd.HasFlag("test-first");
        bool recoverySession = cmd.HasFlag("recovery-session");
        string? dataFolder = cmd.GetOption("data-folder");
        string? dataLabel = cmd.GetOption("data-label");
        bool blueBook = cmd.HasFlag("blue-book");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        var postAction = ParsePostBurnAction(cmd);
        string? speedStr = cmd.GetOption("speed");
        var overburn = ParseOverburnPolicy(cmd, native, discAtOnce: dao);

        if (testFirst && (!native || testWrite))
        {
            throw new CommandLineException("--test-first only applies to a real --native burn (without --test-write).");
        }
        if (recoverySession && !native)
        {
            throw new CommandLineException("--recovery-session requires --native (direct drive commands); Windows IMAPIv2 cannot leave an SAO audio session open for a second data session.");
        }
        if (!string.IsNullOrWhiteSpace(dataFolder) && !native)
        {
            throw new CommandLineException("--data-folder requires --native (direct drive commands); Windows IMAPIv2 cannot leave an SAO audio session open for a second data session.");
        }
        if (!string.IsNullOrWhiteSpace(dataFolder) && recoverySession)
        {
            throw new CommandLineException("--data-folder cannot be combined with --recovery-session.");
        }
        if (blueBook && string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new CommandLineException("--blue-book lays out the --data-folder session as an Enhanced CD; give --data-folder too.");
        }
        bool fillPicture = cmd.Options.ContainsKey("fill-picture") || cmd.HasFlag("fill-picture");
        if (fillPicture && !native)
        {
            throw new CommandLineException("--fill-picture requires --native (direct drive commands): the picture is a second session after the music, and Windows IMAPIv2 cannot leave the audio session open for it.");
        }
        if (fillPicture && (!string.IsNullOrWhiteSpace(dataFolder) || recoverySession))
        {
            throw new CommandLineException("--fill-picture fills the rest of the disc, so it cannot be combined with --data-folder or --recovery-session.");
        }

        using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var (compilation, loadNote) = BuildAudioCompilation(cmd, mfDecoder, stderr);

        if (planOnly)
        {
            if (loadNote != null && !cmd.IsJson)
            {
                stderr.WriteLine(loadNote);
            }
            return WriteAudioPlanOutput(compilation, 80, cmd.IsJson, stdout);
        }

        string driveId = ResolveTargetDriveId(cmd);
        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is VirtualDiscRecorder vRec && !vRec.QueryMediaStatus().MediaPresent)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        }
        var mediaStatus = recorder.QueryMediaStatus();
        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType, isAudio: true);

        if (loadNote != null && !cmd.IsJson)
        {
            stderr.WriteLine(loadNote);
        }

        var (pictureSession, pictureNote) = await PrepareAudioFillPictureAsync(
                cmd, compilation, recorder, mediaStatus, mfDecoder, customHttpClient, dataLabel)
            .ConfigureAwait(false);
        if (pictureNote != null && !cmd.IsJson)
        {
            stderr.WriteLine(pictureNote);
        }

        string? logDir = cmd.GetOption("log-dir");
        string? telemetryOut = ParseTelemetryOutPath(cmd);
        var telemetry = CreateTelemetrySeries(telemetryOut, innerProgress: null, $"burn-audio ({compilation.AlbumTitle})", recorder, mediaStatus.MediaType);
        var nativeEngine = native ? new NativeAudioBurnEngine(logDir) { TestWriteFirst = testFirst } : null;
        var nativeDataEngine = native ? new NativeDataBurnEngine(burnEngine, logDir) : null;
        var audioBurnService = new AudioCdBurnService(nativeEngine ?? burnEngine, mfDecoder, nativeDataEngine);

        var result = await audioBurnService.BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions
            {
                Speed = speedDesc,
                UseDiscAtOnce = dao || compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless,
                WriteCdText = compilation.IncludeCdText,
                PerformNormalization = compilation.NormalizeAllTracks,
                PreGapFrames = compilation.DefaultPreGapFrames,
                MediaCatalogNumber = compilation.UpcEan,
                Simulate = testWrite,
                KeepIndexMarks = native, // only the direct writer can place INDEX 00 marks and hidden audio
                AllowDamagedAudio = cmd.HasFlag("allow-damaged"),
                WriteOffsetSamples = ParseWriteOffset(cmd.GetOption("write-offset"), native),
                IncludeRecoverySession = recoverySession,
                DataSessionFolderPath = dataFolder,
                DataSessionVolumeLabel = dataLabel,
                BlueBookLayout = blueBook,
                SessionTwoImage = pictureSession,
                DisableBufferUnderrunFree = disableBufe,
                Overburn = overburn
            },
            progress: telemetry).ConfigureAwait(false);

        IReadOnlyList<string> telemetryPaths = SaveTelemetryIfRequested(telemetryOut, telemetry);
        ExecutePostBurnActionIfSucceeded(postAction, result.Success, recorder);

        var verification = nativeEngine?.LastResult?.Verification;
        var recoveryManifest = audioBurnService.LastRecoveryManifest;
        bool? audioVerified = verification == null
            ? null
            : (verification.Passed && (!verification.HasPictureArea || verification.AudioCompared || verification.DataTrackCompared));
        bool? fillPictureAreaCompared = pictureSession?.StartLba != null && audioBurnService.LastDataSessionSectors > 0
            ? false
            : (bool?)null;

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                testWrite,
                native,
                recoverySession,
                recoverySessionWritten = recoveryManifest != null,
                dataSessionFolder = dataFolder,
                dataSessionBlueBook = blueBook,
                dataSessionWritten = audioBurnService.LastDataSessionSectors > 0,
                dataSessionStartLba = audioBurnService.LastDataSessionStartLba,
                dataSessionSectors = audioBurnService.LastDataSessionSectors,
                fillPicture = pictureSession?.StartLba is int pictureSessionStart && audioBurnService.LastDataSessionSectors > 0
                    ? new
                    {
                        source = pictureSession.Picture.Source,
                        origin = pictureSession.Picture.Origin,
                        discFrom = pictureSession.DiscFrom,
                        sessionStartLba = pictureSessionStart,
                        volumeSectors = pictureSession.VolumeSectors,
                        pictureStartLba = pictureSession.PictureStartLba,
                        pictureEndLba = pictureSession.PictureEndLba
                    }
                    : null,
                fillPictureNote = pictureNote,
                warning = result.WarningMessage,
                recoverySessionDetails = recoveryManifest == null ? null : new
                {
                    pcmBytes = recoveryManifest.TotalPcmBytes,
                    pcmSectors = recoveryManifest.TotalPcmSectors,
                    par2BlockSizeBytes = recoveryManifest.Par2BlockSizeBytes,
                    par2RecoveryBlocks = recoveryManifest.Par2RecoveryBlocks,
                    tolerance = recoveryManifest.ToleranceSummary,
                    warning = AudioRecoverySessionService.CarHeadUnitWarning
                },
                commandLog = nativeEngine?.LastLogPath,
                discStillBlank = nativeEngine?.LastResult?.DiscStillBlankAfterTestWrite,
                testWritePassedFirst = nativeEngine?.LastTestWritePassed,
                discRecorded = nativeEngine?.LastResult?.DiscRecorded,
                writesAccepted = nativeEngine != null ? nativeEngine.LastResult?.DiscRecorded : (bool?)(result.Success && !testWrite),
                sessionClosed = nativeEngine != null ? (nativeEngine.LastResult is { DiscRecorded: true, DiscLeftOpen: not false, DiscClosed: not false }) : (bool?)(result.Success && !testWrite),
                tocChecked = verification?.TocChecked,
                cdTextChecked = verification?.CdTextChecked,
                audioCompared = verification?.AudioCompared,
                dataTrackCompared = verification?.DataTrackCompared,
                pictureAreaCompared = verification is { HasPictureArea: true } ? verification.PictureAreaCompared : fillPictureAreaCompared,
                verified = audioVerified,
                verifyMessage = verification?.Message,
                readBackOffsetSamples = verification?.OffsetSamples,
                samplesCompared = verification?.SamplesCompared,
                hardToReadSectors = verification?.HardToReadSectors,
                writtenWrongSectors = verification?.WrittenWrongSectors,
                unreadableSectors = verification?.UnreadableSectors,
                verifyWarning = verification?.Warning,
                damagedAudioFiles = result.DamagedAudioFiles,
                writeOffset = nativeEngine?.LastWriteOffsetPlan is { } plan
                    ? new
                    {
                        readOffsetSamples = plan.ReadOffset,
                        readOffsetSource = plan.ReadOffsetSource,
                        combinedOffsetSamples = plan.CombinedOffset,
                        writeOffsetSamples = plan.WriteOffset,
                        shiftSamples = plan.ShiftSamples,
                        note = plan.Explanation
                    }
                    : null,
                driveId = recorder.UniqueId,
                title = compilation.AlbumTitle,
                artist = compilation.AlbumPerformer,
                trackCount = compilation.TrackCount,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                telemetryOut = FormatTelemetryJsonValue(telemetryPaths),
                telemetryPoints = telemetry?.Count,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success && testWrite)
        {
            stdout.WriteLine("Audio CD test write completed (laser off; the disc was not written):");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Tracks:         {compilation.TrackCount}");
            stdout.WriteLine($"  CD-Text:        {(compilation.IncludeCdText ? "yes" : "no")}");
            stdout.WriteLine($"  Sectors sent:   {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            if (nativeEngine?.LastLogPath != null)
            {
                stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
            }
            WriteTelemetryOutSummary(stdout, telemetryPaths);
            return 0;
        }

        if (result.Success)
        {
            stdout.WriteLine("Audio CD burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Album Title:    {compilation.AlbumTitle}");
            stdout.WriteLine($"  Artist:         {compilation.AlbumPerformer}");
            stdout.WriteLine($"  Tracks Burned:  {compilation.TrackCount}");
            stdout.WriteLine($"  Total Sectors:  {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            if (nativeEngine != null)
            {
                stdout.WriteLine("  Writer:         direct drive commands (Session-at-Once)");
                stdout.WriteLine($"  Test write:     {(nativeEngine.LastTestWritePassed ? "passed first" : "not run")}");
                stdout.WriteLine($"  Read-back:      {verification?.Message ?? "not run"}");
                if (verification?.Warning is { } readWarning)
                {
                    stderr.WriteLine($"Warning: {readWarning}");
                }
                if (recoveryManifest != null)
                {
                    stdout.WriteLine($"  Recovery Session: session 2 written ({recoveryManifest.Par2RecoveryBlocks:N0} PAR2 blocks, {recoveryManifest.Par2BlockSizeBytes / 1024} KiB block size)");
                    stdout.WriteLine($"  Tolerance:      {recoveryManifest.ToleranceSummary}");
                    stderr.WriteLine($"Note: {AudioRecoverySessionService.CarHeadUnitWarning}");
                }
                if (audioBurnService.LastDataSessionSectors > 0)
                {
                    stdout.WriteLine($"  Data Session:   session 2 written at LBA {audioBurnService.LastDataSessionStartLba:N0} ({audioBurnService.LastDataSessionSectors:N0} sectors)");
                    if (pictureSession?.StartLba != null)
                    {
                        stdout.WriteLine($"  Picture:        drawn on LBA {pictureSession.PictureStartLba:N0} to {pictureSession.PictureEndLba - 1:N0} ({pictureSession.Picture.Source}: {pictureSession.Picture.Origin}, {pictureSession.DiscFrom})");
                    }
                    if (!string.IsNullOrWhiteSpace(result.WarningMessage))
                    {
                        stderr.WriteLine($"Note: {result.WarningMessage}");
                    }
                }
                if (nativeEngine.LastLogPath != null)
                {
                    stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
                }
            }
            WriteTelemetryOutSummary(stdout, telemetryPaths);
            return 0;
        }

        stderr.WriteLine($"Audio burn failed: {result.ErrorMessage}");
        if (result.DamagedAudioFiles is { Count: > 0 })
        {
            stderr.WriteLine("To burn anyway, run the same command with --allow-damaged.");
        }
        WriteTelemetryOutSummary(stderr, telemetryPaths);
        return 1;
    }

    /// <summary>
    /// check-disc: reads every sector of the CD in the drive once with C2 error pointers and says where it is weak (read-only).
    /// </summary>
    /// <summary>
    /// <c>copy-disc</c>: the app's disc copy (<see cref="DiscCopyService"/>). Messages for the person at the drive go to
    /// stderr, so <c>--json</c> output stays one object on stdout.
    /// </summary>
    private static async Task<int> HandleCopyDiscAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr,
        IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        var source = FindRecorder(deviceService, cmd.GetRequiredOption("source", "--source <id>"));
        var target = cmd.GetOption("target") is { } targetId ? FindRecorder(deviceService, targetId) : source;
        bool singleDrive = string.Equals(source.UniqueId, target.UniqueId, StringComparison.OrdinalIgnoreCase);
        int copies = int.TryParse(cmd.GetOption("copies") ?? "1", out int n) && n is >= 1 and <= 99
            ? n : throw new CommandLineException("--copies takes a number from 1 to 99.");
        int waitMinutes = int.TryParse(cmd.GetOption("wait-minutes") ?? "30", out int w) && w is >= 1 and <= 1440
            ? w : throw new CommandLineException("--wait-minutes takes a number from 1 to 1440.");
        bool native = cmd.HasFlag("native");
        if (cmd.GetOption("log-dir") != null && !native)
        {
            throw new CommandLineException("--log-dir needs --native.");
        }
        string? keep = cmd.GetOption("keep-image");
        if (keep != null && string.IsNullOrWhiteSpace(keep))
        {
            throw new CommandLineException("--keep-image needs a folder: --keep-image <folder>");
        }

        var sourceStatus = source.QueryMediaStatus();
        if (!sourceStatus.MediaPresent || sourceStatus.IsBlank)
        {
            throw new CommandLineException($"There is no recorded disc to copy in {source.VolumePath ?? source.UniqueId}.");
        }
        bool isAudio = DiscCopyService.IsSourceAudioCd(source);
        var speed = ParseSpeed(cmd.GetOption("speed"), target, sourceStatus.MediaType, isAudio);

        async Task<bool> PrepareTarget(int copyIndex, CancellationToken ct)
        {
            if (target is VirtualDiscRecorder virtualTarget)
            {
                var status = virtualTarget.QueryMediaStatus();
                if (singleDrive || copyIndex > 1 || !status.MediaPresent || !status.IsBlank)
                {
                    virtualTarget.InsertMedia(VirtualDiscMedium.CreateBlankFor(sourceStatus));
                }
                return true;
            }
            if (!singleDrive && copyIndex == 1)
            {
                return true; // the blank disc already in the second drive
            }

            target.EjectMedia();
            stderr.WriteLine($"Copy {copyIndex} of {copies}: put a blank disc in {target.VolumePath ?? target.UniqueId} and close the tray"
                + $" (waiting up to {waitMinutes} min).");
            var deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var status = target.QueryMediaStatus();
                    if (status.MediaPresent && status.IsBlank)
                    {
                        stderr.WriteLine($"Blank {status.MediaType} found; copying.");
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // The drive is still loading the disc; ask again.
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            stderr.WriteLine($"No blank disc within {waitMinutes} min.");
            return false;
        }

        var nativeEngine = native ? new NativeAudioBurnEngine(cmd.GetOption("log-dir")) : null;
        var nativeDataEngine = native ? new NativeDataBurnEngine(burnEngine, cmd.GetOption("log-dir")) : null;
        using var decoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        string? stagingFolder = null;
        if (keep != null)
        {
            Directory.CreateDirectory(keep);
            stagingFolder = keep;
        }

        var result = await new DiscCopyService(burnEngine, decoder).CopyDiscAsync(source, target, new DiscCopyOptions
        {
            CopyCount = copies,
            RawCloneMode = cmd.HasFlag("raw"),
            WriteSpeed = speed,
            KeepStagedImage = keep != null,
            CustomStagingImagePath = stagingFolder != null ? Path.Combine(stagingFolder, isAudio ? "album.cue" : cmd.HasFlag("raw") ? "staged_disc.bin" : "staged_disc.iso") : null,
            MixedModeWriter = nativeEngine,
            DataSessionWriter = nativeDataEngine,
            AllowSuspectAudio = cmd.HasFlag("allow-suspect"),
            OnPrepareTargetDiscAsync = PrepareTarget
        }).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                error = result.ErrorMessage,
                warning = result.WarningMessage,
                copiesRequested = result.CopiesRequested,
                copiesCompleted = result.CopiesCompleted,
                audioCd = result.WasAudioCd,
                mixedMode = result.IsMixedMode,
                copiedDataTrack = result.CopiedDataTrack,
                enhancedCd = result.IsEnhancedCd,
                copiedDataSession = result.CopiedDataSession,
                sectorsPerDisc = result.SectorsPerDisc,
                totalSectorsWritten = result.TotalSectorsWritten,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                stagedImage = result.StagedImagePath,
                verified = nativeEngine?.LastResult?.Verification?.Passed,
                verifyMessage = nativeEngine?.LastResult?.Verification?.Message,
                commandLog = nativeEngine?.LastLogPath
            });
        }
        else if (result.Success)
        {
            stdout.WriteLine($"Copied: {result.CopiesCompleted} of {result.CopiesRequested} disc(s), {result.SectorsPerDisc:N0} sectors each, in {result.TotalTime.TotalSeconds:N0} s.");
            if (result.CopiedDataTrack) stdout.WriteLine("  The mixed-mode CD's data track was copied with the audio.");
            if (result.CopiedDataSession) stdout.WriteLine("  The Enhanced CD's audio and data sessions were both copied.");
            if (nativeEngine?.LastResult?.Verification is { } v) stdout.WriteLine($"  {v.Message}");
            if (result.WarningMessage != null) stdout.WriteLine($"  Warning: {result.WarningMessage}");
        }
        else
        {
            stderr.WriteLine($"Copy failed: {result.ErrorMessage}");
        }
        return result.Success ? 0 : 1;
    }

    private static int HandleCheckDisc(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        DiscHealthCertificate? certificate = null;
        if (cmd.GetOption("compare") is { } certificatePath)
        {
            try
            {
                certificate = DiscHealthCertificate.Load(certificatePath);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                throw new CommandLineException($"--compare: {ex.Message}");
            }
            if (certificate.Scan == null)
            {
                throw new CommandLineException("--compare: that certificate has no check-disc counts (the disc could not be read after its burn).");
            }
        }
        var recorder = FindRecorder(deviceService, driveId);
        if (recorder.IsVirtual || string.IsNullOrWhiteSpace(recorder.VolumePath))
        {
            throw new CommandLineException("check-disc reads a real disc: choose a physical drive.");
        }

        DiscHealthReport report;
        using (var transport = Win32ScsiTransport.Open(recorder.VolumePath, FileAccess.ReadWrite))
        {
            int lastPercent = -1;
            report = new DiscHealthScanner(transport).Scan((done, total) =>
            {
                int percent = (int)(100L * done / Math.Max(1, total));
                if (!cmd.IsJson && percent / 10 != lastPercent / 10)
                {
                    stdout.WriteLine($"  Reading: {percent}%");
                }
                lastPercent = percent;
            });
        }

        var comparison = certificate?.Compare(report);
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                driveId = recorder.UniqueId,
                comparison = comparison == null ? null : new
                {
                    verdictBefore = comparison.VerdictBefore,
                    verdictAfter = comparison.VerdictAfter,
                    problemsBefore = comparison.ProblemsBefore,
                    problemsAfter = comparison.ProblemsAfter,
                    sameLength = comparison.SameLength,
                    worse = comparison.Worse,
                    changes = comparison.Changes.Select(c => new { minute = c.Minute, before = c.Before, after = c.After })
                },
                verdict = report.Verdict.ToString(),
                summary = report.Describe(),
                sectorsRead = report.TotalSectors,
                flaggedSectors = report.FlaggedSectors,
                unreadableSectors = report.UnreadableSectors,
                c2Available = report.C2Available,
                problemsPerMinute = report.ProblemsPerMinute,
                outerFifthShare = report.FlaggedShareInOuterFifth,
                seconds = report.Elapsed.TotalSeconds,
                tracks = report.Tracks.Select(t => new { number = t.TrackNumber, audio = t.IsAudio, startLba = t.StartLba, sectors = t.Sectors, flagged = t.FlaggedSectors, unreadable = t.UnreadableSectors })
            });
            return 0;
        }

        stdout.WriteLine();
        stdout.WriteLine(report.Describe());
        foreach (var t in report.Tracks.Where(t => t.FlaggedSectors + t.UnreadableSectors > 0))
        {
            stdout.WriteLine($"  Track {t.TrackNumber:D2}: {t.FlaggedSectors} with uncorrectable errors, {t.UnreadableSectors} unreadable (of {t.Sectors:N0})");
        }
        if (report.FlaggedSectors + report.UnreadableSectors > 0)
        {
            stdout.WriteLine("  By minute of the disc: " + string.Join(" ", report.ProblemsPerMinute.Select((n, m) => n == 0 ? "." : $"{m}:{n}")));
        }
        stdout.WriteLine($"  Took {report.Elapsed.TotalSeconds:F0} s.");
        if (comparison != null)
        {
            foreach (string line in comparison.Describe()) stdout.WriteLine(line);
        }
        return 0;
    }

    private static async Task<int> HandleBurnDvdAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        string? videoTsInput = cmd.GetOption("video-ts") ?? cmd.GetOption("input");
        if (string.IsNullOrWhiteSpace(videoTsInput))
        {
            throw new CommandLineException("Missing required option: --video-ts <folder-or-movie>");
        }
        string? volumeLabel = cmd.GetOption("label");
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine);
        var dvdPostAction = ParsePostBurnAction(cmd);
        bool dvdDisableBufe = cmd.HasFlag("no-burnfree");
        string? fillPicture = ParseFillPicture(cmd, autoAllowed: true); // off unless --fill-picture is given

        var pathTokens = videoTsInput.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allTokensExist = pathTokens.Length > 0 && pathTokens.All(p => File.Exists(p) || Directory.Exists(p));

        if (!File.Exists(videoTsInput) && !Directory.Exists(videoTsInput) && !allTokensExist)
        {
            throw new DirectoryNotFoundException($"VIDEO_TS directory or movie file does not exist: {videoTsInput}");
        }

        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            }
        }

        var analysis = DvdInputNormalizer.AnalyzeInput(videoTsInput);
        if (!analysis.CanAuthorOrBurn)
        {
            throw new InvalidOperationException($"Input cannot be authored or burned to DVD-Video: {string.Join("; ", analysis.NormalizationNotes)}");
        }

        // Checked before any conversion: a DVD-Video is always closed, so only the disc itself can refuse the fill.
        var fill = fillPicture is null ? ((DiscArtMedium Medium, CalibrationParameters Disc, string Source)?)null
            : PrepareFill(cmd, recorder.QueryMediaStatus(), closeDisc: true, append: false);
        string? fillSkipped = null;
        string? autoFrame = null;
        if (fill is not null && string.Equals(fillPicture, DiscFillPicture.AutoSource, StringComparison.Ordinal))
        {
            // The automatic picture is a frame of the main movie; without FFmpeg the disc is burned without the picture.
            string? ffmpeg = FindFillFfmpeg(cmd.GetOption("ffmpeg") is { Length: > 0 } custom ? custom : null);
            autoFrame = Path.Combine(Path.GetTempPath(), $"ignisdisc_fill_frame_{Guid.NewGuid():N}.png");
            (string? grabbed, fillSkipped) = await DiscFillPicture.GrabMovieFrameAsync(
                ffmpeg, DiscFillPicture.FindMainMovie(videoTsInput), autoFrame).ConfigureAwait(false);
            if (grabbed is null)
            {
                fill = null;
                if (!cmd.IsJson) stderr.WriteLine(fillSkipped);
            }
            else
            {
                fillPicture = grabbed;
            }
        }

        FillOutcome? fillOutcome = null;
        string sourceRootDirectory;
        string? tempAuthorDir = null;
        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : (analysis.SuggestedVolumeLabel ?? "DVD_VIDEO");
        DataBurnResult result;
        DvdCellPosition? layerBreak = null;
        string? telemetryOut = ParseTelemetryOutPath(cmd);
        BurnTelemetrySeries? telemetry = null;

        try
        {
            if (analysis.RequiresAuthoring)
            {
                tempAuthorDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_CliAuthor_" + Guid.NewGuid().ToString("N"));
                var authorService = new DvdVideoAuthoringService();
                var authorOptions = BuildDvdAuthoringOptions(cmd, effectiveLabel);

                var authResult = await authorService.AuthorAsync(
                    videoTsInput, tempAuthorDir, authorOptions,
                    cmd.IsJson ? null : new SubtitleStepProgress(stderr)).ConfigureAwait(false);
                if (!authResult.Success)
                {
                    throw new InvalidOperationException($"DVD-Video authoring failed: {authResult.ErrorMessage}");
                }

                sourceRootDirectory = authResult.OutputRootDirectory;
            }
            else
            {
                sourceRootDirectory = analysis.ExistingVideoTsPath ?? videoTsInput;
            }

            var (root, _) = CompileDvdDirectory(sourceRootDirectory, effectiveLabel);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: effectiveLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            var burnService = CreateDataBurnService(nativeEngine ?? burnEngine);
            var dvdOptions = new DataBurnOptions
            {
                VolumeLabel = effectiveLabel,
                CloseDisc = true,
                OverwriteExistingData = cmd.HasFlag("overwrite"),
                Speed = ParseSpeed(cmd.GetOption("speed"), recorder, recorder.QueryMediaStatus().MediaType),
                Simulate = testWrite,
                // Read back by default; a test write records nothing; a direct burn is always read back.
                VerifyAfterBurn = !testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null),
                DisableBufferUnderrunFree = dvdDisableBufe,
                RequestedBookType = ParseOptionalBookType(cmd),
                RequireBookType = cmd.HasFlag("require-book-type")
            };
            var dvdMedia = recorder.QueryMediaStatus().MediaType;
            telemetry = CreateTelemetrySeries(telemetryOut, innerProgress: null, $"burn-dvd ({effectiveLabel})", recorder, dvdMedia);
            if (fill is { } f)
            {
                (result, fillOutcome) = await BurnFilledAsync(burnService, recorder, root, buildOptions, dvdOptions, fillPicture!,
                    f.Medium, f.Disc, f.Source, cmd.IsJson ? null : stderr, telemetry).ConfigureAwait(false);
            }
            else if (nativeEngine != null && dvdMedia is DiscMediaType.DvdPlusRDualLayer or DiscMediaType.DvdMinusRDualLayer)
            {
                // Dual-layer with the direct writer: build the image first so the break can go on a cell boundary.
                (result, layerBreak) = await BurnDvdVideoDualLayerAsync(burnService, recorder, root, dvdOptions, buildOptions, stdout, cmd.IsJson, telemetry).ConfigureAwait(false);
            }
            else
            {
                result = await burnService.BurnCompilationAsync(recorder, root, dvdOptions, buildOptions, telemetry).ConfigureAwait(false);
            }
        }
        finally
        {
            if (tempAuthorDir != null && Directory.Exists(tempAuthorDir))
            {
                try { Directory.Delete(tempAuthorDir, recursive: true); } catch { }
            }

            if (autoFrame != null)
            {
                try { File.Delete(autoFrame); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        IReadOnlyList<string> telemetryPaths = SaveTelemetryIfRequested(telemetryOut, telemetry);
        ExecutePostBurnActionIfSucceeded(dvdPostAction, result.Success, recorder);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                videoTsFolder = videoTsInput.Contains(';') || videoTsInput.Contains('|') ? videoTsInput : Path.GetFullPath(videoTsInput),
                volumeLabel = effectiveLabel,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                testWrite,
                writesAccepted = result.WritesAccepted,
                sessionClosed = result.SessionClosed,
                tocChecked = result.TocChecked,
                filesCompared = result.FilesCompared,
                pictureAreaCompared = result.HasPictureZone ? result.PictureAreaCompared : (bool?)null,
                verified = result.Verified,
                verificationNote = result.VerificationNote,
                bookTypeNote = result.BookTypeNote,
                warning = result.Warning,
                layerBreak = layerBreak == null ? null : new
                {
                    lba = layerBreak.Lba,
                    titleSet = layerBreak.Cell.TitleSet,
                    vobId = layerBreak.Cell.VobId,
                    cellId = layerBreak.Cell.CellId
                },
                directWrite = NativeDataJson(nativeEngine),
                fillPicture = FillJson(fillOutcome, fillSkipped),
                telemetryOut = FormatTelemetryJsonValue(telemetryPaths),
                telemetryPoints = telemetry?.Count,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine(testWrite ? "DVD-Video test write completed (laser off; the disc was not written):" : "DVD-Video burned successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {effectiveLabel}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            if (layerBreak != null) stdout.WriteLine($"  Layer break:    {layerBreak}");
            WriteFillSummary(stdout, fillOutcome, fillSkipped);
            stdout.WriteLine($"  Verified:       {(result.Verified ? "yes" : "no")}{(string.IsNullOrEmpty(result.VerificationNote) ? "" : " - " + result.VerificationNote)}");
            if (result.BookTypeNote != null) stdout.WriteLine($"  Book Type:      {result.BookTypeNote}");
            if (nativeEngine?.LastLogPath != null)
            {
                stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
            }
            WriteTelemetryOutSummary(stdout, telemetryPaths);
            return 0;
        }

        stderr.WriteLine($"DVD-Video burn failed: {result.ErrorMessage}");
        WriteTelemetryOutSummary(stderr, telemetryPaths);
        return 1;
    }

    /// <summary>
    /// DVD-Video on a dual-layer disc with the direct writer: builds the image in a temporary file (deleted when closed),
    /// and when it is larger than the first layer puts the layer break on the cell start nearest an even split
    /// (<see cref="DvdCellLayerBreak.ChooseInImage"/>, as the app's DVD-Video screen does), then burns the image with it.
    /// Stops with nothing written when no cell start can take the break.
    /// </summary>
    private static async Task<(DataBurnResult Result, DvdCellPosition? LayerBreak)> BurnDvdVideoDualLayerAsync(DataBurnService burnService,
        IDiscRecorderBackend recorder, CompilationDirectory root, DataBurnOptions options, IsoBuildOptions buildOptions, TextWriter stdout, bool json,
        IProgress<BurnProgressSnapshot>? burnProgress = null)
    {
        string tempImage = Path.Combine(Path.GetTempPath(), $"ignisdisc_dvd9_{Guid.NewGuid():N}.iso");
        await using var image = new FileStream(tempImage, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        if (!json) stdout.WriteLine("Building the disc image to place the layer break on a cell boundary...");
        await IsoUdfImageBuilder.BuildAsync(root, image, buildOptions, null, CancellationToken.None, 0).ConfigureAwait(false);
        int totalSectors = OpticalConstants.BytesToSectors(image.Length, OpticalConstants.DataSectorSize);

        DvdCellPosition? chosen = null;
        if (totalSectors > DvdLayerBreakCalculator.MaxLayer0Sectors)
        {
            chosen = DvdCellLayerBreak.ChooseInImage(image);
            if (chosen == null)
            {
                return (DataBurnResult.Failed($"No cell boundary of this DVD-Video ({totalSectors:N0} sectors) can take the layer break: it must start " +
                    "on a 16-sector block with at least half the disc on the first layer and no more than the first layer holds. Nothing was written. " +
                    "Without --native, the Windows writer burns the disc and places the break itself."), null);
            }
            if (!json) stdout.WriteLine($"Layer break: {chosen} ({chosen.Lba * 100.0 / totalSectors:F1}% on the first layer).");
        }

        image.Position = 0;
        var withBreak = new DataBurnOptions
        {
            VolumeLabel = options.VolumeLabel,
            CloseDisc = options.CloseDisc,
            VerifyAfterBurn = options.VerifyAfterBurn,
            StrictVerify = options.StrictVerify,
            EjectAfterBurn = options.EjectAfterBurn,
            Speed = options.Speed,
            Simulate = options.Simulate,
            OverwriteExistingData = options.OverwriteExistingData,
            LayerBreakSectors = chosen?.Lba,
            DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
            RequestedBookType = options.RequestedBookType,
            RequireBookType = options.RequireBookType
        };
        return (await burnService.BurnStreamAsync(recorder, image, withBreak, burnProgress).ConfigureAwait(false), chosen);
    }

    private static string? ParseTelemetryOutPath(CommandLineArgs cmd)
    {
        if (!cmd.Options.TryGetValue("telemetry-out", out string? raw))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new CommandLineException("--telemetry-out requires a file path: --telemetry-out <path>");
        }
        return raw;
    }

    private static BurnTelemetrySeries? CreateTelemetrySeries(
        string? telemetryOutPath,
        IProgress<BurnProgressSnapshot>? innerProgress,
        string title,
        IDiscRecorderBackend recorder,
        DiscMediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(telemetryOutPath))
        {
            return null;
        }
        var series = new BurnTelemetrySeries(BurnTelemetrySeries.DefaultMaxPoints, innerProgress);
        series.Reset(title, $"{recorder.VendorId} {recorder.ProductId} ({recorder.UniqueId})".Trim(), mediaType.GetDisplayName());
        return series;
    }

    private static IReadOnlyList<string> SaveTelemetryIfRequested(string? telemetryOutPath, BurnTelemetrySeries? series)
    {
        if (string.IsNullOrWhiteSpace(telemetryOutPath) || series == null)
        {
            return Array.Empty<string>();
        }
        string fullPath = Path.GetFullPath(telemetryOutPath);
        if (fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            series.SaveJson(fullPath);
            return [fullPath];
        }
        if (fullPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            series.SaveSvg(fullPath);
            return [fullPath];
        }

        string svgPath = fullPath + ".svg";
        string jsonPath = fullPath + ".json";
        series.SaveSvg(svgPath);
        series.SaveJson(jsonPath);
        return [svgPath, jsonPath];
    }

    private static object? FormatTelemetryJsonValue(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => null,
        1 => paths[0],
        _ => paths
    };

    private static void WriteTelemetryOutSummary(TextWriter writer, IReadOnlyList<string> paths)
    {
        foreach (string p in paths)
        {
            writer.WriteLine($"  Telemetry:      {p}");
        }
    }

    private static async Task<int> HandleRipAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        string outputPath = cmd.GetRequiredOption("output", "--output <file.iso|file.bin>");
        bool isRaw = cmd.HasFlag("raw") || outputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

        var recorder = FindRecorder(deviceService, driveId);
        var ripper = new DiscRipperService();

        RipOperationResult result;
        string? companionCue = null;

        if (isRaw)
        {
            companionCue = Path.ChangeExtension(outputPath, ".cue");
            result = await ripper.RipToBinCueAsync(recorder, burnEngine, outputPath, companionCue).ConfigureAwait(false);
        }
        else
        {
            result = await ripper.RipToIsoAsync(recorder, burnEngine, outputPath).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                outputPath = Path.GetFullPath(outputPath),
                cuePath = companionCue != null ? Path.GetFullPath(companionCue) : null,
                rawMode = isRaw,
                sectorsRead = result.SectorsRead,
                badSectorsCount = result.BadSectorsCount,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Rip completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Output Path:    {Path.GetFullPath(outputPath)}");
            if (companionCue != null)
            {
                stdout.WriteLine($"  CUE Sheet:      {Path.GetFullPath(companionCue)}");
            }
            stdout.WriteLine($"  Mode:           {(isRaw ? "Raw 2352-byte BIN" : "Mode 1 2048-byte ISO")}");
            stdout.WriteLine($"  Sectors Read:   {result.SectorsRead:N0}");
            stdout.WriteLine($"  Bad Sectors:    {result.BadSectorsCount}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"Rip failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleRipAudioAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine,
        HttpClient? customHttpClient = null)
    {
        string driveId = ResolveTargetDriveId(cmd);

        // --library is the finished collection; --output is the working folder for the WAV files (and the
        // destination itself when no --library is given). Without --output, the WAV files go in a hidden
        // working folder inside the library and that folder is removed when it is empty again.
        string? libraryOpt = cmd.GetOption("library");
        string? libraryFolder = string.IsNullOrWhiteSpace(libraryOpt) ? null : libraryOpt;
        if (libraryOpt != null && libraryFolder == null)
        {
            throw new CommandLineException("Missing value for --library <folder>.");
        }

        bool ownsWorkFolder = false;
        string outputFolder;
        if (libraryFolder != null && string.IsNullOrWhiteSpace(cmd.GetOption("output")))
        {
            outputFolder = Path.Combine(libraryFolder, ".rip-work-" + Guid.NewGuid().ToString("N")[..8]);
            ownsWorkFolder = true;
        }
        else
        {
            outputFolder = cmd.GetRequiredOption("output", "--output <folder>");
        }

        string? modeOpt = cmd.GetOption("mode");
        AudioRipMode ripMode = AudioRipMode.TrackByTrack;
        if (modeOpt != null)
        {
            switch (modeOpt.Trim().ToLowerInvariant())
            {
                case "track" or "track-by-track" or "tracks":
                    ripMode = AudioRipMode.TrackByTrack;
                    break;
                case "image-cue" or "image" or "single-file":
                    ripMode = AudioRipMode.ImageCue;
                    break;
                default:
                    throw new CommandLineException($"Invalid rip mode '{modeOpt}'. Expected 'track' or 'image-cue'.");
            }
        }

        var exportTargets = ParseAudioTargets(cmd, "wav");
        if (ripMode == AudioRipMode.ImageCue)
        {
            if (exportTargets.Count != 1 || exportTargets[0].Format is not (AudioRipFormat.Wav or AudioRipFormat.Flac))
            {
                throw new CommandLineException("Single-file Image + CUE mode (--mode image-cue) supports a single WAV or FLAC output format.");
            }
            if (!string.IsNullOrWhiteSpace(cmd.GetOption("tracks")))
            {
                throw new CommandLineException("--tracks cannot be used with --mode image-cue because single-file Image + CUE mode rips the full Audio CD.");
            }
        }
        string? releaseOpt = cmd.GetOption("release");
        if (releaseOpt != null && string.IsNullOrWhiteSpace(releaseOpt))
        {
            throw new CommandLineException("Missing value for --release <number|id>.");
        }
        if (releaseOpt != null && !cmd.HasFlag("lookup"))
        {
            throw new CommandLineException("--release chooses a MusicBrainz release, so it needs --lookup.");
        }

        string? ffmpegPath = exportTargets.Any(t => AudioRipFormats.RequiresFfmpeg(t.Format)) ? FfmpegLocator.FindFfmpegExecutable() : null;
        string rootFolder = libraryFolder ?? outputFolder;
        string namingTemplate = cmd.GetOption("naming") ?? (libraryFolder != null ? AudioNamingTemplate.Default : AudioNamingTemplate.Flat);
        var exporter = new AudioLibraryExporter(new MediaFoundationAacEncoder());
        var exportCheck = new AudioExportOptions(
            exportTargets, rootFolder, namingTemplate, new AudioAlbumInfo(string.Empty, string.Empty, null), ffmpegPath,
            KeepWav: cmd.HasFlag("keep-wav"),
            WriteFolderImage: !cmd.HasFlag("no-folder-image"),
            Overwrite: cmd.HasFlag("overwrite"),
            ApplyDeEmphasis: cmd.HasFlag("de-emphasis"));
        string? prerequisiteError = ripMode == AudioRipMode.ImageCue
            ? ValidateImageCuePrerequisites(exportCheck)
            : exporter.CheckPrerequisites(exportCheck);
        if (prerequisiteError != null)
        {
            throw new CommandLineException(prerequisiteError);
        }

        string formatLabel = string.Join("+", exportTargets.Select(t => FormatLabel(t.Format)));
        int effectiveBitrate = exportTargets
            .Where(t => AudioRipFormats.UsesBitrate(t.Format) && !t.Mp3VbrQuality.HasValue)
            .Select(t => t.BitrateKbps > 0 ? t.BitrateKbps : AudioRipFormats.DefaultBitrateKbps(t.Format))
            .FirstOrDefault();

        List<int>? requestedTracks = null;
        string? tracksOpt = cmd.GetOption("tracks");
        if (!string.IsNullOrWhiteSpace(tracksOpt))
        {
            requestedTracks = new List<int>();
            var tokens = tracksOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                throw new CommandLineException("Invalid track list. Expected comma-separated positive integers (e.g. --tracks 1,3,5).");
            }
            foreach (var token in tokens)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int trackNum) || trackNum <= 0)
                {
                    throw new CommandLineException($"Invalid track number '{token}'. Track numbers must be positive integers.");
                }
                requestedTracks.Add(trackNum);
            }
        }

        int? explicitReadOffset = null;
        string? readOffsetOpt = cmd.GetOption("read-offset");
        if (readOffsetOpt != null)
        {
            if (!int.TryParse(readOffsetOpt, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsedOffset) ||
                Math.Abs(parsedOffset) > 10_000)
            {
                throw new CommandLineException($"Invalid read offset '{readOffsetOpt}'. Expected an integer number of samples between -10000 and 10000 (e.g. --read-offset 6).");
            }
            explicitReadOffset = parsedOffset;
        }

        string? offsetCachePath = cmd.GetOption("offset-cache");
        string? offsetStorePath = cmd.GetOption("offset-store");
        bool refreshOffsets = cmd.HasFlag("refresh-offsets");

        var recorder = FindRecorder(deviceService, driveId);

        var ids = DiscIdCalculator.CalculateForRecorder(recorder);

        string? warning = null;
        CdTextAlbumMetadata? metadataOverride = null;

        IReadOnlyList<MusicBrainzRelease> releases = [];
        MusicBrainzRelease? chosenRelease = null;
        if (cmd.HasFlag("lookup") && ids != null)
        {
            HttpClient? ownedHttp = null;
            try
            {
                var http = customHttpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                var mbClient = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent);
                releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId).ConfigureAwait(false) ?? [];
            }
            catch (HttpRequestException hre)
            {
                warning = $"MusicBrainz lookup failed: {hre.Message}";
            }
            catch (TaskCanceledException tce)
            {
                warning = $"MusicBrainz lookup timed out: {tce.Message}";
            }
            catch (Exception ex)
            {
                warning = $"MusicBrainz lookup failed: {ex.Message}";
            }
            finally
            {
                ownedHttp?.Dispose();
            }
        }

        // Several releases can share one disc ID (for example a German and an American pressing with different
        // track titles). When they differ, the user picks the edition before the disc is read.
        if (releases.Count > 0)
        {
            if (releaseOpt != null)
            {
                chosenRelease = MusicBrainzReleaseChoice.Select(releases, releaseOpt);
                if (chosenRelease == null)
                {
                    return WriteReleaseChoiceError(cmd, stdout, stderr, driveId, releases, $"No MusicBrainz release matches --release {releaseOpt}.");
                }
            }
            else if (MusicBrainzReleaseChoice.Differences(releases).Count > 0)
            {
                return WriteReleaseChoiceError(cmd, stdout, stderr, driveId, releases, "More than one MusicBrainz release matches this disc, and they differ.");
            }
            else
            {
                chosenRelease = releases[0];
            }

            metadataOverride = MusicBrainzClient.ToAlbumMetadata(chosenRelease, ids!.MusicBrainzId);
        }
        else if (releaseOpt != null)
        {
            string reason = warning ?? "MusicBrainz knows no release for this disc.";
            return WriteSimpleError(cmd, stdout, stderr, $"--release was given, but no release could be chosen. {reason}");
        }

        string? coverPath = cmd.GetOption("cover") ?? cmd.GetOption("cover-art");
        byte[]? coverBytes = null;
        string? coverMime = null;
        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            if (!File.Exists(coverPath))
            {
                // Fail before ripping rather than silently producing FLACs without the requested cover.
                throw new FileNotFoundException($"Cover image not found: '{coverPath}'.", coverPath);
            }

            coverBytes = await File.ReadAllBytesAsync(coverPath).ConfigureAwait(false);
            coverMime = FlacEncoder.DetectImageMimeType(coverBytes)
                ?? throw new InvalidDataException($"'{coverPath}' is not a JPEG, PNG, GIF or WebP image.");
            if (FlacEncoder.CoverArtSizeProblem(coverBytes.Length) is { } tooLarge)
            {
                throw new InvalidDataException(tooLarge); // before the disc is read, not after the rip
            }
        }

        bool repairFromRecovery = cmd.HasFlag("repair") || cmd.HasFlag("repair-from-recovery");
        bool applyDeEmphasis = cmd.HasFlag("de-emphasis");
        HttpClient? ownedOffsetHttp = null;
        HttpClient? offsetHttp = null;
        if (explicitReadOffset == null && (!recorder.IsVirtual || offsetCachePath != null))
        {
            if (customHttpClient != null)
            {
                if (cmd.HasFlag("verify-accuraterip") || refreshOffsets || offsetCachePath != null)
                {
                    offsetHttp = customHttpClient;
                }
            }
            else if (!recorder.IsVirtual || refreshOffsets)
            {
                offsetHttp = ownedOffsetHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            }
        }

        AudioCdRipResult result;
        try
        {
            var options = new AudioRipOptions
            {
                ContinueOnUnreadableSector = cmd.HasFlag("continue-on-error"),
                TrackNumbersToRip = requestedTracks,
                MetadataOverride = metadataOverride,
                ExplicitReadOffsetSamples = explicitReadOffset,
                DriveOffsetStorePath = offsetStorePath,
                DriveOffsetsCachePath = offsetCachePath,
                OffsetDatabaseHttpClient = offsetHttp,
                ForceRefreshOffsetDatabase = refreshOffsets,
                SecureMode = !cmd.HasFlag("no-secure"), // on by default; a drive without C2 pointers falls back to a plain read
                CalculateReplayGain = !cmd.HasFlag("no-replaygain"),
                CoverArtBytes = coverBytes,
                CoverArtMimeType = coverMime,
                RepairFromRecoverySession = repairFromRecovery,
                ApplyDeEmphasis = applyDeEmphasis,
                Mode = ripMode,
                ImageFormat = ripMode == AudioRipMode.ImageCue ? exportTargets[0].Format : AudioRipFormat.Wav,
                FfmpegPath = ffmpegPath,
                WriteRipLog = false
            };

            var ripper = new AudioCdRipperService();
            result = await ripper.RipAudioCdAsync(recorder, burnEngine, outputFolder, options).ConfigureAwait(false);
        }
        finally
        {
            ownedOffsetHttp?.Dispose();
        }

        int readOffset = result.ReadOffsetSamples;
        if (result.ReadOffset?.Warning is { } offsetWarning)
        {
            warning = string.IsNullOrEmpty(warning) ? offsetWarning : warning + " " + offsetWarning;
        }

        string metadataSource = metadataOverride != null
            ? "musicbrainz"
            : (result.CdText != null ? "cd-text" : "none");

        if (!result.Success)
        {
            if (cmd.IsJson)
            {
                var errObj = new
                {
                    success = false,
                    driveId = recorder.UniqueId,
                    outputFolder = Path.GetFullPath(outputFolder),
                    format = formatLabel,
                    bitrateKbps = effectiveBitrate,
                    tracksRipped = 0,
                    sectorsRead = 0,
                    totalTimeSeconds = 0.0,
                    musicBrainzId = ids?.MusicBrainzId,
                    freeDbId = ids?.FreeDbIdHex,
                    accurateRipPath = ids?.AccurateRipDatabasePath,
                    ctdbTocId = ids?.CtdbTocId,
                    metadataSource = metadataSource,
                    warning = warning,
                    files = Array.Empty<string>(),
                    cueSheet = (string?)null,
                    error = result.ErrorMessage
                };
                WriteJsonResult(stdout, errObj);
                return 1;
            }

            stderr.WriteLine($"Rip failed: {result.ErrorMessage}");
            return 1;
        }

        // AccurateRip: checksums were computed on the PCM during the rip, so this is independent of the output format.
        string? accurateRipStatus = null;
        var accurateRipTracks = new List<(AudioTrackRipInfo Info, AccurateRipTrackVerdict? Verdict)>();
        IReadOnlyList<AccurateRipPressing>? accurateRipPressings = null;
        if (cmd.HasFlag("verify-accuraterip"))
        {
            if (ids == null)
            {
                accurateRipStatus = "unavailable";
            }
            else
            {
                HttpClient? ownedHttp = null;
                try
                {
                    var http = customHttpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                    var pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
                    accurateRipPressings = pressings;
                    accurateRipStatus = pressings.Count == 0 ? "not-in-database" : "checked";
                    foreach (var info in result.TrackInfos)
                    {
                        accurateRipTracks.Add((info, pressings.Count == 0
                            ? null
                            : AccurateRipDatabase.Match(pressings, info.AudioTrackIndex, info.AccurateRipV1, info.AccurateRipV2)));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                {
                    accurateRipStatus = "failed";
                    warning = (warning == null ? "" : warning + " ") + $"AccurateRip lookup failed: {ex.Message}";
                }
                finally
                {
                    ownedHttp?.Dispose();
                }
            }

            if (readOffset == 0 && accurateRipStatus == "checked" && accurateRipTracks.Any(t => t.Verdict is { IsAccurate: false })
                && result.ReadOffset?.Warning == null)
            {
                warning = (warning == null ? "" : warning + " ") +
                    "No --read-offset was given; AccurateRip only matches when the drive's read offset is corrected (see the AccurateRip drive offset list).";
            }
        }

        string? ctdbStatus = null;
        (bool Matched, int Confidence, int TotalSubmissions)? ctdbDiscMatch = null;
        var ctdbTracks = new List<(AudioTrackRipInfo Info, CtdbTrackVerdict? Verdict)>();
        CtdbRepairResult? ctdbRepair = null;
        if (cmd.HasFlag("verify-ctdb"))
        {
            if (ids == null || string.IsNullOrWhiteSpace(ids.CtdbToc))
            {
                ctdbStatus = "unavailable";
            }
            else
            {
                HttpClient? ownedHttp = null;
                try
                {
                    var http = customHttpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                    var entries = await CueToolsDatabase.FetchAsync(http, ids).ConfigureAwait(false);
                    ctdbStatus = entries.Count == 0 ? "not-in-database" : "checked";
                    if (entries.Count > 0)
                    {
                        ctdbDiscMatch = CueToolsDatabase.MatchDisc(entries, result.CtdbDiscCrc);
                    }

                    // A rip that differs from CTDB is repaired from the entry's parity (unless --no-ctdb-repair).
                    // AccurateRip is contacted to confirm only when a correction was found, and the WAV files are
                    // only changed when AccurateRip then matches every corrected track.
                    if (ctdbDiscMatch is { Matched: false } && result.CtdbDiscCrc != null && !cmd.HasFlag("no-ctdb-repair"))
                    {
                        var knownPressings = accurateRipPressings;
                        ctdbRepair = await CtdbRepairService.RepairAsync(
                            http,
                            result,
                            entries,
                            ids.CtdbToc,
                            knownPressings != null
                                ? _ => Task.FromResult(knownPressings)
                                : ct => AccurateRipDatabase.FetchAsync(http, ids, cancellationToken: ct)).ConfigureAwait(false);
                        if (ctdbRepair.UpdatedRip is { } repairedRip)
                        {
                            result = repairedRip;
                            ctdbDiscMatch = CueToolsDatabase.MatchDisc(entries, result.CtdbDiscCrc);
                            if (accurateRipPressings is { Count: > 0 } arPressings)
                            {
                                accurateRipTracks = result.TrackInfos
                                    .Select(i => (i, (AccurateRipTrackVerdict?)AccurateRipDatabase.Match(arPressings, i.AudioTrackIndex, i.AccurateRipV1, i.AccurateRipV2)))
                                    .ToList();
                            }
                        }
                    }

                    foreach (var info in result.TrackInfos)
                    {
                        ctdbTracks.Add((info, entries.Count == 0
                            ? null
                            : CueToolsDatabase.MatchTrack(entries, info.AudioTrackIndex, info.CtdbTrackCrc, result.CtdbDiscCrc)));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                {
                    ctdbStatus = "failed";
                    warning = (warning == null ? "" : warning + " ") + $"CTDB lookup failed: {ex.Message}";
                }
                finally
                {
                    ownedHttp?.Dispose();
                }
            }
        }

        // Front cover from the Cover Art Archive when the user gave no image of their own.
        string? coverSource = coverBytes != null ? "file" : null;
        if (coverBytes == null && chosenRelease != null && !cmd.HasFlag("no-cover-lookup"))
        {
            HttpClient? ownedHttp = null;
            try
            {
                var http = customHttpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                var image = await CoverArtArchiveClient.FetchFrontAsync(http, chosenRelease.ReleaseId, MusicBrainzClient.DefaultUserAgent).ConfigureAwait(false);
                if (image != null)
                {
                    coverBytes = image.Bytes;
                    coverMime = image.MimeType;
                    coverSource = "coverartarchive";
                }
                else
                {
                    warning = (warning == null ? "" : warning + " ") + "The Cover Art Archive has no usable front cover for this release.";
                }
            }
            catch (Exception ex)
            {
                warning = (warning == null ? "" : warning + " ") + $"Cover art lookup failed: {ex.Message}";
            }
            finally
            {
                ownedHttp?.Dispose();
            }
        }

        var album = new AudioAlbumInfo(
            result.CdText?.AlbumPerformer ?? string.Empty,
            result.CdText?.AlbumTitle ?? string.Empty,
            chosenRelease == null ? null : MusicBrainzReleaseChoice.Year(chosenRelease),
            chosenRelease?.ReleaseId,
            coverBytes,
            coverMime);
        var exportOptions = exportCheck with { Album = album };

        AudioExportResult exported;
        try
        {
            exported = ripMode == AudioRipMode.ImageCue
                ? await exporter.ExportImageCueAsync(result, exportOptions).ConfigureAwait(false)
                : await exporter.ExportAsync(result, exportOptions).ConfigureAwait(false);
            if (exported.Warnings.Count > 0)
            {
                warning = (warning == null ? "" : warning + " ") + string.Join(" ", exported.Warnings);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The WAV files stay in place, so the rip is not lost when only the export fails.
            string message = $"The disc was read, but writing the {formatLabel} files failed: {ex.Message} The WAV files are kept in '{Path.GetFullPath(outputFolder)}'.";
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new { success = false, format = formatLabel, files = result.RippedFilePaths.Select(Path.GetFullPath).ToList(), error = message });
                return 1;
            }
            stderr.WriteLine(message);
            return 1;
        }

        var finalOutputFiles = exported.Files.Select(f => f.Path).ToList();
        string? primaryImageFile = ripMode == AudioRipMode.ImageCue
            ? finalOutputFiles.FirstOrDefault() ?? result.ImageFilePath
            : null;

        var logDirectories = exported.CueSheets
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (logDirectories.Count == 0)
        {
            string fallbackDir = exported.Files.Count > 0
                ? Path.GetDirectoryName(exported.Files[0].Path) ?? outputFolder
                : outputFolder;
            logDirectories.Add(fallbackDir);
        }

        string logFileName = AudioCdRipperService.ResolveLogFileName(
            null,
            result.CdText?.AlbumTitle,
            primaryImageFile != null ? Path.GetFileNameWithoutExtension(primaryImageFile) : "album");
        var arVerdictsMap = accurateRipTracks.ToDictionary(x => x.Info.TrackNumber, x => x.Verdict);
        var ctdbVerdictsMap = ctdbTracks.ToDictionary(x => x.Info.TrackNumber, x => x.Verdict);
        var logResultSnapshot = ripMode == AudioRipMode.ImageCue && primaryImageFile != null
            ? AudioCdRipResult.Succeeded(
                finalOutputFiles,
                result.TotalSectorsRead,
                result.TotalTime,
                exported.CueSheets.FirstOrDefault() ?? result.CueSheetPath,
                result.CdText,
                result.TrackInfos,
                result.BadSectorLbas,
                result.SecureModeUsed,
                result.ReplayGainAlbumGain,
                result.ReplayGainAlbumPeak,
                result.RecoveryRepair,
                result.HiddenTrackOneAudioPath,
                result.CtdbDiscCrc,
                result.ReadOffset,
                result.Mode,
                primaryImageFile,
                result.HiddenTrackOneAudioFrames,
                result.RipLogPath,
                result.DiscTocTracks,
                result.MediaCatalogNumber,
                result.GapDetectionPerformed,
                result.OverreadIntoLeadInOut,
                result.SecureModeRequested,
                result.DriveUniqueId,
                result.DriveVendorId,
                result.DriveProductId,
                result.DriveProductRevision)
            : result;
        var logContext = new RipLogContext(
            logResultSnapshot,
            ids,
            accurateRipStatus,
            arVerdictsMap,
            ctdbStatus,
            ctdbDiscMatch,
            ctdbVerdictsMap,
            ctdbRepair,
            OutputFormatLabel: formatLabel);
        var ripLogPaths = new List<string>(logDirectories.Count);
        foreach (string logDir in logDirectories)
        {
            string logPath = Path.Combine(logDir, logFileName);
            await RipLogFormatter.WriteToFileAsync(logPath, logContext).ConfigureAwait(false);
            ripLogPaths.Add(logPath);
        }
        string? primaryRipLogPath = ripLogPaths.FirstOrDefault();

        if (ownsWorkFolder)
        {
            try
            {
                if (Directory.Exists(outputFolder) && !Directory.EnumerateFileSystemEntries(outputFolder).Any())
                {
                    Directory.Delete(outputFolder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = (warning == null ? "" : warning + " ") + $"Could not remove the working folder '{outputFolder}': {ex.Message}";
            }
        }

        // A rip with sectors that may be wrong is finished but not clean: exit code 2 (as `recover` uses for lost files),
        // so a script does not take it for a perfect copy.
        bool repairedAll = result.RecoveryRepair is { RecoverySessionFound: true, AllTracksVerified: true };
        int suspectTotal = result.TrackInfos.Sum(t => t.SuspectSectors);
        int unreadableTotal = repairedAll
            ? result.TrackInfos.Sum(t => t.UnreadableSectors)
            : Math.Max(result.TrackInfos.Sum(t => t.UnreadableSectors), result.BadSectorLbas.Count);
        bool repairFailed = repairFromRecovery && result.RecoveryRepair is { RecoverySessionFound: true, AllTracksVerified: false };
        // A CTDB repair confirmed by AccurateRip replaced every wrong sample, so the rip is clean.
        bool ctdbRepaired = ctdbRepair?.Status == CtdbRepairStatus.Repaired;
        bool complete = ctdbRepaired || (!repairFailed && suspectTotal + unreadableTotal == 0);
        int exitCode = complete ? 0 : 2;

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = true,
                complete,
                suspectSectors = suspectTotal,
                unreadableSectors = unreadableTotal,
                recoveryRepair = result.RecoveryRepair == null ? null : new
                {
                    recoverySessionFound = result.RecoveryRepair.RecoverySessionFound,
                    repairAttempted = result.RecoveryRepair.RepairAttempted,
                    allTracksVerified = result.RecoveryRepair.AllTracksVerified,
                    tracksChecked = result.RecoveryRepair.TracksChecked,
                    damagedTracks = result.RecoveryRepair.DamagedTracks,
                    repairedTracks = result.RecoveryRepair.RepairedTracks,
                    failedTracks = result.RecoveryRepair.FailedTracks,
                    summary = result.RecoveryRepair.Summary
                },
                driveId = recorder.UniqueId,
                mode = ripMode == AudioRipMode.ImageCue ? "image-cue" : "track",
                outputFolder = Path.GetFullPath(outputFolder),
                format = formatLabel,
                bitrateKbps = effectiveBitrate,
                tracksRipped = result.TracksRipped,
                sectorsRead = result.TotalSectorsRead,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                musicBrainzId = ids?.MusicBrainzId,
                freeDbId = ids?.FreeDbIdHex,
                accurateRipPath = ids?.AccurateRipDatabasePath,
                ctdbTocId = ids?.CtdbTocId,
                ctdbDiscCrc = result.CtdbDiscCrc?.ToString("X8"),
                metadataSource = metadataSource,
                warning = warning,
                files = finalOutputFiles.Select(Path.GetFullPath).ToList(),
                imageFile = primaryImageFile == null ? null : Path.GetFullPath(primaryImageFile),
                ripLog = primaryRipLogPath == null ? null : Path.GetFullPath(primaryRipLogPath),
                ripLogs = ripLogPaths.Select(Path.GetFullPath).ToList(),
                formats = exportTargets.Select(t => FormatLabel(t.Format)).ToList(),
                exports = exported.Files
                    .GroupBy(f => f.Format)
                    .Select(g => new { format = FormatLabel(g.Key), files = g.Select(f => Path.GetFullPath(f.Path)).ToList() })
                    .ToList(),
                libraryFolder = libraryFolder == null ? null : Path.GetFullPath(libraryFolder),
                cueSheet = exported.CueSheets.Count > 0
                    ? Path.GetFullPath(exported.CueSheets[0])
                    : (result.CueSheetPath != null && File.Exists(result.CueSheetPath) ? Path.GetFullPath(result.CueSheetPath) : null),
                cueSheets = exported.CueSheets.Select(Path.GetFullPath).ToList(),
                folderImages = exported.FolderImages.Select(Path.GetFullPath).ToList(),
                coverSource,
                release = chosenRelease == null ? null : new
                {
                    id = chosenRelease.ReleaseId,
                    title = chosenRelease.Title,
                    artist = chosenRelease.Artist,
                    date = chosenRelease.Date,
                    country = chosenRelease.Country
                },
                readOffset,
                readOffsetSource = result.ReadOffset?.SourceDescription,
                readOffsetBadge = result.ReadOffset?.BadgeText,
                badSectors = result.BadSectorLbas,
                trackChecksums = result.TrackInfos.Select(t => new
                {
                    trackNumber = t.TrackNumber,
                    crc32 = t.Crc32.ToString("X8"),
                    accurateRipV1 = t.AccurateRipV1.ToString("x8"),
                    accurateRipV2 = t.AccurateRipV2.ToString("x8"),
                    ctdbCrc = t.CtdbTrackCrc.ToString("X8"),
                    unreadableSectors = t.UnreadableSectors,
                    recoveredSectors = t.RecoveredSectors,
                    suspectSectors = t.SuspectSectors,
                    startLba = t.StartLba,
                    sectorCount = t.SectorCount,
                    preGapFrames = t.PreGapFrames,
                    peakPercent = t.PeakLevelPercent
                }).ToList(),
                secureMode = cmd.HasFlag("no-secure") ? "off" : result.SecureModeUsed ? "c2" : "unsupported",
                accurateRip = accurateRipStatus == null ? null : new
                {
                    status = accurateRipStatus,
                    tracks = accurateRipTracks.Select(t => new
                    {
                        trackNumber = t.Info.TrackNumber,
                        accurate = t.Verdict?.IsAccurate,
                        confidence = t.Verdict?.Confidence,
                        totalSubmissions = t.Verdict?.TotalSubmissions,
                        matchedV2 = t.Verdict?.MatchedV2
                    }).ToList()
                },
                ctdb = ctdbStatus == null ? null : new
                {
                    status = ctdbStatus,
                    discAccurate = ctdbDiscMatch?.Matched,
                    discConfidence = ctdbDiscMatch?.Confidence,
                    totalSubmissions = ctdbDiscMatch?.TotalSubmissions,
                    tracks = ctdbTracks.Select(t => new
                    {
                        trackNumber = t.Info.TrackNumber,
                        accurate = t.Verdict?.IsAccurate,
                        confidence = t.Verdict?.Confidence,
                        totalSubmissions = t.Verdict?.TotalSubmissions
                    }).ToList()
                },
                ctdbRepair = ctdbRepair == null ? null : new
                {
                    status = ctdbRepair.StatusCode,
                    message = ctdbRepair.Message,
                    entryId = ctdbRepair.EntryId,
                    offsetSamples = ctdbRepair.OffsetSamples,
                    correctedSamples = ctdbRepair.CorrectedSamples,
                    correctedFolder = ctdbRepair.CorrectedFolder,
                    accurateRipContacted = ctdbRepair.AccurateRipContacted,
                    tracks = ctdbRepair.CorrectedTracks.Select(t => new
                    {
                        trackNumber = t.TrackNumber,
                        correctedSamples = t.CorrectedSamples,
                        path = t.CorrectedPath,
                        accurateRipAccurate = t.AccurateRip?.IsAccurate,
                        accurateRipConfidence = t.AccurateRip?.Confidence
                    }).ToList()
                },
                error = (string?)null
            };

            WriteJsonResult(stdout, jsonResult);
            return exitCode;
        }

        stdout.WriteLine(complete
            ? "Audio rip completed successfully:"
            : $"Audio rip finished, but {suspectTotal + unreadableTotal:N0} sector(s) may be wrong ({suspectTotal:N0} still suspect after re-reads, {unreadableTotal:N0} unreadable):");
        stdout.WriteLine($"  Drive:            {recorder.UniqueId}");
        stdout.WriteLine($"  Output Folder:    {Path.GetFullPath(outputFolder)}");
        stdout.WriteLine($"  Format:           {formatLabel}{(effectiveBitrate > 0 ? $" ({effectiveBitrate} kbit/s)" : "")}");
        stdout.WriteLine($"  Tracks Ripped:    {result.TracksRipped}");
        stdout.WriteLine($"  Sectors Read:     {result.TotalSectorsRead:N0}");
        stdout.WriteLine($"  Duration:         {result.TotalTime}");
        stdout.WriteLine($"  MusicBrainz ID:   {ids?.MusicBrainzId ?? "n/a"}");
        stdout.WriteLine($"  freedb ID:        {ids?.FreeDbIdHex ?? "n/a"}");
        stdout.WriteLine($"  AccurateRip:      {ids?.AccurateRipDatabasePath ?? "n/a"}");
        stdout.WriteLine($"  CTDB TOCID:       {ids?.CtdbTocId ?? "n/a"}");
        stdout.WriteLine($"  Metadata Source:  {metadataSource}");
        stdout.WriteLine($"  Read Offset:      {result.ReadOffset?.BadgeText ?? $"{readOffset:+0;-0;0} samples"}");
        if (!cmd.HasFlag("no-secure"))
        {
            stdout.WriteLine(result.SecureModeUsed
                ? $"  Secure Mode:      C2 error pointers ({result.TrackInfos.Sum(t => t.RecoveredSectors)} sectors recovered, {result.TrackInfos.Sum(t => t.SuspectSectors)} still suspect)"
                : "  Secure Mode:      the drive does not report C2 error pointers, so a plain read was used");
            foreach (var t in result.TrackInfos.Where(t => t.SuspectSectors > 0))
            {
                stdout.WriteLine($"    Track {t.TrackNumber:D2}: {t.SuspectSectors} suspect sector(s) - check AccurateRip or re-rip");
            }
        }
        if (result.BadSectorLbas.Count > 0)
        {
            stdout.WriteLine($"  Bad Sectors:      {result.BadSectorLbas.Count} (zero-filled)");
        }
        if (result.RecoveryRepair is { } repair)
        {
            stdout.WriteLine($"  Recovery Session: {repair.Summary}");
        }
        if (accurateRipStatus != null)
        {
            stdout.WriteLine($"  AccurateRip:      {accurateRipStatus}");
            foreach (var (info, verdict) in accurateRipTracks)
            {
                string line = verdict == null
                    ? "not in database"
                    : verdict.IsAccurate
                        ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions}, {(verdict.MatchedV2 ? "v2" : "v1")})"
                        : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                stdout.WriteLine($"    Track {info.TrackNumber:D2}: {line}  [v1 {info.AccurateRipV1:x8} v2 {info.AccurateRipV2:x8}]");
            }
        }
        if (ctdbStatus != null)
        {
            string discLine = ctdbDiscMatch is { } dm && result.CtdbDiscCrc is uint discCrc
                ? $"{ctdbStatus} (disc {(dm.Matched ? $"accurate, confidence {dm.Confidence}/{dm.TotalSubmissions}" : $"NOT accurate, 0/{dm.TotalSubmissions} submissions match")}, crc {discCrc:x8})"
                : ctdbStatus;
            stdout.WriteLine($"  CTDB:             {discLine}");
            foreach (var (info, verdict) in ctdbTracks)
            {
                string line = verdict == null
                    ? "not in database"
                    : verdict.IsAccurate
                        ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions})"
                        : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                stdout.WriteLine($"    Track {info.TrackNumber:D2}: {line}  [ctdb {info.CtdbTrackCrc:x8}]");
            }
        }
        if (ctdbRepair != null)
        {
            stdout.WriteLine($"  CTDB Repair:      {ctdbRepair.StatusCode} - {ctdbRepair.Message}");
            foreach (string line in ctdbRepair.DescribeTracks())
            {
                stdout.WriteLine($"    {line}");
            }
            if (ctdbRepair.AccurateRipContacted && !cmd.HasFlag("verify-accuraterip"))
            {
                stdout.WriteLine("    AccurateRip was contacted only to confirm this repair.");
            }
        }
        if (!string.IsNullOrEmpty(warning))
        {
            stdout.WriteLine($"  Warning:          {warning}");
        }
        if (chosenRelease != null)
        {
            stdout.WriteLine($"  Release:          {chosenRelease.Title} - {chosenRelease.Artist}" +
                (string.IsNullOrWhiteSpace(chosenRelease.Country) && string.IsNullOrWhiteSpace(chosenRelease.Date)
                    ? ""
                    : $" ({string.Join(", ", new[] { chosenRelease.Country, chosenRelease.Date }.Where(x => !string.IsNullOrWhiteSpace(x)))})"));
        }
        if (coverSource != null)
        {
            stdout.WriteLine($"  Cover Art:        {(coverSource == "file" ? "from the --cover file" : "from the Cover Art Archive")}");
        }
        stdout.WriteLine("  Files:");
        foreach (var group in exported.Files.GroupBy(f => f.Format))
        {
            stdout.WriteLine($"    {FormatLabel(group.Key)}:");
            foreach (var file in group)
            {
                stdout.WriteLine($"      {Path.GetFullPath(file.Path)}");
            }
        }
        foreach (string cue in exported.CueSheets)
        {
            stdout.WriteLine($"  CUE Sheet:        {Path.GetFullPath(cue)}");
        }
        if (exported.CueSheets.Count == 0 && !string.IsNullOrEmpty(result.CueSheetPath) && File.Exists(result.CueSheetPath))
        {
            stdout.WriteLine($"  CUE Sheet:        {Path.GetFullPath(result.CueSheetPath)}");
        }
        foreach (string image in exported.FolderImages)
        {
            stdout.WriteLine($"  Folder Image:     {Path.GetFullPath(image)}");
        }
        foreach (string log in ripLogPaths)
        {
            stdout.WriteLine($"  Rip Log:          {Path.GetFullPath(log)}");
        }

        return exitCode;
    }

    private static string? ValidateImageCuePrerequisites(AudioExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootFolder))
        {
            return "The destination folder is empty.";
        }

        string? templateProblem = AudioNamingTemplate.Validate(options.NamingTemplate);
        if (templateProblem != null)
        {
            return templateProblem;
        }

        if (options.Targets.Count != 1 || options.Targets[0].Format is not (AudioRipFormat.Wav or AudioRipFormat.Flac))
        {
            return "Single-file Image + CUE mode supports a single WAV or FLAC output format.";
        }

        if (options.Targets[0].Format == AudioRipFormat.Flac && options.Album?.CoverBytes is { Length: > 0 } cover &&
            FlacEncoder.CoverArtSizeProblem(cover.Length) is { } coverProblem)
        {
            return coverProblem;
        }

        return null;
    }

    private static async Task<int> HandleDetectOffsetAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        var tracks = AudioCdRipperService.QueryTracksAndCdText(recorder).Tracks;
        var ids = DiscIdCalculator.CalculateForRecorder(recorder);
        if (ids == null || tracks.Count == 0)
        {
            throw new CommandLineException("No audio tracks found. Insert an audio CD to detect the drive's read offset.");
        }

        IReadOnlyList<AccurateRipPressing> pressings;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
        }

        if (pressings.Count == 0)
        {
            throw new CommandLineException(
                "This disc is not in the AccurateRip database, so it cannot be used to detect the offset. Try a well-known commercial CD.");
        }

        var detection = await ReadOffsetDetector.DetectAsync(
            burnEngine,
            recorder,
            tracks,
            pressings,
            driveOffsetStorePath: cmd.GetOption("offset-store"),
            driveOffsetsCachePath: cmd.GetOption("offset-cache")).ConfigureAwait(false);
        var best = detection.Best;

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = best != null,
                driveId = recorder.UniqueId,
                readOffset = best?.OffsetSamples,
                confidence = best?.Confidence,
                savedCalibration = detection.SavedCalibration,
                accurateRipDbOffset = detection.DatabaseEntry?.ReadOffsetSamples,
                probedTracks = detection.ProbedTracks,
                candidates = detection.Candidates.Select(c => new { offset = c.OffsetSamples, c.Confidence, c.TrackNumber }),
                accurateRipPath = ids.AccurateRipDatabasePath,
                error = best == null ? (detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database.") : null
            });
            return best != null ? 0 : 1;
        }

        if (best == null)
        {
            stderr.WriteLine($"Offset detection failed: {detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database."}");
            return 1;
        }

        stdout.WriteLine("Read offset detected:");
        stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
        stdout.WriteLine($"  Read Offset:    {best.OffsetSamples:+0;-0;0} samples");
        stdout.WriteLine($"  Confidence:     {best.Confidence} matching submissions");
        stdout.WriteLine($"  Tracks Checked: {string.Join(", ", detection.ProbedTracks)}");
        if (detection.SavedCalibration)
        {
            stdout.WriteLine("  Calibration:    saved for this drive");
        }
        if (detection.DatabaseEntry != null)
        {
            stdout.WriteLine($"  AccurateRip DB: {detection.DatabaseEntry.ReadOffsetSamples:+0;-0;0} samples ({detection.DatabaseEntry.Submissions} submissions)");
        }
        stdout.WriteLine($"  Use it with:    rip-audio --drive {driveId} --read-offset {best.OffsetSamples:+0;-0;0} --verify-accuraterip ...");
        return 0;
    }

    private static async Task<int> HandleArchiveCommandAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string verb = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : string.Empty;
        return verb switch
        {
            "" or "stage" => await HandleArchiveStageAsync(cmd, stdout).ConfigureAwait(false),
            "burn" => await HandleArchiveBurnAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "verify" => await HandleArchiveVerifyAsync(cmd, stdout).ConfigureAwait(false),
            "rebuild" => await HandleArchiveRebuildAsync(cmd, stdout).ConfigureAwait(false),
            "recover" => await HandleRecoverAsync(cmd, stdout, deviceService, burnEngine).ConfigureAwait(false),
            _ => throw new CommandLineException("Usage: archive [stage | burn | verify | rebuild | recover] [options] [--json]")
        };
    }

    private static async Task<int> HandleArchiveStageAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string input = cmd.GetRequiredOption("input", "--input <folder>");
        string? staging = cmd.GetOption("staging");
        bool planOnly = cmd.HasFlag("plan-only");
        if (!planOnly && string.IsNullOrWhiteSpace(staging))
        {
            throw new CommandLineException("Missing required option --staging <folder> (or use --plan-only).");
        }

        DiscMediaType media = (cmd.GetOption("media") ?? "dvd").ToLowerInvariant() switch
        {
            "cd" => DiscMediaType.CdR,
            "dvd" => DiscMediaType.DvdPlusR,                // the smaller single-layer capacity, safe for -R and +R
            "dvd-dl" => DiscMediaType.DvdMinusRDualLayer,   // the smaller dual-layer capacity
            "bd" => DiscMediaType.BdRe,                     // BD-RE's spare area makes it the smaller of BD-R/RE
            "bd-dl" => DiscMediaType.BdReDualLayer,
            "bdxl" => DiscMediaType.BdxlRTripleLayer,
            "bdxl-ql" => DiscMediaType.BdxlRQuadLayer,
            var other => throw new CommandLineException($"Invalid media '{other}'. Supported: cd, dvd, dvd-dl, bd, bd-dl, bdxl, bdxl-ql.")
        };

        string redundancyOpt = cmd.GetOption("redundancy") ?? "10";
        if (!int.TryParse(redundancyOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int redundancy) || redundancy is < 0 or > 100)
        {
            throw new CommandLineException($"Invalid redundancy '{redundancyOpt}'. Expected a percentage between 0 and 100.");
        }

        // Recovery data fills each disc unless --no-fill; --redundancy is then the minimum.
        string order = (cmd.GetOption("order") ?? "size").ToLowerInvariant();
        if (order is not ("size" or "path"))
        {
            throw new CommandLineException($"Invalid order '{order}'. Supported: size (default, fewest discs), path.");
        }

        if (cmd.HasFlag("keep-folders") && order == "path")
        {
            throw new CommandLineException("--keep-folders and --order path cannot be combined: --order path fills discs in path order and splits folders.");
        }

        ArchivePackingMode packing = order == "path"
            ? ArchivePackingMode.PathOrder
            : cmd.HasFlag("keep-folders") ? ArchivePackingMode.KeepFolders : ArchivePackingMode.FirstFitDecreasing;

        string? parityOpt = cmd.GetOption("parity-discs");
        int parityDiscs = 0;
        if (!string.IsNullOrWhiteSpace(parityOpt))
        {
            if (!int.TryParse(parityOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out parityDiscs) ||
                parityDiscs < 0 || parityDiscs >= CrossDiscReedSolomon.MaxTotalDiscs)
            {
                throw new CommandLineException(
                    $"Invalid --parity-discs '{parityOpt}'. Expected an integer between 0 and {CrossDiscReedSolomon.MaxTotalDiscs - 1}.");
            }
        }

        var plan = ArchiveSpanPlanner.Plan(
            input,
            media,
            redundancy,
            fillFreeSpace: !cmd.HasFlag("no-fill"),
            packing: packing,
            parityDiscCount: parityDiscs);

        string? par2Path = null;
        if (!planOnly && redundancy > 0)
        {
            par2Path = Par2Runner.FindPar2Executable()
                ?? throw new CommandLineException(
                    "PAR2 recovery data needs par2cmdline (par2.exe on PATH). Install it, or pass --redundancy 0 for manifests only.");
        }

        IReadOnlyList<ArchiveStagedDisc> staged = Array.Empty<ArchiveStagedDisc>();
        if (!planOnly)
        {
            var progress = cmd.IsJson ? null : new Progress<string>(message => stdout.WriteLine($"  {message}"));
            staged = await ArchiveStager.StageDiscsAsync(plan, staging!, par2Path, progress).ConfigureAwait(false);
        }
        IReadOnlyList<string> stagedDiscs = staged.Select(s => s.DiscRoot).ToList();

        if (cmd.IsJson)
        {
            var discJsonItems = new List<object>(plan.TotalDiscCount > 0 ? plan.TotalDiscCount : plan.Discs.Count);
            foreach (var d in plan.Discs)
            {
                var s = staged.Count >= d.DiscNumber ? staged[d.DiscNumber - 1] : null;
                discJsonItems.Add(new
                {
                    discNumber = d.DiscNumber,
                    isParityDisc = false,
                    files = d.Files.Count,
                    dataBytes = d.DataBytes,
                    path = s?.DiscRoot,
                    totalSectors = s?.TotalSectors,
                    recoverySectors = s?.RecoverySectors,
                    recoveryBlocks = s?.Recovery?.RecoveryBlocks,
                    recoveryBlockSize = s?.Recovery?.BlockSize,
                    recoveryPercent = s?.Recovery == null ? (double?)null : Math.Round(s.Recovery.RedundancyPercent, 1)
                });
            }
            if (plan.ParityDiscCount > 0 && plan.Discs.Count > 0)
            {
                for (int p = 0; p < plan.ParityDiscCount; p++)
                {
                    int discNum = plan.Discs.Count + 1 + p;
                    var s = staged.Count >= discNum ? staged[discNum - 1] : null;
                    discJsonItems.Add(new
                    {
                        discNumber = discNum,
                        isParityDisc = true,
                        files = 0,
                        dataBytes = 0L,
                        path = s?.DiscRoot,
                        totalSectors = s?.TotalSectors,
                        recoverySectors = s?.RecoverySectors,
                        recoveryBlocks = s?.Recovery?.RecoveryBlocks,
                        recoveryBlockSize = s?.Recovery?.BlockSize,
                        recoveryPercent = s?.Recovery == null ? (double?)null : Math.Round(s.Recovery.RedundancyPercent, 1)
                    });
                }
            }

            WriteJsonResult(stdout, new
            {
                success = true,
                setId = plan.SetId,
                media = media.ToString(),
                redundancyPercent = plan.RedundancyPercent,
                parityDiscCount = plan.ParityDiscCount,
                dataDiscCount = plan.Discs.Count,
                totalDiscs = plan.TotalDiscCount,
                usableSectorsPerDisc = plan.UsableSectorsPerDisc,
                ceilingSectorsPerDisc = plan.EffectiveCeilingSectors,
                fillFreeSpace = plan.FillFreeSpace,
                totalBytes = plan.TotalBytes,
                discs = discJsonItems,
                planOnly
            });
            return 0;
        }

        stdout.WriteLine(planOnly ? "Archive plan:" : "Archive staged successfully:");
        stdout.WriteLine($"  Set ID:           {plan.SetId}");
        // --media plans for the smaller of the -R/+R capacities, so the plan fits either kind of disc.
        string mediaName = media switch
        {
            DiscMediaType.CdR => "CD-R (700 MB)",
            DiscMediaType.DvdPlusR => "DVD±R (4.7 GB)",
            DiscMediaType.DvdMinusRDualLayer => "DVD±R DL (8.5 GB)",
            DiscMediaType.BdRe => "BD-R/RE (25 GB)",
            DiscMediaType.BdReDualLayer => "BD-R/RE DL (50 GB)",
            DiscMediaType.BdxlRTripleLayer => "BD-R XL (100 GB)",
            DiscMediaType.BdxlRQuadLayer => "BD-R XL (128 GB)",
            _ => media.GetDisplayName()
        };
        stdout.WriteLine($"  Media:            {mediaName}");
        stdout.WriteLine($"  Redundancy:       {plan.RedundancyPercent}% PAR2" + (plan.ParityDiscCount > 0 ? $" + {plan.ParityDiscCount} cross-disc parity disc(s)" : string.Empty));
        stdout.WriteLine($"  Total Data:       {plan.TotalBytes / 1048576.0:N1} MB in {plan.Discs.Sum(d => d.Files.Count):N0} files");
        stdout.WriteLine($"  Discs:            {plan.Discs.Count}" + (plan.ParityDiscCount > 0 && plan.Discs.Count > 0 ? $" data + {plan.ParityDiscCount} parity ({plan.TotalDiscCount} total)" : string.Empty));
        foreach (var d in plan.Discs)
        {
            var s = staged.Count >= d.DiscNumber ? staged[d.DiscNumber - 1] : null;
            string where = s != null ? $"  -> {s.DiscRoot}" : string.Empty;
            string recovery = s?.Recovery is { } r
                ? $", recovery {r.RecoverySectors * 2048.0 / 1048576:N1} MB ({r.RedundancyPercent:N0} %, survives {r.RecoveryBlocks:N0} damaged blocks of {r.BlockSize / 1024:N0} KB)"
                : string.Empty;
            stdout.WriteLine($"    Disc {d.DiscNumber:D2}: {d.Files.Count:N0} files, {d.DataBytes / 1048576.0:N1} MB{recovery}{where}");
        }
        if (plan.ParityDiscCount > 0 && plan.Discs.Count > 0)
        {
            for (int p = 0; p < plan.ParityDiscCount; p++)
            {
                int discNum = plan.Discs.Count + 1 + p;
                var s = staged.Count >= discNum ? staged[discNum - 1] : null;
                string where = s != null ? $"  -> {s.DiscRoot}" : string.Empty;
                stdout.WriteLine($"    Disc {discNum:D2}: parity {p + 1}/{plan.ParityDiscCount} (cross-disc RS){where}");
            }
        }
        if (!planOnly && stagedDiscs.Count > 0)
        {
            stdout.WriteLine($"  Burn each disc with: archive-burn --drive <id> --disc \"{stagedDiscs[0]}\" [--native]");
            stdout.WriteLine("  Check a burned disc later with: archive-verify --path <drive letter>");
        }
        return 0;
    }

    /// <summary>
    /// archive-burn --drive &lt;id&gt; --disc &lt;staged disc folder&gt; [--speed] [--no-verify | --strict-verify] [--overwrite]
    /// [--native [--test-write | --test-first] [--log-dir]] [--json]: burns one disc staged by archive-stage the way the
    /// Archive screen's Burn button does (ISO 9660 + Joliet + UDF, label from the disc's index, disc closed). One disc
    /// per run, because each archive disc needs its own blank in the drive. The read-back rules are those of burn-data:
    /// read back unless --no-verify, always with --native, never for a test write.
    /// </summary>
    private static async Task<int> HandleArchiveBurnAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        string discFolder = cmd.GetRequiredOption("disc", "--disc <staged disc folder>");
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine);
        bool streamingWrite = ParseStreamingWrite(cmd, nativeEngine);
        bool strictVerify = cmd.HasFlag("strict-verify"); // every read forced from the disc; implies a read-back
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null));
        bool overwrite = cmd.HasFlag("overwrite");

        if (!Directory.Exists(discFolder))
        {
            throw new DirectoryNotFoundException($"Staged disc folder does not exist: {discFolder}");
        }
        string indexPath = Path.Combine(discFolder, ArchiveManifest.IndexFileName);
        if (!File.Exists(indexPath))
        {
            throw new CommandLineException(
                $"'{discFolder}' is not a staged archive disc (no {ArchiveManifest.IndexFileName}). Stage it with archive-stage first, or burn a plain folder with burn-data.");
        }
        ArchiveIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<ArchiveIndex>(await File.ReadAllTextAsync(indexPath).ConfigureAwait(false),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new CommandLineException($"{ArchiveManifest.IndexFileName} in '{discFolder}' is unreadable: {ex.Message}");
        }
        if (index == null || string.IsNullOrWhiteSpace(index.SetId) || index.DiscNumber < 1)
        {
            throw new CommandLineException($"{ArchiveManifest.IndexFileName} in '{discFolder}' has no set ID or disc number.");
        }

        string label = ArchiveManifest.VolumeLabel(index.SetId, index.DiscNumber);
        var recorder = FindRecorder(deviceService, driveId);
        var mediaStatus = recorder.QueryMediaStatus();
        var speedDesc = ParseSpeed(cmd.GetOption("speed"), recorder, mediaStatus.MediaType);
        var root = CompilationFolderImporter.ImportAsRoot(discFolder, strict: true);

        var archivePostAction = ParsePostBurnAction(cmd);
        bool archiveDisableBufe = cmd.HasFlag("no-burnfree");
        DvdBookType? archiveBookType = ParseOptionalBookType(cmd);
        bool archiveRequireBookType = cmd.HasFlag("require-book-type");

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = label,
            Speed = speedDesc,
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = true,
            OverwriteExistingData = overwrite,
            DisableBufferUnderrunFree = archiveDisableBufe,
            StreamingWrite = streamingWrite,
            RequestedBookType = archiveBookType,
            RequireBookType = archiveRequireBookType
        };
        var buildOptions = new IsoBuildOptions(
            VolumeLabel: label,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: null);

        bool healthCertificate = cmd.HasFlag("health-certificate");
        if (healthCertificate && testWrite)
        {
            throw new CommandLineException("--health-certificate describes a finished disc: it cannot go with --test-write.");
        }
        string? mediaId = healthCertificate ? recorder.WithManufacturerId(mediaStatus).MediaManufacturerId : null;

        var phases = new PhaseTimingProgress(cmd.IsJson ? null : stdout);
        var curve = new TransferCurveRecorder(phases);
        var burnService = CreateDataBurnService(nativeEngine ?? burnEngine);
        var result = await burnService.BurnCompilationAsync(recorder, root, burnOptions, buildOptions, curve).ConfigureAwait(false);
        phases.Complete();
        ExecutePostBurnActionIfSucceeded(archivePostAction, result.Success, recorder);

        string? certificateJson = null;
        string? certificateError = null;
        if (healthCertificate && result.Success)
        {
            try
            {
                var scan = TryScanAfterBurn(recorder, mediaStatus.MediaType, cmd.IsJson ? null : stdout);
                var now = DateTime.UtcNow;
                var certificate = new DiscHealthCertificate(
                    DiscHealthCertificate.CurrentSchemaVersion,
                    new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero),
                    index.SetId, index.DiscNumber, label,
                    $"{recorder.VendorId} {recorder.ProductId}".Trim(), mediaStatus.MediaType.ToString(), mediaId,
                    result.SectorsWritten, Math.Round(result.AverageSpeedMultiplier, 2), result.Verified, result.VerificationNote,
                    curve.Curve, scan == null ? null : DiscHealthCertificate.ScanFrom(scan));
                string parent = Path.GetDirectoryName(Path.GetFullPath(discFolder).TrimEnd('\\', '/')) ?? ".";
                string stem = $"disc{index.DiscNumber:D2}";
                certificateJson = Path.Combine(parent, $"{stem}.{DiscHealthCertificate.JsonFileSuffix}");
                certificate.Save(certificateJson, Path.Combine(parent, $"{stem}.{DiscHealthCertificate.SvgFileSuffix}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                certificateJson = null;
                certificateError = ex.Message;
            }
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                healthCertificate = certificateJson,
                healthCertificateError = certificateError,
                discFolder = Path.GetFullPath(discFolder),
                setId = index.SetId,
                discNumber = index.DiscNumber,
                discCount = index.DiscCount,
                volumeLabel = label,
                sectorsWritten = result.SectorsWritten,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                verified = result.Verified,
                verificationNote = result.VerificationNote,
                bookTypeNote = result.BookTypeNote,
                warning = result.Warning,
                directWrite = NativeDataJson(nativeEngine),
                error = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine($"Archive disc {index.DiscNumber} of {index.DiscCount} burned:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {label}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "Passed" : "No")}");
            if (result.VerificationNote != null) stdout.WriteLine($"  Verify Note:    {result.VerificationNote}");
            if (result.BookTypeNote != null) stdout.WriteLine($"  Book Type:      {result.BookTypeNote}");
            if (result.Warning != null) stdout.WriteLine($"  Warning:        {result.Warning}");
            WriteNativeDataSummary(stdout, nativeEngine);
            phases.WriteSummary(stdout);
            if (certificateJson != null) stdout.WriteLine($"  Health certificate: {certificateJson} (and the .svg chart next to it)");
            if (certificateError != null) stdout.WriteLine($"  Health certificate not written: {certificateError}");
            if (index.DiscNumber < index.DiscCount)
            {
                stdout.WriteLine($"  Next: insert a blank disc and burn disc{index.DiscNumber + 1:D2} the same way.");
            }
            return 0;
        }

        stderr.WriteLine($"Archive disc {index.DiscNumber} burn failed: {result.ErrorMessage}");
        WriteNativeDataSummary(stderr, nativeEngine);
        phases.WriteSummary(stderr);
        return 1;
    }

    /// <summary>
    /// Reads a freshly burned CD once for the health certificate. Null for a virtual drive, a disc that is not a CD,
    /// or a drive that cannot be read this way: the certificate then has no per-minute counts, and says so.
    /// </summary>
    private static DiscHealthReport? TryScanAfterBurn(IDiscRecorderBackend recorder, DiscMediaType mediaType, TextWriter? progressOut)
    {
        if (recorder.IsVirtual || string.IsNullOrWhiteSpace(recorder.VolumePath) || !mediaType.IsCd())
        {
            return null;
        }
        try
        {
            using var transport = Win32ScsiTransport.Open(recorder.VolumePath, FileAccess.ReadWrite);
            progressOut?.WriteLine("  Reading the disc once for the health certificate...");
            return new DiscHealthScanner(transport).Scan();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            progressOut?.WriteLine($"  The disc could not be read for the certificate: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// recover (--drive &lt;id&gt; | --image &lt;file.iso&gt;) --out &lt;folder&gt; [--retries N] [--keep-image] [--json]:
    /// reads an archive disc with retries (unreadable sectors zero-filled), copies its files out, rebuilds damaged
    /// ones from the disc's PAR2 recovery data and checks them against its manifest. Exit code 0 only when every
    /// file checks out, 2 when some could not be recovered.
    /// </summary>
    private static async Task<int> HandleRecoverAsync(
        CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        string output = Path.GetFullPath(cmd.GetRequiredOption("out", "--out <folder>"));
        string? image = cmd.GetOption("image");
        string? driveId = cmd.GetOption("drive");
        if ((image == null) == (driveId == null))
        {
            throw new CommandLineException("Give either --drive <id> to read a disc, or --image <file.iso> for an image of one.");
        }

        string retriesOption = cmd.GetOption("retries") ?? "5";
        if (!int.TryParse(retriesOption, NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) || retries is < 0 or > 100)
        {
            throw new CommandLineException($"Invalid --retries '{retriesOption}'. Expected 0 to 100.");
        }

        var progress = cmd.IsJson ? null : new Progress<string>(message => stdout.WriteLine($"  {message}"));
        IReadOnlyList<int> unreadable = Array.Empty<int>();
        int rawRecoveredSectors = 0;
        string? ripped = null;
        if (driveId != null)
        {
            var recorder = FindRecorder(deviceService, driveId);
            ripped = output.TrimEnd(Path.DirectorySeparatorChar) + ".read.iso";
            if (!cmd.IsJson)
            {
                stdout.WriteLine($"Reading the disc in {driveId} (up to {retries} retries per bad sector)...");
            }
            var rip = await new DiscRipperService().RipToIsoAsync(recorder, burnEngine, ripped,
                new RipOptions { MaxRetriesPerSector = retries, ContinueOnUnreadableSector = true, RawSectorSalvage = true }).ConfigureAwait(false);
            if (!File.Exists(ripped))
            {
                throw new CommandLineException("The disc could not be read: " + (rip.ErrorMessage ?? "unknown error"));
            }
            unreadable = rip.BadSectorLbas;
            rawRecoveredSectors = rip.RawRecoveredSectorsCount;
            image = ripped;
        }
        else if (!File.Exists(image))
        {
            throw new CommandLineException($"Image not found: '{image}'.");
        }
        else if (string.Equals(Path.GetExtension(image), ".cue", StringComparison.OrdinalIgnoreCase))
        {
            (ripped, unreadable) = await ExtractCueDataTrackToTempIsoAsync(image, output).ConfigureAwait(false);
            image = ripped;
        }

        var report = await ArchiveRecoveryService.RecoverFromImageAsync(
            image!, unreadable, output, Par2Runner.FindPar2Executable(), progress).ConfigureAwait(false);
        if (rawRecoveredSectors > 0)
        {
            report = report with { RawRecoveredSectors = rawRecoveredSectors };
        }

        string reportPath = output.TrimEnd(Path.DirectorySeparatorChar) + "-recovery-report.txt";
        await File.WriteAllTextAsync(reportPath, report.ToText()).ConfigureAwait(false);
        if (ripped != null && !cmd.HasFlag("keep-image"))
        {
            try { File.Delete(ripped); } catch (IOException) { }
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = report.FullyRecovered,
                output = report.OutputFolder,
                report = reportPath,
                image = ripped != null && cmd.HasFlag("keep-image") ? ripped : image,
                unreadableSectors = report.UnreadableSectors.Count,
                damagedFiles = report.DamagedFiles,
                repairedFiles = report.RepairedFiles,
                stillMissing = report.StillMissing,
                stillCorrupt = report.StillCorrupt,
                filesChecked = report.FilesChecked,
                hadRecoveryData = report.HadRecoveryData,
                problem = report.Problem
            });
        }
        else
        {
            stdout.Write(report.ToText());
            stdout.WriteLine($"Report saved to: {reportPath}");
        }
        return report.FullyRecovered ? 0 : 2;
    }

    private static async Task<(string TempIsoPath, IReadOnlyList<int> BadSectors)> ExtractCueDataTrackToTempIsoAsync(
        string cuePath, string outputBase)
    {
        CueSheet sheet = CueSheetParser.ParseFile(cuePath);
        if (sheet.Files.Count == 0 || sheet.Files[0].Tracks.Count == 0)
        {
            throw new CommandLineException($"The CUE sheet has no tracks: '{cuePath}'.");
        }

        CueFileEntry file = sheet.Files[0];
        CueTrack track1 = file.Tracks[0];
        int sectorSize = track1.DataType switch
        {
            CueTrackDataType.Mode1_2048 => OpticalConstants.DataSectorSize,
            CueTrackDataType.Mode1_2352 or CueTrackDataType.Mode2_2352 => OpticalConstants.RawSectorSize,
            CueTrackDataType.Mode2_2336 => 2336,
            _ => throw new CommandLineException($"Track 1 of '{Path.GetFileName(cuePath)}' is {track1.DataType}, not a data track."),
        };

        string cueDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? string.Empty;
        string binPath = Path.IsPathRooted(file.FileName) ? file.FileName : Path.Combine(cueDir, file.FileName);
        if (!File.Exists(binPath))
        {
            throw new CommandLineException($"Data track file not found: '{binPath}'.");
        }

        int index01 = track1.Indices.FirstOrDefault(i => i.IndexNumber == 1)?.Position.ToFrameCount() ?? 0;
        long startByte = (long)index01 * sectorSize;
        long endByte = new FileInfo(binPath).Length;
        if (file.Tracks.Count > 1)
        {
            CueTrack next = file.Tracks[1];
            int nextFrame = next.Indices.FirstOrDefault(i => i.IndexNumber == 0)?.Position.ToFrameCount()
                ?? next.Indices.FirstOrDefault(i => i.IndexNumber == 1)?.Position.ToFrameCount()
                ?? 0;
            endByte = (long)nextFrame * OpticalConstants.RawSectorSize;
        }

        int sectors = (int)Math.Max(0, (endByte - startByte) / sectorSize);
        if (sectors == 0)
        {
            throw new CommandLineException($"Track 1 of '{Path.GetFileName(cuePath)}' is empty.");
        }

        string tempIso = outputBase.TrimEnd(Path.DirectorySeparatorChar) + ".cue-track1.iso";
        var bad = new List<int>();
        byte[] raw = new byte[sectorSize];
        byte[] user2048 = new byte[OpticalConstants.DataSectorSize];

        await using var bin = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false);
        await using var iso = new FileStream(tempIso, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: false);
        bin.Position = startByte;
        for (int lba = 0; lba < sectors; lba++)
        {
            bin.ReadExactly(raw);
            if (sectorSize == OpticalConstants.RawSectorSize)
            {
                bool intact = false;
                try
                {
                    RawSectorCodec.Extract2048From2352(raw, user2048, out intact);
                }
                catch (InvalidDataException)
                {
                    intact = false;
                }

                if (!intact)
                {
                    Array.Clear(user2048);
                    bad.Add(lba);
                }
            }
            else if (sectorSize == 2336)
            {
                Buffer.BlockCopy(raw, 8, user2048, 0, OpticalConstants.DataSectorSize);
            }
            else
            {
                Buffer.BlockCopy(raw, 0, user2048, 0, OpticalConstants.DataSectorSize);
            }

            iso.Write(user2048);
        }

        return (tempIso, bad);
    }

    private static async Task<int> HandleArchiveVerifyAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string path = cmd.GetRequiredOption("path", "--path <folder|drive>");
        if (path.Length == 2 && path[1] == ':')
        {
            path += "\\";
        }

        var progress = cmd.IsJson ? null : new Progress<string>(_ => { });
        var result = await ArchiveManifest.VerifyAsync(path, progress).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.IsIntact,
                setId = result.SetId,
                discNumber = result.DiscNumber,
                discCount = result.DiscCount,
                filesChecked = result.FilesChecked,
                missing = result.Missing,
                corrupt = result.Corrupt,
                hasRecoveryData = result.HasRecoveryData
            });
            return result.IsIntact ? 0 : 1;
        }

        stdout.WriteLine(result.IsIntact ? "Archive disc is intact:" : "Archive disc has PROBLEMS:");
        if (result.SetId != null)
        {
            stdout.WriteLine($"  Set ID:           {result.SetId} (disc {result.DiscNumber} of {result.DiscCount})");
        }
        stdout.WriteLine($"  Files Checked:    {result.FilesChecked:N0}");
        stdout.WriteLine($"  Missing:          {result.Missing.Count}");
        stdout.WriteLine($"  Corrupt:          {result.Corrupt.Count}");
        stdout.WriteLine($"  Recovery Data:    {(result.HasRecoveryData ? "present (PAR2)" : "none")}");
        foreach (var m in result.Missing) stdout.WriteLine($"    MISSING  {m}");
        foreach (var c in result.Corrupt) stdout.WriteLine($"    CORRUPT  {c}");
        if (!result.IsIntact && result.HasRecoveryData)
        {
            // -B. is required: the recovery set stores paths relative to the disc root, not to the RECOVERY folder.
            stdout.WriteLine("  To repair: copy the disc to a folder, open a terminal in that folder and run:");
            stdout.WriteLine("    par2 repair -B. RECOVERY\\recovery.par2");
            stdout.WriteLine("  (par2 keeps each damaged original as <name>.1; delete those once verified.)");
        }
        return result.IsIntact ? 0 : 1;
    }

    private static async Task<int> HandleArchiveRebuildAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string discsRaw = cmd.GetOption("discs") ?? cmd.GetOption("from")
            ?? throw new CommandLineException("Missing required option --discs <path1;path2;...> (or --from <paths>).");
        string discNumOpt = cmd.GetRequiredOption("disc", "--disc <missing disc number>");
        if (!int.TryParse(discNumOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int missingDisc) || missingDisc < 1)
        {
            throw new CommandLineException($"Invalid --disc '{discNumOpt}'. Expected a positive disc number (1..N+K).");
        }
        string output = Path.GetFullPath(cmd.GetOption("out") ?? cmd.GetOption("output")
            ?? throw new CommandLineException("Missing required option --out <folder>."));

        var paths = discsRaw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToList();
        if (paths.Count == 1 && Directory.Exists(paths[0]) &&
            !File.Exists(Path.Combine(paths[0], ArchiveManifest.IndexFileName)) &&
            !File.Exists(Path.Combine(paths[0], ArchiveParitySet.ParitySetFileName)))
        {
            var subDiscs = Directory.EnumerateDirectories(paths[0], "disc*")
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (subDiscs.Count > 0)
            {
                paths = subDiscs;
            }
        }

        var progress = cmd.IsJson ? null : new Progress<string>(msg => stdout.WriteLine($"  {msg}"));
        var report = await ArchiveRecoveryService.RebuildMissingDiscAsync(paths, missingDisc, output, progress).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = report.Success,
                setId = report.SetId,
                rebuiltDiscNumber = report.RebuiltDiscNumber,
                isParityDisc = report.IsParityDisc,
                output = report.OutputFolder,
                filesRestored = report.FilesRestored,
                bytesRestored = report.BytesRestored,
                sourceDiscsUsed = report.SourceDiscNumbersUsed,
                verifiedFiles = report.VerifiedFiles,
                failedFiles = report.FailedFiles,
                problem = report.Problem
            });
            return report.Success ? 0 : 2;
        }

        stdout.WriteLine(report.Success
            ? $"Rebuilt disc {report.RebuiltDiscNumber} of set {report.SetId}:"
            : $"Failed to rebuild disc {report.RebuiltDiscNumber}:");
        stdout.WriteLine($"  Output:         {report.OutputFolder}");
        stdout.WriteLine($"  Files Restored: {report.FilesRestored:N0} ({report.BytesRestored:N0} bytes)");
        stdout.WriteLine($"  Sources Used:   {string.Join(", ", report.SourceDiscNumbersUsed)}");
        if (report.Problem != null)
        {
            stdout.WriteLine($"  Problem:        {report.Problem}");
        }
        return report.Success ? 0 : 2;
    }

    /// <summary>
    /// <c>verify --cue</c>: checks a recorded audio or mixed-mode CD against the <c>.cue</c> it was burned from, with the
    /// read-back check a direct burn ends with, and writes nothing. The tracks are prepared as <c>burn-audio --native</c>
    /// prepares them, so a disc burned that way on this drive is compared sample for sample.
    /// </summary>
    private static async Task<int> HandleVerifyCueAsync(CommandLineArgs cmd, string cuePath, IDiscRecorderBackend recorder,
        TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(cuePath))
        {
            throw new FileNotFoundException($"CUE sheet not found: {cuePath}", cuePath);
        }

        using var decoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var engine = new NativeAudioBurnEngine(cmd.GetOption("log-dir"));
        var service = new AudioCdBurnService(engine, decoder);
        // The album text starts as burn-audio starts it (--title / --artist, else its defaults), so the CD-Text compared
        // is the CD-Text that command wrote.
        var compilation = new AudioCdCompilation
        {
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            AlbumTitle = cmd.GetOption("title") ?? "Audio CD",
            AlbumPerformer = cmd.GetOption("artist") ?? "Various Artists"
        };
        compilation.ImportCueSheet(cuePath, service.AudioDecoder);
        compilation.IncludeCdText = !cmd.HasFlag("no-cd-text");

        var result = await service.VerifyAsync(recorder, compilation, new AudioBurnOptions
        {
            UseDiscAtOnce = true,
            WriteCdText = compilation.IncludeCdText,
            MediaCatalogNumber = compilation.UpcEan,
            KeepIndexMarks = true,
            WriteOffsetSamples = ParseWriteOffset(cmd.GetOption("write-offset"), native: true)
        }).ConfigureAwait(false);

        var v = engine.LastVerification;
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                cueSheet = Path.GetFullPath(cuePath),
                tocChecked = v?.TocChecked,
                cdTextChecked = v?.CdTextChecked,
                audioCompared = v?.AudioCompared,
                dataTrackCompared = v?.DataTrackCompared,
                pictureAreaCompared = v is { HasPictureArea: true } ? v.PictureAreaCompared : (bool?)null,
                message = v?.Message ?? result.ErrorMessage,
                offsetSamples = v?.OffsetSamples,
                samplesCompared = v?.SamplesCompared,
                hardToReadSectors = v?.HardToReadSectors ?? 0,
                writtenWrongSectors = v?.WrittenWrongSectors ?? 0,
                unreadableSectors = v?.UnreadableSectors ?? 0,
                warning = v?.Warning,
                writeOffset = engine.LastWriteOffsetPlan?.Explanation,
                commandLog = engine.LastLogPath,
                error = result.Success ? null : result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        (result.Success ? stdout : stderr).WriteLine(result.Success
            ? $"Disc check PASSED: {v?.Message}"
            : $"Disc check FAILED: {v?.Message ?? result.ErrorMessage}");
        if (v?.Warning is { } warning)
        {
            stdout.WriteLine($"  Warning: {warning}");
        }
        if (engine.LastLogPath != null)
        {
            stdout.WriteLine($"  Command log: {engine.LastLogPath}");
        }
        return result.Success ? 0 : 1;
    }

    private static async Task<int> HandleVerifyAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        if (cmd.GetOption("cue") is { } cuePath)
        {
            return await HandleVerifyCueAsync(cmd, cuePath, FindRecorder(deviceService, driveId), stdout, stderr).ConfigureAwait(false);
        }
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso> or --cue <file.cue>");

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var verifier = new DiscVerificationService();

        // --strict-verify forces every read from the disc; without it the saved setting decides.
        bool? strict = cmd.HasFlag("strict-verify") ? true : null;
        VerificationResult result;
        if (LegacyDiscImageConverter.IsLegacyImageFile(isoPath))
        {
            await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(isoPath);
            result = await verifier.VerifyDiscAgainstStreamAsync(recorder, burnEngine, legacyStream, raw2352Mode: false, strict: strict).ConfigureAwait(false);
        }
        else
        {
            result = await verifier.VerifyDiscAgainstIsoFileAsync(recorder, burnEngine, isoPath, strict: strict).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.IsMatch,
                isMatch = result.IsMatch,
                driveId = recorder.UniqueId,
                isoPath = Path.GetFullPath(isoPath),
                totalSectorsVerified = result.TotalSectorsVerified,
                mismatchedLbaCount = result.MismatchedLbas.Count,
                mismatchedLbas = result.MismatchedLbas,
                corruptedFiles = result.CorruptedFiles,
                expectedSha256 = result.ExpectedSha256,
                actualSha256 = result.ActualSha256,
                expectedCrc32 = result.ExpectedCrc32,
                actualCrc32 = result.ActualCrc32,
                mediaSurfaceReadbackEnforced = result.MediaSurfaceReadbackEnforced,
                readbackNote = result.ReadbackNote,
                totalTime = result.TotalTime.ToString(),
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.IsMatch ? 0 : 1;
        }

        if (result.IsMatch)
        {
            stdout.WriteLine("Disc verification PASSED:");
            stdout.WriteLine($"  Drive:                   {recorder.UniqueId}");
            stdout.WriteLine($"  ISO File:                {Path.GetFullPath(isoPath)}");
            stdout.WriteLine($"  Total Sectors Verified:  {result.TotalSectorsVerified:N0}");
            stdout.WriteLine($"  Expected SHA-256:        {result.ExpectedSha256}");
            stdout.WriteLine($"  Actual SHA-256:          {result.ActualSha256}");
            stdout.WriteLine($"  Media Readback Enforced: {(result.MediaSurfaceReadbackEnforced ? "Yes" : "No")}");
            if (result.ReadbackNote != null) stdout.WriteLine($"  Readback Note:           {result.ReadbackNote}");
            stdout.WriteLine($"  Duration:                {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine("Disc verification FAILED:");
        stderr.WriteLine($"  Mismatches:              {result.MismatchedLbas.Count} sector(s)");
        if (result.CorruptedFiles.Count > 0)
        {
            stderr.WriteLine($"  Corrupted Files:         {string.Join(", ", result.CorruptedFiles)}");
        }
        stderr.WriteLine($"  Expected SHA-256:        {result.ExpectedSha256}");
        stderr.WriteLine($"  Actual SHA-256:          {result.ActualSha256}");
        stderr.WriteLine($"  Media Readback Enforced: {(result.MediaSurfaceReadbackEnforced ? "Yes" : "No")}");
        if (result.ReadbackNote != null) stderr.WriteLine($"  Readback Note:           {result.ReadbackNote}");
        return 1;
    }

    private static async Task<int> HandleEraseAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        bool fullErase = cmd.HasFlag("full");

        var recorder = FindRecorder(deviceService, driveId);
        var eraseService = new DiscEraseService(burnEngine);

        var result = await eraseService.EraseAsync(recorder, burnEngine, fullErase).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                fullErase = fullErase,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Disc erased successfully:");
            stdout.WriteLine($"  Drive:      {recorder.UniqueId}");
            stdout.WriteLine($"  Mode:       {(fullErase ? "Full erase" : "Quick erase")}");
            stdout.WriteLine($"  Duration:   {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"Erase failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleScanAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = ResolveTargetDriveId(cmd);
        string? blockSizeStr = cmd.GetOption("block-size");
        string? csvOutputPath = cmd.GetOption("csv") ?? cmd.GetOption("output");

        int blockSize = 256;
        if (blockSizeStr != null)
        {
            if (string.IsNullOrWhiteSpace(blockSizeStr) ||
                !int.TryParse(blockSizeStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bs) ||
                bs <= 0)
            {
                throw new CommandLineException($"Invalid --block-size '{blockSizeStr}'. Expected a positive integer number of sectors.");
            }
            blockSize = bs;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var scanService = new SurfaceScanService(burnEngine);
        var scanOptions = new SurfaceScanOptions { BlockSizeSectors = blockSize };

        var report = await scanService.ScanAsync(recorder, burnEngine, scanOptions).ConfigureAwait(false);

        string? fullCsvPath = null;
        if (!string.IsNullOrWhiteSpace(csvOutputPath))
        {
            fullCsvPath = Path.GetFullPath(csvOutputPath);
            await report.ExportCsvAsync(fullCsvPath).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = report.DamagedBlocks == 0,
                driveId = recorder.UniqueId,
                totalSectorsScanned = report.TotalSectorsScanned,
                goodBlocks = report.GoodBlocks,
                slowBlocks = report.SlowBlocks,
                damagedBlocks = report.DamagedBlocks,
                minSpeedKbPerSec = report.MinSpeedKbPerSec,
                maxSpeedKbPerSec = report.MaxSpeedKbPerSec,
                averageSpeedKbPerSec = report.AverageSpeedKbPerSec,
                csvPath = fullCsvPath
            };

            WriteJsonResult(stdout, jsonResult);
            return report.DamagedBlocks == 0 ? 0 : 1;
        }

        stdout.WriteLine($"Surface scan report for '{recorder.UniqueId}':");
        stdout.WriteLine($"  Total Sectors:      {report.TotalSectorsScanned:N0}");
        stdout.WriteLine($"  Good Blocks:        {report.GoodBlocks:N0}");
        stdout.WriteLine($"  Slow Blocks:        {report.SlowBlocks:N0}");
        stdout.WriteLine($"  Damaged Blocks:     {report.DamagedBlocks:N0}");
        stdout.WriteLine($"  Min Read Rate:      {report.MinSpeedKbPerSec:F1} KiB/s");
        stdout.WriteLine($"  Max Read Rate:      {report.MaxSpeedKbPerSec:F1} KiB/s");
        stdout.WriteLine($"  Average Read Rate:  {report.AverageSpeedKbPerSec:F1} KiB/s");
        if (fullCsvPath != null)
        {
            stdout.WriteLine($"  CSV Export:         {fullCsvPath}");
        }

        return report.DamagedBlocks == 0 ? 0 : 1;
    }

    private static async Task<int> HandleQualityScanAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string? outJson = cmd.GetOption("out");
        string? outSvg = cmd.GetOption("svg");
        string? attachHealth = cmd.GetOption("attach-health") ?? cmd.GetOption("health-json");
        bool forceOld = cmd.HasFlag("force-old");

        int? speedMultiplier = null;
        if (cmd.GetOption("speed") is { } speedRaw && !string.Equals(speedRaw, "max", StringComparison.OrdinalIgnoreCase))
        {
            string trimmed = speedRaw.Trim().TrimEnd('x', 'X');
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSpeed) || parsedSpeed <= 0)
            {
                throw new CommandLineException($"Invalid --speed '{speedRaw}'. Expected a positive integer multiplier (e.g. 8 or 8x) or max.");
            }
            speedMultiplier = parsedSpeed;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var scanOptions = new QualityScanOptions
        {
            ReadSpeedMultiplier = speedMultiplier,
            ForceLegacyProtocol = forceOld
        };

        QualityScanReport report;
        var service = new QualityScanService();
        if (recorder is VirtualDiscRecorder virtualRecorder)
        {
            var driver = new VirtualQualityScanDriver(virtualRecorder);
            report = await service.RunScanAsync(driver, scanOptions).ConfigureAwait(false);
        }
        else
        {
            string? mediaId = recorder.WithManufacturerId(recorder.QueryMediaStatus()).MediaManufacturerId;
            using var driver = new MediaTekLiteOnQualityDriver(
                Win32ScsiTransport.Open(NativeAudioBurnEngine.DevicePathFor(recorder), FileAccess.ReadWrite),
                ownsTransport: true,
                mediaId: mediaId);
            report = await service.RunScanAsync(driver, scanOptions).ConfigureAwait(false);
        }

        string? fullJsonPath = null;
        if (!string.IsNullOrWhiteSpace(outJson))
        {
            fullJsonPath = Path.GetFullPath(outJson);
            await report.SaveJsonAsync(fullJsonPath).ConfigureAwait(false);
        }

        string? fullSvgPath = null;
        if (!string.IsNullOrWhiteSpace(outSvg))
        {
            fullSvgPath = Path.GetFullPath(outSvg);
            await report.SaveSvgAsync(fullSvgPath).ConfigureAwait(false);
        }

        string? attachedHealthPath = null;
        if (!string.IsNullOrWhiteSpace(attachHealth) && report.IsSupported && report.Status == QualityScanStatus.Completed)
        {
            attachedHealthPath = Path.GetFullPath(attachHealth);
            await service.AttachToHealthCertificateAsync(attachedHealthPath, report).ConfigureAwait(false);
        }

        bool succeeded = report.IsSupported && report.Status == QualityScanStatus.Completed;
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = succeeded,
                supported = report.IsSupported,
                experimentalHardware = report.IsExperimentalHardwareScan,
                status = report.Status.ToString(),
                message = report.StatusMessage,
                driveId = recorder.UniqueId,
                vendor = report.DriveVendor,
                product = report.DriveProduct,
                protocol = report.Protocol.ToString(),
                mediaKind = report.MediaKind.ToString(),
                totalDiscSectors = report.TotalDiscSectors,
                sectorsScanned = report.Summary.TotalSectorsScanned,
                intervalCount = report.Summary.IntervalCount,
                grade = report.Summary.Grade.ToString(),
                gradeReason = report.Summary.GradeReason,
                averageSpeedX = report.Summary.AverageSpeedX,
                maxSpeedX = report.Summary.MaxSpeedX,
                cd = report.MediaKind == QualityScanMediaKind.Cd ? new
                {
                    totalC1 = report.Summary.TotalC1,
                    totalC2 = report.Summary.TotalC2,
                    totalCu = report.Summary.TotalCu,
                    maxC1PerSecond = report.Summary.MaxC1PerSecond,
                    maxC2PerSecond = report.Summary.MaxC2PerSecond,
                    averageC1PerSecond = report.Summary.AverageC1PerSecond
                } : null,
                dvd = report.MediaKind == QualityScanMediaKind.Dvd ? new
                {
                    totalPie = report.Summary.TotalPie,
                    totalPif = report.Summary.TotalPif,
                    totalPof = report.Summary.TotalPof,
                    maxPiePer8Ecc = report.Summary.MaxPiePer8Ecc,
                    maxPifPerEcc = report.Summary.MaxPifPerEcc,
                    averagePiePer8Ecc = report.Summary.AveragePiePer8Ecc
                } : null,
                bd = report.MediaKind == QualityScanMediaKind.BluRay ? new
                {
                    totalLdc = report.Summary.TotalLdc,
                    totalBis = report.Summary.TotalBis,
                    maxLdcPerCluster = report.Summary.MaxLdcPerCluster,
                    maxBisPerCluster = report.Summary.MaxBisPerCluster
                } : null,
                outJson = fullJsonPath,
                outSvg = fullSvgPath,
                attachedHealthJson = attachedHealthPath
            });
            return succeeded ? 0 : 1;
        }

        if (!succeeded)
        {
            stderr.WriteLine(report.Describe());
            return 1;
        }

        stdout.WriteLine(report.Describe());
        if (fullJsonPath != null) stdout.WriteLine($"  JSON Report: {fullJsonPath}");
        if (fullSvgPath != null) stdout.WriteLine($"  SVG Graph:   {fullSvgPath}");
        if (attachedHealthPath != null) stdout.WriteLine($"  HEALTH.json: {attachedHealthPath}");
        return 0;
    }

    #endregion

    #region Helper Methods

    private static IDiscRecorderBackend FindRecorder(IDiscDeviceService deviceService, string driveId)
    {
        var recorder = deviceService.GetRecorderById(driveId)
            ?? deviceService.GetRecorders(includeVirtual: true).FirstOrDefault(r =>
                string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath?.TrimEnd('\\'), driveId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        if (recorder == null)
        {
            throw new CommandLineException($"Drive '{driveId}' not found. Use 'drives' command to list available drives.");
        }

        return recorder;
    }

    /// <summary>
    /// The write speed for a burn. No <c>--speed</c> (or <c>recommended</c>): <see cref="WriteSpeedPolicy.Recommended"/>,
    /// the fastest speed the drive writes at a constant linear velocity for this disc, up to 16x on CD and 8x on DVD.
    /// <c>slowest</c> and <c>max</c> take the ends of the drive's list. A number such as <c>8</c> or <c>8x</c> is sent
    /// as asked; the drive rounds it to a speed it supports. Returns null when the drive should keep its own speed
    /// (it reports no speeds).
    /// </summary>
    private static WriteSpeedDescriptor? ParseSpeed(string? speedStr, IDiscRecorderBackend recorder, DiscMediaType mediaType, bool isAudio = false)
    {
        string text = speedStr?.Trim() ?? string.Empty;
        int? preference = text.ToLowerInvariant() switch
        {
            "" or "recommended" => WriteSpeedPolicy.Recommended,
            "slowest" or "min" => WriteSpeedPolicy.Slowest,
            "max" or "maximum" => WriteSpeedPolicy.Maximum,
            _ => null
        };
        if (preference is { } p)
        {
            return WriteSpeedPolicy.Choose(WriteSpeedPolicy.ForDisc(DriveSpeeds(recorder), mediaType), p);
        }

        string cleaned = text.EndsWith('x') || text.EndsWith('X') ? text[..^1].Trim() : text; // one optional x: "8xx" is a typo
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) && multiplier > 0 && multiplier <= 100)
        {
            return WriteSpeedDescriptor.FromMultiplier(multiplier, mediaType, isAudio);
        }

        // Before, an unreadable value silently meant "fastest"; a typo must not change how a disc is burned.
        throw new CommandLineException($"--speed '{speedStr}' is not a speed. Use a number such as 8 or 8x, 'recommended' (the default), 'slowest' or 'max'.");
    }

    /// <summary>
    /// <c>--write-offset</c>: absent or <c>auto</c> works the drive's write offset out (read offset from AccurateRip's
    /// list, combined offset from earlier burns); a number of samples is used as given; 0 writes the audio unshifted.
    /// </summary>
    private static int? ParseWriteOffset(string? text, bool native)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!native)
        {
            throw new CommandLineException("--write-offset needs --native: only the direct writer can correct the write offset.");
        }
        if (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int samples) && Math.Abs(samples) <= 5_000)
        {
            return samples;
        }
        throw new CommandLineException($"--write-offset '{text}' is not a number of samples between -5000 and +5000, or 'auto'.");
    }

    /// <summary>The drive's write speeds; none when this kind of drive cannot list them (it then keeps its own speed).</summary>
    private static IReadOnlyList<WriteSpeedDescriptor> DriveSpeeds(IDiscRecorderBackend recorder)
    {
        try
        {
            return recorder.GetCapabilities().SupportedWriteSpeeds ?? [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private static ElToritoBootConfiguration? BuildBootConfiguration(string? bootBiosPath, string? bootUefiPath, string? developerId)
    {
        BootCatalogEntrySpec? biosEntry = null;
        if (!string.IsNullOrWhiteSpace(bootBiosPath))
        {
            if (!File.Exists(bootBiosPath))
            {
                throw new FileNotFoundException($"BIOS boot image file not found: {bootBiosPath}", bootBiosPath);
            }
            var fi = new FileInfo(bootBiosPath);
            ushort sectors = ElToritoCatalogBuilder.ComputeSectorCount(BootPlatformId.X86Bios, fi.Length);
            biosEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(bootBiosPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(bootBiosPath));
        }

        BootCatalogEntrySpec? uefiEntry = null;
        if (!string.IsNullOrWhiteSpace(bootUefiPath))
        {
            if (!File.Exists(bootUefiPath))
            {
                throw new FileNotFoundException($"UEFI boot image file not found: {bootUefiPath}", bootUefiPath);
            }
            var fi = new FileInfo(bootUefiPath);
            ushort sectors = ElToritoCatalogBuilder.ComputeSectorCount(BootPlatformId.Efi, fi.Length);
            uefiEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.Efi,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(bootUefiPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(bootUefiPath));
        }

        string devId = !string.IsNullOrWhiteSpace(developerId) ? developerId : "CDBURNER";

        if (biosEntry != null && uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                AdditionalSectionEntries: new[] { uefiEntry },
                DeveloperId: devId);
        }

        if (biosEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                DeveloperId: devId);
        }

        if (uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: uefiEntry,
                DeveloperId: devId);
        }

        return null;
    }

    private static void PopulateDirectoryFromDisk(CompilationDirectory target, string sourceDiskDir, bool skipDvdIgnoredFiles = false)
    {
        CompilationFolderImporter.Import(target, sourceDiskDir, strict: false,
            skipDvdIgnoredFiles ? DvdInputNormalizer.IsIgnoredUselessFile : null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{(double)bytes / (1024 * 1024 * 1024):F2} GB";
        }
        if (bytes >= 1024 * 1024)
        {
            return $"{(double)bytes / (1024 * 1024):F1} MB";
        }
        if (bytes >= 1024)
        {
            return $"{(double)bytes / 1024:F1} KB";
        }
        return $"{bytes} B";
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine(LegalBanner.VersionNotice);
        stdout.WriteLine();
        stdout.WriteLine("Usage: ignisdisc-cli <command> [options]");
        stdout.WriteLine();
        stdout.WriteLine("Supported Commands:");
        stdout.WriteLine("  drives [--json]");
        stdout.WriteLine("      Lists physical and virtual optical drives and capabilities.");
        stdout.WriteLine("  drive-report --drive <id> [--json]");
        stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
        stdout.WriteLine("  media-info --drive <id> [--json]");
        stdout.WriteLine("      Shows inserted disc status, capacity, sessions, speeds.");
        stdout.WriteLine("  eject --drive <id> [--json]");
        stdout.WriteLine("      Opens the tray. A drive with a spring tray has to be closed by hand afterwards.");
        stdout.WriteLine("  load --drive <id> [--json]");
        stdout.WriteLine("      Closes the tray and waits until the drive is ready. On a drive that cannot close its own tray it says so and exits 1.");
        stdout.WriteLine("  virtual-mount --drive <id> (--blank <cd-r|cd-rw|dvd-r|dvd+r|dvd+rw|dvd-r-dl|bd-r|bd-re|bd-r-dl|bdxl> | --image <path>) [--virtual-state <path>] [--json]");
        stdout.WriteLine("      Mounts a blank virtual disc or populated disc image into a persistent virtual drive without touching physical hardware.");
        stdout.WriteLine("  virtual-eject --drive <id> [--virtual-state <path>] [--json]");
        stdout.WriteLine("      Ejects and clears mounted virtual media from a virtual drive.");
        stdout.WriteLine("  virtual-status [--drive <id>] [--virtual-state <path>] [--json]");
        stdout.WriteLine("      Reports virtual drive state, media geometry, and MMC profile without touching physical hardware.");
        stdout.WriteLine("  build-iso --input <folder> --output <file.iso> [--label <vol>] [--udf] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--fixed-time <unix|iso8601>] [--sha256sums] [--blake3sums] [--json]");
        stdout.WriteLine("      Compiles a folder into an ISO 9660 / Joliet / UDF filesystem image.");
        stdout.WriteLine("  burn-iso (--drive <id> | --drives <id1,id2,...>) --iso <file.iso|.nrg|.cdi|.mdf|.mds|.ccd|.img> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--no-burnfree] [--overwrite] [--book-type dvd-rom|...] [--post-action none|eject|sound|eject-sound|shutdown] [--telemetry-out <path>] [--windows-writer | --native [--test-write | --test-first] [--streaming] [--layer-break <sectors>] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Burns an ISO image directly to one or more optical drives simultaneously (--drives D:,E:). --streaming uses WRITE(12) Streaming (0x80) on BD-RE/DVD-RAM. --layer-break: sectors on the first layer of a DVD+R DL (multiple of 16). --telemetry-out writes burn telemetry as .svg or .json (or both if no extension). A DVD-RW is written with direct drive commands unless --windows-writer.");
        stdout.WriteLine("  burn-data --drive <id> --input <folder> [--label <vol>] [--speed <X|max>] [--udf] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--no-verify | --strict-verify] [--no-close] [--append] [--no-burnfree] [--overwrite] [--book-type dvd-rom|...] [--post-action none|eject|sound|eject-sound|shutdown] [--fixed-time <unix|iso8601>] [--sha256sums] [--blake3sums] [--fill-picture <file.png> [--disc-preset <id>]] [--telemetry-out <path>] [--windows-writer | --native [--test-write | --test-first] [--streaming] [--layer-break <sectors>] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Compiles a folder and burns it to optical media (--append merges and adds a session on an appendable disc). A DVD-RW is written with direct drive commands unless --windows-writer.");
        stdout.WriteLine("  burn-audio (--drive <id> | --plan-only) (--tracks <files|.cue|.m3u|.pls> | --tracks-file <file.json>) [--title <album>] [--artist <name>] [--songwriter <name>] [--upc <13digits>] [--speed <X|max>] [--dao] [--pregap <frames>] [--normalize] [--rms-level] [--loudness track|album|off] [--crossfade <sec>] [--no-cd-text] [--no-burnfree] [--post-action none|eject|sound|eject-sound|shutdown] [--allow-damaged] [--telemetry-out <path>] [--native [--test-write | --test-first] [--recovery-session | --data-folder <dir> [--data-label <label>] [--blue-book] | --fill-picture <picture>|auto [--lookup]] [--write-offset <samples|auto>] [--allow-overburn [--max-overburn-minutes <n>]] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Burns Red Book Audio CD from WAV, FLAC, MP3, AAC/M4A, WMA, .cue, .m3u/.pls, or a JSON --tracks-file (--recovery-session adds session-2 PAR2 recovery; --data-folder burns an Enhanced CD data session; --blue-book writes it as CD-ROM XA Mode 2 with CDPLUS/INFO.CDP and PICTURES/; --plan-only previews without burning).");
        stdout.WriteLine("  burn-dvd --drive <id> --video-ts <folder|movie> [--label <vol>] [--menu-title <title>] [--menu on|off | --no-menu] [--menu-autoplay <never|sec>] [--after-title next|menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <none|sec>] [--subtitles-default on|off] [--subtitle-size small|medium|large] [--video-bitrate <fit|kbps>] [--fit-to-disc] [--title-names <a;b;...>] [--dual-layer] [--generate-subtitles [--subtitle-language <code>]] [--speed <X|max>] [--no-verify] [--no-burnfree] [--overwrite] [--fill-picture <file.png>|auto [--disc-preset <id>]] [--post-action none|eject|sound|eject-sound|shutdown] [--telemetry-out <path>] [--native [--test-write | --test-first] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Validates, normalizes/authors, aligns, and burns a DVD-Video compliant disc from VIDEO_TS or movie files.");
        stdout.WriteLine("      --dual-layer authors movie files for a DVD-9 (longest title first). On a dual-layer disc, --native puts the layer break on a cell boundary.");
        stdout.WriteLine("      --generate-subtitles makes subtitles by speech recognition for movies with no .srt next to them (needs install-subtitle-tool);");
        stdout.WriteLine("      --subtitle-language <code|auto> sets the spoken language (default en).");
        stdout.WriteLine("  install-subtitle-tool [--plan-only] [--json]");
        stdout.WriteLine("      Optional download for subtitles: whisper.cpp (NVIDIA GPU build, AMD/Intel GPU build through Vulkan, else the processor build) and a speech model, each checked against a pinned SHA-256.");
        stdout.WriteLine("  generate-subtitles --input <movie> [--output <file.srt>] [--language <code|auto>] [--ffmpeg <path>] [--json]");
        stdout.WriteLine("      Makes machine-generated subtitles for a movie (FFmpeg + whisper.cpp). Fast on a graphics card, slower on integrated graphics, slowest on the processor; names are often misheard.");
        stdout.WriteLine("  rip --drive <id> --output <file.iso|file.bin> [--raw] [--json]");
        stdout.WriteLine("      Rips optical disc to an ISO or raw BIN+CUE image.");
        stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--mode track|image-cue] [--format wav|flac|mp3|opus|aac[,...]] [--bitrate <kbps>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9] [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--library <folder>] [--naming <template>] [--tracks 1,3,5] [--lookup] [--release <n|mbid>] [--no-cover-lookup] [--no-folder-image] [--overwrite] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--refresh-offsets] [--verify-accuraterip] [--verify-ctdb] [--no-ctdb-repair] [--no-secure] [--repair] [--de-emphasis] [--json]");
        stdout.WriteLine("  convert-audio --input <file|folder>[;<more>] --output <folder> --format flac|mp3|opus|aac[,...] [--recursive] [--naming <template>] [--overwrite] [--bitrate <kbps>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9] [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--json]");
        stdout.WriteLine("      Rips Red Book Audio CD to WAV, FLAC, MP3, Opus or AAC (FLAC, MP3, Opus need the optional ffmpeg; AAC uses the Windows encoder) with CD-Text and optional MusicBrainz lookup. Exit code 2 when sectors may be wrong (still suspect or unreadable).");
        stdout.WriteLine("  copy-disc --source <id> [--target <id>] [--copies <n>] [--speed <X|max>] [--raw] [--native [--log-dir <folder>]] [--allow-suspect] [--keep-image <folder>] [--wait-minutes <n>] [--json]");
        stdout.WriteLine("      Copies a disc, with one drive or two. With one drive (or for each further copy) the tray opens and the copy waits for a blank disc.");
        stdout.WriteLine("      Audio is read with error checks; a copy stops before writing if sectors still read wrong, unless --allow-suspect. --native copies mixed-mode and Enhanced CDs (audio + data sessions) whole.");
        stdout.WriteLine("  duplicate --drives <id1,id2,...> (--source <id> | --iso <file> | --tracks <files|.cue> | --disc <staged-folder>) [--speed <X|max>] [--no-verify | --strict-verify] [--overwrite] [--no-burnfree] [--allow-suspect] [--native [--log-dir <folder>]] [--telemetry-out <path>] [--json]");
        stdout.WriteLine("      Burns the same source disc, ISO image, Audio CD, or staged Archive disc to multiple optical drives simultaneously.");
        stdout.WriteLine("  check-disc --drive <id> [--compare <discNN.HEALTH.json>] [--json]");
        stdout.WriteLine("      Reads a CD once with C2 error pointers and says where it is weak (where a player may skip). Read-only.");
        stdout.WriteLine("      --compare sets the result against a health certificate and lists the minutes that changed.");
        stdout.WriteLine("  detect-offset --drive <id> [--json]");
        stdout.WriteLine("      Finds the drive's audio read offset using an inserted CD that is in the AccurateRip database.");
        stdout.WriteLine("  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl|bd|bd-dl|bdxl|bdxl-ql] [--redundancy <min %>] [--parity-discs <k>] [--no-fill] [--keep-folders | --order size|path] [--json]");
        stdout.WriteLine("      (or: archive [stage | burn | verify | rebuild | recover] ...)");
        stdout.WriteLine("      Splits a folder across discs and stages disc01..discNN with SHA-256 manifests and PAR2 recovery data (fills free space unless --no-fill).");
        stdout.WriteLine("      --parity-discs <k> adds 1..K cross-disc Reed-Solomon parity discs (discN+1..discN+K) capable of rebuilding up to K completely lost discs.");
        stdout.WriteLine("      Files are packed largest first into the first disc with room (--order size, the default); --keep-folders keeps a folder whole when it fits on one disc;");
        stdout.WriteLine("      --order path fills discs in path order. Every disc also gets CATALOG.JSON and INDEX.HTML listing the whole set.");
        stdout.WriteLine("  archive-rebuild --discs <path1;path2;...> --disc <k> --out <folder> [--json]");
        stdout.WriteLine("      Reconstructs a completely lost archive disc k from surviving discs and parity disc(s), verifying SHA-256 checksums.");
        stdout.WriteLine("  archive-burn --drive <id> --disc <staged disc folder> [--speed <X|max>] [--no-verify | --strict-verify] [--no-burnfree] [--overwrite] [--post-action none|eject|sound|eject-sound|shutdown] [--native [--test-write | --test-first] [--streaming] [--log-dir <folder>]] [--health-certificate] [--json]");
        stdout.WriteLine("      --health-certificate (off by default) writes discNN.HEALTH.json and .svg next to the disc folder: read-back result, write-speed curve, drive, media ID, date and, for a CD, one read-health pass.");
        stdout.WriteLine("      Burns one staged archive disc (ISO 9660 + Joliet + UDF, closed). With --native, discs the direct writer does not cover go to the Windows writer, and the output says why.");
        stdout.WriteLine("  archive-verify --path <folder|drive> [--json]");
        stdout.WriteLine("  recover (--drive <id> | --image <file.iso>) --out <folder> [--retries N] [--keep-image] [--json]");
        stdout.WriteLine("      Checks an archive disc against its SHA-256 manifest.");
        stdout.WriteLine("  verify --drive <id> --iso <file.iso|.nrg|.cdi|.mdf|.mds|.ccd|.img> [--strict-verify] [--json]");
        stdout.WriteLine("      Verifies disc sectors against an ISO image with SHA-256 and defect mapping.");
        stdout.WriteLine("  verify --drive <id> --cue <file.cue> [--title <album>] [--artist <name>] [--no-cd-text] [--write-offset <samples|auto>] [--log-dir <folder>] [--json]");
        stdout.WriteLine("      Checks a recorded audio or mixed-mode CD against the .cue it was burned from (track table, CD-Text, codes, every sample), without writing.");
        stdout.WriteLine("  erase --drive <id> [--full] [--json]");
        stdout.WriteLine("      Erases rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).");
        stdout.WriteLine("  scan --drive <id> [--block-size <sectors>] [--csv <file.csv>] [--json]");
        stdout.WriteLine("      Scans optical disc surface and benchmarks transfer rate curve.");
        stdout.WriteLine("  quality-scan --drive <id> [--out <file.json>] [--svg <file.svg>] [--attach-health <discNN.HEALTH.json>] [--speed <X|max>] [--force-old] [--json]");
        stdout.WriteLine("      Runs an optical error-correction scan (CD C1/C2/CU, DVD PIE/PIF/POF, BD LDC/BIS). Physical hardware scans use the MediaTek/Lite-On allow-list (experimental until checked against QPxTool).");
        stdout.WriteLine("  settings get [key] | set <key> <value> | reset [--settings-file <path>] [--json]");
        stdout.WriteLine("      Reads, changes, or resets application settings (default settings.json or --settings-file).");
        stdout.WriteLine("  tools [--ffmpeg <path>] [--par2 <path>] [--json]");
        stdout.WriteLine("      Reports whether optional tools (ffmpeg, par2, subtitle tool) are installed and their versions.");
        stdout.WriteLine("  install-ffmpeg [--dir <folder>] [--plan-only] [--overwrite] [--json]");
        stdout.WriteLine("      Downloads the full Windows FFmpeg build (libx264 for Blu-ray Video, zimg for HDR movies), checks it against the SHA-256 its publisher lists, into %LOCALAPPDATA%\\Programs\\ffmpeg (or --dir).");
        stdout.WriteLine("  install-par2 [--dir <folder>] [--plan-only] [--overwrite] [--json]");
        stdout.WriteLine("      Downloads and verifies par2cmdline-turbo by SHA-256 into %LOCALAPPDATA%\\Programs\\par2cmdline (or --dir).");
        stdout.WriteLine("  check-update [--json]");
        stdout.WriteLine("      Checks GitHub Releases for a newer IgnisDisc version.");
        stdout.WriteLine("  image-info --image <file> [--json]");
        stdout.WriteLine("      Inspects an .iso, .cue, .bin, .nrg, .mdf/.mds, .ccd/.img, or .cdi disc image.");
        stdout.WriteLine("  browse-image (<file> | --drive <id>) [--json]");
        stdout.WriteLine("      (alias: browse) Lists files and directories inside an .iso, .udf, .nrg, .mdf/.mds, .ccd/.img, .cdi, .bin/.cue image, or mounted virtual drive.");
        stdout.WriteLine("  extract-image <file> --out <dir> [--entry <path>...] [--overwrite] [--json]");
        stdout.WriteLine("      Safely extracts all or selected files from a disc image into <dir>.");
        stdout.WriteLine("  convert-image --input <file> --output <file> [--cue <file.cue>] [--overwrite] [--json]");
        stdout.WriteLine("      Converts legacy images or BIN+CUE to .iso, or converts .iso/legacy images to .bin + .cue.");
        stdout.WriteLine("  author-dvd --input <folder|movies> --output <folder|file.iso> [--label <vol>] [--menu-title <title>] [--menu on|off | --no-menu] [--menu-autoplay <never|sec>] [--after-title next|menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <none|sec>] [--subtitles-default on|off] [--subtitle-size small|medium|large] [--video-bitrate <fit|kbps>] [--fit-to-disc] [--title-names <a;b;...>] [--audio-track <lang>:<path>]... [--subtitle-track <lang>:<path>]... [--dual-layer] [--generate-subtitles [--subtitle-language <code|auto>]] [--overwrite] [--json]");
        stdout.WriteLine("      Authors DVD-Video files into a VIDEO_TS folder or compliant .iso without burning a disc.");
        stdout.WriteLine("  author-bdmv --input <movie[;movie...]> --output <folder|file.iso> [--target bluray|avchd] [--video-format 1080p|1080i|720p] [--audio ac3|lpcm] [--subtitle-track <lang>:<file.srt>]... [--generate-subtitles] [--overwrite] [--json]");
        stdout.WriteLine("      Authors a Blu-ray Video (BDMV) or AVCHD folder, or a UDF 2.50 .iso, without burning a disc (file structure checked only).");
        stdout.WriteLine("  jewel-cover (--tracks <files> | --tracks-file <file.json>) --output <file.html> [--title <album>] [--artist <name>] [--songwriter <name>] [--upc <13digits>] [--dao] [--pregap <frames>] [--overwrite] [--json]");
        stdout.WriteLine("      Generates a printable HTML CD jewel case cover and tray inlay from audio tracks.");
        stdout.WriteLine("  audio-plan (--tracks <files> | --tracks-file <file.json>) [--title <album>] [--artist <name>] [--dao] [--pregap <frames>] [--normalize] [--rms-level] [--loudness track|album|off] [--crossfade <sec>] [--media 74|80] [--json]");
        stdout.WriteLine("      Previews Audio CD track layout, durations, 74/80-minute fit, and CD-Text without burning.");
        stdout.WriteLine("  burn-queue --drive <id> --images <img1,img2,...> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--no-burnfree] [--overwrite] [--continue-on-error] [--post-action none|eject|sound|eject-sound|shutdown] [--wait-minutes <n>] [--json]");
        stdout.WriteLine("      Burns a sequential queue of .iso or legacy/BIN images to discs, prompting for disc swaps.");
        stdout.WriteLine("  book-type --drive <id> [--get | --set dvd-rom|dvd+r|dvd+rw|dvd+r-dl|dvd-r|dvd-rw|dvd-ram] [--json]");
        stdout.WriteLine("      Reads or updates DVD+R / DVD+RW / DVD+R DL book-type bitsetting.");
        stdout.WriteLine("  repair-par2 --path <folder> [--par2 <path>] [--delete-backups] [--json]");
        stdout.WriteLine("      Verifies and repairs a folder in place from its PAR2 recovery set (keeps backup files unless --delete-backups).");
        stdout.WriteLine("  ecc-rs03 (create | verify | repair) --image <file.iso> [--mode ecc|augmented] [--ecc <file.ecc>] [--redundancy <normal|high|N%|Nr>] [--media <cd|cd-rw|dvd|dvd+rw|dvd-rw|dvd-ram|dvd-dl|bd|bd-re|bd-dl|bdxl|bdxl-ql>] [--medium-sectors <N>] [--allow-rewritable] [--overwrite] [--json]");
        stdout.WriteLine("      Opt-in clean-room RS03 raw-sector ECC parity (standalone .ecc file or augmented write-once ISO; external dvdisaster interop not yet verified).");
        stdout.WriteLine("  rosetta ladder --media dvd|cd --output <file.iso|file.wav|file.raw> [--json-output <file.json>] [--disc-preset <id>] [--start-angle <deg>] [--no-interleave] [--plan-only] [--json]");
        stdout.WriteLine("      Generates a DVD-R (250x375 down to 13x17 um) or CD-R (250x375 down to 50x75 um) optical resolution-ladder image across inner, middle, and outer radii plus a JSON manifest of rung positions.");
        stdout.WriteLine("  rosetta build --text <folder> --output <file.iso|file.cue|file.bin> [--media dvd|cd] [--title <text>] [--date yyyy-mm-dd] [--files <folder>] [--cells hobbyist|high-density|<rung id>] [--pages-radius <mm>] [--redundancy <0-100>] [--no-fill] [--no-rim] [--no-interleave] [--disc-preset <id>] [--plan-only] [--overwrite] [--json]");
        stdout.WriteLine("      Makes a hybrid Rosetta DVD-R or CD-R image: the text, a page index and optional files with PAR2 recovery data inside r = 38 mm, a 2 MiB zero gap, then the text as microscope-readable pages and the rim header (CD-R >= 50x75 um cells).");
        stdout.WriteLine("  rosetta burn --drive <id> --image <file.iso|file.cue> [any burn-iso or burn-audio option] [--json]");
        stdout.WriteLine("      Burns an image made by 'rosetta build' (forwards to burn-iso for DVD-R, or burn-audio --dao --native for mixed-mode CD-R).");
        stdout.WriteLine("  disc-art generate --media cd|dvd|bd --picture <file.png> --output <file> [--files <folder> [--files-outer]] [--append-to <base.cue> | --pregap-art <base.cue> [--pregap-seconds <s>]] [--label <text>] [--tone photo|binary] [--start-angle <deg>] [--scale <x>] [--offset-x-mm <mm>] [--offset-y-mm <mm>] [--rotate <deg>] [--fit ring|disc] [--art-inner-radius-mm <mm>] [--art-outer-radius-mm <mm>] [--title-ring <text> [--title-ring-height-mm <mm>] [--title-ring-date yyyy-MM-dd|none]] [--read-compat] [--no-interleave] [--rim-header] [--plan-only] [--overwrite] [--disc-preset <id>] [--drive-model <text> --media-id <text>] [--json]");
        stdout.WriteLine("      Draws a picture in the data sectors of a DVD or BD image (optionally with files on the same disc) or makes a CD picture track, with its description file for the preview.");
        stdout.WriteLine("      --pregap-art (experimental, off by default) hides a CD picture in track 1's pre-gap instead; some players and rippers can play it as noise.");
        stdout.WriteLine("  disc-art preview --image <file.iso|.wav|.raw> --output <file.png> [--size <px>] [--json]");
        stdout.WriteLine("      Renders what a drive would show from an image made by 'disc-art generate' (DVD and CD; not yet BD).");
        stdout.WriteLine("  disc-art burn --drive <id> --image <file.iso|.cue> [any burn-iso or burn-audio option] [--json]");
        stdout.WriteLine("      Burns the finished picture-disc image: an .iso with burn-iso, a .cue (CD picture track) with burn-audio --dao --native.");
        stdout.WriteLine("  disc-art calibrate target --media cd|dvd|bd --stage coarse|boundary|fine --output <file> [--disc-preset <id>] [--drive-model <text> --media-id <text>] [--overwrite] [--json]");
        stdout.WriteLine("      Writes a calibration target to burn (CD: .wav/.raw, DVD/BD: .iso) and its calibration record.");
        stdout.WriteLine("  disc-art calibrate fit --points <record.calibration.json> [--candidate <n>] [--drive-model <text> --media-id <text>] [--json]");
        stdout.WriteLine("      Fits the points measured on a photo of the burned target and can save the result per drive model and media ID.");
        stdout.WriteLine("  disc-art measure --drive <id> --output <file.json> [--from <sector>] [--to <sector>] [--spacing <sectors>] [--json]");
        stdout.WriteLine("      Experimental, read-only (CD): maps a written disc's spiral by read timing and writes a turn table for '--turn-map' (capture CSV beside it).");
        stdout.WriteLine();
        stdout.WriteLine("General Options:");
        stdout.WriteLine("  --help, -h             Show help information.");
        stdout.WriteLine("  --version, -v          Show version information.");
        stdout.WriteLine("  --json                 Format output as machine-readable JSON.");
        stdout.WriteLine("  --virtual-media <spec> One-shot virtual disc mount (blank spec or image path) for burn, verify, rip, media-info, and browse.");
        stdout.WriteLine("  --virtual-state <path> Explicit virtual drive state directory (default %LOCALAPPDATA%\\IgnisDisc\\virtual-state).");
        stdout.WriteLine();
        stdout.WriteLine("Safe defaults (each can be turned off):");
        stdout.WriteLine("  Burns use the fastest speed the drive writes at a constant linear velocity for the disc, up to 16x on CD");
        stdout.WriteLine("  and 8x on DVD; --speed <X>, --speed slowest or --speed max to change it.");
        stdout.WriteLine("  Data and DVD-Video burns are read back and compared afterwards; --no-verify skips that.");
        stdout.WriteLine("  rip-audio reads with C2 error pointers and re-reads doubtful sectors; --no-secure reads once.");
        stdout.WriteLine("  burn-audio checks FLAC files first and stops if one is damaged; --allow-damaged burns anyway.");
        stdout.WriteLine("  burn-audio --fill-picture (off unless given): after the music, a hidden Mode 1 data session draws the picture to the end of the disc and closes it; players see the same tracks. auto: cover art embedded in the files, else cover.jpg/folder.jpg next to them, else (only with --lookup) the Cover Art Archive by album title and artist.");
    }

    private static void PrintSubcommandHelp(string? subcommand, TextWriter stdout)
    {
        if (string.Equals(subcommand, "drive-report", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "drivereport", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  drive-report --drive <id> [--json]");
            stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
            return;
        }
        if (string.Equals(subcommand, "virtual-mount", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "virtualmount", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "virtual-eject", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "virtualeject", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "virtual-status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "virtualstatus", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  virtual-mount --drive <id> (--blank <cd-r|cd-rw|dvd-r|dvd+r|dvd+rw|dvd-r-dl|bd-r|bd-re|bd-r-dl|bdxl> | --image <path>) [--virtual-state <path>] [--json]");
            stdout.WriteLine("      Mounts a blank or image-backed virtual medium into a virtual drive persisted in --virtual-state (or the default virtual state directory).");
            stdout.WriteLine("  virtual-eject --drive <id> [--virtual-state <path>] [--json]");
            stdout.WriteLine("      Unmounts and clears virtual media from the virtual drive.");
            stdout.WriteLine("  virtual-status [--drive <id>] [--virtual-state <path>] [--json]");
            stdout.WriteLine("      Lists virtual drives, mounted media status, and VirtualMmcDrive profile/status without touching physical hardware.");
            return;
        }
        if (string.Equals(subcommand, "eject", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "load", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  eject --drive <id> [--json]");
            stdout.WriteLine("      Opens the tray (START STOP UNIT). If the drive cannot close its own tray, the message says to push it in by hand.");
            stdout.WriteLine("  load --drive <id> [--json]");
            stdout.WriteLine("      Closes the tray and waits until the drive is ready or reports that it holds no disc.");
            stdout.WriteLine("      The drive's Removable Medium feature is read first. If it reports no Load capability (a spring");
            stdout.WriteLine("      tray, as on slim USB drives) or a pop-up tray, nothing is sent: the command says to push the tray");
            stdout.WriteLine("      in by hand and exits 1. --json adds needsManualClose, commandSent, discPresent and the drive's");
            stdout.WriteLine("      loadingMechanism, canLoad and canEject.");
            return;
        }
        if (string.Equals(subcommand, "rip-audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "ripaudio", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--mode track|image-cue] [--format wav|flac|mp3|opus|aac[,...]] [--bitrate <kbps>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9] [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--library <folder>] [--naming <template>] [--tracks 1,3,5] [--lookup] [--release <n|mbid>] [--no-cover-lookup] [--no-folder-image] [--overwrite] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--refresh-offsets] [--verify-accuraterip] [--verify-ctdb] [--no-ctdb-repair] [--no-secure] [--repair] [--de-emphasis] [--cover <image>] [--no-replaygain] [--json]");
            stdout.WriteLine("      Rips Red Book Audio CD to WAV, FLAC, MP3, Opus or AAC (FLAC, MP3, Opus need the optional ffmpeg; AAC uses the Windows encoder) with CD-Text, an EAC-style <Album>.log with SHA-256/HMAC-SHA256 integrity footer, and optional MusicBrainz lookup. Exit code 2 when sectors may be wrong (still suspect or unreadable).");
            stdout.WriteLine("      --mode image-cue rips the entire Audio CD (including optional HTOA and pre-gaps) into one .wav or .flac image + exact .cue.");
            stdout.WriteLine("      --repair checks and repairs damaged samples from session 2 PAR2 recovery data on Enhanced CDs.");
            stdout.WriteLine("      --de-emphasis applies the Red Book 50/15 us de-emphasis filter to tracks flagged with pre-emphasis.");
            stdout.WriteLine("      FLAC files get ReplayGain 2.0 tags (unless --no-replaygain) and, with --cover, embedded front cover art.");
            stdout.WriteLine("      --format takes a comma list (flac,mp3): each format is written once, in its own {format} folder unless --naming has {format}.");
            stdout.WriteLine("      --library <folder> files the album by --naming (default {albumartist}\\{album}[ ({year})]\\{track} - {title}); --output is then only the working folder.");
            stdout.WriteLine("      Tokens: {albumartist} {artist} {album} {year} {track} {tracktotal} {title} {disc} {format}; text in [ ] is dropped when a token in it is empty.");
            stdout.WriteLine("      --lookup: when several MusicBrainz releases share the disc ID and differ, the command stops before reading the disc and lists them; pick one with --release <number|mbid>.");
            stdout.WriteLine("      The chosen release's front cover comes from the Cover Art Archive (--no-cover-lookup to skip; --cover <image> wins). folder.jpg is written beside the files (--no-folder-image to skip).");
            stdout.WriteLine("      --bitrate applies only when exactly one lossy format is chosen; otherwise use --mp3-bitrate, --mp3-quality (V0 best .. V9), --opus-bitrate, --aac-bitrate. --overwrite replaces existing files.");
            return;
        }
        if (string.Equals(subcommand, "convert-audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "convertaudio", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  convert-audio --input <file|folder>[;<more>] --output <folder> --format flac|mp3|opus|aac[,...] [--recursive] [--naming <template>] [--overwrite] [--bitrate <kbps>] [--mp3-bitrate <kbps> | --mp3-quality V0..V9] [--opus-bitrate <kbps>] [--aac-bitrate <kbps>] [--json]");
            stdout.WriteLine("      Converts audio files (wav flac mp3 m4a aac ogg opus wma aif) with ffmpeg; tags and, for MP3 and FLAC, cover art are carried over. Input files are never changed.");
            stdout.WriteLine("      Several inputs: separate them with ';'. Without --naming the folder layout under each input folder is kept; with --naming the tags decide the path (same tokens as rip-audio).");
            stdout.WriteLine("      Existing outputs are skipped unless --overwrite. Exit code 2 when some files failed; converting already-lossy files cannot restore quality.");
            return;
        }

        if (string.Equals(subcommand, "settings", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  settings get [key] [--key <k>] [--settings-file <path>] [--json]");
            stdout.WriteLine("  settings set <key> <value> [--key <k>] [--value <v>] [--settings-file <path>] [--json]");
            stdout.WriteLine("  settings reset [--settings-file <path>] [--json]");
            stdout.WriteLine($"      Valid keys: {string.Join(", ", SupportedSettingsKeys)}");
            return;
        }
        if (string.Equals(subcommand, "tools", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "install-ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "install-par2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "check-update", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  tools [--ffmpeg <path>] [--par2 <path>] [--json]");
            stdout.WriteLine("      Checks installed optional tools (ffmpeg, par2 with update check, whisper.cpp subtitle tool).");
            stdout.WriteLine("  install-ffmpeg [--dir <folder>] [--plan-only] [--overwrite] [--json]");
            stdout.WriteLine("      Downloads the full Windows FFmpeg build (libx264 for Blu-ray Video, zimg for HDR movies), checks it against the SHA-256 its publisher lists, into %LOCALAPPDATA%\\Programs\\ffmpeg (or --dir).");
            stdout.WriteLine("  install-par2 [--dir <folder>] [--plan-only] [--overwrite] [--json]");
            stdout.WriteLine("      Downloads and verifies par2cmdline-turbo by SHA-256 into %LOCALAPPDATA%\\Programs\\par2cmdline (or --dir).");
            stdout.WriteLine("  check-update [--json]");
            stdout.WriteLine("      Queries GitHub Releases for a newer IgnisDisc version.");
            return;
        }

        if (string.Equals(subcommand, "image-info", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "imageinfo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "browse-image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "browseimage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "browse", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "extract-image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "extractimage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "convert-image", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "convertimage", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  image-info --image <file> [--json]");
            stdout.WriteLine("      Inspects sector size, sector count, offsets, and tracks of an .iso, .cue, .bin, .nrg, .mdf/.mds, .ccd/.img, or .cdi image.");
            stdout.WriteLine("  browse-image (<file> | --drive <id>) [--virtual-media <spec>] [--virtual-state <path>] [--json]");
            stdout.WriteLine("      (alias: browse) Lists directory tree entries (path, size, UTC timestamp, flags, LBA, filesystem source) in an image or mounted virtual drive.");
            stdout.WriteLine("  extract-image <file> --out <dir> [--entry <path>...] [--overwrite] [--json]");
            stdout.WriteLine("      Safely extracts all entries or repeated --entry <path> selections into <dir> with path-safety validation.");
            stdout.WriteLine("  convert-image --input <file> --output <file> [--cue <file.cue>] [--overwrite] [--json]");
            stdout.WriteLine("      Converts .nrg, .mdf/.mds, .ccd/.img, .cdi, .bin, or .cue to .iso, or converts .iso/legacy images to .bin + .cue.");
            return;
        }
        if (string.Equals(subcommand, "author-bdmv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "authorbdmv", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  author-bdmv --input <movie[;movie...]> --output <folder|file.iso> [--target bluray|avchd] [--label <vol>] [--video-format 1080p|1080i|720p] [--frame-rate 23.976|24|25|29.97|50|59.94] [--audio ac3|lpcm] [--audio-channels 2|6] [--audio-language <code>] [--video-bitrate <kbps>] [--max-video-bitrate <kbps>] [--chapters <none|sec>] [--subtitle-track <lang>:<file.srt>]... [--generate-subtitles [--subtitle-language <code>]] [--ffmpeg <path>] [--overwrite] [--json]");
            stdout.WriteLine("      Authors a Blu-ray Video (BDMV) or AVCHD folder, or a UDF 2.50 image, without burning: FFmpeg makes H.264 and");
            stdout.WriteLine("      AC-3 or LPCM, the stream is muxed to 192-byte BDAV packets, index/playlist/clip files and CERTIFICATE are written,");
            stdout.WriteLine("      and subtitles become PGS bitmap tracks. Needs an FFmpeg with libx264. The result is checked for file structure only;");
            stdout.WriteLine("      it has not been tested on a Blu-ray player.");
            return;
        }
        if (string.Equals(subcommand, "author-dvd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "authordvd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "burn-dvd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "burndvd", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  author-dvd --input <folder|movies> --output <folder|file.iso> [--label <vol>] [--menu-title <title>] [--menu on|off | --no-menu] [--menu-autoplay <never|sec>] [--after-title next|menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <none|sec>] [--subtitles-default on|off] [--subtitle-size small|medium|large] [--video-bitrate <fit|kbps>] [--fit-to-disc] [--title-names <a;b;...>] [--audio-track <lang>:<path>]... [--subtitle-track <lang>:<path>]... [--dual-layer] [--generate-subtitles [--subtitle-language <code|auto>]] [--ffmpeg <path>] [--no-ffmpeg] [--overwrite] [--json]");
            stdout.WriteLine("      Authors DVD-Video files into a VIDEO_TS folder or compliant ISO 9660 + UDF 1.02 image without burning.");
            stdout.WriteLine("  burn-dvd --drive <id> --video-ts <folder|movie> [--label <vol>] [--menu-title <title>] [--menu on|off | --no-menu] [--menu-autoplay <never|sec>] [--after-title next|menu] [--standard ntsc|pal] [--aspect 16:9|4:3] [--chapters <none|sec>] [--subtitles-default on|off] [--subtitle-size small|medium|large] [--video-bitrate <fit|kbps>] [--fit-to-disc] [--title-names <a;b;...>] [--audio-track <lang>:<path>]... [--subtitle-track <lang>:<path>]... [--dual-layer] [--generate-subtitles [--subtitle-language <code|auto>]] [--ffmpeg <path>] [--speed <X|max>] [--no-verify] [--overwrite] [--fill-picture <file.png>|auto [--disc-preset <id>]] [--native [--test-write | --test-first]] [--json]");
            stdout.WriteLine("      Validates, authors, and burns a DVD-Video compliant disc from VIDEO_TS or movie files.");
            stdout.WriteLine("      --fill-picture (off by default) draws a picture on the empty rest of a single-layer DVD or BD; 'auto' takes a");
            stdout.WriteLine("      frame of the main movie with FFmpeg (skipped with a message when FFmpeg is not installed). The whole disc");
            stdout.WriteLine("      is written (longer burn) and closed.");
            return;
        }
        if (string.Equals(subcommand, "jewel-cover", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "jewelcover", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "audio-plan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "audioplan", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  jewel-cover (--tracks <files> | --tracks-file <file.json>) --output <file.html> [--title <album>] [--artist <name>] [--songwriter <name>] [--upc <13digits>] [--dao] [--pregap <frames>] [--no-cd-text] [--overwrite] [--json]");
            stdout.WriteLine("      Generates a printable HTML CD jewel case cover and tray inlay from audio tracks.");
            stdout.WriteLine("  audio-plan (--tracks <files> | --tracks-file <file.json>) [--title <album>] [--artist <name>] [--songwriter <name>] [--upc <13digits>] [--dao] [--pregap <frames>] [--no-cd-text] [--normalize] [--rms-level] [--loudness track|album|off] [--crossfade <sec>] [--media 74|80] [--json]");
            stdout.WriteLine("      Previews Audio CD track layout, durations, 74/80-minute fit, and CD-Text without burning.");
            return;
        }
        if (string.Equals(subcommand, "burn-queue", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "burnqueue", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  burn-queue --drive <id> --images <img1,img2,...> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--no-burnfree] [--overwrite] [--continue-on-error] [--post-action none|eject|sound|eject-sound|shutdown [--confirm-shutdown]] [--wait-minutes <n>] [--json]");
            stdout.WriteLine("      Burns each queued disc image sequentially, ejecting and waiting for blank discs between items.");
            return;
        }
        if (string.Equals(subcommand, "book-type", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "booktype", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "repair-par2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "repairpar2", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  book-type --drive <id> [--get | --set dvd-rom|dvd+r|dvd+rw|dvd+r-dl|dvd-r|dvd-rw|dvd-ram] [--json]");
            stdout.WriteLine("      Reads or updates DVD+R / DVD+RW / DVD+R DL book-type bitsetting.");
            stdout.WriteLine("  repair-par2 --path <folder> [--par2 <path>] [--delete-backups] [--json]");
            stdout.WriteLine("      Verifies and repairs a folder from its PAR2 recovery set (retains backup files unless --delete-backups).");
            return;
        }
        if (string.Equals(subcommand, "disc-art", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "discart", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  disc-art generate --media cd|dvd|bd --picture <file.png> --output <file> [options] [--json]");
            stdout.WriteLine("      Makes a picture disc image and, next to it, <file>.disc-art.json (what the preview needs).");
            stdout.WriteLine("      --media cd|dvd|bd          Disc type. CD output is .wav (with a CUE sheet) or .raw; DVD and BD output is .iso.");
            stdout.WriteLine("      --picture <file.png>       The picture, mapped over the whole disc radius.");
            stdout.WriteLine("      --files <folder>           DVD/BD: put these files on the same disc (ISO 9660 + Joliet + UDF), then 2 MiB of empty sectors, then the picture.");
            stdout.WriteLine("      --files-outer              Put the files at the outer end instead (second session, ISO 9660 + Joliet) and the picture from the start; DVD keeps room for the drive's gap between sessions.");
            stdout.WriteLine("      --append-to <base.cue>     CD: add the picture as the last track of this audio or mixed-mode CUE (needs .wav output; at least one minute must remain).");
            stdout.WriteLine("      --pregap-art <base.cue>    CD, EXPERIMENTAL, off by default: put the picture in the hidden pre-gap before track 1 (INDEX 00) of this");
            stdout.WriteLine("                                 audio CUE, so it sits at the inner edge and players start at track 1. Writes <base>-with-pregap-picture.cue");
            stdout.WriteLine("                                 for 'disc-art burn' (Session-at-Once, direct drive commands). WARNING: some players and rippers can rewind");
            stdout.WriteLine("                                 into the pre-gap, or rip it as a hidden track, and play the picture as loud noise. --fit defaults to ring.");
            stdout.WriteLine($"      --pregap-seconds <s>       Length of the pre-gap picture (default {DiscArtImageBuilder.CdPreGapDefaultSeconds}, {DiscArtImageBuilder.CdPreGapMinimumSeconds} to {DiscArtImageBuilder.CdPreGapMaximumSeconds}); the music moves out by that much.");
            stdout.WriteLine("      --label <text>             Volume label of the files (default DISC_ART); also the title of the rim header.");
            stdout.WriteLine("      --tone photo|binary        Photo (default) dithers midtones; binary is plain black and white.");
            stdout.WriteLine("      --start-angle <deg>        Rotate the picture on the disc.");
            stdout.WriteLine("      --scale <x>                Make the picture x times larger (default 1: its shorter side spans the disc diameter).");
            stdout.WriteLine("      --offset-x-mm <mm>         Move the picture right (negative: left) from the disc centre, as seen on the disc.");
            stdout.WriteLine("      --offset-y-mm <mm>         Move the picture down (negative: up) from the disc centre.");
            stdout.WriteLine("      --rotate <deg>             Turn the picture clockwise about its own centre, before it is moved.");
            stdout.WriteLine("      --fit disc|ring            disc (default): size against the whole disc. ring: size so the picture's edges touch the outer radius of the ring that is drawn.");
            stdout.WriteLine("      --art-inner-radius-mm <mm> DVD/BD: draw the picture only from this radius outward; the sectors before it stay zero.");
            stdout.WriteLine("      --art-outer-radius-mm <mm> DVD/BD: draw only up to this radius; the image ends there, so the burn is shorter and the rest of the disc stays blank.");
            stdout.WriteLine("      --title-ring <text>        DVD: write this title and the date in capitals around the outer edge of the drawn area, large enough to read without a microscope; the picture stops where the ring starts. --picture may be left out for a ring alone.");
            stdout.WriteLine("      --title-ring-height-mm <mm> Height of the title ring (default 3, 2 to 10 mm; letters scale with it).");
            stdout.WriteLine("      --title-ring-date <date>   Date on the ring as yyyy-MM-dd (default today), or none.");
            stdout.WriteLine("      --read-compat              DVD: use the read-compatibility recipe (periodic-9b-16-v1).");
            stdout.WriteLine("      --no-interleave            CD only: write the track without the CD-DA delay interleave.");
            stdout.WriteLine("      --rim-header               Draw the rim header at the outer edge of the picture: title (--label, else the picture's name),");
            stdout.WriteLine("                                 today's date, the first 16 digits of the picture's SHA-256, angle ticks every 5 degrees and");
            stdout.WriteLine("                                 how-to-read diagrams. Full size (2 mm capitals) needs a 4 mm ring; a narrower ring gets a");
            stdout.WriteLine("                                 smaller header, down to glyph cells of 50 x 75 um (CD) or 26 x 34 um (DVD, BD); below that it is refused.");
            stdout.WriteLine("      --plan-only               Print the sector plan and write nothing.");
            stdout.WriteLine("      --overwrite                Replace existing output files.");
            stdout.WriteLine("      --disc-preset, --disc-presets-file, --drive-model, --media-id, --store, --inner-radius-mm, --outer-radius-mm, --sectors, --pitch-*");
            stdout.WriteLine("                                 Disc geometry, as for 'disc-art calibrate target' (a saved calibration for the drive and disc is used automatically).");
            stdout.WriteLine("      --turn-map <file.json>     Experimental, CD picture-only discs: take every angle from a table of the disc's spiral measured by read timing");
            stdout.WriteLine("                                 (it replaces the radii and pitch coefficients). 'disc-art preview' does not know the table and shows the picture twisted.");
            stdout.WriteLine("      --turn-map-offset <sectors> With --turn-map: sectors added to an address before its angle is looked up (default 0).");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art preview --image <file.iso|.wav|.raw> --output <file.png> [--size <px>] [--json]");
            stdout.WriteLine("      Renders what a drive would show from an image made by 'disc-art generate', using its description file. DVD and CD only; BD is not available yet.");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art burn --drive <id> --image <file.iso|.cue> [any burn-iso or burn-audio option] [--json]");
            stdout.WriteLine("      Burns the finished image. An .iso goes to burn-iso (its direct-writer default applies); a .cue (a CD picture track) goes to burn-audio --dao --native.");
            stdout.WriteLine("      A DVD .iso with --files-outer is burned in two sessions with direct drive commands on a blank DVD-R or DVD+R: the picture, left open,");
            stdout.WriteLine("      then the files laid out again for the drive's next writable address (closes the disc). Refused before writing when they no longer fit.");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art calibrate target --media cd|dvd|bd --stage coarse|boundary|fine --output <file> [options] [--json]");
            stdout.WriteLine("      Writes a calibration target to burn and, next to it, its calibration record (<name>.calibration.json).");
            stdout.WriteLine("      The stages are burned in this order: coarse (three wide bands), boundary (wavy light/dark edge), fine (cross).");
            stdout.WriteLine("      --media cd|dvd|bd          Disc type. CD output is .wav (with a CUE sheet) or .raw; DVD and BD output is .iso.");
            stdout.WriteLine("      --stage <name>             coarse, boundary or fine.");
            stdout.WriteLine("      --output, --out <path>     Target file. A full-size DVD or BD target is as large as the disc.");
            stdout.WriteLine("      --disc-preset <id>         Start values (default cd-80, dvd-5-120mm, bd-r-sl-25gb).");
            stdout.WriteLine("      --disc-presets-file <p>    Optional user presets JSON file to merge.");
            stdout.WriteLine("      --drive-model <text> --media-id <text>");
            stdout.WriteLine("                                 Start from the calibration saved for this drive and blank disc, if there is one.");
            stdout.WriteLine("      --inner-radius-mm, --outer-radius-mm, --sectors, --pitch-linear, --pitch-quadratic, --pitch-cubic <n>");
            stdout.WriteLine("                                 Replace single start values.");
            stdout.WriteLine("      --no-interleave            CD only: write the track without the CD-DA delay interleave.");
            stdout.WriteLine("      --store <file>             Calibration store to read (default %APPDATA%\\IgnisDisc\\disc-calibrations.json).");
            stdout.WriteLine("      --overwrite                Replace existing output files.");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art calibrate fit --points <record.calibration.json> [--candidate <n>] [--drive-model <text> --media-id <text>] [--store <file>] [--json]");
            stdout.WriteLine("      Fits the measurement points in a calibration record: band edges (coarse), boundary points (boundary) or");
            stdout.WriteLine("      points along one arm of the cross (fine). Points are millimetres from the disc centre on the rectified photo.");
            stdout.WriteLine("      The angle marks of a coarse target are not read; a coarse record gives the wide-band radii.");
            stdout.WriteLine("      --points <path>            Calibration record with the measured points in its photo section.");
            stdout.WriteLine("      --candidate <n>            Apply candidate n. Needed when the fitter flags the fit; a clean fit applies candidate 1.");
            stdout.WriteLine("      --drive-model <text> --media-id <text>");
            stdout.WriteLine("                                 Save the applied result for this drive model and media ID (ATIP/ADIP manufacturer code).");
            stdout.WriteLine("      --store <file>             Calibration store to write (default %APPDATA%\\IgnisDisc\\disc-calibrations.json).");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art calibrate list [--store <file>] [--json]");
            stdout.WriteLine("      Lists the saved calibrations.");
            stdout.WriteLine();
            stdout.WriteLine("  disc-art measure --drive <id> --output <file.json> [--from <sector>] [--to <sector>] [--spacing <sectors>] [--json]");
            stdout.WriteLine("      Experimental, read-only (CD): maps a written disc's spiral by read timing (one-sector READ CD alternating with an anchor, no writes, no SET CD SPEED)");
            stdout.WriteLine("      and writes the turn table JSON for 'disc-art generate --turn-map' with the raw timing capture CSV beside it.");
            stdout.WriteLine("      Refuses without writing anything if the disc holds fewer than 20,000 readable sectors, the spin rate is unsteady, or the chain margin exceeds 0.25 turn.");
            stdout.WriteLine("      --from <sector>            First station sector (default: 2,000 into the readable area).");
            stdout.WriteLine("      --to <sector>              Last station sector (default: 2,000 before the readable end).");
            stdout.WriteLine("      --spacing <sectors>        Distance between stations (default: 3,000).");
            return;
        }
        if (string.Equals(subcommand, "rosetta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "rosetta-ladder", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "rosettaladder", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  rosetta ladder --media dvd|cd --output <file.iso|file.wav|file.raw> [--json-output <file.json>] [--disc-preset <id>] [--disc-presets-file <path>] [--start-angle <deg>] [--no-interleave] [--plan-only] [--json]");
            stdout.WriteLine("      Generates a DVD-R (250x375 down to 13x17 um) or CD-R (250x375 down to 50x75 um) optical resolution-ladder image across inner, middle, and outer radii plus a JSON manifest of rung positions.");
            stdout.WriteLine("      --media dvd|cd           Target optical medium (DVD-R or CD-R; default dvd).");
            stdout.WriteLine("      --output, --out <path>   Destination raw DVD-R .iso or CD-R .wav (with CUE) / .raw file.");
            stdout.WriteLine("      --json-output <path>     Optional manifest JSON path (default <output>.json).");
            stdout.WriteLine("      --disc-preset <id>       Preset ID from DiscPresetJsonStore (default dvd-5-120mm or cd-80).");
            stdout.WriteLine("      --disc-presets-file <p>  Optional user presets JSON file to merge.");
            stdout.WriteLine("      --start-angle <deg>      Optional angular offset in degrees (default 0).");
            stdout.WriteLine("      --no-interleave          CD-R only: write the track without the CD-DA delay interleave.");
            stdout.WriteLine("      --plan-only              Write only the JSON manifest without writing the disc image.");
            stdout.WriteLine();
            stdout.WriteLine("  rosetta build --text <folder> --output <file.iso|file.cue|file.bin> [options] [--json]");
            stdout.WriteLine("      Makes a hybrid Rosetta DVD-R (.iso) or mixed-mode CD-R (.cue/.bin) image. Inside the pages' inner radius:");
            stdout.WriteLine("      an archive volume (ISO 9660 + Joliet + UDF) with the text files, a page index (which page and tile holds");
            stdout.WriteLine("      which document), optional files, checksum manifests and PAR2 recovery data. Then at least 2 MiB of zero");
            stdout.WriteLine("      sectors, then the same text as pages to read with a microscope, and the rim header (title, date, SHA-256");
            stdout.WriteLine("      prefix). A computer sees only the archive volume.");
            stdout.WriteLine("      --media dvd|cd           Target medium (default dvd). CD-R requires glyph cells of at least 50x75 um.");
            stdout.WriteLine("      --text <folder>          The .txt and .md files (UTF-8) at the top of the folder, in name order.");
            stdout.WriteLine("      --output, --out <path>   Destination .iso (DVD-R) or .cue/.bin (CD-R); a .rosetta.json sidecar is written next to it.");
            stdout.WriteLine("      --title <text>           Archive title (default: the text folder's name).");
            stdout.WriteLine("      --date yyyy-mm-dd        Archive date (default: today, UTC).");
            stdout.WriteLine("      --files <folder>         Extra files for the archive volume, kept under their own paths.");
            stdout.WriteLine("      --cells <id>             Glyph cell preset: hobbyist (100x150 um, default), high-density (26x34 um, DVD only) or a ladder rung id.");
            stdout.WriteLine("      --pages-radius <mm>      Radius where the pages begin (default 38); the archive and its gap end before it.");
            stdout.WriteLine("      --redundancy <0-100>     Minimum PAR2 recovery data in percent (default 10; needs par2). 0: manifests only.");
            stdout.WriteLine("      --no-fill                Do not grow the recovery data to fill the room inside the pages' radius.");
            stdout.WriteLine("      --no-rim                 Leave out the rim header.");
            stdout.WriteLine("      --no-interleave          CD-R only: write the picture track without the CD-DA delay interleave.");
            stdout.WriteLine("      --disc-preset <id>       Disc geometry preset (default dvd-5-120mm / cd-80), or --drive-model/--media-id for a saved calibration.");
            stdout.WriteLine("      --plan-only              Print the layout and capacities without writing anything.");
            stdout.WriteLine();
            stdout.WriteLine("  rosetta burn --drive <id> --image <file.iso|file.cue> [any burn-iso or burn-audio option] [--json]");
            stdout.WriteLine("      Burns an image made by 'rosetta build' with burn-iso (DVD-R) or burn-audio --dao --native (CD-R). Check the disc later with 'recover --drive <id> --out <folder>'.");
            return;
        }

        PrintHelp(stdout);
    }

    #endregion

    #region Internal Command-Line Argument Parser

    internal sealed class CommandLineArgs
    {
        public string? Subcommand { get; set; }
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> MultiOptions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = new();

        public bool IsJson => HasFlag("json");

        // A switch missing from KnownFlags is parsed as a value-less option; still count it as set so a new switch is
        // never silently ignored (this happened to --overwrite).
        public bool HasFlag(string name) =>
            Flags.Contains(name) || (Options.TryGetValue(name, out var val) && val.Length == 0);

        public string? GetOption(string name) => Options.TryGetValue(name, out var val) ? val : null;

        public IReadOnlyList<string> GetOptions(string name) =>
            MultiOptions.TryGetValue(name, out var list) ? list : Array.Empty<string>();

        private void AddOptionValue(string key, string value)
        {
            Options[key] = value;
            if (!MultiOptions.TryGetValue(key, out var list))
            {
                list = new List<string>();
                MultiOptions[key] = list;
            }
            list.Add(value);
        }

        public string GetRequiredOption(string name, string optionSyntax)
        {
            if (!Options.TryGetValue(name, out var val) || string.IsNullOrWhiteSpace(val))
            {
                throw new CommandLineException($"Missing required option: {optionSyntax}");
            }
            return val;
        }

        /// <summary>
        /// True if the argument is a value rather than another option. Negative numbers (e.g. a read offset of -6)
        /// start with '-' but are values.
        /// </summary>
        private static bool IsOptionValue(string arg) =>
            !arg.StartsWith('-') ||
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        private static bool IsCopyDiscCommand(string? subcommand) =>
            string.Equals(subcommand, "copy-disc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "copydisc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "copy", StringComparison.OrdinalIgnoreCase);

        private static bool IsRecoverCommand(string? subcommand) =>
            string.Equals(subcommand, "recover", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "archive-recover", StringComparison.OrdinalIgnoreCase);

        private static bool KeepImageTakesValue(string[] args, int currentIndex, string? currentSubcommand)
        {
            if (!string.IsNullOrEmpty(currentSubcommand))
            {
                return IsCopyDiscCommand(currentSubcommand);
            }

            // When --keep-image appears before the subcommand, inspect the rest of args to see whether
            // this is 'copy-disc --keep-image <folder>' or 'recover --keep-image'.
            if (currentIndex + 1 < args.Length && IsRecoverCommand(args[currentIndex + 1].Trim()))
            {
                return false;
            }

            for (int j = currentIndex + 1; j < args.Length; j++)
            {
                string token = args[j].Trim();
                if (IsRecoverCommand(token))
                {
                    return false;
                }
                if (IsCopyDiscCommand(token))
                {
                    return true;
                }
            }

            return false;
        }

        public static CommandLineArgs Parse(string[] args)
        {
            var parsed = new CommandLineArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg.StartsWith("--") || (arg.StartsWith('-') && !double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                {
                    string trimmed = arg.TrimStart('-');
                    string key;
                    string? inlineVal = null;

                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        key = trimmed.Substring(0, eqIdx);
                        inlineVal = trimmed.Substring(eqIdx + 1);
                    }
                    else
                    {
                        key = trimmed;
                    }

                    bool isKeepImage = string.Equals(key, "keep-image", StringComparison.OrdinalIgnoreCase);
                    bool isFlag = isKeepImage
                        ? !KeepImageTakesValue(args, i, parsed.Subcommand)
                        : KnownFlags.Contains(key);
                    bool isValueOption = isKeepImage
                        ? !isFlag
                        : KnownValueOptions.Contains(key);

                    if (inlineVal != null)
                    {
                        parsed.AddOptionValue(key, inlineVal);
                    }
                    else if (isFlag)
                    {
                        parsed.Flags.Add(key);
                    }
                    else if (isValueOption)
                    {
                        if (i + 1 < args.Length && IsOptionValue(args[i + 1]))
                        {
                            parsed.AddOptionValue(key, args[++i].Trim());
                        }
                        else
                        {
                            // Value option without value: handler reports missing value cleanly.
                            parsed.AddOptionValue(key, string.Empty);
                        }
                    }
                    else
                    {
                        // Unknown switch: do not consume the following argument so subcommands and
                        // --json still parse and validation reports the unknown option cleanly.
                        parsed.Flags.Add(key);
                    }
                }
                else
                {
                    if (parsed.Subcommand == null)
                    {
                        parsed.Subcommand = arg;
                    }
                    else
                    {
                        parsed.Positionals.Add(arg);
                    }
                }
            }

            return parsed;
        }
    }

    #endregion
}
