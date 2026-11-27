using CdBurner.Core.DvdVideo;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Which ffmpeg.exe <see cref="FfmpegLocator"/> picks when several exist. The files here are placeholders: the locator
/// only checks that they exist and never starts them.
/// </summary>
public sealed class FfmpegLocatorOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ffmpeg-order-" + Guid.NewGuid().ToString("N"));
    private readonly string _appDir;
    private readonly string _managedDir;
    private readonly string _pathDir;

    public FfmpegLocatorOrderTests()
    {
        _appDir = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        _managedDir = Directory.CreateDirectory(Path.Combine(_root, "Programs", "ffmpeg")).FullName;
        _pathDir = Directory.CreateDirectory(Path.Combine(_root, "onpath")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Place(string directory)
    {
        string path = Path.Combine(directory, "ffmpeg.exe");
        File.WriteAllText(path, "placeholder");
        return path;
    }

    [Fact]
    public void OneClickCopy_WinsOverAStrayCopyInTheApplicationFolder()
    {
        Place(_appDir);
        string managed = Place(_managedDir);

        Assert.Equal(managed, FfmpegLocator.FindFfmpegExecutable(null, _appDir, _managedDir, _pathDir));
    }

    [Fact]
    public void ApplicationFolderCopy_IsUsedWhenNothingWasInstalled()
    {
        string app = Place(_appDir);
        Place(_pathDir);

        Assert.Equal(app, FfmpegLocator.FindFfmpegExecutable(null, _appDir, _managedDir, _pathDir));
    }

    [Fact]
    public void OneClickCopy_WinsOverPath()
    {
        Place(_pathDir);
        string managed = Place(_managedDir);

        Assert.Equal(managed, FfmpegLocator.FindFfmpegExecutable(null, _appDir, _managedDir, _pathDir));
    }

    [Fact]
    public void UsersOwnSetting_WinsOverEverything()
    {
        Place(_appDir);
        Place(_managedDir);
        string custom = Place(Directory.CreateDirectory(Path.Combine(_root, "mine")).FullName);

        Assert.Equal(custom, FfmpegLocator.FindFfmpegExecutable(custom, _appDir, _managedDir, _pathDir));
    }

    [Theory]
    [InlineData("ffmpeg.exe")]          // the settings default
    [InlineData(@"bin\ffmpeg.exe")]
    public void ABareNameSetting_IsNotAPath_SoTheOneClickCopyStillWins(string setting)
    {
        // Taken as a path, a bare name resolves against the working directory: started from the application folder,
        // the app then used a 2021 ffmpeg.exe found there instead of the current 1-click install (2026-11-26).
        Place(_appDir);
        string managed = Place(_managedDir);

        Assert.Equal(managed, FfmpegLocator.FindFfmpegExecutable(setting, _appDir, _managedDir, _pathDir));
    }
}
