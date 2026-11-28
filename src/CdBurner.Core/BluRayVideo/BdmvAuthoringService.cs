using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Subtitles;

namespace CdBurner.Core.BluRayVideo;

/// <summary>
/// Authors a Blu-ray Video (BDMV) or AVCHD folder tree from movie files: FFmpeg encodes H.264 and AC-3 or LPCM,
/// <see cref="BdavTsMuxer"/> writes the 192-byte-packet stream file, <see cref="BdmvBinaryWriters"/> write
/// <c>index.bdmv</c>, <c>MovieObject.bdmv</c>, the playlist and the clip information (each also under <c>BACKUP</c>),
/// <c>CERTIFICATE/id.bdmv</c> is added, and <see cref="BuildImageAsync"/> packs the tree onto a UDF 2.50 image.
/// Every movie becomes one clip, one playlist, one movie object and one title; subtitles (a .srt file or speech
/// recognition) become PGS bitmap streams.
/// </summary>
/// <remarks>
/// The output is checked structurally against the format description and an open-source reader's parsing rules; it has
/// not been played on a Blu-ray player, so no player compatibility is claimed.
/// </remarks>
public sealed class BdmvAuthoringService
{
    /// <summary>Data capacity of a single-layer BD-R: 12,219,392 sectors of 2,048 bytes.</summary>
    public const long BdrSingleLayerBytes = 12_219_392L * 2048;

    /// <summary>Data capacity of a DVD-5: 2,295,104 sectors of 2,048 bytes.</summary>
    public const long Dvd5Bytes = 2_295_104L * 2048;

    /// <summary>Runs FFmpeg; tests replace it with a fake that writes a transport stream.</summary>
    public IExternalToolRunner ToolRunner { get; init; } = new ProcessToolRunner();

    /// <summary>Makes subtitles for <see cref="BdmvSubtitleSpec.GenerateWithWhisper"/>; null uses the speech-recognition service.</summary>
    public DvdSubtitleSourceResolver.SubtitleGenerator? SubtitleGenerator { get; init; }

    /// <summary>Largest number of movies (titles) on one disc.</summary>
    public const int MaxTitles = 99;

    /// <summary>Largest number of subtitle tracks per clip.</summary>
    public const int MaxSubtitleTracks = 8;

    /// <summary>File extension of the stream file (<c>.m2ts</c>, or <c>.MTS</c> for AVCHD).</summary>
    public static string StreamExtension(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd ? ".MTS" : ".m2ts";

    /// <summary>
    /// The folders under <c>BDMV</c>, empty ones included. A Blu-ray disc also carries the (empty) <c>AUXDATA</c>,
    /// <c>BDJO</c>, <c>JAR</c> and <c>META</c> folders and the backups of <c>BDJO</c> and <c>JAR</c>, as reference
    /// authoring tools write them; an AVCHD disc carries <c>AUXDATA</c>.
    /// </summary>
    public static IReadOnlyList<string> BdmvFolders(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd
        ? ["AUXDATA", "PLAYLIST", "CLIPINF", "STREAM", "BACKUP/PLAYLIST", "BACKUP/CLIPINF"]
        : ["AUXDATA", "BDJO", "JAR", "META", "PLAYLIST", "CLIPINF", "STREAM", "BACKUP/BDJO", "BACKUP/JAR", "BACKUP/PLAYLIST", "BACKUP/CLIPINF"];

    /// <summary>File extension of the clip information (<c>.clpi</c>, or <c>.CPI</c> for AVCHD).</summary>
    public static string ClipInfoExtension(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd ? ".CPI" : ".clpi";

    /// <summary>File extension of the playlist (<c>.mpls</c>, or <c>.MPL</c> for AVCHD).</summary>
    public static string PlaylistExtension(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd ? ".MPL" : ".mpls";

    /// <summary>
    /// Checks the options against what the disc type allows. Returns null when they are usable, else the reason.
    /// </summary>
    public static string? ValidateOptions(BdmvAuthoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var allowed = options.VideoFormat switch
        {
            BdmvVideoFormat.Hd1080p => new[] { BdmvFrameRate.Fps23976, BdmvFrameRate.Fps24 },
            BdmvVideoFormat.Hd1080i => new[] { BdmvFrameRate.Fps25, BdmvFrameRate.Fps2997 },
            BdmvVideoFormat.Hd720p => new[] { BdmvFrameRate.Fps23976, BdmvFrameRate.Fps24, BdmvFrameRate.Fps50, BdmvFrameRate.Fps5994 },
            _ => Array.Empty<BdmvFrameRate>(),
        };
        if (!allowed.Contains(options.FrameRate))
        {
            return $"{options.VideoFormat} cannot use the frame rate {options.FrameRate}; allowed: {string.Join(", ", allowed)}.";
        }
        if (options.AudioCodec == BdmvAudioCodec.Lpcm && options.AudioChannels != 2)
        {
            return "LPCM audio is limited to stereo here; use AC-3 for 5.1.";
        }
        if (options.AudioChannels is not (2 or 6))
        {
            return "Audio channels must be 2 or 6.";
        }
        if (options.VideoBitrateKbps < 1000 || options.MaxVideoBitrateKbps < options.VideoBitrateKbps)
        {
            return "The video bitrate must be at least 1,000 kbit/s and not above the maximum bitrate.";
        }
        if (options.Subtitles.Count > MaxSubtitleTracks)
        {
            return $"At most {MaxSubtitleTracks} subtitle tracks are supported.";
        }
        if (options.Subtitles.Any(s => string.IsNullOrWhiteSpace(s.SourcePath) && !s.GenerateWithWhisper))
        {
            return "A subtitle track needs a .srt file or speech recognition.";
        }
        if (string.IsNullOrWhiteSpace(options.VolumeLabel) || options.VolumeLabel.Length > 32)
        {
            return "The volume label must be 1 to 32 characters.";
        }
        return null;
    }

    /// <summary>Capacity the tree must fit: the option when set, else a single-layer BD-R or a DVD-5.</summary>
    public static long CapacityFor(BdmvAuthoringOptions options) =>
        options.CapacityBytes > 0 ? options.CapacityBytes : options.Target == BdmvDiscTarget.Avchd ? Dvd5Bytes : BdrSingleLayerBytes;

    /// <summary>Authors the tree below <paramref name="outputRootDirectory"/> (which must not already hold a <c>BDMV</c> folder with files).</summary>
    public async Task<BdmvAuthoringResult> AuthorAsync(
        IReadOnlyList<string> inputPaths,
        string outputRootDirectory,
        BdmvAuthoringOptions? options = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootDirectory);
        options ??= new BdmvAuthoringOptions();
        var notes = new List<string>();

        BdmvAuthoringResult Fail(string message) => new() { Success = false, ErrorMessage = message, Target = options.Target, LogNotes = notes };

        string? optionError = ValidateOptions(options);
        if (optionError != null)
        {
            return Fail(optionError);
        }
        if (inputPaths.Count is < 1 or > MaxTitles)
        {
            return Fail($"Give between 1 and {MaxTitles} movie files.");
        }
        foreach (string input in inputPaths)
        {
            if (!File.Exists(input))
            {
                return Fail($"Movie file not found: {input}");
            }
        }

        if (!FfmpegLocator.IsFfmpegAvailable(out string? ffmpeg, options.CustomFfmpegPath) || string.IsNullOrEmpty(ffmpeg))
        {
            return Fail("FFmpeg was not found. Install it from the Tools page (one click) or give its path.");
        }

        string outputRoot = Path.GetFullPath(outputRootDirectory);
        string bdmv = Path.Combine(outputRoot, "BDMV");
        if (Directory.Exists(bdmv) && Directory.EnumerateFileSystemEntries(bdmv).Any())
        {
            return Fail($"'{bdmv}' already holds files; choose an empty folder.");
        }

        progress?.Report((1, "Checking the encoder..."));
        string? encoderProblem = await CheckEncodersAsync(ffmpeg, options, cancellationToken).ConfigureAwait(false);
        if (encoderProblem != null)
        {
            return Fail(encoderProblem);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Bdmv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (string folder in BdmvFolders(options.Target))
            {
                Directory.CreateDirectory(Path.Combine(bdmv, folder.Replace('/', Path.DirectorySeparatorChar)));
            }
            Directory.CreateDirectory(Path.Combine(outputRoot, "CERTIFICATE", "BACKUP"));

            var generated = new List<string>();
            var clips = new List<BdmvClipSummary>();
            for (int i = 0; i < inputPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double baseline = 2 + 94.0 * i / inputPaths.Count;
                double share = 94.0 / inputPaths.Count;
                var summary = await AuthorClipAsync(
                    ffmpeg, inputPaths[i], i, bdmv, tempDir, options, notes, generated,
                    pct => progress?.Report((baseline + share * pct, $"Title {i + 1} of {inputPaths.Count}")),
                    cancellationToken).ConfigureAwait(false);
                clips.Add(summary);
            }

            WriteNavigationFiles(outputRoot, bdmv, options, inputPaths, generated);

            long total = generated.Sum(f => new FileInfo(Path.Combine(outputRoot, f.Replace('/', Path.DirectorySeparatorChar))).Length);
            long capacity = CapacityFor(options);
            if (total > capacity * 99 / 100)
            {
                return new BdmvAuthoringResult
                {
                    Success = false,
                    ErrorMessage = $"The authored files take {total / 1_000_000_000.0:F2} GB; the disc holds {capacity / 1_000_000_000.0:F2} GB. Lower the video bitrate or use fewer movies.",
                    OutputRootDirectory = outputRoot,
                    BdmvDirectory = bdmv,
                    Target = options.Target,
                    TotalSizeBytes = total,
                    GeneratedFiles = generated,
                    Clips = clips,
                    LogNotes = notes,
                };
            }

            progress?.Report((100, "Blu-ray Video folder ready."));
            return new BdmvAuthoringResult
            {
                Success = true,
                OutputRootDirectory = outputRoot,
                BdmvDirectory = bdmv,
                Target = options.Target,
                TotalSizeBytes = total,
                GeneratedFiles = generated,
                Clips = clips,
                LogNotes = notes,
            };
        }
        catch (InvalidDataException ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            CdBurner.Core.Utilities.ProcessAndFileCleanup.TryDeleteDirectoryWithRetry(tempDir);
        }
    }

    private async Task<string?> CheckEncodersAsync(string ffmpeg, BdmvAuthoringOptions options, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        int code = await ToolRunner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], Path.GetTempPath(), lines.Add, cancellationToken).ConfigureAwait(false);
        string listing = string.Join('\n', lines);
        if (code != 0 || listing.Length == 0)
        {
            return "FFmpeg could not list its encoders.";
        }
        if (!listing.Contains("libx264", StringComparison.Ordinal))
        {
            return "This FFmpeg build has no libx264 encoder, which Blu-ray compliant H.264 needs (older 1-click installs got a build without it). " +
                   "Click 'Install FFmpeg (1-Click)' in Settings, or run 'ignisdisc-cli install-ffmpeg --overwrite', to replace it with the full build.";
        }
        if (options.AudioCodec == BdmvAudioCodec.Lpcm && !listing.Contains("pcm_bluray", StringComparison.Ordinal))
        {
            return "This FFmpeg build has no Blu-ray LPCM encoder (pcm_bluray); choose AC-3.";
        }
        if (options.AudioCodec == BdmvAudioCodec.Ac3 && !listing.Contains(" ac3 ", StringComparison.Ordinal))
        {
            return "This FFmpeg build has no AC-3 encoder.";
        }
        return null;
    }

    private async Task<BdmvClipSummary> AuthorClipAsync(
        string ffmpeg,
        string input,
        int index,
        string bdmv,
        string tempDir,
        BdmvAuthoringOptions options,
        List<string> notes,
        List<string> generated,
        Action<double> clipProgress,
        CancellationToken cancellationToken)
    {
        string clipName = BdmvBinaryWriters.ClipName(index);
        string tsPath = Path.Combine(tempDir, clipName + ".ts");
        var (width, height) = BdmvBinaryWriters.VideoSize(options.VideoFormat);

        // An HDR movie (BT.2020, PQ or HLG) is tone-mapped to standard range; passed through it would look washed out.
        var color = await HdrToneMapping.ProbeAsync(ToolRunner, ffmpeg, input, tempDir, cancellationToken).ConfigureAwait(false);
        string? toneMap = HdrToneMapping.ToneMapFilter(color);
        if (toneMap != null)
        {
            var filterLines = new List<string>();
            await ToolRunner.RunAsync(ffmpeg, ["-hide_banner", "-filters"], tempDir, filterLines.Add, cancellationToken).ConfigureAwait(false);
            if (!HdrToneMapping.FilterListingHasToneMapping(filterLines))
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(input)}' is HDR video, and this FFmpeg build has no zscale and tonemap filters to convert it; " +
                    "without them the picture would look washed out. Use an FFmpeg build with zimg (the full builds have it).");
            }
            notes.Add($"'{Path.GetFileName(input)}': HDR ({color.Transfer}) source, tone-mapped to standard range (BT.709).");
        }
        var args = FfmpegBdmvTranscodeBuilder.Build(input, tsPath, options, toneMap);
        var tail = new Queue<string>();
        int exit = await ToolRunner.RunAsync(ffmpeg, args, tempDir, line =>
        {
            tail.Enqueue(line);
            while (tail.Count > 12)
            {
                tail.Dequeue();
            }
        }, cancellationToken).ConfigureAwait(false);
        if (exit != 0 || !File.Exists(tsPath))
        {
            throw new InvalidDataException($"FFmpeg could not encode '{Path.GetFileName(input)}' (exit code {exit}): {string.Join(" | ", tail)}");
        }
        clipProgress(0.7);

        // Subtitle times count from the start of the movie; the transport stream's picture times start a little later.
        long pictureStart = 0;
        if (options.Subtitles.Count > 0)
        {
            using var probe = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            pictureStart = BdavTsMuxer.FindFirstVideoPts(probe, BdmvClipStreams.PrimaryVideoPid)
                ?? throw new InvalidDataException("FFmpeg's output has no video picture with a time stamp.");
        }

        var tracks = new List<IReadOnlyList<PgsDisplaySet>>();
        var subtitleStreams = new List<(ushort Pid, string Language)>();
        for (int s = 0; s < options.Subtitles.Count; s++)
        {
            var spec = options.Subtitles[s];
            string? srt = spec.SourcePath;
            if (string.IsNullOrWhiteSpace(srt))
            {
                var generator = SubtitleGenerator ?? ((movie, p, ct) => new SubtitleGenerationService().GenerateAsync(
                    movie, new SubtitleGenerationOptions { Language = spec.Language, FfmpegPath = options.CustomFfmpegPath }, p, ct));
                var made = await generator(input, null, cancellationToken).ConfigureAwait(false);
                if (!made.Success || made.SrtPath == null)
                {
                    throw new InvalidDataException($"Generating subtitles for '{Path.GetFileName(input)}' failed: {made.ErrorMessage}");
                }
                srt = made.SrtPath;
                notes.Add($"Subtitles for '{Path.GetFileName(input)}': {made.CueCount} machine-generated subtitles ({srt}).");
            }
            if (!File.Exists(srt))
            {
                throw new InvalidDataException($"Subtitle file not found: {srt}");
            }

            var cues = SrtCueList.Clean(SrtCueList.Parse(await File.ReadAllTextAsync(srt, cancellationToken).ConfigureAwait(false)));
            var sets = PgsSubtitleEncoder.Encode(cues, width, height, options.FrameRate, pictureStart);
            if (sets.Count == 0)
            {
                notes.Add($"'{Path.GetFileName(srt)}' has no usable subtitle text; the track was left out.");
                continue;
            }
            tracks.Add(sets);
            subtitleStreams.Add(((ushort)(BdmvClipStreams.FirstGraphicsPid + subtitleStreams.Count), spec.Language));
        }

        var streams = new BdmvClipStreams(
            BdmvClipStreams.PrimaryVideoPid,
            BdmvClipStreams.PrimaryAudioPid,
            BdmvBinaryWriters.AudioFormatCodeForChannels(options.AudioCodec == BdmvAudioCodec.Lpcm ? 2 : options.AudioChannels),
            options.AudioCodec,
            options.AudioLanguage,
            subtitleStreams,
            BdmvClipStreams.ProgramMapPid,
            options.VideoFormat,
            options.FrameRate);

        string streamRel = $"BDMV/STREAM/{clipName}{StreamExtension(options.Target)}";
        string streamPath = Path.Combine(bdmv, "STREAM", clipName + StreamExtension(options.Target));
        BdmvStreamFacts facts;
        using (var tsIn = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var m2ts = new FileStream(streamPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            facts = BdavTsMuxer.Mux(tsIn, m2ts, streams, tracks, options.FrameRate);
        }
        generated.Add(streamRel);
        clipProgress(0.9);

        byte[] clpi = BdmvBinaryWriters.WriteClipInfo(options.Target, streams, facts);
        byte[] mpls = BdmvBinaryWriters.WritePlaylist(options.Target, clipName, facts.StartPts45k, facts.EndPts45k, streams, options.ChapterIntervalSeconds);
        WriteBoth(bdmv, "CLIPINF", clipName + ClipInfoExtension(options.Target), clpi, generated);
        WriteBoth(bdmv, "PLAYLIST", clipName + PlaylistExtension(options.Target), mpls, generated);
        notes.Add($"Clip {clipName}: {facts.SourcePackets:N0} source packets, {facts.EntryPoints.Count} entry points, {tracks.Count} subtitle track(s) from '{Path.GetFileName(input)}'.");
        return new BdmvClipSummary(clipName, facts.SourcePackets, facts.StartPts45k, facts.EndPts45k, facts.EntryPoints.Count, tracks.Count);
    }

    private static void WriteBoth(string bdmv, string folder, string fileName, byte[] data, List<string> generated)
    {
        File.WriteAllBytes(Path.Combine(bdmv, folder, fileName), data);
        File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", folder, fileName), data);
        generated.Add($"BDMV/{folder}/{fileName}");
        generated.Add($"BDMV/BACKUP/{folder}/{fileName}");
    }

    private static void WriteNavigationFiles(string outputRoot, string bdmv, BdmvAuthoringOptions options, IReadOnlyList<string> inputs, List<string> generated)
    {
        int titles = inputs.Count;
        byte[] index = BdmvBinaryWriters.WriteIndex(options.Target, options.VideoFormat, options.FrameRate, titles);
        byte[] movieObject = BdmvBinaryWriters.WriteMovieObject(options.Target, titles);
        foreach (var (name, data) in new[] { ("index.bdmv", index), ("MovieObject.bdmv", movieObject) })
        {
            File.WriteAllBytes(Path.Combine(bdmv, name), data);
            File.WriteAllBytes(Path.Combine(bdmv, "BACKUP", name), data);
            generated.Add($"BDMV/{name}");
            generated.Add($"BDMV/BACKUP/{name}");
        }

        byte[] id = BdmvBinaryWriters.WriteDiscId(options.Target, options.VolumeLabel + "|" + string.Join("|", inputs.Select(Path.GetFileName)));
        File.WriteAllBytes(Path.Combine(outputRoot, "CERTIFICATE", "id.bdmv"), id);
        File.WriteAllBytes(Path.Combine(outputRoot, "CERTIFICATE", "BACKUP", "id.bdmv"), id);
        generated.Add("CERTIFICATE/id.bdmv");
        generated.Add("CERTIFICATE/BACKUP/id.bdmv");
    }

    /// <summary>UDF 2.50 settings for the image, for both disc types (AVCHD discs on DVD also use UDF 2.50).</summary>
    public static IsoBuildOptions CreateImageOptions(BdmvAuthoringOptions options) => new(
        VolumeLabel: options.VolumeLabel,
        IncludeIso9660: true,
        IncludeJoliet: false,
        IncludeUdf: true,
        UdfRevision: 0x0250,
        FixedTimestampUtc: options.FixedTimestampUtc);

    /// <summary>Packs an authored folder (<c>BDMV</c> + <c>CERTIFICATE</c>) into a UDF 2.50 image file.</summary>
    public static async Task BuildImageAsync(
        string authoredRoot,
        string isoPath,
        BdmvAuthoringOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(Path.Combine(authoredRoot, "BDMV", "index.bdmv")))
        {
            throw new FileNotFoundException("The folder holds no BDMV/index.bdmv; author it first.", authoredRoot);
        }

        var root = CompilationDirectory.CreateRoot();
        CompilationFolderImporter.Import(root, authoredRoot, strict: true);
        await IsoUdfImageBuilder.BuildToFileAsync(root, isoPath, CreateImageOptions(options), progress, cancellationToken).ConfigureAwait(false);
    }
}
