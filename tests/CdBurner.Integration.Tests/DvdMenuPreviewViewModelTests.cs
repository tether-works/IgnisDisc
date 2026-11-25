using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// ViewModel tests for live DVD still-menu preview rendering, pagination across >8 titles,
/// chapter submenu preview mode, custom background/audio/font/highlight personalization, and authoring wiring.
/// </summary>
public sealed class DvdMenuPreviewViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public DvdMenuPreviewViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DvdMenuPreviewTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void LivePreview_RendersCorrectAspectDimensions_AndPaginatesBeyondEightTitles()
    {
        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine) { UseFfmpegWhenAvailable = false };

        Assert.NotNull(vm.MenuPreviewImage);
        Assert.Equal(480 * 16 / 9, vm.MenuPreviewImage!.PixelWidth);
        Assert.Equal(480, vm.MenuPreviewImage.PixelHeight);
        Assert.Equal(1, vm.MenuPreviewPageCount);
        Assert.Equal("Page 1 of 1", vm.MenuPreviewPageText);

        vm.SelectedAspectRatio = DvdAspectRatio.Standard4x3;
        Assert.Equal(640, vm.MenuPreviewImage!.PixelWidth);
        Assert.Equal(480, vm.MenuPreviewImage.PixelHeight);

        vm.SelectedStandard = DvdVideoStandard.Pal;
        vm.SelectedAspectRatio = DvdAspectRatio.Widescreen16x9;
        Assert.Equal(1024, vm.MenuPreviewImage!.PixelWidth);
        Assert.Equal(576, vm.MenuPreviewImage.PixelHeight);

        // Add 12 movies -> 2 pages in title menu preview
        var movies = Enumerable.Range(1, 12).Select(i =>
        {
            string p = Path.Combine(_tempDir, $"Movie_{i:D2}.mp4");
            File.WriteAllBytes(p, new byte[32 * OpticalConstants.DataSectorSize]);
            return p;
        }).ToList();
        vm.AddMovieFiles(movies);

        Assert.Equal(2, vm.MenuPreviewPageCount);
        Assert.Equal(0, vm.MenuPreviewPageIndex);
        Assert.False(vm.CanGoToPreviousMenuPreviewPage);
        Assert.True(vm.CanGoToNextMenuPreviewPage);
        Assert.True(vm.NextMenuPreviewPageCommand.CanExecute(null));

        vm.NextMenuPreviewPageCommand.Execute(null);
        Assert.Equal(1, vm.MenuPreviewPageIndex);
        Assert.Equal("Page 2 of 2", vm.MenuPreviewPageText);
        Assert.True(vm.CanGoToPreviousMenuPreviewPage);
        Assert.False(vm.CanGoToNextMenuPreviewPage);

        vm.PreviousMenuPreviewPageCommand.Execute(null);
        Assert.Equal(0, vm.MenuPreviewPageIndex);
        Assert.Equal("Page 1 of 2", vm.MenuPreviewPageText);
    }

    [Fact]
    public void LivePreview_ReactsToPersonalization_ChapterSubmenuToggle_AndMenuDisable()
    {
        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine) { UseFfmpegWhenAvailable = false };
        var initialPreview = vm.MenuPreviewImage;
        Assert.NotNull(initialPreview);

        string bgPath = Path.Combine(_tempDir, "custom_bg.png");
        using (var bmp = new Bitmap(800, 600, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(20, 120, 60));
            bmp.Save(bgPath, ImageFormat.Png);
        }

        vm.MenuBackgroundImagePath = bgPath;
        vm.SelectedMenuFontPreset = DvdMenuFontPreset.ClassicSerif;
        vm.SelectedMenuHighlightPreset = DvdMenuHighlightPreset.Cyan;
        vm.MenuTitle = "Summer Collection";

        Assert.True(vm.HasMenuBackgroundImage);
        Assert.Equal("custom_bg.png", vm.MenuBackgroundStatusText);
        Assert.NotSame(initialPreview, vm.MenuPreviewImage);
        Assert.NotNull(vm.MenuPreviewImage);

        // Chapter submenu preview mode requires GenerateChapterMenus
        vm.GenerateChapterMenus = true;
        vm.PreviewChapterSubmenu = true;
        Assert.True(vm.PreviewChapterSubmenu);
        Assert.StartsWith("Chapters · Page 1 of", vm.MenuPreviewPageText);
        Assert.NotNull(vm.MenuPreviewImage);

        // Turning off title menu clears the preview bitmap and disables chapter preview
        vm.AutoCreatePlayMenu = false;
        Assert.Null(vm.MenuPreviewImage);
        Assert.False(vm.PreviewChapterSubmenu);
    }

    [Fact]
    public async Task EnsureAuthoredAsync_PassesChapterSubmenusAndPersonalizationToDiscOutput()
    {
        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine) { UseFfmpegWhenAvailable = false };
        var movies = Enumerable.Range(1, 9).Select(i =>
        {
            string p = Path.Combine(_tempDir, $"Clip_{i:D2}.mp4");
            File.WriteAllBytes(p, new byte[32 * OpticalConstants.DataSectorSize]);
            return p;
        }).ToList();

        string bgPath = Path.Combine(_tempDir, "bg.png");
        using (var bmp = new Bitmap(640, 480, PixelFormat.Format24bppRgb))
        {
            bmp.Save(bgPath, ImageFormat.Png);
        }
        string audioPath = Path.Combine(_tempDir, "menu.wav");
        File.WriteAllBytes(audioPath, new byte[128]);

        vm.AddMovieFiles(movies);
        vm.GenerateChapterMenus = true;
        vm.SelectedMenuFontPreset = DvdMenuFontPreset.CinemaMono;
        vm.SelectedMenuHighlightPreset = DvdMenuHighlightPreset.Emerald;
        vm.MenuBackgroundImagePath = bgPath;
        vm.MenuAudioPath = audioPath;

        var result = await vm.EnsureAuthoredAsync();
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.VOB")));
        Assert.True(File.Exists(Path.Combine(result.VideoTsDirectory, "VTS_01_0.VOB")));
        Assert.True(File.Exists(Path.Combine(result.VideoTsDirectory, "VTS_09_0.VOB")));

        vm.StartNewDvd();
        Assert.Null(vm.MenuBackgroundImagePath);
        Assert.Null(vm.MenuAudioPath);
        Assert.False(vm.HasMenuBackgroundImage);
        Assert.False(vm.HasMenuAudio);
    }
}
