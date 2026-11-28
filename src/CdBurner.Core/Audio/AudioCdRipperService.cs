using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Audio CD extraction layout mode.
/// </summary>
public enum AudioRipMode
{
    /// <summary>Extract each track to its own audio file alongside a multi-FILE CUE sheet.</summary>
    TrackByTrack = 0,

    /// <summary>Extract the entire Audio CD (including optional HTOA and pre-gaps) into one image file plus an exact single-FILE CUE sheet.</summary>
    ImageCue = 1
}

/// <summary>
/// Configuration options for ripping Audio CD tracks to uncompressed WAV files or a single-file image and companion CUE sheet.
/// </summary>
public class AudioRipOptions
{
    /// <summary>
    /// Maximum read retry attempts per sector on unreadable audio sectors. Default is 3.
    /// </summary>
    public int MaxRetriesPerSector { get; set; } = 3;

    /// <summary>
    /// When true, unreadable sectors that fail after all retries are zero-filled instead of aborting the operation.
    /// </summary>
    public bool ContinueOnUnreadableSector { get; set; } = false;

    /// <summary>
    /// Whether to generate a companion .cue sheet describing the ripped audio files and track layout. Default is true.
    /// </summary>
    public bool GenerateCueSheet { get; set; } = true;

    /// <summary>
    /// Filename of the generated .cue sheet in the output directory. Default is "album.cue".
    /// </summary>
    public string CueSheetFileName { get; set; } = "album.cue";

    /// <summary>
    /// Optional subset of 1-based track numbers to rip. If null or empty, all tracks are ripped.
    /// </summary>
    public IReadOnlyList<int>? TrackNumbersToRip { get; set; }

    /// <summary>
    /// Optional metadata (e.g. from an online lookup) that takes precedence over CD-Text for file names, the CUE
    /// sheet and <see cref="AudioCdRipResult.CdText"/>. Tracks it does not cover keep their CD-Text or generic names.
    /// </summary>
    public CdTextAlbumMetadata? MetadataOverride { get; set; }

    private int _readOffsetSamples;
    private int? _explicitReadOffsetSamples;

    /// <summary>
    /// Drive read offset correction in samples (one sample = 4 bytes of 16-bit stereo), as listed in the AccurateRip
    /// drive offset table (e.g. +6). Audio is taken from that many samples later on the disc; samples before the start
    /// or past the end of the audio area are filled with silence. Setting this property sets
    /// <see cref="ExplicitReadOffsetSamples"/> as an explicit override.
    /// </summary>
    public int ReadOffsetSamples
    {
        get => _readOffsetSamples;
        set
        {
            _readOffsetSamples = value;
            _explicitReadOffsetSamples = value;
        }
    }

    /// <summary>
    /// Optional explicit user override for drive read offset in samples. When null and
    /// <see cref="AutoResolveReadOffset"/> is true, the ripper resolves the offset with precedence:
    /// explicit override &gt; local key-disc calibration &gt; cached AccurateRip DriveOffsets.bin &gt; 0.
    /// </summary>
    public int? ExplicitReadOffsetSamples
    {
        get => _explicitReadOffsetSamples;
        set
        {
            _explicitReadOffsetSamples = value;
            if (value.HasValue)
            {
                _readOffsetSamples = value.Value;
            }
        }
    }

    /// <summary>When true (default) and <see cref="ExplicitReadOffsetSamples"/> is null, resolves the drive read offset automatically.</summary>
    public bool AutoResolveReadOffset { get; set; } = true;

    /// <summary>Optional path override for <see cref="DriveOffsetStore"/> (defaults to local app data on physical drives).</summary>
    public string? DriveOffsetStorePath { get; set; }

    /// <summary>Optional path override for cached <c>DriveOffsets.bin</c> (defaults to local app data on physical drives).</summary>
    public string? DriveOffsetsCachePath { get; set; }

    /// <summary>Optional <see cref="HttpClient"/> used when <c>DriveOffsets.bin</c> needs to be downloaded or refreshed.</summary>
    public HttpClient? OffsetDatabaseHttpClient { get; set; }

    /// <summary>When true, refreshes <c>DriveOffsets.bin</c> before resolving the read offset.</summary>
    public bool ForceRefreshOffsetDatabase { get; set; }

    /// <summary>
    /// Secure extraction: read with C2 error pointers and re-read flagged sectors after flushing the drive cache
    /// (up to <see cref="MaxRetriesPerSector"/> rounds). Ignored, with <see cref="AudioCdRipResult.SecureModeUsed"/>
    /// false, when the engine or drive does not support C2 error pointers.
    /// </summary>
    public bool SecureMode { get; set; }

    /// <summary>
    /// Whether to calculate track and album ReplayGain tags during ripping. Default is true.
    /// </summary>
    public bool CalculateReplayGain { get; init; } = true;

    /// <summary>
    /// Optional front cover art image bytes to embed into ripped FLAC files.
    /// </summary>
    public byte[]? CoverArtBytes { get; init; }

    /// <summary>
    /// Optional MIME type of the cover art image (e.g. "image/jpeg", "image/png"). Default is "image/jpeg".
    /// </summary>
    public string? CoverArtMimeType { get; init; }

    /// <summary>
    /// When true and the disc carries a session-2 PAR2 recovery volume (Enhanced CD / CD-Extra), verifies each
    /// ripped track against session 2 and rebuilds any damaged samples from PAR2.
    /// </summary>
    public bool RepairFromRecoverySession { get; set; } = false;

    /// <summary>
    /// When true, applies the Red Book 50/15 us digital de-emphasis filter to tracks flagged with pre-emphasis.
    /// AccurateRip and CTDB checksums are still computed on the raw disc PCM before de-emphasis.
    /// </summary>
    public bool ApplyDeEmphasis { get; set; } = false;

    /// <summary>
    /// Extraction layout mode: <see cref="AudioRipMode.TrackByTrack"/> (default) or <see cref="AudioRipMode.ImageCue"/>.
    /// </summary>
    public AudioRipMode Mode { get; set; } = AudioRipMode.TrackByTrack;

    /// <summary>
    /// Target audio format when <see cref="Mode"/> is <see cref="AudioRipMode.ImageCue"/> (<see cref="AudioRipFormat.Wav"/> or <see cref="AudioRipFormat.Flac"/>).
    /// </summary>
    public AudioRipFormat ImageFormat { get; set; } = AudioRipFormat.Wav;

    /// <summary>
    /// Optional base file name (without or with extension) for single-file <see cref="AudioRipMode.ImageCue"/> output.
    /// Defaults to the sanitized album title or <c>"album"</c>.
    /// </summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// Optional path to <c>ffmpeg.exe</c> used when encoding a single-file FLAC image with compression.
    /// When omitted, IgnisDisc uses its built-in lossless RFC 9639 FLAC encoder.
    /// </summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// Whether <see cref="AudioCdRipperService"/> writes an EAC-style extraction log (<c>&lt;Album&gt;.log</c>) in the output directory.
    /// </summary>
    public bool WriteRipLog { get; set; } = true;

    /// <summary>
    /// Optional override for the extraction log file name. Defaults to the sanitized album title (or image stem) + <c>.log</c>.
    /// </summary>
    public string? RipLogFileName { get; set; }
}

/// <summary>
/// Per-track outcome of an audio rip, including the checksums needed for AccurateRip verification.
/// </summary>
/// <param name="TrackNumber">Disc track number.</param>
/// <param name="FilePath">Full path of the written WAV file.</param>
/// <param name="Crc32">CRC-32 of the track's PCM data (excluding the WAV header).</param>
/// <param name="AccurateRipV1">AccurateRip v1 checksum.</param>
/// <param name="AccurateRipV2">AccurateRip v2 checksum.</param>
/// <param name="AudioTrackIndex">0-based position of the track among all audio tracks on the disc (AccurateRip entry index).</param>
/// <param name="UnreadableSectors">Number of sectors that failed every retry and were replaced by silence.</param>
/// <param name="ReplayGainTrackGain">Calculated track ReplayGain in dB, if requested.</param>
/// <param name="ReplayGainTrackPeak">Calculated track peak amplitude, if requested.</param>
/// <param name="CtdbTrackCrc">CUETools Database (CTDB) track CRC-32.</param>
public sealed record AudioTrackRipInfo(
    int TrackNumber,
    string FilePath,
    uint Crc32,
    uint AccurateRipV1,
    uint AccurateRipV2,
    int AudioTrackIndex,
    int UnreadableSectors,
    int RecoveredSectors = 0,
    int SuspectSectors = 0,
    string Title = "",
    string Performer = "",
    string? Isrc = null,
    double? ReplayGainTrackGain = null,
    double? ReplayGainTrackPeak = null,
    uint CtdbTrackCrc = 0,
    bool PreEmphasis = false,
    bool DeEmphasisApplied = false,
    int StartLba = 0,
    int SectorCount = 0,
    int PreGapFrames = 0,
    bool CopyPermitted = false,
    bool FourChannel = false,
    double? PeakLevelPercent = null);

/// <summary>
/// Discovered audio track descriptor extracted from optical medium TOC or session metadata.
/// </summary>
public record AudioDiscoveredTrack(
    int TrackNumber,
    int StartLba,
    int SectorCount,
    int PreGapFrames,
    string Title,
    string Performer,
    string? Songwriter = null,
    string? Composer = null,
    string? Isrc = null,
    bool PreEmphasis = false,
    bool CopyPermitted = false,
    bool FourChannel = false);

/// <summary>
/// Final result of an Audio CD ripping operation.
/// </summary>
public class AudioCdRipResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int TracksRipped { get; init; }
    public int TotalSectorsRead { get; init; }
    public TimeSpan TotalTime { get; init; }
    public IReadOnlyList<string> RippedFilePaths { get; init; } = Array.Empty<string>();
    public string? HiddenTrackOneAudioPath { get; init; }
    public string? CueSheetPath { get; init; }
    public CdTextAlbumMetadata? CdText { get; init; }
    public Exception? Exception { get; init; }

    /// <summary>Per-track checksums and defect counts, in rip order.</summary>
    public IReadOnlyList<AudioTrackRipInfo> TrackInfos { get; init; } = Array.Empty<AudioTrackRipInfo>();

    /// <summary>LBAs that failed every retry (zero-filled when continuing past unreadable sectors).</summary>
    public IReadOnlyList<int> BadSectorLbas { get; init; } = Array.Empty<int>();

    /// <summary>True if the rip used C2-based secure extraction (requested and supported by the drive).</summary>
    public bool SecureModeUsed { get; init; }

    /// <summary>Calculated album ReplayGain in dB, if requested.</summary>
    public double? ReplayGainAlbumGain { get; init; }

    /// <summary>Calculated album peak amplitude (0.0 to 1.0), if requested.</summary>
    public double? ReplayGainAlbumPeak { get; init; }

    /// <summary>Result of verifying and repairing tracks from session 2 PAR2 data, when requested.</summary>
    public AudioRecoveryRepairResult? RecoveryRepair { get; init; }

    /// <summary>Whole-disc CUETools Database (CTDB) CRC-32 when all audio tracks were ripped, otherwise null.</summary>
    public uint? CtdbDiscCrc { get; init; }

    /// <summary>How the drive read offset was resolved for this rip.</summary>
    public ReadOffsetResolution? ReadOffset { get; init; }

    /// <summary>Resolved read offset in samples applied during ripping.</summary>
    public int ReadOffsetSamples => ReadOffset?.OffsetSamples ?? 0;

    /// <summary>Extraction mode used for this rip (<see cref="AudioRipMode.TrackByTrack"/> or <see cref="AudioRipMode.ImageCue"/>).</summary>
    public AudioRipMode Mode { get; init; } = AudioRipMode.TrackByTrack;

    /// <summary>Full path to the single-file disc image when <see cref="Mode"/> is <see cref="AudioRipMode.ImageCue"/>.</summary>
    public string? ImageFilePath { get; init; }

    /// <summary>Length of Hidden Track One Audio (HTOA) in CD frames when present and ripped.</summary>
    public int HiddenTrackOneAudioFrames { get; init; }

    /// <summary>Full path to the written EAC-style extraction log (<c>.log</c>), if generated.</summary>
    public string? RipLogPath { get; init; }

    /// <summary>Full disc TOC entries recorded during ripping (audio and any data tracks).</summary>
    public IReadOnlyList<DiscTocTrack> DiscTocTracks { get; init; } = Array.Empty<DiscTocTrack>();

    /// <summary>Media Catalog Number (UPC/EAN) discovered from subcode or CD-Text.</summary>
    public string? MediaCatalogNumber { get; init; }

    /// <summary>True if Q sub-channel gap detection ran during this rip.</summary>
    public bool GapDetectionPerformed { get; init; }

    /// <summary>True if read offset correction overread into lead-in/lead-out boundaries.</summary>
    public bool OverreadIntoLeadInOut { get; init; }

    /// <summary>True if secure mode (C2 error pointers) was requested.</summary>
    public bool SecureModeRequested { get; init; }

    /// <summary>Recorder unique identifier.</summary>
    public string? DriveUniqueId { get; init; }

    /// <summary>Drive INQUIRY vendor identifier.</summary>
    public string? DriveVendorId { get; init; }

    /// <summary>Drive INQUIRY product identifier.</summary>
    public string? DriveProductId { get; init; }

    /// <summary>Drive INQUIRY firmware revision.</summary>
    public string? DriveProductRevision { get; init; }

    public static AudioCdRipResult Succeeded(
        IReadOnlyList<string> filePaths,
        int totalSectors,
        TimeSpan duration,
        string? cueSheetPath = null,
        CdTextAlbumMetadata? cdText = null,
        IReadOnlyList<AudioTrackRipInfo>? trackInfos = null,
        IReadOnlyList<int>? badSectorLbas = null,
        bool secureModeUsed = false,
        double? replayGainAlbumGain = null,
        double? replayGainAlbumPeak = null,
        AudioRecoveryRepairResult? recoveryRepair = null,
        string? hiddenTrackOneAudioPath = null,
        uint? ctdbDiscCrc = null,
        ReadOffsetResolution? readOffset = null,
        AudioRipMode mode = AudioRipMode.TrackByTrack,
        string? imageFilePath = null,
        int hiddenTrackOneAudioFrames = 0,
        string? ripLogPath = null,
        IReadOnlyList<DiscTocTrack>? discTocTracks = null,
        string? mediaCatalogNumber = null,
        bool gapDetectionPerformed = false,
        bool overreadIntoLeadInOut = false,
        bool secureModeRequested = false,
        string? driveUniqueId = null,
        string? driveVendorId = null,
        string? driveProductId = null,
        string? driveProductRevision = null) => new()
    {
        SecureModeUsed = secureModeUsed,
        Success = true,
        TracksRipped = trackInfos is { Count: > 0 } ? trackInfos.Count : filePaths.Count,
        TotalSectorsRead = totalSectors,
        TotalTime = duration,
        RippedFilePaths = filePaths,
        HiddenTrackOneAudioPath = hiddenTrackOneAudioPath,
        CueSheetPath = cueSheetPath,
        CdText = cdText,
        TrackInfos = trackInfos ?? Array.Empty<AudioTrackRipInfo>(),
        BadSectorLbas = badSectorLbas ?? Array.Empty<int>(),
        ReplayGainAlbumGain = replayGainAlbumGain,
        ReplayGainAlbumPeak = replayGainAlbumPeak,
        RecoveryRepair = recoveryRepair,
        CtdbDiscCrc = ctdbDiscCrc,
        ReadOffset = readOffset,
        Mode = mode,
        ImageFilePath = imageFilePath,
        HiddenTrackOneAudioFrames = hiddenTrackOneAudioFrames,
        RipLogPath = ripLogPath,
        DiscTocTracks = discTocTracks ?? Array.Empty<DiscTocTrack>(),
        MediaCatalogNumber = mediaCatalogNumber,
        GapDetectionPerformed = gapDetectionPerformed,
        OverreadIntoLeadInOut = overreadIntoLeadInOut,
        SecureModeRequested = secureModeRequested,
        DriveUniqueId = driveUniqueId,
        DriveVendorId = driveVendorId,
        DriveProductId = driveProductId,
        DriveProductRevision = driveProductRevision
    };

    public static AudioCdRipResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        Exception = ex
    };
}

/// <summary>
/// Digital Audio Extraction (DAE) ripper service extracting Red Book 2352-byte raw sectors
/// into canonical 44.1 kHz 16-bit Stereo RIFF WAVE (.wav) files with decoded CD-Text metadata and companion CUE sheet.
/// </summary>
public class AudioCdRipperService
{
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Sanitizes a string for use in Windows file paths, replacing illegal characters with underscores.
    /// </summary>
    public static string SanitizeFileName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(invalidChars.Contains(c) ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..")
        {
            return "Track";
        }

        string baseName = Path.GetFileNameWithoutExtension(result);
        if (ReservedWindowsDeviceNames.Contains(result) || ReservedWindowsDeviceNames.Contains(baseName))
        {
            result = "_" + result;
        }

        if (result.Length > 100)
        {
            result = result[..100].TrimEnd('.', ' ');
        }

        return string.IsNullOrEmpty(result) ? "Track" : result;
    }

    /// <summary>
    /// Inspects the inserted optical medium for session/track layout and decodes any recorded CD-Text packs.
    /// </summary>
    public static (IReadOnlyList<AudioDiscoveredTrack> Tracks, CdTextAlbumMetadata? CdText) QueryTracksAndCdText(
        IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        CdTextAlbumMetadata? cdText = null;
        var tracks = new List<AudioDiscoveredTrack>();

        // Physical drives: read the real Table of Contents and any CD-Text in the lead-in
        if (recorder is IDiscTocProvider tocProvider)
        {
            var toc = tocProvider.ReadTableOfContents();
            if (toc.Any(t => t.IsAudio))
            {
                cdText = TryParseCdText(tocProvider.ReadCdTextPacks());
            }

            foreach (var t in toc.Where(t => t.IsAudio))
            {
                var (title, performer, songwriter, composer, isrc) = MergeCdText(t.TrackNumber, null, null, null, null, null, cdText);
                tracks.Add(new AudioDiscoveredTrack(
                    t.TrackNumber,
                    t.StartLba,
                    t.SectorCount,
                    t.TrackNumber == 1 ? OpticalConstants.CdLeadInFrames : 0,
                    title,
                    performer,
                    songwriter,
                    composer,
                    isrc,
                    t.PreEmphasis,
                    t.CopyPermitted,
                    t.FourChannel));
            }

            if (tracks.Count > 0)
            {
                return (tracks, cdText);
            }
        }

        // Check if recorder exposes CurrentMedium (VirtualDiscRecorder or via reflection)
        object? mediumObj = null;
        var currentMediumProp = recorder.GetType().GetProperty("CurrentMedium");
        if (currentMediumProp != null)
        {
            mediumObj = currentMediumProp.GetValue(recorder);
        }

        if (mediumObj != null)
        {
            // Read optional CdTextBytes
            var cdTextBytesProp = mediumObj.GetType().GetProperty("CdTextBytes");
            cdText = TryParseCdText(cdTextBytesProp?.GetValue(mediumObj) as byte[]);

            // Read Sessions and Tracks
            var sessionsProp = mediumObj.GetType().GetProperty("Sessions");
            if (sessionsProp?.GetValue(mediumObj) is System.Collections.IEnumerable sessionsList)
            {
                foreach (var session in sessionsList)
                {
                    var tracksProp = session.GetType().GetProperty("Tracks");
                    if (tracksProp?.GetValue(session) is System.Collections.IEnumerable trkList)
                    {
                        foreach (var t in trkList)
                        {
                            var tType = t.GetType();
                            if (tType.GetProperty("IsAudio")?.GetValue(t) is false)
                            {
                                continue; // a data track (mixed-mode CD) is not audio to rip
                            }
                            int trkNum = tType.GetProperty("TrackNumber")?.GetValue(t) is int tn ? tn : (tracks.Count + 1);
                            int startLba = tType.GetProperty("StartLba")?.GetValue(t) is int sl ? sl : 0;
                            int count = tType.GetProperty("SectorCount")?.GetValue(t) is int sc ? sc : 0;
                            int preGap = tType.GetProperty("PreGapFrames")?.GetValue(t) is int pg ? pg : 0;
                            string? title = tType.GetProperty("Title")?.GetValue(t) as string;
                            string? performer = tType.GetProperty("Performer")?.GetValue(t) as string;
                            string? songwriter = tType.GetProperty("Songwriter")?.GetValue(t) as string;
                            string? composer = tType.GetProperty("Composer")?.GetValue(t) as string;
                            string? isrc = tType.GetProperty("Isrc")?.GetValue(t) as string;
                            bool preEmphasis = tType.GetProperty("PreEmphasis")?.GetValue(t) is bool pe && pe;
                            bool copyPermitted = tType.GetProperty("CopyPermitted")?.GetValue(t) is bool cp && cp;
                            bool fourChannel = tType.GetProperty("FourChannel")?.GetValue(t) is bool fc && fc;

                            var merged = MergeCdText(trkNum, title, performer, songwriter, composer, isrc, cdText);
                            tracks.Add(new AudioDiscoveredTrack(
                                trkNum,
                                startLba,
                                count,
                                preGap,
                                merged.Title,
                                merged.Performer,
                                merged.Songwriter,
                                merged.Composer,
                                merged.Isrc,
                                preEmphasis,
                                copyPermitted,
                                fourChannel));
                        }
                    }
                }
            }
        }

        // Fallback: If no session metadata was found but medium has recorded sectors
        if (tracks.Count == 0)
        {
            var mediaStatus = recorder.QueryMediaStatus();
            if (mediaStatus.UsedSectors > 0)
            {
                tracks.Add(new AudioDiscoveredTrack(
                    1,
                    0,
                    mediaStatus.UsedSectors,
                    OpticalConstants.CdLeadInFrames,
                    "Track 01",
                    cdText?.AlbumPerformer ?? "Unknown Artist"));
            }
        }

        return (tracks, cdText);
    }

    private static CdTextAlbumMetadata? TryParseCdText(byte[]? packs)
    {
        if (packs == null || packs.Length < CdTextPack.PackSizeBytes)
        {
            return null;
        }

        try
        {
            return CdTextPackParser.ParsePacks(packs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            // Malformed or CRC-damaged CD-Text: fall back to generic track names.
            return null;
        }
    }

    /// <summary>
    /// Overlays decoded CD-Text on top of the given per-track fields and applies the generic fallbacks
    /// ("Track NN", album performer, "Unknown Artist").
    /// </summary>
    private static (string Title, string Performer, string? Songwriter, string? Composer, string? Isrc) MergeCdText(
        int trackNumber,
        string? title,
        string? performer,
        string? songwriter,
        string? composer,
        string? isrc,
        CdTextAlbumMetadata? cdText)
    {
        var cdt = cdText?.Tracks?.FirstOrDefault(x => x.TrackNumber == trackNumber);
        if (cdt != null)
        {
            if (!string.IsNullOrEmpty(cdt.Title)) title = cdt.Title;
            if (!string.IsNullOrEmpty(cdt.Performer)) performer = cdt.Performer;
            if (!string.IsNullOrEmpty(cdt.Songwriter)) songwriter = cdt.Songwriter;
            if (!string.IsNullOrEmpty(cdt.Composer)) composer = cdt.Composer;
            if (!string.IsNullOrEmpty(cdt.Isrc)) isrc = cdt.Isrc;
        }

        if (string.IsNullOrWhiteSpace(performer) && !string.IsNullOrWhiteSpace(cdText?.AlbumPerformer))
        {
            performer = cdText.AlbumPerformer;
        }

        return (
            string.IsNullOrWhiteSpace(title) ? $"Track {trackNumber:D2}" : title,
            string.IsNullOrWhiteSpace(performer) ? "Unknown Artist" : performer,
            songwriter,
            composer,
            isrc);
    }

    /// <summary>
    /// Rips audio tracks from an inserted optical disc into individual .wav files wrapped with standard 44-byte headers
    /// and optionally generates a companion .cue sheet.
    /// </summary>
    public async Task<AudioCdRipResult> RipAudioCdAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string outputDirectory,
        AudioRipOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new AudioRipOptions();

        cancellationToken.ThrowIfCancellationRequested();

        if (options.Mode == AudioRipMode.ImageCue)
        {
            if (options.TrackNumbersToRip is { Count: > 0 })
            {
                return AudioCdRipResult.Failed("Single-file Image + CUE mode requires ripping the full Audio CD and cannot be combined with a track subset.");
            }
            if (options.ImageFormat is not (AudioRipFormat.Wav or AudioRipFormat.Flac))
            {
                return AudioCdRipResult.Failed($"Single-file Image + CUE mode supports WAV or FLAC output, not {AudioRipFormats.DisplayName(options.ImageFormat)}.");
            }
        }

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return AudioCdRipResult.Failed("No optical media present in recorder.");
        }

        ReadOffsetResolution offsetResolution;
        if (options.ExplicitReadOffsetSamples is not null || options.AutoResolveReadOffset)
        {
            offsetResolution = await ReadOffsetResolver.ResolveAsync(
                recorder,
                options.ExplicitReadOffsetSamples,
                options.DriveOffsetStorePath,
                options.DriveOffsetsCachePath,
                options.OffsetDatabaseHttpClient,
                options.ForceRefreshOffsetDatabase,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            offsetResolution = new ReadOffsetResolution(
                0,
                ReadOffsetSource.DefaultZero,
                "0 samples (uncalibrated)",
                "default 0 (uncalibrated)");
        }

        options = new AudioRipOptions
        {
            MaxRetriesPerSector = options.MaxRetriesPerSector,
            ContinueOnUnreadableSector = options.ContinueOnUnreadableSector,
            GenerateCueSheet = options.GenerateCueSheet,
            CueSheetFileName = options.CueSheetFileName,
            TrackNumbersToRip = options.TrackNumbersToRip,
            MetadataOverride = options.MetadataOverride,
            ReadOffsetSamples = offsetResolution.OffsetSamples,
            SecureMode = options.SecureMode,
            CalculateReplayGain = options.CalculateReplayGain,
            CoverArtBytes = options.CoverArtBytes,
            CoverArtMimeType = options.CoverArtMimeType,
            RepairFromRecoverySession = options.RepairFromRecoverySession,
            ApplyDeEmphasis = options.ApplyDeEmphasis,
            Mode = options.Mode,
            ImageFormat = options.ImageFormat,
            ImageFileName = options.ImageFileName,
            FfmpegPath = options.FfmpegPath,
            WriteRipLog = options.WriteRipLog,
            RipLogFileName = options.RipLogFileName
        };

        Directory.CreateDirectory(outputDirectory);

        IReadOnlyList<DiscTocTrack> discTocTracks = recorder is IDiscTocProvider tocProvider
            ? tocProvider.ReadTableOfContents()
            : Array.Empty<DiscTocTrack>();

        var (allTracks, cdText) = QueryTracksAndCdText(recorder);
        if (discTocTracks.Count == 0 && allTracks.Count > 0)
        {
            discTocTracks = allTracks
                .Select(t => new DiscTocTrack(t.TrackNumber, t.StartLba, t.SectorCount, true, t.PreEmphasis, t.CopyPermitted, t.FourChannel))
                .ToList();
        }
        if (options.MetadataOverride is { } metadataOverride)
        {
            allTracks = allTracks.Select(t =>
            {
                // The generic placeholder must not block the override's album performer.
                string? performer = t.Performer == "Unknown Artist" ? null : t.Performer;
                var m = MergeCdText(t.TrackNumber, t.Title, performer, t.Songwriter, t.Composer, t.Isrc, metadataOverride);
                return t with { Title = m.Title, Performer = m.Performer, Songwriter = m.Songwriter, Composer = m.Composer, Isrc = m.Isrc };
            }).ToList();
            cdText = metadataOverride;
        }
        if (allTracks.Count == 0)
        {
            return AudioCdRipResult.Failed("No recorded audio tracks found on media.");
        }

        var tracksToRip = options.TrackNumbersToRip != null && options.TrackNumbersToRip.Count > 0
            ? allTracks.Where(t => options.TrackNumbersToRip.Contains(t.TrackNumber)).ToList()
            : allTracks.ToList();

        if (tracksToRip.Count == 0)
        {
            return AudioCdRipResult.Failed("None of the specified track numbers were found on the disc.");
        }

        var sw = Stopwatch.StartNew();
        var rippedFilePaths = new List<string>(tracksToRip.Count + 1);
        var wavNameByTrackNumber = new Dictionary<int, string>(tracksToRip.Count);
        var trackInfos = new List<AudioTrackRipInfo>(tracksToRip.Count);
        var replayGainResults = new List<ReplayGainResult>(tracksToRip.Count);
        var badSectorLbas = new List<int>();
        int totalSectorsRead = 0;

        // AccurateRip edge rules and offset correction are relative to the whole audio area, not the tracks selected.
        var audioOrder = allTracks.OrderBy(t => t.StartLba).ToList();

        DiscSubcodeMap subcodeMap;
        using (var subSession = (recorder as IAudioSubChannelProvider)?.TryOpenSubChannelSession())
        {
            subcodeMap = AudioGapDetector.Detect(
                audioOrder,
                subSession,
                bcd =>
                {
                    if (subSession != null)
                    {
                        subSession.UseBcdInReadCdQ = bcd;
                    }
                },
                cancellationToken);
        }

        AudioDiscoveredTrack ApplySubcode(AudioDiscoveredTrack t) =>
            subcodeMap.Tracks.TryGetValue(t.TrackNumber, out var det)
                ? t with
                {
                    Isrc = !string.IsNullOrWhiteSpace(t.Isrc) ? t.Isrc : det.Isrc,
                    PreEmphasis = t.PreEmphasis || det.PreEmphasis,
                    CopyPermitted = t.CopyPermitted || det.CopyPermitted,
                    FourChannel = t.FourChannel || det.FourChannel
                }
                : t;

        allTracks = allTracks.Select(ApplySubcode).ToList();
        tracksToRip = tracksToRip.Select(ApplySubcode).OrderBy(t => t.StartLba).ToList();
        audioOrder = allTracks.OrderBy(t => t.StartLba).ToList();
        string? discCatalog = subcodeMap.MediaCatalogNumber ?? AudioGapDetector.NormalizeMcn(cdText?.UpcEan);

        // CTDB sample windows are computed over the full disc audio area (excluding HTOA before Track 1 INDEX 01).
        long totalDiscAudioSamples = 0;
        var trackStartSamples = new long[audioOrder.Count];
        for (int i = 0; i < audioOrder.Count; i++)
        {
            trackStartSamples[i] = totalDiscAudioSamples;
            totalDiscAudioSamples += (long)audioOrder[i].SectorCount * CtdbChecksum.SamplesPerSector;
        }

        int audioEndLba = audioOrder.Max(t => t.StartLba + t.SectorCount);
        long totalSectorsToRip = tracksToRip.Sum(t => (long)t.SectorCount);
        byte[] silence = new byte[OpticalConstants.RawSectorSize];

        ISecureAudioReader? secureReader =
            options.SecureMode && engine is ISecureAudioReader candidate && candidate.SupportsC2ErrorPointers(recorder, audioOrder[0].StartLba)
                ? candidate
                : null;
        bool hasRecoverySession = options.RepairFromRecoverySession
            && AudioRecoverySessionService.FindRecoverySessionLocation(recorder) != null;
        bool continueOnUnreadable = options.ContinueOnUnreadableSector || hasRecoverySession;

        if (options.Mode == AudioRipMode.ImageCue)
        {
            return await RipImageCueAsync(
                recorder,
                engine,
                outputDirectory,
                options,
                offsetResolution,
                cdText,
                audioOrder,
                subcodeMap,
                discCatalog,
                discTocTracks,
                secureReader,
                continueOnUnreadable,
                silence,
                badSectorLbas,
                trackStartSamples,
                totalDiscAudioSamples,
                audioEndLba,
                sw,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        string? hiddenTrackOneAudioPath = null;
        int hiddenTrackOneAudioFrames = 0;
        int track1SilentPreGapFrames = 0;
        string? cueSheetPath = null;
        string? ripLogPath = null;
        try
        {
        if (audioOrder[0].TrackNumber == 1 &&
            audioOrder[0].StartLba > 0 &&
            tracksToRip.Any(t => t.TrackNumber == 1))
        {
            int htoaSectors = audioOrder[0].StartLba;
            string artistForHtoa = !string.IsNullOrWhiteSpace(cdText?.AlbumPerformer)
                ? cdText.AlbumPerformer
                : audioOrder[0].Performer;
            string safeHtoaPerformer = SanitizeFileName(artistForHtoa);
            if (string.IsNullOrEmpty(safeHtoaPerformer)) safeHtoaPerformer = "Unknown Artist";
            string htoaWavPath = Path.Combine(outputDirectory, $"00 - {safeHtoaPerformer} - Hidden Track One Audio.wav");

            bool hasNonSilentAudio = false;
            RangeExtractOutcome htoaExtract;
            try
            {
                htoaExtract = await ExtractSectorRangeToWavAsync(
                    recorder,
                    engine,
                    secureReader,
                    htoaWavPath,
                    startLba: 0,
                    sectorCount: htoaSectors,
                    audioEndLba,
                    options,
                    continueOnUnreadable,
                    silence,
                    badSectorLbas,
                    progress: null,
                    onChunk: slice =>
                    {
                        if (!hasNonSilentAudio && slice.IndexOfAnyExcept((byte)0) >= 0)
                        {
                            hasNonSilentAudio = true;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteFile(htoaWavPath);
                throw;
            }

            if (!htoaExtract.Completed)
            {
                TryDeleteFile(htoaWavPath);
                return AudioCdRipResult.Failed(
                    $"Failed to rip hidden track one audio: unrecovered read error at LBA {htoaExtract.StoppedAtLba} after {options.MaxRetriesPerSector} retries.",
                    new IOException($"Unrecovered read error at LBA {htoaExtract.StoppedAtLba}."));
            }

            if (hasNonSilentAudio)
            {
                rippedFilePaths.Add(htoaWavPath);
                hiddenTrackOneAudioPath = htoaWavPath;
                hiddenTrackOneAudioFrames = htoaSectors;
                totalSectorsRead += htoaSectors;
            }
            else
            {
                TryDeleteFile(htoaWavPath);
                track1SilentPreGapFrames = htoaSectors;
            }
        }

        foreach (var track in tracksToRip)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string safePerformer = SanitizeFileName(track.Performer);
            if (string.IsNullOrEmpty(safePerformer)) safePerformer = "Unknown Artist";

            string safeTitle = SanitizeFileName(track.Title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = $"Track {track.TrackNumber:D2}";

            string fileName = $"{track.TrackNumber:D2} - {safePerformer} - {safeTitle}.wav";
            string targetWavPath = Path.Combine(outputDirectory, fileName);

            int audioIndex = audioOrder.IndexOf(track);
            long trackSamples = (long)track.SectorCount * CtdbChecksum.SamplesPerSector;
            var arChecksum = new AccurateRipChecksum(
                (long)track.SectorCount * OpticalConstants.RawSectorSize / 4,
                isFirstTrack: audioIndex == 0,
                isLastTrack: audioIndex == audioOrder.Count - 1);
            var ctdbChecksum = CtdbChecksum.ForTrack(trackStartSamples[audioIndex], trackSamples, totalDiscAudioSamples);
            var crc32 = new Crc32Algorithm();
            int maxAbsSample = 0;

            long sectorsBeforeTrack = totalSectorsRead - (hiddenTrackOneAudioPath != null ? audioOrder[0].StartLba : 0);
            RangeExtractOutcome trackExtract;
            bool deEmphasisApplied = false;
            try
            {
                trackExtract = await ExtractSectorRangeToWavAsync(
                    recorder,
                    engine,
                    secureReader,
                    targetWavPath,
                    track.StartLba,
                    track.SectorCount,
                    audioEndLba,
                    options,
                    continueOnUnreadable,
                    silence,
                    badSectorLbas,
                    done => progress?.Report(new BurnProgressSnapshot
                    {
                        ElapsedTime = sw.Elapsed,
                        CurrentLba = (int)Math.Min(int.MaxValue, sectorsBeforeTrack + done),
                        TotalLba = (int)Math.Min(int.MaxValue, totalSectorsToRip),
                        BufferLevelPercent = 100.0,
                        CurrentActionDescription = $"RippingTrack{track.TrackNumber:D2}"
                    }),
                    onChunk: slice =>
                    {
                        arChecksum.Append(slice);
                        ctdbChecksum.Append(slice);
                        crc32.Append(slice);
                        maxAbsSample = MeasureMaxAbsSample(slice, maxAbsSample);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!trackExtract.Completed)
                {
                    TryDeleteFile(targetWavPath);

                    return AudioCdRipResult.Failed(
                        $"Failed to rip track {track.TrackNumber} ('{track.Title}'): unrecovered read error at LBA {trackExtract.StoppedAtLba} after {options.MaxRetriesPerSector} retries.",
                        new IOException($"Unrecovered read error at LBA {trackExtract.StoppedAtLba}."));
                }

                if (options.ApplyDeEmphasis && track.PreEmphasis && !hasRecoverySession && File.Exists(targetWavPath))
                {
                    ApplyDeEmphasisInPlace(targetWavPath);
                    deEmphasisApplied = true;
                }
            }
            catch
            {
                TryDeleteFile(targetWavPath);
                throw;
            }

            rippedFilePaths.Add(targetWavPath);
            wavNameByTrackNumber[track.TrackNumber] = fileName;

            double? trackGain = null;
            double? trackPeak = maxAbsSample / 32768.0;
            if (options.CalculateReplayGain && File.Exists(targetWavPath))
            {
                try
                {
                    using var s = new FileStream(targetWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var rg = ReplayGainCalculator.CalculateTrackReplayGain(s);
                    trackGain = rg.GainDb;
                    trackPeak = rg.PeakAmplitude;
                    replayGainResults.Add(rg);
                }
                catch
                {
                }
            }

            int preGapFrames = ResolveTrackPreGapFrames(track, subcodeMap, track1SilentPreGapFrames, hiddenTrackOneAudioFrames);
            trackInfos.Add(new AudioTrackRipInfo(
                track.TrackNumber,
                targetWavPath,
                crc32.GetCurrentHashAsUInt32(),
                arChecksum.V1,
                arChecksum.V2,
                audioIndex,
                trackExtract.BadSectors,
                trackExtract.RecoveredSectors,
                trackExtract.SuspectSectors,
                track.Title,
                track.Performer,
                track.Isrc,
                trackGain,
                trackPeak,
                ctdbChecksum.Value,
                track.PreEmphasis,
                deEmphasisApplied,
                track.StartLba,
                track.SectorCount,
                preGapFrames,
                track.CopyPermitted,
                track.FourChannel,
                trackPeak.HasValue ? trackPeak.Value * 100.0 : null));
            totalSectorsRead += track.SectorCount;
        }

        AudioRecoveryRepairResult? recoveryRepair = null;
        if (options.RepairFromRecoverySession)
        {
            progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                CurrentLba = (int)Math.Min(int.MaxValue, totalSectorsToRip),
                TotalLba = (int)Math.Min(int.MaxValue, totalSectorsToRip),
                BufferLevelPercent = 100.0,
                CurrentActionDescription = "Repairing damaged tracks from session 2 recovery data..."
            });
            recoveryRepair = await AudioRecoverySessionService.RepairRippedTracksFromDiscAsync(
                recorder,
                engine,
                allTracks,
                trackInfos,
                options,
                cancellationToken).ConfigureAwait(false);

            bool anyDeEmphasizedAfterRepair = false;
            if (options.ApplyDeEmphasis)
            {
                for (int i = 0; i < trackInfos.Count; i++)
                {
                    if (trackInfos[i].PreEmphasis && !trackInfos[i].DeEmphasisApplied && File.Exists(trackInfos[i].FilePath))
                    {
                        ApplyDeEmphasisInPlace(trackInfos[i].FilePath);
                        trackInfos[i] = trackInfos[i] with { DeEmphasisApplied = true };
                        anyDeEmphasizedAfterRepair = true;
                    }
                }
            }

            if ((recoveryRepair.RepairedTracks.Count > 0 || anyDeEmphasizedAfterRepair) && options.CalculateReplayGain)
            {
                replayGainResults.Clear();
                for (int i = 0; i < trackInfos.Count; i++)
                {
                    var info = trackInfos[i];
                    if (File.Exists(info.FilePath))
                    {
                        try
                        {
                            using var s = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var rg = ReplayGainCalculator.CalculateTrackReplayGain(s);
                            replayGainResults.Add(rg);
                            trackInfos[i] = info with { ReplayGainTrackGain = rg.GainDb, ReplayGainTrackPeak = rg.PeakAmplitude };
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        // Calculate album ReplayGain across all ripped tracks
        double? albumGain = null;
        double? albumPeak = null;
        // Album gain gates the 400 ms blocks of every ripped track together (ReplayGain 2.0), so only compute it
        // when every track was measured; a partial album would give a wrong album value.
        if (options.CalculateReplayGain && replayGainResults.Count > 0 && replayGainResults.Count == trackInfos.Count)
        {
            var albumResult = ReplayGainCalculator.CalculateAlbumReplayGain(replayGainResults);
            albumGain = albumResult.AlbumGainDb;
            albumPeak = albumResult.AlbumPeakAmplitude;
        }

        uint? ctdbDiscCrc = trackInfos.Count == audioOrder.Count
            ? CtdbChecksum.CombineTrackCrcs(trackInfos, trackStartSamples, totalDiscAudioSamples)
            : null;

        // Generate companion CUE sheet
        if (options.GenerateCueSheet && wavNameByTrackNumber.Count > 0)
        {
            string cueBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(options.CueSheetFileName ?? "album"));
            if (string.IsNullOrEmpty(cueBaseName)) cueBaseName = "album";
            string safeCueName = cueBaseName + ".cue";
            cueSheetPath = Path.Combine(outputDirectory, safeCueName);
            var cueFiles = new List<CueFileEntry>(tracksToRip.Count);

            for (int i = 0; i < tracksToRip.Count; i++)
            {
                var trk = tracksToRip[i];
                string wavFileName = wavNameByTrackNumber[trk.TrackNumber];

                var flags = BuildCueFlags(trk.CopyPermitted, trk.FourChannel, trk.PreEmphasis && !options.ApplyDeEmphasis);

                CueSplitIndex00? splitIndex00 = null;
                MsfAddress? preGap = null;
                if (trk.TrackNumber == 1 && hiddenTrackOneAudioPath != null)
                {
                    splitIndex00 = new CueSplitIndex00(new MsfAddress(0, 0, 0), Path.GetFileName(hiddenTrackOneAudioPath), "WAVE");
                }
                else if (trk.TrackNumber == 1 && track1SilentPreGapFrames > 0)
                {
                    preGap = MsfAddress.FromFrameCount(track1SilentPreGapFrames);
                }
                else if (subcodeMap.Tracks.TryGetValue(trk.TrackNumber, out var det) &&
                         det.Index00Lba is int idx00 &&
                         idx00 < trk.StartLba)
                {
                    if (i > 0 &&
                        tracksToRip[i - 1].TrackNumber == trk.TrackNumber - 1 &&
                        idx00 > tracksToRip[i - 1].StartLba)
                    {
                        splitIndex00 = new CueSplitIndex00(MsfAddress.FromFrameCount(idx00 - tracksToRip[i - 1].StartLba));
                    }
                    else
                    {
                        // Previous track was skipped by TrackNumbersToRip, so preserve its pre-gap duration as PREGAP.
                        preGap = MsfAddress.FromFrameCount(trk.StartLba - idx00);
                    }
                }

                var cueTrack = new CueTrack(
                    TrackNumber: trk.TrackNumber,
                    DataType: CueTrackDataType.Audio,
                    Title: trk.Title,
                    Performer: trk.Performer,
                    Songwriter: trk.Songwriter,
                    Isrc: trk.Isrc,
                    PreGap: preGap,
                    Flags: flags,
                    Indices: new[] { new CueIndex(1, new MsfAddress(0, 0, 0)) },
                    SplitIndex00: splitIndex00);

                cueFiles.Add(new CueFileEntry(wavFileName, "WAVE", new[] { cueTrack }));
            }

            var cueSheet = new CueSheet(
                Catalog: discCatalog,
                Title: cdText?.AlbumTitle,
                Performer: cdText?.AlbumPerformer,
                Songwriter: cdText?.Songwriter,
                Files: cueFiles
            );

            await CueSheetWriter.WriteToFileAsync(cueSheet, cueSheetPath, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        var ripResult = AudioCdRipResult.Succeeded(
            rippedFilePaths,
            totalSectorsRead,
            sw.Elapsed,
            cueSheetPath,
            cdText,
            trackInfos,
            badSectorLbas,
            secureReader != null,
            albumGain,
            albumPeak,
            recoveryRepair,
            hiddenTrackOneAudioPath,
            ctdbDiscCrc,
            offsetResolution,
            mode: AudioRipMode.TrackByTrack,
            imageFilePath: null,
            hiddenTrackOneAudioFrames: hiddenTrackOneAudioFrames,
            ripLogPath: null,
            discTocTracks: discTocTracks,
            mediaCatalogNumber: discCatalog,
            gapDetectionPerformed: true,
            overreadIntoLeadInOut: offsetResolution.OffsetSamples != 0,
            secureModeRequested: options.SecureMode,
            driveUniqueId: recorder.UniqueId,
            driveVendorId: recorder.VendorId,
            driveProductId: recorder.ProductId,
            driveProductRevision: recorder.ProductRevision);

        if (options.WriteRipLog)
        {
            string logFileName = ResolveLogFileName(options.RipLogFileName, cdText?.AlbumTitle);
            ripLogPath = Path.Combine(outputDirectory, logFileName);
            await RipLogFormatter.WriteToFileAsync(
                ripLogPath,
                new RipLogContext(ripResult, OutputFormatLabel: "WAV"),
                cancellationToken).ConfigureAwait(false);
            ripResult = WithRipLogPath(ripResult, ripLogPath);
        }

        return ripResult;
        }
        catch (OperationCanceledException)
        {
            foreach (string path in rippedFilePaths)
            {
                TryDeleteFile(path);
            }
            if (cueSheetPath != null)
            {
                TryDeleteFile(cueSheetPath);
            }
            if (ripLogPath != null)
            {
                TryDeleteFile(ripLogPath);
            }
            throw;
        }
    }

    private async Task<AudioCdRipResult> RipImageCueAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string outputDirectory,
        AudioRipOptions options,
        ReadOffsetResolution offsetResolution,
        CdTextAlbumMetadata? cdText,
        List<AudioDiscoveredTrack> audioOrder,
        DiscSubcodeMap subcodeMap,
        string? discCatalog,
        IReadOnlyList<DiscTocTrack> discTocTracks,
        ISecureAudioReader? secureReader,
        bool continueOnUnreadable,
        byte[] silence,
        List<int> badSectorLbas,
        long[] trackStartSamples,
        long totalDiscAudioSamples,
        int audioEndLba,
        Stopwatch sw,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        string stem = !string.IsNullOrWhiteSpace(options.ImageFileName)
            ? SanitizeFileName(Path.GetFileNameWithoutExtension(options.ImageFileName))
            : (!string.IsNullOrWhiteSpace(cdText?.AlbumTitle) ? SanitizeFileName(cdText.AlbumTitle) : "album");
        if (string.IsNullOrEmpty(stem))
        {
            stem = "album";
        }

        string finalExt = options.ImageFormat == AudioRipFormat.Flac ? ".flac" : ".wav";
        string finalImagePath = Path.Combine(outputDirectory, stem + finalExt);
        string wavImagePath = options.ImageFormat == AudioRipFormat.Flac
            ? Path.Combine(outputDirectory, $".~{stem}-{Guid.NewGuid():N}.wav")
            : finalImagePath;

        string? cueSheetPath = null;
        if (options.GenerateCueSheet)
        {
            string requestedCue = options.CueSheetFileName;
            string cueBaseName = !string.IsNullOrWhiteSpace(requestedCue) &&
                                 !string.Equals(requestedCue, "album.cue", StringComparison.OrdinalIgnoreCase)
                ? SanitizeFileName(Path.GetFileNameWithoutExtension(requestedCue))
                : stem;
            if (string.IsNullOrEmpty(cueBaseName))
            {
                cueBaseName = stem;
            }
            cueSheetPath = Path.Combine(outputDirectory, cueBaseName + ".cue");
        }

        string? htoaTempWav = null;
        int hiddenTrackOneAudioFrames = 0;
        int track1SilentPreGapFrames = 0;
        int totalSectorsRead = 0;
        int htoaBadSectors = 0;
        int htoaRecoveredSectors = 0;
        int htoaSuspectSectors = 0;

        try
        {
            if (audioOrder[0].TrackNumber == 1 && audioOrder[0].StartLba > 0)
            {
                int htoaSectors = audioOrder[0].StartLba;
                htoaTempWav = Path.Combine(outputDirectory, $".~htoa-{Guid.NewGuid():N}.wav");
                bool hasNonSilentAudio = false;
                var htoaExtract = await ExtractSectorRangeToWavAsync(
                    recorder,
                    engine,
                    secureReader,
                    htoaTempWav,
                    startLba: 0,
                    sectorCount: htoaSectors,
                    audioEndLba,
                    options,
                    continueOnUnreadable,
                    silence,
                    badSectorLbas,
                    progress: null,
                    onChunk: slice =>
                    {
                        if (!hasNonSilentAudio && slice.IndexOfAnyExcept((byte)0) >= 0)
                        {
                            hasNonSilentAudio = true;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!htoaExtract.Completed)
                {
                    return AudioCdRipResult.Failed(
                        $"Failed to rip hidden track one audio: unrecovered read error at LBA {htoaExtract.StoppedAtLba} after {options.MaxRetriesPerSector} retries.",
                        new IOException($"Unrecovered read error at LBA {htoaExtract.StoppedAtLba}."));
                }

                if (hasNonSilentAudio)
                {
                    hiddenTrackOneAudioFrames = htoaSectors;
                    totalSectorsRead += htoaSectors;
                    htoaBadSectors = htoaExtract.BadSectors;
                    htoaRecoveredSectors = htoaExtract.RecoveredSectors;
                    htoaSuspectSectors = htoaExtract.SuspectSectors;
                }
                else
                {
                    TryDeleteFile(htoaTempWav);
                    htoaTempWav = null;
                    track1SilentPreGapFrames = htoaSectors;
                }
            }

            int imageStartLba = hiddenTrackOneAudioFrames > 0 ? 0 : audioOrder[0].StartLba;
            int totalImageSectors = Math.Max(0, audioEndLba - imageStartLba);
            long totalImagePcmBytes = (long)totalImageSectors * OpticalConstants.RawSectorSize;
            long totalTracksSectors = audioOrder.Sum(t => (long)t.SectorCount);
            byte[] wavHeader = WavFileCodec.CreateWavHeader(
                totalImagePcmBytes,
                OpticalConstants.CdAudioSampleRate,
                OpticalConstants.CdAudioChannels,
                OpticalConstants.CdAudioBitsPerSample);

            var trackInfos = new List<AudioTrackRipInfo>(audioOrder.Count);
            var replayGainResults = new List<ReplayGainResult>(audioOrder.Count);
            var trackByteRanges = new List<(long Offset, long Length, AudioDiscoveredTrack Track)>(audioOrder.Count);

            bool keepWav = false;
            try
            {
                await using (var imageStream = new FileStream(wavImagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
                {
                    await imageStream.WriteAsync(wavHeader, cancellationToken).ConfigureAwait(false);

                    if (htoaTempWav != null && File.Exists(htoaTempWav))
                    {
                        await using var htoaStream = new FileStream(htoaTempWav, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                        var (htoaOffset, htoaLength) = WavFileCodec.LocateRedBookPcmData(htoaStream);
                        htoaStream.Position = htoaOffset;
                        byte[] copyBuf = new byte[65536];
                        long rem = htoaLength;
                        while (rem > 0)
                        {
                            int n = await htoaStream.ReadAsync(copyBuf.AsMemory(0, (int)Math.Min(copyBuf.Length, rem)), cancellationToken).ConfigureAwait(false);
                            if (n <= 0) break;
                            await imageStream.WriteAsync(copyBuf.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                            rem -= n;
                        }
                    }

                    long sectorsDoneBeforeTrack = 0;
                    for (int i = 0; i < audioOrder.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var track = audioOrder[i];
                        long trackPcmOffset = imageStream.Position;
                        long trackPcmLength = (long)track.SectorCount * OpticalConstants.RawSectorSize;
                        long trackSamples = (long)track.SectorCount * CtdbChecksum.SamplesPerSector;

                        var arChecksum = new AccurateRipChecksum(
                            trackPcmLength / 4,
                            isFirstTrack: i == 0,
                            isLastTrack: i == audioOrder.Count - 1);
                        var ctdbChecksum = CtdbChecksum.ForTrack(trackStartSamples[i], trackSamples, totalDiscAudioSamples);
                        var crc32 = new Crc32Algorithm();
                        int maxAbsSample = 0;

                        long baseSectors = sectorsDoneBeforeTrack;
                        var outcome = await ExtractSectorRangeToStreamAsync(
                            recorder,
                            engine,
                            secureReader,
                            imageStream,
                            track.StartLba,
                            track.SectorCount,
                            audioEndLba,
                            options,
                            continueOnUnreadable,
                            silence,
                            badSectorLbas,
                            done => progress?.Report(new BurnProgressSnapshot
                            {
                                ElapsedTime = sw.Elapsed,
                                CurrentLba = (int)Math.Min(int.MaxValue, baseSectors + done),
                                TotalLba = (int)Math.Min(int.MaxValue, totalTracksSectors),
                                BufferLevelPercent = 100.0,
                                CurrentActionDescription = $"RippingTrack{track.TrackNumber:D2}"
                            }),
                            slice =>
                            {
                                arChecksum.Append(slice);
                                ctdbChecksum.Append(slice);
                                crc32.Append(slice);
                                maxAbsSample = MeasureMaxAbsSample(slice, maxAbsSample);
                            },
                            cancellationToken).ConfigureAwait(false);

                        if (!outcome.Completed)
                        {
                            return AudioCdRipResult.Failed(
                                $"Failed to rip track {track.TrackNumber} ('{track.Title}'): unrecovered read error at LBA {outcome.StoppedAtLba} after {options.MaxRetriesPerSector} retries.",
                                new IOException($"Unrecovered read error at LBA {outcome.StoppedAtLba}."));
                        }

                        bool deEmphasisApplied = false;
                        if (options.ApplyDeEmphasis && track.PreEmphasis)
                        {
                            long savedPos = imageStream.Position;
                            RedBookPcmDspProcessor.ApplyDeEmphasisToStreamRangeInPlace(imageStream, trackPcmOffset, trackPcmLength);
                            imageStream.Position = savedPos;
                            deEmphasisApplied = true;
                        }

                        trackByteRanges.Add((trackPcmOffset, trackPcmLength, track));
                        sectorsDoneBeforeTrack += track.SectorCount;
                        totalSectorsRead += track.SectorCount;

                        int preGapFrames = ResolveTrackPreGapFrames(track, subcodeMap, track1SilentPreGapFrames, hiddenTrackOneAudioFrames);
                        int trackBad = outcome.BadSectors + (i == 0 ? htoaBadSectors : 0);
                        int trackRecovered = outcome.RecoveredSectors + (i == 0 ? htoaRecoveredSectors : 0);
                        int trackSuspect = outcome.SuspectSectors + (i == 0 ? htoaSuspectSectors : 0);
                        double peakAmplitude = maxAbsSample / 32768.0;

                        trackInfos.Add(new AudioTrackRipInfo(
                            track.TrackNumber,
                            finalImagePath,
                            crc32.GetCurrentHashAsUInt32(),
                            arChecksum.V1,
                            arChecksum.V2,
                            i,
                            trackBad,
                            trackRecovered,
                            trackSuspect,
                            track.Title,
                            track.Performer,
                            track.Isrc,
                            null,
                            peakAmplitude,
                            ctdbChecksum.Value,
                            track.PreEmphasis,
                            deEmphasisApplied,
                            track.StartLba,
                            track.SectorCount,
                            preGapFrames,
                            track.CopyPermitted,
                            track.FourChannel,
                            peakAmplitude * 100.0));
                    }

                    if (options.CalculateReplayGain)
                    {
                        for (int i = 0; i < trackByteRanges.Count; i++)
                        {
                            var (off, len, _) = trackByteRanges[i];
                            try
                            {
                                imageStream.Position = off;
                                using var slice = new BoundedReadStream(imageStream, len);
                                var rg = ReplayGainCalculator.CalculateTrackReplayGain(slice);
                                replayGainResults.Add(rg);
                                trackInfos[i] = trackInfos[i] with
                                {
                                    ReplayGainTrackGain = rg.GainDb,
                                    ReplayGainTrackPeak = rg.PeakAmplitude,
                                    PeakLevelPercent = rg.PeakAmplitude * 100.0
                                };
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                double? albumGain = null;
                double? albumPeak = null;
                if (options.CalculateReplayGain && replayGainResults.Count > 0 && replayGainResults.Count == trackInfos.Count)
                {
                    var albumResult = ReplayGainCalculator.CalculateAlbumReplayGain(replayGainResults);
                    albumGain = albumResult.AlbumGainDb;
                    albumPeak = albumResult.AlbumPeakAmplitude;
                }

                if (options.ImageFormat == AudioRipFormat.Flac)
                {
                    string albumTitle = !string.IsNullOrWhiteSpace(cdText?.AlbumTitle) ? cdText.AlbumTitle : stem;
                    string albumPerformer = !string.IsNullOrWhiteSpace(cdText?.AlbumPerformer) ? cdText.AlbumPerformer : "Unknown Artist";
                    var albumTags = new AudioTrackTags(
                        Title: albumTitle,
                        Artist: albumPerformer,
                        Album: albumTitle,
                        TrackNumber: 0,
                        TrackTotal: trackInfos.Count,
                        ReplayGainAlbumGain: albumGain.HasValue ? ReplayGainCalculator.FormatGainDb(albumGain.Value) : null,
                        ReplayGainAlbumPeak: albumPeak.HasValue ? ReplayGainCalculator.FormatPeak(albumPeak.Value) : null,
                        CoverArtBytes: options.CoverArtBytes,
                        CoverArtMimeType: options.CoverArtMimeType);

                    string tempFlacPath = Path.Combine(outputDirectory, $".~{stem}-{Guid.NewGuid():N}.flac");
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(options.FfmpegPath) && File.Exists(options.FfmpegPath))
                        {
                            await FlacEncoder.EncodeAsync(options.FfmpegPath, wavImagePath, tempFlacPath, albumTags, cancellationToken).ConfigureAwait(false);
                            if (albumTags.CoverArtBytes is { Length: > 0 } || albumTags.ReplayGainAlbumGain != null)
                            {
                                FlacEncoder.EmbedCoverArtAndReplayGain(tempFlacPath, albumTags);
                            }
                        }
                        else
                        {
                            FlacFileCodec.WriteFlacFromWavFile(wavImagePath, tempFlacPath, albumTags, cdText?.Songwriter, cdText?.Composer);
                        }

                        File.Move(tempFlacPath, finalImagePath, overwrite: true);
                    }
                    finally
                    {
                        TryDeleteFile(tempFlacPath);
                    }
                }
                else
                {
                    keepWav = true;
                }

                if (cueSheetPath != null)
                {
                    var cueTracks = new List<CueTrack>(audioOrder.Count);
                    for (int i = 0; i < audioOrder.Count; i++)
                    {
                        var trk = audioOrder[i];
                        var flags = BuildCueFlags(trk.CopyPermitted, trk.FourChannel, trk.PreEmphasis && !options.ApplyDeEmphasis);
                        var indices = new List<CueIndex>(2);
                        MsfAddress? preGap = null;

                        if (i == 0)
                        {
                            if (hiddenTrackOneAudioFrames > 0)
                            {
                                indices.Add(new CueIndex(0, new MsfAddress(0, 0, 0)));
                                indices.Add(new CueIndex(1, MsfAddress.FromFrameCount(trk.StartLba)));
                            }
                            else
                            {
                                if (track1SilentPreGapFrames > 0)
                                {
                                    preGap = MsfAddress.FromFrameCount(track1SilentPreGapFrames);
                                }
                                indices.Add(new CueIndex(1, new MsfAddress(0, 0, 0)));
                            }
                        }
                        else
                        {
                            if (subcodeMap.Tracks.TryGetValue(trk.TrackNumber, out var det) &&
                                det.Index00Lba is int idx00 &&
                                idx00 < trk.StartLba &&
                                idx00 >= imageStartLba)
                            {
                                indices.Add(new CueIndex(0, MsfAddress.FromFrameCount(idx00 - imageStartLba)));
                            }

                            indices.Add(new CueIndex(1, MsfAddress.FromFrameCount(trk.StartLba - imageStartLba)));
                        }

                        cueTracks.Add(new CueTrack(
                            TrackNumber: trk.TrackNumber,
                            DataType: CueTrackDataType.Audio,
                            Title: trk.Title,
                            Performer: trk.Performer,
                            Songwriter: trk.Songwriter,
                            Isrc: trk.Isrc,
                            PreGap: preGap,
                            Flags: flags,
                            Indices: indices));
                    }

                    var cueSheet = new CueSheet(
                        Catalog: discCatalog,
                        Title: cdText?.AlbumTitle,
                        Performer: cdText?.AlbumPerformer,
                        Songwriter: cdText?.Songwriter,
                        Files: new[] { new CueFileEntry(Path.GetFileName(finalImagePath), "WAVE", cueTracks) });

                    await CueSheetWriter.WriteToFileAsync(cueSheet, cueSheetPath, cancellationToken).ConfigureAwait(false);
                }

                uint? ctdbDiscCrc = CtdbChecksum.CombineTrackCrcs(trackInfos, trackStartSamples, totalDiscAudioSamples);
                sw.Stop();
                var ripResult = AudioCdRipResult.Succeeded(
                    new[] { finalImagePath },
                    totalSectorsRead,
                    sw.Elapsed,
                    cueSheetPath,
                    cdText,
                    trackInfos,
                    badSectorLbas,
                    secureReader != null,
                    albumGain,
                    albumPeak,
                    recoveryRepair: null,
                    hiddenTrackOneAudioPath: null,
                    ctdbDiscCrc: ctdbDiscCrc,
                    readOffset: offsetResolution,
                    mode: AudioRipMode.ImageCue,
                    imageFilePath: finalImagePath,
                    hiddenTrackOneAudioFrames: hiddenTrackOneAudioFrames,
                    ripLogPath: null,
                    discTocTracks: discTocTracks,
                    mediaCatalogNumber: discCatalog,
                    gapDetectionPerformed: true,
                    overreadIntoLeadInOut: offsetResolution.OffsetSamples != 0,
                    secureModeRequested: options.SecureMode,
                    driveUniqueId: recorder.UniqueId,
                    driveVendorId: recorder.VendorId,
                    driveProductId: recorder.ProductId,
                    driveProductRevision: recorder.ProductRevision);

                if (options.WriteRipLog)
                {
                    string logFileName = ResolveLogFileName(options.RipLogFileName, cdText?.AlbumTitle, stem);
                    string ripLogPath = Path.Combine(outputDirectory, logFileName);
                    await RipLogFormatter.WriteToFileAsync(
                        ripLogPath,
                        new RipLogContext(ripResult, OutputFormatLabel: AudioRipFormats.DisplayName(options.ImageFormat)),
                        cancellationToken).ConfigureAwait(false);
                    ripResult = WithRipLogPath(ripResult, ripLogPath);
                }

                return ripResult;
            }
            finally
            {
                if (!keepWav)
                {
                    TryDeleteFile(wavImagePath);
                }
            }
        }
        finally
        {
            if (htoaTempWav != null)
            {
                TryDeleteFile(htoaTempWav);
            }
        }
    }

    private static AudioCdRipResult WithRipLogPath(AudioCdRipResult rip, string ripLogPath) =>
        AudioCdRipResult.Succeeded(
            rip.RippedFilePaths,
            rip.TotalSectorsRead,
            rip.TotalTime,
            rip.CueSheetPath,
            rip.CdText,
            rip.TrackInfos,
            rip.BadSectorLbas,
            rip.SecureModeUsed,
            rip.ReplayGainAlbumGain,
            rip.ReplayGainAlbumPeak,
            rip.RecoveryRepair,
            rip.HiddenTrackOneAudioPath,
            rip.CtdbDiscCrc,
            rip.ReadOffset,
            rip.Mode,
            rip.ImageFilePath,
            rip.HiddenTrackOneAudioFrames,
            ripLogPath,
            rip.DiscTocTracks,
            rip.MediaCatalogNumber,
            rip.GapDetectionPerformed,
            rip.OverreadIntoLeadInOut,
            rip.SecureModeRequested,
            rip.DriveUniqueId,
            rip.DriveVendorId,
            rip.DriveProductId,
            rip.DriveProductRevision);

    public static string ResolveLogFileName(string? explicitLogFileName, string? albumTitle, string fallbackStem = "album")
    {
        string raw = !string.IsNullOrWhiteSpace(explicitLogFileName)
            ? Path.GetFileNameWithoutExtension(explicitLogFileName)
            : (!string.IsNullOrWhiteSpace(albumTitle) ? albumTitle : fallbackStem);
        string safe = SanitizeFileName(raw);
        if (string.IsNullOrEmpty(safe))
        {
            safe = fallbackStem;
        }
        return safe + ".log";
    }

    private static List<string> BuildCueFlags(bool copyPermitted, bool fourChannel, bool preEmphasis)
    {
        var flags = new List<string>(3);
        if (copyPermitted) flags.Add("DCP");
        if (fourChannel) flags.Add("4CH");
        if (preEmphasis) flags.Add("PRE");
        return flags;
    }

    private static int ResolveTrackPreGapFrames(
        AudioDiscoveredTrack track,
        DiscSubcodeMap subcodeMap,
        int track1SilentPreGapFrames,
        int hiddenTrackOneAudioFrames)
    {
        if (track.TrackNumber == 1)
        {
            if (hiddenTrackOneAudioFrames > 0)
            {
                return hiddenTrackOneAudioFrames;
            }
            return track1SilentPreGapFrames;
        }

        if (subcodeMap.Tracks.TryGetValue(track.TrackNumber, out var det) &&
            det.Index00Lba is int idx00 &&
            idx00 < track.StartLba)
        {
            return track.StartLba - idx00;
        }

        return 0;
    }

    private static int MeasureMaxAbsSample(ReadOnlySpan<byte> pcm, int currentMax)
    {
        int whole = pcm.Length & ~1;
        for (int i = 0; i < whole; i += 2)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i, 2));
            int abs = s == short.MinValue ? 32768 : Math.Abs(s);
            if (abs > currentMax)
            {
                currentMax = abs;
            }
        }
        return currentMax;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public BoundedReadStream(Stream inner, long remainingBytes)
        {
            _inner = inner;
            _length = remainingBytes;
            _remaining = remainingBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0 || count <= 0) return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly record struct RangeExtractOutcome(bool Completed, int? StoppedAtLba, int BadSectors, int RecoveredSectors, int SuspectSectors);

    private static async Task<RangeExtractOutcome> ExtractSectorRangeToWavAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        ISecureAudioReader? secureReader,
        string targetWavPath,
        int startLba,
        int sectorCount,
        int audioEndLba,
        AudioRipOptions options,
        bool continueOnUnreadable,
        byte[] silence,
        List<int> badSectorLbas,
        Action<int>? progress,
        Action<ReadOnlySpan<byte>> onChunk,
        CancellationToken cancellationToken)
    {
        long totalBytes = (long)sectorCount * OpticalConstants.RawSectorSize;
        byte[] wavHeader = WavFileCodec.CreateWavHeader(
            totalBytes,
            OpticalConstants.CdAudioSampleRate,
            OpticalConstants.CdAudioChannels,
            OpticalConstants.CdAudioBitsPerSample);

        bool keepFile = false;
        try
        {
            await using var fileStream = new FileStream(targetWavPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await fileStream.WriteAsync(wavHeader, cancellationToken).ConfigureAwait(false);
            var outcome = await ExtractSectorRangeToStreamAsync(
                recorder,
                engine,
                secureReader,
                fileStream,
                startLba,
                sectorCount,
                audioEndLba,
                options,
                continueOnUnreadable,
                silence,
                badSectorLbas,
                progress,
                onChunk,
                cancellationToken).ConfigureAwait(false);
            keepFile = outcome.Completed;
            return outcome;
        }
        finally
        {
            if (!keepFile)
            {
                TryDeleteFile(targetWavPath);
            }
        }
    }

    private static async Task<RangeExtractOutcome> ExtractSectorRangeToStreamAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        ISecureAudioReader? secureReader,
        Stream destinationStream,
        int startLba,
        int sectorCount,
        int audioEndLba,
        AudioRipOptions options,
        bool continueOnUnreadable,
        byte[] silence,
        List<int> badSectorLbas,
        Action<int>? progress,
        Action<ReadOnlySpan<byte>> onChunk,
        CancellationToken cancellationToken)
    {
        long totalBytes = (long)sectorCount * OpticalConstants.RawSectorSize;

        // Offset correction: audio starts ReadOffsetSamples samples later on disc than its LBA says,
        // so read the covering sector range and drop the leading `bytesToSkip` bytes.
        long firstByte = (long)startLba * OpticalConstants.RawSectorSize + (long)options.ReadOffsetSamples * 4;
        int firstSector = (int)Math.Floor(firstByte / (double)OpticalConstants.RawSectorSize);
        int endSector = (int)Math.Ceiling((firstByte + totalBytes) / (double)OpticalConstants.RawSectorSize);
        int readStart = Math.Max(firstSector, 0);
        int readEnd = Math.Min(endSector, audioEndLba);
        int bytesToSkip = (int)(firstByte - (long)firstSector * OpticalConstants.RawSectorSize);
        long bytesRemaining = totalBytes;
        int badSectors = 0;
        int recoveredSectors = 0;
        int suspectSectors = 0;
        bool completed = true;
        int? stoppedAtLba = null;

        async ValueTask EmitAsync(ReadOnlyMemory<byte> sector)
        {
            if (bytesToSkip > 0)
            {
                int skip = Math.Min(bytesToSkip, sector.Length);
                sector = sector[skip..];
                bytesToSkip -= skip;
            }
            if (bytesRemaining <= 0 || sector.IsEmpty)
            {
                return;
            }
            if (sector.Length > bytesRemaining)
            {
                sector = sector[..(int)bytesRemaining];
            }

            await destinationStream.WriteAsync(sector, cancellationToken).ConfigureAwait(false);
            onChunk(sector.Span);
            bytesRemaining -= sector.Length;
        }

        async Task EmitOverreadRangeAsync(int overStart, int overEnd)
        {
            for (int s = overStart; s < overEnd; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? overreadSector = options.ReadOffsetSamples != 0
                    ? await TryReadOverreadSectorAsync(recorder, engine, secureReader, s, audioEndLba, options.MaxRetriesPerSector, cancellationToken).ConfigureAwait(false)
                    : null;
                await EmitAsync(overreadSector ?? silence).ConfigureAwait(false);
            }
        }

        if (firstSector < readStart)
        {
            await EmitOverreadRangeAsync(firstSector, Math.Min(readStart, endSector)).ConfigureAwait(false);
        }

        if (readEnd > readStart)
        {
            async ValueTask<bool> OnUnreadableAsync(int lba)
            {
                badSectorLbas.Add(lba);
                badSectors++;
                if (!continueOnUnreadable)
                {
                    return false;
                }
                await EmitAsync(silence).ConfigureAwait(false);
                return true;
            }

            if (secureReader != null)
            {
                var secure = await SecureAudioReader.ReadRangeAsync(
                    secureReader,
                    recorder,
                    readStart,
                    readEnd - readStart,
                    audioEndLba,
                    options.MaxRetriesPerSector,
                    onSector: (_, data) => EmitAsync(data),
                    onUnreadable: OnUnreadableAsync,
                    onProgress: progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                (completed, stoppedAtLba) = (secure.Completed, secure.StoppedAtLba);
                recoveredSectors = secure.RecoveredSectors;
                suspectSectors = secure.SuspectSectors;
            }
            else
            {
                var batched = await BatchedSectorReader.ReadRangeAsync(
                    engine,
                    recorder,
                    readStart,
                    readEnd - readStart,
                    raw2352Mode: true,
                    options.MaxRetriesPerSector,
                    onSector: (_, data) => EmitAsync(data),
                    onUnreadable: OnUnreadableAsync,
                    onProgress: progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                (completed, stoppedAtLba) = (batched.Completed, batched.StoppedAtLba);
            }
        }

        int tailOverStart = Math.Max(readEnd, readStart);
        if (tailOverStart < endSector)
        {
            await EmitOverreadRangeAsync(tailOverStart, endSector).ConfigureAwait(false);
        }

        return new RangeExtractOutcome(completed, stoppedAtLba, badSectors, recoveredSectors, suspectSectors);
    }

    private static async Task<byte[]?> TryReadOverreadSectorAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        ISecureAudioReader? secureReader,
        int lba,
        int audioEndLba,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        try
        {
            if (secureReader != null)
            {
                byte[]? captured = null;
                int c2Flags = 0;
                var first = await secureReader.ReadAudioSectorsWithC2Async(recorder, lba, 1, (gotLba, data, c2) =>
                {
                    if (gotLba == lba && captured == null)
                    {
                        captured = data.ToArray();
                        c2Flags = c2;
                    }
                    return ValueTask.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);

                if (!first.Success || captured == null)
                {
                    return null;
                }

                for (int round = 0; round < maxRetries && c2Flags > 0; round++)
                {
                    await secureReader.FlushReadCacheAsync(recorder, lba, audioEndLba, cancellationToken).ConfigureAwait(false);
                    var retry = await secureReader.ReadAudioSectorsWithC2Async(recorder, lba, 1, (gotLba, data, c2) =>
                    {
                        if (gotLba == lba)
                        {
                            captured = data.ToArray();
                            c2Flags = c2;
                        }
                        return ValueTask.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);

                    if (!retry.Success)
                    {
                        break;
                    }
                }

                return captured;
            }

            byte[]? sector = null;
            var result = await engine.ReadSectorsAsync(recorder, lba, 1, raw2352Mode: true, (gotLba, data) =>
            {
                if (gotLba == lba && sector == null)
                {
                    sector = data.ToArray();
                }
                return ValueTask.CompletedTask;
            }, progress: null, cancellationToken).ConfigureAwait(false);

            return result.Success ? sector : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void ApplyDeEmphasisInPlace(string wavPath)
    {
        string tempPath = wavPath + ".deemph-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            RedBookPcmDspProcessor.ApplyDeEmphasisToWavFile(wavPath, tempPath);
            File.Move(tempPath, wavPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path) =>
        CdBurner.Core.Utilities.ProcessAndFileCleanup.TryDeleteFileWithRetry(path);
}
