using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace CdBurner.Core.Platform;

/// <summary>
/// Abstraction over per-user Windows Registry operations (<c>HKEY_CURRENT_USER</c>) used by
/// Explorer shell integration so registration and removal can be tested deterministically offline.
/// </summary>
public interface IShellIntegrationRegistry
{
    /// <summary>
    /// Creates or opens <paramref name="subKeyPath"/> under <c>HKEY_CURRENT_USER</c> and writes a string value.
    /// Pass <see langword="null"/> or empty string for <paramref name="valueName"/> to set the key's default value.
    /// </summary>
    void SetValue(string subKeyPath, string? valueName, string value);

    /// <summary>
    /// Reads a string value from <paramref name="subKeyPath"/> under <c>HKEY_CURRENT_USER</c>, or returns
    /// <see langword="null"/> when the key or value does not exist.
    /// </summary>
    string? GetValue(string subKeyPath, string? valueName);

    /// <summary>
    /// Removes the named value from <paramref name="subKeyPath"/> under <c>HKEY_CURRENT_USER</c> if present.
    /// Pass <see langword="null"/> or empty string for <paramref name="valueName"/> to delete the key's default value.
    /// </summary>
    void DeleteValue(string subKeyPath, string? valueName);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="subKeyPath"/> exists under <c>HKEY_CURRENT_USER</c>.
    /// </summary>
    bool SubKeyExists(string subKeyPath);

    /// <summary>
    /// Recursively deletes <paramref name="subKeyPath"/> under <c>HKEY_CURRENT_USER</c> if it exists.
    /// </summary>
    void DeleteSubKeyTree(string subKeyPath);

    /// <summary>
    /// Returns the immediate child subkey names under <paramref name="subKeyPath"/>, or an empty list if absent.
    /// </summary>
    IReadOnlyList<string> GetSubKeyNames(string subKeyPath);

    /// <summary>
    /// Returns the value names stored directly on <paramref name="subKeyPath"/>, or an empty list if absent.
    /// </summary>
    IReadOnlyList<string> GetValueNames(string subKeyPath);

    /// <summary>
    /// Notifies Windows Explorer that file associations or shell verbs have changed.
    /// </summary>
    void NotifyShellChanged();
}

/// <summary>
/// Live <c>HKEY_CURRENT_USER</c> registry implementation for Windows Explorer shell integration.
/// </summary>
public sealed class WindowsShellIntegrationRegistry : IShellIntegrationRegistry
{
    public static WindowsShellIntegrationRegistry Instance { get; } = new();

    private WindowsShellIntegrationRegistry()
    {
    }

    public void SetValue(string subKeyPath, string? valueName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key.SetValue(string.IsNullOrEmpty(valueName) ? null : valueName, value, RegistryValueKind.String);
    }

    public string? GetValue(string subKeyPath, string? valueName)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(string.IsNullOrEmpty(valueName) ? null : valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void DeleteValue(string subKeyPath, string? valueName)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        if (key == null) return;
        string? normalizedName = string.IsNullOrEmpty(valueName) ? string.Empty : valueName;
        key.DeleteValue(normalizedName, throwOnMissingValue: false);
    }

    public bool SubKeyExists(string subKeyPath)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return false;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key != null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void DeleteSubKeyTree(string subKeyPath)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return;
        Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
    }

    public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return Array.Empty<string>();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetValueNames(string subKeyPath)
    {
        if (string.IsNullOrWhiteSpace(subKeyPath)) return Array.Empty<string>();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    public void NotifyShellChanged()
    {
        const uint ShcneAssocChanged = 0x08000000;
        const uint ShcnfIdList = 0x0000;
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

/// <summary>
/// No-op registry implementation used for isolated test runs that must not read or write the host user's HKCU hive.
/// </summary>
public sealed class NullShellIntegrationRegistry : IShellIntegrationRegistry
{
    public static NullShellIntegrationRegistry Instance { get; } = new();

    private NullShellIntegrationRegistry()
    {
    }

    public void SetValue(string subKeyPath, string? valueName, string value) { }
    public string? GetValue(string subKeyPath, string? valueName) => null;
    public void DeleteValue(string subKeyPath, string? valueName) { }
    public bool SubKeyExists(string subKeyPath) => false;
    public void DeleteSubKeyTree(string subKeyPath) { }
    public IReadOnlyList<string> GetSubKeyNames(string subKeyPath) => Array.Empty<string>();
    public IReadOnlyList<string> GetValueNames(string subKeyPath) => Array.Empty<string>();
    public void NotifyShellChanged() { }
}
