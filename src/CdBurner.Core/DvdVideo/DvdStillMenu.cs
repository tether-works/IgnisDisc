using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>A title on the menu: its name, a still picture from it (or null), and whether it has subtitles.</summary>
public sealed record DvdMenuTitle(string Name, string? StillImagePath, bool HasSubtitles);

/// <summary>A button the menu drew: which title it plays and whether it switches subtitles on.</summary>
public sealed record DvdMenuButtonPlan(int Title, bool WithSubtitles, Rectangle Bounds);

/// <summary>The finished menu VOB, its VOBUs and the buttons in it.</summary>
public sealed record DvdStillMenuResult(byte[] Vob, IReadOnlyList<DvdVobu> Vobus, IReadOnlyList<DvdMenuButtonPlan> Buttons);

/// <summary>
/// The title menu: a still picture (the disc's heading, and for each title a frame from it with its name) with a Play
/// button per title and, for a title with subtitles, a second one that plays it with them on. The buttons' highlights
/// are a forced subpicture of their outlines, invisible until selected (then amber, white while pressed: see
/// <see cref="DvdNavPackWriter"/>). The picture is drawn at square pixels (854 x 480 for 16:9, 640 x 480 for 4:3; 576
/// lines for PAL) and scaled to the 720-pixel DVD frame; button rectangles are given in DVD pixels.
/// </summary>
public static class DvdStillMenu
{
    private const int DvdWidth = 720;
    private static readonly Color Background1 = Color.FromArgb(14, 14, 16);
    private static readonly Color Background2 = Color.FromArgb(32, 32, 38);
    private static readonly Color Accent = Color.FromArgb(255, 158, 34);
    private static readonly Color ButtonFill = Color.FromArgb(40, 40, 46);
    private static readonly Color ButtonBorder = Color.FromArgb(70, 70, 80);
    private static readonly Color TextColor = Color.FromArgb(236, 236, 236);

    /// <summary>
    /// Draws the menu at DVD resolution (720 x 480 or 576) and lays out its buttons; button order is title by title, Play
    /// first.
    /// </summary>
    public static (Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons) Render(string heading, IReadOnlyList<DvdMenuTitle> titles,
        DvdVideoStandard standard, DvdAspectRatio aspect)
    {
        int height = standard == DvdVideoStandard.Pal ? 576 : 480;
        int width = aspect == DvdAspectRatio.Widescreen16x9 ? height * 16 / 9 : height * 4 / 3;
        var buttons = new List<(int Title, bool Subs, RectangleF Box)>();
        using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Background1, Background2, LinearGradientMode.Vertical))
            {
                g.FillRectangle(bg, 0, 0, width, height);
            }

            // Heading, shrunk to fit, with a short accent rule under it.
            float headingSize = 34;
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            Font headingFont = new("Segoe UI Semibold", headingSize, GraphicsUnit.Pixel);
            while (headingSize > 18 && g.MeasureString(heading, headingFont).Width > width - 80)
            {
                headingFont.Dispose();
                headingSize -= 2;
                headingFont = new Font("Segoe UI Semibold", headingSize, GraphicsUnit.Pixel);
            }
            using (headingFont)
            using (var white = new SolidBrush(TextColor))
            using (var accent = new SolidBrush(Accent))
            {
                g.DrawString(heading, headingFont, white, new RectangleF(40, 22, width - 80, 56), centered);
                g.FillRectangle(accent, width / 2f - 60, 84, 120, 3);
            }

            if (titles.Count <= 3)
            {
                LayOutColumns(g, titles, width, height, buttons);
            }
            else
            {
                LayOutList(g, titles, width, height, buttons);
            }
        }

        // Scale to the DVD frame (anamorphic for 16:9).
        var picture = new Bitmap(DvdWidth, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(picture))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, DvdWidth, height));
        }
        float sx = DvdWidth / (float)width;
        var plans = buttons.Select(b => new DvdMenuButtonPlan(b.Title, b.Subs, Rectangle.FromLTRB(
            (int)Math.Floor(b.Box.Left * sx) - 2, (int)b.Box.Top - 2, (int)Math.Ceiling(b.Box.Right * sx) + 1, (int)b.Box.Bottom + 1))).ToList();
        return (picture, plans);
    }

    /// <summary>One to three titles side by side: still, name, then the buttons stacked under them.</summary>
    private static void LayOutColumns(Graphics g, IReadOnlyList<DvdMenuTitle> titles, int width, int height,
        List<(int, bool, RectangleF)> buttons)
    {
        int n = Math.Max(1, titles.Count);
        const float margin = 40, gap = 30;
        float column = (width - 2 * margin - (n - 1) * gap) / n;
        float buttonW = Math.Min(column, 260), buttonH = 38;
        int maxButtons = titles.Any(t => t.HasSubtitles) ? 2 : 1;
        float below = 12 + 52 + 10 + maxButtons * buttonH + (maxButtons - 1) * 10;
        // Everything between the heading (100) and the bottom of the title-safe area (40 lines up): the still gets
        // what the name and buttons leave.
        float room = height - 40 - 100;
        float stillW = Math.Min(column, n == 1 ? 420 : 300);
        float stillH = Math.Min(stillW * 9 / 16, room - below - 8);
        stillW = stillH * 16 / 9;
        float block = stillH + below;
        float top = 100 + Math.Max(0, (room - block) / 2);

        using var nameFont = new Font("Segoe UI Semibold", 21, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI Semibold", 19, GraphicsUnit.Pixel);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
        using var text = new SolidBrush(TextColor);
        for (int i = 0; i < titles.Count; i++)
        {
            float cx = margin + i * (column + gap) + column / 2;
            var still = new RectangleF(cx - stillW / 2, top, stillW, stillH);
            DrawStill(g, titles[i].StillImagePath, still);
            g.DrawString(titles[i].Name, nameFont, text, new RectangleF(cx - column / 2, still.Bottom + 8, column, 52), centered);
            float y = still.Bottom + 8 + 52 + 10;
            var play = new RectangleF(cx - buttonW / 2, y, buttonW, buttonH);
            DrawButton(g, play, "Play", labelFont, centered);
            buttons.Add((i + 1, false, play));
            if (titles[i].HasSubtitles)
            {
                var subs = new RectangleF(cx - buttonW / 2, y + buttonH + 10, buttonW, buttonH);
                DrawButton(g, subs, "Play with subtitles", labelFont, centered);
                buttons.Add((i + 1, true, subs));
            }
        }
    }

    /// <summary>More than three titles: one row each, name on the left, buttons on the right; at most 8 rows.</summary>
    private static void LayOutList(Graphics g, IReadOnlyList<DvdMenuTitle> titles, int width, int height,
        List<(int, bool, RectangleF)> buttons)
    {
        int rows = Math.Min(8, titles.Count);
        float rowH = Math.Min(44, (height - 130f) / rows);
        using var nameFont = new Font("Segoe UI Semibold", 20, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI Semibold", 17, GraphicsUnit.Pixel);
        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var text = new SolidBrush(TextColor);
        for (int i = 0; i < rows; i++)
        {
            float y = 110 + i * rowH;
            g.DrawString(titles[i].Name, nameFont, text, new RectangleF(50, y, width - 400, rowH - 6), left);
            var play = new RectangleF(width - 330, y + 3, 110, rowH - 10);
            DrawButton(g, play, "Play", labelFont, centered);
            buttons.Add((i + 1, false, play));
            if (titles[i].HasSubtitles)
            {
                var subs = new RectangleF(width - 210, y + 3, 160, rowH - 10);
                DrawButton(g, subs, "Subtitles", labelFont, centered);
                buttons.Add((i + 1, true, subs));
            }
        }
    }

    private static void DrawStill(Graphics g, string? path, RectangleF box)
    {
        using (var frame = new SolidBrush(Color.FromArgb(8, 8, 10)))
        {
            g.FillRectangle(frame, box);
        }
        if (path != null && File.Exists(path))
        {
            try
            {
                using var image = Image.FromFile(path);
                float scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
                float w = image.Width * scale, h = image.Height * scale;
                g.DrawImage(image, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or IOException or ArgumentException)
            {
                // An unreadable still leaves the dark box.
            }
        }
        using var border = new Pen(ButtonBorder, 1);
        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
    }

    private static void DrawButton(Graphics g, RectangleF box, string label, Font font, StringFormat format)
    {
        using var fill = new SolidBrush(ButtonFill);
        using var border = new Pen(ButtonBorder, 1);
        using var text = new SolidBrush(TextColor);
        g.FillRectangle(fill, box);
        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        g.DrawString(label, font, text, box, format);
    }

    /// <summary>
    /// The highlight subpicture: a 3-pixel outline (pixel value 1) around every button on an otherwise empty frame, all
    /// transparent until the player applies a button's colour scheme; displayed forced, with no end.
    /// </summary>
    public static byte[] HighlightSpu(IReadOnlyList<DvdMenuButtonPlan> buttons, int height)
    {
        byte[] pixels = new byte[DvdWidth * height];
        foreach (var b in buttons)
        {
            for (int t = 0; t < 3; t++)
            {
                int x1 = Math.Max(0, b.Bounds.Left + t), x2 = Math.Min(DvdWidth - 1, b.Bounds.Right - t);
                int y1 = Math.Max(0, b.Bounds.Top + t), y2 = Math.Min(height - 1, b.Bounds.Bottom - t);
                for (int x = x1; x <= x2; x++) { pixels[y1 * DvdWidth + x] = 1; pixels[y2 * DvdWidth + x] = 1; }
                for (int y = y1; y <= y2; y++) { pixels[y * DvdWidth + x1] = 1; pixels[y * DvdWidth + x2] = 1; }
            }
        }
        return DvdSubtitleWriter.EncodeSpu(pixels, DvdWidth, height, 0, 0, duration: null, colors: 0x3210, contrast: 0x0000, forced: true);
    }

    /// <summary>
    /// Remote-control neighbours for the buttons in <paramref name="buttons"/> order: up and down within a title's
    /// buttons (wrapping), left and right to the same row of the neighbouring title (or its last button), wrapping.
    /// </summary>
    public static IReadOnlyList<DvdMenuButton> Navigation(IReadOnlyList<DvdMenuButtonPlan> buttons, Func<DvdMenuButtonPlan, byte[]> command)
    {
        var titles = buttons.Select(b => b.Title).Distinct().ToList();
        var result = new List<DvdMenuButton>();
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            var mine = Enumerable.Range(0, buttons.Count).Where(k => buttons[k].Title == b.Title).ToList();
            int row = mine.IndexOf(i);
            int up = mine[(row - 1 + mine.Count) % mine.Count] + 1;
            int down = mine[(row + 1) % mine.Count] + 1;
            int t = titles.IndexOf(b.Title);
            int Side(int step)
            {
                var other = Enumerable.Range(0, buttons.Count).Where(k => buttons[k].Title == titles[(t + step + titles.Count) % titles.Count]).ToList();
                return other[Math.Min(row, other.Count - 1)] + 1;
            }
            result.Add(new DvdMenuButton(b.Bounds.Left, b.Bounds.Top, b.Bounds.Right, b.Bounds.Bottom, up, down, Side(-1), Side(1), command(b)));
        }
        return result;
    }

    /// <summary>
    /// Builds the menu VOB with FFmpeg: the picture as a 1-second still (the menu cell then holds its last frame), silent
    /// AC-3 audio, and the highlight subpicture muxed as subpicture stream 0; fills its NAV packs with the buttons.
    /// Null when FFmpeg fails (the caller falls back to the plain menu).
    /// </summary>
    public static async Task<DvdStillMenuResult?> BuildAsync(string heading, IReadOnlyList<DvdMenuTitle> titles, DvdVideoStandard standard,
        DvdAspectRatio aspect, string ffmpegExe, Func<DvdMenuButtonPlan, byte[]> command, CancellationToken cancellationToken,
        int initialButton = 1)
    {
        string work = Path.Combine(Path.GetTempPath(), "IgnisDisc_Menu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var (picture, plans) = Render(heading, titles, standard, aspect);
            string png = Path.Combine(work, "menu.png");
            using (picture)
            {
                picture.Save(png, ImageFormat.Png);
            }
            int height = standard == DvdVideoStandard.Pal ? 576 : 480;
            string idx = DvdSubtitleWriter.WriteVobSub(HighlightSpu(plans, height), Path.Combine(work, "highlight.sub"), DvdWidth, height);
            string vob = Path.Combine(work, "menu.vob");

            var psi = new ProcessStartInfo { FileName = ffmpegExe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[]
            {
                "-y", "-loop", "1", "-framerate", standard == DvdVideoStandard.Pal ? "25" : "30000/1001", "-i", png,
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-i", idx,
                "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0", "-c:s", "copy", "-t", "1",
                "-target", standard == DvdVideoStandard.Pal ? "pal-dvd" : "ntsc-dvd",
                "-aspect", aspect == DvdAspectRatio.Widescreen16x9 ? "16:9" : "4:3", vob
            })
            {
                psi.ArgumentList.Add(a);
            }
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
            var err = proc.StandardError.ReadToEndAsync(cancellationToken);
            var outp = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(err, outp).ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(vob) || new FileInfo(vob).Length < 16 * OpticalConstants.DataSectorSize)
            {
                return null;
            }

            var buttons = Navigation(plans, command);
            var vobus = DvdNavPackWriter.PatchFile(vob, buttons: buttons, wideButtons: aspect == DvdAspectRatio.Widescreen16x9,
                initialButton: initialButton);
            byte[] bytes = await File.ReadAllBytesAsync(vob, cancellationToken).ConfigureAwait(false);
            return new DvdStillMenuResult(bytes, vobus, plans);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
