using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Devices;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Snapshot of a virtual optical drive backed by <see cref="VirtualMmcDrive"/> and persisted
/// in a virtual state directory so CLI invocations can mount, burn, verify, rip, and inspect
/// virtual media across processes without touching physical drives.
/// </summary>
public sealed record VirtualDriveStatusSnapshot(
    string DriveId,
    string VendorId,
    string ProductId,
    string VolumePath,
    bool TrayOpen,
    bool MediaPresent,
    DiscMediaType MediaType,
    string MediaTypeName,
    bool IsBlank,
    bool IsRewritable,
    bool IsFinalized,
    int TotalSectors,
    int UsedSectors,
    int FreeSectors,
    int SessionCount,
    ushort MmcProfile,
    int DiscStatus,
    string? SourceImagePath);

/// <summary>
/// Persists and restores virtual optical drive state and backing media files in an explicit
/// or default virtual state directory.
/// </summary>
public static class VirtualDriveStateStore
{
    public const string VirtualStateEnvVar = "IGNISDISC_VIRTUAL_STATE";
    private const int CurrentSchemaVersion = 1;
    private const long DirectCopyImageLimitBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IgnisDisc",
        "virtual-state");

    public static string ResolveStateDirectory(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Path.GetFullPath(explicitDirectory.Trim());
        }

        string? fromEnv = Environment.GetEnvironmentVariable(VirtualStateEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv.Trim());
        }

        return DefaultStateDirectory;
    }

    public static bool IsPhysicalDriveIdentifier(string? driveId)
    {
        if (string.IsNullOrWhiteSpace(driveId))
        {
            return false;
        }

        string trimmed = driveId.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Length >= 2 &&
               char.IsLetter(trimmed[0]) &&
               trimmed[1] == ':' &&
               (trimmed.Length == 2 || (trimmed.Length == 3 && trimmed[2] is '\\' or '/')) &&
               char.ToUpperInvariant(trimmed[0]) != 'V';
    }

    public static DiscMediaType ParseBlankMediaType(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        return spec.Trim().ToLowerInvariant() switch
        {
            "cd-r" or "cdr" => DiscMediaType.CdR,
            "cd-rw" or "cdrw" => DiscMediaType.CdRw,
            "dvd-r" or "dvdr" => DiscMediaType.DvdMinusR,
            "dvd-rw" or "dvdrw" => DiscMediaType.DvdMinusRw,
            "dvd+r" or "dvdplusr" => DiscMediaType.DvdPlusR,
            "dvd+rw" or "dvdplusrw" => DiscMediaType.DvdPlusRw,
            "dvd-r-dl" or "dvdrdl" or "dvd-dl" => DiscMediaType.DvdMinusRDualLayer,
            "dvd+r-dl" or "dvd+rdl" or "dvdplusrdl" => DiscMediaType.DvdPlusRDualLayer,
            "dvd-ram" or "dvdram" => DiscMediaType.DvdRam,
            "bd-r" or "bdr" => DiscMediaType.BdR,
            "bd-re" or "bdre" => DiscMediaType.BdRe,
            "bd-r-dl" or "bdrdl" or "bd-dl" => DiscMediaType.BdRDualLayer,
            "bd-re-dl" or "bdredl" => DiscMediaType.BdReDualLayer,
            "bdxl" or "bdxl-tl" or "bd-r-xl" => DiscMediaType.BdxlRTripleLayer,
            "bdxl-ql" => DiscMediaType.BdxlRQuadLayer,
            "bdxl-re" => DiscMediaType.BdxlReTripleLayer,
            _ => throw new ArgumentException(
                $"Unsupported blank virtual media type '{spec}'. Supported: cd-r, cd-rw, dvd-r, dvd-rw, dvd+r, dvd+rw, dvd-r-dl, dvd+r-dl, dvd-ram, bd-r, bd-re, bd-r-dl, bd-re-dl, bdxl, bdxl-ql, bdxl-re.",
                nameof(spec))
        };
    }

    public static bool TryParseBlankMediaType(string? spec, out DiscMediaType mediaType)
    {
        mediaType = DiscMediaType.Unknown;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        try
        {
            mediaType = ParseBlankMediaType(spec);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static VirtualMmcDrive CreateSyncedVirtualMmcDrive(VirtualDiscRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var drive = NativeAudioBurnEngine.CreateVirtualDrive(recorder);
        if (recorder.CurrentMedium is { } medium && drive.Profile == 0)
        {
            drive.Profile = medium.MediaType switch
            {
                DiscMediaType.CdRom => 0x0008,
                DiscMediaType.DvdRom => 0x0010,
                DiscMediaType.BdRom => 0x0040,
                DiscMediaType.BdRDualLayer or DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer => NativeTaoDataWriter.ProfileBdR,
                DiscMediaType.BdReDualLayer or DiscMediaType.BdxlReTripleLayer => NativeTaoDataWriter.ProfileBdRe,
                _ => drive.Profile
            };
        }

        // Exercise the MMC SCSI transport layer (TEST UNIT READY / GET CONFIGURATION / READ DISC INFORMATION)
        // so virtual drive metadata and readiness are verified through VirtualMmcDrive.
        Span<byte> buf = stackalloc byte[32];
        _ = drive.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
        _ = drive.ExecuteCommand(MmcCommandBuilder.BuildGetConfigurationCdb(0, (ushort)buf.Length), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        if (drive.MediaPresent)
        {
            _ = drive.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb((ushort)buf.Length), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        }

        return drive;
    }

    public static VirtualDriveStatusSnapshot QueryStatus(VirtualDiscRecorder recorder, string? sourceImagePath = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        using var mmc = CreateSyncedVirtualMmcDrive(recorder);
        var mediaStatus = recorder.QueryMediaStatus();
        return new VirtualDriveStatusSnapshot(
            DriveId: recorder.UniqueId,
            VendorId: recorder.VendorId,
            ProductId: recorder.ProductId,
            VolumePath: recorder.VolumePath,
            TrayOpen: recorder.TrayOpen,
            MediaPresent: mediaStatus.MediaPresent,
            MediaType: mediaStatus.MediaType,
            MediaTypeName: mediaStatus.MediaType.GetDisplayName(),
            IsBlank: mediaStatus.IsBlank,
            IsRewritable: mediaStatus.IsRewritable,
            IsFinalized: mediaStatus.MediaPresent && !mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsOverwriteOnly,
            TotalSectors: mediaStatus.TotalSectors,
            UsedSectors: mediaStatus.UsedSectors,
            FreeSectors: mediaStatus.FreeSectors,
            SessionCount: mediaStatus.SessionCount,
            MmcProfile: mmc.Profile,
            DiscStatus: mmc.DiscStatus,
            SourceImagePath: sourceImagePath);
    }

    public static VirtualDiscRecorder GetOrCreateVirtualRecorder(DiscDeviceManager deviceManager, string driveId)
    {
        ArgumentNullException.ThrowIfNull(deviceManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(driveId);

        if (IsPhysicalDriveIdentifier(driveId))
        {
            throw new InvalidOperationException($"Drive '{driveId}' refers to a physical drive path. Virtual drive commands never touch physical drives.");
        }

        var existing = deviceManager.GetRecorderById(driveId);
        if (existing != null)
        {
            if (existing is VirtualDiscRecorder virtualRecorder)
            {
                return virtualRecorder;
            }
            throw new InvalidOperationException($"Drive '{driveId}' is not a virtual drive.");
        }

        string normalizedId = driveId.Trim();
        string volumePath = string.Equals(normalizedId, "VDRV-01", StringComparison.OrdinalIgnoreCase)
            ? @"V:\"
            : $@"V:\{SanitizeFileComponent(normalizedId)}\";
        var created = new VirtualDiscRecorder(
            uniqueId: normalizedId,
            vendorId: "IGNIS",
            productId: "VIRTUAL DVD/CD-RW",
            volumePath: volumePath);
        deviceManager.AddVirtualRecorder(created);
        return created;
    }

    public static Dictionary<string, string?> LoadIntoDeviceManager(DiscDeviceManager deviceManager, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(deviceManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        var sourceImagesByDrive = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(stateDirectory))
        {
            return sourceImagesByDrive;
        }

        foreach (string statePath in Directory.EnumerateFiles(stateDirectory, "*.state.json", SearchOption.TopDirectoryOnly))
        {
            VirtualDrivePersistedState? state;
            try
            {
                string json = File.ReadAllText(statePath);
                state = JsonSerializer.Deserialize<VirtualDrivePersistedState>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                continue;
            }

            if (state == null || string.IsNullOrWhiteSpace(state.UniqueId))
            {
                continue;
            }

            VirtualDiscRecorder recorder;
            if (deviceManager.GetRecorderById(state.UniqueId) is VirtualDiscRecorder existing)
            {
                recorder = existing;
            }
            else
            {
                recorder = new VirtualDiscRecorder(
                    uniqueId: state.UniqueId,
                    vendorId: string.IsNullOrWhiteSpace(state.VendorId) ? "IGNIS" : state.VendorId,
                    productId: string.IsNullOrWhiteSpace(state.ProductId) ? "VIRTUAL DVD/CD-RW" : state.ProductId,
                    volumePath: string.IsNullOrWhiteSpace(state.VolumePath) ? $@"V:\{SanitizeFileComponent(state.UniqueId)}\" : state.VolumePath)
                {
                    TrayClosesItself = state.TrayClosesItself
                };
                deviceManager.AddVirtualRecorder(recorder);
            }

            if (state.Medium != null)
            {
                var medium = ReconstructMedium(state.Medium, stateDirectory);
                if (state.TrayOpen)
                {
                    recorder.InsertMedia(medium);
                    recorder.EjectMedia();
                }
                else
                {
                    recorder.InsertMedia(medium);
                }
                sourceImagesByDrive[recorder.UniqueId] = state.Medium.SourceImagePath;
            }
            else
            {
                recorder.UnloadMedia(openTray: state.TrayOpen);
                sourceImagesByDrive[recorder.UniqueId] = null;
            }
        }

        return sourceImagesByDrive;
    }

    public static VirtualDiscMedium MountBlank(
        VirtualDiscRecorder recorder,
        DiscMediaType mediaType,
        string? stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (!mediaType.IsWritable())
        {
            throw new ArgumentException($"Media type '{mediaType.GetDisplayName()}' is not writable.", nameof(mediaType));
        }

        ReleaseExistingMedium(recorder);

        string? backingPath = null;
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
            backingPath = GetBackingFilePath(stateDirectory, recorder.UniqueId);
            DeleteFileIfExists(backingPath);
        }

        int capacity = mediaType.GetNominalCapacitySectors();
        var medium = new VirtualDiscMedium(
            mediaType,
            capacity,
            isBlank: true,
            backingFilePath: backingPath);

        recorder.InsertMedia(medium);
        using (CreateSyncedVirtualMmcDrive(recorder)) { }

        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            SaveDriveState(recorder, stateDirectory, sourceImagePath: null);
        }

        return medium;
    }

    public static VirtualDiscMedium MountImage(
        VirtualDiscRecorder recorder,
        string imagePath,
        string? stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{fullPath}'.", fullPath);
        }

        ReleaseExistingMedium(recorder);

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        string? backingPath = null;
        bool readOnlyBacking = false;
        int sectorCount;

        if (ext is ".iso" or ".udf")
        {
            long length = new FileInfo(fullPath).Length;
            if (length == 0)
            {
                throw new InvalidDataException($"Disc image '{Path.GetFileName(fullPath)}' is empty (0 bytes).");
            }
            sectorCount = OpticalConstants.BytesToSectors(length, OpticalConstants.DataSectorSize);

            if (!string.IsNullOrWhiteSpace(stateDirectory))
            {
                Directory.CreateDirectory(stateDirectory);
                string candidateBacking = GetBackingFilePath(stateDirectory, recorder.UniqueId);
                DeleteFileIfExists(candidateBacking);
                if (length <= DirectCopyImageLimitBytes)
                {
                    File.Copy(fullPath, candidateBacking, overwrite: true);
                    backingPath = candidateBacking;
                }
                else
                {
                    backingPath = fullPath;
                    readOnlyBacking = true;
                }
            }
            else
            {
                backingPath = fullPath;
                readOnlyBacking = true;
            }
        }
        else
        {
            using Stream userDataStream = ext == ".cue"
                ? LegacyDiscImageConverter.OpenCueIsoUserDataStream(fullPath)
                : LegacyDiscImageConverter.OpenIsoUserDataStream(fullPath);

            if (userDataStream.Length == 0)
            {
                throw new InvalidDataException($"Disc image '{Path.GetFileName(fullPath)}' contains no readable data sectors.");
            }
            sectorCount = OpticalConstants.BytesToSectors(userDataStream.Length, OpticalConstants.DataSectorSize);

            if (!string.IsNullOrWhiteSpace(stateDirectory))
            {
                Directory.CreateDirectory(stateDirectory);
                backingPath = GetBackingFilePath(stateDirectory, recorder.UniqueId);
                DeleteFileIfExists(backingPath);
                using var dest = new FileStream(backingPath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536);
                userDataStream.CopyTo(dest);
            }
            else
            {
                string tempBacking = Path.Combine(Path.GetTempPath(), $"IgnisDisc_VMount_{Guid.NewGuid():N}.bin");
                using (var dest = new FileStream(tempBacking, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
                {
                    userDataStream.CopyTo(dest);
                }
                backingPath = tempBacking;
            }
        }

        DiscMediaType mediaType = sectorCount <= OpticalConstants.Cd700MbSectors
            ? DiscMediaType.CdRom
            : sectorCount <= OpticalConstants.DvdPlusR5Sectors
                ? DiscMediaType.DvdRom
                : DiscMediaType.BdRom;

        int totalCapacity = Math.Max(sectorCount, mediaType.GetNominalCapacitySectors());
        bool deleteOnDispose = string.IsNullOrWhiteSpace(stateDirectory) && !readOnlyBacking;

        var medium = new VirtualDiscMedium(
            mediaType,
            totalCapacity,
            isBlank: false,
            backingFilePath: backingPath,
            raw2352Mode: false,
            deleteBackingFileOnDispose: deleteOnDispose,
            readOnlyBacking: readOnlyBacking)
        {
            UsedSectors = sectorCount,
            IsFinalized = true
        };

        var session = new VirtualSession
        {
            SessionNumber = 1,
            StartLba = 0,
            SectorCount = sectorCount
        };
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 1,
            StartLba = 0,
            SectorCount = sectorCount,
            IsAudio = false
        });
        medium.Sessions.Add(session);

        recorder.InsertMedia(medium);
        using (CreateSyncedVirtualMmcDrive(recorder)) { }

        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            SaveDriveState(recorder, stateDirectory, fullPath);
        }

        return medium;
    }

    public static (VirtualDiscMedium Medium, string? SourceImagePath) MountSpec(
        VirtualDiscRecorder recorder,
        string spec,
        string? stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        string trimmed = spec.Trim();
        if (trimmed.StartsWith("blank:", StringComparison.OrdinalIgnoreCase))
        {
            string blankSpec = trimmed["blank:".Length..];
            var mediaType = ParseBlankMediaType(blankSpec);
            return (MountBlank(recorder, mediaType, stateDirectory), null);
        }

        if (trimmed.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            string imgSpec = trimmed["image:".Length..];
            string fullImg = Path.GetFullPath(imgSpec);
            return (MountImage(recorder, fullImg, stateDirectory), fullImg);
        }

        if (TryParseBlankMediaType(trimmed, out var parsedType))
        {
            return (MountBlank(recorder, parsedType, stateDirectory), null);
        }

        string candidatePath = Path.GetFullPath(trimmed);
        if (!File.Exists(candidatePath))
        {
            throw new FileNotFoundException(
                $"Virtual media '{spec}' is neither a supported blank type (cd-r, cd-rw, dvd-r, dvd+r, dvd+rw, dvd-r-dl, bd-r, bd-re, bd-r-dl, bdxl) nor an existing image file.",
                candidatePath);
        }

        return (MountImage(recorder, candidatePath, stateDirectory), candidatePath);
    }

    public static void EjectAndClear(VirtualDiscRecorder recorder, string? stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ReleaseExistingMedium(recorder);
        recorder.UnloadMedia(openTray: true);

        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
            DeleteFileIfExists(GetBackingFilePath(stateDirectory, recorder.UniqueId));
            SaveDriveState(recorder, stateDirectory, sourceImagePath: null);
        }
    }

    public static void SaveDriveState(VirtualDiscRecorder recorder, string stateDirectory, string? sourceImagePath = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        Directory.CreateDirectory(stateDirectory);
        using var mmc = CreateSyncedVirtualMmcDrive(recorder);

        VirtualMediumPersistedState? mediumState = null;
        if (recorder.CurrentMedium is { } m)
        {
            mediumState = BuildMediumState(m, recorder.UniqueId, stateDirectory, sourceImagePath);
        }
        else if (recorder.TrayOpen && recorder.HasDisc)
        {
            // Tray holds an ejected disc: reload temporarily only if needed, otherwise persist empty.
        }

        var state = new VirtualDrivePersistedState
        {
            SchemaVersion = CurrentSchemaVersion,
            UniqueId = recorder.UniqueId,
            VendorId = recorder.VendorId,
            ProductId = recorder.ProductId,
            ProductRevision = recorder.ProductRevision,
            VolumePath = recorder.VolumePath,
            TrayClosesItself = recorder.TrayClosesItself,
            TrayOpen = recorder.TrayOpen,
            MmcProfile = mmc.Profile,
            MmcDiscStatus = mmc.DiscStatus,
            Medium = mediumState
        };

        string stateFile = GetStateFilePath(stateDirectory, recorder.UniqueId);
        string tempFile = stateFile + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempFile, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempFile, stateFile, overwrite: true);
    }

    public static void SaveDeviceManagerState(
        DiscDeviceManager deviceManager,
        string stateDirectory,
        IReadOnlyDictionary<string, string?>? sourceImagesByDrive = null)
    {
        ArgumentNullException.ThrowIfNull(deviceManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        foreach (var recorder in deviceManager.GetVirtualRecorders())
        {
            string stateFile = GetStateFilePath(stateDirectory, recorder.UniqueId);
            if (recorder.CurrentMedium != null || recorder.TrayOpen || File.Exists(stateFile))
            {
                string? sourceImg = null;
                sourceImagesByDrive?.TryGetValue(recorder.UniqueId, out sourceImg);
                SaveDriveState(recorder, stateDirectory, sourceImg);
            }
        }
    }

    private static VirtualMediumPersistedState BuildMediumState(
        VirtualDiscMedium medium,
        string driveId,
        string stateDirectory,
        string? sourceImagePath)
    {
        string? backingFileName = null;
        bool backingReadOnly = false;
        string? resolvedSource = sourceImagePath;

        string expectedBacking = GetBackingFilePath(stateDirectory, driveId);
        if (!string.IsNullOrEmpty(medium.BackingFilePath) &&
            string.Equals(Path.GetFullPath(medium.BackingFilePath), Path.GetFullPath(expectedBacking), StringComparison.OrdinalIgnoreCase))
        {
            backingFileName = Path.GetFileName(expectedBacking);
        }
        else if (!string.IsNullOrEmpty(medium.BackingFilePath) && File.Exists(medium.BackingFilePath))
        {
            resolvedSource ??= medium.BackingFilePath;
            backingReadOnly = true;
        }
        else if (medium.UsedSectors > 0)
        {
            // Persist in-memory written sectors into the state backing file.
            using var fs = new FileStream(expectedBacking, FileMode.Create, FileAccess.Write, FileShare.None, 65_536);
            int sectorSize = medium.SectorSize;
            byte[] buf = new byte[sectorSize];
            for (int lba = 0; lba < medium.UsedSectors; lba++)
            {
                medium.ReadSector(lba, buf, sectorSize);
                fs.Write(buf, 0, sectorSize);
            }
            backingFileName = Path.GetFileName(expectedBacking);
        }

        var state = new VirtualMediumPersistedState
        {
            MediaType = medium.MediaType,
            BookType = medium.BookType,
            TotalCapacitySectors = medium.TotalCapacitySectors,
            IsBlank = medium.IsBlank,
            InRestrictedOverwriteMode = medium.InRestrictedOverwriteMode,
            IsFinalized = medium.IsFinalized,
            Raw2352Mode = medium.Raw2352Mode,
            UsedSectors = medium.UsedSectors,
            NextWritableLba = medium.NextWritableLba,
            SourceImagePath = resolvedSource,
            BackingFileName = backingFileName,
            BackingFileReadOnly = backingReadOnly,
            CdTextBase64 = medium.CdTextBytes is { Length: > 0 } ? Convert.ToBase64String(medium.CdTextBytes) : null,
            MediaCatalogNumber = medium.MediaCatalogNumber
        };

        foreach (var s in medium.Sessions)
        {
            var ps = new VirtualSessionPersistedState
            {
                SessionNumber = s.SessionNumber,
                StartLba = s.StartLba,
                SectorCount = s.SectorCount
            };
            foreach (var t in s.Tracks)
            {
                ps.Tracks.Add(new VirtualTrackPersistedState
                {
                    TrackNumber = t.TrackNumber,
                    StartLba = t.StartLba,
                    SectorCount = t.SectorCount,
                    IsAudio = t.IsAudio,
                    PreGapFrames = t.PreGapFrames,
                    Title = t.Title,
                    Performer = t.Performer,
                    Songwriter = t.Songwriter,
                    Composer = t.Composer,
                    Isrc = t.Isrc,
                    PreEmphasis = t.PreEmphasis,
                    CopyPermitted = t.CopyPermitted,
                    Index00Lba = t.Index00Lba
                });
            }
            state.Sessions.Add(ps);
        }

        return state;
    }

    private static VirtualDiscMedium ReconstructMedium(VirtualMediumPersistedState state, string stateDirectory)
    {
        string? backingPath = null;
        if (!string.IsNullOrWhiteSpace(state.BackingFileName))
        {
            backingPath = Path.Combine(stateDirectory, Path.GetFileName(state.BackingFileName));
        }
        else if (!string.IsNullOrWhiteSpace(state.SourceImagePath) && File.Exists(state.SourceImagePath))
        {
            backingPath = state.SourceImagePath;
        }

        var medium = new VirtualDiscMedium(
            state.MediaType,
            state.TotalCapacitySectors > 0 ? state.TotalCapacitySectors : state.MediaType.GetNominalCapacitySectors(),
            state.IsBlank,
            backingFilePath: backingPath,
            raw2352Mode: state.Raw2352Mode,
            deleteBackingFileOnDispose: false,
            readOnlyBacking: state.BackingFileReadOnly)
        {
            InRestrictedOverwriteMode = state.InRestrictedOverwriteMode,
            IsFinalized = state.IsFinalized,
            UsedSectors = state.UsedSectors,
            NextWritableLba = state.NextWritableLba,
            BookType = state.BookType,
            MediaCatalogNumber = state.MediaCatalogNumber,
            CdTextBytes = string.IsNullOrWhiteSpace(state.CdTextBase64) ? null : Convert.FromBase64String(state.CdTextBase64)
        };

        foreach (var ps in state.Sessions)
        {
            var s = new VirtualSession
            {
                SessionNumber = ps.SessionNumber,
                StartLba = ps.StartLba,
                SectorCount = ps.SectorCount
            };
            foreach (var pt in ps.Tracks)
            {
                s.Tracks.Add(new VirtualTrack
                {
                    TrackNumber = pt.TrackNumber,
                    StartLba = pt.StartLba,
                    SectorCount = pt.SectorCount,
                    IsAudio = pt.IsAudio,
                    PreGapFrames = pt.PreGapFrames,
                    Title = pt.Title,
                    Performer = pt.Performer,
                    Songwriter = pt.Songwriter,
                    Composer = pt.Composer,
                    Isrc = pt.Isrc,
                    PreEmphasis = pt.PreEmphasis,
                    CopyPermitted = pt.CopyPermitted,
                    Index00Lba = pt.Index00Lba
                });
            }
            medium.Sessions.Add(s);
        }

        return medium;
    }

    private static void ReleaseExistingMedium(VirtualDiscRecorder recorder)
    {
        var previous = recorder.CurrentMedium;
        recorder.UnloadMedia(openTray: false);
        previous?.Dispose();
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetStateFilePath(string stateDirectory, string driveId) =>
        Path.Combine(stateDirectory, $"{SanitizeFileComponent(driveId)}.state.json");

    private static string GetBackingFilePath(string stateDirectory, string driveId) =>
        Path.Combine(stateDirectory, $"{SanitizeFileComponent(driveId)}.media.bin");

    private static string SanitizeFileComponent(string value)
    {
        Span<char> chars = stackalloc char[value.Length];
        int len = 0;
        foreach (char c in value)
        {
            chars[len++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return len == 0 ? "VDRV-01" : new string(chars[..len]);
    }

    private sealed class VirtualDrivePersistedState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string UniqueId { get; set; } = "VDRV-01";
        public string VendorId { get; set; } = "IGNIS";
        public string ProductId { get; set; } = "VIRTUAL DVD/CD-RW";
        public string ProductRevision { get; set; } = "1.00";
        public string VolumePath { get; set; } = @"V:\";
        public bool TrayClosesItself { get; set; }
        public bool TrayOpen { get; set; }
        public ushort MmcProfile { get; set; }
        public int MmcDiscStatus { get; set; }
        public VirtualMediumPersistedState? Medium { get; set; }
    }

    private sealed class VirtualMediumPersistedState
    {
        public DiscMediaType MediaType { get; set; }
        public DvdBookType BookType { get; set; }
        public int TotalCapacitySectors { get; set; }
        public bool IsBlank { get; set; }
        public bool InRestrictedOverwriteMode { get; set; }
        public bool IsFinalized { get; set; }
        public bool Raw2352Mode { get; set; }
        public int UsedSectors { get; set; }
        public int NextWritableLba { get; set; }
        public string? SourceImagePath { get; set; }
        public string? BackingFileName { get; set; }
        public bool BackingFileReadOnly { get; set; }
        public string? CdTextBase64 { get; set; }
        public string? MediaCatalogNumber { get; set; }
        public List<VirtualSessionPersistedState> Sessions { get; set; } = new();
    }

    private sealed class VirtualSessionPersistedState
    {
        public int SessionNumber { get; set; }
        public int StartLba { get; set; }
        public int SectorCount { get; set; }
        public List<VirtualTrackPersistedState> Tracks { get; set; } = new();
    }

    private sealed class VirtualTrackPersistedState
    {
        public int TrackNumber { get; set; }
        public int StartLba { get; set; }
        public int SectorCount { get; set; }
        public bool IsAudio { get; set; }
        public int PreGapFrames { get; set; }
        public string? Title { get; set; }
        public string? Performer { get; set; }
        public string? Songwriter { get; set; }
        public string? Composer { get; set; }
        public string? Isrc { get; set; }
        public bool PreEmphasis { get; set; }
        public bool CopyPermitted { get; set; }
        public int? Index00Lba { get; set; }
    }
}
