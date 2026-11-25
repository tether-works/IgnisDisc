using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Structural IFO and NAV-pack PCI/HLI verification for paginated DVD-Video title menus (1 to 99 titles),
/// per-title chapter selection submenus, custom background scaling/letterboxing, background AC-3 audio muxing,
/// and menu font/highlight color presets.
/// </summary>
public sealed class DvdPaginatedMenusAndChaptersTests : IDisposable
{
    private readonly string _tempDir;

    public DvdPaginatedMenusAndChaptersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DvdPagedMenus_" + Guid.NewGuid().ToString("N"));
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
        catch
        {
        }
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private sealed record ParsedHliButton(int X1, int Y1, int X2, int Y2, int Up, int Down, int Left, int Right, byte[] Command);

    private static (List<ParsedHliButton> Group1, List<ParsedHliButton> Group2) ReadNavPackButtons(byte[] vob, int sectorIndex, bool expectWide)
    {
        int offset = sectorIndex * OpticalConstants.DataSectorSize;
        Assert.True(DvdNavPackWriter.IsNavPack(vob.AsSpan(offset, OpticalConstants.DataSectorSize)), $"Sector {sectorIndex} is not a NAV pack.");
        int pci = offset + DvdNavPackWriter.PciOffset;
        Assert.Equal((uint)sectorIndex, U32(vob, pci));

        int hli = pci + 96;
        Assert.Equal(1, U16(vob, hli));
        byte grpMode1 = vob[hli + 14];
        byte grpMode2 = vob[hli + 15];
        int btnCount = vob[hli + 17];
        Assert.True(btnCount > 0);
        if (expectWide)
        {
            Assert.Equal(0x21, grpMode1);
            Assert.Equal(0x20, grpMode2);
        }
        else
        {
            Assert.Equal(0x10, grpMode1);
            Assert.Equal(0x00, grpMode2);
        }

        static List<ParsedHliButton> ParseGroup(byte[] buf, int hliStart, int firstSlot, int count)
        {
            var list = new List<ParsedHliButton>(count);
            for (int i = 0; i < count; i++)
            {
                int b = hliStart + 22 + 24 + 18 * (firstSlot + i);
                int x1 = ((buf[b] & 0x3F) << 4) | (buf[b + 1] >> 4);
                int x2 = ((buf[b + 1] & 0x0F) << 8) | buf[b + 2];
                int y1 = (buf[b + 3] << 4) | (buf[b + 4] >> 4);
                int y2 = ((buf[b + 4] & 0x0F) << 8) | buf[b + 5];
                list.Add(new ParsedHliButton(x1, y1, x2, y2, buf[b + 6], buf[b + 7], buf[b + 8], buf[b + 9], buf.AsSpan(b + 10, 8).ToArray()));
            }
            return list;
        }

        var g1 = ParseGroup(vob, hli, 0, btnCount);
        var g2 = expectWide ? ParseGroup(vob, hli, 18, btnCount) : new List<ParsedHliButton>();
        return (g1, g2);
    }

    private static void AssertValidButtonsInCell(
        byte[] vob,
        int firstSector,
        int frameHeight,
        bool wide)
    {
        var (g1, g2) = ReadNavPackButtons(vob, firstSector, wide);
        for (int i = 0; i < g1.Count; i++)
        {
            var b = g1[i];
            Assert.True(b.X1 >= 0 && b.X2 < 720 && b.X1 < b.X2, $"Invalid X bounds {b.X1}..{b.X2} for button {i + 1}");
            Assert.True(b.Y1 >= 80 && b.Y2 <= frameHeight - 30 && b.Y1 < b.Y2, $"Invalid Y bounds {b.Y1}..{b.Y2} for button {i + 1}");
            Assert.InRange(b.Up, 1, g1.Count);
            Assert.InRange(b.Down, 1, g1.Count);
            Assert.InRange(b.Left, 1, g1.Count);
            Assert.InRange(b.Right, 1, g1.Count);

            var rectI = Rectangle.FromLTRB(b.X1, b.Y1, b.X2, b.Y2);
            for (int j = i + 1; j < g1.Count; j++)
            {
                var rectJ = Rectangle.FromLTRB(g1[j].X1, g1[j].Y1, g1[j].X2, g1[j].Y2);
                Assert.False(rectI.IntersectsWith(rectJ), $"Buttons {i + 1} ({rectI}) and {j + 1} ({rectJ}) overlap.");
            }

            if (wide)
            {
                var lb = g2[i];
                Assert.Equal(b.X1, lb.X1);
                Assert.Equal(b.X2, lb.X2);
                Assert.Equal(frameHeight / 8 + (int)Math.Round(b.Y1 * 0.75, MidpointRounding.AwayFromZero), lb.Y1);
                Assert.Equal(frameHeight / 8 + (int)Math.Round(b.Y2 * 0.75, MidpointRounding.AwayFromZero), lb.Y2);
                Assert.Equal(b.Command, lb.Command);
            }
        }
    }

    [Theory]
    [InlineData(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9)]
    [InlineData(9, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9)]
    [InlineData(25, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9)]
    [InlineData(25, DvdVideoStandard.Pal, DvdAspectRatio.Standard4x3)]
    public async Task PaginatedTitleMenu_VerifiesIfoPgcsLinkPgcnJumpTtAndNavPackHighlights(
        int titleCount,
        DvdVideoStandard standard,
        DvdAspectRatio aspect)
    {
        string workDir = Path.Combine(_tempDir, $"Paged_{titleCount}_{standard}_{aspect}");
        Directory.CreateDirectory(workDir);
        var movies = new List<string>(titleCount);
        for (int i = 1; i <= titleCount; i++)
        {
            string m = Path.Combine(workDir, $"Episode_{i:D2}.mp4");
            File.WriteAllBytes(m, new byte[32 * OpticalConstants.DataSectorSize]);
            movies.Add(m);
        }

        var options = new DvdAuthoringOptions
        {
            Standard = standard,
            AspectRatio = aspect,
            UseFfmpegWhenAvailable = false,
            AutoCreatePlayMenu = true,
            GenerateChapterMenus = false
        };

        var result = await new DvdVideoAuthoringService().AuthorAsync(movies, Path.Combine(workDir, "Disc"), options);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(titleCount, result.TitleCount);
        Assert.True(result.PlannedLayout!.VerifyIfoBupSeparation(out string? sepError), sepError);

        byte[] vmgIfo = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.IFO"));
        byte[] menuVob = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.VOB"));

        int expectedPages = DvdStillMenu.GetTitleMenuPageCount(titleCount, includeChapters: false);
        int ut = (int)U32(vmgIfo, 0xC8) * OpticalConstants.DataSectorSize;
        int pgcit = ut + (int)U32(vmgIfo, ut + 12);
        int expectedTotalPgcs = expectedPages + (titleCount - 1) + (2 * titleCount);
        Assert.Equal(expectedTotalPgcs, U16(vmgIfo, pgcit));

        int cAdt = (int)U32(vmgIfo, 0xD8) * OpticalConstants.DataSectorSize;
        Assert.Equal(expectedPages, U16(vmgIfo, cAdt)); // nr_of_vobs == pageCount

        int frameHeight = standard == DvdVideoStandard.Pal ? 576 : 480;
        bool wide = aspect == DvdAspectRatio.Widescreen16x9;

        var discoveredPlayTitles = new HashSet<int>();
        for (int p = 0; p < expectedPages; p++)
        {
            int cAdtEntry = cAdt + 8 + 12 * p;
            int vobId = U16(vmgIfo, cAdtEntry);
            int cellId = vmgIfo[cAdtEntry + 2];
            int firstSector = (int)U32(vmgIfo, cAdtEntry + 4);
            Assert.Equal(p + 1, vobId);
            Assert.Equal(1, cellId);

            AssertValidButtonsInCell(menuVob, firstSector, frameHeight, wide);
            var (buttons, _) = ReadNavPackButtons(menuVob, firstSector, wide);

            bool hasPrev = p > 0;
            bool hasNext = p < expectedPages - 1;
            if (hasPrev)
            {
                Assert.Contains(buttons, b => b.Command.SequenceEqual(DvdIfoGenerator.LinkPgc(p)));
            }
            if (hasNext)
            {
                Assert.Contains(buttons, b => b.Command.SequenceEqual(DvdIfoGenerator.LinkPgc(p + 2)));
            }

            foreach (var b in buttons)
            {
                // Every Play button uses LinkPGCN into a VMGM PGC containing SetSTN + JumpTT <title>.
                Assert.Equal(0x20, b.Command[0]);
                Assert.Equal(0x04, b.Command[1]);
                int targetPgc = (b.Command[6] << 8) | b.Command[7];
                Assert.InRange(targetPgc, 1, expectedTotalPgcs);
                int targetPgcOff = pgcit + (int)U32(vmgIfo, pgcit + 8 * targetPgc + 4);
                int cmdTbl = targetPgcOff + U16(vmgIfo, targetPgcOff + 0xE4);
                int preCount = U16(vmgIfo, cmdTbl);
                if (preCount == 2)
                {
                    // SetSTN + JumpTT
                    Assert.Equal(0x51, vmgIfo[cmdTbl + 8]);
                    Assert.Equal(0x30, vmgIfo[cmdTbl + 16]);
                    Assert.Equal(0x02, vmgIfo[cmdTbl + 17]);
                    int jumpedTitle = vmgIfo[cmdTbl + 21];
                    Assert.InRange(jumpedTitle, 1, titleCount);
                    discoveredPlayTitles.Add(jumpedTitle);
                }
            }
        }

        Assert.Equal(titleCount, discoveredPlayTitles.Count);

        // Verify VTS_01_0.IFO post-command targets the TitleJumpPgcNumber after all menu pages (not page 2!).
        if (titleCount >= 2)
        {
            byte[] vts1 = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VTS_01_0.IFO"));
            int vtsPgcit = (int)U32(vts1, 0xCC) * OpticalConstants.DataSectorSize;
            int vtsPgc = vtsPgcit + (int)U32(vts1, vtsPgcit + 12);
            int vtsCmd = vtsPgc + U16(vts1, vtsPgc + 0xE4);
            int expectedJumpPgc = DvdIfoGenerator.TitleJumpPgcNumber(2, expectedPages);
            Assert.Equal(DvdIfoGenerator.CallVmgmPgc(expectedJumpPgc), vts1.AsSpan(vtsCmd + 8, 8).ToArray());

            int jumpPgcOff = pgcit + (int)U32(vmgIfo, pgcit + 8 * expectedJumpPgc + 4);
            int jumpCmd = jumpPgcOff + U16(vmgIfo, jumpPgcOff + 0xE4);
            Assert.Equal(new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00 }, vmgIfo.AsSpan(jumpCmd + 8, 8).ToArray());
        }
    }

    [Fact]
    public void TitleMenuPagination_SupportsUpTo99Titles()
    {
        Assert.Equal(13, DvdStillMenu.GetTitleMenuPageCount(99, includeChapters: false));
        Assert.Equal(20, DvdStillMenu.GetTitleMenuPageCount(99, includeChapters: true));

        var titles99 = Enumerable.Range(1, 99)
            .Select(i => new DvdMenuTitle($"Title {i}", null, HasSubtitles: i % 2 == 0))
            .ToList();

        var (picLast, lastPageButtons) = DvdStillMenu.RenderTitlePage(
            "99 Titles",
            titles99,
            pageIndex: 19,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            includeChapters: true);
        using (picLast)
        {
            Assert.Contains(lastPageButtons, b => b.Kind == DvdMenuButtonKind.PlayTitle && b.Title == 99);
            Assert.Contains(lastPageButtons, b => b.Kind == DvdMenuButtonKind.OpenChapters && b.Title == 99);
            Assert.Contains(lastPageButtons, b => b.Kind == DvdMenuButtonKind.PreviousPage && b.Target == 19);
            Assert.DoesNotContain(lastPageButtons, b => b.Kind == DvdMenuButtonKind.NextPage);
            Assert.True(lastPageButtons.Count <= 18);
        }
    }

    [Fact]
    public async Task ChapterSubmenus_GeneratePaginatedVtsMenusWithJumpVtsPttAndBackToMainMenu()
    {
        // 1. Direct multi-page chapter menu VOB test (12 chapters -> 2 pages: 8 + 4)
        var chapters12 = Enumerable.Range(1, 12)
            .Select(c => new DvdMenuChapterInfo(c, $"Chapter {c}", TimeSpan.FromMinutes((c - 1) * 5)))
            .ToList();

        var chapterResult = await DvdStillMenu.BuildMultiPageChapterMenuAsync(
            "Feature Film",
            titleNumber: 2,
            mainMenuPage: 2,
            chapters12,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            new DvdMenuStyle(DvdMenuFontPreset.ClassicSerif, DvdMenuHighlightPreset.Cyan),
            ffmpegExe: null,
            (b, _) => b.Kind switch
            {
                DvdMenuButtonKind.NextPage => DvdIfoGenerator.LinkPgc(b.Target),
                DvdMenuButtonKind.PreviousPage => DvdIfoGenerator.LinkPgc(b.Target),
                DvdMenuButtonKind.BackToMainMenu => DvdIfoGenerator.JumpVmgmPgc(b.Target),
                _ => DvdIfoGenerator.JumpVtsPartOfTitle(1, b.Target)
            },
            CancellationToken.None);

        Assert.Equal(2, chapterResult.Pages.Count);
        AssertValidButtonsInCell(chapterResult.Vob, chapterResult.Pages[0].FirstSector, 480, wide: true);
        AssertValidButtonsInCell(chapterResult.Vob, chapterResult.Pages[1].FirstSector, 480, wide: true);

        var (p1Btns, _) = ReadNavPackButtons(chapterResult.Vob, chapterResult.Pages[0].FirstSector, expectWide: true);
        var (p2Btns, _) = ReadNavPackButtons(chapterResult.Vob, chapterResult.Pages[1].FirstSector, expectWide: true);

        // Page 1: Chapters 1..8 (JumpVTS_PTT), Back to Main Menu (JumpSS_VMGM_PGC 2), Next (LinkPGCN 2)
        Assert.Equal(10, p1Btns.Count);
        Assert.Contains(p1Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsPartOfTitle(1, 1)));
        Assert.Contains(p1Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsPartOfTitle(1, 8)));
        Assert.Contains(p1Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVmgmPgc(2)));
        Assert.Contains(p1Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.LinkPgc(2)));

        // Page 2: Chapters 9..12 (JumpVTS_PTT), Previous (LinkPGCN 1), Back to Main Menu (JumpSS_VMGM_PGC 2)
        Assert.Equal(6, p2Btns.Count);
        Assert.Contains(p2Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsPartOfTitle(1, 9)));
        Assert.Contains(p2Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsPartOfTitle(1, 12)));
        Assert.Contains(p2Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.LinkPgc(1)));
        Assert.Contains(p2Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVmgmPgc(2)));

        // 2. End-to-end 9-title disc authoring with Chapter Submenus enabled
        string discSrc = Path.Combine(_tempDir, "ChapterDiscSrc");
        Directory.CreateDirectory(discSrc);
        var movies = Enumerable.Range(1, 9).Select(i =>
        {
            string p = Path.Combine(discSrc, $"Film_{i:D2}.mp4");
            File.WriteAllBytes(p, new byte[32 * OpticalConstants.DataSectorSize]);
            return p;
        }).ToList();

        var authored = await new DvdVideoAuthoringService().AuthorAsync(movies, Path.Combine(_tempDir, "ChapterDiscOut"), new DvdAuthoringOptions
        {
            UseFfmpegWhenAvailable = false,
            AutoCreatePlayMenu = true,
            GenerateChapterMenus = true,
            MenuHighlightPreset = DvdMenuHighlightPreset.Crimson
        });

        Assert.True(authored.Success, authored.ErrorMessage);
        Assert.True(authored.PlannedLayout!.VerifyIfoBupSeparation(out string? sepErr), sepErr);

        byte[] vmgIfo = File.ReadAllBytes(Path.Combine(authored.VideoTsDirectory, "VIDEO_TS.IFO"));
        byte[] vmgVob = File.ReadAllBytes(Path.Combine(authored.VideoTsDirectory, "VIDEO_TS.VOB"));

        // 9 titles with chapters enabled -> 5 titles/page -> 2 pages. Page 2 has titles 6..9.
        Assert.Equal(2, DvdStillMenu.GetTitleMenuPageCount(9, includeChapters: true));
        int vmgCadt = (int)U32(vmgIfo, 0xD8) * OpticalConstants.DataSectorSize;
        int page2Sector = (int)U32(vmgIfo, vmgCadt + 8 + 12 + 4);
        var (vmgP2Btns, _) = ReadNavPackButtons(vmgVob, page2Sector, expectWide: true);
        Assert.Contains(vmgP2Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsMenu(7, vtsTitleNumber: 1, menuId: 7)));

        // Inspect VTS_07_0.IFO & VTS_07_0.VOB for Title 7's chapter submenu
        string vts7VobPath = Path.Combine(authored.VideoTsDirectory, "VTS_07_0.VOB");
        Assert.True(File.Exists(vts7VobPath));
        byte[] vts7Ifo = File.ReadAllBytes(Path.Combine(authored.VideoTsDirectory, "VTS_07_0.IFO"));
        byte[] vts7Vob = File.ReadAllBytes(vts7VobPath);

        Assert.True(U32(vts7Ifo, 0xC0) > 0); // VTSM_VOBS
        Assert.True(U32(vts7Ifo, 0xD0) > 0); // VTSM_PGCI_UT
        Assert.True(U32(vts7Ifo, 0xD8) > 0); // VTSM_C_ADT
        Assert.True(U32(vts7Ifo, 0xDC) > 0); // VTSM_VOBU_ADMAP
        Assert.Equal(1, vts7Ifo[0x155]);      // VTSM subpicture stream 0

        int vtsmUt = (int)U32(vts7Ifo, 0xD0) * OpticalConstants.DataSectorSize;
        int vtsmPgcit = vtsmUt + (int)U32(vts7Ifo, vtsmUt + 12);
        Assert.Equal(0x87, vts7Ifo[vtsmPgcit + 8]); // Part/Chapter menu entry PGC
        Assert.Equal(0x83, vts7Ifo[vtsmPgcit + 16]); // Root menu alias PGC

        // Check Crimson palette entry 4 in VTSM PGC 1
        int vtsmPgc1 = vtsmPgcit + (int)U32(vts7Ifo, vtsmPgcit + 12);
        uint expectedCrimson = DvdIfoGenerator.RgbToDvdYCrCb(240, 78, 35);
        Assert.Equal(expectedCrimson, U32(vts7Ifo, vtsmPgc1 + 0xA4 + 16));

        var (vts7Btns, _) = ReadNavPackButtons(vts7Vob, 0, expectWide: true);
        Assert.Contains(vts7Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVtsPartOfTitle(1, 1)));
        Assert.Contains(vts7Btns, b => b.Command.SequenceEqual(DvdIfoGenerator.JumpVmgmPgc(2))); // returns to Title Menu Page 2!
    }

    [Theory]
    [InlineData(1600, 600, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, 720, 480)]
    [InlineData(400, 800, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, 720, 480)]
    [InlineData(500, 500, DvdVideoStandard.Pal, DvdAspectRatio.Standard4x3, 720, 576)]
    public void CustomBackgroundImage_ScalesAndLetterboxesCleanlyToNtscAndPal(
        int srcW, int srcH,
        DvdVideoStandard standard, DvdAspectRatio aspect,
        int expectedW, int expectedH)
    {
        string bgPath = Path.Combine(_tempDir, $"bg_{srcW}x{srcH}.png");
        using (var src = new Bitmap(srcW, srcH, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(40, 140, 220));
            src.Save(bgPath, ImageFormat.Png);
        }

        using var scaled = DvdStillMenu.ScaleAndLetterboxBackground(bgPath, standard, aspect);
        Assert.Equal(expectedW, scaled.Width);
        Assert.Equal(expectedH, scaled.Height);

        // Center pixel must contain the scaled image color; edge/corner pixel must be black letterbox/pillarbox bar.
        Color center = scaled.GetPixel(expectedW / 2, expectedH / 2);
        Assert.True(center.B > 180 && center.G > 110, $"Expected blue center pixel, got {center}");

        Color corner = scaled.GetPixel(2, 2);
        Assert.True(corner.R < 10 && corner.G < 10 && corner.B < 10, $"Expected black letterbox/pillarbox border, got {corner}");

        // RenderTitlePage with custom background also produces exact DVD dimensions.
        var (menuPic, btns) = DvdStillMenu.RenderTitlePage(
            "Custom BG",
            [new DvdMenuTitle("Movie", null, false)],
            0,
            standard,
            aspect,
            style: new DvdMenuStyle(DvdMenuFontPreset.CinemaMono, DvdMenuHighlightPreset.Emerald, bgPath));
        using (menuPic)
        {
            Assert.Equal(expectedW, menuPic.Width);
            Assert.Equal(expectedH, menuPic.Height);
            Assert.Single(btns);
        }
    }

    [Fact]
    public async Task BackgroundAudioMuxing_AndColorPresets_ConfigureFfmpegArgsIfoAudioStreamAndPalette()
    {
        string audioFile = Path.Combine(_tempDir, "theme.wav");
        File.WriteAllBytes(audioFile, new byte[128]);

        IReadOnlyList<string> withAudioArgs = DvdStillMenu.BuildFfmpegMenuArguments(
            "menu.png", "highlight.idx", "menu.vob", DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, audioFile, 20);
        Assert.Contains("-stream_loop", withAudioArgs);
        Assert.Contains(audioFile, withAudioArgs);
        Assert.Contains("ac3", withAudioArgs);
        Assert.Contains("48000", withAudioArgs);
        Assert.Contains("20", withAudioArgs);

        IReadOnlyList<string> silentArgs = DvdStillMenu.BuildFfmpegMenuArguments(
            "menu.png", "highlight.idx", "menu.vob", DvdVideoStandard.Pal, DvdAspectRatio.Standard4x3, menuAudioPath: null);
        Assert.Contains("anullsrc=r=48000:cl=stereo", silentArgs);
        Assert.DoesNotContain("-stream_loop", silentArgs);

        // Authoring with custom audio + Gold highlight preset sets VMGM audio attributes and palette entry 4.
        string movie = Path.Combine(_tempDir, "Concert.mp4");
        File.WriteAllBytes(movie, new byte[32 * OpticalConstants.DataSectorSize]);

        var result = await new DvdVideoAuthoringService().AuthorAsync([movie], Path.Combine(_tempDir, "AudioMenuDisc"), new DvdAuthoringOptions
        {
            UseFfmpegWhenAvailable = false,
            AutoCreatePlayMenu = true,
            MenuAudioPath = audioFile,
            MenuHighlightPreset = DvdMenuHighlightPreset.Gold,
            MenuFontPreset = DvdMenuFontPreset.ClassicSerif
        });
        Assert.True(result.Success, result.ErrorMessage);

        byte[] vmgIfo = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.IFO"));
        Assert.Equal(1, vmgIfo[0x103]); // 1 VMGM audio stream
        Assert.Equal(new byte[] { 0x04, 0x01, (byte)'e', (byte)'n' }, vmgIfo.AsSpan(0x104, 4).ToArray());

        int ut = (int)U32(vmgIfo, 0xC8) * OpticalConstants.DataSectorSize;
        int pgcit = ut + (int)U32(vmgIfo, ut + 12);
        int menuPgc = pgcit + (int)U32(vmgIfo, pgcit + 12);
        Assert.Equal(0x8000, U16(vmgIfo, menuPgc + 0x0C)); // PGC declares audio stream 0 present
        Assert.Equal(DvdIfoGenerator.RgbToDvdYCrCb(250, 204, 21), U32(vmgIfo, menuPgc + 0xA4 + 16));
    }
}
