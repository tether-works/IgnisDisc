using System.IO;
using CdBurner.Core.Platform;

namespace CdBurner.App.Launch;

/// <summary>
/// Shell launch verb parsed from command-line arguments.
/// </summary>
public enum AppLaunchAction
{
    None,
    OpenImage,
    BrowseImage,
    BurnFolder,
    ArchiveFolder,
    VerifyDrive,
    ReadDrive
}

/// <summary>
/// Parsed startup routing intent for <c>CdBurner.App</c>.
/// </summary>
public sealed record AppLaunchOptions(AppLaunchAction Action, string? TargetPath);

/// <summary>
/// Parses Windows Explorer shell verbs and positional file/folder paths passed to <c>IgnisDisc.exe</c>.
/// </summary>
public static class AppLaunchParser
{
    private static readonly HashSet<string> SupportedImageExtensions = new(
        ShellIntegrationManager.SupportedImageExtensions,
        StringComparer.OrdinalIgnoreCase);

    public static AppLaunchOptions Parse(string[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return new AppLaunchOptions(AppLaunchAction.None, null);
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i]?.Trim() ?? string.Empty;
            if (arg.Length == 0) continue;

            if (TryMatchFlag(arg, "--open-image", out AppLaunchAction action) ||
                TryMatchFlag(arg, "--browse-image", out action) ||
                TryMatchFlag(arg, "--burn-folder", out action) ||
                TryMatchFlag(arg, "--archive-folder", out action))
            {
                string? rawValue = ExtractFlagValue(args, ref i);
                return string.IsNullOrWhiteSpace(rawValue)
                    ? new AppLaunchOptions(AppLaunchAction.None, null)
                    : new AppLaunchOptions(action, NormalizePath(rawValue));
            }

            if (TryMatchFlag(arg, "--verify-drive", out action) ||
                TryMatchFlag(arg, "--read-drive", out action))
            {
                string? rawValue = ExtractFlagValue(args, ref i);
                return string.IsNullOrWhiteSpace(rawValue)
                    ? new AppLaunchOptions(AppLaunchAction.None, null)
                    : new AppLaunchOptions(action, NormalizeDriveTarget(rawValue));
            }
        }

        // Positional fallback (e.g., double-clicking a supported image file or dropping a folder on the exe)
        foreach (string raw in args)
        {
            string candidate = NormalizePath(raw);
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("--", StringComparison.Ordinal) || candidate.StartsWith('/'))
            {
                continue;
            }

            string ext = Path.GetExtension(candidate);
            if (!string.IsNullOrEmpty(ext) && SupportedImageExtensions.Contains(ext))
            {
                return new AppLaunchOptions(AppLaunchAction.OpenImage, candidate);
            }

            if (Directory.Exists(candidate))
            {
                return new AppLaunchOptions(AppLaunchAction.BurnFolder, candidate);
            }
        }

        return new AppLaunchOptions(AppLaunchAction.None, null);
    }

    private static bool TryMatchFlag(string arg, string flag, out AppLaunchAction action)
    {
        if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
        {
            action = flag switch
            {
                "--open-image" => AppLaunchAction.OpenImage,
                "--browse-image" => AppLaunchAction.BrowseImage,
                "--burn-folder" => AppLaunchAction.BurnFolder,
                "--archive-folder" => AppLaunchAction.ArchiveFolder,
                "--verify-drive" => AppLaunchAction.VerifyDrive,
                "--read-drive" => AppLaunchAction.ReadDrive,
                _ => AppLaunchAction.None
            };
            return true;
        }

        action = AppLaunchAction.None;
        return false;
    }

    private static string? ExtractFlagValue(string[] args, ref int index)
    {
        string current = args[index];
        int eq = current.IndexOf('=');
        if (eq >= 0)
        {
            return current[(eq + 1)..];
        }

        if (index + 1 < args.Length)
        {
            return args[++index];
        }

        return null;
    }

    /// <summary>
    /// Normalizes a filesystem path from shell command lines, stripping stray quote artifacts caused by
    /// Windows <c>\"</c> escaping on paths ending with a backslash.
    /// </summary>
    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string cleaned = raw.Trim().Trim('"').Trim();
        if (cleaned.Length == 2 && char.IsLetter(cleaned[0]) && cleaned[1] == ':')
        {
            cleaned += Path.DirectorySeparatorChar;
        }

        return cleaned;
    }

    /// <summary>
    /// Normalizes a drive target (<c>E</c>, <c>E:</c>, <c>E:\</c>, <c>E:"</c>, or <c>VDRV-01</c>).
    /// </summary>
    public static string NormalizeDriveTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string cleaned = raw.Trim().Trim('"').Trim();
        if (cleaned.Length == 1 && char.IsLetter(cleaned[0]))
        {
            return char.ToUpperInvariant(cleaned[0]) + @":\";
        }

        if (cleaned.Length >= 2 && char.IsLetter(cleaned[0]) && cleaned[1] == ':')
        {
            string rest = cleaned[2..].TrimStart('\\', '/');
            if (rest.Length == 0)
            {
                return char.ToUpperInvariant(cleaned[0]) + @":\";
            }
        }

        return cleaned;
    }
}
