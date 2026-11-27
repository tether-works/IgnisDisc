using System.Diagnostics;
using CdBurner.Core.Subtitles;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Long FFmpeg/whisper jobs run below normal priority so they cannot starve the rest of the PC (2026-11-27: a 4K HDR
/// Blu-ray conversion at normal priority kept the owner's below-normal services from answering for 20 minutes).
/// </summary>
public sealed class BackgroundPriorityTests
{
    private static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0",
            "powershell.exe");

    [Fact]
    public async Task ToolRunner_starts_tools_below_normal_priority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The child waits a moment (the runner lowers the priority right after starting it), then prints its own class.
        var lines = new List<string>();
        int exit = await new ProcessToolRunner().RunAsync(
            WindowsPowerShell,
            ["-NoProfile", "-NonInteractive", "-Command",
             "Start-Sleep -Milliseconds 1500; (Get-Process -Id $PID).PriorityClass"],
            Path.GetTempPath(),
            line => lines.Add(line.Trim()),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("BelowNormal", lines);
    }

    [Fact]
    public void TryLower_lowers_a_running_process_and_never_raises_one()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Process p = StartSleeper();
        try
        {
            Assert.True(BackgroundPriority.TryLower(p));
            p.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass);

            p.PriorityClass = ProcessPriorityClass.Idle;
            Assert.True(BackgroundPriority.TryLower(p));
            p.Refresh();
            Assert.Equal(ProcessPriorityClass.Idle, p.PriorityClass);
        }
        finally
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
    }

    [Fact]
    public void TryLower_on_an_exited_process_returns_false_without_throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Process p = StartSleeper();
        p.Kill(entireProcessTree: true);
        p.WaitForExit(5000);
        Assert.False(BackgroundPriority.TryLower(p));
    }

    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo(WindowsPowerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
        {
            psi.ArgumentList.Add(a);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("PowerShell did not start.");
    }
}
