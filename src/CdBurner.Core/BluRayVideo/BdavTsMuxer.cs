using System.Buffers.Binary;

namespace CdBurner.Core.BluRayVideo;

/// <summary>
/// Turns an ordinary MPEG transport stream (188-byte packets, as FFmpeg writes it) into a BDAV stream file: every
/// packet gets a 4-byte header with its arrival time on the 27 MHz clock (30 bits), giving the 192-byte source packets
/// of <c>.m2ts</c>. On the way it writes a Blu-ray program map (H.264 0x1B, audio 0x81 or 0x80, subtitles 0x90), drops
/// every PID but the video and audio ones, inserts the subtitle display sets as PES packets in time order, and collects
/// what the clip information needs: presentation start and end, the IDR entry points and the average rate.
/// </summary>
public static class BdavTsMuxer
{
    /// <summary>Bytes in a transport packet.</summary>
    public const int TsPacketSize = 188;

    /// <summary>Bytes in a source packet: 4-byte arrival header + transport packet.</summary>
    public const int SourcePacketSize = 192;

    /// <summary>Source packets in one aligned unit (6,144 bytes), the size granule of a stream file.</summary>
    public const int AlignedUnitPackets = 32;

    private const long Pcr30BitMask = (1L << 30) - 1;

    private sealed class Packet(byte[] data, long index)
    {
        public byte[] Data { get; } = data;
        public long Index { get; } = index;
    }

    private sealed class SubtitleQueue(ushort pid, IReadOnlyList<PgsDisplaySet> sets)
    {
        public ushort Pid { get; } = pid;
        public Queue<PgsDisplaySet> Sets { get; } = new(sets);
        public int ContinuityCounter { get; set; }
    }

    /// <summary>
    /// Reads <paramref name="tsInput"/> to its end and writes the BDAV stream to <paramref name="bdavOutput"/>.
    /// </summary>
    /// <param name="tsInput">188-byte transport packets: one H.264 video stream on <see cref="BdmvClipStreams.VideoPid"/> and one audio stream on <see cref="BdmvClipStreams.AudioPid"/>, with the PCR on the video PID.</param>
    /// <param name="bdavOutput">Receives the 192-byte source packets.</param>
    /// <param name="streams">PIDs and attributes to write into the program map.</param>
    /// <param name="subtitleTracks">One list of display sets per entry of <see cref="BdmvClipStreams.Subtitles"/>.</param>
    /// <param name="frameRate">Frame rate, for the end time of the last picture.</param>
    /// <exception cref="InvalidDataException">The input is not a transport stream, has no PCR, no IDR entry point or a PCR that runs backwards.</exception>
    public static BdmvStreamFacts Mux(
        Stream tsInput,
        Stream bdavOutput,
        BdmvClipStreams streams,
        IReadOnlyList<IReadOnlyList<PgsDisplaySet>> subtitleTracks,
        BdmvFrameRate frameRate)
    {
        ArgumentNullException.ThrowIfNull(tsInput);
        ArgumentNullException.ThrowIfNull(bdavOutput);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(subtitleTracks);
        if (subtitleTracks.Count != streams.Subtitles.Count)
        {
            throw new ArgumentException("There must be one display-set list per subtitle stream.", nameof(subtitleTracks));
        }

        var queues = streams.Subtitles.Select((s, i) => new SubtitleQueue(s.Pid, subtitleTracks[i])).ToList();
        var counters = new Dictionary<ushort, int>();
        int patCounter = 0, pmtCounter = 0;
        byte[] pat = BuildPat(streams.PmtPid);
        byte[] pmt = BuildPmt(streams);

        var pending = new List<Packet>();
        var entryPoints = new List<BdmvEntryPoint>();
        long index = 0;
        long minPts = long.MaxValue, maxPts = long.MinValue, curPts = long.MinValue;
        long? firstPcr = null, lastPcr = null;
        long anchorIndex = -1, anchorPcr = 0;
        bool haveAnchor = false;
        double lastSlope = 0;
        byte[] ioBuffer = new byte[TsPacketSize];

        long pcrSeen = 0;
        void Emit(byte[] ts)
        {
            pending.Add(new Packet(ts, index++));
        }

        void Flush(long fromIdx, long toIdx, long fromPcr, double slope)
        {
            // Writes the pending packets with index in [fromIdx, toIdx) using fromPcr + (i - anchorIndex) * slope.
            int written = 0;
            foreach (var p in pending)
            {
                if (p.Index >= toIdx)
                {
                    break;
                }
                long ats = (fromPcr + (long)Math.Round((p.Index - fromIdx) * slope)) & Pcr30BitMask;
                WriteSource(bdavOutput, ats, p.Data);
                written++;
            }
            pending.RemoveRange(0, written);
        }

        void InjectSubtitles(long videoPts)
        {
            foreach (var q in queues)
            {
                while (q.Sets.Count > 0)
                {
                    var set = q.Sets.Peek();
                    long firstTime = set.Segments.Min(s => s.Dts90k > 0 ? s.Dts90k : s.Pts90k);
                    if (firstTime - 90_000 > videoPts)
                    {
                        break;
                    }
                    q.Sets.Dequeue();
                    foreach (var seg in set.Segments)
                    {
                        foreach (byte[] ts in PackSegment(seg, q))
                        {
                            Emit(ts);
                        }
                    }
                }
            }
        }

        // The PAT and program map lead the stream so that subtitle packets never come before the table that names them.
        Emit(PsiPacket(0, pat, ref patCounter));
        Emit(PsiPacket(streams.PmtPid, pmt, ref pmtCounter));

        while (true)
        {
            int read = ReadFully(tsInput, ioBuffer);
            if (read == 0)
            {
                break;
            }
            if (read < TsPacketSize)
            {
                throw new InvalidDataException("The transport stream ends inside a packet.");
            }
            if (ioBuffer[0] != 0x47)
            {
                throw new InvalidDataException($"Lost transport-stream sync after packet {index}.");
            }

            int pid = ((ioBuffer[1] & 0x1F) << 8) | ioBuffer[2];
            if (pid == 0)
            {
                Emit(PsiPacket(0, pat, ref patCounter));
                continue;
            }
            if (pid == streams.PmtPid)
            {
                Emit(PsiPacket(streams.PmtPid, pmt, ref pmtCounter));
                continue;
            }
            if (pid != streams.VideoPid && pid != streams.AudioPid)
            {
                continue;
            }

            byte[] ts = (byte[])ioBuffer.Clone();
            bool payloadStart = (ts[1] & 0x40) != 0;
            int adaptation = (ts[3] >> 4) & 0x3;
            int payloadAt = 4;
            bool randomAccess = false;
            long? pcr = null;
            if ((adaptation & 0x2) != 0)
            {
                int afLength = ts[4];
                if (afLength > 0 && 5 + afLength <= TsPacketSize)
                {
                    randomAccess = (ts[5] & 0x40) != 0;
                    if ((ts[5] & 0x10) != 0 && afLength >= 7)
                    {
                        long b = ((long)ts[6] << 25) | ((long)ts[7] << 17) | ((long)ts[8] << 9) | ((long)ts[9] << 1) | (uint)(ts[10] >> 7);
                        long ext = ((ts[10] & 0x01) << 8) | ts[11];
                        pcr = b * 300 + ext;
                    }
                }
                payloadAt = 5 + afLength;
            }

            if (pid == streams.VideoPid && payloadStart && (adaptation & 0x1) != 0 && payloadAt + 14 <= TsPacketSize
                && ts[payloadAt] == 0 && ts[payloadAt + 1] == 0 && ts[payloadAt + 2] == 1)
            {
                if ((ts[payloadAt + 7] & 0x80) != 0)
                {
                    long pts = ParseTimestamp(ts, payloadAt + 9);
                    minPts = Math.Min(minPts, pts);
                    maxPts = Math.Max(maxPts, pts);
                    curPts = pts;
                    InjectSubtitles(pts);
                    if (randomAccess)
                    {
                        entryPoints.Add(new BdmvEntryPoint(pts, index));
                    }
                }
            }

            long thisIndex = index;
            Emit(ts);

            if (pcr is long pcrValue && pid == streams.VideoPid)
            {
                if (lastPcr is long previous && pcrValue < previous)
                {
                    throw new InvalidDataException("The PCR of the transport stream runs backwards (a discontinuity); the clip cannot be written as one stream.");
                }
                firstPcr ??= pcrValue;
                lastPcr = pcrValue;
                pcrSeen++;
                if (!haveAnchor)
                {
                    anchorIndex = thisIndex;
                    anchorPcr = pcrValue;
                    haveAnchor = true;
                }
                else if (thisIndex > anchorIndex)
                {
                    double slope = (double)(pcrValue - anchorPcr) / (thisIndex - anchorIndex);
                    if (lastSlope == 0 && pcrSeen == 2)
                    {
                        // Packets before the first PCR run on the same clock, backwards.
                        FlushBefore(pending, bdavOutput, anchorIndex, anchorPcr, slope);
                    }
                    lastSlope = slope;
                    Flush(anchorIndex, thisIndex, anchorPcr, slope);
                    anchorIndex = thisIndex;
                    anchorPcr = pcrValue;
                }
            }
        }

        if (!haveAnchor)
        {
            throw new InvalidDataException("The transport stream carries no PCR on the video PID.");
        }
        if (entryPoints.Count == 0)
        {
            throw new InvalidDataException("The video stream has no random-access (IDR) picture.");
        }
        if (minPts == long.MaxValue)
        {
            throw new InvalidDataException("The video stream carries no presentation time stamps.");
        }

        // A stream file is a whole number of aligned units of 32 source packets (6,144 bytes); null packets fill the last one.
        while (index % AlignedUnitPackets != 0)
        {
            var filler = new byte[TsPacketSize];
            Array.Fill(filler, (byte)0xFF);
            filler[0] = 0x47;
            filler[1] = 0x1F;
            filler[2] = 0xFF;
            filler[3] = 0x10;
            Emit(filler);
        }

        // The tail after the last PCR keeps the last slope (or a nominal 1 Mbit/s-free 48 Mbit/s mux when only one PCR exists).
        double tailSlope = lastSlope > 0 ? lastSlope : (double)(SourcePacketSize * 8) * 27_000_000 / 48_000_000;
        if (pending.Count > 0 && pending[0].Index < anchorIndex)
        {
            FlushBefore(pending, bdavOutput, anchorIndex, anchorPcr, tailSlope);
        }
        Flush(anchorIndex, long.MaxValue, anchorPcr, tailSlope);
        bdavOutput.Flush();

        long packets = index;
        double seconds = firstPcr is long f && lastPcr is long l && l > f
            ? (l - f) / 27_000_000.0
            : (maxPts - minPts + 90_000) / 90_000.0;
        uint rate = (uint)Math.Min(uint.MaxValue, Math.Ceiling(packets * (double)SourcePacketSize / Math.Max(seconds, 1.0 / 90_000)));

        long start45 = minPts >> 1;
        long end45 = (maxPts >> 1) + BdmvBinaryWriters.FrameDuration45k(frameRate);
        return new BdmvStreamFacts(packets, start45, end45, rate, entryPoints);
    }

    /// <summary>
    /// Writes the pending packets that come before <paramref name="anchorIndex"/> counting backwards from the anchor's
    /// PCR at <paramref name="slope"/> per packet (used for packets that precede the first PCR).
    /// </summary>
    private static void FlushBefore(List<Packet> pending, Stream output, long anchorIndex, long anchorPcr, double slope)
    {
        int written = 0;
        foreach (var p in pending)
        {
            if (p.Index >= anchorIndex)
            {
                break;
            }
            long ats = (anchorPcr + (long)Math.Round((p.Index - anchorIndex) * slope)) & Pcr30BitMask;
            WriteSource(output, ats, p.Data);
            written++;
        }
        pending.RemoveRange(0, written);
    }

    private static void WriteSource(Stream output, long ats, byte[] ts)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(ats & Pcr30BitMask));   // copy permission 00 + 30-bit arrival time
        output.Write(header);
        output.Write(ts);
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>
    /// The presentation time stamp (90 kHz) of the first video picture in a seekable transport stream, or null when
    /// there is none. The stream position is put back where it was. FFmpeg starts its time stamps a little after zero,
    /// and subtitle times (which count from the start of the movie) must be moved by the same amount.
    /// </summary>
    public static long? FindFirstVideoPts(Stream tsInput, ushort videoPid)
    {
        ArgumentNullException.ThrowIfNull(tsInput);
        long origin = tsInput.Position;
        try
        {
            byte[] packet = new byte[TsPacketSize];
            while (ReadFully(tsInput, packet) == TsPacketSize)
            {
                if (packet[0] != 0x47)
                {
                    return null;
                }
                int pid = ((packet[1] & 0x1F) << 8) | packet[2];
                if (pid != videoPid || (packet[1] & 0x40) == 0 || ((packet[3] >> 4) & 1) == 0)
                {
                    continue;
                }
                int payloadAt = 4 + (((packet[3] >> 5) & 1) != 0 ? 1 + packet[4] : 0);
                if (payloadAt + 14 <= TsPacketSize && packet[payloadAt] == 0 && packet[payloadAt + 1] == 0 && packet[payloadAt + 2] == 1
                    && (packet[payloadAt + 7] & 0x80) != 0)
                {
                    return ParseTimestamp(packet, payloadAt + 9);
                }
            }
            return null;
        }
        finally
        {
            tsInput.Position = origin;
        }
    }

    /// <summary>The 33-bit time stamp in the 5 bytes at <paramref name="at"/>.</summary>
    internal static long ParseTimestamp(byte[] b, int at) =>
        ((long)(b[at] & 0x0E) << 29) | ((long)b[at + 1] << 22) | ((long)(b[at + 2] & 0xFE) << 14) | ((long)b[at + 3] << 7) | (long)(b[at + 4] >> 1);

    // ----- tables -----------------------------------------------------------------------------------------------

    private static byte[] BuildPat(ushort pmtPid)
    {
        var s = new List<byte> { 0x00, 0xB0, 0x0D, 0x00, 0x00, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid };
        AppendCrc(s);
        return s.ToArray();
    }

    private static byte[] BuildPmt(BdmvClipStreams streams)
    {
        var body = new List<byte>
        {
            0x00, 0x01, 0xC1, 0x00, 0x00,                                   // program 1, version 0, current, section 0 of 0
            (byte)(0xE0 | (streams.VideoPid >> 8)), (byte)streams.VideoPid, // PCR PID
            0xF0, 0x06, 0x05, 0x04, (byte)'H', (byte)'D', (byte)'M', (byte)'V', // program info: registration descriptor "HDMV"
        };
        void Es(byte type, ushort pid)
        {
            body.Add(type);
            body.Add((byte)(0xE0 | (pid >> 8)));
            body.Add((byte)pid);
            body.Add(0xF0);
            body.Add(0x00);
        }
        Es(0x1B, streams.VideoPid);
        Es(streams.AudioCodec == BdmvAudioCodec.Lpcm ? (byte)0x80 : (byte)0x81, streams.AudioPid);
        foreach (var (pid, _) in streams.Subtitles)
        {
            Es(0x90, pid);
        }

        int sectionLength = body.Count + 4;
        var s = new List<byte> { 0x02, (byte)(0xB0 | (sectionLength >> 8)), (byte)sectionLength };
        s.AddRange(body);
        AppendCrc(s);
        return s.ToArray();
    }

    /// <summary>Appends the MPEG-2 CRC-32 (polynomial 0x04C11DB7, start value all ones, no final inversion).</summary>
    internal static void AppendCrc(List<byte> section)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte b in section)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
            }
        }
        section.Add((byte)(crc >> 24));
        section.Add((byte)(crc >> 16));
        section.Add((byte)(crc >> 8));
        section.Add((byte)crc);
    }

    private static byte[] PsiPacket(ushort pid, byte[] section, ref int counter)
    {
        var ts = new byte[TsPacketSize];
        ts[0] = 0x47;
        ts[1] = (byte)(0x40 | (pid >> 8));
        ts[2] = (byte)pid;
        ts[3] = (byte)(0x10 | (counter & 0x0F));
        counter++;
        ts[4] = 0;                                  // pointer field
        section.CopyTo(ts, 5);
        Array.Fill(ts, (byte)0xFF, 5 + section.Length, TsPacketSize - 5 - section.Length);
        return ts;
    }

    // ----- subtitle PES -----------------------------------------------------------------------------------------

    private static IEnumerable<byte[]> PackSegment(PgsSegment segment, SubtitleQueue queue)
    {
        bool hasDts = segment.Type == PgsSubtitleEncoder.CompositionSegment;
        int headerLength = hasDts ? 10 : 5;
        int payloadLength = 3 + segment.Body.Length;
        var pes = new List<byte>(9 + headerLength + payloadLength)
        {
            0x00, 0x00, 0x01, 0xBD,
            (byte)((3 + headerLength + payloadLength) >> 8), (byte)(3 + headerLength + payloadLength),
            0x81,
            (byte)(hasDts ? 0xC0 : 0x80),
            (byte)headerLength,
        };
        AppendTimestamp(pes, hasDts ? 0x3 : 0x2, segment.Pts90k);
        if (hasDts)
        {
            AppendTimestamp(pes, 0x1, segment.Dts90k);
        }
        pes.Add(segment.Type);
        pes.Add((byte)(segment.Body.Length >> 8));
        pes.Add((byte)segment.Body.Length);
        pes.AddRange(segment.Body);

        int offset = 0;
        bool first = true;
        while (offset < pes.Count)
        {
            var ts = new byte[TsPacketSize];
            int room = TsPacketSize - 4;
            int take = Math.Min(room, pes.Count - offset);
            ts[0] = 0x47;
            ts[1] = (byte)((first ? 0x40 : 0x00) | (queue.Pid >> 8));
            ts[2] = (byte)queue.Pid;
            int fill = room - take;
            if (fill == 0)
            {
                ts[3] = (byte)(0x10 | (queue.ContinuityCounter & 0x0F));
                pes.CopyTo(offset, ts, 4, take);
            }
            else
            {
                // Short last packet: an adaptation field of stuffing bytes makes it 188 bytes.
                ts[3] = (byte)(0x30 | (queue.ContinuityCounter & 0x0F));
                ts[4] = (byte)(fill - 1);
                if (fill > 1)
                {
                    ts[5] = 0x00;
                    Array.Fill(ts, (byte)0xFF, 6, fill - 2);
                }
                pes.CopyTo(offset, ts, 4 + fill, take);
            }
            queue.ContinuityCounter++;
            offset += take;
            first = false;
            yield return ts;
        }
    }

    private static void AppendTimestamp(List<byte> target, int prefix, long value)
    {
        value &= 0x1_FFFF_FFFFL;
        target.Add((byte)((prefix << 4) | (int)((value >> 29) & 0x0E) | 1));
        target.Add((byte)(value >> 22));
        target.Add((byte)(((value >> 14) & 0xFE) | 1));
        target.Add((byte)(value >> 7));
        target.Add((byte)(((value << 1) & 0xFE) | 1));
    }
}
