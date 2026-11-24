using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Core.Verification;

namespace CdBurner.Core.Burning;

/// <summary>
/// Execution state of a single target drive inside a <see cref="MultiDriveBurnCoordinator"/> run.
/// </summary>
public enum MultiDriveBurnStatus
{
    Pending,
    Running,
    Verifying,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// Real-time progress update emitted for a single target drive during a simultaneous multi-drive burn.
/// </summary>
public sealed record MultiDriveProgressUpdate(
    string DriveId,
    string DisplayName,
    MultiDriveBurnStatus Status,
    double ProgressPercent,
    string ActionDescription,
    BurnProgressSnapshot? Snapshot,
    string? ErrorMessage = null);

/// <summary>
/// Final per-drive outcome of a simultaneous multi-drive burn, including isolated telemetry and engine state.
/// </summary>
public sealed class MultiDriveTargetResult
{
    public required string DriveId { get; init; }
    public required IDiscRecorderBackend Recorder { get; init; }
    public MultiDriveBurnStatus Status { get; init; }
    public bool Success { get; init; }
    public BurnOperationResult? BurnResult { get; init; }
    public DataBurnResult? DataBurnResult { get; init; }
    public BurnTelemetrySeries Telemetry { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
    public IBurnEngineBackend? EngineUsed { get; init; }
    public bool ExclusiveAccessAcquired { get; init; }
}

/// <summary>
/// Aggregate outcome of a simultaneous multi-drive burn across all target drives.
/// </summary>
public sealed class MultiDriveBurnResult
{
    public required IReadOnlyList<MultiDriveTargetResult> Results { get; init; }
    public TimeSpan TotalWallTime { get; init; }
    public int TotalDrives => Results.Count;
    public int SucceededCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => r.Status == MultiDriveBurnStatus.Failed);
    public int CancelledCount => Results.Count(r => r.Status == MultiDriveBurnStatus.Cancelled);
    public bool AllSucceeded => Results.Count > 0 && Results.All(r => r.Success);
    public bool AnySucceeded => Results.Any(r => r.Success);
    public string SummaryMessage { get; init; } = string.Empty;
}

/// <summary>
/// Coordinates burning the same ISO/UDF/legacy image, Audio CD project, or Archive volume to two or more
/// optical drives simultaneously. Each drive executes on its own task with its own engine instance,
/// exclusive-access handle, read-only source stream/reader, telemetry series, post-burn verification,
/// and isolated failure/cancellation handling so a failure on one drive never aborts another.
/// </summary>
public sealed class MultiDriveBurnCoordinator
{
    private readonly Func<IDiscRecorderBackend, IBurnEngineBackend> _engineFactory;
    private readonly Func<IAudioDecoder?>? _audioDecoderFactory;
    private readonly Func<BookTypeService> _bookTypeServiceFactory;
    private readonly Func<IDiscRecorderBackend, IBurnEngineBackend?>? _recoveryDataEngineFactory;
    private readonly Func<IDiscRecorderBackend, IDisposable?>? _exclusiveAccessFactory;

    public MultiDriveBurnCoordinator(
        Func<IDiscRecorderBackend, IBurnEngineBackend> engineFactory,
        Func<IAudioDecoder?>? audioDecoderFactory = null,
        Func<BookTypeService>? bookTypeServiceFactory = null,
        Func<IDiscRecorderBackend, IBurnEngineBackend?>? recoveryDataEngineFactory = null,
        Func<IDiscRecorderBackend, IDisposable?>? exclusiveAccessFactory = null)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _audioDecoderFactory = audioDecoderFactory;
        _bookTypeServiceFactory = bookTypeServiceFactory ?? (() => new BookTypeService());
        _recoveryDataEngineFactory = recoveryDataEngineFactory;
        _exclusiveAccessFactory = exclusiveAccessFactory;
    }

    public MultiDriveBurnCoordinator(
        IBurnEngineBackend burnEngine,
        IAudioDecoder? audioDecoder = null,
        Func<IDiscRecorderBackend, IBurnEngineBackend?>? recoveryDataEngineFactory = null,
        Func<IDiscRecorderBackend, IDisposable?>? exclusiveAccessFactory = null)
        : this(
            _ => burnEngine ?? throw new ArgumentNullException(nameof(burnEngine)),
            audioDecoder != null ? () => audioDecoder : null,
            null,
            recoveryDataEngineFactory,
            exclusiveAccessFactory)
    {
    }

    /// <summary>
    /// Burns an ISO, UDF, or supported legacy disc image file simultaneously to all target <paramref name="recorders"/>.
    /// Each drive opens its own read-only file stream and verifies independently when requested.
    /// </summary>
    public Task<MultiDriveBurnResult> BurnImageAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        string imageFilePath,
        DataBurnOptions? burnOptions = null,
        IProgress<MultiDriveProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecorders(recorders);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFilePath);
        if (!File.Exists(imageFilePath))
        {
            throw new FileNotFoundException($"Image file not found: '{imageFilePath}'.", imageFilePath);
        }

        string title = $"burn-iso ({Path.GetFileName(imageFilePath)})";
        bool isLegacy = LegacyDiscImageConverter.IsLegacyImageFile(imageFilePath);
        var templateOptions = CloneDataBurnOptions(burnOptions ?? new DataBurnOptions());

        return RunAcrossDrivesAsync(
            recorders,
            title,
            progress,
            perDriveCancellationTokens,
            cancellationToken,
            async (recorder, driveEngine, telemetry, driveCt) =>
            {
                var driveBookType = _bookTypeServiceFactory();
                var burnService = new DataBurnService(driveEngine, driveBookType);
                var driveOptions = CloneDataBurnOptions(templateOptions);

                DataBurnResult dataResult;
                if (isLegacy)
                {
                    await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(imageFilePath);
                    dataResult = await burnService.BurnStreamAsync(recorder, legacyStream, driveOptions, telemetry, driveCt).ConfigureAwait(false);
                }
                else
                {
                    dataResult = await burnService.BurnIsoFileAsync(recorder, imageFilePath, driveOptions, telemetry, driveCt).ConfigureAwait(false);
                }

                return (ToBurnOperationResult(dataResult), dataResult);
            });
    }

    /// <summary>
    /// Burns a caller-supplied data stream simultaneously to all target <paramref name="recorders"/>,
    /// invoking <paramref name="streamFactory"/> once per drive so each drive owns an independent stream.
    /// </summary>
    public Task<MultiDriveBurnResult> BurnDataStreamFactoryAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        Func<IDiscRecorderBackend, Stream> streamFactory,
        DataBurnOptions? burnOptions = null,
        string? jobTitle = null,
        IProgress<MultiDriveProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecorders(recorders);
        ArgumentNullException.ThrowIfNull(streamFactory);

        string title = string.IsNullOrWhiteSpace(jobTitle) ? "burn-stream" : jobTitle;
        var templateOptions = CloneDataBurnOptions(burnOptions ?? new DataBurnOptions());

        return RunAcrossDrivesAsync(
            recorders,
            title,
            progress,
            perDriveCancellationTokens,
            cancellationToken,
            async (recorder, driveEngine, telemetry, driveCt) =>
            {
                var burnService = new DataBurnService(driveEngine, _bookTypeServiceFactory());
                var driveOptions = CloneDataBurnOptions(templateOptions);
                await using var stream = streamFactory(recorder)
                    ?? throw new InvalidOperationException($"Stream factory returned null for drive '{recorder.UniqueId}'.");

                var dataResult = await burnService.BurnStreamAsync(recorder, stream, driveOptions, telemetry, driveCt).ConfigureAwait(false);
                return (ToBurnOperationResult(dataResult), dataResult);
            });
    }

    /// <summary>
    /// Burns an Audio CD compilation simultaneously to all target <paramref name="recorders"/>.
    /// Each drive receives an isolated compilation clone, decoder, source reader, and verification pass.
    /// </summary>
    public Task<MultiDriveBurnResult> BurnAudioAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<MultiDriveProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecorders(recorders);
        ArgumentNullException.ThrowIfNull(compilation);
        if (compilation.TrackCount == 0)
        {
            throw new ArgumentException("Audio compilation contains no tracks.", nameof(compilation));
        }

        string title = $"burn-audio ({compilation.AlbumTitle})";
        var templateOptions = CloneAudioBurnOptions(options ?? new AudioBurnOptions());

        return RunAcrossDrivesAsync(
            recorders,
            title,
            progress,
            perDriveCancellationTokens,
            cancellationToken,
            async (recorder, driveEngine, telemetry, driveCt) =>
            {
                var driveDecoder = _audioDecoderFactory?.Invoke();
                try
                {
                    var driveCompilation = CloneCompilationForDrive(compilation, driveDecoder);
                    var driveRecoveryEngine = _recoveryDataEngineFactory?.Invoke(recorder);
                    var driveOptions = CloneAudioBurnOptions(templateOptions);
                    var burnService = new AudioCdBurnService(driveEngine, driveDecoder, driveRecoveryEngine);

                    var burnResult = await burnService.BurnAsync(recorder, driveCompilation, driveOptions, telemetry, driveCt).ConfigureAwait(false);
                    return (burnResult, (DataBurnResult?)null);
                }
                finally
                {
                    if (driveDecoder is IDisposable disposableDecoder)
                    {
                        try { disposableDecoder.Dispose(); } catch { }
                    }
                }
            });
    }

    /// <summary>
    /// Burns a staged Archive disc directory simultaneously to all target <paramref name="recorders"/>.
    /// Each drive builds/reads its own ISO+Joliet+UDF volume stream and verifies independently.
    /// </summary>
    public Task<MultiDriveBurnResult> BurnArchiveAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        string stagedDiscFolder,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<MultiDriveProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecorders(recorders);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDiscFolder);
        if (!Directory.Exists(stagedDiscFolder))
        {
            throw new DirectoryNotFoundException($"Staged archive disc folder not found: '{stagedDiscFolder}'.");
        }

        string? derivedLabel = ResolveArchiveVolumeLabel(stagedDiscFolder);
        string effectiveLabel = burnOptions?.VolumeLabel ?? buildOptions?.VolumeLabel ?? derivedLabel ?? "ARCHIVE_DISC";
        var effectiveBurnOptions = CloneDataBurnOptions(burnOptions ?? new DataBurnOptions(), effectiveLabel);
        var effectiveBuildOptions = buildOptions ?? new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: null);

        return RunAcrossDrivesAsync(
            recorders,
            $"archive-burn ({effectiveLabel})",
            progress,
            perDriveCancellationTokens,
            cancellationToken,
            async (recorder, driveEngine, telemetry, driveCt) =>
            {
                var root = CompilationFolderImporter.ImportAsRoot(stagedDiscFolder, strict: true);
                var burnService = new DataBurnService(driveEngine, _bookTypeServiceFactory());
                var driveBurnOptions = CloneDataBurnOptions(effectiveBurnOptions, effectiveLabel);
                var dataResult = await burnService.BurnCompilationAsync(
                    recorder, root, driveBurnOptions, effectiveBuildOptions, telemetry, driveCt).ConfigureAwait(false);
                return (ToBurnOperationResult(dataResult), dataResult);
            });
    }

    /// <summary>
    /// Burns a <see cref="CompilationDirectory"/> volume (e.g. an Archive or Data compilation) simultaneously
    /// to all target <paramref name="recorders"/>.
    /// </summary>
    public Task<MultiDriveBurnResult> BurnCompilationAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        CompilationDirectory rootDirectory,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<MultiDriveProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecorders(recorders);
        ArgumentNullException.ThrowIfNull(rootDirectory);

        string effectiveLabel = burnOptions?.VolumeLabel ?? buildOptions?.VolumeLabel ?? "IGNISDISC";
        var effectiveBurnOptions = CloneDataBurnOptions(burnOptions ?? new DataBurnOptions(), effectiveLabel);
        var effectiveBuildOptions = buildOptions ?? new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: null);

        return RunAcrossDrivesAsync(
            recorders,
            $"burn-compilation ({effectiveLabel})",
            progress,
            perDriveCancellationTokens,
            cancellationToken,
            async (recorder, driveEngine, telemetry, driveCt) =>
            {
                var burnService = new DataBurnService(driveEngine, _bookTypeServiceFactory());
                var driveBurnOptions = CloneDataBurnOptions(effectiveBurnOptions, effectiveLabel);
                var dataResult = await burnService.BurnCompilationAsync(
                    recorder, rootDirectory, driveBurnOptions, effectiveBuildOptions, telemetry, driveCt).ConfigureAwait(false);
                return (ToBurnOperationResult(dataResult), dataResult);
            });
    }

    private async Task<MultiDriveBurnResult> RunAcrossDrivesAsync(
        IReadOnlyList<IDiscRecorderBackend> recorders,
        string jobTitle,
        IProgress<MultiDriveProgressUpdate>? progress,
        IReadOnlyDictionary<string, CancellationToken>? perDriveCancellationTokens,
        CancellationToken masterCancellationToken,
        Func<IDiscRecorderBackend, IBurnEngineBackend, BurnTelemetrySeries, CancellationToken, Task<(BurnOperationResult BurnResult, DataBurnResult? DataResult)>> work)
    {
        var wallSw = Stopwatch.StartNew();

        var tasks = recorders.Select(recorder => Task.Run(async () =>
        {
            string driveId = recorder.UniqueId;
            string displayName = FormatDriveDisplay(recorder);
            var driveSw = Stopwatch.StartNew();

            var sink = new DriveProgressSink(driveId, displayName, progress);
            var telemetry = new BurnTelemetrySeries(BurnTelemetrySeries.DefaultMaxPoints, sink);
            DiscMediaType mediaType = DiscMediaType.Unknown;
            try
            {
                mediaType = recorder.QueryMediaStatus().MediaType;
            }
            catch
            {
            }
            telemetry.Reset(jobTitle, $"{recorder.VendorId} {recorder.ProductId} ({driveId})".Trim(), mediaType.GetDisplayName());

            CancellationToken driveSpecificToken = perDriveCancellationTokens != null &&
                perDriveCancellationTokens.TryGetValue(driveId, out var specific)
                ? specific
                : CancellationToken.None;

            using var linkedCts = driveSpecificToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(masterCancellationToken, driveSpecificToken)
                : CancellationTokenSource.CreateLinkedTokenSource(masterCancellationToken);
            var driveCt = linkedCts.Token;

            IBurnEngineBackend? driveEngine = null;
            IDisposable? exclusiveHandle = null;
            bool acquiredExclusive = false;

            try
            {
                driveCt.ThrowIfCancellationRequested();

                progress?.Report(new MultiDriveProgressUpdate(
                    driveId, displayName, MultiDriveBurnStatus.Running, 0.0, "Starting", null));

                exclusiveHandle = _exclusiveAccessFactory?.Invoke(recorder);
                acquiredExclusive = exclusiveHandle != null;

                driveEngine = _engineFactory(recorder)
                    ?? throw new InvalidOperationException($"Engine factory returned null for drive '{driveId}'.");

                var (burnResult, dataResult) = await work(recorder, driveEngine, telemetry, driveCt).ConfigureAwait(false);
                driveSw.Stop();

                bool isCancelled = burnResult.Status == BurnStatus.Cancelled || driveCt.IsCancellationRequested;
                var finalStatus = burnResult.Success
                    ? MultiDriveBurnStatus.Succeeded
                    : isCancelled
                        ? MultiDriveBurnStatus.Cancelled
                        : MultiDriveBurnStatus.Failed;

                string finalAction = burnResult.Success
                    ? "Completed"
                    : isCancelled
                        ? "Cancelled"
                        : "Failed";

                progress?.Report(new MultiDriveProgressUpdate(
                    driveId,
                    displayName,
                    finalStatus,
                    burnResult.Success ? 100.0 : sink.LastProgressPercent,
                    finalAction,
                    null,
                    burnResult.Success ? null : burnResult.ErrorMessage));

                return new MultiDriveTargetResult
                {
                    DriveId = driveId,
                    Recorder = recorder,
                    Status = finalStatus,
                    Success = burnResult.Success,
                    BurnResult = burnResult,
                    DataBurnResult = dataResult,
                    Telemetry = telemetry,
                    Duration = driveSw.Elapsed,
                    ErrorMessage = burnResult.Success ? null : burnResult.ErrorMessage,
                    Exception = burnResult.Exception,
                    EngineUsed = driveEngine,
                    ExclusiveAccessAcquired = acquiredExclusive
                };
            }
            catch (OperationCanceledException oce)
            {
                driveSw.Stop();
                string msg = string.IsNullOrWhiteSpace(oce.Message) ? "Burn cancelled." : oce.Message;
                progress?.Report(new MultiDriveProgressUpdate(
                    driveId, displayName, MultiDriveBurnStatus.Cancelled, sink.LastProgressPercent, "Cancelled", null, msg));

                return new MultiDriveTargetResult
                {
                    DriveId = driveId,
                    Recorder = recorder,
                    Status = MultiDriveBurnStatus.Cancelled,
                    Success = false,
                    BurnResult = BurnOperationResult.CancelledResult(msg),
                    Telemetry = telemetry,
                    Duration = driveSw.Elapsed,
                    ErrorMessage = msg,
                    Exception = oce,
                    EngineUsed = driveEngine,
                    ExclusiveAccessAcquired = acquiredExclusive
                };
            }
            catch (Exception ex)
            {
                driveSw.Stop();
                progress?.Report(new MultiDriveProgressUpdate(
                    driveId, displayName, MultiDriveBurnStatus.Failed, sink.LastProgressPercent, "Failed", null, ex.Message));

                return new MultiDriveTargetResult
                {
                    DriveId = driveId,
                    Recorder = recorder,
                    Status = MultiDriveBurnStatus.Failed,
                    Success = false,
                    BurnResult = BurnOperationResult.Failed(ex.Message, ex),
                    Telemetry = telemetry,
                    Duration = driveSw.Elapsed,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    EngineUsed = driveEngine,
                    ExclusiveAccessAcquired = acquiredExclusive
                };
            }
            finally
            {
                if (exclusiveHandle != null)
                {
                    try { exclusiveHandle.Dispose(); } catch { }
                }
            }
        }, CancellationToken.None)).ToList();

        MultiDriveTargetResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        wallSw.Stop();

        int succeeded = results.Count(r => r.Success);
        int failed = results.Count(r => r.Status == MultiDriveBurnStatus.Failed);
        int cancelled = results.Count(r => r.Status == MultiDriveBurnStatus.Cancelled);

        string summary = succeeded == results.Length
            ? $"All {results.Length} drive(s) burned successfully in {wallSw.Elapsed.TotalSeconds:F1} s."
            : $"{succeeded} of {results.Length} drive(s) succeeded ({failed} failed, {cancelled} cancelled).";

        return new MultiDriveBurnResult
        {
            Results = results,
            TotalWallTime = wallSw.Elapsed,
            SummaryMessage = summary
        };
    }

    private static void ValidateRecorders(IReadOnlyList<IDiscRecorderBackend> recorders)
    {
        ArgumentNullException.ThrowIfNull(recorders);
        if (recorders.Count == 0)
        {
            throw new ArgumentException("At least one target drive must be provided.", nameof(recorders));
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < recorders.Count; i++)
        {
            var r = recorders[i] ?? throw new ArgumentException($"Target recorder at index {i} is null.", nameof(recorders));
            if (!seenIds.Add(r.UniqueId))
            {
                throw new ArgumentException($"Duplicate target drive '{r.UniqueId}'. Each simultaneous target must be a distinct drive.", nameof(recorders));
            }
        }
    }

    private static string FormatDriveDisplay(IDiscRecorderBackend recorder)
    {
        string vol = string.IsNullOrWhiteSpace(recorder.VolumePath) ? recorder.UniqueId : recorder.VolumePath.TrimEnd('\\');
        return $"{vol} {recorder.VendorId} {recorder.ProductId}".Trim();
    }

    private static BurnOperationResult ToBurnOperationResult(DataBurnResult dataResult) =>
        dataResult.Success
            ? new BurnOperationResult
            {
                Success = true,
                Status = BurnStatus.Succeeded,
                SectorsWritten = dataResult.SectorsWritten,
                TotalTime = dataResult.TotalTime,
                AverageSpeedMultiplier = dataResult.AverageSpeedMultiplier,
                WritesAccepted = dataResult.WritesAccepted,
                SessionClosed = dataResult.SessionClosed,
                TocChecked = dataResult.TocChecked
            }
            : dataResult.Status == BurnStatus.Cancelled
                ? BurnOperationResult.CancelledResult(dataResult.ErrorMessage ?? "Operation was cancelled.")
                : BurnOperationResult.Failed(dataResult.ErrorMessage ?? "Burn failed.", dataResult.Exception);

    private static DataBurnOptions CloneDataBurnOptions(DataBurnOptions options, string? volumeLabelOverride = null) =>
        new()
        {
            Speed = options.Speed,
            Simulate = options.Simulate,
            EjectWhenDone = options.EjectWhenDone,
            VerifyAfterBurn = options.VerifyAfterBurn,
            StrictVerify = options.StrictVerify,
            CloseDisc = options.CloseDisc,
            VolumeLabel = volumeLabelOverride ?? options.VolumeLabel,
            OverwriteExistingData = options.OverwriteExistingData,
            LayerBreakSectors = options.LayerBreakSectors,
            Overburn = options.Overburn,
            DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
            CdRomXaMode2Form1 = options.CdRomXaMode2Form1,
            PictureZone = options.PictureZone,
            StreamingWrite = options.StreamingWrite,
            RequestedBookType = options.RequestedBookType,
            RequireBookType = options.RequireBookType
        };

    private static AudioBurnOptions CloneAudioBurnOptions(AudioBurnOptions options) =>
        new()
        {
            Speed = options.Speed,
            Simulate = options.Simulate,
            EjectWhenDone = options.EjectWhenDone,
            PerformNormalization = options.PerformNormalization,
            WriteCdText = options.WriteCdText,
            UseDiscAtOnce = options.UseDiscAtOnce,
            PreGapFrames = options.PreGapFrames,
            CdTextBytes = options.CdTextBytes is { Length: > 0 } b ? (byte[])b.Clone() : null,
            AllowDamagedAudio = options.AllowDamagedAudio,
            MediaCatalogNumber = options.MediaCatalogNumber,
            KeepIndexMarks = options.KeepIndexMarks,
            WriteOffsetSamples = options.WriteOffsetSamples,
            LeaveSessionOpen = options.LeaveSessionOpen,
            Overburn = options.Overburn,
            ConfirmOverburn = options.ConfirmOverburn,
            DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
            IncludeRecoverySession = options.IncludeRecoverySession,
            DataSessionFolderPath = options.DataSessionFolderPath,
            DataSessionVolumeLabel = options.DataSessionVolumeLabel,
            VerifyDataSession = options.VerifyDataSession,
            BlueBookLayout = options.BlueBookLayout,
            SessionTwoImage = options.SessionTwoImage
        };

    internal static AudioCdCompilation CloneCompilationForDrive(AudioCdCompilation source, IAudioDecoder? driveDecoder)
    {
        var clone = new AudioCdCompilation
        {
            AlbumTitle = source.AlbumTitle,
            AlbumPerformer = source.AlbumPerformer,
            Songwriter = source.Songwriter,
            UpcEan = source.UpcEan,
            RecordingMode = source.RecordingMode,
            NormalizeAllTracks = source.NormalizeAllTracks,
            UseRmsLoudnessLeveling = source.UseRmsLoudnessLeveling,
            LoudnessNormalization = source.LoudnessNormalization,
            CrossfadeSeconds = source.CrossfadeSeconds,
            DefaultPreGapFrames = source.DefaultPreGapFrames,
            IncludeCdText = source.IncludeCdText
        };

        var albumMap = new Dictionary<SingleFileAlbumSource, SingleFileAlbumSource>(ReferenceEqualityComparer.Instance);
        SingleFileAlbumSource CloneAlbum(SingleFileAlbumSource original)
        {
            if (!albumMap.TryGetValue(original, out var mapped))
            {
                mapped = new SingleFileAlbumSource(original.FilePath, driveDecoder);
                albumMap[original] = mapped;
            }
            return mapped;
        }

        foreach (var t in source.Tracks)
        {
            SingleFileAlbumSource? clonedAlbum = t.AlbumSource != null ? CloneAlbum(t.AlbumSource) : null;
            SingleFileAlbumSource? clonedHiddenAlbum = t.HiddenAudioAlbumSource != null ? CloneAlbum(t.HiddenAudioAlbumSource) : null;

            Func<Stream>? clonedFactory = t.CustomPcmStreamFactory;
            if (clonedAlbum != null && t.SourceStartFrame is int startFrame)
            {
                int? frameCount = t.SourceFrameCount;
                clonedFactory = clonedAlbum.CreateSliceFactory(startFrame, frameCount);
            }
            else if (t.DataTrack != null)
            {
                var dt = t.DataTrack;
                var clonedDt = new DataTrackImage(dt.FilePath, dt.OffsetBytes, dt.SectorCount, dt.SectorSize, dt.Mode);
                clonedFactory = () => clonedDt.OpenWriterData();
            }

            Func<Stream>? clonedHiddenFactory = t.HiddenAudioPcmStreamFactory;
            if (clonedHiddenAlbum != null && t.HiddenAudioFrames > 0)
            {
                clonedHiddenFactory = clonedHiddenAlbum.CreateSliceFactory(0, t.HiddenAudioFrames);
            }

            var clonedTrack = new AudioCdTrackItem(
                t.TrackNumber,
                t.SourceFilePath,
                t.Title,
                t.Performer,
                t.Isrc,
                t.PreGapFrames,
                t.NormalizeAudio,
                clonedFactory)
            {
                Songwriter = t.Songwriter,
                Composer = t.Composer,
                Arranger = t.Arranger,
                Message = t.Message,
                SourceStartFrame = t.SourceStartFrame,
                SourceFrameCount = t.SourceFrameCount,
                AlbumSource = clonedAlbum,
                HiddenAudioAlbumSource = clonedHiddenAlbum,
                Index00LeadFrames = t.Index00LeadFrames,
                HiddenAudioPcmStreamFactory = clonedHiddenFactory,
                HiddenAudioFrames = t.HiddenAudioFrames,
                HiddenAudioIsPreGapPicture = t.HiddenAudioIsPreGapPicture,
                IsPictureTrack = t.IsPictureTrack,
                PreEmphasis = t.PreEmphasis,
                CopyPermitted = t.CopyPermitted,
                DataTrack = t.DataTrack != null
                    ? new DataTrackImage(t.DataTrack.FilePath, t.DataTrack.OffsetBytes, t.DataTrack.SectorCount, t.DataTrack.SectorSize, t.DataTrack.Mode)
                    : null,
                RawFileType = t.RawFileType,
                DurationFrames = t.DurationFrames
            };

            clone.AddTrack(clonedTrack);
        }

        return clone;
    }

    private static string? ResolveArchiveVolumeLabel(string stagedDiscFolder)
    {
        try
        {
            string indexPath = Path.Combine(stagedDiscFolder, ArchiveManifest.IndexFileName);
            if (!File.Exists(indexPath))
            {
                return null;
            }

            var index = System.Text.Json.JsonSerializer.Deserialize<ArchiveIndex>(
                File.ReadAllText(indexPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return index != null && !string.IsNullOrWhiteSpace(index.SetId) && index.DiscNumber >= 1
                ? ArchiveManifest.VolumeLabel(index.SetId, index.DiscNumber)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class DriveProgressSink : IProgress<BurnProgressSnapshot>
    {
        private readonly string _driveId;
        private readonly string _displayName;
        private readonly IProgress<MultiDriveProgressUpdate>? _outer;
        private double _lastProgressPercent;

        public DriveProgressSink(string driveId, string displayName, IProgress<MultiDriveProgressUpdate>? outer)
        {
            _driveId = driveId;
            _displayName = displayName;
            _outer = outer;
        }

        public double LastProgressPercent => Volatile.Read(ref _lastProgressPercent);

        public void Report(BurnProgressSnapshot value)
        {
            if (value == null) return;

            double pct = value.TotalLba > 0
                ? Math.Clamp((double)value.CurrentLba / value.TotalLba * 100.0, 0.0, 100.0)
                : Volatile.Read(ref _lastProgressPercent);
            Volatile.Write(ref _lastProgressPercent, pct);

            string action = value.CurrentActionDescription ?? string.Empty;
            var status = action.Contains("Verif", StringComparison.OrdinalIgnoreCase) ||
                         action.Contains("Checking", StringComparison.OrdinalIgnoreCase)
                ? MultiDriveBurnStatus.Verifying
                : MultiDriveBurnStatus.Running;

            _outer?.Report(new MultiDriveProgressUpdate(_driveId, _displayName, status, pct, action, value));
        }
    }
}
