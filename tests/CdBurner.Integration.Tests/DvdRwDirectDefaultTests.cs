using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Direct drive commands by default for DVD-RW: the one rule (<see cref="DirectWritingDefault"/>), the burn screens
/// ticking the box by themselves until the user sets it, the Settings switch, and a quick-erased DVD-RW the drive cannot
/// leave open going to the Windows writer with the reason.
/// </summary>
public sealed class DvdRwDirectDefaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_DvdRwDefault_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public DvdRwDirectDefaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MediaStatusDto Disc(DiscMediaType type, bool loading = false) =>
        new() { MediaType = type, MediaPresent = true, IsLoading = loading };

    [Fact]
    public void OnlyALoadedDvdRwPrefersDirectWriting()
    {
        Assert.True(DirectWritingDefault.Prefers(Disc(DiscMediaType.DvdMinusRw)));
        Assert.False(DirectWritingDefault.Prefers(Disc(DiscMediaType.DvdMinusRw, loading: true)));
        Assert.False(DirectWritingDefault.Prefers(Disc(DiscMediaType.DvdMinusR)));
        Assert.False(DirectWritingDefault.Prefers(Disc(DiscMediaType.DvdPlusRw)));
        Assert.False(DirectWritingDefault.Prefers(Disc(DiscMediaType.CdRw)));
        Assert.False(DirectWritingDefault.Prefers(new MediaStatusDto { MediaType = DiscMediaType.NoMedia }));
        Assert.False(DirectWritingDefault.Prefers(null));
    }

    [Fact]
    public void AutomaticChoice_TurnsOnAndOff_UntilTheUserChooses()
    {
        var choice = new AutomaticChoice();
        Assert.True(choice.Decide(current: false, wanted: true));
        Assert.False(choice.Decide(current: true, wanted: false));   // it turned it on, so it turns it off
        Assert.Null(choice.Decide(current: false, wanted: false));   // nothing to change

        var userOn = new AutomaticChoice();
        userOn.UserChose();
        Assert.Null(userOn.Decide(current: true, wanted: false));    // the user's tick stays

        var neverAuto = new AutomaticChoice();
        Assert.Null(neverAuto.Decide(current: true, wanted: false)); // on before the app touched it: left alone
    }

    [Fact]
    public void DataScreen_TicksForADvdRw_UnticksForAnotherDisc_AndTheUsersChoiceStands()
    {
        var vm = new DataBurnViewModel(_deviceManager, _burnEngine);
        Assert.False(vm.UseDirectDriveCommands);

        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusRw), enabled: true);
        Assert.True(vm.UseDirectDriveCommands);
        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusR), enabled: true);
        Assert.False(vm.UseDirectDriveCommands);

        // The Settings switch off: a DVD-RW leaves the box alone, and one ticked automatically is unticked.
        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusRw), enabled: false);
        Assert.False(vm.UseDirectDriveCommands);
        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusRw), enabled: true);
        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusRw), enabled: false);
        Assert.False(vm.UseDirectDriveCommands);

        vm.UseDirectDriveCommands = false; // the user's own choice
        vm.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdMinusRw), enabled: true);
        Assert.False(vm.UseDirectDriveCommands);
    }

    [Fact]
    public void ImageArchiveAndDvdVideoScreens_TickForADvdRwToo()
    {
        var image = new ImageStudioViewModel(_deviceManager, _burnEngine);
        var archive = new ArchiveViewModel(_deviceManager, _burnEngine);
        var video = new DvdVideoViewModel(_deviceManager, _burnEngine);

        foreach (var apply in new Action<MediaStatusDto>[]
                 {
                     s => image.ApplyDirectDefaultForDisc(s, true), s => archive.ApplyDirectDefaultForDisc(s, true),
                     s => video.ApplyDirectDefaultForDisc(s, true)
                 })
        {
            apply(Disc(DiscMediaType.DvdMinusRw));
        }
        Assert.True(image.UseDirectDriveCommands);
        Assert.True(archive.UseDirectDriveCommands);
        Assert.True(video.UseDirectDriveCommands);

        image.ApplyDirectDefaultForDisc(Disc(DiscMediaType.CdR), true);
        archive.ApplyDirectDefaultForDisc(Disc(DiscMediaType.CdR), true);
        video.ApplyDirectDefaultForDisc(Disc(DiscMediaType.DvdPlusR), true);
        Assert.False(image.UseDirectDriveCommands);
        Assert.False(archive.UseDirectDriveCommands);
        Assert.False(video.UseDirectDriveCommands); // single-layer movies on a DVD+R: back to the Windows writer
    }

    [Fact]
    public async Task QuickErasedDvdRw_LeftOpen_GoesToTheWindowsWriter_WithTheReason()
    {
        using var recorder = new VirtualDiscRecorder("RW-Q", @"Q:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdRw47());
        // After a quick erase the drive lists incremental writing but does not offer it (GP60NB50, Ritek DVD-RW).
        var direct = new NativeDataBurnEngine(new VirtualBurnEngine(), Path.Combine(_dir, "logs"))
        {
            ConfigureVirtualDrive = d => d.OffersIncremental = false
        };
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("A.DAT", new byte[40_000]);

        var result = await new DataBurnService(direct).BurnCompilationAsync(recorder, root,
            new DataBurnOptions { CloseDisc = false }, new IsoBuildOptions(VolumeLabel: "QUICK", IncludeUdf: false));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(direct.LastFallbackReason);
        Assert.StartsWith("Written with Windows' writer:", direct.LastFallbackReason);
        Assert.Contains("erase --full", direct.LastFallbackReason);
        Assert.Equal(DataBurnService.NotLeftOpenMessage, result.Warning); // and the disc came out overwrite-only
        Assert.True(recorder.CurrentMedium!.InRestrictedOverwriteMode);
    }
}
