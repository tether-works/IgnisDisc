using System.Diagnostics;

namespace CdBurner.Core.Archival;

/// <summary>
/// Locates and invokes external par2cmdline executable to generate recovery data.
/// </summary>
public static class Par2Runner
{
    /// <summary>Default maximum duration allowed for a single par2 process invocation.</summary>
    internal static TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-async-flow timeout override for tests so parallel runs stay isolated.</summary>
    internal static readonly AsyncLocal<TimeSpan?> ProcessTimeoutOverride = new();

    /// <summary>
    /// Searches for par2cmdline on the host machine.
    /// </summary>
    /// <param name="customPath">Optional custom path to verify first.</param>
    /// <returns>Path to par2 executable, or null if not found.</returns>
    public static string? FindPar2Executable(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            try
            {
                if (File.Exists(customPath))
                {
                    return Path.GetFullPath(customPath);
                }
            }
            catch
            {
            }
        }

        // The 1-click install comes before PATH: when it updates an older par2 found on PATH (for example a
        // par2cmdline 1.4.0), the program it just installed is the one every caller then uses.
        string localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "par2cmdline", "par2.exe");
        if (File.Exists(localPrograms))
        {
            return localPrograms;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    string exePath = Path.Combine(dir, "par2.exe");
                    if (File.Exists(exePath))
                    {
                        return Path.GetFullPath(exePath);
                    }

                    string noExtPath = Path.Combine(dir, "par2");
                    if (File.Exists(noExtPath))
                    {
                        return Path.GetFullPath(noExtPath);
                    }
                }
                catch
                {
                }
            }
        }

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            try
            {
                string appExe = Path.Combine(baseDir, "par2.exe");
                if (File.Exists(appExe))
                {
                    return Path.GetFullPath(appExe);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the argument list for par2 create command in the exact order required.
    /// </summary>
    /// <param name="outputPar2Path">Output .par2 file path.</param>
    /// <param name="baseDirectory">Base directory for relative paths in recovery volume.</param>
    /// <param name="redundancyPercent">Redundancy percentage (e.g. 10).</param>
    /// <returns>List of CLI arguments.</returns>
    public static IReadOnlyList<string> BuildCreateArguments(string outputPar2Path, string baseDirectory, int redundancyPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPar2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return new List<string>
        {
            "create",
            "-q",
            $"-r{redundancyPercent}",
            "-R",
            $"-B{baseDirectory}",
            outputPar2Path,
            // Bare wildcard, expanded by par2 relative to the working directory (the disc root). A quoted
            // "C:\path with spaces\*" would not be expanded by the Windows C runtime.
            "*"
        };
    }

    /// <summary>
    /// Arguments for a create with an explicit block size and recovery block count in a single recovery volume
    /// (<c>-s</c>, <c>-c</c>, <c>-n1</c>), as sized by <see cref="Par2RecoverySizer"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildCreateArguments(string outputPar2Path, string baseDirectory, Par2RecoveryLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPar2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(layout);

        return new List<string>
        {
            "create",
            "-q",
            $"-s{layout.BlockSize}",
            $"-c{layout.RecoveryBlocks}",
            "-n1",
            "-R",
            $"-B{baseDirectory}",
            outputPar2Path,
            "*"
        };
    }

    /// <summary>
    /// Creates recovery data with the block size and count in <paramref name="layout"/> (see
    /// <see cref="CreateAsync(string, string, int, string, CancellationToken)"/> for the folders used).
    /// </summary>
    public static Task CreateAsync(
        string par2Path,
        string discRoot,
        Par2RecoveryLayout layout,
        string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return CreateCoreAsync(par2Path, discRoot, scratchDirectory,
            output => BuildCreateArguments(output, discRoot, layout), cancellationToken);
    }

    /// <summary>
    /// Runs par2cmdline in a scratch folder to create recovery volumes, then moves them to discRoot/RECOVERY.
    /// </summary>
    /// <param name="par2Path">Path to par2 executable.</param>
    /// <param name="discRoot">Local directory of the staged disc root.</param>
    /// <param name="redundancyPercent">Redundancy percentage.</param>
    /// <param name="scratchDirectory">Temporary working directory for par2 generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if par2 returns a non-zero exit code.</exception>
    public static Task CreateAsync(
        string par2Path,
        string discRoot,
        int redundancyPercent,
        string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        if (redundancyPercent <= 0)
        {
            return Task.CompletedTask;
        }

        return CreateCoreAsync(par2Path, discRoot, scratchDirectory,
            output => BuildCreateArguments(output, discRoot, redundancyPercent), cancellationToken);
    }

    private static async Task CreateCoreAsync(
        string par2Path,
        string discRoot,
        string scratchDirectory,
        Func<string, IReadOnlyList<string>> buildArguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);

        if (!File.Exists(par2Path))
        {
            throw new FileNotFoundException($"par2 executable not found at '{par2Path}'.", par2Path);
        }

        if (!Directory.Exists(discRoot))
        {
            throw new DirectoryNotFoundException($"Disc root directory not found: '{discRoot}'.");
        }

        Directory.CreateDirectory(scratchDirectory);

        string outputPar2Path = Path.Combine(scratchDirectory, "recovery.par2");
        var args = buildArguments(outputPar2Path);

        var psi = new ProcessStartInfo
        {
            FileName = par2Path,
            WorkingDirectory = discRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        TimeSpan timeout = ProcessTimeoutOverride.Value ?? ProcessTimeout;
        try
        {
            var (exitCode, _, stderr) = await RunProcessWithTimeoutAsync(psi, timeout, "create", cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                string lastStderr = stderr.Length > 500 ? stderr[^500..] : stderr;
                throw new InvalidOperationException(
                    $"par2 creation failed with exit code {exitCode}: {lastStderr}");
            }

            string recoveryFolder = Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName);
            Directory.CreateDirectory(recoveryFolder);

            foreach (var file in Directory.EnumerateFiles(scratchDirectory, "*.par2"))
            {
                string dest = Path.Combine(recoveryFolder, Path.GetFileName(file));
                File.Move(file, dest, overwrite: true);
            }
        }
        catch
        {
            CleanupScratchPar2(scratchDirectory);
            throw;
        }
    }

    private static void CleanupScratchPar2(string scratchDirectory)
    {
        try
        {
            if (!Directory.Exists(scratchDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(scratchDirectory, "*.par2"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Suffix given to a recovery volume while a repair attempt runs without it. par2 only loads "*.par2".
    /// </summary>
    private const string HeldVolumeSuffix = ".held";

    /// <summary>
    /// Repairs a disc copy in place: runs "par2 repair -q -B. RECOVERY\recovery.par2" with WorkingDirectory = folderRoot.
    /// <para>
    /// par2 solves for the damaged blocks with the lowest-numbered recovery blocks it has. When a recovery volume
    /// is missing those numbers have gaps, and for some combinations of damaged blocks the equations cannot be
    /// solved ("RS computation error") even though spare recovery blocks exist. In that case the repair is tried
    /// again with one recovery volume set aside at a time, which gives par2 a different set of equations.
    /// </para>
    /// <para>
    /// A failed attempt can leave a damaged original renamed to "name.1" with nothing under its own name; such
    /// files are renamed back, so a failed repair never changes which files exist.
    /// </para>
    /// </summary>
    /// <param name="par2Path">Path to par2 executable.</param>
    /// <param name="folderRoot">Root folder containing the disc copy to repair.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code (0 = repaired or nothing to do) and the backup files par2 left behind.</returns>
    /// <exception cref="ArgumentException">Thrown if parameters are null or whitespace.</exception>
    /// <exception cref="FileNotFoundException">Thrown if par2 or RECOVERY\recovery.par2 is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown if folderRoot directory does not exist.</exception>
    public static async Task<Par2RepairResult> RepairAsync(
        string par2Path,
        string folderRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRoot);

        if (!Directory.Exists(folderRoot))
        {
            throw new DirectoryNotFoundException($"Folder root directory not found: '{folderRoot}'.");
        }

        string recoveryFolder = Path.Combine(folderRoot, ArchiveManifest.RecoveryFolderName);
        ReleaseHeldVolumes(recoveryFolder);

        var filesBefore = new HashSet<string>(
            Directory.EnumerateFiles(folderRoot, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);

        // par2cmdline refuses any file whose full path passes 260 characters. Long names in a deep folder do, so par2
        // then works through a short directory junction to the same folder (removed afterwards; the folder is kept).
        using var alias = ShortPathAlias.CreateIfNeeded(folderRoot, filesBefore);
        string runRoot = alias?.Path ?? folderRoot;

        int exitCode;
        string errorOutput;
        int attempts = 1;
        try
        {
            (exitCode, errorOutput) = await RunRepairOnceAsync(par2Path, runRoot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestoreDisplacedOriginals(folderRoot, filesBefore);
            throw;
        }

        if (exitCode != 0)
        {
            RestoreDisplacedOriginals(folderRoot, filesBefore);

            if (errorOutput.Contains("RS computation error", StringComparison.OrdinalIgnoreCase))
            {
                var volumes = Directory.EnumerateFiles(recoveryFolder, "*.vol*.par2")
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string volume in volumes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string held = volume + HeldVolumeSuffix;
                    File.Move(volume, held);
                    int retryExitCode;
                    bool attemptThrew = true;
                    try
                    {
                        (retryExitCode, _) = await RunRepairOnceAsync(par2Path, runRoot, cancellationToken).ConfigureAwait(false);
                        attemptThrew = false;
                    }
                    catch
                    {
                        RestoreDisplacedOriginals(folderRoot, filesBefore);
                        throw;
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(held) && !File.Exists(volume))
                            {
                                File.Move(held, volume);
                            }
                        }
                        catch (Exception ex) when (attemptThrew && ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }

                    attempts++;
                    if (retryExitCode == 0)
                    {
                        exitCode = 0;
                        errorOutput = string.Empty;
                        break;
                    }

                    RestoreDisplacedOriginals(folderRoot, filesBefore);
                }
            }
        }

        return new Par2RepairResult(exitCode, errorOutput, FindRepairBackups(folderRoot, filesBefore), attempts);
    }

    /// <summary>
    /// Puts back recovery volumes that an interrupted earlier repair left set aside.
    /// </summary>
    private static void ReleaseHeldVolumes(string recoveryFolder)
    {
        if (!Directory.Exists(recoveryFolder))
        {
            return;
        }

        foreach (string held in Directory.EnumerateFiles(recoveryFolder, "*.par2" + HeldVolumeSuffix).ToList())
        {
            string volume = held[..^HeldVolumeSuffix.Length];
            if (!File.Exists(volume))
            {
                File.Move(held, volume);
            }
        }
    }

    /// <summary>
    /// Files created since <paramref name="filesBefore"/> whose name is an earlier file's name plus ".&lt;number&gt;":
    /// the copies par2 makes of each original before it writes the repaired file.
    /// </summary>
    private static List<(string Backup, string Original)> FindNewBackups(string folderRoot, HashSet<string> filesBefore)
    {
        var backups = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(folderRoot, "*", SearchOption.AllDirectories))
        {
            if (filesBefore.Contains(file))
            {
                continue;
            }

            int dot = file.LastIndexOf('.');
            if (dot <= 0 || dot == file.Length - 1 || file.AsSpan(dot + 1).ContainsAnyExceptInRange('0', '9'))
            {
                continue;
            }

            string original = file[..dot];
            if (filesBefore.Contains(original))
            {
                backups.Add((file, original));
            }
        }

        return backups;
    }

    private static void RestoreDisplacedOriginals(string folderRoot, HashSet<string> filesBefore)
    {
        foreach (var (backup, original) in FindNewBackups(folderRoot, filesBefore))
        {
            // Overwrite any partial/0-byte file par2 may have started writing before failing or timing out.
            File.Move(backup, original, overwrite: true);
        }
    }

    private static IReadOnlyList<string> FindRepairBackups(string folderRoot, HashSet<string> filesBefore) =>
        FindNewBackups(folderRoot, filesBefore)
            .Where(b => File.Exists(b.Original))
            .Select(b => b.Backup)
            .ToList();

    private static async Task<(int ExitCode, string ErrorOutput)> RunRepairOnceAsync(
        string par2Path,
        string folderRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRoot);

        if (!File.Exists(par2Path))
        {
            throw new FileNotFoundException($"par2 executable not found at '{par2Path}'.", par2Path);
        }

        if (!Directory.Exists(folderRoot))
        {
            throw new DirectoryNotFoundException($"Folder root directory not found: '{folderRoot}'.");
        }

        string recoveryPar2 = Path.Combine(folderRoot, ArchiveManifest.RecoveryFolderName, "recovery.par2");
        if (!File.Exists(recoveryPar2))
        {
            throw new FileNotFoundException($"Recovery file not found: '{recoveryPar2}'.", recoveryPar2);
        }

        var psi = new ProcessStartInfo
        {
            FileName = par2Path,
            WorkingDirectory = folderRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("repair");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-B.");
        psi.ArgumentList.Add(Path.Combine(ArchiveManifest.RecoveryFolderName, "recovery.par2"));

        TimeSpan timeout = ProcessTimeoutOverride.Value ?? ProcessTimeout;
        var (exitCode, _, err) = await RunProcessWithTimeoutAsync(psi, timeout, "repair", cancellationToken).ConfigureAwait(false);
        return (exitCode, err);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessWithTimeoutAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var proc = new Process { StartInfo = psi };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start par2 process at '{psi.FileName}'.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            KillProcessTreeSafe(proc);
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { }
            throw new TimeoutException($"par2 {operationName} timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch
        {
            KillProcessTreeSafe(proc);
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { }
            throw;
        }

        return (
            proc.ExitCode,
            outTask.IsCompletedSuccessfully ? outTask.Result : string.Empty,
            errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty);
    }

    private static void KillProcessTreeSafe(Process proc) =>
        CdBurner.Core.Utilities.ProcessAndFileCleanup.KillTreeAndWait(proc);
}

/// <summary>
/// Outcome of <see cref="Par2Runner.RepairAsync"/>.
/// </summary>
/// <param name="ExitCode">par2's exit code for the attempt that counted (0 = repaired or nothing to do).</param>
/// <param name="ErrorOutput">What par2 wrote to its error stream when the repair failed; empty on success.</param>
/// <param name="BackupFiles">Full paths of the "name.N" copies of damaged originals that par2 created during this
/// repair and whose original exists again. Files that were already there before the repair are never listed.</param>
/// <param name="Attempts">Number of par2 runs made.</param>
public sealed record Par2RepairResult(int ExitCode, string ErrorOutput, IReadOnlyList<string> BackupFiles, int Attempts);

/// <summary>
/// A short directory junction (under %TEMP% or a user-writable profile directory) pointing at a folder, so a
/// tool limited to 260-character paths can reach files with long names. Disposing removes the junction only; the
/// folder it points at and its files are untouched.
/// </summary>
internal sealed class ShortPathAlias : IDisposable
{
    /// <summary>par2cmdline's limit (MAX_PATH); paths at or past it are refused.</summary>
    internal const int PathLimit = 259;

    internal static readonly AsyncLocal<string?> TempPathOverride = new();
    internal static readonly AsyncLocal<int?> JunctionTimeoutMsOverride = new();
    internal static readonly AsyncLocal<Func<string, string, ProcessStartInfo>?> JunctionProcessStartInfoOverride = new();

    private ShortPathAlias(string path) => Path = path;

    public string Path { get; }

    /// <summary>Creates a junction when some file under <paramref name="folder"/> has a path too long; otherwise null.</summary>
    public static ShortPathAlias? CreateIfNeeded(string folder, IEnumerable<string> files)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string full = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        int longestRelative = files.Select(f => f.Length - full.Length).DefaultIfEmpty(0).Max() + 16; // + name.1 backups
        if (full.Length + longestRelative <= PathLimit)
        {
            return null;
        }

        string? parent = ChooseShortParent(longestRelative);
        if (parent == null)
        {
            return null;
        }

        string link = System.IO.Path.Combine(parent, "ip2-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            ProcessStartInfo psi = JunctionProcessStartInfoOverride.Value?.Invoke(link, full)
                ?? CreateJunctionStartInfo(link, full);
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            int timeoutMs = JunctionTimeoutMsOverride.Value ?? 10_000;
            if (!proc.WaitForExit(timeoutMs))
            {
                CdBurner.Core.Utilities.ProcessAndFileCleanup.KillTreeAndWait(proc);
                TryDeleteJunction(link);
                return null;
            }

            if (proc.ExitCode == 0 && Directory.Exists(link))
            {
                return new ShortPathAlias(link);
            }

            TryDeleteJunction(link);
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TryDeleteJunction(link);
            return null;
        }
    }

    private static string? ChooseShortParent(int longestRelative)
    {
        const int linkNameLength = 10; // "ip2-" + 6 hex chars
        string? customTemp = TempPathOverride.Value;
        string?[] candidates = customTemp != null
            ? [customTemp]
            :
            [
                System.IO.Path.GetTempPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ];

        foreach (string? raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string candidate = raw.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            // Never place junctions directly in a drive root (e.g. "C:\").
            if (candidate.Length <= 3 && candidate.EndsWith(':'))
            {
                continue;
            }

            if (candidate.Length + 1 + linkNameLength + longestRelative <= PathLimit && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ProcessStartInfo CreateJunctionStartInfo(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        return psi;
    }

    private static void TryDeleteJunction(string link)
    {
        try
        {
            if (Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CdBurner.Core.Utilities.ProcessAndFileCleanup.TryDeleteDirectoryWithRetry(link, recursive: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => TryDeleteJunction(Path);
}
