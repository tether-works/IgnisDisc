using System.Text;

namespace CdBurner.Core.Images;

/// <summary>
/// Formats a CueSheet object model into canonical CDRWIN / Red Book CUE sheet text.
/// </summary>
public static class CueSheetWriter
{
    /// <summary>
    /// Formats a CueSheet into canonical CUE sheet text.
    /// </summary>
    public static string Format(CueSheet cueSheet) => WriteToString(cueSheet);

    /// <summary>
    /// Writes a CueSheet to a string.
    /// </summary>
    public static string WriteToString(CueSheet cueSheet)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        Write(cueSheet, writer);
        return sb.ToString();
    }

    /// <summary>
    /// Writes a CueSheet to a file on disk.
    /// </summary>
    public static void WriteToFile(CueSheet cueSheet, string filePath)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        Write(cueSheet, writer);
    }

    /// <summary>
    /// Asynchronously writes a CueSheet to a file on disk.
    /// </summary>
    public static async Task WriteToFileAsync(CueSheet cueSheet, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        await WriteAsync(cueSheet, writer, cancellationToken);
    }

    /// <summary>
    /// Writes a CueSheet to a TextWriter.
    /// </summary>
    public static void Write(CueSheet cueSheet, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentNullException.ThrowIfNull(writer);

        // Remarks
        foreach (var kvp in cueSheet.Remarks)
        {
            if (string.IsNullOrEmpty(kvp.Value))
            {
                writer.WriteLine($"REM {kvp.Key}");
            }
            else if (kvp.Value.Contains(' ') || kvp.Value.Contains('\t') || kvp.Value.Contains('"') || kvp.Value.Contains('\r') || kvp.Value.Contains('\n'))
            {
                writer.WriteLine($"REM {kvp.Key} \"{EscapeString(kvp.Value)}\"");
            }
            else
            {
                writer.WriteLine($"REM {kvp.Key} {kvp.Value}");
            }
        }

        // Disc Catalog
        if (!string.IsNullOrEmpty(cueSheet.Catalog))
        {
            writer.WriteLine($"CATALOG {cueSheet.Catalog}");
        }

        // Disc Title
        if (!string.IsNullOrEmpty(cueSheet.Title))
        {
            writer.WriteLine($"TITLE \"{EscapeString(cueSheet.Title)}\"");
        }

        // Disc Performer
        if (!string.IsNullOrEmpty(cueSheet.Performer))
        {
            writer.WriteLine($"PERFORMER \"{EscapeString(cueSheet.Performer)}\"");
        }

        // Disc Songwriter
        if (!string.IsNullOrEmpty(cueSheet.Songwriter))
        {
            writer.WriteLine($"SONGWRITER \"{EscapeString(cueSheet.Songwriter)}\"");
        }

        // Files
        for (int fileIdx = 0; fileIdx < cueSheet.Files.Count; fileIdx++)
        {
            var file = cueSheet.Files[fileIdx];
            if (file.Tracks.Count > 0 && file.Tracks[0].SplitIndex00 is { DedicatedFileName: not null } dedicated)
            {
                writer.WriteLine($"FILE \"{EscapeString(dedicated.DedicatedFileName)}\" {(dedicated.DedicatedFileType ?? file.FileType).ToUpperInvariant()}");
                WriteTrackPreamble(writer, file.Tracks[0]);
                writer.WriteLine($"    INDEX 00 {dedicated.Position}");
                writer.WriteLine($"FILE \"{EscapeString(file.FileName)}\" {file.FileType.ToUpperInvariant()}");
                WriteTrackIndicesAndPostGap(writer, file.Tracks[0]);
                for (int t = 1; t < file.Tracks.Count; t++)
                {
                    WriteTrackPreamble(writer, file.Tracks[t]);
                    WriteTrackIndicesAndPostGap(writer, file.Tracks[t]);
                }
            }
            else if (fileIdx > 0 && file.Tracks.Count > 0 && file.Tracks[0].SplitIndex00 is { DedicatedFileName: null })
            {
                writer.WriteLine($"FILE \"{EscapeString(file.FileName)}\" {file.FileType.ToUpperInvariant()}");
                WriteTrackIndicesAndPostGap(writer, file.Tracks[0]);
                for (int t = 1; t < file.Tracks.Count; t++)
                {
                    WriteTrackPreamble(writer, file.Tracks[t]);
                    WriteTrackIndicesAndPostGap(writer, file.Tracks[t]);
                }
            }
            else
            {
                writer.WriteLine($"FILE \"{EscapeString(file.FileName)}\" {file.FileType.ToUpperInvariant()}");
                foreach (var track in file.Tracks)
                {
                    WriteTrackPreamble(writer, track);
                    WriteTrackIndicesAndPostGap(writer, track);
                }
            }

            if (fileIdx + 1 < cueSheet.Files.Count
                && cueSheet.Files[fileIdx + 1].Tracks is { Count: > 0 } nextTracks
                && nextTracks[0].SplitIndex00 is { DedicatedFileName: null } splitNext)
            {
                WriteTrackPreamble(writer, nextTracks[0]);
                writer.WriteLine($"    INDEX 00 {splitNext.Position}");
            }
        }
    }

    private static void WriteTrackPreamble(TextWriter writer, CueTrack track)
    {
        writer.WriteLine($"  TRACK {track.TrackNumber:D2} {FormatDataType(track.DataType)}");

        if (!string.IsNullOrEmpty(track.Title))
        {
            writer.WriteLine($"    TITLE \"{EscapeString(track.Title)}\"");
        }

        if (!string.IsNullOrEmpty(track.Performer))
        {
            writer.WriteLine($"    PERFORMER \"{EscapeString(track.Performer)}\"");
        }

        if (!string.IsNullOrEmpty(track.Songwriter))
        {
            writer.WriteLine($"    SONGWRITER \"{EscapeString(track.Songwriter)}\"");
        }

        if (!string.IsNullOrEmpty(track.Isrc))
        {
            writer.WriteLine($"    ISRC {track.Isrc}");
        }

        if (track.Flags.Count > 0)
        {
            writer.WriteLine($"    FLAGS {string.Join(" ", track.Flags)}");
        }

        if (track.PreGap.HasValue)
        {
            writer.WriteLine($"    PREGAP {track.PreGap.Value}");
        }
    }

    private static void WriteTrackIndicesAndPostGap(TextWriter writer, CueTrack track)
    {
        foreach (var index in track.Indices)
        {
            writer.WriteLine($"    INDEX {index.IndexNumber:D2} {index.Position}");
        }

        if (track.PostGap.HasValue)
        {
            writer.WriteLine($"    POSTGAP {track.PostGap.Value}");
        }
    }

    /// <summary>
    /// Asynchronously writes a CueSheet to a TextWriter.
    /// </summary>
    public static async Task WriteAsync(CueSheet cueSheet, TextWriter writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();
        string formatted = WriteToString(cueSheet);
        await writer.WriteAsync(formatted.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Formats a CueTrackDataType into canonical CUE sheet keyword.
    /// </summary>
    public static string FormatDataType(CueTrackDataType type) => type switch
    {
        CueTrackDataType.Audio => "AUDIO",
        CueTrackDataType.Mode1_2048 => "MODE1/2048",
        CueTrackDataType.Mode1_2352 => "MODE1/2352",
        CueTrackDataType.Mode2_2336 => "MODE2/2336",
        CueTrackDataType.Mode2_2352 => "MODE2/2352",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown track data type.")
    };

    private static string EscapeString(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\\\"");
}
