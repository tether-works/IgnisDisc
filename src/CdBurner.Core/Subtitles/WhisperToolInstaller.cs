using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// An installed whisper.cpp program and speech model.
/// </summary>
/// <param name="CliPath">Full path of <c>whisper-cli.exe</c>.</param>
/// <param name="ModelPath">Full path of the speech model file.</param>
/// <param name="Build">Installed build, or null when the folder was not made by the 1-click install.</param>
/// <param name="VadModelPath">Voice activity detection model, or null when it is not installed (an install from
/// before it was added); generation then runs without it.</param>
public sealed record WhisperInstallation(string CliPath, string ModelPath, WhisperBuildKind? Build, string? VadModelPath = null)
{
    /// <summary>Model file name, for example <c>ggml-large-v3-turbo.bin</c>.</summary>
    public string ModelName => Path.GetFileName(ModelPath);

    /// <summary>Short description for status lines.</summary>
    public string Describe() => Build switch
    {
        WhisperBuildKind.CudaX64 => $"whisper.cpp {WhisperCatalog.WhisperVersion} (NVIDIA GPU), model {ModelName}",
        WhisperBuildKind.VulkanX64 => $"whisper.cpp {WhisperCatalog.WhisperVersion} (AMD or Intel GPU, Vulkan), model {ModelName}",
        WhisperBuildKind.CpuX64 or WhisperBuildKind.CpuArm64 => $"whisper.cpp {WhisperCatalog.WhisperVersion} (processor), model {ModelName}",
        _ => $"whisper.cpp, model {ModelName}"
    };
}

/// <summary>
/// Result of the 1-click subtitle tool install.
/// </summary>
public sealed record WhisperInstallResult(bool Success, WhisperInstallation? Installation, string? ErrorMessage = null);

/// <summary>
/// Finds the whisper.cpp program and model installed by <see cref="WhisperToolInstaller"/>.
/// </summary>
public static class WhisperLocator
{
    /// <summary>Program file name.</summary>
    public const string CliFileName = "whisper-cli.exe";

    /// <summary>Marker file the installer writes next to the program: build kind and version.</summary>
    public const string BuildMarkerFileName = "ignisdisc-build.txt";

    /// <summary>
    /// Default install folder: <c>%LOCALAPPDATA%\Programs\whisper.cpp</c> (per user, no administrator rights).
    /// </summary>
    public static string GetDefaultInstallRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "whisper.cpp");

    /// <summary>Folder holding <c>whisper-cli.exe</c> and its libraries.</summary>
    public static string GetBinDirectory(string installRoot) => Path.Combine(installRoot, "bin");

    /// <summary>Folder holding the speech models.</summary>
    public static string GetModelsDirectory(string installRoot) => Path.Combine(installRoot, "models");

    /// <summary>
    /// The installed program and best installed model, or null when either is missing.
    /// </summary>
    public static WhisperInstallation? Find(string? installRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(installRoot) ? GetDefaultInstallRoot() : Path.GetFullPath(installRoot);
        try
        {
            string cli = Path.Combine(GetBinDirectory(root), CliFileName);
            if (!File.Exists(cli))
            {
                return null;
            }

            foreach (WhisperDownload model in WhisperCatalog.ModelsBestFirst)
            {
                string modelPath = Path.Combine(GetModelsDirectory(root), model.FileName);
                var info = new FileInfo(modelPath);
                // Downloads land as "<name>.part" and are renamed only after the SHA-256 matched, so a file with the
                // model's own name is a finished one.
                if (info.Exists && info.Length > 0)
                {
                    string vadPath = Path.Combine(GetModelsDirectory(root), WhisperCatalog.SileroVadModel.FileName);
                    return new WhisperInstallation(cli, modelPath, ReadBuildMarker(root),
                        File.Exists(vadPath) && new FileInfo(vadPath).Length > 0 ? vadPath : null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// The build recorded by the installer, or null when the marker is missing or names another version.
    /// </summary>
    public static WhisperBuildKind? ReadBuildMarker(string installRoot)
    {
        try
        {
            string marker = Path.Combine(GetBinDirectory(installRoot), BuildMarkerFileName);
            if (!File.Exists(marker))
            {
                return null;
            }

            string[] parts = File.ReadAllText(marker).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                parts[1] == WhisperCatalog.WhisperVersion &&
                Enum.TryParse(parts[0], ignoreCase: false, out WhisperBuildKind build))
            {
                return build;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    internal static string FormatBuildMarker(WhisperBuildKind build) => $"{build} {WhisperCatalog.WhisperVersion}";
}

/// <summary>
/// 1-click install of whisper.cpp and one speech model for "Generate subtitles". Every download is pinned by
/// SHA-256 (<see cref="WhisperCatalog"/>) and checked before it is unpacked or used; nothing is bundled with
/// IgnisDisc. Files go to <c>%LOCALAPPDATA%\Programs\whisper.cpp</c>: <c>bin</c> (the program and its libraries),
/// <c>models</c>, and a notice with the source and license.
/// </summary>
public static class WhisperToolInstaller
{
    private const string LockFileName = "install.lock";
    private const string NoticeFileName = "NOTICE-IgnisDisc.txt";

    /// <summary>
    /// Downloads and installs the program and model in <paramref name="plan"/>. Parts already installed and intact
    /// (same build and version; model file with the pinned hash) are not downloaded again.
    /// </summary>
    /// <param name="plan">What to install; usually <see cref="WhisperCatalog.ChoosePlanForThisPc"/>.</param>
    /// <param name="installRoot">Install folder; default <see cref="WhisperLocator.GetDefaultInstallRoot"/>.</param>
    /// <param name="httpClient">Client to download with; a new one with no overall time limit is created when null.</param>
    /// <param name="progress">Status messages.</param>
    /// <param name="cancellationToken">Cancels the install; partial files are removed.</param>
    public static async Task<WhisperInstallResult> InstallAsync(
        WhisperInstallPlan plan,
        string? installRoot = null,
        HttpClient? httpClient = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string root = string.IsNullOrWhiteSpace(installRoot) ? WhisperLocator.GetDefaultInstallRoot() : Path.GetFullPath(installRoot);

        if (!ExternalToolInstallerService.IsSha256Hex(plan.Program.Sha256) ||
            !ExternalToolInstallerService.IsSha256Hex(plan.Model.Sha256) ||
            !ExternalToolInstallerService.IsSha256Hex(plan.Vad.Sha256))
        {
            return new WhisperInstallResult(false, null, "The pinned SHA-256 of a subtitle tool download is malformed; nothing was downloaded.");
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WhisperInstallResult(false, null, $"Cannot create {root}: {ex.Message}");
        }

        FileStream? installLock;
        try
        {
            // Only one install at a time (the app and the command line could both start one).
            installLock = new FileStream(Path.Combine(root, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return new WhisperInstallResult(false, null, "Another subtitle tool install is already running.");
        }

        bool ownsClient = httpClient == null;
        HttpClient client = httpClient ?? CreateLargeDownloadClient();
        try
        {
            string binDir = WhisperLocator.GetBinDirectory(root);
            string modelsDir = WhisperLocator.GetModelsDirectory(root);
            string modelPath = Path.Combine(modelsDir, plan.Model.FileName);

            bool programCurrent = WhisperLocator.ReadBuildMarker(root) == plan.Build &&
                                  File.Exists(Path.Combine(binDir, WhisperLocator.CliFileName));
            bool modelCurrent = await IsModelIntactAsync(modelPath, plan.Model, progress, cancellationToken).ConfigureAwait(false);
            string vadPath = Path.Combine(modelsDir, plan.Vad.FileName);
            bool vadCurrent = await IsModelIntactAsync(vadPath, plan.Vad, progress, cancellationToken).ConfigureAwait(false);

            long needed = (programCurrent ? 0 : plan.Program.SizeBytes + plan.Program.InstalledBytes) +
                          (modelCurrent ? 0 : plan.Model.InstalledBytes) +
                          (vadCurrent ? 0 : plan.Vad.InstalledBytes);
            string? spaceError = CheckFreeSpace(root, needed);
            if (spaceError != null)
            {
                return new WhisperInstallResult(false, null, spaceError);
            }

            if (!programCurrent)
            {
                string? programError = await InstallProgramAsync(client, plan, root, binDir, progress, cancellationToken).ConfigureAwait(false);
                if (programError != null)
                {
                    return new WhisperInstallResult(false, null, programError);
                }
            }
            else
            {
                progress?.Report($"whisper.cpp {WhisperCatalog.WhisperVersion} ({plan.Build}) is already installed.");
            }

            if (!modelCurrent)
            {
                string? modelError = await DownloadModelAsync(client, plan.Model, modelPath, "speech model", progress, cancellationToken).ConfigureAwait(false);
                if (modelError != null)
                {
                    return new WhisperInstallResult(false, null, modelError);
                }
            }

            if (!vadCurrent)
            {
                string? vadError = await DownloadModelAsync(client, plan.Vad, vadPath, "voice activity model", progress, cancellationToken).ConfigureAwait(false);
                if (vadError != null)
                {
                    return new WhisperInstallResult(false, null, vadError);
                }
            }

            await File.WriteAllTextAsync(Path.Combine(root, NoticeFileName), BuildNotice(plan), cancellationToken).ConfigureAwait(false);

            WhisperInstallation? installation = WhisperLocator.Find(root);
            if (installation == null)
            {
                return new WhisperInstallResult(false, null, $"The install finished but whisper-cli.exe or the model is missing in {root}.");
            }

            progress?.Report($"Subtitle tool installed: {installation.Describe()}.");
            return new WhisperInstallResult(true, installation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            return new WhisperInstallResult(false, null, ex.Message);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }

            installLock.Dispose();
        }
    }

    private static async Task<string?> InstallProgramAsync(
        HttpClient client,
        WhisperInstallPlan plan,
        string root,
        string binDir,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string zipPath = Path.Combine(root, $"download-{Guid.NewGuid():N}.zip.part");
        string stagingDir = binDir + ".new";
        try
        {
            string? error = await ExternalToolInstallerService.DownloadToFileAsync(
                client, plan.Program.Url, zipPath, plan.Program.Sha256,
                $"whisper.cpp {WhisperCatalog.WhisperVersion} ({plan.Program.FileName})", progress, cancellationToken).ConfigureAwait(false);
            if (error != null)
            {
                return error;
            }

            progress?.Report("Unpacking whisper.cpp...");
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }

            Directory.CreateDirectory(stagingDir);
            bool cliFound = false;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Only the command-line program and the libraries it loads. Taking the bare file name means an
                    // entry such as "..\..\x.dll" can never land outside the staging folder.
                    string name = Path.GetFileName(entry.FullName);
                    bool wanted = entry.Length > 0 &&
                                  (string.Equals(name, WhisperLocator.CliFileName, StringComparison.OrdinalIgnoreCase) ||
                                   name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                    if (!wanted)
                    {
                        continue;
                    }

                    entry.ExtractToFile(Path.Combine(stagingDir, name), overwrite: true);
                    cliFound |= string.Equals(name, WhisperLocator.CliFileName, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!cliFound)
            {
                return $"The whisper.cpp archive did not contain {WhisperLocator.CliFileName}.";
            }

            await File.WriteAllTextAsync(Path.Combine(stagingDir, WhisperLocator.BuildMarkerFileName),
                WhisperLocator.FormatBuildMarker(plan.Build), cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(binDir))
            {
                try
                {
                    Directory.Delete(binDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"Cannot replace the installed whisper.cpp (is a subtitle job running?): {ex.Message}";
                }
            }

            Directory.Move(stagingDir, binDir);
            return null;
        }
        finally
        {
            ExternalToolInstallerService.TryDeleteFile(zipPath);
            try
            {
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<string?> DownloadModelAsync(
        HttpClient client, WhisperDownload model, string targetPath, string kind, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string partPath = targetPath + ".part";
        string? error = await ExternalToolInstallerService.DownloadToFileAsync(
            client, model.Url, partPath, model.Sha256, $"{kind} {model.FileName}", progress, cancellationToken).ConfigureAwait(false);
        if (error == null)
        {
            File.Move(partPath, targetPath, overwrite: true);
        }

        return error;
    }

    private static async Task<bool> IsModelIntactAsync(
        string modelPath, WhisperDownload model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var info = new FileInfo(modelPath);
        if (!info.Exists || info.Length != model.SizeBytes)
        {
            return false;
        }

        progress?.Report($"Checking the installed speech model {model.FileName}...");
        await using var stream = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CheckFreeSpace(string root, long neededBytes)
    {
        if (neededBytes <= 0)
        {
            return null;
        }

        try
        {
            string? drive = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(drive))
            {
                return null;
            }

            long free = new DriveInfo(drive).AvailableFreeSpace;
            // 10% margin on top of what the files take.
            long required = neededBytes + neededBytes / 10;
            return free >= required
                ? null
                : $"Not enough free space on {drive}: the subtitle tool needs about {required / 1_000_000_000.0:F1} GB, " +
                  $"{free / 1_000_000_000.0:F1} GB is free. Nothing was downloaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Network or unusual paths: let the download itself report a full disk.
            return null;
        }
    }

    private static string BuildNotice(WhisperInstallPlan plan) =>
        $"""
        Installed by IgnisDisc Optical Studio for "Generate subtitles".

        Program: whisper.cpp {WhisperCatalog.WhisperVersion} ({plan.Build})
          Source:  {plan.Program.Url}
          SHA-256: {plan.Program.Sha256}
          Project: https://github.com/ggml-org/whisper.cpp
          License: MIT (https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE)

        Model: {plan.Model.FileName}
          Source:  {plan.Model.Url}
          SHA-256: {plan.Model.Sha256}
          License: MIT (https://huggingface.co/ggerganov/whisper.cpp)

        Voice activity model: {plan.Vad.FileName} (Silero VAD)
          Source:  {plan.Vad.Url}
          SHA-256: {plan.Vad.Sha256}
          License: MIT (https://huggingface.co/ggml-org/whisper-vad)

        The CUDA build includes NVIDIA CUDA runtime and cuBLAS libraries, distributed by the whisper.cpp project under
        NVIDIA's CUDA redistribution terms.

        To remove: delete this folder.
        """;

    private static HttpClient CreateLargeDownloadClient()
    {
        // No overall time limit: the GPU model is 1.6 GB. A stalled connection is caught by the per-read stall timeout.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ExternalToolInstallerService.DefaultUserAgent);
        return client;
    }
}
