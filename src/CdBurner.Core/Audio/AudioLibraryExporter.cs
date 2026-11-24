using System.Text.RegularExpressions;

namespace CdBurner.Core.Audio;

/// <summary>One output format. <paramref name="BitrateKbps"/> 0 selects the format's default.</summary>
/// <param name="Format">Output format.</param>
/// <param name="BitrateKbps">Bitrate for MP3, Opus and AAC; 0 = format default.</param>
/// <param name="Mp3VbrQuality">LAME -V quality 0 (best) to 9; replaces the bitrate for MP3 when set, ignored for other formats.</param>
public sealed record AudioExportTarget(AudioRipFormat Format, int BitrateKbps = 0, int? Mp3VbrQuality = null);

/// <summary>Album-level facts used for file names, tags, the folder image and the CUE sheet name.</summary>
public sealed record AudioAlbumInfo(
    string AlbumArtist,
    string Album,
    string? Year,
    string? MusicBrainzReleaseId = null,
    byte[]? CoverBytes = null,
    string? CoverMimeType = null);

/// <summary>What <see cref="AudioLibraryExporter.ExportAsync"/> should write and where.</summary>
/// <param name="Targets">At least one output format; each format only once.</param>
/// <param name="RootFolder">Files go to RootFolder\&lt;rendered naming template&gt;.</param>
/// <param name="NamingTemplate">Template in <see cref="AudioNamingTemplate"/> syntax.</param>
/// <param name="Album">Album artist, title, year and cover.</param>
/// <param name="FfmpegPath">Path to ffmpeg.exe; needed for FLAC, MP3 and Opus.</param>
/// <param name="KeepWav">Keep the rip's working WAV files.</param>
/// <param name="WriteFolderImage">Write folder.jpg (or .png/.gif/.webp) in each album folder when a cover exists.</param>
/// <param name="Overwrite">Replace files that already exist at a destination.</param>
public sealed record AudioExportOptions(
    IReadOnlyList<AudioExportTarget> Targets,
    string RootFolder,
    string NamingTemplate,
    AudioAlbumInfo Album,
    string? FfmpegPath,
    bool KeepWav = false,
    bool WriteFolderImage = true,
    bool Overwrite = false,
    bool ApplyDeEmphasis = false);

/// <summary>One file written by an export.</summary>
public sealed record AudioExportedFile(AudioRipFormat Format, int TrackNumber, string Path);

/// <summary>Everything an export wrote, plus non-fatal problems.</summary>
public sealed record AudioExportResult(
    IReadOnlyList<AudioExportedFile> Files,
    IReadOnlyList<string> CueSheets,
    IReadOnlyList<string> FolderImages,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a finished WAV rip into library files: one or more formats, named by a template, tagged with the album
/// year and cover, with a folder image and a CUE sheet that points at the new files.
/// <para>
/// With more than one target, a template without a <c>{format}</c> token gets <c>{format}\</c> in front of it, so
/// FLAC and MP3 never share a folder or collide on a name.
/// </para>
/// <para>
/// Every file is encoded under a temporary name next to its destination and moved into place when it is complete,
/// so a failed encode never leaves a half-written file at a real name. If anything fails, the files this call
/// created are removed again and the rip's WAV files are left alone.
/// </para>
/// </summary>
public sealed class AudioLibraryExporter
{
    private const string HiddenTrackTitle = "Hidden Track One Audio";

    private static readonly Regex CueFileLine = new(@"^FILE\s+""(?<name>.+)""\s+(?<type>\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAacFileEncoder? _aacEncoder;

    public AudioLibraryExporter(IAacFileEncoder? aacEncoder = null)
    {
        _aacEncoder = aacEncoder;
    }

    /// <summary>
    /// Returns a plain-language problem with <paramref name="options"/> (no format chosen, a format twice, bad
    /// template, bad bitrate or MP3 quality, missing ffmpeg or AAC encoder, empty folder), otherwise null. Call it
    /// before the disc is read.
    /// </summary>
    public string? CheckPrerequisites(AudioExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Targets == null || options.Targets.Count == 0)
        {
            return "Choose at least one output format.";
        }

        if (string.IsNullOrWhiteSpace(options.RootFolder))
        {
            return "The destination folder is empty.";
        }

        string? templateProblem = AudioNamingTemplate.Validate(options.NamingTemplate);
        if (templateProblem != null)
        {
            return templateProblem;
        }

        var seen = new HashSet<AudioRipFormat>();
        foreach (var target in options.Targets)
        {
            string name = AudioRipFormats.DisplayName(target.Format);
            if (!seen.Add(target.Format))
            {
                return $"{name} is chosen more than once; each output format can be used only once.";
            }

            if (target.Mp3VbrQuality is < 0 or > 9)
            {
                return "MP3 quality must be between 0 (best) and 9.";
            }

            bool vbr = target.Format == AudioRipFormat.Mp3 && target.Mp3VbrQuality.HasValue;
            if (vbr && target.BitrateKbps > 0)
            {
                return "MP3 takes either a bitrate or a VBR quality, not both.";
            }

            if (target.BitrateKbps > 0)
            {
                string? bitrateError = AudioRipFormats.ValidateBitrate(target.Format, target.BitrateKbps);
                if (bitrateError != null)
                {
                    return bitrateError;
                }
            }

            if (AudioRipFormats.RequiresFfmpeg(target.Format) &&
                (string.IsNullOrWhiteSpace(options.FfmpegPath) || !File.Exists(options.FfmpegPath)))
            {
                return AudioRipFormats.MissingFfmpegMessage(target.Format);
            }

            if (target.Format == AudioRipFormat.Aac && _aacEncoder == null)
            {
                return "AAC output needs the Windows AAC encoder (Media Foundation), which is not available on this system.";
            }

            if (target.Format == AudioRipFormat.Flac && options.Album?.CoverBytes is { Length: > 0 } cover &&
                FlacEncoder.CoverArtSizeProblem(cover.Length) is { } coverProblem)
            {
                return coverProblem;
            }
        }

        return null;
    }

    private sealed record SourceFile(int TrackNumber, string Title, AudioTrackRipInfo? Info, string Wav);

    private sealed record PlannedFile(SourceFile Source, string Destination);

    private sealed class TargetPlan(AudioExportTarget target)
    {
        public AudioExportTarget Target { get; } = target;
        public List<PlannedFile> Files { get; } = [];
        public string? CuePath { get; set; }
    }

    /// <summary>
    /// Writes the rip in every chosen format. Throws <see cref="InvalidOperationException"/> when a destination
    /// already exists and <see cref="AudioExportOptions.Overwrite"/> is false (nothing is written then) or when an
    /// encoder fails (files written by this call are removed, the WAV files stay).
    /// </summary>
    public async Task<AudioExportResult> ExportAsync(
        AudioCdRipResult rip,
        AudioExportOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rip);
        ArgumentNullException.ThrowIfNull(options);
        string? problem = CheckPrerequisites(options);
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }

        var album = options.Album;
        string albumTitle = !string.IsNullOrWhiteSpace(album.Album) ? album.Album.Trim() : "Unknown Album";
        string albumArtist = !string.IsNullOrWhiteSpace(album.AlbumArtist) ? album.AlbumArtist.Trim() : "Unknown Artist";
        string root = Path.GetFullPath(options.RootFolder);
        var warnings = new List<string>();

        var sources = new List<SourceFile>();
        if (rip.HiddenTrackOneAudioPath is { } htoa && File.Exists(htoa))
        {
            sources.Add(new SourceFile(0, HiddenTrackTitle, null, htoa));
        }

        foreach (var info in rip.TrackInfos)
        {
            string title = !string.IsNullOrWhiteSpace(info.Title) ? info.Title : $"Track {info.TrackNumber:D2}";
            sources.Add(new SourceFile(info.TrackNumber, title, info, info.FilePath));
        }

        foreach (var source in sources)
        {
            if (!File.Exists(source.Wav))
            {
                throw new InvalidOperationException($"The ripped file '{source.Wav}' is missing, so it cannot be exported.");
            }
        }

        // Plan every destination before anything is written, so a conflict stops the export with nothing changed.
        var plans = PlanTargets(rip, options, sources, root, albumArtist, albumTitle, warnings);
        string? cueText = rip.CueSheetPath != null && File.Exists(rip.CueSheetPath)
            ? await File.ReadAllTextAsync(rip.CueSheetPath, cancellationToken).ConfigureAwait(false)
            : null;

        var created = new List<string>();
        var createdDirs = new List<string>();
        var files = new List<AudioExportedFile>();
        var cueSheets = new List<string>();
        var folderImages = new List<string>();
        var keptWavs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? coverTempPath = null;
        var deEmphTempWavs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AudioRipFormat currentFormat = plans[0].Target.Format;

        try
        {
            bool anyMp3 = plans.Any(p => p.Target.Format == AudioRipFormat.Mp3);
            if (album.CoverBytes is { Length: > 0 } && anyMp3)
            {
                coverTempPath = Path.Combine(Path.GetTempPath(), "ignisdisc-cover-" + Guid.NewGuid().ToString("N") + MimeExtension(CoverMime(album)));
                await File.WriteAllBytesAsync(coverTempPath, album.CoverBytes, cancellationToken).ConfigureAwait(false);
            }

            foreach (var plan in plans)
            {
                currentFormat = plan.Target.Format;
                var format = plan.Target.Format;
                int bitrate = plan.Target.BitrateKbps > 0 ? plan.Target.BitrateKbps : AudioRipFormats.DefaultBitrateKbps(format);
                if (album.CoverBytes is { Length: > 0 } && format is AudioRipFormat.Opus or AudioRipFormat.Aac)
                {
                    warnings.Add($"Cover art is not embedded in {AudioRipFormats.DisplayName(format)} files (supported in FLAC and MP3).");
                }

                int done = 0;
                foreach (var planned in plan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    done++;
                    progress?.Report($"{AudioRipFormats.DisplayName(format)} {done}/{plan.Files.Count}: {planned.Source.Title}");

                    string destination = planned.Destination;
                    if (format == AudioRipFormat.Wav && SamePath(destination, planned.Source.Wav))
                    {
                        keptWavs.Add(destination);
                        files.Add(new AudioExportedFile(format, planned.Source.TrackNumber, destination));
                        continue;
                    }

                    string encodeSourceWav = planned.Source.Wav;
                    if (options.ApplyDeEmphasis && planned.Source.Info is { PreEmphasis: true, DeEmphasisApplied: false })
                    {
                        if (!deEmphTempWavs.TryGetValue(planned.Source.Wav, out string? deEmphPath))
                        {
                            deEmphPath = Path.Combine(Path.GetTempPath(), "ignisdisc-deemph-" + Guid.NewGuid().ToString("N") + ".wav");
                            RedBookPcmDspProcessor.ApplyDeEmphasisToWavFile(planned.Source.Wav, deEmphPath);
                            deEmphTempWavs[planned.Source.Wav] = deEmphPath;
                        }
                        encodeSourceWav = deEmphPath;
                    }

                    EnsureDirectory(Path.GetDirectoryName(destination)!, createdDirs);
                    string temp = Path.Combine(Path.GetDirectoryName(destination)!, "~ignis-" + Guid.NewGuid().ToString("N") + AudioRipFormats.Extension(format));
                    try
                    {
                        var tags = BuildTags(planned.Source, rip, albumTitle, albumArtist, album, rip.TrackInfos.Count);
                        string? tagWarning = await EncodeFileAsync(
                            plan.Target, options.FfmpegPath, encodeSourceWav, temp, tags, bitrate, coverTempPath, cancellationToken).ConfigureAwait(false);
                        if (tagWarning != null)
                        {
                            warnings.Add($"Track {planned.Source.TrackNumber:D2}: {tagWarning}");
                        }

                        bool existed = File.Exists(destination);
                        File.Move(temp, destination, overwrite: true);
                        if (!existed)
                        {
                            created.Add(destination);
                        }
                    }
                    finally
                    {
                        if (File.Exists(temp))
                        {
                            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                    }

                    if (format == AudioRipFormat.Wav)
                    {
                        keptWavs.Add(destination);
                    }

                    files.Add(new AudioExportedFile(format, planned.Source.TrackNumber, destination));
                }

                if (album.CoverBytes is { Length: > 0 } && options.WriteFolderImage)
                {
                    foreach (string folder in plan.Files.Select(f => Path.GetDirectoryName(f.Destination)!).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string imagePath = Path.Combine(folder, "folder" + MimeExtension(CoverMime(album)));
                        if (folderImages.Contains(imagePath, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        bool existed = File.Exists(imagePath);
                        if (existed && !options.Overwrite)
                        {
                            continue;
                        }

                        await File.WriteAllBytesAsync(imagePath, album.CoverBytes, cancellationToken).ConfigureAwait(false);
                        if (!existed)
                        {
                            created.Add(imagePath);
                        }

                        folderImages.Add(imagePath);
                    }
                }
            }

            // CUE sheets last: the one next to the rip may be rewritten in place, so nothing is touched until all audio succeeded.
            if (cueText != null)
            {
                foreach (var plan in plans.Where(p => p.CuePath != null))
                {
                    string cuePath = plan.CuePath!;
                    bool existed = File.Exists(cuePath);
                    await File.WriteAllTextAsync(cuePath, RewriteCue(cueText, plan), cancellationToken).ConfigureAwait(false);
                    if (!existed)
                    {
                        created.Add(cuePath);
                    }

                    cueSheets.Add(cuePath);
                }
            }
        }
        catch (Exception ex)
        {
            var leftovers = Rollback(created, createdDirs);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            string message = $"{AudioRipFormats.DisplayName(currentFormat)} export failed: {ex.Message}";
            if (leftovers.Count > 0)
            {
                message += " Could not remove: " + string.Join("; ", leftovers) + ".";
            }

            throw new InvalidOperationException(message, ex);
        }
        finally
        {
            if (coverTempPath != null)
            {
                try { File.Delete(coverTempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (string tmp in deEmphTempWavs.Values)
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        if (!options.KeepWav)
        {
            foreach (var source in sources)
            {
                if (!keptWavs.Contains(Path.GetFullPath(source.Wav)))
                {
                    TryDelete(source.Wav, warnings);
                }
            }

            if (rip.CueSheetPath != null && cueSheets.Count > 0 &&
                !cueSheets.Any(c => SamePath(c, rip.CueSheetPath)))
            {
                TryDelete(rip.CueSheetPath, warnings);
            }
        }

        return new AudioExportResult(files, cueSheets, folderImages, warnings);
    }

    /// <summary>
    /// Finalizes a single-file <see cref="AudioRipMode.ImageCue"/> rip: moves or copies the image and CUE sheet into
    /// the target library folder (when different from the rip folder), embeds cover art into FLAC images if fetched
    /// after ripping, and writes <c>folder.jpg</c>.
    /// </summary>
    public async Task<AudioExportResult> ExportImageCueAsync(
        AudioCdRipResult rip,
        AudioExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rip);
        ArgumentNullException.ThrowIfNull(options);
        if (rip.Mode != AudioRipMode.ImageCue || string.IsNullOrWhiteSpace(rip.ImageFilePath) || !File.Exists(rip.ImageFilePath))
        {
            throw new InvalidOperationException("ExportImageCueAsync requires a completed single-file Image + CUE rip.");
        }
        if (string.IsNullOrWhiteSpace(options.RootFolder))
        {
            throw new InvalidOperationException("The destination folder is empty.");
        }

        string? templateProblem = AudioNamingTemplate.Validate(options.NamingTemplate);
        if (templateProblem != null)
        {
            throw new InvalidOperationException(templateProblem);
        }

        var album = options.Album;
        string albumTitle = !string.IsNullOrWhiteSpace(album.Album) ? album.Album.Trim() : "Unknown Album";
        string albumArtist = !string.IsNullOrWhiteSpace(album.AlbumArtist) ? album.AlbumArtist.Trim() : "Unknown Artist";
        string root = Path.GetFullPath(options.RootFolder);
        string rootPrefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        AudioRipFormat format = string.Equals(Path.GetExtension(rip.ImageFilePath), ".flac", StringComparison.OrdinalIgnoreCase)
            ? AudioRipFormat.Flac
            : AudioRipFormat.Wav;
        if (format == AudioRipFormat.Flac && album.CoverBytes is { Length: > 0 } cover &&
            FlacEncoder.CoverArtSizeProblem(cover.Length) is { } coverProblem)
        {
            throw new InvalidOperationException(coverProblem);
        }

        string sourceDir = Path.GetDirectoryName(Path.GetFullPath(rip.ImageFilePath))!;
        string targetDir = root;
        if (!string.Equals(options.NamingTemplate, AudioNamingTemplate.Flat, StringComparison.Ordinal))
        {
            var sampleFields = new AudioNamingFields(albumArtist, albumArtist, albumTitle, album.Year, 1, rip.TrackInfos.Count, albumTitle, format);
            string renderedSample = AudioNamingTemplate.Render(options.NamingTemplate, sampleFields, AudioRipFormats.Extension(format));
            string? relDir = Path.GetDirectoryName(renderedSample);
            if (!string.IsNullOrEmpty(relDir))
            {
                string candidateDir = Path.GetFullPath(Path.Combine(root, relDir));
                if (!candidateDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The naming template put '{relDir}' outside the destination folder.");
                }
                targetDir = candidateDir;
            }
        }

        string stem = AudioCdRipperService.SanitizeFileName(albumTitle);
        if (string.IsNullOrEmpty(stem) || string.Equals(albumTitle, "Unknown Album", StringComparison.Ordinal))
        {
            stem = Path.GetFileNameWithoutExtension(rip.ImageFilePath);
        }

        bool sameDir = SamePath(sourceDir, targetDir);
        string destImagePath = sameDir
            ? Path.GetFullPath(rip.ImageFilePath)
            : Path.Combine(targetDir, stem + AudioRipFormats.Extension(format));
        string? destCuePath = rip.CueSheetPath != null && File.Exists(rip.CueSheetPath)
            ? (sameDir ? Path.GetFullPath(rip.CueSheetPath) : Path.Combine(targetDir, stem + ".cue"))
            : null;

        if (!sameDir)
        {
            if (File.Exists(destImagePath) && !options.Overwrite)
            {
                throw new InvalidOperationException($"'{destImagePath}' already exists. Use --overwrite (or choose another folder) to replace it.");
            }
            if (destCuePath != null && File.Exists(destCuePath) && !options.Overwrite)
            {
                throw new InvalidOperationException($"'{destCuePath}' already exists. Use --overwrite (or choose another folder) to replace it.");
            }
        }

        var created = new List<string>();
        var createdDirs = new List<string>();
        var cueSheets = new List<string>();
        var folderImages = new List<string>();
        var warnings = new List<string>();

        try
        {
            EnsureDirectory(targetDir, createdDirs);

            if (!sameDir)
            {
                bool imageExisted = File.Exists(destImagePath);
                if (options.KeepWav && format == AudioRipFormat.Wav)
                {
                    File.Copy(rip.ImageFilePath, destImagePath, overwrite: true);
                }
                else
                {
                    File.Move(rip.ImageFilePath, destImagePath, overwrite: true);
                }
                if (!imageExisted)
                {
                    created.Add(destImagePath);
                }

                if (destCuePath != null && rip.CueSheetPath != null && File.Exists(rip.CueSheetPath))
                {
                    string cueText = await File.ReadAllTextAsync(rip.CueSheetPath, cancellationToken).ConfigureAwait(false);
                    string rewritten = RewriteSingleImageCueFileName(cueText, Path.GetFileName(rip.ImageFilePath), Path.GetFileName(destImagePath));
                    bool cueExisted = File.Exists(destCuePath);
                    await File.WriteAllTextAsync(destCuePath, rewritten, cancellationToken).ConfigureAwait(false);
                    if (!cueExisted)
                    {
                        created.Add(destCuePath);
                    }
                    cueSheets.Add(destCuePath);

                    if (!options.KeepWav || format != AudioRipFormat.Wav)
                    {
                        TryDelete(rip.CueSheetPath, warnings);
                    }
                }
            }
            else if (destCuePath != null)
            {
                cueSheets.Add(destCuePath);
            }

            if (format == AudioRipFormat.Flac && album.CoverBytes is { Length: > 0 })
            {
                var tags = new AudioTrackTags(
                    Title: albumTitle,
                    Artist: albumArtist,
                    Album: albumTitle,
                    TrackNumber: 0,
                    TrackTotal: rip.TrackInfos.Count,
                    Date: string.IsNullOrWhiteSpace(album.Year) ? null : album.Year.Trim(),
                    ReplayGainAlbumGain: rip.ReplayGainAlbumGain.HasValue ? ReplayGainCalculator.FormatGainDb(rip.ReplayGainAlbumGain.Value) : null,
                    ReplayGainAlbumPeak: rip.ReplayGainAlbumPeak.HasValue ? ReplayGainCalculator.FormatPeak(rip.ReplayGainAlbumPeak.Value) : null,
                    CoverArtBytes: album.CoverBytes,
                    CoverArtMimeType: CoverMime(album));
                FlacEncoder.EmbedCoverArtAndReplayGain(destImagePath, tags);
            }

            if (album.CoverBytes is { Length: > 0 } && options.WriteFolderImage)
            {
                string imagePath = Path.Combine(targetDir, "folder" + MimeExtension(CoverMime(album)));
                bool existed = File.Exists(imagePath);
                if (!existed || options.Overwrite)
                {
                    await File.WriteAllBytesAsync(imagePath, album.CoverBytes, cancellationToken).ConfigureAwait(false);
                    if (!existed)
                    {
                        created.Add(imagePath);
                    }
                    folderImages.Add(imagePath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var leftovers = Rollback(created, createdDirs);
            string message = $"Image + CUE export failed: {ex.Message}";
            if (leftovers.Count > 0)
            {
                message += " Could not remove: " + string.Join("; ", leftovers) + ".";
            }
            throw new InvalidOperationException(message, ex);
        }

        return new AudioExportResult(
            new[] { new AudioExportedFile(format, 0, destImagePath) },
            cueSheets,
            folderImages,
            warnings);
    }

    private static string RewriteSingleImageCueFileName(string cueText, string oldFileName, string newFileName)
    {
        if (string.Equals(oldFileName, newFileName, StringComparison.Ordinal))
        {
            return cueText;
        }

        string separator = cueText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = cueText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            var match = CueFileLine.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            string prefix = lines[i][..(lines[i].Length - trimmed.Length)];
            lines[i] = $"{prefix}FILE \"{newFileName}\" {match.Groups["type"].Value.ToUpperInvariant()}";
        }

        return string.Join(separator, lines);
    }

    private static List<TargetPlan> PlanTargets(
        AudioCdRipResult rip,
        AudioExportOptions options,
        List<SourceFile> sources,
        string root,
        string albumArtist,
        string albumTitle,
        List<string> warnings)
    {
        var plans = new List<TargetPlan>();
        var allSources = new HashSet<string>(sources.Select(s => Path.GetFullPath(s.Wav)), StringComparer.OrdinalIgnoreCase);
        string rootPrefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        bool needFormatFolder = options.Targets.Count > 1 &&
            !options.NamingTemplate.Contains("{format}", StringComparison.OrdinalIgnoreCase);
        string template = needFormatFolder ? @"{format}\" + options.NamingTemplate : options.NamingTemplate;
        string? ripCueFull = rip.CueSheetPath != null && File.Exists(rip.CueSheetPath) ? Path.GetFullPath(rip.CueSheetPath) : null;
        var usedCuePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in options.Targets)
        {
            var plan = new TargetPlan(target);
            string extension = AudioRipFormats.Extension(target.Format);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                string performer = source.Info?.Performer ?? string.Empty;
                string artist = !string.IsNullOrWhiteSpace(performer) && performer != "Unknown Artist" ? performer : albumArtist;
                var fields = new AudioNamingFields(
                    albumArtist, artist, albumTitle, options.Album.Year, source.TrackNumber, rip.TrackInfos.Count,
                    source.Title, target.Format);
                string relative = AudioNamingTemplate.Render(template, fields, extension);
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The naming template put '{relative}' outside the destination folder.");
                }

                full = MakeUnique(full, extension, used);
                plan.Files.Add(new PlannedFile(source, full));
            }

            plans.Add(plan);
        }

        foreach (var plan in plans)
        {
            foreach (var planned in plan.Files)
            {
                string destination = planned.Destination;
                bool ownSource = plan.Target.Format == AudioRipFormat.Wav && SamePath(destination, planned.Source.Wav);
                if (ownSource)
                {
                    continue;
                }

                if (allSources.Contains(destination))
                {
                    throw new InvalidOperationException($"The destination '{destination}' is another track's ripped WAV file; choose another naming template or folder.");
                }

                if (Directory.Exists(destination))
                {
                    throw new InvalidOperationException($"The destination '{destination}' is a folder.");
                }

                if (File.Exists(destination) && !options.Overwrite)
                {
                    throw new InvalidOperationException($"'{destination}' already exists. Use --overwrite (or choose another folder) to replace it.");
                }
            }

            if (ripCueFull == null)
            {
                continue;
            }

            var folders = plan.Files.Select(f => Path.GetDirectoryName(f.Destination)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (folders.Count != 1)
            {
                warnings.Add($"{AudioRipFormats.DisplayName(plan.Target.Format)} files are in {folders.Count} folders, so no CUE sheet was written for them.");
                continue;
            }

            // A rip that already sits in the destination folder keeps its CUE file name (the sheet is rewritten in place).
            string cuePath = SamePath(folders[0], Path.GetDirectoryName(ripCueFull)!)
                ? ripCueFull
                : Path.Combine(folders[0], CueFileName(options.Album));
            if (!usedCuePaths.Add(cuePath))
            {
                warnings.Add($"The CUE sheet for {AudioRipFormats.DisplayName(plan.Target.Format)} would share a file with another format, so it was not written.");
                continue;
            }

            if (!SamePath(cuePath, ripCueFull) && File.Exists(cuePath) && !options.Overwrite)
            {
                throw new InvalidOperationException($"'{cuePath}' already exists. Use --overwrite (or choose another folder) to replace it.");
            }

            plan.CuePath = cuePath;
        }

        return plans;
    }

    private static string CueFileName(AudioAlbumInfo album)
    {
        string name = AudioCdRipperService.SanitizeFileName(album.Album);
        return name.Length == 0 ? "album.cue" : name + ".cue";
    }

    private static string MakeUnique(string fullPath, string extension, HashSet<string> used)
    {
        if (used.Add(fullPath))
        {
            return fullPath;
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        string stem = Path.GetFileName(fullPath);
        stem = stem[..^extension.Length];
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string RewriteCue(string cueText, TargetPlan plan)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var planned in plan.Files)
        {
            names[Path.GetFileName(planned.Source.Wav)] = Path.GetFileName(planned.Destination);
        }

        string fileType = plan.Target.Format == AudioRipFormat.Mp3 ? "MP3" : "WAVE";
        string separator = cueText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = cueText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            var match = CueFileLine.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            string oldName = Path.GetFileName(match.Groups["name"].Value);
            if (names.TryGetValue(oldName, out string? newName))
            {
                string prefix = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{prefix}FILE \"{newName}\" {fileType}";
            }
        }

        return string.Join(separator, lines);
    }

    private async Task<string?> EncodeFileAsync(
        AudioExportTarget target,
        string? ffmpegPath,
        string wavPath,
        string outPath,
        AudioTrackTags tags,
        int bitrate,
        string? coverTempPath,
        CancellationToken cancellationToken)
    {
        switch (target.Format)
        {
            case AudioRipFormat.Wav:
                File.Copy(wavPath, outPath, overwrite: true);
                return null;

            case AudioRipFormat.Flac:
                await FlacEncoder.EncodeAsync(ffmpegPath!, wavPath, outPath, tags, cancellationToken).ConfigureAwait(false);
                if (tags.CoverArtBytes is { Length: > 0 })
                {
                    FlacEncoder.EmbedCoverArtAndReplayGain(outPath, tags);
                }

                return null;

            case AudioRipFormat.Mp3:
                await FfmpegAudioEncoder.EncodeAsync(
                    AudioRipFormat.Mp3, ffmpegPath!, wavPath, outPath, tags, bitrate, coverTempPath, cancellationToken, target.Mp3VbrQuality).ConfigureAwait(false);
                return null;

            case AudioRipFormat.Opus:
                await FfmpegAudioEncoder.EncodeAsync(
                    AudioRipFormat.Opus, ffmpegPath!, wavPath, outPath, tags, bitrate, null, cancellationToken).ConfigureAwait(false);
                return null;

            case AudioRipFormat.Aac:
                return await _aacEncoder!.EncodeAsync(wavPath, outPath, tags, bitrate, cancellationToken).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private static AudioTrackTags BuildTags(
        SourceFile source,
        AudioCdRipResult rip,
        string albumTitle,
        string albumArtist,
        AudioAlbumInfo album,
        int trackTotal)
    {
        var info = source.Info;
        string artist = info != null && !string.IsNullOrWhiteSpace(info.Performer) && info.Performer != "Unknown Artist"
            ? info.Performer
            : albumArtist;
        return new AudioTrackTags(
            Title: source.Title,
            Artist: artist,
            Album: albumTitle,
            TrackNumber: source.TrackNumber,
            TrackTotal: trackTotal,
            Isrc: info?.Isrc,
            Date: string.IsNullOrWhiteSpace(album.Year) ? null : album.Year.Trim(),
            ReplayGainTrackGain: info?.ReplayGainTrackGain is { } tg ? ReplayGainCalculator.FormatGainDb(tg) : null,
            ReplayGainTrackPeak: info?.ReplayGainTrackPeak is { } tp ? ReplayGainCalculator.FormatPeak(tp) : null,
            ReplayGainAlbumGain: rip.ReplayGainAlbumGain.HasValue ? ReplayGainCalculator.FormatGainDb(rip.ReplayGainAlbumGain.Value) : null,
            ReplayGainAlbumPeak: rip.ReplayGainAlbumPeak.HasValue ? ReplayGainCalculator.FormatPeak(rip.ReplayGainAlbumPeak.Value) : null,
            CoverArtBytes: album.CoverBytes,
            CoverArtMimeType: CoverMime(album));
    }

    private static string CoverMime(AudioAlbumInfo album) =>
        (album.CoverBytes != null ? FlacEncoder.DetectImageMimeType(album.CoverBytes) : null)
        ?? (!string.IsNullOrWhiteSpace(album.CoverMimeType) ? album.CoverMimeType : "image/jpeg");

    private static string MimeExtension(string? mime) => mime switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates <paramref name="directory"/> and remembers which folders were new, so they can be removed again.</summary>
    private static void EnsureDirectory(string directory, List<string> createdDirs)
    {
        var missing = new Stack<string>();
        string? current = directory;
        while (current != null && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        Directory.CreateDirectory(directory);
        foreach (string folder in missing)
        {
            createdDirs.Add(folder);
        }
    }

    /// <summary>Deletes files this export created, then the empty folders it created. Returns what could not be removed.</summary>
    private static List<string> Rollback(List<string> createdFiles, List<string> createdDirs)
    {
        var leftovers = new List<string>();
        for (int i = createdFiles.Count - 1; i >= 0; i--)
        {
            try
            {
                File.Delete(createdFiles[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add(createdFiles[i]);
            }
        }

        for (int i = createdDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(createdDirs[i]) && !Directory.EnumerateFileSystemEntries(createdDirs[i]).Any())
                {
                    Directory.Delete(createdDirs[i]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add(createdDirs[i]);
            }
        }

        return leftovers;
    }

    private static void TryDelete(string path, List<string> warnings)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not delete '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
