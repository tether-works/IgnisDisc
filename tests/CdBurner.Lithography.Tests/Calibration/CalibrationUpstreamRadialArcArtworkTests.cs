// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio tests/DiscImageStudio.ArchitectureTests/CalibrationRadialArcArtworkTests.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/tests/DiscImageStudio.ArchitectureTests/CalibrationRadialArcArtworkTests.cs
// IgnisDisc changes: namespace and class name; the upstream Run method of this console-runner class is one xUnit fact.
// See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Calibration;
using System.Text.Json.Nodes;

namespace CdBurner.Lithography.Tests.Calibration;

public static class CalibrationUpstreamRadialArcArtworkTests
{
    [Fact]
    public static void Run()
    {
        var cd = new CalibrationParameters(CalibrationDiscKind.Cd, 24.5, 56.8, 359848, 1200);
        var target = CalibrationTarget.Create(cd, artwork: CalibrationArtwork.RadialArcDashes);
        Require(target.SchemaVersion == 10 && target.HasRadialArcDashes && !target.HasCrossArms
            && !target.HasAngularReference && !target.HasClockRuler && target.VisibleRings.Count == 0
            && target.Groups.Count == 0 && target.RadialArcDashes.Count == 3, "arc target identity");
        AssertGeometry(target);
        string json = CalibrationSessionJson.Serialize(new(target, []));
        var restored = CalibrationSessionJson.Deserialize(json).Target;
        Require(restored.RadialArcDashes.SequenceEqual(target.RadialArcDashes), "frozen arcs survive reload");
        var legacy = JsonNode.Parse(json)!;
        legacy["target"]!["schemaVersion"] = 9;
        var reopened = CalibrationSessionJson.Deserialize(legacy.ToJsonString());
        var savedAgain = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(reopened));
        Require(savedAgain.Target.SchemaVersion == 9, "saving an existing v9 disc preserves its geometry version");
        AssertGeometry(savedAgain.Target);
        var invalid = JsonNode.Parse(json)!;
        invalid["target"]!["schemaVersion"] = 8;
        Throws<ArgumentException>(() => CalibrationSessionJson.Deserialize(invalid.ToJsonString()));
        Throws<ArgumentException>(() => CalibrationCrossFitter.ValidateTraces(target, [new(0, [new(36, 0)])]));
        Throws<ArgumentException>(() => CalibrationTarget.Create(cd with { Kind = CalibrationDiscKind.Dvd },
            artwork: CalibrationArtwork.RadialArcDashes));
        Require(CalibrationTarget.Create(cd, artwork: CalibrationArtwork.ClockRuler).SchemaVersion == 8,
            "clock keeps schema 8");
    }

    private static void AssertGeometry(CalibrationTarget target)
    {
        double[] thicknesses = target.SchemaVersion == 9 ? [.20, .20, .20] : [.32, .30, .28];
        foreach (var arc in target.RadialArcDashes)
        {
            double thickness = thicknesses[arc.Index];
            double expectedHalfAngle = target.SchemaVersion == 9 ? 16 / (2 * arc.RadiusMm) : 20 * Math.PI / 180;
            double expected = target.DrawingInnerRadiusMm + (target.DrawingOuterRadiusMm - target.DrawingInnerRadiusMm)
                * CalibrationRadialArcPattern.Stations[arc.Index];
            Require(Math.Abs(arc.RadiusMm - expected) < 1e-10
                && Math.Abs(arc.OuterRadiusMm - arc.InnerRadiusMm - thickness) < 1e-10
                && Math.Abs(arc.HalfAngleRadians - expectedHalfAngle) < 1e-10, "arc dimensions");
            byte Sample(double radius, double angle) => target.Sample(radius * Math.Cos(angle), radius * Math.Sin(angle));
            Require(Sample(arc.RadiusMm, 0) == 0 && Sample(arc.RadiusMm, arc.HalfAngleRadians * .9) == 0
                && Sample(arc.RadiusMm, arc.HalfAngleRadians * 1.1) == 255
                && Sample(arc.RadiusMm + .49 * thickness, 0) == 0
                && Sample(arc.RadiusMm + .51 * thickness, 0) == 255 && Sample(arc.RadiusMm, Math.PI) == 255,
                "analytic arc bounds");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
