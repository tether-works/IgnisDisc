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

        bool anyExplicitWhisper = options.DefaultSubtitleTracks.Any(t => t.GenerateWithWhisper && string.IsNullOrWhiteSpace(t.SourcePath)) ||
                                  options.TitleSubtitleTracks.Values.Any(list => list.Any(t => t.GenerateWithWhisper && string.IsNullOrWhiteSpace(t.SourcePath)));

        if (!options.GenerateSubtitles && !anyExplicitWhisper)
        {
            return null;
        }

        SubtitleGenerator effectiveGenerator = generator ?? ((movie, p, ct) => new SubtitleGenerationService().GenerateAsync(
            movie,
            new SubtitleGenerationOptions { Language = options.SubtitleLanguage, FfmpegPath = options.CustomFfmpegPath },
            p,
            ct));

        // Materialize DefaultSubtitleTracks into per-movie entries so multi-track + Whisper resolution stays per-title.
        if (options.DefaultSubtitleTracks.Count > 0)
        {
            foreach (string movie in moviePaths)
            {
                if (!DvdVideoAuthoringService.TryGetMovieKey(options.TitleSubtitleTracks, movie, out _))
                {
                    options.TitleSubtitleTracks[movie] = options.DefaultSubtitleTracks.ToList();
                }
            }
        }

        // Handle movies that have explicit multi-track lists (may combine external .srt files with Whisper-generated tracks).
        var multiTrackMovies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var whisperJobs = new List<(string Movie, int? TrackIndex, string Language)>();

        foreach (string movie in moviePaths)
        {
            if (DvdVideoAuthoringService.TryGetMovieKey(options.TitleSubtitleTracks, movie, out string? existingKey) &&
                existingKey != null &&
                options.TitleSubtitleTracks[existingKey] is { Count: > 0 } tracks)
            {
                multiTrackMovies.Add(movie);
                bool addedExplicit = false;
                for (int t = 0; t < tracks.Count && t < DvdVideoProject.MaxSubtitleTracksPerTitle; t++)
                {
                    if (tracks[t].GenerateWithWhisper && string.IsNullOrWhiteSpace(tracks[t].SourcePath))
                    {
                        string lang = tracks[t].Language;
                        if (string.IsNullOrWhiteSpace(lang)) lang = options.SubtitleLanguage;
                        whisperJobs.Add((movie, t, lang));
                        addedExplicit = true;
                    }
                }

                if (options.GenerateSubtitles && !addedExplicit && tracks.Count < DvdVideoProject.MaxSubtitleTracksPerTitle)
                {
                    string targetLang = options.SubtitleLanguage;
                    string normTarget = DvdLanguageCodes.NormalizeIso639Part2(targetLang);
                    bool alreadyHasLang = string.Equals(targetLang, "auto", StringComparison.OrdinalIgnoreCase)
                        ? tracks.Any(t => t.GenerateWithWhisper)
                        : tracks.Any(t => string.Equals(t.NormalizedLanguage, normTarget, StringComparison.OrdinalIgnoreCase));
                    if (!alreadyHasLang)
                    {
                        whisperJobs.Add((movie, null, targetLang));
                    }
                }
            }
        }

        // Legacy single-subtitle path for movies without explicit TitleSubtitleTracks.
        if (options.GenerateSubtitles)
        {
            var legacyMovies = moviePaths.Where(m => !multiTrackMovies.Contains(m)).ToList();
            List<string> missing = legacyMovies.Where(m => DvdVideoAuthoringService.FindSubtitleFile(m, options) == null).ToList();
            foreach (string movie in legacyMovies.Except(missing, StringComparer.OrdinalIgnoreCase))
            {
                logNotes.Add($"Subtitles for '{Path.GetFileName(movie)}': {Path.GetFileName(DvdVideoAuthoringService.FindSubtitleFile(movie, options))} (not generated).");
            }
            foreach (string movie in missing)
            {
                whisperJobs.Add((movie, -1, options.SubtitleLanguage));
            }
        }

        for (int i = 0; i < whisperJobs.Count; i++)
        {
            var (movie, trackIndex, lang) = whisperJobs[i];
            string label = whisperJobs.Count > 1 ? $" ({i + 1} of {whisperJobs.Count})" : string.Empty;
            var jobProgress = progress == null
                ? null
                : new SyncProgress<SubtitleGenerationProgress>(p => progress.Report((3.0, $"Subtitles{label}: {p.Status}")));

            SubtitleGenerationResult result;
            if (generator == null && !string.Equals(lang, options.SubtitleLanguage, StringComparison.OrdinalIgnoreCase))
            {
                result = await new SubtitleGenerationService().GenerateAsync(
                    movie,
                    new SubtitleGenerationOptions { Language = lang, FfmpegPath = options.CustomFfmpegPath },
                    jobProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await effectiveGenerator(movie, jobProgress, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success || result.SrtPath == null)
            {
                return $"Generating subtitles for '{Path.GetFileName(movie)}' failed: {result.ErrorMessage}";
            }

            if (trackIndex == -1)
            {
                options.SubtitleFiles[movie] = result.SrtPath;
            }
            else if (DvdVideoAuthoringService.TryGetMovieKey(options.TitleSubtitleTracks, movie, out string? key) && key != null)
            {
                var list = options.TitleSubtitleTracks[key];
                if (trackIndex is int idx && idx >= 0 && idx < list.Count)
                {
                    list[idx] = list[idx] with { SourcePath = result.SrtPath, GenerateWithWhisper = true };
                }
                else if (list.Count < DvdVideoProject.MaxSubtitleTracksPerTitle)
                {
                    list.Add(new DvdSubtitleTrackSpec(result.SrtPath, lang, GenerateWithWhisper: true));
                }
            }

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
