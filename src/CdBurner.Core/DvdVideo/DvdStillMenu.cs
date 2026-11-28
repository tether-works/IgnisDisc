using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>Font family preset for rendered DVD still menus.</summary>
public enum DvdMenuFontPreset
{
    ModernSans = 0,
    ClassicSerif = 1,
    CinemaMono = 2
}

/// <summary>Accent and button-highlight color preset for rendered DVD still menus.</summary>
public enum DvdMenuHighlightPreset
{
    Amber = 0,
    Cyan = 1,
    Crimson = 2,
    Emerald = 3,
    Gold = 4
}

/// <summary>Purpose of a button on a DVD menu page.</summary>
public enum DvdMenuButtonKind
{
    PlayTitle = 0,
    NextPage = 1,
    PreviousPage = 2,
    OpenChapters = 3,
    PlayChapter = 4,
    BackToMainMenu = 5
}

/// <summary>Visual and audio personalization options for DVD menus.</summary>
public sealed record DvdMenuStyle(
    DvdMenuFontPreset FontPreset = DvdMenuFontPreset.ModernSans,
    DvdMenuHighlightPreset HighlightPreset = DvdMenuHighlightPreset.Amber,
    string? BackgroundImagePath = null,
    string? MenuAudioPath = null,
    int MenuAudioDurationSeconds = 15);

/// <summary>One chapter entry shown on a per-title chapter selection submenu.</summary>
public sealed record DvdMenuChapterInfo(int ChapterNumber, string Label, TimeSpan StartTime);

/// <summary>A title on the menu: its name, a still picture from it (or null), and whether it has subtitles.</summary>
public sealed record DvdMenuTitle(string Name, string? StillImagePath, bool HasSubtitles);

/// <summary>A button the menu drew: which title it plays, whether it switches subtitles on, and its navigation kind/target.</summary>
public sealed record DvdMenuButtonPlan(int Title, bool WithSubtitles, Rectangle Bounds)
{
    /// <summary>What this button does when activated.</summary>
    public DvdMenuButtonKind Kind { get; init; } = DvdMenuButtonKind.PlayTitle;

    /// <summary>Target page, title, or chapter number associated with <see cref="Kind"/>.</summary>
    public int Target { get; init; }
}

/// <summary>Buttons generated for one page of a multi-page menu.</summary>
public sealed record DvdMenuPageButtons(int PageNumber, IReadOnlyList<DvdMenuButtonPlan> Buttons);

/// <summary>The finished single-page menu VOB, its VOBUs and the buttons in it.</summary>
public sealed record DvdStillMenuResult(byte[] Vob, IReadOnlyList<DvdVobu> Vobus, IReadOnlyList<DvdMenuButtonPlan> Buttons);

/// <summary>Finished multi-page menu VOB (VMGM or VTSM), all VOBUs, per-page cell bounds, and buttons.</summary>
public sealed record DvdMultiPageMenuResult(
    byte[] Vob,
    IReadOnlyList<DvdVobu> Vobus,
    IReadOnlyList<DvdMenuPageCell> Pages,
    IReadOnlyList<DvdMenuPageButtons> PageButtons,
    bool UsedAudioStream,
    bool UsedFfmpeg);

/// <summary>
/// Renders paginated DVD-Video title menus (up to 99 titles) and per-title chapter selection submenus with custom
/// backgrounds, font/highlight presets, optional AC-3 menu audio, and 16:9 + 4:3 letterbox highlight navigation.
/// </summary>
public static class DvdStillMenu
{
    public const int DvdWidth = 720;
    public const int MaxTitles = 99;
    public const int TitlesPerListPage = 8;
    public const int TitlesPerListPageWithChapters = 5;
    public const int ChaptersPerPage = 8;

    private static readonly Color Background1 = Color.FromArgb(14, 14, 16);
    private static readonly Color Background2 = Color.FromArgb(32, 32, 38);
    private static readonly Color ButtonFill = Color.FromArgb(40, 40, 46);
    private static readonly Color ButtonBorder = Color.FromArgb(70, 70, 80);
    private static readonly Color TextColor = Color.FromArgb(236, 236, 236);
    private static readonly Color MutedTextColor = Color.FromArgb(182, 186, 196);

    /// <summary>Number of titles placed on each title-menu list page.</summary>
    public static int GetTitlesPerPage(int titleCount, bool includeChapters)
    {
        int clamped = Math.Clamp(titleCount, 1, MaxTitles);
        if (clamped <= 3)
        {
            return clamped;
        }
        return includeChapters ? TitlesPerListPageWithChapters : TitlesPerListPage;
    }

    /// <summary>Total number of pages in a title menu for <paramref name="titleCount"/> titles.</summary>
    public static int GetTitleMenuPageCount(int titleCount, bool includeChapters = false)
    {
        int clamped = Math.Clamp(titleCount, 1, MaxTitles);
        int perPage = GetTitlesPerPage(clamped, includeChapters);
        return (clamped + perPage - 1) / perPage;
    }

    /// <summary>1-based title menu page containing <paramref name="titleNumber"/>.</summary>
    public static int GetTitleMenuPageForTitle(int titleNumber, int titleCount, bool includeChapters = false)
    {
        int clampedCount = Math.Clamp(titleCount, 1, MaxTitles);
        int clampedTitle = Math.Clamp(titleNumber, 1, clampedCount);
        int perPage = GetTitlesPerPage(clampedCount, includeChapters);
        return (clampedTitle - 1) / perPage + 1;
    }

    /// <summary>Total number of pages in a chapter selection submenu for <paramref name="chapterCount"/> chapters.</summary>
    public static int GetChapterMenuPageCount(int chapterCount)
    {
        int clamped = Math.Clamp(chapterCount, 1, MaxTitles);
        return (clamped + ChaptersPerPage - 1) / ChaptersPerPage;
    }

    /// <summary>Accent RGB color for a <see cref="DvdMenuHighlightPreset"/>.</summary>
    public static Color GetAccentColor(DvdMenuHighlightPreset preset) => preset switch
    {
        DvdMenuHighlightPreset.Cyan => Color.FromArgb(56, 189, 248),
        DvdMenuHighlightPreset.Crimson => Color.FromArgb(240, 78, 35),
        DvdMenuHighlightPreset.Emerald => Color.FromArgb(34, 197, 94),
        DvdMenuHighlightPreset.Gold => Color.FromArgb(250, 204, 21),
        _ => Color.FromArgb(255, 158, 34)
    };

    /// <summary>Font family name for a <see cref="DvdMenuFontPreset"/>.</summary>
    public static string GetFontFamilyName(DvdMenuFontPreset preset) => preset switch
    {
        DvdMenuFontPreset.ClassicSerif => "Georgia",
        DvdMenuFontPreset.CinemaMono => "Consolas",
        _ => "Segoe UI Semibold"
    };

    /// <summary>
    /// Draws a single-page title menu at DVD resolution (720 x 480 or 576) and lays out its buttons.
    /// </summary>
    public static (Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons) Render(string heading, IReadOnlyList<DvdMenuTitle> titles,
        DvdVideoStandard standard, DvdAspectRatio aspect) =>
        RenderTitlePage(heading, titles, pageIndex: 0, standard, aspect, includeChapters: false, style: null);

    /// <summary>
    /// Draws one page (0-based <paramref name="pageIndex"/>) of a paginated title menu at DVD resolution (720 x 480 or 576).
    /// </summary>
    public static (Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons) RenderTitlePage(
        string heading,
        IReadOnlyList<DvdMenuTitle> titles,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        bool includeChapters = false,
        DvdMenuStyle? style = null)
    {
        var (canvas, plans, height) = RenderTitlePageCanvas(heading, titles, pageIndex, standard, aspect, includeChapters, style);
        using (canvas)
        {
            return (ScaleCanvasToDvd(canvas, height, plans, out var dvdPlans), dvdPlans);
        }
    }

    /// <summary>
    /// Renders one page (0-based <paramref name="pageIndex"/>) of a title menu at square-pixel display aspect ratio for live preview.
    /// </summary>
    public static (Bitmap Preview, IReadOnlyList<DvdMenuButtonPlan> Buttons) RenderTitlePagePreview(
        string heading,
        IReadOnlyList<DvdMenuTitle> titles,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        bool includeChapters = false,
        DvdMenuStyle? style = null)
    {
        var (canvas, plans, _) = RenderTitlePageCanvas(heading, titles, pageIndex, standard, aspect, includeChapters, style);
        float sx = DvdWidth / (float)canvas.Width;
        return (canvas, ToDvdPlans(plans, sx));
    }

    /// <summary>
    /// Draws one page (0-based <paramref name="pageIndex"/>) of a per-title chapter selection submenu at DVD resolution (720 x 480 or 576).
    /// </summary>
    public static (Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons) RenderChapterPage(
        string titleHeading,
        int titleNumber,
        int mainMenuPage,
        IReadOnlyList<DvdMenuChapterInfo> chapters,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        DvdMenuStyle? style = null)
    {
        var (canvas, plans, height) = RenderChapterPageCanvas(titleHeading, titleNumber, mainMenuPage, chapters, pageIndex, standard, aspect, style);
        using (canvas)
        {
            return (ScaleCanvasToDvd(canvas, height, plans, out var dvdPlans), dvdPlans);
        }
    }

    /// <summary>
    /// Renders one page (0-based <paramref name="pageIndex"/>) of a chapter selection submenu at square-pixel display aspect ratio for live preview.
    /// </summary>
    public static (Bitmap Preview, IReadOnlyList<DvdMenuButtonPlan> Buttons) RenderChapterPagePreview(
        string titleHeading,
        int titleNumber,
        int mainMenuPage,
        IReadOnlyList<DvdMenuChapterInfo> chapters,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        DvdMenuStyle? style = null)
    {
        var (canvas, plans, _) = RenderChapterPageCanvas(titleHeading, titleNumber, mainMenuPage, chapters, pageIndex, standard, aspect, style);
        float sx = DvdWidth / (float)canvas.Width;
        return (canvas, ToDvdPlans(plans, sx));
    }

    /// <summary>
    /// Scales and letterboxes/pillarboxes a custom background image cleanly to NTSC 720x480 or PAL 720x576,
    /// preserving the image's natural aspect ratio on the target 16:9 or 4:3 DVD frame.
    /// </summary>
    public static Bitmap ScaleAndLetterboxBackground(string imagePath, DvdVideoStandard standard, DvdAspectRatio aspect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        using var source = Image.FromFile(imagePath);
        return ScaleAndLetterboxBackground(source, standard, aspect);
    }

    /// <summary>
    /// Scales and letterboxes/pillarboxes <paramref name="source"/> cleanly to NTSC 720x480 or PAL 720x576.
    /// </summary>
    public static Bitmap ScaleAndLetterboxBackground(Image source, DvdVideoStandard standard, DvdAspectRatio aspect)
    {
        ArgumentNullException.ThrowIfNull(source);
        int height = standard == DvdVideoStandard.Pal ? 576 : 480;
        int canvasWidth = aspect == DvdAspectRatio.Widescreen16x9 ? height * 16 / 9 : height * 4 / 3;
        using var canvas = new Bitmap(canvasWidth, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Black);
            DrawScaledImage(g, source, canvasWidth, height);
        }

        var dvd = new Bitmap(DvdWidth, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dvd))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, DvdWidth, height));
        }
        return dvd;
    }

    private static (Bitmap Canvas, List<CanvasButtonPlan> Plans, int Height) RenderTitlePageCanvas(
        string heading,
        IReadOnlyList<DvdMenuTitle> titles,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        bool includeChapters,
        DvdMenuStyle? style)
    {
        style ??= new DvdMenuStyle();
        int height = standard == DvdVideoStandard.Pal ? 576 : 480;
        int width = aspect == DvdAspectRatio.Widescreen16x9 ? height * 16 / 9 : height * 4 / 3;
        var buttons = new List<CanvasButtonPlan>();
        var cappedTitles = titles.Take(MaxTitles).ToList();
        int totalPages = GetTitleMenuPageCount(cappedTitles.Count, includeChapters);
        int clampedPage = Math.Clamp(pageIndex, 0, Math.Max(0, totalPages - 1));

        var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            ConfigureGraphics(g);
            DrawBackground(g, width, height, style);
            DrawHeading(g, heading, width, style);

            if (cappedTitles.Count <= 3)
            {
                LayOutColumns(g, cappedTitles, width, height, includeChapters, style, buttons);
            }
            else
            {
                int perPage = GetTitlesPerPage(cappedTitles.Count, includeChapters);
                int startIdx = clampedPage * perPage;
                var pageSlice = cappedTitles.Skip(startIdx).Take(perPage).ToList();
                LayOutList(g, pageSlice, startTitleNumber: startIdx + 1, clampedPage, totalPages, width, height, includeChapters, style, buttons);
            }
        }
        return (canvas, buttons, height);
    }

    private static (Bitmap Canvas, List<CanvasButtonPlan> Plans, int Height) RenderChapterPageCanvas(
        string titleHeading,
        int titleNumber,
        int mainMenuPage,
        IReadOnlyList<DvdMenuChapterInfo> chapters,
        int pageIndex,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        DvdMenuStyle? style)
    {
        style ??= new DvdMenuStyle();
        int height = standard == DvdVideoStandard.Pal ? 576 : 480;
        int width = aspect == DvdAspectRatio.Widescreen16x9 ? height * 16 / 9 : height * 4 / 3;
        var buttons = new List<CanvasButtonPlan>();
        var normalizedChapters = chapters.Count > 0
            ? chapters.Take(MaxTitles).ToList()
            : [new DvdMenuChapterInfo(1, "Chapter 1", TimeSpan.Zero)];

        int totalPages = GetChapterMenuPageCount(normalizedChapters.Count);
        int clampedPage = Math.Clamp(pageIndex, 0, Math.Max(0, totalPages - 1));
        var pageChapters = normalizedChapters.Skip(clampedPage * ChaptersPerPage).Take(ChaptersPerPage).ToList();

        var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            ConfigureGraphics(g);
            DrawBackground(g, width, height, style);
            string heading = string.IsNullOrWhiteSpace(titleHeading) ? "Chapters" : $"{titleHeading} — Chapters";
            DrawHeading(g, heading, width, style);

            string fontName = GetFontFamilyName(style.FontPreset);
            using var labelFont = new Font(fontName, 17, GraphicsUnit.Pixel);
            using var pageFont = new Font(fontName, 15, GraphicsUnit.Pixel);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            using var muted = new SolidBrush(MutedTextColor);

            const float margin = 50f;
            const float colGap = 28f;
            float colWidth = (width - 2 * margin - colGap) / 2f;
            float topY = 108f;
            float navBarTop = height - 80f;
            float availableH = navBarTop - 32f - topY;
            float rowStride = Math.Min(54f, availableH / 4f);
            float btnHeight = Math.Max(34f, rowStride - 10f);

            for (int i = 0; i < pageChapters.Count; i++)
            {
                int col = i / 4;
                int row = i % 4;
                float x = margin + col * (colWidth + colGap);
                float y = topY + row * rowStride;
                var ch = pageChapters[i];
                var box = new RectangleF(x, y, colWidth, btnHeight);
                string timeText = ch.StartTime.TotalHours >= 1
                    ? ch.StartTime.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                    : ch.StartTime.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
                string text = string.IsNullOrWhiteSpace(ch.Label)
                    ? $"Chapter {ch.ChapterNumber}  ({timeText})"
                    : $"{ch.Label}  ({timeText})";
                DrawButton(g, box, text, labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(titleNumber, false, box, DvdMenuButtonKind.PlayChapter, ch.ChapterNumber));
            }

            if (totalPages > 1)
            {
                g.DrawString($"Page {clampedPage + 1} of {totalPages}", pageFont, muted,
                    new RectangleF(margin, navBarTop - 26f, width - 2 * margin, 22f), centered);
            }

            const float navH = 34f;
            if (clampedPage > 0)
            {
                var prevBox = new RectangleF(margin, navBarTop, 130f, navH);
                DrawButton(g, prevBox, "< Previous", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(0, false, prevBox, DvdMenuButtonKind.PreviousPage, clampedPage));
            }

            float backW = Math.Min(230f, width - 360f);
            var backBox = new RectangleF(width / 2f - backW / 2f, navBarTop, backW, navH);
            DrawButton(g, backBox, "Back to Main Menu", labelFont, centered, style);
            buttons.Add(new CanvasButtonPlan(0, false, backBox, DvdMenuButtonKind.BackToMainMenu, Math.Max(1, mainMenuPage)));

            if (clampedPage < totalPages - 1)
            {
                var nextBox = new RectangleF(width - margin - 130f, navBarTop, 130f, navH);
                DrawButton(g, nextBox, "Next >", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(0, false, nextBox, DvdMenuButtonKind.NextPage, clampedPage + 2));
            }
        }

        return (canvas, buttons, height);
    }

    private static Bitmap ScaleCanvasToDvd(Bitmap canvas, int height, List<CanvasButtonPlan> buttons, out IReadOnlyList<DvdMenuButtonPlan> dvdPlans)
    {
        var picture = new Bitmap(DvdWidth, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(picture))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, DvdWidth, height));
        }
        float sx = DvdWidth / (float)canvas.Width;
        dvdPlans = ToDvdPlans(buttons, sx);
        return picture;
    }

    private static IReadOnlyList<DvdMenuButtonPlan> ToDvdPlans(IReadOnlyList<CanvasButtonPlan> buttons, float sx) =>
        buttons.Select(b => new DvdMenuButtonPlan(b.Title, b.WithSubtitles, Rectangle.FromLTRB(
            (int)Math.Floor(b.Box.Left * sx) - 2,
            (int)b.Box.Top - 2,
            (int)Math.Ceiling(b.Box.Right * sx) + 1,
            (int)b.Box.Bottom + 1))
        {
            Kind = b.Kind,
            Target = b.Target
        }).ToList();

    private static void ConfigureGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private static void DrawBackground(Graphics g, int width, int height, DvdMenuStyle style)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Background1, Background2, LinearGradientMode.Vertical))
        {
            g.FillRectangle(bg, 0, 0, width, height);
        }

        if (!string.IsNullOrWhiteSpace(style.BackgroundImagePath) && File.Exists(style.BackgroundImagePath))
        {
            try
            {
                using var image = Image.FromFile(style.BackgroundImagePath);
                g.Clear(Color.Black);
                DrawScaledImage(g, image, width, height);
                using var scrim = new SolidBrush(Color.FromArgb(115, 10, 10, 14));
                g.FillRectangle(scrim, 0, 0, width, height);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or IOException or ArgumentException)
            {
                // Corrupt or unreadable background falls back to default dark gradient.
            }
        }
    }

    private static void DrawScaledImage(Graphics g, Image image, int targetWidth, int targetHeight)
    {
        float scale = Math.Min((float)targetWidth / image.Width, (float)targetHeight / image.Height);
        float w = image.Width * scale;
        float h = image.Height * scale;
        float x = (targetWidth - w) / 2f;
        float y = (targetHeight - h) / 2f;
        g.DrawImage(image, x, y, w, h);
    }

    private static void DrawHeading(Graphics g, string heading, int width, DvdMenuStyle style)
    {
        string fontName = GetFontFamilyName(style.FontPreset);
        Color accentColor = GetAccentColor(style.HighlightPreset);
        float headingSize = 34;
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
        Font headingFont = new(fontName, headingSize, GraphicsUnit.Pixel);
        while (headingSize > 18 && g.MeasureString(heading, headingFont).Width > width - 80)
        {
            headingFont.Dispose();
            headingSize -= 2;
            headingFont = new Font(fontName, headingSize, GraphicsUnit.Pixel);
        }
        using (headingFont)
        using (var white = new SolidBrush(TextColor))
        using (var accent = new SolidBrush(accentColor))
        {
            g.DrawString(heading, headingFont, white, new RectangleF(40, 22, width - 80, 56), centered);
            g.FillRectangle(accent, width / 2f - 60, 84, 120, 3);
        }
    }

    /// <summary>One to three titles side by side: still, name, then the buttons stacked under them.</summary>
    private static void LayOutColumns(Graphics g, IReadOnlyList<DvdMenuTitle> titles, int width, int height,
        bool includeChapters, DvdMenuStyle style, List<CanvasButtonPlan> buttons)
    {
        int n = Math.Max(1, titles.Count);
        const float margin = 40, gap = 30;
        float column = (width - 2 * margin - (n - 1) * gap) / n;
        int maxButtons = (titles.Any(t => t.HasSubtitles) ? 2 : 1) + (includeChapters ? 1 : 0);
        float buttonW = Math.Min(column, 260), buttonH = maxButtons >= 3 ? 34 : 38, btnGap = maxButtons >= 3 ? 8 : 10;
        float below = 12 + 52 + 10 + maxButtons * buttonH + (maxButtons - 1) * btnGap;
        float room = height - 40 - 100;
        float stillW = Math.Min(column, n == 1 ? 420 : 300);
        float stillH = Math.Min(stillW * 9 / 16, room - below - 8);
        stillW = stillH * 16 / 9;
        float block = stillH + below;
        float top = 100 + Math.Max(0, (room - block) / 2);

        string fontName = GetFontFamilyName(style.FontPreset);
        using var nameFont = new Font(fontName, 21, GraphicsUnit.Pixel);
        using var labelFont = new Font(fontName, maxButtons >= 3 ? 17 : 19, GraphicsUnit.Pixel);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
        using var text = new SolidBrush(TextColor);
        for (int i = 0; i < titles.Count; i++)
        {
            int titleNumber = i + 1;
            float cx = margin + i * (column + gap) + column / 2;
            var still = new RectangleF(cx - stillW / 2, top, stillW, stillH);
            DrawStill(g, titles[i].StillImagePath, still);
            g.DrawString(titles[i].Name, nameFont, text, new RectangleF(cx - column / 2, still.Bottom + 8, column, 52), centered);
            float y = still.Bottom + 8 + 52 + 10;
            var play = new RectangleF(cx - buttonW / 2, y, buttonW, buttonH);
            DrawButton(g, play, "Play", labelFont, centered, style);
            buttons.Add(new CanvasButtonPlan(titleNumber, false, play, DvdMenuButtonKind.PlayTitle, titleNumber));
            y += buttonH + btnGap;
            if (titles[i].HasSubtitles)
            {
                var subs = new RectangleF(cx - buttonW / 2, y, buttonW, buttonH);
                DrawButton(g, subs, "Play with subtitles", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(titleNumber, true, subs, DvdMenuButtonKind.PlayTitle, titleNumber));
                y += buttonH + btnGap;
            }
            if (includeChapters)
            {
                var chapters = new RectangleF(cx - buttonW / 2, y, buttonW, buttonH);
                DrawButton(g, chapters, "Chapters", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(titleNumber, false, chapters, DvdMenuButtonKind.OpenChapters, titleNumber));
            }
        }
    }

    /// <summary>Paginated list layout for 4 to 99 titles with optional Chapters and Next/Previous buttons.</summary>
    private static void LayOutList(Graphics g, IReadOnlyList<DvdMenuTitle> pageTitles, int startTitleNumber,
        int pageIndex, int totalPages, int width, int height, bool includeChapters, DvdMenuStyle style, List<CanvasButtonPlan> buttons)
    {
        int rows = Math.Max(1, pageTitles.Count);
        float bottomReserve = totalPages > 1 ? 92f : 44f;
        float rowH = Math.Min(44f, (height - 106f - bottomReserve) / Math.Max(rows, includeChapters ? TitlesPerListPageWithChapters : TitlesPerListPage));
        rowH = Math.Max(34f, rowH);

        string fontName = GetFontFamilyName(style.FontPreset);
        using var nameFont = new Font(fontName, 19, GraphicsUnit.Pixel);
        using var labelFont = new Font(fontName, 16, GraphicsUnit.Pixel);
        using var pageFont = new Font(fontName, 15, GraphicsUnit.Pixel);
        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var text = new SolidBrush(TextColor);
        using var muted = new SolidBrush(MutedTextColor);

        float rightReserve = includeChapters ? 395f : 340f;
        float nameWidth = Math.Max(120f, width - 50f - rightReserve);

        for (int i = 0; i < pageTitles.Count; i++)
        {
            int titleNumber = startTitleNumber + i;
            float y = 104f + i * rowH;
            float btnH = Math.Max(26f, rowH - 10f);
            float btnY = y + 3f;
            string displayName = $"{titleNumber}. {pageTitles[i].Name}";
            g.DrawString(displayName, nameFont, text, new RectangleF(50f, y, nameWidth, rowH - 4f), left);

            if (includeChapters)
            {
                var chapters = new RectangleF(width - 155f, btnY, 105f, btnH);
                if (pageTitles[i].HasSubtitles)
                {
                    var play = new RectangleF(width - 375f, btnY, 90f, btnH);
                    var subs = new RectangleF(width - 275f, btnY, 110f, btnH);
                    DrawButton(g, play, "Play", labelFont, centered, style);
                    DrawButton(g, subs, "Subtitles", labelFont, centered, style);
                    DrawButton(g, chapters, "Chapters", labelFont, centered, style);
                    buttons.Add(new CanvasButtonPlan(titleNumber, false, play, DvdMenuButtonKind.PlayTitle, titleNumber));
                    buttons.Add(new CanvasButtonPlan(titleNumber, true, subs, DvdMenuButtonKind.PlayTitle, titleNumber));
                    buttons.Add(new CanvasButtonPlan(titleNumber, false, chapters, DvdMenuButtonKind.OpenChapters, titleNumber));
                }
                else
                {
                    var play = new RectangleF(width - 275f, btnY, 110f, btnH);
                    DrawButton(g, play, "Play", labelFont, centered, style);
                    DrawButton(g, chapters, "Chapters", labelFont, centered, style);
                    buttons.Add(new CanvasButtonPlan(titleNumber, false, play, DvdMenuButtonKind.PlayTitle, titleNumber));
                    buttons.Add(new CanvasButtonPlan(titleNumber, false, chapters, DvdMenuButtonKind.OpenChapters, titleNumber));
                }
            }
            else
            {
                var play = new RectangleF(width - 330f, btnY, 110f, btnH);
                DrawButton(g, play, "Play", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(titleNumber, false, play, DvdMenuButtonKind.PlayTitle, titleNumber));
                if (pageTitles[i].HasSubtitles)
                {
                    var subs = new RectangleF(width - 210f, btnY, 160f, btnH);
                    DrawButton(g, subs, "Subtitles", labelFont, centered, style);
                    buttons.Add(new CanvasButtonPlan(titleNumber, true, subs, DvdMenuButtonKind.PlayTitle, titleNumber));
                }
            }
        }

        if (totalPages > 1)
        {
            float navY = height - 80f;
            const float navH = 32f;
            g.DrawString($"Page {pageIndex + 1} of {totalPages}", pageFont, muted,
                new RectangleF(190f, navY, width - 380f, navH), centered);

            if (pageIndex > 0)
            {
                var prev = new RectangleF(50f, navY, 130f, navH);
                DrawButton(g, prev, "< Previous", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(0, false, prev, DvdMenuButtonKind.PreviousPage, pageIndex));
            }
            if (pageIndex < totalPages - 1)
            {
                var next = new RectangleF(width - 180f, navY, 130f, navH);
                DrawButton(g, next, "Next >", labelFont, centered, style);
                buttons.Add(new CanvasButtonPlan(0, false, next, DvdMenuButtonKind.NextPage, pageIndex + 2));
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

    private static void DrawButton(Graphics g, RectangleF box, string label, Font font, StringFormat format, DvdMenuStyle style)
    {
        Color accent = GetAccentColor(style.HighlightPreset);
        using var fill = new SolidBrush(ButtonFill);
        using var border = new Pen(Color.FromArgb(140, accent.R, accent.G, accent.B), 1);
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
    /// Remote-control neighbours for the buttons in <paramref name="buttons"/> order.
    /// Uses column navigation for 1-3 title Play-only menus, and 2D spatial row/column navigation for paginated,
    /// multi-action, and chapter submenus.
    /// </summary>
    public static IReadOnlyList<DvdMenuButton> Navigation(IReadOnlyList<DvdMenuButtonPlan> buttons, Func<DvdMenuButtonPlan, byte[]> command)
    {
        if (buttons.Count == 0)
        {
            return Array.Empty<DvdMenuButton>();
        }

        bool simpleColumnMenu = buttons.All(b => b.Kind == DvdMenuButtonKind.PlayTitle)
            && buttons.Select(b => b.Title).Distinct().Count() <= 3;

        if (simpleColumnMenu)
        {
            var titles = buttons.Select(b => b.Title).Distinct().ToList();
            var result = new List<DvdMenuButton>(buttons.Count);
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

        return SpatialNavigation(buttons, command);
    }

    private static IReadOnlyList<DvdMenuButton> SpatialNavigation(IReadOnlyList<DvdMenuButtonPlan> buttons, Func<DvdMenuButtonPlan, byte[]> command)
    {
        static int CenterX(DvdMenuButtonPlan b) => (b.Bounds.Left + b.Bounds.Right) / 2;
        static int CenterY(DvdMenuButtonPlan b) => (b.Bounds.Top + b.Bounds.Bottom) / 2;

        // Group buttons into horizontal rows sorted top-to-bottom, each row sorted left-to-right.
        var ordered = Enumerable.Range(0, buttons.Count)
            .OrderBy(i => CenterY(buttons[i]))
            .ThenBy(i => CenterX(buttons[i]))
            .ToList();

        var rows = new List<List<int>>();
        foreach (int idx in ordered)
        {
            int cy = CenterY(buttons[idx]);
            if (rows.Count > 0 && Math.Abs(CenterY(buttons[rows[^1][0]]) - cy) <= 18)
            {
                rows[^1].Add(idx);
            }
            else
            {
                rows.Add([idx]);
            }
        }
        foreach (var row in rows)
        {
            row.Sort((a, b) => CenterX(buttons[a]).CompareTo(CenterX(buttons[b])));
        }

        var rowOf = new int[buttons.Count];
        var colOf = new int[buttons.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                rowOf[rows[r][c]] = r;
                colOf[rows[r][c]] = c;
            }
        }

        int NearestInRow(int targetRow, int targetCx) =>
            rows[targetRow].MinBy(idx => Math.Abs(CenterX(buttons[idx]) - targetCx)) + 1;

        var result = new List<DvdMenuButton>(buttons.Count);
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            int r = rowOf[i];
            int c = colOf[i];
            int cx = CenterX(b);
            var row = rows[r];

            int up = NearestInRow((r - 1 + rows.Count) % rows.Count, cx);
            int down = NearestInRow((r + 1) % rows.Count, cx);
            int left = row[(c - 1 + row.Count) % row.Count] + 1;
            int right = row[(c + 1) % row.Count] + 1;

            result.Add(new DvdMenuButton(b.Bounds.Left, b.Bounds.Top, b.Bounds.Right, b.Bounds.Bottom, up, down, left, right, command(b)));
        }
        return result;
    }

    /// <summary>
    /// Builds FFmpeg CLI arguments for encoding a menu still frame, highlight subpicture, and either silent or custom AC-3 audio.
    /// </summary>
    public static IReadOnlyList<string> BuildFfmpegMenuArguments(
        string pngPath,
        string idxPath,
        string outputVobPath,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        string? menuAudioPath = null,
        int audioDurationSeconds = 15)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(idxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVobPath);

        string fps = standard == DvdVideoStandard.Pal ? "25" : "30000/1001";
        string target = standard == DvdVideoStandard.Pal ? "pal-dvd" : "ntsc-dvd";
        string aspectStr = aspect == DvdAspectRatio.Widescreen16x9 ? "16:9" : "4:3";
        bool useCustomAudio = !string.IsNullOrWhiteSpace(menuAudioPath) && File.Exists(menuAudioPath);

        var args = new List<string>
        {
            "-y",
            "-loop", "1",
            "-framerate", fps,
            "-i", pngPath
        };

        if (useCustomAudio)
        {
            int duration = Math.Clamp(audioDurationSeconds, 1, 60);
            args.AddRange([
                "-stream_loop", "-1",
                "-i", menuAudioPath!,
                "-i", idxPath,
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-map", "2:s:0",
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-target", target,
                "-aspect", aspectStr,
                "-c:a", "ac3",
                "-b:a", "192k",
                "-ar", "48000",
                "-ac", "2",
                "-c:s", "copy",
                outputVobPath
            ]);
        }
        else
        {
            args.AddRange([
                "-f", "lavfi",
                "-i", "anullsrc=r=48000:cl=stereo",
                "-i", idxPath,
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-map", "2:s:0",
                "-c:s", "copy",
                "-t", "1",
                "-target", target,
                "-aspect", aspectStr,
                outputVobPath
            ]);
        }

        return args;
    }

    /// <summary>
    /// Builds a single-page menu VOB with FFmpeg and patches its NAV packs. Returns null when FFmpeg fails.
    /// </summary>
    public static async Task<DvdStillMenuResult?> BuildAsync(string heading, IReadOnlyList<DvdMenuTitle> titles, DvdVideoStandard standard,
        DvdAspectRatio aspect, string ffmpegExe, Func<DvdMenuButtonPlan, byte[]> command, CancellationToken cancellationToken,
        int initialButton = 1)
    {
        var single = await BuildSinglePageWithFfmpegAsync(
            ffmpegExe,
            () => Render(heading, titles, standard, aspect),
            standard,
            aspect,
            command,
            menuAudioPath: null,
            audioDurationSeconds: 1,
            initialButton,
            cancellationToken).ConfigureAwait(false);
        return single;
    }

    /// <summary>
    /// Builds a paginated title menu VOB (up to 99 titles) using FFmpeg when available or deterministic synthetic VOB
    /// segments otherwise, patching all NAV packs with Next/Previous LinkPGCN, Play, and optional Chapters buttons.
    /// </summary>
    public static Task<DvdMultiPageMenuResult> BuildMultiPageTitleMenuAsync(
        string heading,
        IReadOnlyList<DvdMenuTitle> titles,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        bool includeChapters,
        DvdMenuStyle? style,
        string? ffmpegExe,
        Func<DvdMenuButtonPlan, int, byte[]> commandForPage,
        CancellationToken cancellationToken,
        int page1InitialButton = 1)
    {
        var capped = titles.Take(MaxTitles).ToList();
        int totalPages = GetTitleMenuPageCount(capped.Count, includeChapters);
        return BuildMultiPageMenuCoreAsync(
            totalPages,
            pageIndex => RenderTitlePage(heading, capped, pageIndex, standard, aspect, includeChapters, style),
            standard,
            aspect,
            style,
            ffmpegExe,
            commandForPage,
            pageIndex => pageIndex == 0 ? page1InitialButton : 1,
            cancellationToken);
    }

    /// <summary>
    /// Builds a paginated per-title chapter selection submenu VOB (VTS_xx_0.VOB) using FFmpeg when available or
    /// deterministic synthetic VOB segments otherwise.
    /// </summary>
    public static Task<DvdMultiPageMenuResult> BuildMultiPageChapterMenuAsync(
        string titleHeading,
        int titleNumber,
        int mainMenuPage,
        IReadOnlyList<DvdMenuChapterInfo> chapters,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        DvdMenuStyle? style,
        string? ffmpegExe,
        Func<DvdMenuButtonPlan, int, byte[]> commandForPage,
        CancellationToken cancellationToken)
    {
        var normalized = chapters.Count > 0
            ? chapters.Take(MaxTitles).ToList()
            : [new DvdMenuChapterInfo(1, "Chapter 1", TimeSpan.Zero)];
        int totalPages = GetChapterMenuPageCount(normalized.Count);
        return BuildMultiPageMenuCoreAsync(
            totalPages,
            pageIndex => RenderChapterPage(titleHeading, titleNumber, mainMenuPage, normalized, pageIndex, standard, aspect, style),
            standard,
            aspect,
            style,
            ffmpegExe,
            commandForPage,
            _ => 1,
            cancellationToken);
    }

    private static async Task<DvdMultiPageMenuResult> BuildMultiPageMenuCoreAsync(
        int totalPages,
        Func<int, (Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons)> renderPage,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        DvdMenuStyle? style,
        string? ffmpegExe,
        Func<DvdMenuButtonPlan, int, byte[]> commandForPage,
        Func<int, int> initialButtonForPage,
        CancellationToken cancellationToken)
    {
        style ??= new DvdMenuStyle();
        var segments = new List<byte[]>(totalPages);
        var cellPatches = new List<DvdMenuCellPatch>(totalPages);
        var pageButtons = new List<DvdMenuPageButtons>(totalPages);
        int sectorCursor = 0;
        bool usedFfmpeg = false;
        bool hasCustomAudio = !string.IsNullOrWhiteSpace(style.MenuAudioPath) && File.Exists(style.MenuAudioPath);
        bool usedAudioStream = false;

        for (int p = 0; p < totalPages; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageNumber = p + 1;
            var (picture, plans) = renderPage(p);
            IReadOnlyList<DvdMenuButton> navButtons = Navigation(plans, btn => commandForPage(btn, pageNumber));

            byte[]? pageVob = null;
            if (!string.IsNullOrWhiteSpace(ffmpegExe) && File.Exists(ffmpegExe))
            {
                using (picture)
                {
                    pageVob = await EncodePageWithFfmpegAsync(
                        ffmpegExe,
                        picture,
                        plans,
                        standard,
                        aspect,
                        hasCustomAudio ? style.MenuAudioPath : null,
                        style.MenuAudioDurationSeconds,
                        cancellationToken).ConfigureAwait(false);

                    if (pageVob == null && hasCustomAudio)
                    {
                        pageVob = await EncodePageWithFfmpegAsync(
                            ffmpegExe,
                            picture,
                            plans,
                            standard,
                            aspect,
                            menuAudioPath: null,
                            audioDurationSeconds: 1,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (pageVob != null && hasCustomAudio)
                    {
                        usedAudioStream = true;
                    }
                }
                if (pageVob != null)
                {
                    usedFfmpeg = true;
                }
            }
            else
            {
                picture.Dispose();
            }

            if (pageVob == null)
            {
                pageVob = DvdMenuGenerator.GenerateSyntheticMenuVob(new DvdMenuOptions
                {
                    Standard = standard,
                    AspectRatio = aspect
                }, sectorCount: 16);
                if (hasCustomAudio)
                {
                    usedAudioStream = true;
                }
            }

            int sectorCount = pageVob.Length / OpticalConstants.DataSectorSize;
            segments.Add(pageVob);
            cellPatches.Add(new DvdMenuCellPatch(
                StartSector: sectorCursor,
                SectorCount: sectorCount,
                VobId: pageNumber,
                CellId: 1,
                Buttons: navButtons,
                InitialButton: initialButtonForPage(p)));
            pageButtons.Add(new DvdMenuPageButtons(pageNumber, plans));
            sectorCursor += sectorCount;
        }

        byte[] combinedVob = new byte[sectorCursor * OpticalConstants.DataSectorSize];
        int byteOffset = 0;
        foreach (byte[] seg in segments)
        {
            Buffer.BlockCopy(seg, 0, combinedVob, byteOffset, seg.Length);
            byteOffset += seg.Length;
        }

        using var ms = new MemoryStream(combinedVob, writable: true);
        var (patchedCells, allVobus) = DvdNavPackWriter.PatchMultiCellMenu(
            ms,
            cellPatches,
            wideButtons: aspect == DvdAspectRatio.Widescreen16x9);
        var pageCells = DvdIfoGenerator.CreateMenuPageCells(patchedCells);

        return new DvdMultiPageMenuResult(combinedVob, allVobus, pageCells, pageButtons, usedAudioStream, usedFfmpeg);
    }

    private static async Task<DvdStillMenuResult?> BuildSinglePageWithFfmpegAsync(
        string ffmpegExe,
        Func<(Bitmap Picture, IReadOnlyList<DvdMenuButtonPlan> Buttons)> render,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        Func<DvdMenuButtonPlan, byte[]> command,
        string? menuAudioPath,
        int audioDurationSeconds,
        int initialButton,
        CancellationToken cancellationToken)
    {
        var (picture, plans) = render();
        byte[]? vobBytes;
        using (picture)
        {
            vobBytes = await EncodePageWithFfmpegAsync(ffmpegExe, picture, plans, standard, aspect, menuAudioPath, audioDurationSeconds, cancellationToken).ConfigureAwait(false);
        }
        if (vobBytes == null)
        {
            return null;
        }
        var buttons = Navigation(plans, command);
        using var ms = new MemoryStream(vobBytes, writable: true);
        var vobus = DvdNavPackWriter.Patch(ms, buttons: buttons, wideButtons: aspect == DvdAspectRatio.Widescreen16x9, initialButton: initialButton);
        return new DvdStillMenuResult(vobBytes, vobus, plans);
    }

    private static async Task<byte[]?> EncodePageWithFfmpegAsync(
        string ffmpegExe,
        Bitmap picture,
        IReadOnlyList<DvdMenuButtonPlan> plans,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        string? menuAudioPath,
        int audioDurationSeconds,
        CancellationToken cancellationToken)
    {
        string work = Path.Combine(Path.GetTempPath(), "IgnisDisc_Menu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string png = Path.Combine(work, "menu.png");
            picture.Save(png, ImageFormat.Png);
            int height = standard == DvdVideoStandard.Pal ? 576 : 480;
            string idx = DvdSubtitleWriter.WriteVobSub(HighlightSpu(plans, height), Path.Combine(work, "highlight.sub"), DvdWidth, height);
            string vob = Path.Combine(work, "menu.vob");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string a in BuildFfmpegMenuArguments(png, idx, vob, standard, aspect, menuAudioPath, audioDurationSeconds))
            {
                psi.ArgumentList.Add(a);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
            var err = proc.StandardError.ReadToEndAsync(cancellationToken);
            var outp = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            try
            {
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(err, outp).ConfigureAwait(false);
            }
            catch
            {
                CdBurner.Core.Utilities.ProcessAndFileCleanup.KillTreeAndWait(proc);
                try { await Task.WhenAll(err, outp).ConfigureAwait(false); } catch { }
                throw;
            }
            if (proc.ExitCode != 0 || !File.Exists(vob) || new FileInfo(vob).Length < 16 * OpticalConstants.DataSectorSize)
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(vob, cancellationToken).ConfigureAwait(false);
            int aligned = bytes.Length / OpticalConstants.DataSectorSize * OpticalConstants.DataSectorSize;
            if (aligned != bytes.Length)
            {
                Array.Resize(ref bytes, aligned);
            }
            return bytes;
        }
        finally
        {
            CdBurner.Core.Utilities.ProcessAndFileCleanup.TryDeleteDirectoryWithRetry(work);
        }
    }

    private readonly record struct CanvasButtonPlan(int Title, bool WithSubtitles, RectangleF Box, DvdMenuButtonKind Kind, int Target);
}
