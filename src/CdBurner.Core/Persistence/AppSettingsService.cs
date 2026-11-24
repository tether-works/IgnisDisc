using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Application visual theme mode.
/// </summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>
/// Persistent user configuration and defaults for burning, ripping, staging, and UI preferences.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Default write speed: <see cref="WriteSpeedPolicy.Recommended"/> (-1, the default) for the drive-aware choice,
    /// <see cref="WriteSpeedPolicy.Slowest"/> (-2) for the slowest speed the drive offers
    /// for the disc, <see cref="WriteSpeedPolicy.Maximum"/> (0) for the fastest, or a multiplier such as 16.
    /// </summary>
    public int DefaultWriteSpeedMultiplier { get; set; } = WriteSpeedPolicy.Recommended;

    /// <summary>
    /// Whether to automatically verify written media sectors against source hash after burning.
    /// </summary>
    public bool VerifyAfterBurnByDefault { get; set; } = true;

    /// <summary>
    /// Verify with every read forced from the disc itself. Off (the default): only the parts of the disc that can
    /// still be in the drive's memory after a burn are forced, which is several times faster.
    /// </summary>
    public bool StrictVerify { get; set; }

    /// <summary>
    /// Whether to eject the optical drive tray after a burn operation completes (on by default: an open tray is the
    /// clearest sign a disc is finished).
    /// </summary>
    public bool EjectAfterBurnByDefault { get; set; } = true;

    /// <summary>
    /// Whether the app announces a finished or failed burn with a sound, a flashing taskbar button and a message.
    /// </summary>
    public bool NotifyWhenBurnFinishes { get; set; } = true;

    /// <summary>
    /// Whether to enable buffer underrun protection (BURN-Proof / Burn-Free) by default.
    /// </summary>
    public bool EnableBurnFreeByDefault { get; set; } = true;

    /// <summary>
    /// Whether the burn screens turn on direct drive commands by themselves when a DVD-RW is in the drive
    /// (<see cref="CdBurner.Core.Burning.DirectWritingDefault"/>). On by default; the user's own tick on a screen wins.
    /// </summary>
    public bool DirectWritingForDvdRw { get; set; } = true;

    /// <summary>
    /// Temporary directory path used for ISO staging, audio decoding, and DVD transcode scratch files.
    /// </summary>
    public string TempStagingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "IgnisDisc", "Staging");

    /// <summary>
    /// Visual theme selection (Dark, Light, or follow Windows System preference).
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>
    /// Full file path or command name for the FFmpeg transcoder executable used in DVD-Video authoring.
    /// </summary>
    public string FfmpegExecutablePath { get; set; } = "ffmpeg.exe";

    /// <summary>
    /// Whether to expose the software-defined virtual optical drive backend simulator.
    /// </summary>
    public bool EnableVirtualDrive { get; set; } = false;

    /// <summary>
    /// In-memory diagnostic ring buffer capacity for the structured burn logger.
    /// </summary>
    public int LogRingBufferCapacity { get; set; } = 1000;

    /// <summary>
    /// Hardware read offset correction in samples (-10000 to +10000) for Audio CD ripping. Default is 0.
    /// </summary>
    public int AudioReadOffsetSamples { get; set; } = 0;

    /// <summary>
    /// Whether to enable C2 error pointer based secure extraction by default when ripping Audio CDs.
    /// </summary>
    public bool SecureAudioRipByDefault { get; set; } = true;

    /// <summary>
    /// Whether to look up album and track metadata from MusicBrainz by default when ripping Audio CDs.
    /// </summary>
    public bool LookupMusicBrainzByDefault { get; set; } = true;

    /// <summary>
    /// Whether to verify extracted audio track CRCs against AccurateRip by default.
    /// </summary>
    public bool VerifyAccurateRipByDefault { get; set; } = true;

    /// <summary>
    /// Whether to verify extracted audio track and disc CRCs against the CUETools Database (CTDB) by default.
    /// Strictly opt-in (default is false).
    /// </summary>
    public bool VerifyCtdbByDefault { get; set; } = false;

    /// <summary>
    /// Output formats chosen on the Audio CD Ripper screen, as a comma list ("wav", "flac", "mp3", "opus", "aac").
    /// Files saved before this setting existed have no value and read as "wav".
    /// </summary>
    public string RipFormats { get; set; } = "wav";

    /// <summary>MP3 quality chosen on the ripper and convert screens, for example "320 kbit/s" or "VBR V0 (about 245 kbit/s)".</summary>
    public string Mp3Quality { get; set; } = "320 kbit/s";

    /// <summary>Library folder for ripped albums. Empty saves the files in the rip output folder with flat names.</summary>
    public string RipLibraryFolder { get; set; } = string.Empty;

    /// <summary>File naming template used when a library folder is set (see AudioNamingTemplate for the tokens).</summary>
    public string RipNamingTemplate { get; set; } = CdBurner.Core.Audio.AudioNamingTemplate.Default;

    /// <summary>Whether to download the front cover from the Cover Art Archive when a MusicBrainz edition is known and no cover file is chosen.</summary>
    public bool FetchCoverArtByDefault { get; set; } = true;

    /// <summary>Whether to save folder.jpg (or .png) next to ripped albums that have a cover.</summary>
    public bool WriteFolderImage { get; set; } = true;

    /// <summary>Whether to apply Red Book 50/15 us digital de-emphasis to tracks flagged with pre-emphasis when ripping.</summary>
    public bool DeEmphasizePreEmphasisOnRip { get; set; } = false;

    /// <summary>
    /// Whether to automatically check for application updates on startup against GitHub Releases.
    /// Strictly opt-in (default is false).
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; set; } = false;

    /// <summary>The settings layout version this build writes.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Layout version of the stored settings. A file written before versions existed reads as 0.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// Brings settings from an older file up to <see cref="CurrentVersion"/>. Version 2 made the defaults safer;
    /// a value still equal to the old default is moved to the new one, because nobody chose it on purpose
    /// (the settings file stores every value, chosen or not). Returns true when anything changed.
    /// </summary>
    public bool UpgradeToCurrentVersion()
    {
        if (SettingsVersion >= CurrentVersion) return false;

        if (SettingsVersion < 2)
        {
            if (DefaultWriteSpeedMultiplier == 16) DefaultWriteSpeedMultiplier = WriteSpeedPolicy.Recommended; // old default 16x
            if (EnableVirtualDrive) EnableVirtualDrive = false;                                        // old default on
            if (!SecureAudioRipByDefault) SecureAudioRipByDefault = true;                              // old default off
        }

        if (SettingsVersion < 3)
        {
            if (!EjectAfterBurnByDefault) EjectAfterBurnByDefault = true;                              // old default off
        }

        SettingsVersion = CurrentVersion;
        return true;
    }

    /// <summary>
    /// Creates a deep copy clone of the current settings.
    /// </summary>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            DefaultWriteSpeedMultiplier = DefaultWriteSpeedMultiplier,
            VerifyAfterBurnByDefault = VerifyAfterBurnByDefault,
            StrictVerify = StrictVerify,
            EjectAfterBurnByDefault = EjectAfterBurnByDefault,
            NotifyWhenBurnFinishes = NotifyWhenBurnFinishes,
            EnableBurnFreeByDefault = EnableBurnFreeByDefault,
            DirectWritingForDvdRw = DirectWritingForDvdRw,
            TempStagingDirectory = TempStagingDirectory,
            ThemeMode = ThemeMode,
            FfmpegExecutablePath = FfmpegExecutablePath,
            EnableVirtualDrive = EnableVirtualDrive,
            LogRingBufferCapacity = LogRingBufferCapacity,
            AudioReadOffsetSamples = AudioReadOffsetSamples,
            SecureAudioRipByDefault = SecureAudioRipByDefault,
            LookupMusicBrainzByDefault = LookupMusicBrainzByDefault,
            VerifyAccurateRipByDefault = VerifyAccurateRipByDefault,
            VerifyCtdbByDefault = VerifyCtdbByDefault,
            RipFormats = RipFormats,
            Mp3Quality = Mp3Quality,
            RipLibraryFolder = RipLibraryFolder,
            RipNamingTemplate = RipNamingTemplate,
            FetchCoverArtByDefault = FetchCoverArtByDefault,
            WriteFolderImage = WriteFolderImage,
            DeEmphasizePreEmphasisOnRip = DeEmphasizePreEmphasisOnRip,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            SettingsVersion = SettingsVersion
        };
    }
}

/// <summary>
/// Service providing thread-safe persistence, loading, and retrieval of <see cref="AppSettings"/>.
/// </summary>
public class AppSettingsService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings;

    /// <summary>
    /// Diagnostic warning recorded when the last load had to recover from backup or fall back to defaults.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    /// <summary>
    /// Raised when a load encounters a corrupt settings file and recovers from backup or falls back to defaults.
    /// </summary>
    public event Action<string>? LoadWarning;

    /// <summary>
    /// Current in-memory configuration settings snapshot. Setting a new value updates the snapshot thread-safely.
    /// </summary>
    public AppSettings CurrentSettings
    {
        get
        {
            lock (_lock)
            {
                return _currentSettings.Clone();
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
            {
                _currentSettings = value.Clone();
            }
        }
    }

    /// <summary>
    /// Convenience property to read current configuration snapshot.
    /// </summary>
    public AppSettings Settings => CurrentSettings;

    /// <summary>
    /// Full file path on disk where settings are persisted.
    /// </summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Initializes a new settings service instance.
    /// </summary>
    /// <param name="customSettingsFilePath">Optional custom path to the settings JSON file.</param>
    public AppSettingsService(string? customSettingsFilePath = null)
    {
        _settingsFilePath = customSettingsFilePath ?? GetDefaultSettingsFilePath();
        _currentSettings = new AppSettings();
    }

    /// <summary>
    /// Computes the default application settings file path under %APPDATA%\IgnisDisc\settings.json.
    /// </summary>
    public static string GetDefaultSettingsFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "IgnisDisc", "settings.json");
    }

    /// <summary>
    /// Loads settings from disk synchronously. If the file does not exist, defaults are retained.
    /// </summary>
    public void Load()
    {
        _ioLock.Wait();
        string? warning;
        try
        {
            AppSettings loaded = LoadFromFile(_settingsFilePath, out warning);
            lock (_lock)
            {
                _currentSettings = loaded;
                LastLoadWarning = warning;
            }
        }
        finally
        {
            _ioLock.Release();
        }

        if (warning is not null)
        {
            LoadWarning?.Invoke(warning);
        }
    }

    /// <summary>
    /// Loads settings from disk asynchronously. If the file does not exist, defaults are retained.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? warning;
        try
        {
            var (settings, loadWarning) = await LoadFromFileWithWarningAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
            warning = loadWarning;
            lock (_lock)
            {
                _currentSettings = settings;
                LastLoadWarning = warning;
            }
        }
        finally
        {
            _ioLock.Release();
        }

        if (warning is not null)
        {
            LoadWarning?.Invoke(warning);
        }
    }

    /// <summary>
    /// Saves current settings to disk synchronously.
    /// </summary>
    public void Save()
    {
        _ioLock.Wait();
        try
        {
            AppSettings snapshot;
            lock (_lock)
            {
                snapshot = _currentSettings.Clone();
            }

            SaveToFile(snapshot, _settingsFilePath);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Saves current settings to disk asynchronously.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings snapshot;
            lock (_lock)
            {
                snapshot = _currentSettings.Clone();
            }

            await SaveToFileAsync(snapshot, _settingsFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Resets all settings to default values.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _currentSettings = new AppSettings();
            LastLoadWarning = null;
        }
    }

    /// <summary>
    /// Synchronously reads an <see cref="AppSettings"/> instance from a JSON file.
    /// Recovers from a <c>.bak</c> backup if the primary file is corrupt, or returns defaults with a warning.
    /// </summary>
    public static AppSettings LoadFromFile(string filePath) => LoadFromFile(filePath, out _);

    /// <summary>
    /// Synchronously reads an <see cref="AppSettings"/> instance from a JSON file and reports any recovery warning.
    /// </summary>
    public static AppSettings LoadFromFile(string filePath, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new AppSettings();
        }

        string fullPath = Path.GetFullPath(filePath);
        SemaphoreSlim pathLock = GetPathLock(fullPath);
        pathLock.Wait();
        try
        {
            if (!File.Exists(fullPath))
            {
                return new AppSettings();
            }

            try
            {
                return ReadAndNormalizeSettings(fullPath);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return TryRecoverFromBackup(fullPath, ex, out warning);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    /// <summary>
    /// Asynchronously reads an <see cref="AppSettings"/> instance from a JSON file.
    /// Recovers from a <c>.bak</c> backup if the primary file is corrupt, or returns defaults with a logged warning.
    /// </summary>
    public static async Task<AppSettings> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var (settings, _) = await LoadFromFileWithWarningAsync(filePath, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    private static async Task<(AppSettings Settings, string? Warning)> LoadFromFileWithWarningAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return (new AppSettings(), null);
        }

        string fullPath = Path.GetFullPath(filePath);
        SemaphoreSlim pathLock = GetPathLock(fullPath);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                return (new AppSettings(), null);
            }

            try
            {
                AppSettings settings = await ReadAndNormalizeSettingsAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return (settings, null);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return await TryRecoverFromBackupAsync(fullPath, ex, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    /// <summary>
    /// Synchronously writes an <see cref="AppSettings"/> instance to a JSON file atomically.
    /// </summary>
    public static void SaveToFile(AppSettings settings, string filePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        SemaphoreSlim pathLock = GetPathLock(fullPath);
        pathLock.Wait();
        try
        {
            EnsureParentDirectory(fullPath);
            settings.SettingsVersion = AppSettings.CurrentVersion; // written by this build: never upgrade it again on load
            string json = JsonSerializer.Serialize(settings, ProjectSerializer.JsonOptions);

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string bakTempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, Utf8NoBom))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, fullPath, overwrite: true);
                TryWriteBackup(fullPath, bakTempPath, json);
            }
            finally
            {
                TryDeleteFile(tempPath);
                TryDeleteFile(bakTempPath);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    /// <summary>
    /// Asynchronously writes an <see cref="AppSettings"/> instance to a JSON file atomically.
    /// </summary>
    public static async Task SaveToFileAsync(AppSettings settings, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        SemaphoreSlim pathLock = GetPathLock(fullPath);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureParentDirectory(fullPath);
            settings.SettingsVersion = AppSettings.CurrentVersion; // written by this build: never upgrade it again on load

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string bakTempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, ProjectSerializer.JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, fullPath, overwrite: true);
                await TryWriteBackupAsync(fullPath, bakTempPath, settings, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteFile(tempPath);
                TryDeleteFile(bakTempPath);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    private static SemaphoreSlim GetPathLock(string fullPath)
        => PathLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));

    private static void EnsureParentDirectory(string fullPath)
    {
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static AppSettings ReadAndNormalizeSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, ProjectSerializer.JsonOptions)
            ?? throw new InvalidDataException($"Settings file '{path}' deserialized to null.");
        NormalizeSettings(settings);
        return settings;
    }

    private static async Task<AppSettings> ReadAndNormalizeSettingsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, ProjectSerializer.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Settings file '{path}' deserialized to null.");
        NormalizeSettings(settings);
        return settings;
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.LogRingBufferCapacity = Math.Clamp(settings.LogRingBufferCapacity, 10, 100_000);
        settings.AudioReadOffsetSamples = Math.Clamp(settings.AudioReadOffsetSamples, -10000, 10000);
        settings.UpgradeToCurrentVersion();
    }

    private static AppSettings TryRecoverFromBackup(string fullPath, Exception primaryError, out string warning)
    {
        string backupPath = fullPath + ".bak";
        if (File.Exists(backupPath))
        {
            try
            {
                AppSettings recovered = ReadAndNormalizeSettings(backupPath);
                warning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}); recovered from backup '{backupPath}'.";
                Trace.TraceWarning(warning);
                return recovered;
            }
            catch (Exception bakEx) when (bakEx is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                warning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}) and backup '{backupPath}' failed ({bakEx.Message}); defaults are in use.";
                Trace.TraceWarning(warning);
                return new AppSettings();
            }
        }

        warning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}); defaults are in use.";
        Trace.TraceWarning(warning);
        return new AppSettings();
    }

    private static async Task<(AppSettings Settings, string Warning)> TryRecoverFromBackupAsync(
        string fullPath,
        Exception primaryError,
        CancellationToken cancellationToken)
    {
        string backupPath = fullPath + ".bak";
        if (File.Exists(backupPath))
        {
            try
            {
                AppSettings recovered = await ReadAndNormalizeSettingsAsync(backupPath, cancellationToken).ConfigureAwait(false);
                string warning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}); recovered from backup '{backupPath}'.";
                Trace.TraceWarning(warning);
                return (recovered, warning);
            }
            catch (Exception bakEx) when (bakEx is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                string warning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}) and backup '{backupPath}' failed ({bakEx.Message}); defaults are in use.";
                Trace.TraceWarning(warning);
                return (new AppSettings(), warning);
            }
        }

        string fallbackWarning = $"Settings file '{fullPath}' could not be read ({primaryError.Message}); defaults are in use.";
        Trace.TraceWarning(fallbackWarning);
        return (new AppSettings(), fallbackWarning);
    }

    private static void TryWriteBackup(string fullPath, string bakTempPath, string json)
    {
        try
        {
            using (var stream = new FileStream(bakTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(bakTempPath, fullPath + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not update settings backup '{fullPath}.bak': {ex.Message}");
        }
    }

    private static async Task TryWriteBackupAsync(
        string fullPath,
        string bakTempPath,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = new FileStream(bakTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, ProjectSerializer.JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(bakTempPath, fullPath + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not update settings backup '{fullPath}.bak': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
