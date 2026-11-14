using System.Buffers.Binary;
using CdBurner.Core.DvdVideo;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// DVD-Video navigation data as players read it. Offsets come from libdvdread's ifo_types.h / nav_types.h (the structures
/// libdvdnav-based players parse): VMGI_MAT 0xC4 TT_SRPT, 0xD0 VTS_ATRT, 0xD8 VMGM_C_ADT, 0xDC VMGM_VOBU_ADMAP;
/// VTSI_MAT 0xC4 title VOBs, 0xC8 PTT_SRPT, 0xCC PGCIT, 0xD4 TMAPT, 0xE0 C_ADT, 0xE4 VOBU_ADMAP; a PGC's table offsets at
/// 0xE4/0xE6/0xE8/0xEA; cell playback first sector at +8, last VOBU start at +16, last sector at +20; PCI data at byte
/// 0x2D of a NAV pack, DSI data at 0x407, VOBU_SRI at DSI + 234 (next VOBU at +80, previous at +84).
/// </summary>
public sealed class DvdNavigationTablesTests
{
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    /// <summary>
    /// A program stream of <paramref name="vobus"/> VOBUs of <paramref name="sectorsEach"/> sectors: a NAV pack, then video
    /// packs, the first with a PTS 0.5 s after the previous VOBU's and an I picture, one with a P picture, one AC-3 pack.
    /// </summary>
    private static byte[] Stream(int vobus, int sectorsEach)
    {
        byte[] vob = new byte[vobus * sectorsEach * 2048];
        for (int v = 0; v < vobus; v++)
        {
            int nav = v * sectorsEach;
            DvdNavPackWriter.FormatNavPack(vob.AsSpan(nav * 2048, 2048));
            for (int k = 1; k < sectorsEach; k++)
            {
                var s = vob.AsSpan((nav + k) * 2048, 2048);
                byte[] pack = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
                pack.CopyTo(s);
                bool audio = k == sectorsEach - 1;
                int p = 14;
                s[p + 2] = 1;
                s[p + 3] = (byte)(audio ? 0xBD : 0xE0);
                BinaryPrimitives.WriteUInt16BigEndian(s[(p + 4)..], (ushort)(2048 - p - 6));
                s[p + 6] = 0x80;
                bool withPts = k == 1 && !audio;
                s[p + 7] = (byte)(withPts ? 0x80 : 0x00);
                s[p + 8] = (byte)(withPts ? 5 : 0);
                if (withPts)
                {
                    long pts = 90_000 + v * 45_000L;
                    s[p + 9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
                    s[p + 10] = (byte)(pts >> 22);
                    s[p + 11] = (byte)(((pts >> 14) & 0xFE) | 1);
                    s[p + 12] = (byte)(pts >> 7);
                    s[p + 13] = (byte)(((pts << 1) & 0xFE) | 1);
                }
                int payload = p + 9 + s[p + 8];
                if (audio)
                {
                    s[payload] = 0x80; // AC-3 substream 0
                }
                else if (k is 1 or 3)
                {
                    // picture start code; coding type in bits 5-3 of the second byte after it (1 = I, 2 = P)
                    s[payload + 2] = 1;
                    s[payload + 5] = (byte)((k == 1 ? 1 : 2) << 3);
                }
            }
        }
        return vob;
    }

    [Fact]
    public void NavPacks_LinkEachVobuToTheNextAndPrevious_AndCarryAddressesAndTimes()
    {
        byte[] vob = Stream(vobus: 4, sectorsEach: 6);

        var vobus = DvdIfoGenerator.PatchVobNavPackets(vob);

        Assert.Equal(new[] { 0, 6, 12, 18 }, vobus.Select(v => v.Sector));
        Assert.Equal(new long[] { 90_000, 135_000, 180_000, 225_000 }, vobus.Select(v => v.StartPts));
        for (int i = 0; i < 4; i++)
        {
            int nav = i * 6 * 2048;
            int pci = nav + 0x2D, dsi = nav + 0x407, sri = dsi + 234;
            Assert.Equal((uint)(i * 6), U32(vob, pci));             // PCI nv_pck_lbn
            Assert.Equal((uint)vobus[i].StartPts, U32(vob, pci + 12)); // vobu_s_ptm
            Assert.Equal((uint)vobus[i].EndPts, U32(vob, pci + 16));   // vobu_e_ptm
            Assert.Equal((uint)(i * 6), U32(vob, dsi + 4));         // DSI nv_pck_lbn
            Assert.Equal(5u, U32(vob, dsi + 8));                    // vobu_ea: the 5 sectors after the NAV pack
            Assert.Equal(3u, U32(vob, dsi + 12));                   // 1st reference (I) ends where the P picture starts
            Assert.Equal(5u, U32(vob, dsi + 16));                   // 2nd reference (P) runs to the VOBU's end
            Assert.Equal(1, U16(vob, dsi + 24));                    // VOB id
            Assert.Equal(1, vob[dsi + 27]);                         // cell id
            Assert.Equal(i < 3 ? 0x80000006u : DvdNavPackWriter.EndOfCell, U32(vob, sri + 80)); // next VOBU
            Assert.Equal(i > 0 ? 0x80000006u : DvdNavPackWriter.EndOfCell, U32(vob, sri + 84)); // previous VOBU
            Assert.Equal(5, U16(vob, dsi + 402));                   // first AC-3 pack, 5 sectors after the NAV pack
        }
        // Cell elapsed time of the third VOBU: 1 s at 30 fps (BCD 00:00:01, frames 00 with the 30 fps flag).
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0xC0 }, vob.AsSpan(2 * 6 * 2048 + 0x407 + 28, 4).ToArray());
        // Fast forward 0.5 s (last FWDA entry) from VOBU 0 reaches VOBU 1; 1 s reaches VOBU 2.
        Assert.Equal(0x80000006u, U32(vob, 0x407 + 234 + 4 + 18 * 4));
        Assert.Equal(0x8000000Cu, U32(vob, 0x407 + 234 + 4 + 17 * 4));
    }

    [Fact]
    public void TitleSetIfo_TablesSitWhereLibdvdreadLooks()
    {
        var vobus = new[] { new DvdVobu(0, 90_000, 135_000), new DvdVobu(6, 135_000, 180_000), new DvdVobu(12, 180_000, 225_000) };

        byte[] ifo = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, titleVobTotalSectors: 18,
            nextTitleNumber: 2, vobus: vobus);

        int sectors = ifo.Length / 2048;
        Assert.Equal((uint)(sectors - 1), U32(ifo, 0x1C));            // last sector of the IFO
        Assert.Equal((uint)(2 * sectors + 18 - 1), U32(ifo, 0x0C));   // last sector of the BUP
        Assert.Equal((uint)sectors, U32(ifo, 0xC4));                   // title VOBs right after the IFO
        Assert.Equal(0x4E, ifo[0x200]);                                // MPEG-2, NTSC, 16:9, letterbox
        Assert.Equal(1, ifo[0x203]);                                   // one audio stream
        Assert.Equal(new byte[] { 0x04, 0x01, (byte)'e', (byte)'n' }, ifo.AsSpan(0x204, 4).ToArray()); // AC-3 stereo, English

        int pgcit = (int)U32(ifo, 0xCC) * 2048;
        Assert.Equal(0x81, ifo[pgcit + 8]);                            // entry PGC of title 1
        int pgc = pgcit + (int)U32(ifo, pgcit + 12);
        Assert.Equal(1, ifo[pgc + 2]);                                 // programs
        Assert.Equal(1, ifo[pgc + 3]);                                 // cells
        Assert.Equal(0x8000, U16(ifo, pgc + 0x0C));                    // audio stream 0 present
        int cmd = pgc + U16(ifo, pgc + 0xE4);
        Assert.Equal(new[] { 0, 1, 0, 15 }, Enumerable.Range(0, 4).Select(i => U16(ifo, cmd + 2 * i)));
        // CallSS VMGM PGC 2 (after the title menu), resume cell 1: JumpTT is not allowed inside a title
        Assert.Equal(new byte[] { 0x30, 0x08, 0, 2, 1, 0xC0, 0, 0 }, ifo.AsSpan(cmd + 8, 8).ToArray());
        Assert.Equal(1, ifo[pgc + U16(ifo, pgc + 0xE6)]);              // program 1 -> cell 1
        int cell = pgc + U16(ifo, pgc + 0xE8);
        Assert.Equal(0u, U32(ifo, cell + 8));
        Assert.Equal(12u, U32(ifo, cell + 16));                        // last VOBU start
        Assert.Equal(17u, U32(ifo, cell + 20));                        // last sector
        int position = pgc + U16(ifo, pgc + 0xEA);
        Assert.Equal(1, U16(ifo, position));
        Assert.Equal(1, ifo[position + 3]);

        int admap = (int)U32(ifo, 0xE4) * 2048;
        Assert.Equal(4u * 3 + 3, U32(ifo, admap));
        Assert.Equal(new uint[] { 0, 6, 12 }, Enumerable.Range(0, 3).Select(i => U32(ifo, admap + 4 + 4 * i)));
        int ptt = (int)U32(ifo, 0xC8) * 2048;
        Assert.Equal(1, U16(ifo, ptt + (int)U32(ifo, ptt + 8)));      // chapter 1: PGC 1
    }

    [Fact]
    public void VideoManager_FirstPlayGoesToTheMenu_WhichStaysTenSecondsThenPlaysTitle1()
    {
        var menu = new[] { new DvdVobu(0, 90_000, 135_000) };

        byte[] vmg = DvdIfoGenerator.BuildVmgIfo(2, DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, menuVobSectors: 20,
            vtsStartSectorOffsets: new[] { 64, 4_000 }, menuVobus: menu);

        Assert.Equal(DvdIfoGenerator.CalculateVmgIfoSectors(2, 20, 1) * 2048, vmg.Length);
        int fp = (int)U32(vmg, 0x84);
        Assert.Equal(0x400, fp);
        int fpCmd = fp + U16(vmg, fp + 0xE4);
        Assert.Equal(1, U16(vmg, fpCmd));                              // one pre command
        Assert.Equal(new byte[] { 0x30, 0x06, 0, 0, 0, 0x42, 0, 0 }, vmg.AsSpan(fpCmd + 8, 8).ToArray()); // JumpSS title menu
        Assert.Equal(0, U16(vmg, fp + 0xE6));                          // no programs or cells in first play

        int tt = (int)U32(vmg, 0xC4) * 2048;
        Assert.Equal(2, U16(vmg, tt));
        Assert.Equal(4_000u, U32(vmg, tt + 8 + 12 + 8));               // title 2's title set start
        Assert.Equal(2, vmg[tt + 8 + 12 + 6]);

        int atrt = (int)U32(vmg, 0xD0) * 2048;
        Assert.Equal(2, U16(vmg, atrt));
        int entry = atrt + (int)U32(vmg, atrt + 8);
        Assert.Equal(0x4E, vmg[entry + 264]);                          // title video attributes
        Assert.Equal(1, vmg[entry + 267]);

        int ut = (int)U32(vmg, 0xC8) * 2048;
        Assert.Equal(0x80, vmg[ut + 11]);                              // a title menu exists
        int pgcit = ut + (int)U32(vmg, ut + 12);
        Assert.Equal(0x82, vmg[pgcit + 8]);
        int pgc = pgcit + (int)U32(vmg, pgcit + 12);
        int cmd = pgc + U16(vmg, pgc + 0xE4);
        Assert.Equal(new byte[] { 0x30, 0x02, 0, 0, 0, 1, 0, 0 }, vmg.AsSpan(cmd + 8, 8).ToArray()); // post: JumpTT 1
        int cell = pgc + U16(vmg, pgc + 0xE8);
        Assert.Equal(DvdIfoGenerator.MenuStillSeconds, vmg[cell + 2]);
        Assert.Equal(19u, U32(vmg, cell + 20));

        // VMGM PGC 2: no cells, one pre command JumpTT 2 (what the end of title 1 calls)
        Assert.Equal(2, U16(vmg, pgcit));
        Assert.Equal(0, vmg[pgcit + 16]);                              // not an entry PGC
        Assert.Equal(2, DvdIfoGenerator.TitleJumpPgcNumber(2, hasTitleMenu: true));
        int jump = pgcit + (int)U32(vmg, pgcit + 16 + 4);
        Assert.Equal(0, vmg[jump + 3]);
        int jumpCmd = jump + U16(vmg, jump + 0xE4);
        Assert.Equal(1, U16(vmg, jumpCmd));
        Assert.Equal(new byte[] { 0x30, 0x02, 0, 0, 0, 2, 0, 0 }, vmg.AsSpan(jumpCmd + 8, 8).ToArray());

        int cAdt = (int)U32(vmg, 0xD8) * 2048;
        Assert.Equal(19u, U32(vmg, cAdt + 4));
        int admap = (int)U32(vmg, 0xDC) * 2048;
        Assert.Equal(7u, U32(vmg, admap));
    }
}
