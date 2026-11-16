using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CdBurner.Core.DvdVideo;

/// <summary>One subtitle: when it shows and hides (from the start of the film) and its text, one entry per line.</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines);

/// <summary>
/// Turns an .srt file into a DVD subtitle stream: each cue drawn as white text with a black outline near the bottom of the
/// picture, coded as a DVD subpicture unit (SPU) and written as a VobSub pair (.idx + .sub) that FFmpeg's DVD muxer takes
/// as subpicture stream 0x20. SPU layout per the DVD subpicture format (mpucoder's spu.html; libdvdread's spu decoder
/// reads the same): a 2-byte size, a 2-byte offset of the control table, the run-length coded top field (even lines) and
/// bottom field (odd lines), then display control sequences (SET_COLOR, SET_CONTR, SET_DAREA, SET_DSPXA, STA_DSP; after
/// the cue's length STP_DSP). Pixel values: 0 background (transparent), 1 text, 2 outline, 3 edge smoothing; the PGC
/// palette (<see cref="DvdIfoGenerator.SubtitlePalette"/>) gives them black, white, black and grey.
/// </summary>
public static class DvdSubtitleWriter
{
    /// <summary>Largest SPU a player must accept.</summary>
    public const int MaxSpuBytes = 53_220;

    private static readonly Regex Tags = new(@"<[^>]+>|\{[^}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex TimeLine = new(
        @"^\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads an .srt file: UTF-8 (with or without a byte order mark), else Windows-1252. Formatting tags (&lt;i&gt;,
    /// {\an8}) are removed, empty cues dropped, and the result ordered by start time.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> ReadSrt(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes); // closest built-in to Windows-1252 for subtitle text
        }
        return ParseSrt(text.TrimStart('﻿'));
    }

    /// <summary>Parses .srt text; see <see cref="ReadSrt"/>.</summary>
    public static IReadOnlyList<SubtitleCue> ParseSrt(string text)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }
            TimeSpan Part(int g) => new TimeSpan(0, int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));
            var start = Part(1);
            var end = Part(5);
            var body = new List<string>();
            for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]); i++)
            {
                string line = Tags.Replace(lines[i], "").Trim();
                if (line.Length > 0)
                {
                    body.Add(line);
                }
            }
            if (body.Count > 0 && end > start)
            {
                cues.Add(new SubtitleCue(start, end, body));
            }
        }
        return cues.OrderBy(c => c.Start).ToList();
    }

    /// <summary>Font, line spacing, outline, and box margin in pixels for a <see cref="DvdSubtitleSize"/>.</summary>
    public static (float FontPx, int LineHeight, int OutlinePx, int MarginPx) GetSizeMetrics(DvdSubtitleSize size) => size switch
    {
        DvdSubtitleSize.Small => (20f, 27, 3, 5),
        DvdSubtitleSize.Large => (32f, 42, 5, 7),
        _ => (26f, 34, 4, 6)
    };

    /// <summary>
    /// Writes <paramref name="cues"/> as VobSub: <paramref name="subPath"/> (program stream packs holding the SPUs) and
    /// the matching .idx (picture size, palette, one timestamp and file position per cue) beside it. Returns the .idx
    /// path, which is what FFmpeg opens. Cues overlapping the next are cut where the next begins.
    /// </summary>
    public static string WriteVobSub(IReadOnlyList<SubtitleCue> cues, string subPath, int width = 720, int height = 480,
        DvdSubtitleSize size = DvdSubtitleSize.Medium)
        => WriteVobSub(cues.Select(c => (c.Start, (TimeSpan?)c.End, (byte[]?)null)).ToList(), cues, subPath, width, height, size);

    /// <summary>Writes one ready-made SPU (a menu's button highlights) shown from the start, as VobSub.</summary>
    public static string WriteVobSub(byte[] spu, string subPath, int width = 720, int height = 480)
        => WriteVobSub(new List<(TimeSpan, TimeSpan?, byte[]?)> { (TimeSpan.Zero, null, spu) }, Array.Empty<SubtitleCue>(), subPath, width, height, DvdSubtitleSize.Medium);

    private static string WriteVobSub(List<(TimeSpan Start, TimeSpan? End, byte[]? Spu)> items, IReadOnlyList<SubtitleCue> cues,
        string subPath, int width, int height, DvdSubtitleSize size)
    {
        string idxPath = Path.ChangeExtension(subPath, ".idx");
        using var sub = new FileStream(subPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var idx = new StringBuilder();
        idx.Append("# VobSub index file, v7 (do not modify this line!)\n");
        idx.Append(CultureInfo.InvariantCulture, $"size: {width}x{height}\n");
        idx.Append("palette: 000000, ffffff, 000000, 808080, ffa020, ffffff, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000, 000000\n");
        idx.Append("langidx: 0\n");
        idx.Append("id: en, index: 0\n");

        for (int i = 0; i < items.Count; i++)
        {
            var (start, itemEnd, ready) = items[i];
            byte[] spu;
            if (ready != null)
            {
                spu = ready;
            }
            else
            {
                var end = i + 1 < items.Count && items[i + 1].Start < itemEnd ? items[i + 1].Start : itemEnd!.Value;
                if (end <= start)
                {
                    continue;
                }
                spu = EncodeCue(cues[i].Lines, end - start, width, height, size);
            }
            long pts = (long)Math.Round(start.TotalSeconds * 90_000);
            idx.Append(CultureInfo.InvariantCulture,
                $"timestamp: {start.Hours:D2}:{start.Minutes:D2}:{start.Seconds:D2}:{start.Milliseconds:D3}, filepos: {sub.Position:X9}\n");
            WritePacks(sub, spu, pts);
        }
        File.WriteAllText(idxPath, idx.ToString(), new UTF8Encoding(false));
        return idxPath;
    }

    /// <summary>Draws the lines and codes them as one SPU shown for <paramref name="duration"/>.</summary>
    public static byte[] EncodeCue(IReadOnlyList<string> lines, TimeSpan duration, int width = 720, int height = 480,
        DvdSubtitleSize size = DvdSubtitleSize.Medium)
    {
        var (pixels, w, h, x, y) = Render(lines, width, height, size);
        try
        {
            return EncodeSpu(pixels, w, h, x, y, duration);
        }
        catch (InvalidOperationException) when (size == DvdSubtitleSize.Large)
        {
            (pixels, w, h, x, y) = Render(lines, width, height, DvdSubtitleSize.Medium);
            return EncodeSpu(pixels, w, h, x, y, duration);
        }
    }

    /// <summary>
    /// Codes a 2-bit picture (<paramref name="pixels"/>, row by row, values 0-3) placed at (<paramref name="x"/>,
    /// <paramref name="y"/>) as an SPU shown from its PTS for <paramref name="duration"/> (null: until replaced).
    /// <paramref name="colors"/> and <paramref name="contrast"/> are the SET_COLOR / SET_CONTR bytes (pixel values 3, 2,
    /// 1, 0 as four nibbles: palette entries, and opacity 0-15).
    /// </summary>
    public static byte[] EncodeSpu(byte[] pixels, int w, int h, int x, int y, TimeSpan? duration,
        ushort colors = 0x3210, ushort contrast = 0xFFF0, bool forced = false)
    {
        var data = new List<byte>(8192) { 0, 0, 0, 0 };
        int top = data.Count;
        for (int row = 0; row < h; row += 2)
        {
            EncodeLine(pixels.AsSpan(row * w, w), data);
        }
        int bottom = data.Count;
        for (int row = 1; row < h; row += 2)
        {
            EncodeLine(pixels.AsSpan(row * w, w), data);
        }
        if ((data.Count & 1) != 0)
        {
            data.Add(0);
        }

        int control = data.Count;
        // First control sequence: set up the picture and show it at once.
        int second = control + 4 + 3 + 3 + 7 + 5 + 1 + 1;
        var c = new List<byte>
        {
            0, 0, (byte)(second >> 8), (byte)second,
            0x03, (byte)(colors >> 8), (byte)colors,
            0x04, (byte)(contrast >> 8), (byte)contrast,
            0x05,
            (byte)(x >> 4), (byte)(((x & 0x0F) << 4) | ((x + w - 1) >> 8)), (byte)(x + w - 1),
            (byte)(y >> 4), (byte)(((y & 0x0F) << 4) | ((y + h - 1) >> 8)), (byte)(y + h - 1),
            0x06, (byte)(top >> 8), (byte)top, (byte)(bottom >> 8), (byte)bottom,
            (byte)(forced ? 0x00 : 0x01), // FSTA_DSP: shown even with subtitles off (menu highlights); STA_DSP otherwise
            0xFF
        };
        if (duration is { } d)
        {
            // Second control sequence: hide after the duration (units of 1024/90000 s); its next pointer is itself.
            int delay = (int)Math.Clamp(Math.Round(d.TotalSeconds * 90_000 / 1024), 1, 0xFFFF);
            c.AddRange([(byte)(delay >> 8), (byte)delay, (byte)(second >> 8), (byte)second, 0x02, 0xFF]);
        }
        else
        {
            // No hide: the first sequence is also the last, pointing at itself.
            c[2] = (byte)(control >> 8);
            c[3] = (byte)control;
        }
        data.AddRange(c);
        if (data.Count > MaxSpuBytes)
        {
            throw new InvalidOperationException($"A subtitle picture codes to {data.Count:N0} bytes, more than the {MaxSpuBytes:N0} a DVD player takes.");
        }
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data), (ushort)data.Count);
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data)[2..], (ushort)control);
        return data.ToArray();
    }

    /// <summary>
    /// One line of 2-bit pixels as DVD run lengths, each run n pixels of value v: n 1-3 in 4 bits (nnvv), 4-15 in 8
    /// (00nnnnvv), 16-63 in 12 (0000nnnnnnvv), 64-255 in 16 (000000nnnnnnnnvv); a run to the end of the line is 16 bits
    /// with n = 0. Each line ends on a whole byte.
    /// </summary>
    private static void EncodeLine(ReadOnlySpan<byte> line, List<byte> output)
    {
        var nibbles = new List<int>(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            int v = line[i] & 3;
            int n = 1;
            while (i + n < line.Length && (line[i + n] & 3) == v && n < 255)
            {
                n++;
            }
            if (i + n == line.Length && n > 3)
            {
                nibbles.AddRange([0, 0, 0, v]); // to the end of the line
            }
            else if (n < 4)
            {
                nibbles.Add((n << 2) | v);
            }
            else if (n < 16)
            {
                nibbles.AddRange([n >> 2, ((n & 3) << 2) | v]);                 // 00nn nnvv
            }
            else if (n < 64)
            {
                nibbles.AddRange([0, n >> 2, ((n & 3) << 2) | v]);              // 0000 nnnn nnvv
            }
            else
            {
                nibbles.AddRange([0, n >> 6, (n >> 2) & 0x0F, ((n & 3) << 2) | v]); // 0000 00nn nnnn nnvv
            }
            i += n;
        }
        if ((nibbles.Count & 1) != 0)
        {
            nibbles.Add(0);
        }
        for (int k = 0; k < nibbles.Count; k += 2)
        {
            output.Add((byte)((nibbles[k] << 4) | nibbles[k + 1]));
        }
    }

    /// <summary>
    /// Draws up to three lines of white text with a black outline, centred, their bottom 48 lines above the picture's
    /// bottom (inside the title-safe area), and maps the result to 2-bit pixels. Long lines are wrapped to fit 640 pixels.
    /// </summary>
    public static (byte[] Pixels, int W, int H, int X, int Y) Render(string text, int width = 720, int height = 480,
        DvdSubtitleSize size = DvdSubtitleSize.Medium) =>
        Render(string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'),
            width, height, size);

    /// <summary>
    /// Draws up to three lines of white text with a black outline, centred, their bottom 48 lines above the picture's
    /// bottom (inside the title-safe area), and maps the result to 2-bit pixels. Long lines are wrapped to fit 640 pixels.
    /// </summary>
    public static (byte[] Pixels, int W, int H, int X, int Y) Render(IReadOnlyList<string> lines, int width = 720, int height = 480,
        DvdSubtitleSize size = DvdSubtitleSize.Medium)
    {
        var (fontPx, lineHeight, outline, margin) = GetSizeMetrics(size);
        using var font = new Font("Segoe UI Semibold", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        var wrapped = Wrap(lines, font, 640);
        int h = Math.Max(1, wrapped.Count) * lineHeight + 2 * margin;
        h += h & 1; // whole field pairs
        int w = width - 16;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            using var pen = new Pen(Color.Black, outline) { LineJoin = LineJoin.Round };
            for (int i = 0; i < wrapped.Count; i++)
            {
                using var path = new GraphicsPath();
                path.AddString(wrapped[i], font.FontFamily, (int)font.Style, font.Size, new RectangleF(0, margin + i * lineHeight, w, lineHeight + 8), format);
                g.DrawPath(pen, path);
                g.FillPath(Brushes.White, path);
            }
        }

        byte[] pixels = new byte[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int b = row[4 * x], gr = row[4 * x + 1], r = row[4 * x + 2], a = row[4 * x + 3];
                    int luma = (r * 299 + gr * 587 + b * 114) / 1000;
                    pixels[y * w + x] = (byte)(a < 96 ? 0 : luma >= 200 ? 1 : luma < 70 ? 2 : 3);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        int top = height - 48 - h;
        return (pixels, w, h, 8, top - (top & 1));
    }

    /// <summary>Breaks lines that are wider than <paramref name="maxWidth"/> at spaces; keeps at most three lines.</summary>
    private static List<string> Wrap(IReadOnlyList<string> lines, Font font, int maxWidth)
    {
        var result = new List<string>();
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        foreach (string line in lines)
        {
            string current = "";
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth)
                {
                    result.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            if (current.Length > 0)
            {
                result.Add(current);
            }
        }
        return result.Count <= 3 ? result : result.Take(3).ToList();
    }

    /// <summary>
    /// Writes an SPU as 2,048-byte MPEG-2 program stream packs: private stream 1, substream 0x20, the PTS on the first
    /// PES packet; the last pack is filled with a padding packet.
    /// </summary>
    private static void WritePacks(Stream output, byte[] spu, long pts)
    {
        int offset = 0;
        bool first = true;
        while (offset < spu.Length)
        {
            byte[] pack = new byte[2048];
            WritePackHeader(pack, Math.Max(0, pts - 9_000));
            int p = 14;
            int header = first ? 3 + 5 : 3;               // MPEG-2 PES header (+ PTS)
            int room = 2048 - p - 6 - header - 1;          // minus PES start, header, substream id
            int take = Math.Min(room, spu.Length - offset);
            if (room - take is > 0 and < 6)
            {
                take -= 6 - (room - take); // leave room for a padding packet; the rest goes in the next pack
            }
            int pesLength = header + 1 + take;
            pack[p] = 0; pack[p + 1] = 0; pack[p + 2] = 1; pack[p + 3] = 0xBD;
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
            pack[at++] = 0x20; // subpicture stream 0
            spu.AsSpan(offset, take).CopyTo(pack.AsSpan(at));
            at += take;
            offset += take;
            first = false;

            int left = 2048 - at;
            if (left >= 6)
            {
                pack[at] = 0; pack[at + 1] = 0; pack[at + 2] = 1; pack[at + 3] = 0xBE;
                BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(at + 4), (ushort)(left - 6));
                pack.AsSpan(at + 6).Fill(0xFF);
            }
            output.Write(pack);
        }
    }

    private static void WritePackHeader(byte[] pack, long scr)
    {
        pack[0] = 0; pack[1] = 0; pack[2] = 1; pack[3] = 0xBA;
        pack[4] = (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03));
        pack[5] = (byte)(scr >> 20);
        pack[6] = (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03));
        pack[7] = (byte)(scr >> 5);
        pack[8] = (byte)(((scr << 3) & 0xF8) | 0x04);
        pack[9] = 0x01;
        pack[10] = 0x01; pack[11] = 0x89; pack[12] = 0xC3; // mux rate 25,200 x 50 bytes/s
        pack[13] = 0xF8;                                    // no stuffing
    }

    private static void WritePts(Span<byte> b, long pts)
    {
        b[0] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        b[1] = (byte)(pts >> 22);
        b[2] = (byte)(((pts >> 14) & 0xFE) | 1);
        b[3] = (byte)(pts >> 7);
        b[4] = (byte)(((pts << 1) & 0xFE) | 1);
    }
}
