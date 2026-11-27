using CdBurner.Core.Subtitles;

namespace CdBurner.Core.DvdVideo;

/// <summary>How a video's brightness values are coded (its transfer characteristic).</summary>
public enum VideoTransfer
{
    /// <summary>Standard dynamic range (BT.709 / BT.601 and anything not recognised as HDR).</summary>
    Sdr,

    /// <summary>HDR10 / Dolby Vision base layer: SMPTE ST 2084 "PQ".</summary>
    Pq,

    /// <summary>Hybrid Log-Gamma (ARIB STD-B67), used by broadcast HDR.</summary>
    Hlg,
}

/// <summary>The colour coding of a movie's first video stream, as FFmpeg reports it.</summary>
/// <param name="Transfer">SDR, PQ or HLG.</param>
/// <param name="Matrix">FFmpeg's name of the YUV matrix (for example <c>bt2020nc</c>), or null when not reported.</param>
public sealed record VideoColorInfo(VideoTransfer Transfer, string? Matrix)
{
    /// <summary>True for PQ and HLG sources.</summary>
    public bool IsHdr => Transfer != VideoTransfer.Sdr;
}

/// <summary>
/// Turns HDR movies (4K Blu-ray rips and the like: BT.2020 colours, PQ or HLG brightness) into the standard-range BT.709
/// picture that DVD-Video and Blu-ray Video (H.264) carry. Without it the 10-bit HDR values are only scaled and
/// labelled BT.709, and players show a flat, grey, washed-out picture (measured on a 4K HDR10 film, 2026-11-26).
/// The chain is the usual one for FFmpeg's zimg filter: linear light at a nominal 100 cd/m², BT.2020 to BT.709
/// primaries, the Hable curve for the highlights without desaturation, then BT.709 transfer, matrix and TV range.
/// It runs after the picture is scaled down, so the floating-point step works on 1080p frames, not 4K ones.
/// </summary>
public static class HdrToneMapping
{
    /// <summary>
    /// Reads the first video stream line of FFmpeg's input report (<c>ffmpeg -hide_banner -i movie</c>), for example
    /// <c>Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x1608 ...</c>.
    /// A source with no colour information, or no video line, is taken as SDR.
    /// </summary>
    public static VideoColorInfo ParseFfmpegInputReport(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? video = lines.FirstOrDefault(l => l.Contains("Stream #", StringComparison.Ordinal) && l.Contains(": Video:", StringComparison.Ordinal));
        if (video is null)
        {
            return new VideoColorInfo(VideoTransfer.Sdr, null);
        }

        VideoTransfer transfer = video.Contains("smpte2084", StringComparison.OrdinalIgnoreCase) ? VideoTransfer.Pq
            : video.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase) ? VideoTransfer.Hlg
            : VideoTransfer.Sdr;
        string? matrix = video.Contains("bt2020c/", StringComparison.OrdinalIgnoreCase) ? "bt2020c"
            : video.Contains("bt2020nc", StringComparison.OrdinalIgnoreCase) ? "bt2020nc"
            : null;
        return new VideoColorInfo(transfer, matrix);
    }

    /// <summary>
    /// The filters that take an HDR picture to standard-range BT.709 (comma-separated, to go inside a <c>-vf</c> chain),
    /// or null for an SDR source. An HDR source whose matrix FFmpeg did not report is read as BT.2020 non-constant
    /// luminance, which every HDR10 and HLG release uses.
    /// </summary>
    public static string? ToneMapFilter(VideoColorInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsHdr)
        {
            return null;
        }
        string transferIn = source.Transfer == VideoTransfer.Pq ? "smpte2084" : "arib-std-b67";
        string matrixIn = source.Matrix ?? "bt2020nc";
        return $"zscale=tin={transferIn}:min={matrixIn}:pin=bt2020:rin=tv:t=linear:npl=100," +
               "format=gbrpf32le," +
               "zscale=p=bt709," +
               "tonemap=tonemap=hable:desat=0," +
               "zscale=t=bt709:m=bt709:r=tv";
    }

    /// <summary>True when an FFmpeg filter listing (<c>ffmpeg -hide_banner -filters</c>) has both filters the chain needs.</summary>
    public static bool FilterListingHasToneMapping(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        bool zscale = false, tonemap = false;
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                zscale |= parts[1] == "zscale";
                tonemap |= parts[1] == "tonemap";
            }
        }
        return zscale && tonemap;
    }

    /// <summary>
    /// Asks FFmpeg how the movie's video is coded: <c>ffmpeg -hide_banner -i movie</c> (it exits with an error because
    /// no output is given; the report on standard error is what is read). Read-only.
    /// </summary>
    public static async Task<VideoColorInfo> ProbeAsync(IExternalToolRunner runner, string ffmpeg, string moviePath, string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var lines = new List<string>();
        await runner.RunAsync(ffmpeg, ProbeArguments(moviePath), workingDirectory, lines.Add, cancellationToken).ConfigureAwait(false);
        return ParseFfmpegInputReport(lines);
    }

    /// <summary>The probe's arguments: exactly <c>-hide_banner -i movie</c>, nothing written.</summary>
    public static IReadOnlyList<string> ProbeArguments(string moviePath) => ["-hide_banner", "-i", moviePath];

    /// <summary>True when <paramref name="arguments"/> is the probe call (for tool fakes in tests and logs).</summary>
    public static bool IsProbe(IReadOnlyList<string> arguments) =>
        arguments.Count == 3 && arguments[0] == "-hide_banner" && arguments[1] == "-i";
}
