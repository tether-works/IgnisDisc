namespace CdBurner.Core.DvdVideo;

/// <summary>
/// DVD-Video broadcast television system standard.
/// </summary>
public enum DvdVideoStandard
{
    /// <summary>
    /// National Television System Committee (NTSC): 720x480 resolution at 29.97 frames/second.
    /// Used predominantly in North America, Japan, and South Korea.
    /// </summary>
    Ntsc,

    /// <summary>
    /// Phase Alternating Line (PAL): 720x576 resolution at 25 frames/second.
    /// Used predominantly in Europe, Australia, and Asia.
    /// </summary>
    Pal
}

/// <summary>
/// DVD-Video display aspect ratio.
/// </summary>
public enum DvdAspectRatio
{
    /// <summary>
    /// Widescreen anamorphic 16:9 aspect ratio.
    /// </summary>
    Widescreen16x9,

    /// <summary>
    /// Standard full-frame 4:3 aspect ratio.
    /// </summary>
    Standard4x3
}

/// <summary>One audio stream passed to <see cref="FfmpegDvdTranscodeBuilder"/>.</summary>
public sealed record DvdMuxAudioInput(
    string? SourcePath = null,
    int StreamIndex = 0,
    string Language = "eng",
    int Channels = 2,
    int BitrateKbps = 0,
    string Codec = "ac3");

/// <summary>One VobSub subtitle stream passed to <see cref="FfmpegDvdTranscodeBuilder"/>.</summary>
public sealed record DvdMuxSubtitleInput(
    string VobSubIndexPath,
    string Language = "eng");

/// <summary>
/// Builds deterministic FFmpeg command-line arguments for transcoding video files
/// into fully compliant DVD-Video MPEG-2 program stream VOB files (NTSC or PAL).
/// </summary>
public sealed class FfmpegDvdTranscodeBuilder
{
    /// <summary>
    /// Source video file path.
    /// </summary>
    public string? InputPath { get; set; }

    /// <summary>
    /// Destination .vob or .mpg file path.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Target television standard (NTSC or PAL). Default: NTSC.
    /// </summary>
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Ntsc;

    /// <summary>
    /// Target display aspect ratio. Default: 16:9 Widescreen.
    /// </summary>
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Widescreen16x9;

    /// <summary>
    /// Whether to force overwrite the output file without prompting (-y). Default: true.
    /// </summary>
    public bool OverwriteOutput { get; set; } = true;

    /// <summary>
    /// Whether to apply an aspect-ratio-preserving scale and pad video filter (-vf)
    /// to normalize arbitrary input resolutions to 720x480 (NTSC) or 720x576 (PAL). Default: false.
    /// </summary>
    public bool EnableScaleAndPadFilter { get; set; } = false;

    /// <summary>
    /// A VobSub index (.idx, from <see cref="DvdSubtitleWriter.WriteVobSub"/>) muxed in as DVD subpicture stream 0
    /// (copied as it is); null for no subtitles.
    /// </summary>
    public string? SubtitleIndexPath { get; set; }

    /// <summary>
    /// Optional target video bitrate in kilobits per second (e.g. 6000 for 6 Mbps).
    /// </summary>
    public int? VideoBitrateKbps { get; set; }

    /// <summary>
    /// Optional maximum video bitrate in kilobits per second (e.g. 8000 for 8 Mbps).
    /// DVD-Video maximum total bitrate is 9800 kbps (video + audio).
    /// </summary>
    public int? MaxVideoBitrateKbps { get; set; }

    /// <summary>
    /// Optional VBV buffer size in kilobits (e.g. 1835 kb for DVD).
    /// </summary>
    public int? BufferSizeKb { get; set; }

    /// <summary>
    /// Audio codec. Default: "ac3" (Dolby Digital).
    /// </summary>
    public string AudioCodec { get; set; } = "ac3";

    /// <summary>
    /// Audio bitrate in kilobits per second. Default: 192 (stereo) or 448 (5.1 surround).
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Audio sample rate in Hertz. DVD-Video requires 48,000 Hz. Default: 48000.
    /// </summary>
    public int AudioSampleRateHz { get; set; } = 48000;

    /// <summary>
    /// Audio channel count (2 for stereo, 6 for 5.1 surround). Default: 2.
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>
    /// Optional multi-audio tracks (up to 8). When populated, overrides single-stream audio mapping.
    /// </summary>
    public IList<DvdMuxAudioInput> AudioTracks { get; } = new List<DvdMuxAudioInput>();

    /// <summary>
    /// Optional multi-subtitle VobSub inputs (up to 8). When populated, overrides <see cref="SubtitleIndexPath"/>.
    /// </summary>
    public IList<DvdMuxSubtitleInput> SubtitleTracks { get; } = new List<DvdMuxSubtitleInput>();

    public FfmpegDvdTranscodeBuilder WithInput(string inputPath)
    {
        InputPath = inputPath;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithOutput(string outputPath)
    {
        OutputPath = outputPath;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithStandard(DvdVideoStandard standard)
    {
        Standard = standard;
        return this;
    }

    /// <summary>
    /// The picture fitted to the disc's display shape without stretching. A DVD frame is always 720 pixels wide; the
    /// player widens it to 4:3 or 16:9. So the source is first taken to square pixels (honouring its own pixel shape,
    /// e.g. 10:11 for DV camcorder video), fitted inside a square-pixel canvas of the target shape (16:9 NTSC 854×480,
    /// 4:3 NTSC 640×480; PAL 1024×576 / 768×576) with bars where it is narrower or wider, and only then squeezed to
    /// 720 pixels wide. Before, the source was fitted to 720×480 as pixels and then labelled 16:9, so a 4:3 video
    /// played a third too wide.
    /// </summary>
    /// <param name="toneMapFilter">For an HDR source, <see cref="HdrToneMapping.ToneMapFilter"/>, run once the picture is
    /// down to DVD size and before the bars are added; null for SDR.</param>
    public static string BuildScaleAndPadFilter(DvdVideoStandard standard, DvdAspectRatio aspectRatio, string? toneMapFilter = null)
    {
        int height = standard == DvdVideoStandard.Ntsc ? 480 : 576;
        int canvasWidth = aspectRatio == DvdAspectRatio.Standard4x3
            ? height * 4 / 3
            : (int)Math.Round(height * 16 / 9.0 / 2) * 2;
        // A source without a pixel shape (sar 0) is taken as square. Commas inside expressions are escaped for the filtergraph.
        return "scale=trunc(iw*if(gt(sar\\,0)\\,sar\\,1)/2)*2:ih,setsar=1," +
               $"scale={canvasWidth}:{height}:force_original_aspect_ratio=decrease," +
               (string.IsNullOrEmpty(toneMapFilter) ? "" : toneMapFilter + ",") +
               $"pad={canvasWidth}:{height}:(ow-iw)/2:(oh-ih)/2," +
               $"scale=720:{height},format=yuv420p";
    }

    /// <summary>HDR sources: the tone-mapping filters (<see cref="HdrToneMapping.ToneMapFilter"/>); null for SDR.</summary>
    public string? ToneMapFilter { get; set; }

    public FfmpegDvdTranscodeBuilder WithToneMapFilter(string? toneMapFilter)
    {
        ToneMapFilter = toneMapFilter;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithAspectRatio(DvdAspectRatio aspectRatio)
    {
        AspectRatio = aspectRatio;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithVideoBitrate(int bitrateKbps, int? maxRateKbps = null, int? bufferSizeKb = null)
    {
        VideoBitrateKbps = bitrateKbps;
        MaxVideoBitrateKbps = maxRateKbps;
        BufferSizeKb = bufferSizeKb;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithAudio(string codec = "ac3", int bitrateKbps = 192, int channels = 2)
    {
        AudioCodec = codec;
        AudioBitrateKbps = bitrateKbps;
        AudioChannels = channels;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithSubtitles(string? vobSubIndexPath)
    {
        SubtitleIndexPath = vobSubIndexPath;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithAudioTracks(IEnumerable<DvdMuxAudioInput> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        AudioTracks.Clear();
        foreach (var track in tracks.Take(DvdVideoProject.MaxAudioTracksPerTitle))
        {
            AudioTracks.Add(track);
        }
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithSubtitleTracks(IEnumerable<DvdMuxSubtitleInput> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        SubtitleTracks.Clear();
        foreach (var track in tracks.Take(DvdVideoProject.MaxSubtitleTracksPerTitle))
        {
            SubtitleTracks.Add(track);
        }
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithScaleAndPadFilter(bool enabled = true)
    {
        EnableScaleAndPadFilter = enabled;
        return this;
    }

    /// <summary>
    /// Builds a deterministic, ordered list of CLI arguments suitable for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public IReadOnlyList<string> BuildArgumentList()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            throw new InvalidOperationException("Input path must be specified.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new InvalidOperationException("Output path must be specified.");
        }

        var args = new List<string>();

        if (OverwriteOutput)
        {
            args.Add("-y");
        }

        args.Add("-i");
        args.Add(InputPath);

        var cappedAudio = AudioTracks.Take(DvdVideoProject.MaxAudioTracksPerTitle).ToList();
        var cappedSubtitles = SubtitleTracks.Count > 0
            ? SubtitleTracks.Where(s => !string.IsNullOrWhiteSpace(s.VobSubIndexPath)).Take(DvdVideoProject.MaxSubtitleTracksPerTitle).ToList()
            : (!string.IsNullOrWhiteSpace(SubtitleIndexPath)
                ? [new DvdMuxSubtitleInput(SubtitleIndexPath!, "eng")]
                : new List<DvdMuxSubtitleInput>());

        bool useMultiTrackMapping = cappedAudio.Count > 0 || SubtitleTracks.Count > 0;
        if (!useMultiTrackMapping && cappedSubtitles.Count == 1)
        {
            args.Add("-i");
            args.Add(cappedSubtitles[0].VobSubIndexPath);
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0", "-map", "1:s:0", "-c:s", "copy"]);
        }
        else if (useMultiTrackMapping)
        {
            var audioInputIndices = new List<int>(cappedAudio.Count);
            int nextInputIndex = 1;
            string primaryFull = SafeFullPath(InputPath);

            foreach (var audio in cappedAudio)
            {
                if (string.IsNullOrWhiteSpace(audio.SourcePath) ||
                    string.Equals(SafeFullPath(audio.SourcePath), primaryFull, StringComparison.OrdinalIgnoreCase))
                {
                    audioInputIndices.Add(0);
                }
                else
                {
                    args.Add("-i");
                    args.Add(audio.SourcePath);
                    audioInputIndices.Add(nextInputIndex++);
                }
            }

            var subInputIndices = new List<int>(cappedSubtitles.Count);
            foreach (var sub in cappedSubtitles)
            {
                args.Add("-i");
                args.Add(sub.VobSubIndexPath);
                subInputIndices.Add(nextInputIndex++);
            }

            args.AddRange(["-map", "0:v:0"]);
            if (cappedAudio.Count == 0)
            {
                args.AddRange(["-map", "0:a:0"]);
            }
            else
            {
                for (int a = 0; a < cappedAudio.Count; a++)
                {
                    args.AddRange(["-map", $"{audioInputIndices[a]}:a:{Math.Max(0, cappedAudio[a].StreamIndex)}"]);
                }
            }

            for (int s = 0; s < cappedSubtitles.Count; s++)
            {
                args.AddRange(["-map", $"{subInputIndices[s]}:s:0"]);
            }
        }

        // Target preset: -target ntsc-dvd or -target pal-dvd
        string target = Standard switch
        {
            DvdVideoStandard.Ntsc => "ntsc-dvd",
            DvdVideoStandard.Pal => "pal-dvd",
            _ => throw new ArgumentOutOfRangeException(nameof(Standard))
        };
        args.Add("-target");
        args.Add(target);

        // Aspect ratio: -aspect 16:9 or -aspect 4:3
        string aspect = AspectRatio switch
        {
            DvdAspectRatio.Widescreen16x9 => "16:9",
            DvdAspectRatio.Standard4x3 => "4:3",
            _ => "16:9"
        };
        args.Add("-aspect");
        args.Add(aspect);

        if (EnableScaleAndPadFilter)
        {
            args.Add("-vf");
            args.Add(BuildScaleAndPadFilter(Standard, AspectRatio, ToneMapFilter));
        }

        // Optional custom video bitrates
        if (VideoBitrateKbps.HasValue)
        {
            args.Add("-b:v");
            args.Add($"{VideoBitrateKbps.Value}k");
        }

        if (MaxVideoBitrateKbps.HasValue)
        {
            args.Add("-maxrate");
            args.Add($"{MaxVideoBitrateKbps.Value}k");
        }

        if (BufferSizeKb.HasValue)
        {
            args.Add("-bufsize");
            args.Add($"{BufferSizeKb.Value}k");
        }

        // Audio parameters
        if (cappedAudio.Count > 0)
        {
            for (int a = 0; a < cappedAudio.Count; a++)
            {
                var track = cappedAudio[a];
                string codec = string.IsNullOrWhiteSpace(track.Codec) ? AudioCodec : track.Codec;
                int channels = Math.Clamp(track.Channels, 1, 8);
                int bitrate = track.BitrateKbps > 0 ? track.BitrateKbps : (channels >= 6 ? 448 : AudioBitrateKbps);
                args.Add($"-c:a:{a}");
                args.Add(codec);
                args.Add($"-b:a:{a}");
                args.Add($"{bitrate}k");
                args.Add($"-ar:a:{a}");
                args.Add(AudioSampleRateHz.ToString());
                args.Add($"-ac:a:{a}");
                args.Add(channels.ToString());

                string iso2 = DvdLanguageCodes.NormalizeIso639Part2(track.Language);
                if (!string.IsNullOrEmpty(iso2) && iso2 != "und" && iso2 != "auto")
                {
                    args.Add($"-metadata:s:a:{a}");
                    args.Add($"language={iso2}");
                }
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(AudioCodec))
            {
                args.Add("-c:a");
                args.Add(AudioCodec);
            }

            if (AudioBitrateKbps > 0)
            {
                args.Add("-b:a");
                args.Add($"{AudioBitrateKbps}k");
            }

            if (AudioSampleRateHz > 0)
            {
                args.Add("-ar");
                args.Add(AudioSampleRateHz.ToString());
            }

            if (AudioChannels > 0)
            {
                args.Add("-ac");
                args.Add(AudioChannels.ToString());
            }
        }

        if (useMultiTrackMapping && cappedSubtitles.Count > 0)
        {
            for (int s = 0; s < cappedSubtitles.Count; s++)
            {
                args.Add($"-c:s:{s}");
                args.Add("copy");
                string iso2 = DvdLanguageCodes.NormalizeIso639Part2(cappedSubtitles[s].Language);
                if (!string.IsNullOrEmpty(iso2) && iso2 != "und" && iso2 != "auto")
                {
                    args.Add($"-metadata:s:s:{s}");
                    args.Add($"language={iso2}");
                }
            }
        }

        args.Add(OutputPath);

        return args;
    }

    private static string SafeFullPath(string path)
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

    /// <summary>
    /// Builds the argument string (excluding the ffmpeg executable name) with proper shell escaping.
    /// </summary>
    public string BuildArguments()
    {
        var list = BuildArgumentList();
        return string.Join(" ", list.Select(EscapeCliArgument));
    }

    /// <summary>
    /// Builds the full command line string (e.g. ffmpeg -y -i "input.mp4" -target ntsc-dvd -aspect 16:9 "output.vob").
    /// </summary>
    public string BuildCommandLine(string ffmpegExecutable = "ffmpeg")
    {
        string escapedExe = EscapeCliArgument(ffmpegExecutable);
        string args = BuildArguments();
        return $"{escapedExe} {args}";
    }

    /// <summary>
    /// Creates a builder configured for NTSC DVD transcoding.
    /// </summary>
    public static FfmpegDvdTranscodeBuilder ForNtsc(
        string inputPath,
        string outputPath,
        DvdAspectRatio aspectRatio = DvdAspectRatio.Widescreen16x9)
    {
        return new FfmpegDvdTranscodeBuilder
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            Standard = DvdVideoStandard.Ntsc,
            AspectRatio = aspectRatio
        };
    }

    /// <summary>
    /// Creates a builder configured for PAL DVD transcoding.
    /// </summary>
    public static FfmpegDvdTranscodeBuilder ForPal(
        string inputPath,
        string outputPath,
        DvdAspectRatio aspectRatio = DvdAspectRatio.Widescreen16x9)
    {
        return new FfmpegDvdTranscodeBuilder
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            Standard = DvdVideoStandard.Pal,
            AspectRatio = aspectRatio
        };
    }

    /// <summary>
    /// Generates standard NTSC argument string: -target ntsc-dvd -aspect 16:9.
    /// </summary>
    public static string BuildNtscArguments(string inputPath, string outputPath, string aspectRatio = "16:9")
    {
        var aspectEnum = aspectRatio == "4:3" ? DvdAspectRatio.Standard4x3 : DvdAspectRatio.Widescreen16x9;
        return ForNtsc(inputPath, outputPath, aspectEnum).BuildArguments();
    }

    /// <summary>
    /// Generates standard PAL argument string: -target pal-dvd -aspect 16:9.
    /// </summary>
    public static string BuildPalArguments(string inputPath, string outputPath, string aspectRatio = "16:9")
    {
        var aspectEnum = aspectRatio == "4:3" ? DvdAspectRatio.Standard4x3 : DvdAspectRatio.Widescreen16x9;
        return ForPal(inputPath, outputPath, aspectEnum).BuildArguments();
    }

    private static string EscapeCliArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"'))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }
}
