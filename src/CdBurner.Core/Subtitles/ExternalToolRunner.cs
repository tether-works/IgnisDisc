using System.Diagnostics;
using System.Text;

namespace CdBurner.Core.Subtitles;

/// <summary>
/// Runs a command-line tool. Exists so tests can replace FFmpeg and whisper.cpp with fakes.
/// </summary>
public interface IExternalToolRunner
{
    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/> (each passed as one argument, no
    /// quoting needed) in <paramref name="workingDirectory"/>, calling <paramref name="onOutputLine"/> for every line
    /// the tool writes to standard output or standard error. Returns the exit code. On cancellation the tool and any
    /// processes it started are ended and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs tools with <see cref="Process"/>: no window, both output streams read at the same time (a tool that fills
/// one pipe while the other is not read would otherwise hang), and the whole process tree ended on cancellation.
/// </summary>
public sealed class ProcessToolRunner : IExternalToolRunner
{
    /// <inheritdoc />
    public async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        object lineLock = new();
        void Forward(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data == null || onOutputLine == null)
            {
                return;
            }

            // Both streams raise events on pool threads; the callback sees one line at a time.
            lock (lineLock)
            {
                onOutputLine(e.Data);
            }
        }

        process.OutputDataReceived += Forward;
        process.ErrorDataReceived += Forward;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {executablePath}.");
        }

        // Conversions run for an hour on every core; below normal keeps the rest of the PC responsive.
        CdBurner.Core.Utilities.BackgroundPriority.TryLower(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CdBurner.Core.Utilities.ProcessAndFileCleanup.KillTreeAndWait(process);
            throw;
        }

        // The parameterless wait flushes the last asynchronous output lines.
        process.WaitForExit();
        return process.ExitCode;
    }
}
