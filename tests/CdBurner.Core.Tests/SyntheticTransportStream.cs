namespace CdBurner.Core.Tests;

/// <summary>A small transport stream built by hand, standing in for FFmpeg's output: known picture times, key frames, PCRs, an audio stream, tables and a foreign PID.</summary>
internal static class SyntheticTransportStream
{
    /// <summary>Presentation time of picture <paramref name="i"/> (90 kHz), starting at one second, 24000/1001 fps.</summary>
    public static long PictureTime90k(int i) => 90_000 + (long)Math.Round(i * 3753.75);

    public static byte[] Create(int frames, int keyInterval = 24)
    {
        var rng = new Random(5);
        var packets = new List<byte[]>();
        var cc = new Dictionary<int, int>();
        long clock = 0;
        long audioPts = 90_000;
        int audioFrames = 0;

        byte Cc(int pid) { cc.TryGetValue(pid, out int v); cc[pid] = (v + 1) & 0xF; return (byte)v; }
        void Section(int pid, byte[] section)
        {
            var ts = new byte[188];
            Array.Fill(ts, (byte)0xFF);
            ts[0] = 0x47; ts[1] = (byte)(0x40 | (pid >> 8)); ts[2] = (byte)pid; ts[3] = (byte)(0x10 | Cc(pid)); ts[4] = 0;
            section.CopyTo(ts, 5);
            packets.Add(ts);
            clock += 4061;
        }
        void Tables()
        {
            var pat = new List<byte> { 0x00, 0xB0, 0x0D, 0x00, 0x00, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xE1, 0x00 };
            AddCrc(pat);
            Section(0, pat.ToArray());
            var pmt = new List<byte>
            {
                0x02, 0xB0, 0x00, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xF0, 0x11, 0xF0, 0x00,
                0x1B, 0xF0, 0x11, 0xF0, 0x00,        // video on 0x1011
                0x06, 0xF1, 0x00, 0xF0, 0x00,        // audio on 0x1100 as private data (the way a generic muxer marks AC-3)
            };
            pmt[2] = (byte)(pmt.Count - 3 + 4);
            AddCrc(pmt);
            Section(0x100, pmt.ToArray());
            Section(0x11, new byte[] { 0x42, 0xF0, 0x05, 0, 1, 0xC1, 0, 0, 0, 0, 0, 0 });   // a service description table: foreign PID
        }

        void Pes(int pid, byte streamId, long pts, byte[] payload, bool random, bool pcr)
        {
            var pes = new List<byte> { 0, 0, 1, streamId };
            int length = streamId == 0xE0 ? 0 : 3 + 5 + payload.Length;
            pes.Add((byte)(length >> 8)); pes.Add((byte)length);
            pes.Add(0x84); pes.Add(0x80); pes.Add(5);
            pes.AddRange(Stamp(0x2, pts));
            pes.AddRange(payload);

            int offset = 0;
            bool first = true;
            while (offset < pes.Count)
            {
                var ts = new byte[188];
                ts[0] = 0x47;
                ts[1] = (byte)((first ? 0x40 : 0) | (pid >> 8));
                ts[2] = (byte)pid;
                bool withPcr = first && pcr;
                int afLen = first ? (withPcr ? 7 : (random ? 1 : -1)) : -1;
                int header = 4 + (afLen >= 0 ? 1 + afLen : 0);
                int room = 188 - header;
                int take = Math.Min(room, pes.Count - offset);
                int stuffing = room - take;
                if (stuffing > 0)
                {
                    afLen = afLen < 0 ? stuffing - 1 : afLen + stuffing;   // a new adaptation field spends one byte on its length
                    header = 4 + 1 + afLen;
                    room = 188 - header;
                    take = Math.Min(room, pes.Count - offset);
                }
                ts[3] = (byte)((afLen >= 0 ? 0x30 : 0x10) | Cc(pid));
                if (afLen >= 0)
                {
                    ts[4] = (byte)afLen;
                    if (afLen > 0)
                    {
                        byte flags = 0;
                        if (first && random) flags |= 0x40;
                        if (withPcr) flags |= 0x10;
                        ts[5] = flags;
                        int p = 6;
                        if (withPcr)
                        {
                            long b = clock / 300, e = clock % 300;
                            ts[p++] = (byte)(b >> 25); ts[p++] = (byte)(b >> 17); ts[p++] = (byte)(b >> 9); ts[p++] = (byte)(b >> 1);
                            ts[p++] = (byte)(((b & 1) << 7) | 0x7E | (e >> 8)); ts[p++] = (byte)e;
                        }
                        while (p < 5 + afLen) ts[p++] = 0xFF;
                    }
                }
                pes.CopyTo(offset, ts, header, take);
                offset += take;
                first = false;
                packets.Add(ts);
                clock += 4061;
            }
        }

        // The byte clock waits for each picture's slot: PCR runs 0.6 s ahead of the presentation time.
        clock = 27_000_000L * 4 / 10;
        Tables();
        for (int i = 0; i < frames; i++)
        {
            long pts = PictureTime90k(i);
            clock = Math.Max(clock, (pts - 54_000) * 300);
            if (i % 12 == 0)
            {
                Tables();
            }
            bool key = keyInterval > 0 && i % keyInterval == 0;
            int size = key ? 40_000 : 6_000;
            var payload = new byte[size];
            for (int k = 0; k < size; k++) payload[k] = (byte)rng.Next(1, 256);
            payload[0] = 0; payload[1] = 0; payload[2] = 0; payload[3] = 1; payload[4] = 0x09; payload[5] = 0xF0;
            Pes(0x1011, 0xE0, pts, payload, key, pcr: true);

            long nextPts = PictureTime90k(i + 1);
            while (audioPts < nextPts)
            {
                var audio = new byte[1536];
                for (int k = 0; k < audio.Length; k++) audio[k] = (byte)rng.Next(1, 256);
                audio[0] = 0x0B; audio[1] = 0x77;
                Pes(0x1100, 0xBD, audioPts, audio, random: false, pcr: false);
                audioPts += 2880;
                audioFrames++;
            }
        }
        _ = audioFrames;
        return packets.SelectMany(p => p).ToArray();
    }

    private static byte[] Stamp(int prefix, long value) =>
    [
        (byte)((prefix << 4) | (int)((value >> 29) & 0x0E) | 1),
        (byte)(value >> 22),
        (byte)(((value >> 14) & 0xFE) | 1),
        (byte)(value >> 7),
        (byte)(((value << 1) & 0xFE) | 1),
    ];

    private static void AddCrc(List<byte> section)
    {
        uint crc = BdmvStructuralValidator.Crc32Mpeg(section.ToArray());
        // The CRC of a section followed by its own CRC is zero; append the CRC computed over the body.
        section.Add((byte)(crc >> 24)); section.Add((byte)(crc >> 16)); section.Add((byte)(crc >> 8)); section.Add((byte)crc);
    }
}
