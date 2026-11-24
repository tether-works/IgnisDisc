using System.Buffers.Binary;
using CdBurner.Core.DvdVideo;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Chapters, subtitles and the button menu of authored DVD-Video discs. Layouts as libdvdread and libdvdnav read them:
/// VTS_PTT_SRPT entries (PGC, program), C_ADT cell entries, cell playback seamless flag (byte 0 bit 3), VM commands as
/// decoder.c decodes them (LinkPGCN 20 04 .. nn nn; SetSTN 51 00 00 00 (80 | display 40 | stream) ..), PCI highlight
/// buttons (18 bytes each after HL_GI and BTN_COLIT), and the DVD subpicture unit (2-byte size, control table offset,
/// run-length coded fields, control commands) per the DVD subpicture format, decoded here independently of the encoder.
/// </summary>
public sealed class DvdMenusChaptersSubtitlesTests
{
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    [Fact]
    public void Srt_IsParsed_TagsDropped_AndOrderedByStart()
    {
        const string srt = "2\r\n00:00:05,500 --> 00:00:07,000\r\n<i>Second</i>\r\n\r\n1\r\n00:00:01,000 --> 00:00:03,250\r\nFirst line\r\n{\\an8}two\r\n\r\n3\r\n00:00:09,000 --> 00:00:08,000\r\nbackwards\r\n";

        var cues = DvdSubtitleWriter.ParseSrt(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3250), cues[0].End);
        Assert.Equal(new[] { "First line", "two" }, cues[0].Lines);
        Assert.Equal(new[] { "Second" }, cues[1].Lines);
    }

    /// <summary>Decodes one field of DVD subpicture run lengths (nibble codes 4/8/12/16 bits, n = 0 to the line end).</summary>
    private static byte[] DecodeField(byte[] spu, int offset, int width, int lines)
    {
        byte[] result = new byte[width * lines];
        int nibble = offset * 2;
        int Next() { int b = spu[nibble / 2]; int v = nibble % 2 == 0 ? b >> 4 : b & 0x0F; nibble++; return v; }
        for (int line = 0; line < lines; line++)
        {
            int x = 0;
            while (x < width)
            {
                int code = Next();
                if (code < 4)
                {
                    code = (code << 4) | Next();
                    if (code < 0x10)
                    {
                        code = (code << 4) | Next();
                        if (code < 0x40)
                        {
                            code = (code << 4) | Next();
                        }
                    }
                }
                int n = code >> 2, v = code & 3;
                if (n == 0) n = width - x;
                for (int k = 0; k < n; k++) result[line * width + x + k] = (byte)v;
                x += n;
            }
            if (nibble % 2 != 0) nibble++;
        }
        return result;
    }

    [Fact]
    public void SubpictureUnit_DecodesBackToThePicture_AndCarriesItsDisplayCommands()
    {
        const int w = 300, h = 6;
        byte[] pixels = new byte[w * h];
        var rnd = new Random(7);
        for (int i = 0; i < pixels.Length; i++)
        {
            // Runs of every length class: 1-3, 4-15, 16-63, 64-255 and to the end of the line.
            pixels[i] = (byte)((i % w) switch { < 2 => 1, < 12 => 2, < 50 => 3, < 200 => 0, _ => rnd.Next(10) == 0 ? 1 : 0 });
        }

        byte[] spu = DvdSubtitleWriter.EncodeSpu(pixels, w, h, 10, 20, TimeSpan.FromSeconds(2));

        Assert.Equal(spu.Length, U16(spu, 0));
        int control = U16(spu, 2);
        Assert.Equal(0, U16(spu, control));                    // shown at once
        int second = U16(spu, control + 2);
        Assert.Equal(0x03, spu[control + 4]);                  // SET_COLOR
        Assert.Equal(0x05, spu[control + 10]);                 // SET_DAREA
        // x 10..309, y 20..25 as 12-bit pairs: 000 A01 35 / 014 019
        Assert.Equal(new byte[] { 0x00, 0xA1, 0x35, 0x01, 0x40, 0x19 }, spu.AsSpan(control + 11, 6).ToArray());
        Assert.Equal(0x06, spu[control + 17]);                 // SET_DSPXA
        int top = U16(spu, control + 18), bottom = U16(spu, control + 20);
        Assert.Equal(0x01, spu[control + 22]);                 // STA_DSP
        Assert.Equal((int)Math.Round(2 * 90_000 / 1024.0), U16(spu, second)); // hide after 2 s
        Assert.Equal(second, U16(spu, second + 2));           // last sequence points at itself
        Assert.Equal(0x02, spu[second + 4]);                   // STP_DSP

        byte[] even = DecodeField(spu, top, w, h / 2), odd = DecodeField(spu, bottom, w, h / 2);
        for (int y = 0; y < h; y++)
        {
            var row = (y % 2 == 0 ? even : odd).AsSpan(y / 2 * w, w);
            Assert.True(row.SequenceEqual(pixels.AsSpan(y * w, w)), $"line {y}");
        }
    }

    [Fact]
    public void ChapterCells_GiveOneProgramAndChapterEach_FollowingOnSeamlessly()
    {
        var vobus = new[]
        {
            new DvdVobu(0, 0, 45_000, 1), new DvdVobu(10, 45_000, 90_000, 1),
            new DvdVobu(20, 90_000, 135_000, 2), new DvdVobu(30, 135_000, 180_000, 2),
            new DvdVobu(40, 180_000, 225_000, 3)
        };

        byte[] ifo = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, titleVobTotalSectors: 50, vobus: vobus);

        int ptt = (int)U32(ifo, 0xC8) * 2048;
        Assert.Equal(12u + 3 * 4 - 1, U32(ifo, ptt + 4));
        Assert.Equal(new[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => U16(ifo, ptt + 12 + 4 * i + 2))); // programs 1-3 of PGC 1
        int pgcit = (int)U32(ifo, 0xCC) * 2048;
        int pgc = pgcit + (int)U32(ifo, pgcit + 12);
        Assert.Equal(3, ifo[pgc + 2]);
        Assert.Equal(3, ifo[pgc + 3]);
        int map = pgc + U16(ifo, pgc + 0xE6);
        Assert.Equal(new byte[] { 1, 2, 3 }, ifo.AsSpan(map, 3).ToArray());
        int cells = pgc + U16(ifo, pgc + 0xE8);
        Assert.Equal(new[] { 0x02, 0x08, 0x08 }, Enumerable.Range(0, 3).Select(i => (int)ifo[cells + 24 * i])); // Cell 1 STC_discontinuity, then seamless
        Assert.Equal(new uint[] { 0, 20, 40 }, Enumerable.Range(0, 3).Select(i => U32(ifo, cells + 24 * i + 8)));  // first sectors
        Assert.Equal(new uint[] { 19, 39, 49 }, Enumerable.Range(0, 3).Select(i => U32(ifo, cells + 24 * i + 20))); // last sectors
        int cAdt = (int)U32(ifo, 0xE0) * 2048;
        Assert.Equal(8u + 3 * 12 - 1, U32(ifo, cAdt + 4));
        Assert.Equal(new[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => (int)ifo[cAdt + 8 + 12 * i + 2]));

        byte[] vmg = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, 0, chapterCounts: new[] { 3 });
        Assert.Equal(3, U16(vmg, (int)U32(vmg, 0xC4) * 2048 + 8 + 2)); // TT_SRPT: 3 chapters
    }

    [Fact]
    public void TitleWithSubtitles_DeclaresTheStream_InBothIfos()
    {
        var vobus = new[] { new DvdVobu(0, 0, 90_000) };

        byte[] vts = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, 32, vobus: vobus, subtitleStream: true);
        byte[] vmg = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, 0, subtitleStreams: new[] { true });

        Assert.Equal(1, vts[0x255]);                                         // one subpicture stream
        Assert.Equal(new byte[] { 0x01, 0, (byte)'e', (byte)'n' }, vts.AsSpan(0x256, 4).ToArray());
        int pgcit = (int)U32(vts, 0xCC) * 2048;
        int pgc = pgcit + (int)U32(vts, pgcit + 12);
        Assert.Equal(0x80000000u, U32(vts, pgc + 0x1C));                     // stream 0 present for every display mode
        Assert.Equal(0x00EB8080u, U32(vts, pgc + 0xA4 + 4));                 // palette entry 1: white
        int atrt = (int)U32(vmg, 0xD0) * 2048;
        int entry = atrt + (int)U32(vmg, atrt + 8);
        Assert.Equal(1, vmg[entry + 349]);
    }

    [Fact]
    public void ButtonMenu_LinksEachButtonToAChainThatSetsSubtitlesThenPlays_AndNeverAutoplays()
    {
        var menu = new[] { new DvdVobu(0, 0, 30_000) };

        byte[] vmg = DvdIfoGenerator.BuildVmgIfo(2, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, menuVobSectors: 40,
            menuVobus: menu, menuHasButtons: true);

        Assert.Equal(DvdIfoGenerator.CalculateVmgIfoSectors(2, 40, 1, menuHasButtons: true) * 2048, vmg.Length);
        Assert.Equal(1, vmg[0x155]);                                         // the menu's subpicture stream
        int ut = (int)U32(vmg, 0xC8) * 2048;
        int pgcit = ut + (int)U32(vmg, ut + 12);
        Assert.Equal(1 + 1 + 4, U16(vmg, pgcit));                            // menu, chain to title 2, 4 play chains
        int Pgc(int n) => pgcit + (int)U32(vmg, pgcit + 8 * n + 4);
        int menuPgc = Pgc(1);
        int cell = menuPgc + U16(vmg, menuPgc + 0xE8);
        Assert.Equal(0xFF, vmg[cell + 2]);                                   // still until a button is used
        int menuCmd = menuPgc + U16(vmg, menuPgc + 0xE4);
        Assert.Equal(new byte[] { 0x20, 0x04, 0, 0, 0, 0, 0, 1 }, vmg.AsSpan(menuCmd + 8, 8).ToArray()); // post: back to the menu

        int withSubs2 = DvdIfoGenerator.TitlePlayPgcNumber(2, 2, withSubtitles: true);
        int plain1 = DvdIfoGenerator.TitlePlayPgcNumber(1, 2, withSubtitles: false);
        Assert.Equal(6, withSubs2);
        Assert.Equal(3, plain1);
        int p = Pgc(withSubs2), c = p + U16(vmg, p + 0xE4);
        Assert.Equal(2, U16(vmg, c));                                        // two pre commands
        Assert.Equal(new byte[] { 0x51, 0, 0, 0, 0xC0, 0, 0, 0 }, vmg.AsSpan(c + 8, 8).ToArray());  // subtitles on
        Assert.Equal(new byte[] { 0x30, 0x02, 0, 0, 0, 2, 0, 0 }, vmg.AsSpan(c + 16, 8).ToArray()); // JumpTT 2
        p = Pgc(plain1); c = p + U16(vmg, p + 0xE4);
        Assert.Equal(new byte[] { 0x51, 0, 0, 0, 0x80, 0, 0, 0 }, vmg.AsSpan(c + 8, 8).ToArray());  // subtitles off
        Assert.Equal(new byte[] { 0x20, 0x04, 0, 0, 0, 0, 0, 6 }, DvdIfoGenerator.LinkPgc(6));
    }

    [Theory]
    [InlineData(1, true, DvdVideoStandard.Ntsc)]
    [InlineData(1, false, DvdVideoStandard.Ntsc)]
    [InlineData(2, true, DvdVideoStandard.Ntsc)]
    [InlineData(3, true, DvdVideoStandard.Ntsc)]
    [InlineData(6, true, DvdVideoStandard.Ntsc)]
    [InlineData(1, true, DvdVideoStandard.Pal)]
    public void MenuButtons_StayInsideTheTitleSafeArea_AndDoNotOverlap(int titleCount, bool subtitles, DvdVideoStandard standard)
    {
        // A single film with subtitles once put its second button off the bottom of the picture.
        var titles = Enumerable.Range(1, titleCount).Select(i => new DvdMenuTitle($"Film {i}", null, subtitles)).ToList();

        var (picture, buttons) = DvdStillMenu.Render("Heading", titles, standard, DvdAspectRatio.Widescreen16x9);

        using (picture)
        {
            int height = standard == DvdVideoStandard.Pal ? 576 : 480;
            Assert.Equal(height, picture.Height);
            Assert.Equal(titleCount * (subtitles ? 2 : 1), buttons.Count);
            foreach (var b in buttons)
            {
                Assert.True(b.Bounds.Top >= 90 && b.Bounds.Bottom <= height - 36 && b.Bounds.Left >= 0 && b.Bounds.Right < 720,
                    $"button for title {b.Title} at {b.Bounds}");
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                for (int j = i + 1; j < buttons.Count; j++)
                {
                    Assert.False(buttons[i].Bounds.IntersectsWith(buttons[j].Bounds), $"buttons {i + 1} and {j + 1} overlap");
                }
            }
        }
    }

    [Fact]
    public void SubtitleFileForAMovie_IsFound_HoweverItsPathIsSpelled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DvdSubsLookup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string movie = Path.Combine(dir, "Film.mp4");
            string srt = Path.Combine(dir, "made.srt");
            File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
            var options = new DvdAuthoringOptions();
            options.SubtitleFiles[movie.Replace('\\', '/')] = srt; // given with forward slashes

            Assert.Equal(srt, DvdVideoAuthoringService.FindSubtitleFile(movie, options));
            Assert.Equal(srt, DvdVideoAuthoringService.FindSubtitleFile(movie.ToUpperInvariant(), options));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FfmpegThatFails_StopsAuthoring_InsteadOfPuttingPlaceholderVideoOnTheDisc()
    {
        // findstr.exe stands in for a broken FFmpeg: it rejects FFmpeg's arguments and exits with an error.
        string fake = Path.Combine(Environment.SystemDirectory, "findstr.exe");
        Assert.True(File.Exists(fake));
        string dir = Path.Combine(Path.GetTempPath(), "DvdFfmpegFails_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string movie = Path.Combine(dir, "Film.mp4");
            File.WriteAllBytes(movie, new byte[64 * 2048]);
            var options = new DvdAuthoringOptions { CustomFfmpegPath = fake, AutoCreatePlayMenu = false };

            var result = await new DvdVideoAuthoringService().AuthorAsync(new[] { movie }, Path.Combine(dir, "Disc"), options);

            Assert.False(result.Success);
            Assert.Contains("could not convert", result.ErrorMessage);
            Assert.DoesNotContain(result.LogNotes, n => n.Contains("synthetic", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(dir, "Disc", "VIDEO_TS")) &&
                         Directory.GetFiles(Path.Combine(dir, "Disc", "VIDEO_TS"), "VTS_*.VOB").Length > 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MenuButtons_AreWrittenToEveryNavPack_WithNeighboursAndCommands()
    {
        byte[] vob = new byte[2 * 2048];
        DvdNavPackWriter.FormatNavPack(vob.AsSpan(0, 2048));
        var plans = new[]
        {
            new DvdMenuButtonPlan(1, false, System.Drawing.Rectangle.FromLTRB(100, 300, 300, 340)),
            new DvdMenuButtonPlan(1, true, System.Drawing.Rectangle.FromLTRB(100, 350, 300, 390)),
            new DvdMenuButtonPlan(2, false, System.Drawing.Rectangle.FromLTRB(400, 300, 600, 340))
        };
        var buttons = DvdStillMenu.Navigation(plans, b => DvdIfoGenerator.LinkPgc(b.Title * 10 + (b.WithSubtitles ? 1 : 0)));

        using (var stream = new MemoryStream(vob, writable: true))
        {
            DvdNavPackWriter.Patch(stream, buttons: buttons, wideButtons: true);
        }

        int hli = 0x2D + 96;
        Assert.Equal(1, U16(vob, hli));                 // highlight information present
        Assert.Equal(0x21, vob[hli + 14]);              // two groups, group 1 wide (16:9)
        Assert.Equal(0x20, vob[hli + 15]);              // group 2 4:3 letterbox
        Assert.Equal(3, vob[hli + 17]);                 // buttons
        Assert.Equal(1, vob[hli + 20]);                 // button 1 selected first
        Assert.Equal(0x004000F0u, U32(vob, hli + 22));  // selected: amber, opaque
        int b2 = hli + 22 + 24 + 18;                    // button 2
        Assert.Equal(new byte[] { 1, 1, 3, 3 }, vob.AsSpan(b2 + 6, 4).ToArray()); // up/down within title 1, left/right to title 2
        Assert.Equal(DvdIfoGenerator.LinkPgc(11), vob.AsSpan(b2 + 10, 8).ToArray());
        int b3 = hli + 22 + 24 + 36;
        Assert.Equal(400 >> 4, vob[b3] & 0x3F);
        Assert.Equal(new byte[] { 3, 3, 1, 1 }, vob.AsSpan(b3 + 6, 4).ToArray());

        int lb1 = hli + 22 + 24 + 18 * 18;              // group 2 (letterbox) button 1
        Assert.Equal(100 >> 4, vob[lb1] & 0x3F);        // X unchanged in 4:3 letterbox
        int lbY1 = (vob[lb1 + 3] << 4) | (vob[lb1 + 4] >> 4);
        int lbY2 = ((vob[lb1 + 4] & 0x0F) << 8) | vob[lb1 + 5];
        Assert.Equal(60 + 225, lbY1);                   // 480/8 + 300 * 3/4
        Assert.Equal(60 + 255, lbY2);                   // 480/8 + 340 * 3/4
        Assert.Equal(DvdIfoGenerator.LinkPgc(10), vob.AsSpan(lb1 + 10, 8).ToArray());
    }

    [Fact]
    public async Task TitleMenuOnVsOff_ControlsVideoTsVobAndFirstPlayJump()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DvdMenuToggle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string movie = Path.Combine(dir, "Film.mp4");
            File.WriteAllBytes(movie, new byte[64 * 2048]);
            var svc = new DvdVideoAuthoringService();

            var noMenu = await svc.AuthorAsync(new[] { movie }, Path.Combine(dir, "NoMenu"),
                new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = false });
            Assert.True(noMenu.Success, noMenu.ErrorMessage);
            Assert.False(File.Exists(Path.Combine(noMenu.VideoTsDirectory, "VIDEO_TS.VOB")));
            byte[] vmgNoMenu = File.ReadAllBytes(Path.Combine(noMenu.VideoTsDirectory, "VIDEO_TS.IFO"));
            Assert.Equal(0u, U32(vmgNoMenu, 0xC0)); // VMGM_VOBS == 0
            int fpNoMenu = 0x400 + U16(vmgNoMenu, 0x400 + 0xE4);
            Assert.Equal(2, U16(vmgNoMenu, fpNoMenu)); // SetSTN + JumpTT 1
            Assert.Equal(new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 }, vmgNoMenu.AsSpan(fpNoMenu + 16, 8).ToArray());

            var withMenu = await svc.AuthorAsync(new[] { movie }, Path.Combine(dir, "WithMenu"),
                new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = true });
            Assert.True(withMenu.Success, withMenu.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(withMenu.VideoTsDirectory, "VIDEO_TS.VOB")));
            byte[] vmgMenu = File.ReadAllBytes(Path.Combine(withMenu.VideoTsDirectory, "VIDEO_TS.IFO"));
            Assert.True(U32(vmgMenu, 0xC0) > 0);
            int fpMenu = 0x400 + U16(vmgMenu, 0x400 + 0xE4);
            Assert.Equal(new byte[] { 0x30, 0x06, 0x00, 0x00, 0x00, 0x42, 0x00, 0x00 }, vmgMenu.AsSpan(fpMenu + 16, 8).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MenuAutoPlay_NeverVsSeconds_SetsCellStillTimeAndMenuPostCommand()
    {
        var menu = new[] { new DvdVobu(0, 0, 30_000) };

        byte[] never = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 40, menuVobus: menu, menuHasButtons: true, menuAutoPlaySeconds: 0);
        byte[] timed = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 40, menuVobus: menu, menuHasButtons: true, menuAutoPlaySeconds: 15);

        static (byte Still, byte[] Post) InspectMenuPgc(byte[] vmg)
        {
            int ut = (int)U32(vmg, 0xC8) * 2048;
            int pgcit = ut + (int)U32(vmg, ut + 12);
            int pgc = pgcit + (int)U32(vmg, pgcit + 12);
            int cell = pgc + U16(vmg, pgc + 0xE8);
            int cmd = pgc + U16(vmg, pgc + 0xE4);
            return (vmg[cell + 2], vmg.AsSpan(cmd + 8, 8).ToArray());
        }

        var (neverStill, neverPost) = InspectMenuPgc(never);
        Assert.Equal(0xFF, neverStill);
        Assert.Equal(new byte[] { 0x20, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }, neverPost); // LinkPGCN 1

        var (timedStill, timedPost) = InspectMenuPgc(timed);
        Assert.Equal(15, timedStill);
        Assert.Equal(new byte[] { 0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 }, timedPost); // JumpTT 1
    }

    [Fact]
    public async Task AfterTitleAction_ControlsVtsPostCommands_WithoutJumpTtInVtsDomain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DvdAfterTitle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string m1 = Path.Combine(dir, "One.mp4");
            string m2 = Path.Combine(dir, "Two.mp4");
            File.WriteAllBytes(m1, new byte[64 * 2048]);
            File.WriteAllBytes(m2, new byte[64 * 2048]);
            var svc = new DvdVideoAuthoringService();

            static byte[] VtsPostCommand(string videoTs, int setNum)
            {
                byte[] vts = File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{setNum:D2}_0.IFO"));
                int pgcit = (int)U32(vts, 0xCC) * 2048;
                int pgc = pgcit + (int)U32(vts, pgcit + 12);
                int cmd = pgc + U16(vts, pgc + 0xE4);
                return vts.AsSpan(cmd + 8, 8).ToArray();
            }

            var playNext = await svc.AuthorAsync(new[] { m1, m2 }, Path.Combine(dir, "Next"),
                new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = true, AfterTitleAction = DvdAfterTitleAction.PlayNextTitle });
            Assert.True(playNext.Success, playNext.ErrorMessage);
            byte[] next1 = VtsPostCommand(playNext.VideoTsDirectory, 1);
            byte[] next2 = VtsPostCommand(playNext.VideoTsDirectory, 2);
            Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x02, 0x01, 0xC0, 0x00, 0x00 }, next1); // CallSS VMGM PGC 2
            Assert.NotEqual(0x02, next1[1]); // never JumpTT in VTS domain
            Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00 }, next2); // CallSS VMGM menu 2

            var returnMenu = await svc.AuthorAsync(new[] { m1, m2 }, Path.Combine(dir, "Menu"),
                new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = true, AfterTitleAction = DvdAfterTitleAction.ReturnToMenu });
            Assert.True(returnMenu.Success, returnMenu.ErrorMessage);
            Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00 }, VtsPostCommand(returnMenu.VideoTsDirectory, 1));
            Assert.Equal(new byte[] { 0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00 }, VtsPostCommand(returnMenu.VideoTsDirectory, 2));

            var returnNoMenu = await svc.AuthorAsync(new[] { m1, m2 }, Path.Combine(dir, "Exit"),
                new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = false, AfterTitleAction = DvdAfterTitleAction.ReturnToMenu });
            Assert.True(returnNoMenu.Success, returnNoMenu.ErrorMessage);
            Assert.Equal(new byte[] { 0x30, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, VtsPostCommand(returnNoMenu.VideoTsDirectory, 1));
            Assert.Equal(new byte[] { 0x30, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, VtsPostCommand(returnNoMenu.VideoTsDirectory, 2));
            Assert.Contains(returnNoMenu.LogNotes, n => n.Contains("No title menu is enabled"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ChapterInterval_ZeroProducesOneCell_PositiveSplitsIntoChapterCellsAndPtts()
    {
        static byte[] Build12MinSyntheticVob()
        {
            // 12 VOBUs spaced 60 s apart (0..660 s, ending at 720 s), 2 sectors per VOBU.
            int count = 12;
            byte[] vob = new byte[count * 2 * 2048];
            for (int v = 0; v < count; v++)
            {
                DvdNavPackWriter.FormatNavPack(vob.AsSpan(v * 2 * 2048, 2048));
                var s = vob.AsSpan((v * 2 + 1) * 2048, 2048);
                byte[] pack = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
                pack.CopyTo(s);
                int p = 14;
                s[p + 2] = 1;
                s[p + 3] = 0xE0;
                BinaryPrimitives.WriteUInt16BigEndian(s[(p + 4)..], (ushort)(2048 - p - 6));
                s[p + 6] = 0x80;
                s[p + 7] = 0x80;
                s[p + 8] = 5;
                long pts = v * 60L * 90_000L;
                s[p + 9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
                s[p + 10] = (byte)(pts >> 22);
                s[p + 11] = (byte)(((pts >> 14) & 0xFE) | 1);
                s[p + 12] = (byte)(pts >> 7);
                s[p + 13] = (byte)(((pts << 1) & 0xFE) | 1);
            }
            return vob;
        }

        byte[] noChaptersVob = Build12MinSyntheticVob();
        IReadOnlyList<DvdVobu> oneCellVobus;
        using (var ms = new MemoryStream(noChaptersVob, writable: true))
        {
            oneCellVobus = DvdNavPackWriter.Patch(ms, chapterSeconds: 0);
        }
        byte[] oneCellIfo = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            noChaptersVob.Length / 2048, vobus: oneCellVobus);
        int onePtt = (int)U32(oneCellIfo, 0xC8) * 2048;
        Assert.Equal(12u + 4 - 1, U32(oneCellIfo, onePtt + 4));
        int onePgcit = (int)U32(oneCellIfo, 0xCC) * 2048;
        Assert.Equal(1, oneCellIfo[onePgcit + (int)U32(oneCellIfo, onePgcit + 12) + 2]);

        byte[] chaptersVob = Build12MinSyntheticVob();
        IReadOnlyList<DvdVobu> multiCellVobus;
        using (var ms = new MemoryStream(chaptersVob, writable: true))
        {
            multiCellVobus = DvdNavPackWriter.Patch(ms, chapterSeconds: 180);
        }
        Assert.Equal(4, multiCellVobus.Select(v => v.CellId).Distinct().Count());
        byte[] multiCellIfo = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            chaptersVob.Length / 2048, vobus: multiCellVobus);
        int multiPtt = (int)U32(multiCellIfo, 0xC8) * 2048;
        Assert.Equal(12u + 4 * 4 - 1, U32(multiCellIfo, multiPtt + 4));
        int multiPgcit = (int)U32(multiCellIfo, 0xCC) * 2048;
        Assert.Equal(4, multiCellIfo[multiPgcit + (int)U32(multiCellIfo, multiPgcit + 12) + 2]);
    }

    [Fact]
    public void SubtitlesDefaultAndInitialButton_WriteSetStnAndHliSelectedButton()
    {
        byte[] vmgOn = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 0, subtitlesOnByDefault: true);
        byte[] vmgOff = DvdIfoGenerator.BuildVmgIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 0, subtitlesOnByDefault: false);

        int cmdOn = 0x400 + U16(vmgOn, 0x400 + 0xE4);
        Assert.Equal(2, U16(vmgOn, cmdOn));
        Assert.Equal(new byte[] { 0x51, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00 }, vmgOn.AsSpan(cmdOn + 8, 8).ToArray());

        int cmdOff = 0x400 + U16(vmgOff, 0x400 + 0xE4);
        Assert.Equal(2, U16(vmgOff, cmdOff));
        Assert.Equal(new byte[] { 0x51, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00 }, vmgOff.AsSpan(cmdOff + 8, 8).ToArray());

        byte[] vob = new byte[2 * 2048];
        DvdNavPackWriter.FormatNavPack(vob.AsSpan(0, 2048));
        var plans = new[]
        {
            new DvdMenuButtonPlan(1, false, System.Drawing.Rectangle.FromLTRB(100, 300, 300, 340)),
            new DvdMenuButtonPlan(1, true, System.Drawing.Rectangle.FromLTRB(100, 350, 300, 390))
        };
        var buttons = DvdStillMenu.Navigation(plans, b => DvdIfoGenerator.LinkPgc(b.WithSubtitles ? 3 : 2));
        using (var ms = new MemoryStream(vob, writable: true))
        {
            DvdNavPackWriter.Patch(ms, buttons: buttons, wideButtons: true, initialButton: 2);
        }
        Assert.Equal(2, vob[0x2D + 96 + 20]);
    }

    [Fact]
    public void SubtitleSize_ScalesRenderedDimensionsAndEncodesMatchingDisplayArea()
    {
        var small = DvdSubtitleWriter.Render("Hello world", 720, 480, DvdSubtitleSize.Small);
        var medium = DvdSubtitleWriter.Render("Hello world", 720, 480, DvdSubtitleSize.Medium);
        var large = DvdSubtitleWriter.Render("Hello world", 720, 480, DvdSubtitleSize.Large);

        Assert.True(small.H < medium.H && medium.H < large.H);
        Assert.Equal(0, small.H % 2);
        Assert.Equal(0, medium.H % 2);
        Assert.Equal(0, large.H % 2);
        int InkPixels((byte[] Pixels, int W, int H, int X, int Y) r) => r.Pixels.Count(p => p != 0);
        Assert.True(InkPixels(small) < InkPixels(medium) && InkPixels(medium) < InkPixels(large));

        byte[] spu = DvdSubtitleWriter.EncodeCue(new[] { "Hello world" }, TimeSpan.FromSeconds(2), 720, 480, DvdSubtitleSize.Large);
        int control = U16(spu, 2);
        Assert.Equal(0x05, spu[control + 10]); // SET_DAREA
        int x1 = (spu[control + 11] << 4) | (spu[control + 12] >> 4);
        int x2 = ((spu[control + 12] & 0x0F) << 8) | spu[control + 13];
        int y1 = (spu[control + 14] << 4) | (spu[control + 15] >> 4);
        int y2 = ((spu[control + 15] & 0x0F) << 8) | spu[control + 16];
        Assert.Equal(large.X, x1);
        Assert.Equal(large.X + large.W - 1, x2);
        Assert.Equal(large.Y, y1);
        Assert.Equal(large.Y + large.H - 1, y2);
    }

    [Fact]
    public void VideoQuality_FitToDiscLowersBitrate_FixedBitrateKeepsTargetAndWarns()
    {
        double[] twoTwoHourFilms = [7_200.0, 7_200.0];

        var fitted = DvdVideoAuthoringService.PlanVideoBitrate(twoTwoHourFilms,
            new DvdAuthoringOptions { FitToDisc = true, VideoBitrateKbps = 6000, DualLayer = false });
        Assert.True(fitted.VideoKbps < 3000);

        var fixedHigh = DvdVideoAuthoringService.PlanVideoBitrate(twoTwoHourFilms,
            new DvdAuthoringOptions { FitToDisc = false, VideoBitrateKbps = 8000, DualLayer = false });
        Assert.Equal(8000, fixedHigh.VideoKbps);
        Assert.NotNull(fixedHigh.Note);
        Assert.Contains("exceed DVD-5", fixedHigh.Note);
    }

    [Fact]
    public async Task CustomTitleNames_OverrideCleanedFileNames_InResolverAndAuthoring()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DvdCustomTitles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string m1 = Path.Combine(dir, "raw_file_01.mp4");
            string m2 = Path.Combine(dir, "raw_file_02.mp4");
            File.WriteAllBytes(m1, new byte[64 * 2048]);
            File.WriteAllBytes(m2, new byte[64 * 2048]);

            var options = new DvdAuthoringOptions
            {
                UseFfmpegWhenAvailable = false,
                AutoCreatePlayMenu = true
            };
            options.TitleNames[m1] = "Opening Night";
            options.OrderedTitleNames.Add(string.Empty);
            options.OrderedTitleNames.Add("Second Feature");

            var resolved = DvdVideoAuthoringService.ResolveTitleNames(new[] { m1, m2 }, new[] { "Raw File 01", "Raw File 02" }, options);
            Assert.Equal(new[] { "Opening Night", "Second Feature" }, resolved);

            var result = await new DvdVideoAuthoringService().AuthorAsync(new[] { m1, m2 }, Path.Combine(dir, "Disc"), options);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.VOB")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(DvdVideoStandard.Ntsc, 480, DvdAspectRatio.Widescreen16x9)]
    [InlineData(DvdVideoStandard.Pal, 576, DvdAspectRatio.Widescreen16x9)]
    [InlineData(DvdVideoStandard.Ntsc, 480, DvdAspectRatio.Standard4x3)]
    public async Task MenuNavPacks_EmitTwoScaledGroupsFor16x9Letterbox_AndOneGroupFor4x3(
        DvdVideoStandard standard, int height, DvdAspectRatio aspect)
    {
        byte[] vob = new byte[2 * 2048];
        DvdNavPackWriter.FormatNavPack(vob.AsSpan(0, 2048));
        var seqSector = vob.AsSpan(2048, 2048);
        byte[] packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
        packHeader.CopyTo(seqSector);
        seqSector[16] = 1;
        seqSector[17] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(seqSector[18..], 2048 - 20);
        seqSector[20] = 0x80;
        seqSector[25] = 1;
        seqSector[26] = 0xB3;
        seqSector[27] = 0x2D;
        seqSector[28] = (byte)(height >> 8);
        seqSector[29] = (byte)(height & 0xFF);
        seqSector[30] = (byte)(standard == DvdVideoStandard.Pal ? 3 : 4);

        var plans = new[]
        {
            new DvdMenuButtonPlan(1, false, System.Drawing.Rectangle.FromLTRB(80, 160, 280, 200)),
            new DvdMenuButtonPlan(2, false, System.Drawing.Rectangle.FromLTRB(360, 240, 560, 280))
        };
        var buttons = DvdStillMenu.Navigation(plans, b => DvdIfoGenerator.LinkPgc(b.Title + 1));
        bool wide = aspect == DvdAspectRatio.Widescreen16x9;

        using (var ms = new MemoryStream(vob, writable: true))
        {
            DvdNavPackWriter.Patch(ms, buttons: buttons, wideButtons: wide);
        }

        int hli = 0x2D + 96;
        Assert.Equal(2, vob[hli + 17]);
        Assert.Equal(2, vob[hli + 18]);
        if (wide)
        {
            Assert.Equal(0x21, vob[hli + 14]); // 2 groups, group 1 wide (type 1)
            Assert.Equal(0x20, vob[hli + 15]); // group 2 letterbox (type 2)
            for (int i = 0; i < buttons.Count; i++)
            {
                int wOff = hli + 22 + 24 + 18 * i;
                int lOff = hli + 22 + 24 + 18 * (18 + i);
                int wX1 = ((vob[wOff] & 0x3F) << 4) | (vob[wOff + 1] >> 4);
                int lX1 = ((vob[lOff] & 0x3F) << 4) | (vob[lOff + 1] >> 4);
                int wX2 = ((vob[wOff + 1] & 0x0F) << 8) | vob[wOff + 2];
                int lX2 = ((vob[lOff + 1] & 0x0F) << 8) | vob[lOff + 2];
                Assert.Equal(wX1, lX1);
                Assert.Equal(wX2, lX2);

                int lY1 = (vob[lOff + 3] << 4) | (vob[lOff + 4] >> 4);
                int lY2 = ((vob[lOff + 4] & 0x0F) << 8) | vob[lOff + 5];
                Assert.Equal(height / 8 + (int)Math.Round(buttons[i].Y1 * 0.75, MidpointRounding.AwayFromZero), lY1);
                Assert.Equal(height / 8 + (int)Math.Round(buttons[i].Y2 * 0.75, MidpointRounding.AwayFromZero), lY2);
                Assert.True(lY1 >= height / 8 && lY2 < height - height / 8 && lY1 <= lY2);
                Assert.True(vob.AsSpan(wOff + 6, 12).SequenceEqual(vob.AsSpan(lOff + 6, 12)));
            }
        }
        else
        {
            Assert.Equal(0x10, vob[hli + 14]);
            Assert.Equal(0x00, vob[hli + 15]);
        }

        // Also check the VMGM VIDEO_TS.VOB authored for a 16:9 disc.
        if (wide && standard == DvdVideoStandard.Ntsc)
        {
            string dir = Path.Combine(Path.GetTempPath(), "DvdVmgmMenuHli_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string movie = Path.Combine(dir, "Feature.mp4");
                File.WriteAllBytes(movie, new byte[64 * 2048]);
                var result = await new DvdVideoAuthoringService().AuthorAsync(new[] { movie }, Path.Combine(dir, "Disc"),
                    new DvdAuthoringOptions { UseFfmpegWhenAvailable = false, AutoCreatePlayMenu = true, AspectRatio = aspect, Standard = standard });
                Assert.True(result.Success, result.ErrorMessage);
                byte[] vmgmVob = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VIDEO_TS.VOB"));
                Assert.Equal(0x21, vmgmVob[hli + 14]);
                Assert.Equal(0x20, vmgmVob[hli + 15]);
                Assert.Equal(1, vmgmVob[hli + 17]);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void BuildVtsIfo_SetsCell1StcDiscontinuity_AndNonSeamlessAtLayerBreakCell()
    {
        var vobus = new[]
        {
            new DvdVobu(0, 0, 90_000, 1),
            new DvdVobu(10, 90_000, 180_000, 2),
            new DvdVobu(20, 180_000, 270_000, 3),
            new DvdVobu(30, 270_000, 360_000, 4)
        };

        byte[] ifo = DvdIfoGenerator.BuildVtsIfo(
            1,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            titleVobTotalSectors: 40,
            vobus: vobus,
            layerBreakCellId: 3);

        int pgcit = (int)U32(ifo, 0xCC) * 2048;
        int pgc = pgcit + (int)U32(ifo, pgcit + 12);
        int cells = pgc + U16(ifo, pgc + 0xE8);
        Assert.Equal(new[] { 0x02, 0x08, 0x00, 0x08 }, Enumerable.Range(0, 4).Select(i => (int)ifo[cells + 24 * i]));
    }
}
