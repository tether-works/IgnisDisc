using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private static bool HasMultiDriveOption(CommandLineArgs cmd) =>
        cmd.Options.ContainsKey("drives");

    private static IReadOnlyList<string> ParseDriveIdList(CommandLineArgs cmd, string optionName = "drives")
    {
        string raw = cmd.GetRequiredOption(optionName, $"--{optionName} <id1,id2,...>");
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new CommandLineException($"Invalid --{optionName} list '{raw}': provide comma-separated drive IDs (e.g. --{optionName} D:,E:).");
        }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts)
        {
            if (!distinct.Add(part))
            {
                throw new CommandLineException($"Duplicate drive '{part}' in --{optionName}. Each target must be a distinct drive.");
            }
        }

        return parts;
    }

    private static IReadOnlyList<IDiscRecorderBackend> ResolveTargetRecorders(
        IDiscDeviceService deviceService,
        IReadOnlyList<string> driveIds)
    {
        var recorders = new List<IDiscRecorderBackend>(driveIds.Count);
        var seenUniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in driveIds)
        {
            var recorder = FindRecorder(deviceService, id);
            if (!seenUniqueIds.Add(recorder.UniqueId))
            {
                throw new CommandLineException($"Drive '{id}' resolves to duplicate recorder '{recorder.UniqueId}'. Each target must be a distinct drive.");
            }
            recorders.Add(recorder);
        }

        return recorders;
    }

    private static async Task<int> HandleBurnIsoMultiDriveAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        if (cmd.Options.ContainsKey("drive"))
        {
            throw new CommandLineException("Cannot combine --drive and --drives.");
        }

        var driveIds = ParseDriveIdList(cmd);
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso>");
        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        string? speedStr = cmd.GetOption("speed");
        var (nativeEngineTemplate, testWrite) = ResolveNativeDataEngine(cmd, burnEngine, hasDirectDefault: true);
        bool windowsWriter = cmd.HasFlag("windows-writer");
        if (nativeEngineTemplate != null && windowsWriter)
        {
            throw new CommandLineException("--native and --windows-writer cannot be used together.");
        }

        bool strictVerify = cmd.HasFlag("strict-verify");
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        DvdBookType? requestedBookType = ParseOptionalBookType(cmd);
        bool requireBookType = cmd.HasFlag("require-book-type");
        var postAction = ParsePostBurnAction(cmd);
        string? telemetryOut = ParseTelemetryOutPath(cmd);
        string? logDir = cmd.GetOption("log-dir");
        bool testFirst = cmd.HasFlag("test-first");

        var recorders = ResolveTargetRecorders(deviceService, driveIds);
        var firstStatus = recorders[0].QueryMediaStatus();
        bool useDirect = nativeEngineTemplate != null || (!windowsWriter && recorders.Any(r => DirectWritingDefault.Prefers(r.QueryMediaStatus())));
        if (nativeEngineTemplate == null && useDirect && !cmd.IsJson)
        {
            stdout.WriteLine(DirectWritingDefault.Note + " (--windows-writer uses Windows' writer.)");
        }

        var validationNative = useDirect ? (nativeEngineTemplate ?? new NativeDataBurnEngine(burnEngine, logDir)) : null;
        bool streamingWrite = ParseStreamingWrite(cmd, validationNative);
        int? layerBreak = ParseLayerBreak(cmd, validationNative, firstStatus);
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || useDirect));
        var speedDesc = ParseSpeed(speedStr, recorders[0], firstStatus.MediaType);

        var burnOptions = new DataBurnOptions
        {
            Speed = speedDesc,
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite,
            LayerBreakSectors = layerBreak,
            DisableBufferUnderrunFree = disableBufe,
            StreamingWrite = streamingWrite,
            RequestedBookType = requestedBookType,
            RequireBookType = requireBookType
        };

        var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(
            burnEngine,
            useDirectDriveCommands: useDirect,
            logDirectory: logDir,
            testWriteFirst: testFirst);

        var result = await coordinator.BurnImageAsync(recorders, isoPath, burnOptions).ConfigureAwait(false);
        return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: Path.GetFullPath(isoPath), sourceDriveId: null);
    }

    private static async Task<int> HandleDuplicateAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        if (cmd.Options.ContainsKey("drive"))
        {
            throw new CommandLineException("Use --drives <id1,id2,...> with 'duplicate'.");
        }

        var driveIds = ParseDriveIdList(cmd);
        var recorders = ResolveTargetRecorders(deviceService, driveIds);

        string? sourceId = cmd.GetOption("source");
        string? isoPath = cmd.GetOption("iso");
        string? discFolder = cmd.GetOption("disc");
        bool hasTracks = !string.IsNullOrWhiteSpace(cmd.GetOption("tracks")) || !string.IsNullOrWhiteSpace(cmd.GetOption("tracks-file"));

        int sourceKinds = (string.IsNullOrWhiteSpace(sourceId) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(isoPath) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(discFolder) ? 0 : 1)
            + (hasTracks ? 1 : 0);

        if (sourceKinds == 0)
        {
            throw new CommandLineException(
                "Missing source for 'duplicate': provide --source <id>, --iso <file>, --tracks <files|.cue>, or --disc <staged-folder>.");
        }
        if (sourceKinds > 1)
        {
            throw new CommandLineException(
                "Choose only one source for 'duplicate': --source, --iso, --tracks/--tracks-file, or --disc.");
        }

        var (nativeEngineTemplate, testWrite) = ResolveNativeDataEngine(cmd, burnEngine, hasDirectDefault: true);
        bool windowsWriter = cmd.HasFlag("windows-writer");
        if (nativeEngineTemplate != null && windowsWriter)
        {
            throw new CommandLineException("--native and --windows-writer cannot be used together.");
        }

        bool useDirect = nativeEngineTemplate != null || cmd.HasFlag("native");
        bool strictVerify = cmd.HasFlag("strict-verify");
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || useDirect));
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");
        bool disableBufe = cmd.HasFlag("no-burnfree");
        DvdBookType? requestedBookType = ParseOptionalBookType(cmd);
        bool requireBookType = cmd.HasFlag("require-book-type");
        var postAction = ParsePostBurnAction(cmd);
        string? telemetryOut = ParseTelemetryOutPath(cmd);
        string? logDir = cmd.GetOption("log-dir");
        bool testFirst = cmd.HasFlag("test-first");
        string? speedStr = cmd.GetOption("speed");

        if (!string.IsNullOrWhiteSpace(isoPath))
        {
            if (!File.Exists(isoPath))
            {
                throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
            }
            var firstStatus = recorders[0].QueryMediaStatus();
            useDirect |= !windowsWriter && recorders.Any(r => DirectWritingDefault.Prefers(r.QueryMediaStatus()));
            var validationNative = useDirect ? (nativeEngineTemplate ?? new NativeDataBurnEngine(burnEngine, logDir)) : null;
            bool streamingWrite = ParseStreamingWrite(cmd, validationNative);
            int? layerBreak = ParseLayerBreak(cmd, validationNative, firstStatus);
            var speedDesc = ParseSpeed(speedStr, recorders[0], firstStatus.MediaType);

            var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(burnEngine, useDirect, logDir, testFirst);
            var result = await coordinator.BurnImageAsync(recorders, isoPath, new DataBurnOptions
            {
                Speed = speedDesc,
                Simulate = testWrite,
                VerifyAfterBurn = verify || useDirect,
                StrictVerify = strictVerify,
                CloseDisc = closeDisc,
                OverwriteExistingData = overwrite,
                LayerBreakSectors = layerBreak,
                DisableBufferUnderrunFree = disableBufe,
                StreamingWrite = streamingWrite,
                RequestedBookType = requestedBookType,
                RequireBookType = requireBookType
            }).ConfigureAwait(false);
            return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: Path.GetFullPath(isoPath), sourceDriveId: null);
        }

        if (!string.IsNullOrWhiteSpace(discFolder))
        {
            if (!Directory.Exists(discFolder))
            {
                throw new DirectoryNotFoundException($"Staged disc folder does not exist: {discFolder}");
            }
            var firstStatus = recorders[0].QueryMediaStatus();
            useDirect |= !windowsWriter && recorders.Any(r => DirectWritingDefault.Prefers(r.QueryMediaStatus()));
            var validationNative = useDirect ? (nativeEngineTemplate ?? new NativeDataBurnEngine(burnEngine, logDir)) : null;
            bool streamingWrite = ParseStreamingWrite(cmd, validationNative);
            var speedDesc = ParseSpeed(speedStr, recorders[0], firstStatus.MediaType);

            var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(burnEngine, useDirect, logDir, testFirst);
            var result = await coordinator.BurnArchiveAsync(recorders, discFolder, new DataBurnOptions
            {
                Speed = speedDesc,
                Simulate = testWrite,
                VerifyAfterBurn = verify || useDirect,
                StrictVerify = strictVerify,
                CloseDisc = closeDisc,
                OverwriteExistingData = overwrite,
                DisableBufferUnderrunFree = disableBufe,
                StreamingWrite = streamingWrite,
                RequestedBookType = requestedBookType,
                RequireBookType = requireBookType
            }).ConfigureAwait(false);
            return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: null, sourceDriveId: null);
        }

        if (hasTracks)
        {
            using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
            var (compilation, loadNote) = BuildAudioCompilation(cmd, mfDecoder, stderr);
            if (loadNote != null && !cmd.IsJson)
            {
                stderr.WriteLine(loadNote);
            }
            var firstStatus = recorders[0].QueryMediaStatus();
            var speedDesc = ParseSpeed(speedStr, recorders[0], firstStatus.MediaType, isAudio: true);
            bool dao = cmd.HasFlag("dao");
            var overburn = ParseOverburnPolicy(cmd, useDirect, discAtOnce: dao || compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless);

            var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(burnEngine, useDirect, logDir, testFirst);
            var result = await coordinator.BurnAudioAsync(recorders, compilation, new AudioBurnOptions
            {
                Speed = speedDesc,
                UseDiscAtOnce = dao || compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless,
                WriteCdText = compilation.IncludeCdText,
                PerformNormalization = compilation.NormalizeAllTracks,
                PreGapFrames = compilation.DefaultPreGapFrames,
                MediaCatalogNumber = compilation.UpcEan,
                Simulate = testWrite,
                KeepIndexMarks = useDirect,
                AllowDamagedAudio = cmd.HasFlag("allow-damaged"),
                WriteOffsetSamples = ParseWriteOffset(cmd.GetOption("write-offset"), useDirect),
                DisableBufferUnderrunFree = disableBufe,
                Overburn = overburn
            }).ConfigureAwait(false);
            return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: null, sourceDriveId: null);
        }

        // --source <id>: read source disc once into temp staging, then burn simultaneously to all target drives.
        var source = FindRecorder(deviceService, sourceId!);
        if (recorders.Any(r => string.Equals(r.UniqueId, source.UniqueId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CommandLineException(
                $"Source drive '{source.UniqueId}' cannot also be in --drives during simultaneous duplication.");
        }

        var sourceStatus = source.QueryMediaStatus();
        if (!sourceStatus.MediaPresent || sourceStatus.IsBlank || sourceStatus.UsedSectors <= 0)
        {
            throw new CommandLineException($"There is no recorded disc to duplicate in {source.VolumePath ?? source.UniqueId}.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Dup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            bool isAudio = DiscCopyService.IsSourceAudioCd(source);
            var coordinator = MultiDriveBurnEngineFactory.CreateCoordinator(burnEngine, useDirect, logDir, testFirst);

            if (isAudio)
            {
                string audioDir = Path.Combine(tempDir, "audio");
                Directory.CreateDirectory(audioDir);
                var ripper = new AudioCdRipperService();
                var audioRip = await ripper.RipAudioCdAsync(
                    source,
                    burnEngine,
                    audioDir,
                    new AudioRipOptions { GenerateCueSheet = true, SecureMode = true }).ConfigureAwait(false);
                if (!audioRip.Success || string.IsNullOrWhiteSpace(audioRip.CueSheetPath) || !File.Exists(audioRip.CueSheetPath))
                {
                    throw new InvalidOperationException(audioRip.ErrorMessage ?? "Failed to read source Audio CD.");
                }

                int suspect = audioRip.TrackInfos.Sum(t => t.SuspectSectors + t.UnreadableSectors);
                if (suspect > 0 && !cmd.HasFlag("allow-suspect"))
                {
                    throw new InvalidOperationException(
                        $"{suspect:N0} audio sector(s) of the source still read with errors after re-reading. Pass --allow-suspect to duplicate anyway.");
                }

                using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
                var compilation = new AudioCdCompilation
                {
                    RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                    IncludeCdText = true
                };
                compilation.ImportCueSheet(audioRip.CueSheetPath, mfDecoder);

                var speedDesc = ParseSpeed(speedStr, recorders[0], sourceStatus.MediaType, isAudio: true);
                var result = await coordinator.BurnAudioAsync(recorders, compilation, new AudioBurnOptions
                {
                    UseDiscAtOnce = true,
                    WriteCdText = true,
                    KeepIndexMarks = useDirect,
                    Simulate = testWrite,
                    DisableBufferUnderrunFree = disableBufe,
                    Speed = speedDesc
                }).ConfigureAwait(false);
                return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: null, sourceDriveId: source.UniqueId);
            }
            else
            {
                bool rawMode = cmd.HasFlag("raw");
                string stagedImage = Path.Combine(tempDir, rawMode ? "source.bin" : "source.iso");
                var ripper = new DiscRipperService();
                RipOperationResult ripResult = rawMode
                    ? await ripper.RipToBinCueAsync(source, burnEngine, stagedImage, Path.ChangeExtension(stagedImage, ".cue")).ConfigureAwait(false)
                    : await ripper.RipToIsoAsync(source, burnEngine, stagedImage).ConfigureAwait(false);
                if (!ripResult.Success)
                {
                    throw new InvalidOperationException(ripResult.ErrorMessage ?? "Failed to read source disc.");
                }

                var speedDesc = ParseSpeed(speedStr, recorders[0], sourceStatus.MediaType);
                var result = await coordinator.BurnImageAsync(recorders, stagedImage, new DataBurnOptions
                {
                    VolumeLabel = "DISC_COPY",
                    Speed = speedDesc,
                    Simulate = testWrite,
                    VerifyAfterBurn = !rawMode && (verify || useDirect),
                    StrictVerify = strictVerify,
                    CloseDisc = closeDisc,
                    OverwriteExistingData = overwrite,
                    DisableBufferUnderrunFree = disableBufe,
                    RequestedBookType = requestedBookType,
                    RequireBookType = requireBookType
                }).ConfigureAwait(false);
                return EmitMultiDriveResult(cmd, stdout, stderr, result, postAction, telemetryOut, isoPath: null, sourceDriveId: source.UniqueId);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static int EmitMultiDriveResult(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        MultiDriveBurnResult result,
        PostBurnAction postAction,
        string? telemetryOut,
        string? isoPath,
        string? sourceDriveId)
    {
        var telemetryPathsByDrive = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in result.Results)
        {
            string? driveTelemetryPath = BuildPerDriveTelemetryPath(telemetryOut, target.DriveId, result.TotalDrives);
            telemetryPathsByDrive[target.DriveId] = SaveTelemetryIfRequested(driveTelemetryPath, target.Telemetry);
            if (postAction != PostBurnAction.ShutDown)
            {
                ExecutePostBurnActionIfSucceeded(postAction, target.Success, target.Recorder);
            }
        }
        if (postAction == PostBurnAction.ShutDown && result.AllSucceeded && result.Results.Count > 0)
        {
            ExecutePostBurnActionIfSucceeded(postAction, true, result.Results[0].Recorder);
        }

        if (cmd.IsJson)
        {
            var perDriveJson = result.Results.Select(r =>
            {
                var nativeData = r.EngineUsed is MultiDriveBurnEngineFactory.PerDriveNativeCompositeEngine p ? p.DataEngine : r.EngineUsed as NativeDataBurnEngine;
                var nativeAudio = r.EngineUsed is MultiDriveBurnEngineFactory.PerDriveNativeCompositeEngine p2 ? p2.AudioEngine : r.EngineUsed as NativeAudioBurnEngine;
                string? commandLog = nativeData?.LastLogPath ?? nativeAudio?.LastLogPath;
                bool? verified = r.DataBurnResult != null
                    ? r.DataBurnResult.Verified
                    : nativeAudio?.LastResult?.Verification?.Passed;
                string? verifyNote = r.DataBurnResult?.VerificationNote ?? nativeAudio?.LastResult?.Verification?.Message;
                IReadOnlyList<string> driveTelemetry = telemetryPathsByDrive.GetValueOrDefault(r.DriveId) ?? Array.Empty<string>();

                return new
                {
                    driveId = r.DriveId,
                    volumePath = r.Recorder.VolumePath,
                    success = r.Success,
                    status = r.Status.ToString(),
                    sectorsWritten = r.BurnResult?.SectorsWritten ?? 0,
                    totalTimeSeconds = r.Duration.TotalSeconds,
                    averageSpeedMultiplier = r.BurnResult?.AverageSpeedMultiplier ?? 0.0,
                    verified,
                    verificationNote = verifyNote,
                    commandLog,
                    telemetryOut = FormatTelemetryJsonValue(driveTelemetry),
                    telemetryPoints = r.Telemetry.Count,
                    error = r.ErrorMessage
                };
            }).ToList();

            WriteJsonResult(stdout, new
            {
                success = result.AllSucceeded,
                allSucceeded = result.AllSucceeded,
                anySucceeded = result.AnySucceeded,
                multiDrive = true,
                sourceDriveId,
                isoPath,
                drivesRequested = result.TotalDrives,
                succeededCount = result.SucceededCount,
                failedCount = result.FailedCount,
                cancelledCount = result.CancelledCount,
                totalTimeSeconds = result.TotalWallTime.TotalSeconds,
                summary = result.SummaryMessage,
                results = perDriveJson
            });
            return result.AllSucceeded ? 0 : 1;
        }

        stdout.WriteLine($"Multi-drive burn finished: {result.SummaryMessage}");
        foreach (var r in result.Results)
        {
            string vol = string.IsNullOrWhiteSpace(r.Recorder.VolumePath) ? r.DriveId : $"{r.DriveId} ({r.Recorder.VolumePath})";
            if (r.Success)
            {
                stdout.WriteLine($"  [OK]   {vol}: {r.BurnResult?.SectorsWritten ?? 0:N0} sectors in {r.Duration.TotalSeconds:F1} s");
            }
            else
            {
                stderr.WriteLine($"  [FAIL] {vol}: {r.ErrorMessage}");
            }
        }
        return result.AllSucceeded ? 0 : 1;
    }

    private static string? BuildPerDriveTelemetryPath(string? telemetryOut, string driveId, int driveCount)
    {
        if (string.IsNullOrWhiteSpace(telemetryOut))
        {
            return null;
        }
        if (driveCount <= 1)
        {
            return telemetryOut;
        }

        string safeDrive = new string(driveId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        string full = Path.GetFullPath(telemetryOut);
        string dir = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);
        return Path.Combine(dir, $"{stem}-{safeDrive}{ext}");
    }
}
