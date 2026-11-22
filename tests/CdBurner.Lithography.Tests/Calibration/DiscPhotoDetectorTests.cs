using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Lithography.Tests.Calibration;

/// <summary>
/// Finding the disc in a photo without clicks, and rectifying from what was found. The pictures are drawn here: a
/// dark surface, a bright disc with a centre hole and distracting bands inside it, and a bright table edge below.
/// </summary>
public sealed class DiscPhotoDetectorTests
{
    private const int Width = 1600;
    private const int Height = 1400;

    /// <summary>A disc of the given radii centred at (cx, cy); <paramref name="squash"/> below 1 makes it an ellipse.</summary>
    private static BgraRaster DrawDisc(double cx, double cy, double radius, double holeRadius, double squash = 1.0, bool bandsAndEdge = true)
    {
        byte[] pixels = new byte[Width * Height * 4];
        var random = new Random(12345);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                double dx = x - cx;
                double dy = (y - cy) / squash;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double value = 35 + random.Next(0, 8); // dark surface with grain
                if (bandsAndEdge && y >= 1352 && y <= 1360) value = 200; // a bright table edge below the disc
                if (r <= radius && r >= holeRadius)
                {
                    value = 150;
                    if (bandsAndEdge && r > radius * 0.4 && r < radius * 0.92 && ((int)(r / 14)) % 3 == 0) value = 215;
                }

                int i = (y * Width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)Math.Clamp(value, 0, 255);
                pixels[i + 3] = 255;
            }

        return new BgraRaster(Width, Height, pixels);
    }

    [Fact]
    public void Finds_the_rim_and_the_hole_of_a_disc_on_a_dark_surface()
    {
        DiscPhotoDetection? found = DiscPhotoDetector.Detect(DrawDisc(820, 700, 600, 75));

        Assert.NotNull(found);
        Assert.InRange(found.CenterX, 817, 823);
        Assert.InRange(found.CenterY, 697, 703);
        Assert.InRange(found.OuterRadius, 596, 604);
        Assert.InRange(found.HoleRadius, 70, 80);
        Assert.Equal(48, found.OuterPoints.Count);
        Assert.Equal(48, found.HolePoints.Count);
        foreach (CalibrationPoint p in found.OuterPoints)
            Assert.InRange(Math.Sqrt(Math.Pow(p.X - 820, 2) + Math.Pow(p.Y - 700, 2)), 595, 605);
    }

    [Fact]
    public void Finds_the_rim_of_a_disc_seen_at_a_slight_angle_and_rectifies_it()
    {
        // 0.93: the disc is 7 % shorter than it is wide, like a photo taken a little off the vertical.
        DiscPhotoDetection? found = DiscPhotoDetector.Detect(DrawDisc(800, 690, 600, 75, squash: 0.93));

        Assert.NotNull(found);
        Assert.InRange(found.CenterX, 796, 804);
        Assert.InRange(found.CenterY, 686, 694);
        foreach (CalibrationPoint p in found.OuterPoints)
        {
            double dx = p.X - 800, dy = (p.Y - 690) / 0.93;
            Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 594, 606);
        }

        DiscPhotoRectification result = DiscPhotoRectifier.Fit(found.OuterPoints, found.HolePoints);
        Assert.True(result.Fit.Succeeded, result.Fit.Message);
    }

    [Fact]
    public void Returns_nothing_for_a_picture_with_no_disc()
    {
        byte[] flat = new byte[Width * Height * 4];
        Array.Fill(flat, (byte)90);
        Assert.Null(DiscPhotoDetector.Detect(new BgraRaster(Width, Height, flat)));

        var random = new Random(7);
        byte[] noise = new byte[Width * Height * 4];
        for (int i = 0; i < noise.Length; i += 4) { noise[i] = noise[i + 1] = noise[i + 2] = (byte)random.Next(60, 120); noise[i + 3] = 255; }
        Assert.Null(DiscPhotoDetector.Detect(new BgraRaster(Width, Height, noise)));
    }

    [Fact]
    public void Cuts_the_photo_down_to_the_disc_and_keeps_the_outline_inside_the_cut()
    {
        BgraRaster photo = DrawDisc(820, 700, 600, 75);
        DiscPhotoDetection found = DiscPhotoDetector.Detect(photo)!;

        (BgraRaster cut, int left, int top) = DiscPhotoDetector.CropToDisc(photo, found, margin: 0.05);

        Assert.InRange(cut.Width, 1255, 1275); // 2 x 600 x 1.05
        Assert.InRange(cut.Height, 1255, 1275);
        Assert.InRange(left, 185, 195);
        Assert.InRange(top, 65, 75);
        Assert.Equal(cut.Width * cut.Height * 4, cut.Pixels.Length);
        foreach (CalibrationPoint p in found.OuterPoints)
        {
            Assert.InRange(p.X - left, 0, cut.Width - 1);
            Assert.InRange(p.Y - top, 0, cut.Height - 1);
        }
    }

    [Fact]
    public void A_bevel_ring_larger_than_the_bare_hole_is_fitted_with_its_own_radius()
    {
        // Rim 1430 px for 60 mm; a ring that looks 9.9 mm from the centre, as a bevel or the clamping ring does.
        var outer = Circle(0, 0, 1430, 48);
        var hole = Circle(0, 0, 1430 * 9.9 / 60, 48);

        DiscPhotoRectification result = DiscPhotoRectifier.Fit(outer, hole);

        Assert.True(result.Fit.Succeeded, result.Fit.Message);
        Assert.Equal(DiscPhotoRectificationKind.HoleRadiusFound, result.Kind);
        Assert.InRange(result.HoleRadiusMm, 9.4, 10.4);
    }

    [Fact]
    public void A_hole_outline_no_camera_view_of_a_circle_could_make_is_left_out()
    {
        // Five lobes: a reflection pattern or a ring with a notch, not any view of a circle.
        var outer = Circle(0, 0, 1430, 48);
        var hole = Enumerable.Range(0, 48).Select(i =>
        {
            double angle = 2 * Math.PI * (i + 0.37) / 48;
            double r = 230 + 90 * Math.Cos(5 * angle);
            return new CalibrationPoint(r * Math.Cos(angle), r * Math.Sin(angle));
        }).ToArray();

        DiscPhotoRectification result = DiscPhotoRectifier.Fit(outer, hole);

        Assert.True(result.Fit.Succeeded, result.Fit.Message);
        Assert.Equal(DiscPhotoRectificationKind.RimOnly, result.Kind);
        Assert.Contains("outer rim alone", result.Fit.Message);
    }

    [Fact]
    public void A_missing_hole_outline_is_rectified_from_the_rim()
    {
        DiscPhotoRectification result = DiscPhotoRectifier.Fit(Circle(300, 200, 1400, 48), []);

        Assert.True(result.Fit.Succeeded, result.Fit.Message);
        Assert.Equal(DiscPhotoRectificationKind.RimOnly, result.Kind);
        Assert.Contains("No hole edge was marked", result.Fit.Message);
    }

    [Fact]
    public void A_correct_pair_of_outlines_still_uses_the_standard_hole()
    {
        DiscPhotoRectification result = DiscPhotoRectifier.Fit(Circle(0, 0, 1200, 36), Circle(0, 0, 150, 36));

        Assert.True(result.Fit.Succeeded, result.Fit.Message);
        Assert.Equal(DiscPhotoRectificationKind.Standard, result.Kind);
        Assert.Equal(7.5, result.HoleRadiusMm);
    }

    [Fact]
    public void Too_few_rim_points_are_refused_not_guessed()
    {
        DiscPhotoRectification result = DiscPhotoRectifier.Fit(Circle(0, 0, 1000, 3), []);

        Assert.False(result.Fit.Succeeded);
    }

    private static CalibrationPoint[] Circle(double cx, double cy, double radius, int count)
        => Enumerable.Range(0, count).Select(i =>
        {
            double angle = 2 * Math.PI * (i + 0.37) / count;
            return new CalibrationPoint(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }).ToArray();
}
