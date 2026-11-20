using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

public enum RosettaLadderMedium
{
    Dvd,
    Cd,
}

public sealed record RosettaLadderOptions(
    string DiscPresetId = "dvd-5-120mm",
    uint TotalSectors = 2_295_104,
    uint? UsableSectors = null,
    uint PsnOffset = 0x30000,
    double InnerRadiusMm = 24.0,
    double OuterRadiusMm = 58.0,
    double ChannelBitLengthNm = 133.3,
    double PitchLinear = 0.0,
    double PitchQuadratic = 0.0,
    double PitchCubic = 0.0,
    double StartAngleDegrees = 0.0,
    bool ReadCompatibility = true,
    int FastParallelism = 0,
    RosettaLadderMedium Medium = RosettaLadderMedium.Dvd,
    double LinearVelocityMmPerSecond = CdDiscParameters.StandardLinearVelocityMmPerSecond,
    bool CdInterleave = true)
{
    public uint OutputSectors => UsableSectors ?? TotalSectors;

    public static RosettaLadderOptions FromDvdPreset(
        DvdDiscPresetDefinition preset,
        double startAngleDegrees = 0.0,
        bool readCompatibility = true,
        int fastParallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new RosettaLadderOptions(
            DiscPresetId: preset.Id,
            TotalSectors: preset.TotalSectors,
            UsableSectors: preset.UsableSectors,
            InnerRadiusMm: preset.InnerRadiusMm,
            OuterRadiusMm: preset.OuterRadiusMm,
            ChannelBitLengthNm: preset.ChannelBitLengthNm,
            PitchLinear: preset.PitchLinear,
            PitchQuadratic: preset.PitchQuadratic,
            PitchCubic: preset.PitchCubic,
            StartAngleDegrees: startAngleDegrees,
            ReadCompatibility: readCompatibility,
            FastParallelism: fastParallelism,
            Medium: RosettaLadderMedium.Dvd);
    }

    public static RosettaLadderOptions FromCdPreset(
        CdDiscPresetDefinition preset,
        double startAngleDegrees = 0.0,
        bool interleave = true)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Sectors <= 0 || preset.Sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset.Sectors, "CD preset Sectors must be in 1..uint.MaxValue.");
        }

        uint? usableSectors = null;
        if (preset.UsableSectors is { } usable)
        {
            if (usable <= 0 || usable > preset.Sectors)
            {
                throw new ArgumentOutOfRangeException(nameof(preset), usable, "CD preset UsableSectors must be in 1..Sectors.");
            }
            usableSectors = checked((uint)usable);
        }

        return new RosettaLadderOptions(
            DiscPresetId: preset.Id,
            TotalSectors: checked((uint)preset.Sectors),
            UsableSectors: usableSectors,
            PsnOffset: 0,
            InnerRadiusMm: preset.InnerRadiusMm,
            OuterRadiusMm: preset.OuterRadiusMm,
            ChannelBitLengthNm: 0.0,
            PitchLinear: preset.PitchLinear,
            PitchQuadratic: preset.PitchQuadratic,
            PitchCubic: preset.PitchCubic,
            StartAngleDegrees: startAngleDegrees,
            ReadCompatibility: false,
            FastParallelism: 0,
            Medium: RosettaLadderMedium.Cd,
            LinearVelocityMmPerSecond: preset.LinearVelocityMmPerSecond,
            CdInterleave: interleave);
    }
}

public sealed record RosettaPolarBox(
    double InnerRadiusMm,
    double OuterRadiusMm,
    double MidRadiusMm,
    double StartAngleDegrees,
    double EndAngleDegrees,
    double AngularSpanDegrees,
    double CenterXMm,
    double CenterYDownMm,
    double WidthMm,
    double HeightMm)
{
    [JsonIgnore] public double InnerRadiusMeters => InnerRadiusMm * 1.0e-3;
    [JsonIgnore] public double OuterRadiusMeters => OuterRadiusMm * 1.0e-3;
    [JsonIgnore] public double MidRadiusMeters => MidRadiusMm * 1.0e-3;
    [JsonIgnore] public double StartAngleRadians => StartAngleDegrees * (Math.PI / 180.0);
    [JsonIgnore] public double AngularSpanRadians => AngularSpanDegrees * (Math.PI / 180.0);
    [JsonIgnore] public double WidthMeters => WidthMm * 1.0e-3;
    [JsonIgnore] public double HeightMeters => HeightMm * 1.0e-3;
}

public sealed record RosettaLadderLabelSpec(
    IReadOnlyList<string> Lines,
    double CellWidthUm,
    double CellHeightUm,
    int Columns,
    int Rows,
    RosettaPolarBox Bounds);

public sealed record RosettaLadderZone(
    string ZoneId,
    string ShortTag,
    int ZoneIndex,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double MidRadiusMm,
    uint StartLba,
    uint EndLba)
{
    public uint SectorCount => EndLba - StartLba;
}

public sealed record RosettaLadderRung(
    string RungId,
    string ZoneId,
    int ZoneIndex,
    int LadderIndex,
    string PresetId,
    double CellWidthUm,
    double CellHeightUm,
    double SpleenPixelWidthUm,
    double SpleenPixelHeightUm,
    bool CdSupported,
    int Columns,
    int Rows,
    string HeaderText,
    double SlotStartAngleDegrees,
    RosettaLadderLabelSpec Label,
    RosettaPolarBox Page,
    uint StartLba,
    uint EndLba);

public sealed record RosettaLadderManifest(
    int SchemaVersion,
    string Format,
    string Media,
    string DiscPresetId,
    uint StartLba,
    uint FillSectors,
    uint TotalSectors,
    uint PsnOffset,
    string ReadCompatibility,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic,
    double StartAngleDegrees,
    bool ClockwiseOnReadSide,
    string OrientationNote,
    string Font,
    double LabelCellWidthUm,
    double LabelCellHeightUm,
    IReadOnlyList<string> TestTextLines,
    IReadOnlyList<RosettaLadderZone> Zones,
    IReadOnlyList<RosettaLadderRung> Rungs,
    double LinearVelocityMmPerSecond = 0.0,
    bool CdInterleave = false,
    int CircEdgeZoneBytes = 0);

public sealed class RosettaLadderLayout
{
    internal RosettaLadderLayout(
        RosettaLadderOptions options,
        DvdTrackGeometry? trackGeometry,
        CdDiscParameters? cdParameters,
        RosettaLadderManifest manifest,
        IReadOnlyList<RosettaPagePreset> ladderPresets)
    {
        Options = options;
        TrackGeometry = trackGeometry;
        CdParameters = cdParameters;
        Manifest = manifest;
        LadderPresets = ladderPresets;
    }

    public RosettaLadderOptions Options { get; }
    public DvdTrackGeometry? TrackGeometry { get; }
    public CdDiscParameters? CdParameters { get; }
    public RosettaLadderManifest Manifest { get; }
    public IReadOnlyList<RosettaPagePreset> LadderPresets { get; }
}

/// <summary>
/// Generates a DVD-R or CD-R optical resolution-ladder disc image with labelled rungs spanning
/// inner, middle, and outer radii plus a JSON manifest of rung positions.
/// </summary>
public static class RosettaLadderDisc
{
    public const int SchemaVersion = 1;
    public const string FormatId = "rosetta-resolution-ladder-v1";
    public const int LabelColumns = 10;
    public const int LabelRows = 2;
    public const double SlotAngleDegrees = 45.0;
    public const double SlotLeadMarginDegrees = 1.0;
    public const double LabelToPageGapMm = 0.400;
    public const double MinimumRadialSpanMm = 18.0;

    private const double TwoPi = 2.0 * Math.PI;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double RadialLbaGuardMm = 0.005;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<string> DefaultTestTextLines { get; } =
    [
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz",
        "0123456789 +-*/=?!@#%&()[]",
        "0O O0 1Il lI1 5S S5 8B B8",
        "2Z Z2 6G G6 9g q9 uv rn m",
        "THE QUICK FOX JUMPS 012345",
    ];

    public static RosettaLadderLayout CreateLayout(
        RosettaLadderOptions? options = null,
        RosettaPresetCatalog? catalog = null)
    {
        options ??= new RosettaLadderOptions();
        catalog ??= RosettaPresetCatalog.BuiltIn;

        ValidateOptions(options);

        bool isCd = options.Medium == RosettaLadderMedium.Cd;
        IReadOnlyList<RosettaPagePreset> ladderPresets = isCd
            ? catalog.Ladder.Where(p => p.CdSupported && p.CellSize.IsCdCompatible).ToArray()
            : catalog.Ladder;
        if (ladderPresets.Count == 0 || ladderPresets.Count > 8)
        {
            throw new ArgumentException(
                isCd
                    ? $"Rosetta CD-R ladder preset catalog must contain 1 to 8 rungs >= 50x75 um with cdSupported=true (got {ladderPresets.Count})."
                    : $"Rosetta ladder preset catalog must contain 1 to 8 rungs (got {ladderPresets.Count}).",
                nameof(catalog));
        }

        uint fillSectors = options.OutputSectors;
        double maxPageHeightMm = ladderPresets.Max(p => p.PageRadialHeightMeters * 1.0e3);

        DvdTrackGeometry? geom = null;
        CdDiscParameters? cdParams = null;
        double bitLengthMm = 0.0;
        double sectorLengthMm;
        if (isCd)
        {
            cdParams = BuildCdParameters(options);
            sectorLengthMm = options.LinearVelocityMmPerSecond / 75.0;
        }
        else
        {
            bitLengthMm = options.ChannelBitLengthNm * 1.0e-6;
            double trackLengthMm = options.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
            geom = DvdTrackGeometry.Create(
                options.InnerRadiusMm,
                options.OuterRadiusMm,
                trackLengthMm,
                options.PitchLinear,
                options.PitchQuadratic,
                options.PitchCubic);
            sectorLengthMm = DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
        }

        (uint StartLba, uint EndLba) ComputeLbaRange(double rInnerMm, double rOuterMm) => isCd
            ? ComputeAlignedCdLbaRange(cdParams!, rInnerMm, rOuterMm, fillSectors)
            : ComputeAlignedLbaRange(geom!, rInnerMm, rOuterMm, bitLengthMm, fillSectors);

        double innerNominalOuterMm = options.InnerRadiusMm + 6.200;
        double middleNominalOuterMm = (0.5 * (options.InnerRadiusMm + options.OuterRadiusMm)) + (0.5 * maxPageHeightMm);
        double outerNominalOuterMm = options.OuterRadiusMm - 1.200;

        (string ZoneId, string ShortTag, double OuterMm)[] zoneSpecs =
        [
            ("inner", "IN", SnapOffResonance(innerNominalOuterMm, options.InnerRadiusMm + 5.700, options.InnerRadiusMm + 6.700, sectorLengthMm)),
            ("middle", "MID", SnapOffResonance(middleNominalOuterMm, middleNominalOuterMm - 0.500, middleNominalOuterMm + 0.500, sectorLengthMm)),
            ("outer", "OUT", SnapOffResonance(outerNominalOuterMm, options.OuterRadiusMm - 1.700, options.OuterRadiusMm - 1.000, sectorLengthMm)),
        ];

        var zones = new RosettaLadderZone[zoneSpecs.Length];
        for (int z = 0; z < zoneSpecs.Length; z++)
        {
            double outerMm = zoneSpecs[z].OuterMm;
            double innerMm = outerMm - maxPageHeightMm;
            double midMm = 0.5 * (innerMm + outerMm);
            (uint startLba, uint endLba) = ComputeLbaRange(innerMm, outerMm);
            zones[z] = new RosettaLadderZone(
                zoneSpecs[z].ZoneId,
                zoneSpecs[z].ShortTag,
                z,
                innerMm,
                outerMm,
                midMm,
                startLba,
                endLba);
        }

        if (zones[0].InnerRadiusMm <= options.InnerRadiusMm
            || zones[0].OuterRadiusMm + 1.0 > zones[1].InnerRadiusMm
            || zones[1].OuterRadiusMm + 1.0 > zones[2].InnerRadiusMm
            || zones[2].OuterRadiusMm >= options.OuterRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Radial zones do not maintain at least 1.0 mm separation inside the disc data band.");
        }

        if (isCd)
        {
            long drawableEndByte = CdCircEdgeZones.LogicalDrawableEndByte(cdParams!.TotalBytes);
            double innerEdgeRadiusMm = cdParams.RadiusFromByte(CdCircEdgeZones.InnerEdgeZoneBytes);
            double outerEdgeRadiusMm = cdParams.RadiusFromByte(drawableEndByte);
            if (zones[0].InnerRadiusMm <= innerEdgeRadiusMm
                || zones[^1].OuterRadiusMm >= outerEdgeRadiusMm
                || (long)zones[0].StartLba * CdDiscParameters.BytesPerSector < CdCircEdgeZones.InnerEdgeZoneBytes
                || (long)zones[^1].EndLba * CdDiscParameters.BytesPerSector > drawableEndByte)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "CD-R ladder zones must stay outside the ~18 mm CIRC edge zones at the start and end of the track.");
            }
        }

        double slotAngleDeg = 360.0 / ladderPresets.Count;
        RosettaCellSize labelCell = ladderPresets[0].CellSize;
        double labelW = LabelColumns * labelCell.WidthMeters * 1.0e3;
        double labelH = LabelRows * labelCell.HeightMeters * 1.0e3;

        var rungs = new RosettaLadderRung[zones.Length * ladderPresets.Count];
        int rungCursor = 0;

        for (int z = 0; z < zones.Length; z++)
        {
            RosettaLadderZone zone = zones[z];
            double labelOuterMm = zone.OuterRadiusMm;
            double labelInnerMm = labelOuterMm - labelH;
            double labelMidMm = 0.5 * (labelInnerMm + labelOuterMm);
            double labelSpanDeg = (labelW / labelMidMm) * RadiansToDegrees;
            double gapDeg = (LabelToPageGapMm / labelMidMm) * RadiansToDegrees;

            for (int k = 0; k < ladderPresets.Count; k++)
            {
                RosettaPagePreset preset = ladderPresets[k];
                double baseAngleDeg = NormalizeDegrees(options.StartAngleDegrees + (k * slotAngleDeg));
                double labelStartDeg = NormalizeDegrees(baseAngleDeg + SlotLeadMarginDegrees);
                RosettaPolarBox labelBox = CreatePolarBox(labelInnerMm, labelOuterMm, labelStartDeg, labelSpanDeg, labelW, labelH);

                double pageW = preset.PageTangentialWidthMeters * 1.0e3;
                double pageH = preset.PageRadialHeightMeters * 1.0e3;
                double pageOuterMm = zone.OuterRadiusMm;
                double pageInnerMm = pageOuterMm - pageH;
                double pageMidMm = 0.5 * (pageInnerMm + pageOuterMm);
                double pageStartDeg = NormalizeDegrees(labelStartDeg + labelSpanDeg + gapDeg);
                double pageSpanDeg = (pageW / pageMidMm) * RadiansToDegrees;

                if (SlotLeadMarginDegrees + labelSpanDeg + gapDeg + pageSpanDeg >= slotAngleDeg - 1.0)
                {
                    throw new InvalidOperationException(
                        $"Rung {k + 1} in zone '{zone.ZoneId}' exceeds its {slotAngleDeg:F1}-degree angular slot.");
                }

                RosettaPolarBox pageBox = CreatePolarBox(pageInnerMm, pageOuterMm, pageStartDeg, pageSpanDeg, pageW, pageH);

                string sizeTag = string.Create(CultureInfo.InvariantCulture, $"{preset.CellSize.WidthUm:0.##}x{preset.CellSize.HeightUm:0.##}");
                string[] labelLines =
                [
                    sizeTag.PadRight(8) + "->",
                    string.Create(CultureInfo.InvariantCulture, $"{zone.ShortTag,-3} um #{k + 1,-2}"),
                ];

                var labelSpec = new RosettaLadderLabelSpec(
                    labelLines,
                    labelCell.WidthUm,
                    labelCell.HeightUm,
                    LabelColumns,
                    LabelRows,
                    labelBox);

                string rungId = string.Create(CultureInfo.InvariantCulture, $"{zone.ShortTag}-R{k + 1:D2}-{sizeTag}");
                string headerText = string.Create(CultureInfo.InvariantCulture, $"{zone.ShortTag} {sizeTag} um R{k + 1}/{ladderPresets.Count}");
                (uint rungStartLba, uint rungEndLba) = ComputeLbaRange(
                    Math.Min(labelInnerMm, pageInnerMm),
                    zone.OuterRadiusMm);

                rungs[rungCursor++] = new RosettaLadderRung(
                    rungId,
                    zone.ZoneId,
                    z,
                    k,
                    preset.Id,
                    preset.CellSize.WidthUm,
                    preset.CellSize.HeightUm,
                    preset.CellSize.SpleenPixelWidthUm,
                    preset.CellSize.SpleenPixelHeightUm,
                    preset.CdSupported,
                    preset.Columns,
                    preset.Rows,
                    headerText,
                    baseAngleDeg,
                    labelSpec,
                    pageBox,
                    rungStartLba,
                    rungEndLba);
            }
        }

        string discPresetId = string.IsNullOrWhiteSpace(options.DiscPresetId)
            ? (isCd ? "custom-cd" : "custom-dvd")
            : options.DiscPresetId;
        var manifest = new RosettaLadderManifest(
            SchemaVersion,
            FormatId,
            isCd ? "CD-R" : "DVD-R",
            discPresetId,
            StartLba: 0,
            FillSectors: fillSectors,
            TotalSectors: options.TotalSectors,
            PsnOffset: isCd ? 0u : options.PsnOffset,
            ReadCompatibility: isCd ? "none" : (options.ReadCompatibility ? DvdReadCompatibilityModes.Current : DvdReadCompatibilityModes.Off),
            InnerRadiusMm: options.InnerRadiusMm,
            OuterRadiusMm: options.OuterRadiusMm,
            ChannelBitLengthNm: isCd ? 0.0 : options.ChannelBitLengthNm,
            PitchLinear: options.PitchLinear,
            PitchQuadratic: options.PitchQuadratic,
            PitchCubic: options.PitchCubic,
            StartAngleDegrees: options.StartAngleDegrees,
            ClockwiseOnReadSide: true,
            OrientationNote: "Angles are clockwise from +X (3 o'clock) on the read-side disc view (y down); text reads clockwise tangentially and rows progress radially inward from OuterRadiusMm.",
            Font: "Spleen 5x8",
            LabelCellWidthUm: labelCell.WidthUm,
            LabelCellHeightUm: labelCell.HeightUm,
            TestTextLines: DefaultTestTextLines,
            Zones: zones,
            Rungs: rungs,
            LinearVelocityMmPerSecond: isCd ? options.LinearVelocityMmPerSecond : 0.0,
            CdInterleave: isCd && options.CdInterleave,
            CircEdgeZoneBytes: isCd && options.CdInterleave ? CdCircEdgeZones.InnerEdgeZoneBytes : 0);

        return new RosettaLadderLayout(options, geom, cdParams, manifest, ladderPresets);
    }

    public static Func<double, double, byte> CreateReadSideGrayscaleSampler(
        RosettaLadderLayout layout,
        Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Spleen5x8Font resolvedFont = font ?? Spleen5x8Font.BuiltIn;
        IReadOnlyList<RosettaLadderZone> zones = layout.Manifest.Zones;
        IReadOnlyList<RosettaLadderRung> rungs = layout.Manifest.Rungs;
        IReadOnlyList<string> lines = layout.Manifest.TestTextLines;
        int slotCount = layout.LadderPresets.Count;
        double slotAngleRad = TwoPi / slotCount;
        double invSlotAngleRad = 1.0 / slotAngleRad;
        double startAngleRad = NormalizeRadians(layout.Options.StartAngleDegrees * DegreesToRadians);

        double z0InSq = zones[0].InnerRadiusMm * zones[0].InnerRadiusMm;
        double z0OutSq = zones[0].OuterRadiusMm * zones[0].OuterRadiusMm;
        double z1InSq = zones[1].InnerRadiusMm * zones[1].InnerRadiusMm;
        double z1OutSq = zones[1].OuterRadiusMm * zones[1].OuterRadiusMm;
        double z2InSq = zones[2].InnerRadiusMm * zones[2].InnerRadiusMm;
        double z2OutSq = zones[2].OuterRadiusMm * zones[2].OuterRadiusMm;

        var fastRungs = new FastRungEntry[rungs.Count];
        for (int i = 0; i < rungs.Count; i++)
        {
            fastRungs[i] = FastRungEntry.Create(rungs[i], lines, resolvedFont);
        }

        return (xMm, yDownMm) =>
        {
            double rSq = (xMm * xMm) + (yDownMm * yDownMm);
            int zoneIndex;
            if (rSq >= z0InSq && rSq < z0OutSq)
            {
                zoneIndex = 0;
            }
            else if (rSq >= z1InSq && rSq < z1OutSq)
            {
                zoneIndex = 1;
            }
            else if (rSq >= z2InSq && rSq < z2OutSq)
            {
                zoneIndex = 2;
            }
            else
            {
                return RosettaDvdEncoder.PaperLuminance;
            }

            double cwRad = Math.Atan2(yDownMm, xMm);
            if (cwRad < 0.0)
            {
                cwRad += TwoPi;
            }

            return SampleInZone(rSq, cwRad, zoneIndex, startAngleRad, invSlotAngleRad, slotCount, fastRungs);
        };
    }

    internal static Func<DvdCodewordSample, byte> CreateCodewordGrayscaleSampler(
        RosettaLadderLayout layout,
        Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Spleen5x8Font resolvedFont = font ?? Spleen5x8Font.BuiltIn;
        IReadOnlyList<RosettaLadderZone> zones = layout.Manifest.Zones;
        IReadOnlyList<RosettaLadderRung> rungs = layout.Manifest.Rungs;
        IReadOnlyList<string> lines = layout.Manifest.TestTextLines;
        int slotCount = layout.LadderPresets.Count;
        double slotAngleRad = TwoPi / slotCount;
        double invSlotAngleRad = 1.0 / slotAngleRad;
        double startAngleRad = NormalizeRadians(layout.Options.StartAngleDegrees * DegreesToRadians);

        double z0InSq = zones[0].InnerRadiusMm * zones[0].InnerRadiusMm;
        double z0OutSq = zones[0].OuterRadiusMm * zones[0].OuterRadiusMm;
        double z1InSq = zones[1].InnerRadiusMm * zones[1].InnerRadiusMm;
        double z1OutSq = zones[1].OuterRadiusMm * zones[1].OuterRadiusMm;
        double z2InSq = zones[2].InnerRadiusMm * zones[2].InnerRadiusMm;
        double z2OutSq = zones[2].OuterRadiusMm * zones[2].OuterRadiusMm;

        var fastRungs = new FastRungEntry[rungs.Count];
        for (int i = 0; i < rungs.Count; i++)
        {
            fastRungs[i] = FastRungEntry.Create(rungs[i], lines, resolvedFont);
        }

        return sample =>
        {
            double rSq = (sample.Xmm * sample.Xmm) + (sample.Ymm * sample.Ymm);
            int zoneIndex;
            if (rSq >= z0InSq && rSq < z0OutSq)
            {
                zoneIndex = 0;
            }
            else if (rSq >= z1InSq && rSq < z1OutSq)
            {
                zoneIndex = 1;
            }
            else if (rSq >= z2InSq && rSq < z2OutSq)
            {
                zoneIndex = 2;
            }
            else
            {
                return RosettaDvdEncoder.PaperLuminance;
            }

            double cwRad = sample.TrackAngleRadians - (TwoPi * Math.Floor(sample.TrackAngleRadians / TwoPi));
            if (cwRad < 0.0 || cwRad >= TwoPi)
            {
                cwRad = 0.0;
            }

            double relRad = cwRad - startAngleRad;
            if (relRad < 0.0)
            {
                relRad += TwoPi;
            }
            int k = (int)(relRad * invSlotAngleRad);
            if ((uint)k >= (uint)slotCount)
            {
                k = slotCount - 1;
            }

            ref readonly FastRungEntry entry = ref fastRungs[(zoneIndex * slotCount) + k];
            if (rSq < entry.MinInnerSq || rSq >= entry.MaxOuterSq)
            {
                return RosettaDvdEncoder.PaperLuminance;
            }

            double boxRel = cwRad - entry.BoxMinAngleRad;
            if (boxRel < 0.0)
            {
                boxRel += TwoPi;
            }
            if (boxRel > entry.BoxTotalSpanRad)
            {
                return RosettaDvdEncoder.PaperLuminance;
            }

            double rMm = Math.Sqrt(rSq);
            if (rMm >= entry.LabelInnerMm && rMm < entry.LabelOuterMm)
            {
                double lDelta = cwRad - entry.LabelStartRad;
                if (lDelta < 0.0) lDelta += TwoPi;
                if (lDelta < entry.LabelSpanRad)
                {
                    int lx = (int)(lDelta * entry.LabelUGridScale);
                    if (lx >= entry.LabelGridW) lx = entry.LabelGridW - 1;
                    double yMm = entry.LabelHeightMm - (rMm - entry.LabelInnerMm);
                    int ly = yMm <= 0.0 ? 0 : (int)(yMm * entry.LabelYGridScale);
                    if (ly >= entry.LabelGridH) ly = entry.LabelGridH - 1;
                    return entry.LabelBitmap[(ly * entry.LabelGridW) + lx];
                }
            }

            if (rMm >= entry.PageInnerMm && rMm < entry.PageOuterMm)
            {
                double pDelta = cwRad - entry.PageStartRad;
                if (pDelta < 0.0) pDelta += TwoPi;
                if (pDelta < entry.PageSpanRad)
                {
                    int px = (int)(pDelta * entry.PageUGridScale);
                    if (px >= entry.PageGridW) px = entry.PageGridW - 1;
                    double yMm = entry.PageHeightMm - (rMm - entry.PageInnerMm);
                    int py = yMm <= 0.0 ? 0 : (int)(yMm * entry.PageYGridScale);
                    if (py >= entry.PageGridH) py = entry.PageGridH - 1;
                    return entry.PageBitmap[(py * entry.PageGridW) + px];
                }
            }

            return RosettaDvdEncoder.PaperLuminance;
        };
    }

    private static byte SampleInZone(
        double rSq,
        double cwRad,
        int zoneIndex,
        double startAngleRad,
        double invSlotAngleRad,
        int slotCount,
        FastRungEntry[] fastRungs)
    {
        double relRad = cwRad - startAngleRad;
        if (relRad < 0.0)
        {
            relRad += TwoPi;
        }
        int k = (int)(relRad * invSlotAngleRad);
        if ((uint)k >= (uint)slotCount)
        {
            k = slotCount - 1;
        }

        ref readonly FastRungEntry entry = ref fastRungs[(zoneIndex * slotCount) + k];
        if (rSq < entry.MinInnerSq || rSq >= entry.MaxOuterSq)
        {
            return RosettaDvdEncoder.PaperLuminance;
        }

        double boxRel = cwRad - entry.BoxMinAngleRad;
        if (boxRel < 0.0)
        {
            boxRel += TwoPi;
        }
        if (boxRel > entry.BoxTotalSpanRad)
        {
            return RosettaDvdEncoder.PaperLuminance;
        }

        double rMm = Math.Sqrt(rSq);
        if (rMm >= entry.LabelInnerMm && rMm < entry.LabelOuterMm)
        {
            double lDelta = cwRad - entry.LabelStartRad;
            if (lDelta < 0.0) lDelta += TwoPi;
            if (lDelta < entry.LabelSpanRad)
            {
                int lx = (int)(lDelta * entry.LabelUGridScale);
                if (lx >= entry.LabelGridW) lx = entry.LabelGridW - 1;
                double yMm = entry.LabelHeightMm - (rMm - entry.LabelInnerMm);
                int ly = yMm <= 0.0 ? 0 : (int)(yMm * entry.LabelYGridScale);
                if (ly >= entry.LabelGridH) ly = entry.LabelGridH - 1;
                return entry.LabelBitmap[(ly * entry.LabelGridW) + lx];
            }
        }

        if (rMm >= entry.PageInnerMm && rMm < entry.PageOuterMm)
        {
            double pDelta = cwRad - entry.PageStartRad;
            if (pDelta < 0.0) pDelta += TwoPi;
            if (pDelta < entry.PageSpanRad)
            {
                int px = (int)(pDelta * entry.PageUGridScale);
                if (px >= entry.PageGridW) px = entry.PageGridW - 1;
                double yMm = entry.PageHeightMm - (rMm - entry.PageInnerMm);
                int py = yMm <= 0.0 ? 0 : (int)(yMm * entry.PageYGridScale);
                if (py >= entry.PageGridH) py = entry.PageGridH - 1;
                return entry.PageBitmap[(py * entry.PageGridW) + px];
            }
        }

        return RosettaDvdEncoder.PaperLuminance;
    }

    public static bool IsExpectedLabelSubpixelInk(
        RosettaLadderRung rung,
        int gx,
        int gy,
        Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(rung);
        Spleen5x8Font resolvedFont = font ?? Spleen5x8Font.BuiltIn;
        if ((uint)gx >= LabelColumns * Spleen5x8Font.CellPixelWidth
            || (uint)gy >= LabelRows * Spleen5x8Font.CellPixelHeight)
        {
            return false;
        }

        int col = gx / Spleen5x8Font.CellPixelWidth;
        int px = gx % Spleen5x8Font.CellPixelWidth;
        int row = gy / Spleen5x8Font.CellPixelHeight;
        int py = gy % Spleen5x8Font.CellPixelHeight;
        if ((uint)row >= (uint)rung.Label.Lines.Count)
        {
            return false;
        }

        string line = rung.Label.Lines[row];
        char ch = col < line.Length ? line[col] : ' ';
        return ch != ' ' && resolvedFont.IsPixelSet(ch, px, py);
    }

    public static bool IsExpectedPageSubpixelInk(
        RosettaLadderRung rung,
        IReadOnlyList<string> testTextLines,
        int gx,
        int gy,
        Spleen5x8Font? font = null)
    {
        ArgumentNullException.ThrowIfNull(rung);
        ArgumentNullException.ThrowIfNull(testTextLines);
        Spleen5x8Font resolvedFont = font ?? Spleen5x8Font.BuiltIn;

        int gridW = rung.Columns * Spleen5x8Font.CellPixelWidth;
        int gridH = (rung.Rows * Spleen5x8Font.CellPixelHeight) + 3;
        if ((uint)gx >= (uint)gridW || (uint)gy >= (uint)gridH)
        {
            return false;
        }

        // Top-left 3x3 '+' crosshair.
        if (gx < 15 && gy < 24)
        {
            int cx = gx / Spleen5x8Font.CellPixelWidth;
            int cy = gy / Spleen5x8Font.CellPixelHeight;
            return cx == 1 || cy == 1;
        }

        // Bottom-right 3x3 '+' crosshair.
        if (gx >= gridW - 15 && gy >= gridH - 24)
        {
            int cx = (gx - (gridW - 15)) / Spleen5x8Font.CellPixelWidth;
            int cy = (gy - (gridH - 24)) / Spleen5x8Font.CellPixelHeight;
            return cx == 1 || cy == 1;
        }

        int col = gx / Spleen5x8Font.CellPixelWidth;
        int px = gx % Spleen5x8Font.CellPixelWidth;

        // Header row (gy 0..7).
        if (gy < Spleen5x8Font.CellPixelHeight)
        {
            if (col < 4)
            {
                return false;
            }
            int hdrIdx = col - 4;
            return hdrIdx < rung.HeaderText.Length
                && rung.HeaderText[hdrIdx] != ' '
                && resolvedFont.IsPixelSet(rung.HeaderText[hdrIdx], px, gy);
        }

        // 1-pixel horizontal rule band (gy 8..10).
        if (gy < Spleen5x8Font.CellPixelHeight + 3)
        {
            return gy == 9 && col >= 4;
        }

        // Body rows (gy >= 11).
        int bodyGy = gy - (Spleen5x8Font.CellPixelHeight + 3);
        int bodyRow = bodyGy / Spleen5x8Font.CellPixelHeight;
        int py = bodyGy % Spleen5x8Font.CellPixelHeight;
        int bodyRows = rung.Rows - 1;

        if ((bodyRow < 2 && col < 4) || (bodyRow >= bodyRows - 3 && col >= rung.Columns - 4))
        {
            return false;
        }

        if ((uint)bodyRow >= (uint)testTextLines.Count)
        {
            return false;
        }

        int startCol = bodyRow < 2 ? 4 : 0;
        int charIdx = col - startCol;
        string line = testTextLines[bodyRow];
        return (uint)charIdx < (uint)line.Length
            && line[charIdx] != ' '
            && resolvedFont.IsPixelSet(line[charIdx], px, py);
    }

    public static FastDvdStreamSummary WriteStream(
        RosettaLadderLayout layout,
        Stream output,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(output);
        if (layout.Options.Medium != RosettaLadderMedium.Dvd)
        {
            throw new ArgumentException("WriteStream requires a DVD-R RosettaLadderLayout; use WriteCdStream for CD-R.", nameof(layout));
        }

        RosettaLadderManifest manifest = layout.Manifest;
        double bitLengthMm = manifest.ChannelBitLengthNm * 1.0e-6;
        double trackLengthMm = manifest.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
        DvdTrackGeometry geom = DvdTrackGeometry.Create(
            manifest.InnerRadiusMm,
            manifest.OuterRadiusMm,
            trackLengthMm,
            manifest.PitchLinear,
            manifest.PitchQuadratic,
            manifest.PitchCubic);

        Func<DvdCodewordSample, byte> codewordSampler = CreateCodewordGrayscaleSampler(layout);
        (_, List<(uint StartBlock, int BlockCount)> activeRuns) = CollectActiveBlockRuns(geom, manifest, maxGapBlocks: 64);

        uint fillSectors = manifest.FillSectors;
        int totalBlocks = checked((int)(fillSectors / DvdEccBlockEncoder.SectorCount));
        long totalBytes = checked((long)fillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);

        int completedBlocksBase = 0;
        long bytesWrittenBase = 0;
        long controlledWords = 0;
        long compatibilityChanges = 0;
        uint sectorsWritten = 0;
        Stopwatch totalWatch = Stopwatch.StartNew();

        Action<FastDvdStreamProgress>? WrapProgress()
        {
            if (progress is null)
            {
                return null;
            }
            int baseBlocks = completedBlocksBase;
            long baseBytes = bytesWrittenBase;
            return p => progress(new FastDvdStreamProgress(
                baseBlocks + p.CompletedBlocks,
                totalBlocks,
                p.BlockLba,
                baseBytes + p.BytesWritten,
                totalBytes,
                totalWatch.Elapsed));
        }

        void WriteSpacer(uint startLba, uint sectorCount)
        {
            if (sectorCount == 0)
            {
                return;
            }
            MultiBlockSolveOptions segOptions = BuildSegmentSolveOptions(layout, startLba, sectorCount);
            FastDvdStreamSummary seg = FastDispersionImageWriter.WriteSectorPatternStream(
                static _ => RosettaDvdEncoder.PaperLuminance,
                output,
                segOptions,
                WrapProgress(),
                cancellationToken);
            completedBlocksBase += seg.CompletedBlocks;
            bytesWrittenBase = checked(bytesWrittenBase + seg.BytesWritten);
            sectorsWritten += seg.SectorsWritten;
            controlledWords += seg.ControlledWords;
            compatibilityChanges += seg.CompatibilityChanges;
        }

        void WriteActive(uint startLba, uint sectorCount)
        {
            if (sectorCount == 0)
            {
                return;
            }
            MultiBlockSolveOptions segOptions = BuildSegmentSolveOptions(layout, startLba, sectorCount);
            FastDvdStreamSummary seg = FastDispersionImageWriter.WriteCodewordPatternStream(
                codewordSampler,
                output,
                segOptions,
                WrapProgress(),
                cancellationToken);
            completedBlocksBase += seg.CompletedBlocks;
            bytesWrittenBase = checked(bytesWrittenBase + seg.BytesWritten);
            sectorsWritten += seg.SectorsWritten;
            controlledWords += seg.ControlledWords;
            compatibilityChanges += seg.CompatibilityChanges;
        }

        uint cursor = 0;
        for (int i = 0; i < activeRuns.Count; i++)
        {
            (uint runStartBlock, int runBlockCount) = activeRuns[i];
            uint startLba = runStartBlock * DvdEccBlockEncoder.SectorCount;
            uint endLba = (runStartBlock + (uint)runBlockCount) * DvdEccBlockEncoder.SectorCount;
            if (cursor < startLba)
            {
                WriteSpacer(cursor, startLba - cursor);
            }
            WriteActive(startLba, endLba - startLba);
            cursor = endLba;
        }

        if (cursor < fillSectors)
        {
            WriteSpacer(cursor, fillSectors - cursor);
        }

        totalWatch.Stop();
        return new FastDvdStreamSummary(
            completedBlocksBase,
            sectorsWritten,
            bytesWrittenBase,
            controlledWords,
            totalWatch.Elapsed,
            compatibilityChanges);
    }

    public static FastDvdStreamSummary WriteIsoFile(
        RosettaLadderLayout layout,
        string isoOutputPath,
        string? jsonOutputPath = null,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoOutputPath);
        if (layout.Options.Medium != RosettaLadderMedium.Dvd)
        {
            throw new ArgumentException("WriteIsoFile requires a DVD-R RosettaLadderLayout; use WriteCdFile for CD-R.", nameof(layout));
        }

        string isoFullPath = Path.GetFullPath(isoOutputPath);
        string? isoDir = Path.GetDirectoryName(isoFullPath);
        if (!string.IsNullOrEmpty(isoDir))
        {
            Directory.CreateDirectory(isoDir);
        }

        string resolvedJsonPath = string.IsNullOrWhiteSpace(jsonOutputPath)
            ? isoFullPath + ".json"
            : Path.GetFullPath(jsonOutputPath);
        string? jsonDir = Path.GetDirectoryName(resolvedJsonPath);
        if (!string.IsNullOrEmpty(jsonDir))
        {
            Directory.CreateDirectory(jsonDir);
        }

        FastDvdStreamSummary summary;
        using (var fs = new FileStream(isoFullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
        {
            summary = WriteStream(layout, fs, progress, cancellationToken);
        }

        File.WriteAllText(resolvedJsonPath, SerializeManifestJson(layout.Manifest));
        return summary;
    }

    public static CdGenerationSummary WriteCdStream(
        RosettaLadderLayout layout,
        Stream output,
        CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(output);
        if (layout.Options.Medium != RosettaLadderMedium.Cd || layout.CdParameters is not { } cdParams)
        {
            throw new ArgumentException("WriteCdStream requires a CD-R RosettaLadderLayout.", nameof(layout));
        }

        Func<double, double, byte> sampler = CreateReadSideGrayscaleSampler(layout);
        return CdTrackGenerator.GeneratePatternToStream(
            sampler,
            output,
            cdParams,
            layout.Options.CdInterleave,
            progress,
            cancellationToken,
            outputDescription: "rosetta-cd-ladder-stream",
            audioByteOrder: audioByteOrder,
            useSimdSampling: true,
            useParallelSampling: true);
    }

    public static CdGenerationSummary WriteCdFile(
        RosettaLadderLayout layout,
        string trackOutputPath,
        string? jsonOutputPath = null,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackOutputPath);
        if (layout.Options.Medium != RosettaLadderMedium.Cd || layout.CdParameters is not { } cdParams)
        {
            throw new ArgumentException("WriteCdFile requires a CD-R RosettaLadderLayout.", nameof(layout));
        }

        string trackFullPath = Path.GetFullPath(trackOutputPath);
        string resolvedJsonPath = string.IsNullOrWhiteSpace(jsonOutputPath)
            ? trackFullPath + ".json"
            : Path.GetFullPath(jsonOutputPath);
        string? jsonDir = Path.GetDirectoryName(resolvedJsonPath);
        if (!string.IsNullOrEmpty(jsonDir))
        {
            Directory.CreateDirectory(jsonDir);
        }

        Func<double, double, byte> sampler = CreateReadSideGrayscaleSampler(layout);
        CdGenerationSummary summary = CdTrackGenerator.GeneratePattern(
            sampler,
            trackFullPath,
            cdParams,
            layout.Options.CdInterleave,
            progress,
            cancellationToken,
            useSimdSampling: true,
            useParallelSampling: true);

        File.WriteAllText(resolvedJsonPath, SerializeManifestJson(layout.Manifest));
        return summary;
    }

    public static string SerializeManifestJson(RosettaLadderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, ManifestJsonOptions);
    }

    public static RosettaLadderManifest LoadManifestJson(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        string text = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<RosettaLadderManifest>(text, ManifestJsonOptions)
            ?? throw new InvalidDataException($"Manifest file '{jsonPath}' did not deserialize to a RosettaLadderManifest.");
    }

    internal static MultiBlockSolveOptions BuildSegmentSolveOptions(
        RosettaLadderLayout layout,
        uint startLba,
        uint fillSectors)
    {
        RosettaLadderOptions opt = layout.Options;
        var encodeOptions = new RosettaDvdEncodeOptions(
            TotalSectors: opt.TotalSectors,
            StartLba: startLba,
            FillSectors: fillSectors,
            PsnOffset: opt.PsnOffset,
            InnerRadiusMm: opt.InnerRadiusMm,
            OuterRadiusMm: opt.OuterRadiusMm,
            ChannelBitLengthNm: opt.ChannelBitLengthNm,
            StartAngleDegrees: 0.0,
            ReadCompatibility: opt.ReadCompatibility,
            FastParallelism: opt.FastParallelism,
            Tone: ToneProfile.LegacyBinary,
            PitchLinear: opt.PitchLinear,
            PitchQuadratic: opt.PitchQuadratic,
            PitchCubic: opt.PitchCubic,
            UsableSectors: opt.UsableSectors);
        return RosettaDvdEncoder.BuildSolveOptions(encodeOptions);
    }

    internal static bool TryMapBoxSubpixel(
        RosettaPolarBox box,
        double rMm,
        double cwRad,
        int gridW,
        int gridH,
        out int gx,
        out int gy)
    {
        if (rMm < box.InnerRadiusMm || rMm >= box.OuterRadiusMm)
        {
            gx = 0;
            gy = 0;
            return false;
        }

        double deltaRad = cwRad - box.StartAngleRadians;
        if (deltaRad < 0.0)
        {
            deltaRad += TwoPi;
        }
        if (deltaRad >= TwoPi)
        {
            deltaRad -= TwoPi;
        }

        if (deltaRad >= box.AngularSpanRadians)
        {
            gx = 0;
            gy = 0;
            return false;
        }

        double uMm = Math.Clamp(deltaRad * box.MidRadiusMm, 0.0, Math.BitDecrement(box.WidthMm));
        double yMm = Math.Clamp(box.HeightMm - (rMm - box.InnerRadiusMm), 0.0, Math.BitDecrement(box.HeightMm));
        gx = Math.Clamp((int)((uMm / box.WidthMm) * gridW), 0, gridW - 1);
        gy = Math.Clamp((int)((yMm / box.HeightMm) * gridH), 0, gridH - 1);
        return true;
    }

    private static double SnapOffResonance(
        double nominalOuterMm,
        double minOuterMm,
        double maxOuterMm,
        double sectorLengthMm)
    {
        // Keep the zone outer edge near 0.68 fractional sectors/turn so the 0.295 mm-tall 13x17 um
        // rung (~0.36 sectors/turn radial span) stays clear of integer PO-parity resonances across
        // its full height, and keep the 13-row PI frequency clear of integer multiples too.
        double rowLengthMm = sectorLengthMm / 13.0;
        double bestRadius = nominalOuterMm;
        double bestScore = double.NegativeInfinity;

        for (double r = minOuterMm; r <= maxOuterMm + 1.0e-9; r += 0.005)
        {
            double circumference = TwoPi * r;
            double sectorsPerTurn = circumference / sectorLengthMm;
            double fracSector = sectorsPerTurn - Math.Floor(sectorsPerTurn);
            double rowsPerTurn = circumference / rowLengthMm;
            double fracRow = rowsPerTurn - Math.Floor(rowsPerTurn);

            double poMargin = Math.Min(fracSector - 0.36, 1.0 - fracSector);
            double piDist = Math.Min(fracRow, 1.0 - fracRow);
            double proximityPenalty = Math.Abs(r - nominalOuterMm) * 0.05;
            double score = Math.Min(poMargin, piDist) - proximityPenalty;

            if (score > bestScore)
            {
                bestScore = score;
                bestRadius = r;
            }
        }

        return bestRadius;
    }

    private static (uint StartLba, uint EndLba) ComputeAlignedLbaRange(
        DvdTrackGeometry geom,
        double rInnerMm,
        double rOuterMm,
        double bitLengthMm,
        uint maxOutputSectors)
    {
        double clampedInner = Math.Max(geom.InnerRadiusMm, rInnerMm - RadialLbaGuardMm);
        double clampedOuter = Math.Min(geom.OuterRadiusMm, rOuterMm + RadialLbaGuardMm);
        double s0 = geom.ArcLengthAtRadius(clampedInner);
        double s1 = geom.ArcLengthAtRadius(clampedOuter);
        double sectorLengthMm = DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;

        uint rawStart = (uint)Math.Max(0.0, Math.Floor(s0 / sectorLengthMm));
        uint startLba = (rawStart / DvdEccBlockEncoder.SectorCount) * DvdEccBlockEncoder.SectorCount;

        uint rawEnd = (uint)Math.Ceiling(s1 / sectorLengthMm);
        uint endLba = (uint)Math.Min(
            (ulong)maxOutputSectors,
            ((ulong)(rawEnd + DvdEccBlockEncoder.SectorCount - 1) / DvdEccBlockEncoder.SectorCount) * DvdEccBlockEncoder.SectorCount);

        return (startLba, endLba);
    }

    internal static CdDiscParameters BuildCdParameters(RosettaLadderOptions options)
    {
        var cdParams = new CdDiscParameters(
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.TotalSectors,
            options.LinearVelocityMmPerSecond,
            StartAngleRadians: 0.0,
            ImageOuterRadiusMm: options.OuterRadiusMm,
            PitchLinear: options.PitchLinear,
            PitchQuadratic: options.PitchQuadratic,
            PitchCubic: options.PitchCubic,
            UsableSectors: options.UsableSectors);
        cdParams.Validate();
        return cdParams;
    }

    internal static CdDiscParameters BuildCdParameters(RosettaLadderManifest manifest)
    {
        double velocity = manifest.LinearVelocityMmPerSecond > 0.0
            ? manifest.LinearVelocityMmPerSecond
            : CdDiscParameters.StandardLinearVelocityMmPerSecond;
        var cdParams = new CdDiscParameters(
            manifest.InnerRadiusMm,
            manifest.OuterRadiusMm,
            manifest.TotalSectors,
            velocity,
            StartAngleRadians: 0.0,
            ImageOuterRadiusMm: manifest.OuterRadiusMm,
            PitchLinear: manifest.PitchLinear,
            PitchQuadratic: manifest.PitchQuadratic,
            PitchCubic: manifest.PitchCubic,
            UsableSectors: manifest.FillSectors == manifest.TotalSectors ? null : manifest.FillSectors);
        cdParams.Validate();
        return cdParams;
    }

    private static (uint StartLba, uint EndLba) ComputeAlignedCdLbaRange(
        CdDiscParameters cdParams,
        double rInnerMm,
        double rOuterMm,
        uint maxOutputSectors)
    {
        double clampedInner = Math.Max(cdParams.InnerRadiusMm, rInnerMm - RadialLbaGuardMm);
        double clampedOuter = Math.Min(cdParams.OuterRadiusMm, rOuterMm + RadialLbaGuardMm);

        double ExactSectorAtRadius(double rMm)
        {
            if (cdParams.VariablePitchGeometry is { } varGeom)
            {
                double sectorLengthMm = cdParams.LinearVelocityMmPerSecond / 75.0;
                return varGeom.ArcLengthAtRadius(rMm) / sectorLengthMm;
            }

            double rInSq = cdParams.InnerRadiusMm * cdParams.InnerRadiusMm;
            double rOutSq = cdParams.OuterRadiusMm * cdParams.OuterRadiusMm;
            double fraction = Math.Clamp(((rMm * rMm) - rInSq) / (rOutSq - rInSq), 0.0, 1.0);
            return fraction * cdParams.Sectors;
        }

        uint startLba = (uint)Math.Clamp((long)Math.Floor(ExactSectorAtRadius(clampedInner)), 0L, maxOutputSectors);
        uint endLba = (uint)Math.Clamp((long)Math.Ceiling(ExactSectorAtRadius(clampedOuter)), 0L, maxOutputSectors);
        return (startLba, endLba);
    }

    private static RosettaPolarBox CreatePolarBox(
        double innerMm,
        double outerMm,
        double startDeg,
        double spanDeg,
        double widthMm,
        double heightMm)
    {
        double midMm = 0.5 * (innerMm + outerMm);
        double endDeg = startDeg + spanDeg;
        double midAngleRad = (startDeg + (0.5 * spanDeg)) * DegreesToRadians;
        double centerXMm = midMm * Math.Cos(midAngleRad);
        double centerYDownMm = midMm * Math.Sin(midAngleRad);
        return new RosettaPolarBox(
            innerMm,
            outerMm,
            midMm,
            startDeg,
            endDeg,
            spanDeg,
            centerXMm,
            centerYDownMm,
            widthMm,
            heightMm);
    }

    private static void ValidateOptions(RosettaLadderOptions options)
    {
        if (!Enum.IsDefined(options.Medium))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Medium, "Unsupported Rosetta ladder medium.");
        }

        bool isCd = options.Medium == RosettaLadderMedium.Cd;
        if (isCd)
        {
            if (options.TotalSectors == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.TotalSectors, "TotalSectors must be positive.");
            }
            if (options.UsableSectors is { } usableCd && (usableCd == 0 || usableCd > options.TotalSectors))
            {
                throw new ArgumentOutOfRangeException(nameof(options), usableCd, "UsableSectors must be in 1..TotalSectors.");
            }
            if (!double.IsFinite(options.LinearVelocityMmPerSecond) || options.LinearVelocityMmPerSecond <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.LinearVelocityMmPerSecond, "LinearVelocityMmPerSecond must be finite and positive.");
            }
            long totalBytes = checked((long)options.OutputSectors * CdDiscParameters.BytesPerSector);
            if (options.CdInterleave && totalBytes <= 2L * CdCircEdgeZones.InnerEdgeZoneBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.OutputSectors,
                    $"An interleaved CD-R ladder track must exceed both CIRC edge zones ({2 * CdCircEdgeZones.InnerEdgeZoneBytes} bytes).");
            }
        }
        else
        {
            if (options.TotalSectors < DvdEccBlockEncoder.SectorCount)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.TotalSectors, "TotalSectors must cover at least one 16-sector ECC block.");
            }

            if (options.UsableSectors is { } usable)
            {
                if (usable < DvdEccBlockEncoder.SectorCount
                    || usable > options.TotalSectors
                    || usable % DvdEccBlockEncoder.SectorCount != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(options),
                        usable,
                        "UsableSectors must be a positive multiple of 16 and at most TotalSectors.");
                }
            }
            else if (options.TotalSectors % DvdEccBlockEncoder.SectorCount != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.TotalSectors,
                    "TotalSectors must be a multiple of 16 when UsableSectors is omitted.");
            }

            if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.ChannelBitLengthNm, "ChannelBitLengthNm must be finite and positive.");
            }
        }

        if (!double.IsFinite(options.InnerRadiusMm) || options.InnerRadiusMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.InnerRadiusMm, "InnerRadiusMm must be finite and positive.");
        }
        if (!double.IsFinite(options.OuterRadiusMm) || options.OuterRadiusMm <= options.InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm, "OuterRadiusMm must be finite and greater than InnerRadiusMm.");
        }

        double radialSpanMm = options.OuterRadiusMm - options.InnerRadiusMm;
        if (radialSpanMm < MinimumRadialSpanMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                radialSpanMm,
                $"{(isCd ? "CD-R" : "DVD")} resolution ladder requires at least {MinimumRadialSpanMm:F1} mm of radial span (got {radialSpanMm:F2} mm).");
        }

        if (!double.IsFinite(options.StartAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.StartAngleDegrees, "StartAngleDegrees must be finite.");
        }
        if (options.FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FastParallelism, "FastParallelism must be 0 or 1..64.");
        }

        DvdTrackGeometry.ValidatePitchCoefficients(options.PitchLinear, options.PitchQuadratic, options.PitchCubic);
    }

    private static double NormalizeDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped < 0.0)
        {
            wrapped += 360.0;
        }
        return wrapped >= 360.0 ? 0.0 : wrapped;
    }

    private static double NormalizeRadians(double radians)
    {
        double wrapped = radians % TwoPi;
        if (wrapped < 0.0)
        {
            wrapped += TwoPi;
        }
        return wrapped >= TwoPi ? 0.0 : wrapped;
    }

    internal static (System.Collections.BitArray ActiveBlocks, List<(uint StartBlock, int BlockCount)> Runs) CollectActiveBlockRuns(
        DvdTrackGeometry geom,
        RosettaLadderManifest manifest,
        int maxGapBlocks)
    {
        int totalBlocks = checked((int)(manifest.FillSectors / DvdEccBlockEncoder.SectorCount));
        if (totalBlocks <= 0)
        {
            return (new System.Collections.BitArray(0), []);
        }

        const double radialGuardMm = 0.005;
        const double angularGuardRadians = 0.002;
        uint maxBlockIndex = (uint)(totalBlocks - 1);
        double bitLengthMm = manifest.ChannelBitLengthNm * 1.0e-6;
        double blockLengthMm = DvdEccBlockEncoder.ChannelBitsPerBlock * bitLengthMm;
        var activeBlocks = new System.Collections.BitArray(totalBlocks, defaultValue: false);

        for (int i = 0; i < manifest.Rungs.Count; i++)
        {
            RosettaLadderRung rung = manifest.Rungs[i];
            MarkBoxBlocks(rung.Label.Bounds);
            MarkBoxBlocks(rung.Page);
        }

        var runs = new List<(uint StartBlock, int BlockCount)>();
        int idx = 0;
        while (idx < totalBlocks)
        {
            if (!activeBlocks[idx])
            {
                idx++;
                continue;
            }
            int start = idx;
            while (idx < totalBlocks && activeBlocks[idx])
            {
                idx++;
            }
            if (maxGapBlocks > 0 && runs.Count > 0)
            {
                (uint prevStart, int prevCount) = runs[^1];
                int prevEnd = (int)prevStart + prevCount;
                if (start - prevEnd <= maxGapBlocks)
                {
                    runs[^1] = (prevStart, idx - (int)prevStart);
                    continue;
                }
            }
            runs.Add(((uint)start, idx - start));
        }

        return (activeBlocks, runs);

        void MarkBoxBlocks(RosettaPolarBox box)
        {
            double rMin = Math.Max(geom.InnerRadiusMm, box.InnerRadiusMm - radialGuardMm);
            double rMax = Math.Min(geom.OuterRadiusMm, box.OuterRadiusMm + radialGuardMm);
            double minAngle = Math.Max(0.0, geom.AngleAtRadius(rMin));
            double maxAngle = Math.Min(geom.TotalAngleRadians, geom.AngleAtRadius(rMax));
            if (maxAngle <= minAngle)
            {
                return;
            }

            int turnStart = Math.Max(0, (int)Math.Floor(minAngle / TwoPi) - 1);
            int turnEnd = (int)Math.Ceiling(maxAngle / TwoPi) + 1;

            for (int turn = turnStart; turn <= turnEnd; turn++)
            {
                double a0 = Math.Max(0.0, (turn * TwoPi) + box.StartAngleRadians - angularGuardRadians);
                double a1 = Math.Min(geom.TotalAngleRadians, (turn * TwoPi) + box.StartAngleRadians + box.AngularSpanRadians + angularGuardRadians);
                if (a1 <= minAngle || a0 >= maxAngle || a1 <= a0)
                {
                    continue;
                }

                double s0 = geom.ArcLengthAtAngle(a0);
                double s1 = geom.ArcLengthAtAngle(a1);
                uint blk0 = (uint)Math.Min(maxBlockIndex, (ulong)Math.Max(0.0, Math.Floor(s0 / blockLengthMm)));
                uint blk1 = (uint)Math.Min(maxBlockIndex, (ulong)Math.Max(0.0, Math.Floor(s1 / blockLengthMm)));

                for (uint blk = blk0; blk <= blk1; blk++)
                {
                    activeBlocks[(int)blk] = true;
                }
            }
        }
    }

    private readonly record struct FastRungEntry(
        double MinInnerSq,
        double MaxOuterSq,
        double BoxMinAngleRad,
        double BoxTotalSpanRad,
        double LabelInnerMm,
        double LabelOuterMm,
        double LabelStartRad,
        double LabelSpanRad,
        double LabelUGridScale,
        double LabelHeightMm,
        double LabelYGridScale,
        int LabelGridW,
        int LabelGridH,
        byte[] LabelBitmap,
        double PageInnerMm,
        double PageOuterMm,
        double PageStartRad,
        double PageSpanRad,
        double PageUGridScale,
        double PageHeightMm,
        double PageYGridScale,
        int PageGridW,
        int PageGridH,
        byte[] PageBitmap)
    {
        public static FastRungEntry Create(
            RosettaLadderRung rung,
            IReadOnlyList<string> testTextLines,
            Spleen5x8Font font)
        {
            RosettaPolarBox label = rung.Label.Bounds;
            RosettaPolarBox page = rung.Page;
            double minInnerMm = Math.Min(label.InnerRadiusMm, page.InnerRadiusMm);
            double maxOuterMm = page.OuterRadiusMm;
            double minAngleRad = label.StartAngleRadians;
            double endOffset = page.StartAngleRadians - minAngleRad;
            if (endOffset < 0.0)
            {
                endOffset += TwoPi;
            }
            double totalSpanRad = endOffset + page.AngularSpanRadians;

            int labelGridW = LabelColumns * Spleen5x8Font.CellPixelWidth;
            int labelGridH = LabelRows * Spleen5x8Font.CellPixelHeight;
            var labelBitmap = new byte[labelGridW * labelGridH];
            for (int gy = 0; gy < labelGridH; gy++)
            {
                int rowOffset = gy * labelGridW;
                for (int gx = 0; gx < labelGridW; gx++)
                {
                    labelBitmap[rowOffset + gx] = IsExpectedLabelSubpixelInk(rung, gx, gy, font)
                        ? RosettaDvdEncoder.InkLuminance
                        : RosettaDvdEncoder.PaperLuminance;
                }
            }

            int pageGridW = rung.Columns * Spleen5x8Font.CellPixelWidth;
            int pageGridH = (rung.Rows * Spleen5x8Font.CellPixelHeight) + 3;
            var pageBitmap = new byte[pageGridW * pageGridH];
            for (int gy = 0; gy < pageGridH; gy++)
            {
                int rowOffset = gy * pageGridW;
                for (int gx = 0; gx < pageGridW; gx++)
                {
                    pageBitmap[rowOffset + gx] = IsExpectedPageSubpixelInk(rung, testTextLines, gx, gy, font)
                        ? RosettaDvdEncoder.InkLuminance
                        : RosettaDvdEncoder.PaperLuminance;
                }
            }

            return new FastRungEntry(
                minInnerMm * minInnerMm,
                maxOuterMm * maxOuterMm,
                minAngleRad,
                totalSpanRad,
                label.InnerRadiusMm,
                label.OuterRadiusMm,
                label.StartAngleRadians,
                label.AngularSpanRadians,
                (label.MidRadiusMm / label.WidthMm) * labelGridW,
                label.HeightMm,
                labelGridH / label.HeightMm,
                labelGridW,
                labelGridH,
                labelBitmap,
                page.InnerRadiusMm,
                page.OuterRadiusMm,
                page.StartAngleRadians,
                page.AngularSpanRadians,
                (page.MidRadiusMm / page.WidthMm) * pageGridW,
                page.HeightMm,
                pageGridH / page.HeightMm,
                pageGridW,
                pageGridH,
                pageBitmap);
        }
    }
}
