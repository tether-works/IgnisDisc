// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/Calibration/CalibrationSessionJson.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/Calibration/CalibrationSessionJson.cs
// IgnisDisc changes: namespace; messages in English, taken from upstream's own translation catalog. See THIRD-PARTY-NOTICES.md.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdBurner.Lithography.Calibration;

public sealed record CalibrationSession(
    CalibrationTarget Target, List<CalibrationObservation> Observations,
    CalibrationFitOptions? FitOptions = null, bool CdInterleave = true,
    CalibrationPhotoSession? Photo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationConfirmedFit? ConfirmedFit = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Observations);
        Target.Validate();
        CalibrationFitter.ValidateObservations(Target, Observations);
        FitOptions?.Validate(Target);
        Photo?.Validate();
        ConfirmedFit?.Validate(Target, Photo);
        if (Target.HasWideAddressBands && Photo?.UseRingSeed == true)
            throw new ArgumentException("Use the band measurements to estimate initial radii for the next disc.");
        bool wrongWideDiscSize = Target.Parameters.Kind == CalibrationDiscKind.Cd && Photo?.PhysicalOuterRadiusMm != 60;
        if (Target.HasWideAddressBands && wrongWideDiscSize && Photo is { } widePhoto
            && (widePhoto.RingObservations is { Count: > 0 } || widePhoto.ArcDraft is not null))
            throw new ArgumentException("Broad-band measurements require a rectified photo of a 12 cm disc and provide initial radii for the next target.");
        if (Photo?.RadialArcLengths is { } lengths) lengths.Validate(Target, Photo);
        if (Photo?.PreferredClockAngleDegrees is double angle &&
            (!Target.HasClockRuler || !double.IsFinite(angle) || angle < 0 || angle >= 360))
            throw new ArgumentException("The preferred direction for reading the clock ruler is invalid.");
        if ((!Target.HasCrossArms || Target.HasSingleRadialLine || Target.HasBoundaryPattern) && Photo is not null)
            CalibrationCrossFitter.ValidateTraces(Target, Photo.Traces);
        if (Photo?.BoundarySegments is { } segments)
        {
            if (!Target.HasBoundaryPattern || Photo.Traces.Count != 0)
                throw new ArgumentException("Boundary points require the matching boundary-calibration record and cannot be combined with centerline points from cross or single-line targets.");
            CalibrationBoundaryFitter.ValidateSegments(Target, segments);
        }
        if (Photo?.RingObservations is { } rings)
            CalibrationRingFitter.ValidateObservations(Target, rings, Photo.PhysicalOuterRadiusMm);
        if (Photo?.ArcDraft is { } draft && (uint)draft.RingIndex >= (uint)CalibrationRingFitter.MeasurementCount(Target))
            throw new ArgumentException("The ring number or measured radius is invalid.");
        if (Photo?.BandRaySelection is int selection &&
            (!Target.HasWideAddressBands || wrongWideDiscSize || !Photo.RectificationConfirmed
             || Photo.ArcDraft is not null || (uint)selection >= (uint)CalibrationRingFitter.MeasurementCount(Target)))
            throw new ArgumentException("The selected edge or photo state in the single-ray record is invalid.");
    }
}

/// <summary>A manually confirmed fit belongs to the photographed target, whose original
/// generation geometry stays frozen. Its selected candidate supplies the next disc's setup.</summary>
public sealed record CalibrationConfirmedFit(string TargetId, CalibrationFitResult Result, int SelectedCandidateIndex)
{
    internal void Validate(CalibrationTarget target, CalibrationPhotoSession? photo)
    {
        if (TargetId != target.Id || photo?.RectificationConfirmed != true
            || !(target.HasCrossArms || target.HasSingleRadialLine || target.HasBoundaryPattern)
            || Result is null || Result.Candidates is null || Result.Candidates.Count is < 1 or > 20
            || (uint)SelectedCandidateIndex >= (uint)Result.Candidates.Count
            || Result.Warnings is null || Result.Warnings.Any(warning => warning is null)
            || !double.IsFinite(Result.RotationRadians) || !ValidError(Result.RmsErrorMm))
            throw new ArgumentException("Invalid confirmed calibration fit.");
        if (Result.BestParameters is { } best) target.ValidateCompatible(best);
        if (Result.Succeeded && Result.BestParameters is null)
            throw new ArgumentException("Invalid confirmed calibration fit.");
        foreach (CalibrationFitCandidate candidate in Result.Candidates)
        {
            if (candidate is null || candidate.Parameters is null
                || !double.IsFinite(candidate.RotationRadians) || !ValidError(candidate.RmsErrorMm)
                || !ValidError(candidate.RobustCost) || !ValidError(candidate.MaxErrorMm) || candidate.OutlierCount < 0)
                throw new ArgumentException("Invalid confirmed calibration fit candidate.");
            target.ValidateCompatible(candidate.Parameters);
        }
    }

    private static bool ValidError(double value) => double.IsFinite(value) && value >= 0;
}

/// <summary>Selection in three-point mode and up to two clicks on an unfinished inner edge.</summary>
public sealed record CalibrationRingArcDraft(int RingIndex, IReadOnlyList<CalibrationPoint> Points);

/// <summary>The bounded source photo is embedded at its working resolution so a saved calibration can be reopened on its own.</summary>
public sealed record CalibrationPhotoSession(string ImagePngBase64,
    IReadOnlyList<CalibrationPoint> OuterBoundary, IReadOnlyList<CalibrationPoint> HoleBoundary,
    double PhysicalOuterRadiusMm, bool RectificationConfirmed,
    IReadOnlyList<CalibrationCrossTrace> Traces,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsCoarseMode = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalibrationRingObservation>? RingObservations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UseRingSeed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationRingArcDraft? ArcDraft = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreferredClockAngleDegrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationArcLengthSession? RadialArcLengths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BandRaySelection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalibrationBoundarySegment>? BoundarySegments = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ImagePngBase64) || ImagePngBase64.Length > CalibrationPhotoLimits.MaxEncodedPhotoLength)
            throw new ArgumentException("The calibration photo is empty or too large. Import the photo again.");
        if (PhysicalOuterRadiusMm is not (40 or 60))
            throw new ArgumentException("Choose a 12 cm or 8 cm disc.");
        ValidateBoundary(OuterBoundary);
        ValidateBoundary(HoleBoundary);
        if (Traces is null || Traces.Count > 1 || Traces.Any(t => t is null || t.ArmIndex != 0
            || t.Points is null || t.Points.Count > 4000 || t.Points.Any(p => p is null
                || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                || Math.Sqrt(p.X * p.X + p.Y * p.Y) > PhysicalOuterRadiusMm))
            || Traces.Select(t => t.ArmIndex).Distinct().Count() != Traces.Count)
            throw new ArgumentException("The cross-trace record is invalid.");
        if (!RectificationConfirmed && Traces.Any(t => t.Points.Count > 0))
            throw new ArgumentException("Confirm photo rectification before saving trace points.");
        if (BoundarySegments is { } segments && (segments.Count > 64
            || segments.Any(segment => segment is null || segment.Points is null
                || segment.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                    || p.X * p.X + p.Y * p.Y > PhysicalOuterRadiusMm * PhysicalOuterRadiusMm))
            || (!RectificationConfirmed && segments.Any(segment => segment.Points.Count > 0))))
            throw new ArgumentException("Segmented boundary traces require confirmed photo rectification, with every point on the disc.");
        if (RingObservations is { Count: > 0 } rings
            && (!RectificationConfirmed || rings.Any(r => r is null || !double.IsFinite(r.RadiusMm)
                || r.RadiusMm < 7.5 || r.RadiusMm > PhysicalOuterRadiusMm)))
            throw new ArgumentException("Ring measurements require a rectified photo and the arcs must lie on the disc.");
        if (UseRingSeed && RingObservations is not { Count: >= 3 })
            throw new ArgumentException("At least three aligned rings are required to use their measurements as the starting point for fine calibration.");
        if (ArcDraft is { } draft && (!RectificationConfirmed || draft.Points is null || draft.Points.Count > 2
            || draft.Points.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                || p.X * p.X + p.Y * p.Y < 7.5 * 7.5 || p.X * p.X + p.Y * p.Y > PhysicalOuterRadiusMm * PhysicalOuterRadiusMm)
            || (draft.Points.Count > 0 && RingObservations?.Any(r => r.RingIndex == draft.RingIndex) == true)))
            throw new ArgumentException("The unfinished three-point ring measurement is invalid.");
    }

    private static void ValidateBoundary(IReadOnlyList<CalibrationPoint> points)
    {
        if (points is null || points.Count is < 5 or > 128 || points.Any(p => p is null
            || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0
            || p.X >= CalibrationPhotoLimits.MaxDimension || p.Y >= CalibrationPhotoLimits.MaxDimension))
            throw new ArgumentException("The photo outline control points are invalid.");
    }
}

public static class CalibrationSessionJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(CalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        string json = JsonSerializer.Serialize(session, JsonOptions);
        if (json.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("The calibration record file is too large.");
        return json;
    }

    public static CalibrationSession Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("The calibration record file is too large.");
        CalibrationSession session = JsonSerializer.Deserialize<CalibrationSession>(json, JsonOptions)
            ?? throw new ArgumentException("The calibration record is empty.");
        session.Validate();
        return session;
    }

    public static void Save(string path, CalibrationSession session)
    {
        string json = Serialize(session);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static CalibrationSession Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > CalibrationPhotoLimits.MaxSessionLength) throw new ArgumentException("The calibration record file is too large.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Deserialize(reader.ReadToEnd());
    }
}
