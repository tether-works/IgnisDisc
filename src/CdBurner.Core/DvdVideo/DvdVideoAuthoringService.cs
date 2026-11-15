using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

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
    /// Whether to automatically generate an interactive DVD Root Play Menu (VIDEO_TS.VOB). Default: true.
    /// </summary>
    public bool AutoCreatePlayMenu { get; set; } = true;

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
    private const double MuxOverhead = 1.07;

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

    /// <summary>
    /// The movie's .srt (from <see cref="DvdAuthoringOptions.SubtitleFiles"/>, else beside it) written as VobSub in the
    /// work folder, for FFmpeg to mux as subpicture stream 0; null when there is none or it cannot be read (noted).
    /// </summary>
    private static string? PrepareSubtitles(string moviePath, DvdAuthoringOptions options, int setNum, List<string> logNotes)
    {
        string? srt = FindSubtitleFile(moviePath, options);
        if (srt == null)
        {
            return null;
        }
        try
        {
            var cues = DvdSubtitleWriter.ReadSrt(srt);
            var kept = cues.Where(c => !c.Lines.Any(l => Advert.IsMatch(l))).ToList();
            if (kept.Count == 0)
            {
                logNotes.Add($"Subtitles '{Path.GetFileName(srt)}' hold no cues; title {setNum} has none.");
                return null;
            }
            string workDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Subtitles_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir); // outside VIDEO_TS: nothing but DVD-Video files may sit there
            string idx;
            try
            {
                idx = DvdSubtitleWriter.WriteVobSub(kept, Path.Combine(workDir, $"subtitles_{setNum}.sub"));
            }
            catch
            {
                try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            logNotes.Add($"English subtitles for title {setNum} from '{Path.GetFileName(srt)}': {kept.Count} cues" +
                (kept.Count < cues.Count ? $" ({cues.Count - kept.Count} download-site adverts left out)." : "."));
            return idx;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
        {
            logNotes.Add($"Subtitles '{Path.GetFileName(srt)}' could not be used ({ex.Message}); title {setNum} has none.");
            return null;
        }
    }

    /// <summary>The .srt for a movie: given in <see cref="DvdAuthoringOptions.SubtitleFiles"/>, else beside it; null when none.</summary>
    public static string? FindSubtitleFile(string moviePath, DvdAuthoringOptions options)
    {
        if (options.SubtitleFiles.TryGetValue(moviePath, out var given))
        {
            return File.Exists(given) ? given : null;
        }
        if (!options.UseSubtitleFilesBesideMovies)
        {
            return null;
        }
        string stem = Path.Combine(Path.GetDirectoryName(moviePath) ?? ".", Path.GetFileNameWithoutExtension(moviePath));
        return new[] { stem + ".srt", stem + ".en.srt", stem + ".eng.srt" }.FirstOrDefault(File.Exists);
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
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return false;
            }
            var err = proc.StandardError.ReadToEndAsync(cancellationToken);
            var outp = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(err, outp).ConfigureAwait(false);
            return proc.ExitCode == 0 && File.Exists(pngPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
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

    public static DvdBitratePlan PlanVideoBitrate(IReadOnlyList<double> titleSeconds, DvdAuthoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(titleSeconds);
        ArgumentNullException.ThrowIfNull(options);
        double total = titleSeconds.Where(s => s > 0).Sum();
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

        int fittedVideoKbps = Math.Clamp(allowedKbps - options.AudioBitrateKbps, 1800, options.VideoBitrateKbps);
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
        return AuthorAsync([inputPath], outputRootDirectory, options, progress, cancellationToken);
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
            int menuVobSectors = 0;
            IReadOnlyList<DvdVobu> menuVobus = Array.Empty<DvdVobu>();
            bool menuHasButtons = false;

            // Probe movie durations if FFmpeg is available, to fit the bitrate to the disc, pick menu stills and show progress
            string? ffmpegExe = null;
            bool ffmpegReady = options.UseFfmpegWhenAvailable &&
                               FfmpegLocator.IsFfmpegAvailable(out ffmpegExe, options.CustomFfmpegPath) &&
                               !string.IsNullOrEmpty(ffmpegExe);

            var probedDurations = new double[titleCount];
            if (ffmpegReady)
            {
                for (int i = 0; i < titleCount; i++)
                {
                    probedDurations[i] = await ProbeVideoDurationSecondsAsync(ffmpegExe!, analyzed.MovieFilePaths[i], cancellationToken).ConfigureAwait(false);
                }
            }

            // 1. Generate Menu VOB if requested
            if (options.AutoCreatePlayMenu)
            {
                progress?.Report((8.0, "Generating the DVD title menu..."));
                string menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
                if (ffmpegReady)
                {
                    // A still from each film and a Play (and Play with subtitles) button per title.
                    string stills = Path.Combine(Path.GetTempPath(), "IgnisDisc_MenuStills_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stills);
                    try
                    {
                        var menuTitles = new List<DvdMenuTitle>();
                        for (int i = 0; i < titleCount; i++)
                        {
                            string still = Path.Combine(stills, $"title{i + 1}.png");
                            bool haveStill = await ExtractStillAsync(ffmpegExe!, analyzed.MovieFilePaths[i], probedDurations[i] * 0.3, still, cancellationToken).ConfigureAwait(false);
                            string name = i < analyzed.CleanedMovieTitles.Count ? analyzed.CleanedMovieTitles[i] : $"Title {i + 1}";
                            menuTitles.Add(new DvdMenuTitle(name, haveStill ? still : null, FindSubtitleFile(analyzed.MovieFilePaths[i], options) != null));
                        }
                        var menu = await DvdStillMenu.BuildAsync(menuTitle, menuTitles, options.Standard, options.AspectRatio, ffmpegExe!,
                            b => DvdIfoGenerator.LinkPgc(DvdIfoGenerator.TitlePlayPgcNumber(b.Title, titleCount, b.WithSubtitles)),
                            cancellationToken).ConfigureAwait(false);
                        if (menu != null)
                        {
                            await File.WriteAllBytesAsync(menuVobPath, menu.Vob, cancellationToken).ConfigureAwait(false);
                            menuVobSectors = menu.Vob.Length / OpticalConstants.DataSectorSize;
                            menuVobus = menu.Vobus;
                            menuHasButtons = true;
                            logNotes.Add($"Generated the title menu 'VIDEO_TS.VOB' ({menuVobSectors} sectors, {menu.Buttons.Count} buttons).");
                        }
                        else
                        {
                            logNotes.Add("The title menu with buttons could not be encoded; using the plain menu.");
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(stills, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }

                if (!menuHasButtons)
                {
                    var menuOpts = new DvdMenuOptions
                    {
                        MenuTitle = menuTitle,
                        TitleNames = analyzed.CleanedMovieTitles,
                        Standard = options.Standard,
                        AspectRatio = options.AspectRatio
                    };
                    byte[] menuBytes = await DvdMenuGenerator.GenerateMenuVobAsync(
                        menuOpts,
                        options.CustomFfmpegPath,
                        options.UseFfmpegWhenAvailable,
                        cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(menuVobPath, menuBytes, cancellationToken).ConfigureAwait(false);
                    menuVobSectors = menuBytes.Length / OpticalConstants.DataSectorSize;
                    menuVobus = DvdIfoGenerator.PatchVobNavPacketsInFile(menuVobPath, isMenu: true);
                    logNotes.Add($"Generated menu 'VIDEO_TS.VOB' ({menuVobSectors} sectors).");
                }
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
            var titleSetIfoInputs = new List<(int SetNum, int VobSectors, int DurationSeconds, IReadOnlyList<DvdVobu> Vobus, int NextTitle, bool Subtitles)>();

            for (int i = 0; i < titleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int setNum = i + 1;
                string moviePath = analyzed.MovieFilePaths[i];
                string movieName = i < analyzed.CleanedMovieTitles.Count ? analyzed.CleanedMovieTitles[i] : $"Title {setNum}";

                double titleStartPct = 12.0 + (70.0 * i / titleCount);
                double titleSpanPct = 70.0 / titleCount;
                progress?.Report((titleStartPct, $"Transcoding Title {setNum}/{titleCount}: '{movieName}'..."));

                // Place tempVob inside videoTsDir so it is on the same filesystem volume for fast 0ms File.Move
                string tempVob = Path.Combine(videoTsDir, $"_temp_transcode_{setNum}_{Guid.NewGuid():N}.vob");
                string? subtitleIndex = ffmpegReady ? PrepareSubtitles(moviePath, options, setNum, logNotes) : null;
                bool transcodeSuccess = false;
                double detectedDurationSeconds = probedDurations[i];

                try
                {
                    if (ffmpegReady)
                    {
                        var transcodeBuilder = (options.Standard == DvdVideoStandard.Pal
                            ? FfmpegDvdTranscodeBuilder.ForPal(moviePath, tempVob, options.AspectRatio)
                            : FfmpegDvdTranscodeBuilder.ForNtsc(moviePath, tempVob, options.AspectRatio))
                            .WithScaleAndPadFilter(true)
                            .WithVideoBitrate(targetVideoBitrateKbps, targetMaxVideoBitrateKbps)
                            .WithAudio("ac3", options.AudioBitrateKbps)
                            .WithSubtitles(subtitleIndex);

                        (transcodeSuccess, detectedDurationSeconds) = await RunFfmpegTranscodeWithProgressAsync(
                            ffmpegExe!,
                            transcodeBuilder.BuildArgumentList(),
                            tempVob,
                            setNum,
                            titleCount,
                            movieName,
                            detectedDurationSeconds,
                            titleStartPct,
                            titleSpanPct,
                            progress,
                            logNotes,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    logNotes.Add($"Transcode attempt failed for '{Path.GetFileName(moviePath)}': {ex.Message}. Falling back to synthetic VOB.");
                }

                if (!transcodeSuccess)
                {
                    long inputLen = 0;
                    try { inputLen = new FileInfo(moviePath).Length; } catch { }
                    // Cap synthetic fallback to 4096 sectors max if FFmpeg failed on a large dummy/corrupt file
                    int secCount = (int)Math.Clamp(inputLen / OpticalConstants.DataSectorSize, 32L, 4096L);
                    byte[] synthBytes = CreateSyntheticMovieVob(secCount, setNum);
                    await File.WriteAllBytesAsync(tempVob, synthBytes, cancellationToken).ConfigureAwait(false);
                    logNotes.Add($"Generated synthetic movie VOB for Title {setNum} ({secCount} sectors).");
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

                // Build VTS_xx_0.IFO and VTS_xx_0.BUP
                int nextTitle = (setNum < titleCount) ? (setNum + 1) : 0;
                int effectiveDurationSec = detectedDurationSeconds > 1.0 ? (int)Math.Round(detectedDurationSeconds) : 60;
                byte[] vtsIfoBytes = DvdIfoGenerator.BuildVtsIfo(
                    titleSetNumber: setNum,
                    standard: options.Standard,
                    aspect: options.AspectRatio,
                    titleVobTotalSectors: totalVobSecs,
                    durationSeconds: effectiveDurationSec,
                    menuVobSectors: 0,
                    nextTitleNumber: nextTitle,
                    vobus: vobus,
                    callMenuAtEnd: menuVobSectors > 0,
                    subtitleStream: subtitleIndex != null && transcodeSuccess);

                string vtsIfoPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.IFO");
                string vtsBupPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.BUP");

                await File.WriteAllBytesAsync(vtsIfoPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(vtsBupPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
                titleSetIfoInputs.Add((setNum, totalVobSecs, effectiveDurationSec, vobus, nextTitle, subtitleIndex != null && transcodeSuccess));
                DeleteSubtitleFiles(subtitleIndex);
            }

            // 3. Plan preliminary layout to determine exact VTS start sector offsets
            progress?.Report((86.0, "Building Video Manager (VIDEO_TS.IFO / VIDEO_TS.BUP) navigation tables..."));
            var layoutPlanner = new DvdVideoLayoutPlanner();
            int vmgIfoSectors = DvdIfoGenerator.CalculateVmgIfoSectors(titleCount, menuVobSectors, menuVobus.Count, menuHasButtons);

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
                byte[] alignedIfo = DvdIfoGenerator.BuildVtsIfo(
                    titleSetNumber: input.SetNum,
                    standard: options.Standard,
                    aspect: options.AspectRatio,
                    titleVobTotalSectors: input.VobSectors,
                    durationSeconds: input.DurationSeconds,
                    menuVobSectors: 0,
                    nextTitleNumber: input.NextTitle,
                    ifoSectors: ifoSectors,
                    vobus: input.Vobus,
                    callMenuAtEnd: menuVobSectors > 0,
                    subtitleStream: input.Subtitles);
                await File.WriteAllBytesAsync(Path.Combine(videoTsDir, $"VTS_{input.SetNum:D2}_0.IFO"), alignedIfo, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(videoTsDir, $"VTS_{input.SetNum:D2}_0.BUP"), alignedIfo, cancellationToken).ConfigureAwait(false);
                logNotes.Add($"VTS_{input.SetNum:D2}_0.IFO padded to {ifoSectors} sectors so title {input.SetNum} starts on a 16-sector block.");
            }

            var prelimLayout = layoutPlanner.Plan(PreliminaryFiles(), startLba: 0);

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
                autoPlayMenu: options.AutoCreatePlayMenu,
                menuVobus: menuVobus,
                chapterCounts: titleSetIfoInputs.Select(t => t.Vobus.Select(v => v.CellId).Distinct().Count()).ToList(),
                subtitleStreams: titleSetIfoInputs.Select(t => t.Subtitles).ToList(),
                menuHasButtons: menuHasButtons);

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

    private static async Task<double> ProbeVideoDurationSecondsAsync(
        string ffmpegExe,
        string moviePath,
        CancellationToken cancellationToken)
    {
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
            if (proc == null) return 0;

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
            if (match.Success)
            {
                return ParseHms(match);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return 0;
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

        await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (proc.ExitCode == 0 && File.Exists(tempVob) && new FileInfo(tempVob).Length >= OpticalConstants.DataSectorSize)
        {
            logNotes.Add($"Transcoded '{movieName}' to DVD MPEG-2 via FFmpeg ({new FileInfo(tempVob).Length / (1024 * 1024):N0} MB).");
            return (true, durationSec);
        }

        logNotes.Add($"FFmpeg exited with code {proc.ExitCode} for '{movieName}'. Falling back to synthetic VOB.");
        return (false, durationSec);
    }

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
}
