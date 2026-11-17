using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// A picture-disc image with the picture from sector 0 and the files at the outer edge, burned as two sessions with the
/// direct writer on the simulated drive: the picture session left open, then the files laid out again for the drive's
/// next writable address. Session starts are checked against the gaps measured on a real drive (LG GP60NB50), the files
/// are read back from the last session, the picture sectors against the image, and images that no longer fit are
/// refused with the disc untouched.
/// </summary>
public sealed class PictureDiscTwoSessionBurnTests : IDisposable
{
    // Measured on the GP60NB50 (docs/NATIVE-WRITER-DESIGN.md and the drive notes): DVD-R, a 36,032-sector first session
    // left open reported its lead-out at 65,264 and the next session at 93,952; after a 102,464-sector first track the
    // next session was at 131,152. DVD+R: the next session 2,048 sectors after the track (17,472 after 15,424 sectors;
    // 104,480 after 102,432).
    private const int DvdMinusRMinimumFirstSessionEnd = 65_264;
    private const int DvdMinusRGapAfterFirstSession = 28_688;
    private const int DvdPlusRGap = 2_048;
    private const int Sector = 2048;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-twosession-" + Guid.NewGuid().ToString("N"));

    public PictureDiscTwoSessionBurnTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(DiscMediaType.DvdMinusR, 36_032, 93_952)]
    [InlineData(DiscMediaType.DvdMinusR, 102_464, 131_152)]
    [InlineData(DiscMediaType.DvdPlusR, 15_424, 17_472)]
    [InlineData(DiscMediaType.DvdPlusR, 102_432, 104_480)]
    public void NextSessionAddress_MatchesTheRealDrive(DiscMediaType type, int firstSessionEnd, int measuredNext)
    {
        Assert.Equal(measuredNext, PictureDiscTwoSessionBurner.FilesSessionStartAfter(type, firstSessionEnd));
    }

    [Theory]
    [InlineData(DiscMediaType.CdR)]
    [InlineData(DiscMediaType.DvdMinusRw)]
    [InlineData(DiscMediaType.DvdPlusRw)]
    [InlineData(DiscMediaType.BdR)]
    public void OtherDiscs_HaveNoPrediction_AndAReason(DiscMediaType type)
    {
        Assert.Null(PictureDiscTwoSessionBurner.FilesSessionStartAfter(type, 10_000));
        Assert.Contains("DVD-R and DVD+R", PictureDiscTwoSessionBurner.WhyNotSupported(type));
    }

    /// <summary>
    /// DVD+R: the image as <c>disc-art generate</c> lays it out (the planner keeps the DVD session reserve), burned on a
    /// disc of the planned size.
    /// </summary>
    [Fact]
    public async Task DvdPlusR_FilesFollowThePictureAfterTheMeasuredGap_AndReadBackFromSessionTwo()
    {
        const int capacity = 40_000;
        var (image, layout, files) = MakeImage(capacity, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
        Assert.Equal(PictureDiscFilesPlacement.OuterFiles, layout.Placement);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(engine.LastFallbackReason);
        int expectedStart = layout.PictureEndLba + DvdPlusRGap;
        Assert.Equal(expectedStart, result.PredictedFilesSessionStartLba);
        AssertTwoSessions(medium, image, layout, files, expectedStart, result);
    }

    /// <summary>
    /// DVD-R with a picture session shorter than the drive's minimum: the next session starts after the 65,264-sector
    /// minimum plus the gap, not after the picture.
    /// </summary>
    [Fact]
    public async Task DvdMinusR_ShortPictureSession_FilesStartAfterTheMinimumFirstSessionAndTheGap()
    {
        const int capacity = 100_000;
        var (image, layout, files) = MakeImage(capacity, PictureDiscLayoutPlanner.MinimumGapSectors, pictureEndLba: 16_000);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(engine.LastFallbackReason);
        int expectedStart = DvdMinusRMinimumFirstSessionEnd + DvdMinusRGapAfterFirstSession;
        Assert.Equal(93_952, expectedStart);
        AssertTwoSessions(medium, image, layout, files, expectedStart, result);
        Assert.Equal(DataBurnService.AddedSessionHint, result.Warning); // a DVD-R may show session 1 until reloaded
    }

    /// <summary>DVD-R with a picture session past the minimum: the generated layout fits, files 28,688 sectors on.</summary>
    [Fact]
    public async Task DvdMinusR_GeneratedLayout_FitsAfterTheMeasuredGap()
    {
        const int capacity = 96_000;
        var (image, layout, files) = MakeImage(capacity, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
        Assert.True(layout.PictureEndLba > DvdMinusRMinimumFirstSessionEnd);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.True(result.Success, result.ErrorMessage);
        AssertTwoSessions(medium, image, layout, files, layout.PictureEndLba + DvdMinusRGapAfterFirstSession, result);
    }

    [Fact]
    public async Task ImageWithoutRoomForTheSessionGap_IsRefusedBeforeWriting()
    {
        // Planned with only the 2 MiB gap: the files end at the disc's last block, so the DVD+R's 2,048-sector gap
        // pushes them past the end.
        const int capacity = 40_000;
        var (image, layout, _) = MakeImage(capacity, PictureDiscLayoutPlanner.MinimumGapSectors);
        Assert.True(layout.FilesSessionStartLba - layout.PictureEndLba < DvdPlusRGap);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Contains("no longer fit", result.ErrorMessage);
        Assert.Contains("Nothing was written", result.ErrorMessage);
        Assert.Equal(layout.PictureEndLba + DvdPlusRGap, result.PredictedFilesSessionStartLba);
        Assert.Null(engine.LastResult); // the drive was never opened for writing
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);
    }

    [Fact]
    public async Task DvdMinusR_TooSmallForItsMinimumFirstSession_IsRefusedBeforeWriting()
    {
        // Fits a DVD+R of 40,000 sectors, but a DVD-R puts the second session at 93,952 at the earliest.
        const int capacity = 40_000;
        var (image, layout, _) = MakeImage(capacity, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Equal(93_952, result.PredictedFilesSessionStartLba);
        Assert.Null(engine.LastResult);
        Assert.True(medium.IsBlank);
    }

    public static TheoryData<DiscMediaType, int> UnsupportedDiscs() => new()
    {
        { DiscMediaType.DvdPlusRw, 40_000 },
        { DiscMediaType.DvdMinusRw, 40_000 },
        { DiscMediaType.CdR, 40_000 },
    };

    [Theory]
    [MemberData(nameof(UnsupportedDiscs))]
    public async Task OtherDiscs_AreRefusedBeforeWriting(DiscMediaType type, int capacity)
    {
        var (image, layout, _) = MakeImage(40_000, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
        using var medium = new VirtualDiscMedium(type, capacity);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Contains("DVD-R and DVD+R", result.ErrorMessage);
        Assert.Null(engine.LastResult);
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task ADiscAlreadyWritten_IsRefusedBeforeWriting()
    {
        var (image, layout, _) = MakeImage(40_000, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 40_000);
        var (engine, recorder) = Setup(medium);
        var first = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(new byte[32 * Sector]),
            new DataBurnOptions { CloseDisc = false });
        Assert.True(first.Success, first.ErrorMessage);

        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Contains("not blank", result.ErrorMessage);
        Assert.Single(medium.Sessions);
    }

    [Fact]
    public async Task AnImageOfTheWrongSize_IsRefusedBeforeWriting()
    {
        var (image, layout, _) = MakeImage(40_000, PictureDiscTwoSessionBurner.DvdFilesSessionReserveSectors);
        using (var stream = new FileStream(image, FileMode.Open))
        {
            stream.SetLength(stream.Length - Sector);
        }

        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, 40_000);
        var (engine, recorder) = Setup(medium);
        var result = await new PictureDiscTwoSessionBurner(new DataBurnService(engine)).BurnAsync(recorder, image, layout);

        Assert.False(result.Success);
        Assert.True(result.NothingWritten);
        Assert.Null(engine.LastResult);
        Assert.True(medium.IsBlank);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static void AssertTwoSessions(VirtualDiscMedium medium, string image, PictureDiscLayout layout,
        Dictionary<string, byte[]> files, int expectedStart, PictureDiscTwoSessionResult result)
    {
        Assert.Equal(expectedStart, result.FilesSessionStartLba);
        Assert.True(result.PictureSession!.Verified);
        Assert.True(result.FilesSession!.Verified);

        // Two sessions: the picture from sector 0, the files where the drive put the next one; disc closed.
        Assert.Equal(2, medium.Sessions.Count);
        Assert.Equal(0, medium.Sessions[0].StartLba);
        Assert.Equal(layout.PictureEndLba, medium.Sessions[0].SectorCount);
        Assert.Equal(expectedStart, medium.Sessions[1].StartLba);
        Assert.True(medium.IsFinalized);

        // A reader opens the last session and finds every file, intact, with everything at or after its start.
        using var reader = IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: expectedStart);
        Assert.True(reader.HasJoliet);
        Assert.False(reader.HasUdf);
        Assert.Equal("ART_FILES", reader.VolumeLabel);
        Assert.Equal(files.Count, reader.AllFiles.Count);
        foreach (var (path, data) in files)
        {
            var entry = reader.FindFile(path);
            Assert.NotNull(entry);
            Assert.True(entry!.StartLba >= expectedStart, $"{path} at {entry.StartLba}");
            Assert.Equal(data, reader.ReadFileBytes(entry));
        }

        Assert.All(reader.AllDirectories, d => Assert.True(d.StartLba >= expectedStart, $"{d.VirtualPath} at {d.StartLba}"));

        // The picture sectors are the image's, byte for byte; session 2 wrote nothing below its own start.
        byte[] expected = new byte[layout.PictureEndLba * Sector];
        using (var source = File.OpenRead(image))
        {
            source.ReadExactly(expected);
        }

        byte[] onDisc = new byte[expected.Length];
        medium.ReadSectors(0, layout.PictureEndLba, onDisc, Sector);
        Assert.True(expected.AsSpan().SequenceEqual(onDisc), "the picture zone on the disc differs from the image");
    }

    private (NativeDataBurnEngine Engine, VirtualDiscRecorder Recorder) Setup(VirtualDiscMedium medium)
    {
        var engine = new NativeDataBurnEngine(logDirectory: Path.Combine(_dir, "logs")) { Wait = _ => { } };
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return (engine, recorder);
    }

    private static Dictionary<string, byte[]> Files()
    {
        var random = new Random(125);
        byte[] Bytes(int n)
        {
            byte[] b = new byte[n];
            random.NextBytes(b);
            return b;
        }

        return new Dictionary<string, byte[]>
        {
            ["/README.TXT"] = "files outside, picture inside"u8.ToArray(),
            ["/DATA/LARGE.BIN"] = Bytes(300_001),
            ["/DATA/Long file name with spaces.bin"] = Bytes(4_096),
            ["/DATA/SUB/EMPTY.TXT"] = Array.Empty<byte>(),
        };
    }

    /// <summary>
    /// Writes a whole-disc image: picture blocks (a pattern unique to each sector) from sector 0, zeros, then the files
    /// session. With <paramref name="pictureEndLba"/> the picture ends there and the files sit at the end of the disc.
    /// </summary>
    private (string Image, PictureDiscLayout Layout, Dictionary<string, byte[]> Files) MakeImage(int capacity, int gap, int? pictureEndLba = null)
    {
        var files = Files();
        var root = CompilationDirectory.CreateRoot();
        foreach (var (path, data) in files)
        {
            string[] parts = path.TrimStart('/').Split('/');
            var directory = parts.Length > 1 ? root.AddDirectory(string.Join('/', parts[..^1])) : root;
            directory.AddFile(new CompilationFile(parts[^1], data.Length, () => new MemoryStream(data, writable: false)));
        }

        var options = new IsoBuildOptions(VolumeLabel: "ART_FILES", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false);
        var layout = PictureDiscLayoutPlanner.Plan(root, options, PictureDiscFilesPlacement.OuterFiles, PictureDiscMedium.Dvd, capacity, gap);
        if (pictureEndLba is { } end)
        {
            layout = layout with { PictureEndLba = end };
        }

        string image = Path.Combine(_dir, $"outer-{Guid.NewGuid():N}.iso");
        using (var output = new FileStream(image, FileMode.CreateNew))
        {
            PictureDiscImageComposer.Compose(root, options, layout, (lba, block) =>
            {
                for (int i = 0; i < block.Length; i++)
                {
                    long sector = lba + i / Sector;
                    block[i] = (byte)(sector * 131 + (i % Sector) * 7 + 1);
                }
            }, output);
        }

        return (image, layout, files);
    }
}
