// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.DiscArt;

/// <summary>
/// Disc presets as picture-disc geometry. The command line (<c>disc-art generate --disc-preset</c>) and the Disc Art
/// screen both go through here, so a preset gives the same <see cref="CalibrationParameters"/> and therefore the same
/// image bytes in both.
/// </summary>
public static class DiscArtPresets
{
    /// <summary>Preset used when none is chosen.</summary>
    public static string DefaultPresetId(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => "cd-80",
        CalibrationDiscKind.Dvd => "dvd-5-120mm",
        _ => "bd-r-sl-25gb",
    };

    /// <summary>Built-in presets, merged with the user's presets file when it exists. <paramref name="userPresetsPath"/>
    /// null means the default file (<see cref="DiscPresetJsonStore.GetDefaultPath"/>); a missing file is no error.</summary>
    public static DiscPresetJsonDocument Load(string? userPresetsPath = null)
    {
        DiscPresetJsonDocument doc = DiscPresetJsonStore.LoadBuiltIn();
        string path = Path.GetFullPath(string.IsNullOrWhiteSpace(userPresetsPath)
            ? DiscPresetJsonStore.GetDefaultPath() : userPresetsPath);
        return File.Exists(path) ? DiscPresetJsonStore.Merge(doc, DiscPresetJsonStore.Load(path)) : doc;
    }

    public static CalibrationParameters ToParameters(CdDiscPresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new CalibrationParameters(CalibrationDiscKind.Cd, preset.InnerRadiusMm, preset.OuterRadiusMm,
            preset.Sectors, preset.LinearVelocityMmPerSecond, preset.PitchLinear, preset.PitchQuadratic,
            preset.PitchCubic, UsableSectors: preset.UsableSectors);
    }

    public static CalibrationParameters ToParameters(DvdDiscPresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new CalibrationParameters(CalibrationDiscKind.Dvd, preset.InnerRadiusMm, preset.OuterRadiusMm,
            preset.TotalSectors, preset.ChannelBitLengthNm, preset.PitchLinear, preset.PitchQuadratic,
            preset.PitchCubic, UsableSectors: preset.UsableSectors);
    }

    /// <summary>A BD preset is checked first (sector count in whole clusters, radii); a bad one throws
    /// <see cref="ArgumentException"/>.</summary>
    public static CalibrationParameters ToParameters(BluRayDiscPresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var disc = new BluRayDiscParameters(preset.TotalSectors, 1, preset.InnerRadiusMm, preset.OuterRadiusMm,
            PitchLinear: preset.PitchLinear, PitchQuadratic: preset.PitchQuadratic,
            PitchCubic: preset.PitchCubic, UsableSectors: preset.UsableSectors);
        disc.Validate();
        return new CalibrationParameters(CalibrationDiscKind.BluRay, disc.InnerRadiusMm, disc.OuterRadiusMm,
            disc.TotalSectors, CalibrationParameters.BluRayChannelBitLengthNm, disc.PitchLinear, disc.PitchQuadratic,
            disc.PitchCubic, BluRayInvertMapping: preset.InvertMapping, DrawingStartSector: 0,
            DrawingEndSectorExclusive: disc.OutputSectors, UsableSectors: disc.OutputSectors);
    }
}
