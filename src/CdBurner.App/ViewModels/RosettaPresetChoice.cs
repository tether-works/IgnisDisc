// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.App.ViewModels;

/// <summary>
/// One Rosetta glyph-cell preset presented in the UI, backed by <see cref="RosettaPagePreset"/> from
/// <see cref="RosettaPresetCatalog"/> and verified optical/data-layout advice.
/// </summary>
public sealed record RosettaPresetChoice(
    string Id,
    string DisplayName,
    RosettaPagePreset Preset,
    bool IsExperimental,
    string MicroscopeSummary,
    string ToolTipText)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Builds the Rosetta UI preset choices and checked media/microscope advice grounded in physical optics and
/// data-layout calculations until resolution-ladder burns measure real discs.
/// </summary>
public static class RosettaUiPresets
{
    public const string HobbyistId = "hobbyist";
    public const string HighDensityId = "high-density";
    public const string ExperimentalBluRayId = "experimental-bdr-13x17";

    public const string StartingPointStatus =
        "Starting defaults (simulator-tested; awaiting microscope ladder measurement — no disc has been read at any cell size yet).";

    public static IReadOnlyList<RosettaPresetChoice> BuildChoices(RosettaPresetCatalog? catalog = null)
    {
        catalog ??= RosettaPresetCatalog.BuiltIn;
        RosettaPagePreset hobbyist = catalog.Hobbyist;
        RosettaPagePreset highDensity = catalog.HighDensity;
        RosettaPagePreset experimentalBd = catalog.GetPreset("ladder-13x17");

        return
        [
            new RosettaPresetChoice(
                HobbyistId,
                "Hobbyist (pocket / USB microscope)",
                hobbyist,
                IsExperimental: false,
                MicroscopeSummary: string.Create(CultureInfo.InvariantCulture,
                    $"DVD-R starting default: {hobbyist.CellSize.WidthUm:0.##} x {hobbyist.CellSize.HeightUm:0.##} um cells ({hobbyist.CellSize.SpleenPixelWidthUm:0.##} x {hobbyist.CellSize.SpleenPixelHeightUm:0.#} um pixels, ~10 um resolution; USB/HDMI digital microscope 20-220x). Simulator-tested; awaiting microscope ladder measurement."),
                ToolTipText: string.Create(CultureInfo.InvariantCulture,
                    $"Hobbyist (pocket / USB microscope): {hobbyist.CellSize.WidthUm:0.##} x {hobbyist.CellSize.HeightUm:0.##} um glyph cells ({hobbyist.CellSize.SpleenPixelWidthUm:0.##} x {hobbyist.CellSize.SpleenPixelHeightUm:0.#} um 5x8 pixels, ~10 um resolution needed; USB/HDMI microscope 20-220x). Starting point for DVD-R (simulator-tested; awaiting microscope ladder measurement). Also satisfies CD-R's >= 50 x 75 um floor.")),
            new RosettaPresetChoice(
                HighDensityId,
                "High-density (epi-metallurgical microscope)",
                highDensity,
                IsExperimental: false,
                MicroscopeSummary: string.Create(CultureInfo.InvariantCulture,
                    $"DVD-R starting default: {highDensity.CellSize.WidthUm:0.##} x {highDensity.CellSize.HeightUm:0.##} um cells ({highDensity.CellSize.SpleenPixelWidthUm:0.##} x {highDensity.CellSize.SpleenPixelHeightUm:0.#} um pixels, ~2.5 um resolution, NA >= 0.13; reflected-light 10x objective). Simulator-tested; awaiting microscope ladder measurement."),
                ToolTipText: string.Create(CultureInfo.InvariantCulture,
                    $"High-density (epi-metallurgical microscope): {highDensity.CellSize.WidthUm:0.##} x {highDensity.CellSize.HeightUm:0.##} um glyph cells ({highDensity.CellSize.SpleenPixelWidthUm:0.##} x {highDensity.CellSize.SpleenPixelHeightUm:0.#} um 5x8 pixels, ~2.5 um resolution, NA >= 0.13; ordinary 10x NA 0.25 works). Starting point for DVD-R (simulator-tested; awaiting microscope ladder measurement). Blocked on CD-R (must stay >= 50 x 75 um).")),
            new RosettaPresetChoice(
                ExperimentalBluRayId,
                "Experimental BD-R target (13 x 17 um, unmeasured)",
                experimentalBd,
                IsExperimental: true,
                MicroscopeSummary: string.Create(CultureInfo.InvariantCulture,
                    $"Experimental only (BD-R target): {experimentalBd.CellSize.WidthUm:0.##} x {experimentalBd.CellSize.HeightUm:0.##} um cells ({experimentalBd.CellSize.SpleenPixelWidthUm:0.##} x {experimentalBd.CellSize.SpleenPixelHeightUm:0.#} um pixels, ~1.3 um resolution, 20x NA >= 0.3 reflected light). BD-R contrast and polarity are unmeasured; coaxial (epi) illumination required."),
                ToolTipText: string.Create(CultureInfo.InvariantCulture,
                    $"Experimental BD-R target only: {experimentalBd.CellSize.WidthUm:0.##} x {experimentalBd.CellSize.HeightUm:0.##} um cells ({experimentalBd.CellSize.SpleenPixelWidthUm:0.##} x {experimentalBd.CellSize.SpleenPixelHeightUm:0.#} um pixels, ~1.3 um resolution, NA >= ~0.26, 20x NA >= 0.3 reflected light). Marginal on DVD (0.6 mm cover NA ceiling 0.26-0.37), impossible on CD-R (EFM symbols are 4.7-5.5 um), and unmeasured on BD-R (coaxial epi illumination required)."))
        ];
    }

    public static bool IsPresetAllowedForMedium(RosettaPresetChoice preset, DiscArtMedium medium)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return medium switch
        {
            DiscArtMedium.Cd => !preset.IsExperimental && preset.Preset.CdSupported && preset.Preset.CellSize.IsCdCompatible,
            DiscArtMedium.Dvd => !preset.IsExperimental,
            DiscArtMedium.BluRay => true,
            _ => false,
        };
    }

    public static string? DisallowedReason(RosettaPresetChoice preset, DiscArtMedium medium)
    {
        if (IsPresetAllowedForMedium(preset, medium)) return null;
        if (medium == DiscArtMedium.Cd && !preset.Preset.CellSize.IsCdCompatible)
        {
            return "Blocked on CD-R: never use cells below 50 x 75 um because 27% of every EFM frame (9 of 33 symbols) is fixed parity/subcode in ~19 um blocks (1 symbol = 4.7-5.5 um).";
        }

        if (preset.IsExperimental && medium != DiscArtMedium.BluRay)
        {
            return medium == DiscArtMedium.Cd
                ? "Blocked on CD-R: 13 x 17 um cells (2.6 x 2.1 um pixel) are shorter than a 4.7-5.5 um EFM symbol; CD-R must stay >= 50 x 75 um."
                : "Experimental BD-R target only: 13 x 17 um (2.6 x 2.1 um pixel, ~1.3 um resolution) is marginal on DVD-R's 0.6 mm cover layer (NA ceiling 0.26-0.37) and unmeasured.";
        }

        return "This preset is not available for the selected medium.";
    }

    public static string GetMediaAdvice(DiscArtMedium medium) => medium switch
    {
        DiscArtMedium.Dvd =>
            "DVD-R (starting defaults: Hobbyist 100 x 150 um, High-density 26 x 34 um — simulator-tested; awaiting microscope ladder measurement). "
            + "Cover layer is 0.6 mm polycarbonate (dry-objective NA ceiling 0.26-0.37, 1.3-0.9 um resolution; ordinary 10x NA 0.25 works; 40x NA 0.65 degrades without a correction collar). "
            + "Track pitch 0.74 um diffracts visible light; fixed PI/PO parity leaves ~13% speckle (10 PI bytes = 21 um per 388 um row; 1 PO row = 388 um per 5.16 mm sector).",
        DiscArtMedium.Cd =>
            "CD-R constraint: never use glyph cells below 50 x 75 um (10 x 9 um pixels, ~5 um resolution). "
            + "27% of each EFM frame (9 of 33 symbols) is fixed parity/subcode in ~19 um blocks (1 EFM symbol = 4.7-5.5 um; frame 163-190 um), plus 1,332-byte interleaver edge zones. "
            + "Read-side cover layer is 1.2 mm (NA ceiling 0.22-0.31, 1.5-1.1 um resolution); 1.6 um track pitch diffracts visible light. Hybrid Rosetta disc generation awaits the CD port.",
        _ =>
            "BD-R (experimental / unmeasured): 0.1 mm cover layer (dry-objective NA ceiling 0.41-0.58, 0.8-0.6 um resolution) and ~15% fixed cluster bytes (65,536 steerable bytes per 71.4 mm cluster). "
            + "Its 0.32 um track pitch gives no first-order diffraction at normal incidence for visible light (400-700 nm; green 550 nm needs >= 46 deg), so coaxial (epi) illumination is required (e.g., 45 deg glass plate at low NA, or an epi-illuminator at 20x). "
            + "Real BD-R contrast and polarity are unmeasured; 13 x 17 um is experimental only. Hybrid Rosetta disc generation is DVD-R only for now.",
    };
}
