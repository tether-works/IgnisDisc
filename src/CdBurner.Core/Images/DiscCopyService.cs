using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Classification of the recorded disc in a source drive for 1-Click Disc Copy planning.
/// </summary>
public enum CopyDiscKind
{
    NoDisc,
    BlankDisc,
    DataDisc,
    AudioCd,
    MixedModeCd,
    EnhancedCd
}

/// <summary>
/// Plain-language summary of what 1-Click Disc Copy will read and write from a source drive.
/// </summary>
public sealed record DiscCopyPlanSummary(
    CopyDiscKind Kind,
    int AudioTrackCount,
    int DataTrackCount,
    int TotalSectors,
    bool WillCopyAudio,
    bool WillCopyData,
    string SummaryText);

/// <summary>
/// Options for 1-click disc-to-disc copying and multi-copy batch mastering.
/// </summary>
public sealed class DiscCopyOptions
{
    /// <summary>
    /// Number of destination copies to burn sequentially (1..99). Default is 1.
    /// </summary>
    public int CopyCount { get; set; } = 1;

    /// <summary>
    /// Data discs only: stage the source as raw 2,352-byte sectors (.bin + .cue) instead of an .iso. The copy is still
    /// burned from the extracted 2,048-byte user data, because IMAPIv2's data writer cannot write raw sectors.
    /// Audio CDs ignore this and are always copied track by track.
    /// </summary>
    public bool RawCloneMode { get; set; }

    /// <summary>
    /// When true, performs a bit-for-bit sector verification pass after each burned copy.
    /// </summary>
    public bool VerifyEachCopy { get; set; } = true;

    /// <summary>
    /// When true, retains the temporary staged disc image file on disk after copying completes.
    /// </summary>
    public bool KeepStagedImage { get; set; }

    /// <summary>
    /// Optional explicit file path for the staged disc image (.iso or .cue).
    /// If omitted, a temporary staging path is created automatically.
    /// </summary>
    public string? CustomStagingImagePath { get; set; }

    /// <summary>
    /// Optional write speed for burning target copies.
    /// </summary>
    public WriteSpeedDescriptor? WriteSpeed { get; set; }

    /// <summary>
    /// Optional callback invoked before each target copy (1-based copy index) to allow
    /// single-drive media swapping or inserting the next blank disc in a batch run.
    /// Return false to cancel the copy batch.
    /// </summary>
    public Func<int, CancellationToken, Task<bool>>? OnPrepareTargetDiscAsync { get; set; }

    /// <summary>
    /// The direct drive-command writer, when the user turned it on. A mixed-mode CD (track 1 data, then audio) is then
    /// copied whole: its data track is read and written ahead of the audio. Without it only the audio is copied.
    /// </summary>
    public IBurnEngineBackend? MixedModeWriter { get; set; }

    /// <summary>
    /// Optional direct data-session writer (<c>NativeDataBurnEngine</c>) used together with <see cref="MixedModeWriter"/>
    /// when copying an Enhanced CD (audio in session 1, data in session 2). Without it, only the audio session is copied.
    /// </summary>
    public IBurnEngineBackend? DataSessionWriter { get; set; }

    /// <summary>
    /// Copy an audio CD even when some sectors still read with errors after the careful re-reads (the copy then has
    /// clicks or wrong samples there). Off by default: the copy stops before the target disc is touched.
    /// </summary>
    public bool AllowSuspectAudio { get; set; }

    /// <summary>
    /// Asked, with a description of the problem, when source audio still reads with errors and
    /// <see cref="AllowSuspectAudio"/> is off; true copies the sectors as read. Nothing has been written when it is asked.
    /// Without it the copy stops.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmSuspectAudioAsync { get; set; }

    /// <summary>
    /// When true, allows replacing existing data on rewritable target media (such as DVD+RW or BD-RE).
    /// Off by default so a non-blank target disc is not overwritten without explicit opt-in.
    /// </summary>
    public bool AllowOverwrite { get; set; }
}

/// <summary>
/// Result of a disc-to-disc copy or multi-copy batch operation.
/// </summary>
public sealed record DiscCopyResult
{
    public bool Success { get; init; }
    public int CopiesRequested { get; init; }
    public int CopiesCompleted { get; init; }
    public int SectorsPerDisc { get; init; }
    public int TotalSectorsWritten { get; init; }
    public bool WasAudioCd { get; init; }
    public bool IsMixedMode { get; init; }

    /// <summary>The data track of a mixed-mode CD was copied too (not only the audio).</summary>
    public bool CopiedDataTrack { get; init; }
    public bool IsEnhancedCd { get; init; }

    /// <summary>The second-session data volume of an Enhanced CD was read and rewritten at the target disc's NWA.</summary>
    public bool CopiedDataSession { get; init; }
    public string? WarningMessage { get; init; }
    public string? StagedImagePath { get; init; }
    public TimeSpan TotalTime { get; init; }
    public string? ErrorMessage { get; init; }

    public static DiscCopyResult Failed(string errorMessage, int copiesRequested = 1, int copiesCompleted = 0, string? stagedPath = null) =>
        new()
        {
            Success = false,
            CopiesRequested = copiesRequested,
            CopiesCompleted = copiesCompleted,
            StagedImagePath = stagedPath,
            ErrorMessage = errorMessage
        };
}

/// <summary>
/// Orchestrates 1-click Disc-to-Disc copying (drive-to-drive or single-drive swap)
/// and multi-copy batch burning for Data CDs/DVDs and Red Book Audio CDs.
/// </summary>
public sealed class DiscCopyService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DiscRipperService _ripperService;
    private readonly DataBurnService _dataBurnService;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly AudioCdRipperService _audioRipperService;
    private readonly DiscVerificationService _verificationService;
    private readonly IAudioDecoder? _audioDecoder;

    public DiscCopyService(
        IBurnEngineBackend burnEngine,
        IAudioDecoder? audioDecoder = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _ripperService = new DiscRipperService();
        _dataBurnService = new DataBurnService(_burnEngine);
        _audioBurnService = new AudioCdBurnService(_burnEngine, audioDecoder);
        _audioRipperService = new AudioCdRipperService();
        _verificationService = new DiscVerificationService();
        _audioDecoder = audioDecoder;
    }

    /// <summary>
    /// Copies a source optical disc from <paramref name="sourceRecorder"/> to <paramref name="targetRecorder"/>
    /// for <see cref="DiscCopyOptions.CopyCount"/> copies.
    /// </summary>
    public async Task<DiscCopyResult> CopyDiscAsync(
        IDiscRecorderBackend sourceRecorder,
        IDiscRecorderBackend targetRecorder,
        DiscCopyOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRecorder);
        ArgumentNullException.ThrowIfNull(targetRecorder);

        options ??= new DiscCopyOptions();
        int copyCount = Math.Clamp(options.CopyCount, 1, 99);
        var sw = Stopwatch.StartNew();

        var sourceStatus = sourceRecorder.QueryMediaStatus();
        if (!sourceStatus.MediaPresent || sourceStatus.IsBlank || sourceStatus.UsedSectors <= 0)
        {
            return DiscCopyResult.Failed("Source drive does not contain a recorded disc to copy.", copyCount);
        }

        // Without a media-swap callback there is no way to get a blank disc in front of the laser: a single drive still
        // holds the source, and a second copy would land on the first. Refuse up front instead of failing mid-batch
        // (or, on rewritable media, silently overwriting the previous copy).
        if (options.OnPrepareTargetDiscAsync == null)
        {
            if (IsSameDrive(sourceRecorder, targetRecorder))
            {
                return DiscCopyResult.Failed(
                    "Single-drive copy needs a disc-swap prompt (OnPrepareTargetDiscAsync) so the source can be ejected and a blank disc inserted.",
                    copyCount);
            }

            if (copyCount > 1)
            {
                return DiscCopyResult.Failed(
                    "Multi-copy batches need a disc-swap prompt (OnPrepareTargetDiscAsync) to insert a new blank disc before each copy.",
                    copyCount);
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Copy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string? stagedPath = null;
        try
        {
            var sourceTracks = ReadSourceTracks(sourceRecorder);
            var (isAudioDisc, isMixedMode) = Classify(sourceTracks);

            // Windows' burning service (IMAPIv2) cannot write a data track and audio tracks in one pass. The direct
            // drive-command writer can, for a mixed-mode CD (track 1 data, then audio) or an Enhanced CD (session 1
            // audio, then session 2 data). Otherwise only the audio is copied, and the user is told so up front.
            bool dataFirst = isMixedMode && sourceTracks.Count > 1 && !sourceTracks[0].IsAudio && sourceTracks.Skip(1).All(t => t.IsAudio);
            bool enhancedCd = isMixedMode && sourceTracks.Count > 1 && sourceTracks[0].IsAudio && !sourceTracks[^1].IsAudio
                && sourceTracks.Take(sourceTracks.Count - 1).All(t => t.IsAudio);
            bool copyDataTrack = dataFirst && options.MixedModeWriter is { WritesMixedModeCds: true };
            bool copyDataSession = enhancedCd && options.MixedModeWriter is { WritesMixedModeCds: true } && options.DataSessionWriter != null;
            string? mixedModeWarning = !isMixedMode || copyDataTrack || copyDataSession ? null
                : enhancedCd
                    ? "Enhanced CD (audio, then data in session 2) detected: only the audio session was copied. Turn on direct drive commands to copy the data session as well."
                    : dataFirst
                        ? "Mixed-mode CD detected: only the audio tracks are copied. Turn on direct drive commands to copy the data track as well."
                        : "Multi-track mixed CD layout is not supported for data copying: only the audio tracks were copied.";

            if (mixedModeWarning != null)
            {
                progress?.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = mixedModeWarning,
                    BufferLevelPercent = 100.0
                });
            }

            // Audio CDs are always copied track by track: Raw Clone Mode applies to data discs only (IMAPIv2's data
            // writer cannot write raw 2,352-byte sectors).
            if (isAudioDisc)
            {
                string audioRipDir = !string.IsNullOrWhiteSpace(options.CustomStagingImagePath)
                    ? (Path.GetDirectoryName(options.CustomStagingImagePath) ?? tempDir)
                    : Path.Combine(tempDir, "audio_stage");
                Directory.CreateDirectory(audioRipDir);

                var audioRip = await _audioRipperService.RipAudioCdAsync(
                    sourceRecorder,
                    _burnEngine,
                    audioRipDir,
                    // Secure mode: C2 error pointers and re-reads, so a sector the drive reads wrongly is found, not copied.
                    new AudioRipOptions { GenerateCueSheet = true, SecureMode = true },
                    progress,
                    ct).ConfigureAwait(false);

                if (!audioRip.Success || string.IsNullOrWhiteSpace(audioRip.CueSheetPath) || !File.Exists(audioRip.CueSheetPath))
                {
                    return DiscCopyResult.Failed(audioRip.ErrorMessage ?? "Failed to extract source Audio CD.", copyCount);
                }

                stagedPath = audioRip.CueSheetPath;
                int suspect = audioRip.TrackInfos.Sum(t => t.SuspectSectors + t.UnreadableSectors);
                if (suspect > 0 && !options.AllowSuspectAudio)
                {
                    string where = string.Join(", ", audioRip.TrackInfos
                        .Where(t => t.SuspectSectors + t.UnreadableSectors > 0)
                        .Select(t => $"track {t.TrackNumber}: {t.SuspectSectors + t.UnreadableSectors:N0}"));
                    string problem = $"{suspect:N0} audio sector(s) of the source still read with errors after re-reading ({where}), "
                        + "so the copy would have clicks or wrong samples there.";
                    bool accepted = options.ConfirmSuspectAudioAsync != null
                        && await options.ConfirmSuspectAudioAsync(problem, ct).ConfigureAwait(false);
                    if (!accepted)
                    {
                        return DiscCopyResult.Failed(
                            $"{problem} Nothing was written to the disc. Clean the source disc or try another drive, or copy anyway to accept them.",
                            copyCount, 0, stagedPath);
                    }
                }
                string? readWarning = audioRip.SecureModeUsed ? null
                    : "The drive does not report read errors (C2 pointers), so a misread in the audio would not be noticed.";
                var compilation = new AudioCdCompilation
                {
                    RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                    IncludeCdText = true
                };
                var burnService = options.MixedModeWriter != null
                    ? new AudioCdBurnService(options.MixedModeWriter, _audioDecoder)
                    : _audioBurnService;
                if (copyDataTrack)
                {
                    // Track 1 runs to the next track's two-second pre-gap: the data, then its post-gap.
                    int dataSectors = sourceTracks[1].StartLba - sourceTracks[0].StartLba - OpticalConstants.CdLeadInFrames;
                    string dataPath = Path.Combine(audioRipDir, "data_track.iso");
                    string? readProblem = await ReadDataTrackAsync(sourceRecorder, sourceTracks[0].StartLba, dataSectors, dataPath, progress, ct).ConfigureAwait(false);
                    if (readProblem != null)
                    {
                        return DiscCopyResult.Failed($"The data track could not be read: {readProblem} Nothing was written to the disc.", copyCount);
                    }
                    // Track 1's CD-Text as the source disc has it, so the copy's CD-Text matches the source's.
                    var sourceText = audioRip.CdText?.Tracks?.FirstOrDefault(t => t.TrackNumber == sourceTracks[0].TrackNumber);
                    string dataPerformer = !string.IsNullOrWhiteSpace(sourceText?.Performer) ? sourceText.Performer
                        : !string.IsNullOrWhiteSpace(audioRip.CdText?.AlbumPerformer) ? audioRip.CdText.AlbumPerformer
                        : compilation.AlbumPerformer;
                    compilation.AddTrack(AudioCdCompilation.CreateDataTrack(
                        DataTrackImage.Open(dataPath, 0, dataSectors, DataTrackImage.UserDataSize), sourceText?.Title, dataPerformer));
                }
                compilation.ImportCueSheet(stagedPath, _audioBurnService.AudioDecoder);

                StagedEnhancedDataSession? stagedData = null;
                if (copyDataSession)
                {
                    var dataTrack = sourceTracks[^1];
                    string session2StageDir = Path.Combine(tempDir, "session2_stage");
                    var (session, dataReadError) = await StageEnhancedCdDataSessionAsync(
                        sourceRecorder,
                        dataTrack,
                        session2StageDir,
                        progress,
                        ct).ConfigureAwait(false);
                    if (dataReadError != null || session == null)
                    {
                        return DiscCopyResult.Failed(
                            $"The data session could not be read: {dataReadError ?? "unknown error"} Nothing was written to the disc.",
                            copyCount,
                            0,
                            stagedPath);
                    }
                    stagedData = session;
                }

                int compilationTotalFrames = EstimateAudioSessionFrames(compilation);
                int completed = 0;
                int totalSectors = 0;
                int sectorsPerDisc = audioRip.TotalSectorsRead;

                for (int copyIndex = 1; copyIndex <= copyCount; copyIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (options.OnPrepareTargetDiscAsync != null)
                    {
                        bool proceed = await options.OnPrepareTargetDiscAsync(copyIndex, ct).ConfigureAwait(false);
                        if (!proceed)
                        {
                            return DiscCopyResult.Failed("Disc copy cancelled while waiting for target blank media.", copyCount, completed, stagedPath);
                        }
                    }

                    if (copyDataSession && stagedData != null)
                    {
                        var targetStatus = targetRecorder.QueryMediaStatus();
                        int predictedTargetNwa = compilationTotalFrames + (int)RecoverySessionBudget.FixedSessionSectors;
                        int targetIsoSectors = IsoUdfImageBuilder.CalculateTotalSectors(
                            stagedData.Root,
                            stagedData.BuildOptions,
                            predictedTargetNwa) - predictedTargetNwa;
                        if (targetStatus.TotalSectors > 0 &&
                            (long)predictedTargetNwa + Math.Max(300, targetIsoSectors) + 2 > targetStatus.TotalSectors)
                        {
                            return DiscCopyResult.Failed(
                                "Target disc does not have enough capacity for both sessions of this Enhanced CD. Nothing was written to the disc.",
                                copyCount,
                                completed,
                                stagedPath);
                        }
                    }

                    var burnRes = await burnService.BurnAsync(
                        targetRecorder,
                        compilation,
                        new AudioBurnOptions
                        {
                            UseDiscAtOnce = true,
                            WriteCdText = true,
                            KeepIndexMarks = options.MixedModeWriter != null,
                            LeaveSessionOpen = copyDataSession,
                            EjectWhenDone = false,
                            Speed = options.WriteSpeed
                        },
                        progress,
                        ct).ConfigureAwait(false);

                    if (!burnRes.Success)
                    {
                        return DiscCopyResult.Failed(
                            $"Copy {copyIndex} of {copyCount} failed: {burnRes.ErrorMessage}",
                            copyCount,
                            completed,
                            stagedPath);
                    }

                    int discSectorsWritten = burnRes.SectorsWritten;
                    if (copyDataSession && stagedData != null)
                    {
                        var postSession1 = targetRecorder.QueryMediaStatus();
                        if (!postSession1.IsAppendable || postSession1.NextWritableLba <= 0)
                        {
                            return DiscCopyResult.Failed(
                                $"Copy {copyIndex} of {copyCount} failed: Session 1 audio finished, but the drive did not leave the disc open for session 2.",
                                copyCount,
                                completed,
                                stagedPath);
                        }

                        var dataRes = await new DataBurnService(options.DataSessionWriter!).BurnCompilationAsync(
                            targetRecorder,
                            stagedData.Root,
                            new DataBurnOptions
                            {
                                VolumeLabel = stagedData.BuildOptions.VolumeLabel,
                                Speed = options.WriteSpeed,
                                CloseDisc = true,
                                VerifyAfterBurn = options.VerifyEachCopy,
                                EjectWhenDone = false
                            },
                            stagedData.BuildOptions,
                            progress,
                            ct).ConfigureAwait(false);

                        if (!dataRes.Success)
                        {
                            return DiscCopyResult.Failed(
                                $"Copy {copyIndex} of {copyCount} failed in session 2: {dataRes.ErrorMessage}",
                                copyCount,
                                completed,
                                stagedPath);
                        }

                        discSectorsWritten += dataRes.SectorsWritten;
                    }

                    completed++;
                    totalSectors += discSectorsWritten;
                    sectorsPerDisc = discSectorsWritten;
                }

                sw.Stop();
                return new DiscCopyResult
                {
                    Success = true,
                    CopiesRequested = copyCount,
                    CopiesCompleted = completed,
                    SectorsPerDisc = sectorsPerDisc,
                    TotalSectorsWritten = totalSectors,
                    WasAudioCd = true,
                    IsMixedMode = isMixedMode,
                    CopiedDataTrack = copyDataTrack,
                    IsEnhancedCd = enhancedCd,
                    CopiedDataSession = copyDataSession,
                    WarningMessage = string.Join(" ", new[] { mixedModeWarning, readWarning, suspect > 0 ? $"{suspect:N0} audio sector(s) were copied as read, with errors." : null }.Where(w => w != null)) is { Length: > 0 } w ? w : null,
                    StagedImagePath = options.KeepStagedImage ? stagedPath : null,
                    TotalTime = sw.Elapsed
                };
            }
            else
            {
                // Data / Video CD or DVD copy
                bool rawMode = options.RawCloneMode;
                stagedPath = !string.IsNullOrWhiteSpace(options.CustomStagingImagePath)
                    ? options.CustomStagingImagePath
                    : Path.Combine(tempDir, rawMode ? "staged_disc.bin" : "staged_disc.iso");

                RipOperationResult ripResult;
                if (rawMode)
                {
                    string cuePath = Path.ChangeExtension(stagedPath, ".cue");
                    ripResult = await _ripperService.RipToBinCueAsync(
                        sourceRecorder,
                        _burnEngine,
                        stagedPath,
                        cuePath,
                        progress: progress,
                        ct: ct).ConfigureAwait(false);
                }
                else
                {
                    ripResult = await _ripperService.RipToIsoAsync(
                        sourceRecorder,
                        _burnEngine,
                        stagedPath,
                        progress: progress,
                        ct: ct).ConfigureAwait(false);
                }

                if (!ripResult.Success)
                {
                    return DiscCopyResult.Failed(ripResult.ErrorMessage ?? "Failed to read source disc.", copyCount);
                }

                int completed = 0;
                int totalSectors = 0;
                int sectorsPerDisc = ripResult.SectorsRead;

                for (int copyIndex = 1; copyIndex <= copyCount; copyIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (options.OnPrepareTargetDiscAsync != null)
                    {
                        bool proceed = await options.OnPrepareTargetDiscAsync(copyIndex, ct).ConfigureAwait(false);
                        if (!proceed)
                        {
                            return DiscCopyResult.Failed("Disc copy cancelled while waiting for target blank media.", copyCount, completed, stagedPath);
                        }
                    }

                    BurnOperationResult burnRes;
                    if (rawMode)
                    {
                        // The staged .bin holds raw 2,352-byte sectors (kept for the user when KeepStagedImage is on).
                        // The data writer takes 2,048-byte user data, so extract it on the fly; writing the raw bytes
                        // as data would produce an unreadable disc.
                        await using var fs = LegacyDiscImageConverter.OpenIsoUserDataStream(stagedPath);
                        var rawDataRes = await _dataBurnService.BurnStreamAsync(
                            targetRecorder,
                            fs,
                            new DataBurnOptions
                            {
                                CloseDisc = true,
                                Speed = options.WriteSpeed,
                                OverwriteExistingData = options.AllowOverwrite
                            },
                            progress,
                            ct).ConfigureAwait(false);

                        burnRes = rawDataRes.Success
                            ? BurnOperationResult.Succeeded(rawDataRes.SectorsWritten, rawDataRes.TotalTime, rawDataRes.AverageSpeedMultiplier)
                            : BurnOperationResult.Failed(rawDataRes.ErrorMessage ?? "Burn failed.");
                    }
                    else
                    {
                        var dataRes = await _dataBurnService.BurnIsoFileAsync(
                            targetRecorder,
                            stagedPath,
                            new DataBurnOptions
                            {
                                VolumeLabel = "DISC_COPY",
                                CloseDisc = true,
                                Speed = options.WriteSpeed,
                                OverwriteExistingData = options.AllowOverwrite
                            },
                            progress,
                            ct).ConfigureAwait(false);

                        burnRes = dataRes.Success
                            ? BurnOperationResult.Succeeded(dataRes.SectorsWritten, dataRes.TotalTime, dataRes.AverageSpeedMultiplier)
                            : BurnOperationResult.Failed(dataRes.ErrorMessage ?? "Burn failed.");
                    }

                    if (!burnRes.Success)
                    {
                        return DiscCopyResult.Failed(
                            $"Copy {copyIndex} of {copyCount} failed: {burnRes.ErrorMessage}",
                            copyCount,
                            completed,
                            stagedPath);
                    }

                    if (options.VerifyEachCopy && !rawMode)
                    {
                        var verifyRes = await _verificationService.VerifyDiscAgainstIsoFileAsync(
                            targetRecorder,
                            _burnEngine,
                            stagedPath,
                            progress: progress,
                            ct: ct).ConfigureAwait(false);

                        if (!verifyRes.IsMatch)
                        {
                            return DiscCopyResult.Failed(
                                $"Verification failed on copy {copyIndex} of {copyCount}: {verifyRes.ErrorMessage}",
                                copyCount,
                                completed,
                                stagedPath);
                        }
                    }

                    completed++;
                    totalSectors += burnRes.SectorsWritten;
                }

                sw.Stop();
                return new DiscCopyResult
                {
                    Success = true,
                    CopiesRequested = copyCount,
                    CopiesCompleted = completed,
                    SectorsPerDisc = sectorsPerDisc,
                    TotalSectorsWritten = totalSectors,
                    WasAudioCd = isAudioDisc,
                    IsMixedMode = isMixedMode,
                    StagedImagePath = options.KeepStagedImage ? stagedPath : null,
                    TotalTime = sw.Elapsed
                };
            }
        }
        finally
        {
            if (!options.KeepStagedImage)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignore temp cleanup errors
                }
            }
        }
    }

    /// <summary>
    /// Inspects <paramref name="sourceRecorder"/> and returns a plain-language summary of what 1-Click Disc Copy
    /// will read and write with the current <paramref name="directDriveCommandsEnabled"/> setting.
    /// </summary>
    public static DiscCopyPlanSummary DescribeCopyPlan(IDiscRecorderBackend? sourceRecorder, bool directDriveCommandsEnabled)
    {
        if (sourceRecorder == null)
        {
            return new DiscCopyPlanSummary(
                CopyDiscKind.NoDisc, 0, 0, 0, false, false,
                "Select a source optical drive to see what will be copied.");
        }

        var status = sourceRecorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            return new DiscCopyPlanSummary(
                CopyDiscKind.NoDisc, 0, 0, 0, false, false,
                "No disc in the source drive.");
        }

        if (status.IsBlank || status.UsedSectors <= 0)
        {
            return new DiscCopyPlanSummary(
                CopyDiscKind.BlankDisc, 0, 0, 0, false, false,
                "The disc in the source drive is blank — insert a recorded CD, DVD, or Blu-ray to copy.");
        }

        var tracks = ReadSourceTracks(sourceRecorder);
        int audioCount = tracks.Count(t => t.IsAudio);
        int dataCount = tracks.Count(t => !t.IsAudio);
        int totalSectors = status.UsedSectors;

        if (tracks.Count >= 2 && tracks[0].IsAudio && !tracks[^1].IsAudio && tracks.Take(tracks.Count - 1).All(t => t.IsAudio))
        {
            string summary = directDriveCommandsEnabled
                ? $"Enhanced CD ({audioCount} audio track(s) in session 1 + data track in session 2): session 1 audio and CD-Text will be ripped securely with error checks, and session 2 data files will be read and rewritten at the new disc's next writable address."
                : $"Enhanced CD ({audioCount} audio track(s) in session 1 + data track in session 2): only session 1 audio tracks will be copied. Session 2 data will be skipped — turn on Direct drive commands to copy both sessions.";
            return new DiscCopyPlanSummary(
                CopyDiscKind.EnhancedCd,
                audioCount,
                dataCount,
                totalSectors,
                WillCopyAudio: true,
                WillCopyData: directDriveCommandsEnabled,
                summary);
        }

        if (tracks.Count >= 2 && !tracks[0].IsAudio && tracks.Skip(1).All(t => t.IsAudio))
        {
            string summary = directDriveCommandsEnabled
                ? $"Mixed-mode CD (data in track 1 + {audioCount} audio track(s)): track 1 data and all audio tracks with CD-Text will be copied together."
                : $"Mixed-mode CD (data in track 1 + {audioCount} audio track(s)): only the audio tracks will be copied. Track 1 data will be skipped — turn on Direct drive commands to copy both.";
            return new DiscCopyPlanSummary(
                CopyDiscKind.MixedModeCd,
                audioCount,
                dataCount,
                totalSectors,
                WillCopyAudio: true,
                WillCopyData: directDriveCommandsEnabled,
                summary);
        }

        if (tracks.Count > 0 && tracks.All(t => t.IsAudio))
        {
            return new DiscCopyPlanSummary(
                CopyDiscKind.AudioCd,
                audioCount,
                0,
                totalSectors,
                WillCopyAudio: true,
                WillCopyData: false,
                $"Audio CD ({audioCount} track(s)): all audio tracks, gaps, and CD-Text will be ripped securely with error checks and written to the target CD.");
        }

        if (audioCount > 0)
        {
            return new DiscCopyPlanSummary(
                CopyDiscKind.MixedModeCd,
                audioCount,
                dataCount,
                totalSectors,
                WillCopyAudio: true,
                WillCopyData: false,
                $"Mixed CD ({audioCount} audio track(s), {dataCount} data track(s)): multi-track mixed layout is not supported for data copying, so only the audio tracks will be copied.");
        }

        return new DiscCopyPlanSummary(
            CopyDiscKind.DataDisc,
            0,
            Math.Max(1, dataCount),
            totalSectors,
            WillCopyAudio: false,
            WillCopyData: true,
            $"Data {status.MediaType.GetDisplayName()} ({totalSectors:N0} sectors): all data sectors will be copied to the target disc.");
    }

    private static int EstimateAudioSessionFrames(AudioCdCompilation compilation)
    {
        int total = 0;
        for (int i = 0; i < compilation.Tracks.Count; i++)
        {
            var t = compilation.Tracks[i];
            int preGap = i > 0
                ? Math.Max(0, t.PreGapFrames)
                : Math.Max(0, t.PreGapFrames - OpticalConstants.CdLeadInFrames);
            total += t.DurationFrames + preGap + (i == 0 ? t.HiddenAudioFrames : 0);
        }
        return total;
    }

    private sealed record StagedEnhancedDataSession(CompilationDirectory Root, IsoBuildOptions BuildOptions);

    private async Task<(StagedEnhancedDataSession? Session, string? Error)> StageEnhancedCdDataSessionAsync(
        IDiscRecorderBackend sourceRecorder,
        DiscTocTrack dataTrack,
        string stageDir,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        const int pvdProbeSectors = 17;
        if (dataTrack.SectorCount < pvdProbeSectors)
        {
            return (null, $"session 2 track {dataTrack.TrackNumber} is only {Math.Max(0, dataTrack.SectorCount)} sectors long.");
        }

        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "Inspecting Enhanced CD session 2 volume...",
            TotalLba = dataTrack.SectorCount,
            BufferLevelPercent = 100.0
        });

        byte[] headBuffer = new byte[pvdProbeSectors * OpticalConstants.DataSectorSize];
        int headReadCount = 0;
        var headRead = await _burnEngine.ReadSectorsAsync(
            sourceRecorder,
            dataTrack.StartLba,
            pvdProbeSectors,
            raw2352Mode: false,
            (_, sector) =>
            {
                if (sector.Length != OpticalConstants.DataSectorSize)
                {
                    throw new InvalidDataException($"the drive returned {sector.Length} bytes for a data sector.");
                }
                sector.Span.CopyTo(headBuffer.AsSpan(headReadCount * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize));
                headReadCount++;
                return ValueTask.CompletedTask;
            },
            progress,
            ct).ConfigureAwait(false);

        if (!headRead.Success || headReadCount != pvdProbeSectors)
        {
            return (null, headRead.ErrorMessage ?? $"only {headReadCount} of {pvdProbeSectors} header sectors could be read.");
        }

        var pvd = headBuffer.AsSpan(16 * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize);
        if (pvd[0] != 0x01 || Encoding.ASCII.GetString(pvd.Slice(1, 5)) != "CD001")
        {
            return (null, $"missing ISO 9660 Primary Volume Descriptor at LBA {dataTrack.StartLba + 16}.");
        }

        int volSpace = BinaryPrimitives.ReadInt32LittleEndian(pvd.Slice(80, 4));
        int isoSectors;
        if (volSpace > dataTrack.StartLba + 16)
        {
            isoSectors = volSpace - dataTrack.StartLba;
        }
        else if (volSpace >= pvdProbeSectors && volSpace <= dataTrack.SectorCount)
        {
            isoSectors = volSpace;
        }
        else
        {
            isoSectors = -1;
        }

        if (isoSectors < pvdProbeSectors || isoSectors > dataTrack.SectorCount)
        {
            return (null, $"session 2 volume size ({volSpace}) is outside track {dataTrack.TrackNumber} ({dataTrack.SectorCount} sectors).");
        }

        Directory.CreateDirectory(stageDir);
        string sourceBinPath = Path.Combine(stageDir, "session2_source.bin");
        int totalWritten = pvdProbeSectors;

        await using (var binFile = new FileStream(sourceBinPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await binFile.WriteAsync(headBuffer, ct).ConfigureAwait(false);

            if (isoSectors > pvdProbeSectors)
            {
                int remaining = isoSectors - pvdProbeSectors;
                var tailRead = await _burnEngine.ReadSectorsAsync(
                    sourceRecorder,
                    dataTrack.StartLba + pvdProbeSectors,
                    remaining,
                    raw2352Mode: false,
                    async (_, sector) =>
                    {
                        if (sector.Length != OpticalConstants.DataSectorSize)
                        {
                            throw new InvalidDataException($"the drive returned {sector.Length} bytes for a data sector.");
                        }
                        await binFile.WriteAsync(sector, ct).ConfigureAwait(false);
                        totalWritten++;
                    },
                    progress,
                    ct).ConfigureAwait(false);

                if (!tailRead.Success)
                {
                    return (null, tailRead.ErrorMessage ?? "the drive reported a read error in session 2.");
                }
            }
        }

        if (totalWritten != isoSectors)
        {
            return (null, $"only {totalWritten:N0} of {isoSectors:N0} session 2 sectors were read.");
        }

        string extractRoot = Path.Combine(stageDir, "session2_files");
        Directory.CreateDirectory(extractRoot);

        await using var binRead = new FileStream(sourceBinPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false);
        using var offsetStream = new SessionOffsetStream(binRead, dataTrack.StartLba, leaveOpen: true);
        using var isoReader = IsoImageReader.FromStream(offsetStream, leaveOpen: true, sessionStartLba: dataTrack.StartLba);

        var staged = await CloneIsoTreeToCompilationAsync(isoReader, offsetStream, extractRoot, ct).ConfigureAwait(false);
        return (staged, null);
    }

    private static async Task<StagedEnhancedDataSession> CloneIsoTreeToCompilationAsync(
        IsoImageReader isoReader,
        Stream isoStream,
        string extractRoot,
        CancellationToken ct)
    {
        var root = CompilationDirectory.CreateRoot();
        int fileCounter = 0;
        byte[] copyBuffer = new byte[64 * 1024];

        async Task CopyDirectoryAsync(IsoDirectoryEntry sourceDir, CompilationDirectory targetDir)
        {
            foreach (var subDir in sourceDir.SubDirectories)
            {
                ct.ThrowIfCancellationRequested();
                var childDir = new CompilationDirectory(
                    subDir.Name,
                    lastWriteTimeUtc: subDir.RecordingTimeUtc,
                    attributes: subDir.IsHidden
                        ? (FileAttributes.Directory | FileAttributes.Hidden)
                        : FileAttributes.Directory);
                targetDir.AddDirectory(childDir);
                await CopyDirectoryAsync(subDir, childDir).ConfigureAwait(false);
            }

            foreach (var isoFile in sourceDir.Files)
            {
                ct.ThrowIfCancellationRequested();
                string stagedFilePath = Path.Combine(extractRoot, $"f_{++fileCounter:D6}.dat");
                await using (var outStream = new FileStream(stagedFilePath, FileMode.Create, FileAccess.Write, FileShare.None, copyBuffer.Length, useAsync: true))
                {
                    if (isoFile.SizeBytes > 0)
                    {
                        isoStream.Position = (long)isoFile.StartLba * OpticalConstants.DataSectorSize;
                        long remaining = isoFile.SizeBytes;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(copyBuffer.Length, remaining);
                            int read = isoStream.Read(copyBuffer, 0, toRead);
                            if (read <= 0)
                            {
                                throw new EndOfStreamException($"Unexpected end of session 2 image while extracting '{isoFile.VirtualPath}'.");
                            }
                            await outStream.WriteAsync(copyBuffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            remaining -= read;
                        }
                    }
                }

                var compFile = new CompilationFile(
                    isoFile.Name,
                    stagedFilePath,
                    isoFile.SizeBytes,
                    importedStartLba: null,
                    streamFactory: null,
                    parent: null,
                    lastWriteTimeUtc: isoFile.RecordingTimeUtc,
                    attributes: isoFile.IsHidden ? FileAttributes.Hidden : FileAttributes.Normal);
                targetDir.AddFile(compFile, DuplicateFilePolicy.Overwrite);
            }
        }

        await CopyDirectoryAsync(isoReader.RootDirectory, root).ConfigureAwait(false);

        string label = string.IsNullOrWhiteSpace(isoReader.VolumeLabel) ? "CD_EXTRA" : isoReader.VolumeLabel;
        var buildOptions = new IsoBuildOptions(
            VolumeLabel: label,
            IncludeIso9660: true,
            IncludeJoliet: isoReader.HasJoliet,
            IncludeUdf: false);

        return new StagedEnhancedDataSession(root, buildOptions);
    }

    private sealed class SessionOffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _baseOffset;
        private readonly long _length;
        private readonly bool _leaveOpen;
        private long _position;

        public SessionOffsetStream(Stream inner, int sessionStartLba, bool leaveOpen = false)
        {
            _inner = inner;
            _baseOffset = (long)sessionStartLba * OpticalConstants.DataSectorSize;
            _length = _baseOffset + inner.Length;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _length)
            {
                return 0;
            }

            int totalRead = 0;
            if (_position < _baseOffset)
            {
                int zeroCount = (int)Math.Min(buffer.Length, _baseOffset - _position);
                buffer[..zeroCount].Clear();
                _position += zeroCount;
                totalRead += zeroCount;
                buffer = buffer[zeroCount..];
            }

            if (!buffer.IsEmpty && _position < _length)
            {
                _inner.Position = _position - _baseOffset;
                int maxRead = (int)Math.Min(buffer.Length, _length - _position);
                int read = _inner.Read(buffer[..maxRead]);
                _position += read;
                totalRead += read;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0)
            {
                throw new IOException("Cannot seek before start of stream.");
            }
            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// True when two recorder handles refer to the same physical (or virtual) drive.
    /// </summary>
    public static bool IsSameDrive(IDiscRecorderBackend a, IDiscRecorderBackend b) =>
        ReferenceEquals(a, b) ||
        (!string.IsNullOrWhiteSpace(a.UniqueId) && string.Equals(a.UniqueId, b.UniqueId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Detects a Red Book Audio CD in <paramref name="recorder"/>. Physical drives (<see cref="IDiscTocProvider"/>, e.g.
    /// the IMAPI recorder) are classified from the real Table of Contents; virtual drives from their mounted medium.
    /// </summary>
    public static bool IsSourceAudioCd(IDiscRecorderBackend recorder) =>
        InspectSourceDiscLayout(recorder).IsAudioDisc;

    /// <summary>
    /// Inspects the disc Table of Contents or mounted medium to classify whether the disc has audio tracks,
    /// and whether it is a mixed-mode or Enhanced CD (containing both audio and data tracks).
    /// </summary>
    public static (bool IsAudioDisc, bool IsMixedMode) InspectSourceDiscLayout(IDiscRecorderBackend recorder) =>
        Classify(ReadSourceTracks(recorder));

    private static (bool IsAudioDisc, bool IsMixedMode) Classify(IReadOnlyList<DiscTocTrack> tracks)
    {
        bool hasAudio = tracks.Any(t => t.IsAudio);
        bool hasData = tracks.Any(t => !t.IsAudio);
        return (hasAudio, hasAudio && hasData);
    }

    /// <summary>
    /// Reads a mixed-mode CD's data track as 2,048-byte user data into <paramref name="path"/>. Returns null when every
    /// sector was read, or what went wrong.
    /// </summary>
    private async Task<string?> ReadDataTrackAsync(IDiscRecorderBackend recorder, int startLba, int sectors, string path,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        if (sectors < 4 * OpticalConstants.AudioFramesPerSecond)
        {
            return $"track 1 is only {Math.Max(0, sectors)} sectors long.";
        }

        progress?.Report(new BurnProgressSnapshot { CurrentActionDescription = "Reading the data track", TotalLba = sectors, BufferLevelPercent = 100.0 });
        int written = 0;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var read = await _burnEngine.ReadSectorsAsync(recorder, startLba, sectors, raw2352Mode: false,
                async (_, data) =>
                {
                    if (data.Length != DataTrackImage.UserDataSize)
                    {
                        throw new InvalidDataException($"the drive returned {data.Length} bytes for a data sector.");
                    }
                    await file.WriteAsync(data, ct).ConfigureAwait(false);
                    written++;
                },
                progress, ct).ConfigureAwait(false);
            if (!read.Success)
            {
                return read.ErrorMessage ?? "the drive reported a read error.";
            }
        }
        return written == sectors ? null : $"only {written:N0} of {sectors:N0} sectors were read.";
    }

    /// <summary>
    /// The disc's tracks with their start, length and audio/data flag: from the drive's Table of Contents on a
    /// physical drive, from the mounted medium on a virtual one. Empty when neither is available.
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> ReadSourceTracks(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        // Physical drives: the TOC's per-track control bits are authoritative.
        if (recorder is IDiscTocProvider tocProvider)
        {
            try
            {
                var toc = tocProvider.ReadTableOfContents();
                if (toc.Count > 0)
                {
                    return toc;
                }
            }
            catch
            {
                // Fall through to the medium inspection below.
            }
        }

        try
        {
            // Check if VirtualDiscRecorder has CurrentMedium with audio tracks
            var prop = recorder.GetType().GetProperty("CurrentMedium");
            if (prop?.GetValue(recorder) is object medium)
            {
                var sessionsProp = medium.GetType().GetProperty("Sessions");
                if (sessionsProp?.GetValue(medium) is System.Collections.IEnumerable sessions)
                {
                    var found = new List<DiscTocTrack>();
                    foreach (object session in sessions)
                    {
                        var tracksProp = session.GetType().GetProperty("Tracks");
                        if (tracksProp?.GetValue(session) is System.Collections.IEnumerable tracks)
                        {
                            foreach (object track in tracks)
                            {
                                var type = track.GetType();
                                if (type.GetProperty("IsAudio")?.GetValue(track) is bool isAudio)
                                {
                                    found.Add(new DiscTocTrack(
                                        type.GetProperty("TrackNumber")?.GetValue(track) is int n ? n : found.Count + 1,
                                        type.GetProperty("StartLba")?.GetValue(track) is int s ? s : 0,
                                        type.GetProperty("SectorCount")?.GetValue(track) is int c ? c : 0,
                                        isAudio));
                                }
                            }
                        }
                    }
                    if (found.Count > 0)
                    {
                        return found;
                    }
                }
            }
        }
        catch
        {
        }

        return Array.Empty<DiscTocTrack>();
    }
}
