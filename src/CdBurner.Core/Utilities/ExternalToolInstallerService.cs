using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CdBurner.Core.Archival;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Result of downloading and extracting an optional external tool executable.
/// </summary>
/// <param name="Success">True if the executable was downloaded and extracted successfully.</param>
/// <param name="ExecutablePath">Full path to the installed executable on disk, or null on failure.</param>
/// <param name="LicensePath">Full path to the extracted license file next to the executable, if present in the archive.</param>
/// <param name="ErrorMessage">Error description if the installation failed; otherwise null.</param>
public sealed record ToolInstallResult(
    bool Success,
    string? ExecutablePath,
    string? LicensePath,
    string? ErrorMessage = null);

/// <summary>
/// Downloads and extracts optional external command-line tools (<c>ffmpeg.exe</c> and <c>par2.exe</c>)
/// into per-user <c>%LOCALAPPDATA%\Programs</c> directories without requiring administrator rights.
/// </summary>
public static class ExternalToolInstallerService
{
    /// <summary>
    /// Official BtbN LGPL v2.1 Windows x64 FFmpeg release archive URL.
    /// </summary>
    public const string DefaultFfmpegLgplWin64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

    /// <summary>
    /// par2cmdline-turbo release installed by <see cref="InstallPar2Async"/>. par2cmdline-turbo
    /// (https://github.com/animetosho/par2cmdline-turbo, GPL-2.0-or-later) keeps par2cmdline's command line and the
    /// PAR 2.0 file format and replaces the slow parts (GF16 multiply, MD5/CRC32) with SIMD code; its recovery files
    /// verify and repair with par2cmdline 1.4.0 and the other way round.
    /// </summary>
    public const string Par2TurboVersion = "1.5.0";

    /// <summary>
    /// Official par2cmdline-turbo v1.5.0 Windows x64 release archive URL (pinned by <see cref="DefaultPar2WinX64Sha256"/>).
    /// </summary>
    public const string DefaultPar2WinX64Url =
        "https://github.com/animetosho/par2cmdline-turbo/releases/download/v1.5.0/par2cmdline-turbo-1.5.0-win-x64.zip";

    /// <summary>
    /// SHA-256 of <see cref="DefaultPar2WinX64Url"/>: the asset digest GitHub publishes for the release, re-computed
    /// from the downloaded file on 2026-11-14.
    /// </summary>
    public const string DefaultPar2WinX64Sha256 =
        "873a6f25822415f432224cc6f734407ca2732004184e002960fc68ed1e99c5d7";

    /// <summary>
    /// Official par2cmdline-turbo v1.5.0 Windows ARM64 release archive URL (pinned by <see cref="DefaultPar2WinArm64Sha256"/>).
    /// </summary>
    public const string DefaultPar2WinArm64Url =
        "https://github.com/animetosho/par2cmdline-turbo/releases/download/v1.5.0/par2cmdline-turbo-1.5.0-win-arm64.zip";

    /// <summary>
    /// SHA-256 of <see cref="DefaultPar2WinArm64Url"/>: the asset digest GitHub publishes for the release, re-computed
    /// from the downloaded file on 2026-11-14.
    /// </summary>
    public const string DefaultPar2WinArm64Sha256 =
        "bb73312747b706dcedd13b8d4de7acd20e17367ebb0b26ebc3ea7d55ea744735";

    /// <summary>
    /// User-Agent header sent on GitHub release downloads.
    /// </summary>
    public const string DefaultUserAgent = "IgnisDisc-OpticalStudio (+https://github.com/tether-works/IgnisDisc)";

    /// <summary>
    /// Returns the default per-user directory where FFmpeg is installed (<c>%LOCALAPPDATA%\Programs\ffmpeg</c>).
    /// </summary>
    public static string GetDefaultFfmpegInstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "ffmpeg");
    }

    /// <summary>
    /// Returns the default per-user directory where par2cmdline is installed (<c>%LOCALAPPDATA%\Programs\par2cmdline</c>).
    /// </summary>
    public static string GetDefaultPar2InstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "par2cmdline");
    }

    /// <summary>
    /// Downloads and extracts the LGPL Windows build of <c>ffmpeg.exe</c> (and its companion license text)
    /// into the specified or default per-user directory.
    /// </summary>
    public static Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string installDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetDefaultFfmpegInstallDirectory()
            : Path.GetFullPath(targetDirectory);
        string url = string.IsNullOrWhiteSpace(downloadUrlOverride)
            ? DefaultFfmpegLgplWin64Url
            : downloadUrlOverride;

        return DownloadAndExtractToolAsync(
            toolDisplayName: "FFmpeg (LGPL)",
            executableFileName: "ffmpeg.exe",
            licenseOutputFileName: "ffmpeg-LICENSE.txt",
            downloadUrl: url,
            // BtbN publishes FFmpeg only as a rolling "latest" build, so there is no fixed hash to pin.
            expectedSha256: null,
            installDirectory: installDir,
            httpClient: httpClient,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Downloads par2cmdline-turbo (<c>par2.exe</c>) into the specified or default per-user directory, replacing an
    /// older <c>par2.exe</c> there. The archive's SHA-256 must equal the pinned value for this PC's architecture
    /// (<see cref="DefaultPar2WinX64Sha256"/> or <see cref="DefaultPar2WinArm64Sha256"/>); any other download is
    /// refused before anything is extracted.
    /// </summary>
    /// <param name="targetDirectory">Install folder; default <see cref="GetDefaultPar2InstallDirectory"/>.</param>
    /// <param name="httpClient">Client to download with; a new one is created when null.</param>
    /// <param name="downloadUrlOverride">Different archive URL (tests, mirrors). The pinned hash still applies unless
    /// <paramref name="expectedSha256Override"/> gives the hash of that archive.</param>
    /// <param name="progress">Progress messages for the status line.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="expectedSha256Override">SHA-256 (64 hex digits) the archive must have instead of the pinned
    /// one. There is no way to skip the check.</param>
    public static Task<ToolInstallResult> InstallPar2Async(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? expectedSha256Override = null)
    {
        string installDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetDefaultPar2InstallDirectory()
            : Path.GetFullPath(targetDirectory);
        bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string url = string.IsNullOrWhiteSpace(downloadUrlOverride)
            ? (arm64 ? DefaultPar2WinArm64Url : DefaultPar2WinX64Url)
            : downloadUrlOverride;
        string expectedSha256 = string.IsNullOrWhiteSpace(expectedSha256Override)
            ? (arm64 ? DefaultPar2WinArm64Sha256 : DefaultPar2WinX64Sha256)
            : expectedSha256Override.Trim();

        return DownloadAndExtractToolAsync(
            toolDisplayName: $"par2cmdline-turbo {Par2TurboVersion}",
            executableFileName: "par2.exe",
            licenseOutputFileName: "par2-LICENSE.txt",
            downloadUrl: url,
            expectedSha256: expectedSha256,
            installDirectory: installDir,
            httpClient: httpClient,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="installed"/> is the original par2cmdline (for example 1.4.0) or a par2cmdline-turbo
    /// older than <see cref="Par2TurboVersion"/>, so the 1-click install should be offered as an update. False when
    /// the version is unknown (nothing to compare) or already current.
    /// </summary>
    public static bool IsPar2UpdateRecommended(Par2ToolVersion? installed)
    {
        if (installed == null)
        {
            return false;
        }

        return !installed.IsTurbo || installed.Version < Version.Parse(Par2TurboVersion);
    }

    /// <summary>
    /// Status text for an installed par2: its path and version, plus the update offer when
    /// <see cref="IsPar2UpdateRecommended"/> is true.
    /// </summary>
    public static string DescribeInstalledPar2(string par2Path, Par2ToolVersion? installed)
    {
        string version = installed?.ToString() ?? "version unknown";
        if (!IsPar2UpdateRecommended(installed))
        {
            return $"{par2Path} ({version})";
        }

        return $"{par2Path} ({version}). Update available: 'Install par2 (1-Click)' installs par2cmdline-turbo " +
               $"{Par2TurboVersion}, which creates recovery data about 10 times faster and reads the same files.";
    }

    private static bool IsSha256Hex(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task<ToolInstallResult> DownloadAndExtractToolAsync(
        string toolDisplayName,
        string executableFileName,
        string licenseOutputFileName,
        string downloadUrl,
        string? expectedSha256,
        string installDirectory,
        HttpClient? httpClient,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        bool ownsClient = httpClient == null;
        HttpClient client = httpClient ?? CreateDefaultHttpClient();

        string tempZipPath = Path.Combine(Path.GetTempPath(), $"ignisdisc_tool_{Guid.NewGuid():N}.zip");

        try
        {
            if (expectedSha256 != null && !IsSha256Hex(expectedSha256))
            {
                return new ToolInstallResult(false, null, null,
                    $"The expected SHA-256 for {toolDisplayName} is not 64 hexadecimal digits; nothing was downloaded.");
            }

            Directory.CreateDirectory(installDirectory);
            progress?.Report($"Downloading {toolDisplayName}...");

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
            {
                if (!request.Headers.UserAgent.Any())
                {
                    request.Headers.UserAgent.ParseAdd(DefaultUserAgent);
                }

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new ToolInstallResult(
                        false,
                        null,
                        null,
                        $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) while downloading {toolDisplayName}.");
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await networkStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes is > 0)
                    {
                        double pct = (double)totalRead / totalBytes.Value * 100.0;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                        progress?.Report($"Downloading {toolDisplayName}... {pct:F0}% ({mbRead:F1} / {mbTotal:F1} MB)");
                    }
                }
            }

            if (expectedSha256 != null)
            {
                // Checked before the archive is opened: a download that is not the pinned release is never unpacked.
                string actualSha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new ToolInstallResult(
                        false,
                        null,
                        null,
                        $"Refused {toolDisplayName}: the download's SHA-256 is {actualSha256}, expected " +
                        $"{expectedSha256.ToLowerInvariant()}. Nothing was installed.");
                }

                progress?.Report($"{toolDisplayName}: SHA-256 matches the pinned release.");
            }

            progress?.Report($"Extracting {executableFileName}...");

            string targetExePath = Path.Combine(installDirectory, executableFileName);
            string tempExePath = targetExePath + ".tmp";
            string? extractedLicensePath = null;

            using (var archive = ZipFile.OpenRead(tempZipPath))
            {
                var exeEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), executableFileName, StringComparison.OrdinalIgnoreCase) &&
                    e.Length > 0);

                if (exeEntry == null)
                {
                    return new ToolInstallResult(
                        false,
                        null,
                        null,
                        $"Archive did not contain '{executableFileName}'.");
                }

                exeEntry.ExtractToFile(tempExePath, overwrite: true);

                var licenseEntry = archive.Entries.FirstOrDefault(e =>
                {
                    string fileName = Path.GetFileName(e.FullName);
                    return e.Length > 0 &&
                           (fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase));
                });

                if (licenseEntry != null)
                {
                    string targetLicense = Path.Combine(installDirectory, licenseOutputFileName);
                    licenseEntry.ExtractToFile(targetLicense, overwrite: true);
                    extractedLicensePath = targetLicense;
                }
            }

            File.Move(tempExePath, targetExePath, overwrite: true);
            progress?.Report($"{toolDisplayName} installed at {targetExePath}.");

            return new ToolInstallResult(true, targetExePath, extractedLicensePath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolInstallResult(false, null, null, ex.Message);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }

            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return client;
    }
}
