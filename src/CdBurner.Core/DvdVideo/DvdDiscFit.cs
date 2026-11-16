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

    /// <summary>Would fit a 4.7 GB DVD compressed, but the user chose a dual-layer DVD for a higher bitrate.</summary>
    DualLayerChosen,

    /// <summary>Too long for a 4.7 GB DVD even at the lowest bitrate; fits a dual-layer DVD (8.5 GB).</summary>
    NeedsDualLayer,

    /// <summary>Too long even for a dual-layer DVD at the lowest bitrate. Cannot be burned as one disc.</summary>
    TooLong
}

/// <summary>
/// The planned size of a DVD made from movies of known running times: the video bitrate the authoring will use, the
/// bytes that gives, and a one-line summary for the screen. <see cref="SingleLayerKbps"/> and
/// <see cref="DualLayerKbps"/> are the bitrates on each kind of disc (null when it does not fit), for offering the
/// choice (<see cref="HasLayerChoice"/>).
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
    public bool DualLayer => Kind is DvdFitKind.NeedsDualLayer or DvdFitKind.DualLayerChosen;

    /// <summary>False only when no DVD can hold it.</summary>
    public bool CanBurn => Kind != DvdFitKind.TooLong;

    /// <summary>Video bitrate on a 4.7 GB DVD; null when it does not fit one.</summary>
    public int? SingleLayerKbps { get; init; }

    /// <summary>Video bitrate on a dual-layer DVD; null when it does not fit one.</summary>
    public int? DualLayerKbps { get; init; }

    /// <summary>
    /// Both discs can take it and the dual-layer one gives a clearly higher bitrate (the 4.7 GB one has to compress):
    /// worth offering the user the choice.
    /// </summary>
    public bool HasLayerChoice { get; init; }
}

/// <summary>
/// Works out, before any conversion, whether movies fit onto a DVD. It uses the same bitrate rule as the authoring
/// (<see cref="DvdVideoAuthoringService.PlanVideoBitrate"/>), so what it predicts is what gets written: the bitrate is
/// lowered to fit a 4.7 GB DVD, down to <see cref="DvdVideoAuthoringService.MinimumVideoKbps"/>; longer than that
/// needs a dual-layer DVD, and longer than a dual-layer DVD holds at that bitrate cannot be burned. When the 4.7 GB
/// DVD has to compress and a dual-layer DVD would keep a clearly higher bitrate, both are offered.
/// </summary>
public static class DvdDiscFit
{
    /// <summary>
    /// A dual-layer DVD is offered when its bitrate is at least this much higher than the 4.7 GB DVD's (smaller gains
    /// are not worth the dearer disc).
    /// </summary>
    public const double WorthwhileGain = 1.15;

    /// <summary>
    /// The fit for titles of the given running times (seconds, in disc order). <see cref="DvdFitKind.Unknown"/> when
    /// any running time is unknown (0 or less). <paramref name="preferDualLayer"/>: the user chose a dual-layer DVD;
    /// used when the choice is offered (<see cref="DvdFitEstimate.HasLayerChoice"/>), else ignored.
    /// </summary>
    public static DvdFitEstimate Estimate(IReadOnlyList<double> titleSeconds, DvdAuthoringOptions options, bool preferDualLayer = false)
    {
        ArgumentNullException.ThrowIfNull(titleSeconds);
        ArgumentNullException.ThrowIfNull(options);
        if (titleSeconds.Count == 0 || titleSeconds.Any(s => !(s > 0) || double.IsInfinity(s)))
        {
            return new DvdFitEstimate(DvdFitKind.Unknown, 0, options.VideoBitrateKbps, Array.Empty<long>(), string.Empty);
        }

        double total = titleSeconds.Sum();
        string length = FormatLength(total);
        int minVideoKbps = options.FitToDisc
            ? DvdVideoAuthoringService.MinimumVideoKbps
            : Math.Clamp(options.VideoBitrateKbps, DvdVideoAuthoringService.MinimumVideoKbps, 9000);
        int lowestKbps = minVideoKbps + options.AudioBitrateKbps;
        int audio = options.AudioBitrateKbps;

        DvdBitratePlan? single = Bytes(lowestKbps, total) <= DvdVideoAuthoringService.Dvd5PayloadBytes
            ? DvdVideoAuthoringService.PlanVideoBitrate(titleSeconds, WithLayers(options, dualLayer: false))
            : null;
        DvdBitratePlan? dual = Bytes(lowestKbps, total) <= DvdVideoAuthoringService.Dvd9PayloadBytes
            ? DvdVideoAuthoringService.PlanVideoBitrate(titleSeconds, WithLayers(options, dualLayer: true))
            : null;

        // A choice only when the 4.7 GB DVD compresses, the dual-layer one keeps clearly more, and its image is really
        // bigger than a 4.7 GB DVD (else the planning margin alone would make a "dual-layer" disc that fits a 4.7 GB one).
        bool choice = single != null && dual != null
            && single.VideoKbps < options.VideoBitrateKbps
            && dual.VideoKbps >= single.VideoKbps * WorthwhileGain
            && Bytes(dual.VideoKbps + audio, total) > DvdLayerBreakCalculator.Dvd5SectorLimit * 2048.0;

        if (single != null && !(choice && preferDualLayer))
        {
            bool full = single.VideoKbps >= options.VideoBitrateKbps;
            string summary = full
                ? $"{length} of video: fits a 4.7 GB DVD at full quality."
                : $"{length} of video: compressed to fit a 4.7 GB DVD (video {single.VideoKbps:N0} kbps, {Quality(single.VideoKbps, options)}).";
            return new DvdFitEstimate(full ? DvdFitKind.FullQuality : DvdFitKind.Compressed, total, single.VideoKbps,
                TitleBytesAt(single.VideoKbps + audio, titleSeconds), summary)
            {
                SingleLayerKbps = single.VideoKbps,
                DualLayerKbps = dual?.VideoKbps,
                HasLayerChoice = choice
            };
        }

        if (single != null && dual != null)
        {
            return new DvdFitEstimate(DvdFitKind.DualLayerChosen, total, dual.VideoKbps,
                TitleBytesAt(dual.VideoKbps + audio, titleSeconds),
                $"{length} of video on a dual-layer DVD (DVD+R DL, 8.5 GB): video {dual.VideoKbps:N0} kbps, {Quality(dual.VideoKbps, options)}.")
            {
                SingleLayerKbps = single.VideoKbps,
                DualLayerKbps = dual.VideoKbps,
                HasLayerChoice = true
            };
        }

        if (dual != null)
        {
            return new DvdFitEstimate(DvdFitKind.NeedsDualLayer, total, dual.VideoKbps,
                TitleBytesAt(dual.VideoKbps + audio, titleSeconds),
                $"{length} of video is too long for a 4.7 GB DVD. Use a dual-layer DVD (DVD+R DL, 8.5 GB): video {dual.VideoKbps:N0} kbps, " +
                $"{Quality(dual.VideoKbps, options)}. Or remove a movie and burn it on a second disc.")
            {
                DualLayerKbps = dual.VideoKbps
            };
        }

        return new DvdFitEstimate(DvdFitKind.TooLong, total, minVideoKbps,
            TitleBytesAt(lowestKbps, titleSeconds),
            options.FitToDisc
                ? $"{length} of video is too long for one DVD, even a dual-layer one. A 4.7 GB DVD holds about " +
                  $"{FormatLength(LongestAtLowest(DvdVideoAuthoringService.Dvd5PayloadBytes, lowestKbps))} and a dual-layer DVD about " +
                  $"{FormatLength(LongestAtLowest(DvdVideoAuthoringService.Dvd9PayloadBytes, lowestKbps))}. Remove movies and burn them on another disc."
                : $"At {minVideoKbps:N0} kbps, {length} of video is too long for one DVD (choose Fit to disc, a lower bitrate, or fewer movies).");
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
        FitToDisc = options.FitToDisc,
        DualLayer = dualLayer
    };
}
