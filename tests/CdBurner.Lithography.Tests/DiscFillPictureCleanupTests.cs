using System.Diagnostics;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Lithography.Tests;

/// <summary>
/// Verifies <see cref="DiscFillPicture.GrabMovieFrameAsync"/> cleans up partial/0-byte output images on failure,
/// timeout, and cancellation.
/// </summary>
public sealed class DiscFillPictureCleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_grabcleanup_" + Guid.NewGuid().ToString("N"));

    public DiscFillPictureCleanupTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task GrabMovieFrame_FfmpegFailureWithZeroByteOutput_DeletesPartialPng()
    {
        string movie = Path.Combine(_tempDir, "movie.mp4");
        File.WriteAllBytes(movie, new byte[64]);
        string outputPng = Path.Combine(_tempDir, "zero.png");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_zero.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_GRAB=0\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-frames:v\" set \"IS_GRAB=1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_GRAB%\"==\"1\" (",
            "  type nul > \"%LASTARG%\"",
            "  echo decode failed 1>&2",
            "  exit /b 1",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        var (picture, skipReason) = await DiscFillPicture.GrabMovieFrameAsync(fakeFfmpeg, movie, outputPng);

        Assert.Null(picture);
        Assert.NotNull(skipReason);
        Assert.False(File.Exists(outputPng));
    }

    [Fact]
    public async Task GrabMovieFrame_Timeout_KillsFfmpegAndDeletesPartialPng()
    {
        string movie = Path.Combine(_tempDir, "movie_timeout.mp4");
        File.WriteAllBytes(movie, new byte[64]);
        string outputPng = Path.Combine(_tempDir, "timeout.png");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_timeout.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_GRAB=0\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-frames:v\" set \"IS_GRAB=1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_GRAB%\"==\"1\" (",
            "  (ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "  exit /b 0",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        DiscFillPicture.FrameGrabTimeoutOverride.Value = TimeSpan.FromMilliseconds(150);
        try
        {
            var (picture, skipReason) = await DiscFillPicture.GrabMovieFrameAsync(fakeFfmpeg, movie, outputPng);

            Assert.Null(picture);
            Assert.NotNull(skipReason);
            Assert.False(File.Exists(outputPng));
        }
        finally
        {
            DiscFillPicture.FrameGrabTimeoutOverride.Value = null;
        }
    }

    [Fact]
    public async Task GrabMovieFrame_Cancelled_KillsFfmpegAndDeletesPartialPng()
    {
        string movie = Path.Combine(_tempDir, "movie_cancel.mp4");
        File.WriteAllBytes(movie, new byte[64]);
        string outputPng = Path.Combine(_tempDir, "cancel.png");
        string startedFlag = Path.Combine(_tempDir, "grab_started.flag");
        string fakeFfmpeg = Path.Combine(_tempDir, "fake_ffmpeg_cancel.cmd");

        File.WriteAllText(fakeFfmpeg, string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set \"IS_GRAB=0\"",
            "set \"LASTARG=\"",
            ":loop",
            "if \"%~1\"==\"\" goto done",
            "if \"%~1\"==\"-frames:v\" set \"IS_GRAB=1\"",
            "set \"LASTARG=%~1\"",
            "shift",
            "goto loop",
            ":done",
            "if \"%IS_GRAB%\"==\"1\" (",
            $"  echo started> \"{startedFlag}\"",
            "  (ping -n 30 127.0.0.1 >nul) > \"%LASTARG%\"",
            "  exit /b 0",
            ")",
            "echo   Duration: 00:01:00.00, start: 0.000000, bitrate: 1000 kb/s 1>&2",
            "exit /b 0"));

        using var cts = new CancellationTokenSource();
        Task<(string? PicturePath, string? SkipReason)> task =
            DiscFillPicture.GrabMovieFrameAsync(fakeFfmpeg, movie, outputPng, cts.Token);

        var sw = Stopwatch.StartNew();
        while (!File.Exists(startedFlag))
        {
            if (sw.ElapsedMilliseconds > 10_000)
            {
                throw new TimeoutException("Timed out waiting for fake ffmpeg to start.");
            }
            await Task.Delay(20);
        }

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(outputPng));
    }
}
