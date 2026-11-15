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
        Assert.Equal(new[] { 0, 0x08, 0x08 }, Enumerable.Range(0, 3).Select(i => (int)ifo[cells + 24 * i]));    // seamless after the first
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
        Assert.Equal(0x11, vob[hli + 14]);              // one group, wide
        Assert.Equal(3, vob[hli + 17]);                 // buttons
        Assert.Equal(1, vob[hli + 20]);                 // button 1 selected first
        Assert.Equal(0x004000F0u, U32(vob, hli + 22));  // selected: amber, opaque
        int b2 = hli + 22 + 24 + 18;                    // button 2
        Assert.Equal(new byte[] { 1, 1, 3, 3 }, vob.AsSpan(b2 + 6, 4).ToArray()); // up/down within title 1, left/right to title 2
        Assert.Equal(DvdIfoGenerator.LinkPgc(11), vob.AsSpan(b2 + 10, 8).ToArray());
        int b3 = hli + 22 + 24 + 36;
        Assert.Equal(400 >> 4, vob[b3] & 0x3F);
        Assert.Equal(new byte[] { 3, 3, 1, 1 }, vob.AsSpan(b3 + 6, 4).ToArray());
    }
}
