using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.DiscArt;

/// <summary>What the picture is sized against: the whole disc, or the ring that is actually drawn.</summary>
public enum DiscArtFit
{
    /// <summary>The picture's shorter side spans the disc diameter (what a picture disc did before placement existed).</summary>
    Disc,

    /// <summary>The picture's shorter side spans the diameter of the outer edge of the drawn ring, so the picture's
    /// edges touch the ring's outer radius. Nothing is drawn inside the ring's inner radius.</summary>
    Ring,
}

/// <summary>
/// Where the picture sits on the disc. Coordinates are as seen on the finished disc or its preview: x to the right,
/// y downwards, millimetres from the disc centre; a positive rotation turns the picture clockwise.
/// Order of operations: size (<see cref="Fit"/> and <see cref="Scale"/>), rotate about the picture's centre, move.
/// </summary>
public sealed record DiscArtTransform(
    double Scale = 1,
    double OffsetXMm = 0,
    double OffsetYMm = 0,
    double RotationDegrees = 0,
    DiscArtFit Fit = DiscArtFit.Disc)
{
    public const double MinimumScale = 0.01;
    public const double MaximumScale = 100;
    public const double MaximumOffsetMm = 500;

    public static DiscArtTransform Default { get; } = new();

    /// <summary>True when the picture goes to the encoder untouched (the same bytes as before placement existed).</summary>
    public bool IsIdentity => Scale == 1 && OffsetXMm == 0 && OffsetYMm == 0 && RotationDegrees == 0 && Fit == DiscArtFit.Disc;

    public void Validate()
    {
        if (!double.IsFinite(Scale) || Scale < MinimumScale || Scale > MaximumScale)
        {
            throw new ArgumentException($"The picture scale must be between {MinimumScale} and {MaximumScale}.");
        }

        if (!double.IsFinite(OffsetXMm) || !double.IsFinite(OffsetYMm)
            || Math.Abs(OffsetXMm) > MaximumOffsetMm || Math.Abs(OffsetYMm) > MaximumOffsetMm)
        {
            throw new ArgumentException($"The picture offset must be within {MaximumOffsetMm} mm of the disc centre.");
        }

        if (!double.IsFinite(RotationDegrees))
        {
            throw new ArgumentException("The rotation must be a finite number of degrees.");
        }

        if (!Enum.IsDefined(Fit))
        {
            throw new ArgumentException("Unknown fit; use disc or ring.");
        }
    }
}

/// <summary>
/// Renders a picture with a <see cref="DiscArtTransform"/> onto a square canvas whose width is the disc diameter
/// (2 x <c>discRadiusMm</c>), so an encoder that maps its picture across the disc puts every pixel where the transform
/// says. The canvas outside the picture is transparent white: an encoder that reads alpha leaves those sectors alone,
/// one that ignores alpha draws white, as it does outside a picture today.
/// </summary>
public static class DiscArtCanvas
{
    public const int MinimumCanvasPixels = 1024;
    public const int MaximumCanvasPixels = 4096;

    /// <summary>Canvas width in pixels for a picture: its larger side, kept within 1,024 to 4,096.</summary>
    public static int CanvasSize(BgraRaster picture)
        => Math.Clamp(Math.Max(picture.Width, picture.Height), MinimumCanvasPixels, MaximumCanvasPixels);

    /// <summary>Millimetres one pixel of the canvas covers.</summary>
    public static double MillimetresPerPixel(int canvasSize, double discRadiusMm) => 2.0 * discRadiusMm / canvasSize;

    public static BgraRaster Render(BgraRaster picture, DiscArtTransform transform, double discRadiusMm,
        double ringOuterRadiusMm, int canvasSize = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(transform);
        picture.Validate();
        transform.Validate();
        if (!double.IsFinite(discRadiusMm) || discRadiusMm <= 0)
        {
            throw new ArgumentException("The disc radius must be positive.");
        }

        if (!double.IsFinite(ringOuterRadiusMm) || ringOuterRadiusMm <= 0)
        {
            throw new ArgumentException("The ring's outer radius must be positive.");
        }

        int n = canvasSize > 0 ? canvasSize : CanvasSize(picture);
        double mmPerCanvasPixel = MillimetresPerPixel(n, discRadiusMm);
        double baseRadius = transform.Fit == DiscArtFit.Ring ? ringOuterRadiusMm : discRadiusMm;
        // Millimetres one picture pixel covers: the shorter side spans 2 x baseRadius at scale 1.
        double mmPerPicturePixel = 2.0 * baseRadius / Math.Min(picture.Width, picture.Height) * transform.Scale;
        double radians = transform.RotationDegrees * (Math.PI / 180.0);
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        bool nearest = IsOpaqueBlackAndWhite(picture);

        byte[] canvas = new byte[checked(n * n * 4)];
        for (int cy = 0; cy < n; cy++)
        {
            if ((cy & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            double v = (cy + 0.5 - n / 2.0) * mmPerCanvasPixel - transform.OffsetYMm;
            for (int cx = 0; cx < n; cx++)
            {
                double u = (cx + 0.5 - n / 2.0) * mmPerCanvasPixel - transform.OffsetXMm;
                // Undo the clockwise rotation (x right, y down), then convert millimetres to picture pixels.
                double px = (u * cos + v * sin) / mmPerPicturePixel + picture.Width / 2.0 - 0.5;
                double py = (-u * sin + v * cos) / mmPerPicturePixel + picture.Height / 2.0 - 0.5;
                WritePixel(canvas, (cy * n + cx) * 4, picture, px, py, nearest);
            }
        }

        return new BgraRaster(n, n, canvas);
    }

    private static void WritePixel(byte[] canvas, int offset, BgraRaster picture, double px, double py, bool nearest)
    {
        int w = picture.Width, h = picture.Height;
        byte[] src = picture.Pixels;
        if (px < -0.5 || py < -0.5 || px > w - 0.5 || py > h - 0.5)
        {
            canvas[offset] = canvas[offset + 1] = canvas[offset + 2] = 255;
            canvas[offset + 3] = 0;
            return;
        }

        if (nearest)
        {
            int x = Math.Clamp((int)Math.Floor(px + 0.5), 0, w - 1);
            int y = Math.Clamp((int)Math.Floor(py + 0.5), 0, h - 1);
            Buffer.BlockCopy(src, (y * w + x) * 4, canvas, offset, 4);
            return;
        }

        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double fx = px - x0, fy = py - y0;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        double alpha = 0, b = 0, g = 0, r = 0;
        Add(x0, y0, (1 - fx) * (1 - fy));
        Add(x1, y0, fx * (1 - fy));
        Add(x0, y1, (1 - fx) * fy);
        Add(x1, y1, fx * fy);
        if (alpha <= 0)
        {
            canvas[offset] = canvas[offset + 1] = canvas[offset + 2] = 255;
            canvas[offset + 3] = 0;
            return;
        }

        // Colours were weighted by alpha (straight alpha in, so a transparent pixel adds no colour).
        canvas[offset] = (byte)Math.Clamp((int)Math.Round(b / alpha), 0, 255);
        canvas[offset + 1] = (byte)Math.Clamp((int)Math.Round(g / alpha), 0, 255);
        canvas[offset + 2] = (byte)Math.Clamp((int)Math.Round(r / alpha), 0, 255);
        canvas[offset + 3] = (byte)Math.Clamp((int)Math.Round(alpha), 0, 255);

        void Add(int x, int y, double weight)
        {
            int o = (y * w + x) * 4;
            double a = weight * src[o + 3];
            alpha += a;
            b += a * src[o];
            g += a * src[o + 1];
            r += a * src[o + 2];
        }
    }

    /// <summary>A dithered picture (only pure black and pure white, fully opaque) keeps its two levels: blending would
    /// invent greys and change how much of the disc is drawn white.</summary>
    private static bool IsOpaqueBlackAndWhite(BgraRaster picture)
    {
        byte[] p = picture.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            if (p[i + 3] != 255 || (p[i] != 0 && p[i] != 255) || p[i + 1] != p[i] || p[i + 2] != p[i])
            {
                return false;
            }
        }

        return true;
    }
}
