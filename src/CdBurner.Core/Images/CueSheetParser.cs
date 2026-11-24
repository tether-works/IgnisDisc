using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Parses CDRWIN / Red Book CUE sheets from text, streams, or files into a CueSheet object model.
/// </summary>
public static class CueSheetParser
{
    /// <summary>
    /// Parses a CUE sheet from a string.
    /// </summary>
    public static CueSheet Parse(string cueText)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        using var reader = new StringReader(cueText);
        return Parse(reader);
    }

    /// <summary>
    /// Parses a CUE sheet from a file on disk.
    /// </summary>
    public static CueSheet ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string text = AudioPlaylistParser.DecodePlaylistText(File.ReadAllBytes(filePath));
        return Parse(text);
    }

    /// <summary>
    /// Asynchronously parses a CUE sheet from a file on disk.
    /// </summary>
    public static async Task<CueSheet> ParseFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Parse(AudioPlaylistParser.DecodePlaylistText(bytes));
    }

    /// <summary>
    /// Attempts to parse a CUE sheet from a string.
    /// </summary>
    public static bool TryParse(string? cueText, [NotNullWhen(true)] out CueSheet? cueSheet, out string? errorMessage)
    {
        cueSheet = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(cueText))
        {
            errorMessage = "CUE sheet text cannot be null or empty.";
            return false;
        }

        try
        {
            cueSheet = Parse(cueText);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Parses a CUE sheet from a TextReader.
    /// </summary>
    public static CueSheet Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var state = new CueParseState();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            state.ProcessLine(line);
        }
        return state.Build();
    }

    /// <summary>
    /// Asynchronously parses a CUE sheet from a TextReader.
    /// </summary>
    public static async Task<CueSheet> ParseAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var state = new CueParseState();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            state.ProcessLine(line);
        }
        return state.Build();
    }

    private sealed class CueParseState
    {
        private string? _catalog;
        private string? _discTitle;
        private string? _discPerformer;
        private string? _discSongwriter;
        private readonly Dictionary<string, string> _remarks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CueFileEntry> _files = new();
        private CueFileEntryBuilder? _currentFile;
        private CueTrackBuilder? _currentTrack;

        private void FinishCurrentTrack()
        {
            if (_currentTrack != null)
            {
                _currentFile ??= new CueFileEntryBuilder(string.Empty, "BINARY");
                _currentFile.Tracks.Add(_currentTrack.Build());
                _currentTrack = null;
            }
        }

        private void FinishCurrentFile()
        {
            FinishCurrentTrack();
            if (_currentFile != null)
            {
                _files.Add(_currentFile.Build());
                _currentFile = null;
            }
        }

        public void ProcessLine(string line)
        {
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0)
            {
                return;
            }

            var tokens = TokenizeLine(trimmed);
            if (tokens.Count == 0)
            {
                return;
            }

            string cmd = tokens[0].TrimStart('\uFEFF').ToUpperInvariant();
            switch (cmd)
            {
                case "REM":
                    if (tokens.Count >= 2)
                    {
                        string key = tokens[1];
                        string val = tokens.Count >= 3 ? string.Join(" ", tokens.Skip(2)) : string.Empty;
                        _remarks[key] = val;
                    }
                    break;

                case "CATALOG":
                    if (tokens.Count >= 2)
                    {
                        _catalog = tokens[1];
                    }
                    break;

                case "TITLE":
                    if (tokens.Count >= 2)
                    {
                        string title = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (_currentTrack != null)
                        {
                            _currentTrack.Title = title;
                        }
                        else
                        {
                            _discTitle = title;
                        }
                    }
                    break;

                case "PERFORMER":
                    if (tokens.Count >= 2)
                    {
                        string performer = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (_currentTrack != null)
                        {
                            _currentTrack.Performer = performer;
                        }
                        else
                        {
                            _discPerformer = performer;
                        }
                    }
                    break;

                case "SONGWRITER":
                    if (tokens.Count >= 2)
                    {
                        string songwriter = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (_currentTrack != null)
                        {
                            _currentTrack.Songwriter = songwriter;
                        }
                        else
                        {
                            _discSongwriter = songwriter;
                        }
                    }
                    break;

                case "FILE":
                    {
                        // EAC "gaps appended to previous track" layout: TRACK n+1 opens (with only INDEX 00) inside
                        // FILE n (or a dedicated pre-gap FILE) and its INDEX 01 follows the next FILE line. Carry that
                        // still-open track into the new FILE and preserve the cross-FILE INDEX 00 via SplitIndex00.
                        var carried = TakeTrackAwaitingIndex01(ref _currentTrack, ref _currentFile);
                        FinishCurrentFile();
                        var (fileName, fileType) = ParseFileTokens(tokens);
                        _currentFile = new CueFileEntryBuilder(fileName, fileType);
                        _currentTrack = carried;
                    }
                    break;

                case "TRACK":
                    FinishCurrentTrack();
                    int trackNum = 1;
                    if (tokens.Count >= 2 &&
                        (!int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out trackNum) || trackNum < 1))
                    {
                        throw new FormatException($"Invalid CUE track number: '{tokens[1]}'.");
                    }
                    CueTrackDataType dataType = tokens.Count >= 3 ? ParseTrackDataType(tokens[2]) : CueTrackDataType.Audio;
                    _currentTrack = new CueTrackBuilder(trackNum, dataType);
                    break;

                case "ISRC":
                    if (tokens.Count >= 2 && _currentTrack != null)
                    {
                        _currentTrack.Isrc = tokens[1];
                    }
                    break;

                case "FLAGS":
                    if (_currentTrack != null)
                    {
                        for (int i = 1; i < tokens.Count; i++)
                        {
                            _currentTrack.Flags.Add(tokens[i].ToUpperInvariant());
                        }
                    }
                    break;

                case "PREGAP":
                    if (tokens.Count >= 2 && _currentTrack != null)
                    {
                        _currentTrack.PreGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "POSTGAP":
                    if (tokens.Count >= 2 && _currentTrack != null)
                    {
                        _currentTrack.PostGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "INDEX":
                    if (tokens.Count >= 3 && _currentTrack != null)
                    {
                        if (!int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out int idxNum) || idxNum > 99)
                        {
                            throw new FormatException($"Invalid CUE index number: '{tokens[1]}'.");
                        }
                        MsfAddress pos = MsfAddress.Parse(tokens[2]);
                        _currentTrack.Indices.Add(new CueIndex(idxNum, pos));
                    }
                    break;
            }
        }

        public CueSheet Build()
        {
            FinishCurrentFile();
            return new CueSheet(_catalog, _discTitle, _discPerformer, _discSongwriter, _files, _remarks);
        }
    }

    /// <summary>
    /// Parses a CUE track data type string (e.g. AUDIO, MODE1/2048, MODE1/2352, MODE2/2336, MODE2/2352).
    /// </summary>
    public static CueTrackDataType ParseTrackDataType(string s)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(s);
        string normalized = s.Trim().ToUpperInvariant().Replace('_', '/');
        return normalized switch
        {
            "AUDIO" => CueTrackDataType.Audio,
            "MODE1/2048" => CueTrackDataType.Mode1_2048,
            "MODE1/2352" => CueTrackDataType.Mode1_2352,
            "MODE2/2336" => CueTrackDataType.Mode2_2336,
            "MODE2/2352" => CueTrackDataType.Mode2_2352,
            _ => throw new FormatException($"Unsupported or unrecognized CUE track data type: '{s}'.")
        };
    }

    private static readonly HashSet<string> KnownFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BINARY", "MOTOROLA", "AIFF", "WAVE", "MP3", "FLAC", "OGG", "AAC", "WAV", "APE", "WV", "TTA"
    };

    private static (string FileName, string FileType) ParseFileTokens(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            return (string.Empty, "BINARY");
        }

        if (tokens.Count == 2)
        {
            return (tokens[1], "BINARY");
        }

        if (tokens.Count == 3)
        {
            return (tokens[1], tokens[2]);
        }

        // Unquoted filename with spaces: if the last token is a known CUE file type, join middle tokens
        string lastToken = tokens[^1];
        if (KnownFileTypes.Contains(lastToken))
        {
            string joinedFileName = string.Join(" ", tokens.Skip(1).Take(tokens.Count - 2));
            return (joinedFileName, lastToken);
        }

        return (tokens[1], tokens[2]);
    }

    internal static List<string> TokenizeLine(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"')
            {
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        sb.Append(line[i]);
                        i++;
                    }
                }
                tokens.Add(sb.ToString());
                if (i < line.Length && line[i] == '"')
                {
                    i++; // skip closing quote
                }
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                tokens.Add(line[start..i]);
            }
        }
        return tokens;
    }

    private static CueTrackBuilder? TakeTrackAwaitingIndex01(
        ref CueTrackBuilder? currentTrack,
        ref CueFileEntryBuilder? currentFile)
    {
        if (currentTrack == null || currentTrack.Indices.Any(i => i.IndexNumber >= 1))
        {
            return null;
        }

        CueIndex? idx00 = currentTrack.Indices.FirstOrDefault(i => i.IndexNumber == 0);
        var carried = currentTrack;
        currentTrack = null;
        carried.Indices.Clear();

        if (currentFile != null && currentFile.Tracks.Count == 0)
        {
            carried.SplitIndex00 = new CueSplitIndex00(
                idx00?.Position ?? new MsfAddress(0, 0, 0),
                currentFile.FileName,
                currentFile.FileType);
            currentFile = null;
        }
        else if (idx00 != null)
        {
            carried.SplitIndex00 = new CueSplitIndex00(idx00.Position);
        }

        return carried;
    }

    private sealed class CueFileEntryBuilder(string fileName, string fileType)
    {
        public string FileName { get; } = fileName;
        public string FileType { get; } = fileType;
        public List<CueTrack> Tracks { get; } = new();

        public CueFileEntry Build() => new(FileName, FileType, Tracks);
    }

    private sealed class CueTrackBuilder(int trackNumber, CueTrackDataType dataType)
    {
        public int TrackNumber { get; } = trackNumber;
        public CueTrackDataType DataType { get; } = dataType;
        public string? Title { get; set; }
        public string? Performer { get; set; }
        public string? Songwriter { get; set; }
        public string? Isrc { get; set; }
        public MsfAddress? PreGap { get; set; }
        public MsfAddress? PostGap { get; set; }
        public List<string> Flags { get; } = new();
        public List<CueIndex> Indices { get; } = new();
        public CueSplitIndex00? SplitIndex00 { get; set; }

        public CueTrack Build() => new(
            TrackNumber,
            DataType,
            Title,
            Performer,
            Songwriter,
            Isrc,
            PreGap,
            PostGap,
            Flags,
            Indices,
            SplitIndex00);
    }
}
