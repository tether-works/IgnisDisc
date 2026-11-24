using System.IO;
using CdBurner.Core.Platform;

namespace CdBurner.Setup.Tests;

public sealed class ShellIntegrationSetupTests : IDisposable
{
    private readonly string _root;
    private readonly string _payload;
    private readonly string _installDir;
    private readonly ShortcutManager _shortcuts;

    public ShellIntegrationSetupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ignisdisc-shell-setup-tests-" + Guid.NewGuid().ToString("N"));
        _payload = Path.Combine(_root, "payload");
        _installDir = Path.Combine(_root, "Programs", "IgnisDisc");
        Directory.CreateDirectory(_payload);
        File.WriteAllText(Path.Combine(_payload, "IgnisDisc.exe"), "app");
        _shortcuts = ShortcutManager.ForRoot(Path.Combine(_root, "shell"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class MockShellRegistry : IShellIntegrationRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool ThrowOnWrite { get; set; }
        public IReadOnlyCollection<string> KeyPaths => _keys.Keys;

        public void SetValue(string subKeyPath, string? valueName, string value)
        {
            if (ThrowOnWrite) throw new UnauthorizedAccessException("Mock registry write denied");
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
            foreach (string key in _keys.Keys.Where(k =>
                string.Equals(k, subKeyPath, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
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
                .Select(r => { int i = r.IndexOf('\\'); return i >= 0 ? r[..i] : r; })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetValueNames(string subKeyPath) =>
            _keys.TryGetValue(subKeyPath, out var values) ? values.Keys.ToList() : Array.Empty<string>();

        public void NotifyShellChanged() { }
    }

    [Fact]
    public void ParseArguments_ShellIntegration_IsOptInDefaultOff()
    {
        var defaults = Program.ParseArguments(new[] { "--silent" }, @"C:\default");
        Assert.False(defaults.EnableShellIntegration);

        var enabled = Program.ParseArguments(new[] { "--silent", "--shell-integration" }, @"C:\default");
        Assert.True(enabled.EnableShellIntegration);
    }

    [Fact]
    public void InstallPayload_DefaultDoesNotRegisterShellKeys()
    {
        var registry = new MockShellRegistry();

        var warnings = Program.InstallPayload(
            _installDir,
            createDesktopShortcut: false,
            createStartMenuShortcut: false,
            addToPath: false,
            registerUninstaller: false,
            progress: null,
            ensureClosed: false,
            shortcuts: _shortcuts,
            payloadDirectory: _payload,
            shellRegistry: registry);

        Assert.Empty(warnings);
        Assert.Empty(registry.KeyPaths);
        Assert.False(ShellIntegrationManager.IsRegistered(registry));
    }

    [Fact]
    public void InstallPayload_OptIn_RegistersShellKeys_AndUninstallRemovesAllKeys()
    {
        var registry = new MockShellRegistry();

        var warnings = Program.InstallPayload(
            _installDir,
            createDesktopShortcut: false,
            createStartMenuShortcut: false,
            addToPath: false,
            registerUninstaller: false,
            progress: null,
            ensureClosed: false,
            shortcuts: _shortcuts,
            payloadDirectory: _payload,
            enableShellIntegration: true,
            shellRegistry: registry);

        Assert.Empty(warnings);
        Assert.True(ShellIntegrationManager.IsRegistered(registry));
        string installedExe = Path.Combine(_installDir, "IgnisDisc.exe");
        Assert.Equal($"\"{installedExe}\" --open-image \"%1\"", registry.GetValue(ShellIntegrationManager.ProgIdKeyPath + @"\shell\open\command", null));

        // Idempotent reinstall
        Program.InstallPayload(
            _installDir,
            createDesktopShortcut: false,
            createStartMenuShortcut: false,
            addToPath: false,
            registerUninstaller: false,
            progress: null,
            ensureClosed: false,
            shortcuts: _shortcuts,
            payloadDirectory: _payload,
            enableShellIntegration: true,
            shellRegistry: registry);
        Assert.True(ShellIntegrationManager.IsRegistered(registry));

        // Uninstall cleanly removes every registered HKCU key
        int exitCode = Program.RunUninstall(
            _installDir,
            silent: true,
            _shortcuts,
            cleanupScheduler: (_, _) => true,
            shellRegistry: registry);

        Assert.Equal(0, exitCode);
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
        Assert.Empty(registry.KeyPaths);
    }

    [Fact]
    public void InstallPayload_RecordsWarningWhenShellRegistryWriteFails()
    {
        var registry = new MockShellRegistry { ThrowOnWrite = true };

        var warnings = Program.InstallPayload(
            _installDir,
            createDesktopShortcut: false,
            createStartMenuShortcut: false,
            addToPath: false,
            registerUninstaller: false,
            progress: null,
            ensureClosed: false,
            shortcuts: _shortcuts,
            payloadDirectory: _payload,
            enableShellIntegration: true,
            shellRegistry: registry);

        Assert.Single(warnings);
        Assert.Contains("Explorer shell integration", warnings[0]);
    }
}
