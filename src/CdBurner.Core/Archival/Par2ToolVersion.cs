using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CdBurner.Core.Archival;

/// <summary>
/// Which par2 program is installed and its version, read from the first line of <c>par2 -V</c>
/// (for example "par2cmdline version 1.4.0" or "par2cmdline-turbo version 1.5.0").
/// </summary>
/// <param name="Product">Program name as printed: "par2cmdline" or "par2cmdline-turbo".</param>
/// <param name="Version">Version number as printed.</param>
public sealed partial record Par2ToolVersion(string Product, Version Version)
{
    /// <summary>Program name printed by par2cmdline-turbo.</summary>
    public const string TurboProductName = "par2cmdline-turbo";

    /// <summary>True for par2cmdline-turbo, false for the original par2cmdline.</summary>
    public bool IsTurbo => string.Equals(Product, TurboProductName, StringComparison.Ordinal);

    /// <summary>Text shown to the user, for example "par2cmdline 1.4.0".</summary>
    public override string ToString() => $"{Product} {Version}";

    [GeneratedRegex(@"^(par2cmdline(?:-turbo)?) version (\d+\.\d+(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    /// <summary>
    /// Parses the output of <c>par2 -V</c>. Returns null when the first non-empty line is not a par2cmdline or
    /// par2cmdline-turbo version line.
    /// </summary>
    public static Par2ToolVersion? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string firstLine = output
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

        Match match = VersionLine().Match(firstLine);
        if (!match.Success || !Version.TryParse(match.Groups[2].Value, out Version? version))
        {
            return null;
        }

        return new Par2ToolVersion(match.Groups[1].Value, version);
    }

    /// <summary>
    /// Runs <c>par2 -V</c> and parses its answer. Returns null if the file cannot be started, does not answer within
    /// <paramref name="timeout"/> (default 5 s), exits with an error, or prints something else.
    /// </summary>
    public static Par2ToolVersion? Query(string par2Path, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(par2Path) || !File.Exists(par2Path))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = par2Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-V");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            // Read both pipes asynchronously so a full stderr buffer cannot stall the child.
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeout ?? TimeSpan.FromSeconds(5)))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the wait and the kill.
                }

                return null;
            }

            proc.WaitForExit();
            _ = stderr.Result;
            return proc.ExitCode == 0 ? Parse(stdout.Result) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not an executable Windows can start (for example a damaged or placeholder file).
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
