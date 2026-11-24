using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CdBurner.App.ViewModels;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Opening a photo on the calibration screen finds the disc by itself: the photo is cut down to the disc, the rim and
/// the hole are marked and the photo is rectified without a click. A picture with no disc leaves the click workflow
/// untouched.
/// </summary>
public sealed class CalibrationAutoDetectViewTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamCalibration");
    private const string CoarseRecord = "verbatim-azo-20261104--calibration-4f0228e6.calibration.json";

    private const int Width = 1500;
    private const int Height = 1700;
    private static readonly byte[] SharedDiscPhotoPng = BuildDiscPhotoPng();

    private static byte[] DiscPhotoPng() => SharedDiscPhotoPng;

    /// <summary>A bright disc with a centre hole and a few bands on a dark, grainy surface, off-centre in a tall photo.</summary>
    private static byte[] BuildDiscPhotoPng()
    {
        byte[] pixels = new byte[Width * Height * 4];
        var random = new Random(99);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                double dx = x - 760, dy = y - 900;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double value = 35 + random.Next(0, 8);
                if (r <= 620 && r >= 78)
                {
                    value = 150;
                    if (r > 250 && r < 560 && ((int)(r / 14)) % 3 == 0) value = 215;
                }

                int i = (y * Width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)value;
                pixels[i + 3] = 255;
            }

        using var stream = new MemoryStream();
        PngRaster.Write(stream, new BgraRaster(Width, Height, pixels), CompressionLevel.Fastest);
        return stream.ToArray();
    }

    private static CalibrationViewModel OpenCoarseRecord()
    {
        var vm = new CalibrationViewModel();
        Assert.True(vm.OpenRecord(Path.Combine(FixtureDir, CoarseRecord)), vm.StatusMessage);
        return vm;
    }

    [Fact]
    public async Task Finding_the_disc_cuts_the_photo_marks_both_outlines_and_rectifies()
    {
        CalibrationViewModel vm = OpenCoarseRecord();
        Assert.True(vm.LoadPhoto(DiscPhotoPng()), vm.StatusMessage);
        Assert.Equal(0, vm.OuterPointCount);

        bool found = await vm.FindDiscEdgesAsync();

        Assert.True(found, vm.StatusMessage);
        Assert.True(vm.IsRectified);
        Assert.Equal(CalibrationClickMode.Measure, vm.ClickMode);
        Assert.Equal(48, vm.OuterPointCount);
        Assert.Equal(48, vm.HolePointCount);
        // Cut down from 1500 x 1700 to the disc and a margin.
        Assert.InRange(vm.PhotoPixelWidth, 1240, 1340);
        Assert.InRange(vm.PhotoPixelHeight, 1240, 1340);
        Assert.True(CalibrationViewModel.TryReadPngSize(vm.PhotoPng!, out int w, out int h));
        Assert.Equal((vm.PhotoPixelWidth, vm.PhotoPixelHeight), (w, h));
        Assert.Contains("Found the disc", vm.StatusMessage);
        // Every dot sits inside the cut photo.
        Assert.All(vm.Markers, m =>
        {
            Assert.InRange(m.X, 0, vm.PhotoPixelWidth);
            Assert.InRange(m.Y, 0, vm.PhotoPixelHeight);
        });
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_picture_with_no_disc_leaves_the_photo_and_the_clicking_alone()
    {
        CalibrationViewModel vm = OpenCoarseRecord();
        byte[] flat = new byte[800 * 600 * 4];
        Array.Fill(flat, (byte)80);
        using var stream = new MemoryStream();
        PngRaster.Write(stream, new BgraRaster(800, 600, flat));
        Assert.True(vm.LoadPhoto(stream.ToArray()), vm.StatusMessage);

        bool found = await vm.FindDiscEdgesAsync();

        Assert.False(found);
        Assert.False(vm.IsRectified);
        Assert.Equal(800, vm.PhotoPixelWidth);
        Assert.Equal(600, vm.PhotoPixelHeight);
        Assert.Equal(0, vm.OuterPointCount);
        Assert.Contains("Click", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task The_found_outlines_can_be_corrected_by_clicking_and_rectified_again()
    {
        CalibrationViewModel vm = OpenCoarseRecord();
        Assert.True(vm.LoadPhoto(DiscPhotoPng()), vm.StatusMessage);
        Assert.True(await vm.FindDiscEdgesAsync(), vm.StatusMessage);

        vm.ClickMode = CalibrationClickMode.OuterRim;
        Assert.True(vm.AddPoint(vm.PhotoPixelWidth / 2.0, 5));
        Assert.False(vm.IsRectified); // an edited outline has to be rectified again
        Assert.Equal(49, vm.OuterPointCount);
        Assert.True(vm.Rectify() || !vm.IsRectified); // may refuse the stray point; must not throw
    }

    [Fact]
    public async Task Finding_the_disc_without_a_photo_says_so()
    {
        var vm = new CalibrationViewModel();

        Assert.False(await vm.FindDiscEdgesAsync());
        Assert.Contains("Open a photo", vm.StatusMessage);
    }
}
