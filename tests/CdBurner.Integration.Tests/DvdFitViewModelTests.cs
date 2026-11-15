using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The DVD-Video screen plans the disc from the movies' running times (the size once converted, not the movie files'
/// size), says how they fit, and does not let a burn start that cannot fit.
/// </summary>
public sealed class DvdFitViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_DvdFit_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly Dictionary<string, double> _runningTimes = new(StringComparer.OrdinalIgnoreCase);

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
        catch (IOException)
        {
        }
    }

    private string Movie(string name, double minutes)
    {
        Directory.CreateDirectory(_tempDir);
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[16]); // tiny file: the plan must come from the running time, not this size
        _runningTimes[path] = minutes * 60;
        return path;
    }

    private DvdVideoViewModel NewViewModel() => new(_deviceManager, new VirtualBurnEngine())
    {
        UseFfmpegWhenAvailable = false,
        DurationProbe = (movie, _) => Task.FromResult(_runningTimes.TryGetValue(movie, out double s) ? s : 0)
    };

    [Fact]
    public async Task TwoFeatureFilms_AreCompressedToFit_AndPlannedAtTheirConvertedSize()
    {
        var vm = NewViewModel();
        var release = new TaskCompletionSource();
        var probe = vm.DurationProbe!;
        vm.DurationProbe = async (movie, ct) => { await release.Task; return await probe(movie, ct); }; // held until checked
        vm.AddMovieFiles([Movie("Tales.mkv", 115), Movie("Poppy Hill.mkv", 91)]);
        Assert.Contains("Reading the running times", vm.FitSummaryText);
        Assert.Null(vm.FitEstimate);

        release.SetResult();
        await vm.FitEstimateTask;

        Assert.Equal(DvdFitKind.Compressed, vm.FitEstimate?.Kind);
        Assert.Contains("compressed to fit a 4.7 GB DVD", vm.FitSummaryText);
        Assert.Equal(CapacityStatus.FitsComfortably, vm.FitStatus);
        Assert.InRange(vm.TotalSectors, 1_900_000, DvdLayerBreakCalculator.Dvd5SectorLimit); // ~4.25 GB, not 32 bytes
        Assert.False(vm.IsDualLayerRequired);
        Assert.Equal("Single-Layer DVD-5 (~4.7 GB)", vm.MediaRequirementText);
        Assert.False(vm.IsTooLongForDvd);
        Assert.True(vm.StartDvdBurnCommand.CanExecute(null));
        Assert.Equal("Ready (Auto-Convert & Menu)", vm.ValidationStatusText);
    }

    [Fact]
    public async Task FiveHours_NeedsADualLayerDisc_AndA47GbDiscIsRefusedBeforeConverting()
    {
        var vm = NewViewModel();
        vm.AddMovieFiles([Movie("Part 1.mp4", 150), Movie("Part 2.mp4", 150)]);
        await vm.FitEstimateTask;

        Assert.Equal(DvdFitKind.NeedsDualLayer, vm.FitEstimate?.Kind);
        Assert.Equal(CapacityStatus.NearCapacityWarning, vm.FitStatus);
        Assert.True(vm.IsDualLayerRequired);
        Assert.Contains("dual-layer DVD", vm.FitSummaryText);
        Assert.True(vm.StartDvdBurnCommand.CanExecute(null)); // a dual-layer disc can take it

        var drive = _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        drive.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());

        var result = await vm.StartDvdBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.Contains("This disc holds", result.ErrorMessage);
        Assert.Contains("dual-layer DVD (DVD+R DL", result.ErrorMessage);
        Assert.Null(vm.LastAuthoredVideoTsPath); // stopped before any converting
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ALongFilm_OffersADualLayerDvd_AndChoosingItPlansTheBiggerDisc()
    {
        var vm = NewViewModel();
        vm.AddMovieFiles([Movie("Epic.mkv", 165)]);
        await vm.FitEstimateTask;

        Assert.True(vm.HasLayerChoice);
        Assert.True(vm.UseSingleLayerDisc);
        Assert.Equal(DvdFitKind.Compressed, vm.FitEstimate?.Kind);
        Assert.StartsWith("4.7 GB DVD: compressed to", vm.SingleLayerChoiceText);
        Assert.StartsWith("Dual-layer DVD (DVD+R DL, 8.5 GB):", vm.DualLayerChoiceText);
        Assert.False(vm.IsDualLayerRequired);
        int singleSectors = vm.TotalSectors;

        vm.UseDualLayerDisc = true;

        Assert.False(vm.UseSingleLayerDisc);
        Assert.True(vm.HasLayerChoice); // still offered, so the user can go back
        Assert.Equal(DvdFitKind.DualLayerChosen, vm.FitEstimate?.Kind);
        Assert.True(vm.TotalSectors > DvdLayerBreakCalculator.Dvd5SectorLimit && vm.TotalSectors > singleSectors);
        Assert.True(vm.IsDualLayerRequired);
        Assert.Equal("Dual-Layer DVD-9 (OTP)", vm.MediaRequirementText);
        Assert.Equal(CapacityStatus.FitsComfortably, vm.FitStatus); // a choice, not a warning

        // A 4.7 GB disc in the drive: refused before converting, pointing back to the other choice.
        var drive = _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        drive.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
        var result = await vm.StartDvdBurnAsync("VDRV-01");
        Assert.False(result.Success);
        Assert.Contains("choose the 4.7 GB DVD", result.ErrorMessage);

        vm.UseSingleLayerDisc = true;
        Assert.Equal(DvdFitKind.Compressed, vm.FitEstimate?.Kind);
        Assert.Equal(singleSectors, vm.TotalSectors);
        Assert.False(vm.IsDualLayerRequired);

        // New DVD goes back to the cheaper disc.
        vm.UseDualLayerDisc = true;
        vm.StartNewDvd();
        Assert.False(vm.UseDualLayerDisc);
        Assert.False(vm.HasLayerChoice);
    }

    [Fact]
    public async Task AShortFilm_OffersNoChoice()
    {
        var vm = NewViewModel();
        vm.AddMovieFiles([Movie("Short.mkv", 80)]);
        await vm.FitEstimateTask;

        Assert.Equal(DvdFitKind.FullQuality, vm.FitEstimate?.Kind);
        Assert.False(vm.HasLayerChoice);
    }

    [Fact]
    public async Task NineHours_IsTooLongForAnyDvd_AndCannotBeBurnedOrBuilt()
    {
        var vm = NewViewModel();
        vm.AddMovieFiles([Movie("A.mkv", 180), Movie("B.mkv", 180), Movie("C.mkv", 180)]);
        await vm.FitEstimateTask;

        Assert.True(vm.IsTooLongForDvd);
        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, vm.FitStatus);
        Assert.Equal("Too long for one DVD", vm.ValidationStatusText);
        Assert.Contains("even a dual-layer one", vm.FitSummaryText);
        Assert.False(vm.StartDvdBurnCommand.CanExecute(null));
        Assert.False(vm.BuildDvdIsoCommand.CanExecute(null));

        var result = await vm.StartDvdBurnAsync("VDRV-01");
        Assert.False(result.Success);
        Assert.Contains("even a dual-layer one", result.ErrorMessage);

        // Taking a movie off brings it back within a dual-layer DVD.
        vm.SourceMediaFiles.RemoveAt(2);
        vm.ValidateAndPlanLayout();
        await vm.FitEstimateTask;
        Assert.False(vm.IsTooLongForDvd);
        Assert.True(vm.StartDvdBurnCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnreadableRunningTime_FallsBackToTheFileSize_AndSaysSo()
    {
        var vm = NewViewModel();
        string movie = Movie("Unknown.mkv", 0);
        vm.AddMovieFiles([movie]);
        await vm.FitEstimateTask;

        Assert.Null(vm.FitEstimate);
        Assert.Contains("could not be read", vm.FitSummaryText);
        Assert.True(vm.StartDvdBurnCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewDvd_DropsTheFitLine()
    {
        var vm = NewViewModel();
        vm.AddMovieFiles([Movie("Film.mkv", 100)]);
        await vm.FitEstimateTask;
        Assert.True(vm.HasFitSummary);

        vm.StartNewDvd();

        Assert.Null(vm.FitEstimate);
        Assert.False(vm.HasFitSummary);
        Assert.Equal("No movies added yet", vm.ValidationStatusText);
    }

    [Fact]
    public void WithoutFfmpegOrAProbe_NothingIsReadAndNothingIsSaid()
    {
        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine()) { UseFfmpegWhenAvailable = false };
        vm.AddMovieFiles([Movie("Film.mkv", 100)]);

        Assert.True(vm.FitEstimateTask.IsCompleted);
        Assert.Null(vm.FitEstimate);
        Assert.False(vm.HasFitSummary);
    }
}
