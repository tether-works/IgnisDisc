using System.IO;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// A DVD-RW is either sequential (sessions, erase to reuse) or in restricted overwrite mode (written over in place).
/// Measured on a real drive (GP60NB50, Ritek DVD-RW): Windows' writer formats a blank DVD-RW into restricted overwrite
/// mode before it writes, even when the disc is to be left open, and an erase returns it to sequential mode.
/// </summary>
public sealed class DvdRwWriteModeTests
{
    private static async Task<byte[]> BuildIsoAsync()
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);
        root.AddFile("PAYLOAD.DAT", payload);
        using var ms = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, ms, new IsoBuildOptions(VolumeLabel: "DVDRW"));
        return ms.ToArray();
    }

    [Fact]
    public async Task BlankDvdRw_LeftOpenByTheWindowsWriter_ComesOutOverwriteOnly_AndTheBurnSaysSo()
    {
        byte[] iso = await BuildIsoAsync();
        using var recorder = new VirtualDiscRecorder("RW-01", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdRw47());
        var service = new DataBurnService(new VirtualBurnEngine());

        var result = await service.BurnStreamAsync(recorder, new MemoryStream(iso), new DataBurnOptions { CloseDisc = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(DataBurnService.NotLeftOpenMessage, result.Warning);
        var status = recorder.QueryMediaStatus();
        Assert.True(status.IsOverwriteOnly);
        Assert.False(status.IsAppendable);

        // A second burn replaces the first, so it needs the user's yes, as on a DVD+RW.
        var again = await service.BurnStreamAsync(recorder, new MemoryStream(iso), new DataBurnOptions { CloseDisc = false });
        Assert.False(again.Success);
        Assert.Equal(DataBurnService.ReplaceExistingDataMessage, again.ErrorMessage);
    }

    [Fact]
    public async Task BlankDvdRw_ClosedByTheWindowsWriter_IsOverwriteOnly_WithNoWarning()
    {
        byte[] iso = await BuildIsoAsync();
        using var recorder = new VirtualDiscRecorder("RW-02", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdRw47());

        var result = await new DataBurnService(new VirtualBurnEngine()).BurnStreamAsync(recorder, new MemoryStream(iso));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.Warning); // closing was asked for; an overwritable disc is no loss
        Assert.True(recorder.QueryMediaStatus().IsOverwriteOnly);
        Assert.False(recorder.QueryMediaStatus().RequiresEraseToWrite);
    }

    [Fact]
    public async Task EraseReturnsTheDvdRwToSequentialMode()
    {
        byte[] iso = await BuildIsoAsync();
        using var recorder = new VirtualDiscRecorder("RW-03", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdRw47());
        var engine = new VirtualBurnEngine();
        await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(iso));

        var erased = await engine.EraseMediaAsync(recorder, fullErase: false, progress: null, CancellationToken.None);

        Assert.True(erased.Success);
        var status = recorder.QueryMediaStatus();
        Assert.True(status.IsBlank);
        Assert.False(status.IsOverwriteOnly);
    }

    [Fact]
    public async Task SequentialDvdRw_LeftOpen_StaysAppendable_WithNoWarning()
    {
        // A writer that keeps the DVD-RW sequential (direct drive commands after a full erase).
        byte[] iso = await BuildIsoAsync();
        using var recorder = new VirtualDiscRecorder("RW-04", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdRw47());
        var engine = new VirtualBurnEngine { FormatsBlankDvdMinusRwForOverwrite = false };

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(iso), new DataBurnOptions { CloseDisc = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.Warning);
        var status = recorder.QueryMediaStatus();
        Assert.False(status.IsOverwriteOnly);
        Assert.True(status.IsAppendable);
    }
}
