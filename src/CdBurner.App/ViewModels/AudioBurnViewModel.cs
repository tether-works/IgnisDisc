using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model orchestrating Red Book Audio CD mastering, CD-Text album and track metadata editing,
/// TAO vs gapless DAO recording modes, DSP peak normalization, duration/capacity tracking, and Digital Audio Extraction (ripper).
/// </summary>
public class AudioBurnViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Stops any playing preview (releasing the SoundPlayer and its WAV buffer) and cancels an in-flight burn or rip.
    /// Called when the main window closes so preview audio never keeps playing after shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPreview();

        try
        {
            _activeBurnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly IAudioDecoder _audioDecoder;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly AudioCdRipperService _ripperService;
    private readonly AppSettingsService? _settingsService;

    private string _albumTitle = "Untitled Album";
    private string _albumPerformer = "Various Artists";
    private string? _albumSongwriter;
    private string? _upcEanCode;
    private AudioRecordingMode _recordingMode = AudioRecordingMode.DiscAtOnceGapless;
    private bool _normalizeAllTracks = false;
    private bool _useRmsLoudnessLeveling = false;
    private bool _normalizeLoudness = false;
    private bool _loudnessPerAlbum = false;
    private double _crossfadeSeconds = 0.0;
    private int _defaultPreGapFrames = OpticalConstants.CdLeadInFrames; // 150 frames = 2.0s
    private bool _includeCdText = true;
    private bool _isPreviewPlaying;
    private string _previewStatusText = "Select a track to preview";
    private string? _lastExportedCoverHtmlPath;
    private System.Media.SoundPlayer? _soundPlayer;
    private MemoryStream? _previewWavStream;
    private CancellationTokenSource? _previewCts;
    private MsfAddress _totalDuration = new(0, 0, 0);
    private double _discUsagePercent;
    private string _ripOutputFolder = string.Empty;
    private int _readOffsetSamples;
    private bool _secureRip = true; // matches AppSettings.SecureAudioRipByDefault
    private bool _lookupMetadata = true;
    private bool _verifyAccurateRip = true;
    private bool _verifyCtdb;
    private bool _continueOnUnreadable;
    private bool _includeRecoverySession; // off by default: some car head units refuse Enhanced CDs
    private string _dataSessionFolderPath = string.Empty;
    private string _dataSessionVolumeLabel = "CD_EXTRA";
    private bool _blueBookLayout;
    private bool _fillWithPicture; // off by default: the whole disc is written and closed
    private string _fillPicturePath = string.Empty; // empty: the automatic picture
    private bool _fillPictureLookupOnline; // off by default: asks MusicBrainz and the Cover Art Archive
    private string _dataSessionSummaryText = string.Empty;
    private bool _repairFromRecoverySession = true;
    private string _recoverySessionCapacityText = string.Empty;
    private AudioRecoveryEstimate? _recoveryEstimate;
    private string _ripReport = string.Empty;
    private bool _ripAsWav = true;
    private bool _ripAsFlac;
    private bool _ripAsMp3;
    private bool _ripAsOpus;
    private bool _ripAsAac;
    private string _selectedMp3Quality = AudioTargetChoices.DefaultMp3Quality;
    private string _ripLibraryFolder = string.Empty;
    private string _ripNamingTemplate = AudioNamingTemplate.Default;
    private bool _fetchCoverArt = true;
    private bool _writeFolderImage = true;
    private bool _overwriteExistingFiles;
    private IReadOnlyList<MusicBrainzRelease> _releaseList = [];
    private string? _releaseDiscId;
    private int _selectedReleaseIndex = -1;
    private string? _releaseDifferenceNote;
    private int _releaseLookupGeneration;
    private string _ripBitrateKbps = string.Empty;
    private string _ripCoverArtPath = string.Empty;

    private AudioTrackRowViewModel? _selectedTrack;
    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private BurnOperationResult? _lastBurnResult;
    private AudioCdRipResult? _lastRipResult;

    #region Properties

    /// <summary>
    /// Optical drive enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical burn engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Audio decoder used to probe and decode audio files.
    /// </summary>
    public IAudioDecoder AudioDecoder => _audioDecoder;

    /// <summary>
    /// High-level Audio CD burning service.
    /// </summary>
    public AudioCdBurnService AudioBurnService => _audioBurnService;

    /// <summary>
    /// High-level Audio CD ripping service.
    /// </summary>
    public AudioCdRipperService RipperService => _ripperService;

    /// <summary>
    /// Album or compilation title for CD-Text.
    /// </summary>
    public string AlbumTitle
    {
        get => _albumTitle;
        set => SetProperty(ref _albumTitle, value);
    }

    /// <summary>
    /// Primary album performer or artist for CD-Text.
    /// </summary>
    public string AlbumPerformer
    {
        get => _albumPerformer;
        set
        {
            if (SetProperty(ref _albumPerformer, value))
            {
                // Update performers that haven't been customized
                foreach (var trk in Tracks)
                {
                    if (string.IsNullOrWhiteSpace(trk.Performer) || trk.Performer == "Various Artists")
                    {
                        trk.Performer = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Album songwriter metadata for CD-Text.
    /// </summary>
    public string? AlbumSongwriter
    {
        get => _albumSongwriter;
        set => SetProperty(ref _albumSongwriter, value);
    }

    /// <summary>
    /// Universal Product Code / European Article Number (UPC/EAN) media catalog number for CD-Text.
    /// </summary>
    public string? UpcEanCode
    {
        get => _upcEanCode;
        set
        {
            if (SetProperty(ref _upcEanCode, value))
            {
                OnPropertyChanged(nameof(UpcEanProblem));
            }
        }
    }

    /// <summary>What is wrong with <see cref="UpcEanCode"/>, in plain words; null when it is empty or valid.</summary>
    public string? UpcEanProblem => CdBurner.Hardware.NativeWriting.SaoAudioLayout.DescribeMcnProblem(_upcEanCode);

    /// <summary>
    /// Why a direct burn cannot start with the codes as typed, or null. The direct writer leaves out an invalid ISRC or
    /// catalog number, so a typo would vanish from the disc without a word; it is caught here instead.
    /// </summary>
    public string? CodeProblem()
    {
        if (UpcEanProblem is { } upc)
        {
            return $"UPC/EAN \"{UpcEanCode}\": {upc}";
        }
        var bad = Tracks.FirstOrDefault(t => t.HasIsrcProblem);
        return bad is null ? null : $"Track {bad.TrackNumber} ISRC \"{bad.Isrc}\": {bad.IsrcProblem}";
    }

    /// <summary>
    /// Audio recording mode: Track-at-Once (TAO) or gapless Disc-at-Once (DAO).
    /// </summary>
    public AudioRecordingMode RecordingMode
    {
        get => _recordingMode;
        set => SetProperty(ref _recordingMode, value);
    }

    /// <summary>
    /// When true, peak audio DSP normalization is applied across all tracks prior to burning.
    /// </summary>
    public bool NormalizeAllTracks
    {
        get => _normalizeAllTracks;
        set => SetProperty(ref _normalizeAllTracks, value);
    }

    /// <summary>
    /// When true, perceptual RMS / ReplayGain-style loudness leveling (-14 dBFS) is applied across all tracks.
    /// </summary>
    public bool UseRmsLoudnessLeveling
    {
        get => _useRmsLoudnessLeveling;
        set => SetProperty(ref _useRmsLoudnessLeveling, value);
    }

    /// <summary>
    /// When true, tracks are brought to -18 LUFS with a -1 dBTP true-peak limiter (per track, or per album when
    /// <see cref="LoudnessPerAlbum"/> is set). Off by default.
    /// </summary>
    public bool NormalizeLoudness
    {
        get => _normalizeLoudness;
        set => SetProperty(ref _normalizeLoudness, value);
    }

    /// <summary>When true with <see cref="NormalizeLoudness"/>, one gain for the whole disc keeps the level differences between tracks.</summary>
    public bool LoudnessPerAlbum
    {
        get => _loudnessPerAlbum;
        set => SetProperty(ref _loudnessPerAlbum, value);
    }

    /// <summary>
    /// Equal-power crossfade duration in seconds between adjacent tracks (0.0 = disabled, max 10.0s).
    /// </summary>
    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set => SetProperty(ref _crossfadeSeconds, Math.Clamp(value, 0.0, 10.0));
    }

    /// <summary>
    /// Indicates whether the built-in audio track preview player is currently playing.
    /// </summary>
    public bool IsPreviewPlaying
    {
        get => _isPreviewPlaying;
        set
        {
            if (SetProperty(ref _isPreviewPlaying, value))
            {
                PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
                StopPreviewCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Status text for the built-in audio track preview player.
    /// </summary>
    public string PreviewStatusText
    {
        get => _previewStatusText;
        set => SetProperty(ref _previewStatusText, value);
    }

    /// <summary>
    /// Path to the most recently exported printable CD Jewel Case Cover &amp; Tracklist HTML file.
    /// </summary>
    public string? LastExportedCoverHtmlPath
    {
        get => _lastExportedCoverHtmlPath;
        set => SetProperty(ref _lastExportedCoverHtmlPath, value);
    }

    /// <summary>
    /// Default pre-gap in 75 fps frames assigned to new tracks (default 150 frames = 2.0 seconds).
    /// </summary>
    public int DefaultPreGapFrames
    {
        get => _defaultPreGapFrames;
        set => SetProperty(ref _defaultPreGapFrames, value);
    }

    /// <summary>
    /// Whether to generate and record Lead-In CD-Text binary packs.
    /// </summary>
    public bool IncludeCdText
    {
        get => _includeCdText;
        set => SetProperty(ref _includeCdText, value);
    }

    private bool _useDirectDriveCommands;
    private bool _directIsAppDefault;
    private bool _testWriteFirst;

    /// <summary>
    /// Burn with IgnisDisc's own drive commands (Session-at-Once) instead of the Windows burning service. This
    /// writes CD-Text, ISRC and catalog codes, pre-emphasis flags and a CUE sheet's gap marks to the disc, and reads
    /// the disc back afterwards. Off for a new view model; the app turns it on by default
    /// (<see cref="UseDirectDriveCommandsByDefault"/>). Setting it is the user's own choice.
    /// </summary>
    public bool UseDirectDriveCommands
    {
        get => _useDirectDriveCommands;
        set
        {
            _directIsAppDefault = false;
            if (SetProperty(ref _useDirectDriveCommands, value) && !value)
            {
                TestWriteFirst = false;
                IncludeRecoverySession = false;
                DataSessionFolderPath = string.Empty;
                FillWithPicture = false;
            }
        }
    }

    /// <summary>
    /// Turns direct drive commands on as the app's default rather than the user's choice: the only way the Windows
    /// burning service could write the album and track names (CD-Text) is not there, so direct is the better default.
    /// A drive that cannot take them (no Session-at-Once) then burns with the Windows writer and says so, instead of
    /// refusing as it does when the user ticked the box.
    /// </summary>
    public void UseDirectDriveCommandsByDefault()
    {
        if (SetProperty(ref _useDirectDriveCommands, true, nameof(UseDirectDriveCommands)))
        {
            _directIsAppDefault = true;
        }
    }

    /// <summary>
    /// With <see cref="UseDirectDriveCommands"/>: run a laser-off test write before the real one. Most drives then
    /// need the disc reloaded before they record it, so the burn stops after the test and says so.
    /// </summary>
    public bool TestWriteFirst
    {
        get => _testWriteFirst;
        set => SetProperty(ref _testWriteFirst, value && _useDirectDriveCommands);
    }

    /// <summary>
    /// Off-by-default option to write PAR2 recovery data over the disc's PCM as a second data session (Enhanced CD /
    /// CD-Extra). Requires direct drive commands; some car head units refuse Enhanced CDs.
    /// </summary>
    public bool IncludeRecoverySession
    {
        get => _includeRecoverySession;
        set
        {
            if (SetProperty(ref _includeRecoverySession, value))
            {
                if (value)
                {
                    FillWithPicture = false;
                    if (!string.IsNullOrWhiteSpace(_dataSessionFolderPath))
                    {
                        _dataSessionFolderPath = string.Empty;
                        OnPropertyChanged(nameof(DataSessionFolderPath));
                        OnPropertyChanged(nameof(HasDataSessionFolder));
                    }
                    if (!_useDirectDriveCommands)
                    {
                        UseDirectDriveCommands = true;
                    }
                }
                RecalculateTotals();
            }
        }
    }

    /// <summary>
    /// Optional folder on disk written as a Mode 1 ISO 9660 + Joliet second session (Enhanced CD / CD-Extra)
    /// after session 1 audio. Requires direct drive commands and cannot be combined with <see cref="IncludeRecoverySession"/>.
    /// </summary>
    public string DataSessionFolderPath
    {
        get => _dataSessionFolderPath;
        set
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _dataSessionFolderPath, trimmed))
            {
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    FillWithPicture = false;
                    if (_includeRecoverySession)
                    {
                        _includeRecoverySession = false;
                        OnPropertyChanged(nameof(IncludeRecoverySession));
                    }
                    if (!_useDirectDriveCommands)
                    {
                        UseDirectDriveCommands = true;
                    }
                }
                OnPropertyChanged(nameof(HasDataSessionFolder));
                RecalculateTotals();
            }
        }
    }

    /// <summary>
    /// Lay the session-2 data folder out as an Enhanced CD (Blue Book): CD-ROM XA Mode 2 Form 1 with CDPLUS/INFO.CDP and
    /// PICTURES/ next to the folder's files. Off by default: Windows reads a Mode 1 session best.
    /// </summary>
    public bool BlueBookLayout
    {
        get => _blueBookLayout;
        set
        {
            if (SetProperty(ref _blueBookLayout, value))
            {
                RecalculateTotals();
            }
        }
    }

    /// <summary>Optional volume label for the session-2 data folder (defaults to <c>"CD_EXTRA"</c>).</summary>
    public string DataSessionVolumeLabel
    {
        get => _dataSessionVolumeLabel;
        set
        {
            string normalized = value ?? string.Empty;
            if (SetProperty(ref _dataSessionVolumeLabel, normalized) && HasDataSessionFolder)
            {
                RecalculateTotals();
            }
        }
    }

    /// <summary>
    /// Fill the empty space after the music with a picture, drawn in a hidden second session (Mode 1 data) to the end of
    /// the disc; CD players see the same tracks. Off by default. Needs direct drive commands and cannot be combined
    /// with a recovery session or a data folder (each fills session 2).
    /// </summary>
    public bool FillWithPicture
    {
        get => _fillWithPicture;
        set
        {
            if (SetProperty(ref _fillWithPicture, value) && value)
            {
                if (_includeRecoverySession)
                {
                    _includeRecoverySession = false;
                    OnPropertyChanged(nameof(IncludeRecoverySession));
                }
                if (!string.IsNullOrWhiteSpace(_dataSessionFolderPath))
                {
                    _dataSessionFolderPath = string.Empty;
                    OnPropertyChanged(nameof(DataSessionFolderPath));
                    OnPropertyChanged(nameof(HasDataSessionFolder));
                }
                if (!_useDirectDriveCommands)
                {
                    UseDirectDriveCommands = true;
                }
                RecalculateTotals();
            }
        }
    }

    /// <summary>The chosen picture for <see cref="FillWithPicture"/>; empty for the automatic one (cover art).</summary>
    public string FillPicturePath
    {
        get => _fillPicturePath;
        set
        {
            if (SetProperty(ref _fillPicturePath, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(FillPictureChoiceText));
            }
        }
    }

    /// <summary>
    /// With the automatic picture: when no cover art is embedded in the files or lies next to them, look the album up
    /// on MusicBrainz and take its front cover from the Cover Art Archive. Off by default (an online lookup).
    /// </summary>
    public bool FillPictureLookupOnline
    {
        get => _fillPictureLookupOnline;
        set => SetProperty(ref _fillPictureLookupOnline, value);
    }

    /// <summary>What the picture fill will draw, for the screen.</summary>
    public string FillPictureChoiceText => string.IsNullOrWhiteSpace(_fillPicturePath)
        ? "Picture: automatic (cover art in the files, else cover.jpg / folder.jpg next to them)"
        : $"Picture: {Path.GetFileName(_fillPicturePath)}";

    /// <summary>The explanation shown next to the picture fill option.</summary>
    public string FillPictureNote => CdBurner.Lithography.DiscArt.AudioCdFillPicture.ScreenNote;

    /// <summary>HTTP client for the online cover lookup; null makes a new one per burn (tests set a fake).</summary>
    public HttpClient? FillPictureHttpClient { get; set; }

    /// <summary>Opens a file picker for the fill picture.</summary>
    public RelayCommand BrowseFillPictureCommand { get; }

    /// <summary>Goes back to the automatic fill picture.</summary>
    public RelayCommand ClearFillPictureCommand { get; }

    /// <summary>True when a session-2 data folder is selected for an Enhanced CD.</summary>
    public bool HasDataSessionFolder => !string.IsNullOrWhiteSpace(_dataSessionFolderPath);

    /// <summary>Plain-language capacity and file summary for the selected session-2 data folder.</summary>
    public string DataSessionSummaryText
    {
        get => _dataSessionSummaryText;
        private set => SetProperty(ref _dataSessionSummaryText, value);
    }

    /// <summary>
    /// Whether the Audio CD ripper checks for a session-2 PAR2 recovery volume and repairs damaged samples.
    /// </summary>
    public bool RepairFromRecoverySession
    {
        get => _repairFromRecoverySession;
        set => SetProperty(ref _repairFromRecoverySession, value);
    }

    /// <summary>Latest capacity and damage-tolerance estimate for a session-2 recovery session.</summary>
    public AudioRecoveryEstimate? RecoveryEstimate => _recoveryEstimate;

    /// <summary>Plain-language session-2 overhead (~2.5 min / 11,400 sectors) and PAR2 repair tolerance line.</summary>
    public string RecoverySessionCapacityText
    {
        get
        {
            if (string.IsNullOrEmpty(_recoverySessionCapacityText))
            {
                UpdateRecoveryEstimate(Tracks.Sum(t => t.DurationFrames), OpticalConstants.Cd700MbSectors);
            }
            return _recoverySessionCapacityText;
        }
        private set => SetProperty(ref _recoverySessionCapacityText, value);
    }

    /// <summary>Car head-unit compatibility warning shown alongside the recovery session option.</summary>
    public string RecoverySessionWarningText => AudioRecoverySessionService.CarHeadUnitWarning;

    /// <summary>Folder for the command logs of direct burns; null for the default under %LOCALAPPDATA%.</summary>
    public string? DirectBurnLogDirectory { get; set; }

    /// <summary>The direct writer's own result for the last burn made with <see cref="UseDirectDriveCommands"/>.</summary>
    public CdBurner.Hardware.NativeWriting.NativeSaoWriteResult? LastDirectBurnResult { get; private set; }

    /// <summary>The command log of the last direct burn.</summary>
    public string? LastDirectBurnLogPath { get; private set; }

    /// <summary>
    /// Ordered list of audio tracks in this compilation.
    /// </summary>
    public ObservableCollection<AudioTrackRowViewModel> Tracks { get; } = new();

    /// <summary>
    /// Indicates whether the compilation currently contains at least one audio track.
    /// </summary>
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>
    /// Currently selected track in the track table.
    /// </summary>
    public AudioTrackRowViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                MoveTrackUpCommand.RaiseCanExecuteChanged();
                MoveTrackDownCommand.RaiseCanExecuteChanged();
                RemoveTrackCommand.RaiseCanExecuteChanged();
                PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Total duration in Red Book Minute:Second:Frame format across all compilation tracks.
    /// </summary>
    public MsfAddress TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetProperty(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
                OnPropertyChanged(nameof(DiscUsageText));
            }
        }
    }

    /// <summary>
    /// Percentage (0.0 to 100.0) of target CD media capacity consumed by this compilation.
    /// </summary>
    public double DiscUsagePercent
    {
        get => _discUsagePercent;
        set
        {
            if (SetProperty(ref _discUsagePercent, value))
            {
                OnPropertyChanged(nameof(DiscUsageText));
            }
        }
    }

    /// <summary>
    /// Target destination folder for ripping Audio CD tracks to uncompressed WAV files.
    /// </summary>
    public string RipOutputFolder
    {
        get => _ripOutputFolder;
        set => SetProperty(ref _ripOutputFolder, value);
    }

    /// <summary>
    /// Available optical recorders discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Optical recorder selected as target for burning or ripping.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                RecalculateTotals();
            }
        }
    }

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set => SetProperty(ref _selectedWriteSpeed, value);
    }

    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private CancellationTokenSource? _activeBurnCts;

    /// <summary>
    /// Indicates whether a burn or rip operation is currently executing.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                StartAudioBurnCommand.RaiseCanExecuteChanged();
                RipAudioCdCommand.RaiseCanExecuteChanged();
                DetectReadOffsetCommand?.RaiseCanExecuteChanged();
                CancelBurnCommand.RaiseCanExecuteChanged();
                ClearAllTracksCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Real-time status description displayed in the view.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Burning or ripping progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Formatted total duration string (mm:ss:ff).
    /// </summary>
    public string TotalDurationText => _totalDuration.ToString();

    /// <summary>
    /// Formatted disc usage summary string (e.g. "42:15:30 / 80:00:00 (52.8%)").
    /// </summary>
    public string DiscUsageText => $"{_totalDuration} ({Math.Min(100.0, _discUsagePercent):F1}%)";

    /// <summary>
    /// Formatted track count summary.
    /// </summary>
    public string TotalTracksText => $"{Tracks.Count} / {AudioCdCompilation.MaxTracks} Tracks";

    /// <summary>
    /// Result of the most recent burn operation.
    /// </summary>
    public BurnOperationResult? LastBurnResult
    {
        get => _lastBurnResult;
        set => SetProperty(ref _lastBurnResult, value);
    }

    /// <summary>
    /// Result of the most recent rip operation.
    /// </summary>
    public AudioCdRipResult? LastRipResult
    {
        get => _lastRipResult;
        set => SetProperty(ref _lastRipResult, value);
    }

    /// <summary>
    /// Hardware read offset correction in samples (-10000 to +10000) applied during Digital Audio Extraction.
    /// </summary>
    public int ReadOffsetSamples
    {
        get => _readOffsetSamples;
        set
        {
            int clamped = Math.Clamp(value, -10000, 10000);
            if (_readOffsetSamples != clamped)
            {
                _readOffsetSamples = clamped;
                OnPropertyChanged();
                SaveSettingsIfAvailable();
            }
            else if (_readOffsetSamples != value)
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether to use C2 error pointer based secure extraction and re-read suspect sectors.
    /// </summary>
    public bool SecureRip
    {
        get => _secureRip;
        set
        {
            if (SetProperty(ref _secureRip, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Whether to query MusicBrainz online database for album and track metadata prior to ripping.
    /// </summary>
    public bool LookupMetadata
    {
        get => _lookupMetadata;
        set
        {
            if (SetProperty(ref _lookupMetadata, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Whether to verify extracted track checksums against the online AccurateRip database after ripping.
    /// </summary>
    public bool VerifyAccurateRip
    {
        get => _verifyAccurateRip;
        set
        {
            if (SetProperty(ref _verifyAccurateRip, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Whether to verify extracted track and disc checksums against the online CUETools Database (CTDB) after ripping.
    /// Strictly opt-in (default false).
    /// </summary>
    public bool VerifyCtdb
    {
        get => _verifyCtdb;
        set
        {
            if (SetProperty(ref _verifyCtdb, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>Save the rip as uncompressed WAV files.</summary>
    public bool RipAsWav
    {
        get => _ripAsWav;
        set => SetFormatFlag(ref _ripAsWav, value);
    }

    /// <summary>Also save the rip as FLAC (needs the optional ffmpeg download).</summary>
    public bool RipAsFlac
    {
        get => _ripAsFlac;
        set => SetFormatFlag(ref _ripAsFlac, value);
    }

    /// <summary>Also save the rip as MP3 (needs the optional ffmpeg download).</summary>
    public bool RipAsMp3
    {
        get => _ripAsMp3;
        set => SetFormatFlag(ref _ripAsMp3, value);
    }

    /// <summary>Also save the rip as Opus (needs the optional ffmpeg download).</summary>
    public bool RipAsOpus
    {
        get => _ripAsOpus;
        set => SetFormatFlag(ref _ripAsOpus, value);
    }

    /// <summary>Also save the rip as AAC (uses the Windows AAC encoder).</summary>
    public bool RipAsAac
    {
        get => _ripAsAac;
        set => SetFormatFlag(ref _ripAsAac, value);
    }

    /// <summary>
    /// The first checked format in WAV, FLAC, MP3, Opus, AAC order (WAV when none is checked). Setting it checks exactly
    /// that one format, which keeps older code that thinks of a single format working.
    /// </summary>
    public AudioRipFormat SelectedRipFormat
    {
        get => CheckedRipFormats() is { Count: > 0 } formats ? formats[0] : AudioRipFormat.Wav;
        set
        {
            _ripAsWav = value == AudioRipFormat.Wav;
            _ripAsFlac = value == AudioRipFormat.Flac;
            _ripAsMp3 = value == AudioRipFormat.Mp3;
            _ripAsOpus = value == AudioRipFormat.Opus;
            _ripAsAac = value == AudioRipFormat.Aac;
            OnPropertyChanged(nameof(RipAsWav));
            OnPropertyChanged(nameof(RipAsFlac));
            OnPropertyChanged(nameof(RipAsMp3));
            OnPropertyChanged(nameof(RipAsOpus));
            OnPropertyChanged(nameof(RipAsAac));
            RipFormatSelectionChanged();
        }
    }

    private List<AudioRipFormat> CheckedRipFormats()
    {
        var formats = new List<AudioRipFormat>();
        if (_ripAsWav) formats.Add(AudioRipFormat.Wav);
        if (_ripAsFlac) formats.Add(AudioRipFormat.Flac);
        if (_ripAsMp3) formats.Add(AudioRipFormat.Mp3);
        if (_ripAsOpus) formats.Add(AudioRipFormat.Opus);
        if (_ripAsAac) formats.Add(AudioRipFormat.Aac);
        return formats;
    }

    private void SetFormatFlag(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            RipFormatSelectionChanged();
        }
    }

    private void RipFormatSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedRipFormat));
        OnPropertyChanged(nameof(RipBitrateApplies));
        OnPropertyChanged(nameof(RipMp3Selected));
        OnPropertyChanged(nameof(RipNamingPreview));
        SaveSettingsIfAvailable();
    }

    /// <summary>Whether the bitrate box applies: Opus or AAC is checked.</summary>
    public bool RipBitrateApplies => _ripAsOpus || _ripAsAac;

    /// <summary>Whether the MP3 quality box applies: MP3 is checked.</summary>
    public bool RipMp3Selected => _ripAsMp3;

    /// <summary>MP3 qualities offered: constant bitrates and the two common VBR settings.</summary>
    public IReadOnlyList<string> Mp3QualityChoices => AudioTargetChoices.Mp3Qualities;

    /// <summary>The chosen MP3 quality, one of <see cref="Mp3QualityChoices"/>. Unknown text is ignored.</summary>
    public string SelectedMp3Quality
    {
        get => _selectedMp3Quality;
        set
        {
            if (value != null && AudioTargetChoices.Mp3Qualities.Contains(value) && SetProperty(ref _selectedMp3Quality, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Folder that receives the finished album (artist and album folders under it, named by <see cref="RipNamingTemplate"/>).
    /// Empty saves the files in <see cref="RipOutputFolder"/> with the flat names used before.
    /// </summary>
    public string RipLibraryFolder
    {
        get => _ripLibraryFolder;
        set
        {
            if (SetProperty(ref _ripLibraryFolder, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasRipLibraryFolder));
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>True when a library folder is set, so the naming template is used.</summary>
    public bool HasRipLibraryFolder => !string.IsNullOrWhiteSpace(_ripLibraryFolder);

    /// <summary>Templates offered in the File names box; any other text can be typed.</summary>
    public IReadOnlyList<string> NamingTemplateChoices { get; } =
        [AudioNamingTemplate.Default, AudioNamingTemplate.Flat, @"{format}\" + AudioNamingTemplate.Default];

    /// <summary>Naming template for files in the library folder (tokens are listed in <see cref="AudioNamingTemplate"/>).</summary>
    public string RipNamingTemplate
    {
        get => _ripNamingTemplate;
        set
        {
            if (SetProperty(ref _ripNamingTemplate, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(RipNamingProblem));
                OnPropertyChanged(nameof(RipNamingPreview));
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>What is wrong with <see cref="RipNamingTemplate"/>, or null when it can be used.</summary>
    public string? RipNamingProblem => AudioNamingTemplate.Validate(_ripNamingTemplate);

    /// <summary>The sample track used for the preview under the File names box.</summary>
    public static AudioNamingFields NamingPreviewSample(AudioRipFormat format) =>
        new("Rammstein", "Rammstein", "Sehnsucht", "1997", 12, 12, "Stripped", format);

    /// <summary>The path the template gives a sample track, for example "Rammstein\Sehnsucht (1997)\12 - Stripped.mp3". Empty when the template is invalid.</summary>
    public string RipNamingPreview
    {
        get
        {
            if (AudioNamingTemplate.Validate(_ripNamingTemplate) != null)
            {
                return string.Empty;
            }

            var format = SelectedRipFormat;
            string template = CheckedRipFormats().Count > 1 && !_ripNamingTemplate.Contains("{format}", StringComparison.OrdinalIgnoreCase)
                ? @"{format}\" + _ripNamingTemplate
                : _ripNamingTemplate;
            try
            {
                return AudioNamingTemplate.Render(template, NamingPreviewSample(format), AudioRipFormats.Extension(format));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Download the front cover from the Cover Art Archive when a MusicBrainz edition is known and no cover file is chosen.</summary>
    public bool FetchCoverArt
    {
        get => _fetchCoverArt;
        set
        {
            if (SetProperty(ref _fetchCoverArt, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>Save folder.jpg (or .png) next to the album's files when a cover exists.</summary>
    public bool WriteFolderImage
    {
        get => _writeFolderImage;
        set
        {
            if (SetProperty(ref _writeFolderImage, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>Replace files that already exist at the destination. Off: such a rip stops and keeps the WAV files.</summary>
    public bool OverwriteExistingFiles
    {
        get => _overwriteExistingFiles;
        set => SetProperty(ref _overwriteExistingFiles, value);
    }

    /// <summary>One line per MusicBrainz edition of the inserted disc; filled when editions differ or after "Look up editions".</summary>
    public ObservableCollection<string> ReleaseChoices { get; } = new();

    /// <summary>Index into <see cref="ReleaseChoices"/> of the edition the user picked; -1 when none.</summary>
    public int SelectedReleaseIndex
    {
        get => _selectedReleaseIndex;
        set => SetProperty(ref _selectedReleaseIndex, value);
    }

    /// <summary>Plain-language list of what differs between the editions, or null.</summary>
    public string? ReleaseDifferenceNote
    {
        get => _releaseDifferenceNote;
        private set
        {
            if (SetProperty(ref _releaseDifferenceNote, value))
            {
                OnPropertyChanged(nameof(HasReleaseDifferenceNote));
            }
        }
    }

    /// <summary>True when <see cref="ReleaseDifferenceNote"/> has text.</summary>
    public bool HasReleaseDifferenceNote => !string.IsNullOrEmpty(_releaseDifferenceNote);

    /// <summary>True when more than one edition is listed, so the Edition box is shown.</summary>
    public bool HasReleaseChoice => ReleaseChoices.Count > 1;

    /// <summary>The convert-files panel (the "Convert Audio" tab).</summary>
    public AudioConvertViewModel Convert { get; }

    /// <summary>Bitrate in kbit/s for MP3, Opus and AAC; empty uses the default for the format.</summary>
    public string RipBitrateKbps
    {
        get => _ripBitrateKbps;
        set => SetProperty(ref _ripBitrateKbps, value ?? string.Empty);
    }

    /// <summary>
    /// Optional front-cover image (JPEG, PNG, GIF or WebP) embedded in ripped FLAC and MP3 files. Empty means no cover.
    /// A chosen file is used instead of the Cover Art Archive.
    /// </summary>
    public string RipCoverArtPath
    {
        get => _ripCoverArtPath;
        set => SetProperty(ref _ripCoverArtPath, value ?? string.Empty);
    }

    /// <summary>
    /// When true, unreadable sectors that fail all retries are zero-filled with silence instead of failing the rip.
    /// </summary>
    public bool ContinueOnUnreadable
    {
        get => _continueOnUnreadable;
        set => SetProperty(ref _continueOnUnreadable, value);
    }

    /// <summary>
    /// Multi-line summary report of the most recent Audio CD extraction and verification operation.
    /// </summary>
    public string RipReport
    {
        get => _ripReport;
        set => SetProperty(ref _ripReport, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Adds one or more audio files to the compilation.
    /// </summary>
    public RelayCommand AddAudioFilesCommand { get; }

    /// <summary>
    /// Removes the currently selected track from the compilation.
    /// </summary>
    public RelayCommand RemoveTrackCommand { get; }

    /// <summary>
    /// Removes all tracks from the compilation.
    /// </summary>
    public RelayCommand ClearAllTracksCommand { get; }

    /// <summary>
    /// Moves the selected track up one position in the track sequence.
    /// </summary>
    public RelayCommand MoveTrackUpCommand { get; }

    /// <summary>
    /// Moves the selected track down one position in the track sequence.
    /// </summary>
    public RelayCommand MoveTrackDownCommand { get; }

    /// <summary>
    /// Starts burning the Audio CD compilation to the optical recorder.
    /// </summary>
    public AsyncRelayCommand StartAudioBurnCommand { get; }

    /// <summary>Opens a folder picker for the optional session-2 data folder (Enhanced CD / CD-Extra).</summary>
    public RelayCommand BrowseDataSessionFolderCommand { get; }

    /// <summary>Clears the selected session-2 data folder.</summary>
    public RelayCommand ClearDataSessionFolderCommand { get; }

    /// <summary>
    /// Cancels any actively executing Audio CD burn operation.
    /// </summary>
    public RelayCommand CancelBurnCommand { get; }

    /// <summary>
    /// Extracts Red Book audio tracks from the inserted CD into WAV files and CUE sheet.
    /// </summary>
    public AsyncRelayCommand RipAudioCdCommand { get; }

    /// <summary>
    /// Measures the optical drive's audio read offset using an inserted CD matched against AccurateRip.
    /// </summary>
    public AsyncRelayCommand DetectReadOffsetCommand { get; }

    /// <summary>
    /// Queries MusicBrainz with 1 click for the inserted Audio CD's table-of-contents DiscID.
    /// </summary>
    public AsyncRelayCommand LookupDiscMetadataCommand { get; }

    /// <summary>
    /// Browses for an output directory to store ripped audio files.
    /// </summary>
    public RelayCommand BrowseRipOutputFolderCommand { get; }

    /// <summary>Opens a folder picker for the library folder.</summary>
    public RelayCommand BrowseRipLibraryFolderCommand { get; }

    /// <summary>
    /// Reads the disc's table of contents (no audio) and asks musicbrainz.org which editions share it; fills
    /// <see cref="ReleaseChoices"/>.
    /// </summary>
    public AsyncRelayCommand LookUpReleasesCommand { get; }

    /// <summary>The most recent "Look up editions" run, so tests can await it.</summary>
    public Task? LookUpReleasesTask { get; private set; }

    /// <summary>Picks the cover image to embed in ripped files.</summary>
    public RelayCommand BrowseRipCoverArtCommand { get; }

    /// <summary>Removes the chosen cover image.</summary>
    public RelayCommand ClearRipCoverArtCommand { get; }

    /// <summary>
    /// Decodes and plays a Red Book audio preview of the currently selected track.
    /// </summary>
    public RelayCommand PreviewSelectedTrackCommand { get; }

    /// <summary>
    /// Stops any active audio track preview playback.
    /// </summary>
    public RelayCommand StopPreviewCommand { get; }

    /// <summary>
    /// Generates and exports a printable CD Jewel Case Front Booklet, Rear Tray Inlay, and Tracklist HTML sheet.
    /// </summary>
    public RelayCommand ExportCoverSheetCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="AudioBurnViewModel"/>.
    /// </summary>
    /// <param name="deviceService">Optional optical device enumeration service.</param>
    /// <param name="burnEngine">Optional optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder implementation.</param>
    /// <param name="settingsService">Optional application settings persistence service.</param>
    public AudioBurnViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null,
        IAudioDecoder? audioDecoder = null,
        AppSettingsService? settingsService = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _audioDecoder = audioDecoder ?? new MediaFoundationAudioDecoder(settingsService?.CurrentSettings.FfmpegExecutablePath);
        _audioBurnService = new AudioCdBurnService(_burnEngine, _audioDecoder);
        _ripperService = new AudioCdRipperService();
        _settingsService = settingsService;

        if (_settingsService != null)
        {
            var s = _settingsService.CurrentSettings;
            _readOffsetSamples = Math.Clamp(s.AudioReadOffsetSamples, -10000, 10000);
            _secureRip = s.SecureAudioRipByDefault;
            _lookupMetadata = s.LookupMusicBrainzByDefault;
            _verifyAccurateRip = s.VerifyAccurateRipByDefault;
            _verifyCtdb = s.VerifyCtdbByDefault;
            LoadRipExportSettings(s);
        }
        else
        {
            _readOffsetSamples = 0;
            _secureRip = false;
            _lookupMetadata = true;
            _verifyAccurateRip = true;
            _verifyCtdb = false;
        }
        _continueOnUnreadable = false;
        _ripReport = string.Empty;

        try
        {
            _ripOutputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "CdBurner_RippedAudio");
        }
        catch
        {
            _ripOutputFolder = Path.Combine(Path.GetTempPath(), "CdBurner_RippedAudio");
        }

        AddAudioFilesCommand = new RelayCommand(p =>
        {
            if (p is IEnumerable<string> paths) AddAudioFiles(paths);
            else if (p is string s && !string.IsNullOrWhiteSpace(s)) AddAudioFile(s);
            else BrowseAndAddFiles();
        });

        RemoveTrackCommand = new RelayCommand(p => RemoveTrack(p as AudioTrackRowViewModel), _ => SelectedTrack != null || Tracks.Count > 0);
        ClearAllTracksCommand = new RelayCommand(_ => ClearAllTracks(), _ => !IsBusy && Tracks.Count > 0);
        MoveTrackUpCommand = new RelayCommand(p => MoveTrackUp(p as AudioTrackRowViewModel), _ => CanMoveTrackUp(SelectedTrack));
        MoveTrackDownCommand = new RelayCommand(p => MoveTrackDown(p as AudioTrackRowViewModel), _ => CanMoveTrackDown(SelectedTrack));
        StartAudioBurnCommand = new AsyncRelayCommand(async p => await StartAudioBurnAsync(p as string), _ => !IsBusy && Tracks.Count > 0);
        BrowseDataSessionFolderCommand = new RelayCommand(BrowseDataSessionFolder);
        ClearDataSessionFolderCommand = new RelayCommand(_ => DataSessionFolderPath = string.Empty);
        BrowseFillPictureCommand = new RelayCommand(BrowseFillPicture);
        ClearFillPictureCommand = new RelayCommand(_ => FillPicturePath = string.Empty);
        CancelBurnCommand = new RelayCommand(_ => CancelActiveBurn(), _ => IsBusy);
        RipAudioCdCommand = new AsyncRelayCommand(async p => await RipAudioCdAsync(p as string), _ => !IsBusy);
        DetectReadOffsetCommand = new AsyncRelayCommand(DetectReadOffsetAsync, () => !IsBusy);
        LookupDiscMetadataCommand = new AsyncRelayCommand(async _ => await LookupDiscMetadataAsync(), _ => !IsBusy);
        BrowseRipOutputFolderCommand = new RelayCommand(BrowseRipOutputFolder);
        BrowseRipLibraryFolderCommand = new RelayCommand(BrowseRipLibraryFolder);
        LookUpReleasesCommand = new AsyncRelayCommand(_ =>
        {
            LookUpReleasesTask = LookUpReleasesAsync();
            return LookUpReleasesTask;
        }, _ => !IsBusy);
        ReleaseChoices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReleaseChoice));
        Convert = new AudioConvertViewModel(_settingsService);
        BrowseRipCoverArtCommand = new RelayCommand(BrowseRipCoverArt);
        ClearRipCoverArtCommand = new RelayCommand(_ => RipCoverArtPath = string.Empty);
        PreviewSelectedTrackCommand = new RelayCommand(p => PreviewTrack(p as AudioTrackRowViewModel), _ => SelectedTrack != null || Tracks.Count > 0);
        StopPreviewCommand = new RelayCommand(_ => StopPreview(), _ => IsPreviewPlaying);
        ExportCoverSheetCommand = new RelayCommand(p => ExportJewelCaseCoverHtml(p as string), _ => Tracks.Count > 0);

        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        // Pre-select physical optical drive if available, otherwise fallback
        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();

        RecalculateTotals();
    }

    #region Track Management Operations

    /// <summary>
    /// Adds a single physical audio file to the compilation.
    /// </summary>
    public void AddAudioFile(string filePath)
    {
        AddAudioFiles(new[] { filePath });
    }

    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".fla", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".m4p", ".raw", ".pcm"
    };

    private static List<string> ExpandAudioPaths(IEnumerable<string> rawPaths)
    {
        var result = new List<string>();
        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (File.Exists(raw))
            {
                result.Add(raw);
            }
            else if (Directory.Exists(raw))
            {
                try
                {
                    var dirFiles = Directory.EnumerateFiles(raw, "*.*", SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var audioInDir = dirFiles.Where(f => SupportedAudioExtensions.Contains(Path.GetExtension(f))).ToList();
                    var cuesInDir = dirFiles.Where(f => string.Equals(Path.GetExtension(f), ".cue", StringComparison.OrdinalIgnoreCase)).ToList();

                    // If a folder is a single-file album rip (1 audio file + 1 .cue sheet), import via the .cue sheet so tracks are split by INDEX 01
                    if (audioInDir.Count == 1 && cuesInDir.Count == 1)
                    {
                        result.Add(cuesInDir[0]);
                    }
                    else if (audioInDir.Count > 0)
                    {
                        result.AddRange(audioInDir);
                    }
                    else if (cuesInDir.Count > 0)
                    {
                        result.AddRange(cuesInDir);
                    }
                }
                catch
                {
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Adds multiple physical audio files (or folders / CUE sheets) to the compilation, skipping and reporting any DRM-protected or unreadable files.
    /// </summary>
    public void AddAudioFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // One shared context for the whole drop, including files pulled in through playlists, so that tracks are
        // re-indexed once, every skipped file lands in a single report, and the final status counts everything.
        var ctx = new AddFilesContext();
        AddAudioFilesCore(filePaths, ctx, depth: 0);
        ReportAddResults(ctx);
    }

    private sealed class AddFilesContext
    {
        public List<string> DrmProtectedFiles { get; } = new();
        public List<(string FilePath, string Reason)> UnreadableFiles { get; } = new();
        public HashSet<string> VisitedPlaylists { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> MixedModeNotices { get; } = new();
        public int AddedCount { get; set; }
        public bool TrackLimitReached { get; set; }
    }

    private const int MaxPlaylistNesting = 4;

    private void AddAudioFilesCore(IEnumerable<string> filePaths, AddFilesContext ctx, int depth)
    {
        var pathList = ExpandAudioPaths(filePaths);
        if (pathList.Count == 0) return;

        var drmProtectedFiles = ctx.DrmProtectedFiles;
        var unreadableFiles = ctx.UnreadableFiles;

        foreach (var path in pathList)
        {
            if (Tracks.Count >= AudioCdCompilation.MaxTracks)
            {
                ctx.TrackLimitReached = true;
                break;
            }

            // Support M3U / M3U8 / PLS Playlists (.m3u, .m3u8, .pls)
            if (AudioPlaylistParser.IsPlaylistFile(path))
            {
                // Guard against playlists that include themselves (directly or via another playlist).
                string fullPlaylistPath;
                try { fullPlaylistPath = Path.GetFullPath(path); } catch { fullPlaylistPath = path; }
                if (depth >= MaxPlaylistNesting || !ctx.VisitedPlaylists.Add(fullPlaylistPath))
                {
                    unreadableFiles.Add((path, "Playlist is nested too deeply or includes itself; skipped."));
                    continue;
                }

                try
                {
                    var playlistEntries = AudioPlaylistParser.ParseFile(path);
                    if (playlistEntries.Count == 0)
                    {
                        unreadableFiles.Add((path, "Playlist contains no valid local audio tracks."));
                        continue;
                    }

                    foreach (var plEntry in playlistEntries)
                    {
                        if (Tracks.Count >= AudioCdCompilation.MaxTracks) break;
                        if (!File.Exists(plEntry.FilePath))
                        {
                            unreadableFiles.Add((plEntry.FilePath, $"Referenced in playlist '{Path.GetFileName(path)}' but file not found."));
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(plEntry.Album) &&
                            (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album"))
                        {
                            AlbumTitle = plEntry.Album;
                        }

                        if (!string.IsNullOrWhiteSpace(plEntry.Performer) &&
                            (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists"))
                        {
                            AlbumPerformer = plEntry.Performer;
                        }

                        int countBefore = Tracks.Count;
                        AddAudioFilesCore(new[] { plEntry.FilePath }, ctx, depth + 1); // counts & reports via ctx
                        if (Tracks.Count == countBefore + 1)
                        {
                            var lastRow = Tracks[^1];
                            if (!string.IsNullOrWhiteSpace(plEntry.Title) &&
                                (string.IsNullOrWhiteSpace(lastRow.Title) || lastRow.Title == Path.GetFileNameWithoutExtension(plEntry.FilePath)))
                            {
                                lastRow.Title = plEntry.Title;
                            }
                            if (!string.IsNullOrWhiteSpace(plEntry.Performer) &&
                                (string.IsNullOrWhiteSpace(lastRow.Performer) || lastRow.Performer == "Various Artists"))
                            {
                                lastRow.Performer = plEntry.Performer;
                            }
                        }
                    }
                }
                catch (Exception plEx)
                {
                    unreadableFiles.Add((path, plEx.Message));
                }
                continue;
            }

            // Support CUE sheets (.cue) referencing .flac, .wav, .mp3, or single-file album images
            if (string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var cueSheet = CueSheetParser.ParseFile(path);
                    bool isMixed = cueSheet.IsMixedMode;
                    int audioTrackCount = cueSheet.Tracks.Count(t => t.IsAudio);
                    int dataTrackCount = cueSheet.Tracks.Count(t => !t.IsAudio);

                    var tempComp = new AudioCdCompilation
                    {
                        AlbumTitle = AlbumTitle,
                        AlbumPerformer = AlbumPerformer,
                        Songwriter = AlbumSongwriter,
                        UpcEan = UpcEanCode,
                        DefaultPreGapFrames = DefaultPreGapFrames
                    };

                    int cueAdded = tempComp.ImportCueSheet(cueSheet, Path.GetDirectoryName(path) ?? string.Empty, _audioDecoder);
                    if (cueAdded == 0)
                    {
                        unreadableFiles.Add((path, "CUE sheet contains no Red Book AUDIO tracks."));
                        continue;
                    }

                    if (isMixed)
                    {
                        bool keptData = tempComp.HasDataTrack && Tracks.Count == 0;
                        int skipped = dataTrackCount - (keptData ? 1 : 0);
                        ctx.MixedModeNotices.Add(keptData
                            ? $"Track 1 of this mixed-mode CUE is a data track: it is kept, so the disc is a mixed-mode CD, which only direct drive commands can write."
                              + (skipped > 0 ? $" {skipped} other data track(s) were left out." : string.Empty)
                            : $"Imported {audioTrackCount} audio track(s) from mixed-mode CUE (skipped {dataTrackCount} data track(s)).");
                    }

                    if ((string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album") &&
                        !string.IsNullOrWhiteSpace(tempComp.AlbumTitle) && tempComp.AlbumTitle != "Untitled Album")
                    {
                        AlbumTitle = tempComp.AlbumTitle;
                    }

                    if ((string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists") &&
                        !string.IsNullOrWhiteSpace(tempComp.AlbumPerformer) && tempComp.AlbumPerformer != "Various Artists")
                    {
                        AlbumPerformer = tempComp.AlbumPerformer;
                    }

                    if (string.IsNullOrWhiteSpace(AlbumSongwriter) && !string.IsNullOrWhiteSpace(tempComp.Songwriter))
                    {
                        AlbumSongwriter = tempComp.Songwriter;
                    }

                    if (string.IsNullOrWhiteSpace(UpcEanCode) && !string.IsNullOrWhiteSpace(tempComp.UpcEan))
                    {
                        UpcEanCode = tempComp.UpcEan;
                    }

                    AudioTrackRowViewModel? previousCueRow = null;
                    foreach (var cueTrk in tempComp.Tracks)
                    {
                        if (Tracks.Count >= AudioCdCompilation.MaxTracks) break;
                        if (cueTrk.IsDataTrack && Tracks.Count > 0) continue; // a data track can only be track 1

                        long trkSize = 0;
                        string trkFormat = Path.GetExtension(cueTrk.SourceFilePath).TrimStart('.').ToUpperInvariant();
                        int trkRate = OpticalConstants.CdAudioSampleRate;
                        int trkBps = OpticalConstants.CdAudioBitsPerSample;
                        int trkCh = OpticalConstants.CdAudioChannels;

                        if (cueTrk.DataTrack is { } dataImage)
                        {
                            trkSize = dataImage.UserDataBytes;
                            trkFormat = dataImage.Mode == 2 ? "Data (Mode 2 XA)" : "Data (Mode 1)";
                        }
                        else if (File.Exists(cueTrk.SourceFilePath))
                        {
                            try
                            {
                                trkSize = new FileInfo(cueTrk.SourceFilePath).Length;
                                var m = _audioDecoder.ProbeAudioFile(cueTrk.SourceFilePath);
                                if (!string.IsNullOrWhiteSpace(m.FormatName)) trkFormat = m.FormatName;
                                if (m.SampleRate > 0) trkRate = m.SampleRate;
                                if (m.BitsPerSample > 0) trkBps = m.BitsPerSample;
                                if (m.Channels > 0) trkCh = m.Channels;
                            }
                            catch
                            {
                            }
                        }

                        int nextNum = Tracks.Count + 1;
                        var cueRow = new AudioTrackRowViewModel
                        {
                            TrackNumber = nextNum,
                            SourceFilePath = cueTrk.SourceFilePath,
                            Title = cueTrk.Title,
                            Performer = cueTrk.Performer,
                            Songwriter = cueTrk.Songwriter ?? AlbumSongwriter,
                            Composer = cueTrk.Composer,
                            Isrc = cueTrk.Isrc,
                            PreGapFrames = nextNum == 1 && cueTrk.PreGapFrames == 0 ? DefaultPreGapFrames : cueTrk.PreGapFrames,
                            DurationFrames = cueTrk.DurationFrames,
                            FileSizeBytes = trkSize,
                            FormatName = trkFormat,
                            SampleRate = trkRate,
                            BitsPerSample = trkBps,
                            Channels = trkCh,
                            CustomPcmStreamFactory = cueTrk.CustomPcmStreamFactory,
                            DataTrack = cueTrk.DataTrack,
                            RawFileType = cueTrk.RawFileType,
                            PreEmphasis = cueTrk.PreEmphasis,
                            CopyPermitted = cueTrk.CopyPermitted,
                            Index00LeadFrames = cueTrk.Index00LeadFrames,
                            Index00Predecessor = cueTrk.Index00LeadFrames > 0 ? previousCueRow : null,
                            HiddenAudioPcmStreamFactory = cueTrk.HiddenAudioPcmStreamFactory,
                            HiddenAudioFrames = cueTrk.HiddenAudioFrames,
                            AlbumSource = cueTrk.AlbumSource,
                            SourceStartFrame = cueTrk.SourceStartFrame,
                            SourceFrameCount = cueTrk.SourceFrameCount
                        };

                        Tracks.Add(cueRow);
                        previousCueRow = cueRow;
                        SelectedTrack = cueRow;
                        ctx.AddedCount++;
                    }
                }
                catch (Exception cueEx)
                {
                    unreadableFiles.Add((path, cueEx.Message));
                }
                continue;
            }

            // 1. Explicit DRM check (.m4p, FairPlay atoms, ASF DRM)
            if (AudioDrmDetector.IsDrmProtected(path, out string drmDescription))
            {
                drmProtectedFiles.Add(path);
                continue;
            }

            long fileSize = 0;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch { }

            string fileName = Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string performer = AlbumPerformer;
            string? songwriter = AlbumSongwriter;
            string? composer = null;
            string? isrc = null;
            string formatName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            int sampleRate = OpticalConstants.CdAudioSampleRate;
            int bitsPerSample = OpticalConstants.CdAudioBitsPerSample;
            int channels = OpticalConstants.CdAudioChannels;
            int durationFrames = 0;

            try
            {
                var meta = _audioDecoder.ProbeAudioFile(path);
                if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title;
                if (!string.IsNullOrWhiteSpace(meta.Artist)) performer = meta.Artist;
                if (!string.IsNullOrWhiteSpace(meta.Songwriter)) songwriter = meta.Songwriter;
                if (!string.IsNullOrWhiteSpace(meta.Composer)) composer = meta.Composer;
                if (!string.IsNullOrWhiteSpace(meta.Isrc)) isrc = meta.Isrc;
                if (!string.IsNullOrWhiteSpace(meta.FormatName)) formatName = meta.FormatName;
                if (meta.SampleRate > 0) sampleRate = meta.SampleRate;
                if (meta.BitsPerSample > 0) bitsPerSample = meta.BitsPerSample;
                if (meta.Channels > 0) channels = meta.Channels;

                if ((string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album") &&
                    !string.IsNullOrWhiteSpace(meta.Album))
                {
                    AlbumTitle = meta.Album;
                }

                string? albumArtistCandidate = !string.IsNullOrWhiteSpace(meta.AlbumArtist) ? meta.AlbumArtist : meta.Artist;
                if ((string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists") &&
                    !string.IsNullOrWhiteSpace(albumArtistCandidate))
                {
                    AlbumPerformer = albumArtistCandidate;
                }

                if (meta.TotalRedBookBytes > 0)
                {
                    durationFrames = OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize);
                }
                else if (meta.Duration > TimeSpan.Zero)
                {
                    durationFrames = (int)Math.Round(meta.Duration.TotalSeconds * OpticalConstants.AudioFramesPerSecond);
                }
                else
                {
                    throw new InvalidDataException("Audio probe reported zero duration.");
                }
            }
            catch (DrmProtectedAudioException)
            {
                drmProtectedFiles.Add(path);
                continue;
            }
            catch (Exception ex)
            {
                string ext = Path.GetExtension(path);
                // Allow raw PCM fallback strictly for headerless .raw / .pcm files
                if ((string.Equals(ext, ".raw", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".pcm", StringComparison.OrdinalIgnoreCase)) && fileSize > 0)
                {
                    durationFrames = OpticalConstants.BytesToSectors(fileSize, OpticalConstants.RawSectorSize);
                }
                else
                {
                    unreadableFiles.Add((path, ex.Message));
                    continue;
                }
            }

            int trackNum = Tracks.Count + 1;
            var row = new AudioTrackRowViewModel
            {
                TrackNumber = trackNum,
                SourceFilePath = path,
                Title = title,
                Performer = performer,
                Songwriter = songwriter,
                Composer = composer,
                Isrc = isrc,
                PreGapFrames = trackNum == 1 ? DefaultPreGapFrames : 0,
                DurationFrames = durationFrames,
                FileSizeBytes = fileSize,
                FormatName = formatName,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                Channels = channels
            };

            Tracks.Add(row);
            SelectedTrack = row;
            ctx.AddedCount++;
        }
    }

    /// <summary>
    /// Re-indexes once and publishes a single status line / dialog for everything a drop added or skipped.
    /// </summary>
    private void ReportAddResults(AddFilesContext ctx)
    {
        var drmProtectedFiles = ctx.DrmProtectedFiles;
        var unreadableFiles = ctx.UnreadableFiles;
        int addedCount = ctx.AddedCount;

        if (addedCount > 0)
        {
            ReindexTracks();
        }

        // The status line always reports what was added AND what was skipped, so a playlist with
        // 10 good tracks and 1 missing file reads "Added 10 ... Skipped 1 ..." rather than only the failure.
        var parts = new List<string>();
        if (ctx.MixedModeNotices.Count > 0)
        {
            parts.AddRange(ctx.MixedModeNotices);
        }
        else if (addedCount > 0)
        {
            parts.Add($"Added {addedCount} audio track(s). Total: {Tracks.Count}.");
        }
        if (addedCount > 0 && ctx.MixedModeNotices.Count > 0)
        {
            parts.Add($"Total: {Tracks.Count}.");
        }
        if (drmProtectedFiles.Count > 0) parts.Add($"Skipped {drmProtectedFiles.Count} DRM-protected Apple Music file(s). Use DRM-free tracks.");
        if (unreadableFiles.Count > 0) parts.Add($"Skipped {unreadableFiles.Count} unsupported, missing or corrupt audio file(s).");
        if (ctx.TrackLimitReached) parts.Add($"Audio CD compilation has reached the Red Book limit of {AudioCdCompilation.MaxTracks} tracks.");
        if (parts.Count > 0)
        {
            StatusMessage = string.Join(" ", parts);
        }

        // Inform user about skipped DRM or corrupted files
        if (drmProtectedFiles.Count > 0)
        {
            string fileList = string.Join("\n", drmProtectedFiles.Take(6).Select(p => $"  • {Path.GetFileName(p)}"));
            if (drmProtectedFiles.Count > 6)
            {
                fileList += $"\n  ...and {drmProtectedFiles.Count - 6} more file(s)";
            }

            string warning = Strings.Format(nameof(Strings.DialogDrmSkippedBody), drmProtectedFiles.Count, fileList);

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    warning,
                    Strings.DialogDrmSkippedTitle,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        if (unreadableFiles.Count > 0)
        {
            string failList = string.Join("\n", unreadableFiles.Take(6).Select(f => $"  • {Path.GetFileName(f.FilePath)}: {f.Reason}"));
            if (unreadableFiles.Count > 6)
            {
                failList += $"\n  ...and {unreadableFiles.Count - 6} more file(s)";
            }

            string addedNote = addedCount > 0 ? $"{addedCount} track(s) were added. " : string.Empty;
            string errorMsg = Strings.Format(nameof(Strings.DialogUnsupportedFilesBody), addedNote, unreadableFiles.Count, failList);

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    errorMsg,
                    Strings.DialogUnsupportedFilesTitle,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Removes a track from the compilation and re-indexes remaining tracks sequentially.
    /// </summary>
    public void RemoveTrack(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target != null && Tracks.Remove(target))
        {
            ReindexTracks();
            SelectedTrack = Tracks.FirstOrDefault();
        }
    }

    /// <summary>
    /// Removes all audio tracks from the compilation to allow burning consecutive CDs rapidly.
    /// </summary>
    public void ClearAllTracks()
    {
        if (Tracks.Count == 0) return;

        Tracks.Clear();
        SelectedTrack = null;
        RecalculateTotals();
        MoveTrackUpCommand.RaiseCanExecuteChanged();
        MoveTrackDownCommand.RaiseCanExecuteChanged();
        RemoveTrackCommand.RaiseCanExecuteChanged();
        ClearAllTracksCommand.RaiseCanExecuteChanged();
        StartAudioBurnCommand.RaiseCanExecuteChanged();
        StatusMessage = Strings.StatusAudioBurnClearedAllTracksFromCompilation;
    }

    /// <summary>
    /// Moves the specified track up one position in the track order.
    /// </summary>
    public void MoveTrackUp(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return;

        int index = Tracks.IndexOf(target);
        if (index > 0)
        {
            Tracks.Move(index, index - 1);
            ReindexTracks();
            SelectedTrack = target;
        }
    }

    /// <summary>
    /// Moves the specified track down one position in the track order.
    /// </summary>
    public void MoveTrackDown(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return;

        int index = Tracks.IndexOf(target);
        if (index >= 0 && index < Tracks.Count - 1)
        {
            Tracks.Move(index, index + 1);
            ReindexTracks();
            SelectedTrack = target;
        }
    }

    private bool CanMoveTrackUp(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return false;
        int index = Tracks.IndexOf(target);
        return index > 0;
    }

    private bool CanMoveTrackDown(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return false;
        int index = Tracks.IndexOf(target);
        return index >= 0 && index < Tracks.Count - 1;
    }

    /// <summary>
    /// Re-indexes all tracks sequentially from 1 to Count and recalculates compilation metrics.
    /// </summary>
    public void ReindexTracks()
    {
        for (int i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].TrackNumber = i + 1;
        }

        if (Tracks.Count > 0 && Tracks[0].PreGapFrames == 0)
        {
            Tracks[0].PreGapFrames = DefaultPreGapFrames;
        }

        RecalculateTotals();
        MoveTrackUpCommand.RaiseCanExecuteChanged();
        MoveTrackDownCommand.RaiseCanExecuteChanged();
        RemoveTrackCommand.RaiseCanExecuteChanged();
        ClearAllTracksCommand.RaiseCanExecuteChanged();
        StartAudioBurnCommand.RaiseCanExecuteChanged();
        PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
        ExportCoverSheetCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Recalculates total duration and disc capacity usage percentage.
    /// </summary>
    public void RecalculateTotals()
    {
        int totalFrames = Tracks.Sum(t => t.DurationFrames);
        TotalDuration = MsfAddress.FromFrameCount(totalFrames);

        int maxFrames = OpticalConstants.Cd700MbSectors; // 360,000 frames (80-min CD-R)
        var mediaStatus = _selectedRecorder?.QueryMediaStatus();
        if (mediaStatus != null && mediaStatus.TotalSectors > 0)
        {
            maxFrames = mediaStatus.TotalSectors;
        }

        long effectiveFrames = totalFrames;
        if (HasDataSessionFolder)
        {
            if (Directory.Exists(_dataSessionFolderPath))
            {
                try
                {
                    var plan = AudioCdBurnService.PlanDataSessionFolder(_dataSessionFolderPath, _dataSessionVolumeLabel, totalFrames,
                        _blueBookLayout ? new BlueBookDiscInfo(AlbumTitle, AlbumPerformer, Array.Empty<BlueBookTrackInfo>()) : null);
                    string layoutNote = _blueBookLayout ? " Enhanced CD layout: CD-ROM XA Mode 2 with CDPLUS/INFO.CDP and PICTURES/." : string.Empty;
                    effectiveFrames = plan.TotalDiscSectorsRequired;
                    string shortNote = plan.ShortenedNamesWarning != null ? $" {plan.ShortenedNamesWarning}" : string.Empty;
                    string fitNote = maxFrames > 0 && plan.TotalDiscSectorsRequired > maxFrames
                        ? $" Exceeds disc capacity ({maxFrames:N0} sectors)."
                        : string.Empty;
                    DataSessionSummaryText =
                        $"Session 2 data folder: {plan.FileCount:N0} file(s), {plan.PaddedTrackSectors:N0} sectors + 11,400-sector session gap.{layoutNote}{shortNote}{fitNote} {AudioRecoverySessionService.CarHeadUnitWarning}";
                }
                catch (Exception ex)
                {
                    DataSessionSummaryText = $"Session 2 data folder issue: {ex.Message}";
                }
            }
            else
            {
                DataSessionSummaryText = $"Session 2 data folder not found: '{_dataSessionFolderPath}'.";
            }
        }
        else
        {
            DataSessionSummaryText = string.Empty;
        }

        DiscUsagePercent = maxFrames > 0 ? ((double)effectiveFrames / maxFrames * 100.0) : 0.0;
        UpdateRecoveryEstimate(totalFrames, maxFrames);

        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(DiscUsageText));
        OnPropertyChanged(nameof(TotalTracksText));
        OnPropertyChanged(nameof(HasTracks));
    }

    private void BrowseDataSessionFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Session 2 Data Folder for Enhanced CD (CD-Extra)",
            InitialDirectory = Directory.Exists(DataSessionFolderPath)
                ? DataSessionFolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            DataSessionFolderPath = dlg.FolderName;
        }
    }

    private void BrowseFillPicture(object? _)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the picture for the empty disc space",
            Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            FillPicturePath = dlg.FileName;
        }
    }

    /// <summary>
    /// The picture session for a burn with <see cref="FillWithPicture"/>: the chosen picture, or the automatic one. Returns
    /// (null, note) when the automatic picture finds nothing (the disc is burned without it); throws
    /// <see cref="ArgumentException"/> when the chosen picture cannot be read.
    /// </summary>
    internal async Task<(CdBurner.Lithography.DiscArt.AudioCdPictureSession? Session, string? Note)> PrepareFillPictureAsync(
        AudioCdCompilation compilation, CancellationToken ct)
    {
        CdBurner.Lithography.DiscArt.AudioCdPicture? picture;
        string? note = null;
        if (!string.IsNullOrWhiteSpace(_fillPicturePath))
        {
            picture = CdBurner.Lithography.DiscArt.AudioCdFillPicture.LoadChosen(_fillPicturePath);
        }
        else
        {
            HttpClient? owned = null;
            try
            {
                HttpClient? http = _fillPictureLookupOnline
                    ? FillPictureHttpClient ?? (owned = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    : null;
                (picture, note) = await CdBurner.Lithography.DiscArt.AudioCdFillPicture.FindAutoPictureAsync(
                    compilation, _audioDecoder, http, cancellationToken: ct).ConfigureAwait(true);
            }
            finally
            {
                owned?.Dispose();
            }
        }
        return picture == null
            ? (null, note)
            : (new CdBurner.Lithography.DiscArt.AudioCdPictureSession(picture, BlueBookDiscInfo.From(compilation)), null);
    }

    private void UpdateRecoveryEstimate(long audioFrames, long capacityFrames)
    {
        var est = AudioRecoverySessionService.EstimateCapacity(audioFrames, capacityFrames);
        _recoveryEstimate = est;
        OnPropertyChanged(nameof(RecoveryEstimate));
        RecoverySessionCapacityText = audioFrames <= 0
            ? "Session 2 overhead: ~2.5 min (11,400 sectors). Add tracks to calculate PAR2 recovery budget."
            : $"Session 2 overhead: ~2.5 min ({est.OverheadSectors:N0} sectors). {est.ToleranceSummary}";
    }

    private void BrowseAndAddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add Audio Tracks, Playlists, or CUE Sheets to Audio CD Compilation",
            Filter = "Supported Audio, Playlists & CUE (*.flac;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.cue;*.m3u;*.m3u8;*.pls)|*.flac;*.fla;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.cue;*.m3u;*.m3u8;*.pls|FLAC Lossless Audio (*.flac;*.fla)|*.flac;*.fla|WAV Audio (*.wav)|*.wav|MP3 Audio (*.mp3)|*.mp3|Playlists (*.m3u;*.m3u8;*.pls)|*.m3u;*.m3u8;*.pls|CUE Sheet (*.cue)|*.cue|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            AddAudioFiles(dlg.FileNames);
        }
    }

    /// <summary>
    /// Decodes and plays a Red Book audio preview of the specified or currently selected track.
    /// </summary>
    public void PreviewTrack(AudioTrackRowViewModel? track = null) => _ = PreviewTrackAsync(track);

    /// <summary>
    /// Decodes the first 30 seconds of a track on a background thread (a single-file album or a long hi-res FLAC can take
    /// seconds to decode, which used to freeze the window), then plays it. <see cref="IsPreviewPlaying"/> returns to false
    /// when the clip finishes on its own, when <see cref="StopPreview"/> is called, or when another preview starts.
    /// </summary>
    public async Task PreviewTrackAsync(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack ?? Tracks.FirstOrDefault();
        if (target == null)
        {
            PreviewStatusText = "No track selected to preview.";
            return;
        }

        StopPreview();

        if (target.IsDataTrack)
        {
            PreviewStatusText = $"Track {target.TrackNumber} is a data track: there is no audio to preview.";
            return;
        }

        bool hasSource = target.CustomPcmStreamFactory != null ||
                         (!string.IsNullOrWhiteSpace(target.SourceFilePath) && File.Exists(target.SourceFilePath));
        if (!hasSource)
        {
            PreviewStatusText = $"Cannot preview Track {target.TrackNumber}: source file not found.";
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        PreviewStatusText = $"Preparing preview of Track {target.TrackNumber:D2}…";

        bool leveling = UseRmsLoudnessLeveling;
        bool normalize = NormalizeAllTracks || target.NormalizeAudio;

        try
        {
            byte[] pcmBytes = await Task.Run(() => DecodePreviewPcm(target, leveling, normalize, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_previewCts, cts))
            {
                return; // superseded by StopPreview or a newer preview
            }

            _previewWavStream = new MemoryStream(44 + pcmBytes.Length);
            WavFileCodec.WriteWavHeader(_previewWavStream, pcmBytes.Length);
            _previewWavStream.Write(pcmBytes, 0, pcmBytes.Length);
            _previewWavStream.Position = 0;

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                _soundPlayer = new System.Media.SoundPlayer(_previewWavStream);
                _soundPlayer.Play();
            }

            IsPreviewPlaying = true;
            PreviewStatusText = $"🔊 Playing Track {target.TrackNumber:D2}: {target.Title} ({target.FormatSummary})";
            StatusMessage = PreviewStatusText;

            double clipSeconds = (double)pcmBytes.Length / (OpticalConstants.CdAudioSampleRate * OpticalConstants.CdAudioBytesPerSample);
            _ = ResetPreviewWhenClipEndsAsync(TimeSpan.FromSeconds(clipSeconds), cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                IsPreviewPlaying = false;
                PreviewStatusText = $"Preview failed: {ex.Message}";
                StatusMessage = PreviewStatusText;
            }
        }
    }

    private byte[] DecodePreviewPcm(AudioTrackRowViewModel target, bool leveling, bool normalize, CancellationToken ct)
    {
        Stream pcmStream = target.CustomPcmStreamFactory != null
            ? target.CustomPcmStreamFactory()
            : _audioDecoder.OpenRedBookPcmStream(target.SourceFilePath);

        using (pcmStream)
        {
            ct.ThrowIfCancellationRequested();

            // Cap preview buffer to first 30 seconds (5.29 MB) for snappy playback
            const int maxPreviewBytes = 30 * OpticalConstants.CdAudioSampleRate * OpticalConstants.CdAudioBytesPerSample;
            using var cappedPcm = new MemoryStream();
            byte[] buf = new byte[16384];
            int remaining = maxPreviewBytes;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int r = pcmStream.Read(buf, 0, Math.Min(buf.Length, remaining));
                if (r <= 0) break;
                cappedPcm.Write(buf, 0, r);
                remaining -= r;
            }

            byte[] pcmBytes = cappedPcm.ToArray();
            if (leveling)
            {
                pcmBytes = RedBookPcmDspProcessor.NormalizeRmsLoudness(pcmBytes);
            }
            else if (normalize)
            {
                pcmBytes = RedBookPcmDspProcessor.NormalizePcm(pcmBytes);
            }

            return pcmBytes;
        }
    }

    private async Task ResetPreviewWhenClipEndsAsync(TimeSpan clipLength, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(clipLength + TimeSpan.FromMilliseconds(250), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_previewCts, cts) && IsPreviewPlaying)
        {
            ReleasePreviewPlayer();
            IsPreviewPlaying = false;
            PreviewStatusText = "Preview finished.";
        }
    }

    /// <summary>
    /// Stops any active audio track preview playback.
    /// </summary>
    public void StopPreview()
    {
        try
        {
            _previewCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _previewCts = null;

        ReleasePreviewPlayer();

        if (IsPreviewPlaying)
        {
            IsPreviewPlaying = false;
            PreviewStatusText = "Preview stopped.";
            StatusMessage = Strings.StatusAudioBurnAudioPreviewStopped;
        }
    }

    private void ReleasePreviewPlayer()
    {
        try
        {
            _soundPlayer?.Stop();
            _soundPlayer?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _soundPlayer = null;
        }

        try
        {
            _previewWavStream?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _previewWavStream = null;
        }
    }

    /// <summary>
    /// Builds an <see cref="AudioCdCompilation"/> snapshot from the current tracks and CD-Text settings.
    /// </summary>
    public AudioCdCompilation BuildCurrentCompilation()
    {
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = AlbumTitle,
            AlbumPerformer = AlbumPerformer,
            Songwriter = AlbumSongwriter,
            UpcEan = UpcEanCode,
            RecordingMode = RecordingMode,
            NormalizeAllTracks = NormalizeAllTracks,
            UseRmsLoudnessLeveling = UseRmsLoudnessLeveling,
            LoudnessNormalization = !NormalizeLoudness
                ? LoudnessNormalizationMode.Off
                : LoudnessPerAlbum ? LoudnessNormalizationMode.PerAlbum : LoudnessNormalizationMode.PerTrack,
            CrossfadeSeconds = CrossfadeSeconds,
            DefaultPreGapFrames = DefaultPreGapFrames,
            IncludeCdText = IncludeCdText
        };

        for (int i = 0; i < Tracks.Count; i++)
        {
            var row = Tracks[i];
            compilation.AddTrack(new AudioCdTrackItem(
                TrackNumber: row.TrackNumber,
                SourceFilePath: row.SourceFilePath,
                Title: row.Title,
                Performer: !string.IsNullOrEmpty(row.Performer) ? row.Performer : AlbumPerformer,
                Isrc: row.Isrc,
                PreGapFrames: row.PreGapFrames,
                NormalizeAudio: row.NormalizeAudio,
                CustomPcmStreamFactory: row.CustomPcmStreamFactory
            )
            {
                Songwriter = row.Songwriter ?? AlbumSongwriter,
                Composer = row.Composer,
                DurationFrames = row.DurationFrames,
                SourceStartFrame = row.SourceStartFrame,
                SourceFrameCount = row.SourceFrameCount,
                DataTrack = row.DataTrack,
                RawFileType = row.RawFileType,
                PreEmphasis = row.PreEmphasis,
                CopyPermitted = row.CopyPermitted,
                // A gap mark stays only while the track it came after is still directly in front of it.
                Index00LeadFrames = i > 0 && row.Index00Predecessor != null && ReferenceEquals(Tracks[i - 1], row.Index00Predecessor)
                    ? row.Index00LeadFrames : 0,
                HiddenAudioPcmStreamFactory = row.HiddenAudioPcmStreamFactory,
                HiddenAudioFrames = row.HiddenAudioFrames,
                AlbumSource = row.AlbumSource
            });
        }

        return compilation;
    }

    /// <summary>
    /// Generates and saves a printable CD Jewel Case Booklet, Rear Tray Inlay, and Tracklist HTML file.
    /// </summary>
    public string? ExportJewelCaseCoverHtml(string? outputHtmlPath = null)
    {
        if (Tracks.Count == 0)
        {
            StatusMessage = Strings.StatusAudioBurnAddAtLeastOneTrackBefore;
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputHtmlPath))
        {
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Export Printable CD Jewel Case Cover & Tracklist",
                    Filter = "Printable HTML Cover & Tracklist (*.html)|*.html",
                    FileName = $"{SanitizeFilePart(AlbumTitle)}_JewelCaseCover.html"
                };
                if (sfd.ShowDialog() != true || string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    return null;
                }
                outputHtmlPath = sfd.FileName;
            }
            else
            {
                outputHtmlPath = Path.Combine(Path.GetTempPath(), $"{SanitizeFilePart(AlbumTitle)}_JewelCaseCover.html");
            }
        }

        var compilation = BuildCurrentCompilation();
        JewelCaseCoverGenerator.SavePrintableCoverHtml(compilation, outputHtmlPath);
        LastExportedCoverHtmlPath = outputHtmlPath;
        StatusMessage = $"Exported printable CD jewel case cover & tracklist to '{Path.GetFileName(outputHtmlPath)}'.";
        return outputHtmlPath;
    }

    private static string SanitizeFilePart(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "AudioCD";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private void BrowseRipOutputFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Ripped Audio Tracks",
            InitialDirectory = Directory.Exists(RipOutputFolder) ? RipOutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            RipOutputFolder = dlg.FolderName;
        }
    }

    private void BrowseRipLibraryFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Library Folder for Ripped Albums",
            InitialDirectory = Directory.Exists(RipLibraryFolder) ? RipLibraryFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            RipLibraryFolder = dlg.FolderName;
        }
    }

    private void BrowseRipCoverArt()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Cover Image to Embed in Ripped Tracks",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.gif;*.webp|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            RipCoverArtPath = dlg.FileName;
        }
    }

    #endregion

    #region Burning & Ripping Operations

    /// <summary>
    /// Starts burning the Audio CD compilation to the specified or selected optical recorder.
    /// </summary>
    public async Task<BurnOperationResult> StartAudioBurnAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (Tracks.Count == 0)
        {
            StatusMessage = Strings.StatusAudioBurnNoAudioTracksInCompilationTo;
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        if (string.IsNullOrWhiteSpace(driveId) && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            var confirmed = CdBurner.App.Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                AvailableRecorders,
                SelectedRecorder,
                $"Burn Audio CD ({Tracks.Count} Tracks)");

            if (confirmed == null)
            {
                StatusMessage = Strings.StatusAudioBurnAudioCdBurnCancelledByUser;
                var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
                LastBurnResult = cancelled;
                return cancelled;
            }

            SelectedRecorder = confirmed;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = Strings.StatusAudioBurnNoOpticalRecorderSelectedOrAvailable;
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        // Auto-insert blank CD-R into virtual drive if empty tray
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
            }
        }

        _activeBurnCts?.Dispose();
        _activeBurnCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var activeToken = _activeBurnCts.Token;

        IsBusy = true;
        CancelBurnCommand.RaiseCanExecuteChanged();
        BurnProgress = 0;
        StatusMessage = $"Preparing Audio CD compilation for {recorder.UniqueId}...";

        try
        {
            StopPreview();
            var compilation = BuildCurrentCompilation();

            var progress = new ClosableProgress<BurnProgressSnapshot>(p =>
            {
                if (!_isBusy) return; // Prevent stale background progress from overwriting final status
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            // Direct drive commands only for drives whose probe shows Session-at-Once. When they are on only as the
            // app's default, a drive without it burns with the Windows writer instead of refusing.
            bool direct = UseDirectDriveCommands || IncludeRecoverySession || HasDataSessionFolder || FillWithPicture;
            string? writerNote = null;
            if (direct && _directIsAppDefault && !IncludeRecoverySession && !HasDataSessionFolder && !FillWithPicture && !recorder.GetCapabilities().SupportsNativeSao)
            {
                direct = false;
                writerNote = "This drive does not report Session-at-Once writing, so the Windows writer burned the disc (no CD-Text, track codes or exact gaps).";
            }
            CdBurner.Hardware.NativeWriting.NativeAudioBurnEngine? directEngine = null;
            CdBurner.Hardware.NativeWriting.NativeDataBurnEngine? directDataEngine = null;
            LastDirectBurnResult = null;
            LastDirectBurnLogPath = null;
            if (direct && CodeProblem() is { } codeProblem)
            {
                StatusMessage = $"Burn not started: {codeProblem}";
                var refused = BurnOperationResult.Failed(StatusMessage);
                LastBurnResult = refused;
                return refused;
            }
            if (direct)
            {
                if (!recorder.GetCapabilities().SupportsNativeSao)
                {
                    StatusMessage = Strings.StatusBurnFailedNoSessionAtOnce;
                    var unsupported = BurnOperationResult.Failed(StatusMessage);
                    LastBurnResult = unsupported;
                    return unsupported;
                }
                directEngine = new CdBurner.Hardware.NativeWriting.NativeAudioBurnEngine(DirectBurnLogDirectory) { TestWriteFirst = TestWriteFirst };
                directDataEngine = new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine, DirectBurnLogDirectory);
            }

            CdBurner.Lithography.DiscArt.AudioCdPictureSession? pictureSession = null;
            string? pictureNote = null;
            if (FillWithPicture)
            {
                try
                {
                    StatusMessage = "Finding the picture for the empty disc space...";
                    (pictureSession, pictureNote) = await PrepareFillPictureAsync(compilation, activeToken);
                }
                catch (ArgumentException ex)
                {
                    StatusMessage = $"Burn not started: {ex.Message}";
                    var refused = BurnOperationResult.Failed(StatusMessage);
                    LastBurnResult = refused;
                    return refused;
                }
            }

            AudioBurnOptions Options(bool allowDamagedAudio) => new()
            {
                UseDiscAtOnce = (RecordingMode == AudioRecordingMode.DiscAtOnceGapless),
                WriteCdText = IncludeCdText,
                PerformNormalization = NormalizeAllTracks,
                PreGapFrames = DefaultPreGapFrames,
                Speed = SelectedWriteSpeed,
                KeepIndexMarks = direct,
                AllowDamagedAudio = allowDamagedAudio,
                IncludeRecoverySession = IncludeRecoverySession,
                DataSessionFolderPath = HasDataSessionFolder ? DataSessionFolderPath : null,
                DataSessionVolumeLabel = DataSessionVolumeLabel,
                BlueBookLayout = HasDataSessionFolder && BlueBookLayout,
                SessionTwoImage = pictureSession
            };

            var burnService = directEngine != null ? new AudioCdBurnService(directEngine, _audioDecoder, directDataEngine) : _audioBurnService;
            var burnResult = await burnService.BurnAsync(recorder, compilation, Options(allowDamagedAudio: false), progress, activeToken);

            // A damaged FLAC stops the burn before the drive is touched; the user may accept the damage and go on.
            if (burnResult.DamagedAudioFiles is { Count: > 0 } && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var answer = System.Windows.MessageBox.Show(
                    Strings.Format(nameof(Strings.DialogDamagedAudioBody), burnResult.ErrorMessage),
                    Strings.DialogDamagedAudioTitle,
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);
                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    burnResult = await burnService.BurnAsync(recorder, compilation, Options(allowDamagedAudio: true), progress, activeToken);
                }
                else
                {
                    progress.Close();
                    StatusMessage = $"Burn not started: {burnResult.ErrorMessage}";
                    LastBurnResult = burnResult;
                    return burnResult;
                }
            }
            progress.Close(); // a late progress report must not overwrite the final status line
            LastBurnResult = burnResult;
            LastDirectBurnResult = directEngine?.LastResult;
            LastDirectBurnLogPath = directEngine?.LastLogPath;

            if (burnResult.Success && direct)
            {
                StatusMessage = pictureSession?.StartLba != null && burnService.LastDataSessionSectors > 0
                    ? $"Audio CD burned and read back correctly; the picture fills the rest of the disc in a hidden second session (LBA {pictureSession.PictureStartLba:N0} to {pictureSession.PictureEndLba - 1:N0}, {pictureSession.Picture.Source}), {burnResult.TotalTime}."
                    : burnService.LastDataSessionSectors > 0
                    ? $"Enhanced CD burned ({burnResult.SectorsWritten:N0} total sectors, session 2 data at LBA {burnService.LastDataSessionStartLba:N0}, {burnResult.TotalTime})."
                    : burnService.LastRecoveryManifest is { } recManifest
                        ? $"Audio CD + PAR2 recovery session burned ({burnResult.SectorsWritten:N0} frames, {recManifest.Par2RecoveryBlocks:N0} recovery blocks, {burnResult.TotalTime})."
                        : $"Audio CD burned with direct drive commands and read back correctly ({burnResult.SectorsWritten:N0} frames, {burnResult.TotalTime}).";
                if (pictureNote != null)
                {
                    StatusMessage += " " + pictureNote; // automatic picture found nothing: burned without it
                }
                BurnProgress = 100.0;
                if (directEngine?.LastWriteOffsetPlan is { } offsetPlan)
                {
                    // Say whether the audio sits sample-exact for offset-corrected rippers, or why not yet.
                    StatusMessage += " " + offsetPlan.Explanation;
                }
                if (directEngine?.LastResult?.Verification?.Warning is { } readWarning)
                {
                    // Right audio, but parts of the disc needed careful re-reads: say so before it goes in a car.
                    StatusMessage += " " + readWarning;
                    if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
                    {
                        System.Windows.MessageBox.Show(
                            Strings.Format(nameof(Strings.DialogHardToReadBody), readWarning),
                            Strings.DialogHardToReadTitle,
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
            else if (burnResult.Success)
            {
                StatusMessage = $"Audio CD burned successfully ({burnResult.SectorsWritten:N0} frames, {burnResult.TotalTime}).";
                bool hasDiscMetadata = (compilation.AlbumTitle is { Length: > 0 } title && title != "Untitled Album")
                    || compilation.Tracks.Any(t => !string.IsNullOrWhiteSpace(t.Isrc));
                if (!recorder.IsVirtual && IncludeCdText && hasDiscMetadata)
                {
                    // Windows' burning API (IMAPIv2) cannot write lead-in CD-Text or ISRC codes; be explicit about it.
                    StatusMessage += " Note: CD-Text and ISRC metadata are not written to physical discs in this version (they are kept in the project and .cue files).";
                }
                BurnProgress = 100.0;
                if (writerNote != null)
                {
                    StatusMessage += " " + writerNote;
                }
            }
            if (burnResult.Success && !(direct && directEngine?.LastResult?.Verification?.Warning != null))
            {
                // A failed burn and a hard-to-read warning already show their own message box.
                CompletionNotice.Show("Audio CD finished", StatusMessage, success: true);
            }
            else
            {
                StatusMessage = $"Burn failed: {burnResult.ErrorMessage}";
                if (direct && directEngine?.LastResult is { StoppedBeforeWriting: true } && !TestWriteFirst)
                {
                    // Stopped before the laser came on: the same disc can still be burned the standard way.
                    StatusMessage += Strings.StatusDiscUnchangedSwitchOffDirect;
                }
                if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
                {
                    System.Windows.MessageBox.Show(
                        Strings.Format(nameof(Strings.DialogBurnFailedBody), burnResult.ErrorMessage),
                        Strings.DialogBurnFailedTitle,
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            }

            return burnResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusAudioBurnAudioCdBurnCancelledByUser;
            var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
            LastBurnResult = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Burn error: {ex.Message}";
            var fail = BurnOperationResult.Failed(ex.Message);
            LastBurnResult = fail;
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    Strings.Format(nameof(Strings.DialogBurnErrorBody), ex.Message),
                    Strings.DialogBurnErrorTitle,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            return fail;
        }
        finally
        {
            _activeBurnCts?.Dispose();
            _activeBurnCts = null;
            IsBusy = false;
            CancelBurnCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Cancels any actively executing Audio CD burn operation.
    /// </summary>
    public void CancelActiveBurn()
    {
        if (_activeBurnCts != null && !_activeBurnCts.IsCancellationRequested)
        {
            StatusMessage = Strings.StatusAudioBurnCancellingAudioCdBurn;
            try
            {
                _activeBurnCts.Cancel();
            }
            catch
            {
            }
        }
    }

    private bool _applyingSettings;

    /// <summary>
    /// Applies saved rip options (called after the settings file is loaded at startup) without saving them back.
    /// </summary>
    public void ApplyRipSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applyingSettings = true;
        try
        {
            ReadOffsetSamples = settings.AudioReadOffsetSamples;
            SecureRip = settings.SecureAudioRipByDefault;
            LookupMetadata = settings.LookupMusicBrainzByDefault;
            VerifyAccurateRip = settings.VerifyAccurateRipByDefault;
            VerifyCtdb = settings.VerifyCtdbByDefault;
            LoadRipExportSettings(settings);
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>Reads the format, MP3 quality, library folder and naming settings (an older file without them gives the defaults).</summary>
    private void LoadRipExportSettings(AppSettings settings)
    {
        bool wasApplying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            var formats = new HashSet<AudioRipFormat>();
            foreach (string token in (settings.RipFormats ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (AudioRipFormats.TryParse(token, out AudioRipFormat format))
                {
                    formats.Add(format);
                }
            }

            if (formats.Count == 0)
            {
                formats.Add(AudioRipFormat.Wav);
            }

            RipAsWav = formats.Contains(AudioRipFormat.Wav);
            RipAsFlac = formats.Contains(AudioRipFormat.Flac);
            RipAsMp3 = formats.Contains(AudioRipFormat.Mp3);
            RipAsOpus = formats.Contains(AudioRipFormat.Opus);
            RipAsAac = formats.Contains(AudioRipFormat.Aac);
            SelectedMp3Quality = AudioTargetChoices.Mp3Qualities.Contains(settings.Mp3Quality) ? settings.Mp3Quality : AudioTargetChoices.DefaultMp3Quality;
            RipLibraryFolder = settings.RipLibraryFolder ?? string.Empty;
            RipNamingTemplate = string.IsNullOrWhiteSpace(settings.RipNamingTemplate) ? AudioNamingTemplate.Default : settings.RipNamingTemplate;
            FetchCoverArt = settings.FetchCoverArtByDefault;
            WriteFolderImage = settings.WriteFolderImage;
        }
        finally
        {
            _applyingSettings = wasApplying;
        }
    }

    /// <summary>
    /// Persists rip settings back to the configuration file if an <see cref="AppSettingsService"/> was supplied.
    /// </summary>
    private void SaveSettingsIfAvailable()
    {
        if (_settingsService == null || _applyingSettings) return;
        try
        {
            var settings = _settingsService.CurrentSettings;
            settings.AudioReadOffsetSamples = _readOffsetSamples;
            settings.SecureAudioRipByDefault = _secureRip;
            settings.LookupMusicBrainzByDefault = _lookupMetadata;
            settings.VerifyAccurateRipByDefault = _verifyAccurateRip;
            settings.VerifyCtdbByDefault = _verifyCtdb;
            settings.RipFormats = string.Join(",", CheckedRipFormats().Select(f => AudioRipFormats.Extension(f).TrimStart('.')));
            settings.Mp3Quality = _selectedMp3Quality;
            settings.RipLibraryFolder = _ripLibraryFolder;
            settings.RipNamingTemplate = _ripNamingTemplate;
            settings.FetchCoverArtByDefault = _fetchCoverArt;
            settings.WriteFolderImage = _writeFolderImage;
            _settingsService.CurrentSettings = settings;
            _settingsService.Save();
        }
        catch
        {
            // Ignore settings persistence exceptions in UI
        }
    }

    /// <summary>
    /// Measures the optical drive's audio read offset using an inserted CD matched against AccurateRip.
    /// </summary>
    /// <summary>
    /// Queries MusicBrainz for the inserted Audio CD's table-of-contents DiscID and displays the release metadata.
    /// </summary>
    public async Task<CdTextAlbumMetadata?> LookupDiscMetadataAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        var recorder = ResolveRecorder();
        if (recorder == null)
        {
            StatusMessage = Strings.StatusAudioBurnNoOpticalRecorderSelectedOrAvailable;
            RipReport = "MusicBrainz lookup failed: No optical recorder selected or available.";
            return null;
        }

        IsBusy = true;
        StatusMessage = $"Looking up Audio CD in {recorder.UniqueId} on MusicBrainz...";
        RipReport = string.Empty;

        try
        {
            var ids = DiscIdCalculator.CalculateForRecorder(recorder);
            if (ids == null)
            {
                StatusMessage = Strings.StatusAudioBurnNoAudioTracksFoundOnInserted;
                RipReport = "MusicBrainz lookup failed: No audio tracks found on the inserted disc.";
                return null;
            }

            bool ownsClient = httpClient == null;
            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                var mbClient = new MusicBrainzClient(client, MusicBrainzClient.DefaultUserAgent);
                var releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId, ct).ConfigureAwait(true);
                if (releases == null || releases.Count == 0)
                {
                    StatusMessage = $"No MusicBrainz match for DiscID {ids.MusicBrainzId}.";
                    RipReport = $"MusicBrainz lookup completed: No matching release found for DiscID {ids.MusicBrainzId} (freedb {ids.FreeDbIdHex}).";
                    return null;
                }

                var meta = MusicBrainzClient.ToAlbumMetadata(releases[0], ids.MusicBrainzId);
                if (meta != null)
                {
                    if (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album")
                    {
                        AlbumTitle = meta.AlbumTitle;
                    }
                    if (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists")
                    {
                        AlbumPerformer = meta.AlbumPerformer;
                    }
                    for (int i = 0; i < Tracks.Count && i < (meta.Tracks?.Count ?? 0); i++)
                    {
                        if (!string.IsNullOrWhiteSpace(meta.Tracks![i].Title))
                        {
                            Tracks[i].Title = meta.Tracks![i].Title!;
                        }
                        if (!string.IsNullOrWhiteSpace(meta.Tracks![i].Performer))
                        {
                            Tracks[i].Performer = meta.Tracks![i].Performer!;
                        }
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"MusicBrainz release found (DiscID {ids.MusicBrainzId}):");
                    sb.AppendLine($"  Album:   {meta.AlbumTitle}");
                    sb.AppendLine($"  Artist:  {meta.AlbumPerformer}");
                    sb.AppendLine($"  Tracks:  {meta.Tracks?.Count ?? 0}");
                    foreach (var t in meta.Tracks ?? Array.Empty<CdTextTrackMetadata>())
                    {
                        sb.AppendLine($"    Track {t.TrackNumber:D2}: {t.Performer} - {t.Title}");
                    }
                    RipReport = sb.ToString().TrimEnd();
                    StatusMessage = $"MusicBrainz: {meta.AlbumPerformer} — {meta.AlbumTitle} ({meta.Tracks?.Count ?? 0} tracks)";
                }
                return meta;
            }
            finally
            {
                if (ownsClient)
                {
                    client.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"MusicBrainz lookup error: {ex.Message}";
            RipReport = $"MusicBrainz lookup failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DetectReadOffsetAsync()
    {
        var recorder = ResolveRecorder();
        if (recorder == null)
        {
            StatusMessage = Strings.StatusAudioBurnNoOpticalRecorderSelectedOrAvailable;
            RipReport = "Offset detection failed: No optical recorder selected or available.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Detecting read offset for {recorder.UniqueId}...";
        RipReport = string.Empty;

        try
        {
            var query = AudioCdRipperService.QueryTracksAndCdText(recorder);
            var tracks = query.Tracks;
            var ids = DiscIdCalculator.CalculateForRecorder(recorder);
            if (ids == null || tracks.Count == 0)
            {
                StatusMessage = Strings.StatusAudioBurnNoAudioTracksFoundOnInserted;
                RipReport = "Offset detection failed: No audio tracks found. Insert an audio CD to detect the drive's read offset.";
                return;
            }

            IReadOnlyList<AccurateRipPressing> pressings;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
            }

            if (pressings.Count == 0)
            {
                StatusMessage = Strings.StatusAudioBurnDiscNotFoundInAccurateripDatabase;
                RipReport = "Offset detection failed: This disc is not in the AccurateRip database, so it cannot be used to detect the offset. Try a well-known commercial CD.";
                return;
            }

            var detection = await ReadOffsetDetector.DetectAsync(_burnEngine, recorder, tracks, pressings).ConfigureAwait(false);
            var best = detection.Best;

            if (best == null)
            {
                string errMsg = detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database.";
                StatusMessage = $"Offset detection failed: {errMsg}";
                RipReport = $"Offset detection failed: {errMsg}";
                return;
            }

            ReadOffsetSamples = best.OffsetSamples;
            StatusMessage = $"Read offset detected: {best.OffsetSamples:+0;-0;0} samples (confidence {best.Confidence}).";

            var sb = new StringBuilder();
            sb.AppendLine("Read offset detected successfully:");
            sb.AppendLine($"  Drive:          {recorder.UniqueId}");
            sb.AppendLine($"  Read Offset:    {best.OffsetSamples:+0;-0;0} samples");
            sb.AppendLine($"  Confidence:     {best.Confidence} matching submissions");
            sb.AppendLine($"  Tracks Checked: {string.Join(", ", detection.ProbedTracks)}");
            RipReport = sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusAudioBurnOffsetDetectionCancelled;
            RipReport = "Offset detection cancelled by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Offset detection error: {ex.Message}";
            RipReport = $"Offset detection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The rip result with the files of the first chosen format as its file list (the other formats are in the report).</summary>
    private static AudioCdRipResult WithExportedFiles(AudioCdRipResult rip, AudioExportResult exported, AudioRipFormat primary)
    {
        var primaryFiles = exported.Files.Where(f => f.Format == primary).OrderBy(f => f.TrackNumber).ToList();
        if (primaryFiles.Count == 0)
        {
            return rip;
        }

        return AudioCdRipResult.Succeeded(
            primaryFiles.Select(f => f.Path).ToList(), rip.TotalSectorsRead, rip.TotalTime,
            exported.CueSheets.FirstOrDefault() ?? rip.CueSheetPath, rip.CdText, rip.TrackInfos,
            rip.BadSectorLbas, rip.SecureModeUsed, rip.ReplayGainAlbumGain, rip.ReplayGainAlbumPeak,
            rip.RecoveryRepair,
            hiddenTrackOneAudioPath: primaryFiles.FirstOrDefault(f => f.TrackNumber == 0)?.Path,
            ctdbDiscCrc: rip.CtdbDiscCrc);
    }

    /// <summary>
    /// The edition to use for this rip: the one the user picked for this disc, otherwise the only one (or the first
    /// when the editions name every track alike). Null when the editions differ and none was picked.
    /// </summary>
    private MusicBrainzRelease? PickRelease(IReadOnlyList<MusicBrainzRelease> releases, string discId)
    {
        if (string.Equals(_releaseDiscId, discId, StringComparison.Ordinal) &&
            _selectedReleaseIndex >= 0 && _selectedReleaseIndex < _releaseList.Count)
        {
            string wanted = _releaseList[_selectedReleaseIndex].ReleaseId;
            var match = releases.FirstOrDefault(r => string.Equals(r.ReleaseId, wanted, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return MusicBrainzReleaseChoice.Differences(releases).Count > 0 ? null : releases[0];
    }

    private void SetReleaseChoices(string discId, IReadOnlyList<MusicBrainzRelease> releases)
    {
        _releaseList = releases;
        _releaseDiscId = discId;
        ReleaseChoices.Clear();
        for (int i = 0; i < releases.Count; i++)
        {
            ReleaseChoices.Add(MusicBrainzReleaseChoice.Describe(releases[i], i + 1));
        }

        SelectedReleaseIndex = -1;
        ReleaseDifferenceNote = BuildDifferenceNote(releases);
    }

    private void ClearReleaseChoices()
    {
        _releaseList = [];
        _releaseDiscId = null;
        ReleaseChoices.Clear();
        SelectedReleaseIndex = -1;
        ReleaseDifferenceNote = null;
    }

    private static string? BuildDifferenceNote(IReadOnlyList<MusicBrainzRelease> releases)
    {
        var differences = MusicBrainzReleaseChoice.Differences(releases);
        if (differences.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var difference in differences)
        {
            string where = difference.TrackNumber == 0 ? "the album title or artist" : $"track {difference.TrackNumber}";
            var quoted = difference.Titles.Select(t => $"'{t}'").ToList();
            string alternatives = quoted.Count > 2
                ? string.Join(", ", quoted.Take(quoted.Count - 1)) + " or " + quoted[^1]
                : string.Join(" or ", quoted);
            parts.Add($"{where} is {alternatives}");
        }

        return "Editions differ: " + string.Join("; ", parts) + ". Choose the one on your CD.";
    }

    /// <summary>
    /// Reads the disc's table of contents (no audio is read) and asks MusicBrainz which editions share it. Fills
    /// <see cref="ReleaseChoices"/>; choose one before ripping.
    /// </summary>
    public async Task LookUpReleasesAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        var recorder = ResolveRecorder();
        if (recorder == null)
        {
            StatusMessage = Strings.StatusAudioBurnNoOpticalRecorderSelectedOrAvailable;
            return;
        }

        int generation = ++_releaseLookupGeneration;
        IsBusy = true;
        StatusMessage = $"Looking up editions of the disc in {recorder.UniqueId} on musicbrainz.org...";
        HttpClient? ownedHttp = null;
        try
        {
            var ids = DiscIdCalculator.CalculateForRecorder(recorder);
            if (ids == null)
            {
                StatusMessage = Strings.StatusAudioBurnNoAudioTracksFoundOnInserted;
                return;
            }

            var http = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            var mbClient = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent);
            var releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId, ct);
            if (generation != _releaseLookupGeneration)
            {
                return; // a newer lookup is running; drop this answer
            }

            if (releases.Count == 0)
            {
                ClearReleaseChoices();
                StatusMessage = $"MusicBrainz knows no edition of the disc {ids.MusicBrainzId}.";
                return;
            }

            SetReleaseChoices(ids.MusicBrainzId, releases);
            StatusMessage = releases.Count == 1
                ? "MusicBrainz lists one edition of this disc."
                : ReleaseDifferenceNote == null
                    ? $"MusicBrainz lists {releases.Count} editions of this disc; they name the tracks alike."
                    : $"MusicBrainz lists {releases.Count} editions of this disc. Choose yours.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Edition lookup cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"MusicBrainz lookup failed: {ex.Message}";
        }
        finally
        {
            ownedHttp?.Dispose();
            if (generation == _releaseLookupGeneration)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Extracts Red Book audio tracks from the inserted CD into uncompressed WAV files and companion CUE sheet.
    /// </summary>
    public async Task<AudioCdRipResult> RipAudioCdAsync(string? driveId = null, string? outputFolder = null, CancellationToken ct = default, HttpClient? httpClient = null)
    {
        RipReport = string.Empty;

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = Strings.StatusAudioBurnNoOpticalRecorderSelectedOrAvailable;
            var fail = AudioCdRipResult.Failed(StatusMessage);
            LastRipResult = fail;
            RipReport = $"Rip failed: {StatusMessage}";
            return fail;
        }

        string targetFolder = !string.IsNullOrWhiteSpace(outputFolder)
            ? outputFolder
            : (!string.IsNullOrWhiteSpace(RipOutputFolder) ? RipOutputFolder : Path.Combine(Path.GetTempPath(), "CdBurner_RippedAudio"));

        // Check everything the export needs before reading the disc, so a missing ffmpeg, a bad bitrate or a bad
        // naming template costs no rip time.
        var exporter = new AudioLibraryExporter(new MediaFoundationAacEncoder());
        var formats = CheckedRipFormats();
        var targets = new List<AudioExportTarget>();
        string? formatProblem = formats.Count == 0
            ? "Choose at least one output format."
            : AudioTargetChoices.BuildTargets(formats, SelectedMp3Quality, RipBitrateKbps, targets);
        bool useLibrary = !string.IsNullOrWhiteSpace(RipLibraryFolder);
        if (formatProblem == null && useLibrary)
        {
            formatProblem = AudioNamingTemplate.Validate(RipNamingTemplate);
        }

        string? customFfmpeg = _settingsService?.CurrentSettings.FfmpegExecutablePath;
        string? ffmpegPath = targets.Any(t => AudioRipFormats.RequiresFfmpeg(t.Format))
            ? FfmpegLocator.FindFfmpegExecutable(string.IsNullOrWhiteSpace(customFfmpeg) ? null : customFfmpeg)
            : null;
        string exportRoot = useLibrary ? RipLibraryFolder.Trim() : targetFolder;
        var exportCheck = new AudioExportOptions(
            targets, exportRoot, useLibrary ? RipNamingTemplate : AudioNamingTemplate.Flat,
            new AudioAlbumInfo(string.Empty, string.Empty, null), ffmpegPath,
            KeepWav: false, WriteFolderImage: WriteFolderImage, Overwrite: OverwriteExistingFiles);
        if (formatProblem == null)
        {
            formatProblem = exporter.CheckPrerequisites(exportCheck);
        }

        // Read and check the cover now, so a missing or wrong file fails before the disc is read (same rule as rip-audio --cover).
        byte[]? coverBytes = null;
        string? coverMime = null;
        if (formatProblem == null && !string.IsNullOrWhiteSpace(RipCoverArtPath))
        {
            try
            {
                coverBytes = await File.ReadAllBytesAsync(RipCoverArtPath.Trim(), ct);
                coverMime = FlacEncoder.DetectImageMimeType(coverBytes);
                if (coverMime == null)
                {
                    formatProblem = $"'{RipCoverArtPath}' is not a JPEG, PNG, GIF or WebP image.";
                }
                else
                {
                    formatProblem = FlacEncoder.CoverArtSizeProblem(coverBytes.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                formatProblem = $"Cover image could not be read: {ex.Message}";
            }
        }
        if (formatProblem != null)
        {
            StatusMessage = formatProblem;
            var fail = AudioCdRipResult.Failed(formatProblem);
            LastRipResult = fail;
            RipReport = $"Rip not started: {formatProblem}";
            return fail;
        }

        _activeBurnCts?.Dispose();
        _activeBurnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeToken = _activeBurnCts.Token;

        IsBusy = true;
        CancelBurnCommand.RaiseCanExecuteChanged();
        BurnProgress = 0;
        StatusMessage = $"Ripping Audio CD tracks from {recorder.UniqueId}...";

        try
        {
            var progress = new ClosableProgress<BurnProgressSnapshot>(p =>
            {
                if (!_isBusy) return;
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });
            var exportProgress = new ClosableProgress<string>(message =>
            {
                if (_isBusy) StatusMessage = message;
            });

            DiscIdentifiers? ids = null;
            try
            {
                ids = DiscIdCalculator.CalculateForRecorder(recorder);
            }
            catch
            {
            }

            string? warning = null;
            CdTextAlbumMetadata? metadataOverride = null;
            MusicBrainzRelease? chosenRelease = null;

            // Choices made for another disc are never applied to this one.
            if (ids != null && _releaseDiscId != null && !string.Equals(_releaseDiscId, ids.MusicBrainzId, StringComparison.Ordinal))
            {
                ClearReleaseChoices();
            }

            if (LookupMetadata && ids != null)
            {
                IReadOnlyList<MusicBrainzRelease> releases = [];
                HttpClient? ownedHttp = null;
                try
                {
                    var http = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                    var mbClient = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent);
                    // Resumes on the UI thread (no ConfigureAwait(false)): the edition list below is bound to the screen.
                    releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId, activeToken) ?? [];
                }
                catch (OperationCanceledException) when (activeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException hre)
                {
                    warning = $"MusicBrainz lookup failed: {hre.Message}";
                }
                catch (TaskCanceledException tce)
                {
                    warning = $"MusicBrainz lookup timed out: {tce.Message}";
                }
                catch (Exception ex)
                {
                    warning = $"MusicBrainz lookup failed: {ex.Message}";
                }
                finally
                {
                    ownedHttp?.Dispose();
                }

                if (releases.Count > 0)
                {
                    chosenRelease = PickRelease(releases, ids.MusicBrainzId);
                    if (chosenRelease == null)
                    {
                        // Editions with different track names: stop before the disc is read and let the user choose.
                        progress.Close();
                        exportProgress.Close();
                        SetReleaseChoices(ids.MusicBrainzId, releases);
                        const string chooseMessage =
                            "MusicBrainz lists several editions of this disc that name tracks differently. Choose yours, then start the rip again.";
                        StatusMessage = chooseMessage;
                        var stopped = AudioCdRipResult.Failed(chooseMessage);
                        LastRipResult = stopped;
                        RipReport = "Rip not started: " + chooseMessage + (ReleaseDifferenceNote == null ? string.Empty : "\n" + ReleaseDifferenceNote);
                        return stopped;
                    }

                    metadataOverride = MusicBrainzClient.ToAlbumMetadata(chosenRelease, ids.MusicBrainzId);
                }
            }

            var options = new AudioRipOptions
            {
                GenerateCueSheet = true,
                CueSheetFileName = "album.cue",
                ContinueOnUnreadableSector = ContinueOnUnreadable,
                MetadataOverride = metadataOverride,
                ReadOffsetSamples = ReadOffsetSamples,
                SecureMode = SecureRip,
                CalculateReplayGain = true, // track and album ReplayGain 2.0, same as rip-audio without --no-replaygain
                CoverArtBytes = coverBytes,
                CoverArtMimeType = coverMime,
                RepairFromRecoverySession = RepairFromRecoverySession
            };

            var ripResult = await _ripperService.RipAudioCdAsync(recorder, _burnEngine, targetFolder, options, progress, activeToken);
            progress.Close(); // a late progress report must not overwrite the final status line
            LastRipResult = ripResult;

            string metadataSource = metadataOverride != null
                ? "musicbrainz"
                : (ripResult.CdText != null ? "cd-text" : "none");

            string? accurateRipStatus = null;
            var accurateRipTracks = new List<(AudioTrackRipInfo Info, AccurateRipTrackVerdict? Verdict)>();
            IReadOnlyList<AccurateRipPressing>? accurateRipPressings = null;

            if (ripResult.Success && VerifyAccurateRip)
            {
                if (ids == null)
                {
                    accurateRipStatus = "unavailable";
                }
                else
                {
                    HttpClient? ownedHttp = null;
                    try
                    {
                        var http = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                        var pressings = await AccurateRipDatabase.FetchAsync(http, ids, cancellationToken: activeToken).ConfigureAwait(false);
                        accurateRipPressings = pressings;
                        accurateRipStatus = pressings.Count == 0 ? "not-in-database" : "checked";
                        foreach (var info in ripResult.TrackInfos)
                        {
                            accurateRipTracks.Add((info, pressings.Count == 0
                                ? null
                                : AccurateRipDatabase.Match(pressings, info.AudioTrackIndex, info.AccurateRipV1, info.AccurateRipV2)));
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                    {
                        accurateRipStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"AccurateRip lookup failed: {ex.Message}";
                    }
                    catch (Exception ex)
                    {
                        accurateRipStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"AccurateRip lookup failed: {ex.Message}";
                    }
                    finally
                    {
                        ownedHttp?.Dispose();
                    }
                }

                if (ReadOffsetSamples == 0 && accurateRipStatus == "checked" && accurateRipTracks.Any(t => t.Verdict is { IsAccurate: false }))
                {
                    warning = (warning == null ? "" : warning + "\n") +
                        "No read offset was configured; AccurateRip only matches when the drive's read offset is corrected (use 'Detect' or check the AccurateRip drive offset list).";
                }
            }

            string? ctdbStatus = null;
            (bool Matched, int Confidence, int TotalSubmissions)? ctdbDiscMatch = null;
            var ctdbTracks = new List<(AudioTrackRipInfo Info, CtdbTrackVerdict? Verdict)>();
            CtdbRepairResult? ctdbRepair = null;

            if (ripResult.Success && VerifyCtdb)
            {
                if (ids == null || string.IsNullOrWhiteSpace(ids.CtdbToc))
                {
                    ctdbStatus = "unavailable";
                }
                else
                {
                    HttpClient? ownedHttp = null;
                    try
                    {
                        var http = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                        var entries = await CueToolsDatabase.FetchAsync(http, ids, cancellationToken: activeToken).ConfigureAwait(false);
                        ctdbStatus = entries.Count == 0 ? "not-in-database" : "checked";
                        if (entries.Count > 0)
                        {
                            ctdbDiscMatch = CueToolsDatabase.MatchDisc(entries, ripResult.CtdbDiscCrc);
                        }

                        // A rip that differs from CTDB is repaired from the entry's parity; AccurateRip is contacted to
                        // confirm only when a correction was found, and the rip is only changed when it then matches.
                        if (ctdbDiscMatch is { Matched: false } && ripResult.CtdbDiscCrc != null)
                        {
                            StatusMessage = "The rip differs from the CUETools database; trying to repair it from CTDB parity...";
                            var knownPressings = accurateRipPressings;
                            ctdbRepair = await CtdbRepairService.RepairAsync(
                                http,
                                ripResult,
                                entries,
                                ids.CtdbToc,
                                knownPressings != null
                                    ? _ => Task.FromResult(knownPressings)
                                    : ct => AccurateRipDatabase.FetchAsync(http, ids, cancellationToken: ct),
                                cancellationToken: activeToken).ConfigureAwait(false);
                            if (ctdbRepair.UpdatedRip is { } repairedRip)
                            {
                                ripResult = repairedRip;
                                LastRipResult = ripResult;
                                ctdbDiscMatch = CueToolsDatabase.MatchDisc(entries, ripResult.CtdbDiscCrc);
                                if (accurateRipPressings is { Count: > 0 } arPressings)
                                {
                                    accurateRipTracks = ripResult.TrackInfos
                                        .Select(i => (i, (AccurateRipTrackVerdict?)AccurateRipDatabase.Match(arPressings, i.AudioTrackIndex, i.AccurateRipV1, i.AccurateRipV2)))
                                        .ToList();
                                }
                            }
                        }

                        foreach (var info in ripResult.TrackInfos)
                        {
                            ctdbTracks.Add((info, entries.Count == 0
                                ? null
                                : CueToolsDatabase.MatchTrack(entries, info.AudioTrackIndex, info.CtdbTrackCrc, ripResult.CtdbDiscCrc)));
                        }
                    }
                    catch (OperationCanceledException) when (activeToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                    {
                        ctdbStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"CTDB lookup failed: {ex.Message}";
                    }
                    catch (Exception ex)
                    {
                        ctdbStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"CTDB lookup failed: {ex.Message}";
                    }
                    finally
                    {
                        ownedHttp?.Dispose();
                    }
                }
            }

            // Cover art and export come after verification and any CTDB repair, which work on the ripped WAV files.
            string? coverSource = coverBytes != null ? "file" : null;
            if (ripResult.Success && coverBytes == null && FetchCoverArt && chosenRelease != null)
            {
                StatusMessage = "Fetching the front cover from the Cover Art Archive...";
                HttpClient? ownedHttp = null;
                try
                {
                    var http = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
                    var image = await CoverArtArchiveClient.FetchFrontAsync(http, chosenRelease.ReleaseId, MusicBrainzClient.DefaultUserAgent, cancellationToken: activeToken).ConfigureAwait(false);
                    if (image != null)
                    {
                        coverBytes = image.Bytes;
                        coverMime = image.MimeType;
                        coverSource = "coverartarchive";
                    }
                    else
                    {
                        warning = (warning == null ? "" : warning + "\n") + "The Cover Art Archive has no usable front cover for this release.";
                    }
                }
                catch (OperationCanceledException) when (activeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warning = (warning == null ? "" : warning + "\n") + $"Cover art lookup failed: {ex.Message}";
                }
                finally
                {
                    ownedHttp?.Dispose();
                }
            }

            string? exportNote = null;
            AudioExportResult? exported = null;
            if (ripResult.Success)
            {
                StatusMessage = "Writing the album files...";
                var album = new AudioAlbumInfo(
                    ripResult.CdText?.AlbumPerformer ?? string.Empty,
                    ripResult.CdText?.AlbumTitle ?? string.Empty,
                    chosenRelease == null ? null : MusicBrainzReleaseChoice.Year(chosenRelease),
                    chosenRelease?.ReleaseId,
                    coverBytes,
                    coverMime);
                try
                {
                    exported = await exporter.ExportAsync(ripResult, exportCheck with { Album = album }, exportProgress, activeToken);
                    if (exported.Warnings.Count > 0)
                    {
                        warning = (warning == null ? "" : warning + "\n") + string.Join("\n", exported.Warnings);
                    }

                    ripResult = WithExportedFiles(ripResult, exported, targets[0].Format);
                    LastRipResult = ripResult;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // The WAV files stay in place, so the rip is not lost when only the export fails.
                    exportNote = $"The disc was read, but writing the files failed: {ex.Message} The WAV files are kept in '{Path.GetFullPath(targetFolder)}'.";
                }
                finally
                {
                    exportProgress.Close();
                }
            }

            var sb = new StringBuilder();
            if (ripResult.Success)
            {
                StatusMessage = exportNote != null
                    ? $"Rip completed, but writing the files failed: {exportNote}"
                    : $"Rip completed: {ripResult.TracksRipped} track(s) saved to '{exportRoot}' ({ripResult.TotalTime}).";
                BurnProgress = 100.0;

                sb.AppendLine("Audio rip completed successfully:");
                sb.AppendLine($"  Drive:            {recorder.UniqueId}");
                sb.AppendLine($"  Output Folder:    {Path.GetFullPath(targetFolder)}");
                if (useLibrary)
                {
                    sb.AppendLine($"  Library Folder:   {Path.GetFullPath(exportRoot)}");
                }
                sb.AppendLine($"  Tracks Ripped:    {ripResult.TracksRipped}");
                sb.AppendLine($"  Sectors Read:     {ripResult.TotalSectorsRead:N0}");
                sb.AppendLine($"  Duration:         {ripResult.TotalTime}");
                sb.AppendLine($"  MusicBrainz ID:   {ids?.MusicBrainzId ?? "n/a"}");
                sb.AppendLine($"  freedb ID:        {ids?.FreeDbIdHex ?? "n/a"}");
                sb.AppendLine($"  AccurateRip:      {ids?.AccurateRipDatabasePath ?? "n/a"}");
                sb.AppendLine($"  CTDB TOCID:       {ids?.CtdbTocId ?? "n/a"}");
                sb.AppendLine($"  Metadata Source:  {metadataSource}");
                sb.AppendLine($"  Read Offset:      {ReadOffsetSamples:+0;-0;0} samples");
                if (SecureRip)
                {
                    sb.AppendLine(ripResult.SecureModeUsed
                        ? $"  Secure Mode:      C2 error pointers ({ripResult.TrackInfos.Sum(t => t.RecoveredSectors)} sectors recovered, {ripResult.TrackInfos.Sum(t => t.SuspectSectors)} still suspect)"
                        : "  Secure Mode:      requested, but the drive does not report C2 error pointers (plain read used)");
                    foreach (var t in ripResult.TrackInfos.Where(t => t.SuspectSectors > 0))
                    {
                        sb.AppendLine($"    Track {t.TrackNumber:D2}: {t.SuspectSectors} suspect sector(s) - check AccurateRip or re-rip");
                    }
                }
                if (ripResult.BadSectorLbas.Count > 0)
                {
                    sb.AppendLine($"  Bad Sectors:      {ripResult.BadSectorLbas.Count} (zero-filled)");
                }
                if (ripResult.RecoveryRepair is { } repair)
                {
                    sb.AppendLine($"  Recovery Session: {repair.Summary}");
                }
                if (accurateRipStatus != null)
                {
                    sb.AppendLine($"  AccurateRip:      {accurateRipStatus}");
                    foreach (var (info, verdict) in accurateRipTracks)
                    {
                        string line = verdict == null
                            ? "not in database"
                            : verdict.IsAccurate
                                ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions}, {(verdict.MatchedV2 ? "v2" : "v1")})"
                                : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                        sb.AppendLine($"    Track {info.TrackNumber:D2}: {line}  [v1 {info.AccurateRipV1:x8} v2 {info.AccurateRipV2:x8}]");
                    }
                }
                if (ctdbStatus != null)
                {
                    string discLine = ctdbDiscMatch is { } dm && ripResult.CtdbDiscCrc is uint discCrc
                        ? $"{ctdbStatus} (disc {(dm.Matched ? $"accurate, confidence {dm.Confidence}/{dm.TotalSubmissions}" : $"NOT accurate, 0/{dm.TotalSubmissions} submissions match")}, crc {discCrc:x8})"
                        : ctdbStatus;
                    sb.AppendLine($"  CTDB:             {discLine}");
                    foreach (var (info, verdict) in ctdbTracks)
                    {
                        string line = verdict == null
                            ? "not in database"
                            : verdict.IsAccurate
                                ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions})"
                                : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                        sb.AppendLine($"    Track {info.TrackNumber:D2}: {line}  [ctdb {info.CtdbTrackCrc:x8}]");
                    }
                }
                if (ctdbRepair != null)
                {
                    sb.AppendLine($"  CTDB Repair:      {ctdbRepair.StatusCode} - {ctdbRepair.Message}");
                    foreach (string line in ctdbRepair.DescribeTracks())
                    {
                        sb.AppendLine($"    {line}");
                    }
                    if (ctdbRepair.AccurateRipContacted && !VerifyAccurateRip)
                    {
                        sb.AppendLine("    AccurateRip was contacted only to confirm this repair.");
                    }
                }
                if (!string.IsNullOrEmpty(warning))
                {
                    sb.AppendLine($"  Warning:          {warning}");
                }
                sb.AppendLine($"  Formats:          {string.Join(", ", targets.Select(t => AudioRipFormats.DisplayName(t.Format)))}");
                if (chosenRelease != null)
                {
                    var details = new[] { chosenRelease.Country, MusicBrainzReleaseChoice.Year(chosenRelease) }.Where(d => !string.IsNullOrWhiteSpace(d));
                    string detailText = string.Join(", ", details);
                    sb.AppendLine($"  Edition:          {(detailText.Length > 0 ? detailText + " " : string.Empty)}(MusicBrainz {chosenRelease.ReleaseId})");
                }
                if (coverSource == "coverartarchive")
                {
                    sb.AppendLine("  Cover:            Cover Art Archive");
                }
                else if (coverSource == "file")
                {
                    sb.AppendLine($"  Cover:            {RipCoverArtPath.Trim()}");
                }
                if (!string.IsNullOrEmpty(exportNote))
                {
                    sb.AppendLine($"  Export:           {exportNote}");
                }
                if (exported != null)
                {
                    foreach (var group in exported.Files.GroupBy(f => f.Format))
                    {
                        sb.AppendLine($"  Files ({AudioRipFormats.DisplayName(group.Key)}):");
                        foreach (var file in group.OrderBy(f => f.TrackNumber))
                        {
                            sb.AppendLine($"    {Path.GetFullPath(file.Path)}");
                        }
                    }
                    foreach (string cue in exported.CueSheets)
                    {
                        sb.AppendLine($"  CUE Sheet:        {Path.GetFullPath(cue)}");
                    }
                    foreach (string image in exported.FolderImages)
                    {
                        sb.AppendLine($"  Folder Image:     {Path.GetFullPath(image)}");
                    }
                }
                else
                {
                    sb.AppendLine("  Files:");
                    foreach (var file in ripResult.RippedFilePaths)
                    {
                        sb.AppendLine($"    {Path.GetFullPath(file)}");
                    }
                    if (!string.IsNullOrEmpty(ripResult.CueSheetPath) && File.Exists(ripResult.CueSheetPath))
                    {
                        sb.AppendLine($"  CUE Sheet:        {Path.GetFullPath(ripResult.CueSheetPath)}");
                    }
                }
            }
            else
            {
                StatusMessage = $"Rip failed: {ripResult.ErrorMessage}";

                sb.AppendLine($"Audio rip failed: {ripResult.ErrorMessage}");
                if (ids != null)
                {
                    sb.AppendLine($"  MusicBrainz ID:   {ids.MusicBrainzId}");
                    sb.AppendLine($"  freedb ID:        {ids.FreeDbIdHex}");
                    sb.AppendLine($"  AccurateRip:      {ids.AccurateRipDatabasePath}");
                    sb.AppendLine($"  CTDB TOCID:       {ids.CtdbTocId}");
                }
                if (!string.IsNullOrEmpty(warning))
                {
                    sb.AppendLine($"  Warning:          {warning}");
                }
            }

            RipReport = sb.ToString().TrimEnd();
            return ripResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusAudioBurnAudioCdRipCancelledByUser;
            var cancelled = AudioCdRipResult.Failed(StatusMessage);
            LastRipResult = cancelled;
            RipReport = "Audio CD rip cancelled by user.";
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rip error: {ex.Message}";
            var fail = AudioCdRipResult.Failed(ex.Message, ex);
            LastRipResult = fail;
            RipReport = $"Rip error: {ex.Message}";
            return fail;
        }
        finally
        {
            _activeBurnCts?.Dispose();
            _activeBurnCts = null;
            IsBusy = false;
            CancelBurnCommand.RaiseCanExecuteChanged();
        }
    }

    private IDiscRecorderBackend? ResolveRecorder(string? driveId = null)
    {
        if (SelectedRecorder != null && (string.IsNullOrWhiteSpace(driveId) || string.Equals(SelectedRecorder.UniqueId, driveId, StringComparison.OrdinalIgnoreCase)))
        {
            return SelectedRecorder;
        }

        if (!string.IsNullOrWhiteSpace(driveId))
        {
            var found = _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                SelectedRecorder = found;
                return found;
            }
        }

        if (SelectedRecorder != null)
        {
            return SelectedRecorder;
        }

        var defaultRec = _deviceService.GetRecorders().FirstOrDefault(r => !r.IsVirtual)
                         ?? _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                         ?? _deviceService.GetRecorders().FirstOrDefault();
        if (defaultRec != null)
        {
            SelectedRecorder = defaultRec;
        }
        return defaultRec;
    }

    #endregion
}
