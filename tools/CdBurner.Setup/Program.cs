using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.Core.Platform;
using Microsoft.Win32;

namespace CdBurner.Setup;

internal static class Program
{
    public const string AppTitle = "IgnisDisc Optical Studio";
    /// <summary>The version stamped into this setup program at build time (Directory.Build.props).</summary>
    public static string AppVersion { get; } = ReadOwnVersion();

    public const string Publisher = "tetherworks";
    public const string UninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IgnisDisc";
    public const string LegacyUninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CdBurner";

    private const string ManifestFileName = ".ignisdisc-manifest.txt";

    /// <summary>
    /// The product version from this assembly's metadata. The informational version may carry a "+commit" suffix,
    /// which is dropped so Apps &amp; Features shows plain "1.18".
    /// </summary>
    internal static string ReadOwnVersion()
    {
        var assembly = typeof(Program).Assembly;
        string? info = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion)
            .FirstOrDefault();
        string? version = info?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(version)) return version;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>The processor family this setup runs on, for the text in the wizard header.</summary>
    internal static string ArchitectureLabel(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => architecture.ToString()
    };

    /// <summary>The parsed command line. <see cref="Error"/> is set when an option is missing its value.</summary>
    internal sealed record SetupOptions(
        bool Silent,
        bool Uninstall,
        bool CreateDesktopShortcut,
        bool CreateStartMenuShortcut,
        bool AddToPath,
        string InstallDir,
        string? ShortcutRoot,
        string? Error,
        bool EnableShellIntegration = false);

    internal static SetupOptions ParseArguments(string[] args, string defaultInstallDir)
    {
        bool silent = false;
        bool uninstall = false;
        bool desktop = true;
        bool startMenu = true;
        bool addToPath = true;
        bool enableShellIntegration = false;
        string installDir = defaultInstallDir;
        string? shortcutRoot = null;
        string? error = null;

        for (int i = 0; i < args.Length && error == null; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                uninstall = true;
            }
            else if (string.Equals(arg, "--no-shortcuts", StringComparison.OrdinalIgnoreCase))
            {
                desktop = false;
                startMenu = false;
            }
            else if (string.Equals(arg, "--no-path", StringComparison.OrdinalIgnoreCase))
            {
                addToPath = false;
            }
            else if (string.Equals(arg, "--shell-integration", StringComparison.OrdinalIgnoreCase))
            {
                enableShellIntegration = true;
            }
            else if (string.Equals(arg, "--install-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "--install-dir needs a folder path.";
                }
                else
                {
                    installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++i]));
                }
            }
            else if (string.Equals(arg, "--shortcut-root", StringComparison.OrdinalIgnoreCase))
            {
                // Puts the shortcuts in <folder>\Desktop and <folder>\Programs instead of the real ones. Used by the
                // installer tests so a test install never replaces or removes the user's own shortcuts.
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "--shortcut-root needs a folder path.";
                }
                else
                {
                    shortcutRoot = Path.GetFullPath(args[++i]);
                }
            }
        }

        return new SetupOptions(silent, uninstall, desktop, startMenu, addToPath, installDir, shortcutRoot, error, enableShellIntegration);
    }

    /// <summary>
    /// Whether this install is listed in Windows' Installed Apps. Silent and wizard installs follow the same rule: every
    /// install is listed, except one run with --shortcut-root, which is an isolated test install that must leave the
    /// machine's own Installed Apps entry alone.
    /// </summary>
    internal static bool ShouldRegisterUninstaller(string? shortcutRoot) => shortcutRoot == null;

    [STAThread]
    public static int Main(string[] args)
    {
        string defaultInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            GetExistingInstallLocation() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "IgnisDisc")));

        SetupOptions options = ParseArguments(args, defaultInstallDir);
        if (options.Error != null)
        {
            Console.Error.WriteLine(options.Error);
            if (!options.Silent)
            {
                System.Windows.MessageBox.Show(options.Error, $"{AppTitle} Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 2;
        }

        bool silent = options.Silent;
        string installDir = options.InstallDir;
        ShortcutManager shortcuts = options.ShortcutRoot != null
            ? ShortcutManager.ForRoot(options.ShortcutRoot)
            : ShortcutManager.ForCurrentUser();
        IShellIntegrationRegistry shellRegistry = options.ShortcutRoot != null
            ? NullShellIntegrationRegistry.Instance
            : WindowsShellIntegrationRegistry.Instance;

        if (options.Uninstall)
        {
            return RunUninstall(installDir, silent, shortcuts, shellRegistry: shellRegistry);
        }

        if (silent)
        {
            try
            {
                IReadOnlyList<string> warnings = InstallPayload(
                    installDir,
                    options.CreateDesktopShortcut,
                    options.CreateStartMenuShortcut,
                    options.AddToPath,
                    registerUninstaller: ShouldRegisterUninstaller(options.ShortcutRoot),
                    progress: null,
                    shortcuts: shortcuts,
                    enableShellIntegration: options.EnableShellIntegration,
                    shellRegistry: shellRegistry);
                foreach (string warning in warnings) Console.Error.WriteLine($"Warning: {warning}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Installation failed: {ex.Message}");
                return 1;
            }
        }

        var app = new System.Windows.Application();
        var window = new SetupWizardWindow(installDir, shortcuts, ShouldRegisterUninstaller(options.ShortcutRoot), shellRegistry);
        app.Run(window);
        return window.ExitCode;
    }

    public static string? GetExistingInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath)
                            ?? Registry.CurrentUser.OpenSubKey(LegacyUninstallRegKeyPath);
            if (key?.GetValue("InstallLocation") is string loc && Directory.Exists(loc))
            {
                return loc;
            }
        }
        catch { }

        string defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "IgnisDisc");
        if (Directory.Exists(defaultDir) && File.Exists(Path.Combine(defaultDir, "IgnisDisc.exe")))
        {
            return defaultDir;
        }

        string legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CdBurner");
        if (Directory.Exists(legacyDir) && File.Exists(Path.Combine(legacyDir, "CdBurner.App.exe")))
        {
            return legacyDir;
        }

        return null;
    }

    /// <summary>
    /// The version of the IgnisDisc already in <paramref name="installDir"/>, read from its program file. The Apps &amp;
    /// Features entry is only used when no program file is there: older installers did not always update it (1.08
    /// left "1.07" behind), so it can name the wrong version.
    /// </summary>
    public static string? GetExistingInstallVersion(string installDir)
    {
        foreach (string exeName in ShortcutManager.KnownAppExeNames)
        {
            string? version = ReadProductVersion(Path.Combine(installDir, exeName));
            if (version != null) return version;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath)
                            ?? Registry.CurrentUser.OpenSubKey(LegacyUninstallRegKeyPath);
            if (key?.GetValue("DisplayVersion") is string ver && !string.IsNullOrWhiteSpace(ver))
            {
                return ver;
            }
        }
        catch { }

        return null;
    }

    internal static string? ReadProductVersion(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            string? version = vi.ProductVersion?.Split('+')[0];
            return string.IsNullOrWhiteSpace(version) ? vi.FileVersion : version;
        }
        catch
        {
            return null;
        }
    }

    public static bool EnsureRunningProcessesClosed(string installDir, bool silent)
    {
        string[] processNames = { "IgnisDisc", "CdBurner.App", "ignisdisc-cli", "cdburner-cli" };
        var running = new List<Process>();
        string? installDirWithSep = string.IsNullOrWhiteSpace(installDir)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)) + Path.DirectorySeparatorChar;

        foreach (var name in processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                bool added = false;
                try
                {
                    string? exePath = null;
                    try { exePath = p.MainModule?.FileName; } catch { }

                    if (!string.IsNullOrEmpty(exePath))
                    {
                        string normalizedExe = Path.GetFullPath(exePath);
                        if (string.IsNullOrEmpty(installDirWithSep) ||
                            normalizedExe.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase))
                        {
                            running.Add(p);
                            added = true;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (!added)
                    {
                        p.Dispose();
                    }
                }
            }
        }

        if (running.Count == 0)
        {
            return true;
        }

        if (!silent)
        {
            var res = System.Windows.MessageBox.Show(
                $"{AppTitle} is currently running.\n\nClick OK to close it automatically and proceed with the update, or Cancel to abort.",
                $"{AppTitle} Setup",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (res != MessageBoxResult.OK)
            {
                foreach (var p in running)
                {
                    p.Dispose();
                }
                return false;
            }
        }

        foreach (var p in running)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2000))
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }

        return true;
    }

    /// <summary>
    /// The folder holding the files to install: the folder this wizard runs from. IgnisDisc-Setup.exe unpacks the full
    /// app (including this wizard) into a temporary folder and starts the wizard there.
    /// </summary>
    public static string GetPayloadDirectory()
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!File.Exists(Path.Combine(dir, "IgnisDisc.exe")))
        {
            throw new FileNotFoundException("The IgnisDisc files to install were not found next to the setup wizard. Run IgnisDisc-Setup.exe.");
        }

        return dir;
    }

    /// <summary>
    /// Copies the files, replaces the shortcuts, and registers the install. Returns warnings for parts that did not
    /// work but do not stop the program from running (currently: a shortcut that could not be created).
    /// </summary>
    public static IReadOnlyList<string> InstallPayload(
        string installDir,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool addToPath,
        bool registerUninstaller,
        IProgress<(double Percent, string Status)>? progress,
        bool ensureClosed = true,
        ShortcutManager? shortcuts = null,
        string? payloadDirectory = null,
        string userEnvironmentKeyPath = UserEnvironmentKeyPath,
        bool enableShellIntegration = false,
        IShellIntegrationRegistry? shellRegistry = null)
    {
        installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        string installDirWithSep = installDir + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(installDir);
        progress?.Report((2, "Checking for running instances..."));

        if (ensureClosed && !EnsureRunningProcessesClosed(installDir, silent: progress == null))
        {
            throw new InvalidOperationException($"Update was cancelled because {AppTitle} is currently running.");
        }

        progress?.Report((5, $"Preparing target directory: {installDir}"));

        string payloadDir = payloadDirectory != null
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadDirectory))
            : GetPayloadDirectory();
        if (string.Equals(payloadDir, installDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("IgnisDisc is already installed in this folder. Run IgnisDisc-Setup.exe to install or update it.");
        }

        // Files an earlier version installed, used below to remove ones this version no longer ships.
        string manifestPath = Path.Combine(installDir, ManifestFileName);
        string[] previousFiles = Array.Empty<string>();
        try
        {
            if (File.Exists(manifestPath)) previousFiles = File.ReadAllLines(manifestPath);
        }
        catch
        {
        }

        var installedFiles = CopyPayloadWithRollback(payloadDir, installDir, progress);

        // Remove files from the previous version that this version no longer includes (only files it recorded).
        var current = new HashSet<string>(installedFiles, StringComparer.OrdinalIgnoreCase) { ManifestFileName };
        foreach (string old in previousFiles)
        {
            if (string.IsNullOrWhiteSpace(old) || current.Contains(old.Trim())) continue;
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(installDir, old.Trim()));
                if (candidate.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch
            {
            }
        }

        string appExePath = Path.Combine(installDir, "IgnisDisc.exe");
        string uninstallerPath = Path.Combine(installDir, "Uninstall-IgnisDisc.exe");

        try
        {
            installedFiles.Add(ManifestFileName);
            File.WriteAllLines(manifestPath, installedFiles.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
        }

        // Replaces the Desktop and Start menu shortcuts and removes ones left by earlier versions, so exactly one of
        // each opens the program that was just installed.
        progress?.Report((90, "Updating Desktop and Start menu shortcuts..."));
        IReadOnlyList<string> warnings = (shortcuts ?? ShortcutManager.ForCurrentUser()).Install(
            installDir,
            appExePath,
            createDesktopShortcut,
            createStartMenuShortcut);

        if (addToPath)
        {
            progress?.Report((95, "Adding ignisdisc-cli to User PATH..."));
            string? pathProblem = AddDirectoryToUserPath(installDir, userEnvironmentKeyPath);
            if (pathProblem != null)
            {
                var withPathWarning = new List<string>(warnings)
                {
                    $"ignisdisc-cli could not be added to your PATH: {pathProblem}"
                };
                warnings = withPathWarning;
            }
        }

        if (enableShellIntegration)
        {
            progress?.Report((97, "Registering Windows Explorer shell integration..."));
            string? shellWarning = InstallerEngine.ApplyShellIntegration(true, appExePath, shellRegistry);
            if (shellWarning != null)
            {
                warnings = new List<string>(warnings) { shellWarning };
            }
        }

        if (registerUninstaller)
        {
            progress?.Report((98, "Registering IgnisDisc with Windows Installed Apps..."));
            RegisterUninstallerInRegistry(installDir, appExePath, uninstallerPath);
        }

        progress?.Report((100, $"Installation complete! Installed to {installDir}"));
        return warnings;
    }

    /// <summary>
    /// Copies every payload file into <paramref name="installDir"/> in two steps: all files are first copied next to
    /// their final names under a temporary name, then each is swapped in (the old file is renamed aside first). If any
    /// step fails, every swapped file is put back, new files and new folders are removed, and the error is rethrown, so a
    /// failed update never leaves a half-old, half-new installation. Returns the relative paths that were installed.
    /// </summary>
    internal static List<string> CopyPayloadWithRollback(
        string payloadDir,
        string installDir,
        IProgress<(double Percent, string Status)>? progress)
    {
        const string StagedSuffix = ".ignisdisc-new";
        const string BackupSuffix = ".ignisdisc-old";
        string installDirWithSep = installDir + Path.DirectorySeparatorChar;

        string[] sourceFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories);
        int total = Math.Max(1, sourceFiles.Length);
        int processed = 0;

        var installedFiles = new List<string>();
        var staged = new List<(string Destination, string StagedPath)>();
        var createdDirectories = new List<string>();
        var swapped = new List<(string Destination, string? Backup)>();

        try
        {
            foreach (string sourcePath in sourceFiles)
            {
                processed++;
                string relative = Path.GetRelativePath(payloadDir, sourcePath);
                string destinationPath = Path.GetFullPath(Path.Combine(installDir, relative));
                if (!destinationPath.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    // Remember which folders this install creates (outermost first) so a rollback can remove them again.
                    var missing = new Stack<string>();
                    for (string? d = parentDir; d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
                    {
                        missing.Push(d);
                    }

                    Directory.CreateDirectory(parentDir);
                    createdDirectories.AddRange(missing);
                }

                string stagedPath = destinationPath + StagedSuffix;
                File.Copy(sourcePath, stagedPath, overwrite: true);
                staged.Add((destinationPath, stagedPath));
                installedFiles.Add(relative);

                if (processed % 8 == 0 || processed == total)
                {
                    double pct = 5.0 + (processed * 60.0 / total);
                    progress?.Report((pct, $"Copying ({processed}/{total}): {Path.GetFileName(sourcePath)}"));
                }
            }

            int swappedCount = 0;
            foreach (var (destination, stagedPath) in staged)
            {
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = destination + BackupSuffix;
                    File.Move(destination, backup, overwrite: true);
                }

                // Recorded before the second move: if that move fails, the rollback still knows where the old file went.
                swapped.Add((destination, backup));
                File.Move(stagedPath, destination);

                swappedCount++;
                if (swappedCount % 8 == 0 || swappedCount == staged.Count)
                {
                    double pct = 65.0 + (swappedCount * 20.0 / Math.Max(1, staged.Count));
                    progress?.Report((pct, $"Installing ({swappedCount}/{staged.Count}): {Path.GetFileName(destination)}"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RollbackCopy(staged, swapped, createdDirectories);
            throw new IOException($"The files could not be installed and the previous installation was left as it was: {ex.Message}", ex);
        }

        // The swap succeeded; the renamed old files are no longer needed. A backup that cannot be deleted now is harmless
        // (it is only a stray file), so it must not turn a finished install into a failure.
        foreach (var (_, backup) in swapped)
        {
            if (backup == null) continue;
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return installedFiles;
    }

    private static void RollbackCopy(
        List<(string Destination, string StagedPath)> staged,
        List<(string Destination, string? Backup)> swapped,
        List<string> createdDirectories)
    {
        // Undo in reverse order. Each step is best effort: one stuck file must not stop the others from being restored.
        for (int i = swapped.Count - 1; i >= 0; i--)
        {
            var (destination, backup) = swapped[i];
            try
            {
                if (backup != null)
                {
                    File.Move(backup, destination, overwrite: true);
                }
                else if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var (_, stagedPath) in staged)
        {
            TryDeleteFile(stagedPath);
        }

        for (int i = createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(createdDirectories[i]) && !Directory.EnumerateFileSystemEntries(createdDirectories[i]).Any())
                {
                    Directory.Delete(createdDirectories[i]);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The registry key (under HKEY_CURRENT_USER) that holds the user's PATH.</summary>
    internal const string UserEnvironmentKeyPath = "Environment";

    /// <summary>
    /// Splits a PATH value on semicolons that are outside double quotes and strips the quotes around each entry, so
    /// <c>"C:\Program Files\Tool"</c> and <c>C:\Program Files\Tool</c> are the same entry. An entry that itself contains a
    /// semicolon cannot be unquoted without changing its meaning, so it keeps its quotes.
    /// </summary>
    internal static List<string> SplitPathValue(string? value)
    {
        var entries = new List<string>();
        if (string.IsNullOrEmpty(value)) return entries;

        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        void Flush()
        {
            string entry = current.ToString().Trim();
            current.Clear();
            if (entry.Length == 0) return;

            string unquoted = entry.Replace("\"", string.Empty).Trim();
            if (unquoted.Length == 0) return;
            entries.Add(unquoted.Contains(';') ? "\"" + unquoted + "\"" : unquoted);
        }

        foreach (char c in value)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == ';' && !inQuotes)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return entries;
    }

    private static bool PathEntryMatches(string entry, string normalizedDir)
    {
        static string Normalize(string s)
        {
            // Relative entries (such as ".") depend on the current folder, so they never name the install folder.
            if (!Path.IsPathRooted(s)) return s;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Trim('"'))); }
            catch (ArgumentException) { return s; }
            catch (NotSupportedException) { return s; }
        }

        // An entry like %LOCALAPPDATA%\Programs\IgnisDisc names the same folder as its expanded form.
        string expanded = Environment.ExpandEnvironmentVariables(entry);
        return string.Equals(Normalize(entry), normalizedDir, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Normalize(expanded), normalizedDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds <paramref name="installDir"/> to the user PATH stored under <paramref name="keyPath"/>. The value is read
    /// without expanding %VARIABLES% and written back with its original registry type, because rewriting an expandable
    /// PATH as plain text would freeze every %VARIABLE% in it at today's value. Returns null on success, otherwise why
    /// the PATH could not be changed.
    /// </summary>
    internal static string? AddDirectoryToUserPath(string installDir, string keyPath = UserEnvironmentKeyPath)
    {
        try
        {
            string normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            object? raw = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            RegistryValueKind kind = raw is string ? key.GetValueKind("Path") : RegistryValueKind.ExpandString;
            if (kind != RegistryValueKind.String) kind = RegistryValueKind.ExpandString;

            List<string> parts = SplitPathValue(raw as string);
            if (parts.Any(p => PathEntryMatches(p, normalizedInstallDir)))
            {
                return null;
            }

            parts.Add(normalizedInstallDir);
            key.SetValue("Path", string.Join(";", parts), kind);
            BroadcastEnvironmentChange(keyPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    internal static string? RemoveDirectoryFromUserPath(string installDir, string keyPath = UserEnvironmentKeyPath)
    {
        try
        {
            string normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw ||
                string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            RegistryValueKind kind = key.GetValueKind("Path");
            if (kind != RegistryValueKind.String) kind = RegistryValueKind.ExpandString;

            List<string> parts = SplitPathValue(raw);
            int removed = parts.RemoveAll(p => PathEntryMatches(p, normalizedInstallDir));
            if (removed > 0)
            {
                key.SetValue("Path", string.Join(";", parts), kind);
                BroadcastEnvironmentChange(keyPath);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeoutMs, out UIntPtr result);

    /// <summary>
    /// Tells running programs (Explorer, new terminals) that the environment changed. Writing the registry value directly
    /// does not do this on its own. Skipped for a test key, which is not the real environment.
    /// </summary>
    private static void BroadcastEnvironmentChange(string keyPath)
    {
        if (!string.Equals(keyPath, UserEnvironmentKeyPath, StringComparison.Ordinal)) return;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        try
        {
            SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static void RegisterUninstallerInRegistry(string installDir, string appExePath, string uninstallerPath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(UninstallRegKeyPath);
            if (key != null)
            {
                key.SetValue("DisplayName", AppTitle);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", Publisher);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", appExePath);
                key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall --install-dir \"{installDir}\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallRegKeyPath, throwOnMissingSubKey: false);
            }
            catch { }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    /// <summary>
    /// True when the Apps &amp; Features entry was registered for <paramref name="installDir"/>.
    /// </summary>
    internal static bool RegistryEntryBelongsTo(string installDir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath);
            if (key?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Files this installation owns: those listed in its install manifest, or (for very old installs without a manifest)
    /// files that are unmistakably IgnisDisc's.
    /// </summary>
    private static List<string> CollectInstalledFiles(string installDir, string installDirWithSep)
    {
        var files = new List<string>();
        string manifestPath = Path.Combine(installDir, ManifestFileName);

        if (File.Exists(manifestPath))
        {
            string[] relativePaths = Array.Empty<string>();
            try { relativePaths = File.ReadAllLines(manifestPath); } catch { }

            foreach (string rel in relativePaths)
            {
                if (string.IsNullOrWhiteSpace(rel)) continue;
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(installDir, rel.Trim()));
                    if (candidate.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    {
                        files.Add(candidate);
                    }
                }
                catch { }
            }

            files.Add(manifestPath);
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // No install manifest: generic patterns such as *.dll / *.json are only used when the folder clearly is an
        // IgnisDisc install folder, so that an install into a shared folder (e.g. Downloads) never loses unrelated files.
        bool looksLikeDedicatedInstall =
            File.Exists(Path.Combine(installDir, "IgnisDisc.exe")) &&
            File.Exists(Path.Combine(installDir, "IgnisDisc.dll")) &&
            File.Exists(Path.Combine(installDir, "CdBurner.Core.dll")) &&
            string.Equals(Path.GetFileName(installDir), "IgnisDisc", StringComparison.OrdinalIgnoreCase);

        string[] knownPatterns = looksLikeDedicatedInstall
            ? new[] { "IgnisDisc.*", "ignisdisc-cli.*", "CdBurner.*", "cdburner-cli.*", "Uninstall-IgnisDisc.*", "*.dll", "*.json", "*.pdb", "ffmpeg.exe" }
            : new[] { "IgnisDisc.*", "ignisdisc-cli.*", "CdBurner.*", "cdburner-cli.*", "Uninstall-IgnisDisc.*" };

        foreach (string pattern in knownPatterns)
        {
            foreach (string file in Directory.GetFiles(installDir, pattern, SearchOption.TopDirectoryOnly))
            {
                files.Add(Path.GetFullPath(file));
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Removes <paramref name="files"/> (and then empty folders up to <paramref name="installDir"/>) after this process
    /// exits, using a hidden Windows PowerShell script that deletes itself. Nothing outside the install folder is touched.
    /// </summary>
    internal static bool ScheduleDeferredCleanup(string installDir, IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return true;

        try
        {
            static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            string rootWithSep = root + Path.DirectorySeparatorChar;
            var safeFiles = files.Where(f => Path.GetFullPath(f).StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)).ToList();
            if (safeFiles.Count == 0) return true;

            string scriptPath = Path.Combine(Path.GetTempPath(), $"ignisdisc-uninstall-{Guid.NewGuid():N}.ps1");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine($"Wait-Process -Id {Environment.ProcessId} -Timeout 300");
            sb.AppendLine("Start-Sleep -Milliseconds 500");
            sb.AppendLine("$files = @(");
            sb.AppendLine(string.Join(",\n", safeFiles.Select(Quote)));
            sb.AppendLine(")");
            sb.AppendLine("foreach ($f in $files) { for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $f); $i++) { Remove-Item -LiteralPath $f -Force; if (Test-Path -LiteralPath $f) { Start-Sleep -Milliseconds 500 } } }");
            sb.AppendLine($"$root = {Quote(root)}");
            sb.AppendLine("if (Test-Path -LiteralPath $root) {");
            sb.AppendLine("  Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { if (-not (Get-ChildItem -LiteralPath $_.FullName -Force)) { Remove-Item -LiteralPath $_.FullName -Force } }");
            sb.AppendLine("  if (-not (Get-ChildItem -LiteralPath $root -Force)) { Remove-Item -LiteralPath $root -Force }");
            sb.AppendLine("}");
            sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            File.WriteAllText(scriptPath, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            using var cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            });
            return cleanup != null;
        }
        catch
        {
            return false; // e.g. Windows PowerShell removed by policy
        }
    }

    internal static int RunUninstall(
        string installDir,
        bool silent,
        ShortcutManager shortcuts,
        Func<string, IReadOnlyCollection<string>, bool>? cleanupScheduler = null,
        string? currentExePath = null,
        string userEnvironmentKeyPath = UserEnvironmentKeyPath,
        IShellIntegrationRegistry? shellRegistry = null)
    {
        cleanupScheduler ??= ScheduleDeferredCleanup;
        try
        {
            installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            string installDirWithSep = installDir + Path.DirectorySeparatorChar;

            if (!silent)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"Are you sure you want to uninstall {AppTitle} from:\n{installDir}?",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return 0;
                }
            }

            if (!EnsureRunningProcessesClosed(installDir, silent))
            {
                return 1;
            }

            // Only remove shortcuts and the Apps & Features entry that belong to THIS install folder (plus shortcuts to
            // IgnisDisc copies that no longer exist), so uninstalling one copy (or a test install in another folder)
            // never touches a different IgnisDisc installation.
            shortcuts.Uninstall(installDir);
            InstallerEngine.RemoveShellIntegrationOnUninstall(installDir, shellRegistry);

            if (RegistryEntryBelongsTo(installDir))
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, throwOnMissingSubKey: false);
            }

            RemoveDirectoryFromUserPath(installDir, userEnvironmentKeyPath);

            bool filesRemain = false;
            if (Directory.Exists(installDir))
            {
                // This uninstaller runs from the install folder and shares its .NET runtime, so it must not delete those
                // files while running (doing so can crash it mid-way). Collect the files to remove and delete them all
                // after this process exits.
                var toDelete = CollectInstalledFiles(installDir, installDirWithSep);
                bool scheduled = cleanupScheduler(installDir, toDelete);

                if (!scheduled)
                {
                    // The deferred cleanup could not be started. Delete directly when this program is not itself inside the
                    // install folder; otherwise nothing can remove the files, so report the uninstall as incomplete.
                    string currentExe = Path.GetFullPath(currentExePath ?? Environment.ProcessPath ?? string.Empty);
                    bool runningFromInstallDir = currentExe.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase);
                    if (!runningFromInstallDir)
                    {
                        foreach (string file in toDelete) TryDeleteFile(file);
                    }

                    filesRemain = toDelete.Any(File.Exists);
                }
            }

            if (filesRemain)
            {
                string message = $"{AppTitle} was removed from Windows, but some files in {installDir} could not be deleted. Delete that folder by hand.";
                Console.Error.WriteLine(message);
                if (!silent)
                {
                    System.Windows.MessageBox.Show(message, $"{AppTitle} Uninstaller", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return 1;
            }

            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    $"{AppTitle} has been uninstalled.",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    $"Error during uninstall: {ex.Message}",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return 1;
        }
    }
}

internal sealed class SetupWizardWindow : Window
{
    private readonly System.Windows.Controls.TextBox _installDirBox;
    private readonly System.Windows.Controls.CheckBox _desktopShortcutCheck;
    private readonly System.Windows.Controls.CheckBox _startMenuShortcutCheck;
    private readonly System.Windows.Controls.CheckBox _addToPathCheck;
    private readonly System.Windows.Controls.CheckBox _shellIntegrationCheck;
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly System.Windows.Controls.Button _installButton;
    private readonly System.Windows.Controls.Button _launchButton;
    private readonly System.Windows.Controls.Button _closeButton;
    private readonly bool _isUpgrade;
    private readonly ShortcutManager _shortcuts;
    private readonly bool _registerUninstaller;
    private readonly IShellIntegrationRegistry _shellRegistry;
    private bool _isInstalled;

    public int ExitCode { get; private set; }

    public SetupWizardWindow(
        string defaultInstallDir,
        ShortcutManager shortcuts,
        bool registerUninstaller = true,
        IShellIntegrationRegistry? shellRegistry = null)
    {
        _shortcuts = shortcuts;
        _registerUninstaller = registerUninstaller;
        _shellRegistry = shellRegistry ?? WindowsShellIntegrationRegistry.Instance;
        string? previousVer = Program.GetExistingInstallVersion(defaultInstallDir);
        _isUpgrade = !string.IsNullOrEmpty(previousVer);

        Title = _isUpgrade
            ? $"{Program.AppTitle} — Upgrade to v{Program.AppVersion}"
            : $"{Program.AppTitle} v{Program.AppVersion} — Setup";
        Width = 640;
        Height = 505;
        MinWidth = 600;
        MinHeight = 485;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 31));
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 244, 248));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI, sans-serif");

        var rootGrid = new Grid { Margin = new Thickness(28) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        headerStack.Children.Add(new TextBlock
        {
            Text = "💿 IgnisDisc Optical Studio",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = _isUpgrade
                ? $"Existing Version {previousVer} Detected • Ready to Upgrade to v{Program.AppVersion}"
                : $"Version {Program.AppVersion} • Self-Contained Windows 10/11 {Program.ArchitectureLabel(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture)} Installer (No .NET Runtime Required)",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(_isUpgrade ? System.Windows.Media.Color.FromRgb(56, 189, 248) : System.Windows.Media.Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetRow(headerStack, 0);
        rootGrid.Children.Add(headerStack);

        // Body Card
        var cardBorder = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 36, 51)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20)
        };

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(new TextBlock
        {
            Text = "Installation Folder",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _installDirBox = new System.Windows.Controls.TextBox
        {
            Text = defaultInstallDir,
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 13,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105))
        };
        Grid.SetColumn(_installDirBox, 0);
        dirGrid.Children.Add(_installDirBox);

        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0)
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select installation folder for IgnisDisc Optical Studio",
                InitialDirectory = Directory.Exists(_installDirBox.Text)
                    ? _installDirBox.Text
                    : Path.GetDirectoryName(_installDirBox.Text) ?? string.Empty
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                string selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dlg.FolderName));
                string leafName = Path.GetFileName(selected);
                if (!string.Equals(leafName, "IgnisDisc", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(leafName, "CdBurner", StringComparison.OrdinalIgnoreCase))
                {
                    selected = Path.Combine(selected, "IgnisDisc");
                }
                _installDirBox.Text = selected;
            }
        };
        Grid.SetColumn(browseBtn, 1);
        dirGrid.Children.Add(browseBtn);
        bodyStack.Children.Add(dirGrid);

        _desktopShortcutCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Create Desktop shortcut (IgnisDisc Optical Studio)",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 6)
        };
        bodyStack.Children.Add(_desktopShortcutCheck);

        _startMenuShortcutCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Create Start Menu program shortcut",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 6)
        };
        bodyStack.Children.Add(_startMenuShortcutCheck);

        _addToPathCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Add ignisdisc-cli.exe to User PATH for command-line automation",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 6)
        };
        bodyStack.Children.Add(_addToPathCheck);

        _shellIntegrationCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Add Windows Explorer context menus & disc image associations (opt-in, per-user)",
            IsChecked = false,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 16)
        };
        bodyStack.Children.Add(_shellIntegrationCheck);

        _progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 12,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        bodyStack.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Text = _isUpgrade
                ? $"Ready to upgrade to IgnisDisc v{Program.AppVersion}. User settings in AppData are safely preserved."
                : "Ready to install IgnisDisc Optical Studio (GUI) and ignisdisc-cli.",
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        bodyStack.Children.Add(_statusText);

        cardBorder.Child = bodyStack;
        Grid.SetRow(cardBorder, 1);
        rootGrid.Children.Add(cardBorder);

        // Footer Buttons
        var footerStack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        _installButton = new System.Windows.Controls.Button
        {
            Content = _isUpgrade ? "Upgrade Now" : "Install Now",
            Padding = new Thickness(22, 9, 22, 9),
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(_isUpgrade ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(2, 132, 199)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0)
        };
        _installButton.Click += OnInstallClicked;
        footerStack.Children.Add(_installButton);

        _launchButton = new System.Windows.Controls.Button
        {
            Content = "🚀 Launch IgnisDisc Studio",
            Padding = new Thickness(18, 9, 18, 9),
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0),
            Visibility = Visibility.Collapsed
        };
        _launchButton.Click += OnLaunchClicked;
        footerStack.Children.Add(_launchButton);

        _closeButton = new System.Windows.Controls.Button
        {
            Content = "Close",
            Padding = new Thickness(18, 9, 18, 9),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _closeButton.Click += (_, _) => Close();
        footerStack.Children.Add(_closeButton);

        Grid.SetRow(footerStack, 2);
        rootGrid.Children.Add(footerStack);

        Content = rootGrid;
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (_isInstalled) return;

        string targetDir = _installDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            System.Windows.MessageBox.Show("Please specify a valid installation folder.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        _installDirBox.Text = targetDir;

        if (!Program.EnsureRunningProcessesClosed(targetDir, silent: false))
        {
            return;
        }

        _installButton.IsEnabled = false;
        _installDirBox.IsEnabled = false;
        _desktopShortcutCheck.IsEnabled = false;
        _startMenuShortcutCheck.IsEnabled = false;
        _addToPathCheck.IsEnabled = false;
        _shellIntegrationCheck.IsEnabled = false;

        bool desktop = _desktopShortcutCheck.IsChecked == true;
        bool startMenu = _startMenuShortcutCheck.IsChecked == true;
        bool addPath = _addToPathCheck.IsChecked == true;
        bool enableShellIntegration = _shellIntegrationCheck.IsChecked == true;

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            _progressBar.Value = report.Percent;
            _statusText.Text = report.Status;
        });

        try
        {
            IReadOnlyList<string> warnings = await Task.Run(() =>
            {
                // The dialog above already asked the user; a second quiet pass closes anything that was started again
                // in the meantime, so no program file is in use when the files are swapped in.
                Program.EnsureRunningProcessesClosed(targetDir, silent: true);
                return Program.InstallPayload(
                    targetDir,
                    desktop,
                    startMenu,
                    addPath,
                    registerUninstaller: _registerUninstaller,
                    progress,
                    ensureClosed: false,
                    shortcuts: _shortcuts,
                    enableShellIntegration: enableShellIntegration,
                    shellRegistry: _shellRegistry);
            });

            _isInstalled = true;
            _installButton.Content = _isUpgrade ? "✓ Upgraded" : "✓ Installed";
            _statusText.Text = _isUpgrade
                ? $"IgnisDisc was successfully upgraded to v{Program.AppVersion}."
                : $"IgnisDisc v{Program.AppVersion} installed successfully.";
            _launchButton.Visibility = Visibility.Visible;
            ExitCode = 0;

            if (warnings.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    $"{Program.AppTitle} v{Program.AppVersion} is installed in:\n{targetDir}\n\n" + string.Join("\n", warnings),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            _statusText.Text = $"Installation failed: {ex.Message}";
            _installButton.IsEnabled = true;
            _installDirBox.IsEnabled = true;
            _desktopShortcutCheck.IsEnabled = true;
            _startMenuShortcutCheck.IsEnabled = true;
            _addToPathCheck.IsEnabled = true;
            _shellIntegrationCheck.IsEnabled = true;
            System.Windows.MessageBox.Show(
                $"Failed to install {Program.AppTitle}:\n\n{ex.Message}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnLaunchClicked(object sender, RoutedEventArgs e)
    {
        string appExe = Path.Combine(_installDirBox.Text.Trim(), "IgnisDisc.exe");
        if (File.Exists(appExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = Path.GetDirectoryName(appExe) ?? string.Empty,
                UseShellExecute = true
            });
            Close();
        }
    }
}
