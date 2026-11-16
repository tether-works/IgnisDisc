using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.Tests.Presets;

/// <summary>
/// The nominal CD 80-minute geometry against ECMA-130 (2nd edition, June 1996). Upstream's own audit
/// (docs/PHYSICAL_MODEL_AUDIT.md, finding one) found that its 359,849 sectors over 24.5-56.8 mm at 1,200 mm/s imply a
/// 1.43 um track pitch, outside the standard. Measured disc presets are not held to these limits: a real disc may
/// differ from the standard, and a measurement must not be moved to fit it.
/// </summary>
public sealed class CdPresetStandardGeometryTests
{
    // 8.6: user data zone from d7 = 50.0 mm (+0.0 / -0.4 mm) to d8 = 116 mm max.
    private const double UserDataStartRadiusMinMm = 49.6 / 2;
    private const double UserDataStartRadiusMaxMm = 50.0 / 2;
    private const double UserDataEndRadiusMaxMm = 116.0 / 2;

    // 11.3: pitch 1.6 um +/- 0.1 um. 11.4: scanning velocity 1.20 m/s to 1.40 m/s.
    private const double PitchMinUm = 1.5;
    private const double PitchMaxUm = 1.7;
    private const double VelocityMinMmPerSecond = 1_200;
    private const double VelocityMaxMmPerSecond = 1_400;

    // 75 sectors per second of track. 22.3.3.5: absolute time 00:00:00 is the first section of the first track, at
    // d7; that track's 2-second pre-gap (150 sectors) comes before LBA 0, where a generated picture track starts.
    private const double SectorsPerSecond = 75;
    private const int PreGapSectors = 150;

    public static TheoryData<string> NominalCdPresets => new() { "cd-80", "user-custom-cd" };

    private static CdDiscPresetDefinition Preset(string id) => id == "user-custom-cd"
        ? DiscPresetJsonStore.CreateInitialUserDocument().CdPresets.Single(p => p.Id == id)
        : DiscPresetJsonStore.LoadBuiltIn().CdPresets.Single(p => p.Id == id);

    [Theory]
    [MemberData(nameof(NominalCdPresets))]
    public void Nominal_80_minute_geometry_is_inside_ECMA_130(string id)
    {
        CdDiscPresetDefinition preset = Preset(id);
        double v = preset.LinearVelocityMmPerSecond;
        double trackLengthMm = preset.Sectors / SectorsPerSecond * v;
        // Constant pitch p: the track covers the annulus area, pi * (ro^2 - ri^2) = p * length.
        double pitchMm = Math.PI * ((preset.OuterRadiusMm * preset.OuterRadiusMm)
            - (preset.InnerRadiusMm * preset.InnerRadiusMm)) / trackLengthMm;
        double areaPerSectorMm2 = pitchMm * v / SectorsPerSecond;
        double absoluteTimeZeroRadiusMm = Math.Sqrt((preset.InnerRadiusMm * preset.InnerRadiusMm)
            - (PreGapSectors * areaPerSectorMm2 / Math.PI));

        Assert.InRange(pitchMm * 1_000, PitchMinUm, PitchMaxUm);
        Assert.InRange(v, VelocityMinMmPerSecond, VelocityMaxMmPerSecond);
        Assert.InRange(absoluteTimeZeroRadiusMm, UserDataStartRadiusMinMm, UserDataStartRadiusMaxMm);
        Assert.True(preset.OuterRadiusMm <= UserDataEndRadiusMaxMm,
            $"The data must end by {UserDataEndRadiusMaxMm} mm (ECMA-130 8.6), not {preset.OuterRadiusMm} mm.");
    }

    [Theory]
    [MemberData(nameof(NominalCdPresets))]
    public void Nominal_80_minute_geometry_still_plays_about_80_minutes(string id)
    {
        CdDiscPresetDefinition preset = Preset(id);
        double minutes = (preset.Sectors + PreGapSectors) / SectorsPerSecond / 60;

        // 1.5 um, 1.2 m/s and 24.8-58.0 mm hold at most 79:57:58 including the pre-gap; the rest of a real 80-minute
        // blank (which exceeds the standard to reach 79:59:74) stays unused.
        Assert.InRange(minutes, 79.9, 80.0);
        Assert.True((preset.UsableSectors ?? preset.Sectors) <= preset.Sectors);
    }
}
