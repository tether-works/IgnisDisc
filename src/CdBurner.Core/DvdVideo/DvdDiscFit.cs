namespace CdBurner.Core.DvdVideo;

/// <summary>How a set of movies fits onto a DVD once it is converted (see <see cref="DvdDiscFit.Estimate"/>).</summary>
public enum DvdFitKind
{
    /// <summary>The running time is not known (FFmpeg missing or could not read a movie): nothing is promised.</summary>
    Unknown,

    /// <summary>Fits a 4.7 GB DVD at the full bitrate.</summary>
    FullQuality,

    /// <summary>Fits a 4.7 GB DVD with the video bitrate lowered.</summary>
    Compressed,

    /// <summary>Too long for a 4.7 GB DVD even at the lowest bitrate; fits a dual-layer DVD (8.5 GB).</summary>
    NeedsDualLayer,

    /// <summary>Too long even for a dual-layer DVD at the lowest bitrate. Cannot be burned as one disc.</summary>
    TooLong
}

/// <summary>
/// The planned size of a DVD made from movies of known running times: the video bitrate the authoring will use, the
/// bytes that gives, and a one-line summary for the screen.
/// </summary>
public sealed record DvdFitEstimate(
    DvdFitKind Kind,
    double TotalSeconds,
    int VideoKbps,
    IReadOnlyList<long> TitleBytes,
    string Summary)
{
    /// <summary>All titles together, in bytes (video, audio and the MPEG/DVD overhead).</summary>
    public long TotalBytes => TitleBytes.Sum();

    /// <summary>Author for a dual-layer disc (<see cref="DvdAuthoringOptions.DualLayer"/>).</summary>
    public bool DualLayer => Kind == DvdFitKind.NeedsDualLayer;

    /// <summary>False only when no DVD can hold it.</summary>
    public bool CanBurn => Kind != DvdFitKind.TooLong;
}

/// <summary>
/// Works out, before any conversion, whether movies fit onto a DVD. It uses the same bitrate rule as the authoring
/// (<see cref="DvdVideoAuthoringService.PlanVideoBitrate"/>), so what it predicts is what gets written: the bitrate is
/// lowered to fit a 4.7 GB DVD, down to <see cref="DvdVideoAuthoringService.MinimumVideoKbps"/>; longer than that
/// needs a dual-layer DVD, and longer than a dual-layer DVD holds at that bitrate cannot be burned.
/// </summary>
public static class DvdDiscFit
{
    /// <summary>
    /// The fit for titles of the given running times (seconds, in disc order). <see cref="DvdFitKind.Unknown"/> when
    /// any running time is unknown (0 or less).
    /// </summary>
    public static DvdFitEstimate Estimate(IReadOnlyList<double> titleSeconds, DvdAuthoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(titleSeconds);
        ArgumentNullException.ThrowIfNull(options);
        if (titleSeconds.Count == 0 || titleSeconds.Any(s => !(s > 0) || double.IsInfinity(s)))
        {
            return new DvdFitEstimate(DvdFitKind.Unknown, 0, options.VideoBitrateKbps, Array.Empty<long>(), string.Empty);
        }

        double total = titleSeconds.Sum();
        string length = FormatLength(total);
        int lowestKbps = DvdVideoAuthoringService.MinimumVideoKbps + options.AudioBitrateKbps;

        if (Bytes(lowestKbps, total) <= DvdVideoAuthoringService.Dvd5PayloadBytes)
        {
            var single = DvdVideoAuthoringService.PlanVideoBitrate(titleSeconds, WithLayers(options, dualLayer: false));
            bool full = single.VideoKbps >= options.VideoBitrateKbps;
            string summary = full
                ? $"{length} of video: fits a 4.7 GB DVD at full quality."
                : $"{length} of video: compressed to fit a 4.7 GB DVD (video {single.VideoKbps:N0} kbps, {Quality(single.VideoKbps, options)}).";
            return new DvdFitEstimate(full ? DvdFitKind.FullQuality : DvdFitKind.Compressed, total, single.VideoKbps,
                TitleBytesAt(single.VideoKbps + options.AudioBitrateKbps, titleSeconds), summary);
        }

        if (Bytes(lowestKbps, total) <= DvdVideoAuthoringService.Dvd9PayloadBytes)
        {
            var dual = DvdVideoAuthoringService.PlanVideoBitrate(titleSeconds, WithLayers(options, dualLayer: true));
            return new DvdFitEstimate(DvdFitKind.NeedsDualLayer, total, dual.VideoKbps,
                TitleBytesAt(dual.VideoKbps + options.AudioBitrateKbps, titleSeconds),
                $"{length} of video is too long for a 4.7 GB DVD. Use a dual-layer DVD (DVD+R DL, 8.5 GB): video {dual.VideoKbps:N0} kbps, " +
                $"{Quality(dual.VideoKbps, options)}. Or remove a movie and burn it on a second disc.");
        }

        return new DvdFitEstimate(DvdFitKind.TooLong, total, DvdVideoAuthoringService.MinimumVideoKbps,
            TitleBytesAt(lowestKbps, titleSeconds),
            $"{length} of video is too long for one DVD, even a dual-layer one. A 4.7 GB DVD holds about " +
            $"{FormatLength(LongestAtLowest(DvdVideoAuthoringService.Dvd5PayloadBytes, lowestKbps))} and a dual-layer DVD about " +
            $"{FormatLength(LongestAtLowest(DvdVideoAuthoringService.Dvd9PayloadBytes, lowestKbps))}. Remove movies and burn them on another disc.");
    }

    /// <summary>Plain words for how the picture will look at a video bitrate.</summary>
    public static string Quality(int videoKbps, DvdAuthoringOptions options) => videoKbps switch
    {
        _ when videoKbps >= options.VideoBitrateKbps => "full quality",
        >= 4000 => "very good quality",
        >= 2800 => "good quality",
        _ => "fair quality: fine on a TV, softer in fast scenes"
    };

    /// <summary>"3 h 26 min" or "47 min".</summary>
    public static string FormatLength(double seconds)
    {
        int minutes = (int)Math.Round(Math.Max(0, seconds) / 60.0);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    private static double Bytes(int totalKbps, double seconds) => totalKbps * 1000.0 / 8.0 * DvdVideoAuthoringService.MuxOverhead * seconds;

    private static double LongestAtLowest(double payloadBytes, int totalKbps) => payloadBytes / (totalKbps * 1000.0 / 8.0 * DvdVideoAuthoringService.MuxOverhead);

    private static long[] TitleBytesAt(int totalKbps, IReadOnlyList<double> titleSeconds) =>
        titleSeconds.Select(s => (long)Math.Ceiling(Bytes(totalKbps, s))).ToArray();

    private static DvdAuthoringOptions WithLayers(DvdAuthoringOptions options, bool dualLayer) => new()
    {
        VideoBitrateKbps = options.VideoBitrateKbps,
        MaxVideoBitrateKbps = options.MaxVideoBitrateKbps,
        AudioBitrateKbps = options.AudioBitrateKbps,
        DualLayer = dualLayer
    };
}
