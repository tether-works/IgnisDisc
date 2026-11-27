using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CdBurner.Core.BluRayVideo;

/// <summary>Stream numbers (PIDs) and attributes of one clip, as written into its playlist and clip information.</summary>
/// <param name="VideoPid">PID of the H.264 video stream (0x1011).</param>
/// <param name="AudioPid">PID of the main audio stream (0x1100).</param>
/// <param name="AudioFormatCode">Channel layout code of the audio: 1 mono, 3 stereo, 6 multichannel.</param>
/// <param name="AudioCodec">AC-3 or LPCM.</param>
/// <param name="AudioLanguage">ISO 639-2 language of the audio.</param>
/// <param name="Subtitles">PID (0x1200 upward) and language of each PGS track.</param>
/// <param name="PmtPid">PID of the program map table.</param>
/// <param name="VideoFormat">Picture size code.</param>
/// <param name="FrameRate">Frame rate code.</param>
public sealed record BdmvClipStreams(
    ushort VideoPid,
    ushort AudioPid,
    int AudioFormatCode,
    BdmvAudioCodec AudioCodec,
    string AudioLanguage,
    IReadOnlyList<(ushort Pid, string Language)> Subtitles,
    ushort PmtPid,
    BdmvVideoFormat VideoFormat,
    BdmvFrameRate FrameRate)
{
    /// <summary>Standard PIDs of a Blu-ray main title.</summary>
    public const ushort PrimaryVideoPid = 0x1011;

    /// <summary>First primary audio PID.</summary>
    public const ushort PrimaryAudioPid = 0x1100;

    /// <summary>First presentation-graphics PID.</summary>
    public const ushort FirstGraphicsPid = 0x1200;

    /// <summary>PID of the program map table the muxer writes.</summary>
    public const ushort ProgramMapPid = 0x0100;
}

/// <summary>One random-access point: an IDR picture's presentation time (90 kHz) and the source packet it starts in.</summary>
public sealed record BdmvEntryPoint(long Pts90k, long SourcePacketNumber);

/// <summary>Timing and random-access facts about a finished stream file.</summary>
/// <param name="SourcePackets">Number of 192-byte source packets.</param>
/// <param name="StartPts45k">Presentation start (45 kHz).</param>
/// <param name="EndPts45k">Presentation end (45 kHz).</param>
/// <param name="RecordingRateBytesPerSecond">Average rate of the stream file in bytes per second.</param>
/// <param name="EntryPoints">IDR pictures in packet order.</param>
public sealed record BdmvStreamFacts(
    long SourcePackets,
    long StartPts45k,
    long EndPts45k,
    uint RecordingRateBytesPerSecond,
    IReadOnlyList<BdmvEntryPoint> EntryPoints);

/// <summary>
/// Writes the binary navigation files of a BDMV tree: <c>index.bdmv</c>, <c>MovieObject.bdmv</c>, the playlist
/// (<c>.mpls</c>), the clip information (<c>.clpi</c>) and <c>CERTIFICATE/id.bdmv</c>. Field order and sizes follow the
/// Blu-ray Disc Read-Only format (Part 3) as read by open players; every file starts with a four-letter type tag and a
/// four-digit version (<c>0200</c> for Blu-ray, <c>0100</c> for AVCHD).
/// </summary>
public static class BdmvBinaryWriters
{
    /// <summary>The 45 kHz clock of playlists and clip information.</summary>
    public const long Clock45k = 45_000;

    /// <summary>The highest transport rate of a Blu-ray main clip, 48 Mbit/s, in bytes per second.</summary>
    public const uint BluRayMaxRecordingRateBytesPerSecond = 48_000_000 / 8;

    /// <summary>The length field of every StreamCodingInfo in clip information (21 bytes follow it).</summary>
    public const byte StreamCodingInfoLength = 21;

    /// <summary>The length field of every playlist stream entry (9 bytes follow it).</summary>
    public const byte StreamEntryLength = 9;

    private static void WriteNoIsrc(BeWriter w)
    {
        w.Ascii("000000000000");     // ISRC: country 2, owner 3, year 2, serial 5 characters
        w.Zeros(4);                  // reserved
    }

    /// <summary>Version tag of the files for a disc type.</summary>
    public static string VersionFor(BdmvDiscTarget target) => target == BdmvDiscTarget.Avchd ? "0100" : "0200";

    /// <summary>Clip name for a clip number: five digits.</summary>
    public static string ClipName(int index) => index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The audio channel-layout code: 1 mono, 3 stereo, 6 five-channel-plus-LFE ("multichannel").</summary>
    public static int AudioFormatCodeForChannels(int channels) => channels switch
    {
        1 => 1,
        2 => 3,
        6 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only 1, 2 or 6 audio channels are supported."),
    };

    /// <summary>Frame duration in 45 kHz ticks (rounded up) for the end time of a clip.</summary>
    public static long FrameDuration45k(BdmvFrameRate rate) => rate switch
    {
        BdmvFrameRate.Fps23976 => 1877,
        BdmvFrameRate.Fps24 => 1875,
        BdmvFrameRate.Fps25 => 1800,
        BdmvFrameRate.Fps2997 => 1502,
        BdmvFrameRate.Fps50 => 900,
        BdmvFrameRate.Fps5994 => 751,
        _ => throw new ArgumentOutOfRangeException(nameof(rate)),
    };

    /// <summary>The picture width and height of a video format.</summary>
    public static (int Width, int Height) VideoSize(BdmvVideoFormat format) => format switch
    {
        BdmvVideoFormat.Hd720p => (1280, 720),
        _ => (1920, 1080),
    };

    // ----- index.bdmv -----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>index.bdmv</c>: <c>INDX</c> + version, the start address of the index table, 24 reserved bytes, the 34-byte
    /// application info (video format and frame rate of the disc) and an index table in which the first-playback entry,
    /// the top-menu entry and every title run the movie object of the same number.
    /// </summary>
    public static byte[] WriteIndex(BdmvDiscTarget target, BdmvVideoFormat format, BdmvFrameRate rate, int titleCount)
    {
        if (titleCount is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(titleCount));
        }

        var w = new BeWriter();
        w.Ascii("INDX");
        w.Ascii(VersionFor(target));
        int indexesStartAt = w.Position;
        w.U32(0);                    // Indexes_start_address, patched below
        w.U32(0);                    // ExtensionData_start_address: none
        w.Zeros(24);

        // AppInfoBDMV: length 34 = 2 bytes of flags and format + 32 bytes of provider data.
        w.U32(34);
        w.U8(0);                     // initial_output_mode_preference 0 (2D), no stereoscopic content
        w.U8((byte)(((int)format << 4) | (int)rate));
        w.Zeros(32);

        w.PatchU32(indexesStartAt, (uint)w.Position);

        // Indexes(): length, first playback, top menu, title count, titles. Every entry is 12 bytes.
        w.U32((uint)(12 + 12 + 2 + 12 * titleCount));
        WriteHdmvIndexEntry(w, objectNumber: 0, titleAccessType: null);
        WriteHdmvIndexEntry(w, objectNumber: 0, titleAccessType: null);
        w.U16((ushort)titleCount);
        for (int i = 0; i < titleCount; i++)
        {
            WriteHdmvIndexEntry(w, objectNumber: i, titleAccessType: 0);
        }

        return w.ToArray();
    }

    private static void WriteHdmvIndexEntry(BeWriter w, int objectNumber, int? titleAccessType)
    {
        // object_type 01 = HDMV; titles also carry a 2-bit access type, the first-playback and top-menu entries do not.
        uint first = 0x4000_0000u | (uint)((titleAccessType ?? 0) << 28);
        w.U32(first);
        w.U32((uint)objectNumber & 0xFFFF);   // playback_type 00 (movie), 14 reserved bits, id_ref
        w.U32(0);
    }

    // ----- MovieObject.bdmv -----------------------------------------------------------------------------------

    /// <summary>
    /// <c>MovieObject.bdmv</c>: <c>MOBJ</c> + version, extension address (none), padding to byte 40, then
    /// <paramref name="titleCount"/> movie objects of one navigation command each, "play playlist number i".
    /// </summary>
    public static byte[] WriteMovieObject(BdmvDiscTarget target, int titleCount)
    {
        var w = new BeWriter();
        w.Ascii("MOBJ");
        w.Ascii(VersionFor(target));
        w.U32(0);                    // ExtensionData_start_address: none
        w.Zeros(40 - w.Position);

        int lengthAt = w.Position;
        w.U32(0);                    // length of everything after this field, patched below
        w.U32(0);                    // reserved
        w.U16((ushort)titleCount);
        for (int i = 0; i < titleCount; i++)
        {
            w.U16(0);                // resume intention, menu-call mask, title-search mask all 0; 13 reserved bits
            w.U16(1);                // one command
            w.Bytes(PlayPlaylistCommand(i));
        }

        w.PatchU32(lengthAt, (uint)(w.Position - lengthAt - 4));
        return w.ToArray();
    }

    /// <summary>
    /// The 12-byte HDMV command <c>PlayPL(playlist)</c>: one operand, branch group, play sub-group, operand 1 an
    /// immediate value, branch option "play playlist" (0).
    /// </summary>
    public static byte[] PlayPlaylistCommand(int playlistNumber)
    {
        var command = new byte[12];
        command[0] = (1 << 5) | (0 << 3) | 2;    // operand count 1, group BRANCH 0, sub-group PLAY 2
        command[1] = 0x80;                        // operand 1 immediate, operand 2 register, branch option PLAY_PL = 0
        BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(4), (uint)playlistNumber);
        return command;
    }

    // ----- CERTIFICATE/id.bdmv --------------------------------------------------------------------------------

    /// <summary>
    /// <c>CERTIFICATE/id.bdmv</c>: <c>BDID</c> + version, data start (40), extension address (none), padding, then the
    /// 4-byte organisation ID (0) and a 16-byte disc ID taken from <paramref name="discIdSeed"/> so the same input
    /// always gives the same ID.
    /// </summary>
    public static byte[] WriteDiscId(BdmvDiscTarget target, string discIdSeed)
    {
        var w = new BeWriter();
        w.Ascii("BDID");
        w.Ascii(VersionFor(target));
        w.U32(40);
        w.U32(0);
        w.Zeros(40 - w.Position);
        w.U32(0);
        w.Bytes(SHA256.HashData(Encoding.UTF8.GetBytes(discIdSeed)).AsSpan(0, 16).ToArray());
        return w.ToArray();
    }

    // ----- PLAYLIST/xxxxx.mpls --------------------------------------------------------------------------------

    /// <summary>
    /// A movie playlist with one play item that plays <paramref name="clipName"/> from <paramref name="inTime45k"/> to
    /// <paramref name="outTime45k"/>. The item's stream table lists the video, the audio and the subtitle streams; the
    /// playlist marks hold one entry mark at the start and one per chapter interval.
    /// </summary>
    public static byte[] WritePlaylist(
        BdmvDiscTarget target,
        string clipName,
        long inTime45k,
        long outTime45k,
        BdmvClipStreams streams,
        int chapterIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (clipName.Length != 5)
        {
            throw new ArgumentException("A clip name has five characters.", nameof(clipName));
        }
        if (outTime45k <= inTime45k)
        {
            throw new ArgumentException("The play item must end after it starts.", nameof(outTime45k));
        }

        var w = new BeWriter();
        w.Ascii("MPLS");
        w.Ascii(VersionFor(target));
        int listAt = w.Position;
        w.U32(0);                    // PlayList_start_address
        w.U32(0);                    // PlayListMark_start_address
        w.U32(0);                    // ExtensionData_start_address: none
        w.Zeros(20);

        // AppInfoPlayList: length 14.
        w.U32(14);
        w.U8(0);
        w.U8(1);                     // playback type 1: sequential play
        w.U16(0);
        w.Zeros(8);                  // no user operation is prohibited
        w.U16(0);                    // random access allowed, no audio mix, 11 reserved bits

        // PlayList(): length, reserved, play item count, sub path count, the play item.
        w.PatchU32(listAt, (uint)w.Position);
        int playListLengthAt = w.Position;
        w.U32(0);
        w.U16(0);
        w.U16(1);
        w.U16(0);

        int itemLengthAt = w.Position;
        w.U16(0);
        w.Ascii(clipName);
        w.Ascii("M2TS");
        w.U16(0x0001);               // 11 reserved bits, not multi-angle, connection condition 1 (the first item, no join with an earlier one)
        w.U8(0);                     // STC id
        w.U32((uint)inTime45k);
        w.U32((uint)outTime45k);
        w.Zeros(8);                  // UO mask
        w.U8(0);                     // random access allowed
        w.U8(0);                     // still mode: none
        w.U16(0);

        WriteStreamNumberTable(w, streams);

        w.PatchU16(itemLengthAt, (ushort)(w.Position - itemLengthAt - 2));
        w.PatchU32(playListLengthAt, (uint)(w.Position - playListLengthAt - 4));

        // PlayListMark(): one entry mark at the start, then one per chapter interval.
        w.PatchU32(listAt + 4, (uint)w.Position);
        var marks = new List<long> { inTime45k };
        if (chapterIntervalSeconds > 0)
        {
            long step = chapterIntervalSeconds * Clock45k;
            for (long t = inTime45k + step; t < outTime45k - 5 * Clock45k; t += step)
            {
                marks.Add(t);
            }
        }
        int markLengthAt = w.Position;
        w.U32(0);
        w.U16((ushort)marks.Count);
        foreach (long t in marks)
        {
            w.U8(0);
            w.U8(1);                 // mark type 1: entry mark (a chapter)
            w.U16(0);                // play item 0
            w.U32((uint)t);
            w.U16(0xFFFF);           // not tied to one elementary stream
            w.U32(0);
        }
        w.PatchU32(markLengthAt, (uint)(w.Position - markLengthAt - 4));
        return w.ToArray();
    }

    private static void WriteStreamNumberTable(BeWriter w, BdmvClipStreams s)
    {
        int lengthAt = w.Position;
        w.U16(0);
        w.U16(0);                    // reserved
        w.U8(1);                     // primary video
        w.U8(1);                     // primary audio
        w.U8((byte)s.Subtitles.Count);
        w.U8(0);                     // interactive graphics
        w.U8(0);                     // secondary audio
        w.U8(0);                     // secondary video
        w.U8(0);                     // picture-in-picture subtitles
        w.U8(0);                     // Dolby Vision enhancement layer
        w.Zeros(4);

        WriteStreamEntry(w, s.VideoPid);
        w.U8(5);                     // attribute length
        w.U8(0x1B);                  // H.264
        w.U8((byte)(((int)s.VideoFormat << 4) | (int)s.FrameRate));
        w.Zeros(3);

        WriteStreamEntry(w, s.AudioPid);
        w.U8(5);
        w.U8(AudioCodingType(s.AudioCodec));
        w.U8((byte)((s.AudioFormatCode << 4) | 1));     // channel layout, 48 kHz
        w.Ascii(Language(s.AudioLanguage));

        foreach (var (pid, language) in s.Subtitles)
        {
            WriteStreamEntry(w, pid);
            w.U8(5);
            w.U8(0x90);              // presentation graphics
            w.Ascii(Language(language));
            w.U8(0);
        }

        w.PatchU16(lengthAt, (ushort)(w.Position - lengthAt - 2));
    }

    /// <summary>
    /// A stream entry is always 10 bytes: length 9, stream type 1 (an elementary stream of the play item's clip), the
    /// PID and 6 reserved bytes. The fields of the other stream types share that space, so a player may step over
    /// entries by the fixed size rather than by the length byte; a shorter entry misaligns the whole table for it.
    /// </summary>
    private static void WriteStreamEntry(BeWriter w, ushort pid)
    {
        w.U8(StreamEntryLength);
        w.U8(1);                     // stream type 1
        w.U16(pid);
        w.Zeros(6);                  // reserved
    }

    private static byte AudioCodingType(BdmvAudioCodec codec) => codec == BdmvAudioCodec.Lpcm ? (byte)0x80 : (byte)0x81;

    private static string Language(string language) =>
        language is { Length: 3 } && language.All(c => c is >= 'a' and <= 'z') ? language : "und";

    // ----- CLIPINF/xxxxx.clpi ---------------------------------------------------------------------------------

    /// <summary>
    /// Clip information: the clip's size and rate, its ATC/STC sequence (one of each), the program with its streams, and
    /// the random-access map of the video stream.
    /// </summary>
    public static byte[] WriteClipInfo(BdmvDiscTarget target, BdmvClipStreams streams, BdmvStreamFacts facts)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(facts);

        var w = new BeWriter();
        w.Ascii("HDMV");
        w.Ascii(VersionFor(target));
        int addressesAt = w.Position;
        w.Zeros(5 * 4);              // SequenceInfo, ProgramInfo, CPI, ClipMark, ExtensionData start addresses
        w.Zeros(40 - w.Position);

        // ClipInfo
        int clipInfoAt = w.Position;
        w.U32(0);
        w.U16(0);
        w.U8(1);                     // clip stream type 1: movie clip
        w.U8(1);                     // application type 1: main TS of a movie
        w.U32(0);                    // 31 reserved bits, no ATC delta
        // TS_recording_rate is the rate the player must be able to read the clip at, so a Blu-ray clip states the
        // format's maximum transport rate (48 Mbit/s), as reference authoring tools do, not the film's average.
        w.U32(target == BdmvDiscTarget.BluRay ? BluRayMaxRecordingRateBytesPerSecond : facts.RecordingRateBytesPerSecond);
        w.U32((uint)facts.SourcePackets);
        w.Zeros(128);
        w.U16(30);                   // TS type info block: validity flags, format identifier, 9 + 16 bytes reserved
        w.U8(0x80);
        w.Ascii("HDMV");
        w.Zeros(25);
        w.PatchU32(clipInfoAt, (uint)(w.Position - clipInfoAt - 4));

        // SequenceInfo: one ATC sequence holding one STC sequence.
        w.Align4();
        w.PatchU32(addressesAt, (uint)w.Position);
        int seqAt = w.Position;
        w.U32(0);
        w.U8(0);
        w.U8(1);                     // ATC sequences
        w.U32(0);                    // SPN_ATC_start
        w.U8(1);                     // STC sequences in it
        w.U8(0);                     // first STC id
        w.U16(streams.VideoPid);     // the PCR rides on the video PID
        w.U32(0);                    // SPN_STC_start
        w.U32((uint)facts.StartPts45k);
        w.U32((uint)facts.EndPts45k);
        w.PatchU32(seqAt, (uint)(w.Position - seqAt - 4));

        // ProgramInfo: one program with the streams of the clip.
        w.Align4();
        w.PatchU32(addressesAt + 4, (uint)w.Position);
        int progAt = w.Position;
        w.U32(0);
        w.U8(0);
        w.U8(1);                     // programs
        w.U32(0);                    // SPN_program_sequence_start
        w.U16(streams.PmtPid);
        w.U8((byte)(2 + streams.Subtitles.Count));
        w.U8(0);                     // reserved

        // Every StreamCodingInfo is 22 bytes: length 21, five bytes that depend on the stream type, a 12-character
        // ISRC ("0" when there is none) and 4 reserved bytes.
        w.U16(streams.VideoPid);
        w.U8(StreamCodingInfoLength);
        w.U8(0x1B);
        w.U8((byte)(((int)streams.VideoFormat << 4) | (int)streams.FrameRate));
        w.U8(0x30);                  // 16:9
        w.U16(0);                    // no HDR
        WriteNoIsrc(w);

        w.U16(streams.AudioPid);
        w.U8(StreamCodingInfoLength);
        w.U8(AudioCodingType(streams.AudioCodec));
        w.U8((byte)((streams.AudioFormatCode << 4) | 1));
        w.Ascii(Language(streams.AudioLanguage));
        WriteNoIsrc(w);

        foreach (var (pid, language) in streams.Subtitles)
        {
            w.U16(pid);
            w.U8(StreamCodingInfoLength);
            w.U8(0x90);
            w.Ascii(Language(language));
            w.U8(0);
            WriteNoIsrc(w);
        }
        w.PatchU32(progAt, (uint)(w.Position - progAt - 4));

        // CPI: the entry-point map of the video stream.
        w.Align4();
        w.PatchU32(addressesAt + 8, (uint)w.Position);
        WriteCpi(w, streams.VideoPid, facts.EntryPoints);

        // ClipMark: no marks.
        w.Align4();
        w.PatchU32(addressesAt + 12, (uint)w.Position);
        w.U32(0);

        return w.ToArray();
    }

    /// <summary>
    /// The entry-point map: a coarse table (one entry per run of points that share the upper bits of time and packet
    /// number) pointing into a fine table (11 bits of time, 17 bits of packet number). A reader joins the two as
    /// <c>coarse time bits 31..19 + fine time bits 18..8</c> (45 kHz) and <c>coarse packet bits 31..17 + fine packet
    /// bits 16..0</c>.
    /// </summary>
    public static void WriteCpi(BeWriter w, ushort videoPid, IReadOnlyList<BdmvEntryPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var coarse = new List<(int FineId, uint Pts, uint Spn)>();
        var fine = new List<(uint Pts, uint Spn)>();
        long? groupPts = null;
        long? groupSpn = null;
        foreach (var p in points)
        {
            long pts45 = (p.Pts90k >> 1) & 0xFFFF_FFFFL;
            long spn = p.SourcePacketNumber;
            if (groupPts != pts45 >> 19 || groupSpn != spn >> 17)
            {
                coarse.Add((fine.Count, (uint)((pts45 >> 18) & 0x3FFF), (uint)spn));
                groupPts = pts45 >> 19;
                groupSpn = spn >> 17;
            }
            fine.Add(((uint)((pts45 >> 8) & 0x7FF), (uint)(spn & 0x1FFFF)));
        }

        int cpiLengthAt = w.Position;
        w.U32(0);
        w.U16(0x0001);               // 12 reserved bits, CPI type 1: EP map
        int epMapAt = w.Position;
        w.U8(0);
        w.U8(1);                     // one stream PID
        w.U16(videoPid);
        // 10 reserved bits, EP stream type 1 (video) in 4 bits, 16-bit coarse count, 18-bit fine count: 48 bits.
        ulong packed = (1UL << 34) | ((ulong)(uint)coarse.Count << 18) | (uint)fine.Count;
        w.U16((ushort)(packed >> 32));
        w.U32((uint)packed);
        int addressAt = w.Position;
        w.U32(0);

        w.PatchU32(addressAt, (uint)(w.Position - epMapAt));
        w.U32((uint)(4 + 8 * coarse.Count));   // fine table start, relative to this table
        foreach (var (fineId, pts, spn) in coarse)
        {
            w.U32(((uint)fineId << 14) | pts);
            w.U32(spn);
        }
        foreach (var (pts, spn) in fine)
        {
            w.U32((pts << 17) | spn);
        }
        w.PatchU32(cpiLengthAt, (uint)(w.Position - cpiLengthAt - 4));
    }
}

/// <summary>A big-endian byte writer with patching of earlier positions (the files hold many forward addresses and lengths).</summary>
public sealed class BeWriter
{
    private readonly List<byte> _bytes = new();

    /// <summary>Number of bytes written.</summary>
    public int Position => _bytes.Count;

    /// <summary>Writes one byte.</summary>
    public void U8(byte value) => _bytes.Add(value);

    /// <summary>Writes a 16-bit value.</summary>
    public void U16(ushort value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
    }

    /// <summary>Writes a 32-bit value.</summary>
    public void U32(uint value)
    {
        _bytes.Add((byte)(value >> 24));
        _bytes.Add((byte)(value >> 16));
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
    }

    /// <summary>Writes ASCII text.</summary>
    public void Ascii(string text) => _bytes.AddRange(Encoding.ASCII.GetBytes(text));

    /// <summary>Writes raw bytes.</summary>
    public void Bytes(byte[] data) => _bytes.AddRange(data);

    /// <summary>Writes <paramref name="count"/> zero bytes.</summary>
    public void Zeros(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _bytes.Add(0);
        }
    }

    /// <summary>Pads with zeros to a multiple of four bytes.</summary>
    public void Align4()
    {
        while (_bytes.Count % 4 != 0)
        {
            _bytes.Add(0);
        }
    }

    /// <summary>Overwrites a 16-bit value.</summary>
    public void PatchU16(int at, ushort value)
    {
        _bytes[at] = (byte)(value >> 8);
        _bytes[at + 1] = (byte)value;
    }

    /// <summary>Overwrites a 32-bit value.</summary>
    public void PatchU32(int at, uint value)
    {
        _bytes[at] = (byte)(value >> 24);
        _bytes[at + 1] = (byte)(value >> 16);
        _bytes[at + 2] = (byte)(value >> 8);
        _bytes[at + 3] = (byte)value;
    }

    /// <summary>The bytes written.</summary>
    public byte[] ToArray() => _bytes.ToArray();
}
