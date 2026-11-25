using System.Globalization;

namespace CdBurner.Core.BluRayVideo;

/// <summary>
/// Builds the FFmpeg command line that turns a movie into the elementary streams of a Blu-ray title: H.264 High
/// profile at level 4.1 with x264's Blu-ray constraints (closed GOPs, an IDR at least once a second, access unit
/// delimiters, 30 Mbit buffer), 48 kHz AC-3 or LPCM audio, written as a transport stream with the Blu-ray PIDs.
/// <see cref="BdavTsMuxer"/> then adds the arrival times, program map and subtitles.
/// </summary>
public static class FfmpegBdmvTranscodeBuilder
{
    /// <summary>Largest video bitrate the disc types allow with the audio on top: BD 40 Mbit/s of the 48 Mbit/s mux, AVCHD 24 Mbit/s.</summary>
    public static int VideoBitrateLimitKbps(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd ? 17_000 : 35_000;

    /// <summary>The frame rate as an FFmpeg fraction.</summary>
    public static string FrameRateFraction(BdmvFrameRate rate) => rate switch
    {
        BdmvFrameRate.Fps23976 => "24000/1001",
        BdmvFrameRate.Fps24 => "24",
        BdmvFrameRate.Fps25 => "25",
        BdmvFrameRate.Fps2997 => "30000/1001",
        BdmvFrameRate.Fps50 => "50",
        BdmvFrameRate.Fps5994 => "60000/1001",
        _ => throw new ArgumentOutOfRangeException(nameof(rate)),
    };

    /// <summary>Pictures per second, rounded up, for the key-frame interval (one second at most).</summary>
    public static int KeyFrameInterval(BdmvFrameRate rate) => rate switch
    {
        BdmvFrameRate.Fps23976 or BdmvFrameRate.Fps24 => 24,
        BdmvFrameRate.Fps25 => 25,
        BdmvFrameRate.Fps2997 => 30,
        BdmvFrameRate.Fps50 => 50,
        BdmvFrameRate.Fps5994 => 60,
        _ => throw new ArgumentOutOfRangeException(nameof(rate)),
    };

    /// <summary>The arguments for one movie (each one string, no quoting needed).</summary>
    public static IReadOnlyList<string> Build(string inputPath, string outputTsPath, BdmvAuthoringOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTsPath);
        ArgumentNullException.ThrowIfNull(options);

        var (width, height) = BdmvBinaryWriters.VideoSize(options.VideoFormat);
        bool interlaced = options.VideoFormat == BdmvVideoFormat.Hd1080i;
        int maxRate = Math.Min(options.MaxVideoBitrateKbps, VideoBitrateLimitKbps(options.Target));
        int avgRate = Math.Min(options.VideoBitrateKbps, maxRate);
        string fraction = FrameRateFraction(options.FrameRate);
        var inv = CultureInfo.InvariantCulture;

        var x264 = new List<string>
        {
            "bluray-compat=1",
            "open-gop=0",
            $"keyint={KeyFrameInterval(options.FrameRate)}",
            "min-keyint=1",
            "slices=4",
            "bframes=3",
            "ref=3",
            $"vbv-maxrate={maxRate.ToString(inv)}",
            "vbv-bufsize=30000",
            "nal-hrd=vbr",
            "aud=1",
            "colorprim=bt709",
            "transfer=bt709",
            "colormatrix=bt709",
            "force-cfr=1",
        };
        if (interlaced)
        {
            x264.Add("tff=1");
        }

        string filter =
            $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fraction},format=yuv420p";

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a:0",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-level:v", "4.1",
            "-b:v", $"{avgRate.ToString(inv)}k", "-maxrate", $"{maxRate.ToString(inv)}k", "-bufsize", "30000k",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-x264-params", string.Join(':', x264),
        };
        if (interlaced)
        {
            args.AddRange(["-flags", "+ilme+ildct"]);
        }

        if (options.AudioCodec == BdmvAudioCodec.Lpcm)
        {
            args.AddRange(["-c:a", "pcm_bluray", "-ar", "48000", "-ac", "2", "-sample_fmt", "s16"]);
        }
        else
        {
            int channels = options.AudioChannels == 6 ? 6 : 2;
            args.AddRange(["-c:a", "ac3", "-ar", "48000", "-ac", channels.ToString(inv), "-b:a", channels == 6 ? "448k" : "192k"]);
        }

        args.AddRange(
        [
            "-f", "mpegts",
            "-streamid", $"0:{BdmvClipStreams.PrimaryVideoPid.ToString(inv)}",
            "-streamid", $"1:{BdmvClipStreams.PrimaryAudioPid.ToString(inv)}",
            "-mpegts_pmt_start_pid", BdmvClipStreams.ProgramMapPid.ToString(inv),
            "-mpegts_transport_stream_id", "1",
            "-pcr_period", "40",
            outputTsPath,
        ]);
        return args;
    }
}
