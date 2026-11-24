using CdBurner.Core.Persistence;
using CdBurner.Core.Platform;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class ShellIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ShellIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_ShellTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class InMemoryShellIntegrationRegistry : IShellIntegrationRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public int NotifyCount { get; private set; }

        public IReadOnlyCollection<string> KeyPaths => _keys.Keys;

        public void SetValue(string subKeyPath, string? valueName, string value)
        {
            if (!_keys.TryGetValue(subKeyPath, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _keys[subKeyPath] = values;
            }

            values[valueName ?? string.Empty] = value;
        }

        public string? GetValue(string subKeyPath, string? valueName)
        {
            if (!_keys.TryGetValue(subKeyPath, out var values)) return null;
            return values.TryGetValue(valueName ?? string.Empty, out var value) ? value : null;
        }

        public void DeleteValue(string subKeyPath, string? valueName)
        {
            if (_keys.TryGetValue(subKeyPath, out var values))
            {
                values.Remove(valueName ?? string.Empty);
            }
        }

        public bool SubKeyExists(string subKeyPath) => _keys.ContainsKey(subKeyPath);

        public void DeleteSubKeyTree(string subKeyPath)
        {
            string prefix = subKeyPath.TrimEnd('\\') + @"\";
            var toRemove = _keys.Keys
                .Where(k => string.Equals(k, subKeyPath, StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string key in toRemove)
            {
                _keys.Remove(key);
            }
        }

        public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
        {
            string prefix = subKeyPath.TrimEnd('\\') + @"\";
            return _keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..])
                .Select(rest =>
                {
                    int slash = rest.IndexOf('\\');
                    return slash >= 0 ? rest[..slash] : rest;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetValueNames(string subKeyPath)
        {
            return _keys.TryGetValue(subKeyPath, out var values)
                ? values.Keys.ToList()
                : Array.Empty<string>();
        }

        public void NotifyShellChanged() => NotifyCount++;

        public Dictionary<string, Dictionary<string, string>> Snapshot() =>
            _keys.ToDictionary(
                kvp => kvp.Key,
                kvp => new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppSettings_ExplorerShellIntegration_DefaultsToFalse_AndClones()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.EnableExplorerShellIntegration);

        defaults.EnableExplorerShellIntegration = true;
        var clone = defaults.Clone();
        Assert.True(clone.EnableExplorerShellIntegration);
    }

    [Fact]
    public void Register_CreatesAllPerUserExplorerVerbsAndFileAssociations()
    {
        var registry = new InMemoryShellIntegrationRegistry();
        string exePath = Path.Combine(_tempDir, "IgnisDisc.exe");

        ShellIntegrationManager.Register(registry, exePath);

        Assert.True(ShellIntegrationManager.IsRegistered(registry));
        Assert.Equal(1, registry.NotifyCount);
        Assert.All(registry.KeyPaths, k => Assert.StartsWith(@"Software\Classes\", k, StringComparison.OrdinalIgnoreCase));

        // Image extensions: .iso, .cue, .img, .nrg, .mds, .ccd, .cdi
        string[] expectedExts = [".iso", ".cue", ".img", ".nrg", ".mds", ".ccd", ".cdi"];
        Assert.Equal(expectedExts, ShellIntegrationManager.SupportedImageExtensions);

        foreach (string ext in expectedExts)
        {
            Assert.Equal(string.Empty, registry.GetValue($@"Software\Classes\{ext}\OpenWithProgids", ShellIntegrationManager.ProgId));

            string burnKey = $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ShellIntegrationManager.BurnImageVerbKeyName}";
            Assert.Equal("Burn disc image with IgnisDisc", registry.GetValue(burnKey, null));
            Assert.Equal($"\"{exePath}\" --open-image \"%1\"", registry.GetValue(burnKey + @"\command", null));

            string browseKey = $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ShellIntegrationManager.BrowseImageVerbKeyName}";
            Assert.Equal("Browse image in IgnisDisc", registry.GetValue(browseKey, null));
            Assert.Equal($"\"{exePath}\" --browse-image \"%1\"", registry.GetValue(browseKey + @"\command", null));
        }

        // Folder verbs
        Assert.Equal("Burn folder to disc with IgnisDisc", registry.GetValue(ShellIntegrationManager.BurnFolderKeyPath, null));
        Assert.Equal($"\"{exePath}\" --burn-folder \"%1\"", registry.GetValue(ShellIntegrationManager.BurnFolderKeyPath + @"\command", null));
        Assert.Equal("Archive folder with IgnisDisc", registry.GetValue(ShellIntegrationManager.ArchiveFolderKeyPath, null));
        Assert.Equal($"\"{exePath}\" --archive-folder \"%1\"", registry.GetValue(ShellIntegrationManager.ArchiveFolderKeyPath + @"\command", null));

        // Drive verbs
        Assert.Equal("Verify disc in IgnisDisc", registry.GetValue(ShellIntegrationManager.VerifyDriveKeyPath, null));
        Assert.Equal($"\"{exePath}\" --verify-drive \"%1\"", registry.GetValue(ShellIntegrationManager.VerifyDriveKeyPath + @"\command", null));
        Assert.Equal("Create image in IgnisDisc", registry.GetValue(ShellIntegrationManager.ReadDriveKeyPath, null));
        Assert.Equal($"\"{exePath}\" --read-drive \"%1\"", registry.GetValue(ShellIntegrationManager.ReadDriveKeyPath + @"\command", null));
    }

    [Fact]
    public void Register_AndUnregister_AreIdempotent_AndCleanlyRemoveAllKeys()
    {
        var registry = new InMemoryShellIntegrationRegistry();
        string exePath = Path.Combine(_tempDir, "IgnisDisc.exe");

        ShellIntegrationManager.Register(registry, exePath);
        var firstSnapshot = registry.Snapshot();

        ShellIntegrationManager.Register(registry, exePath);
        var secondSnapshot = registry.Snapshot();

        Assert.Equal(firstSnapshot.Keys.OrderBy(k => k), secondSnapshot.Keys.OrderBy(k => k));
        foreach (string key in firstSnapshot.Keys)
        {
            Assert.Equal(firstSnapshot[key], secondSnapshot[key]);
        }

        ShellIntegrationManager.Unregister(registry);
        Assert.False(ShellIntegrationManager.IsRegistered(registry));
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
        Assert.Empty(registry.KeyPaths);

        // Second unregister is a clean no-op
        ShellIntegrationManager.Unregister(registry);
        Assert.Empty(registry.KeyPaths);
    }

    [Fact]
    public void Unregister_PreservesExistingOsAndThirdPartyAssociations()
    {
        var registry = new InMemoryShellIntegrationRegistry();
        registry.SetValue(@"Software\Classes\.iso", null, "Windows.IsoFile");
        registry.SetValue(@"Software\Classes\.iso\OpenWithProgids", "Windows.IsoFile", string.Empty);
        registry.SetValue(@"Software\Classes\Directory\shell\OtherApp", null, "Open with OtherApp");
        registry.SetValue(@"Software\Classes\Drive\shell\OtherDriveTool", null, "Inspect with OtherDriveTool");

        string exePath = Path.Combine(_tempDir, "IgnisDisc.exe");
        ShellIntegrationManager.Register(registry, exePath);

        // Must not clobber existing .iso ProgID
        Assert.Equal("Windows.IsoFile", registry.GetValue(@"Software\Classes\.iso", null));

        ShellIntegrationManager.Unregister(registry);

        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
        Assert.Equal("Windows.IsoFile", registry.GetValue(@"Software\Classes\.iso", null));
        Assert.Equal(string.Empty, registry.GetValue(@"Software\Classes\.iso\OpenWithProgids", "Windows.IsoFile"));
        Assert.Equal("Open with OtherApp", registry.GetValue(@"Software\Classes\Directory\shell\OtherApp", null));
        Assert.Equal("Inspect with OtherDriveTool", registry.GetValue(@"Software\Classes\Drive\shell\OtherDriveTool", null));
    }

    [Fact]
    public void UnregisterForInstall_KeepsKeysWhenAnotherExistingInstallOwnsThem()
    {
        var registry = new InMemoryShellIntegrationRegistry();
        string installA = Path.Combine(_tempDir, "InstallA");
        string installB = Path.Combine(_tempDir, "InstallB");
        Directory.CreateDirectory(installA);
        Directory.CreateDirectory(installB);
        string exeA = Path.Combine(installA, "IgnisDisc.exe");
        string exeB = Path.Combine(installB, "IgnisDisc.exe");
        File.WriteAllText(exeB, "app");

        ShellIntegrationManager.Register(registry, exeB);
        ShellIntegrationManager.UnregisterForInstall(registry, installA);

        Assert.True(ShellIntegrationManager.IsRegistered(registry));

        ShellIntegrationManager.UnregisterForInstall(registry, installB);
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));

        // Also removes stale keys whose target exe no longer exists
        ShellIntegrationManager.Register(registry, exeA);
        ShellIntegrationManager.UnregisterForInstall(registry, installB);
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
    }
}
