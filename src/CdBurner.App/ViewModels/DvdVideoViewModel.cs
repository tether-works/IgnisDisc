using System.Windows.Media.Imaging;
using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using System.Net.Http;
using CdBurner.Core.Primitives;
using CdBurner.Core.Subtitles;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.App.ViewModels;

/// <summary>Numeric option paired with a display label for DVD-Video ComboBoxes.</summary>
public sealed record DvdIntChoice(int Value, string Label);

/// <summary>Video bitrate/quality preset for DVD-Video authoring.</summary>
public sealed record DvdQualityPreset(string Id, string Label, bool FitToDisc, int VideoKbps, int MaxVideoKbps);

/// <summary>
/// View model managing DVD-Video authoring, VIDEO_TS structure verification,
/// 32 KB (16-sector) IFO/BUP ECC alignment gap inspection, DVD-9 layer break selection, and burning.
/// </summary>
public class DvdVideoViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;

    private string _videoTsFolderPath = string.Empty;
    private string _volumeLabel = "DVD_VIDEO";
    private bool _isStructureValid;
    private bool _isDualLayerRequired;
    private int _selectedLayerBreakLba;
    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private string? _lastBuiltIsoPath;
    private long _totalSizeBytes;
    private int _totalSectors;
    private int _titleSetCount;
    private bool _hasAudioTs;

    private DvdVideoStandard _selectedStandard = DvdVideoStandard.Ntsc;
    private DvdAspectRatio _selectedAspectRatio = DvdAspectRatio.Widescreen16x9;
    private bool _autoCreatePlayMenu = true;
    private int _menuAutoPlaySeconds;
    private DvdAfterTitleAction _afterTitleAction = DvdAfterTitleAction.PlayNextTitle;
    private int _chapterIntervalSeconds = 300;
    private bool _subtitlesOnByDefault;
    private DvdSubtitleSize _subtitleSize = DvdSubtitleSize.Medium;
    private bool _fitToDisc = true;
    private int _videoBitrateKbps = 6000;
    private int _maxVideoBitrateKbps = 8000;
    private DvdQualityPreset _selectedQualityPreset;
    private bool _generateSubtitles;
    private string _subtitleLanguage = "en";
    private WhisperInstallation? _subtitleTool;
    private string _subtitleToolStatusText = string.Empty;
    private string _subtitleToolOfferText = string.Empty;
    private string _menuTitle = "DVD Video";
    private bool _requiresAutoAuthoring;
    private string? _lastAuthoredVideoTsPath;
    private string? _tempAuthoredDirectory;
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private string? _customFfmpegPath;
    private bool _isFfmpegAvailable;
    private string _ffmpegStatusText = string.Empty;
    private bool _verifyAfterBurn;
    private bool _ejectAfterBurn;
    private CancellationTokenSource? _activeCts;

    /// <summary>Running times read so far, by movie path (0: could not be read), for <see cref="FitEstimate"/>.</summary>
    private readonly Dictionary<string, double> _probedSeconds = new(StringComparer.OrdinalIgnoreCase);
    private int _probeGeneration;
    private DvdFitEstimate? _fitEstimate;
    private string _fitSummaryText = string.Empty;
    private bool _useDualLayerDisc;
    private bool _dualLayer;
    private bool _generateChapterMenus;
    private string? _menuBackgroundImagePath;
    private string? _menuAudioPath;
    private DvdMenuFontPreset _selectedMenuFontPreset = DvdMenuFontPreset.ModernSans;
    private DvdMenuHighlightPreset _selectedMenuHighlightPreset = DvdMenuHighlightPreset.Amber;
    private BitmapSource? _menuPreviewImage;
    private int _menuPreviewPageIndex;
    private int _menuPreviewPageCount = 1;
    private bool _previewChapterSubmenu;

    #region Properties

    /// <summary>
    /// Underlying optical drive discovery and enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Underlying optical burning backend engine.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Selected source video files when authoring from arbitrary movie media.
    /// </summary>
    public ObservableCollection<string> SourceMediaFiles { get; } = new();

    /// <summary>
    /// One row per movie in <see cref="SourceMediaFiles"/>: the subtitle file it will get (found beside it, chosen by
    /// the user, generated, or none), so it is clear before authoring which .srt goes on the disc.
    /// </summary>
    public ObservableCollection<DvdSubtitleChoiceRow> SubtitleRows { get; } = new();

    /// <summary>True when there is at least one movie to show subtitles for.</summary>
    public bool HasSubtitleRows => SubtitleRows.Count > 0;

    /// <summary>
    /// Subtitle files the user picked, by movie path. An empty string means "no subtitle file for this movie" (the
    /// one found beside it was cleared).
    /// </summary>
    private readonly Dictionary<string, string> _chosenSubtitles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom menu title names entered by the user, keyed by full movie path.</summary>
    private readonly Dictionary<string, string> _customTitleNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Physical path to the VIDEO_TS directory, root folder containing VIDEO_TS, or delimited movie file list.
    /// </summary>
    public string VideoTsFolderPath
    {
        get => _videoTsFolderPath;
        set
        {
            if (SetProperty(ref _videoTsFolderPath, value))
            {
                OnPropertyChanged(nameof(ValidationStatusText));
                OnPropertyChanged(nameof(AreAuthoringOptionsEnabled));
                SourceMediaFiles.Clear();
                LastAuthoredVideoTsPath = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ValidateAndPlanLayout();
                }
                else
                {
                    ResetState();
                }
            }
        }
    }

    /// <summary>
    /// Optical volume label (max 32 uppercase characters).
    /// </summary>
    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value);
    }

    /// <summary>
    /// Target television broadcast standard (NTSC or PAL).
    /// </summary>
    public DvdVideoStandard SelectedStandard
    {
        get => _selectedStandard;
        set
        {
            if (SetProperty(ref _selectedStandard, value))
            {
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>
    /// Available TV broadcast standards.
    /// </summary>
    public IReadOnlyList<DvdVideoStandard> AvailableStandards { get; } = new[] { DvdVideoStandard.Ntsc, DvdVideoStandard.Pal };

    /// <summary>
    /// Target display aspect ratio (16:9 Widescreen or 4:3 Standard).
    /// </summary>
    public DvdAspectRatio SelectedAspectRatio
    {
        get => _selectedAspectRatio;
        set
        {
            if (SetProperty(ref _selectedAspectRatio, value))
            {
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>
    /// Available display aspect ratios.
    /// </summary>
    public IReadOnlyList<DvdAspectRatio> AvailableAspectRatios { get; } = new[] { DvdAspectRatio.Widescreen16x9, DvdAspectRatio.Standard4x3 };

    /// <summary>
    /// Whether to create a DVD title menu.
    /// </summary>
    public bool AutoCreatePlayMenu
    {
        get => _autoCreatePlayMenu;
        set
        {
            if (SetProperty(ref _autoCreatePlayMenu, value))
            {
                if (!value && _afterTitleAction == DvdAfterTitleAction.ReturnToMenu)
                {
                    AfterTitleAction = DvdAfterTitleAction.PlayNextTitle;
                }
                if (!value)
                {
                    PreviewChapterSubmenu = false;
                }
                InvalidateAuthoredCache();
                RefreshMenuPreview();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>Whether to generate paginated per-title chapter selection submenus (with Back to Main Menu).</summary>
    public bool GenerateChapterMenus
    {
        get => _generateChapterMenus;
        set
        {
            if (SetProperty(ref _generateChapterMenus, value))
            {
                if (!value)
                {
                    _previewChapterSubmenu = false;
                    OnPropertyChanged(nameof(PreviewChapterSubmenu));
                }
                InvalidateAuthoredCache();
                RefreshMenuPreview();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>Available font presets for DVD menus.</summary>
    public IReadOnlyList<DvdMenuFontPreset> AvailableMenuFontPresets { get; } =
        [DvdMenuFontPreset.ModernSans, DvdMenuFontPreset.ClassicSerif, DvdMenuFontPreset.CinemaMono];

    /// <summary>Selected font preset for DVD menus.</summary>
    public DvdMenuFontPreset SelectedMenuFontPreset
    {
        get => _selectedMenuFontPreset;
        set
        {
            if (SetProperty(ref _selectedMenuFontPreset, value))
            {
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>Available highlight/accent color presets for DVD menus.</summary>
    public IReadOnlyList<DvdMenuHighlightPreset> AvailableMenuHighlightPresets { get; } =
        [DvdMenuHighlightPreset.Amber, DvdMenuHighlightPreset.Cyan, DvdMenuHighlightPreset.Crimson, DvdMenuHighlightPreset.Emerald, DvdMenuHighlightPreset.Gold];

    /// <summary>Selected highlight/accent color preset for DVD menus.</summary>
    public DvdMenuHighlightPreset SelectedMenuHighlightPreset
    {
        get => _selectedMenuHighlightPreset;
        set
        {
            if (SetProperty(ref _selectedMenuHighlightPreset, value))
            {
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>Optional custom background image path scaled and letterboxed onto DVD menus.</summary>
    public string? MenuBackgroundImagePath
    {
        get => _menuBackgroundImagePath;
        set
        {
            if (SetProperty(ref _menuBackgroundImagePath, value))
            {
                OnPropertyChanged(nameof(HasMenuBackgroundImage));
                OnPropertyChanged(nameof(MenuBackgroundStatusText));
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>True when a custom menu background image path is set.</summary>
    public bool HasMenuBackgroundImage => !string.IsNullOrWhiteSpace(_menuBackgroundImagePath);

    /// <summary>Short label describing the active menu background.</summary>
    public string MenuBackgroundStatusText =>
        string.IsNullOrWhiteSpace(_menuBackgroundImagePath) ? "Default dark gradient" : Path.GetFileName(_menuBackgroundImagePath);

    /// <summary>Optional background audio file muxed as AC-3 into DVD menus.</summary>
    public string? MenuAudioPath
    {
        get => _menuAudioPath;
        set
        {
            if (SetProperty(ref _menuAudioPath, value))
            {
                OnPropertyChanged(nameof(HasMenuAudio));
                OnPropertyChanged(nameof(MenuAudioStatusText));
                ClearMenuAudioCommand?.RaiseCanExecuteChanged();
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>True when a custom menu background audio path is set.</summary>
    public bool HasMenuAudio => !string.IsNullOrWhiteSpace(_menuAudioPath);

    /// <summary>Short label describing the active menu audio.</summary>
    public string MenuAudioStatusText =>
        string.IsNullOrWhiteSpace(_menuAudioPath) ? "Silent menu" : Path.GetFileName(_menuAudioPath);

    /// <summary>Live rendered still-menu preview image for display in <c>DvdVideoView</c>.</summary>
    public BitmapSource? MenuPreviewImage
    {
        get => _menuPreviewImage;
        private set => SetProperty(ref _menuPreviewImage, value);
    }

    /// <summary>0-based page index currently shown in the live still-menu preview.</summary>
    public int MenuPreviewPageIndex
    {
        get => _menuPreviewPageIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, _menuPreviewPageCount - 1));
            if (SetProperty(ref _menuPreviewPageIndex, clamped))
            {
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>Total number of pages in the live still-menu preview.</summary>
    public int MenuPreviewPageCount
    {
        get => _menuPreviewPageCount;
        private set
        {
            if (SetProperty(ref _menuPreviewPageCount, value))
            {
                OnPropertyChanged(nameof(MenuPreviewPageText));
                OnPropertyChanged(nameof(CanGoToPreviousMenuPreviewPage));
                OnPropertyChanged(nameof(CanGoToNextMenuPreviewPage));
            }
        }
    }

    /// <summary>Whether the live preview displays the Chapter Selection submenu instead of the main title menu.</summary>
    public bool PreviewChapterSubmenu
    {
        get => _previewChapterSubmenu;
        set
        {
            bool effective = value && GenerateChapterMenus && AutoCreatePlayMenu;
            if (SetProperty(ref _previewChapterSubmenu, effective))
            {
                _menuPreviewPageIndex = 0;
                OnPropertyChanged(nameof(MenuPreviewPageIndex));
                RefreshMenuPreview();
            }
        }
    }

    public string MenuPreviewPageText => PreviewChapterSubmenu
        ? $"Chapters · Page {_menuPreviewPageIndex + 1} of {_menuPreviewPageCount}"
        : $"Page {_menuPreviewPageIndex + 1} of {_menuPreviewPageCount}";

    public bool CanGoToPreviousMenuPreviewPage => AutoCreatePlayMenu && _menuPreviewPageIndex > 0;
    public bool CanGoToNextMenuPreviewPage => AutoCreatePlayMenu && _menuPreviewPageIndex < _menuPreviewPageCount - 1;

    /// <summary>True when authoring options apply (movies staged or no folder loaded yet).</summary>
    public bool AreAuthoringOptionsEnabled =>
        RequiresAutoAuthoring || (SourceMediaFiles.Count == 0 && string.IsNullOrWhiteSpace(VideoTsFolderPath));

    /// <summary>Seconds before the title menu starts Title 1 automatically; 0 (default) waits indefinitely.</summary>
    public int MenuAutoPlaySeconds
    {
        get => _menuAutoPlaySeconds;
        set
        {
            int clamped = Math.Clamp(value, 0, 254);
            if (SetProperty(ref _menuAutoPlaySeconds, clamped))
            {
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>Choices for <see cref="MenuAutoPlaySeconds"/>.</summary>
    public IReadOnlyList<DvdIntChoice> MenuAutoPlayChoices { get; } =
    [
        new(0, "Never"),
        new(5, "After 5 s"),
        new(10, "After 10 s"),
        new(15, "After 15 s"),
        new(30, "After 30 s"),
        new(60, "After 60 s")
    ];

    /// <summary>What happens when a title finishes playing.</summary>
    public DvdAfterTitleAction AfterTitleAction
    {
        get => _afterTitleAction;
        set
        {
            if (SetProperty(ref _afterTitleAction, value))
            {
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>Available post-title actions.</summary>
    public IReadOnlyList<DvdAfterTitleAction> AvailableAfterTitleActions { get; } =
        [DvdAfterTitleAction.PlayNextTitle, DvdAfterTitleAction.ReturnToMenu];

    /// <summary>Chapter mark interval in seconds (0 = one chapter per title).</summary>
    public int ChapterIntervalSeconds
    {
        get => _chapterIntervalSeconds;
        set
        {
            int clamped = Math.Clamp(value, 0, 3600);
            if (SetProperty(ref _chapterIntervalSeconds, clamped))
            {
                OnPropertyChanged(nameof(ChapterIntervalMinutes));
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>Chapter mark interval in whole minutes (0 = no chapters).</summary>
    public int ChapterIntervalMinutes
    {
        get => _chapterIntervalSeconds / 60;
        set => ChapterIntervalSeconds = Math.Clamp(value, 0, 60) * 60;
    }

    /// <summary>Choices for <see cref="ChapterIntervalMinutes"/>.</summary>
    public IReadOnlyList<DvdIntChoice> ChapterIntervalChoices { get; } =
    [
        new(0, "No chapters"),
        new(2, "Every 2 min"),
        new(5, "Every 5 min"),
        new(10, "Every 10 min"),
        new(15, "Every 15 min"),
        new(30, "Every 30 min")
    ];

    /// <summary>Whether First-Play turns subpicture stream 0 on by default.</summary>
    public bool SubtitlesOnByDefault
    {
        get => _subtitlesOnByDefault;
        set
        {
            if (SetProperty(ref _subtitlesOnByDefault, value))
            {
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>Rendered subtitle text size.</summary>
    public DvdSubtitleSize SubtitleSize
    {
        get => _subtitleSize;
        set
        {
            if (SetProperty(ref _subtitleSize, value))
            {
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>Available subtitle text sizes.</summary>
    public IReadOnlyList<DvdSubtitleSize> AvailableSubtitleSizes { get; } =
        [DvdSubtitleSize.Small, DvdSubtitleSize.Medium, DvdSubtitleSize.Large];

    /// <summary>Video quality preset choices.</summary>
    public IReadOnlyList<DvdQualityPreset> AvailableQualityPresets { get; } =
    [
        new("fit", "Fit to disc (auto)", true, 6000, 8000),
        new("high", "High (8 Mbps)", false, 8000, 9000),
        new("standard", "Standard (6 Mbps)", false, 6000, 8000),
        new("longplay", "Long play (4 Mbps)", false, 4000, 5500),
        new("extended", "Extended (2.5 Mbps)", false, 2500, 3500)
    ];

    /// <summary>Selected video quality preset.</summary>
    public DvdQualityPreset SelectedQualityPreset
    {
        get => _selectedQualityPreset;
        set
        {
            if (value == null) return;
            if (SetProperty(ref _selectedQualityPreset, value))
            {
                _fitToDisc = value.FitToDisc;
                _videoBitrateKbps = value.VideoKbps;
                _maxVideoBitrateKbps = value.MaxVideoKbps;
                OnPropertyChanged(nameof(FitToDisc));
                OnPropertyChanged(nameof(VideoBitrateKbps));
                OnPropertyChanged(nameof(MaxVideoBitrateKbps));
                InvalidateAuthoredCache();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>When true, lowers bitrate to fit the target disc; when false, keeps <see cref="VideoBitrateKbps"/>.</summary>
    public bool FitToDisc
    {
        get => _fitToDisc;
        set
        {
            if (SetProperty(ref _fitToDisc, value))
            {
                SyncSelectedQualityPreset();
                InvalidateAuthoredCache();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>Target video bitrate in kbps.</summary>
    public int VideoBitrateKbps
    {
        get => _videoBitrateKbps;
        set
        {
            int clamped = Math.Clamp(value, DvdVideoAuthoringService.MinimumVideoKbps, 9000);
            if (SetProperty(ref _videoBitrateKbps, clamped))
            {
                if (_maxVideoBitrateKbps < clamped)
                {
                    _maxVideoBitrateKbps = Math.Clamp((int)(clamped * 1.25), clamped, 9000);
                    OnPropertyChanged(nameof(MaxVideoBitrateKbps));
                }
                SyncSelectedQualityPreset();
                InvalidateAuthoredCache();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>Maximum video bitrate in kbps.</summary>
    public int MaxVideoBitrateKbps
    {
        get => _maxVideoBitrateKbps;
        set
        {
            int clamped = Math.Clamp(value, _videoBitrateKbps, 9600);
            if (SetProperty(ref _maxVideoBitrateKbps, clamped))
            {
                InvalidateAuthoredCache();
            }
        }
    }

    /// <summary>Whether to author for a dual-layer DVD-9 disc.</summary>
    public bool DualLayer
    {
        get => _dualLayer;
        set
        {
            if (SetProperty(ref _dualLayer, value))
            {
                if (_useDualLayerDisc != value)
                {
                    _useDualLayerDisc = value;
                    OnPropertyChanged(nameof(UseDualLayerDisc));
                    OnPropertyChanged(nameof(UseSingleLayerDisc));
                }
                InvalidateAuthoredCache();
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    private void SyncSelectedQualityPreset()
    {
        var match = AvailableQualityPresets.FirstOrDefault(p =>
            p.FitToDisc == _fitToDisc && (_fitToDisc || p.VideoKbps == _videoBitrateKbps));
        if (match != null && !Equals(_selectedQualityPreset, match))
        {
            _selectedQualityPreset = match;
            OnPropertyChanged(nameof(SelectedQualityPreset));
        }
    }

    /// <summary>Sets a custom menu title name for <paramref name="moviePath"/> (empty clears back to the cleaned filename).</summary>
    public void SetTitleName(string moviePath, string? titleName)
    {
        string full = Path.GetFullPath(moviePath);
        string trimmed = (titleName ?? string.Empty).Trim();
        if (trimmed.Length > 80)
        {
            trimmed = trimmed[..80];
        }
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _customTitleNames.Remove(full);
        }
        else
        {
            _customTitleNames[full] = trimmed;
        }
        InvalidateAuthoredCache();
        RefreshMenuPreview();
    }

    /// <summary>Returns the menu title name for <paramref name="moviePath"/> (custom name or cleaned filename).</summary>
    public string GetTitleName(string moviePath)
    {
        string full = Path.GetFullPath(moviePath);
        return _customTitleNames.TryGetValue(full, out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : DvdInputNormalizer.CleanMovieTitle(moviePath);
    }

    /// <summary>
    /// Make subtitles by speech recognition for movies with no .srt next to them (optional whisper.cpp download).
    /// </summary>
    public bool GenerateSubtitles
    {
        get => _generateSubtitles;
        set
        {
            if (SetProperty(ref _generateSubtitles, value))
            {
                OnPropertyChanged(nameof(IsSubtitleToolOfferVisible));
                RefreshSubtitleRows();
            }
        }
    }

    /// <summary>
    /// Spoken language for generated subtitles: a two- or three-letter code such as "en", or "auto".
    /// </summary>
    public string SubtitleLanguage
    {
        get => _subtitleLanguage;
        set => SetProperty(ref _subtitleLanguage, value);
    }

    /// <summary>
    /// Folder the subtitle tool is installed in and looked for; null means the default
    /// (<c>%LOCALAPPDATA%\Programs\whisper.cpp</c>). Tests point it at a temporary folder.
    /// </summary>
    public string? SubtitleToolInstallRoot { get; set; }

    /// <summary>True when the subtitle tool (whisper.cpp and a model) is installed.</summary>
    public bool IsSubtitleToolInstalled => _subtitleTool != null;

    /// <summary>Show the 1-click subtitle tool offer: generation is switched on and the tool is missing.</summary>
    public bool IsSubtitleToolOfferVisible => _generateSubtitles && _subtitleTool == null;

    /// <summary>Which subtitle tool is installed, or that it is missing.</summary>
    public string SubtitleToolStatusText
    {
        get => _subtitleToolStatusText;
        private set => SetProperty(ref _subtitleToolStatusText, value);
    }

    /// <summary>What the 1-click install would download for this PC and why (button tooltip).</summary>
    public string SubtitleToolOfferText
    {
        get => _subtitleToolOfferText;
        private set => SetProperty(ref _subtitleToolOfferText, value);
    }

    /// <summary>
    /// Title displayed on the interactive DVD Root Menu.
    /// </summary>
    public string MenuTitle
    {
        get => _menuTitle;
        set
        {
            if (SetProperty(ref _menuTitle, value))
            {
                InvalidateAuthoredCache();
                RefreshMenuPreview();
            }
        }
    }

    /// <summary>
    /// Indicates whether the input media requires automated MPEG-2 transcoding or structure normalization.
    /// </summary>
    public bool RequiresAutoAuthoring
    {
        get => _requiresAutoAuthoring;
        private set
        {
            if (SetProperty(ref _requiresAutoAuthoring, value))
            {
                OnPropertyChanged(nameof(AreAuthoringOptionsEnabled));
                RefreshFitState();
            }
        }
    }

    /// <summary>
    /// Path to the most recently authored compliant VIDEO_TS folder in temporary storage.
    /// </summary>
    public string? LastAuthoredVideoTsPath
    {
        get => _lastAuthoredVideoTsPath;
        private set => SetProperty(ref _lastAuthoredVideoTsPath, value);
    }

    /// <summary>
    /// Whether to use FFmpeg when available on the host system to transcode arbitrary video streams.
    /// </summary>
    public bool UseFfmpegWhenAvailable { get; set; } = true;

    /// <summary>
    /// Optional custom path to the FFmpeg executable configured in application settings.
    /// </summary>
    public string? CustomFfmpegPath
    {
        get => _customFfmpegPath;
        set
        {
            if (SetProperty(ref _customFfmpegPath, value))
            {
                RefreshFfmpegStatus();
            }
        }
    }

    /// <summary>
    /// Whether an FFmpeg executable was found on the host system.
    /// </summary>
    public bool IsFfmpegAvailable
    {
        get => _isFfmpegAvailable;
        private set
        {
            if (SetProperty(ref _isFfmpegAvailable, value))
            {
                OnPropertyChanged(nameof(IsFfmpegMissing));
            }
        }
    }

    /// <summary>
    /// True when <c>ffmpeg.exe</c> is not installed on the system.
    /// </summary>
    public bool IsFfmpegMissing => !_isFfmpegAvailable;

    /// <summary>
    /// Informational status line describing FFmpeg availability.
    /// </summary>
    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => SetProperty(ref _ffmpegStatusText, value);
    }

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set => SetProperty(ref _selectedWriteSpeed, value);
    }

    /// <summary>
    /// Fill the empty part of a single-layer DVD with a picture (<see cref="DiscFillPicture"/>): the DVD-Video stays on
    /// the inner part, the whole disc is written (longer burn) and it is closed. Off by default.
    /// </summary>
    public bool FillWithPicture
    {
        get => _fillWithPicture;
        set => SetProperty(ref _fillWithPicture, value);
    }

    private bool _fillWithPicture;

    /// <summary>
    /// The picture is a frame of the main movie, taken with FFmpeg (skipped, with a message, when FFmpeg is not
    /// installed); otherwise <see cref="FillPicturePath"/>.
    /// </summary>
    public bool FillPictureFromMovie
    {
        get => _fillPictureFromMovie;
        set => SetProperty(ref _fillPictureFromMovie, value);
    }

    private bool _fillPictureFromMovie = true;

    /// <summary>The PNG drawn on the empty part of the disc when <see cref="FillPictureFromMovie"/> is off.</summary>
    public string? FillPicturePath
    {
        get => _fillPicturePath;
        set => SetProperty(ref _fillPicturePath, value);
    }

    private string? _fillPicturePath;

    /// <summary>
    /// Disc geometry for the picture fill; null uses the built-in single-layer preset for the disc in the drive
    /// (dvd-5-120mm or bd-r-sl-25gb), cut to the disc's size.
    /// </summary>
    public CdBurner.Lithography.Calibration.CalibrationParameters? FillDiscGeometry { get; set; }

    /// <summary>What the picture fill does to the burn (longer, closed disc), shown under the option.</summary>
    public string FillPictureNote => DiscFillPicture.ScreenNote;

    /// <summary>
    /// Whether to perform post-burn sector read verification.
    /// </summary>
    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    /// <summary>
    /// Whether to eject the optical drive tray after burning completes.
    /// </summary>
    public bool EjectAfterBurn
    {
        get => _ejectAfterBurn;
        set => SetProperty(ref _ejectAfterBurn, value);
    }

    /// <summary>
    /// Indicates whether an active operation can be cancelled.
    /// </summary>
    public bool CanCancel => _isBusy;

    /// <summary>
    /// Indicates whether the specified VIDEO_TS structure passes all DVD-Video standards.
    /// </summary>
    public bool IsStructureValid
    {
        get => _isStructureValid;
        private set
        {
            if (SetProperty(ref _isStructureValid, value))
            {
                OnPropertyChanged(nameof(ValidationStatusText));
                BuildDvdIsoCommand.RaiseCanExecuteChanged();
                StartDvdBurnCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Collection of structure validation error, warning, and informational messages.
    /// </summary>
    public ObservableCollection<string> ValidationMessages { get; } = new();

    /// <summary>
    /// Planned physical extent placement rows showing each IFO, VOB, BUP file and 32 KB ECC padding gaps.
    /// </summary>
    public ObservableCollection<DvdExtentAlignmentRow> ExtentLayoutRows { get; } = new();

    /// <summary>
    /// Indicates whether the compilation sector count exceeds single-layer DVD-5 (2,295,104 sectors)
    /// and strictly requires DVD-9 Dual-Layer media.
    /// </summary>
    public bool IsDualLayerRequired
    {
        get => _isDualLayerRequired;
        private set
        {
            if (SetProperty(ref _isDualLayerRequired, value))
            {
                OnPropertyChanged(nameof(MediaRequirementText));
                ApplyDirectDefaultForLayers();
            }
        }
    }

    /// <summary>
    /// Layer break choices for a dual-layer disc. When the VIDEO_TS IFO files can be read these are cell starts (where
    /// players expect the layer change) within the allowed range, as addresses on the planned layout; otherwise the
    /// even split. The address on the disc itself is worked out when the image is built.
    /// </summary>
    public ObservableCollection<int> CandidateLayerBreakLbas { get; } = new();

    /// <summary>The cell behind each planned break address, so the burn can find the same cell in the built image.</summary>
    private readonly Dictionary<int, DvdCellStart> _plannedBreakCells = new();

    /// <summary>
    /// Burn with IgnisDisc's own drive commands instead of the Windows writer. Off by default. On a dual-layer DVD+R
    /// it places the layer break on the chosen cell boundary; the Windows writer cannot set a layer break.
    /// </summary>
    public bool UseDirectDriveCommands
    {
        get => _useDirectDriveCommands;
        set
        {
            _directChosenByUser = true; // from here on the user's choice stands, whatever the disc needs
            _directTurnedOnForDualLayer = false;
            SetProperty(ref _useDirectDriveCommands, value);
        }
    }

    private bool _useDirectDriveCommands;
    private bool _directChosenByUser;
    private bool _directTurnedOnForDualLayer;
    private bool _discPrefersDirect;
    private bool _setDvdRomBookTypeWhenSupported;

    /// <summary>
    /// Opt-in: before burning DVD+R or DVD+R DL media, attempt to set its Book Type to DVD-ROM when supported.
    /// Unsupported drives report cleanly without failing the burn.
    /// </summary>
    public bool SetDvdRomBookTypeWhenSupported
    {
        get => _setDvdRomBookTypeWhenSupported;
        set => SetProperty(ref _setDvdRomBookTypeWhenSupported, value);
    }

    /// <summary>
    /// Default for direct drive commands until the user sets it: on for a dual-layer disc (only the direct writer puts
    /// the layer break on a cell boundary, where players expect it) and while a DVD-RW is in the drive
    /// (<see cref="ApplyDirectDefaultForDisc"/>); back off otherwise (the Windows writer is the better-proven choice
    /// for the rest).
    /// </summary>
    private void ApplyDirectDefaultForLayers()
    {
        if (_directChosenByUser)
        {
            return;
        }
        bool wanted = IsDualLayerRequired || _discPrefersDirect;
        if (wanted && !_useDirectDriveCommands)
        {
            _directTurnedOnForDualLayer = true; // turned on automatically, so it may also turn itself off
            SetProperty(ref _useDirectDriveCommands, true, nameof(UseDirectDriveCommands));
        }
        else if (!wanted && _directTurnedOnForDualLayer)
        {
            _directTurnedOnForDualLayer = false;
            SetProperty(ref _useDirectDriveCommands, false, nameof(UseDirectDriveCommands));
        }
    }

    /// <summary>
    /// Direct drive commands by themselves while a DVD-RW is in the drive and <paramref name="enabled"/> (the setting)
    /// allows it (<see cref="DirectWritingDefault.Prefers"/>), until the user sets the box.
    /// </summary>
    public void ApplyDirectDefaultForDisc(MediaStatusDto? status, bool enabled)
    {
        _discPrefersDirect = enabled && DirectWritingDefault.Prefers(status);
        ApplyDirectDefaultForLayers();
    }

    /// <summary>Folder for the command logs of direct burns; null for the default under %LOCALAPPDATA%.</summary>
    public string? DirectBurnLogDirectory { get; set; }

    /// <summary>The direct writer used for the last burn made with <see cref="UseDirectDriveCommands"/>, and its log.</summary>
    public CdBurner.Hardware.NativeWriting.NativeDataBurnEngine? LastDirectBurnEngine { get; private set; }

    /// <summary>The cell the last dual-layer direct burn put the layer break on, with its address on the disc.</summary>
    public DvdCellPosition? LastLayerBreak { get; private set; }

    /// <summary>
    /// Currently chosen Layer Break transition LBA for Dual-Layer DVD-9 (OTP) recording.
    /// </summary>
    public int SelectedLayerBreakLba
    {
        get => _selectedLayerBreakLba;
        set => SetProperty(ref _selectedLayerBreakLba, value);
    }

    /// <summary>
    /// Currently selected optical recorder backend.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SetProperty(ref _selectedRecorder, value);
    }

    /// <summary>
    /// Discovered optical recorder drives.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Total byte size of all DVD-Video files combined.
    /// </summary>
    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        private set
        {
            if (SetProperty(ref _totalSizeBytes, value))
            {
                OnPropertyChanged(nameof(FormattedTotalSize));
            }
        }
    }

    /// <summary>
    /// Total 2048-byte optical data sectors required including 32 KB ECC padding blocks.
    /// </summary>
    public int TotalSectors
    {
        get => _totalSectors;
        private set
        {
            if (SetProperty(ref _totalSectors, value))
            {
                RefreshFitState();
            }
        }
    }

    /// <summary>
    /// Number of title sets discovered (VTS_01, VTS_02, etc.).
    /// </summary>
    public int TitleSetCount
    {
        get => _titleSetCount;
        private set => SetProperty(ref _titleSetCount, value);
    }

    /// <summary>
    /// Indicates whether an AUDIO_TS directory is present.
    /// </summary>
    public bool HasAudioTs
    {
        get => _hasAudioTs;
        private set => SetProperty(ref _hasAudioTs, value);
    }

    /// <summary>
    /// Formatted total compilation size string.
    /// </summary>
    public string FormattedTotalSize => CompilationItemViewModel.FormatBytes(TotalSizeBytes);

    /// <summary>
    /// Display status of DVD-Video structure check.
    /// </summary>
    public string ValidationStatusText =>
        SourceMediaFiles.Count == 0 && string.IsNullOrWhiteSpace(VideoTsFolderPath)
            ? "No movies added yet"
            : !IsStructureValid ? "Structure Invalid"
            : IsTooLongForDvd ? "Too long for one DVD"
            : (RequiresAutoAuthoring ? "Ready (Auto-Convert & Menu)" : "DVD-Video Compliant");

    /// <summary>
    /// Reads a movie's running time in seconds (0 when it cannot) for <see cref="FitEstimate"/>. Null reads it with
    /// FFmpeg when <see cref="UseFfmpegWhenAvailable"/> is on. Tests set a fake.
    /// </summary>
    public Func<string, CancellationToken, Task<double>>? DurationProbe { get; set; }

    /// <summary>Finishes once the running times being read are in and the plan has been redone with them.</summary>
    public Task FitEstimateTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// How the movies fit onto a DVD once converted (the video bitrate and size the authoring will use); null for a
    /// VIDEO_TS folder, before the running times are read, or when they cannot be.
    /// </summary>
    public DvdFitEstimate? FitEstimate
    {
        get => _fitEstimate;
        private set
        {
            if (SetProperty(ref _fitEstimate, value))
            {
                RefreshFitState();
            }
        }
    }

    /// <summary>One line on how the movies fit: full quality, compressed to fit, needs a dual-layer DVD, or too long.</summary>
    public string FitSummaryText
    {
        get => _fitSummaryText;
        private set
        {
            if (SetProperty(ref _fitSummaryText, value))
            {
                OnPropertyChanged(nameof(HasFitSummary));
            }
        }
    }

    public bool HasFitSummary => !string.IsNullOrEmpty(FitSummaryText);

    /// <summary>
    /// No DVD can hold this: movies longer than a dual-layer DVD holds at the lowest bitrate, or a VIDEO_TS folder
    /// bigger than a dual-layer DVD. Burn and Build ISO are off.
    /// </summary>
    public bool IsTooLongForDvd =>
        FitEstimate?.Kind == DvdFitKind.TooLong ||
        (!RequiresAutoAuthoring && TotalSectors > DvdLayerBreakCalculator.MaxDvd9Sectors);

    /// <summary>Colour of <see cref="FitSummaryText"/>: warning for a dual-layer DVD, error when no DVD holds it.</summary>
    public CapacityStatus FitStatus =>
        IsTooLongForDvd ? CapacityStatus.ExceedsMediaCapacity
        : FitEstimate?.Kind == DvdFitKind.NeedsDualLayer ? CapacityStatus.NearCapacityWarning
        : CapacityStatus.FitsComfortably;

    /// <summary>
    /// The user's pick when both discs are offered (<see cref="HasLayerChoice"/>): a dual-layer DVD at a higher
    /// bitrate instead of compressing more for a 4.7 GB DVD. Redoes the plan.
    /// </summary>
    public bool UseDualLayerDisc
    {
        get => _useDualLayerDisc;
        set
        {
            if (SetProperty(ref _useDualLayerDisc, value))
            {
                if (_dualLayer != value)
                {
                    _dualLayer = value;
                    OnPropertyChanged(nameof(DualLayer));
                }
                InvalidateAuthoredCache();
                OnPropertyChanged(nameof(UseSingleLayerDisc));
                if (RequiresAutoAuthoring && !IsBusy)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>The other radio button of <see cref="UseDualLayerDisc"/>.</summary>
    public bool UseSingleLayerDisc
    {
        get => !UseDualLayerDisc;
        set => UseDualLayerDisc = !value;
    }

    /// <summary>Both a 4.7 GB DVD (compressed) and a dual-layer DVD (sharper) can take these movies: show the choice.</summary>
    public bool HasLayerChoice => FitEstimate?.HasLayerChoice == true;

    /// <summary>Label of the 4.7 GB DVD choice: its bitrate and how it will look.</summary>
    public string SingleLayerChoiceText => FitEstimate?.SingleLayerKbps is int k
        ? $"4.7 GB DVD: compressed to {k:N0} kbps, {DvdDiscFit.Quality(k, BuildFitOptions())}"
        : string.Empty;

    /// <summary>Label of the dual-layer DVD choice: its bitrate and how it will look.</summary>
    public string DualLayerChoiceText => FitEstimate?.DualLayerKbps is int k
        ? $"Dual-layer DVD (DVD+R DL, 8.5 GB): {k:N0} kbps, {DvdDiscFit.Quality(k, BuildFitOptions())}"
        : string.Empty;

    private void RefreshFitState()
    {
        OnPropertyChanged(nameof(HasLayerChoice));
        OnPropertyChanged(nameof(SingleLayerChoiceText));
        OnPropertyChanged(nameof(DualLayerChoiceText));
        OnPropertyChanged(nameof(IsTooLongForDvd));
        OnPropertyChanged(nameof(FitStatus));
        OnPropertyChanged(nameof(ValidationStatusText));
        StartDvdBurnCommand?.RaiseCanExecuteChanged();
        BuildDvdIsoCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Display text describing media type requirement.
    /// </summary>
    public string MediaRequirementText => IsDualLayerRequired ? "Dual-Layer DVD-9 (OTP)" : "Single-Layer DVD-5 (~4.7 GB)";

    /// <summary>
    /// Indicates whether an authoring, ISO build, or burn operation is actively executing.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                CancelCommand?.RaiseCanExecuteChanged();
                NewDvdCommand?.RaiseCanExecuteChanged();
                BuildDvdIsoCommand?.RaiseCanExecuteChanged();
                StartDvdBurnCommand?.RaiseCanExecuteChanged();
                InstallFfmpegCommand?.RaiseCanExecuteChanged();
                InstallSubtitleToolCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// User-facing status line.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Active burning or image building progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Absolute path of the most recently built DVD-Video ISO image file.
    /// </summary>
    public string? LastBuiltIsoPath
    {
        get => _lastBuiltIsoPath;
        private set => SetProperty(ref _lastBuiltIsoPath, value);
    }

    #endregion

    #region Commands

    public RelayCommand AddMovieFilesCommand { get; }
    public RelayCommand BrowseFillPictureCommand { get; }
    public RelayCommand BrowseMenuBackgroundCommand { get; }
    public RelayCommand ClearMenuBackgroundCommand { get; }
    public RelayCommand BrowseMenuAudioCommand { get; }
    public RelayCommand ClearMenuAudioCommand { get; }
    public RelayCommand PreviousMenuPreviewPageCommand { get; }
    public RelayCommand NextMenuPreviewPageCommand { get; }

    /// <summary>Opens a file dialog to pick the .srt for one movie (parameter: its <see cref="DvdSubtitleChoiceRow"/>).</summary>
    public RelayCommand ChooseSubtitleFileCommand { get; }

    /// <summary>Uses no subtitle file for one movie (parameter: its <see cref="DvdSubtitleChoiceRow"/>).</summary>
    public RelayCommand ClearSubtitleFileCommand { get; }
    public RelayCommand ClearSourceCommand { get; }

    /// <summary>Starts a new DVD: forgets the movies, subtitle picks, disc name and menu title (see <see cref="StartNewDvd"/>).</summary>
    public RelayCommand NewDvdCommand { get; }
    public RelayCommand SelectVideoTsFolderCommand { get; }
    public RelayCommand ValidateAndPlanLayoutCommand { get; }
    public AsyncRelayCommand BuildDvdIsoCommand { get; }
    public AsyncRelayCommand StartDvdBurnCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }
    public AsyncRelayCommand InstallSubtitleToolCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Live burn telemetry recorder and SVG/JSON plot exporter.</summary>
    public BurnTelemetryMonitor Telemetry { get; } = new();

    #endregion

    public DvdVideoViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null)
    {
        _selectedQualityPreset = AvailableQualityPresets[0];
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();

        AddMovieFilesCommand = new RelayCommand(ExecuteAddMovieFiles);
        BrowseFillPictureCommand = new RelayCommand(ExecuteBrowseFillPicture);
        BrowseMenuBackgroundCommand = new RelayCommand(ExecuteBrowseMenuBackground);
        ClearMenuBackgroundCommand = new RelayCommand(_ => MenuBackgroundImagePath = null, _ => HasMenuBackgroundImage);
        BrowseMenuAudioCommand = new RelayCommand(ExecuteBrowseMenuAudio);
        ClearMenuAudioCommand = new RelayCommand(_ => MenuAudioPath = null, _ => HasMenuAudio);
        PreviousMenuPreviewPageCommand = new RelayCommand(_ => MenuPreviewPageIndex--, _ => CanGoToPreviousMenuPreviewPage);
        NextMenuPreviewPageCommand = new RelayCommand(_ => MenuPreviewPageIndex++, _ => CanGoToNextMenuPreviewPage);
        ChooseSubtitleFileCommand = new RelayCommand(p => { if (p is DvdSubtitleChoiceRow row) PickSubtitleFile(row); }, _ => !IsBusy);
        ClearSubtitleFileCommand = new RelayCommand(p => { if (p is DvdSubtitleChoiceRow row) ChooseSubtitleFile(row.MoviePath, null); }, _ => !IsBusy);
        SourceMediaFiles.CollectionChanged += (_, _) => RefreshSubtitleRows();
        ClearSourceCommand = new RelayCommand(_ => ClearSource());
        NewDvdCommand = new RelayCommand(_ => StartNewDvd(), _ => !IsBusy);
        SelectVideoTsFolderCommand = new RelayCommand(ExecuteSelectVideoTsFolder);
        ValidateAndPlanLayoutCommand = new RelayCommand(_ => ValidateAndPlanLayout());
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => CanCancel);
        BuildDvdIsoCommand = new AsyncRelayCommand(async param =>
        {
            string? path = param as string;
            if (string.IsNullOrWhiteSpace(path) && System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Build DVD-Video ISO Image",
                    Filter = "DVD-Video ISO Image (*.iso)|*.iso|All Files (*.*)|*.*",
                    FileName = $"{(string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel)}.iso"
                };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    return;
                }
                path = dlg.FileName;
            }
            await BuildDvdIsoAsync(path);
        }, _ => IsStructureValid && !IsTooLongForDvd);
        StartDvdBurnCommand = new AsyncRelayCommand(async param => await StartDvdBurnAsync(param), _ => IsStructureValid && !IsTooLongForDvd);
        InstallFfmpegCommand = new AsyncRelayCommand(async _ => await InstallFfmpegAsync(), _ => !IsBusy);
        InstallSubtitleToolCommand = new AsyncRelayCommand(async _ => await InstallSubtitleToolAsync(), _ => !IsBusy);

        RefreshFfmpegStatus();
        RefreshSubtitleToolStatus();

        RefreshDrives();
        RefreshMenuPreview();
    }

    #region Operations

    /// <summary>
    /// Re-evaluates whether <c>ffmpeg.exe</c> is available on the host machine and updates UI status.
    /// </summary>
    public void RefreshFfmpegStatus()
    {
        string? exe = FfmpegLocator.FindFfmpegExecutable(
            string.IsNullOrWhiteSpace(_customFfmpegPath) ? null : _customFfmpegPath);
        IsFfmpegAvailable = !string.IsNullOrEmpty(exe);
        FfmpegStatusText = IsFfmpegAvailable
            ? $"FFmpeg: {Path.GetFileName(exe)}"
            : "FFmpeg not installed";
    }

    /// <summary>
    /// Downloads and extracts the LGPL Windows build of FFmpeg into per-user storage and enables video transcoding.
    /// </summary>
    public async Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = Strings.StatusDvdVideoDownloadingFfmpegLgpl;

        try
        {
            var progressHandler = new Progress<string>(msg =>
            {
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallFfmpegAsync(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progressHandler,
                cancellationToken).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                CustomFfmpegPath = result.ExecutablePath;
                RefreshFfmpegStatus();
                BurnProgress = 100.0;
                StatusMessage = $"FFmpeg (LGPL) installed: {result.ExecutablePath}";
            }
            else
            {
                StatusMessage = $"FFmpeg install failed: {result.ErrorMessage}";
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-checks whether the subtitle tool is installed and what the 1-click install would download.
    /// </summary>
    public void RefreshSubtitleToolStatus()
    {
        _subtitleTool = WhisperLocator.Find(SubtitleToolInstallRoot);
        SubtitleToolStatusText = _subtitleTool != null
            ? $"Subtitles: {_subtitleTool.Describe()}"
            : "Subtitle tool not installed";
        try
        {
            WhisperInstallPlan plan = WhisperCatalog.ChoosePlanForThisPc();
            SubtitleToolOfferText = $"Downloads whisper.cpp {WhisperCatalog.WhisperVersion} and the {plan.Model.FileName} speech model " +
                $"({plan.DownloadBytes / 1_000_000.0:N0} MB, each checked against a pinned SHA-256) into %LOCALAPPDATA%\\Programs\\whisper.cpp. " +
                plan.Reason + " Subtitles are machine-generated and will mishear some words, especially names.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SubtitleToolOfferText = "Downloads whisper.cpp and a speech model for subtitles.";
        }

        OnPropertyChanged(nameof(IsSubtitleToolInstalled));
        OnPropertyChanged(nameof(IsSubtitleToolOfferVisible));
    }

    /// <summary>
    /// 1-click install of the subtitle tool (whisper.cpp and a speech model) chosen for this PC.
    /// </summary>
    public async Task<WhisperInstallResult> InstallSubtitleToolAsync(
        WhisperInstallPlan? plan = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = "Downloading the subtitle tool...";
        try
        {
            plan ??= WhisperCatalog.ChoosePlanForThisPc();
            var progressHandler = new Progress<string>(msg => StatusMessage = msg);
            WhisperInstallResult result = await WhisperToolInstaller.InstallAsync(
                plan, SubtitleToolInstallRoot, httpClient, progressHandler, cancellationToken).ConfigureAwait(true);

            RefreshSubtitleToolStatus();
            if (result.Success)
            {
                BurnProgress = 100.0;
                StatusMessage = $"Subtitle tool installed: {result.Installation!.Describe()}";
            }
            else
            {
                StatusMessage = $"Subtitle tool install failed: {result.ErrorMessage}";
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddMovieFiles(IEnumerable<string> filePaths)
    {
        if (filePaths == null) return;

        foreach (var path in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string full = Path.GetFullPath(path);
                if (!SourceMediaFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    SourceMediaFiles.Add(full);
                }
            }
        }

        _videoTsFolderPath = string.Join("; ", SourceMediaFiles);
        OnPropertyChanged(nameof(VideoTsFolderPath));
        OnPropertyChanged(nameof(ValidationStatusText));
        ValidateAndPlanLayout();
    }

    /// <summary>
    /// Uses <paramref name="subtitlePath"/> as the subtitles for <paramref name="moviePath"/>; null means no subtitle
    /// file for that movie (made by speech recognition instead when "Generate subtitles" is on).
    /// </summary>
    public void ChooseSubtitleFile(string moviePath, string? subtitlePath)
    {
        _chosenSubtitles[Path.GetFullPath(moviePath)] = subtitlePath == null ? string.Empty : Path.GetFullPath(subtitlePath);
        RefreshSubtitleRows();
    }

    private void PickSubtitleFile(DvdSubtitleChoiceRow row)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Subtitles for {row.MovieName}",
            Filter = "SubRip subtitles (*.srt)|*.srt|All Files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(row.SubtitlePath ?? row.MoviePath) ?? string.Empty
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
        {
            ChooseSubtitleFile(row.MoviePath, dlg.FileName);
        }
    }

    /// <summary>
    /// Rebuilds <see cref="SubtitleRows"/> from the movie list, the user's picks and the "Generate subtitles" switch,
    /// using the same lookup authoring uses (<see cref="DvdVideoAuthoringService.FindSubtitleFile"/>), and reads each
    /// .srt so the row can say how many subtitles it holds or that it cannot be read.
    /// </summary>
    public void RefreshSubtitleRows()
    {
        var lookup = new DvdAuthoringOptions();
        foreach (var (movie, srt) in _chosenSubtitles)
        {
            lookup.SubtitleFiles[movie] = srt;
        }

        var activeSet = new HashSet<string>(SourceMediaFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (string staleKey in _customTitleNames.Keys.Where(k => !activeSet.Contains(k)).ToList())
        {
            _customTitleNames.Remove(staleKey);
        }

        SubtitleRows.Clear();
        foreach (string movie in SourceMediaFiles)
        {
            var row = new DvdSubtitleChoiceRow(movie, GetTitleName(movie), SetTitleName);
            bool chosen = _chosenSubtitles.TryGetValue(movie, out string? pick);
            string? srt = DvdVideoAuthoringService.FindSubtitleFile(movie, lookup);
            string ifNone = GenerateSubtitles
                ? "they will be made by speech recognition (Generate subtitles is on)."
                : "this movie gets no subtitles. Choose an .srt file, or tick Generate subtitles.";

            if (srt != null)
            {
                row.SubtitlePath = srt;
                string? problem = null;
                int cues = 0;
                try
                {
                    cues = DvdSubtitleWriter.ReadSrt(srt).Count;
                    if (cues == 0) problem = "it holds no subtitles";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
                {
                    problem = ex.Message;
                }

                if (problem != null)
                {
                    row.Source = DvdSubtitleSource.Unreadable;
                    row.StatusText = $"{Path.GetFileName(srt)} cannot be used ({problem}); " + ifNone;
                }
                else
                {
                    row.Source = chosen ? DvdSubtitleSource.Chosen : DvdSubtitleSource.FoundBesideMovie;
                    row.StatusText = (chosen ? "Using the file you chose: " : "Found beside the movie: ")
                        + $"{Path.GetFileName(srt)} ({cues:N0} subtitle{(cues == 1 ? "" : "s")}).";
                }
            }
            else
            {
                row.Source = GenerateSubtitles ? DvdSubtitleSource.WillBeGenerated : DvdSubtitleSource.None;
                row.StatusText = chosen && string.IsNullOrEmpty(pick)
                    ? "Subtitle file left off: " + ifNone
                    : chosen
                        ? $"The chosen file {Path.GetFileName(pick)} is gone: " + ifNone
                        : "No .srt file with the movie's name beside it: " + ifNone;
            }
            SubtitleRows.Add(row);
        }
        OnPropertyChanged(nameof(HasSubtitleRows));
        RefreshMenuPreview();
    }

    /// <summary>
    /// Clears all source media files and resets state.
    /// </summary>
    public void ClearSource()
    {
        CleanupTempAuthoredDirectory();
        _chosenSubtitles.Clear();
        _customTitleNames.Clear();
        SourceMediaFiles.Clear();
        VideoTsFolderPath = string.Empty;
    }

    /// <summary>
    /// Gets the screen ready for a different DVD after a burn: forgets the movies or VIDEO_TS folder, the subtitle
    /// picks, the planned layout, the last ISO, and the disc name and menu title (which were filled in from the old
    /// movies, so the next movies get their own). Standard, aspect, menu, subtitle and writer settings are kept, as
    /// they are the user's preferences rather than part of one disc. Does nothing while a burn or build is running.
    /// </summary>
    public void StartNewDvd()
    {
        if (IsBusy)
        {
            return;
        }

        ClearSource();
        ResetState(); // ClearSource only resets when the path changed; a path already blank would leave the old plan
        _probedSeconds.Clear();
        _useDualLayerDisc = false; // a choice for the last movies; the next ones start on the cheaper disc
        _dualLayer = false;
        OnPropertyChanged(nameof(UseDualLayerDisc));
        OnPropertyChanged(nameof(UseSingleLayerDisc));
        OnPropertyChanged(nameof(DualLayer));
        VolumeLabel = "DVD_VIDEO";
        MenuTitle = "DVD Video";
        MenuBackgroundImagePath = null;
        MenuAudioPath = null;
        _menuPreviewPageIndex = 0;
        _previewChapterSubmenu = false;
        OnPropertyChanged(nameof(MenuPreviewPageIndex));
        OnPropertyChanged(nameof(PreviewChapterSubmenu));
        LastBuiltIsoPath = null;
        BurnProgress = 0;
        StatusMessage = "New DVD: add movies or choose a VIDEO_TS folder.";
        RefreshMenuPreview();
    }

    private void InvalidateAuthoredCache()
    {
        CleanupTempAuthoredDirectory();
    }

    private void CleanupTempAuthoredDirectory()
    {
        if (!string.IsNullOrEmpty(_tempAuthoredDirectory))
        {
            try
            {
                if (Directory.Exists(_tempAuthoredDirectory))
                {
                    Directory.Delete(_tempAuthoredDirectory, recursive: true);
                }
            }
            catch
            {
            }
            _tempAuthoredDirectory = null;
        }
        LastAuthoredVideoTsPath = null;
    }

    /// <summary>
    /// Validates the DVD-Video structure (or movie source files) and computes the
    /// 32 KB ECC aligned physical extent layout.
    /// </summary>
    public void ValidateAndPlanLayout()
    {
        ValidationMessages.Clear();
        ExtentLayoutRows.Clear();
        CandidateLayerBreakLbas.Clear();
        _plannedBreakCells.Clear();
        SelectedLayerBreakLba = 0;
        IsDualLayerRequired = false;
        TotalSizeBytes = 0;
        TotalSectors = 0;
        TitleSetCount = 0;
        HasAudioTs = false;
        LastAuthoredVideoTsPath = null;
        RequiresAutoAuthoring = false;
        FitEstimate = null;
        FitSummaryText = string.Empty;

        List<string> inputs;
        if (SourceMediaFiles.Count > 0)
        {
            inputs = SourceMediaFiles.ToList();
        }
        else if (!string.IsNullOrWhiteSpace(VideoTsFolderPath))
        {
            inputs = VideoTsFolderPath
                .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        else
        {
            IsStructureValid = false;
            ValidationMessages.Add("Please specify a VIDEO_TS folder or movie files.");
            return;
        }

        if (inputs.Count == 0)
        {
            IsStructureValid = false;
            ValidationMessages.Add("Please specify a VIDEO_TS folder or movie files.");
            return;
        }

        // 1. First check if a direct VIDEO_TS directory was provided that passes standard validation
        bool isDirectFolder = SourceMediaFiles.Count == 0 && inputs.Count == 1 && Directory.Exists(inputs[0]);
        if (isDirectFolder)
        {
            string actualVideoTsPath = ResolveVideoTsPath(inputs[0]);
            if (Directory.Exists(actualVideoTsPath))
            {
                var validator = new DvdVideoValidator();
                var valResult = validator.ValidateDirectory(actualVideoTsPath);

                bool hasMovieFiles = false;
                try
                {
                    var dirFiles = Directory.GetFiles(actualVideoTsPath, "*.*", SearchOption.TopDirectoryOnly);
                    hasMovieFiles = dirFiles.Any(f => DvdInputNormalizer.IsSupportedVideoFile(f) && !f.EndsWith(".vob", StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (valResult.IsValid && !hasMovieFiles)
                {
                    RequiresAutoAuthoring = false;
                    IsStructureValid = true;
                    TotalSizeBytes = valResult.TotalSizeBytes;
                    TitleSetCount = valResult.TitleSetNumbers.Count;
                    HasAudioTs = valResult.HasAudioTs;

                    foreach (var err in valResult.Errors)
                    {
                        ValidationMessages.Add($"[Error] {err}");
                    }
                    foreach (var warn in valResult.Warnings)
                    {
                        ValidationMessages.Add($"[Warning] {warn}");
                    }

                    ValidationMessages.Add($"VIDEO_TS structure verified: {valResult.TitleSetNumbers.Count} title set(s), {valResult.FileCount} file(s), {CompilationItemViewModel.FormatBytes(valResult.TotalSizeBytes)}.");

                    var planner = new DvdVideoLayoutPlanner();
                    var videoTsDirInfo = new DirectoryInfo(actualVideoTsPath);
                    var fileEntries = videoTsDirInfo.GetFiles()
                        .Where(f => !DvdInputNormalizer.IsIgnoredUselessFile(f.FullName))
                        .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                        .ToList();

                    var layout = planner.Plan(fileEntries, startLba: 0);
                    PopulateLayoutAndDualLayer(layout, actualVideoTsPath);
                    return;
                }
            }
        }

        // 2. Otherwise analyze with DvdInputNormalizer
        var analysis = DvdInputNormalizer.AnalyzeInputs(inputs);
        if (!analysis.CanAuthorOrBurn)
        {
            IsStructureValid = false;
            if (analysis.NormalizationNotes.Count > 0)
            {
                foreach (var note in analysis.NormalizationNotes)
                {
                    ValidationMessages.Add($"[Error] {note}");
                }
            }
            else
            {
                ValidationMessages.Add("Input does not contain valid DVD-Video structures or supported movie files.");
            }
            return;
        }

        if (analysis.Kind == DvdInputKind.CompliantVideoTs && analysis.IgnoredFiles.Count == 0)
        {
            string actualVideoTsPath = analysis.ExistingVideoTsPath!;
            var validator = new DvdVideoValidator();
            var valResult = validator.ValidateDirectory(actualVideoTsPath);
            RequiresAutoAuthoring = false;
            IsStructureValid = valResult.IsValid;
            TotalSizeBytes = valResult.TotalSizeBytes;
            TitleSetCount = valResult.TitleSetNumbers.Count;
            HasAudioTs = valResult.HasAudioTs;

            foreach (var err in valResult.Errors)
            {
                ValidationMessages.Add($"[Error] {err}");
            }
            foreach (var warn in valResult.Warnings)
            {
                ValidationMessages.Add($"[Warning] {warn}");
            }

            if (!valResult.IsValid) return;

            ValidationMessages.Add($"VIDEO_TS structure verified: {valResult.TitleSetNumbers.Count} title set(s), {valResult.FileCount} file(s), {CompilationItemViewModel.FormatBytes(valResult.TotalSizeBytes)}.");

            var planner = new DvdVideoLayoutPlanner();
            var videoTsDirInfo = new DirectoryInfo(actualVideoTsPath);
            var fileEntries = videoTsDirInfo.GetFiles()
                .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                .ToList();

            var layout = planner.Plan(fileEntries, startLba: 0);
            PopulateLayoutAndDualLayer(layout);
            return;
        }

        if (analysis.RequiresAuthoring || analysis.IgnoredFiles.Count > 0)
        {
            RequiresAutoAuthoring = true;
            IsStructureValid = true;

            if (string.IsNullOrWhiteSpace(VolumeLabel) || VolumeLabel == "DVD_VIDEO")
            {
                VolumeLabel = analysis.SuggestedVolumeLabel;
            }
            if (string.IsNullOrWhiteSpace(MenuTitle) || MenuTitle == "DVD Video")
            {
                MenuTitle = analysis.SuggestedMenuTitle;
            }

            foreach (var note in analysis.NormalizationNotes)
            {
                ValidationMessages.Add($"[Info] {note}");
            }
            if (analysis.IgnoredFiles.Count > 0)
            {
                ValidationMessages.Add($"[Info] Ignored useless file(s): {string.Join(", ", analysis.IgnoredFiles.Select(Path.GetFileName))}");
            }

            // Build preview layout in ExtentLayoutRows using DvdVideoLayoutPlanner
            var previewEntries = new List<IDvdVideoFileEntry>();
            const int ifoBytes = 12288; // 6 sectors = 12,288 bytes
            previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.IFO", new byte[ifoBytes]));
            if (AutoCreatePlayMenu)
            {
                previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.VOB", new byte[32768])); // 16 sectors
            }
            previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.BUP", new byte[ifoBytes]));

            // Each title's size once converted, from its running time and the bitrate the authoring will use; the
            // movie file's own size only while the running times are being read or cannot be (it says nothing about
            // the converted size: a small HEVC file can grow, a large one shrink).
            var fit = PlanFit(analysis.MovieFilePaths, out bool readNeeded);
            int count = Math.Max(1, analysis.MovieFilePaths.Count);
            for (int i = 0; i < count; i++)
            {
                int setNum = i + 1;
                long fileLength = 65536L;
                if (fit != null && i < fit.TitleBytes.Count)
                {
                    fileLength = fit.TitleBytes[i];
                }
                else if (i < analysis.MovieFilePaths.Count)
                {
                    try
                    {
                        var fi = new FileInfo(analysis.MovieFilePaths[i]);
                        if (fi.Exists)
                        {
                            fileLength = fi.Length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        // the placeholder size stays; authoring reports the file itself if it cannot be read
                    }
                }
                long vobSize = Math.Max(65536L, fileLength);

                previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_0.IFO", new byte[ifoBytes]));
                if (AutoCreatePlayMenu && GenerateChapterMenus)
                {
                    previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_0.VOB", new byte[32768]));
                }
                if (vobSize <= 10_000_000)
                {
                    previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_1.VOB", new byte[(int)vobSize]));
                }
                else
                {
                    previewEntries.Add(new PreviewDvdVideoFileEntry($"VTS_{setNum:D2}_1.VOB", vobSize));
                }
                previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_0.BUP", new byte[ifoBytes]));
            }

            var planner = new DvdVideoLayoutPlanner();
            var layout = planner.Plan(previewEntries, startLba: 0);

            TotalSizeBytes = previewEntries.Sum(e => e.SizeBytes);
            TitleSetCount = count;
            HasAudioTs = true;
            FitEstimate = fit;
            PopulateLayoutAndDualLayer(layout);

            // Last, once this plan is complete: a read that answers at once redoes the plan inside this call, and
            // nothing after it may overwrite that newer plan.
            if (readNeeded)
            {
                StartReadingRunningTimes(analysis.MovieFilePaths);
            }
        }
    }

    /// <summary>
    /// The fit of <paramref name="movies"/> from their running times, or null while they still have to be read
    /// (<paramref name="readNeeded"/>: the caller starts the read, which redoes the plan when it finishes) or when one
    /// cannot be read. Sets <see cref="FitSummaryText"/>.
    /// </summary>
    private DvdFitEstimate? PlanFit(IReadOnlyList<string> movies, out bool readNeeded)
    {
        readNeeded = false;
        var seconds = new List<double>(movies.Count);
        bool missing = false;
        foreach (string movie in movies)
        {
            if (_probedSeconds.TryGetValue(movie, out double s))
            {
                seconds.Add(s);
            }
            else
            {
                missing = true;
            }
        }

        if (missing)
        {
            if (DurationProbe != null || UseFfmpegWhenAvailable)
            {
                readNeeded = true; // started by the caller once this plan is done (see ValidateAndPlanLayout)
                FitSummaryText = "Reading the running times to work out how much fits on a DVD...";
            }
            return null;
        }

        var fit = DvdDiscFit.Estimate(seconds, BuildFitOptions(), DualLayer || UseDualLayerDisc);
        FitSummaryText = fit.Kind == DvdFitKind.Unknown
            ? "The running time of a movie could not be read, so the size shown is the movie files' own size, not the size once converted."
            : fit.Summary;
        return fit.Kind == DvdFitKind.Unknown ? null : fit;
    }

    private DvdAuthoringOptions BuildFitOptions() => new()
    {
        VideoBitrateKbps = VideoBitrateKbps,
        MaxVideoBitrateKbps = MaxVideoBitrateKbps,
        FitToDisc = FitToDisc,
        DualLayer = DualLayer || UseDualLayerDisc
    };

    private void StartReadingRunningTimes(IReadOnlyList<string> movies)
    {
        int generation = ++_probeGeneration;
        var probe = DurationProbe ?? ((movie, ct) => Task.Run(() => DvdVideoAuthoringService.ProbeDurationSecondsAsync(
            movie, string.IsNullOrWhiteSpace(CustomFfmpegPath) ? null : CustomFfmpegPath, ct), ct));
        FitEstimateTask = ReadRunningTimesAsync(generation, movies.Where(m => !_probedSeconds.ContainsKey(m)).ToList(), probe);
    }

    /// <summary>
    /// Reads the running times of <paramref name="movies"/>, then redoes the plan with them, unless the movie list
    /// changed meanwhile (a newer read is running) or a burn or build has started (it waits for this first).
    /// </summary>
    private async Task ReadRunningTimesAsync(int generation, List<string> movies, Func<string, CancellationToken, Task<double>> probe)
    {
        var found = new List<(string Movie, double Seconds)>(movies.Count);
        foreach (string movie in movies)
        {
            double seconds;
            try
            {
                seconds = await probe(movie, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unreadable counts as unknown: the screen then says the running time could not be read.
                System.Diagnostics.Debug.WriteLine($"Running time of '{movie}' could not be read: {ex.Message}");
                seconds = 0;
            }
            found.Add((movie, seconds));
        }

        foreach (var (movie, seconds) in found)
        {
            _probedSeconds[movie] = seconds;
        }

        if (generation == _probeGeneration && !IsBusy)
        {
            ValidateAndPlanLayout();
        }
    }

    /// <summary>
    /// Cancels any active DVD authoring, transcoding, ISO build, or burn operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = Strings.StatusDvdVideoCancellingDvdVideoOperation;
            try
            {
                _activeCts.Cancel();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Executes automated DVD authoring (menu generation and MPEG-2 transcoding) if required,
    /// populating temporary VIDEO_TS output.
    /// </summary>
    public async Task<DvdAuthoringResult> EnsureAuthoredAsync(CancellationToken cancellationToken = default)
    {
        List<string> inputs;
        if (SourceMediaFiles.Count > 0)
        {
            inputs = SourceMediaFiles.ToList();
        }
        else if (!string.IsNullOrWhiteSpace(VideoTsFolderPath))
        {
            inputs = VideoTsFolderPath
                .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        else
        {
            inputs = new List<string>();
        }

        if (inputs.Count == 0)
        {
            return new DvdAuthoringResult
            {
                Success = false,
                ErrorMessage = "No input files specified for authoring."
            };
        }

        CleanupTempAuthoredDirectory();
        string tempAuthorDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_DvdAuthor_" + Guid.NewGuid().ToString("N"));
        _tempAuthoredDirectory = tempAuthorDir;

        var options = new DvdAuthoringOptions
        {
            Standard = SelectedStandard,
            AspectRatio = SelectedAspectRatio,
            VolumeLabel = string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel,
            MenuTitle = string.IsNullOrWhiteSpace(MenuTitle) ? "DVD Video" : MenuTitle,
            AutoCreatePlayMenu = AutoCreatePlayMenu,
            MenuAutoPlaySeconds = MenuAutoPlaySeconds,
            AfterTitleAction = AfterTitleAction,
            ChapterIntervalSeconds = ChapterIntervalSeconds,
            SubtitlesOnByDefault = SubtitlesOnByDefault,
            SubtitleSize = SubtitleSize,
            FitToDisc = FitToDisc,
            VideoBitrateKbps = VideoBitrateKbps,
            MaxVideoBitrateKbps = MaxVideoBitrateKbps,
            UseFfmpegWhenAvailable = UseFfmpegWhenAvailable,
            CustomFfmpegPath = string.IsNullOrWhiteSpace(CustomFfmpegPath) ? null : CustomFfmpegPath,
            GenerateSubtitles = GenerateSubtitles,
            SubtitleLanguage = string.IsNullOrWhiteSpace(SubtitleLanguage) ? "en" : SubtitleLanguage.Trim().ToLowerInvariant(),
            DualLayer = DualLayer || FitEstimate?.DualLayer == true,
            GenerateChapterMenus = GenerateChapterMenus,
            MenuBackgroundImagePath = string.IsNullOrWhiteSpace(MenuBackgroundImagePath) ? null : MenuBackgroundImagePath,
            MenuAudioPath = string.IsNullOrWhiteSpace(MenuAudioPath) ? null : MenuAudioPath,
            MenuFontPreset = SelectedMenuFontPreset,
            MenuHighlightPreset = SelectedMenuHighlightPreset
        };

        foreach (var (movie, srt) in _chosenSubtitles)
        {
            options.SubtitleFiles[movie] = srt; // the user's picks; "" = no file for that movie
        }
        foreach (var (movie, titleName) in _customTitleNames)
        {
            if (!string.IsNullOrWhiteSpace(titleName))
            {
                options.TitleNames[movie] = titleName.Trim();
            }
        }

        var authoringService = new DvdVideoAuthoringService
        {
            SubtitleGenerator = GenerateSubtitles ? CreateSubtitleGenerator(options) : null
        };
        var progressHandler = new Progress<(double Percent, string Status)>(snap =>
        {
            BurnProgress = Math.Clamp(snap.Percent, 0, 100);
            StatusMessage = snap.Status;
        });

        bool manageBusy = !IsBusy;
        if (manageBusy)
        {
            IsBusy = true;
            BurnProgress = 1.0;
            StatusMessage = Strings.StatusDvdVideoPreparingDvdVideoAuthoringPipeline;
        }

        try
        {
            var result = await authoringService.AuthorAsync(inputs, tempAuthorDir, options, progressHandler, cancellationToken);

            foreach (var note in result.LogNotes)
            {
                ValidationMessages.Add($"[Authoring] {note}");
            }

            if (result.Success)
            {
                LastAuthoredVideoTsPath = result.VideoTsDirectory;
                TotalSizeBytes = result.TotalSizeBytes;
                TotalSectors = result.TotalSectors;
                TitleSetCount = result.TitleCount;
                HasAudioTs = true;

                if (Directory.Exists(result.VideoTsDirectory))
                {
                    var dirInfo = new DirectoryInfo(result.VideoTsDirectory);
                    var fileEntries = dirInfo.GetFiles()
                        .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                        .ToList();

                    var planner = new DvdVideoLayoutPlanner();
                    var layout = planner.Plan(fileEntries, startLba: 0);
                    ExtentLayoutRows.Clear();
                    CandidateLayerBreakLbas.Clear();
                    PopulateLayoutAndDualLayer(layout, result.VideoTsDirectory);
                }
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                ValidationMessages.Add($"[Error] {result.ErrorMessage}");
            }

            return result;
        }
        finally
        {
            if (manageBusy)
            {
                IsBusy = false;
            }
        }
    }

    private DvdSubtitleSourceResolver.SubtitleGenerator CreateSubtitleGenerator(DvdAuthoringOptions options)
    {
        WhisperInstallation? tool = WhisperLocator.Find(SubtitleToolInstallRoot);
        return (movie, progress, ct) => new SubtitleGenerationService().GenerateAsync(
            movie,
            new SubtitleGenerationOptions { Language = options.SubtitleLanguage, FfmpegPath = options.CustomFfmpegPath, Whisper = tool },
            progress,
            ct);
    }

    /// <summary>
    /// Builds a DVD-Video compliant ISO image stream (UDF 1.02 + ISO 9660 Bridge) with
    /// 32 KB IFO/BUP ECC gap alignment.
    /// </summary>
    public async Task<string> BuildDvdIsoAsync(string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetTempPath(), $"{VolumeLabel}_{DateTime.Now:yyyyMMddHHmmss}.iso");
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await FitEstimateTask;
        if (!IsStructureValid)
        {
            ValidateAndPlanLayout();
            await FitEstimateTask;
            if (!IsStructureValid)
            {
                throw new InvalidOperationException($"Cannot build DVD ISO: structure is not valid. {string.Join("; ", ValidationMessages)}");
            }
        }

        if (IsTooLongForDvd)
        {
            throw new InvalidOperationException(HasFitSummary ? FitSummaryText : "Too big for any DVD.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;

        IsBusy = true;
        BurnProgress = 1.0;
        try
        {
            if (RequiresAutoAuthoring && (string.IsNullOrEmpty(LastAuthoredVideoTsPath) || !Directory.Exists(LastAuthoredVideoTsPath)))
            {
                var authResult = await EnsureAuthoredAsync(cts.Token);
                if (!authResult.Success)
                {
                    throw new InvalidOperationException(authResult.ErrorMessage ?? "DVD authoring failed.");
                }
            }

            StatusMessage = Strings.StatusDvdVideoAuthoringDvdVideoIsoImage;
            string actualVideoTsPath = !string.IsNullOrEmpty(LastAuthoredVideoTsPath) && Directory.Exists(LastAuthoredVideoTsPath)
                ? LastAuthoredVideoTsPath
                : ResolveVideoTsPath(VideoTsFolderPath);

            var (root, videoTsDir) = CreateDvdCompilationTree(actualVideoTsPath);

            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            await IsoUdfImageBuilder.BuildToFileAsync(root, outputPath, buildOptions, null, cts.Token);
            BurnProgress = 100.0;
            LastBuiltIsoPath = outputPath;
            StatusMessage = $"DVD-Video ISO built successfully: {Path.GetFileName(outputPath)}";
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusDvdVideoDvdVideoIsoBuildCancelled;
            return string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error building DVD ISO: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"BuildDvdIsoAsync error: {ex}");
            return string.Empty;
        }
        finally
        {
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Burns the DVD-Video authoring compilation to the target optical recorder drive.
    /// </summary>
    public async Task<DataBurnResult> StartDvdBurnAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        await FitEstimateTask; // running times still being read: plan with them, not with the movie files' sizes
        if (!IsStructureValid)
        {
            ValidateAndPlanLayout();
            await FitEstimateTask;
            if (!IsStructureValid)
            {
                return DataBurnResult.Failed($"Cannot burn DVD: VIDEO_TS structure is invalid. {string.Join("; ", ValidationMessages)}");
            }
        }

        if (IsTooLongForDvd)
        {
            StatusMessage = HasFitSummary ? FitSummaryText : "Too big for any DVD: remove a movie and burn it on another disc.";
            return DataBurnResult.Failed(StatusMessage);
        }

        // Resolve and confirm the target optical recorder FIRST before running lengthy transcoding/authoring
        IDiscRecorderBackend? targetRecorder = null;
        bool replaceAllowed = false; // true only after the user saw the "will replace files" warning and clicked Burn
        if (parameter is IDiscRecorderBackend rec)
        {
            targetRecorder = rec;
        }
        else if (parameter is string driveId && !string.IsNullOrWhiteSpace(driveId))
        {
            targetRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase))
                             ?? DeviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
        }
        else if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            var confirmed = CdBurner.App.Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                AvailableRecorders,
                SelectedRecorder,
                $"Burn DVD-Video Disc ({VolumeLabel})",
                UseDirectDriveCommands ? new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine) : null);

            if (confirmed == null)
            {
                StatusMessage = Strings.StatusDvdVideoDvdVideoBurnCancelledByUser;
                return DataBurnResult.Failed(StatusMessage);
            }

            SelectedRecorder = confirmed;
            targetRecorder = confirmed;
            replaceAllowed = true;
        }

        targetRecorder ??= SelectedRecorder;
        if (targetRecorder == null)
        {
            RefreshDrives();
            targetRecorder = SelectedRecorder;
        }

        if (targetRecorder == null)
        {
            return DataBurnResult.Failed("No optical recorder selected or available.");
        }

        // Auto-mount blank DVD media on virtual drive if needed
        if (targetRecorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            }
        }

        if (DiscTooSmallMessage(targetRecorder) is string tooSmall)
        {
            StatusMessage = tooSmall;
            return DataBurnResult.Failed(tooSmall);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;

        IsBusy = true;
        BurnProgress = 1.0;
        FillPlan? fill = null;
        string? fillSkipped = null;

        try
        {
            if (FillWithPicture)
            {
                try
                {
                    (fill, fillSkipped) = await PrepareFillAsync(targetRecorder, cts.Token);
                }
                catch (InvalidOperationException ex)
                {
                    StatusMessage = ex.Message;
                    return DataBurnResult.Failed(ex.Message);
                }
            }

            if (RequiresAutoAuthoring && (string.IsNullOrEmpty(LastAuthoredVideoTsPath) || !Directory.Exists(LastAuthoredVideoTsPath)))
            {
                StatusMessage = Strings.StatusDvdVideoTranscodingAuthoringDvdVideoStructure;
                var authResult = await EnsureAuthoredAsync(cts.Token);
                if (!authResult.Success)
                {
                    StatusMessage = $"Authoring failed: {authResult.ErrorMessage}";
                    return DataBurnResult.Failed(authResult.ErrorMessage ?? "DVD authoring failed.");
                }
            }

            StatusMessage = $"Building ISO & burning DVD-Video disc to {targetRecorder.VolumePath} ({targetRecorder.ProductId})...";
            BurnProgress = 0;
            Telemetry.BeginSession($"DVD-Video ({(!string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "DVD_VIDEO")})", targetRecorder.VolumePath, targetRecorder.QueryMediaStatus().MediaType.GetDisplayName());

            string actualVideoTsPath = !string.IsNullOrEmpty(LastAuthoredVideoTsPath) && Directory.Exists(LastAuthoredVideoTsPath)
                ? LastAuthoredVideoTsPath
                : ResolveVideoTsPath(VideoTsFolderPath);

            var (root, videoTsDir) = CreateDvdCompilationTree(actualVideoTsPath);

            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            string effectiveLabel = !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "DVD_VIDEO";
            var buildOptions = new IsoBuildOptions(
                VolumeLabel: effectiveLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            var burnOptions = new DataBurnOptions
            {
                VolumeLabel = effectiveLabel,
                CloseDisc = true,
                VerifyAfterBurn = VerifyAfterBurn || UseDirectDriveCommands, // a direct burn is always read back
                EjectAfterBurn = EjectAfterBurn,
                Speed = SelectedWriteSpeed,
                OverwriteExistingData = replaceAllowed,
                RequestedBookType = SetDvdRomBookTypeWhenSupported ? DvdBookType.DvdRom : null,
                RequireBookType = false
            };

            var directEngine = UseDirectDriveCommands
                ? new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine, DirectBurnLogDirectory)
                : null;
            LastDirectBurnEngine = directEngine;
            LastLayerBreak = null;
            var burnService = new DataBurnService(directEngine ?? _burnEngine, new BookTypeService(new VendorBookTypeController()));
            var progressHandler = new ClosableProgress<BurnProgressSnapshot>(snap =>
            {
                Telemetry.RecordSnapshot(snap);
                if (snap.TotalLba > 0)
                {
                    BurnProgress = Math.Clamp((double)snap.CurrentLba / snap.TotalLba * 100.0, 0, 100);
                }
                else
                {
                    BurnProgress = Math.Clamp(snap.BufferLevelPercent, 0, 100);
                }

                if (!string.IsNullOrEmpty(snap.CurrentActionDescription))
                {
                    StatusMessage = $"{snap.CurrentActionDescription} ({BurnProgress:F0}%)";
                }
            });

            DataBurnResult result;
            if (fill is not null)
            {
                StatusMessage = "Drawing the picture on the rest of the disc (the whole disc is written and closed)...";
                var drawProgress = new ClosableProgress<CdBurner.Lithography.DiscJobProgress>(p =>
                {
                    if (p.Fraction is { } f) StatusMessage = $"{p.Message} ({f * 100:F0}%)";
                });
                try
                {
                    (result, _) = await DiscFillPicture.BurnAsync(burnService, targetRecorder, root, buildOptions, burnOptions,
                        fill.Picture, fill.Medium, fill.Disc, drawProgress, progressHandler, null, cts.Token);
                }
                catch (ArgumentException ex)
                {
                    drawProgress.Close();
                    StatusMessage = $"Nothing was written: {ex.Message}";
                    return DataBurnResult.Failed(StatusMessage);
                }

                drawProgress.Close();
            }
            else
            {
                result = directEngine != null && IsDualLayerRequired
                    ? await BurnDualLayerOnCellBoundaryAsync(burnService, targetRecorder, root, burnOptions, buildOptions, progressHandler, cts.Token)
                    : await burnService.BurnCompilationAsync(targetRecorder, root, burnOptions, buildOptions, progressHandler, cts.Token);
            }
            progressHandler.Close(); // a late progress report must not overwrite the final status line
            if (result.Success)
            {
                BurnProgress = 100.0;
                StatusMessage = $"DVD-Video burn completed successfully ({result.SectorsWritten:N0} sectors written to {targetRecorder.VolumePath})."
                    + (LastLayerBreak is { } brk ? $" Layer break: {brk}." : "")
                    + (directEngine?.LastSummary is { } how ? " " + how : "")
                    + (string.IsNullOrWhiteSpace(result.BookTypeNote) ? "" : " " + result.BookTypeNote)
                    + (string.IsNullOrWhiteSpace(result.VerificationNote) ? "" : " " + result.VerificationNote)
                    + (fill is not null ? " The rest of the disc carries the picture; the disc is closed." : "")
                    + (fillSkipped is not null ? " " + fillSkipped : "");
            }
            else
            {
                StatusMessage = $"Burn failed: {result.ErrorMessage}";
            }
            CompletionNotice.Show(result.Success ? "DVD-Video disc finished" : "DVD-Video burn failed", StatusMessage, result.Success);
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusDvdVideoDvdVideoBurnCancelled;
            return DataBurnResult.Cancelled(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"StartDvdBurnAsync error: {ex}");
            return DataBurnResult.Failed(ex.Message, ex);
        }
        finally
        {
            Telemetry.EndSession();
            CleanupTempAuthoredDirectory();
            if (fill?.TempFrame is { } frame)
            {
                try { File.Delete(frame); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            _activeCts = null;
            IsBusy = false;
        }
    }

    #endregion

    #region Helpers

    private void PopulateLayoutAndDualLayer(DvdVideoLayout layout, string? videoTsFolder = null)
    {
        _plannedBreakCells.Clear();
        TotalSectors = layout.TotalSectors;

        foreach (var placement in layout.Placements)
        {
            var row = new DvdExtentAlignmentRow
            {
                FileName = placement.FileName,
                StartLba = placement.StartLba.Value,
                EndLba = placement.EndLba.Value,
                SectorCount = placement.SectorCount,
                SizeBytes = placement.SizeBytes,
                IsPadding = placement.IsPadding,
                EccPaddingSectors = placement.IsPadding ? placement.SectorCount : 0,
                Status = placement.IsPadding ? "32 KB ECC Alignment Padding" : "Compliant Extent"
            };
            ExtentLayoutRows.Add(row);
        }

        // 3. Verify IFO/BUP ECC Separation
        if (layout.VerifyIfoBupSeparation(out var sepError))
        {
            ValidationMessages.Add("32 KB ECC block separation verified: all .BUP files are safe from .IFO write collisions.");
        }
        else
        {
            ValidationMessages.Add($"[Warning] ECC separation: {sepError}");
        }

        // 4. Dual-Layer assessment
        IsDualLayerRequired = DvdLayerBreakCalculator.RequiresLayerBreak(layout.TotalSectors) || (RequiresAutoAuthoring && DualLayer);
        if (layout.TotalSectors > DvdLayerBreakCalculator.MaxDvd9Sectors)
        {
            // No layer break to plan: no DVD holds it (the layer break calculator refuses sizes past a DVD-9).
            ValidationMessages.Add($"[Error] Too big for any DVD: {CompilationItemViewModel.FormatBytes(layout.TotalSectors * 2048L)} " +
                                   $"({layout.TotalSectors:N0} sectors); a dual-layer DVD holds {DvdLayerBreakCalculator.MaxDvd9Sectors:N0}.");
        }
        else if (IsDualLayerRequired && PlannedCellBreaks(layout, videoTsFolder) is { Count: > 0 } cellBreaks)
        {
            // Real cell starts in the allowed range (layer 0 at least half, within its capacity). Whether a cell also
            // lands on a 16-sector block is only known on the built image, so that is checked at burn time.
            int minLayer0 = DvdLayerBreakCalculator.CalculateMinimumLayer0Sectors(layout.TotalSectors);
            foreach (var p in cellBreaks)
            {
                CandidateLayerBreakLbas.Add(p.Lba);
                _plannedBreakCells[p.Lba] = p.Cell;
            }
            var nearest = cellBreaks.OrderBy(p => Math.Abs(p.Lba - minLayer0)).First();
            SelectedLayerBreakLba = nearest.Lba;
            ValidationMessages.Add($"Dual-Layer DVD-9 required ({layout.TotalSectors:N0} sectors). {cellBreaks.Count} cell boundaries can take the layer break; nearest an even split: title set {nearest.Cell.TitleSet}, cell {nearest.Cell.CellId}. The address on the disc is worked out when the image is built.");
        }
        else if (IsDualLayerRequired)
        {
            var candidates = DvdLayerBreakCalculator.CalculateCandidates(layout.TotalSectors, Array.Empty<int>());
            if (candidates.Count == 0)
            {
                var opt = DvdLayerBreakCalculator.SelectOptimalBreak(layout.TotalSectors);
                if (opt != null)
                {
                    CandidateLayerBreakLbas.Add(opt.BreakLba);
                    SelectedLayerBreakLba = opt.BreakLba;
                }
            }
            else
            {
                foreach (var c in candidates)
                {
                    CandidateLayerBreakLbas.Add(c.BreakLba);
                }
                SelectedLayerBreakLba = candidates[0].BreakLba;
            }

            ValidationMessages.Add($"Dual-Layer DVD-9 required ({layout.TotalSectors:N0} sectors). Optimal Layer Break LBA: {SelectedLayerBreakLba:N0}.");
        }
        else
        {
            SelectedLayerBreakLba = 0;
            ValidationMessages.Add($"Single-Layer DVD-5 sufficient ({layout.TotalSectors:N0} sectors <= {DvdLayerBreakCalculator.Dvd5SectorLimit:N0}).");
        }
    }

    /// <summary>
    /// Cell starts of the VIDEO_TS folder placed on the planned layout, within the range a layer break may take;
    /// empty when there is no folder or no readable title set IFO.
    /// </summary>
    private static IReadOnlyList<DvdCellPosition> PlannedCellBreaks(DvdVideoLayout layout, string? videoTsFolder)
    {
        if (string.IsNullOrEmpty(videoTsFolder) || !Directory.Exists(videoTsFolder))
        {
            return Array.Empty<DvdCellPosition>();
        }

        var files = layout.FilePlacements
            .GroupBy(p => p.FileName.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (g.First().StartLba.Value, g.First().SectorCount));
        var positions = DvdCellLayerBreak.PlaceCells(files, name => ReadSmallFile(Path.Combine(videoTsFolder, name)));
        int minLayer0 = DvdLayerBreakCalculator.CalculateMinimumLayer0Sectors(layout.TotalSectors);
        return positions
            .Where(p => p.Lba >= minLayer0 && p.Lba <= DvdLayerBreakCalculator.MaxLayer0Sectors)
            .GroupBy(p => p.Lba).Select(g => g.First())
            .OrderBy(p => p.Lba)
            .ToList();
    }

    /// <summary>An IFO's bytes, or null if it cannot be read or is too large to be one.</summary>
    private static byte[]? ReadSmallFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 16 * 1024 * 1024 ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dual-layer burn with the direct writer: builds the image, finds the cells in it, puts the layer break on the
    /// usable cell start nearest the one picked (or nearest an even split), then burns the image with that break.
    /// Stops before writing when no cell start can take the break.
    /// </summary>
    private async Task<DataBurnResult> BurnDualLayerOnCellBoundaryAsync(DataBurnService burnService, IDiscRecorderBackend recorder,
        CompilationDirectory root, DataBurnOptions burnOptions, IsoBuildOptions buildOptions, IProgress<BurnProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        string tempImage = Path.Combine(Path.GetTempPath(), $"ignisdisc_dvd9_{Guid.NewGuid():N}.iso");
        await using var image = new FileStream(tempImage, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        StatusMessage = Strings.StatusDvdVideoBuildingTheDiscImageToPlace;
        await IsoUdfImageBuilder.BuildAsync(root, image, buildOptions, null, cancellationToken, 0);
        int totalSectors = OpticalConstants.BytesToSectors(image.Length, OpticalConstants.DataSectorSize);

        DvdCellStart? picked = _plannedBreakCells.TryGetValue(SelectedLayerBreakLba, out var cell) ? cell : null;
        DvdCellPosition? chosen = DvdCellLayerBreak.ChooseInImage(image, picked);

        if (chosen == null)
        {
            return DataBurnResult.Failed("No cell boundary of this DVD-Video can take the layer break (it must start on a 16-sector " +
                "block with at least half the disc on the first layer). Nothing was written. Without Direct drive commands, the " +
                "Windows writer burns the disc and places the break itself.");
        }

        LastLayerBreak = chosen;
        StatusMessage = $"Layer break: {chosen} ({chosen.Lba * 100.0 / totalSectors:F1}% on the first layer). Burning...";
        image.Position = 0;
        var options = new DataBurnOptions
        {
            VolumeLabel = burnOptions.VolumeLabel,
            CloseDisc = burnOptions.CloseDisc,
            VerifyAfterBurn = burnOptions.VerifyAfterBurn,
            StrictVerify = burnOptions.StrictVerify,
            EjectAfterBurn = burnOptions.EjectAfterBurn,
            Speed = burnOptions.Speed,
            OverwriteExistingData = burnOptions.OverwriteExistingData,
            LayerBreakSectors = chosen.Lba,
            RequestedBookType = burnOptions.RequestedBookType,
            RequireBookType = burnOptions.RequireBookType
        };
        return await burnService.BurnStreamAsync(recorder, image, options, progress, cancellationToken);
    }

    private void RefreshDrives()
    {
        var recorders = _deviceService.GetRecorders();
        AvailableRecorders.Clear();
        foreach (var r in recorders)
        {
            AvailableRecorders.Add(r);
        }

        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();
    }

    private void ResetState()
    {
        CleanupTempAuthoredDirectory();
        IsStructureValid = false;
        RequiresAutoAuthoring = false;
        OnPropertyChanged(nameof(AreAuthoringOptionsEnabled));
        LastAuthoredVideoTsPath = null;
        ValidationMessages.Clear();
        ExtentLayoutRows.Clear();
        CandidateLayerBreakLbas.Clear();
        _plannedBreakCells.Clear();
        SelectedLayerBreakLba = 0;
        IsDualLayerRequired = false;
        TotalSizeBytes = 0;
        TotalSectors = 0;
        TitleSetCount = 0;
        HasAudioTs = false;
        _probeGeneration++; // a running-time read still under way must not bring the old movies back into the plan
        FitEstimate = null;
        FitSummaryText = string.Empty;
    }

    /// <summary>
    /// A message when the disc in <paramref name="recorder"/> cannot hold this DVD (<see cref="TotalSectors"/>: the
    /// planned size, converted size for movies). Checked before converting, so a too-small disc is found in seconds,
    /// not after an hour of conversion. Null when it fits or the drive does not say how much the disc holds (the burn
    /// itself checks again once the image is built).
    /// </summary>
    private string? DiscTooSmallMessage(IDiscRecorderBackend recorder)
    {
        if (TotalSectors <= 0)
        {
            return null;
        }

        MediaStatusDto status;
        try
        {
            status = recorder.QueryMediaStatus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Disc size not readable before the DVD-Video burn: {ex.Message}");
            return null;
        }

        if (!status.MediaPresent)
        {
            return null;
        }

        // A rewritable disc with data is erased first, so all of it counts; otherwise only the free part.
        int room = status.IsRewritable && !status.IsBlank ? Math.Max(status.FreeSectors, status.TotalSectors) : status.FreeSectors;
        if (room <= 0 || TotalSectors <= room)
        {
            return null;
        }

        return $"This disc holds {CompilationItemViewModel.FormatBytes(room * 2048L)}, but this DVD needs about " +
               $"{CompilationItemViewModel.FormatBytes(TotalSectors * 2048L)}. " +
               (FitEstimate?.Kind == DvdFitKind.DualLayerChosen
                   ? "Put in a dual-layer DVD (DVD+R DL, 8.5 GB), or choose the 4.7 GB DVD (compressed more)."
                   : TotalSectors <= DvdLayerBreakCalculator.MaxDvd9Sectors
                   ? "Put in a dual-layer DVD (DVD+R DL, 8.5 GB), or remove a movie and burn it on another disc."
                   : "Remove a movie and burn it on another disc.");
    }

    /// <summary>Re-renders the live still-menu preview bitmap for <c>DvdVideoView</c>.</summary>
    public void RefreshMenuPreview()
    {
        if (!AutoCreatePlayMenu)
        {
            MenuPreviewImage = null;
            MenuPreviewPageCount = 1;
            if (_menuPreviewPageIndex != 0)
            {
                _menuPreviewPageIndex = 0;
                OnPropertyChanged(nameof(MenuPreviewPageIndex));
            }
            OnPropertyChanged(nameof(MenuPreviewPageText));
            OnPropertyChanged(nameof(CanGoToPreviousMenuPreviewPage));
            OnPropertyChanged(nameof(CanGoToNextMenuPreviewPage));
            PreviousMenuPreviewPageCommand.RaiseCanExecuteChanged();
            NextMenuPreviewPageCommand.RaiseCanExecuteChanged();
            return;
        }

        var lookup = new DvdAuthoringOptions();
        foreach (var (movie, srt) in _chosenSubtitles)
        {
            lookup.SubtitleFiles[movie] = srt;
        }

        var titles = new List<DvdMenuTitle>();
        foreach (string movie in SourceMediaFiles.Take(DvdStillMenu.MaxTitles))
        {
            bool hasSubs = DvdVideoAuthoringService.FindSubtitleFile(movie, lookup) != null;
            titles.Add(new DvdMenuTitle(GetTitleName(movie), null, hasSubs));
        }
        if (titles.Count == 0)
        {
            int fallbackCount = Math.Clamp(TitleSetCount > 0 ? TitleSetCount : 1, 1, DvdStillMenu.MaxTitles);
            for (int i = 1; i <= fallbackCount; i++)
            {
                titles.Add(new DvdMenuTitle(fallbackCount == 1 ? "Sample Movie" : $"Title {i}", null, false));
            }
        }

        var style = new DvdMenuStyle(
            SelectedMenuFontPreset,
            SelectedMenuHighlightPreset,
            MenuBackgroundImagePath,
            MenuAudioPath);

        string heading = string.IsNullOrWhiteSpace(MenuTitle) ? "DVD Video" : MenuTitle;
        System.Drawing.Bitmap previewBitmap;

        if (PreviewChapterSubmenu && GenerateChapterMenus)
        {
            double durationSec = 1800.0;
            if (SourceMediaFiles.Count > 0 && _probedSeconds.TryGetValue(SourceMediaFiles[0], out double probed) && probed > 1.0)
            {
                durationSec = probed;
            }
            int interval = ChapterIntervalSeconds > 0 ? ChapterIntervalSeconds : (int)Math.Ceiling(durationSec) + 1;
            int chapterCount = Math.Clamp((int)Math.Floor(durationSec / Math.Max(1, interval)) + 1, 1, 24);
            var chapters = Enumerable.Range(0, chapterCount)
                .Select(i => new DvdMenuChapterInfo(i + 1, $"Chapter {i + 1}", TimeSpan.FromSeconds(i * (ChapterIntervalSeconds > 0 ? ChapterIntervalSeconds : 0))))
                .ToList();
            int pages = DvdStillMenu.GetChapterMenuPageCount(chapters.Count);
            MenuPreviewPageCount = pages;
            if (_menuPreviewPageIndex >= pages)
            {
                _menuPreviewPageIndex = pages - 1;
                OnPropertyChanged(nameof(MenuPreviewPageIndex));
            }
            int mainPage = DvdStillMenu.GetTitleMenuPageForTitle(1, titles.Count, includeChapters: true);
            (previewBitmap, _) = DvdStillMenu.RenderChapterPagePreview(
                titles[0].Name,
                titleNumber: 1,
                mainMenuPage: mainPage,
                chapters,
                _menuPreviewPageIndex,
                SelectedStandard,
                SelectedAspectRatio,
                style);
        }
        else
        {
            int pages = DvdStillMenu.GetTitleMenuPageCount(titles.Count, GenerateChapterMenus);
            MenuPreviewPageCount = pages;
            if (_menuPreviewPageIndex >= pages)
            {
                _menuPreviewPageIndex = pages - 1;
                OnPropertyChanged(nameof(MenuPreviewPageIndex));
            }
            (previewBitmap, _) = DvdStillMenu.RenderTitlePagePreview(
                heading,
                titles,
                _menuPreviewPageIndex,
                SelectedStandard,
                SelectedAspectRatio,
                GenerateChapterMenus,
                style);
        }

        using (previewBitmap)
        {
            MenuPreviewImage = ConvertBitmapToFrozenSource(previewBitmap);
        }

        OnPropertyChanged(nameof(MenuPreviewPageText));
        OnPropertyChanged(nameof(CanGoToPreviousMenuPreviewPage));
        OnPropertyChanged(nameof(CanGoToNextMenuPreviewPage));
        PreviousMenuPreviewPageCommand.RaiseCanExecuteChanged();
        NextMenuPreviewPageCommand.RaiseCanExecuteChanged();
        ClearMenuBackgroundCommand.RaiseCanExecuteChanged();
        ClearMenuAudioCommand.RaiseCanExecuteChanged();
    }

    private static BitmapSource ConvertBitmapToFrozenSource(System.Drawing.Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgr24,
                null,
                data.Scan0,
                data.Stride * bitmap.Height,
                data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void ExecuteBrowseMenuBackground(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            MenuBackgroundImagePath = path;
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Custom DVD Menu Background Image",
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            MenuBackgroundImagePath = dlg.FileName;
        }
    }

    private void ExecuteBrowseMenuAudio(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            MenuAudioPath = path;
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Background Menu Audio",
            Filter = "Audio Files (*.ac3;*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg)|*.ac3;*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            MenuAudioPath = dlg.FileName;
        }
    }

    private void ExecuteBrowseFillPicture(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            FillPicturePath = path;
            FillPictureFromMovie = false;
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the picture for the empty part of the disc",
            Filter = "PNG pictures (*.png)|*.png"
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            FillPicturePath = dlg.FileName;
            FillPictureFromMovie = false;
            FillWithPicture = true;
        }
    }

    /// <summary>A checked picture fill: what to draw and on which disc geometry.</summary>
    private sealed record FillPlan(string Picture, DiscArtMedium Medium, CalibrationParameters Disc, string? TempFrame);

    /// <summary>
    /// Checks a picture-fill burn before anything is converted or drawn. Returns the fill, or null with
    /// <c>Skipped</c> set when the movie frame could not be taken (the disc is then burned without the picture). Throws
    /// <see cref="InvalidOperationException"/> with a plain message when the disc or the choices refuse it.
    /// </summary>
    private async Task<(FillPlan? Fill, string? Skipped)> PrepareFillAsync(IDiscRecorderBackend recorder, CancellationToken cancellationToken)
    {
        MediaStatusDto status = recorder.QueryMediaStatus();
        if (DiscFillPicture.Refusal(status, closeDisc: true, appendSession: false) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        DiscArtMedium medium = DiscFillPicture.MediumFor(status.MediaType)!.Value;
        long discSectors = status.IsBlank && status.FreeSectors > 0 ? status.FreeSectors : status.TotalSectors;
        CalibrationParameters disc;
        try
        {
            disc = DiscFillPicture.FitToDisc(FillDiscGeometry ?? DiscFillPicture.PresetDisc(medium), discSectors);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        if (!FillPictureFromMovie)
        {
            if (string.IsNullOrWhiteSpace(FillPicturePath) || !File.Exists(FillPicturePath))
            {
                throw new InvalidOperationException("Choose the PNG picture for the empty part of the disc, or use a frame of the movie.");
            }

            return (new FillPlan(FillPicturePath, medium, disc, null), null);
        }

        string? ffmpeg = FfmpegLocator.FindFfmpegExecutable(string.IsNullOrWhiteSpace(CustomFfmpegPath) ? null : CustomFfmpegPath);
        string frame = Path.Combine(Path.GetTempPath(), $"ignisdisc_fill_frame_{Guid.NewGuid():N}.png");
        StatusMessage = "Taking a frame of the movie for the picture...";
        (string? grabbed, string? skipped) = await DiscFillPicture.GrabMovieFrameAsync(
            ffmpeg, DiscFillPicture.FindMainMovie(VideoTsFolderPath), frame, cancellationToken);
        if (grabbed is null)
        {
            try { File.Delete(frame); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return (null, skipped);
        }

        return (new FillPlan(grabbed, medium, disc, grabbed), null);
    }

    private void ExecuteAddMovieFiles(object? parameter)
    {
        if (parameter is IEnumerable<string> paths)
        {
            AddMovieFiles(paths);
            return;
        }
        if (parameter is string single && !string.IsNullOrWhiteSpace(single))
        {
            AddMovieFiles([single]);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Movie Video Files",
            Multiselect = true,
            Filter = "Video Files (*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.mpg;*.vob;*.ts)|*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.mpg;*.vob;*.ts;*.m4v;*.webm;*.m2ts|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true && dlg.FileNames != null && dlg.FileNames.Length > 0)
        {
            AddMovieFiles(dlg.FileNames);
        }
    }

    private void ExecuteSelectVideoTsFolder(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            VideoTsFolderPath = path;
            return;
        }

        // Interactive folder dialog
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select VIDEO_TS or DVD Root Directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            VideoTsFolderPath = dialog.FolderName;
        }
    }

    private static string ResolveVideoTsPath(string folderPath)
    {
        var dirInfo = new DirectoryInfo(folderPath);
        if (string.Equals(dirInfo.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            return dirInfo.FullName;
        }

        string subVideoTs = Path.Combine(folderPath, "VIDEO_TS");
        if (Directory.Exists(subVideoTs))
        {
            return subVideoTs;
        }

        return folderPath;
    }

    private static (CompilationDirectory Root, CompilationDirectory VideoTsDir) CreateDvdCompilationTree(string actualVideoTsPath)
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTsDir = root.AddDirectory("VIDEO_TS");
        PopulateDirectoryFromDisk(videoTsDir, actualVideoTsPath);

        // Check for sibling or parent AUDIO_TS; guarantee AUDIO_TS always exists
        bool hasAudioTs = false;
        var dirInfo = new DirectoryInfo(actualVideoTsPath);
        string? parent = dirInfo.Parent?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
            if (Directory.Exists(siblingAudioTs))
            {
                var audioTsDir = root.AddDirectory("AUDIO_TS");
                PopulateDirectoryFromDisk(audioTsDir, siblingAudioTs);
                hasAudioTs = true;
            }
        }

        if (!hasAudioTs)
        {
            root.AddDirectory("AUDIO_TS");
        }

        return (root, videoTsDir);
    }

    private static void PopulateDirectoryFromDisk(CompilationDirectory targetDir, string diskDir)
    {
        var di = new DirectoryInfo(diskDir);
        if (!di.Exists) return;

        foreach (var file in di.GetFiles())
        {
            if (DvdInputNormalizer.IsIgnoredUselessFile(file.FullName))
            {
                continue;
            }
            targetDir.AddFile(new CompilationFile(file.FullName, file.Length));
        }

        foreach (var sub in di.GetDirectories())
        {
            var child = targetDir.AddDirectory(sub.Name);
            PopulateDirectoryFromDisk(child, sub.FullName);
        }
    }

    private sealed class PreviewDvdVideoFileEntry : IDvdVideoFileEntry
    {
        public string Name { get; }
        public long SizeBytes { get; }

        public PreviewDvdVideoFileEntry(string name, long sizeBytes)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            SizeBytes = sizeBytes;
        }

        public Stream OpenRead() => Stream.Null;
    }

    #endregion
}
