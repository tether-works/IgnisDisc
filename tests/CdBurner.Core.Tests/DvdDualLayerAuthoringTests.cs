using System.Buffers.Binary;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Authoring a DVD-Video for a dual-layer disc: the bitrate puts the first titles holding half the running time on the
/// first layer, title VOBs start on 16-sector ECC blocks (a usable break), and the IFO pointers match where the image
/// builder puts the files. IFO header fields follow mpucoder's VMGI_MAT / VTSI_MAT reference: 0x0C last sector of the
/// set (the BUP), 0x1C last sector of the IFO, 0xC4 first title VOB sector (VTS), TT_SRPT entry bytes 8-11 the title
/// set's start sector counted from VIDEO_TS.IFO.
/// </summary>
public sealed class DvdDualLayerAuthoringTests
{
    private const double Kiki = 6_182.7;   // 1:43:02.7, the longer title first
    private const double Totoro = 5_185.2; // 1:26:25.2

    /// <summary>Bytes a title takes at the planned bitrates, with the 7% multiplex overhead the plan allows for.</summary>
    private static double Bytes(DvdBitratePlan plan, DvdAuthoringOptions options, double seconds) =>
        (plan.VideoKbps + options.AudioBitrateKbps) * 1000.0 / 8.0 * 1.07 * seconds;

    [Fact]
    public void SingleLayer_TwoFeatureFilms_AreSqueezedOntoADvd5()
    {
        var options = new DvdAuthoringOptions();

        var plan = DvdVideoAuthoringService.PlanVideoBitrate(new[] { Kiki, Totoro }, options);

        Assert.True(Bytes(plan, options, Kiki + Totoro) <= DvdVideoAuthoringService.Dvd5PayloadBytes);
        Assert.True(Bytes(plan, options, Kiki + Totoro) > 0.99 * DvdVideoAuthoringService.Dvd5PayloadBytes);
        Assert.Contains("Single-Layer DVD-5", plan.Note);
    }

    [Fact]
    public void DualLayer_TwoFeatureFilms_PutTheFirstOnLayer0_SoTheSecondStartsWhereTheBreakCanGo()
    {
        var options = new DvdAuthoringOptions { DualLayer = true };

        var plan = DvdVideoAuthoringService.PlanVideoBitrate(new[] { Kiki, Totoro }, options);

        double first = Bytes(plan, options, Kiki), second = Bytes(plan, options, Totoro);
        Assert.True(first <= DvdVideoAuthoringService.Dvd9Layer0PayloadBytes, $"title 1: {first:N0} bytes");
        Assert.True(first > 0.99 * DvdVideoAuthoringService.Dvd9Layer0PayloadBytes, "the bitrate is not lowered more than needed");
        Assert.True(first >= second, "at least half the disc lies before the break");
        Assert.True(first + second <= DvdVideoAuthoringService.Dvd9PayloadBytes);
        Assert.True(plan.VideoKbps > 4_000 && plan.VideoKbps < options.VideoBitrateKbps);
        Assert.Contains("title 2 starts where the layer break can go", plan.Note);
    }

    [Fact]
    public void DualLayer_TitlesThatFitAtTheChosenBitrate_KeepIt()
    {
        var options = new DvdAuthoringOptions { DualLayer = true };

        var plan = DvdVideoAuthoringService.PlanVideoBitrate(new[] { 3_000.0, 2_400.0 }, options);

        Assert.Equal(options.VideoBitrateKbps, plan.VideoKbps);
        Assert.Equal(options.MaxVideoBitrateKbps, plan.MaxVideoKbps);
        Assert.Null(plan.Note);
    }

    [Theory]
    [InlineData(new[] { 7_200.0 })]          // one title: no title start after the first
    [InlineData(new[] { 1_000.0, 5_000.0 })] // the last title is longer than the rest together
    public void DualLayer_WithoutATitleStartPastHalf_SaysNoBreakCanBePlaced(double[] titles)
    {
        var plan = DvdVideoAuthoringService.PlanVideoBitrate(titles, new DvdAuthoringOptions { DualLayer = true });

        Assert.Contains("No title start can take the layer break", plan.Note);
    }

    [Fact]
    public void PaddedTitleSetIfo_MovesItsVobPointer_AndKeepsItsCellTable()
    {
        byte[] ifo = DvdIfoGenerator.BuildVtsIfo(2, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, titleVobTotalSectors: 5_000, ifoSectors: 13);

        Assert.Equal(13 * 2048, ifo.Length);
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(0xC4)));            // title VOBs right after the IFO
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(0x1C)));            // last sector of the IFO
        Assert.Equal(13u + 5_000 + 13 - 1, BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(0x0C))); // last sector of the BUP
        Assert.Equal(new[] { new DvdCellStart(2, 1, 1, 0) }, DvdCellLayerBreak.ReadTitleCellStarts(2, ifo));
    }

    [Fact]
    public async Task AuthoredTwoTitleDisc_HasEachTitleOnA16SectorBlock_AndPointersThatMatchTheBuiltImage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DvdDualLayerAuthoring_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Odd sizes, so the synthetic VOBs (one sector per 2 KiB of input) do not line up on 16 sectors by chance.
            string first = Path.Combine(tempDir, "First.mkv"), second = Path.Combine(tempDir, "Second.mkv");
            File.WriteAllBytes(first, new byte[37 * 2048]);
            File.WriteAllBytes(second, new byte[53 * 2048]);
            var options = new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = true, DualLayer = true };

            var authored = await new DvdVideoAuthoringService().AuthorAsync(new[] { first, second }, Path.Combine(tempDir, "Disc"), options);
            Assert.True(authored.Success, authored.ErrorMessage);

            // The compilation as burn-dvd builds it: VIDEO_TS planned, AUDIO_TS empty, ISO 9660 + UDF.
            var root = CompilationDirectory.CreateRoot();
            var videoTs = root.AddDirectory("VIDEO_TS");
            foreach (string path in Directory.GetFiles(authored.VideoTsDirectory))
            {
                videoTs.AddFile(Path.GetFileName(path), File.ReadAllBytes(path));
            }
            root.AddDirectory("AUDIO_TS");
            new DvdVideoLayoutPlanner().ApplyToCompilation(videoTs, startLba: 0);
            using var image = new MemoryStream();
            await IsoUdfImageBuilder.BuildAsync(root, image, new IsoBuildOptions(VolumeLabel: "DVD", IncludeUdf: true, IncludeJoliet: false));

            image.Position = 0;
            using var reader = IsoImageReader.FromStream(image, leaveOpen: true);
            var files = reader.FindDirectory("/VIDEO_TS")!.Files.ToDictionary(f => f.Name.ToUpperInvariant());
            int vmg = files["VIDEO_TS.IFO"].StartLba;
            byte[] vmgIfo = reader.ReadFileBytes(files["VIDEO_TS.IFO"]);
            Assert.Equal(0, vmg % 16);

            for (int set = 1; set <= 2; set++)
            {
                var ifoEntry = files[$"VTS_{set:D2}_0.IFO"];
                byte[] ifo = reader.ReadFileBytes(ifoEntry);
                int vob = files[$"VTS_{set:D2}_1.VOB"].StartLba;
                var bup = files[$"VTS_{set:D2}_0.BUP"];

                Assert.Equal(0, vob % 16);
                Assert.Equal(ifo, reader.ReadFileBytes(bup));
                uint ttStart = BinaryPrimitives.ReadUInt32BigEndian(vmgIfo.AsSpan(2048 + 8 + (set - 1) * 12 + 8));
                Assert.Equal(ifoEntry.StartLba - vmg, (int)ttStart);
                Assert.Equal(vob - ifoEntry.StartLba, (int)BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(0xC4)));
                int bupLastSector = bup.StartLba + (int)(bup.SizeBytes / 2048) - 1;
                Assert.Equal(bupLastSector - ifoEntry.StartLba, (int)BinaryPrimitives.ReadUInt32BigEndian(ifo.AsSpan(0x0C)));
            }

            // The start of title 2 is a placed cell start, so a dual-layer burn can put the break there.
            var cells = DvdCellLayerBreak.PlaceCells(
                files.ToDictionary(f => f.Key, f => (f.Value.StartLba, (int)(f.Value.SizeBytes / 2048))),
                name => files.TryGetValue(name, out var f) ? reader.ReadFileBytes(f) : null);
            Assert.Contains(cells, c => c.Cell.TitleSet == 2 && c.Lba == files["VTS_02_1.VOB"].StartLba);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
