using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>One AC-3 audio stream input to <see cref="Mpeg2Muxer"/> (substream 0x80..0x87).</summary>
public sealed record Mpeg2AudioMuxStream(
    int StreamIndex,
    string Language = "eng",
    int Channels = 2,
    ReadOnlyMemory<byte> Ac3Payload = default);

/// <summary>One SPU subpicture stream input to <see cref="Mpeg2Muxer"/> (substream 0x20..0x27).</summary>
public sealed record Mpeg2SubtitleMuxStream(
    int StreamIndex,
    string Language = "eng",
    IReadOnlyList<(long Pts, byte[] Spu)>? Units = null);

/// <summary>Mux configuration for <see cref="Mpeg2Muxer"/>.</summary>
public sealed record Mpeg2MuxOptions(
    DvdVideoStandard Standard = DvdVideoStandard.Ntsc,
    DvdAspectRatio AspectRatio = DvdAspectRatio.Widescreen16x9,
    int VideoSectorCount = 32,
    int ChapterIntervalSeconds = 0,
    int VobId = 1,
    int CellId = 1);

/// <summary>Summary of elementary and private substreams found in an MPEG-2 Program Stream VOB.</summary>
public sealed record Mpeg2MuxInspection(
    int TotalSectors,
    int NavPackCount,
    int VideoPackCount,
    IReadOnlyList<byte> AudioSubstreamIds,
    IReadOnlyList<byte> SubtitleSubstreamIds);

/// <summary>
/// Pure C# DVD-Video MPEG-2 Program Stream pack muxer and inspector. Produces 2,048-byte DVD VOB packs
/// carrying NAV packs, MPEG-2 video (<c>0xE0</c>), up to 8 AC-3 audio streams (<c>0xBD</c> substreams
/// <c>0x80..0x87</c>), and up to 8 SPU subpicture streams (<c>0xBD</c> substreams <c>0x20..0x27</c>).
/// </summary>
public static class Mpeg2Muxer
{
    public const int SectorSize = OpticalConstants.DataSectorSize;
    public const int MaxAudioStreams = DvdVideoProject.MaxAudioTracksPerTitle;
    public const int MaxSubtitleStreams = DvdVideoProject.MaxSubtitleTracksPerTitle;
    public const byte Ac3SubstreamBase = 0x80;
    public const byte SpuSubstreamBase = 0x20;

    /// <summary>
    /// Muxes MPEG-2 video, up to 8 AC-3 audio streams (<c>0x80..0x87</c>), and up to 8 SPU subpicture streams
    /// (<c>0x20..0x27</c>) into a DVD-compliant MPEG-2 Program Stream VOB buffer with patched NAV packs.
    /// </summary>
    public static byte[] MuxProgramStream(
        IReadOnlyList<Mpeg2AudioMuxStream>? audioStreams,
        IReadOnlyList<Mpeg2SubtitleMuxStream>? subtitleStreams,
        Mpeg2MuxOptions? options = null)
    {
        options ??= new Mpeg2MuxOptions();
        var audios = (audioStreams ?? Array.Empty<Mpeg2AudioMuxStream>()).Take(MaxAudioStreams).ToList();
        var subtitles = (subtitleStreams ?? Array.Empty<Mpeg2SubtitleMuxStream>()).Take(MaxSubtitleStreams).ToList();

        foreach (var audio in audios)
        {
            if (audio.StreamIndex is < 0 or >= MaxAudioStreams)
            {
                throw new ArgumentOutOfRangeException(nameof(audioStreams), $"Audio stream index {audio.StreamIndex} must be in 0..{MaxAudioStreams - 1}.");
            }
        }
        foreach (var sub in subtitles)
        {
            if (sub.StreamIndex is < 0 or >= MaxSubtitleStreams)
            {
                throw new ArgumentOutOfRangeException(nameof(subtitleStreams), $"Subtitle stream index {sub.StreamIndex} must be in 0..{MaxSubtitleStreams - 1}.");
            }
        }

        // Default to 1 AC-3 audio stream (0x80) when none supplied so DVD VOBs always carry stream 0 audio unless explicitly empty.
        if (audios.Count == 0 && audioStreams == null)
        {
            audios.Add(new Mpeg2AudioMuxStream(0, "eng", 2));
        }

        using var ms = new MemoryStream();
        int targetVideoSectors = Math.Max(8, options.VideoSectorCount);
        int vobuCount = Math.Max(1, (targetVideoSectors + 7) / 8);
        bool pal = options.Standard == DvdVideoStandard.Pal;
        long frameDuration = pal ? 3600 : 3003;
        long vobuPtsSpan = pal ? 45_000 : 45_045; // ~0.5 s per VOBU

        // Collect all SPU units per stream so each is emitted with its PTS.
        var pendingSubUnits = new List<(int StreamIndex, long Pts, byte[] Spu)>();
        foreach (var sub in subtitles)
        {
            if (sub.Units is { Count: > 0 })
            {
                foreach (var (pts, spu) in sub.Units)
                {
                    if (spu.Length > 0)
                    {
                        pendingSubUnits.Add((sub.StreamIndex, pts, spu));
                    }
                }
            }
            else
            {
                // Placeholder empty/minimal 1x2 SPU so stream substream ID 0x20+idx is explicitly present in the PS.
                byte[] minimalSpu = DvdSubtitleWriter.EncodeSpu(new byte[4], 2, 2, 0, 0, TimeSpan.FromSeconds(1));
                pendingSubUnits.Add((sub.StreamIndex, 90_000L, minimalSpu));
            }
        }
        pendingSubUnits.Sort((a, b) => a.Pts.CompareTo(b.Pts));
        int nextSubIdx = 0;

        byte[] packBuf = new byte[SectorSize];
        for (int v = 0; v < vobuCount; v++)
        {
            long vobuStartPts = 90_000L + v * vobuPtsSpan;
            long scr = Math.Max(0, vobuStartPts - 9_000);

            // 1. NAV pack at start of VOBU
            DvdNavPackWriter.FormatNavPack(packBuf);
            ms.Write(packBuf, 0, SectorSize);

            // 2. Video pack(s) with Sequence Header + I/P frames
            int vidPacks = Math.Max(2, targetVideoSectors / vobuCount);
            for (int k = 0; k < vidPacks; k++)
            {
                WriteVideoPack(packBuf, scr + k * frameDuration, vobuStartPts + k * frameDuration,
                    includeSequenceHeader: k == 0, isIntra: k == 0, pal, options.AspectRatio);
                ms.Write(packBuf, 0, SectorSize);
            }

            // 3. One AC-3 Private Stream 1 pack per audio stream (substream 0x80 + StreamIndex)
            for (int a = 0; a < audios.Count; a++)
            {
                var audio = audios[a];
                WriteAc3AudioPack(packBuf, scr + a * 300, vobuStartPts, (byte)(Ac3SubstreamBase + audio.StreamIndex), audio.Ac3Payload.Span);
                ms.Write(packBuf, 0, SectorSize);
            }

            // 4. Emit any SPU packets due by the end of this VOBU (and all remaining on last VOBU)
            long vobuEndPts = vobuStartPts + vobuPtsSpan;
            while (nextSubIdx < pendingSubUnits.Count &&
                   (v == vobuCount - 1 || pendingSubUnits[nextSubIdx].Pts < vobuEndPts))
            {
                var unit = pendingSubUnits[nextSubIdx++];
                WriteSpuPacks(ms, (byte)(SpuSubstreamBase + unit.StreamIndex), unit.Spu, unit.Pts);
            }
        }

        // Pad up to DVD minimum 16 sectors if needed
        while (ms.Length < 16 * SectorSize)
        {
            WriteVideoPack(packBuf, 90_000, 90_000, includeSequenceHeader: false, isIntra: false, pal, options.AspectRatio);
            ms.Write(packBuf, 0, SectorSize);
        }

        ms.Position = 0;
        DvdNavPackWriter.Patch(ms, options.VobId, options.CellId, chapterSeconds: Math.Max(0, options.ChapterIntervalSeconds));
        return ms.ToArray();
    }

    /// <summary>Inspects an MPEG-2 Program Stream VOB and reports distinct AC-3 and SPU substream IDs.</summary>
    public static Mpeg2MuxInspection InspectProgramStream(ReadOnlySpan<byte> vobBytes)
    {
        int totalSectors = vobBytes.Length / SectorSize;
        int navCount = 0;
        int videoCount = 0;
        var audioIds = new SortedSet<byte>();
        var spuIds = new SortedSet<byte>();

        for (int s = 0; s < totalSectors; s++)
        {
            var pack = vobBytes.Slice(s * SectorSize, SectorSize);
            if (DvdNavPackWriter.IsNavPack(pack))
            {
                navCount++;
                continue;
            }
            if (pack[0] != 0 || pack[1] != 0 || pack[2] != 1 || pack[3] != 0xBA)
            {
                continue;
            }

            int p = 14 + (pack[13] & 7);
            while (p + 9 <= SectorSize && pack[p] == 0 && pack[p + 1] == 0 && pack[p + 2] == 1)
            {
                byte streamId = pack[p + 3];
                int len = BinaryPrimitives.ReadUInt16BigEndian(pack[(p + 4)..]);
                int end = Math.Min(SectorSize, p + 6 + len);

                if (streamId == 0xE0)
                {
                    videoCount++;
                }
                else if (streamId == 0xBD && (pack[p + 6] & 0xC0) == 0x80)
                {
                    int payload = p + 9 + pack[p + 8];
                    if (payload < end)
                    {
                        byte subId = pack[payload];
                        if (subId is >= 0x80 and <= 0x87)
                        {
                            audioIds.Add(subId);
                        }
                        else if (subId is >= 0x20 and <= 0x27)
                        {
                            spuIds.Add(subId);
                        }
                    }
                }
                p = end;
            }
        }

        return new Mpeg2MuxInspection(totalSectors, navCount, videoCount, audioIds.ToList(), spuIds.ToList());
    }

    /// <summary>Inspects an MPEG-2 Program Stream VOB stream from the beginning.</summary>
    public static Mpeg2MuxInspection InspectProgramStream(Stream vobStream)
    {
        ArgumentNullException.ThrowIfNull(vobStream);
        byte[] buffer = new byte[vobStream.Length];
        vobStream.Position = 0;
        vobStream.ReadExactly(buffer);
        return InspectProgramStream(buffer);
    }

    private static void WriteVideoPack(
        Span<byte> pack,
        long scr,
        long pts,
        bool includeSequenceHeader,
        bool isIntra,
        bool pal,
        DvdAspectRatio aspect)
    {
        pack.Clear();
        WritePackHeader(pack, scr);
        int p = 14;
        pack[p] = 0x00;
        pack[p + 1] = 0x00;
        pack[p + 2] = 0x01;
        pack[p + 3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(pack[(p + 4)..], (ushort)(SectorSize - p - 6));
        pack[p + 6] = 0x80;
        pack[p + 7] = 0x80; // PTS present
        pack[p + 8] = 5;    // header_data_length
        WritePts(pack[(p + 9)..], pts);

        int payload = p + 14;
        if (includeSequenceHeader)
        {
            int height = pal ? 576 : 480;
            int rateCode = pal ? 3 : 4;
            int aspectCode = aspect == DvdAspectRatio.Widescreen16x9 ? 3 : 2;
            pack[payload] = 0x00;
            pack[payload + 1] = 0x00;
            pack[payload + 2] = 0x01;
            pack[payload + 3] = 0xB3;
            pack[payload + 4] = (byte)(720 >> 4);
            pack[payload + 5] = (byte)(((720 & 0x0F) << 4) | (height >> 8));
            pack[payload + 6] = (byte)(height & 0xFF);
            pack[payload + 7] = (byte)((aspectCode << 4) | rateCode);
            payload += 12;
        }

        // Picture start code (00 00 01 00): coding type 1 = I, 2 = P in bits 5..3 of byte +5
        pack[payload] = 0x00;
        pack[payload + 1] = 0x00;
        pack[payload + 2] = 0x01;
        pack[payload + 3] = 0x00;
        pack[payload + 5] = (byte)((isIntra ? 1 : 2) << 3);
    }

    private static void WriteAc3AudioPack(Span<byte> pack, long scr, long pts, byte substreamId, ReadOnlySpan<byte> ac3Payload)
    {
        pack.Clear();
        WritePackHeader(pack, scr);
        int p = 14;
        pack[p] = 0x00;
        pack[p + 1] = 0x00;
        pack[p + 2] = 0x01;
        pack[p + 3] = 0xBD; // Private Stream 1
        BinaryPrimitives.WriteUInt16BigEndian(pack[(p + 4)..], (ushort)(SectorSize - p - 6));
        pack[p + 6] = 0x80;
        pack[p + 7] = 0x80; // PTS present
        pack[p + 8] = 5;
        WritePts(pack[(p + 9)..], pts);

        int at = p + 14;
        pack[at++] = substreamId; // 0x80..0x87
        pack[at++] = 0x01;        // 1 AC-3 frame starting in this pack
        pack[at++] = 0x00;        // first access unit pointer (hi)
        pack[at++] = 0x01;        // first access unit pointer (lo)

        int room = SectorSize - at;
        if (!ac3Payload.IsEmpty)
        {
            ac3Payload[..Math.Min(room, ac3Payload.Length)].CopyTo(pack[at..]);
        }
        else
        {
            // Minimal AC-3 syncframe header (0x0B 0x77)
            pack[at] = 0x0B;
            pack[at + 1] = 0x77;
        }
    }

    private static void WriteSpuPacks(Stream output, byte substreamId, byte[] spu, long pts)
    {
        byte[] pack = new byte[SectorSize];
        int offset = 0;
        bool first = true;
        while (offset < spu.Length)
        {
            Array.Clear(pack);
            WritePackHeader(pack, Math.Max(0, pts - 9_000));
            int p = 14;
            int header = first ? 3 + 5 : 3;
            int room = SectorSize - p - 6 - header - 1;
            int take = Math.Min(room, spu.Length - offset);
            if (room - take is > 0 and < 6)
            {
                take -= 6 - (room - take);
            }
            int pesLength = header + 1 + take;
            pack[p] = 0x00;
            pack[p + 1] = 0x00;
            pack[p + 2] = 0x01;
            pack[p + 3] = 0xBD;
            BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(p + 4), (ushort)pesLength);
            pack[p + 6] = 0x81;
            pack[p + 7] = (byte)(first ? 0x80 : 0x00);
            pack[p + 8] = (byte)(first ? 5 : 0);
            int at = p + 9;
            if (first)
            {
                WritePts(pack.AsSpan(at), pts);
                at += 5;
            }
            pack[at++] = substreamId; // 0x20..0x27
            spu.AsSpan(offset, take).CopyTo(pack.AsSpan(at));
            at += take;
            offset += take;
            first = false;

            int left = SectorSize - at;
            if (left >= 6)
            {
                pack[at] = 0x00;
                pack[at + 1] = 0x00;
                pack[at + 2] = 0x01;
                pack[at + 3] = 0xBE;
                BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(at + 4), (ushort)(left - 6));
                pack.AsSpan(at + 6).Fill(0xFF);
            }
            output.Write(pack, 0, SectorSize);
        }
    }

    internal static void WritePackHeader(Span<byte> pack, long scr)
    {
        pack[0] = 0x00;
        pack[1] = 0x00;
        pack[2] = 0x01;
        pack[3] = 0xBA;
        pack[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        pack[5] = (byte)(scr >> 20);
        pack[6] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
        pack[7] = (byte)(scr >> 5);
        pack[8] = (byte)(((scr << 3) & 0xF8) | 0x04);
        pack[9] = 0x01;
        pack[10] = 0x01;
        pack[11] = 0x89;
        pack[12] = 0xC3;
        pack[13] = 0xF8;
    }

    internal static void WritePts(Span<byte> b, long pts)
    {
        b[0] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        b[1] = (byte)(pts >> 22);
        b[2] = (byte)(((pts >> 14) & 0xFE) | 1);
        b[3] = (byte)(pts >> 7);
        b[4] = (byte)(((pts << 1) & 0xFE) | 1);
    }
}
