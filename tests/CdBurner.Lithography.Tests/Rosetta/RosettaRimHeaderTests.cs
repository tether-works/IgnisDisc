using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Rosetta;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>
/// The rim header as specified: outer 3 to 4 mm, capitals of at least 2 mm, a tick every 5 degrees, title, date
/// and SHA-256 prefix, reading diagrams, nothing thinner than 150 micrometres. Expected positions are worked out
/// here from that specification and measured on the drawn pattern or on the read-back picture.
/// </summary>
public sealed class RosettaRimHeaderTests : IDisposable
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const string Title = "Rosetta test";
    private const string ShaPrefix = "0123abcd4567";
    private const string ExpectedHeaderText = "ROSETTA TEST  2026-11-16  SHA-256 0123ABCD4567";
    private static readonly DateOnly Date = new(2026, 11, 16);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-rimheader-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public RosettaRimHeaderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static RosettaRimHeader CreateHeader(double outerRadiusMm = 58.0)
        => new(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, outerRadiusMm));

    [Fact]
    public void Band_IsTheOuterThreeToFourMillimetres_AndShowsTitleDateAndShaPrefix()
    {
        RosettaRimHeader header = CreateHeader();

        Assert.Equal(58.0, header.OuterRadiusMm);
        Assert.InRange(header.OuterRadiusMm - header.InnerRadiusMm, 3.0, 4.0);
        Assert.Equal(ExpectedHeaderText, header.HeaderText);

        // Nothing is drawn outside the band.
        for (double degrees = 0.0; degrees < 360.0; degrees += 0.25)
        {
            Assert.False(header.IsInk(header.InnerRadiusMm - 0.01, degrees * DegreesToRadians));
            Assert.False(header.IsInk(header.OuterRadiusMm + 0.01, degrees * DegreesToRadians));
        }

        // The strip goes once around without overlapping itself: header text first, just after 0 degrees.
        Assert.Equal(5, header.Items.Count);
        Assert.Equal(ExpectedHeaderText, header.Items[0].Text);
        Assert.InRange(header.Items[0].StartAngleDegrees, 0.5, 3.0);
        double previousEnd = 0.0;
        foreach (RosettaRimHeaderItem item in header.Items)
        {
            Assert.True(item.StartAngleDegrees > previousEnd, "Items must not overlap.");
            previousEnd = item.StartAngleDegrees + item.AngularSpanDegrees;
        }
        Assert.True(previousEnd < 360.0, $"The strip ends at {previousEnd:F1} degrees and must not wrap onto its start.");
    }

    [Fact]
    public void CapitalsAndDigits_AreAtLeastTwoMillimetresTall()
    {
        RosettaRimHeader header = CreateHeader();
        RosettaRimHeaderItem item = header.Items[0];
        double cellDegrees = item.AngularSpanDegrees / item.Columns;
        const double stepMm = 0.005;

        int measured = 0;
        for (int column = 0; column < item.Columns; column++)
        {
            char c = item.Text[column];
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                continue;
            }

            // Radial extent of the glyph's ink, over lines through each of its five pixel columns.
            double lowestMm = double.MaxValue;
            double highestMm = double.MinValue;
            for (int px = 0; px < 5; px++)
            {
                double angle = (item.StartAngleDegrees + ((column + ((px + 0.5) / 5.0)) * cellDegrees)) * DegreesToRadians;
                for (double r = header.InnerRadiusMm; r <= header.TextOuterRadiusMm; r += stepMm)
                {
                    if (header.IsInk(r, angle))
                    {
                        lowestMm = Math.Min(lowestMm, r);
                        highestMm = Math.Max(highestMm, r);
                    }
                }
            }

            double heightMm = highestMm - lowestMm + stepMm;
            Assert.True(heightMm >= 2.0, $"'{c}' in column {column} is {heightMm:F3} mm tall.");
            measured++;
        }

        Assert.True(measured >= 30, $"Only {measured} capitals and digits were measured.");
    }

    [Fact]
    public void Ticks_AreCentredOnEveryFiveDegrees_WithThreeLengthsAndAWideZeroMark()
    {
        RosettaRimHeader header = CreateHeader();
        const double stepDegrees = 0.002;

        // 0.2 mm below the rim every tick is present; 0.5 mm below only the 10-degree ticks; 0.75 mm only the
        // 30-degree ticks.
        foreach ((double depthMm, int everyNthTick) in new[] { (0.20, 1), (0.50, 2), (0.75, 6) })
        {
            double radiusMm = header.OuterRadiusMm - depthMm;
            List<(double CentreDegrees, double WidthDegrees)> runs = CircularRuns(
                degrees => header.IsInk(radiusMm, degrees * DegreesToRadians), stepDegrees);

            Assert.Equal(72 / everyNthTick, runs.Count);
            foreach ((double centre, double widthDegrees) in runs)
            {
                double expected = Math.Round(centre / (5.0 * everyNthTick)) * 5.0 * everyNthTick;
                Assert.True(Math.Abs(centre - expected) <= 0.004, $"Tick at {centre:F4} degrees, expected {expected:F1}.");

                bool zeroMark = expected % 360.0 == 0.0;
                double widthMm = widthDegrees * DegreesToRadians * radiusMm;
                Assert.InRange(widthMm, zeroMark ? 0.88 : 0.29, zeroMark ? 0.91 : 0.31);
            }
        }

        Assert.Equal(0.88, RosettaRimHeader.TickLengthMm(0), 6);
        Assert.Equal(0.40, RosettaRimHeader.TickLengthMm(1), 6);
        Assert.Equal(0.60, RosettaRimHeader.TickLengthMm(2), 6);
        Assert.Equal(0.88, RosettaRimHeader.TickLengthMm(66), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => RosettaRimHeader.TickLengthMm(72));
    }

    [Fact]
    public void EveryStrokeAndEveryGapInsideAMark_IsAtLeast150Micrometres()
    {
        RosettaRimHeader header = CreateHeader();
        const double stepMm = 0.004;
        double bandMm = header.OuterRadiusMm - header.InnerRadiusMm;

        // Along circles: ink runs and the gaps between them.
        int circleCount = (int)(bandMm / 0.02);
        double[] shortestOnCircle = new double[circleCount];
        Parallel.For(0, circleCount, i =>
        {
            double radiusMm = header.InnerRadiusMm + ((i + 0.5) * 0.02);
            double stepDegrees = stepMm / radiusMm / DegreesToRadians;
            double shortest = double.MaxValue;
            foreach (bool ink in new[] { true, false })
            {
                foreach ((double _, double widthDegrees) in CircularRuns(
                    degrees => header.IsInk(radiusMm, degrees * DegreesToRadians) == ink, stepDegrees))
                {
                    shortest = Math.Min(shortest, widthDegrees * DegreesToRadians * radiusMm);
                }
            }
            shortestOnCircle[i] = shortest;
        });

        // Along radial lines: ink runs, and gaps that have ink on both sides.
        int lineCount = (int)(2.0 * Math.PI * header.OuterRadiusMm / 0.01);
        int samples = (int)(bandMm / stepMm) + 1;
        double[] shortestOnLine = new double[lineCount];
        Parallel.For(0, lineCount, i =>
        {
            double angle = 2.0 * Math.PI * i / lineCount;
            double shortest = double.MaxValue;
            bool previous = false;
            bool seenInk = false;
            int runLength = 0;
            for (int s = 0; s <= samples; s++)
            {
                bool ink = s < samples && header.IsInk(header.InnerRadiusMm + (s * stepMm), angle);
                if (ink == previous && s < samples)
                {
                    runLength++;
                    continue;
                }

                // A run just ended. Count ink runs always, blank runs only between two ink runs.
                if (runLength > 0 && (previous || (seenInk && s < samples)))
                {
                    shortest = Math.Min(shortest, runLength * stepMm);
                }
                seenInk |= previous;
                previous = ink;
                runLength = 1;
            }
            shortestOnLine[i] = shortest;
        });

        double shortestTangentialMm = shortestOnCircle.Min();
        double shortestRadialMm = shortestOnLine.Min();
        _output.WriteLine($"Shortest run: {shortestTangentialMm * 1000:F0} um along the track, {shortestRadialMm * 1000:F0} um across it.");
        Assert.True(shortestTangentialMm >= 0.150, $"A stroke or gap is only {shortestTangentialMm * 1000:F0} um wide along the track.");
        Assert.True(shortestRadialMm >= 0.150, $"A stroke or gap is only {shortestRadialMm * 1000:F0} um wide across the track.");
        Assert.Equal(0.150, RosettaRimHeader.MinimumFeatureMm);
    }

    [Fact]
    public void Diagrams_ShowMicroscopeAboveTheDisc_AndAGlassPlateAt45Degrees()
    {
        RosettaRimHeader header = CreateHeader();

        Assert.Equal(RosettaRimHeaderItemKind.Text, header.Items[1].Kind);
        Assert.Contains("SHINY SIDE", header.Items[1].Text, StringComparison.Ordinal);
        Assert.Contains("LIGHT FROM LENS SIDE", header.Items[1].Text, StringComparison.Ordinal);
        Assert.Equal(RosettaRimHeaderItemKind.Text, header.Items[3].Kind);
        Assert.Contains("BD-R", header.Items[3].Text, StringComparison.Ordinal);
        Assert.Contains("GLASS PLATE AT 45°", header.Items[3].Text, StringComparison.Ordinal);

        foreach (int index in new[] { 2, 4 })
        {
            RosettaRimHeaderItem diagram = header.Items[index];
            Assert.Equal(RosettaRimHeaderItemKind.Diagram, diagram.Kind);
            Assert.Equal(16, diagram.BitmapRows.Count);
            Assert.All(diagram.BitmapRows, row => Assert.Equal(diagram.Columns, row.Length));
            Assert.All(diagram.BitmapRows, row => Assert.All(row, c => Assert.True(c is '#' or '.')));

            // The disc is the full-width bar at the bottom (towards the disc centre), blank row above it;
            // the microscope is at the top (towards the rim).
            Assert.Equal(new string('#', diagram.Columns), diagram.BitmapRows[15]);
            Assert.Equal(new string('#', diagram.Columns), diagram.BitmapRows[14]);
            Assert.Equal(new string('.', diagram.Columns), diagram.BitmapRows[13]);
            Assert.Contains("#######", diagram.BitmapRows[0], StringComparison.Ordinal);
        }

        // The glass plate: its pixels are square, so one column per row is 45 degrees.
        IReadOnlyList<string> plate = header.Items[4].BitmapRows;
        for (int row = 7; row <= 12; row++)
        {
            Assert.Equal(plate[row - 1].IndexOf('#', StringComparison.Ordinal) - 1, plate[row].IndexOf('#', StringComparison.Ordinal));
        }
        Assert.Equal(RosettaRimHeader.TextHeightMm / 16.0, RosettaRimHeader.DiagramPixelMm, 9);
    }

    [Fact]
    public void Options_AreChecked_AndALongTitleIsCutToFit()
    {
        Assert.Throws<ArgumentException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, "0123")));
        Assert.Throws<ArgumentException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, "0123456z")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, double.NaN)));
        // An 8 cm disc has too short a rim for the whole strip.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, 38.0)));

        var longTitle = new RosettaRimHeader(new RosettaRimHeaderOptions(new string('W', 300), Date, ShaPrefix));
        Assert.InRange(longTitle.Title.Length, 20, 299);
        Assert.EndsWith("2026-11-16  SHA-256 0123ABCD4567", longTitle.HeaderText, StringComparison.Ordinal);
        RosettaRimHeaderItem last = longTitle.Items[^1];
        Assert.True(last.StartAngleDegrees + last.AngularSpanDegrees < 360.0);

        // Characters the 5x8 font cannot draw are shown as '?'; line breaks become spaces.
        var odd = new RosettaRimHeader(new RosettaRimHeaderOptions("a永b\nc", Date, ShaPrefix));
        Assert.Equal("A?B C", odd.Title);
    }

    [Fact]
    public void DefaultHeader_UsesExactlyTheFullSizeLengths()
    {
        RosettaRimHeaderMetrics m = CreateHeader().Metrics;

        // Bit for bit: the DVD book with a header must not change.
        Assert.Equal(RosettaRimHeaderMetrics.FullSize, m);
        Assert.Equal(1.0, m.Scale);
        Assert.Null(m.TurnsPerGlyphRow);
        Assert.Equal(RosettaRimHeader.BandHeightMm, m.BandHeightMm);
        Assert.Equal(RosettaRimHeader.TextHeightMm, m.TextHeightMm);
        Assert.Equal(RosettaRimHeader.GlyphCellWidthMm, m.GlyphCellWidthMm);
        Assert.Equal(RosettaRimHeader.DiagramPixelMm, m.DiagramPixelMm);
        Assert.Equal(RosettaRimHeader.CapitalHeightMm, m.CapitalHeightMm);
        Assert.Equal(RosettaRimHeader.TickWidthMm, m.TickWidthMm);
        Assert.Equal(RosettaRimHeader.MinimumFeatureMm, m.MinimumFeatureMm);
    }

    [Fact]
    public void FitToBand_FourMillimetres_IsFullSize_WithGlyphRowsOfWholeEvenTurns()
    {
        // CD pitch 1.6 um: 0.34 mm / 1.6 um = 212.5 turns, rounded down to an even 212.
        RosettaRimHeader header = RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.Cd, 54.0, 58.0, 0.0016);
        RosettaRimHeaderMetrics m = header.Metrics;

        Assert.Equal(1.0, m.Scale);
        Assert.Equal(212, m.TurnsPerGlyphRow);
        Assert.Equal(212 * 0.0016, m.GlyphPixelHeightMm, 12);
        Assert.Equal(106 * 0.0016, m.DiagramPixelMm, 12); // half a glyph row: whole turns too
        Assert.Equal(RosettaRimHeader.GlyphCellWidthMm, m.GlyphCellWidthMm, 12);
        Assert.Equal(58.0, header.OuterRadiusMm);
        Assert.True(header.InnerRadiusMm - m.PageGutterMm >= 54.0, "band and gutter stay inside the ring");
        Assert.Equal(ExpectedHeaderText, header.HeaderText);
    }

    [Fact]
    public void FitToBand_NarrowRing_ScalesEveryLength_AndKeepsTheLayout()
    {
        // 0.5 mm on a CD: an eighth of the full depth.
        RosettaRimHeader header = RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.Cd, 57.5, 58.0, 0.0016);
        RosettaRimHeaderMetrics m = header.Metrics;

        Assert.Equal(0.125, m.Scale, 12);
        Assert.Equal(RosettaRimHeader.GlyphCellWidthMm * 0.125, m.GlyphCellWidthMm, 12);
        Assert.Equal(RosettaRimHeader.TickZoneHeightMm * 0.125, m.TickZoneHeightMm, 12);
        Assert.Equal(0, m.TurnsPerGlyphRow!.Value % 2);
        Assert.Equal(m.TurnsPerGlyphRow.Value * 0.0016, m.GlyphPixelHeightMm, 12);
        Assert.True(m.GlyphCellWidthMm >= 0.050 && m.TextHeightMm >= 0.075, "glyph cells at least 50 x 75 um");
        Assert.True(header.InnerRadiusMm - m.PageGutterMm >= 57.5 - 1e-9, "band and gutter stay inside the ring");
        Assert.Equal(5, header.Items.Count);
        Assert.Equal(ExpectedHeaderText, header.HeaderText);

        // The wide 0-degree tick at its scaled depth, the gap below it, and the first glyph row below that.
        Assert.True(header.IsInk(58.0 - (0.5 * m.TickZoneHeightMm), 0.0));
        Assert.False(header.IsInk(58.0 - (0.5 * m.TickZoneHeightMm), 2.5 * DegreesToRadians));
        Assert.False(header.IsInk(58.0 - m.TickZoneHeightMm - (0.5 * m.TickToTextGapMm), 0.0));
        // The same angles on the full-size header are ticks only near the rim.
        Assert.False(CreateHeader().IsInk(58.0 - (0.5 * m.TickZoneHeightMm) - 1.0, 0.0));
    }

    [Fact]
    public void FitToBand_RefusesBelowEachFormatsMinimumCell_WithTheReason()
    {
        // 0.09 mm: CD needs 50 x 75 um cells, about 0.114 mm; DVD and BD need 26 x 34 um, about 0.059 mm.
        ArgumentException cd = Assert.Throws<ArgumentException>(() =>
            RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.Cd, 57.91, 58.0, 0.0016));
        Assert.Contains("only 0.090 mm wide", cd.Message);
        Assert.Contains("a CD needs glyph cells of at least 50 x 75 um", cd.Message);
        Assert.Contains("at least 0.114 mm", cd.Message);

        RosettaRimHeader dvd = RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.Dvd, 57.91, 58.0, 0.00074);
        Assert.True(dvd.Metrics.GlyphCellWidthMm >= 0.026 && dvd.Metrics.TextHeightMm >= 0.034);

        ArgumentException bd = Assert.Throws<ArgumentException>(() =>
            RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.BluRay, 57.95, 58.0, 0.00032));
        Assert.Contains("a BD needs glyph cells of at least 26 x 34 um", bd.Message);

        // A pitch so coarse that a full-size glyph row would be under two turns.
        ArgumentException coarse = Assert.Throws<ArgumentException>(() =>
            RosettaRimHeader.FitToBand(Title, Date, ShaPrefix, RosettaRimHeaderFormat.Cd, 40.0, 58.0, 0.2));
        Assert.Contains("too coarse for the rim header", coarse.Message);
    }

    [Fact]
    public void ScaleAndPitchOptions_AreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, Scale: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, Scale: 1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, TrackPitchMm: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RosettaRimHeader(new RosettaRimHeaderOptions(Title, Date, ShaPrefix, TrackPitchMm: 0.2)));
    }

    [Fact]
    public void Overlay_CoversBandAndGutter_InPictureCoordinates()
    {
        RosettaRimHeader header = CreateHeader();
        var overlay = new RosettaRimHeaderOverlay(header);

        Assert.Equal(header.InnerRadiusMm - RosettaRimHeader.PageGutterMm, overlay.InnerRadiusMm, 12);
        Assert.Equal(58.0, overlay.OuterRadiusMm);
        // Right-hand side of the picture is 0 degrees: the wide tick.
        Assert.True(overlay.TrySample(57.8, 0.0, out byte tick));
        Assert.Equal(RosettaDvdEncoder.InkLuminance, tick);
        // In the gutter: part of the overlay, blank.
        Assert.True(overlay.TrySample(-(header.InnerRadiusMm - 0.1), 0.0, out byte gutter));
        Assert.Equal(RosettaDvdEncoder.PaperLuminance, gutter);
        // Inside the ring the picture shows.
        Assert.False(overlay.TrySample(0.0, 40.0, out _));
    }

    [Fact]
    public void Book_WithRimHeader_KeepsPagesBelowTheBand_AndDrawsBoth()
    {
        RosettaPagePreset preset = RosettaPresetCatalog.BuiltIn.Hobbyist;
        RosettaDocument[] docs = [new("DOC", "Text under the rim header.")];
        var options = new RosettaArchiveOptions(Title, Date, preset, InnerRadiusMm: 38.0, OuterRadiusMm: 58.0);

        RosettaBook plain = RosettaBook.Build(docs, options);
        RosettaBook withHeader = RosettaBook.Build(docs, options with { IncludeRimHeader = true });

        Assert.Null(plain.RimHeader);
        RosettaRimHeader header = Assert.IsType<RosettaRimHeader>(withHeader.RimHeader);
        Assert.Equal(58.0, header.OuterRadiusMm);
        Assert.Equal($"ROSETTA TEST  2026-11-16  SHA-256 {withHeader.TextSha256Prefix.ToUpperInvariant()}", header.HeaderText);
        Assert.Equal(RosettaBook.ComputeTextSha256Hex(docs)[..12], withHeader.TextSha256Prefix);

        foreach (RosettaBand band in withHeader.Bands)
        {
            double pagesOuterMm = band.Layout.OuterRadiusMeters * 1.0e3;
            Assert.True(pagesOuterMm <= header.InnerRadiusMm - 0.2 + 1.0e-9, $"Pages reach {pagesOuterMm:F3} mm.");
        }
        Assert.True(withHeader.TotalPhysicalTiles < plain.TotalPhysicalTiles);

        // One sampler draws both: the 0 degree tick at the rim (turned by the start angle) and the first page.
        const double startDegrees = 20.0;
        Func<double, double, byte> sampler = RosettaDvdEncoder.CreateReadSideGrayscaleSampler(withHeader, startDegrees);
        Func<double, double, byte> headerOnly = RosettaDvdEncoder.CreateReadSideGrayscaleSampler(header, startDegrees);
        (double x, double y) = Polar(57.8, startDegrees);
        Assert.Equal(RosettaDvdEncoder.InkLuminance, sampler(x, y));
        Assert.Equal(RosettaDvdEncoder.InkLuminance, headerOnly(x, y));
        (x, y) = Polar(57.8, startDegrees + 2.5);
        Assert.Equal(RosettaDvdEncoder.PaperLuminance, sampler(x, y));
        Assert.Equal(RosettaDvdEncoder.PaperLuminance, headerOnly(x, y));

        // Top-left '+' of page R00-S00: the centre cell of its 3x3 block is ink.
        RosettaBand first = withHeader.Bands[0];
        double pageTopMm = (first.Layout.InnerRadiusMeters + first.Layout.PageRadialHeightMeters) * 1.0e3;
        double crossRadiusMm = pageTopMm - (1.5 * preset.CellSize.HeightMeters * 1.0e3);
        double crossDegrees = startDegrees + (1.5 * preset.CellSize.WidthMeters * 1.0e3 / (pageTopMm - 1.0) / DegreesToRadians);
        (x, y) = Polar(crossRadiusMm, crossDegrees);
        Assert.Equal(RosettaDvdEncoder.InkLuminance, sampler(x, y));
        Assert.Equal(RosettaDvdEncoder.PaperLuminance, headerOnly(x, y));
    }

    [Fact]
    public void EncodeOptions_FromCalibration_TakeTheFittedTrackGeometry()
    {
        var fitted = new CalibrationParameters(
            CalibrationDiscKind.Dvd, InnerRadiusMm: 23.95, OuterRadiusMm: 58.08, Sectors: 2_298_496, LinearDensity: 133.1,
            PitchLinear: 0.012, PitchQuadratic: -0.006, PitchCubic: 0.003, UsableSectors: 2_295_104);

        RosettaDvdEncodeOptions options = RosettaDvdEncodeOptions.FromCalibration(fitted, startAngleDegrees: 12.5);

        Assert.Equal(2_298_496u, options.TotalSectors);
        Assert.Equal(2_295_104u, options.UsableSectors);
        Assert.Equal(23.95, options.InnerRadiusMm);
        Assert.Equal(58.08, options.OuterRadiusMm);
        Assert.Equal(133.1, options.ChannelBitLengthNm);
        Assert.Equal(0.012, options.PitchLinear);
        Assert.Equal(-0.006, options.PitchQuadratic);
        Assert.Equal(0.003, options.PitchCubic);
        Assert.Equal(12.5, options.StartAngleDegrees);
        Assert.True(options.ReadCompatibility);
        _ = RosettaDvdEncoder.BuildSolveOptions(options);

        var cd = new CalibrationParameters(CalibrationDiscKind.Cd, 25.0, 58.0, 333_000, 1.2);
        Assert.Throws<ArgumentException>(() => RosettaDvdEncodeOptions.FromCalibration(cd));
    }

    /// <summary>
    /// The whole chain on a simulated disc: a saved calibration with a non-uniform track pitch, the DVD encoder,
    /// then the read-back renderer, which places every written byte from the sector data and the track geometry
    /// alone. On that picture the ticks must sit on multiples of 5 degrees and the text must be legible.
    /// <para>
    /// The simulated track starts at 51.95 mm and its turns are 2.96 um apart (four times a DVD's 0.74 um), which
    /// keeps the image at 283 MB. Angles depend on the geometry in the same way at either spacing.
    /// </para>
    /// </summary>
    [Fact]
    public void SimulatedReadback_OfACalibratedDisc_ShowsTicksAtFiveDegreeStepsAndTheHeaderText()
    {
        const double startAngleDegrees = 30.0;
        const int previewSize = 4096;
        var fitted = new CalibrationParameters(
            CalibrationDiscKind.Dvd, InnerRadiusMm: 51.95, OuterRadiusMm: 58.05, Sectors: 138_240, LinearDensity: 133.1,
            PitchLinear: 0.012, PitchQuadratic: -0.006, PitchCubic: 0.003);

        // Through the calibration store, as a burn would get it.
        string storePath = Path.Combine(_tempDir, CalibrationStore.FileName);
        CalibrationStore.Save(storePath, new CalibrationStoreEntry(
            "TEST DRIVE", "TESTMEDIA01", fitted, DateTimeOffset.UnixEpoch, "target", "Fine", 0.01));
        CalibrationStoreEntry stored = Assert.IsType<CalibrationStoreEntry>(
            CalibrationStore.Find(CalibrationStore.Load(storePath), "test drive", "testmedia01", CalibrationDiscKind.Dvd));
        RosettaDvdEncodeOptions encodeOptions = RosettaDvdEncodeOptions.FromCalibration(stored.Parameters, startAngleDegrees);

        RosettaPagePreset preset = RosettaPresetCatalog.BuiltIn.Hobbyist;
        RosettaBook book = RosettaBook.Build(
            [new("DOC", "Text under the rim header.")],
            new RosettaArchiveOptions(Title, Date, preset, InnerRadiusMm: 51.95, OuterRadiusMm: 58.0, IncludeRimHeader: true));
        RosettaRimHeader header = book.RimHeader!;

        string isoPath = Path.Combine(_tempDir, "rim.iso");
        string pngPath = Path.Combine(_tempDir, "rim.png");
        RosettaDvdEncoder.WriteIsoFile(book, isoPath, encodeOptions);

        IsoReadbackSummary summary = IsoReadbackRenderer.Render(isoPath, pngPath, ReadbackOptions(encodeOptions, previewSize, samplesPerSector: 512));
        _output.WriteLine($"Read-back: pitch {summary.TrackPitchMicrometres:F3} um, {summary.TotalTurns:F0} turns, {58.05 * 2000 / previewSize:F1} um per pixel.");
        BgraRaster readback = PngRaster.Load(pngPath);
        bool IsDark(double radiusMm, double degrees) => IsDarkAt(readback, 58.05, radiusMm, degrees);

        // 1. Ticks. Expected: tick k at k x 5 degrees on the disc, which the start angle turns to k x 5 + 30.
        foreach ((double depthMm, int everyNthTick) in new[] { (0.20, 1), (0.50, 2), (0.75, 6) })
        {
            double radiusMm = 58.0 - depthMm;
            List<(double CentreDegrees, double WidthDegrees)> runs = CircularRuns(degrees => IsDark(radiusMm, degrees), 0.01);
            Assert.Equal(72 / everyNthTick, runs.Count);
            double worst = 0.0;
            foreach ((double centre, double widthDegrees) in runs)
            {
                double onDisc = centre - startAngleDegrees;
                double expected = Math.Round(onDisc / (5.0 * everyNthTick)) * 5.0 * everyNthTick;
                worst = Math.Max(worst, Math.Abs(onDisc - expected));

                bool zeroMark = Math.Abs(expected % 360.0) < 1.0e-9;
                double widthMm = widthDegrees * DegreesToRadians * radiusMm;
                Assert.InRange(widthMm, zeroMark ? 0.80 : 0.22, zeroMark ? 1.00 : 0.38);
            }
            _output.WriteLine($"{runs.Count} ticks at {depthMm:F2} mm below the rim, largest angle error {worst:F3} degrees.");
            // 0.06 degrees is 60 um at this radius, about two pixels of the read-back picture.
            Assert.True(worst <= 0.06, $"A tick is {worst:F3} degrees away from its 5-degree position.");
        }

        // 2. Text and diagrams, pixel for pixel, from their stated start angles.
        Spleen5x8Font font = Spleen5x8Font.BuiltIn;
        Assert.Equal(ExpectedHeaderText.Replace("0123ABCD4567", book.TextSha256Prefix.ToUpperInvariant(), StringComparison.Ordinal), header.HeaderText);
        int comparedPixels = 0;
        foreach (RosettaRimHeaderItem item in header.Items)
        {
            bool text = item.Kind == RosettaRimHeaderItemKind.Text;
            int pixelsPerColumn = text ? 5 : 1;
            int rows = text ? 8 : 16;
            double rowMm = RosettaRimHeader.TextHeightMm / rows;
            double pixelDegrees = item.AngularSpanDegrees / (item.Columns * pixelsPerColumn);
            for (int column = 0; column < item.Columns; column++)
            {
                for (int px = 0; px < pixelsPerColumn; px++)
                {
                    double degrees = startAngleDegrees + item.StartAngleDegrees + (((column * pixelsPerColumn) + px + 0.5) * pixelDegrees);
                    for (int py = 0; py < rows; py++)
                    {
                        bool expected = text ? font.IsPixelSet(item.Text[column], px, py) : item.BitmapRows[py][column] == '#';
                        bool actual = IsDark(header.TextOuterRadiusMm - ((py + 0.5) * rowMm), degrees);
                        Assert.True(expected == actual,
                            $"{item.Kind} at {item.StartAngleDegrees:F1} degrees, column {column} ('{(text ? item.Text[column] : '#')}'), pixel ({px},{py}): expected {(expected ? "ink" : "blank")}.");
                        comparedPixels++;
                    }
                }
            }
        }
        _output.WriteLine($"{comparedPixels} header pixels match.");

        // 3. The pages are still there below the band: the top-left '+' of page R00-S00.
        RosettaBand band = book.Bands[0];
        bool[,] cross = RosettaDvdEncoder.SampleCellBitmapFromReadback(
            readback, 58.05, band.Layout, 0, 0, 0.0, 0.0,
            3.0 * preset.CellSize.WidthMeters, 3.0 * preset.CellSize.HeightMeters, 3, 3, startAngleDegrees);
        for (int cy = 0; cy < 3; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                Assert.Equal(cx == 1 || cy == 1, cross[cx, cy]);
            }
        }

        // 4. Control: read with the uncalibrated (uniform pitch) geometry, the same sectors no longer form
        // ticks at 5-degree steps. This is why the header needs the calibration.
        string uncalibratedPng = Path.Combine(_tempDir, "rim-uncalibrated.png");
        IsoReadbackRenderer.Render(isoPath, uncalibratedPng, ReadbackOptions(
            encodeOptions with { PitchLinear = 0.0, PitchQuadratic = 0.0, PitchCubic = 0.0 }, 2048, samplesPerSector: 256));
        BgraRaster uncalibrated = PngRaster.Load(uncalibratedPng);
        List<(double CentreDegrees, double WidthDegrees)> smeared = CircularRuns(
            degrees => IsDarkAt(uncalibrated, 58.05, 57.8, degrees), 0.01);
        bool ticksInPlace = smeared.Count == 72 && smeared.All(run =>
        {
            double onDisc = run.CentreDegrees - startAngleDegrees;
            return Math.Abs(onDisc - (Math.Round(onDisc / 5.0) * 5.0)) <= 0.06;
        });
        Assert.False(ticksInPlace, "Reading with the wrong track geometry must not show the ticks in place.");
    }

    private static IsoReadbackOptions ReadbackOptions(RosettaDvdEncodeOptions encode, int previewSize, int samplesPerSector)
        => new(
            TotalSectors: encode.TotalSectors,
            StartLba: 0,
            FillSectors: encode.TotalSectors,
            PsnOffset: encode.PsnOffset,
            InnerRadiusMm: encode.InnerRadiusMm,
            OuterRadiusMm: encode.OuterRadiusMm,
            ChannelBitLengthNm: encode.ChannelBitLengthNm,
            Clockwise: true,
            PreviewSize: previewSize,
            SamplesPerSector: samplesPerSector,
            PitchLinear: encode.PitchLinear,
            PitchQuadratic: encode.PitchQuadratic,
            PitchCubic: encode.PitchCubic,
            PreviewOuterRadiusMm: encode.OuterRadiusMm);

    /// <summary>Read-side coordinates in millimetres (x right, y down) of a point at a clockwise angle.</summary>
    private static (double X, double YDown) Polar(double radiusMm, double clockwiseDegrees)
        => (radiusMm * Math.Cos(clockwiseDegrees * DegreesToRadians), radiusMm * Math.Sin(clockwiseDegrees * DegreesToRadians));

    /// <summary>
    /// True when the read-back picture is dark at the given disc position (clockwise angle, y down), by the
    /// majority of the 3x3 pixels around it. The renderer leaves a pixel white when no byte it can place falls
    /// on it; at this test's wide track spacing that happens to single pixels where the parity rows of
    /// neighbouring turns line up. Three pixels are 85 um at 4096 pixels, half the smallest header feature.
    /// </summary>
    private static bool IsDarkAt(BgraRaster picture, double pictureOuterRadiusMm, double radiusMm, double clockwiseDegrees)
    {
        (double x, double yDown) = Polar(radiusMm, clockwiseDegrees);
        double centre = (picture.Width - 1) / 2.0;
        int px = (int)Math.Round(centre + (centre * x / pictureOuterRadiusMm));
        int py = (int)Math.Round(centre + (centre * yDown / pictureOuterRadiusMm));
        int dark = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (picture.Pixels[((((py + dy) * picture.Width) + px + dx) * 4)] < 128)
                {
                    dark++;
                }
            }
        }
        return dark >= 5;
    }

    /// <summary>
    /// Walks once around a circle in steps of <paramref name="stepDegrees"/> and returns each stretch where
    /// <paramref name="isSet"/> is true, as centre angle and width, joining a stretch that crosses 360 degrees.
    /// </summary>
    private static List<(double CentreDegrees, double WidthDegrees)> CircularRuns(Func<double, bool> isSet, double stepDegrees)
    {
        int count = (int)Math.Round(360.0 / stepDegrees);
        double step = 360.0 / count;
        bool[] set = new bool[count];
        for (int i = 0; i < count; i++)
        {
            set[i] = isSet(i * step);
        }

        var runs = new List<(double, double)>();
        int origin = Array.IndexOf(set, false);
        if (origin < 0)
        {
            runs.Add((180.0, 360.0));
            return runs;
        }

        int runStart = -1;
        for (int k = 1; k <= count; k++)
        {
            int unwrapped = origin + k;
            bool value = k < count && set[unwrapped % count];
            if (value && runStart < 0)
            {
                runStart = unwrapped;
            }
            else if (!value && runStart >= 0)
            {
                int runEnd = unwrapped - 1;
                double centre = (0.5 * (runStart + runEnd) * step) % 360.0;
                runs.Add((centre, (runEnd - runStart + 1) * step));
                runStart = -1;
            }
        }
        return runs;
    }
}
