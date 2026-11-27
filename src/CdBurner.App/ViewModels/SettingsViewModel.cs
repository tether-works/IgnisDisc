using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security;
using CdBurner.App.Mvvm;
using CdBurner.App.Themes;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Platform;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing application preferences, default write speeds, overburn/buffer underrun policies,
/// scratch directories, external tool installation, online services, visual themes, and diagnostic logging configuration.
/// </summary>
public class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService;
    private readonly MainViewModel? _mainViewModel;
    private readonly IShellIntegrationRegistry _shellIntegrationRegistry;
    private readonly Func<string?> _appExecutablePathProvider;
    private readonly string _driveOffsetsCachePath;

    private int _defaultWriteSpeedMultiplier;
    private bool _verifyAfterBurnByDefault;
    private bool _strictVerify;
    private bool _ejectAfterBurnByDefault;
    private bool _enableBurnFreeByDefault;
    private string _tempStagingDirectory = string.Empty;
    private ThemeMode _themeMode = ThemeMode.System;
    private string _uiLanguage = LocalizationManager.SystemDefault;
    private string _ffmpegExecutablePath = string.Empty;
    private bool _enableVirtualDrive;
    private int _logRingBufferCapacity;
    private bool _lookupMusicBrainzByDefault = true;
    private bool _verifyAccurateRipByDefault = true;
    private bool _verifyCtdbByDefault;
    private IDiscRecorderBackend? _selectedRecorder;
    private string _statusMessage = "Ready";
    private bool _checkForUpdatesOnStartup;
    private bool _enableExplorerShellIntegration;
    private string _updateStatusMessage = Strings.StatusSettingsUpdateCheckHasNotBeenRun;
    private bool _isCheckingForUpdates;
    private bool _notifyWhenBurnFinishes = true;
    private bool _autoSave;
    private bool _loading;

    /// <summary>
    /// The settings stored in the settings file; a change to any of them is saved at once while <see cref="AutoSave"/>
    /// is on.
    /// </summary>
    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(DefaultWriteSpeedMultiplier), nameof(VerifyAfterBurnByDefault), nameof(StrictVerify),
        nameof(EjectAfterBurnByDefault), nameof(NotifyWhenBurnFinishes), nameof(EnableBurnFreeByDefault),
        nameof(TempStagingDirectory), nameof(ThemeMode), nameof(UiLanguage), nameof(FfmpegExecutablePath), nameof(EnableVirtualDrive),
        nameof(LogRingBufferCapacity), nameof(LookupMusicBrainzByDefault), nameof(VerifyAccurateRipByDefault),
        nameof(VerifyCtdbByDefault), nameof(CheckForUpdatesOnStartup), nameof(DirectWritingForDvdRw),
        nameof(EnableExplorerShellIntegration)
    ];

    /// <summary>
    /// Save every change to the settings file as soon as it is made. Off until the app turns it on after loading the
    /// saved settings, so building this view model (in tests, or before the file is read) never writes the file.
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set => _autoSave = value;
    }
    private bool _isInstallingTool;
    private bool _isFfmpegInstalled;
    private string _ffmpegStatusText = string.Empty;
    private bool _isPar2Installed;
    private bool _isPar2UpdateAvailable;
    private string _par2StatusText = string.Empty;
    private string _offsetDatabaseStatusText = "Not cached — click 'Refresh Offset Database' to download AccurateRip drive offsets.";
    private bool _isRefreshingOffsetDatabase;
    private string _audioReadOffsetSummaryText = "Automatic (key-disc calibration or AccurateRip DB)";
    private bool _hasManualAudioReadOffsetOverride;

    /// <summary>
    /// Raised when settings are saved or reset so the shell can apply changes immediately.
    /// </summary>
    public event EventHandler? SettingsSaved;

    /// <summary>Raised when the cached AccurateRip <c>DriveOffsets.bin</c> database is refreshed.</summary>
    public event EventHandler? OffsetDatabaseRefreshed;

    #region Properties

    /// <summary>
    /// Available optical recorders discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Currently selected default optical recorder drive.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SetProperty(ref _selectedRecorder, value);
    }

    /// <summary>
    /// Underlying persistent configuration service.
    /// </summary>
    public AppSettingsService SettingsService => _settingsService;

    /// <summary>
    /// Default optical burn speed multiplier (0 = Maximum supported speed).
    /// </summary>
    public int DefaultWriteSpeedMultiplier
    {
        get => _defaultWriteSpeedMultiplier;
        set => SetProperty(ref _defaultWriteSpeedMultiplier, value);
    }

    /// <summary>
    /// Whether to automatically verify written media sectors against source hash after burning.
    /// </summary>
    public bool VerifyAfterBurnByDefault
    {
        get => _verifyAfterBurnByDefault;
        set => SetProperty(ref _verifyAfterBurnByDefault, value);
    }

    /// <summary>
    /// Verify with every read forced from the disc itself (several times slower). Off: only the parts that can
    /// still be in the drive's memory after a burn are forced.
    /// </summary>
    public bool StrictVerify
    {
        get => _strictVerify;
        set => SetProperty(ref _strictVerify, value);
    }

    /// <summary>
    /// Whether to eject the optical drive tray after a burn operation completes.
    /// </summary>
    public bool EjectAfterBurnByDefault
    {
        get => _ejectAfterBurnByDefault;
        set => SetProperty(ref _ejectAfterBurnByDefault, value);
    }

    /// <summary>
    /// Whether a finished or failed burn is announced with a sound, a flashing taskbar button and a message.
    /// </summary>
    public bool NotifyWhenBurnFinishes
    {
        get => _notifyWhenBurnFinishes;
        set => SetProperty(ref _notifyWhenBurnFinishes, value);
    }

    /// <summary>
    /// Burn screens turn on direct drive commands by themselves while a DVD-RW is in the drive: Windows' writer formats
    /// a DVD-RW for overwriting and cannot add sessions to it. A screen's own tick wins.
    /// </summary>
    public bool DirectWritingForDvdRw
    {
        get => _directWritingForDvdRw;
        set => SetProperty(ref _directWritingForDvdRw, value);
    }

    private bool _directWritingForDvdRw = true;

    /// <summary>
    /// Whether to enable buffer underrun protection (BURN-Proof / Burn-Free) by default.
    /// </summary>
    public bool EnableBurnFreeByDefault
    {
        get => _enableBurnFreeByDefault;
        set => SetProperty(ref _enableBurnFreeByDefault, value);
    }

    /// <summary>
    /// Temporary directory path used for ISO staging, audio decoding, and DVD transcode scratch files.
    /// </summary>
    public string TempStagingDirectory
    {
        get => _tempStagingDirectory;
        set => SetProperty(ref _tempStagingDirectory, value);
    }

    /// <summary>
    /// Visual theme selection (Dark, Light, or follow Windows System preference).
    /// </summary>
    public ThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (SetProperty(ref _themeMode, value))
            {
                ThemeManager.ApplyTheme(value);
            }
        }
    }

    /// <summary>
    /// Language of the app's text as a culture name; empty follows the Windows display language. A change is applied
    /// to the open windows at once and saved with the other settings.
    /// </summary>
    public string UiLanguage
    {
        get => _uiLanguage;
        set
        {
            var normalized = LocalizationManager.Normalize(value);
            if (SetProperty(ref _uiLanguage, normalized))
            {
                LocalizationManager.Apply(normalized);
                foreach (var option in AvailableLanguages)
                {
                    option.Refresh();
                }
            }
        }
    }

    /// <summary>Languages offered by the selector: the system default first, then every shipped language.</summary>
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
        new[] { LocalizationManager.SystemDefault }
            .Concat(LocalizationManager.SupportedLanguages)
            .Select(code => new LanguageOption(code))
            .ToList();
    /// <summary>
    /// Full file path or command name for the FFmpeg transcoder executable used in DVD-Video authoring.
    /// </summary>
    public string FfmpegExecutablePath
    {
        get => _ffmpegExecutablePath;
        set
        {
            if (SetProperty(ref _ffmpegExecutablePath, value))
            {
                RefreshToolStatuses();
            }
        }
    }

    /// <summary>
    /// Whether FFmpeg is currently detected on the host machine.
    /// </summary>
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        private set => SetProperty(ref _isFfmpegInstalled, value);
    }

    /// <summary>
    /// Human-readable status of FFmpeg detection or installation.
    /// </summary>
    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => SetProperty(ref _ffmpegStatusText, value);
    }

    /// <summary>
    /// Whether par2cmdline (<c>par2.exe</c>) is currently detected on the host machine.
    /// </summary>
    public bool IsPar2Installed
    {
        get => _isPar2Installed;
        private set => SetProperty(ref _isPar2Installed, value);
    }

    /// <summary>
    /// True when the detected par2 is par2cmdline 1.4.0 (or another version older than the 1-click
    /// par2cmdline-turbo release); the status text then offers the update.
    /// </summary>
    public bool IsPar2UpdateAvailable
    {
        get => _isPar2UpdateAvailable;
        private set => SetProperty(ref _isPar2UpdateAvailable, value);
    }

    /// <summary>
    /// Human-readable status of par2cmdline detection or installation.
    /// </summary>
    public string Par2StatusText
    {
        get => _par2StatusText;
        private set => SetProperty(ref _par2StatusText, value);
    }

    /// <summary>
    /// Whether to expose the software-defined virtual optical drive backend simulator.
    /// </summary>
    public bool EnableVirtualDrive
    {
        get => _enableVirtualDrive;
        set => SetProperty(ref _enableVirtualDrive, value);
    }

    /// <summary>
    /// In-memory diagnostic ring buffer capacity for the structured burn logger.
    /// </summary>
    public int LogRingBufferCapacity
    {
        get => _logRingBufferCapacity;
        set => SetProperty(ref _logRingBufferCapacity, value);
    }

    /// <summary>
    /// Whether to look up Audio CD album and track titles from MusicBrainz by default when ripping.
    /// </summary>
    public bool LookupMusicBrainzByDefault
    {
        get => _lookupMusicBrainzByDefault;
        set => SetProperty(ref _lookupMusicBrainzByDefault, value);
    }

    /// <summary>
    /// Whether to verify extracted Audio CD tracks against AccurateRip by default when ripping.
    /// </summary>
    public bool VerifyAccurateRipByDefault
    {
        get => _verifyAccurateRipByDefault;
        set => SetProperty(ref _verifyAccurateRipByDefault, value);
    }

    /// <summary>
    /// Whether to verify extracted Audio CD tracks against the CUETools Database (CTDB) by default when ripping.
    /// Strictly opt-in (default false).
    /// </summary>
    public bool VerifyCtdbByDefault
    {
        get => _verifyCtdbByDefault;
        set => SetProperty(ref _verifyCtdbByDefault, value);
    }

    /// <summary>
    /// Whether to check for updates against GitHub Releases on startup. Strictly opt-in (default false).
    /// </summary>
    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetProperty(ref _checkForUpdatesOnStartup, value);
    }

    /// <summary>
    /// Whether per-user Windows Explorer context menus and disc image file associations are registered.
    /// Strictly opt-in (default false).
    /// </summary>
    public bool EnableExplorerShellIntegration
    {
        get => _enableExplorerShellIntegration;
        set => SetProperty(ref _enableExplorerShellIntegration, value);
    }

    /// <summary>
    /// Status message from the most recent GitHub update check.
    /// </summary>
    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        set => SetProperty(ref _updateStatusMessage, value);
    }

    /// <summary>Human-readable status of the local AccurateRip <c>DriveOffsets.bin</c> cache.</summary>
    public string OffsetDatabaseStatusText
    {
        get => _offsetDatabaseStatusText;
        private set => SetProperty(ref _offsetDatabaseStatusText, value);
    }

    /// <summary>True while downloading/refreshing <c>DriveOffsets.bin</c>.</summary>
    public bool IsRefreshingOffsetDatabase
    {
        get => _isRefreshingOffsetDatabase;
        private set
        {
            if (SetProperty(ref _isRefreshingOffsetDatabase, value))
            {
                RefreshOffsetDatabaseCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Summary of how Audio CD rip read offsets are currently resolved.</summary>
    public string AudioReadOffsetSummaryText
    {
        get => _audioReadOffsetSummaryText;
        private set => SetProperty(ref _audioReadOffsetSummaryText, value);
    }

    /// <summary>True when a manual Audio CD read-offset override is saved in settings.</summary>
    public bool HasManualAudioReadOffsetOverride
    {
        get => _hasManualAudioReadOffsetOverride;
        private set
        {
            if (SetProperty(ref _hasManualAudioReadOffsetOverride, value))
            {
                ClearAudioReadOffsetOverrideCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// User-facing status notification message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Available theme options for binding.
    /// </summary>
    public IReadOnlyList<ThemeMode> AvailableThemes { get; } = new[]
    {
        ThemeMode.System,
        ThemeMode.Dark,
        ThemeMode.Light
    };

    /// <summary>
    /// Default write speed choices: Recommended (the default) first, then Slowest, Maximum and fixed multipliers.
    /// </summary>
    public IReadOnlyList<SpeedChoice> AvailableSpeeds { get; } =
        new[] { WriteSpeedPolicy.Recommended, WriteSpeedPolicy.Slowest, WriteSpeedPolicy.Maximum, 4, 8, 10, 16, 24, 32, 48, 52 }
            .Select(v => new SpeedChoice(v, WriteSpeedPolicy.Describe(v)))
            .ToArray();

    /// <summary>One entry of the default write speed list.</summary>
    public sealed record SpeedChoice(int Value, string Label);

    #endregion

    #region Commands

    /// <summary>
    /// Downloads and installs the official full build of FFmpeg into <c>%LOCALAPPDATA%\Programs\ffmpeg</c>, replacing an older one.
    /// </summary>
    public AsyncRelayCommand InstallFfmpegCommand { get; }

    /// <summary>
    /// Downloads and installs <c>par2cmdline</c> (<c>par2.exe</c>) into <c>%LOCALAPPDATA%\Programs\par2cmdline</c>.
    /// </summary>
    public AsyncRelayCommand InstallPar2Command { get; }

    /// <summary>
    /// Queries GitHub Releases API to check for newer versions of IgnisDisc.
    /// </summary>
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    /// <summary>Downloads or refreshes the cached AccurateRip <c>DriveOffsets.bin</c> database.</summary>
    public AsyncRelayCommand RefreshOffsetDatabaseCommand { get; }

    /// <summary>Clears any saved manual Audio CD read-offset override so automatic resolution is used.</summary>
    public RelayCommand ClearAudioReadOffsetOverrideCommand { get; }

    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public RelayCommand SaveSettingsCommand { get; }

    /// <summary>
    /// Resets all settings back to factory defaults.
    /// </summary>
    public RelayCommand ResetSettingsCommand { get; }

    /// <summary>
    /// Reloads settings from disk.
    /// </summary>
    public RelayCommand ReloadSettingsCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="SettingsViewModel"/>.
    /// </summary>
    public SettingsViewModel(
        AppSettingsService? settingsService = null,
        MainViewModel? mainViewModel = null,
        IShellIntegrationRegistry? shellIntegrationRegistry = null,
        Func<string?>? appExecutablePathProvider = null,
        string? driveOffsetsCachePath = null)
    {
        _settingsService = settingsService ?? new AppSettingsService();
        _mainViewModel = mainViewModel;
        _shellIntegrationRegistry = shellIntegrationRegistry ?? WindowsShellIntegrationRegistry.Instance;
        _appExecutablePathProvider = appExecutablePathProvider ?? ResolveDefaultAppExecutablePath;
        _driveOffsetsCachePath = string.IsNullOrWhiteSpace(driveOffsetsCachePath)
            ? AccurateRipDriveOffsets.DefaultCachePath
            : driveOffsetsCachePath;

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        ReloadSettingsCommand = new RelayCommand(LoadSettings);
        CheckForUpdatesCommand = new AsyncRelayCommand(async _ => await CheckForUpdatesAsync(), _ => !_isCheckingForUpdates);
        RefreshOffsetDatabaseCommand = new AsyncRelayCommand(async _ => await RefreshOffsetDatabaseAsync(), _ => !_isRefreshingOffsetDatabase);
        ClearAudioReadOffsetOverrideCommand = new RelayCommand(_ => ClearAudioReadOffsetOverride(), _ => _hasManualAudioReadOffsetOverride);
        InstallFfmpegCommand = new AsyncRelayCommand(async _ => await InstallFfmpegAsync(), _ => !_isInstallingTool);
        InstallPar2Command = new AsyncRelayCommand(async _ => await InstallPar2Async(), _ => !_isInstallingTool);

        LoadSettings();
        PropertyChanged += SaveOnChange;
    }

    private void SaveOnChange(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_autoSave || _loading || e.PropertyName == null || !PersistedProperties.Contains(e.PropertyName))
        {
            return;
        }
        try
        {
            // The FFmpeg path's own setter re-checks the tools; doing it on every change would start par2 each time.
            SaveSettings(refreshTools: false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Say so instead of losing the change silently; the value stays on screen and the next change retries.
            StatusMessage = $"Could not save the settings: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-evaluates whether <c>ffmpeg.exe</c> and <c>par2.exe</c> are available on the host machine.
    /// </summary>
    public void RefreshToolStatuses()
    {
        string? resolvedFfmpeg = FfmpegLocator.FindFfmpegExecutable(
            string.IsNullOrWhiteSpace(_ffmpegExecutablePath) ? null : _ffmpegExecutablePath);
        IsFfmpegInstalled = !string.IsNullOrEmpty(resolvedFfmpeg);
        // An older 1-click install (the LGPL build) has no libx264: say so, and the button replaces it with the full build.
        string? ffmpegMissing = IsFfmpegInstalled ? FfmpegCapabilities.Query(resolvedFfmpeg!)?.MissingDescription : null;
        FfmpegStatusText = !IsFfmpegInstalled
            ? "Not found — click 'Install FFmpeg (1-Click)' to download the full build automatically."
            : ffmpegMissing == null
                ? $"Installed: {resolvedFfmpeg}"
                : $"Installed: {resolvedFfmpeg}, but it has {ffmpegMissing}. Click 'Install FFmpeg (1-Click)' to replace it with the full build.";

        string? resolvedPar2 = Par2Runner.FindPar2Executable();
        IsPar2Installed = !string.IsNullOrEmpty(resolvedPar2);
        Par2ToolVersion? par2Version = IsPar2Installed ? Par2ToolVersion.Query(resolvedPar2!) : null;
        IsPar2UpdateAvailable = ExternalToolInstallerService.IsPar2UpdateRecommended(par2Version);
        Par2StatusText = IsPar2Installed
            ? $"Installed: {ExternalToolInstallerService.DescribeInstalledPar2(resolvedPar2!, par2Version)}"
            :"Not found — click 'Install par2 (1-Click)' to enable PAR2 archive recovery.";

        RefreshOffsetDatabaseStatusFromCache();
    }

    /// <summary>Reads the local <c>DriveOffsets.bin</c> cache status without making network calls.</summary>
    public void RefreshOffsetDatabaseStatusFromCache()
    {
        try
        {
            if (File.Exists(_driveOffsetsCachePath))
            {
                var entries = AccurateRipDriveOffsets.Parse(File.ReadAllBytes(_driveOffsetsCachePath));
                if (entries.Count > 0)
                {
                    DateTime mtime = File.GetLastWriteTimeUtc(_driveOffsetsCachePath);
                    bool fresh = DateTime.UtcNow - mtime < AccurateRipDriveOffsets.MaxCacheAge;
                    OffsetDatabaseStatusText = fresh
                        ? $"Cached: {entries.Count:N0} drives (updated {mtime:yyyy-MM-dd})"
                        : $"Cached (stale, >30 days): {entries.Count:N0} drives (updated {mtime:yyyy-MM-dd})";
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        OffsetDatabaseStatusText = "Not cached — click 'Refresh Offset Database' to download AccurateRip drive offsets.";
    }

    /// <summary>
    /// Downloads or refreshes AccurateRip's <c>DriveOffsets.bin</c> cache; offline failures fall back gracefully
    /// to an existing stale cache or empty list without throwing.
    /// </summary>
    public async Task<DriveOffsetDatabaseLoadResult> RefreshOffsetDatabaseAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        IsRefreshingOffsetDatabase = true;
        OffsetDatabaseStatusText = "Refreshing AccurateRip drive offset database...";
        HttpClient? ownedHttp = null;
        try
        {
            var client = httpClient ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            var result = await AccurateRipDriveOffsets.LoadDatabaseAsync(client, _driveOffsetsCachePath, forceRefresh: true, ct);
            OffsetDatabaseStatusText = result.Status switch
            {
                DriveOffsetDatabaseStatus.Downloaded =>
                    $"Updated: {result.Entries.Count:N0} drives ({result.CacheTimestampUtc:yyyy-MM-dd})",
                DriveOffsetDatabaseStatus.FreshCache =>
                    $"Cached: {result.Entries.Count:N0} drives",
                DriveOffsetDatabaseStatus.StaleCacheFallback =>
                    $"Offline fallback (using cached copy: {result.Entries.Count:N0} drives)",
                _ => string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Unavailable (offline and no cached DriveOffsets.bin)"
                    : $"Unavailable ({result.ErrorMessage})"
            };
            StatusMessage = $"Offset database: {OffsetDatabaseStatusText}";
            OffsetDatabaseRefreshed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            OffsetDatabaseStatusText = $"Unavailable ({ex.Message})";
            StatusMessage = $"Offset database refresh failed: {ex.Message}";
            return new DriveOffsetDatabaseLoadResult([], DriveOffsetDatabaseStatus.Unavailable, null, ex.Message);
        }
        finally
        {
            ownedHttp?.Dispose();
            IsRefreshingOffsetDatabase = false;
        }
    }

    /// <summary>Clears any saved manual Audio CD read-offset override so automatic resolution is used.</summary>
    public void ClearAudioReadOffsetOverride()
    {
        var settings = _settingsService.CurrentSettings.Clone();
        settings.AudioReadOffsetOverrideSamples = null;
        settings.AudioReadOffsetSamples = 0;
        _settingsService.CurrentSettings = settings;
        if (_autoSave)
        {
            _settingsService.Save();
        }
        UpdateReadOffsetSummary(settings);
        StatusMessage = "Audio CD read offset reset to automatic resolution.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateReadOffsetSummary(AppSettings settings)
    {
        int? ov = settings.GetEffectiveReadOffsetOverride();
        HasManualAudioReadOffsetOverride = ov.HasValue;
        AudioReadOffsetSummaryText = ov is int manual
            ? $"Manual override active: {ReadOffsetResolver.FormatSignedSamples(manual)} samples"
            : "Automatic (key-disc calibration > AccurateRip DB > 0)";
    }

    /// <summary>
    /// Loads settings from the service into view model properties.
    /// </summary>
    public void LoadSettings()
    {
        _loading = true;
        try
        {
            LoadValues();
        }
        finally
        {
            _loading = false;
        }
        RefreshToolStatuses();
        StatusMessage = !string.IsNullOrWhiteSpace(_settingsService.LastLoadWarning)
            ? _settingsService.LastLoadWarning
            : Strings.StatusSettingsSettingsLoaded;
    }

    private void LoadValues()
    {
        var settings = _settingsService.CurrentSettings;
        DefaultWriteSpeedMultiplier = settings.DefaultWriteSpeedMultiplier;
        VerifyAfterBurnByDefault = settings.VerifyAfterBurnByDefault;
        StrictVerify = settings.StrictVerify;
        EjectAfterBurnByDefault = settings.EjectAfterBurnByDefault;
        EnableBurnFreeByDefault = settings.EnableBurnFreeByDefault;
        TempStagingDirectory = settings.TempStagingDirectory;
        ThemeMode = settings.ThemeMode;
        UiLanguage = settings.UiLanguage;
        FfmpegExecutablePath = settings.FfmpegExecutablePath;
        EnableVirtualDrive = settings.EnableVirtualDrive;
        LogRingBufferCapacity = settings.LogRingBufferCapacity;
        LookupMusicBrainzByDefault = settings.LookupMusicBrainzByDefault;
        VerifyAccurateRipByDefault = settings.VerifyAccurateRipByDefault;
        VerifyCtdbByDefault = settings.VerifyCtdbByDefault;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        NotifyWhenBurnFinishes = settings.NotifyWhenBurnFinishes;
        DirectWritingForDvdRw = settings.DirectWritingForDvdRw;
        EnableExplorerShellIntegration = settings.EnableExplorerShellIntegration
            || ShellIntegrationManager.IsRegistered(_shellIntegrationRegistry);
        UpdateReadOffsetSummary(settings);
    }

    /// <summary>
    /// Persists current view model properties to the settings service and file.
    /// </summary>
    public void SaveSettings() => SaveSettings(refreshTools: true);

    private void SaveSettings(bool refreshTools)
    {
        // Start from the current settings so values owned by other screens (e.g. audio read offset) are kept.
        var settings = _settingsService.CurrentSettings.Clone();
        settings.DefaultWriteSpeedMultiplier = DefaultWriteSpeedMultiplier;
        settings.VerifyAfterBurnByDefault = VerifyAfterBurnByDefault;
        settings.StrictVerify = StrictVerify;
        settings.EjectAfterBurnByDefault = EjectAfterBurnByDefault;
        settings.EnableBurnFreeByDefault = EnableBurnFreeByDefault;
        settings.DirectWritingForDvdRw = DirectWritingForDvdRw;
        settings.TempStagingDirectory = TempStagingDirectory;
        settings.ThemeMode = ThemeMode;
        settings.UiLanguage = UiLanguage;
        settings.FfmpegExecutablePath = FfmpegExecutablePath;
        settings.EnableVirtualDrive = EnableVirtualDrive;
        settings.LogRingBufferCapacity = LogRingBufferCapacity;
        settings.LookupMusicBrainzByDefault = LookupMusicBrainzByDefault;
        settings.VerifyAccurateRipByDefault = VerifyAccurateRipByDefault;
        settings.VerifyCtdbByDefault = VerifyCtdbByDefault;
        settings.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        settings.NotifyWhenBurnFinishes = NotifyWhenBurnFinishes;
        settings.EnableExplorerShellIntegration = EnableExplorerShellIntegration;

        _settingsService.CurrentSettings = settings;
        _settingsService.Save();
        string? shellError = ApplyShellIntegration(EnableExplorerShellIntegration);
        ThemeManager.ApplyTheme(ThemeMode);
        if (_mainViewModel != null && SelectedRecorder != null)
        {
            _mainViewModel.SelectedRecorder = SelectedRecorder;
        }
        if (refreshTools)
        {
            RefreshToolStatuses();
        }
        StatusMessage = shellError ?? Strings.StatusSettingsSettingsSavedSuccessfully;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resets configuration back to defaults and refreshes view model properties.
    /// </summary>
    public void ResetSettings()
    {
        _settingsService.ResetToDefaults();
        string? shellError = ApplyShellIntegration(enabled: false);
        LoadSettings();
        if (_autoSave)
        {
            // Nothing else saves now that there is no Save button: write the defaults to the file too.
            _settingsService.Save();
        }
        StatusMessage = shellError ?? Strings.StatusSettingsSettingsResetToDefaultValues;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    private string? ApplyShellIntegration(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string? exePath = _appExecutablePathProvider();
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return "Could not locate IgnisDisc.exe for Explorer shell integration.";
                }

                ShellIntegrationManager.Register(_shellIntegrationRegistry, exePath);
            }
            else if (ShellIntegrationManager.HasAnyRegisteredKeys(_shellIntegrationRegistry))
            {
                ShellIntegrationManager.Unregister(_shellIntegrationRegistry);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Could not update Explorer shell integration: {ex.Message}";
        }
    }

    private static string? ResolveDefaultAppExecutablePath()
    {
        string baseCandidate = Path.Combine(AppContext.BaseDirectory, "IgnisDisc.exe");
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string fileName = Path.GetFileName(processPath);
            if (string.Equals(fileName, "IgnisDisc.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "CdBurner.App.exe", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
        }

        return baseCandidate;
    }

    /// <summary>
    /// Downloads and extracts the full Windows build of FFmpeg, updates <see cref="FfmpegExecutablePath"/>, and persists settings.
    /// </summary>
    public async Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken ct = default)
    {
        _isInstallingTool = true;
        InstallFfmpegCommand.RaiseCanExecuteChanged();
        InstallPar2Command.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<string>(msg =>
            {
                FfmpegStatusText = msg;
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallFfmpegAsync(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progress,
                ct).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                FfmpegExecutablePath = result.ExecutablePath;
                SaveSettings();
                StatusMessage = $"{ExternalToolInstallerService.FfmpegDisplayName} installed: {result.ExecutablePath}";
            }
            else
            {
                FfmpegStatusText = $"FFmpeg install failed: {result.ErrorMessage}";
                StatusMessage = FfmpegStatusText;
            }

            return result;
        }
        finally
        {
            _isInstallingTool = false;
            InstallFfmpegCommand.RaiseCanExecuteChanged();
            InstallPar2Command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Downloads and extracts <c>par2cmdline</c> (<c>par2.exe</c>) and refreshes workspace tool availability.
    /// </summary>
    public async Task<ToolInstallResult> InstallPar2Async(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken ct = default,
        string? expectedSha256Override = null)
    {
        _isInstallingTool = true;
        InstallFfmpegCommand.RaiseCanExecuteChanged();
        InstallPar2Command.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<string>(msg =>
            {
                Par2StatusText = msg;
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallPar2Async(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progress,
                ct,
                expectedSha256Override).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                RefreshToolStatuses();
                StatusMessage = $"par2cmdline installed: {result.ExecutablePath}";
                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Par2StatusText = $"par2 install failed: {result.ErrorMessage}";
                StatusMessage = Par2StatusText;
            }

            return result;
        }
        finally
        {
            _isInstallingTool = false;
            InstallFfmpegCommand.RaiseCanExecuteChanged();
            InstallPar2Command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Runs the update check once at startup when the user turned it on; the result appears on the Settings page.
    /// Never throws: a failed check must not affect startup.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.Success && result.IsUpdateAvailable)
            {
                StatusMessage = $"IgnisDisc {result.LatestVersion} is available (you have {result.CurrentVersion}). See Settings.";
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Checks GitHub Releases for updates.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        _isCheckingForUpdates = true;
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        UpdateStatusMessage = Strings.StatusSettingsCheckingGithubReleasesForUpdates;

        try
        {
            var result = await GitHubUpdateChecker.CheckForUpdateAsync(null, httpClient, null, ct);
            if (!result.Success)
            {
                UpdateStatusMessage = $"Update check failed: {result.ErrorMessage}";
            }
            else if (result.IsUpdateAvailable)
            {
                UpdateStatusMessage = $"Update available: {result.LatestVersion} (Current: {result.CurrentVersion}). Visit: {result.ReleaseUrl}";
            }
            else
            {
                UpdateStatusMessage = $"IgnisDisc is up to date (version {result.CurrentVersion}).";
            }
            return result;
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Update check error: {ex.Message}";
            return new UpdateCheckResult(false, string.Empty, string.Empty, string.Empty, ex.Message);
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
        }
    }
}
