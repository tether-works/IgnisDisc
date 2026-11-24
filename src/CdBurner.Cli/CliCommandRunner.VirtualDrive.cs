using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Cli;

/// <summary>
/// Virtual optical drive control commands (<c>virtual-mount</c>, <c>virtual-eject</c>, <c>virtual-status</c>)
/// and one-shot virtual automation context (<c>--virtual-media</c>, <c>--virtual-state</c>).
/// </summary>
public static partial class CliCommandRunner
{
    internal sealed class VirtualAutomationContext : IDisposable
    {
        public string? StateDirectory { get; init; }
        public bool ExplicitState { get; init; }
        public Dictionary<string, string?> SourceImagesByDrive { get; } = new(StringComparer.OrdinalIgnoreCase);
        public VirtualDiscMedium? EphemeralMedia { get; set; }

        public void Dispose()
        {
            EphemeralMedia?.Dispose();
            EphemeralMedia = null;
        }
    }

    internal static bool IsVirtualDriveSubcommand(string? subcommand) =>
        subcommand is not null &&
        (subcommand.Equals("virtual-mount", StringComparison.OrdinalIgnoreCase) ||
         subcommand.Equals("virtualmount", StringComparison.OrdinalIgnoreCase) ||
         subcommand.Equals("virtual-eject", StringComparison.OrdinalIgnoreCase) ||
         subcommand.Equals("virtualeject", StringComparison.OrdinalIgnoreCase) ||
         subcommand.Equals("virtual-status", StringComparison.OrdinalIgnoreCase) ||
         subcommand.Equals("virtualstatus", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldSkipPhysicalEnumeration(CommandLineArgs cmd)
    {
        if (IsVirtualDriveSubcommand(cmd.Subcommand) ||
            cmd.Options.ContainsKey("virtual-state") ||
            cmd.Options.ContainsKey("virtual-media"))
        {
            return true;
        }

        return IsExplicitVirtualDriveId(cmd.GetOption("drive")) ||
               IsExplicitVirtualDriveId(cmd.GetOption("source")) ||
               IsExplicitVirtualDriveId(cmd.GetOption("target"));
    }

    private static bool IsExplicitVirtualDriveId(string? driveId)
    {
        if (string.IsNullOrWhiteSpace(driveId))
        {
            return false;
        }

        string trimmed = driveId.Trim();
        return trimmed.StartsWith("VDRV-", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "V:", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, @"V:\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveTargetDriveId(CommandLineArgs cmd)
    {
        if (!cmd.Options.ContainsKey("drive") && cmd.Options.ContainsKey("virtual-media"))
        {
            return "VDRV-01";
        }
        return cmd.GetRequiredOption("drive", "--drive <id>");
    }

    internal static VirtualAutomationContext PrepareVirtualAutomation(
        CommandLineArgs cmd,
        IDiscDeviceService deviceService,
        bool ownsDeviceService)
    {
        string? explicitStateRaw = cmd.GetOption("virtual-state");
        if (cmd.Options.ContainsKey("virtual-state") && string.IsNullOrWhiteSpace(explicitStateRaw))
        {
            throw new CommandLineException("Missing value for option '--virtual-state <path>'.");
        }

        string? virtualMediaSpec = cmd.GetOption("virtual-media");
        if (cmd.Options.ContainsKey("virtual-media") && string.IsNullOrWhiteSpace(virtualMediaSpec))
        {
            throw new CommandLineException("Missing value for option '--virtual-media <spec>'.");
        }

        bool explicitState = !string.IsNullOrWhiteSpace(explicitStateRaw);
        string? stateDir = explicitState
            ? Path.GetFullPath(explicitStateRaw!.Trim())
            : (!string.IsNullOrWhiteSpace(virtualMediaSpec)
                ? null
                : (ownsDeviceService ? VirtualDriveStateStore.ResolveStateDirectory(null) : null));

        var context = new VirtualAutomationContext
        {
            StateDirectory = stateDir,
            ExplicitState = explicitState
        };

        if (deviceService is DiscDeviceManager dm && stateDir != null)
        {
            foreach (var kvp in VirtualDriveStateStore.LoadIntoDeviceManager(dm, stateDir))
            {
                context.SourceImagesByDrive[kvp.Key] = kvp.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(virtualMediaSpec))
        {
            string targetDriveId = ResolveTargetDriveId(cmd);
            VirtualDiscRecorder vRec = ResolveVirtualRecorder(deviceService, targetDriveId, allowCreate: true);
            try
            {
                var (mounted, sourceImagePath) = VirtualDriveStateStore.MountSpec(vRec, virtualMediaSpec, stateDir);
                context.SourceImagesByDrive[vRec.UniqueId] = sourceImagePath;
                if (stateDir == null)
                {
                    context.EphemeralMedia = mounted;
                }
            }
            catch (ArgumentException ex)
            {
                throw new CommandLineException(ex.Message);
            }
        }

        return context;
    }

    internal static void SaveVirtualAutomationState(
        VirtualAutomationContext context,
        IDiscDeviceService deviceService,
        string subcommand)
    {
        if (context.StateDirectory == null || deviceService is not DiscDeviceManager dm)
        {
            return;
        }

        bool mutating = subcommand is "burn-iso" or "burniso" or "burn-data" or "burndata"
            or "burn-audio" or "burnaudio" or "burn-dvd" or "burndvd"
            or "archive-burn" or "archiveburn" or "burn-queue" or "burnqueue"
            or "erase" or "erase-disc" or "eject" or "load"
            or "book-type" or "booktype" or "copy-disc" or "copydisc" or "copy"
            or "rosetta" or "disc-art" or "discart";

        if (context.ExplicitState || IsVirtualDriveSubcommand(subcommand) ||
            (mutating && Directory.Exists(context.StateDirectory)))
        {
            VirtualDriveStateStore.SaveDeviceManagerState(dm, context.StateDirectory, context.SourceImagesByDrive);
        }
    }

    private static VirtualDiscRecorder ResolveVirtualRecorder(
        IDiscDeviceService deviceService,
        string driveId,
        bool allowCreate)
    {
        if (VirtualDriveStateStore.IsPhysicalDriveIdentifier(driveId))
        {
            throw new CommandLineException($"Drive '{driveId}' refers to a physical drive path. Virtual drive commands never touch physical drives.");
        }

        if (deviceService is DiscDeviceManager dm)
        {
            if (allowCreate)
            {
                try
                {
                    return VirtualDriveStateStore.GetOrCreateVirtualRecorder(dm, driveId);
                }
                catch (InvalidOperationException ex)
                {
                    throw new CommandLineException(ex.Message);
                }
            }
        }

        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is not VirtualDiscRecorder vRec)
        {
            throw new CommandLineException($"Drive '{driveId}' is not a virtual drive. Virtual drive commands never touch physical drives.");
        }
        return vRec;
    }

    private static int HandleVirtualMount(
        CommandLineArgs cmd,
        TextWriter stdout,
        IDiscDeviceService deviceService,
        VirtualAutomationContext virtualContext)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        bool hasBlank = cmd.Options.TryGetValue("blank", out string? blankSpec);
        bool hasImage = cmd.Options.TryGetValue("image", out string? imagePath);

        if (hasBlank && hasImage)
        {
            throw new CommandLineException("Specify either --blank <type> or --image <path>, not both.");
        }
        if (!hasBlank && !hasImage)
        {
            throw new CommandLineException("Specify either --blank <type> or --image <path>.");
        }
        if (hasBlank && string.IsNullOrWhiteSpace(blankSpec))
        {
            throw new CommandLineException("Missing value for option '--blank <type>'.");
        }
        if (hasImage && string.IsNullOrWhiteSpace(imagePath))
        {
            throw new CommandLineException("Missing value for option '--image <path>'.");
        }

        var recorder = ResolveVirtualRecorder(deviceService, driveId, allowCreate: true);
        string? stateDir = virtualContext.StateDirectory;
        string? sourceImagePath = null;

        try
        {
            if (hasBlank)
            {
                DiscMediaType mediaType = VirtualDriveStateStore.ParseBlankMediaType(blankSpec!);
                VirtualDriveStateStore.MountBlank(recorder, mediaType, stateDir);
            }
            else
            {
                sourceImagePath = Path.GetFullPath(imagePath!);
                VirtualDriveStateStore.MountImage(recorder, sourceImagePath, stateDir);
            }
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException(ex.Message);
        }

        virtualContext.SourceImagesByDrive[recorder.UniqueId] = sourceImagePath;
        var status = VirtualDriveStateStore.QueryStatus(recorder, sourceImagePath);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                driveId = status.DriveId,
                stateDirectory = stateDir,
                trayOpen = status.TrayOpen,
                mediaPresent = status.MediaPresent,
                mediaType = status.MediaType.ToString(),
                mediaTypeName = status.MediaTypeName,
                isBlank = status.IsBlank,
                isRewritable = status.IsRewritable,
                isFinalized = status.IsFinalized,
                totalSectors = status.TotalSectors,
                usedSectors = status.UsedSectors,
                freeSectors = status.FreeSectors,
                sessionCount = status.SessionCount,
                mmcProfile = $"0x{status.MmcProfile:X4}",
                discStatus = status.DiscStatus,
                sourceImagePath = status.SourceImagePath
            });
            return 0;
        }

        stdout.WriteLine($"Mounted {(status.IsBlank ? "blank " : string.Empty)}{status.MediaTypeName} in virtual drive '{status.DriveId}':");
        stdout.WriteLine($"  Total Sectors: {status.TotalSectors:N0}");
        stdout.WriteLine($"  Used Sectors:  {status.UsedSectors:N0}");
        stdout.WriteLine($"  MMC Profile:   0x{status.MmcProfile:X4}");
        if (status.SourceImagePath != null)
        {
            stdout.WriteLine($"  Source Image:  {status.SourceImagePath}");
        }
        if (stateDir != null)
        {
            stdout.WriteLine($"  State Dir:     {stateDir}");
        }
        return 0;
    }

    private static int HandleVirtualEject(
        CommandLineArgs cmd,
        TextWriter stdout,
        IDiscDeviceService deviceService,
        VirtualAutomationContext virtualContext)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = ResolveVirtualRecorder(deviceService, driveId, allowCreate: false);
        string? stateDir = virtualContext.StateDirectory;

        VirtualDriveStateStore.EjectAndClear(recorder, stateDir);
        virtualContext.SourceImagesByDrive.Remove(recorder.UniqueId);
        var status = VirtualDriveStateStore.QueryStatus(recorder, null);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                driveId = status.DriveId,
                stateDirectory = stateDir,
                ejected = true,
                trayOpen = status.TrayOpen,
                mediaPresent = status.MediaPresent,
                mmcProfile = $"0x{status.MmcProfile:X4}",
                discStatus = status.DiscStatus
            });
            return 0;
        }

        stdout.WriteLine($"Ejected virtual media from '{status.DriveId}' (tray open, drive empty).");
        if (stateDir != null)
        {
            stdout.WriteLine($"  State Dir: {stateDir}");
        }
        return 0;
    }

    private static int HandleVirtualStatus(
        CommandLineArgs cmd,
        TextWriter stdout,
        IDiscDeviceService deviceService,
        VirtualAutomationContext virtualContext)
    {
        string? driveFilter = cmd.GetOption("drive");
        if (cmd.Options.ContainsKey("drive") && string.IsNullOrWhiteSpace(driveFilter))
        {
            throw new CommandLineException("Missing value for option '--drive <id>'.");
        }

        List<VirtualDiscRecorder> recorders;
        if (!string.IsNullOrWhiteSpace(driveFilter))
        {
            recorders = [ResolveVirtualRecorder(deviceService, driveFilter, allowCreate: false)];
        }
        else
        {
            recorders = deviceService.GetRecorders(includeVirtual: true)
                .OfType<VirtualDiscRecorder>()
                .OrderBy(r => r.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var snapshots = recorders.Select(r =>
        {
            virtualContext.SourceImagesByDrive.TryGetValue(r.UniqueId, out string? srcImg);
            return VirtualDriveStateStore.QueryStatus(r, srcImg);
        }).ToList();

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                stateDirectory = virtualContext.StateDirectory,
                drives = snapshots.Select(s => new
                {
                    driveId = s.DriveId,
                    vendorId = s.VendorId,
                    productId = s.ProductId,
                    volumePath = s.VolumePath,
                    trayOpen = s.TrayOpen,
                    mediaPresent = s.MediaPresent,
                    mediaType = s.MediaType.ToString(),
                    mediaTypeName = s.MediaTypeName,
                    isBlank = s.IsBlank,
                    isRewritable = s.IsRewritable,
                    isFinalized = s.IsFinalized,
                    totalSectors = s.TotalSectors,
                    usedSectors = s.UsedSectors,
                    freeSectors = s.FreeSectors,
                    sessionCount = s.SessionCount,
                    mmcProfile = $"0x{s.MmcProfile:X4}",
                    discStatus = s.DiscStatus,
                    sourceImagePath = s.SourceImagePath
                }).ToList()
            });
            return 0;
        }

        stdout.WriteLine($"Virtual drive status ({snapshots.Count} drive(s)):");
        if (virtualContext.StateDirectory != null)
        {
            stdout.WriteLine($"  State Dir: {virtualContext.StateDirectory}");
        }
        foreach (var s in snapshots)
        {
            string stateDesc = s.MediaPresent
                ? $"{s.MediaTypeName}, {(s.IsBlank ? "blank" : $"{s.UsedSectors:N0}/{s.TotalSectors:N0} sectors")}"
                : (s.TrayOpen ? "empty (tray open)" : "empty");
            stdout.WriteLine($"  {s.DriveId,-12} {s.VolumePath,-8} [{stateDesc}]  MMC=0x{s.MmcProfile:X4}");
        }
        return 0;
    }
}
