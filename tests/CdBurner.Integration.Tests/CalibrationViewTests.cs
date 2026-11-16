using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The calibration screen: fits of upstream's published records must equal <c>disc-art calibrate fit</c>, points
/// clicked on a photo taken at an angle must come out in disc millimetres, and the screen must draw the dots where
/// the clicks were. No drive is used.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class CalibrationViewTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamCalibration");
    private const string CoarseRecord = "verbatim-azo-20261104--calibration-4f0228e6.calibration.json";
    private const string BoundaryRecord = "verbatim-azo-20261104--calibration-3f2ce8c2.calibration.json";
    private const string FineRecord = "verbatim-azo-20261104--calibration-14580aac.calibration.json";

    // Synthetic camera: disc millimetres (y down) -> photo pixels, with perspective (a tilted view).
    private const int PhotoWidth = 1400;
    private const int PhotoHeight = 1300;
    private static readonly double[] Camera =
    [
        8.0 * Math.Cos(0.17), -8.0 * Math.Sin(0.17), 700,
        8.0 * Math.Sin(0.17) * 0.9, 8.0 * Math.Cos(0.17) * 0.9, 650,
        0.0015, -0.0010, 1,
    ];

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-calibration-view-" + Guid.NewGuid().ToString("N"));

    public CalibrationViewTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CalibrationPoint ToPhoto(CalibrationPoint mm)
    {
        double w = Camera[6] * mm.X + Camera[7] * mm.Y + Camera[8];
        return new CalibrationPoint((Camera[0] * mm.X + Camera[1] * mm.Y + Camera[2]) / w,
            (Camera[3] * mm.X + Camera[4] * mm.Y + Camera[5]) / w);
    }

    private static IEnumerable<CalibrationPoint> Circle(double radiusMm, int count, double startDegrees)
        => Enumerable.Range(0, count).Select(i =>
        {
            double angle = (startDegrees + 360.0 * i / count) * Math.PI / 180;
            return new CalibrationPoint(radiusMm * Math.Cos(angle), radiusMm * Math.Sin(angle));
        });

    private static byte[] GrayPhotoPng()
    {
        byte[] pixels = new byte[PhotoWidth * PhotoHeight * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 0x40;
            pixels[i + 3] = 0xFF;
        }

        using var stream = new MemoryStream();
        PngRaster.Write(stream, new BgraRaster(PhotoWidth, PhotoHeight, pixels));
        return stream.ToArray();
    }

    /// <summary>A record whose photo is the synthetic one, rim and hole marked and rectified.</summary>
    private static CalibrationViewModel RectifiedOnSyntheticPhoto(string recordFile)
    {
        var vm = new CalibrationViewModel();
        Assert.True(vm.OpenRecord(Path.Combine(FixtureDir, recordFile)), vm.StatusMessage);
        Assert.True(vm.LoadPhoto(GrayPhotoPng()), vm.StatusMessage);
        Assert.Equal(0, vm.MeasurePointCount); // the record's points belong to its own photo

        vm.ClickMode = CalibrationClickMode.OuterRim;
        foreach (CalibrationPoint p in Circle(60, 10, 7).Select(ToPhoto)) Assert.True(vm.AddPoint(p.X, p.Y), vm.StatusMessage);
        vm.ClickMode = CalibrationClickMode.CenterHole;
        foreach (CalibrationPoint p in Circle(7.5, 8, 20).Select(ToPhoto)) Assert.True(vm.AddPoint(p.X, p.Y), vm.StatusMessage);
        Assert.True(vm.Rectify(), vm.StatusMessage);
        Assert.True(vm.IsRectified);
        Assert.Equal(CalibrationClickMode.Measure, vm.ClickMode);
        return vm;
    }

    private static async Task<JsonElement> CliFitAsync(string recordPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        await CliCommandRunner.RunAsync(["disc-art", "calibrate", "fit", "--points", recordPath, "--json"],
            stdout, stderr, devices, new VirtualBurnEngine());
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Compares the screen's fit with the command line's. <paramref name="allCandidates"/> compares every candidate;
    /// otherwise only the applied one (points clicked through a camera are not bit-identical to the recorded ones,
    /// so a weaker extra candidate may come and go).
    /// </summary>
    private static void AssertSameAsCli(JsonElement cli, CalibrationViewModel vm, double radiusTolerance,
        double pitchTolerance, bool allCandidates)
    {
        CalibrationRecordFit fit = Assert.IsType<CalibrationRecordFit>(vm.LastFit);
        Assert.Equal(cli.GetProperty("pointCount").GetInt32(), fit.PointCount);
        Assert.Equal(cli.GetProperty("fitSucceeded").GetBoolean(), fit.Result.Succeeded);
        JsonElement[] candidates = cli.GetProperty("candidates").EnumerateArray().ToArray();
        if (allCandidates) Assert.Equal(candidates.Length, vm.Candidates.Count);
        for (int i = 0; i < (allCandidates ? candidates.Length : 1); i++)
        {
            JsonElement p = candidates[i].GetProperty("parameters");
            CalibrationParameters mine = vm.Candidates[i].Candidate.Parameters;
            Assert.Equal(p.GetProperty("innerRadiusMm").GetDouble(), mine.InnerRadiusMm, radiusTolerance);
            Assert.Equal(p.GetProperty("outerRadiusMm").GetDouble(), mine.OuterRadiusMm, radiusTolerance);
            Assert.Equal(p.GetProperty("pitchLinear").GetDouble(), mine.PitchLinear, pitchTolerance);
            Assert.Equal(p.GetProperty("pitchQuadratic").GetDouble(), mine.PitchQuadratic, pitchTolerance);
            Assert.Equal(p.GetProperty("pitchCubic").GetDouble(), mine.PitchCubic, pitchTolerance);
        }

        // The candidate applied without a choice is the same one the command line applies (or none for both).
        int? cliChosen = cli.TryGetProperty("chosenCandidate", out JsonElement chosen) && chosen.ValueKind == JsonValueKind.Number
            ? chosen.GetInt32() : null;
        Assert.Equal(cliChosen, vm.SelectedCandidate?.Number);
    }

    [Theory]
    [InlineData(CoarseRecord)]
    [InlineData(FineRecord)]
    public async Task A_record_saved_by_the_screen_fits_on_the_command_line_to_the_screen_result(string recordFile)
    {
        var vm = new CalibrationViewModel();
        Assert.True(vm.OpenRecord(Path.Combine(FixtureDir, recordFile)), vm.StatusMessage);
        Assert.True(vm.IsRectified);
        await vm.FitAsync();
        Assert.NotNull(vm.LastFit);

        string saved = Path.Combine(_tempDir, recordFile);
        Assert.True(vm.SaveRecord(saved), vm.StatusMessage);
        AssertSameAsCli(await CliFitAsync(saved), vm, 1e-9, 1e-12, allCandidates: true);

        CalibrationParameters applied = Assert.IsType<CalibrationParameters>(vm.SelectedCandidate?.Candidate.Parameters);
        if (recordFile == FineRecord)
        {
            // Upstream docs/CALIBRATION_TUTORIAL.md: the fine fit and its coefficient set B.
            Assert.Equal(23.99144094121467, applied.InnerRadiusMm, 1e-9);
            Assert.Equal(57.964899248503315, applied.OuterRadiusMm, 1e-9);
            Assert.Equal(-8.817669151956023E-05, applied.PitchCubic, 1e-12);
        }
        else
        {
            // Upstream docs/CALIBRATION_TUTORIAL.md: wide-band estimate of the coarse record.
            Assert.Equal(24.016692871476, applied.InnerRadiusMm, 1e-9);
            Assert.Equal(57.7092422445, applied.OuterRadiusMm, 1e-9);
        }
    }

    [Fact]
    public void A_boundary_record_saved_by_the_screen_gives_the_fitter_the_recorded_segments_unchanged()
    {
        // Fitting a boundary record takes over a minute; DiscArtCalibrateCliTests fits this record once against
        // upstream's published radii. Here the screen must hand the fitter exactly the recorded input.
        string original = Path.Combine(FixtureDir, BoundaryRecord);
        var vm = new CalibrationViewModel();
        Assert.True(vm.OpenRecord(original), vm.StatusMessage);
        Assert.Equal(CalibrationMeasureStage.BoundarySegments, vm.MeasureStage);
        string saved = Path.Combine(_tempDir, BoundaryRecord);
        Assert.True(vm.SaveRecord(saved), vm.StatusMessage);

        CalibrationSession before = CalibrationSessionJson.Load(original);
        CalibrationSession after = CalibrationSessionJson.Load(saved);
        Assert.Equal(CalibrationSessionJson.Serialize(before with { ConfirmedFit = null }), CalibrationSessionJson.Serialize(after));
        Assert.Equal(154, after.Photo!.BoundarySegments!.Sum(segment => segment.Points.Count));
    }

    [Fact]
    public async Task Fine_points_clicked_on_a_tilted_photo_fit_like_the_recorded_points_and_save_per_drive_and_media()
    {
        CalibrationSession recorded = CalibrationSessionJson.Load(Path.Combine(FixtureDir, FineRecord));
        CalibrationPoint[] recordedTrace = recorded.Photo!.Traces.Single().Points.ToArray();
        CalibrationViewModel vm = RectifiedOnSyntheticPhoto(FineRecord);

        // The rectification puts the rim and hole on circles of 60 mm and 7.5 mm.
        foreach (CalibrationPoint p in Circle(60, 7, 3).Concat(Circle(7.5, 7, 3)))
        {
            CalibrationPoint back = vm.Transform!.SourceToDisc(ToPhoto(p));
            Assert.Equal(Math.Sqrt(p.X * p.X + p.Y * p.Y), Math.Sqrt(back.X * back.X + back.Y * back.Y), 6);
        }

        // A stray click, removed with Undo.
        CalibrationPoint stray = ToPhoto(new CalibrationPoint(30, 30));
        Assert.True(vm.AddPoint(stray.X, stray.Y));
        vm.Undo();
        Assert.Equal(0, vm.MeasurePointCount);
        foreach (CalibrationPoint p in recordedTrace.Select(ToPhoto)) Assert.True(vm.AddPoint(p.X, p.Y), vm.StatusMessage);
        Assert.Equal(recordedTrace.Length, vm.MeasurePointCount);

        // A circle carries no direction, so the clicked points may come out turned about the centre; radii and
        // the angles between points are what the photo fixes.
        CalibrationPoint[] measured = vm.BuildSession().Photo!.Traces.Single().Points.ToArray();
        double turn = Math.Atan2(measured[0].Y, measured[0].X) - Math.Atan2(recordedTrace[0].Y, recordedTrace[0].X);
        for (int i = 0; i < measured.Length; i++)
        {
            Assert.Equal(Math.Sqrt(recordedTrace[i].X * recordedTrace[i].X + recordedTrace[i].Y * recordedTrace[i].Y),
                Math.Sqrt(measured[i].X * measured[i].X + measured[i].Y * measured[i].Y), 6);
            double expected = Math.Atan2(recordedTrace[i].Y, recordedTrace[i].X) + turn;
            Assert.Equal(0, Math.IEEERemainder(Math.Atan2(measured[i].Y, measured[i].X) - expected, 2 * Math.PI), 6);
        }

        await vm.FitAsync();
        AssertSameAsCli(await CliFitAsync(Path.Combine(FixtureDir, FineRecord)), vm, 1e-4, 1e-6, allCandidates: false);

        vm.DriveModel = "  Example Drive DVR-1 ";
        vm.MediaId = "MCC 03RG20";
        vm.StorePath = Path.Combine(_tempDir, "store", CalibrationStore.FileName);
        Assert.True(vm.SaveToStore(), vm.StatusMessage);
        CalibrationStoreEntry entry = Assert.Single(CalibrationStore.Load(vm.StorePath).Entries);
        Assert.Equal("Example Drive DVR-1", entry.DriveModel);
        Assert.Equal("MCC 03RG20", entry.MediaId);
        Assert.Equal("fine", entry.Stage);
        Assert.Equal(recorded.Target.Id, entry.TargetId);
        Assert.Equal(vm.SelectedCandidate!.Candidate.Parameters, entry.Parameters);

        // Moving an outline point drops the rectification; rectifying again keeps the measured points in place.
        vm.ClickMode = CalibrationClickMode.OuterRim;
        vm.Undo();
        Assert.False(vm.IsRectified);
        Assert.Null(vm.LastFit);
        Assert.True(vm.Rectify(), vm.StatusMessage);
        CalibrationPoint[] again = vm.BuildSession().Photo!.Traces.Single().Points.ToArray();
        for (int i = 0; i < again.Length; i++)
        {
            CalibrationPoint pixel = vm.Transform!.DiscToSource(again[i]);
            CalibrationPoint clicked = ToPhoto(recordedTrace[i]);
            Assert.Equal(clicked.X, pixel.X, 6);
            Assert.Equal(clicked.Y, pixel.Y, 6);
        }
    }

    [Fact]
    public void Band_edges_clicked_in_any_order_get_upstreams_edge_numbers()
    {
        CalibrationSession recorded = CalibrationSessionJson.Load(Path.Combine(FixtureDir, CoarseRecord));
        CalibrationRingObservation[] edges = recorded.Photo!.RingObservations!.ToArray();
        Assert.Equal(6, edges.Length);
        CalibrationViewModel vm = RectifiedOnSyntheticPhoto(CoarseRecord);
        Assert.Equal(CalibrationMeasureStage.BandEdges, vm.MeasureStage);

        foreach (CalibrationRingObservation edge in edges.Reverse())
        {
            CalibrationPoint p = ToPhoto(edge.Points!.Single());
            Assert.True(vm.AddPoint(p.X, p.Y), vm.StatusMessage);
        }

        CalibrationPoint extra = ToPhoto(new CalibrationPoint(40, 0));
        Assert.False(vm.AddPoint(extra.X, extra.Y)); // all edges marked
        CalibrationRingObservation[] numbered = vm.BuildSession().Photo!.RingObservations!.ToArray();
        foreach (CalibrationRingObservation edge in edges)
        {
            CalibrationRingObservation mine = Assert.Single(numbered, o => Math.Abs(o.RadiusMm - edge.RadiusMm) < 1e-4);
            Assert.Equal(edge.RingIndex, mine.RingIndex);
        }
    }

    [Fact]
    public void Boundary_points_go_into_segments_and_undo_removes_points_then_an_empty_segment()
    {
        CalibrationViewModel vm = RectifiedOnSyntheticPhoto(BoundaryRecord);
        Assert.Equal(CalibrationMeasureStage.BoundarySegments, vm.MeasureStage);
        foreach (double angle in new[] { 0.0, 5, 10 })
        {
            CalibrationPoint p = ToPhoto(new CalibrationPoint(30 * Math.Cos(angle * Math.PI / 180), 30 * Math.Sin(angle * Math.PI / 180)));
            Assert.True(vm.AddPoint(p.X, p.Y));
        }

        vm.StartNewSegment();
        CalibrationPoint q = ToPhoto(new CalibrationPoint(0, 40));
        Assert.True(vm.AddPoint(q.X, q.Y));
        Assert.Equal(2, vm.BoundarySegmentCount);
        CalibrationBoundarySegment[] segments = vm.BuildSession().Photo!.BoundarySegments!.ToArray();
        Assert.Equal(new[] { 3, 1 }, segments.Select(s => s.Points.Count));
        Assert.Equal(2, segments.Select(s => s.Id).Distinct().Count());

        vm.Undo(); // the point of segment 2
        vm.Undo(); // the now empty segment 2
        Assert.Equal(1, vm.BoundarySegmentCount);
        Assert.Equal(3, vm.MeasurePointCount);

        // Clicks off the disc are refused.
        CalibrationPoint outside = ToPhoto(new CalibrationPoint(59.9 * 1.05, 0));
        Assert.False(vm.AddPoint(outside.X, outside.Y));
        Assert.False(vm.AddPoint(-1, 10));
        Assert.Equal(3, vm.MeasurePointCount);
    }

    [Fact]
    public void Screen_opens_from_the_navigation_rail_and_draws_the_dots_on_the_clicked_pixels()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using (var devices = new DiscDeviceManager(enablePhysicalEnumeration: false))
                using (var main = new MainViewModel(devices, new VirtualBurnEngine()))
                {
                    main.NavigateCommand.Execute(NavigationTab.DiscCalibration);
                    var workspace = Assert.IsType<WorkspaceViewModel>(main.CurrentWorkspaceViewModel);
                    Assert.Same(main.Calibration, workspace.Content);
                }

                CalibrationViewModel vm = RectifiedOnSyntheticPhoto(FineRecord);
                CalibrationPoint measure = ToPhoto(new CalibrationPoint(-35, 12));
                Assert.True(vm.AddPoint(measure.X, measure.Y));

                var view = new CalibrationView { DataContext = vm };
                view.Measure(new Size(1200, 800));
                view.Arrange(new Rect(0, 0, 1200, 800));
                view.UpdateLayout();

                var surface = (FrameworkElement)view.FindName("PhotoSurface");
                Assert.Equal(PhotoWidth, surface.ActualWidth);
                Assert.Equal(PhotoHeight, surface.ActualHeight);

                var bitmap = new RenderTargetBitmap(PhotoWidth, PhotoHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                Color Pixel(CalibrationPoint p)
                {
                    byte[] bgra = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)Math.Floor(p.X), (int)Math.Floor(p.Y), 1, 1), bgra, 4, 0);
                    return Color.FromArgb(bgra[3], bgra[2], bgra[1], bgra[0]);
                }

                // The photo itself where nothing is drawn, then each dot colour at the pixel that was clicked.
                Assert.Equal(Color.FromRgb(0x40, 0x40, 0x40), Pixel(new CalibrationPoint(5, 5)));
                Assert.Equal(Color.FromRgb(0xFF, 0x9E, 0x22), Pixel(ToPhoto(Circle(60, 10, 7).First())));
                Assert.Equal(Color.FromRgb(0x22, 0xC8, 0xFF), Pixel(ToPhoto(Circle(7.5, 8, 20).First())));
                Assert.Equal(Color.FromRgb(0x38, 0xC1, 0x72), Pixel(measure));
                // The rectified outline: dots every 4 degrees on the 60 mm circle (angle 0 of the rectified frame
                // is the first), drawn on the rim of the photo. One well away from the clicked rim points is checked.
                CalibrationPoint[] rimClicks = Circle(60, 10, 7).Select(ToPhoto).ToArray();
                CalibrationPoint outlineDot = Enumerable.Range(0, 90)
                    .Select(i => vm.Transform!.DiscToSource(new CalibrationPoint(60 * Math.Cos(i * Math.PI / 45), 60 * Math.Sin(i * Math.PI / 45))))
                    .First(d => rimClicks.All(c => Math.Sqrt((c.X - d.X) * (c.X - d.X) + (c.Y - d.Y) * (c.Y - d.Y)) > 20));
                Assert.Equal(Color.FromRgb(0xF0, 0x4E, 0x23), Pixel(outlineDot));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The view test did not finish.");
        if (failure is not null) throw new InvalidOperationException("Calibration view test failed: " + failure, failure);
    }
}
