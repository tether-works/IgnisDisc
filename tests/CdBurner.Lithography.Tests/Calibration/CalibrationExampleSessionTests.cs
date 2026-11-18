using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.Tests.Calibration;

/// <summary>
/// Upstream's published calibration records (DiscImageStudio docs/examples at the pinned commit; the embedded
/// photographs are replaced by a 1x1 picture, the measurement points are unchanged) refitted with the ported
/// fitters. The expected numbers are the ones upstream publishes in docs/CALIBRATION_TUTORIAL.md for the same
/// records (the wide-band estimate of the first stage, the generation radii of the third record, and the radii
/// and coefficient set B of the disc burned after the fine stage) and in docs/DISC_PRESETS.md, not values computed
/// by this port.
/// </summary>
public sealed class CalibrationExampleSessionTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamCalibration");

    // The tutorial prints every digit of a double, so a refit that follows the same arithmetic agrees far below a
    // nanometre. 1e-9 mm leaves room only for floating-point reordering.
    private const double RadiusToleranceMm = 1e-9;
    private const double CoefficientTolerance = 1e-12;

    private static CalibrationSession Load(string name) => CalibrationSessionJson.Load(Path.Combine(FixtureDir, name));

    [Theory]
    [InlineData("dvd-calibration--calibration-81a72aac.calibration.json", 6)]
    [InlineData("dvd-calibration--calibration-f2f851f7.calibration.json", 6)]
    [InlineData("verbatim-azo-20261104--calibration-4f0228e6.calibration.json", 13)]
    [InlineData("verbatim-azo-20261104--calibration-3f2ce8c2.calibration.json", 16)]
    [InlineData("verbatim-azo-20261104--calibration-2ba20083_2.calibration.json", 16)]
    [InlineData("verbatim-azo-20261104--calibration-14580aac.calibration.json", 14)]
    public void Published_records_load_and_survive_a_save_and_reload(string name, int schemaVersion)
    {
        CalibrationSession session = Load(name);

        Assert.Equal(schemaVersion, session.Target.SchemaVersion);
        Assert.Equal(CalibrationDiscKind.Dvd, session.Target.Parameters.Kind);
        Assert.Equal(2_297_888, session.Target.Parameters.Sectors);
        Assert.Equal(133.3, session.Target.Parameters.LinearDensity);

        CalibrationSession again = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(session));
        Assert.Equal(session.Target.Id, again.Target.Id);
        Assert.Equal(session.Target.Parameters, again.Target.Parameters);
        Assert.Equal(session.FitOptions, again.FitOptions);
        Assert.Equal(CalibrationSessionJson.Serialize(session), CalibrationSessionJson.Serialize(again));
    }

    [Fact]
    public void Verified_azo_record_holds_the_parameters_of_the_built_in_preset()
    {
        // DISC_PRESETS.md: the Verbatim DVD-R AZO preset comes from calibration-f2f851f7 at full precision.
        CalibrationParameters record = Load("dvd-calibration--calibration-f2f851f7.calibration.json").Target.Parameters;
        DvdDiscPresetDefinition preset = DiscPresetJsonStore.LoadBuiltIn().DvdPresets
            .Single(p => p.Id == "verbatim-dvd-r-azo-43533");

        Assert.Equal(preset.InnerRadiusMm, record.InnerRadiusMm);
        Assert.Equal(preset.OuterRadiusMm, record.OuterRadiusMm);
        Assert.Equal(preset.TotalSectors, record.Sectors);
        Assert.Equal(preset.PitchLinear, record.PitchLinear);
        Assert.Equal(preset.PitchQuadratic, record.PitchQuadratic);
        Assert.Equal(preset.PitchCubic, record.PitchCubic);
    }

    [Fact]
    public void Coarse_record_refits_to_the_published_wide_band_radii()
    {
        CalibrationSession session = Load("verbatim-azo-20261104--calibration-4f0228e6.calibration.json");
        Assert.Equal(CalibrationStage.Coarse, CalibrationTargetFile.StageOf(session.Target));

        CalibrationRecordFit fit = CalibrationRecordFitter.Fit(session);

        Assert.Equal(CalibrationMeasurementKind.Rings, fit.Measurement);
        Assert.Equal(6, fit.PointCount);
        Assert.True(fit.Result.Succeeded, string.Join(" | ", fit.Result.Warnings));
        CalibrationParameters best = fit.Result.BestParameters!;
        Assert.Equal(24.016692871476085, best.InnerRadiusMm, RadiusToleranceMm);
        Assert.Equal(57.70924224450089, best.OuterRadiusMm, RadiusToleranceMm);
    }

    [Fact]
    public void Fine_record_refits_to_the_published_radii_and_pitch_coefficients()
    {
        CalibrationSession session = Load("verbatim-azo-20261104--calibration-14580aac.calibration.json");
        Assert.Equal(CalibrationStage.Fine, CalibrationTargetFile.StageOf(session.Target));

        CalibrationRecordFit fit = CalibrationRecordFitter.Fit(session);

        Assert.Equal(CalibrationMeasurementKind.CenterlineTrace, fit.Measurement);
        Assert.Equal(33, fit.PointCount);
        Assert.True(fit.Result.Succeeded, string.Join(" | ", fit.Result.Warnings));
        CalibrationParameters best = fit.Result.BestParameters!;
        Assert.Equal(23.99144094121467, best.InnerRadiusMm, RadiusToleranceMm);
        Assert.Equal(57.964899248503315, best.OuterRadiusMm, RadiusToleranceMm);
        Assert.Equal(-7.937249285949148E-05, best.PitchLinear, CoefficientTolerance);
        Assert.Equal(0.00014593344294674726, best.PitchQuadratic, CoefficientTolerance);
        Assert.Equal(-8.817669151956023E-05, best.PitchCubic, CoefficientTolerance);
    }

    [Fact]
    public void Record_without_measurements_is_refused()
    {
        CalibrationSession session = Load("dvd-calibration--calibration-f2f851f7.calibration.json");

        ArgumentException error = Assert.Throws<ArgumentException>(() => CalibrationRecordFitter.Fit(session));

        Assert.Contains("no measurements", error.Message);
    }

    /// <summary>Own class so xUnit runs it beside the other tests of this file.</summary>
    public sealed class BoundaryRecord
    {

        [Fact]
        public void Boundary_record_first_candidate_is_the_published_radii_of_the_next_disc()
        {
            CalibrationSession session = Load("verbatim-azo-20261104--calibration-3f2ce8c2.calibration.json");
            Assert.Equal(CalibrationStage.Boundary, CalibrationTargetFile.StageOf(session.Target));
            Assert.Equal(13, session.Photo!.BoundarySegments!.Count);

            CalibrationRecordFit fit = CalibrationRecordFitter.Fit(session);

            Assert.Equal(CalibrationMeasurementKind.BoundarySegments, fit.Measurement);
            Assert.Equal(154, fit.PointCount);
            Assert.Equal(0.02, fit.RadiusRangeMm);
            Assert.False(fit.RangeExpanded);
            // Upstream's fitter flags these hand-placed points ("do not match a single disc model") and leaves the
            // choice to the person, who applied the first candidate: its radii are the published ones.
            Assert.NotEmpty(fit.Result.Candidates);
            CalibrationParameters best = fit.Result.Candidates[0].Parameters;
            Assert.Equal(23.990847220268247, best.InnerRadiusMm, RadiusToleranceMm);
            Assert.Equal(57.96535646048506, best.OuterRadiusMm, RadiusToleranceMm);
            Assert.Equal(session.Target.Parameters.PitchLinear, best.PitchLinear);
            Assert.Equal(session.Target.Parameters.PitchQuadratic, best.PitchQuadratic);
            Assert.Equal(session.Target.Parameters.PitchCubic, best.PitchCubic);
        }
    }

    /// <summary>Own class so xUnit runs it beside the other tests of this file.</summary>
    public sealed class SupplementaryBoundaryRecord
    {

        [Fact]
        public void Supplementary_boundary_record_gives_candidates_inside_its_saved_search_range()
        {
            // The tutorial gives the points of this record (17 segments, 169 points, range 0.02 mm, no widening) but
            // no fitted numbers, so this checks only what it states and that the answer stays inside the range.
            CalibrationSession session = Load("verbatim-azo-20261104--calibration-2ba20083_2.calibration.json");
            Assert.Equal(17, session.Photo!.BoundarySegments!.Count);

            CalibrationRecordFit fit = CalibrationRecordFitter.Fit(session);

            Assert.Equal(169, fit.PointCount);
            Assert.Equal(0.02, fit.RadiusRangeMm);
            Assert.NotEmpty(fit.Result.Candidates);
            CalibrationParameters start = session.Target.Parameters;
            CalibrationParameters best = fit.Result.Candidates[0].Parameters;
            Assert.InRange(best.InnerRadiusMm, start.InnerRadiusMm - 0.02, start.InnerRadiusMm + 0.02);
            Assert.InRange(best.OuterRadiusMm, start.OuterRadiusMm - 0.02, start.OuterRadiusMm + 0.02);
        }
    }
}
