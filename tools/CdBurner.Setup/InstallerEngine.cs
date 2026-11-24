using System.IO;
using System.Security;
using CdBurner.Core.Platform;

namespace CdBurner.Setup;

/// <summary>
/// Coordinates per-user Explorer shell integration registration and removal during setup and uninstall.
/// </summary>
internal static class InstallerEngine
{
    /// <summary>
    /// Registers per-user <c>HKCU\Software\Classes</c> Explorer context menus and file associations when
    /// <paramref name="enableShellIntegration"/> is <see langword="true"/>. Any registry access failure is
    /// returned as a non-fatal warning message so installation itself still completes.
    /// </summary>
    public static string? ApplyShellIntegration(
        bool enableShellIntegration,
        string appExePath,
        IShellIntegrationRegistry? shellRegistry = null)
    {
        if (!enableShellIntegration)
        {
            return null;
        }

        IShellIntegrationRegistry registry = shellRegistry ?? WindowsShellIntegrationRegistry.Instance;
        try
        {
            ShellIntegrationManager.Register(registry, appExePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Windows Explorer shell integration could not be registered: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes per-user <c>HKCU\Software\Classes</c> Explorer shell integration entries belonging to
    /// <paramref name="installDir"/> during uninstall. Best-effort; never throws on registry permission failures.
    /// </summary>
    public static void RemoveShellIntegrationOnUninstall(
        string installDir,
        IShellIntegrationRegistry? shellRegistry = null)
    {
        IShellIntegrationRegistry registry = shellRegistry ?? WindowsShellIntegrationRegistry.Instance;
        try
        {
            ShellIntegrationManager.UnregisterForInstall(registry, installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }
}
