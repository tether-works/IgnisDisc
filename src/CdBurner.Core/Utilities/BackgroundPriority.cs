using System.Diagnostics;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Long FFmpeg and whisper.cpp jobs (Blu-ray and DVD conversions, subtitles) use every CPU core for an hour or more.
/// At normal priority they starve every below-normal program on the PC: on 2026-11-27 a 4K HDR Blu-ray conversion kept
/// the owner's background services from answering for 20 minutes. Below normal, the job still gets all the spare CPU
/// (it finishes about as fast on an idle PC) but the desktop and other services stay responsive.
/// Burning is never lowered: the drive's buffer must be fed on time.
/// </summary>
public static class BackgroundPriority
{
    /// <summary>
    /// Sets <paramref name="process"/> to below-normal priority. Returns false, and changes nothing, when the process
    /// has already exited or Windows refuses; a conversion must never fail because of this.
    /// </summary>
    public static bool TryLower(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            // Only ever lower: a process someone already set to Idle stays at Idle.
            if (process.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)
            {
                return true;
            }

            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // Exited between the check and the change, or access denied: run at the priority it has.
            return false;
        }
    }
}
