using System.Buffers.Binary;
using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Reads and embeds RFC 7845 / Xiph VorbisComment <c>METADATA_BLOCK_PICTURE</c> cover art in Ogg Opus files.
/// </summary>
public static class OggOpusMetadata
{
    private const int MaxBytesPerOggPage = 255 * 255;

    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    private static uint[] BuildOggCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
            {
                r = (r & 0x8000_0000) != 0 ? (r << 1) ^ 0x04C1_1DB7 : (r << 1);
            }
            table[i] = r;
        }
        return table;
    }

    /// <summary>
    /// Computes the RFC 3533 Ogg page CRC-32 (polynomial <c>0x04C11DB7</c>, unreflected, zero initial value).
    /// Expects bytes 22..25 in <paramref name="page"/> to be zeroed before calling.
    /// </summary>
    public static uint ComputeOggCrc32(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
        {
            crc = (crc << 8) ^ OggCrcTable[((crc >> 24) & 0xFF) ^ page[i]];
        }
        return crc;
    }

    /// <summary>
    /// Embeds <paramref name="coverArtBytes"/> as a <c>METADATA_BLOCK_PICTURE</c> VorbisComment inside
    /// <paramref name="opusFilePath"/>, replacing the file atomically.
    /// </summary>
    public static void EmbedCoverArt(string opusFilePath, byte[] coverArtBytes, string? mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opusFilePath);
        ArgumentNullException.ThrowIfNull(coverArtBytes);
        if (coverArtBytes.Length == 0) return;

        string tempPath = opusFilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var input = new FileStream(opusFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                EmbedCoverArt(input, output, coverArtBytes, mimeType);
            }
            File.Move(tempPath, opusFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Rewrites an Ogg Opus stream from <paramref name="input"/> to <paramref name="output"/>, embedding
    /// <paramref name="coverArtBytes"/> into the <c>OpusTags</c> header packet and renumbering all following pages.
    /// </summary>
    public static void EmbedCoverArt(Stream input, Stream output, byte[] coverArtBytes, string? mimeType)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(coverArtBytes);
        if (coverArtBytes.Length == 0)
        {
            input.CopyTo(output);
            return;
        }

        // Page 0: OpusHead
        var headPage = ReadNextPage(input)
            ?? throw new InvalidDataException("Invalid Ogg Opus stream: missing OpusHead page.");
        if ((headPage.HeaderType & 0x02) == 0 ||
            headPage.Payload.Length < 8 ||
            !headPage.Payload.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            throw new InvalidDataException("Invalid Ogg Opus stream: first page is not OpusHead.");
        }

        WriteOggPage(output, headPage.HeaderType, headPage.GranulePosition, headPage.SerialNumber, pageSeq: 0, headPage.SegmentTable, headPage.Payload);

        // Read the full OpusTags packet (may span 1 or more Ogg pages)
        using var tagsPacketStream = new MemoryStream();
        var trailingPages = new List<RawOggPage>();
        bool tagsPacketComplete = false;
        while (!tagsPacketComplete)
        {
            var page = ReadNextPage(input)
                ?? throw new InvalidDataException("Invalid Ogg Opus stream: unexpected end of stream before OpusTags ended.");
            if (page.SerialNumber != headPage.SerialNumber)
            {
                throw new InvalidDataException("Multiplexed Ogg streams are not supported for cover art embedding.");
            }

            int offset = 0;
            for (int i = 0; i < page.SegmentTable.Length; i++)
            {
                byte seg = page.SegmentTable[i];
                tagsPacketStream.Write(page.Payload, offset, seg);
                offset += seg;
                if (seg < 255)
                {
                    tagsPacketComplete = true;
                    // OpusTags must occupy its own header packet; any leftover segments on the same page shouldn't occur
                    // in standard Opus streams, but preserve them just in case.
                    if (offset < page.Payload.Length)
                    {
                        byte[] restPayload = page.Payload.AsSpan(offset).ToArray();
                        byte[] restSegments = page.SegmentTable.AsSpan(i + 1).ToArray();
                        trailingPages.Add(new RawOggPage((byte)(page.HeaderType & ~0x01), page.GranulePosition, page.SerialNumber, restSegments, restPayload));
                    }
                    break;
                }
            }
        }

        byte[] updatedTagsPacket = BuildUpdatedOpusTagsPacket(tagsPacketStream.ToArray(), coverArtBytes, mimeType);
        uint nextSeq = WritePacketAsPages(output, updatedTagsPacket, headPage.SerialNumber, startSeq: 1, granulePosition: 0);

        foreach (var extra in trailingPages)
        {
            WriteOggPage(output, extra.HeaderType, extra.GranulePosition, extra.SerialNumber, nextSeq++, extra.SegmentTable, extra.Payload);
        }

        while (ReadNextPage(input) is { } audioPage)
        {
            WriteOggPage(output, audioPage.HeaderType, audioPage.GranulePosition, audioPage.SerialNumber, nextSeq++, audioPage.SegmentTable, audioPage.Payload);
        }
    }

    /// <summary>
    /// Extracts front cover art from a <c>METADATA_BLOCK_PICTURE</c> entry inside VorbisComments, if present and valid.
    /// </summary>
    public static bool TryExtractCoverArt(
        IReadOnlyDictionary<string, string>? comments,
        out byte[]? coverBytes,
        out string? mimeType)
    {
        coverBytes = null;
        mimeType = null;
        if (comments == null || !comments.TryGetValue("METADATA_BLOCK_PICTURE", out string? b64) || string.IsNullOrWhiteSpace(b64))
        {
            return false;
        }

        byte[] block;
        try
        {
            block = Convert.FromBase64String(b64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        // FLAC METADATA_BLOCK_PICTURE layout:
        // u32 type | u32 mimeLen | mime | u32 descLen | desc | u32 w | u32 h | u32 depth | u32 colors | u32 dataLen | data
        if (block.Length < 32) return false;
        int pos = 4; // skip pictureType
        uint mimeLen = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(pos, 4));
        pos += 4;
        if (mimeLen > block.Length - pos - 24) return false;
        string parsedMime = Encoding.ASCII.GetString(block, pos, (int)mimeLen);
        pos += (int)mimeLen;

        uint descLen = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(pos, 4));
        pos += 4;
        if (descLen > block.Length - pos - 20) return false;
        pos += (int)descLen;

        pos += 16; // width, height, color depth, indexed colors
        if (pos + 4 > block.Length) return false;
        uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(pos, 4));
        pos += 4;
        if (dataLen == 0 || dataLen > block.Length - pos) return false;

        byte[] image = block.AsSpan(pos, (int)dataLen).ToArray();
        string? detectedMime = FlacEncoder.DetectImageMimeType(image);
        if (detectedMime == null && string.IsNullOrWhiteSpace(parsedMime))
        {
            return false;
        }

        coverBytes = image;
        mimeType = detectedMime ?? parsedMime;
        return true;
    }

    private static byte[] BuildUpdatedOpusTagsPacket(byte[] existingPacket, byte[] coverArtBytes, string? mimeType)
    {
        if (existingPacket.Length < 16 || !existingPacket.AsSpan(0, 8).SequenceEqual("OpusTags"u8))
        {
            throw new InvalidDataException("Invalid Ogg Opus stream: missing OpusTags packet.");
        }

        int pos = 8;
        uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(existingPacket.AsSpan(pos, 4));
        pos += 4;
        if (vendorLen > existingPacket.Length - pos - 4)
        {
            throw new InvalidDataException("Invalid OpusTags packet: truncated vendor string.");
        }
        string vendor = Encoding.UTF8.GetString(existingPacket, pos, (int)vendorLen);
        pos += (int)vendorLen;

        uint commentCount = BinaryPrimitives.ReadUInt32LittleEndian(existingPacket.AsSpan(pos, 4));
        pos += 4;
        var comments = new List<string>((int)Math.Min(commentCount + 1, 1024));
        for (uint i = 0; i < commentCount && pos + 4 <= existingPacket.Length; i++)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(existingPacket.AsSpan(pos, 4));
            pos += 4;
            if (len > existingPacket.Length - pos)
            {
                break;
            }
            string entry = Encoding.UTF8.GetString(existingPacket, pos, (int)len);
            pos += (int)len;
            if (!entry.StartsWith("METADATA_BLOCK_PICTURE=", StringComparison.OrdinalIgnoreCase))
            {
                comments.Add(entry);
            }
        }

        byte[] pictureBlock = FlacEncoder.CreatePictureBlockBody(coverArtBytes, mimeType);
        comments.Add("METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(pictureBlock));

        using var ms = new MemoryStream();
        ms.Write("OpusTags"u8);
        Span<byte> u32 = stackalloc byte[4];

        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)vendorBytes.Length);
        ms.Write(u32);
        ms.Write(vendorBytes);

        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)comments.Count);
        ms.Write(u32);
        foreach (string c in comments)
        {
            byte[] cBytes = Encoding.UTF8.GetBytes(c);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)cBytes.Length);
            ms.Write(u32);
            ms.Write(cBytes);
        }

        return ms.ToArray();
    }

    private static uint WritePacketAsPages(Stream output, byte[] packet, uint serialNumber, uint startSeq, long granulePosition)
    {
        uint seq = startSeq;
        int offset = 0;
        bool isFirst = true;

        while (true)
        {
            int remaining = packet.Length - offset;
            int chunkLen = Math.Min(remaining, MaxBytesPerOggPage);
            bool isFinal = offset + chunkLen >= packet.Length;

            var segments = new List<byte>(255);
            int left = chunkLen;
            while (left >= 255)
            {
                segments.Add(255);
                left -= 255;
                if (segments.Count == 255) break;
            }
            if (segments.Count < 255 && isFinal)
            {
                segments.Add((byte)left);
            }

            byte headerType = isFirst ? (byte)0x00 : (byte)0x01;
            long pageGranule = isFinal ? granulePosition : -1;
            WriteOggPage(output, headerType, pageGranule, serialNumber, seq++, segments.ToArray(), packet.AsSpan(offset, chunkLen));

            offset += chunkLen;
            isFirst = false;

            if (isFinal)
            {
                // If the packet ended on an exact multiple of 255*255 (all 255-byte segments), RFC 3533 requires
                // a terminating 0-byte lacing value on the next page to mark the end of the packet.
                if (segments.Count == 255 && segments[^1] == 255)
                {
                    WriteOggPage(output, 0x01, granulePosition, serialNumber, seq++, [0], ReadOnlySpan<byte>.Empty);
                }
                break;
            }
        }

        return seq;
    }

    private static void WriteOggPage(
        Stream output,
        byte headerType,
        long granulePosition,
        uint serialNumber,
        uint pageSeq,
        ReadOnlySpan<byte> segmentTable,
        ReadOnlySpan<byte> payload)
    {
        byte[] page = new byte[27 + segmentTable.Length + payload.Length];
        "OggS"u8.CopyTo(page.AsSpan(0, 4));
        page[4] = 0; // stream structure version
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serialNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), pageSeq);
        // bytes 22..25 remain 0 for CRC calculation
        page[26] = (byte)segmentTable.Length;
        segmentTable.CopyTo(page.AsSpan(27));
        payload.CopyTo(page.AsSpan(27 + segmentTable.Length));

        uint crc = ComputeOggCrc32(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), crc);
        output.Write(page, 0, page.Length);
    }

    private sealed record RawOggPage(byte HeaderType, long GranulePosition, uint SerialNumber, byte[] SegmentTable, byte[] Payload);

    private static RawOggPage? ReadNextPage(Stream input)
    {
        byte[] header = new byte[27];
        if (!ReadExactOrEof(input, header))
        {
            return null;
        }
        if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8) || header[4] != 0)
        {
            throw new InvalidDataException("Invalid Ogg page signature.");
        }

        int segmentCount = header[26];
        byte[] segments = new byte[segmentCount];
        if (segmentCount > 0 && !ReadExactOrEof(input, segments))
        {
            throw new InvalidDataException("Truncated Ogg segment table.");
        }

        int payloadLen = 0;
        foreach (byte s in segments) payloadLen += s;
        byte[] payload = new byte[payloadLen];
        if (payloadLen > 0 && !ReadExactOrEof(input, payload))
        {
            throw new InvalidDataException("Truncated Ogg page payload.");
        }

        return new RawOggPage(
            header[5],
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4)),
            segments,
            payload);
    }

    private static bool ReadExactOrEof(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = stream.Read(buffer[total..]);
            if (r <= 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException("Unexpected end of Ogg stream.");
            }
            total += r;
        }
        return true;
    }
}
