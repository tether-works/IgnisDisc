using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>What a DVD player does when a title finishes playing.</summary>
public enum DvdAfterTitleAction
{
    /// <summary>Advance to the next title (then return to the title menu, or stop if there is none).</summary>
    PlayNextTitle = 0,

    /// <summary>Return directly to the title menu after each title finishes.</summary>
    ReturnToMenu = 1
}

/// <summary>Rendered text size for authored SRT/VobSub DVD subpictures.</summary>
public enum DvdSubtitleSize
{
    Small = 0,
    Medium = 1,
    Large = 2
}

/// <summary>
/// User configuration options for the universal one-click DVD-Video authoring pipeline.
/// </summary>
public sealed class DvdAuthoringOptions
{
    /// <summary>
    /// Target television standard (NTSC or PAL). Default: NTSC.
    /// </summary>
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Ntsc;

    /// <summary>
    /// Target display aspect ratio (16:9 Widescreen or 4:3 Full-Frame). Default: 16:9 Widescreen.
    /// </summary>
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Widescreen16x9;

    /// <summary>
    /// Optical volume label (max 32 uppercase characters). Default: "DVD_VIDEO".
    /// </summary>
    public string VolumeLabel { get; set; } = "DVD_VIDEO";

    /// <summary>
    /// Top title displayed on the interactive DVD root menu. Default: "DVD Video".
    /// </summary>
    public string MenuTitle { get; set; } = "DVD Video";

    /// <summary>
    /// Whether to create a DVD title menu (VIDEO_TS.VOB) with a still frame and play button per title. Default: true.
    /// </summary>
    public bool AutoCreatePlayMenu { get; set; } = true;

    /// <summary>
    /// Seconds before the title menu starts Title 1 automatically; 0 (default) waits indefinitely.
    /// </summary>
    public int MenuAutoPlaySeconds { get; set; }

    /// <summary>
    /// What happens when a title finishes playing. Default: <see cref="DvdAfterTitleAction.PlayNextTitle"/>.
    /// </summary>
    public DvdAfterTitleAction AfterTitleAction { get; set; } = DvdAfterTitleAction.PlayNextTitle;

    /// <summary>
    /// Whether First-Play turns subpicture stream 0 on by default. Default: false.
    /// </summary>
    public bool SubtitlesOnByDefault { get; set; }

    /// <summary>
    /// Rendered text size for authored SRT/VobSub DVD subpictures. Default: <see cref="DvdSubtitleSize.Medium"/>.
    /// </summary>
    public DvdSubtitleSize SubtitleSize { get; set; } = DvdSubtitleSize.Medium;

    /// <summary>
    /// When true (default), lowers the video bitrate so total title duration fits the target DVD-5 / DVD-9 payload.
    /// When false, encodes at <see cref="VideoBitrateKbps"/> without lowering for disc capacity.
    /// </summary>
    public bool FitToDisc { get; set; } = true;

    /// <summary>
    /// Optional custom menu title names keyed by full source movie path (case-insensitive).
    /// </summary>
    public IDictionary<string, string> TitleNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional custom menu title names indexed by 0-based title order.
    /// </summary>
    public IList<string> OrderedTitleNames { get; } = new List<string>();

    /// <summary>
    /// Whether to use FFmpeg when available on the system to transcode arbitrary video streams into MPEG-2. Default: true.
    /// </summary>
    public bool UseFfmpegWhenAvailable { get; set; } = true;

    /// <summary>
    /// Optional custom path to the FFmpeg executable.
    /// </summary>
    public string? CustomFfmpegPath { get; set; }

    /// <summary>
    /// Target video bitrate in kilobits per second. Default: 6000 kbps (6 Mbps).
    /// </summary>
    public int VideoBitrateKbps { get; set; } = 6000;

    /// <summary>
    /// Maximum video bitrate in kilobits per second. Default: 8000 kbps (8 Mbps).
    /// </summary>
    public int MaxVideoBitrateKbps { get; set; } = 8000;

    /// <summary>
    /// Audio bitrate in kilobits per second. Default: 192 kbps.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Author for a dual-layer disc (DVD-9 on DVD+R DL) instead of fitting everything onto a single-layer DVD-5. The
    /// bitrate is then chosen so the titles up to the first one that brings the running time past half fit on the first
    /// layer, which puts a title start (a cell boundary) where the layer break can go. Default: false.
    /// </summary>
    public bool DualLayer { get; set; }

    /// <summary>
    /// Chapter length in seconds: each title gets a chapter mark (a new cell and program) at the first VOBU on or after
    /// every multiple of it, so a player's next / previous chapter and chapter search work. 0: one chapter per title.
    /// Default: 300 (5 minutes).
    /// </summary>
    public int ChapterIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// English subtitles for movie files, by movie path: an .srt each. A movie not listed uses an .srt beside it with the
    /// same name (name.srt, name.en.srt or name.eng.srt) when <see cref="UseSubtitleFilesBesideMovies"/> is set.
    /// </summary>
    public IDictionary<string, string> SubtitleFiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Look for an .srt beside each movie file (see <see cref="SubtitleFiles"/>). Default: true.</summary>
    public bool UseSubtitleFilesBesideMovies { get; set; } = true;

    /// <summary>
    /// Make subtitles by speech recognition for movies that have no .srt (see <see cref="SubtitleFiles"/>); the
    /// generated .srt is added to <see cref="SubtitleFiles"/> before the menu is built. Needs the optional subtitle
    /// tool (<see cref="CdBurner.Core.Subtitles.WhisperToolInstaller"/>). Default: false.
    /// </summary>
    public bool GenerateSubtitles { get; set; }

    /// <summary>
    /// Spoken language for <see cref="GenerateSubtitles"/>: a two- or three-letter code such as "en", or "auto".
    /// Default: "en". The disc's subtitle stream is declared English whatever this is.
    /// </summary>
    public string SubtitleLanguage { get; set; } = "en";

    /// <summary>
    /// Whether to generate paginated per-title Chapter Selection submenus (VTS_xx_0.VOB) with a Back to Main Menu button.
    /// Default: false.
    /// </summary>
    public bool GenerateChapterMenus { get; set; }

    /// <summary>
    /// Optional custom background image path scaled and letterboxed to NTSC 720x480 or PAL 720x576 for DVD menus.
    /// </summary>
    public string? MenuBackgroundImagePath { get; set; }

    /// <summary>
    /// Optional background audio file muxed as stereo 48 kHz AC-3 into DVD menus.
    /// </summary>
    public string? MenuAudioPath { get; set; }

    /// <summary>Duration in seconds for looping custom menu background audio (clamped 1..60). Default: 15.</summary>
    public int MenuAudioDurationSeconds { get; set; } = 15;

    /// <summary>Font family preset used for DVD menu text. Default: <see cref="DvdMenuFontPreset.ModernSans"/>.</summary>
    public DvdMenuFontPreset MenuFontPreset { get; set; } = DvdMenuFontPreset.ModernSans;

    /// <summary>Accent and button-highlight color preset used for DVD menus. Default: <see cref="DvdMenuHighlightPreset.Amber"/>.</summary>
    public DvdMenuHighlightPreset MenuHighlightPreset { get; set; } = DvdMenuHighlightPreset.Amber;

    /// <summary>Per-title audio stream tracks (up to 8 per title), keyed by movie path.</summary>
    public IDictionary<string, IList<DvdAudioTrackSpec>> TitleAudioTracks { get; } =
        new Dictionary<string, IList<DvdAudioTrackSpec>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-title subtitle stream tracks (up to 8 per title), keyed by movie path.</summary>
    public IDictionary<string, IList<DvdSubtitleTrackSpec>> TitleSubtitleTracks { get; } =
        new Dictionary<string, IList<DvdSubtitleTrackSpec>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default audio stream list applied when a title has no explicit entry in <see cref="TitleAudioTracks"/>.</summary>
    public IList<DvdAudioTrackSpec> DefaultAudioTracks { get; } = new List<DvdAudioTrackSpec>();

    /// <summary>Default subtitle stream list applied when a title has no explicit entry in <see cref="TitleSubtitleTracks"/>.</summary>
    public IList<DvdSubtitleTrackSpec> DefaultSubtitleTracks { get; } = new List<DvdSubtitleTrackSpec>();
}

/// <summary>The video bitrate chosen for an authoring run, and why it was lowered (null when it was not).</summary>
public sealed record DvdBitratePlan(int VideoKbps, int MaxVideoKbps, string? Note);

/// <summary>
/// Result containing authored DVD directory paths, generated files, validation status, and planned disc layout.
/// </summary>
public sealed class DvdAuthoringResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string OutputRootDirectory { get; init; } = string.Empty;
    public string VideoTsDirectory { get; init; } = string.Empty;
    public string AudioTsDirectory { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = "DVD_VIDEO";
    public DvdInputKind InputKind { get; init; }
    public int TitleCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public int TotalSectors { get; init; }
    public IReadOnlyList<string> GeneratedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LogNotes { get; init; } = Array.Empty<string>();
    public DvdVideoValidationResult? ValidationResult { get; init; }
    public DvdVideoLayout? PlannedLayout { get; init; }
}

/// <summary>
/// Universal One-Click DVD-Video Authoring Engine.
/// Accepts arbitrary input (single/multiple movie files or existing VIDEO_TS folders), normalizes the structure,
/// transcodes video streams (or falls back to deterministic synthetic streams for testing), generates compliant
/// IFO/BUP/VOB headers, patches NAV packs, generates menus, and produces 100% DVD-compliant optical disc layouts.
/// </summary>
public sealed class DvdVideoAuthoringService
{
    /// <summary>
    /// Makes subtitles for <see cref="DvdAuthoringOptions.GenerateSubtitles"/>; null uses the speech-recognition
    /// service. Tests set a fake.
    /// </summary>
    public DvdSubtitleSourceResolver.SubtitleGenerator? SubtitleGenerator { get; init; }

    /// <summary>Bytes of video files planned for a single-layer DVD-5 (4.7 GB disc, about 10% left for margin).</summary>
    public const double Dvd5PayloadBytes = 4_250_000_000.0;

    /// <summary>Bytes of video files planned for a DVD-9 (4,173,824 sectors, about 10% left for margin).</summary>
    public const double Dvd9PayloadBytes = 7_700_000_000.0;

    /// <summary>Bytes planned for the first layer of a DVD-9 (2,086,912 sectors, about 10% left for margin).</summary>
    public const double Dvd9Layer0PayloadBytes = 3_850_000_000.0;

    /// <summary>MPEG program stream and DVD navigation overhead on top of the audio and video bitrates.</summary>
    public const double MuxOverhead = 1.07;

    /// <summary>The lowest video bitrate <see cref="PlanVideoBitrate"/> goes to: below it MPEG-2 at DVD size turns blocky.</summary>
    public const int MinimumVideoKbps = 1800;

    /// <summary>
    /// Chooses the video bitrate for titles of the given running times (seconds, in disc order; 0 when unknown). The
    /// bitrate asked for in <paramref name="options"/> is kept when everything fits, else lowered (to 1,800 kbps at
    /// the least). Single layer: all titles within <see cref="Dvd5PayloadBytes"/>. Dual layer
    /// (<see cref="DvdAuthoringOptions.DualLayer"/>): all titles within <see cref="Dvd9PayloadBytes"/>, and the
    /// titles up to the first one that brings the running time to at least half also within
    /// <see cref="Dvd9Layer0PayloadBytes"/>. The next title then starts on a cell boundary that holds at least half the
    /// disc before it and lies within the first layer: a usable layer break. With one title, or a last title longer
    /// than all the others together, there is no such boundary; the note says so.
    /// </summary>
    /// <summary>Cue text that is a download site's advert rather than dialogue (release groups add these to .srt files).</summary>
    private static readonly System.Text.RegularExpressions.Regex Advert = new(@"\bYTS\b|YTS\.|YIFY|opensubtitles|Downloaded from",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed record PreparedSubtitleTrack(
        DvdSubtitleTrackSpec Track,
        string? VobSubIndexPath,
        IReadOnlyList<(long Pts, byte[] Spu)> SpuUnits);

    internal static bool TryGetMovieKey<T>(IDictionary<string, T> map, string moviePath, out string? matchedKey)
    {
        if (map.ContainsKey(moviePath))
        {
            matchedKey = moviePath;
            return true;
        }
        string full = NormalizedPath(moviePath);
        if (map.ContainsKey(full))
        {
            matchedKey = full;
            return true;
        }
        foreach (string key in map.Keys)
        {
            if (string.Equals(NormalizedPath(key), full, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = key;
                return true;
            }
        }
        matchedKey = null;
        return false;
    }

    /// <summary>Resolves up to 8 audio stream specs for a title.</summary>
    public static IReadOnlyList<DvdAudioTrackSpec> ResolveAudioTracks(string moviePath, DvdAuthoringOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moviePath);
        ArgumentNullException.ThrowIfNull(options);

        IList<DvdAudioTrackSpec>? configured = null;
        if (TryGetMovieKey(options.TitleAudioTracks, moviePath, out string? key) && key != null && options.TitleAudioTracks[key].Count > 0)
        {
            configured = options.TitleAudioTracks[key];
        }
        else if (options.DefaultAudioTracks.Count > 0)
        {
            configured = options.DefaultAudioTracks;
        }

        if (configured != null)
        {
            return configured.Take(DvdVideoProject.MaxAudioTracksPerTitle).ToList();
        }

        return [new DvdAudioTrackSpec(SourcePath: null, Language: "eng", Channels: 2, BitrateKbps: options.AudioBitrateKbps)];
    }

    /// <summary>Resolves up to 8 subtitle stream specs for a title (combining multi-track lists and legacy/beside-movie .srt).</summary>
    public static IReadOnlyList<DvdSubtitleTrackSpec> ResolveSubtitleTracks(string moviePath, DvdAuthoringOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moviePath);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<DvdSubtitleTrackSpec>();
        if (TryGetMovieKey(options.TitleSubtitleTracks, moviePath, out string? key) && key != null)
        {
            foreach (var track in options.TitleSubtitleTracks[key])
            {
                if (result.Count >= DvdVideoProject.MaxSubtitleTracksPerTitle) break;
                result.Add(track);
            }
        }
        else if (options.DefaultSubtitleTracks.Count > 0)
        {
            foreach (var track in options.DefaultSubtitleTracks)
            {
                if (result.Count >= DvdVideoProject.MaxSubtitleTracksPerTitle) break;
                result.Add(track);
            }
        }

        string? legacySrt = FindSubtitleFile(moviePath, options);
        if (legacySrt != null && result.Count < DvdVideoProject.MaxSubtitleTracksPerTitle)
        {
            string fullLegacy = NormalizedPath(legacySrt);
            bool alreadyPresent = result.Any(t =>
                !string.IsNullOrWhiteSpace(t.SourcePath) &&
                string.Equals(NormalizedPath(t.SourcePath!), fullLegacy, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                string lang = string.IsNullOrWhiteSpace(options.SubtitleLanguage) ||
                              string.Equals(options.SubtitleLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                    ? "eng"
                    : options.SubtitleLanguage;
                result.Add(new DvdSubtitleTrackSpec(legacySrt, lang));
            }
        }

        return result;
    }

    private static List<PreparedSubtitleTrack> PrepareSubtitleTracks(
        IReadOnlyList<DvdSubtitleTrackSpec> tracks,
        DvdAuthoringOptions options,
        int setNum,
        bool writeVobSubFiles,
        List<string> logNotes)
    {
        var prepared = new List<PreparedSubtitleTrack>();
        int videoHeight = options.Standard == DvdVideoStandard.Pal ? 576 : 480;
        for (int s = 0; s < tracks.Count && prepared.Count < DvdVideoProject.MaxSubtitleTracksPerTitle; s++)
        {
            var track = tracks[s];
            string? srt = track.SourcePath;
            if (string.IsNullOrWhiteSpace(srt) || !File.Exists(srt))
            {
                if (!string.IsNullOrWhiteSpace(srt))
                {
                    logNotes.Add($"Subtitles '{Path.GetFileName(srt)}' were not found; skipping subtitle stream {s + 1} for title {setNum}.");
                }
                continue;
            }

            try
            {
                var cues = DvdSubtitleWriter.ReadSrt(srt);
                var kept = cues.Where(c => !c.Lines.Any(l => Advert.IsMatch(l))).ToList();
                if (kept.Count == 0)
                {
                    logNotes.Add($"Subtitles '{Path.GetFileName(srt)}' hold no cues; skipping subtitle stream {s + 1} for title {setNum}.");
                    continue;
                }

                string? idx = null;
                if (writeVobSubFiles)
                {
                    string workDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Subtitles_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(workDir);
                    try
                    {
                        idx = DvdSubtitleWriter.WriteVobSub(
                            kept,
                            Path.Combine(workDir, $"subtitles_{setNum}_{prepared.Count}.sub"),
                            width: 720,
                            height: videoHeight,
                            size: options.SubtitleSize,
                            languageCode: track.NormalizedLanguage,
                            streamIndex: 0);
                    }
                    catch
                    {
                        try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        throw;
                    }
                }

                var spuUnits = writeVobSubFiles
                    ? Array.Empty<(long Pts, byte[] Spu)>()
                    : DvdSubtitleWriter.EncodeCuesToSpuUnits(kept, width: 720, height: videoHeight, size: options.SubtitleSize);

                prepared.Add(new PreparedSubtitleTrack(track, idx, spuUnits));
                logNotes.Add($"Subtitle stream {prepared.Count} ({track.NormalizedLanguage}) for title {setNum} from '{Path.GetFileName(srt)}': {kept.Count} cues" +
                    (kept.Count < cues.Count ? $" ({cues.Count - kept.Count} download-site adverts left out)." : "."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
            {
                logNotes.Add($"Subtitles '{Path.GetFileName(srt)}' could not be used ({ex.Message}); skipping subtitle stream {s + 1} for title {setNum}.");
            }
        }
        return prepared;
    }

    /// <summary>The .srt for a movie: given in <see cref="DvdAuthoringOptions.SubtitleFiles"/>, else beside it; null when none.</summary>
    public static string? FindSubtitleFile(string moviePath, DvdAuthoringOptions options)
    {
        // Matched by full path, so F:/a/b.mp4, F:\a\b.mp4 and a relative spelling of it are the same movie.
        string full = NormalizedPath(moviePath);
        foreach (var (key, given) in options.SubtitleFiles)
        {
            if (string.Equals(NormalizedPath(key), full, StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(given) ? given : null;
            }
        }
        if (!options.UseSubtitleFilesBesideMovies)
        {
            return null;
        }
        string stem = Path.Combine(Path.GetDirectoryName(moviePath) ?? ".", Path.GetFileNameWithoutExtension(moviePath));
        return new[] { stem + ".srt", stem + ".en.srt", stem + ".eng.srt" }.FirstOrDefault(File.Exists);
    }

    private static string NormalizedPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>One frame of the movie at <paramref name="seconds"/>, 640 pixels wide, as PNG; false when FFmpeg fails.</summary>
    private static async Task<bool> ExtractStillAsync(string ffmpegExe, string moviePath, double seconds, string pngPath, CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string a in new[] { "-y", "-ss", Math.Max(0, seconds).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                     "-i", moviePath, "-frames:v", "1", "-vf", "scale=640:-2", pngPath })
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return false;
            }
            var err = proc.StandardError.ReadToEndAsync(cancellationToken);
            var outp = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await Task.WhenAll(err, outp, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(err, outp).ConfigureAwait(false); } catch { }
                try { if (File.Exists(pngPath)) File.Delete(pngPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            bool ok = proc.ExitCode == 0 && File.Exists(pngPath) && new FileInfo(pngPath).Length > 0;
            if (!ok)
            {
                try { if (File.Exists(pngPath)) File.Delete(pngPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            try { if (File.Exists(pngPath)) File.Delete(pngPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private static void DeleteSubtitleFiles(string? idxPath)
    {
        if (idxPath == null)
        {
            return;
        }
        try { Directory.Delete(Path.GetDirectoryName(idxPath)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Resolves the display title for each movie from <see cref="DvdAuthoringOptions.TitleNames"/>,
    /// <see cref="DvdAuthoringOptions.OrderedTitleNames"/>, or the cleaned filename.
    /// </summary>
    public static IReadOnlyList<string> ResolveTitleNames(
        IReadOnlyList<string> movieFilePaths,
        IReadOnlyList<string> cleanedMovieTitles,
        DvdAuthoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(movieFilePaths);
        ArgumentNullException.ThrowIfNull(cleanedMovieTitles);
        ArgumentNullException.ThrowIfNull(options);

        static string ClampTitle(string value)
        {
            string trimmed = value.Trim();
            return trimmed.Length > 80 ? trimmed[..80] : trimmed;
        }

        var result = new List<string>(movieFilePaths.Count);
        for (int i = 0; i < movieFilePaths.Count; i++)
        {
            string path = movieFilePaths[i];
            string full = NormalizedPath(path);
            string? custom = null;

            if (options.TitleNames.TryGetValue(full, out string? byFull) && !string.IsNullOrWhiteSpace(byFull))
            {
                custom = byFull;
            }
            else if (options.TitleNames.TryGetValue(path, out string? byRaw) && !string.IsNullOrWhiteSpace(byRaw))
            {
                custom = byRaw;
            }
            else
            {
                foreach (var kv in options.TitleNames)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value) &&
                        string.Equals(NormalizedPath(kv.Key), full, StringComparison.OrdinalIgnoreCase))
                    {
                        custom = kv.Value;
                        break;
                    }
                }
            }

            if (custom != null)
            {
                result.Add(ClampTitle(custom));
            }
            else if (i < options.OrderedTitleNames.Count && !string.IsNullOrWhiteSpace(options.OrderedTitleNames[i]))
            {
                result.Add(ClampTitle(options.OrderedTitleNames[i]));
            }
            else if (i < cleanedMovieTitles.Count && !string.IsNullOrWhiteSpace(cleanedMovieTitles[i]))
            {
                result.Add(cleanedMovieTitles[i]);
            }
            else
            {
                result.Add(DvdInputNormalizer.CleanMovieTitle(path));
            }
        }
        return result;
    }

    public static DvdBitratePlan PlanVideoBitrate(IReadOnlyList<double> titleSeconds, DvdAuthoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(titleSeconds);
        ArgumentNullException.ThrowIfNull(options);
        int effectiveAudioKbps = options.AudioBitrateKbps;
        if (options.DefaultAudioTracks.Count > 0)
        {
            effectiveAudioKbps = Math.Max(effectiveAudioKbps,
                options.DefaultAudioTracks.Take(DvdVideoProject.MaxAudioTracksPerTitle).Sum(a => a.EffectiveBitrateKbps(options.AudioBitrateKbps)));
        }
        foreach (var list in options.TitleAudioTracks.Values)
        {
            if (list.Count > 0)
            {
                effectiveAudioKbps = Math.Max(effectiveAudioKbps,
                    list.Take(DvdVideoProject.MaxAudioTracksPerTitle).Sum(a => a.EffectiveBitrateKbps(options.AudioBitrateKbps)));
            }
        }

        double total = titleSeconds.Where(s => s > 0).Sum();
        if (!options.FitToDisc)
        {
            int fixedKbps = Math.Clamp(options.VideoBitrateKbps, MinimumVideoKbps, 9000);
            int fixedMaxKbps = Math.Clamp(Math.Max(options.MaxVideoBitrateKbps, fixedKbps), fixedKbps, Math.Max(fixedKbps, 9800 - effectiveAudioKbps));
            double targetBytes = options.DualLayer ? Dvd9PayloadBytes : Dvd5PayloadBytes;
            long projectedBytes = total > 0
                ? (long)Math.Ceiling((fixedKbps + effectiveAudioKbps) * 1000.0 / 8.0 * MuxOverhead * total)
                : 0;
            string? overNote = projectedBytes > targetBytes
                ? $"At {fixedKbps} kbps ({FormatSeconds(total)}), the authored VOBs (~{projectedBytes / 1_000_000_000.0:F2} GB) may exceed {(options.DualLayer ? "DVD-9" : "DVD-5")} capacity."
                : null;
            return new DvdBitratePlan(fixedKbps, fixedMaxKbps, overNote);
        }
        if (total <= 60)
        {
            return new DvdBitratePlan(options.VideoBitrateKbps, options.MaxVideoBitrateKbps, null);
        }

        static int TotalKbps(double bytes, double seconds) => (int)(bytes * 8.0 / 1000.0 / MuxOverhead / seconds);

        int allowedKbps;
        string target;
        string? warning = null;
        if (!options.DualLayer)
        {
            allowedKbps = TotalKbps(Dvd5PayloadBytes, total);
            target = $"fit {FormatSeconds(total)} onto Single-Layer DVD-5";
        }
        else
        {
            allowedKbps = TotalKbps(Dvd9PayloadBytes, total);
            target = $"fit {FormatSeconds(total)} onto dual-layer DVD-9";
            double before = 0;
            int titlesOnLayer0 = 0;
            for (int i = 0; i < titleSeconds.Count - 1; i++)
            {
                before += Math.Max(0, titleSeconds[i]);
                if (before * 2 >= total)
                {
                    titlesOnLayer0 = i + 1;
                    break;
                }
            }

            if (titlesOnLayer0 > 0)
            {
                int layer0Kbps = TotalKbps(Dvd9Layer0PayloadBytes, before);
                if (layer0Kbps < allowedKbps)
                {
                    allowedKbps = layer0Kbps;
                    target = $"fit title{(titlesOnLayer0 > 1 ? "s 1-" + titlesOnLayer0 : " 1")} ({FormatSeconds(before)}) onto the first layer of a DVD-9, " +
                             $"so title {titlesOnLayer0 + 1} starts where the layer break can go";
                }
            }
            else
            {
                warning = "No title start can take the layer break of a dual-layer disc (there is one title, or the last title runs longer " +
                          "than all the others together). Put a longer title first, or burn with the Windows writer, which places the break itself.";
            }
        }

        int fittedVideoKbps = Math.Clamp(allowedKbps - effectiveAudioKbps, MinimumVideoKbps, options.VideoBitrateKbps);
        if (fittedVideoKbps >= options.VideoBitrateKbps)
        {
            return new DvdBitratePlan(options.VideoBitrateKbps, options.MaxVideoBitrateKbps, warning);
        }

        int maxKbps = Math.Clamp((int)(fittedVideoKbps * 1.35), fittedVideoKbps + 500, options.MaxVideoBitrateKbps);
        string note = $"Auto-adjusted video bitrate to {fittedVideoKbps} kbps (max {maxKbps} kbps) to {target}.";
        return new DvdBitratePlan(fittedVideoKbps, maxKbps, warning == null ? note : note + " " + warning);
    }

    /// <summary>
    /// Creates a compliant sectorCount * 2048 byte VOB buffer containing a valid NAV pack at sector 0
    /// and alternating MPEG-2 Video & AC-3 Audio PES sectors.
    /// </summary>
    public static byte[] CreateSyntheticMovieVob(int sectorCount = 32, int titleNumber = 1)
    {
        sectorCount = Math.Max(16, sectorCount);
        byte[] vob = new byte[sectorCount * OpticalConstants.DataSectorSize];

        for (int s = 1; s < sectorCount; s++)
        {
            int offset = s * OpticalConstants.DataSectorSize;

            // Pack Header (14 bytes)
            byte[] packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
            packHeader.CopyTo(vob, offset);

            if (s % 2 == 1)
            {
                // MPEG-2 Video PES packet
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xE0;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(OpticalConstants.DataSectorSize - 20));
                vob[offset + 20] = 0x80;
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;

                // MPEG-2 Sequence Header (00 00 01 B3)
                vob[offset + 23] = 0x00;
                vob[offset + 24] = 0x00;
                vob[offset + 25] = 0x01;
                vob[offset + 26] = 0xB3;
                vob[offset + 27] = 0x2D; // 720 width
                vob[offset + 28] = 0x01;
                vob[offset + 29] = 0xE0; // 480 height
            }
            else
            {
                // AC-3 Audio PES packet
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xBD;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(OpticalConstants.DataSectorSize - 20));
                vob[offset + 20] = 0x80;
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;
                vob[offset + 23] = 0x80; // AC-3 Substream 0
                vob[offset + 24] = 0x0B; // sync frame
                vob[offset + 25] = 0x77;
            }
        }

        DvdIfoGenerator.PatchVobNavPackets(vob, vobId: 1, cellId: 1, isMenu: false);
        return vob;
    }

    /// <summary>
    /// Authors DVD-Video from a <see cref="DvdVideoProject"/> with per-title audio and subtitle track definitions.
    /// </summary>
    public Task<DvdAuthoringResult> AuthorAsync(
        DvdVideoProject project,
        string outputRootDirectory,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var options = project.ToAuthoringOptions();
        return AuthorAsync(project.Titles.Select(t => t.MoviePath), outputRootDirectory, options, progress, cancellationToken);
    }

    /// <summary>
    /// Authors DVD-Video from a single path (movie file, directory, or semicolon-delimited list).
    /// </summary>
    public Task<DvdAuthoringResult> AuthorAsync(
        string inputPath,
        string outputRootDirectory,
        DvdAuthoringOptions? options = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var paths = inputPath.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return AuthorAsync(paths, outputRootDirectory, options, progress, cancellationToken);
    }

    private static readonly Regex FfmpegDurationRegex = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegTimeRegex = new(
        @"time=\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegSpeedRegex = new(
        @"speed=\s*(?<spd>[\d\.]+x)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Authors DVD-Video from multiple input paths.
    /// </summary>
    public Task<DvdAuthoringResult> AuthorAsync(
        IEnumerable<string> inputPaths,
        string outputRootDirectory,
        DvdAuthoringOptions? options = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootDirectory);
        var pathList = inputPaths.ToList();

        return Task.Run(
            () => AuthorCoreAsync(pathList, outputRootDirectory, options, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<DvdAuthoringResult> AuthorCoreAsync(
        IReadOnlyList<string> inputPaths,
        string outputRootDirectory,
        DvdAuthoringOptions? options,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken cancellationToken)
    {
        options ??= new DvdAuthoringOptions();
        var logNotes = new List<string>();

        progress?.Report((2.0, "Analyzing input files and directory structures..."));
        var analyzed = DvdInputNormalizer.AnalyzeInputs(inputPaths);
        foreach (var note in analyzed.NormalizationNotes)
        {
            logNotes.Add(note);
        }

        if (analyzed.Kind == DvdInputKind.EmptyOrInvalid)
        {
            return new DvdAuthoringResult
            {
                Success = false,
                ErrorMessage = "No valid DVD-Video directory or supported movie files were found in the provided input.",
                InputKind = DvdInputKind.EmptyOrInvalid,
                LogNotes = logNotes
            };
        }

        // Determine destination paths
        string rootDir = Path.GetFullPath(outputRootDirectory);
        string videoTsDir;
        string audioTsDir;

        string trimmedName = Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(trimmedName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            videoTsDir = rootDir;
            rootDir = Path.GetDirectoryName(rootDir) ?? rootDir;
            audioTsDir = Path.Combine(rootDir, "AUDIO_TS");
        }
        else
        {
            videoTsDir = Path.Combine(rootDir, "VIDEO_TS");
            audioTsDir = Path.Combine(rootDir, "AUDIO_TS");
        }

        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(videoTsDir);
        Directory.CreateDirectory(audioTsDir);

        progress?.Report((5.0, "DVD directories prepared. Processing video content..."));

        string volumeLabel = string.IsNullOrWhiteSpace(options.VolumeLabel) || options.VolumeLabel == "DVD_VIDEO"
            ? analyzed.SuggestedVolumeLabel
            : DvdInputNormalizer.SanitizeVolumeLabel(options.VolumeLabel);

        string menuTitle = string.IsNullOrWhiteSpace(options.MenuTitle) || options.MenuTitle == "DVD Video"
            ? analyzed.SuggestedMenuTitle
            : options.MenuTitle;

        int titleCount = 0;

        if (analyzed.Kind == DvdInputKind.CompliantVideoTs)
        {
            progress?.Report((30.0, "Copying compliant VIDEO_TS files..."));
            string srcVideoTs = analyzed.ExistingVideoTsPath!;

            if (!string.Equals(Path.GetFullPath(srcVideoTs), Path.GetFullPath(videoTsDir), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.GetFiles(srcVideoTs, "*.*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string dst = Path.Combine(videoTsDir, Path.GetFileName(file).ToUpperInvariant());
                    File.Copy(file, dst, overwrite: true);
                }
            }

            var valCheck = new DvdVideoValidator().ValidateDirectory(videoTsDir);
            titleCount = valCheck.TitleSetNumbers.Count;
            logNotes.Add($"Used existing compliant VIDEO_TS with {titleCount} Title Set(s).");
        }
        else if (analyzed.Kind == DvdInputKind.RepairableVideoTs)
        {
            progress?.Report((30.0, "Repairing and normalizing VIDEO_TS files..."));
            DvdInputNormalizer.RepairVideoTsDirectory(analyzed.ExistingVideoTsPath!, videoTsDir, logNotes);

            // If menu requested and VIDEO_TS.VOB missing, synthesize
            string menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
            if (options.AutoCreatePlayMenu && !File.Exists(menuVobPath))
            {
                var menuOpts = new DvdMenuOptions
                {
                    MenuTitle = menuTitle,
                    Standard = options.Standard,
                    AspectRatio = options.AspectRatio
                };

                byte[] menuBytes = await DvdMenuGenerator.GenerateMenuVobAsync(
                    menuOpts,
                    options.CustomFfmpegPath,
                    options.UseFfmpegWhenAvailable,
                    cancellationToken).ConfigureAwait(false);

                await File.WriteAllBytesAsync(menuVobPath, menuBytes, cancellationToken).ConfigureAwait(false);
                logNotes.Add("Created default play menu 'VIDEO_TS.VOB'.");
            }

            var valCheck = new DvdVideoValidator().ValidateDirectory(videoTsDir);
            titleCount = valCheck.TitleSetNumbers.Count;
        }
        else if (analyzed.Kind == DvdInputKind.MovieFiles)
        {
            // Subtitles first: generating them is the step most likely to fail, and it should fail before the transcode.
            string? subtitleError = await DvdSubtitleSourceResolver.ResolveAsync(
                analyzed.MovieFilePaths, options, logNotes, progress, SubtitleGenerator, cancellationToken).ConfigureAwait(false);
            if (subtitleError != null)
            {
                return new DvdAuthoringResult
                {
                    Success = false,
                    ErrorMessage = subtitleError,
                    InputKind = analyzed.Kind,
                    LogNotes = logNotes
                };
            }

            titleCount = Math.Min(analyzed.MovieFilePaths.Count, 99);
            var resolvedTitles = ResolveTitleNames(analyzed.MovieFilePaths, analyzed.CleanedMovieTitles, options);
            int menuVobSectors = 0;
            IReadOnlyList<DvdVobu> menuVobus = Array.Empty<DvdVobu>();
            IReadOnlyList<DvdMenuPageCell> menuPages = Array.Empty<DvdMenuPageCell>();
            bool menuHasButtons = false;
            bool menuHasAudio = false;
            var menuStyle = new DvdMenuStyle(
                options.MenuFontPreset,
                options.MenuHighlightPreset,
                options.MenuBackgroundImagePath,
                options.MenuAudioPath,
                options.MenuAudioDurationSeconds);
            uint[] menuPalette = DvdIfoGenerator.BuildMenuPalette(options.MenuHighlightPreset);
            int titleMenuPageCount = options.AutoCreatePlayMenu
                ? DvdStillMenu.GetTitleMenuPageCount(titleCount, options.GenerateChapterMenus)
                : 0;

            // Probe movie durations if FFmpeg is available, to fit the bitrate to the disc, pick menu stills and show progress
            string? ffmpegExe = null;
            bool ffmpegReady = options.UseFfmpegWhenAvailable &&
                               FfmpegLocator.IsFfmpegAvailable(out ffmpegExe, options.CustomFfmpegPath) &&
                               !string.IsNullOrEmpty(ffmpegExe);

            var probedDurations = new double[titleCount];
            var toneMapFilters = new string?[titleCount];
            if (ffmpegReady)
            {
                for (int i = 0; i < titleCount; i++)
                {
                    var probed = await ProbeMovieAsync(ffmpegExe!, analyzed.MovieFilePaths[i], cancellationToken).ConfigureAwait(false);
                    probedDurations[i] = probed.Seconds;
                    toneMapFilters[i] = HdrToneMapping.ToneMapFilter(probed.Color);
                    if (toneMapFilters[i] != null)
                    {
                        logNotes.Add($"'{Path.GetFileName(analyzed.MovieFilePaths[i])}': HDR ({probed.Color.Transfer}) source, tone-mapped to standard range (BT.709).");
                    }
                }

                // HDR passed through without zscale/tonemap would look washed out: stop rather than author that.
                if (toneMapFilters.Any(f => f != null))
                {
                    var filterLines = new List<string>();
                    await new CdBurner.Core.Subtitles.ProcessToolRunner().RunAsync(ffmpegExe!, ["-hide_banner", "-filters"],
                        Path.GetTempPath(), filterLines.Add, cancellationToken).ConfigureAwait(false);
                    if (!HdrToneMapping.FilterListingHasToneMapping(filterLines))
                    {
                        return new DvdAuthoringResult
                        {
                            Success = false,
                            ErrorMessage = "The movie is HDR video, and this FFmpeg build has no zscale and tonemap filters to convert it; " +
                                           "without them the picture would look washed out. Use an FFmpeg build with zimg (the 1-click download has it).",
                            InputKind = analyzed.Kind,
                            LogNotes = logNotes
                        };
                    }
                }
            }

            // 1. Generate Paginated Title Menu VOB if requested (supporting 1..99 titles)
            if (options.AutoCreatePlayMenu)
            {
                progress?.Report((8.0, "Generating the DVD title menu..."));
                string menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
                string? stills = ffmpegReady
                    ? Path.Combine(Path.GetTempPath(), "IgnisDisc_MenuStills_" + Guid.NewGuid().ToString("N"))
                    : null;
                if (stills != null)
                {
                    Directory.CreateDirectory(stills);
                }

                try
                {
                    var menuTitles = new List<DvdMenuTitle>(titleCount);
                    for (int i = 0; i < titleCount; i++)
                    {
                        string? stillPath = null;
                        if (ffmpegReady && stills != null && titleCount <= 3)
                        {
                            string still = Path.Combine(stills, $"title{i + 1}.png");
                            if (await ExtractStillAsync(ffmpegExe!, analyzed.MovieFilePaths[i], probedDurations[i] * 0.3, still, cancellationToken).ConfigureAwait(false))
                            {
                                stillPath = still;
                            }
                        }
                        string name = i < resolvedTitles.Count ? resolvedTitles[i] : $"Title {i + 1}";
                        bool hasSubs = ResolveSubtitleTracks(analyzed.MovieFilePaths[i], options).Count > 0;
                        menuTitles.Add(new DvdMenuTitle(name, stillPath, hasSubs));
                    }

                    int initialButton = options.SubtitlesOnByDefault && menuTitles.Count > 0 && menuTitles[0].HasSubtitles ? 2 : 1;
                    byte[] TitleMenuCommand(DvdMenuButtonPlan b, int _) => b.Kind switch
                    {
                        DvdMenuButtonKind.NextPage => DvdIfoGenerator.LinkPgc(b.Target),
                        DvdMenuButtonKind.PreviousPage => DvdIfoGenerator.LinkPgc(b.Target),
                        DvdMenuButtonKind.OpenChapters => DvdIfoGenerator.JumpVtsMenu(b.Title, vtsTitleNumber: 1, menuId: 7),
                        _ => DvdIfoGenerator.LinkPgc(DvdIfoGenerator.TitlePlayPgcNumber(b.Title, titleCount, b.WithSubtitles, titleMenuPageCount))
                    };

                    var multiMenu = await DvdStillMenu.BuildMultiPageTitleMenuAsync(
                        menuTitle,
                        menuTitles,
                        options.Standard,
                        options.AspectRatio,
                        options.GenerateChapterMenus,
                        menuStyle,
                        ffmpegReady ? ffmpegExe : null,
                        TitleMenuCommand,
                        cancellationToken,
                        page1InitialButton: initialButton).ConfigureAwait(false);

                    await File.WriteAllBytesAsync(menuVobPath, multiMenu.Vob, cancellationToken).ConfigureAwait(false);
                    menuVobSectors = multiMenu.Vob.Length / OpticalConstants.DataSectorSize;
                    menuVobus = multiMenu.Vobus;
                    menuPages = multiMenu.Pages;
                    menuHasButtons = true;
                    menuHasAudio = multiMenu.UsedAudioStream;
                    int totalButtons = multiMenu.PageButtons.Sum(p => p.Buttons.Count);
                    logNotes.Add($"Generated the title menu 'VIDEO_TS.VOB' ({menuVobSectors} sectors, {menuPages.Count} page(s), {totalButtons} buttons).");
                }
                finally
                {
                    if (stills != null)
                    {
                        try { Directory.Delete(stills, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
            else if (options.AfterTitleAction == DvdAfterTitleAction.ReturnToMenu)
            {
                logNotes.Add("No title menu is enabled; playback will stop after each title instead of returning to a menu.");
            }

            // Video bitrate so feature-length movies fit on the disc (single-layer DVD-5, or DVD-9 with a usable break)
            var bitratePlan = PlanVideoBitrate(probedDurations, options);
            int targetVideoBitrateKbps = bitratePlan.VideoKbps;
            int targetMaxVideoBitrateKbps = bitratePlan.MaxVideoKbps;
            if (bitratePlan.Note != null)
            {
                logNotes.Add(bitratePlan.Note);
            }

            // 2. Process each movie track into a Title Set (VTS_xx)
            var titleTotalSectors = new Dictionary<int, int>();
            var titleSetIfoInputs = new List<TitleSetIfoInput>();

            for (int i = 0; i < titleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int setNum = i + 1;
                string moviePath = analyzed.MovieFilePaths[i];
                string movieName = i < resolvedTitles.Count ? resolvedTitles[i] : $"Title {setNum}";

                double titleStartPct = 12.0 + (70.0 * i / titleCount);
                double titleSpanPct = 70.0 / titleCount;
                progress?.Report((titleStartPct, $"Transcoding Title {setNum}/{titleCount}: '{movieName}'..."));

                // Place tempVob inside videoTsDir so it is on the same filesystem volume for fast 0ms File.Move
                string tempVob = Path.Combine(videoTsDir, $"_temp_transcode_{setNum}_{Guid.NewGuid():N}.vob");
                var resolvedAudioTracks = ResolveAudioTracks(moviePath, options);
                var resolvedSubtitleSpecs = ResolveSubtitleTracks(moviePath, options);
                var preparedSubtitles = PrepareSubtitleTracks(resolvedSubtitleSpecs, options, setNum, writeVobSubFiles: ffmpegReady, logNotes);
                try
                {
                    bool transcodeSuccess = false;
                    double detectedDurationSeconds = probedDurations[i];

                    async Task<(bool, double)> TranscodeAsync(IReadOnlyList<PreparedSubtitleTrack> subs)
                    {
                        var transcodeBuilder = (options.Standard == DvdVideoStandard.Pal
                            ? FfmpegDvdTranscodeBuilder.ForPal(moviePath, tempVob, options.AspectRatio)
                            : FfmpegDvdTranscodeBuilder.ForNtsc(moviePath, tempVob, options.AspectRatio))
                            .WithScaleAndPadFilter(true)
                            .WithToneMapFilter(toneMapFilters[i])
                            .WithVideoBitrate(targetVideoBitrateKbps, targetMaxVideoBitrateKbps)
                            .WithAudio("ac3", options.AudioBitrateKbps);

                        bool useMultiAudio = resolvedAudioTracks.Count > 1 ||
                                             !string.IsNullOrWhiteSpace(resolvedAudioTracks[0].SourcePath) ||
                                             resolvedAudioTracks[0].Channels != 2 ||
                                             !string.Equals(resolvedAudioTracks[0].NormalizedLanguage, "eng", StringComparison.OrdinalIgnoreCase);

                        if (useMultiAudio || subs.Count > 1 || (subs.Count == 1 && !string.Equals(subs[0].Track.NormalizedLanguage, "eng", StringComparison.OrdinalIgnoreCase)))
                        {
                            transcodeBuilder.WithAudioTracks(resolvedAudioTracks.Select(a => new DvdMuxAudioInput(
                                a.SourcePath,
                                a.StreamIndex,
                                a.NormalizedLanguage,
                                a.Channels,
                                a.EffectiveBitrateKbps(options.AudioBitrateKbps))));
                            transcodeBuilder.WithSubtitleTracks(subs
                                .Where(s => s.VobSubIndexPath != null)
                                .Select(s => new DvdMuxSubtitleInput(s.VobSubIndexPath!, s.Track.NormalizedLanguage)));
                        }
                        else if (subs.Count == 1)
                        {
                            transcodeBuilder.WithSubtitles(subs[0].VobSubIndexPath);
                        }

                        return await RunFfmpegTranscodeWithProgressAsync(ffmpegExe!, transcodeBuilder.BuildArgumentList(), tempVob, setNum,
                            titleCount, movieName, detectedDurationSeconds, titleStartPct, titleSpanPct, progress, logNotes, cancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        if (ffmpegReady)
                        {
                            (transcodeSuccess, detectedDurationSeconds) = await TranscodeAsync(preparedSubtitles).ConfigureAwait(false);
                            if (!transcodeSuccess && preparedSubtitles.Count > 0)
                            {
                                // Some FFmpeg builds fail to mux large subtitle pictures; the film matters more than its subtitles.
                                logNotes.Add($"FFmpeg could not write title {setNum} with its subtitles; writing it again without them.");
                                foreach (var sub in preparedSubtitles)
                                {
                                    DeleteSubtitleFiles(sub.VobSubIndexPath);
                                }
                                preparedSubtitles.Clear();
                                try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                                (transcodeSuccess, detectedDurationSeconds) = await TranscodeAsync(preparedSubtitles).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch { }
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        logNotes.Add($"Transcode attempt failed for '{Path.GetFileName(moviePath)}': {ex.Message}.");
                    }

                    if (!transcodeSuccess && ffmpegReady)
                    {
                        // A disc of placeholder video in place of the film is never what was asked for.
                        return new DvdAuthoringResult
                        {
                            Success = false,
                            ErrorMessage = $"FFmpeg could not convert '{movieName}' to DVD video (see the log notes). Nothing was authored; check the file plays, or update FFmpeg.",
                            InputKind = analyzed.Kind,
                            LogNotes = logNotes
                        };
                    }

                    if (!transcodeSuccess)
                    {
                        long inputLen = 0;
                        try { inputLen = new FileInfo(moviePath).Length; } catch { }
                        // Cap synthetic fallback to 4096 sectors max if FFmpeg failed on a large dummy/corrupt file
                        int secCount = (int)Math.Clamp(inputLen / OpticalConstants.DataSectorSize, 32L, 4096L);
                        var muxAudio = resolvedAudioTracks
                            .Select((a, idx) => new Mpeg2AudioMuxStream(idx, a.NormalizedLanguage, a.Channels))
                            .ToList();
                        var muxSubs = preparedSubtitles
                            .Select((s, idx) => new Mpeg2SubtitleMuxStream(idx, s.Track.NormalizedLanguage, s.SpuUnits))
                            .ToList();
                        byte[] synthBytes = Mpeg2Muxer.MuxProgramStream(muxAudio, muxSubs,
                            new Mpeg2MuxOptions(options.Standard, options.AspectRatio, secCount, Math.Max(0, options.ChapterIntervalSeconds)));
                        await File.WriteAllBytesAsync(tempVob, synthBytes, cancellationToken).ConfigureAwait(false);
                        logNotes.Add($"Generated synthetic movie VOB for Title {setNum} ({synthBytes.Length / OpticalConstants.DataSectorSize} sectors, {muxAudio.Count} audio stream(s), {muxSubs.Count} subtitle stream(s)).");
                    }

                    // Patch NAV packets on the contiguous VOB stream before splitting into 1 GB extents
                    double navPct = titleStartPct + titleSpanPct * 0.92;
                    progress?.Report((navPct, $"Patching DVD NAV packets & VOBU address maps for Title {setNum}/{titleCount}..."));
                    var vobus = DvdIfoGenerator.PatchVobNavPacketsInFile(tempVob, vobId: 1, cellId: 1, isMenu: false,
                        chapterSeconds: Math.Max(0, options.ChapterIntervalSeconds)).ToList();

                    // Split oversized VOB (> 1 GB)
                    double splitPct = titleStartPct + titleSpanPct * 0.96;
                    progress?.Report((splitPct, $"Formatting 1 GB DVD VOB extents for Title {setNum}/{titleCount}: '{movieName}'..."));
                    var vobParts = DvdIfoGenerator.SplitVobFileIfOversized(tempVob, videoTsDir, setNum);

                    // Clean up temp transcode file
                    try
                    {
                        if (File.Exists(tempVob) && !vobParts.Contains(tempVob, StringComparer.OrdinalIgnoreCase))
                        {
                            File.Delete(tempVob);
                        }
                    }
                    catch { }

                    int totalVobSecs = 0;
                    for (int p = 0; p < vobParts.Count; p++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string part = vobParts[p];
                        totalVobSecs += (int)(new FileInfo(part).Length / OpticalConstants.DataSectorSize);
                    }

                    vobus.RemoveAll(v => v.Sector >= totalVobSecs);
                    if (vobus.Count == 0)
                    {
                        vobus.Add(new DvdVobu(0, -1, -1));
                    }
                    titleTotalSectors[setNum] = totalVobSecs;

                    // Optional paginated per-title Chapter Selection submenu (VTS_xx_0.VOB)
                    int vtsMenuVobSectors = 0;
                    IReadOnlyList<DvdMenuPageCell> vtsMenuPages = Array.Empty<DvdMenuPageCell>();
                    IReadOnlyList<DvdVobu> vtsMenuVobus = Array.Empty<DvdVobu>();
                    bool vtsMenuHasAudio = false;
                    if (options.AutoCreatePlayMenu && options.GenerateChapterMenus)
                    {
                        var chapters = ExtractChapters(vobus);
                        int mainMenuPage = DvdStillMenu.GetTitleMenuPageForTitle(setNum, titleCount, includeChapters: true);
                        byte[] ChapterMenuCommand(DvdMenuButtonPlan b, int _) => b.Kind switch
                        {
                            DvdMenuButtonKind.NextPage => DvdIfoGenerator.LinkPgc(b.Target),
                            DvdMenuButtonKind.PreviousPage => DvdIfoGenerator.LinkPgc(b.Target),
                            DvdMenuButtonKind.BackToMainMenu => DvdIfoGenerator.JumpVmgmPgc(b.Target),
                            _ => DvdIfoGenerator.JumpVtsPartOfTitle(1, b.Target)
                        };

                        var chapterMenu = await DvdStillMenu.BuildMultiPageChapterMenuAsync(
                            movieName,
                            setNum,
                            mainMenuPage,
                            chapters,
                            options.Standard,
                            options.AspectRatio,
                            menuStyle,
                            ffmpegReady ? ffmpegExe : null,
                            ChapterMenuCommand,
                            cancellationToken).ConfigureAwait(false);

                        string vtsMenuVobPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.VOB");
                        await File.WriteAllBytesAsync(vtsMenuVobPath, chapterMenu.Vob, cancellationToken).ConfigureAwait(false);
                        vtsMenuVobSectors = chapterMenu.Vob.Length / OpticalConstants.DataSectorSize;
                        vtsMenuPages = chapterMenu.Pages;
                        vtsMenuVobus = chapterMenu.Vobus;
                        vtsMenuHasAudio = chapterMenu.UsedAudioStream;
                        logNotes.Add($"Generated chapter submenu 'VTS_{setNum:D2}_0.VOB' ({vtsMenuPages.Count} page(s), {chapters.Count} chapter(s)).");
                    }

                    // Build VTS_xx_0.IFO and VTS_xx_0.BUP
                    bool hasMenu = menuVobSectors > 0;
                    bool chainToNext = options.AfterTitleAction == DvdAfterTitleAction.PlayNextTitle;
                    int nextTitle = chainToNext && setNum < titleCount ? setNum + 1 : 0;
                    int effectiveDurationSec = detectedDurationSeconds > 1.0 ? (int)Math.Round(detectedDurationSeconds) : 60;
                    var audioInfos = resolvedAudioTracks.Select(a => a.ToIfoInfo()).ToList();
                    var subtitleInfos = preparedSubtitles.Select(s => s.Track.ToIfoInfo()).ToList();
                    var vtsInput = new TitleSetIfoInput(
                        setNum,
                        totalVobSecs,
                        effectiveDurationSec,
                        vobus,
                        nextTitle,
                        subtitleInfos.Count > 0,
                        vtsMenuVobSectors,
                        vtsMenuPages,
                        vtsMenuVobus,
                        vtsMenuHasAudio,
                        audioInfos,
                        subtitleInfos);
                    byte[] vtsIfoBytes = BuildVtsIfoFromInput(vtsInput, options, ifoSectors: 0, callMenuAtEnd: hasMenu,
                        menuPalette: menuPalette, vmgMenuPageCount: titleMenuPageCount);

                    string vtsIfoPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.IFO");
                    string vtsBupPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.BUP");

                    await File.WriteAllBytesAsync(vtsIfoPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(vtsBupPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
                    titleSetIfoInputs.Add(vtsInput);
                }
                finally
                {
                    foreach (var sub in preparedSubtitles)
                    {
                        DeleteSubtitleFiles(sub.VobSubIndexPath);
                    }
                    try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }

            // 3. Plan preliminary layout to determine exact VTS start sector offsets
            progress?.Report((86.0, "Building Video Manager (VIDEO_TS.IFO / VIDEO_TS.BUP) navigation tables..."));
            var layoutPlanner = new DvdVideoLayoutPlanner();
            int vmgIfoSectors = DvdIfoGenerator.CalculateVmgIfoSectors(titleCount, menuVobSectors, menuVobus.Count, menuHasButtons,
                menuPageCount: Math.Max(1, menuPages.Count));

            List<IDvdVideoFileEntry> PreliminaryFiles()
            {
                var entries = Directory.GetFiles(videoTsDir)
                    .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(new FileInfo(f)))
                    .ToList();
                if (!entries.Any(f => string.Equals(f.Name, "VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.IFO", new byte[vmgIfoSectors * OpticalConstants.DataSectorSize]));
                }
                if (!entries.Any(f => string.Equals(f.Name, "VIDEO_TS.BUP", StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.BUP", new byte[vmgIfoSectors * OpticalConstants.DataSectorSize]));
                }
                return entries;
            }

            // 3a. Each title's first VOB on a 16-sector ECC block, counted from VIDEO_TS.IFO (which the image builder puts
            //     on a block boundary): the title start is then a cell boundary a dual-layer break can use. The IFO
            //     (and its BUP copy) grows by up to 15 sectors and its pointers follow; VOB files are not touched.
            foreach (var input in titleSetIfoInputs)
            {
                var placement = layoutPlanner.Plan(PreliminaryFiles(), startLba: 0).GetPlacement($"VTS_{input.SetNum:D2}_1.VOB");
                int misalignment = placement == null ? 0 : placement.StartLba.Value % OpticalConstants.DvdEccBlockSectors;
                if (misalignment == 0)
                {
                    continue;
                }
                int currentSectors = (int)(new FileInfo(Path.Combine(videoTsDir, $"VTS_{input.SetNum:D2}_0.IFO")).Length / OpticalConstants.DataSectorSize);
                int ifoSectors = currentSectors + OpticalConstants.DvdEccBlockSectors - misalignment;
                byte[] alignedIfo = BuildVtsIfoFromInput(input, options, ifoSectors, callMenuAtEnd: menuVobSectors > 0,
                    menuPalette: menuPalette, vmgMenuPageCount: titleMenuPageCount);
                await File.WriteAllBytesAsync(Path.Combine(videoTsDir, $"VTS_{input.SetNum:D2}_0.IFO"), alignedIfo, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(videoTsDir, $"VTS_{input.SetNum:D2}_0.BUP"), alignedIfo, cancellationToken).ConfigureAwait(false);
                logNotes.Add($"VTS_{input.SetNum:D2}_0.IFO padded to {ifoSectors} sectors so title {input.SetNum} starts on a 16-sector block.");
            }

            var prelimLayout = layoutPlanner.Plan(PreliminaryFiles(), startLba: 0);

            if (options.DualLayer && prelimLayout.TotalSectors > DvdLayerBreakCalculator.Dvd5SectorLimit)
            {
                var placedFiles = prelimLayout.FilePlacements
                    .GroupBy(p => p.FileName.ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => (g.First().StartLba.Value, g.First().SectorCount));
                var cells = DvdCellLayerBreak.PlaceCells(placedFiles,
                    name => File.Exists(Path.Combine(videoTsDir, name)) ? File.ReadAllBytes(Path.Combine(videoTsDir, name)) : null);
                var chosen = DvdCellLayerBreak.Choose(cells, prelimLayout.TotalSectors);
                if (chosen != null && chosen.Cell.CellId > 1)
                {
                    var match = titleSetIfoInputs.FirstOrDefault(t => t.SetNum == chosen.Cell.TitleSet);
                    if (match != null)
                    {
                        string ifoPath = Path.Combine(videoTsDir, $"VTS_{match.SetNum:D2}_0.IFO");
                        int currentSectors = (int)(new FileInfo(ifoPath).Length / OpticalConstants.DataSectorSize);
                        byte[] updatedIfo = BuildVtsIfoFromInput(match, options, currentSectors, callMenuAtEnd: menuVobSectors > 0,
                            menuPalette: menuPalette, vmgMenuPageCount: titleMenuPageCount, layerBreakCellId: chosen.Cell.CellId);
                        await File.WriteAllBytesAsync(ifoPath, updatedIfo, cancellationToken).ConfigureAwait(false);
                        await File.WriteAllBytesAsync(Path.Combine(videoTsDir, $"VTS_{match.SetNum:D2}_0.BUP"), updatedIfo, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var vtsStartOffsets = new List<int>();
            for (int i = 1; i <= titleCount; i++)
            {
                var ifoPlacement = prelimLayout.GetPlacement($"VTS_{i:D2}_0.IFO");
                int startLba = ifoPlacement?.StartLba.Value ?? (vmgIfoSectors + menuVobSectors + (i - 1) * 32);
                vtsStartOffsets.Add(startLba);
            }

            // 4. Write VIDEO_TS.IFO and identical VIDEO_TS.BUP
            byte[] vmgIfoBytes = DvdIfoGenerator.BuildVmgIfo(
                titleSetCount: titleCount,
                standard: options.Standard,
                aspect: options.AspectRatio,
                menuVobSectors: menuVobSectors,
                vtsStartSectorOffsets: vtsStartOffsets,
                autoPlayMenu: options.AutoCreatePlayMenu && menuVobSectors > 0,
                menuVobus: menuVobus,
                chapterCounts: titleSetIfoInputs.Select(t => t.Vobus.Select(v => v.CellId).Distinct().Count()).ToList(),
                subtitleStreams: titleSetIfoInputs.Select(t => t.Subtitles).ToList(),
                menuHasButtons: menuHasButtons,
                menuAutoPlaySeconds: options.MenuAutoPlaySeconds,
                subtitlesOnByDefault: options.SubtitlesOnByDefault,
                chainTitles: options.AfterTitleAction == DvdAfterTitleAction.PlayNextTitle,
                menuPages: menuPages,
                menuHasAudio: menuHasAudio,
                menuPalette: menuPalette,
                vtsHasMenus: titleSetIfoInputs.Select(t => t.MenuVobSectors > 0).ToList(),
                vtsMenuHasAudio: titleSetIfoInputs.Select(t => t.MenuHasAudio).ToList(),
                titleAudioStreams: titleSetIfoInputs.Select(t => t.AudioStreams).ToList(),
                titleSubtitleStreams: titleSetIfoInputs.Select(t => t.SubtitleStreams).ToList());

            string vmgIfoPath = Path.Combine(videoTsDir, "VIDEO_TS.IFO");
            string vmgBupPath = Path.Combine(videoTsDir, "VIDEO_TS.BUP");

            await File.WriteAllBytesAsync(vmgIfoPath, vmgIfoBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(vmgBupPath, vmgIfoBytes, cancellationToken).ConfigureAwait(false);
            logNotes.Add($"Built compliant 'VIDEO_TS.IFO' and 'VIDEO_TS.BUP' for {titleCount} Title Set(s).");
        }

        // Final Validation & Layout Calculation
        progress?.Report((92.0, "Validating authored DVD structure and calculating 32 KB ECC alignment..."));
        var validator = new DvdVideoValidator();
        var valResult = validator.ValidateDirectory(rootDir);

        var finalFilesOnDisk = Directory.GetFiles(videoTsDir)
            .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(new FileInfo(f)))
            .ToList();

        var finalPlanner = new DvdVideoLayoutPlanner();
        var finalLayout = finalPlanner.Plan(finalFilesOnDisk, startLba: 0);

        long totalBytes = finalFilesOnDisk.Sum(f => f.SizeBytes);
        int totalSectors = finalLayout.TotalSectors;

        var generatedFilesList = Directory.GetFiles(videoTsDir, "*.*", SearchOption.TopDirectoryOnly).ToList();

        progress?.Report((100.0, "DVD-Video authoring complete. Ready to burn!"));

        return new DvdAuthoringResult
        {
            Success = valResult.IsValid,
            ErrorMessage = valResult.IsValid ? null : string.Join("; ", valResult.Errors),
            OutputRootDirectory = rootDir,
            VideoTsDirectory = videoTsDir,
            AudioTsDirectory = audioTsDir,
            VolumeLabel = volumeLabel,
            InputKind = analyzed.Kind,
            TitleCount = titleCount,
            TotalSizeBytes = totalBytes,
            TotalSectors = totalSectors,
            GeneratedFiles = generatedFilesList,
            LogNotes = logNotes,
            ValidationResult = valResult,
            PlannedLayout = finalLayout
        };
    }

    /// <summary>
    /// A movie's running time in seconds, read by FFmpeg (at <paramref name="customFfmpegPath"/>, else the one found);
    /// 0 when FFmpeg is missing or cannot read it.
    /// </summary>
    public static async Task<double> ProbeDurationSecondsAsync(string moviePath, string? customFfmpegPath, CancellationToken cancellationToken = default)
    {
        if (!FfmpegLocator.IsFfmpegAvailable(out string? ffmpegExe, customFfmpegPath) || string.IsNullOrEmpty(ffmpegExe))
        {
            return 0;
        }

        return await ProbeVideoDurationSecondsAsync(ffmpegExe, moviePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<double> ProbeVideoDurationSecondsAsync(string ffmpegExe, string moviePath, CancellationToken cancellationToken) =>
        (await ProbeMovieAsync(ffmpegExe, moviePath, cancellationToken).ConfigureAwait(false)).Seconds;

    /// <summary>
    /// The running time and the colour coding of a movie, both from one FFmpeg input report (<c>-hide_banner -i</c>).
    /// 0 seconds and SDR when FFmpeg cannot be run or cannot read the file.
    /// </summary>
    private static async Task<(double Seconds, VideoColorInfo Color)> ProbeMovieAsync(
        string ffmpegExe,
        string moviePath,
        CancellationToken cancellationToken)
    {
        var sdr = new VideoColorInfo(VideoTransfer.Sdr, null);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(moviePath);

            using var proc = Process.Start(psi);
            if (proc == null) return (0, sdr);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }

            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;
            var match = FfmpegDurationRegex.Match(stderr);
            return (match.Success ? ParseHms(match) : 0, HdrToneMapping.ParseFfmpegInputReport(stderr.Split('\n')));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return (0, sdr);
    }

    private static async Task<(bool Success, double DurationSeconds)> RunFfmpegTranscodeWithProgressAsync(
        string ffmpegExe,
        IReadOnlyList<string> arguments,
        string tempVob,
        int setNum,
        int titleCount,
        string movieName,
        double initialDurationSeconds,
        double startPct,
        double spanPct,
        IProgress<(double Percent, string Status)>? progress,
        List<string> logNotes,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("0");
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return (false, initialDurationSeconds);
        }

        using var killReg = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        double durationSec = initialDurationSeconds;
        long lastReportTick = 0;
        long lastOutput = Stopwatch.GetTimestamp(); // any line from FFmpeg; read by the watchdog below
        bool stalled = false;
        long throttleTicks = Stopwatch.Frequency / 4; // Max 4 UI updates per second

        // Drain stdout in background
        var stdOutTask = Task.Run(async () =>
        {
            try
            {
                char[] buf = new char[512];
                while (await proc.StandardOutput.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false) > 0)
                {
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        // Stream stderr in real time, splitting on \r and \n so FFmpeg progress lines never fill the OS pipe buffer
        var stdErrTask = Task.Run(async () =>
        {
            var sb = new StringBuilder(256);
            char[] buf = new char[512];
            try
            {
                int read;
                while ((read = await proc.StandardError.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                string line = sb.ToString();
                                sb.Clear();
                                ProcessFfmpegLine(line);
                            }
                        }
                        else if (sb.Length < 1024)
                        {
                            sb.Append(c);
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    ProcessFfmpegLine(sb.ToString());
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        void ProcessFfmpegLine(string line)
        {
            Interlocked.Exchange(ref lastOutput, Stopwatch.GetTimestamp());
            if (durationSec <= 0 && line.Contains("Duration:", StringComparison.Ordinal))
            {
                var durMatch = FfmpegDurationRegex.Match(line);
                if (durMatch.Success)
                {
                    durationSec = ParseHms(durMatch);
                }
            }

            if (progress != null && line.Contains("time=", StringComparison.Ordinal))
            {
                long now = Stopwatch.GetTimestamp();
                if (now - lastReportTick < throttleTicks)
                {
                    return;
                }

                var timeMatch = FfmpegTimeRegex.Match(line);
                if (timeMatch.Success)
                {
                    lastReportTick = now;
                    double curSec = ParseHms(timeMatch);
                    var spdMatch = FfmpegSpeedRegex.Match(line);
                    string speedStr = spdMatch.Success ? spdMatch.Groups["spd"].Value : string.Empty;

                    if (durationSec > 0)
                    {
                        double ratio = Math.Clamp(curSec / durationSec, 0.0, 0.99);
                        double pct = startPct + (spanPct * 0.90 * ratio);
                        string status = string.IsNullOrEmpty(speedStr)
                            ? $"Transcoding Title {setNum}/{titleCount}: '{movieName}' — {ratio * 100.0:F1}% ({FormatSeconds(curSec)} / {FormatSeconds(durationSec)})"
                            : $"Transcoding Title {setNum}/{titleCount}: '{movieName}' — {ratio * 100.0:F1}% ({FormatSeconds(curSec)} / {FormatSeconds(durationSec)}, {speedStr})";
                        progress.Report((pct, status));
                    }
                    else
                    {
                        double pct = Math.Min(startPct + spanPct * 0.85, startPct + (curSec % 60));
                        string status = $"Transcoding Title {setNum}/{titleCount}: '{movieName}' ({FormatSeconds(curSec)})...";
                        progress.Report((pct, status));
                    }
                }
            }
        }

        // Watchdog: FFmpeg prints progress about twice a second. One that stays silent this long has hung (an old build
        // was seen to crash while muxing subtitles and never exit); it is stopped so authoring can go on or fail.
        TimeSpan noOutputTimeout = FfmpegNoOutputTimeoutOverride.Value ?? FfmpegNoOutputTimeout;
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(noOutputTimeout.TotalMilliseconds / 2, 25, 5000));
        using var watchdogStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!watchdogStop.IsCancellationRequested && !HasExitedSafe(proc))
                {
                    await Task.Delay(pollInterval, watchdogStop.Token).ConfigureAwait(false);
                    if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastOutput)) > noOutputTimeout && !HasExitedSafe(proc))
                    {
                        Volatile.Write(ref stalled, true);
                        try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!HasExitedSafe(proc))
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false); } catch { }
            throw;
        }
        finally
        {
            watchdogStop.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (Volatile.Read(ref stalled))
        {
            logNotes.Add($"FFmpeg printed nothing for {noOutputTimeout.TotalMinutes:F0} minutes while converting '{movieName}' and was stopped.");
            return (false, durationSec);
        }

        if (proc.ExitCode == 0 && File.Exists(tempVob) && new FileInfo(tempVob).Length >= OpticalConstants.DataSectorSize)
        {
            logNotes.Add($"Transcoded '{movieName}' to DVD MPEG-2 via FFmpeg ({new FileInfo(tempVob).Length / (1024 * 1024):N0} MB).");
            return (true, durationSec);
        }

        logNotes.Add($"FFmpeg exited with code {proc.ExitCode} for '{movieName}'.");
        return (false, durationSec);
    }

    private static bool HasExitedSafe(Process proc)
    {
        try
        {
            return proc.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>How long FFmpeg may print nothing before a transcode counts as hung.</summary>
    internal static TimeSpan FfmpegNoOutputTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Per-async-flow override for <see cref="FfmpegNoOutputTimeout"/> in tests.</summary>
    internal static readonly AsyncLocal<TimeSpan?> FfmpegNoOutputTimeoutOverride = new();

    private static double ParseHms(Match match)
    {
        double h = double.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hv) ? hv : 0;
        double m = double.TryParse(match.Groups["m"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv) ? mv : 0;
        double s = double.TryParse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
        return (h * 3600.0) + (m * 60.0) + s;
    }

    private static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private sealed record TitleSetIfoInput(
        int SetNum,
        int VobSectors,
        int DurationSeconds,
        IReadOnlyList<DvdVobu> Vobus,
        int NextTitle,
        bool Subtitles,
        int MenuVobSectors,
        IReadOnlyList<DvdMenuPageCell> MenuPages,
        IReadOnlyList<DvdVobu> MenuVobus,
        bool MenuHasAudio,
        IReadOnlyList<DvdAudioStreamInfo> AudioStreams,
        IReadOnlyList<DvdSubtitleStreamInfo> SubtitleStreams);

    private static byte[] BuildVtsIfoFromInput(
        TitleSetIfoInput input,
        DvdAuthoringOptions options,
        int ifoSectors,
        bool callMenuAtEnd,
        uint[] menuPalette,
        int vmgMenuPageCount,
        int? layerBreakCellId = null) =>
        DvdIfoGenerator.BuildVtsIfo(
            titleSetNumber: input.SetNum,
            standard: options.Standard,
            aspect: options.AspectRatio,
            titleVobTotalSectors: input.VobSectors,
            durationSeconds: input.DurationSeconds,
            menuVobSectors: input.MenuVobSectors,
            nextTitleNumber: input.NextTitle,
            ifoSectors: ifoSectors,
            vobus: input.Vobus,
            callMenuAtEnd: callMenuAtEnd,
            subtitleStream: input.Subtitles,
            layerBreakCellId: layerBreakCellId,
            menuPages: input.MenuPages,
            menuVobus: input.MenuVobus,
            menuHasButtons: input.MenuVobSectors > 0,
            menuHasAudio: input.MenuHasAudio,
            menuPalette: menuPalette,
            vmgMenuPageCount: vmgMenuPageCount,
            audioStreams: input.AudioStreams,
            subtitleStreams: input.SubtitleStreams);

    private static IReadOnlyList<DvdMenuChapterInfo> ExtractChapters(IReadOnlyList<DvdVobu> vobus)
    {
        var chapters = new List<DvdMenuChapterInfo>();
        long originPts = vobus.Count > 0 && vobus[0].StartPts >= 0 ? vobus[0].StartPts : 0;
        int lastCell = -1;
        foreach (var v in vobus)
        {
            if (v.CellId != lastCell)
            {
                lastCell = v.CellId;
                int chapterNum = chapters.Count + 1;
                TimeSpan start = v.StartPts >= originPts ? TimeSpan.FromSeconds((v.StartPts - originPts) / 90_000.0) : TimeSpan.Zero;
                chapters.Add(new DvdMenuChapterInfo(chapterNum, $"Chapter {chapterNum}", start));
            }
        }
        return chapters.Count > 0 ? chapters : [new DvdMenuChapterInfo(1, "Chapter 1", TimeSpan.Zero)];
    }
}
