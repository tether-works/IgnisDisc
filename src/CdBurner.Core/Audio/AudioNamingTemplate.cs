using System.Globalization;
using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>Values a naming template can use for one track.</summary>
/// <param name="AlbumArtist">Album artist (used for the folder).</param>
/// <param name="Artist">Track artist; falls back to <paramref name="AlbumArtist"/> when empty.</param>
/// <param name="Album">Album title.</param>
/// <param name="Year">Release year, or null when unknown.</param>
/// <param name="TrackNumber">1-based track number (0 for hidden track one audio).</param>
/// <param name="TrackTotal">Number of tracks on the album.</param>
/// <param name="Title">Track title.</param>
/// <param name="Format">Output format, for templates that keep each format in its own folder.</param>
/// <param name="DiscNumber">Disc number in a multi-disc set (1 for a single disc).</param>
public sealed record AudioNamingFields(
    string AlbumArtist,
    string Artist,
    string Album,
    string? Year,
    int TrackNumber,
    int TrackTotal,
    string Title,
    AudioRipFormat Format,
    int DiscNumber = 1);

/// <summary>
/// Turns a naming template such as <c>{albumartist}\{album}[ ({year})]\{track} - {title}</c> into a relative file
/// path. Tokens: <c>{albumartist}</c>, <c>{artist}</c>, <c>{album}</c>, <c>{year}</c>, <c>{track}</c> (two digits),
/// <c>{tracktotal}</c>, <c>{title}</c>, <c>{disc}</c>, <c>{format}</c> (FLAC, MP3, ...). Text in square brackets is
/// dropped when a token inside it is empty, so <c>[ ({year})]</c> disappears when the year is unknown.
/// <para>
/// Tag text can never leave the target folder: every path segment is cleaned with
/// <see cref="AudioCdRipperService.SanitizeFileName"/> after the tokens are filled in, so a slash, "..", a drive
/// letter or a reserved device name inside a title becomes an ordinary file name. Only the template's own
/// separators create folders.
/// </para>
/// </summary>
public static class AudioNamingTemplate
{
    /// <summary>Artist folder, album folder with year, then "NN - Title".</summary>
    public const string Default = @"{albumartist}\{album}[ ({year})]\{track} - {title}";

    /// <summary>Everything in one folder, named "NN - Artist - Title" like a plain rip.</summary>
    public const string Flat = "{track} - {artist} - {title}";

    private static readonly string[] Tokens =
        ["albumartist", "artist", "album", "year", "track", "tracktotal", "title", "disc", "format"];

    /// <summary>Returns a plain-language problem with <paramref name="template"/>, or null when it can be used.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "The naming template is empty.";
        }

        if (template.Length > 500)
        {
            return "The naming template is longer than 500 characters.";
        }

        if (Path.IsPathRooted(template) || template.Contains(':'))
        {
            return "The naming template must be a relative path (no drive letter or leading slash).";
        }

        int depth = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '[')
            {
                if (++depth > 1) return "Square brackets in the naming template cannot be nested.";
            }
            else if (c == ']')
            {
                if (--depth < 0) return "The naming template has a ']' without a matching '['.";
            }
            else if (c == '{')
            {
                int end = template.IndexOf('}', i + 1);
                if (end < 0) return "The naming template has a '{' without a matching '}'.";
                string token = template[(i + 1)..end];
                if (!Tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Unknown token {{{token}}} in the naming template. Use: {string.Join(", ", Tokens.Select(t => "{" + t + "}"))}.";
                }

                i = end;
            }
            else if (c == '}')
            {
                return "The naming template has a '}' without a matching '{'.";
            }
        }

        if (depth != 0) return "The naming template has a '[' without a matching ']'.";

        string[] segments = template.Split('\\', '/');
        if (segments.Any(s => s.Trim() is "" or "." or ".."))
        {
            return "The naming template has an empty, '.' or '..' folder name.";
        }

        if (!segments[^1].Contains("{track}", StringComparison.OrdinalIgnoreCase) &&
            !segments[^1].Contains("{title}", StringComparison.OrdinalIgnoreCase))
        {
            return "The file name part of the naming template needs {track} or {title}, or every track gets the same name.";
        }

        return null;
    }

    /// <summary>
    /// Fills in <paramref name="template"/> and returns a relative path ending in <paramref name="extension"/>
    /// (for example ".mp3"), with Windows separators and every segment cleaned for use as a file or folder name.
    /// </summary>
    /// <exception cref="ArgumentException">The template is not valid (see <see cref="Validate"/>).</exception>
    public static string Render(string template, AudioNamingFields fields, string extension)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(extension);
        string? problem = Validate(template);
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(template));
        }

        var segments = template.Split('\\', '/');
        var cleaned = new List<string>(segments.Length);
        for (int s = 0; s < segments.Length; s++)
        {
            string filled = FillSegment(segments[s], fields).Trim();
            bool isFile = s == segments.Length - 1;
            string name = AudioCdRipperService.SanitizeFileName(isFile ? filled + extension : filled);
            if (isFile && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                // SanitizeFileName shortens long names; keep the extension.
                name = AudioCdRipperService.SanitizeFileName(filled[..Math.Min(filled.Length, 100 - extension.Length)]) + extension;
            }

            cleaned.Add(name);
        }

        return string.Join('\\', cleaned);
    }

    private static string FillSegment(string segment, AudioNamingFields f)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < segment.Length)
        {
            if (segment[i] == '[')
            {
                int end = segment.IndexOf(']', i + 1);
                string inner = segment[(i + 1)..end];
                string innerFilled = FillTokens(inner, f, out bool anyEmpty);
                if (!anyEmpty)
                {
                    sb.Append(innerFilled);
                }

                i = end + 1;
            }
            else
            {
                int next = segment.IndexOf('[', i);
                string plain = next < 0 ? segment[i..] : segment[i..next];
                sb.Append(FillTokens(plain, f, out _));
                i = next < 0 ? segment.Length : next;
            }
        }

        return sb.ToString();
    }

    private static string FillTokens(string text, AudioNamingFields f, out bool anyEmpty)
    {
        anyEmpty = false;
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                sb.Append(text[i++]);
                continue;
            }

            int end = text.IndexOf('}', i + 1);
            string value = Value(text[(i + 1)..end], f);
            anyEmpty |= value.Length == 0;
            // Tag text may not create folders: separators inside a value are replaced here, before the path is split.
            sb.Append(value.Replace('\\', '_').Replace('/', '_'));
            i = end + 1;
        }

        return sb.ToString();
    }

    private static string Value(string token, AudioNamingFields f)
    {
        string albumArtist = Clean(f.AlbumArtist, "Unknown Artist");
        return token.ToLowerInvariant() switch
        {
            "albumartist" => albumArtist,
            "artist" => Clean(f.Artist, albumArtist),
            "album" => Clean(f.Album, "Unknown Album"),
            "year" => f.Year?.Trim() ?? string.Empty,
            "track" => f.TrackNumber.ToString("D2", CultureInfo.InvariantCulture),
            "tracktotal" => f.TrackTotal > 0 ? f.TrackTotal.ToString("D2", CultureInfo.InvariantCulture) : string.Empty,
            "title" => Clean(f.Title, $"Track {f.TrackNumber:D2}"),
            "disc" => f.DiscNumber.ToString(CultureInfo.InvariantCulture),
            "format" => AudioRipFormats.DisplayName(f.Format),
            _ => string.Empty
        };
    }

    private static string Clean(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "Unknown Artist" && fallback != "Unknown Artist"
            ? fallback
            : value.Trim();
}
