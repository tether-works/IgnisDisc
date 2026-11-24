namespace CdBurner.Core.Platform;

/// <summary>
/// Registers and removes per-user (<c>HKCU\Software\Classes</c>) Windows Explorer context menus and disc image
/// file associations for IgnisDisc without requiring administrator privileges.
/// </summary>
public static class ShellIntegrationManager
{
    public const string ClassesRootPrefix = @"Software\Classes";
    public const string ProgId = "IgnisDisc.DiscImage";
    public const string AppKeyName = "IgnisDisc.exe";
    public const string ProgIdKeyPath = ClassesRootPrefix + @"\" + ProgId;
    public const string ApplicationKeyPath = ClassesRootPrefix + @"\Applications\" + AppKeyName;

    public const string BurnImageVerbKeyName = "IgnisDisc.BurnImage";
    public const string BrowseImageVerbKeyName = "IgnisDisc.BrowseImage";
    public const string BurnFolderVerbKeyName = "IgnisDisc.BurnFolder";
    public const string ArchiveFolderVerbKeyName = "IgnisDisc.ArchiveFolder";
    public const string VerifyDriveVerbKeyName = "IgnisDisc.VerifyDrive";
    public const string ReadDriveVerbKeyName = "IgnisDisc.ReadDrive";

    public const string BurnImageLabel = "Burn disc image with IgnisDisc";
    public const string BrowseImageLabel = "Browse image in IgnisDisc";
    public const string BurnFolderLabel = "Burn folder to disc with IgnisDisc";
    public const string ArchiveFolderLabel = "Archive folder with IgnisDisc";
    public const string VerifyDriveLabel = "Verify disc in IgnisDisc";
    public const string ReadDriveLabel = "Create image in IgnisDisc";

    public const string BurnFolderKeyPath = ClassesRootPrefix + @"\Directory\shell\" + BurnFolderVerbKeyName;
    public const string ArchiveFolderKeyPath = ClassesRootPrefix + @"\Directory\shell\" + ArchiveFolderVerbKeyName;
    public const string VerifyDriveKeyPath = ClassesRootPrefix + @"\Drive\shell\" + VerifyDriveVerbKeyName;
    public const string ReadDriveKeyPath = ClassesRootPrefix + @"\Drive\shell\" + ReadDriveVerbKeyName;

    /// <summary>
    /// Disc image extensions registered for Explorer context menus and Open With associations.
    /// </summary>
    public static IReadOnlyList<string> SupportedImageExtensions { get; } =
    [
        ".iso", ".cue", ".img", ".nrg", ".mds", ".ccd", ".cdi"
    ];

    /// <summary>
    /// Registers per-user <c>HKCU\Software\Classes</c> file associations, icon handlers, and Explorer context menu
    /// verbs pointing at <paramref name="appExePath"/>. Safe to call repeatedly.
    /// </summary>
    public static void Register(IShellIntegrationRegistry registry, string appExePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(appExePath);

        string normalizedExe = Path.GetFullPath(appExePath);
        string iconValue = $"\"{normalizedExe}\",0";

        // 1. ProgID for disc image file associations
        registry.SetValue(ProgIdKeyPath, null, "IgnisDisc Disc Image");
        registry.SetValue(ProgIdKeyPath + @"\DefaultIcon", null, iconValue);
        WriteVerb(registry, ProgIdKeyPath + @"\shell\open", BurnImageLabel, iconValue, normalizedExe, "--open-image");
        WriteVerb(registry, ProgIdKeyPath + @"\shell\browse", BrowseImageLabel, iconValue, normalizedExe, "--browse-image");

        // 2. Applications\IgnisDisc.exe entry for Open With dialog
        registry.SetValue(ApplicationKeyPath, "FriendlyAppName", "IgnisDisc Optical Studio");
        WriteVerb(registry, ApplicationKeyPath + @"\shell\open", BurnImageLabel, iconValue, normalizedExe, "--open-image");

        // 3. Per-extension OpenWithProgids, default association (only when unassigned), and SystemFileAssociations verbs
        foreach (string ext in SupportedImageExtensions)
        {
            registry.SetValue(ApplicationKeyPath + @"\SupportedTypes", ext, string.Empty);

            string extKey = ClassesRootPrefix + @"\" + ext;
            string? existingDefault = registry.GetValue(extKey, null);
            if (string.IsNullOrWhiteSpace(existingDefault) ||
                string.Equals(existingDefault, ProgId, StringComparison.OrdinalIgnoreCase))
            {
                registry.SetValue(extKey, null, ProgId);
            }

            registry.SetValue(extKey + @"\OpenWithProgids", ProgId, string.Empty);

            string sfaShell = ClassesRootPrefix + @"\SystemFileAssociations\" + ext + @"\shell";
            WriteVerb(registry, sfaShell + @"\" + BurnImageVerbKeyName, BurnImageLabel, iconValue, normalizedExe, "--open-image");
            WriteVerb(registry, sfaShell + @"\" + BrowseImageVerbKeyName, BrowseImageLabel, iconValue, normalizedExe, "--browse-image");
        }

        // 4. Folder context menus (Directory\shell)
        WriteVerb(registry, BurnFolderKeyPath, BurnFolderLabel, iconValue, normalizedExe, "--burn-folder");
        WriteVerb(registry, ArchiveFolderKeyPath, ArchiveFolderLabel, iconValue, normalizedExe, "--archive-folder");

        // 5. Optical drive context menus (Drive\shell)
        WriteVerb(registry, VerifyDriveKeyPath, VerifyDriveLabel, iconValue, normalizedExe, "--verify-drive");
        WriteVerb(registry, ReadDriveKeyPath, ReadDriveLabel, iconValue, normalizedExe, "--read-drive");

        registry.NotifyShellChanged();
    }

    /// <summary>
    /// Removes all per-user Explorer shell integration keys and values registered by IgnisDisc, preserving any
    /// unrelated associations or verbs owned by Windows or other applications. Safe to call repeatedly.
    /// </summary>
    public static void Unregister(IShellIntegrationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.DeleteSubKeyTree(ProgIdKeyPath);
        registry.DeleteSubKeyTree(ApplicationKeyPath);

        foreach (string ext in SupportedImageExtensions)
        {
            string sfaExtKey = ClassesRootPrefix + @"\SystemFileAssociations\" + ext;
            string sfaShellKey = sfaExtKey + @"\shell";
            registry.DeleteSubKeyTree(sfaShellKey + @"\" + BurnImageVerbKeyName);
            registry.DeleteSubKeyTree(sfaShellKey + @"\" + BrowseImageVerbKeyName);
            DeleteKeyIfEmpty(registry, sfaShellKey);
            DeleteKeyIfEmpty(registry, sfaExtKey);

            string extKey = ClassesRootPrefix + @"\" + ext;
            string openWithKey = extKey + @"\OpenWithProgids";
            registry.DeleteValue(openWithKey, ProgId);
            DeleteKeyIfEmpty(registry, openWithKey);

            string? currentDefault = registry.GetValue(extKey, null);
            if (string.Equals(currentDefault, ProgId, StringComparison.OrdinalIgnoreCase))
            {
                registry.DeleteValue(extKey, null);
            }

            DeleteKeyIfEmpty(registry, extKey);
        }

        registry.DeleteSubKeyTree(BurnFolderKeyPath);
        registry.DeleteSubKeyTree(ArchiveFolderKeyPath);
        registry.DeleteSubKeyTree(VerifyDriveKeyPath);
        registry.DeleteSubKeyTree(ReadDriveKeyPath);

        registry.NotifyShellChanged();
    }

    /// <summary>
    /// Unregisters shell integration during uninstall unless the current registration points to a different
    /// IgnisDisc installation directory that still exists on disk.
    /// </summary>
    public static void UnregisterForInstall(IShellIntegrationRegistry registry, string installDir)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);

        string normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)) + Path.DirectorySeparatorChar;
        string? registeredCommand =
            registry.GetValue(ProgIdKeyPath + @"\shell\open\command", null)
            ?? registry.GetValue(BurnFolderKeyPath + @"\command", null)
            ?? registry.GetValue(VerifyDriveKeyPath + @"\command", null);

        string? registeredExe = TryExtractExePathFromCommand(registeredCommand);
        if (!string.IsNullOrEmpty(registeredExe))
        {
            string fullExe = Path.GetFullPath(registeredExe);
            bool belongsToThisInstall = fullExe.StartsWith(normalizedInstallDir, StringComparison.OrdinalIgnoreCase);
            if (!belongsToThisInstall && File.Exists(fullExe))
            {
                return;
            }
        }

        Unregister(registry);
    }

    /// <summary>
    /// Returns <see langword="true"/> when IgnisDisc shell integration keys are currently registered.
    /// </summary>
    public static bool IsRegistered(IShellIntegrationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.SubKeyExists(ProgIdKeyPath)
               && registry.SubKeyExists(BurnFolderKeyPath)
               && registry.SubKeyExists(VerifyDriveKeyPath);
    }

    /// <summary>
    /// Returns <see langword="true"/> if any IgnisDisc shell integration key remains in <paramref name="registry"/>.
    /// </summary>
    public static bool HasAnyRegisteredKeys(IShellIntegrationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (registry.SubKeyExists(ProgIdKeyPath) ||
            registry.SubKeyExists(ApplicationKeyPath) ||
            registry.SubKeyExists(BurnFolderKeyPath) ||
            registry.SubKeyExists(ArchiveFolderKeyPath) ||
            registry.SubKeyExists(VerifyDriveKeyPath) ||
            registry.SubKeyExists(ReadDriveKeyPath))
        {
            return true;
        }

        foreach (string ext in SupportedImageExtensions)
        {
            string sfaShellKey = ClassesRootPrefix + @"\SystemFileAssociations\" + ext + @"\shell";
            if (registry.SubKeyExists(sfaShellKey + @"\" + BurnImageVerbKeyName) ||
                registry.SubKeyExists(sfaShellKey + @"\" + BrowseImageVerbKeyName))
            {
                return true;
            }

            string openWithKey = ClassesRootPrefix + @"\" + ext + @"\OpenWithProgids";
            if (registry.GetValueNames(openWithKey).Any(n => string.Equals(n, ProgId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? TryExtractExePathFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return trimmed[1..endQuote];
            }
        }

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private static void WriteVerb(
        IShellIntegrationRegistry registry,
        string verbKeyPath,
        string label,
        string iconValue,
        string exePath,
        string cliFlag)
    {
        registry.SetValue(verbKeyPath, null, label);
        registry.SetValue(verbKeyPath, "Icon", iconValue);
        registry.SetValue(verbKeyPath + @"\command", null, $"\"{exePath}\" {cliFlag} \"%1\"");
    }

    private static void DeleteKeyIfEmpty(IShellIntegrationRegistry registry, string subKeyPath)
    {
        if (!registry.SubKeyExists(subKeyPath)) return;
        if (registry.GetSubKeyNames(subKeyPath).Count == 0 && registry.GetValueNames(subKeyPath).Count == 0)
        {
            registry.DeleteSubKeyTree(subKeyPath);
        }
    }
}
