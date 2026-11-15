using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// One subtitle: shown from <see cref="Start"/> to <see cref="End"/>.
/// </summary>
public sealed record SrtCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Reads and writes SubRip (.srt) subtitles. Used to tidy the speech-recognition output: text trimmed, empty or
/// zero-length cues dropped, cues numbered from 1.
/// </summary>
public static class SrtCueList
{
    private static readonly Regex TimingLine = new(
        @"^\s*(?<s>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<e>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses SRT text. Blocks without a valid timing line are skipped.
    /// </summary>
    public static IReadOnlyList<SrtCue> Parse(string srtText)
    {
        ArgumentNullException.ThrowIfNull(srtText);
        var cues = new List<SrtCue>();
        string[] lines = srtText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            Match match = TimingLine.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var text = new List<string>();
            int j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
            {
                text.Add(lines[j].Trim());
            }

            cues.Add(new SrtCue(ParseTime(match.Groups["s"].Value), ParseTime(match.Groups["e"].Value), string.Join("\n", text)));
            i = j;
        }

        return cues;
    }

    /// <summary>
    /// Drops cues with no text or no duration and returns the rest in time order.
    /// </summary>
    public static IReadOnlyList<SrtCue> Clean(IEnumerable<SrtCue> cues) => cues
        .Select(c => c with { Text = c.Text.Trim() })
        .Where(c => c.Text.Length > 0 && c.End > c.Start)
        .OrderBy(c => c.Start)
        .ToList();

    /// <summary>
    /// Keeps at most two cues in a row with the same words (case and punctuation ignored). Speech recognition can
    /// fall into a loop and repeat one line for minutes; two real repeats ("No! No!") stay.
    /// </summary>
    public static IReadOnlyList<SrtCue> DropRepeats(IReadOnlyList<SrtCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var result = new List<SrtCue>(cues.Count);
        string? previous = null;
        int run = 0;
        foreach (SrtCue cue in cues)
        {
            string key = new string(cue.Text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            run = key == previous ? run + 1 : 1;
            previous = key;
            if (run <= 2)
            {
                result.Add(cue);
            }
        }

        return result;
    }

    /// <summary>
    /// Ends each cue no later than 1.5 s plus 70 ms per character after it starts, and at most 7 s: speech
    /// recognition stretches a short line over the silence after it (one "I'm sorry." stayed 30 s on a real film).
    /// Cues are only shortened, never lengthened.
    /// </summary>
    public static IReadOnlyList<SrtCue> LimitDuration(IReadOnlyList<SrtCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return cues.Select(c =>
        {
            TimeSpan limit = TimeSpan.FromMilliseconds(Math.Min(7000, 1500 + 70 * c.Text.Length));
            return c.End - c.Start > limit ? c with { End = c.Start + limit } : c;
        }).ToList();
    }

    /// <summary>
    /// Longest subtitle text kept in one cue: two lines of 42 characters, the usual limit for TV subtitles.
    /// </summary>
    public const int MaxCueCharacters = 84;

    /// <summary>
    /// Splits cues whose text is longer than <paramref name="maxCharacters"/> into several cues at word boundaries,
    /// preferring a break after punctuation, and shares the cue's time between the parts by their length. Speech
    /// recognition with voice activity detection returns whole stretches of speech (10 seconds and 130 characters on
    /// a real film), too long to read on a TV.
    /// </summary>
    public static IReadOnlyList<SrtCue> SplitLong(IReadOnlyList<SrtCue> cues, int maxCharacters = MaxCueCharacters)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 10);

        var result = new List<SrtCue>(cues.Count);
        foreach (SrtCue cue in cues)
        {
            string flat = string.Join(' ', cue.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (flat.Length <= maxCharacters)
            {
                result.Add(cue);
                continue;
            }

            List<string> pieces = PackWords(flat.Split(' '), maxCharacters);
            long totalChars = pieces.Sum(p => (long)p.Length);
            TimeSpan duration = cue.End - cue.Start;
            TimeSpan start = cue.Start;
            long charsSoFar = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                charsSoFar += pieces[i].Length;
                TimeSpan end = i == pieces.Count - 1
                    ? cue.End
                    : cue.Start + TimeSpan.FromTicks(duration.Ticks * charsSoFar / totalChars);
                result.Add(new SrtCue(start, end, pieces[i]));
                start = end;
            }
        }

        return result;
    }

    private static List<string> PackWords(string[] words, int maxCharacters)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (string word in words)
        {
            int added = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
            bool sentenceBreak = current.Length >= maxCharacters / 2 && current.Length > 0 &&
                                 ".?!;".Contains(current[^1], StringComparison.Ordinal);
            if (current.Length > 0 && (added > maxCharacters || sentenceBreak))
            {
                pieces.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }

        return pieces;
    }

    /// <summary>
    /// Writes cues as SRT text (CRLF line ends, numbered from 1).
    /// </summary>
    public static string Format(IReadOnlyList<SrtCue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(FormatTime(cues[i].Start)).Append(" --> ").Append(FormatTime(cues[i].End)).Append("\r\n");
            sb.Append(cues[i].Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
        }

        return sb.ToString();
    }

    private static TimeSpan ParseTime(string value)
    {
        string[] hms = value.Replace('.', ',').Split(':', ',');
        int ms = int.Parse(hms[3].PadRight(3, '0'), CultureInfo.InvariantCulture);
        return new TimeSpan(0,
            int.Parse(hms[0], CultureInfo.InvariantCulture),
            int.Parse(hms[1], CultureInfo.InvariantCulture),
            int.Parse(hms[2], CultureInfo.InvariantCulture),
            ms);
    }

    private static string FormatTime(TimeSpan t) => string.Create(CultureInfo.InvariantCulture,
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}");
}
