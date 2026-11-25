using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CdBurner.Core.Subtitles;

namespace CdBurner.Core.BluRayVideo;

/// <summary>One presentation-graphics segment: type, the times of its PES packet (90 kHz) and its body.</summary>
/// <param name="Type">0x14 palette, 0x15 object, 0x16 composition, 0x17 window, 0x80 end of display set.</param>
/// <param name="Pts90k">Presentation time stamp.</param>
/// <param name="Dts90k">Decoding time stamp (only the composition segment carries one in a transport stream).</param>
/// <param name="Body">Segment body, without the 3-byte type-and-size header.</param>
public sealed record PgsSegment(byte Type, long Pts90k, long Dts90k, byte[] Body);

/// <summary>The segments that show or clear one subtitle, in stream order.</summary>
public sealed record PgsDisplaySet(long Pts90k, IReadOnlyList<PgsSegment> Segments);

/// <summary>
/// Renders subtitle cues to bitmaps and encodes them as Blu-ray presentation graphics (PGS): per cue a display set that
/// shows the picture (composition, window, palette, object, end) and one that clears it. The picture is drawn with four
/// palette entries (clear, white, black outline, grey edge) and run-length coded as the format requires.
/// </summary>
public static class PgsSubtitleEncoder
{
    /// <summary>Segment type: palette definition.</summary>
    public const byte PaletteSegment = 0x14;

    /// <summary>Segment type: object (bitmap) definition.</summary>
    public const byte ObjectSegment = 0x15;

    /// <summary>Segment type: presentation composition.</summary>
    public const byte CompositionSegment = 0x16;

    /// <summary>Segment type: window definition.</summary>
    public const byte WindowSegment = 0x17;

    /// <summary>Segment type: end of display set.</summary>
    public const byte EndSegment = 0x80;

    /// <summary>Largest body of one segment (the size field is 16 bits).</summary>
    public const int MaxSegmentBody = 65535;

    /// <summary>Colour index of a clear pixel.</summary>
    public const byte ClearIndex = 0;

    /// <summary>Colour index of the white text.</summary>
    public const byte TextIndex = 1;

    /// <summary>Colour index of the black outline.</summary>
    public const byte OutlineIndex = 2;

    /// <summary>Colour index of the grey anti-aliasing edge.</summary>
    public const byte EdgeIndex = 3;

    /// <summary>A rendered subtitle: palette indexes, size and where the picture sits on the video plane.</summary>
    public sealed record RenderedCue(byte[] Pixels, int Width, int Height, int X, int Y);

    /// <summary>
    /// Encodes <paramref name="cues"/> for a video plane of <paramref name="videoWidth"/> x <paramref name="videoHeight"/>.
    /// Cues without text are skipped; a cue that overlaps the next ends where the next starts.
    /// </summary>
    public static IReadOnlyList<PgsDisplaySet> Encode(
        IReadOnlyList<SrtCue> cues,
        int videoWidth,
        int videoHeight,
        BdmvFrameRate frameRate,
        long timeOffset90k = 0)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var sets = new List<PgsDisplaySet>();
        var ordered = cues.Where(c => !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Start).ToList();
        ushort composition = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var cue = ordered[i];
            long start = ToTicks(cue.Start);
            long end = ToTicks(cue.End);
            if (i + 1 < ordered.Count)
            {
                end = Math.Min(end, ToTicks(ordered[i + 1].Start));
            }
            if (end <= start)
            {
                continue;
            }

            var rendered = Render(cue.Text, videoWidth, videoHeight);
            if (rendered == null)
            {
                continue;
            }

            long planeInit = TransferTicks(videoWidth, videoHeight);
            long decode = TransferTicks(rendered.Width, rendered.Height);
            long lead = planeInit + decode + 450;
            // A picture needs time to decode before it is shown: a cue at the very start shows a little later.
            start = Math.Max(start, lead);
            end = Math.Max(end, start + 1);

            sets.Add(Shift(BuildShowSet(rendered, start, lead, planeInit, decode, ++composition, videoWidth, videoHeight, frameRate), timeOffset90k));
            sets.Add(Shift(BuildClearSet(end, planeInit, ++composition, videoWidth, videoHeight, frameRate), timeOffset90k));
        }
        return sets;
    }

    private static PgsDisplaySet Shift(PgsDisplaySet set, long offset) => offset == 0
        ? set
        : new PgsDisplaySet(
            set.Pts90k + offset,
            set.Segments.Select(s => s with { Pts90k = s.Pts90k + offset, Dts90k = s.Type == CompositionSegment ? s.Dts90k + offset : s.Dts90k }).ToList());

    private static long ToTicks(TimeSpan time) => (long)Math.Round(time.TotalSeconds * 90_000.0);

    /// <summary>Time to move a bitmap of this size at 128 Mbit/s, in 90 kHz ticks.</summary>
    private static long TransferTicks(int width, int height) =>
        (long)Math.Ceiling(90_000.0 * 8.0 * width * height / 128_000_000.0);

    private static PgsDisplaySet BuildShowSet(
        RenderedCue picture, long pts, long lead, long planeInit, long decode,
        ushort composition, int videoWidth, int videoHeight, BdmvFrameRate rate)
    {
        long dts = pts - lead;
        var segments = new List<PgsSegment>
        {
            new(CompositionSegment, pts, dts, CompositionBody(videoWidth, videoHeight, rate, composition, state: 0x80, picture)),
            new(WindowSegment, dts + planeInit, 0, WindowBody(picture)),
            new(PaletteSegment, dts + planeInit, 0, PaletteBody()),
        };
        long objectPts = dts + planeInit + decode;
        foreach (byte[] body in ObjectBodies(picture))
        {
            segments.Add(new PgsSegment(ObjectSegment, objectPts, 0, body));
        }
        segments.Add(new PgsSegment(EndSegment, objectPts, 0, Array.Empty<byte>()));
        return new PgsDisplaySet(pts, segments);
    }

    private static PgsDisplaySet BuildClearSet(
        long end, long planeInit, ushort composition, int videoWidth, int videoHeight, BdmvFrameRate rate)
    {
        long dts = Math.Max(0, end - planeInit);
        var segments = new List<PgsSegment>
        {
            new(CompositionSegment, end, dts, CompositionBody(videoWidth, videoHeight, rate, composition, state: 0x00, picture: null)),
            new(EndSegment, dts, 0, Array.Empty<byte>()),
        };
        return new PgsDisplaySet(end, segments);
    }

    private static byte[] CompositionBody(int w, int h, BdmvFrameRate rate, ushort composition, byte state, RenderedCue? picture)
    {
        var body = new List<byte>();
        body.AddRange(U16(w));
        body.AddRange(U16(h));
        body.Add((byte)((int)rate << 4));
        body.AddRange(U16(composition));
        body.Add(state);
        body.Add(0);                                  // palette update flag: no
        body.Add(0);                                  // palette id
        body.Add((byte)(picture == null ? 0 : 1));    // composition objects
        if (picture != null)
        {
            body.AddRange(U16(0));                    // object id
            body.Add(0);                              // window id
            body.Add(0);                              // not cropped
            body.AddRange(U16(picture.X));
            body.AddRange(U16(picture.Y));
        }
        return body.ToArray();
    }

    private static byte[] WindowBody(RenderedCue picture)
    {
        var body = new List<byte> { 1, 0 };           // one window, id 0
        body.AddRange(U16(picture.X));
        body.AddRange(U16(picture.Y));
        body.AddRange(U16(picture.Width));
        body.AddRange(U16(picture.Height));
        return body.ToArray();
    }

    /// <summary>The palette: index, Y, Cr, Cb, alpha for each of the four colours (BT.709 limited range).</summary>
    public static byte[] PaletteBody() =>
    [
        0, 0,                                         // palette id 0, version 0
        ClearIndex, 16, 128, 128, 0,
        TextIndex, 235, 128, 128, 255,
        OutlineIndex, 16, 128, 128, 255,
        EdgeIndex, 126, 128, 128, 255,
    ];

    private static IEnumerable<byte[]> ObjectBodies(RenderedCue picture) => ObjectBodies(picture.Pixels, picture.Width, picture.Height);

    /// <summary>
    /// The object segments for a bitmap: one when the coded picture fits a segment, else a first segment (flag 0x80)
    /// followed by continuation segments, the last flagged 0x40.
    /// </summary>
    internal static IEnumerable<byte[]> ObjectBodies(byte[] pixels, int width, int height)
    {
        byte[] rle = EncodeRle(pixels, width, height);
        int objectDataLength = rle.Length + 4;        // the width and height fields count
        const int firstOverhead = 4 + 3 + 4;          // id, version, flag, length, width, height
        const int nextOverhead = 4;
        int offset = 0;
        bool first = true;
        while (first || offset < rle.Length)
        {
            int room = MaxSegmentBody - (first ? firstOverhead : nextOverhead);
            int take = Math.Min(room, rle.Length - offset);
            bool last = offset + take >= rle.Length;
            var body = new List<byte>();
            body.AddRange(U16(0));                    // object id
            body.Add(0);                              // version
            body.Add((byte)((first ? 0x80 : 0) | (last ? 0x40 : 0)));
            if (first)
            {
                body.Add((byte)(objectDataLength >> 16));
                body.Add((byte)(objectDataLength >> 8));
                body.Add((byte)objectDataLength);
                body.AddRange(U16(width));
                body.AddRange(U16(height));
            }
            body.AddRange(rle.AsSpan(offset, take).ToArray());
            offset += take;
            first = false;
            yield return body.ToArray();
        }
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    /// <summary>
    /// Run-length codes <paramref name="pixels"/> (one colour index per byte, rows top to bottom) the way PGS objects
    /// are coded: a non-zero byte is one pixel; <c>00 00</c> ends a line; <c>00 0L</c> and <c>00 4L LL</c> are runs of
    /// colour 0; <c>00 8L CC</c> and <c>00 CL LL CC</c> are runs of colour CC (6- and 14-bit lengths).
    /// </summary>
    public static byte[] EncodeRle(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException("The pixel count must be width x height.", nameof(pixels));
        }

        var output = new List<byte>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                byte color = pixels[y * width + x];
                int run = 1;
                while (x + run < width && pixels[y * width + x + run] == color && run < 0x3FFF)
                {
                    run++;
                }

                if (color == 0)
                {
                    output.Add(0);
                    if (run < 64)
                    {
                        output.Add((byte)run);
                    }
                    else
                    {
                        output.Add((byte)(0x40 | (run >> 8)));
                        output.Add((byte)run);
                    }
                }
                else if (run < 3)
                {
                    for (int i = 0; i < run; i++)
                    {
                        output.Add(color);
                    }
                }
                else
                {
                    output.Add(0);
                    if (run < 64)
                    {
                        output.Add((byte)(0x80 | run));
                    }
                    else
                    {
                        output.Add((byte)(0xC0 | (run >> 8)));
                        output.Add((byte)run);
                    }
                    output.Add(color);
                }
                x += run;
            }
            output.Add(0);
            output.Add(0);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Writes display sets as a <c>.sup</c> file: per segment <c>"PG"</c>, PTS and DTS (90 kHz, 32 bits), type and size,
    /// then the body.
    /// </summary>
    public static void WriteSup(IReadOnlyList<PgsDisplaySet> sets, Stream output)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(output);
        Span<byte> header = stackalloc byte[13];
        foreach (var set in sets)
        {
            foreach (var segment in set.Segments)
            {
                header[0] = (byte)'P';
                header[1] = (byte)'G';
                BinaryPrimitives.WriteUInt32BigEndian(header[2..], (uint)segment.Pts90k);
                BinaryPrimitives.WriteUInt32BigEndian(header[6..], (uint)segment.Dts90k);
                header[10] = segment.Type;
                BinaryPrimitives.WriteUInt16BigEndian(header[11..], (ushort)segment.Body.Length);
                output.Write(header);
                output.Write(segment.Body);
            }
        }
    }

    /// <summary>
    /// Draws the text white with a black outline, centred near the bottom of the picture, and cuts the result to the
    /// smallest rectangle that holds it. Null when nothing visible was drawn.
    /// </summary>
    public static RenderedCue? Render(string text, int videoWidth, int videoHeight)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        int fontPx = Math.Max(24, videoHeight / 20);
        int lineHeight = fontPx + fontPx / 4;
        int outline = Math.Max(2, fontPx / 12);
        int maxTextWidth = videoWidth * 85 / 100;
        using var font = new Font("Segoe UI Semibold", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        var wrapped = Wrap(lines, font, maxTextWidth).Take(3).ToList();

        int w = videoWidth - 2 * (videoWidth / 40);
        int h = wrapped.Count * lineHeight + 2 * outline + 8;
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
                path.AddString(wrapped[i], font.FontFamily, (int)font.Style, fontPx,
                    new RectangleF(0, outline + 4 + i * lineHeight, w, lineHeight + 8), format);
                g.DrawPath(pen, path);
                g.FillPath(Brushes.White, path);
            }
        }

        byte[] full = new byte[w * h];
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
                    full[y * w + x] = a < 96 ? ClearIndex : luma >= 200 ? TextIndex : luma < 70 ? OutlineIndex : EdgeIndex;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (full[y * w + x] != ClearIndex)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        if (maxX < 0)
        {
            return null;
        }

        int cw = maxX - minX + 1;
        int ch = maxY - minY + 1;
        byte[] cropped = new byte[cw * ch];
        for (int y = 0; y < ch; y++)
        {
            Array.Copy(full, (minY + y) * w + minX, cropped, y * cw, cw);
        }

        int left = videoWidth / 40 + minX;
        int top = videoHeight - videoHeight / 12 - ch;
        return new RenderedCue(cropped, cw, ch, left, Math.Max(0, top));
    }

    private static IEnumerable<string> Wrap(IReadOnlyList<string> lines, Font font, int maxWidth)
    {
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        foreach (string line in lines)
        {
            string current = string.Empty;
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth)
                {
                    yield return current;
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            if (current.Length > 0)
            {
                yield return current;
            }
        }
    }
}
