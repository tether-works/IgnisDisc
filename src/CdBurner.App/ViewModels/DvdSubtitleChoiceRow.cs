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

/// <summary>
/// One movie on the DVD-Video screen and the subtitle file it will get, so the user can see which .srt was found and
/// pick or clear one before authoring.
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
}
