using System.IO;
using System.Buffers.Binary;

namespace CdBurner.Core.Tests;

/// <summary>
/// An independent binary reader for a BDMV tree, written from the field layouts of the Blu-ray Disc Read-Only format
/// (and cross-checked against the open-source libbluray parsers) and sharing no code with the writers. It checks the
/// things a player's navigation would trip over: file tags and versions, address and length fields, the movie object
/// commands, playlist items against clip information, the clip's entry-point map against what is really in the stream
/// file, the 192-byte source packets with their arrival times, continuity counters, tables with CRC, and the
/// presentation-graphics segments.
/// </summary>
internal sealed class BdmvStructuralValidator
{
    public const long AtcMask = (1L << 30) - 1;

    public sealed record ScannedPes(long Spn, long Pts90k, long? Dts90k, bool RandomAccess);

    public sealed record ScannedSegment(byte Type, long Pts90k, long? Dts90k, byte[] Body, long Spn);

    public sealed class ClipScan
    {
        public long Packets;
        public List<ScannedPes> VideoPictures { get; } = new();
        public List<ScannedSegment> Segments { get; } = new();
        public Dictionary<ushort, byte> PmtStreams { get; } = new();
        public ushort PmtPid;
        public ushort PcrPid;
        public int PcrCount;
        public long MaxAtcStep;
        public long FirstAtc;
    }

    public List<string> Errors { get; } = new();
    public Dictionary<string, ClipScan> Scans { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly bool _avchd;

    private BdmvStructuralValidator(IReadOnlyDictionary<string, byte[]> files, bool avchd)
    {
        _files = files;
        _avchd = avchd;
    }

    private string Version => _avchd ? "0100" : "0200";
    private string StreamExt => _avchd ? ".MTS" : ".m2ts";
    private string ClipExt => _avchd ? ".CPI" : ".clpi";
    private string ListExt => _avchd ? ".MPL" : ".mpls";

    public static BdmvStructuralValidator Validate(IReadOnlyDictionary<string, byte[]> files, bool avchd)
    {
        var v = new BdmvStructuralValidator(new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase), avchd);
        v.Run();
        return v;
    }

    private void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Errors.Add(message);
        }
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private byte[]? Get(string path)
    {
        if (_files.TryGetValue(path, out var data))
        {
            return data;
        }
        Errors.Add($"missing file {path}");
        return null;
    }

    private void Run()
    {
        var playlists = _files.Keys.Where(k => k.StartsWith("BDMV/PLAYLIST/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var clipinfos = _files.Keys.Where(k => k.StartsWith("BDMV/CLIPINF/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Expect(playlists.Count >= 1 && clipinfos.Count >= 1, "no playlists or clip information");

        CheckIndexAndObjects(playlists.Count);
        foreach (string clpiPath in clipinfos)
        {
            CheckClip(clpiPath);
        }
        foreach (string mplsPath in playlists)
        {
            CheckPlaylist(mplsPath);
        }
        CheckBackupsAndCertificate();
    }

    // ----- index.bdmv and MovieObject.bdmv ------------------------------------------------------------------

    private void CheckIndexAndObjects(int playlistCount)
    {
        var index = Get("BDMV/index.bdmv");
        var mobj = Get("BDMV/MovieObject.bdmv");
        if (index == null || mobj == null)
        {
            return;
        }

        Expect(System.Text.Encoding.ASCII.GetString(index, 0, 8) == "INDX" + Version, "index.bdmv tag/version");
        uint indexesStart = U32(index, 8);
        Expect(U32(index, 12) == 0, "index.bdmv has no extension data");
        Expect(U32(index, 40) == 34, "AppInfoBDMV length must be 34");
        Expect(indexesStart == 40 + 4 + 34, "index table follows the app info");
        Expect(index.Length >= indexesStart + 4, "index.bdmv truncated");
        if (index.Length < indexesStart + 4)
        {
            return;
        }
        uint tableLength = U32(index, (int)indexesStart);
        Expect(indexesStart + 4 + tableLength == index.Length, "index table length must reach the end of the file");
        int p = (int)indexesStart + 4;
        for (int e = 0; e < 2; e++, p += 12)
        {
            Expect(index[p] >> 6 == 1, "first-playback and top-menu entries are HDMV objects");
        }
        int titles = U16(index, p);
        p += 2;
        Expect(tableLength == 12 + 12 + 2 + 12 * titles, "index table length matches the title count");
        Expect(titles == playlistCount, "one title per playlist");

        Expect(System.Text.Encoding.ASCII.GetString(mobj, 0, 8) == "MOBJ" + Version, "MovieObject.bdmv tag/version");
        Expect(U32(mobj, 8) == 0, "MovieObject.bdmv has no extension data");
        uint dataLength = U32(mobj, 40);
        Expect(40 + 4 + dataLength == mobj.Length, "MovieObject length reaches the end of the file");
        int objects = U16(mobj, 48);
        int q = 50;
        var played = new List<int>();
        for (int o = 0; o < objects; o++)
        {
            int commands = U16(mobj, q + 2);
            q += 4;
            for (int c = 0; c < commands; c++, q += 12)
            {
                byte b0 = mobj[q], b1 = mobj[q + 1];
                bool playPl = b0 >> 5 == 1 && ((b0 >> 3) & 3) == 0 && (b0 & 7) == 2 && (b1 & 0x0F) == 0 && (b1 & 0x80) != 0;
                Expect(playPl, $"object {o}: command {c} is not an immediate PlayPL");
                if (playPl)
                {
                    played.Add((int)U32(mobj, q + 4));
                }
            }
        }
        Expect(q == mobj.Length, "movie objects fill the file exactly");

        for (int t = 0; t < titles; t++)
        {
            int objectRef = U16(index, p + 12 * t + 6);
            Expect(index[p + 12 * t] >> 6 == 1, $"title {t} is an HDMV object");
            Expect(objectRef < objects, $"title {t} refers to movie object {objectRef}");
            if (objectRef < played.Count)
            {
                Expect(_files.ContainsKey($"BDMV/PLAYLIST/{played[objectRef]:D5}{ListExt}"), $"object {objectRef} plays a playlist that exists");
            }
        }
    }

    // ----- clip information and stream file -----------------------------------------------------------------

    private void CheckClip(string clpiPath)
    {
        var clpi = Get(clpiPath);
        if (clpi == null)
        {
            return;
        }
        string name = Path.GetFileNameWithoutExtension(clpiPath);
        var stream = Get($"BDMV/STREAM/{name}{StreamExt}");
        if (stream == null)
        {
            return;
        }

        Expect(System.Text.Encoding.ASCII.GetString(clpi, 0, 8) == "HDMV" + Version, $"{name}: clip information tag/version");
        uint seqAt = U32(clpi, 8), progAt = U32(clpi, 12), cpiAt = U32(clpi, 16), markAt = U32(clpi, 20);
        Expect(U32(clpi, 24) == 0, $"{name}: no extension data");
        Expect(40 < seqAt && seqAt < progAt && progAt < cpiAt && cpiAt < markAt && markAt <= clpi.Length, $"{name}: section addresses ascend inside the file");
        Expect(seqAt % 4 == 0 && progAt % 4 == 0 && cpiAt % 4 == 0 && markAt % 4 == 0, $"{name}: sections are 4-byte aligned");
        Expect(40 + 4 + U32(clpi, 40) == seqAt, $"{name}: ClipInfo length reaches the sequence info");

        uint sourcePackets = U32(clpi, 56);
        Expect(U32(clpi, 52) > 0, $"{name}: TS recording rate set");
        Expect(stream.Length == (long)sourcePackets * 192, $"{name}: ClipInfo packet count x 192 equals the stream file size");
        Expect(stream.Length % 6144 == 0, $"{name}: stream file is a whole number of 6,144-byte aligned units");

        var scan = ScanStream(name, stream);
        Scans[name] = scan;
        Expect(scan.Packets == sourcePackets, $"{name}: scanned packet count");

        // SequenceInfo
        int s = (int)seqAt;
        Expect(clpi[s + 5] == 1 && clpi[s + 10] == 1, $"{name}: one ATC and one STC sequence");
        Expect(U32(clpi, s + 6) == 0 && U32(clpi, s + 14) == 0, $"{name}: sequences start at packet 0");
        Expect(U16(clpi, s + 12) == scan.PcrPid, $"{name}: PCR PID in the sequence info matches the stream");
        uint startPts = U32(clpi, s + 18), endPts = U32(clpi, s + 22);
        if (scan.VideoPictures.Count > 0)
        {
            long min = scan.VideoPictures.Min(v => v.Pts90k) >> 1;
            long max = scan.VideoPictures.Max(v => v.Pts90k) >> 1;
            Expect(startPts == min, $"{name}: presentation start {startPts} equals first picture time {min}");
            Expect(endPts > max && endPts - max <= 1900, $"{name}: presentation end {endPts} is one frame after the last picture {max}");
        }

        // ProgramInfo against the PMT inside the stream
        int pr = (int)progAt;
        Expect(clpi[pr + 5] == 1, $"{name}: one program");
        Expect(U16(clpi, pr + 10) == scan.PmtPid, $"{name}: PMT PID matches the stream");
        int streams = clpi[pr + 12];
        int sp = pr + 14;
        var listed = new Dictionary<ushort, byte>();
        for (int i = 0; i < streams; i++)
        {
            ushort pid = U16(clpi, sp);
            int len = clpi[sp + 2];
            listed[pid] = clpi[sp + 3];
            sp += 3 + len;
        }
        Expect(sp - pr - 4 == U32(clpi, pr), $"{name}: ProgramInfo length matches its streams");
        Expect(listed.Count == scan.PmtStreams.Count && listed.All(kv => scan.PmtStreams.TryGetValue(kv.Key, out var t) && Coding(t) == kv.Value),
            $"{name}: ProgramInfo streams equal the PMT streams");

        // CPI / EP map
        var eps = ReadEpMap(name, clpi, (int)cpiAt, out ushort epPid);
        Expect(epPid == 0x1011, $"{name}: EP map is for the video PID");
        var expected = scan.VideoPictures.Where(v => v.RandomAccess).Select(v => (v.Spn, Pts: (v.Pts90k >> 1) >> 8 << 8)).ToList();
        Expect(expected.Count > 0, $"{name}: stream has random-access pictures");
        Expect(eps.Count == expected.Count, $"{name}: EP map holds {eps.Count} entries, the stream has {expected.Count} random-access pictures");
        for (int i = 0; i < Math.Min(eps.Count, expected.Count); i++)
        {
            Expect(eps[i].Spn == expected[i].Spn && eps[i].Pts == expected[i].Pts, $"{name}: EP {i} is ({eps[i].Spn}, {eps[i].Pts}), the stream has ({expected[i].Spn}, {expected[i].Pts})");
        }
        Expect(eps.Select(e => e.Spn).SequenceEqual(eps.Select(e => e.Spn).OrderBy(x => x)), $"{name}: EP packet numbers ascend");

        // ClipMark
        Expect(U32(clpi, (int)markAt) == 0, $"{name}: no clip marks");
    }

    private static byte Coding(byte streamType) => streamType; // PMT stream types equal the BD coding types (0x1B, 0x80/0x81, 0x90)

    private List<(long Spn, long Pts)> ReadEpMap(string name, byte[] clpi, int cpiAt, out ushort pid)
    {
        var result = new List<(long, long)>();
        pid = 0;
        uint length = U32(clpi, cpiAt);
        Expect(cpiAt + 4 + length <= clpi.Length, $"{name}: CPI length inside the file");
        Expect((U16(clpi, cpiAt + 4) & 0xF) == 1, $"{name}: CPI type is EP map");
        int ep = cpiAt + 6;
        Expect(clpi[ep + 1] == 1, $"{name}: one EP map stream");
        pid = U16(clpi, ep + 2);
        ulong packed = ((ulong)U16(clpi, ep + 4) << 32) | U32(clpi, ep + 6);
        int streamType = (int)((packed >> 34) & 0xF);
        int coarseCount = (int)((packed >> 18) & 0xFFFF);
        int fineCount = (int)(packed & 0x3FFFF);
        Expect(streamType == 1, $"{name}: EP stream type is video");
        int start = ep + (int)U32(clpi, ep + 10);
        uint fineStart = U32(clpi, start);
        Expect(fineStart == 4 + 8 * coarseCount, $"{name}: fine table follows the coarse table");
        var coarse = new List<(int Ref, uint Pts, uint Spn)>();
        for (int i = 0; i < coarseCount; i++)
        {
            uint a = U32(clpi, start + 4 + 8 * i);
            coarse.Add(((int)(a >> 14), a & 0x3FFF, U32(clpi, start + 8 + 8 * i)));
        }
        Expect(start + fineStart + 4L * fineCount == cpiAt + 4 + length, $"{name}: CPI length ends with the fine table");
        for (int j = 0; j < fineCount; j++)
        {
            uint f = U32(clpi, (int)(start + fineStart + 4 * j));
            int owner = coarse.FindLastIndex(c => c.Ref <= j);
            Expect(owner >= 0, $"{name}: fine entry {j} has a coarse owner");
            if (owner < 0)
            {
                continue;
            }
            long pts = (((long)coarse[owner].Pts & ~1L) << 18) + ((long)((f >> 17) & 0x7FF) << 8);
            long spn = (coarse[owner].Spn & ~0x1FFFFL) + (f & 0x1FFFF);
            result.Add((spn, pts));
        }
        return result;
    }

    // ----- the stream file ----------------------------------------------------------------------------------

    private ClipScan ScanStream(string name, byte[] data)
    {
        var scan = new ClipScan();
        scan.Packets = data.Length / 192;
        var continuity = new Dictionary<int, int>();
        var pesBuffers = new Dictionary<int, (List<byte> Bytes, long Spn)>();
        long previousAtc = -1;
        ushort pmtPid = 0;
        ushort pcrPid = 0;

        for (long i = 0; i < scan.Packets; i++)
        {
            int at = (int)(i * 192);
            uint header = U32(data, at);
            Expect(header >> 30 == 0, $"{name}: packet {i} copy permission bits are 0");
            long atc = header & AtcMask;
            if (previousAtc >= 0)
            {
                long step = (atc - previousAtc) & AtcMask;
                Expect(step < (1L << 28), $"{name}: packet {i} arrival time jumps by {step}");
                scan.MaxAtcStep = Math.Max(scan.MaxAtcStep, step);
            }
            else
            {
                scan.FirstAtc = atc;
            }
            previousAtc = atc;

            int t = at + 4;
            if (data[t] != 0x47)
            {
                Errors.Add($"{name}: packet {i} lost sync");
                return scan;
            }
            bool pusi = (data[t + 1] & 0x40) != 0;
            int pid = ((data[t + 1] & 0x1F) << 8) | data[t + 2];
            int afc = (data[t + 3] >> 4) & 3;
            int cc = data[t + 3] & 0xF;
            int payload = t + 4;
            bool rai = false;
            if ((afc & 2) != 0)
            {
                int afLen = data[t + 4];
                Expect(5 + afLen <= 188, $"{name}: packet {i} adaptation field fits");
                if (afLen > 0)
                {
                    rai = (data[t + 5] & 0x40) != 0;
                    if ((data[t + 5] & 0x10) != 0)
                    {
                        long b = ((long)data[t + 6] << 25) | ((long)data[t + 7] << 17) | ((long)data[t + 8] << 9) | ((long)data[t + 9] << 1) | (uint)(data[t + 10] >> 7);
                        long pcr = b * 300 + (((data[t + 10] & 1) << 8) | data[t + 11]);
                        scan.PcrCount++;
                        pcrPid = (ushort)pid;
                        Expect((pcr & AtcMask) == atc, $"{name}: PCR packet {i}: arrival time {atc} equals PCR {pcr & AtcMask}");
                    }
                }
                payload = t + 5 + afLen;
            }

            if (pid == 0x1FFF)
            {
                continue;
            }
            if ((afc & 1) != 0)
            {
                if (continuity.TryGetValue(pid, out int last))
                {
                    Expect(cc == ((last + 1) & 0xF), $"{name}: PID {pid:X} continuity broken at packet {i}");
                }
                continuity[pid] = cc;
            }

            if (pid == 0 || pid == pmtPid)
            {
                if (!pusi)
                {
                    continue;
                }
                int sec = payload + 1 + data[payload];
                int sectionLength = ((data[sec + 1] & 0x0F) << 8) | data[sec + 2];
                Expect(Crc32Mpeg(data.AsSpan(sec, 3 + sectionLength)) == 0, $"{name}: table on PID {pid:X} at packet {i} has a bad CRC");
                if (pid == 0)
                {
                    pmtPid = (ushort)(((data[sec + 10] & 0x1F) << 8) | data[sec + 11]);
                    scan.PmtPid = pmtPid;
                }
                else if (scan.PmtStreams.Count == 0)
                {
                    int infoLength = ((data[sec + 10] & 0x0F) << 8) | data[sec + 11];
                    scan.PcrPid = (ushort)(((data[sec + 8] & 0x1F) << 8) | data[sec + 9]);
                    int es = sec + 12 + infoLength;
                    int end = sec + 3 + sectionLength - 4;
                    while (es + 5 <= end)
                    {
                        scan.PmtStreams[(ushort)(((data[es + 1] & 0x1F) << 8) | data[es + 2])] = data[es];
                        es += 5 + (((data[es + 3] & 0x0F) << 8) | data[es + 4]);
                    }
                }
                continue;
            }

            if ((afc & 1) == 0)
            {
                continue;
            }
            bool isVideo = scan.PmtStreams.TryGetValue((ushort)pid, out var st) && st == 0x1B;
            bool isGraphics = st == 0x90 && scan.PmtStreams.ContainsKey((ushort)pid);
            if (!scan.PmtStreams.ContainsKey((ushort)pid))
            {
                Errors.Add($"{name}: packet {i} on PID {pid:X} that the PMT does not list");
                continue;
            }

            if (pusi)
            {
                if (isGraphics && pesBuffers.TryGetValue(pid, out var previous))
                {
                    FinishSegment(name, scan, previous.Bytes, previous.Spn);
                }
                Expect(data[payload] == 0 && data[payload + 1] == 0 && data[payload + 2] == 1, $"{name}: packet {i} PES start code");
                bool hasPts = (data[payload + 7] & 0x80) != 0;
                bool hasDts = (data[payload + 7] & 0x40) != 0;
                if (isVideo)
                {
                    Expect(data[payload + 3] == 0xE0, $"{name}: video PES stream id");
                    Expect(hasPts, $"{name}: video picture at packet {i} carries a PTS");
                    scan.VideoPictures.Add(new ScannedPes(i, hasPts ? Timestamp(data, payload + 9) : 0, hasDts ? Timestamp(data, payload + 14) : null, rai));
                }
                if (isGraphics)
                {
                    pesBuffers[pid] = (new List<byte>(data.AsSpan(payload, t + 188 - payload).ToArray()), i);
                }
            }
            else if (isGraphics && pesBuffers.TryGetValue(pid, out var open))
            {
                open.Bytes.AddRange(data.AsSpan(payload, t + 188 - payload).ToArray());
            }
        }

        foreach (var (_, open) in pesBuffers)
        {
            FinishSegment(name, scan, open.Bytes, open.Spn);
        }
        // Display sets of different PIDs interleave: order by packet number for the callers.
        scan.Segments.Sort((a, b) => a.Spn.CompareTo(b.Spn));
        Expect(scan.PmtStreams.Count > 0 && scan.PmtPid != 0, $"{name}: stream carries a PAT and a PMT");
        Expect(scan.PcrCount > 0, $"{name}: stream carries PCRs");
        Expect(scan.PcrPid == pcrPid, $"{name}: the PMT names the PID that carries the PCR");
        return scan;
    }

    private void FinishSegment(string name, ClipScan scan, List<byte> pes, long spn)
    {
        byte[] b = pes.ToArray();
        Expect(b.Length >= 14 && b[3] == 0xBD, $"{name}: graphics PES at packet {spn} is private stream 1");
        if (b.Length < 14)
        {
            return;
        }
        int pesLength = (b[4] << 8) | b[5];
        Expect(6 + pesLength <= b.Length && 6 + pesLength >= b.Length - 183, $"{name}: graphics PES length at packet {spn}");
        bool hasDts = (b[7] & 0x40) != 0;
        int headerLength = b[8];
        long pts = Timestamp(b, 9);
        long? dts = hasDts ? Timestamp(b, 14) : null;
        int payloadAt = 9 + headerLength;
        byte type = b[payloadAt];
        int size = (b[payloadAt + 1] << 8) | b[payloadAt + 2];
        Expect(6 + pesLength == payloadAt + 3 + size, $"{name}: graphics PES at packet {spn} holds exactly one segment");
        if (6 + pesLength == payloadAt + 3 + size && payloadAt + 3 + size <= b.Length)
        {
            scan.Segments.Add(new ScannedSegment(type, pts, dts, b.AsSpan(payloadAt + 3, size).ToArray(), spn));
        }
    }

    private static long Timestamp(byte[] b, int at) =>
        ((long)(b[at] & 0x0E) << 29) | ((long)b[at + 1] << 22) | ((long)(b[at + 2] & 0xFE) << 14) | ((long)b[at + 3] << 7) | (long)(b[at + 4] >> 1);

    public static uint Crc32Mpeg(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 24;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
            }
        }
        return crc;
    }

    // ----- playlists ----------------------------------------------------------------------------------------

    private void CheckPlaylist(string mplsPath)
    {
        var mpls = Get(mplsPath);
        if (mpls == null)
        {
            return;
        }
        string label = Path.GetFileName(mplsPath);
        Expect(System.Text.Encoding.ASCII.GetString(mpls, 0, 8) == "MPLS" + Version, $"{label}: tag/version");
        uint listAt = U32(mpls, 8), markAt = U32(mpls, 12);
        Expect(U32(mpls, 16) == 0, $"{label}: no extension data");
        Expect(U32(mpls, 40) == 14, $"{label}: AppInfoPlayList length");
        Expect(listAt == 40 + 4 + 14 && listAt < markAt && markAt <= mpls.Length, $"{label}: addresses");

        int p = (int)listAt;
        Expect(listAt + 4 + U32(mpls, p) == markAt, $"{label}: PlayList length reaches the marks");
        int items = U16(mpls, p + 6);
        int subPaths = U16(mpls, p + 8);
        Expect(items == 1 && subPaths == 0, $"{label}: one play item, no sub paths");
        p += 10;

        long itemIn = 0, itemOut = 0;
        for (int i = 0; i < items; i++)
        {
            int length = U16(mpls, p);
            int q = p + 2;
            string clip = System.Text.Encoding.ASCII.GetString(mpls, q, 5);
            Expect(System.Text.Encoding.ASCII.GetString(mpls, q + 5, 4) == "M2TS", $"{label}: codec identifier");
            Expect((mpls[q + 10] & 0x0F) == 1, $"{label}: connection condition");
            itemIn = U32(mpls, q + 12);
            itemOut = U32(mpls, q + 16);
            Expect(itemIn < itemOut, $"{label}: IN time {itemIn} is before OUT time {itemOut}");

            Expect(_files.ContainsKey($"BDMV/CLIPINF/{clip}{ClipExt}") && _files.ContainsKey($"BDMV/STREAM/{clip}{StreamExt}"), $"{label}: clip {clip} exists");
            if (_files.TryGetValue($"BDMV/CLIPINF/{clip}{ClipExt}", out var clpi))
            {
                uint seq = U32(clpi, 8);
                uint clipStart = U32(clpi, (int)seq + 18), clipEnd = U32(clpi, (int)seq + 22);
                Expect(itemIn >= clipStart && itemOut <= clipEnd, $"{label}: play item {itemIn}..{itemOut} lies inside the clip {clipStart}..{clipEnd}");
                Expect(itemIn == clipStart && itemOut == clipEnd, $"{label}: play item covers the whole clip");

                // STN streams against the clip's program
                int stn = p + 2 + 32;   // 32 fixed bytes of the play item come first
                int stnLength = U16(mpls, stn);
                Expect(stn + 2 + stnLength == p + 2 + length, $"{label}: STN table ends with the play item");
                int video = mpls[stn + 4], audio = mpls[stn + 5], pg = mpls[stn + 6];
                Expect(video == 1 && audio == 1, $"{label}: one video and one audio stream");
                int sp = stn + 4 + 8 + 4;
                var pids = new List<(ushort Pid, byte Coding)>();
                for (int s = 0; s < video + audio + pg; s++)
                {
                    int entryLength = mpls[sp];
                    Expect(entryLength == 3 && mpls[sp + 1] == 1, $"{label}: stream entry {s}");
                    ushort pid = U16(mpls, sp + 2);
                    sp += 1 + entryLength;
                    int attrLength = mpls[sp];
                    pids.Add((pid, mpls[sp + 1]));
                    sp += 1 + attrLength;
                }
                Expect(sp == stn + 2 + stnLength, $"{label}: STN streams fill the table");

                uint prog = U32(clpi, 12);
                int count = clpi[(int)prog + 12];
                var clipStreams = new Dictionary<ushort, byte>();
                int cp = (int)prog + 14;
                for (int s = 0; s < count; s++)
                {
                    clipStreams[U16(clpi, cp)] = clpi[cp + 3];
                    cp += 3 + clpi[cp + 2];
                }
                Expect(pids.All(x => clipStreams.TryGetValue(x.Pid, out var c) && c == x.Coding), $"{label}: playlist streams exist in the clip with the same coding type");
                Expect(clipStreams.Count == pids.Count, $"{label}: every clip stream is selectable");
            }
            p += 2 + length;
        }

        int m = (int)markAt;
        Expect(m + 4 + U32(mpls, m) == mpls.Length, $"{label}: PlayListMark length reaches the end of the file");
        int marks = U16(mpls, m + 4);
        Expect(marks >= 1, $"{label}: at least one mark");
        long previous = -1;
        for (int k = 0; k < marks; k++)
        {
            int o = m + 6 + 14 * k;
            long time = U32(mpls, o + 4);
            Expect(mpls[o + 1] == 1, $"{label}: mark {k} is an entry mark");
            Expect(U16(mpls, o + 2) == 0, $"{label}: mark {k} refers to play item 0");
            Expect(time >= itemIn && time < itemOut, $"{label}: mark {k} time {time} inside the play item");
            Expect(time > previous, $"{label}: mark times increase");
            previous = time;
            if (k == 0)
            {
                Expect(time == itemIn, $"{label}: the first mark is at the start");
            }
        }
    }

    private void CheckBackupsAndCertificate()
    {
        foreach (string path in _files.Keys.Where(k => k.StartsWith("BDMV/", StringComparison.OrdinalIgnoreCase)
                     && !k.StartsWith("BDMV/BACKUP/", StringComparison.OrdinalIgnoreCase)
                     && !k.StartsWith("BDMV/STREAM/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            string backup = "BDMV/BACKUP/" + path["BDMV/".Length..];
            Expect(_files.TryGetValue(backup, out var copy) && copy.AsSpan().SequenceEqual(_files[path]), $"{backup} is an identical copy of {path}");
        }

        var id = Get("CERTIFICATE/id.bdmv");
        if (id != null)
        {
            Expect(System.Text.Encoding.ASCII.GetString(id, 0, 8) == "BDID" + Version, "id.bdmv tag/version");
            Expect(U32(id, 8) == 40 && U32(id, 12) == 0 && id.Length == 60, "id.bdmv layout");
            Expect(_files.TryGetValue("CERTIFICATE/BACKUP/id.bdmv", out var copy) && copy.AsSpan().SequenceEqual(id), "CERTIFICATE/BACKUP/id.bdmv copy");
        }
    }
}
