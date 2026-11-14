using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Builds the DVD-Video information files (VIDEO_TS.IFO and VTS_nn_0.IFO; the BUP files are byte copies), fills the NAV
/// packs of the video files (<see cref="DvdNavPackWriter"/>) and splits title VOBs into 1 GB files. Table layouts follow
/// libdvdread's ifo_types.h, the structures DVD players are modelled on, and pass its ifo_read.c checks: every table
/// starts on a sector of its own and the IFO grows with what it lists. Each title set holds one title of one cell
/// (VOB 1, cell 1); the VMG holds the first-play program chain, the title search table, the title set attributes, a
/// command-only VMGM program chain per title from title 2 on (the end of a title calls it to play the next one), and,
/// with a menu, the title menu (one cell, one button that plays title 1, and a 10-second still after which title 1
/// starts by itself).
/// </summary>
public static class DvdIfoGenerator
{
    private const int SectorSize = OpticalConstants.DataSectorSize; // 2048 bytes

    /// <summary>Size of a program chain's fixed part (PGC_SIZE): the command table may start right after it.</summary>
    private const int PgcSize = 0xEC;

    /// <summary>One title set attribute entry in VTS_ATRT, all 32 subpicture attributes included (VTS_ATTRIBUTES_SIZE).</summary>
    private const int VtsAttributesSize = 542;

    /// <summary>Seconds the title menu stays up before title 1 starts by itself.</summary>
    public const int MenuStillSeconds = 10;

    /// <summary>VM command JumpTT 1: play title 1.</summary>
    public static readonly byte[] JumpTitle1 = [0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];

    private static byte[] JumpTitle(int title) => [0x30, 0x02, 0x00, 0x00, 0x00, (byte)Math.Clamp(title, 1, 99), 0x00, 0x00];

    /// <summary>JumpSS VMGM menu 2 (the title menu), from the first-play program chain.</summary>
    private static readonly byte[] JumpTitleMenu = [0x30, 0x06, 0x00, 0x00, 0x00, 0x42, 0x00, 0x00];

    /// <summary>CallSS VMGM menu 2 (the title menu), resuming at cell 1, at the end of a title.</summary>
    private static readonly byte[] CallTitleMenu = [0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00];

    /// <summary>
    /// CallSS VMGM PGC n, resuming at cell 1: the end of a title goes through VMGM program chain n, a command-only chain
    /// that plays the next title. JumpTT itself is only allowed in first play and the VMGM (libdvdnav's vm.c asserts it),
    /// so a title cannot jump to another title directly.
    /// </summary>
    private static byte[] CallVmgmPgc(int pgcNumber) => [0x30, 0x08, (byte)(pgcNumber >> 8), (byte)pgcNumber, 0x01, 0xC0, 0x00, 0x00];

    /// <summary>
    /// Number of the VMGM program chain that plays <paramref name="title"/> (2 or later): the chains come after the
    /// title menu, one per title from title 2 on.
    /// </summary>
    public static int TitleJumpPgcNumber(int title, bool hasTitleMenu) => (hasTitleMenu ? 1 : 0) + Math.Max(2, title) - 1;

    /// <summary>Exit: stop playback.</summary>
    private static readonly byte[] Exit = [0x30, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>
    /// Fills the NAV packs of an in-memory VOB and returns its VOBUs (see <see cref="DvdNavPackWriter"/>). A menu VOB
    /// gets one button that plays title 1.
    /// </summary>
    public static IReadOnlyList<DvdVobu> PatchVobNavPackets(byte[] vobBytes, int vobId = 1, int cellId = 1, bool isMenu = false, int buttonCount = 1)
    {
        ArgumentNullException.ThrowIfNull(vobBytes);
        if (vobBytes.Length < SectorSize)
        {
            return Array.Empty<DvdVobu>();
        }
        using var stream = new MemoryStream(vobBytes, 0, vobBytes.Length / SectorSize * SectorSize, writable: true);
        return DvdNavPackWriter.Patch(stream, vobId, cellId, isMenu ? JumpTitle1 : null);
    }

    /// <summary>Fills the NAV packs of a VOB file in place and returns its VOBUs.</summary>
    public static IReadOnlyList<DvdVobu> PatchVobNavPacketsInFile(string vobFilePath, int vobId = 1, int cellId = 1, bool isMenu = false, int buttonCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vobFilePath);
        if (!File.Exists(vobFilePath) || new FileInfo(vobFilePath).Length < SectorSize)
        {
            return Array.Empty<DvdVobu>();
        }
        return DvdNavPackWriter.PatchFile(vobFilePath, vobId, cellId, isMenu ? JumpTitle1 : null);
    }

    /// <summary>
    /// Scans a VOB file on disk and returns the sector indices of all NAV packs (always including sector 0).
    /// </summary>
    public static IReadOnlyList<int> ExtractNavPackSectors(string vobFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vobFilePath);
        var result = new List<int> { 0 };
        if (!File.Exists(vobFilePath)) return result;

        using var fs = new FileStream(vobFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        int totalSectors = (int)(fs.Length / SectorSize);
        byte[] sector = new byte[SectorSize];
        for (int s = 1; s < totalSectors; s++)
        {
            fs.Position = (long)s * SectorSize;
            fs.ReadExactly(sector);
            if (DvdNavPackWriter.IsNavPack(sector))
            {
                result.Add(s);
            }
        }
        return result;
    }

    /// <summary>Sectors of the VIDEO_TS.IFO <see cref="BuildVmgIfo"/> builds for these contents.</summary>
    public static int CalculateVmgIfoSectors(int titleSetCount, int menuVobSectors, int menuVobuCount = 1)
    {
        titleSetCount = Math.Clamp(titleSetCount, 1, 99);
        int sectors = 1 + Sectors(8 + 12 * titleSetCount) + Sectors(8 + titleSetCount * (4 + VtsAttributesSize));
        if (PgciUtBytes(titleSetCount, menuVobSectors > 0) is int ut and > 0)
        {
            sectors += Sectors(ut);
        }
        if (menuVobSectors > 0)
        {
            sectors += 1 + Sectors(4 + 4 * Math.Max(1, menuVobuCount));
        }
        return sectors;
    }

    /// <summary>
    /// Builds VIDEO_TS.IFO. <paramref name="vtsStartSectorOffsets"/> gives where each title set's IFO starts, counted in
    /// sectors from the start of VIDEO_TS.IFO (as <see cref="DvdVideoLayoutPlanner"/> places the files). With
    /// <paramref name="menuVobSectors"/> &gt; 0 the VMG has a title menu in VIDEO_TS.VOB (VOBUs from
    /// <paramref name="menuVobus"/>), and first play goes to it when <paramref name="autoPlayMenu"/> is set.
    /// </summary>
    public static byte[] BuildVmgIfo(
        int titleSetCount,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        int menuVobSectors,
        IReadOnlyList<int>? vtsStartSectorOffsets = null,
        bool autoPlayMenu = true,
        IReadOnlyList<DvdVobu>? menuVobus = null)
    {
        titleSetCount = Math.Clamp(titleSetCount, 1, 99);
        bool hasMenu = menuVobSectors > 0;
        var menuUnits = hasMenu ? (menuVobus is { Count: > 0 } ? menuVobus : new[] { new DvdVobu(0, -1, -1) }) : Array.Empty<DvdVobu>();

        int ttSrptSector = 1;
        int atrtSector = ttSrptSector + Sectors(8 + 12 * titleSetCount);
        int atrtBytes = 8 + titleSetCount * (4 + VtsAttributesSize);
        int next = atrtSector + Sectors(atrtBytes);
        int pgciUtSector = 0, cAdtSector = 0, admapSector = 0;
        if (PgciUtBytes(titleSetCount, hasMenu) > 0)
        {
            pgciUtSector = next;
            next += Sectors(PgciUtBytes(titleSetCount, hasMenu));
        }
        if (hasMenu)
        {
            cAdtSector = next++;
            admapSector = next;
            next += Sectors(4 + 4 * menuUnits.Count);
        }
        int ifoSectors = next;
        byte[] ifo = new byte[ifoSectors * SectorSize];
        var mat = ifo.AsSpan();

        // --- VMGI_MAT ---
        Encoding.ASCII.GetBytes(DvdVideoValidator.VmgMagicSignature).CopyTo(ifo, 0);
        // Last sector of the VMG set (the BUP), as DvdVideoLayoutPlanner places it: IFO, menu VOB, 16 sectors of padding
        // when the menu is shorter than one ECC block, BUP.
        int vmgPadding = menuVobSectors < OpticalConstants.DvdEccBlockSectors ? OpticalConstants.DvdEccBlockSectors : 0;
        W32(mat, 0x0C, 2 * ifoSectors + menuVobSectors + vmgPadding - 1);
        W32(mat, 0x1C, ifoSectors - 1);            // last sector of the IFO
        mat[0x21] = 0x11;                          // specification version 1.1
        // 0x22 VMG category: region mask 0, playable in every region
        W16(mat, 0x26, 1);                         // volumes
        W16(mat, 0x28, 1);                         // this volume
        mat[0x2A] = 1;                             // side
        W16(mat, 0x3E, titleSetCount);
        Encoding.ASCII.GetBytes("IGNISDISC").CopyTo(ifo, 0x40); // provider id, zero padded

        byte[] firstPlay = BuildPgc(programs: 0, cells: 0, durationPts: 0, thirtyFps: true, hasAudio: false,
            pre: hasMenu && autoPlayMenu ? JumpTitleMenu : JumpTitle1, post: null, cell: null);
        firstPlay.CopyTo(ifo, 0x400);
        W32(mat, 0x80, 0x400 + firstPlay.Length - 1); // last byte of VMGI_MAT with the first-play PGC
        W32(mat, 0x84, 0x400);                        // first-play PGC
        W32(mat, 0xC0, hasMenu ? ifoSectors : 0);     // VMGM_VOBS
        W32(mat, 0xC4, ttSrptSector);
        W32(mat, 0xC8, pgciUtSector);
        // 0xCC PTL_MAIT, 0xD4 TXTDT_MGI: none
        W32(mat, 0xD0, atrtSector);
        W32(mat, 0xD8, cAdtSector);
        W32(mat, 0xDC, admapSector);
        if (hasMenu)
        {
            WriteVideoAttributes(mat[0x100..], standard, aspect); // menu video; the menu declares no audio
        }

        // --- TT_SRPT: one title per title set ---
        var tt = mat[(ttSrptSector * SectorSize)..];
        W16(tt, 0, titleSetCount);
        W32(tt, 4, 8 + 12 * titleSetCount - 1);
        for (int i = 1; i <= titleSetCount; i++)
        {
            var t = tt[(8 + (i - 1) * 12)..];
            // Playback type: one sequential PGC; its post command jumps (JumpTT / CallSS) unless it is the last title
            // of a disc without a menu.
            t[0] = (byte)(i < titleSetCount || hasMenu ? 0x10 : 0x00);
            t[1] = 1;      // angles
            W16(t, 2, 1);  // chapters
            t[6] = (byte)i; // title set
            t[7] = 1;      // title within the title set
            int start = vtsStartSectorOffsets != null && i <= vtsStartSectorOffsets.Count
                ? vtsStartSectorOffsets[i - 1]
                : ifoSectors * 2 + menuVobSectors + vmgPadding + (i - 1) * 32;
            W32(t, 8, start);
        }

        // --- VTS_ATRT ---
        var atrt = mat[(atrtSector * SectorSize)..];
        W16(atrt, 0, titleSetCount);
        W32(atrt, 4, atrtBytes - 1);
        for (int i = 0; i < titleSetCount; i++)
        {
            int entry = 8 + titleSetCount * 4 + i * VtsAttributesSize;
            W32(atrt, 8 + i * 4, entry);
            var e = atrt[entry..];
            W32(e, 0, VtsAttributesSize - 1);
            WriteVideoAttributes(e[264..], standard, aspect); // title VOBs' video
            e[267] = 1;                                        // one audio stream
            WriteAc3Stereo(e[268..]);
        }

        if (pgciUtSector > 0)
        {
            // --- VMGM_PGCI_UT: one language unit ("en"): the title menu (entry PGC), then one command-only PGC per title
            //     from title 2 on, which the end of the previous title calls (CallSS VMGM PGC) to play it ---
            var pgcs = new List<(byte EntryId, byte[] Pgc)>();
            if (hasMenu)
            {
                pgcs.Add((0x82, BuildPgc(programs: 1, cells: 1, durationPts: Duration(menuUnits), thirtyFps: standard == DvdVideoStandard.Ntsc,
                    hasAudio: false, pre: null, post: JumpTitle1,
                    cell: new CellInfo(0, menuUnits[^1].Sector, menuVobSectors - 1, MenuStillSeconds))));
            }
            for (int title = 2; title <= titleSetCount; title++)
            {
                pgcs.Add((0x00, BuildPgc(programs: 0, cells: 0, durationPts: 0, thirtyFps: true, hasAudio: false,
                    pre: JumpTitle(title), post: null, cell: null)));
            }

            var ut = mat[(pgciUtSector * SectorSize)..];
            int pgcitBytes = 8 + 8 * pgcs.Count + pgcs.Sum(p => p.Pgc.Length);
            W16(ut, 0, 1);
            W32(ut, 4, 16 + pgcitBytes - 1);
            ut[8] = (byte)'e';
            ut[9] = (byte)'n';
            ut[11] = (byte)(hasMenu ? 0x80 : 0x00); // a title menu exists
            W32(ut, 12, 16);                        // the unit's PGCIT, from the start of PGCI_UT
            var pgcit = ut[16..];
            W16(pgcit, 0, pgcs.Count);
            W32(pgcit, 4, pgcitBytes - 1);
            int at = 8 + 8 * pgcs.Count;
            for (int i = 0; i < pgcs.Count; i++)
            {
                pgcit[8 + 8 * i] = pgcs[i].EntryId; // 0x82: entry PGC, title menu; 0: not an entry PGC
                W32(pgcit, 8 + 8 * i + 4, at);      // PGC, from the start of the PGCIT
                pgcs[i].Pgc.CopyTo(pgcit[at..]);
                at += pgcs[i].Pgc.Length;
            }
        }

        if (hasMenu)
        {
            WriteCellAddressTable(mat[(cAdtSector * SectorSize)..], menuVobSectors);
            WriteVobuAddressMap(mat[(admapSector * SectorSize)..], menuUnits);
        }

        return ifo;
    }

    /// <summary>
    /// The bytes of the VMGM PGCI_UT (0 when there is none): header and language unit (16), PGCIT header (8), a search
    /// pointer (8) and the PGC for the title menu and for each title from 2 on.
    /// </summary>
    private static int PgciUtBytes(int titleSetCount, bool hasMenu)
    {
        int jumps = Math.Max(0, titleSetCount - 1);
        if (!hasMenu && jumps == 0)
        {
            return 0;
        }
        int bytes = 16 + 8 + 8 * (jumps + (hasMenu ? 1 : 0)) + jumps * PgcBytes(programs: 0, cells: 0, commands: 1);
        return hasMenu ? bytes + PgcBytes(programs: 1, cells: 1, commands: 1) : bytes;
    }

    /// <summary>
    /// Builds a title set IFO (VTS_nn_0.IFO) for one title of one cell. <paramref name="vobus"/> (from
    /// <see cref="PatchVobNavPacketsInFile"/>) gives the VOBU address map, the time map and the running time; without
    /// it <paramref name="navPackSectors"/> and <paramref name="durationSeconds"/> are used and there is no time map.
    /// At the end the title goes on to <paramref name="nextTitleNumber"/> (through the VMGM program chain
    /// <see cref="TitleJumpPgcNumber"/>), else calls the title menu or stops; <paramref name="callMenuAtEnd"/> says
    /// whether the VMG has a title menu. The IFO is at least <paramref name="ifoSectors"/> sectors: a larger
    /// one moves the title VOBs further out, which lets the authoring put them on a 16-sector block. Header fields: 0x0C
    /// last sector of the title set (the BUP, a copy of the IFO after the VOB files), 0x1C last sector of the IFO, 0xC0 /
    /// 0xC4 first sector of the menu / title VOBs, counted from the start of the IFO.
    /// </summary>
    public static byte[] BuildVtsIfo(
        int titleSetNumber,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        int titleVobTotalSectors,
        int durationSeconds = 60,
        int menuVobSectors = 0,
        IReadOnlyList<int>? navPackSectors = null,
        int nextTitleNumber = 0,
        int ifoSectors = 0,
        IReadOnlyList<DvdVobu>? vobus = null,
        bool callMenuAtEnd = true)
    {
        titleSetNumber = Math.Clamp(titleSetNumber, 1, 99);
        titleVobTotalSectors = Math.Max(16, titleVobTotalSectors);
        bool thirtyFps = standard == DvdVideoStandard.Ntsc;
        var units = vobus is { Count: > 0 }
            ? vobus
            : (navPackSectors is { Count: > 0 } ? navPackSectors : new[] { 0 }).Select(s => new DvdVobu(s, -1, -1)).ToList();
        bool timed = units[0].StartPts >= 0 && units[^1].EndPts > units[0].StartPts;
        long durationPts = timed ? Duration(units) : Math.Max(1, durationSeconds) * 90_000L;

        byte[] post = nextTitleNumber > 0
            ? CallVmgmPgc(TitleJumpPgcNumber(nextTitleNumber, hasTitleMenu: callMenuAtEnd))
            : (callMenuAtEnd ? CallTitleMenu : Exit);
        byte[] pgc = BuildPgc(programs: 1, cells: 1, durationPts, thirtyFps, hasAudio: true, pre: null, post: post,
            cell: new CellInfo(0, units[^1].Sector, titleVobTotalSectors - 1, 0));
        byte[] timeMap = BuildTimeMap(units, timed);

        const int pttSector = 1, pgcitSector = 2, tmaptSector = 3;
        int cAdtSector = tmaptSector + Sectors(timeMap.Length);
        int admapSector = cAdtSector + 1;
        int natural = admapSector + Sectors(4 + 4 * units.Count);
        int total = Math.Max(natural, ifoSectors);
        byte[] ifo = new byte[total * SectorSize];
        var mat = ifo.AsSpan();

        // --- VTSI_MAT ---
        Encoding.ASCII.GetBytes(DvdVideoValidator.VtsMagicSignature).CopyTo(ifo, 0);
        W32(mat, 0x0C, 2 * total + menuVobSectors + titleVobTotalSectors - 1);
        W32(mat, 0x1C, total - 1);
        mat[0x21] = 0x11;
        W32(mat, 0x80, 0x3D7); // last byte of VTSI_MAT (VTSI_MAT_SIZE 984)
        W32(mat, 0xC0, menuVobSectors > 0 ? total : 0);
        W32(mat, 0xC4, total + menuVobSectors);
        W32(mat, 0xC8, pttSector);
        W32(mat, 0xCC, pgcitSector);
        W32(mat, 0xD4, tmaptSector);
        W32(mat, 0xE0, cAdtSector);
        W32(mat, 0xE4, admapSector);
        WriteVideoAttributes(mat[0x200..], standard, aspect);
        mat[0x203] = 1; // one audio stream
        WriteAc3Stereo(mat[0x204..]);

        // --- VTS_PTT_SRPT: title 1, chapter 1 = PGC 1, program 1 ---
        var ptt = mat[(pttSector * SectorSize)..];
        W16(ptt, 0, 1);
        W32(ptt, 4, 15);
        W32(ptt, 8, 12);
        W16(ptt, 12, 1);
        W16(ptt, 14, 1);

        // --- VTS_PGCIT: the title's entry PGC ---
        var pgcit = mat[(pgcitSector * SectorSize)..];
        W16(pgcit, 0, 1);
        W32(pgcit, 4, 16 + pgc.Length - 1);
        pgcit[8] = 0x81; // entry PGC of title 1
        W32(pgcit, 12, 16);
        pgc.CopyTo(pgcit[16..]);

        timeMap.CopyTo(mat[(tmaptSector * SectorSize)..]);
        WriteCellAddressTable(mat[(cAdtSector * SectorSize)..], titleVobTotalSectors);
        WriteVobuAddressMap(mat[(admapSector * SectorSize)..], units);
        return ifo;
    }

    /// <summary>Where a cell lies in its VOB, counted in sectors, and how long it stays as a still at its end.</summary>
    private readonly record struct CellInfo(int FirstSector, int LastVobuStartSector, int LastSector, int StillSeconds);

    private static int PgcBytes(int programs, int cells, int commands)
    {
        int size = PgcSize + 8 + 8 * commands;
        if (cells > 0)
        {
            size = Align4(size + programs) + 24 * cells + 4 * cells;
        }
        return size;
    }

    /// <summary>
    /// A program chain (pgc_t): programs and cells counts, running time, audio stream 0 present when
    /// <paramref name="hasAudio"/>, then its command table (one pre or post command), program map, cell playback and
    /// cell position tables, with their offsets at 0xE4-0xEA.
    /// </summary>
    private static byte[] BuildPgc(int programs, int cells, long durationPts, bool thirtyFps, bool hasAudio, byte[]? pre, byte[]? post, CellInfo? cell)
    {
        int commands = (pre != null ? 1 : 0) + (post != null ? 1 : 0);
        byte[] pgc = new byte[PgcBytes(programs, cells, commands)];
        var p = pgc.AsSpan();
        p[2] = (byte)programs;
        p[3] = (byte)cells;
        if (cells > 0)
        {
            DvdNavPackWriter.Bcd(durationPts, thirtyFps).CopyTo(p[4..]);
        }
        if (hasAudio)
        {
            W16(p, 0x0C, 0x8000); // audio stream 0 present, decoding stream 0
        }

        int at = PgcSize;
        W16(p, 0xE4, at);
        var table = p[at..];
        W16(table, 0, pre != null ? 1 : 0);
        W16(table, 2, post != null ? 1 : 0);
        W16(table, 6, 8 + 8 * commands - 1);
        int c = 8;
        if (pre != null) { pre.CopyTo(table[c..]); c += 8; }
        if (post != null) { post.CopyTo(table[c..]); c += 8; }
        at += c;

        if (cells > 0 && cell is { } info)
        {
            W16(p, 0xE6, at);
            p[at] = 1; // program 1 starts at cell 1
            at = Align4(at + programs);
            W16(p, 0xE8, at);
            var playback = p[at..];
            playback[2] = (byte)info.StillSeconds;
            DvdNavPackWriter.Bcd(durationPts, thirtyFps).CopyTo(playback[4..]);
            W32(playback, 8, info.FirstSector);
            W32(playback, 16, info.LastVobuStartSector);
            W32(playback, 20, info.LastSector);
            at += 24;
            W16(p, 0xEA, at);
            W16(p, at, 1);   // VOB id 1
            p[at + 3] = 1;   // cell id 1
        }
        return pgc;
    }

    /// <summary>
    /// VTS_TMAPT with one time map: the VOBU holding each multiple of the time unit, the unit chosen so the map has at
    /// most 2,048 entries. Without times the map is empty.
    /// </summary>
    private static byte[] BuildTimeMap(IReadOnlyList<DvdVobu> units, bool timed)
    {
        var entries = new List<int>();
        int unit = 1;
        if (timed)
        {
            long start = units[0].StartPts;
            long seconds = (units[^1].EndPts - start) / 90_000;
            unit = (int)Math.Clamp((seconds + 2047) / 2048, 1, 255);
            int v = 0;
            for (long t = unit; t < seconds; t += unit)
            {
                long target = start + t * 90_000;
                while (v + 1 < units.Count && units[v + 1].StartPts >= 0 && units[v + 1].StartPts <= target)
                {
                    v++;
                }
                entries.Add(units[v].Sector);
            }
        }
        byte[] map = new byte[8 + 4 + 4 + 4 * entries.Count];
        var m = map.AsSpan();
        W16(m, 0, 1);
        W32(m, 4, map.Length - 1);
        W32(m, 8, 12);
        m[12] = (byte)unit;
        W16(m, 14, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            W32(m, 16 + 4 * i, entries[i]);
        }
        return map;
    }

    /// <summary>C_ADT with one cell, VOB 1 cell 1, over the whole VOB.</summary>
    private static void WriteCellAddressTable(Span<byte> t, int vobSectors)
    {
        W16(t, 0, 1);
        W32(t, 4, 8 + 12 - 1);
        W16(t, 8, 1);
        t[10] = 1;
        W32(t, 12, 0);
        W32(t, 16, Math.Max(1, vobSectors) - 1);
    }

    /// <summary>VOBU_ADMAP: the first sector of every VOBU.</summary>
    private static void WriteVobuAddressMap(Span<byte> t, IReadOnlyList<DvdVobu> units)
    {
        W32(t, 0, 4 + 4 * units.Count - 1);
        for (int i = 0; i < units.Count; i++)
        {
            W32(t, 4 + 4 * i, units[i].Sector);
        }
    }

    /// <summary>
    /// video_attr_t: MPEG-2, NTSC or PAL, 4:3 or 16:9; a 16:9 picture is shown letterboxed on a 4:3 screen (no pan and
    /// scan), 720 pixels wide.
    /// </summary>
    private static void WriteVideoAttributes(Span<byte> a, DvdVideoStandard standard, DvdAspectRatio aspect)
    {
        byte b = 0x40; // MPEG-2
        if (standard == DvdVideoStandard.Pal) b |= 0x10;
        if (aspect == DvdAspectRatio.Widescreen16x9) b |= 0x0C | 0x02; // 16:9, letterbox only
        a[0] = b;
        a[1] = 0x00;
    }

    /// <summary>audio_attr_t: AC-3, language given ("en"), 48 kHz, 2 channels.</summary>
    private static void WriteAc3Stereo(Span<byte> a)
    {
        a[0] = 0x04; // AC-3, language type 1
        a[1] = 0x01; // 48 kHz, channels - 1 = 1
        a[2] = (byte)'e';
        a[3] = (byte)'n';
    }

    private static long Duration(IReadOnlyList<DvdVobu> units) =>
        units.Count > 0 && units[0].StartPts >= 0 && units[^1].EndPts > units[0].StartPts ? units[^1].EndPts - units[0].StartPts : 0;

    private static int Sectors(int bytes) => Math.Max(1, (bytes + SectorSize - 1) / SectorSize);

    private static int Align4(int value) => (value + 3) & ~3;

    private static void W16(Span<byte> s, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(s[offset..], (ushort)value);

    private static void W32(Span<byte> s, int offset, long value) => BinaryPrimitives.WriteUInt32BigEndian(s[offset..], (uint)value);

    /// <summary>
    /// Splits an oversized VOB file (> 1 GB) on 2048-byte sector boundaries into VTS_xx_1.VOB, VTS_xx_2.VOB, ...
    /// If the file is smaller than or equal to maxVobBytes, moves or copies it to target single file.
    /// </summary>
    public static IReadOnlyList<string> SplitVobFileIfOversized(
        string sourceVobPath,
        string targetDirectory,
        int titleSetNumber,
        long maxVobBytes = 1_048_576_000L)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVobPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        titleSetNumber = Math.Clamp(titleSetNumber, 1, 99);

        Directory.CreateDirectory(targetDirectory);
        long sectorAlignedMaxBytes = (maxVobBytes / SectorSize) * SectorSize;
        if (sectorAlignedMaxBytes <= 0) sectorAlignedMaxBytes = 1_048_576_000L;

        var sourceFi = new FileInfo(sourceVobPath);
        if (!sourceFi.Exists)
        {
            throw new FileNotFoundException($"Source VOB file '{sourceVobPath}' was not found.", sourceVobPath);
        }

        string singleVobName = $"VTS_{titleSetNumber:D2}_1.VOB";
        string singleVobPath = Path.Combine(targetDirectory, singleVobName);

        if (sourceFi.Length <= sectorAlignedMaxBytes)
        {
            if (!string.Equals(Path.GetFullPath(sourceVobPath), Path.GetFullPath(singleVobPath), StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(sourceVobPath).StartsWith("ignis_transcode_", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(sourceVobPath).StartsWith("_temp_transcode_", StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourceVobPath, singleVobPath, overwrite: true);
                }
                else
                {
                    File.Copy(sourceVobPath, singleVobPath, overwrite: true);
                }
            }
            return new[] { singleVobPath };
        }

        // Split across multiple VOB parts (_1.VOB, _2.VOB, ...)
        var generatedFiles = new List<string>();
        byte[] buffer = new byte[1024 * 1024]; // 1 MB sector-aligned buffer

        using var srcStream = new FileStream(sourceVobPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024);
        int partIndex = 1;
        long totalRead = 0;

        while (totalRead < sourceFi.Length)
        {
            string partName = $"VTS_{titleSetNumber:D2}_{partIndex}.VOB";
            string partPath = Path.Combine(targetDirectory, partName);
            generatedFiles.Add(partPath);

            long currentPartWritten = 0;
            using (var dstStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
            {
                while (currentPartWritten < sectorAlignedMaxBytes && totalRead < sourceFi.Length)
                {
                    int toRead = (int)Math.Min(buffer.Length, sectorAlignedMaxBytes - currentPartWritten);
                    toRead = (int)Math.Min(toRead, sourceFi.Length - totalRead);

                    int read = srcStream.Read(buffer, 0, toRead);
                    if (read <= 0) break;

                    dstStream.Write(buffer, 0, read);
                    currentPartWritten += read;
                    totalRead += read;
                }
            }

            partIndex++;
            if (partIndex > 9)
            {
                // Maximum 9 VOB files per Title Set in DVD-Video specification
                break;
            }
        }

        return generatedFiles;
    }
}
