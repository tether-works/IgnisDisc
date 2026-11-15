using CdBurner.Core.Subtitles;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Before DVD-Video authoring, makes subtitles by speech recognition for each movie that has none: no entry in
/// <see cref="DvdAuthoringOptions.SubtitleFiles"/> and no .srt beside it (the same rule authoring uses,
/// <see cref="DvdVideoAuthoringService.FindSubtitleFile"/>). The .srt is written by
/// <see cref="SubtitleGenerationService"/> to its cache, never next to the movie, and added to
/// <see cref="DvdAuthoringOptions.SubtitleFiles"/>, so the title menu and subtitle stream pick it up.
/// </summary>
public static class DvdSubtitleSourceResolver
{
    /// <summary>
    /// Generates subtitles for one movie; the default calls <see cref="SubtitleGenerationService.GenerateAsync"/>.
    /// </summary>
    public delegate Task<SubtitleGenerationResult> SubtitleGenerator(
        string moviePath,
        IProgress<SubtitleGenerationProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// With <see cref="DvdAuthoringOptions.GenerateSubtitles"/> on, generates subtitles for the movies in
    /// <paramref name="moviePaths"/> that have none. Returns null when nothing failed, or an error message when a
    /// generation failed (authoring should stop: the user asked for subtitles).
    /// </summary>
    public static async Task<string?> ResolveAsync(
        IReadOnlyList<string> moviePaths,
        DvdAuthoringOptions options,
        ICollection<string> logNotes,
        IProgress<(double Percent, string Status)>? progress,
        SubtitleGenerator? generator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moviePaths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logNotes);

        if (!options.GenerateSubtitles)
        {
            return null;
        }

        List<string> missing = moviePaths.Where(m => DvdVideoAuthoringService.FindSubtitleFile(m, options) == null).ToList();
        foreach (string movie in moviePaths.Except(missing, StringComparer.OrdinalIgnoreCase))
        {
            logNotes.Add($"Subtitles for '{Path.GetFileName(movie)}': {Path.GetFileName(DvdVideoAuthoringService.FindSubtitleFile(movie, options))} (not generated).");
        }

        generator ??= (movie, p, ct) => new SubtitleGenerationService().GenerateAsync(
            movie,
            new SubtitleGenerationOptions { Language = options.SubtitleLanguage, FfmpegPath = options.CustomFfmpegPath },
            p,
            ct);

        for (int i = 0; i < missing.Count; i++)
        {
            string movie = missing[i];
            string label = missing.Count > 1 ? $" ({i + 1} of {missing.Count})" : string.Empty;
            var jobProgress = progress == null
                ? null
                : new SyncProgress<SubtitleGenerationProgress>(p => progress.Report((3.0, $"Subtitles{label}: {p.Status}")));

            SubtitleGenerationResult result = await generator(movie, jobProgress, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.SrtPath == null)
            {
                return $"Generating subtitles for '{Path.GetFileName(movie)}' failed: {result.ErrorMessage}";
            }

            options.SubtitleFiles[movie] = result.SrtPath;
            string how = result.FromCache
                ? "reused from an earlier run"
                : $"generated in {result.Elapsed.TotalMinutes:F1} min" + (result.Backend != null ? $" on the {result.Backend}" : string.Empty);
            logNotes.Add($"Subtitles for '{Path.GetFileName(movie)}': {result.CueCount} machine-generated subtitles, {how} ({result.SrtPath}).");
            if (result.ErrorMessage != null)
            {
                logNotes.Add(result.ErrorMessage);
            }
        }

        return null;
    }

    /// <summary>Reports on the calling thread (unlike <see cref="Progress{T}"/>, which posts to a sync context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
