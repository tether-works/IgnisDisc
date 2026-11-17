using System.IO;
using System.Security.Cryptography;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Placing the picture (scale, move, rotate, fit). The known answers are geometry worked out by hand from the
/// definition (x right, y down, millimetres from the centre, rotation clockwise), then read back from the canvas and from
/// the preview of a generated DVD image: the preview is what a drive would show, so a marked pixel must show up at the
/// computed position.
/// </summary>
public sealed class DiscArtTransformTests : IDisposable
{
    private const int PictureSize = 64;
    private const double DiscRadius = 58;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-artplace-" + Guid.NewGuid().ToString("N"));

    private static readonly CalibrationParameters TinyDvd = new(CalibrationDiscKind.Dvd, 24, 58, 4096, 133.3);
    private static readonly CalibrationParameters TinyBd = new(CalibrationDiscKind.BluRay, 24, 58, 4096,
        CalibrationParameters.BluRayChannelBitLengthNm);
    private static readonly CalibrationParameters TinyCd = new(CalibrationDiscKind.Cd, 24.5, 56.8, 6000, 1200);

    public DiscArtTransformTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>64 x 64 black picture with a 4 x 4 white mark whose centre is (dx, dy) picture pixels from the picture's centre.</summary>
    private static BgraRaster MarkedPicture(int dx, int dy)
    {
        byte[] pixels = new byte[PictureSize * PictureSize * 4];
        for (int i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 255;
        for (int y = PictureSize / 2 + dy - 2; y < PictureSize / 2 + dy + 2; y++)
            for (int x = PictureSize / 2 + dx - 2; x < PictureSize / 2 + dx + 2; x++)
            {
                int o = (y * PictureSize + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = 255;
            }

        return new BgraRaster(PictureSize, PictureSize, pixels);
    }

    private string WritePicture(string name, BgraRaster raster)
    {
        string path = P(name);
        PngRaster.SaveBgra32(path, raster);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Median position (mm from the disc centre, x right, y down) of the bright pixels of a read-back preview inside the drawn ring.</summary>
    private static (double X, double Y, int Count) BrightCentre(BgraRaster preview, double aroundX, double aroundY)
    {
        double centre = (preview.Width - 1) / 2.0;
        double mmPerPixel = DiscRadius / centre;
        var xs = new List<double>();
        var ys = new List<double>();
        for (int y = 0; y < preview.Height; y++)
            for (int x = 0; x < preview.Width; x++)
            {
                double dx = (x - centre) * mmPerPixel, dy = (y - centre) * mmPerPixel;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 27 || r > 55 || Math.Abs(dx - aroundX) > 9 || Math.Abs(dy - aroundY) > 9 || preview.Pixels[(y * preview.Width + x) * 4] < 128) continue;
                xs.Add(dx);
                ys.Add(dy);
            }

        // Only a window around the expected position counts: the drawing leaves stray bright dots between the turns, and a
        // moved picture leaves part of the ring without picture (random data there). The median ignores the dots.
        xs.Sort();
        ys.Sort();
        return xs.Count == 0 ? (0, 0, 0) : (xs[xs.Count / 2], ys[ys.Count / 2], xs.Count);
    }

    private (double X, double Y, int Count) GenerateAndRead(BgraRaster picture, DiscArtTransform? transform, double aroundX, double aroundY)
    {
        string png = WritePicture("mark.png", picture);
        string iso = P("mark-" + Guid.NewGuid().ToString("N")[..6] + ".iso");
        DiscArtImageBuilder.Generate(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd, iso, Transform: transform));
        string preview = P("preview.png");
        DiscArtImageBuilder.Preview(iso, preview, 256);
        return BrightCentre(PngRaster.Load(preview), aroundX, aroundY);
    }

    // Read-back of a generated image ----------------------------------------------------------------------------------

    [Fact]
    public void Dvd_default_transform_gives_the_same_image_bytes_as_no_transform()
    {
        BgraRaster picture = MarkedPicture(20, 0);
        string png = WritePicture("same.png", picture);
        foreach ((DiscArtMedium medium, CalibrationParameters disc, string name) in new[]
        {
            (DiscArtMedium.Dvd, TinyDvd, "dvd.iso"), (DiscArtMedium.BluRay, TinyBd, "bd.iso"), (DiscArtMedium.Cd, TinyCd, "cd.wav"),
        })
        {
            string plain = P("plain-" + name), placed = P("placed-" + name), explicitDefault = P("default-" + name);
            DiscArtImageBuilder.Generate(new DiscArtRequest(medium, png, disc, plain));
            DiscArtImageBuilder.Generate(new DiscArtRequest(medium, png, disc, placed, Transform: DiscArtTransform.Default));
            DiscArtImageBuilder.Generate(new DiscArtRequest(medium, png, disc, explicitDefault,
                Transform: new DiscArtTransform(1, 0, 0, 0, DiscArtFit.Disc)));
            Assert.Equal(Sha(plain), Sha(placed));
            Assert.Equal(Sha(plain), Sha(explicitDefault));
            Assert.False(File.Exists(placed + ".canvas.png"));
        }
    }

    [Fact]
    public void Dvd_a_moved_picture_changes_the_image_and_leaves_no_temporary_files()
    {
        string png = WritePicture("moved.png", MarkedPicture(20, 0));
        string plain = P("plain.iso"), moved = P("moved.iso");
        DiscArtImageBuilder.Generate(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd, plain));
        DiscArtResult result = DiscArtImageBuilder.Generate(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd, moved,
            Transform: new DiscArtTransform(OffsetXMm: 5)));

        Assert.NotEqual(Sha(plain), Sha(moved));
        Assert.Equal(new[] { "moved.iso", "moved.iso.disc-art.json" },
            Directory.GetFiles(_dir, "moved.iso*").Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.Contains("\"offsetXMm\": 5", File.ReadAllText(result.SidecarPath));
        // A default placement writes no transform into the description.
        Assert.DoesNotContain("transform", File.ReadAllText(plain + ".disc-art.json"));
    }

    [Fact]
    public void Dvd_rotating_90_degrees_moves_a_mark_from_the_right_to_below_the_centre()
    {
        // Mark 20 picture pixels right of centre: 20 x (116 mm / 64 px) = 36.25 mm. Turned clockwise by 90 degrees it
        // lies 36.25 mm straight below the centre.
        const double expected = 20 * 2 * DiscRadius / PictureSize;

        (double x, double y, int count) = GenerateAndRead(MarkedPicture(20, 0), new DiscArtTransform(RotationDegrees: 90), 0, expected);

        Assert.True(count > 20, $"bright pixels: {count}");
        Assert.InRange(x, -2.5, 2.5);
        Assert.InRange(y, expected - 2.5, expected + 2.5);
    }

    [Fact]
    public void Dvd_without_a_transform_shows_the_mark_where_the_picture_has_it()
    {
        const double expected = 20 * 2 * DiscRadius / PictureSize;

        (double x, double y, _) = GenerateAndRead(MarkedPicture(20, 0), null, expected, 0);

        Assert.InRange(x, expected - 2.5, expected + 2.5);
        Assert.InRange(y, -2.5, 2.5);
    }

    [Fact]
    public void Dvd_scaling_by_2_doubles_the_distance_of_a_mark_from_the_centre()
    {
        // 12 px = 21.75 mm; at 2x it is 43.5 mm from the centre, still inside the 24 to 58 mm ring.
        const double expected = 2 * 12 * 2 * DiscRadius / PictureSize;

        (double x, double y, int count) = GenerateAndRead(MarkedPicture(12, 0), new DiscArtTransform(Scale: 2), expected, 0);

        Assert.True(count > 20, $"bright pixels: {count}");
        Assert.InRange(x, expected - 2.5, expected + 2.5);
        Assert.InRange(y, -2.5, 2.5);
    }

    [Fact]
    public void Dvd_moving_the_picture_moves_the_mark_by_the_same_millimetres()
    {
        // Mark 24 px right (43.5 mm); move 8 mm left and 6 mm down.
        const double markX = 24 * 2 * DiscRadius / PictureSize;

        (double x, double y, _) = GenerateAndRead(MarkedPicture(24, 0), new DiscArtTransform(OffsetXMm: -8, OffsetYMm: 6), markX - 8, 6);

        Assert.InRange(x, markX - 8 - 2.5, markX - 8 + 2.5);
        Assert.InRange(y, 6 - 2.5, 6 + 2.5);
    }

    // The canvas itself ------------------------------------------------------------------------------------------------

    /// <summary>Millimetres from the centre of the centre of the opaque white pixels of a canvas.</summary>
    private static (double X, double Y) WhiteCentre(BgraRaster canvas, double discRadius)
    {
        double mm = DiscArtCanvas.MillimetresPerPixel(canvas.Width, discRadius);
        double sx = 0, sy = 0;
        int n = 0;
        for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                int o = (y * canvas.Width + x) * 4;
                if (canvas.Pixels[o + 3] != 255 || canvas.Pixels[o] != 255) continue;
                sx += (x + 0.5 - canvas.Width / 2.0) * mm;
                sy += (y + 0.5 - canvas.Height / 2.0) * mm;
                n++;
            }

        Assert.True(n > 0, "no white pixels on the canvas");
        return (sx / n, sy / n);
    }

    [Theory]
    [InlineData(0, 36.25, 0)]       // no rotation: right of centre
    [InlineData(90, 0, 36.25)]       // clockwise: right of centre -> below
    [InlineData(180, -36.25, 0)]
    [InlineData(270, 0, -36.25)]
    public void Canvas_rotation_puts_a_mark_at_the_expected_millimetres(double degrees, double expectedX, double expectedY)
    {
        BgraRaster canvas = DiscArtCanvas.Render(MarkedPicture(20, 0), new DiscArtTransform(RotationDegrees: degrees), DiscRadius, DiscRadius);

        (double x, double y) = WhiteCentre(canvas, DiscRadius);

        // Quarter turns are exact on the pixel grid; allow one canvas pixel (0.11 mm) plus the mark's half pixel rounding.
        Assert.InRange(x, expectedX - 0.5, expectedX + 0.5);
        Assert.InRange(y, expectedY - 0.5, expectedY + 0.5);
    }

    [Fact]
    public void Canvas_scale_and_offset_apply_scale_first_then_the_move()
    {
        // Mark at (+12 px, -8 px) = (21.75 mm, -14.5 mm). Scale 2 -> (43.5, -29); offset (5, 3) -> (48.5, -26).
        BgraRaster canvas = DiscArtCanvas.Render(MarkedPicture(12, -8), new DiscArtTransform(2, 5, 3), DiscRadius, DiscRadius);

        (double x, double y) = WhiteCentre(canvas, DiscRadius);

        Assert.InRange(x, 48.5 - 0.5, 48.5 + 0.5);
        Assert.InRange(y, -26 - 0.5, -26 + 0.5);
    }

    [Fact]
    public void Fit_ring_puts_the_picture_edges_on_the_outer_radius_of_the_ring()
    {
        // A fully white picture; the drawn ring ends at 40 mm although the disc is 58 mm.
        byte[] white = new byte[PictureSize * PictureSize * 4];
        Array.Fill(white, (byte)255);
        var picture = new BgraRaster(PictureSize, PictureSize, white);

        BgraRaster ring = DiscArtCanvas.Render(picture, new DiscArtTransform(Fit: DiscArtFit.Ring), DiscRadius, 40);
        BgraRaster disc = DiscArtCanvas.Render(picture, DiscArtTransform.Default, DiscRadius, 40);

        Assert.Equal(40, OpaqueHalfWidthMm(ring), 0.3);
        Assert.Equal(DiscRadius, OpaqueHalfWidthMm(disc), 0.3);
    }

    [Fact]
    public void Fit_ring_with_a_scale_multiplies_the_ring_size()
    {
        byte[] white = new byte[PictureSize * PictureSize * 4];
        Array.Fill(white, (byte)255);
        var picture = new BgraRaster(PictureSize, PictureSize, white);

        BgraRaster canvas = DiscArtCanvas.Render(picture, new DiscArtTransform(Scale: 0.5, Fit: DiscArtFit.Ring), DiscRadius, 40);

        Assert.Equal(20, OpaqueHalfWidthMm(canvas), 0.3);
    }

    /// <summary>Half the width, in millimetres, of the opaque part of the middle row.</summary>
    private static double OpaqueHalfWidthMm(BgraRaster canvas)
    {
        int row = canvas.Height / 2;
        int first = -1, last = -1;
        for (int x = 0; x < canvas.Width; x++)
        {
            if (canvas.Pixels[(row * canvas.Width + x) * 4 + 3] == 0) continue;
            if (first < 0) first = x;
            last = x;
        }

        return (last - first + 1) * DiscArtCanvas.MillimetresPerPixel(canvas.Width, DiscRadius) / 2;
    }

    [Fact]
    public void Fit_ring_on_a_generated_picture_uses_the_radius_the_plan_reports()
    {
        // With files on the inner part the ring starts further out; the picture is sized to the plan's outer radius.
        string folder = P("files");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "A.BIN"), new byte[200_000]);
        string png = WritePicture("ring.png", MarkedPicture(10, 0));
        DiscArtResult result = DiscArtImageBuilder.Generate(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd, P("ring.iso"),
            FilesFolder: folder, Transform: new DiscArtTransform(Fit: DiscArtFit.Ring)));

        Assert.True(result.Plan.PictureInnerRadiusMm > TinyDvd.InnerRadiusMm);
        Assert.Equal(58, result.Plan.PictureOuterRadiusMm, 3);
        Assert.True(File.Exists(P("ring.iso")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(double.NaN)]
    public void A_scale_out_of_range_is_refused_in_plain_words(double scale)
    {
        string png = WritePicture("bad.png", MarkedPicture(0, 0));
        var ex = Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(
            new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd, P("bad.iso"), Transform: new DiscArtTransform(Scale: scale))));
        Assert.Contains("scale", ex.Message);
    }

    [Fact]
    public void An_offset_or_rotation_that_is_not_a_number_is_refused()
    {
        string png = WritePicture("bad2.png", MarkedPicture(0, 0));
        Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd,
            P("a.iso"), Transform: new DiscArtTransform(OffsetXMm: double.PositiveInfinity))));
        Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Plan(new DiscArtRequest(DiscArtMedium.Dvd, png, TinyDvd,
            P("b.iso"), Transform: new DiscArtTransform(RotationDegrees: double.NaN))));
    }

    [Theory]
    [InlineData(DiscArtMedium.BluRay)]
    [InlineData(DiscArtMedium.Cd)]
    public void Bd_and_cd_accept_a_placed_picture(DiscArtMedium medium)
    {
        string png = WritePicture("other.png", MarkedPicture(20, 0));
        string output = P(medium == DiscArtMedium.Cd ? "placed.wav" : "placed.iso");
        string plain = P(medium == DiscArtMedium.Cd ? "plain.wav" : "plain.iso");
        CalibrationParameters disc = medium == DiscArtMedium.Cd ? TinyCd : TinyBd;

        DiscArtImageBuilder.Generate(new DiscArtRequest(medium, png, disc, plain));
        DiscArtResult result = DiscArtImageBuilder.Generate(new DiscArtRequest(medium, png, disc, output,
            Transform: new DiscArtTransform(1, 0, 0, 90)));

        Assert.NotEqual(Sha(plain), Sha(output));
        Assert.Equal(new FileInfo(plain).Length, result.BytesWritten);
        Assert.False(File.Exists(output + ".canvas.png"));
    }
}
