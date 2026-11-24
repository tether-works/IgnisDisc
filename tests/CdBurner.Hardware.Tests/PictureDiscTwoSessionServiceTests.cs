using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The shared entry point for burning an outer-files picture disc in two sessions: which images it refuses before any
/// drive is touched, how it words a failure, and one complete burn onto the simulated drive.
/// </summary>
public sealed class PictureDiscTwoSessionServiceTests : IDisposable
{
    private const int Sector = 2048;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-twosession-svc-" + Guid.NewGuid().ToString("N"));

    public PictureDiscTwoSessionServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CompilationDirectory SmallTree()
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] data = "outer files".ToArray().Select(c => (byte)c).ToArray();
        root.AddFile(new CompilationFile("README.TXT", data.Length, () => new MemoryStream(data, writable: false)));
        return root;
    }

    private static PictureDiscLayout PlanLayout(PictureDiscFilesPlacement placement, PictureDiscMedium medium, int capacity)
    {
        var options = new IsoBuildOptions(VolumeLabel: "ART_FILES", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false);
        return PictureDiscLayoutPlanner.Plan(
            SmallTree(), options, placement, medium, capacity, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
    }

    [Fact]
    public void WhyLayoutNotSupported_AcceptsAnOuterFilesDvd()
    {
        var layout = PlanLayout(PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, 40_000);
        Assert.Null(PictureDiscTwoSessionService.WhyLayoutNotSupported(layout));
    }

    [Fact]
    public void WhyLayoutNotSupported_RefusesFilesInside()
    {
        var layout = PlanLayout(PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, 40_000)
            with { Placement = PictureDiscFilesPlacement.InnerFiles };
        string? reason = PictureDiscTwoSessionService.WhyLayoutNotSupported(layout);
        Assert.NotNull(reason);
        Assert.Contains("two sessions", reason);
    }

    [Fact]
    public void WhyLayoutNotSupported_RefusesBluRayAndNamesTheWayOut()
    {
        var layout = PlanLayout(PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, 40_000)
            with { Medium = PictureDiscMedium.BluRay };
        string? reason = PictureDiscTwoSessionService.WhyLayoutNotSupported(layout);
        Assert.NotNull(reason);
        Assert.Contains("BD", reason);
        Assert.Contains("--files-outer", reason);
    }

    [Fact]
    public void WhyLayoutNotSupported_RejectsNullLayout()
    {
        Assert.Throws<ArgumentNullException>(() => PictureDiscTwoSessionService.WhyLayoutNotSupported(null!));
    }

    [Fact]
    public void FormatError_IsNullForSuccess()
    {
        Assert.Null(PictureDiscTwoSessionService.FormatError(new PictureDiscTwoSessionResult { Success = true }));
    }

    [Fact]
    public void FormatError_AddsTheNothingWrittenNote()
    {
        var result = new PictureDiscTwoSessionResult { Success = false, NothingWritten = true, ErrorMessage = "The disc is too small." };
        Assert.Equal("The disc is too small. Nothing was written to the disc.", PictureDiscTwoSessionService.FormatError(result));
    }

    [Fact]
    public void FormatError_DoesNotRepeatAnExistingNote()
    {
        var result = new PictureDiscTwoSessionResult { Success = false, NothingWritten = true, ErrorMessage = "Refused. Nothing was written to the disc." };
        Assert.Equal("Refused. Nothing was written to the disc.", PictureDiscTwoSessionService.FormatError(result));
    }

    [Fact]
    public void FormatError_KeepsTheMessageWhenSomethingWasWritten()
    {
        var result = new PictureDiscTwoSessionResult { Success = false, NothingWritten = false, ErrorMessage = "Session 2 failed." };
        Assert.Equal("Session 2 failed.", PictureDiscTwoSessionService.FormatError(result));
    }

    [Fact]
    public void FormatError_KeepsANullMessage()
    {
        Assert.Null(PictureDiscTwoSessionService.FormatError(new PictureDiscTwoSessionResult { Success = false, NothingWritten = true }));
    }

    [Fact]
    public void Constructor_RejectsNullEngine()
    {
        Assert.Throws<ArgumentNullException>(() => new PictureDiscTwoSessionService(null!));
    }

    [Fact]
    public async Task BurnAsync_RejectsBadArguments()
    {
        var service = new PictureDiscTwoSessionService(new VirtualBurnEngine());
        var layout = PlanLayout(PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, 40_000);
        using var recorder = new VirtualDiscRecorder();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.BurnAsync(null!, "x.iso", layout));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BurnAsync(recorder, " ", layout));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.BurnAsync(recorder, "x.iso", null!));
    }

    [Fact]
    public async Task BurnAsync_UnsupportedLayout_IsRefusedWithoutCreatingAWriter()
    {
        var service = new PictureDiscTwoSessionService(new VirtualBurnEngine());
        var layout = PlanLayout(PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, 40_000)
            with { Medium = PictureDiscMedium.BluRay };
        using var recorder = new VirtualDiscRecorder();

        var result = await service.BurnAsync(recorder, Path.Combine(_dir, "missing.iso"), layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Null(service.LastEngine);
        Assert.Equal(PictureDiscTwoSessionService.WhyLayoutNotSupported(layout), result.ErrorMessage);
    }

    [Fact]
    public async Task BurnAsync_OuterFilesDvdPlusR_WritesTwoSessionsOnTheSimulatedDrive()
    {
        const int capacity = 40_000;
        var root = SmallTree();
        var options = new IsoBuildOptions(VolumeLabel: "ART_FILES", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false);
        var layout = PictureDiscLayoutPlanner.Plan(
            root, options, PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, capacity,
            PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);

        string image = Path.Combine(_dir, "outer.iso");
        using (var output = new FileStream(image, FileMode.CreateNew))
        {
            PictureDiscImageComposer.Compose(root, options, layout, (lba, block) =>
            {
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = (byte)((lba + i / Sector) * 131 + (i % Sector) * 7 + 1);
                }
            }, output);
        }

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, capacity);
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var service = new PictureDiscTwoSessionService(new VirtualBurnEngine(recorder), Path.Combine(_dir, "logs"));

        var result = await service.BurnAsync(recorder, image, layout);

        Assert.True(result.Success, PictureDiscTwoSessionService.FormatError(result));
        Assert.NotNull(service.LastEngine);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.Equal(0, medium.Sessions[0].StartLba);
        // DVD+R: the next session starts 2,048 sectors after the picture session (measured on a real drive).
        Assert.Equal(layout.PictureEndLba + 2048, medium.Sessions[1].StartLba);
        Assert.True(medium.IsFinalized);
        Assert.Null(PictureDiscTwoSessionService.FormatError(result));
    }
}
