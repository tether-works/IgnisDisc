using System.Globalization;

namespace CdBurner.Core.Audio;

/// <summary>One track whose title differs between releases that share the disc ID.</summary>
/// <param name="TrackNumber">1-based track number; 0 for a difference in album title or album artist.</param>
/// <param name="Titles">The distinct titles (or album names), in release order.</param>
public sealed record MusicBrainzTrackDifference(int TrackNumber, IReadOnlyList<string> Titles);

/// <summary>
/// Helpers for the case where one disc ID matches several MusicBrainz releases (for example a German and an
/// American pressing with different track titles), so the user can pick the edition that is on the shelf.
/// </summary>
public static class MusicBrainzReleaseChoice
{
    /// <summary>
    /// Album title, album artist or any track title (trimmed, case-insensitive) that differs between releases.
    /// Empty when there are 0 or 1 releases or they all agree. Album-level differences use track number 0.
    /// </summary>
    public static IReadOnlyList<MusicBrainzTrackDifference> Differences(IReadOnlyList<MusicBrainzRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var result = new List<MusicBrainzTrackDifference>();
        if (releases.Count < 2)
        {
            return result;
        }

        AddIfDifferent(result, 0, releases.Select(r => r.Title));
        AddIfDifferent(result, 0, releases.Select(r => r.Artist));

        var positions = releases.SelectMany(r => r.Tracks).Select(t => t.Position).Distinct().Order().ToList();
        foreach (int position in positions)
        {
            var titles = new List<string>();
            foreach (var release in releases)
            {
                var track = release.Tracks.FirstOrDefault(t => t.Position == position);
                if (track != null)
                {
                    titles.Add(track.Title);
                }
            }

            AddIfDifferent(result, position, titles);
        }

        return result;
    }

    private static void AddIfDifferent(List<MusicBrainzTrackDifference> result, int trackNumber, IEnumerable<string?> values)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (seen.Add(Normalize(trimmed)))
            {
                distinct.Add(trimmed);
            }
        }

        if (distinct.Count > 1)
        {
            result.Add(new MusicBrainzTrackDifference(trackNumber, distinct));
        }
    }

    /// <summary>
    /// Folds differences that only come from typing style, so they do not count as different editions: curly and
    /// straight quotes and apostrophes, en and em dashes, and runs of spaces.
    /// </summary>
    internal static string Normalize(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        bool lastSpace = false;
        foreach (char c in value)
        {
            char folded = c switch
            {
                '‘' or '’' or '‛' or '′' or '`' or '´' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                ' ' or ' ' or ' ' => ' ',
                _ => c
            };
            if (folded == ' ')
            {
                if (lastSpace) continue;
                lastSpace = true;
            }
            else
            {
                lastSpace = false;
            }

            sb.Append(folded);
        }

        return sb.ToString().Trim();
    }

    /// <summary>One line for a release list, for example "2: Sehnsucht - Rammstein (US, 1997)".</summary>
    public static string Describe(MusicBrainzRelease release, int oneBasedIndex)
    {
        ArgumentNullException.ThrowIfNull(release);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(release.Country))
        {
            details.Add(release.Country.Trim());
        }

        if (!string.IsNullOrWhiteSpace(release.Date))
        {
            details.Add(release.Date.Trim());
        }

        string suffix = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{oneBasedIndex}: {release.Title} - {release.Artist}{suffix}");
    }

    /// <summary>Finds a release by its 1-based number or by its MusicBrainz release ID (case-insensitive); null when nothing matches.</summary>
    public static MusicBrainzRelease? Select(IReadOnlyList<MusicBrainzRelease> releases, string selector)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        string text = selector.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= releases.Count ? releases[number - 1] : null;
        }

        return releases.FirstOrDefault(r => string.Equals(r.ReleaseId, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first four characters of the release date when they are digits ("1997-03-24" gives "1997"), otherwise null.</summary>
    public static string? Year(MusicBrainzRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        string? date = release.Date?.Trim();
        if (date == null || date.Length < 4)
        {
            return null;
        }

        string year = date[..4];
        return year.All(char.IsAsciiDigit) ? year : null;
    }
}
