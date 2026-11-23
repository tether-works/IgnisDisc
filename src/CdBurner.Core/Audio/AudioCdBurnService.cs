using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Capacity and layout plan for a Mode 1 ISO 9660 + Joliet data session (session 2) on an Enhanced CD (CD-Extra).
/// </summary>
public sealed record EnhancedCdDataPlan(
    CompilationDirectory Root,
    IsoBuildOptions BuildOptions,
    int FileCount,
    int IsoSectors,
    int PaddedTrackSectors,
    int PredictedStartLba,
    long TotalDiscSectorsRequired,
    string? ShortenedNamesWarning);

/// <summary>
/// Service orchestrating Red Book Audio CD burning (Track-at-Once and gapless Disc-at-Once with Lead-In CD-Text),
/// decoding source audio, applying DSP normalization and 2352-byte sector padding, and executing the burn backend.
/// </summary>
public class AudioCdBurnService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly IAudioDecoder _audioDecoder;
    private readonly IBurnEngineBackend? _recoveryDataEngine;

    /// <summary>
    /// Gets the burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Gets the audio decoder.
    /// </summary>
    public IAudioDecoder AudioDecoder => _audioDecoder;

    /// <summary>
    /// Manifest written to session 2 on the most recent Enhanced CD recovery burn, or null if none ran.
    /// </summary>
    public AudioRecoveryManifest? LastRecoveryManifest { get; private set; }

    /// <summary>Number of 2,048-byte ISO sectors written to session 2 on the most recent Enhanced CD folder burn, or 0.</summary>
    public int LastDataSessionSectors { get; private set; }

    /// <summary>Start LBA (NWA) of session 2 on the most recent Enhanced CD folder burn, or null.</summary>
    public int? LastDataSessionStartLba { get; private set; }

    /// <summary>
    /// Computes the ISO 9660 + Joliet session-2 layout and total disc sectors required for an Enhanced CD
    /// carrying <paramref name="audioSessionFrames"/> of session-1 audio followed by <paramref name="folderPath"/>.
    /// </summary>
    /// <param name="blueBook">When given, the session is laid out as an Enhanced CD (Blue Book): <c>CDPLUS/INFO.CDP</c>
    /// with this text and <c>PICTURES/</c> are added, and the volume is marked CD-ROM XA (see <see cref="BlueBookLayout"/>).</param>
    public static EnhancedCdDataPlan PlanDataSessionFolder(
        string folderPath,
        string? volumeLabel,
        int audioSessionFrames,
        BlueBookDiscInfo? blueBook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Data session folder does not exist: '{folderPath}'.");
        }

        var root = CompilationFolderImporter.ImportAsRoot(folderPath, strict: true);
        if (root.Children.Count == 0)
        {
            throw new InvalidOperationException("The selected data session folder is empty.");
        }

        var files = root.GetAllFilesRecursive();
        foreach (var file in files)
        {
            if (file.SizeBytes > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"File '{file.Name}' ({file.SizeBytes:N0} bytes) exceeds the 4 GiB ISO 9660 / Joliet single-file limit.");
            }
        }

        string label = string.IsNullOrWhiteSpace(volumeLabel) ? "CD_EXTRA" : volumeLabel.Trim();
        var buildOptions = new IsoBuildOptions(
            VolumeLabel: label,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: false);
        if (blueBook != null)
        {
            BlueBookLayout.Apply(root, blueBook);
            buildOptions = BlueBookLayout.WithBlueBook(buildOptions);
            files = root.GetAllFilesRecursive();
        }

        int predictedNwa = Math.Max(0, audioSessionFrames) + (int)RecoverySessionBudget.FixedSessionSectors;
        int isoSectors = IsoUdfImageBuilder.CalculateTotalSectors(root, buildOptions, predictedNwa) - predictedNwa;
        int paddedSectors = Math.Max(300, isoSectors);
        long totalRequired = (long)predictedNwa + paddedSectors + 2;
        string? shortenedWarning = DataBurnService.DescribeShortenedNames(root, buildOptions, predictedNwa);

        return new EnhancedCdDataPlan(
            root,
            buildOptions,
            files.Count,
            isoSectors,
            paddedSectors,
            predictedNwa,
            totalRequired,
            shortenedWarning);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="AudioCdBurnService"/>.
    /// </summary>
    /// <param name="burnEngine">The optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder instance. If null, a built-in RIFF WAVE decoder is used.</param>
    /// <param name="recoveryDataEngine">Optional data burn engine used to append session 2 when <see cref="AudioBurnOptions.IncludeRecoverySession"/> is enabled.</param>
    public AudioCdBurnService(
        IBurnEngineBackend burnEngine,
        IAudioDecoder? audioDecoder = null,
        IBurnEngineBackend? recoveryDataEngine = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _audioDecoder = audioDecoder ?? new CoreWavAudioDecoder();
        _recoveryDataEngine = recoveryDataEngine;
    }

    /// <summary>
    /// Decodes each FLAC source once, fully, and returns the damaged ones with a plain description. Other formats are
    /// left to their decoders. A file that cannot be opened is left to the burn, which reports it in its own words.
    /// </summary>
    public static IReadOnlyList<(string Path, string Problem)> CheckFlacSources(
        AudioCdCompilation compilation, CancellationToken cancellationToken = default, IProgress<BurnProgressSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var damaged = new List<(string, string)>();
        var flacs = compilation.Tracks.Select(t => t.SourceFilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && FlacFileCodec.IsFlacFile(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < flacs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = flacs[i];
            progress?.Report(new BurnProgressSnapshot
            {
                CurrentLba = i,
                TotalLba = flacs.Count,
                CurrentActionDescription = $"Checking FLAC files for damage ({i + 1} of {flacs.Count})"
            });

            try
            {
                var report = FlacFileCodec.CheckIntegrity(path);
                if (report.IsDamaged)
                {
                    damaged.Add((path, report.Describe()));
                }
            }
            catch (InvalidDataException ex)
            {
                damaged.Add((path, ex.Message));
            }
            catch (IOException)
            {
                // Unreadable right now: the burn opens it again and reports the error itself.
            }
        }
        return damaged;
    }

    /// <summary>
    /// Burns an Audio CD compilation to the specified recorder.
    /// </summary>
    public Task<BurnOperationResult> BurnAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(recorder, compilation, options, progress, checkOnly: false, cancellationToken);

    /// <summary>
    /// Checks the recorded disc in <paramref name="recorder"/> against <paramref name="compilation"/> without writing:
    /// the tracks are prepared exactly as for a burn, then the engine's read-back check compares the disc with them.
    /// Needs an engine that can check discs (<see cref="IAudioDiscVerifier"/>, the direct drive-command writer).
    /// </summary>
    public Task<BurnOperationResult> VerifyAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(recorder, compilation, options, progress, checkOnly: true, cancellationToken);

    private async Task<BurnOperationResult> RunAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options,
        IProgress<BurnProgressSnapshot>? progress,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(compilation);
        options ??= new AudioBurnOptions();

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate tracks
        if (compilation.TrackCount == 0)
        {
            return BurnOperationResult.Failed("Compilation contains no audio tracks.");
        }

        if (compilation.TrackCount > AudioCdCompilation.MaxTracks)
        {
            return BurnOperationResult.Failed(
                $"Compilation contains {compilation.TrackCount} tracks, which exceeds the Red Book limit of {AudioCdCompilation.MaxTracks}.");
        }

        // 1a. A mixed-mode CD (data track 1, then audio) needs the direct drive-command writer.
        if (compilation.HasDataTrack)
        {
            if (!compilation.Tracks[0].IsDataTrack || compilation.Tracks.Skip(1).Any(t => t.IsDataTrack))
            {
                return BurnOperationResult.Failed(
                    "Only track 1 of a mixed-mode CD can be a data track. Move the data track to the top, or remove it. Nothing was written to the disc.");
            }
            if (compilation.TrackCount < 2)
            {
                return BurnOperationResult.Failed("A mixed-mode CD needs audio after its data track. Nothing was written to the disc.");
            }
            if (!_burnEngine.WritesMixedModeCds && !checkOnly)
            {
                return BurnOperationResult.Failed(
                    "Track 1 is a data track (a mixed-mode CD). Only direct drive commands can write one: turn them on, or remove the data track. "
                    + "Nothing was written to the disc.");
            }
        }

        IAudioDiscVerifier? verifier = null;
        if (checkOnly && (verifier = _burnEngine as IAudioDiscVerifier) == null)
        {
            return BurnOperationResult.Failed("Checking a disc against its source needs direct drive commands.");
        }

        // 1b. Damaged FLAC sources would be burned in as silence or missing audio: stop before the drive is touched.
        if (!checkOnly && !options.AllowDamagedAudio && !options.Simulate)
        {
            var damaged = CheckFlacSources(compilation, cancellationToken, progress);
            if (damaged.Count > 0)
            {
                string list = string.Join(" ", damaged.Select(d => $"'{Path.GetFileName(d.Path)}': {d.Problem}"));
                return new BurnOperationResult
                {
                    Success = false,
                    Status = BurnStatus.Failed,
                    ErrorMessage = $"{damaged.Count} FLAC file(s) are damaged, so the disc would not hold the full audio. {list} "
                        + "Nothing was written to the disc. Replace the file(s), or burn anyway to accept the damage.",
                    DamagedAudioFiles = damaged.Select(d => d.Path).ToList()
                };
            }
        }

        // 2. Query and validate media
        var mediaStatus = await CdBurner.Core.Burning.MediaStatusWait.QueryWhenLoadedAsync(recorder, cancellationToken);
        if (!mediaStatus.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        if (mediaStatus.IsLoading)
        {
            return BurnOperationResult.Failed(CdBurner.Core.Burning.MediaStatusWait.StillLoadingMessage);
        }

        if (checkOnly)
        {
            // A check reads a recorded disc; none of the writing checks apply.
            if (mediaStatus.IsBlank)
            {
                return BurnOperationResult.Failed("The disc is blank: there is nothing to check.");
            }
        }
        else
        {
            if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
            {
                return BurnOperationResult.Failed("Mounted media is not writable (disc is finalized or not blank/rewritable).");
            }

            if (mediaStatus.RequiresEraseToWrite)
            {
                return BurnOperationResult.Failed(CdBurner.Core.Burning.DataBurnService.EraseRequiredMessage);
            }

            if (mediaStatus.IsOverwriteOnly || mediaStatus.MediaType.IsDvd())
            {
                return BurnOperationResult.Failed("Audio CDs require blank CD-R or CD-RW media.");
            }
        }

        LastRecoveryManifest = null;
        LastDataSessionSectors = 0;
        LastDataSessionStartLba = null;

        bool hasDataFolder = !string.IsNullOrWhiteSpace(options.DataSessionFolderPath);
        bool hasSessionTwoImage = options.SessionTwoImage != null && !checkOnly;
        if (hasSessionTwoImage)
        {
            if (hasDataFolder || options.IncludeRecoverySession)
            {
                return BurnOperationResult.Failed(
                    "The picture session fills the rest of the disc, so it cannot be combined with a data folder or a PAR2 recovery session. Nothing was written to the disc.");
            }
            if (compilation.HasDataTrack)
            {
                return BurnOperationResult.Failed(
                    "The picture session cannot be combined with a mixed-mode data track in track 1. Nothing was written to the disc.");
            }
            if (_recoveryDataEngine == null || !_burnEngine.WritesMixedModeCds)
            {
                return BurnOperationResult.Failed(
                    "A picture session after the audio requires direct drive commands. Nothing was written to the disc.");
            }
        }
        if (options.BlueBookLayout && !hasDataFolder && !checkOnly)
        {
            return BurnOperationResult.Failed(
                "The Enhanced CD (Blue Book) layout applies to a data folder in session 2; choose a data folder or turn the layout off. Nothing was written to the disc.");
        }
        if (hasDataFolder && !checkOnly)
        {
            if (options.IncludeRecoverySession)
            {
                return BurnOperationResult.Failed(
                    "Choose either a data folder or a PAR2 recovery session for session 2, not both. Nothing was written to the disc.");
            }
            if (compilation.HasDataTrack)
            {
                return BurnOperationResult.Failed(
                    "An Enhanced CD data folder (session 2) cannot be combined with a mixed-mode data track in track 1. Nothing was written to the disc.");
            }
            if (_recoveryDataEngine == null || !_burnEngine.WritesMixedModeCds)
            {
                return BurnOperationResult.Failed(
                    "Enhanced CDs (audio, then a data folder in session 2) require direct drive commands. Nothing was written to the disc.");
            }
            if (!Directory.Exists(options.DataSessionFolderPath))
            {
                return BurnOperationResult.Failed(
                    $"Data session folder does not exist: '{options.DataSessionFolderPath}'. Nothing was written to the disc.");
            }

            try
            {
                var preImport = CompilationFolderImporter.ImportAsRoot(options.DataSessionFolderPath!, strict: true);
                if (preImport.Children.Count == 0)
                {
                    return BurnOperationResult.Failed("The selected data session folder is empty. Nothing was written to the disc.");
                }
                foreach (var file in preImport.GetAllFilesRecursive())
                {
                    if (file.SizeBytes > uint.MaxValue)
                    {
                        return BurnOperationResult.Failed(
                            $"File '{file.Name}' exceeds the 4 GiB ISO 9660 / Joliet single-file limit. Nothing was written to the disc.");
                    }
                }
                if (options.BlueBookLayout)
                {
                    // Before any audio is written: a name clash with the Enhanced CD folders stops here, not after session 1.
                    BlueBookLayout.Apply(preImport, BlueBookDiscInfo.From(compilation));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return BurnOperationResult.Failed(
                    $"Could not read data session folder '{options.DataSessionFolderPath}': {ex.Message} Nothing was written to the disc.",
                    ex);
            }
        }

        string? par2Path = null;
        if (options.IncludeRecoverySession && !checkOnly)
        {
            if (_recoveryDataEngine == null)
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions require direct drive commands (--native).");
            }
            if (compilation.Tracks.Any(t => t.IsDataTrack))
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions cannot be combined with a track-based data track in the audio compilation.");
            }
            par2Path = Par2Runner.FindPar2Executable();
            if (string.IsNullOrEmpty(par2Path))
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions require par2.exe, which was not found on PATH or next to the application.");
            }
        }

        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "PreparingAudioTracks",
            CurrentLba = 0,
            TotalLba = 100,
            BufferLevelPercent = 100.0
        });

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Process, decode, normalize, and pad tracks
        var audioTrackSources = new List<AudioTrackSource>(compilation.TrackCount);

        try
        {
            int totalRequiredFrames = 0;
            var preparedTracks = new byte[compilation.Tracks.Count][];
            var albumMeasurements = new ReplayGainResult?[compilation.Tracks.Count];

            for (int i = 0; i < compilation.Tracks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trackItem = compilation.Tracks[i];

                progress?.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = $"Preparing track {i + 1}/{compilation.Tracks.Count}: {trackItem.Title}",
                    CurrentLba = i,
                    TotalLba = Math.Max(1, compilation.Tracks.Count),
                    BufferLevelPercent = 100.0
                });

                if (trackItem.IsDataTrack)
                {
                    preparedTracks[i] = Array.Empty<byte>(); // read from the image while writing; nothing to decode
                    continue;
                }

                Stream pcmStream;
                try
                {
                    if (trackItem.CustomPcmStreamFactory != null)
                    {
                        pcmStream = trackItem.CustomPcmStreamFactory();
                    }
                    else if (!string.IsNullOrEmpty(trackItem.SourceFilePath) && File.Exists(trackItem.SourceFilePath))
                    {
                        pcmStream = _audioDecoder.OpenRedBookPcmStream(trackItem.SourceFilePath);
                    }
                    else
                    {
                        return BurnOperationResult.Failed(
                            $"Audio source not found for track {trackItem.TrackNumber}: '{trackItem.SourceFilePath}'.");
                    }
                }
                catch (DrmProtectedAudioException drmEx)
                {
                    return BurnOperationResult.Failed(
                        $"Track {trackItem.TrackNumber} ('{trackItem.Title}') cannot be burned: {drmEx.Message}");
                }
                catch (Exception ex)
                {
                    return BurnOperationResult.Failed(
                        $"Failed to decode track {trackItem.TrackNumber} ('{trackItem.Title}'): {ex.Message}");
                }

                var trackStreams = new List<Stream>(3) { pcmStream };
                byte[] pcmBytes;

                try
                {
                    // Normalization
                    bool shouldNormalize = compilation.NormalizeAllTracks || trackItem.NormalizeAudio || options.PerformNormalization;
                    if (shouldNormalize)
                    {
                        try
                        {
                            var normalized = RedBookPcmDspProcessor.NormalizePcmStream(pcmStream);
                            trackStreams.Add(normalized);
                            pcmStream = normalized;
                        }
                        catch (Exception ex)
                        {
                            return BurnOperationResult.Failed(
                                $"DSP normalization failed for track {trackItem.TrackNumber} ('{trackItem.Title}'): {ex.Message}");
                        }
                    }

                    // Padding to 2352-byte boundary
                    var padded = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(pcmStream);
                    trackStreams.Add(padded);
                    pcmStream = padded;

                    if (pcmStream is MemoryStream ms && ms.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array?.Length)
                    {
                        pcmBytes = segment.Array;
                    }
                    else
                    {
                        using var mem = new MemoryStream();
                        await pcmStream.CopyToAsync(mem, cancellationToken).ConfigureAwait(false);
                        pcmBytes = mem.ToArray();
                    }

                    if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerTrack)
                    {
                        var measured = ReplayGainCalculator.CalculateTrackReplayGain(pcmBytes);
                        if (!double.IsNegativeInfinity(measured.IntegratedLoudnessLufs))
                        {
                            pcmBytes = RedBookPcmDspProcessor.ApplyLoudnessGain(pcmBytes, measured.GainDb);
                        }
                    }
                    else if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerAlbum)
                    {
                        albumMeasurements[i] = ReplayGainCalculator.CalculateTrackReplayGain(pcmBytes);
                    }
                    else if (compilation.UseRmsLoudnessLeveling)
                    {
                        pcmBytes = RedBookPcmDspProcessor.NormalizeRmsLoudness(pcmBytes);
                    }
                }
                finally
                {
                    foreach (var s in trackStreams)
                    {
                        try { s.Dispose(); } catch { }
                    }
                }

                preparedTracks[i] = pcmBytes;
            }

            // Album mode: one gain from all tracks' gated blocks together (data tracks have no measurement).
            if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerAlbum)
            {
                var measured = albumMeasurements.Where(m => m != null).Select(m => m!).ToList();
                double albumGainDb = ReplayGainCalculator.CalculateAlbumReplayGain(measured).AlbumGainDb;
                if (measured.Any(m => !double.IsNegativeInfinity(m.IntegratedLoudnessLufs)))
                {
                    for (int i = 0; i < preparedTracks.Length; i++)
                    {
                        if (albumMeasurements[i] is { } own && !double.IsNegativeInfinity(own.IntegratedLoudnessLufs))
                        {
                            preparedTracks[i] = RedBookPcmDspProcessor.ApplyLoudnessGain(preparedTracks[i], albumGainDb);
                        }
                    }
                }
            }

            // Crossfade adjacent tracks by overlapping them (the next track's head is mixed into this track's tail
            // and removed from the next track), so the transition plays exactly once. Only meaningful when tracks
            // butt against each other: gapless DAO with no pre-gap on the incoming track.
            if (compilation.CrossfadeSeconds > 0 && compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless)
            {
                for (int k = 0; k + 1 < preparedTracks.Length; k++)
                {
                    if (compilation.Tracks[k + 1].PreGapFrames > 0 || compilation.Tracks[k].IsDataTrack) continue;
                    (preparedTracks[k], preparedTracks[k + 1]) = RedBookPcmDspProcessor.ApplyCrossfadeBetweenTracks(
                        preparedTracks[k], preparedTracks[k + 1], compilation.CrossfadeSeconds);
                }
            }

            // INDEX 00 marks for the direct drive-command writer: the tail of the previous track's audio becomes
            // this track's pre-gap audio. The bytes on the disc stay in the same order; only the track mark moves.
            // Not done with a crossfade (track lengths have changed) or when it would leave a track under 4 seconds.
            var gapAudio = new (byte[] Source, int Offset, int Frames)?[preparedTracks.Length];
            var audibleLength = new int[preparedTracks.Length];
            for (int i = 0; i < preparedTracks.Length; i++)
            {
                audibleLength[i] = preparedTracks[i].Length;
            }
            if (options.KeepIndexMarks && compilation.CrossfadeSeconds <= 0)
            {
                const int minimumTrackFrames = 4 * OpticalConstants.AudioFramesPerSecond;
                for (int i = 1; i < preparedTracks.Length; i++)
                {
                    if (compilation.Tracks[i - 1].IsDataTrack) continue;
                    int lead = compilation.Tracks[i].Index00LeadFrames;
                    int previousFrames = preparedTracks[i - 1].Length / OpticalConstants.RawSectorSize;
                    if (lead > 0 && previousFrames - lead >= minimumTrackFrames)
                    {
                        audibleLength[i - 1] = (previousFrames - lead) * OpticalConstants.RawSectorSize;
                        gapAudio[i] = (preparedTracks[i - 1], audibleLength[i - 1], lead);
                    }
                }
            }

            for (int i = 0; i < preparedTracks.Length; i++)
            {
                var trackItem = compilation.Tracks[i];
                if (trackItem.DataTrack is { } data)
                {
                    // The data, then its two-second post-gap, which the writer adds.
                    totalRequiredFrames += data.SectorCount + AudioCdCompilation.MixedModeAudioPreGapFrames;
                    audioTrackSources.Add(new AudioTrackSource
                    {
                        TrackNumber = 1,
                        FilePath = trackItem.SourceFilePath,
                        Title = trackItem.Title,
                        Performer = !string.IsNullOrEmpty(trackItem.Performer) ? trackItem.Performer : compilation.AlbumPerformer,
                        PreGapFrames = trackItem.PreGapFrames,
                        TotalPcmBytes = data.WriterPayloadBytes,
                        Duration = TimeSpan.FromSeconds((double)data.SectorCount / OpticalConstants.AudioFramesPerSecond),
                        PcmStreamFactory = data.OpenWriterData,
                        IsDataTrack = true,
                        DataTrackMode = data.Mode
                    });
                    continue;
                }

                byte[] pcmBytes = preparedTracks[i];
                int pcmLength = audibleLength[i];
                int frames = pcmBytes.Length / OpticalConstants.RawSectorSize;
                trackItem.DurationFrames = frames;
                int preGap = i > 0 ? Math.Max(0, trackItem.PreGapFrames) : Math.Max(0, trackItem.PreGapFrames - OpticalConstants.CdLeadInFrames);
                totalRequiredFrames += frames + preGap;

                Func<Stream>? preGapFactory = null;
                int preGapAudioFrames = 0;
                bool isPreGapPicture = false;
                if (gapAudio[i] is { } gap)
                {
                    preGapFactory = () => new MemoryStream(gap.Source, gap.Offset, gap.Frames * OpticalConstants.RawSectorSize, writable: false);
                    preGapAudioFrames = gap.Frames;
                }
                else if (i == 0 && options.KeepIndexMarks && trackItem is { HiddenAudioFrames: > 0, HiddenAudioPcmStreamFactory: not null })
                {
                    preGapFactory = trackItem.HiddenAudioPcmStreamFactory;
                    preGapAudioFrames = trackItem.HiddenAudioFrames;
                    isPreGapPicture = trackItem.HiddenAudioIsPreGapPicture;
                    totalRequiredFrames += preGapAudioFrames;
                }

                audioTrackSources.Add(new AudioTrackSource
                {
                    TrackNumber = trackItem.TrackNumber > 0 ? trackItem.TrackNumber : i + 1,
                    FilePath = trackItem.SourceFilePath,
                    Title = trackItem.Title,
                    Performer = !string.IsNullOrEmpty(trackItem.Performer) ? trackItem.Performer : compilation.AlbumPerformer,
                    Songwriter = trackItem.Songwriter ?? compilation.Songwriter,
                    Composer = trackItem.Composer ?? compilation.Composer,
                    Isrc = trackItem.Isrc,
                    PreGapFrames = trackItem.PreGapFrames,
                    TotalPcmBytes = pcmLength,
                    Duration = TimeSpan.FromSeconds((double)pcmLength / OpticalConstants.RawSectorSize / OpticalConstants.AudioFramesPerSecond),
                    PcmStream = new MemoryStream(pcmBytes, 0, pcmLength, writable: false),
                    PcmStreamFactory = () => new MemoryStream(pcmBytes, 0, pcmLength, writable: false),
                    PreGapPcmStreamFactory = preGapFactory,
                    PreGapAudioFrames = preGapAudioFrames,
                    IsPreGapPicture = isPreGapPicture,
                    IsPictureTrack = trackItem.IsPictureTrack,
                    PreEmphasis = trackItem.PreEmphasis,
                    CopyPermitted = trackItem.CopyPermitted
                });
            }

            // 4. Validate capacity against media
            if (!checkOnly && mediaStatus.FreeSectors > 0 && totalRequiredFrames > mediaStatus.FreeSectors)
            {
                return BurnOperationResult.Failed(
                    $"Compilation requires {totalRequiredFrames} frames, exceeding media free capacity of {mediaStatus.FreeSectors} frames.");
            }

            long discCapacitySectors = mediaStatus.TotalSectors > 0
                ? mediaStatus.TotalSectors
                : RecoverySessionBudget.MeasuredCapacitySectors;
            if (options.IncludeRecoverySession && !checkOnly)
            {
                long audioOnlySectors = audioTrackSources.Sum(t => (t.TotalPcmBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
                var recoveryEstimate = AudioRecoverySessionService.EstimateCapacity(audioOnlySectors, discCapacitySectors);
                if (!recoveryEstimate.Fits)
                {
                    return BurnOperationResult.Failed($"Cannot fit recovery session: {recoveryEstimate.ToleranceSummary}");
                }
            }

            EnhancedCdDataPlan? dataPlan = null;
            if (hasDataFolder && !checkOnly)
            {
                dataPlan = PlanDataSessionFolder(options.DataSessionFolderPath!, options.DataSessionVolumeLabel, totalRequiredFrames,
                    options.BlueBookLayout ? BlueBookDiscInfo.From(compilation) : null);
                if (dataPlan.TotalDiscSectorsRequired > discCapacitySectors ||
                    (mediaStatus.FreeSectors > 0 && dataPlan.TotalDiscSectorsRequired > mediaStatus.FreeSectors))
                {
                    return BurnOperationResult.Failed(
                        $"Audio ({totalRequiredFrames:N0} sectors), session gap ({RecoverySessionBudget.FixedSessionSectors:N0} sectors), and data folder ({dataPlan.PaddedTrackSectors:N0} sectors) need {dataPlan.TotalDiscSectorsRequired:N0} sectors, exceeding disc capacity ({discCapacitySectors:N0} sectors). Nothing was written to the disc.");
                }
            }

            bool useDao = compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless;
            if (hasSessionTwoImage)
            {
                int taoExtra = useDao
                    ? 0
                    : audioTrackSources.Skip(1).Sum(t => t.IsDataTrack
                        ? 0
                        : Math.Max(0, OpticalConstants.CdLeadInFrames - Math.Max(0, t.PreGapFrames)));
                long audioSessionFrames = (long)totalRequiredFrames + taoExtra;
                long needed = audioSessionFrames + RecoverySessionBudget.FixedSessionSectors + options.SessionTwoImage!.MinimumSectors
                    + SessionTwoRunOutSectors;
                if (needed > discCapacitySectors)
                {
                    return BurnOperationResult.Failed(
                        $"Audio ({audioSessionFrames:N0} sectors), session gap ({RecoverySessionBudget.FixedSessionSectors:N0} sectors) and the smallest picture session ({options.SessionTwoImage.MinimumSectors:N0} sectors) need {needed:N0} sectors, more than the disc holds ({discCapacitySectors:N0}). Turn the picture off or remove a track. Nothing was written to the disc.");
                }

                int predictedNwa = checked((int)(audioSessionFrames + RecoverySessionBudget.FixedSessionSectors));
                long effectiveCapacity = mediaStatus.FreeSectors > 0
                    ? Math.Min(discCapacitySectors, mediaStatus.FreeSectors)
                    : discCapacitySectors;
                long predictedAvailable = SessionTwoSectorsAvailable(predictedNwa, effectiveCapacity, freeSectors: 0);
                if (options.SessionTwoImage.PreflightError(predictedNwa, predictedAvailable) is { } preflightError)
                {
                    return BurnOperationResult.Failed($"{preflightError} Nothing was written to the disc.");
                }
            }

            // 5. Generate CD-Text payload if requested
            bool includeCdText = compilation.IncludeCdText || options.WriteCdText;
            byte[]? cdTextPayload = includeCdText ? compilation.BuildCdTextPacks() : null;

            bool leaveOpenForSession2 = (options.IncludeRecoverySession || hasDataFolder || hasSessionTwoImage) && !checkOnly;
            var effectiveOptions = new AudioBurnOptions
            {
                Speed = options.Speed,
                Simulate = options.Simulate,
                EjectWhenDone = options.EjectWhenDone && !leaveOpenForSession2,
                PerformNormalization = false, // Already performed
                WriteCdText = includeCdText,
                UseDiscAtOnce = useDao,
                PreGapFrames = options.PreGapFrames > 0 ? options.PreGapFrames : compilation.DefaultPreGapFrames,
                CdTextBytes = cdTextPayload,
                MediaCatalogNumber = options.MediaCatalogNumber ?? compilation.UpcEan,
                KeepIndexMarks = options.KeepIndexMarks,
                WriteOffsetSamples = options.WriteOffsetSamples,
                LeaveSessionOpen = options.LeaveSessionOpen || leaveOpenForSession2,
                IncludeRecoverySession = options.IncludeRecoverySession,
                DisableBufferUnderrunFree = options.DisableBufferUnderrunFree
            };

            // 6. Execute burn through backend (or, for a check, read the disc back against the prepared tracks)
            if (verifier != null)
            {
                return await verifier.VerifyAudioTracksAsync(recorder, audioTrackSources, effectiveOptions, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var session1Result = await _burnEngine.BurnAudioTracksAsync(
                recorder,
                audioTrackSources,
                effectiveOptions,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!session1Result.Success || !leaveOpenForSession2 || options.Simulate)
            {
                return session1Result;
            }

            // 7. Build and write session 2 (data folder or PAR2 over PCM) at the next writable LBA and close the disc.
            var postAudioStatus = recorder.QueryMediaStatus();
            if (!postAudioStatus.IsAppendable || postAudioStatus.NextWritableLba <= 0)
            {
                return BurnOperationResult.Failed(
                    "Session 1 audio finished, but the drive did not leave the disc open for session 2.",
                    session1Result.Exception);
            }

            if (hasSessionTwoImage)
            {
                return await WriteSessionTwoImageAsync(recorder, options, session1Result, postAudioStatus, discCapacitySectors,
                    progress, cancellationToken).ConfigureAwait(false);
            }

            if (hasDataFolder)
            {
                int actualNwa = postAudioStatus.NextWritableLba;
                var session2FolderOptions = new DataBurnOptions
                {
                    VolumeLabel = dataPlan!.BuildOptions.VolumeLabel,
                    Speed = options.Speed,
                    Simulate = false,
                    CloseDisc = true,
                    VerifyAfterBurn = options.VerifyDataSession,
                    EjectWhenDone = options.EjectWhenDone,
                    DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
                    CdRomXaMode2Form1 = options.BlueBookLayout
                };

                var folderSessionResult = await new DataBurnService(_recoveryDataEngine!).BurnCompilationAsync(
                    recorder,
                    dataPlan.Root,
                    session2FolderOptions,
                    dataPlan.BuildOptions,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (!folderSessionResult.Success)
                {
                    return BurnOperationResult.Failed(
                        $"Session 1 audio completed, but session 2 data write failed: {folderSessionResult.ErrorMessage}",
                        folderSessionResult.Exception);
                }

                LastDataSessionStartLba = actualNwa;
                LastDataSessionSectors = folderSessionResult.SectorsWritten;
                return new BurnOperationResult
                {
                    Success = true,
                    Status = BurnStatus.Succeeded,
                    SectorsWritten = session1Result.SectorsWritten + folderSessionResult.SectorsWritten,
                    TotalTime = session1Result.TotalTime + folderSessionResult.TotalTime,
                    AverageSpeedMultiplier = session1Result.AverageSpeedMultiplier > 0
                        ? session1Result.AverageSpeedMultiplier
                        : folderSessionResult.AverageSpeedMultiplier,
                    WarningMessage = DataBurnService.JoinWarnings(folderSessionResult.Warning, AudioRecoverySessionService.CarHeadUnitWarning)
                };
            }

            progress?.Report(new BurnProgressSnapshot
            {
                CurrentActionDescription = "Building PAR2 recovery session...",
                CurrentLba = 0,
                TotalLba = 100,
                BufferLevelPercent = 100.0
            });

            var textProgress = progress == null
                ? null
                : new Progress<string>(msg => progress.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = msg,
                    CurrentLba = 0,
                    TotalLba = 100,
                    BufferLevelPercent = 100.0
                }));

            using var stagedIso = await AudioRecoverySessionService.BuildRecoverySessionIsoAsync(
                audioTrackSources,
                compilation,
                postAudioStatus.NextWritableLba,
                discCapacitySectors,
                par2Path!,
                textProgress,
                cancellationToken).ConfigureAwait(false);

            LastRecoveryManifest = stagedIso.Manifest;
            var session2Options = new DataBurnOptions
            {
                Speed = options.Speed,
                Simulate = false,
                CloseDisc = true,
                EjectWhenDone = options.EjectWhenDone,
                VerifyAfterBurn = false,
                VolumeLabel = AudioRecoveryManifest.VolumeLabel,
                DisableBufferUnderrunFree = options.DisableBufferUnderrunFree
            };

            var session2Result = await _recoveryDataEngine!.BurnDataStreamAsync(
                recorder,
                stagedIso.IsoStream,
                session2Options,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!session2Result.Success)
            {
                return BurnOperationResult.Failed(
                    $"Session 1 audio completed, but session 2 recovery write failed: {session2Result.ErrorMessage}",
                    session2Result.Exception);
            }

            return BurnOperationResult.Succeeded(
                session1Result.SectorsWritten + session2Result.SectorsWritten,
                session1Result.TotalTime + session2Result.TotalTime,
                session1Result.AverageSpeedMultiplier > 0 ? session1Result.AverageSpeedMultiplier : session2Result.AverageSpeedMultiplier);
        }
        finally
        {
            foreach (var trk in audioTrackSources)
            {
                try { trk.PcmStream?.Dispose(); } catch { }
            }
            compilation.ReleaseCachedAlbumBuffers();
        }
    }

    /// <summary>A Track-at-Once data track ends with 2 run-out blocks (Orange Book; the direct writer checks the same).</summary>
    public const int SessionTwoRunOutSectors = 2;

    /// <summary>
    /// Sectors a session starting at <paramref name="startLba"/> may use on a disc of <paramref name="capacitySectors"/>
    /// (the start of the last possible lead-out) with <paramref name="freeSectors"/> free as the drive reports it (0: not
    /// reported): up to the lead-out, less the track's run-out blocks.
    /// </summary>
    public static long SessionTwoSectorsAvailable(int startLba, long capacitySectors, long freeSectors)
    {
        long untilLeadOut = capacitySectors - startLba;
        if (freeSectors > 0)
        {
            untilLeadOut = Math.Min(untilLeadOut, freeSectors);
        }
        return Math.Max(0, untilLeadOut - SessionTwoRunOutSectors);
    }

    private async Task<BurnOperationResult> WriteSessionTwoImageAsync(
        IDiscRecorderBackend recorder,
        AudioBurnOptions options,
        BurnOperationResult session1Result,
        MediaStatusDto postAudioStatus,
        long discCapacitySectors,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        int nwa = postAudioStatus.NextWritableLba;
        long capacity = postAudioStatus.TotalSectors > 0 ? postAudioStatus.TotalSectors : discCapacitySectors;
        long available = SessionTwoSectorsAvailable(nwa, capacity, postAudioStatus.FreeSectors);
        const string audioDone = "Session 1 audio completed and plays, but the picture session was not written";

        Stream image;
        try
        {
            image = await options.SessionTwoImage!.BuildAsync(nwa, available, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BurnOperationResult.Failed($"{audioDone}: {ex.Message} The disc is left open.", ex);
        }

        await using (image.ConfigureAwait(false))
        {
            var session2Options = new DataBurnOptions
            {
                VolumeLabel = options.DataSessionVolumeLabel,
                Speed = options.Speed,
                Simulate = false,
                CloseDisc = true, // the session runs to the end of the disc: nothing could follow it
                EjectWhenDone = options.EjectWhenDone,
                VerifyAfterBurn = false, // the picture sectors are not files; the file system is checked by reading it
                DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
                CdRomXaMode2Form1 = false // the picture is drawn for Mode 1 sectors
            };
            var session2Result = await _recoveryDataEngine!.BurnDataStreamAsync(recorder, image, session2Options, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!session2Result.Success)
            {
                return BurnOperationResult.Failed($"{audioDone}: {session2Result.ErrorMessage}", session2Result.Exception);
            }

            LastDataSessionStartLba = nwa;
            LastDataSessionSectors = session2Result.SectorsWritten;
            return BurnOperationResult.Succeeded(
                session1Result.SectorsWritten + session2Result.SectorsWritten,
                session1Result.TotalTime + session2Result.TotalTime,
                session1Result.AverageSpeedMultiplier > 0 ? session1Result.AverageSpeedMultiplier : session2Result.AverageSpeedMultiplier);
        }
    }

    /// <summary>
    /// Overload for burning an Audio CD compilation.
    /// </summary>
    public Task<BurnOperationResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        BurnAsync(recorder, compilation, options, progress, cancellationToken);

    /// <summary>
    /// Built-in fallback audio decoder for standard RIFF WAVE (.wav) and lossless FLAC (.flac) files.
    /// </summary>
    private sealed class CoreWavAudioDecoder : IAudioDecoder
    {
        public AudioFormatMetadata ProbeAudioFile(string filePath) =>
            FlacFileCodec.IsFlacFile(filePath)
                ? FlacFileCodec.ProbeAudioFile(filePath)
                : WavFileCodec.ProbeAudioFile(filePath);

        public Stream OpenRedBookPcmStream(string filePath) =>
            FlacFileCodec.IsFlacFile(filePath)
                ? FlacFileCodec.OpenRedBookPcmStream(filePath)
                : WavFileCodec.OpenRedBookPcmStream(filePath);
    }
}
