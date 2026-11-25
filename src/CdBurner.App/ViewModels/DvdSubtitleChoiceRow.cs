using System.Collections.ObjectModel;
using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.Core.DvdVideo;

namespace CdBurner.App.ViewModels;

/// <summary>Where one movie's subtitles come from.</summary>
public enum DvdSubtitleSource
{
    /// <summary>An .srt with the movie's name was found beside it.</summary>
    FoundBesideMovie,

    /// <summary>The user picked an .srt for this movie.</summary>
    Chosen,

    /// <summary>No .srt; one is made by speech recognition ("Generate subtitles" is on).</summary>
    WillBeGenerated,

    /// <summary>No .srt and none will be made: the title has no subtitles.</summary>
    None,

    /// <summary>An .srt was found or chosen but could not be read; the title has no subtitles unless one is made.</summary>
    Unreadable
}

/// <summary>One editable audio stream entry belonging to a movie row in <see cref="DvdVideoViewModel"/>.</summary>
public sealed class DvdAudioTrackRow : ObservableObject
{
    private string _language;
    private int _channels;
    private DvdAudioContentKind _contentKind;
    private readonly Action? _onChanged;

    public DvdAudioTrackRow(
        string moviePath,
        int streamIndex,
        string? sourcePath,
        string language,
        int channels,
        DvdAudioContentKind contentKind,
        Action? onChanged = null)
    {
        MoviePath = moviePath;
        StreamIndex = streamIndex;
        SourcePath = sourcePath;
        _language = DvdLanguageCodes.NormalizeIso639Part2(language);
        _channels = Math.Clamp(channels, 1, 8);
        _contentKind = contentKind;
        _onChanged = onChanged;
    }

    public string MoviePath { get; }
    public int StreamIndex { get; }
    public string StreamIdLabel => $"0x{Mpeg2Muxer.Ac3SubstreamBase + StreamIndex:X2}";
    public string? SourcePath { get; }
    public string SourceName => string.IsNullOrWhiteSpace(SourcePath)
        ? "Embedded movie audio"
        : Path.GetFileName(SourcePath);

    public IReadOnlyList<int> AvailableChannels { get; } = [2, 6, 1];

    public IReadOnlyList<DvdAudioContentKind> AvailableContentKinds { get; } =
    [
        DvdAudioContentKind.Normal,
        DvdAudioContentKind.DirectorsComments,
        DvdAudioContentKind.AlternateDirectorsComments,
        DvdAudioContentKind.VisuallyImpaired
    ];

    public string Language
    {
        get => _language;
        set
        {
            string normalized = DvdLanguageCodes.NormalizeIso639Part2(value);
            if (SetProperty(ref _language, normalized))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public int Channels
    {
        get => _channels;
        set
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (SetProperty(ref _channels, clamped))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public DvdAudioContentKind ContentKind
    {
        get => _contentKind;
        set
        {
            if (SetProperty(ref _contentKind, value))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public DvdAudioTrackSpec ToSpec() => new(SourcePath, Language, Channels, ContentKind: ContentKind);
}

/// <summary>One editable subtitle stream entry belonging to a movie row in <see cref="DvdVideoViewModel"/>.</summary>
public sealed class DvdSubtitleTrackRow : ObservableObject
{
    private string _language;
    private readonly Action? _onChanged;

    public DvdSubtitleTrackRow(
        string moviePath,
        int streamIndex,
        string? sourcePath,
        string language,
        bool generateWithWhisper,
        Action? onChanged = null)
    {
        MoviePath = moviePath;
        StreamIndex = streamIndex;
        SourcePath = sourcePath;
        _language = string.Equals(language?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : DvdLanguageCodes.NormalizeIso639Part2(language);
        GenerateWithWhisper = generateWithWhisper;
        _onChanged = onChanged;
    }

    public string MoviePath { get; }
    public int StreamIndex { get; }
    public string StreamIdLabel => $"0x{Mpeg2Muxer.SpuSubstreamBase + StreamIndex:X2}";
    public string? SourcePath { get; }
    public bool GenerateWithWhisper { get; }

    public string SourceName => GenerateWithWhisper && string.IsNullOrWhiteSpace(SourcePath)
        ? "Speech recognition (Whisper)"
        : Path.GetFileName(SourcePath ?? string.Empty);

    public string Language
    {
        get => _language;
        set
        {
            string normalized = string.Equals(value?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : DvdLanguageCodes.NormalizeIso639Part2(value);
            if (SetProperty(ref _language, normalized))
            {
                _onChanged?.Invoke();
            }
        }
    }

    public DvdSubtitleTrackSpec ToSpec() => new(SourcePath, Language, GenerateWithWhisper);
}

/// <summary>
/// One movie on the DVD-Video screen and the subtitle file it will get, so the user can see which .srt was found and
/// pick or clear one before authoring, plus configure up to 8 audio and 8 subtitle streams per title.
/// </summary>
public sealed class DvdSubtitleChoiceRow : ObservableObject
{
    private string? _subtitlePath;
    private DvdSubtitleSource _source;
    private string _statusText = string.Empty;
    private string _menuTitleName;
    private readonly Action<string, string>? _onTitleNameChanged;

    public DvdSubtitleChoiceRow(string moviePath, string? initialTitleName = null, Action<string, string>? onTitleNameChanged = null)
    {
        MoviePath = moviePath;
        _menuTitleName = string.IsNullOrWhiteSpace(initialTitleName)
            ? DvdInputNormalizer.CleanMovieTitle(moviePath)
            : initialTitleName;
        _onTitleNameChanged = onTitleNameChanged;
    }

    /// <summary>Full path of the movie.</summary>
    public string MoviePath { get; }

    /// <summary>The movie's file name, for display.</summary>
    public string MovieName => Path.GetFileName(MoviePath);

    /// <summary>Editable title displayed on the DVD title menu for this movie.</summary>
    public string MenuTitleName
    {
        get => _menuTitleName;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref _menuTitleName, normalized))
            {
                _onTitleNameChanged?.Invoke(MoviePath, _menuTitleName);
            }
        }
    }

    /// <summary>The .srt that will be used; null when none.</summary>
    public string? SubtitlePath
    {
        get => _subtitlePath;
        set
        {
            if (SetProperty(ref _subtitlePath, value))
            {
                OnPropertyChanged(nameof(HasSubtitleFile));
            }
        }
    }

    /// <summary>True when an .srt file will be used (found or chosen); the Clear button shows then.</summary>
    public bool HasSubtitleFile => _subtitlePath != null;

    /// <summary>Where the subtitles come from.</summary>
    public DvdSubtitleSource Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    /// <summary>One plain line saying what this movie's subtitles will be.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Per-title audio streams (up to 8, 0x80..0x87).</summary>
    public ObservableCollection<DvdAudioTrackRow> AudioTracks { get; } = new();

    /// <summary>Per-title subtitle streams (up to 8, 0x20..0x27).</summary>
    public ObservableCollection<DvdSubtitleTrackRow> SubtitleTracks { get; } = new();

    public bool HasAudioTracks => AudioTracks.Count > 0;
    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;
    public bool CanAddAudioTrack => AudioTracks.Count < DvdVideoProject.MaxAudioTracksPerTitle;
    public bool CanAddSubtitleTrack => SubtitleTracks.Count < DvdVideoProject.MaxSubtitleTracksPerTitle;

    public void NotifyTrackCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasAudioTracks));
        OnPropertyChanged(nameof(HasSubtitleTracks));
        OnPropertyChanged(nameof(CanAddAudioTrack));
        OnPropertyChanged(nameof(CanAddSubtitleTrack));
    }
}
