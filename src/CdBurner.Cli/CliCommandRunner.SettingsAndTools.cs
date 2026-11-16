using System.Net.Http;
using System.Runtime.InteropServices;
using CdBurner.Core;
using CdBurner.Core.Archival;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Core.Subtitles;
using CdBurner.Core.Utilities;

namespace CdBurner.Cli;

/// <summary>
/// Settings, optional-tool discovery/installation, and update-check CLI commands:
/// <c>settings</c>, <c>tools</c>, <c>install-ffmpeg</c>, <c>install-par2</c>, and <c>check-update</c>.
/// </summary>
public static partial class CliCommandRunner
{
    private static readonly IReadOnlyList<string> SupportedSettingsKeys =
    [
        "defaultWriteSpeedMultiplier",
        "verifyAfterBurnByDefault",
        "strictVerify",
        "ejectAfterBurnByDefault",
        "enableBurnFreeByDefault",
        "directWritingForDvdRw",
        "tempStagingDirectory",
        "themeMode",
        "ffmpegExecutablePath",
        "enableVirtualDrive",
        "logRingBufferCapacity",
        "audioReadOffsetSamples",
        "secureAudioRipByDefault",
        "lookupMusicBrainzByDefault",
        "verifyAccurateRipByDefault",
        "checkForUpdatesOnStartup"
    ];

    private static string NormalizeSettingKey(string key) =>
        key.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

    private static AppSettingsService OpenSettingsService(CommandLineArgs cmd)
    {
        string? customFile = cmd.GetOption("settings-file") ?? cmd.GetOption("config");
        var service = string.IsNullOrWhiteSpace(customFile)
            ? new AppSettingsService()
            : new AppSettingsService(Path.GetFullPath(customFile));
        service.Load();
        return service;
    }

    private static string ResolveCanonicalSettingKey(string rawKey)
    {
        string normalized = NormalizeSettingKey(rawKey);
        foreach (string key in SupportedSettingsKeys)
        {
            if (string.Equals(normalized, NormalizeSettingKey(key), StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        throw new CommandLineException(
            $"Unknown setting '{rawKey}'. Valid settings: {string.Join(", ", SupportedSettingsKeys)}.");
    }

    private static object? GetSettingValue(AppSettings settings, string canonicalKey) => canonicalKey switch
    {
        "defaultWriteSpeedMultiplier" => settings.DefaultWriteSpeedMultiplier,
        "verifyAfterBurnByDefault" => settings.VerifyAfterBurnByDefault,
        "strictVerify" => settings.StrictVerify,
        "ejectAfterBurnByDefault" => settings.EjectAfterBurnByDefault,
        "enableBurnFreeByDefault" => settings.EnableBurnFreeByDefault,
        "directWritingForDvdRw" => settings.DirectWritingForDvdRw,
        "tempStagingDirectory" => settings.TempStagingDirectory,
        "themeMode" => settings.ThemeMode.ToString(),
        "ffmpegExecutablePath" => settings.FfmpegExecutablePath,
        "enableVirtualDrive" => settings.EnableVirtualDrive,
        "logRingBufferCapacity" => settings.LogRingBufferCapacity,
        "audioReadOffsetSamples" => settings.AudioReadOffsetSamples,
        "secureAudioRipByDefault" => settings.SecureAudioRipByDefault,
        "lookupMusicBrainzByDefault" => settings.LookupMusicBrainzByDefault,
        "verifyAccurateRipByDefault" => settings.VerifyAccurateRipByDefault,
        "checkForUpdatesOnStartup" => settings.CheckForUpdatesOnStartup,
        _ => throw new CommandLineException($"Unknown setting '{canonicalKey}'.")
    };

    private static void ApplySettingValue(AppSettings settings, string canonicalKey, string rawValue)
    {
        switch (canonicalKey)
        {
            case "defaultWriteSpeedMultiplier":
                settings.DefaultWriteSpeedMultiplier = ParseSettingSpeedMultiplier(rawValue);
                break;
            case "verifyAfterBurnByDefault":
                settings.VerifyAfterBurnByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "strictVerify":
                settings.StrictVerify = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "ejectAfterBurnByDefault":
                settings.EjectAfterBurnByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "enableBurnFreeByDefault":
                settings.EnableBurnFreeByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "directWritingForDvdRw":
                settings.DirectWritingForDvdRw = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "tempStagingDirectory":
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    throw new CommandLineException("tempStagingDirectory must be a non-empty folder path.");
                }
                settings.TempStagingDirectory = rawValue.Trim();
                break;
            case "themeMode":
                if (!Enum.TryParse<ThemeMode>(rawValue.Trim(), ignoreCase: true, out var theme) ||
                    !Enum.IsDefined(theme))
                {
                    throw new CommandLineException(
                        $"Invalid themeMode '{rawValue}'. Use Dark, Light, or System.");
                }
                settings.ThemeMode = theme;
                break;
            case "ffmpegExecutablePath":
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    throw new CommandLineException("ffmpegExecutablePath must be a non-empty path or executable name (for example ffmpeg.exe).");
                }
                settings.FfmpegExecutablePath = rawValue.Trim();
                break;
            case "enableVirtualDrive":
                settings.EnableVirtualDrive = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "logRingBufferCapacity":
                if (!int.TryParse(rawValue.Trim(), out int cap) || cap < 10 || cap > 100_000)
                {
                    throw new CommandLineException("logRingBufferCapacity must be an integer between 10 and 100000.");
                }
                settings.LogRingBufferCapacity = cap;
                break;
            case "audioReadOffsetSamples":
                if (!int.TryParse(rawValue.Trim(), out int offset) || offset < -10_000 || offset > 10_000)
                {
                    throw new CommandLineException("audioReadOffsetSamples must be an integer between -10000 and 10000.");
                }
                settings.AudioReadOffsetSamples = offset;
                break;
            case "secureAudioRipByDefault":
                settings.SecureAudioRipByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "lookupMusicBrainzByDefault":
                settings.LookupMusicBrainzByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "verifyAccurateRipByDefault":
                settings.VerifyAccurateRipByDefault = ParseSettingBool(canonicalKey, rawValue);
                break;
            case "checkForUpdatesOnStartup":
                settings.CheckForUpdatesOnStartup = ParseSettingBool(canonicalKey, rawValue);
                break;
            default:
                throw new CommandLineException($"Unknown setting '{canonicalKey}'.");
        }
    }

    private static bool ParseSettingBool(string key, string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new CommandLineException($"Setting '{key}' expects a boolean (true/false, on/off, yes/no, 1/0), not '{raw}'.")
        };
    }

    private static int ParseSettingSpeedMultiplier(string raw)
    {
        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "recommended", StringComparison.OrdinalIgnoreCase))
        {
            return WriteSpeedPolicy.Recommended;
        }
        if (string.Equals(trimmed, "slowest", StringComparison.OrdinalIgnoreCase))
        {
            return WriteSpeedPolicy.Slowest;
        }
        if (string.Equals(trimmed, "max", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "maximum", StringComparison.OrdinalIgnoreCase))
        {
            return WriteSpeedPolicy.Maximum;
        }

        string numeric = trimmed.TrimEnd('x', 'X');
        if (int.TryParse(numeric, out int multiplier) &&
            (multiplier is WriteSpeedPolicy.Slowest or WriteSpeedPolicy.Recommended or WriteSpeedPolicy.Maximum ||
             (multiplier >= 1 && multiplier <= 100)))
        {
            return multiplier;
        }

        throw new CommandLineException(
            $"Invalid defaultWriteSpeedMultiplier '{raw}'. Use 'recommended' (-1), 'slowest' (-2), 'max' (0), or a multiplier from 1 to 100.");
    }

    private static Dictionary<string, object?> BuildSettingsMap(AppSettings settings)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string key in SupportedSettingsKeys)
        {
            map[key] = GetSettingValue(settings, key);
        }
        return map;
    }

    private static string FormatSpeedPolicyDisplay(int multiplier) => multiplier switch
    {
        WriteSpeedPolicy.Recommended => "-1 (recommended)",
        WriteSpeedPolicy.Slowest => "-2 (slowest)",
        WriteSpeedPolicy.Maximum => "0 (max)",
        _ => $"{multiplier}x"
    };

    private static int HandleSettingsCommand(CommandLineArgs cmd, TextWriter stdout)
    {
        string? action = cmd.Positionals.Count > 0 ? cmd.Positionals[0].ToLowerInvariant() : null;
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new CommandLineException(
                "Missing settings action. Use: settings get [key], settings set <key> <value>, or settings reset.");
        }

        var service = OpenSettingsService(cmd);

        switch (action)
        {
            case "get":
            {
                string? rawKey = cmd.GetOption("key") ?? (cmd.Positionals.Count > 1 ? cmd.Positionals[1] : null);
                if (cmd.Positionals.Count > 2)
                {
                    throw new CommandLineException($"Unexpected extra argument '{cmd.Positionals[2]}' for 'settings get'.");
                }

                if (!string.IsNullOrWhiteSpace(rawKey))
                {
                    string canonical = ResolveCanonicalSettingKey(rawKey);
                    object? value = GetSettingValue(service.CurrentSettings, canonical);
                    if (cmd.IsJson)
                    {
                        WriteJsonResult(stdout, new
                        {
                            success = true,
                            action = "get",
                            settingsFile = service.SettingsFilePath,
                            key = canonical,
                            value
                        });
                    }
                    else
                    {
                        stdout.WriteLine($"{canonical} = {value}");
                    }
                    return 0;
                }

                var map = BuildSettingsMap(service.CurrentSettings);
                if (cmd.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        action = "get",
                        settingsFile = service.SettingsFilePath,
                        settings = map
                    });
                }
                else
                {
                    stdout.WriteLine(LegalBanner.VersionNotice);
                    stdout.WriteLine($"Settings file: {service.SettingsFilePath}");
                    foreach (var kvp in map)
                    {
                        string display = kvp.Key == "defaultWriteSpeedMultiplier" && kvp.Value is int m
                            ? FormatSpeedPolicyDisplay(m)
                            : kvp.Value?.ToString() ?? string.Empty;
                        stdout.WriteLine($"  {kvp.Key,-30} {display}");
                    }
                }
                return 0;
            }

            case "set":
            {
                string? rawKey = cmd.GetOption("key") ?? (cmd.Positionals.Count > 1 ? cmd.Positionals[1] : null);
                string? rawValue = cmd.GetOption("value") ?? (cmd.Positionals.Count > 2 ? cmd.Positionals[2] : null);
                if (cmd.Positionals.Count > 3)
                {
                    throw new CommandLineException($"Unexpected extra argument '{cmd.Positionals[3]}' for 'settings set'.");
                }
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    throw new CommandLineException("Missing setting key. Use: settings set <key> <value> (or --key <k> --value <v>).");
                }
                if (rawValue == null)
                {
                    throw new CommandLineException($"Missing value for setting '{rawKey}'. Use: settings set {rawKey} <value>.");
                }

                string canonical = ResolveCanonicalSettingKey(rawKey);
                var updated = service.CurrentSettings.Clone();
                ApplySettingValue(updated, canonical, rawValue);
                service.CurrentSettings = updated;
                service.Save();

                object? persistedValue = GetSettingValue(service.CurrentSettings, canonical);
                if (cmd.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        action = "set",
                        settingsFile = service.SettingsFilePath,
                        key = canonical,
                        value = persistedValue
                    });
                }
                else
                {
                    stdout.WriteLine($"Saved setting: {canonical} = {persistedValue}");
                    stdout.WriteLine($"Settings file: {service.SettingsFilePath}");
                }
                return 0;
            }

            case "reset":
            {
                if (cmd.Positionals.Count > 1)
                {
                    throw new CommandLineException($"Unexpected argument '{cmd.Positionals[1]}' for 'settings reset'.");
                }

                service.ResetToDefaults();
                service.Save();
                var map = BuildSettingsMap(service.CurrentSettings);

                if (cmd.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        action = "reset",
                        settingsFile = service.SettingsFilePath,
                        settings = map
                    });
                }
                else
                {
                    stdout.WriteLine($"Settings reset to defaults at {service.SettingsFilePath}.");
                }
                return 0;
            }

            default:
                throw new CommandLineException(
                    $"Unknown settings action '{action}'. Use: get, set, or reset.");
        }
    }

    private static int HandleToolsCommand(CommandLineArgs cmd, TextWriter stdout)
    {
        string? ffmpegOverride = cmd.GetOption("ffmpeg");
        string? par2Override = cmd.GetOption("par2");

        string? ffmpegPath = FfmpegLocator.FindFfmpegExecutable(ffmpegOverride);
        string? par2Path = Par2Runner.FindPar2Executable(par2Override);
        Par2ToolVersion? par2Version = par2Path != null ? Par2ToolVersion.Query(par2Path) : null;
        bool par2UpdateRecommended = par2Path != null && ExternalToolInstallerService.IsPar2UpdateRecommended(par2Version);
        string? par2Description = par2Path != null
            ? ExternalToolInstallerService.DescribeInstalledPar2(par2Path, par2Version)
            : null;
        WhisperInstallation? whisper = WhisperLocator.Find();

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                ffmpeg = new
                {
                    available = ffmpegPath != null,
                    path = ffmpegPath
                },
                par2 = new
                {
                    available = par2Path != null,
                    path = par2Path,
                    product = par2Version?.Product,
                    version = par2Version?.Version.ToString(),
                    updateRecommended = par2UpdateRecommended,
                    summary = par2Description
                },
                subtitleTool = new
                {
                    available = whisper != null,
                    cliPath = whisper?.CliPath,
                    modelPath = whisper?.ModelPath,
                    build = whisper?.Build.ToString(),
                    summary = whisper?.Describe()
                }
            });
            return 0;
        }

        stdout.WriteLine(LegalBanner.VersionNotice);
        stdout.WriteLine();
        stdout.WriteLine("Optional External Tools:");
        stdout.WriteLine($"  ffmpeg:        {(ffmpegPath != null ? $"available ({ffmpegPath})" : "not found (install with: ignisdisc-cli install-ffmpeg)")}");
        stdout.WriteLine($"  par2:          {(par2Path != null ? par2Description : "not found (install with: ignisdisc-cli install-par2)")}");
        stdout.WriteLine($"  subtitle tool: {(whisper != null ? whisper.Describe() : "not found (install with: ignisdisc-cli install-subtitle-tool)")}");
        return 0;
    }

    internal static async Task<int> HandleInstallFfmpegAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null)
    {
        string targetDir = ResolveToolInstallDirectory(
            cmd.GetOption("dir"),
            ExternalToolInstallerService.GetDefaultFfmpegInstallDirectory());
        string targetExe = Path.Combine(targetDir, "ffmpeg.exe");
        bool planOnly = cmd.HasFlag("plan-only");

        if (planOnly)
        {
            string? existing = FfmpegLocator.FindFfmpegExecutable(targetExe);
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    planOnly = true,
                    tool = "ffmpeg",
                    installDirectory = targetDir,
                    targetExecutable = targetExe,
                    downloadUrl = downloadUrlOverride ?? ExternalToolInstallerService.DefaultFfmpegLgplWin64Url,
                    alreadyInstalled = existing != null,
                    existingPath = existing
                });
                return 0;
            }

            stdout.WriteLine($"FFmpeg install plan:");
            stdout.WriteLine($"  Directory: {targetDir}");
            stdout.WriteLine($"  Source:    {downloadUrlOverride ?? ExternalToolInstallerService.DefaultFfmpegLgplWin64Url}");
            stdout.WriteLine($"  Installed: {existing ?? "no"}");
            return 0;
        }

        if (File.Exists(targetExe) && !cmd.HasFlag("overwrite"))
        {
            throw new CommandLineException(
                $"'{targetExe}' already exists. Pass --overwrite to replace the existing ffmpeg executable.");
        }

        var progress = cmd.IsJson ? null : new ThrottledLineProgress(stderr);
        var result = await ExternalToolInstallerService.InstallFfmpegAsync(
            targetDir,
            httpClient,
            downloadUrlOverride,
            progress).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                tool = "ffmpeg",
                executablePath = result.ExecutablePath,
                licensePath = result.LicensePath,
                error = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            stderr.WriteLine($"FFmpeg install failed: {result.ErrorMessage}");
            return 1;
        }

        stdout.WriteLine($"FFmpeg installed: {result.ExecutablePath}");
        if (!string.IsNullOrWhiteSpace(result.LicensePath))
        {
            stdout.WriteLine($"License saved:    {result.LicensePath}");
        }
        return 0;
    }

    internal static async Task<int> HandleInstallPar2Async(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        string? expectedSha256Override = null)
    {
        string targetDir = ResolveToolInstallDirectory(
            cmd.GetOption("dir"),
            ExternalToolInstallerService.GetDefaultPar2InstallDirectory());
        string targetExe = Path.Combine(targetDir, "par2.exe");
        bool planOnly = cmd.HasFlag("plan-only");

        if (planOnly)
        {
            bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            string defaultUrl = arm64
                ? ExternalToolInstallerService.DefaultPar2WinArm64Url
                : ExternalToolInstallerService.DefaultPar2WinX64Url;
            string defaultSha = arm64
                ? ExternalToolInstallerService.DefaultPar2WinArm64Sha256
                : ExternalToolInstallerService.DefaultPar2WinX64Sha256;
            string? existing = Par2Runner.FindPar2Executable(targetExe);
            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    planOnly = true,
                    tool = "par2",
                    installDirectory = targetDir,
                    targetExecutable = targetExe,
                    version = ExternalToolInstallerService.Par2TurboVersion,
                    downloadUrl = downloadUrlOverride ?? defaultUrl,
                    expectedSha256 = expectedSha256Override ?? defaultSha,
                    alreadyInstalled = existing != null,
                    existingPath = existing
                });
                return 0;
            }

            stdout.WriteLine($"par2cmdline-turbo {ExternalToolInstallerService.Par2TurboVersion} install plan:");
            stdout.WriteLine($"  Directory: {targetDir}");
            stdout.WriteLine($"  Source:    {downloadUrlOverride ?? defaultUrl}");
            stdout.WriteLine($"  Installed: {existing ?? "no"}");
            return 0;
        }

        if (File.Exists(targetExe) && !cmd.HasFlag("overwrite"))
        {
            throw new CommandLineException(
                $"'{targetExe}' already exists. Pass --overwrite to replace the existing par2 executable.");
        }

        var progress = cmd.IsJson ? null : new ThrottledLineProgress(stderr);
        var result = await ExternalToolInstallerService.InstallPar2Async(
            targetDir,
            httpClient,
            downloadUrlOverride,
            progress,
            CancellationToken.None,
            expectedSha256Override).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                tool = "par2",
                version = ExternalToolInstallerService.Par2TurboVersion,
                executablePath = result.ExecutablePath,
                licensePath = result.LicensePath,
                error = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            stderr.WriteLine($"par2 install failed: {result.ErrorMessage}");
            return 1;
        }

        stdout.WriteLine($"par2 installed: {result.ExecutablePath}");
        if (!string.IsNullOrWhiteSpace(result.LicensePath))
        {
            stdout.WriteLine($"License saved:  {result.LicensePath}");
        }
        return 0;
    }

    internal static async Task<int> HandleCheckUpdateAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        HttpClient? httpClient = null,
        string? apiUrlOverride = null,
        string? currentVersionOverride = null)
    {
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(
            currentVersionOverride,
            httpClient,
            apiUrlOverride).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                isUpdateAvailable = result.IsUpdateAvailable,
                currentVersion = result.CurrentVersion,
                latestVersion = result.LatestVersion,
                releaseUrl = result.ReleaseUrl,
                error = result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        if (!result.Success)
        {
            stderr.WriteLine($"Update check failed: {result.ErrorMessage}");
            return 1;
        }

        if (result.IsUpdateAvailable)
        {
            stdout.WriteLine($"Update available: {result.LatestVersion} (current: {result.CurrentVersion}).");
            stdout.WriteLine($"Download: {result.ReleaseUrl}");
        }
        else
        {
            stdout.WriteLine($"IgnisDisc is up to date (version {result.CurrentVersion}, latest {result.LatestVersion}).");
        }

        return 0;
    }

    private static string ResolveToolInstallDirectory(string? requestedDir, string defaultDir)
    {
        if (string.IsNullOrWhiteSpace(requestedDir))
        {
            return defaultDir;
        }

        return Path.GetFullPath(requestedDir.Trim());
    }
}
