using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// One VOBU (video object unit) of a VOB stream: the sector of its NAV pack counted from the start of the stream, its
/// start and end presentation times in 90 kHz units (-1 when the stream gave none), and the cell (chapter) it belongs to.
/// </summary>
public readonly record struct DvdVobu(int Sector, long StartPts, long EndPts, int CellId = 1);

/// <summary>
/// A menu button: its rectangle on the 720-pixel-wide picture (inclusive), the buttons (1-based) the remote's up, down,
/// left and right move to, and the VM command it runs when activated.
/// </summary>
public sealed record DvdMenuButton(int X1, int Y1, int X2, int Y2, int Up, int Down, int Left, int Right, byte[] Command);

/// <summary>
/// Fills the NAV packs of an MPEG-2 program stream (one per VOBU, as FFmpeg's DVD muxer leaves them: pack header, system
/// header, PCI and DSI private stream 2 packets) with what DVD players navigate by. Layout per libdvdread's nav_types.h
/// and nav_read.c (PCI data at byte 0x2D, DSI data at 0x407): PCI general information (NAV pack address, VOBU start and
/// end presentation time, cell elapsed time) and, for a menu, one highlight button; DSI general information (SCR,
/// address, VOBU end address, the ends of the first three reference pictures, VOB and cell ids, cell elapsed time), the
/// search information (next and previous VOBU, which libdvdnav follows from VOBU to VOBU, and the fast forward and
/// reverse table at 120 s down to 0.5 s) and the offset of the first audio pack. The stream is one cell, or with a chapter
/// length one cell per chapter: a new cell starts at the first VOBU at or after each multiple of that length, and the
/// search information and cell elapsed time stay within the cell, as libdvdnav steps from cell to cell.
/// </summary>
public static class DvdNavPackWriter
{
    private const int SectorSize = OpticalConstants.DataSectorSize;

    /// <summary>Byte offset of the PCI data in a NAV pack (after pack header, system header, PES header, substream id).</summary>
    public const int PciOffset = 0x2D;

    /// <summary>Byte offset of the DSI data in a NAV pack.</summary>
    public const int DsiOffset = 0x407;

    /// <summary>Search information value for "no VOBU there within this cell".</summary>
    public const uint EndOfCell = 0x3FFFFFFF;

    /// <summary>Offset of the search information (VOBU_SRI) within the DSI data: after DSI_GI (32), SML_PBI (148) and SML_AGLI (54).</summary>
    private const int SriOffset = 32 + 148 + 54;

    /// <summary>Offset of the synchronous information (SYNCI) within the DSI data, after the 168-byte VOBU_SRI.</summary>
    private const int SynciOffset = SriOffset + 168;

    /// <summary>Highlight information within the PCI data, after PCI_GI (60) and NSML_AGLI (36).</summary>
    private const int HliOffset = 60 + 36;

    /// <summary>Fast forward / reverse distances of VOBU_SRI in half seconds, farthest first (FWDA order).</summary>
    private static readonly int[] ScanHalfSeconds = [240, 120, 60, 20, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];

    private sealed class VobuScan
    {
        public int Sector;
        public uint Scr;
        public long MinPts = -1;
        public long MaxPts = -1;
        public int FirstAudioSector = -1;
        public readonly List<(int Sector, int Type)> Pictures = new();
    }

    /// <summary>
    /// Patches every NAV pack of <paramref name="vob"/> in place and returns its VOBUs. A stream whose first sector is not
    /// a NAV pack gets one formatted there (the synthetic streams of tests). <paramref name="buttonCommand"/> makes a menu
    /// VOB: one highlight button over the whole picture, selected from the start, that runs the command.
    /// </summary>
    public static IReadOnlyList<DvdVobu> Patch(Stream vob, int vobId = 1, int cellId = 1, byte[]? buttonCommand = null, int chapterSeconds = 0,
        IReadOnlyList<DvdMenuButton>? buttons = null, bool wideButtons = false)
    {
        ArgumentNullException.ThrowIfNull(vob);
        int totalSectors = (int)(vob.Length / SectorSize);
        if (totalSectors == 0)
        {
            return Array.Empty<DvdVobu>();
        }

        byte[] sector = new byte[SectorSize];
        vob.Position = 0;
        vob.ReadExactly(sector);
        if (!IsNavPack(sector))
        {
            FormatNavPack(sector);
            vob.Position = 0;
            vob.Write(sector);
        }

        var (scans, frameDuration, thirtyFps, height) = Scan(vob, totalSectors);
        var vobus = AssignCells(Times(scans, totalSectors, frameDuration), cellId, chapterSeconds);

        for (int i = 0; i < scans.Count; i++)
        {
            var scan = scans[i];
            var vobu = vobus[i];
            int lastSector = (i + 1 < scans.Count ? scans[i + 1].Sector : totalSectors) - 1;
            int vobuEa = lastSector - scan.Sector;

            vob.Position = (long)scan.Sector * SectorSize;
            vob.ReadExactly(sector);
            if (!HasDsi(sector))
            {
                FormatNavPack(sector);
            }

            var pci = sector.AsSpan(PciOffset, 1024 - PciOffset);
            var dsi = sector.AsSpan(DsiOffset, SectorSize - DsiOffset);
            pci.Clear();
            dsi.Clear();

            var (cellFirst, cellLast) = CellRange(vobus, i);
            long cellStart = vobus[cellFirst].StartPts >= 0 ? vobus[cellFirst].StartPts : 0;
            byte[] elapsed = Bcd(vobu.StartPts >= 0 ? vobu.StartPts - cellStart : 0, thirtyFps);
            // PCI_GI
            BinaryPrimitives.WriteUInt32BigEndian(pci, (uint)scan.Sector);
            BinaryPrimitives.WriteUInt32BigEndian(pci[12..], (uint)Math.Max(0, vobu.StartPts));
            BinaryPrimitives.WriteUInt32BigEndian(pci[16..], (uint)Math.Max(0, vobu.EndPts));
            if (i == scans.Count - 1)
            {
                BinaryPrimitives.WriteUInt32BigEndian(pci[20..], (uint)Math.Max(0, vobu.EndPts)); // the sequence ends here
            }
            elapsed.CopyTo(pci[24..]);
            if (buttons is { Count: > 0 })
            {
                WriteButtons(pci[HliOffset..], vobu, buttons, wideButtons);
            }
            else if (buttonCommand != null)
            {
                WriteButton(pci[HliOffset..], vobu, height, buttonCommand);
            }

            // DSI_GI
            BinaryPrimitives.WriteUInt32BigEndian(dsi, scan.Scr);
            BinaryPrimitives.WriteUInt32BigEndian(dsi[4..], (uint)scan.Sector);
            BinaryPrimitives.WriteUInt32BigEndian(dsi[8..], (uint)vobuEa);
            var refs = ReferenceEnds(scan, vobuEa);
            BinaryPrimitives.WriteUInt32BigEndian(dsi[12..], (uint)refs[0]);
            BinaryPrimitives.WriteUInt32BigEndian(dsi[16..], (uint)refs[1]);
            BinaryPrimitives.WriteUInt32BigEndian(dsi[20..], (uint)refs[2]);
            BinaryPrimitives.WriteUInt16BigEndian(dsi[24..], (ushort)vobId);
            dsi[27] = (byte)vobu.CellId;
            elapsed.CopyTo(dsi[28..]);

            // VOBU_SRI: every VOBU holds video, so next/previous video equal next/previous VOBU.
            var sri = dsi[SriOffset..];
            uint next = i < cellLast ? 0x80000000u | (uint)(vobus[i + 1].Sector - scan.Sector) : EndOfCell;
            uint prev = i > cellFirst ? 0x80000000u | (uint)(scan.Sector - vobus[i - 1].Sector) : EndOfCell;
            BinaryPrimitives.WriteUInt32BigEndian(sri, next);
            for (int k = 0; k < ScanHalfSeconds.Length; k++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(sri[(4 + 4 * k)..], Forward(vobus, i, cellLast, ScanHalfSeconds[k] * 45_000L));
                // BWDA runs nearest first, the mirror of FWDA.
                BinaryPrimitives.WriteUInt32BigEndian(sri[(88 + 4 * k)..], Backward(vobus, i, cellFirst, ScanHalfSeconds[ScanHalfSeconds.Length - 1 - k] * 45_000L));
            }
            BinaryPrimitives.WriteUInt32BigEndian(sri[80..], next);
            BinaryPrimitives.WriteUInt32BigEndian(sri[84..], prev);
            BinaryPrimitives.WriteUInt32BigEndian(sri[164..], prev);

            // SYNCI: the first pack of audio stream 0, counted from the NAV pack.
            BinaryPrimitives.WriteUInt16BigEndian(dsi[SynciOffset..],
                (ushort)(scan.FirstAudioSector >= 0 ? Math.Min(0x7FFF, scan.FirstAudioSector - scan.Sector) : 0x3FFF));

            vob.Position = (long)scan.Sector * SectorSize;
            vob.Write(sector);
        }
        vob.Flush();
        return vobus;
    }

    /// <summary>Patches a VOB file in place; see <see cref="Patch(Stream, int, int, byte[])"/>.</summary>
    public static IReadOnlyList<DvdVobu> PatchFile(string path, int vobId = 1, int cellId = 1, byte[]? buttonCommand = null, int chapterSeconds = 0,
        IReadOnlyList<DvdMenuButton>? buttons = null, bool wideButtons = false)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        return Patch(fs, vobId, cellId, buttonCommand, chapterSeconds, buttons, wideButtons);
    }

    /// <summary>
    /// Cell numbers: <paramref name="firstCell"/> for every VOBU, or with <paramref name="chapterSeconds"/> &gt; 0 a new
    /// cell at the first VOBU whose start is at or past each multiple of it (at most 99 cells, the chapter limit), except
    /// within half a chapter (at most 60 s) of the end, so the last chapter is never a sliver.
    /// </summary>
    private static List<DvdVobu> AssignCells(List<DvdVobu> vobus, int firstCell, int chapterSeconds)
    {
        if (chapterSeconds <= 0 || vobus.Count == 0 || vobus[0].StartPts < 0)
        {
            return vobus.Select(v => v with { CellId = firstCell }).ToList();
        }
        long origin = vobus[0].StartPts;
        long length = chapterSeconds * 90_000L;
        long end = vobus[^1].EndPts;
        long shortest = Math.Min(60 * 90_000L, length / 2); // no chapter mark this close to the end
        int cell = firstCell;
        long nextMark = origin + length;
        var result = new List<DvdVobu>(vobus.Count);
        foreach (var v in vobus)
        {
            if (v.StartPts >= nextMark && cell < 99 && end - v.StartPts >= shortest)
            {
                cell++;
                nextMark = origin + (1 + (v.StartPts - origin) / length) * length;
            }
            result.Add(v with { CellId = cell });
        }
        return result;
    }

    /// <summary>The first and last VOBU index of the cell VOBU <paramref name="i"/> is in.</summary>
    private static (int First, int Last) CellRange(List<DvdVobu> vobus, int i)
    {
        int first = i, last = i;
        while (first > 0 && vobus[first - 1].CellId == vobus[i].CellId) first--;
        while (last + 1 < vobus.Count && vobus[last + 1].CellId == vobus[i].CellId) last++;
        return (first, last);
    }

    /// <summary>True when the sector starts with a pack header and holds a PCI packet where a NAV pack has it.</summary>
    public static bool IsNavPack(ReadOnlySpan<byte> s) =>
        s.Length >= 45 && s[0] == 0 && s[1] == 0 && s[2] == 1 && s[3] == 0xBA &&
        s[38] == 0 && s[39] == 0 && s[40] == 1 && s[41] == 0xBF && s[44] == 0x00;

    private static bool HasDsi(ReadOnlySpan<byte> s) =>
        s[1024] == 0 && s[1025] == 0 && s[1026] == 1 && s[1027] == 0xBF && s[1030] == 0x01;

    /// <summary>Writes an empty NAV pack: pack header, system header, PCI and DSI packets with their lengths.</summary>
    public static void FormatNavPack(Span<byte> s)
    {
        s[..SectorSize].Clear();
        ReadOnlySpan<byte> packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
        // Rate bound, audio bound 1, video bound 1; buffer entries for video (E0), MPEG audio (C0), private 1 (BD), private 2 (BF).
        ReadOnlySpan<byte> systemHeader = [0x00, 0x00, 0x01, 0xBB, 0x00, 0x12, 0x80, 0xC4, 0xE1, 0x04, 0xE1, 0xFF, 0xE0, 0xE0, 0xE8, 0xC0, 0xC0, 0x20, 0xBD, 0xE0, 0x3A, 0xBF, 0xE0, 0x02];
        packHeader.CopyTo(s);
        systemHeader.CopyTo(s[14..]);
        ReadOnlySpan<byte> pci = [0x00, 0x00, 0x01, 0xBF, 0x03, 0xD4, 0x00];
        ReadOnlySpan<byte> dsi = [0x00, 0x00, 0x01, 0xBF, 0x03, 0xFA, 0x01];
        pci.CopyTo(s[38..]);
        dsi.CopyTo(s[1024..]);
    }

    private static (List<VobuScan> Scans, long FrameDuration, bool ThirtyFps, int Height) Scan(Stream vob, int totalSectors)
    {
        var scans = new List<VobuScan>();
        VobuScan? current = null;
        long frameDuration = 3003; // 29.97 fps until a sequence header says otherwise
        bool thirtyFps = true, rateKnown = false;
        int height = 480;
        byte[] carry = Array.Empty<byte>();
        int carrySector = 0;

        const int chunkSectors = 512;
        byte[] chunk = new byte[chunkSectors * SectorSize];
        vob.Position = 0;
        for (int first = 0; first < totalSectors; first += chunkSectors)
        {
            int count = Math.Min(chunkSectors, totalSectors - first);
            vob.ReadExactly(chunk.AsSpan(0, count * SectorSize));
            for (int k = 0; k < count; k++)
            {
                int sectorIndex = first + k;
                var s = chunk.AsSpan(k * SectorSize, SectorSize);
                if (IsNavPack(s))
                {
                    current = new VobuScan { Sector = sectorIndex, Scr = ReadScr(s) };
                    scans.Add(current);
                    continue;
                }
                if (current == null || s[0] != 0 || s[1] != 0 || s[2] != 1 || s[3] != 0xBA)
                {
                    continue;
                }

                int p = 14 + (s[13] & 7);
                while (p + 9 <= SectorSize && s[p] == 0 && s[p + 1] == 0 && s[p + 2] == 1)
                {
                    int streamId = s[p + 3];
                    int length = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 4)..]);
                    int end = Math.Min(SectorSize, p + 6 + length);
                    if (streamId is 0xE0 or 0xBD or (>= 0xC0 and <= 0xDF) && (s[p + 6] & 0xC0) == 0x80)
                    {
                        int headerLength = s[p + 8];
                        int payload = p + 9 + headerLength;
                        if (streamId == 0xE0)
                        {
                            if ((s[p + 7] & 0x80) != 0)
                            {
                                long pts = ReadPts(s[(p + 9)..]);
                                current.MinPts = current.MinPts < 0 ? pts : Math.Min(current.MinPts, pts);
                                current.MaxPts = Math.Max(current.MaxPts, pts);
                            }
                            if (payload < end)
                            {
                                byte[] data = new byte[carry.Length + end - payload];
                                carry.CopyTo(data, 0);
                                s[payload..end].CopyTo(data.AsSpan(carry.Length));
                                for (int i = 0; i + 5 < data.Length; i++)
                                {
                                    if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                                    {
                                        continue;
                                    }
                                    int at = i < carry.Length ? carrySector : sectorIndex;
                                    if (data[i + 3] == 0x00)
                                    {
                                        current.Pictures.Add((at - current.Sector, (data[i + 5] >> 3) & 7));
                                    }
                                    else if (data[i + 3] == 0xB3 && i + 7 < data.Length && !rateKnown)
                                    {
                                        height = ((data[i + 5] & 0x0F) << 8) | data[i + 6];
                                        // frame_rate_code: 3 = 25 fps (PAL), 4 = 29.97 fps (NTSC); DVD uses no others.
                                        int rateCode = data[i + 7] & 0x0F;
                                        thirtyFps = rateCode != 3;
                                        frameDuration = thirtyFps ? 3003 : 3600;
                                        rateKnown = true;
                                    }
                                }
                                carry = data.AsSpan(Math.Max(0, data.Length - 5)).ToArray();
                                carrySector = sectorIndex;
                            }
                        }
                        else if (current.FirstAudioSector < 0 &&
                                 (streamId != 0xBD || (payload < end && (s[payload] & 0xF8) == 0x80)))
                        {
                            current.FirstAudioSector = sectorIndex;
                        }
                    }
                    p = end;
                }
            }
        }
        return (scans, frameDuration, thirtyFps, height);
    }

    /// <summary>Start and end time of each VOBU: the earliest video PTS in it, and the next VOBU's start (the last one
    /// ends one frame after its latest PTS). A VOBU without a PTS takes the previous one's end.</summary>
    private static List<DvdVobu> Times(List<VobuScan> scans, int totalSectors, long frameDuration)
    {
        var result = new List<DvdVobu>(scans.Count);
        long previousEnd = -1;
        for (int i = 0; i < scans.Count; i++)
        {
            long start = scans[i].MinPts >= 0 ? scans[i].MinPts : previousEnd;
            long end = -1;
            for (int j = i + 1; j < scans.Count && end < 0; j++)
            {
                end = scans[j].MinPts;
            }
            if (end < 0 || i == scans.Count - 1)
            {
                end = scans[i].MaxPts >= 0 ? scans[i].MaxPts + frameDuration : (start >= 0 ? start + frameDuration : -1);
            }
            result.Add(new DvdVobu(scans[i].Sector, start, end));
            previousEnd = end;
        }
        return result;
    }

    /// <summary>
    /// Last sectors (relative to the NAV pack) of the first three reference (I or P) pictures in coded order: each ends
    /// in the sector where the next picture starts. Missing ones repeat the one before; with none, the VOBU's end.
    /// </summary>
    private static int[] ReferenceEnds(VobuScan scan, int vobuEa)
    {
        int[] ends = new int[3];
        int found = 0;
        for (int i = 0; i < scan.Pictures.Count && found < 3; i++)
        {
            if (scan.Pictures[i].Type is 1 or 2)
            {
                ends[found++] = i + 1 < scan.Pictures.Count ? Math.Min(vobuEa, scan.Pictures[i + 1].Sector) : vobuEa;
            }
        }
        for (int i = found; i < 3; i++)
        {
            ends[i] = i == 0 ? vobuEa : ends[i - 1];
        }
        return ends;
    }

    private static uint Forward(List<DvdVobu> vobus, int i, int cellLast, long distance)
    {
        if (vobus[i].StartPts < 0)
        {
            return EndOfCell;
        }
        long target = vobus[i].StartPts + distance;
        for (int j = i + 1; j <= cellLast; j++)
        {
            if (vobus[j].StartPts >= target)
            {
                return 0x80000000u | (uint)(vobus[j].Sector - vobus[i].Sector);
            }
        }
        return EndOfCell;
    }

    private static uint Backward(List<DvdVobu> vobus, int i, int cellFirst, long distance)
    {
        if (vobus[i].StartPts < 0)
        {
            return EndOfCell;
        }
        long target = vobus[i].StartPts - distance;
        for (int j = i - 1; j >= cellFirst; j--)
        {
            if (vobus[j].StartPts >= 0 && vobus[j].StartPts <= target)
            {
                return 0x80000000u | (uint)(vobus[i].Sector - vobus[j].Sector);
            }
        }
        return EndOfCell;
    }

    /// <summary>
    /// One button group with one button over the whole picture, selected from the start (HL_GI, BTN_COLIT, BTNI as
    /// libdvdread's read_hli parses them; a button is 18 bytes on disc).
    /// </summary>
    private static void WriteButton(Span<byte> hli, DvdVobu vobu, int height, byte[] command)
    {
        BinaryPrimitives.WriteUInt16BigEndian(hli, 1); // highlight information, different from the previous VOBU's
        BinaryPrimitives.WriteUInt32BigEndian(hli[2..], (uint)Math.Max(0, vobu.StartPts));
        BinaryPrimitives.WriteUInt32BigEndian(hli[6..], 0xFFFFFFFF); // until the end of the cell
        BinaryPrimitives.WriteUInt32BigEndian(hli[10..], 0xFFFFFFFF);
        hli[14] = 0x10; // one button group, display type 0
        hli[16] = 0;    // button offset
        hli[17] = 1;    // buttons
        hli[18] = 1;    // selectable by number
        hli[20] = 1;    // forcibly selected button
        var button = hli[(22 + 24)..];
        int xEnd = 719, yEnd = Math.Clamp(height, 2, 576) - 1;
        button[0] = (byte)((1 << 6) | (0 >> 4));                  // color 1, x start 0
        button[1] = (byte)(((0 & 0x0F) << 4) | (xEnd >> 8));
        button[2] = (byte)xEnd;
        button[3] = 0;                                              // no auto action, y start 0
        button[4] = (byte)(yEnd >> 8);
        button[5] = (byte)yEnd;
        button[6] = button[7] = button[8] = button[9] = 1;          // up, down, left, right: itself
        command.AsSpan(0, 8).CopyTo(button[10..]);
    }

    /// <summary>
    /// Highlight information for menu buttons (one group of up to 36): selected button 1 at the start; colour scheme 1
    /// shows the button's subpicture pixels of value 1 in palette entry 4 (amber) when selected and 5 (white) when
    /// activated, fully opaque (BTN_COLIT: four colour nibbles e2 e1 p b, then four contrast nibbles). Each button: colour
    /// scheme 1, its rectangle (10-bit coordinates), no auto action, neighbours, command (18 bytes, as read_hli parses).
    /// </summary>
    private static void WriteButtons(Span<byte> hli, DvdVobu vobu, IReadOnlyList<DvdMenuButton> buttons, bool wide)
    {
        int count = Math.Min(36, buttons.Count);
        BinaryPrimitives.WriteUInt16BigEndian(hli, 1);
        BinaryPrimitives.WriteUInt32BigEndian(hli[2..], (uint)Math.Max(0, vobu.StartPts));
        BinaryPrimitives.WriteUInt32BigEndian(hli[6..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(hli[10..], 0xFFFFFFFF);
        hli[14] = (byte)(0x10 | (wide ? 0x01 : 0x00)); // one button group; display type 1 = wide (16:9)
        hli[16] = 0;
        hli[17] = (byte)count;
        hli[18] = (byte)count;
        hli[20] = 1;
        var colors = hli[22..];
        BinaryPrimitives.WriteUInt32BigEndian(colors, 0x004000F0);      // scheme 1, selected: pattern = amber, opaque
        BinaryPrimitives.WriteUInt32BigEndian(colors[4..], 0x005000F0); // scheme 1, activated: pattern = white, opaque
        for (int i = 0; i < count; i++)
        {
            var b = buttons[i];
            var e = hli[(22 + 24 + 18 * i)..];
            int x1 = Math.Clamp(b.X1, 0, 1023), x2 = Math.Clamp(b.X2, x1, 1023);
            int y1 = Math.Clamp(b.Y1, 0, 1023), y2 = Math.Clamp(b.Y2, y1, 1023);
            e[0] = (byte)((1 << 6) | (x1 >> 4));
            e[1] = (byte)(((x1 & 0x0F) << 4) | (x2 >> 8));
            e[2] = (byte)x2;
            e[3] = (byte)(y1 >> 4);
            e[4] = (byte)(((y1 & 0x0F) << 4) | (y2 >> 8));
            e[5] = (byte)y2;
            e[6] = (byte)Math.Clamp(b.Up, 1, count);
            e[7] = (byte)Math.Clamp(b.Down, 1, count);
            e[8] = (byte)Math.Clamp(b.Left, 1, count);
            e[9] = (byte)Math.Clamp(b.Right, 1, count);
            b.Command.AsSpan(0, 8).CopyTo(e[10..]);
        }
    }

    /// <summary>A time as DVD BCD hours, minutes, seconds, frames, with the frame rate in the top two frame bits.</summary>
    public static byte[] Bcd(long pts90k, bool thirtyFps)
    {
        long ticks = Math.Max(0, pts90k);
        long totalSeconds = ticks / 90_000;
        int fps = thirtyFps ? 30 : 25;
        int frames = (int)(ticks % 90_000 * fps / 90_000);
        return
        [
            ToBcd((int)Math.Min(99, totalSeconds / 3600)),
            ToBcd((int)(totalSeconds / 60 % 60)),
            ToBcd((int)(totalSeconds % 60)),
            (byte)((thirtyFps ? 0xC0 : 0x40) | ToBcd(Math.Min(frames, fps - 1)))
        ];
    }

    private static byte ToBcd(int value) => (byte)((value / 10 << 4) | value % 10);

    private static long ReadPts(ReadOnlySpan<byte> b) =>
        ((long)(b[0] & 0x0E) << 29) | ((long)b[1] << 22) | ((long)(b[2] & 0xFE) << 14) | ((long)b[3] << 7) | ((long)b[4] >> 1);

    /// <summary>The low 32 bits of the 33-bit SCR base of an MPEG-2 pack header.</summary>
    private static uint ReadScr(ReadOnlySpan<byte> s)
    {
        long scr = ((long)(s[4] & 0x38) << 27) | ((long)(s[4] & 0x03) << 28) | ((long)s[5] << 20) |
                   ((long)(s[6] & 0xF8) << 12) | ((long)(s[6] & 0x03) << 13) | ((long)s[7] << 5) | ((long)s[8] >> 3);
        return (uint)scr;
    }
}
