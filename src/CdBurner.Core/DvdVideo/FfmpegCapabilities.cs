using System.Diagnostics;
using CdBurner.Core.Archival;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// What an installed <c>ffmpeg.exe</c> can do for IgnisDisc's video features: the libx264 encoder (Blu-ray Video and AVCHD
/// need Blu-ray compliant H.264) and the zscale + tonemap filters (HDR movies, see <see cref="HdrToneMapping"/>).
/// LGPL builds, which IgnisDisc's 1-click download installed before 2026-11-26, have the filters but not libx264.
/// </summary>
public sealed record FfmpegCapabilities(bool HasLibx264, bool HasToneMapping)
{
    /// <summary>True when every video feature can run with this build.</summary>
    public bool IsComplete => HasLibx264 && HasToneMapping;

    /// <summary>What is missing, in words, or null when nothing is.</summary>
    public string? MissingDescription => (HasLibx264, HasToneMapping) switch
    {
        (true, true) => null,
        (false, true) => "no libx264 encoder, so it cannot make Blu-ray Video discs",
        (true, false) => "no zscale filter, so HDR movies cannot be converted",
        _ => "no libx264 encoder (Blu-ray Video) and no zscale filter (HDR movies)",
    };

    /// <summary>Reads <c>ffmpeg -encoders</c> and <c>ffmpeg -filters</c> listings.</summary>
    public static FfmpegCapabilities FromListings(IEnumerable<string> encoders, IEnumerable<string> filters)
    {
        ArgumentNullException.ThrowIfNull(encoders);
        ArgumentNullException.ThrowIfNull(filters);
        bool x264 = encoders.Any(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, "libx264", ..]);
        return new FfmpegCapabilities(x264, HdrToneMapping.FilterListingHasToneMapping(filters));
    }

    /// <summary>
    /// Asks the FFmpeg at <paramref name="ffmpegPath"/> for its encoders and filters. Null when the file is not a Windows
    /// program (it is then never started: Windows answers that with a window the user has to close), cannot be
    /// started, or does not answer within <paramref name="timeout"/> (default 5 s per listing).
    /// </summary>
    public static FfmpegCapabilities? Query(string ffmpegPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath) || !Par2ToolVersion.LooksLikeWindowsProgram(ffmpegPath))
        {
            return null;
        }
        var encoders = RunListing(ffmpegPath, "-encoders", timeout ?? TimeSpan.FromSeconds(5));
        var filters = encoders == null ? null : RunListing(ffmpegPath, "-filters", timeout ?? TimeSpan.FromSeconds(5));
        return encoders == null || filters == null ? null : FromListings(encoders, filters);
    }

    private static string[]? RunListing(string ffmpegPath, string listing, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add(listing);
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }
            // Both pipes read at once so a full stderr buffer cannot stall FFmpeg.
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeout))
            {
                CdBurner.Core.Utilities.ProcessAndFileCleanup.KillTreeAndWait(proc);
                return null;
            }
            proc.WaitForExit();
            _ = stderr.Result;
            return proc.ExitCode == 0 ? stdout.Result.Split('\n', StringSplitOptions.TrimEntries) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
