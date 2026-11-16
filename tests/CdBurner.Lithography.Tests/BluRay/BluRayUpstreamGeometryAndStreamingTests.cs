// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio tests/DiscImageStudio.ArchitectureTests/BluRayModuleTests.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/tests/DiscImageStudio.ArchitectureTests/BluRayModuleTests.cs
// IgnisDisc changes: namespace; each upstream test method is an xUnit fact instead of a step of a console runner; the
// tests of upstream's command module (bd-info, bd-probe, bd-encode and bd-generate command lines) are left out because
// the module catalog is not ported. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.BluRay.Encoding;
using CdBurner.Lithography.Imaging;
using System.Text.Json;

namespace CdBurner.Lithography.Tests.BluRay;

public static class BluRayUpstreamGeometryAndStreamingTests
{
    private const string RitekPresetId = "bd-r-ritek-10x";
    private const double RitekInnerRadiusMm = 23.98822105616562;
    private const double RitekOuterRadiusMm = 58.023234731100764;
    private const string Ritek12xPresetId = "bd-r-ritek-12x";
    private const double Ritek12xInnerRadiusMm = 23.98795;
    private const double Ritek12xOuterRadiusMm = 58.02236;
    // Preserved from the saved user preset "Verbatim 64099"
    // (user-bd-20261028-123134-08af), without re-fitting its calibration record.
    private const string VerbatimPresetId = "bd-r-verbatim-64099";
    private const double VerbatimInnerRadiusMm = 23.987042157339335;
    private const double VerbatimOuterRadiusMm = 57.987960341783236;
    private const string Verbatim64293PresetId = "bd-r-verbatim-64293";

    [Fact]
    public static void TestParametersAndGeometry()
    {
        BluRayDiscParameters disc = new();
        disc.Validate();
        Equal(5, BluRayDiscPreset.BuiltIn.Count, "generic and calibrated single-layer presets");
        True(BluRayDiscPreset.BuiltIn.All(preset => preset.LayerCount == 1), "all builtins retain single-layer support");
        Equal(new BluRayDiscParameters(UsableSectors: BluRayDiscParameters.SectorsPerLayer), BluRayDiscPreset.BuiltIn[0].CreateParameters(), "generic geometry remains the first builtin");
        BluRayDiscPreset ritek = BluRayDiscPreset.Find(RitekPresetId);
        Equal("铼德 BD-R 10x", ritek.DisplayName, "Ritek calibrated builtin name");
        Equal(new BluRayDiscParameters(12_219_392, 1, RitekInnerRadiusMm, RitekOuterRadiusMm, 0, UsableSectors: 12_219_392),
            ritek.CreateParameters(), "Ritek geometry preserves the source JSON values exactly");
        True(!ritek.InvertMapping && !ritek.IsCustom && !ritek.IsUserPreset, "Ritek is a normal-polarity builtin");
        BluRayDiscPreset verbatim = BluRayDiscPreset.Find(VerbatimPresetId);
        Equal("Verbatim BD-R (64099)", verbatim.DisplayName, "Verbatim calibrated builtin name");
        Equal(new BluRayDiscParameters(12_219_392, 1, VerbatimInnerRadiusMm, VerbatimOuterRadiusMm, 0, UsableSectors: 12_219_392),
            verbatim.CreateParameters(), "Verbatim geometry preserves the saved user parameters exactly");
        True(!verbatim.InvertMapping && !verbatim.IsCustom && !verbatim.IsUserPreset,
            "Verbatim is a normal-polarity builtin");
        BluRayDiscPreset verbatim64293 = BluRayDiscPreset.Find(Verbatim64293PresetId);
        Equal("Verbatim BD-R (64293)", verbatim64293.DisplayName, "Verbatim BD-R 64293 calibrated builtin name");
        Equal(verbatim.CreateParameters(), verbatim64293.CreateParameters(),
            "Verbatim BD-R 64293 reuses the 64099 geometry");
        True(!verbatim64293.InvertMapping && !verbatim64293.IsCustom && !verbatim64293.IsUserPreset,
            "Verbatim BD-R 64293 is a normal-polarity builtin");
        True(verbatim64293.Contributor == "bczhc", "Verbatim BD-R 64293 contributor is recorded");
        BluRayDiscPreset ritek12x = BluRayDiscPreset.Find(Ritek12xPresetId);
        Equal("铼德 BD-R 12x", ritek12x.DisplayName, "Ritek BD-R 12x calibrated builtin name");
        Equal(new BluRayDiscParameters(12_219_392, 1, Ritek12xInnerRadiusMm, Ritek12xOuterRadiusMm, 0, UsableSectors: 12_219_392),
            ritek12x.CreateParameters(), "Ritek BD-R 12x geometry preserves the source values exactly");
        True(!ritek12x.InvertMapping && !ritek12x.IsCustom && !ritek12x.IsUserPreset,
            "Ritek BD-R 12x is a normal-polarity builtin");
        True(ritek12x.Contributor == "DeepAQ", "Ritek BD-R 12x contributor is recorded");
        BluRayDiscParameters explicitGeometry = new(2048, UsableSectors: 1024);
        BluRayDiscPreset explicitPreset = new("bounded", "Bounded", 1, explicitGeometry);
        Equal(explicitGeometry, explicitPreset.CreateParameters(), "explicit usable capacity is inherited without another deduction");
        Equal(25_025_314_816L, disc.ContentLength, "nominal BD-R byte capacity");
        Equal(0x100000U, disc.GetPhysicalSectorNumber(0), "first physical sector");
        Equal(0xCA73FFU, disc.GetPhysicalSectorNumber(disc.TotalSectors - 1), "last physical sector");
        Throws<ArgumentOutOfRangeException>(() => disc.GetPhysicalSectorNumber(disc.TotalSectors));
        Throws<ArgumentOutOfRangeException>(() => (disc with { LayerCount = 2 }).Validate());
        Throws<ArgumentException>(() => BluRayDiscPreset.Find("bd-r-dl-50gb"));
        (disc with { TotalSectors = 32 }).Validate();
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = 0 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = 63 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { TotalSectors = BluRayDiscParameters.SectorsPerLayer + 32 }).Validate());
        foreach (uint total in new[] { 32U, 64U, 128U, 12_000_000U })
        {
            BluRayTrackGeometry custom = new(disc with { TotalSectors = total });
            Near(58, custom.AtSector(total).RadiusMm, 1e-10, "custom capacity reaches outer radius");
            Near(geometryLength(total), custom.TrackLengthPerLayerMm, 1e-9, "custom track length uses total sectors");
            double growth = custom.EffectiveTrackPitchMicrometers / (2000 * Math.PI);
            double primitive(double r) => (r * Math.Sqrt(r * r + growth * growth)
                + growth * growth * Math.Asinh(r / growth)) / (2 * growth);
            BluRayTrackPoint middle = custom.AtSector(total / 2.0);
            Near(custom.TrackLengthPerLayerMm / 2, primitive(middle.RadiusMm) - primitive(24),
                Math.Max(1e-7, custom.TrackLengthPerLayerMm * 1e-12), "custom midpoint preserves exact arc length");
        }
        static double geometryLength(uint sectors) => sectors / 32.0
            * BluRayDiscParameters.ChannelBitsPerRecordingUnit * BluRayDiscParameters.ChannelBitLengthNm * 1e-6;
        Throws<ArgumentOutOfRangeException>(() => (disc with { InnerRadiusMm = double.NaN }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { OuterRadiusMm = 61 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => (disc with { InnerRadiusMm = 58 }).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, uint.MaxValue - 31, 64).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, 1, 32).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, 0, 33).Validate());
        Throws<ArgumentOutOfRangeException>(() => new BluRayStreamingOptions(disc, disc.TotalSectors - 32, 64).Validate());
        new BluRayStreamingOptions(disc, disc.TotalSectors - 32, 32).Validate();

        BluRayTrackGeometry geometry = new(disc);
        Near(24, geometry.AtSector(0).RadiusMm, 1e-12, "inner boundary");
        Near(58, geometry.AtSector(disc.TotalSectors).RadiusMm, 1e-12, "outer boundary");
        True(geometry.AtSector(32).RadiusMm < 24.001, "short range retains full-disc geometry");
        True(geometry.AtSector(1).AngleRadians < geometry.AtSector(0).AngleRadians,
            "outward track proceeds clockwise on the data-side image");
        True(geometry.EffectiveTrackPitchMicrometers is > 0.30 and < 0.34, "derived pitch includes recording unit overhead");
        True(geometry.AtSector(disc.TotalSectors / 2).RadiusMm > 40, "arc length grows with annular area");
        double lastRadius = 0;
        for (int i = 0; i <= 100; i++)
        {
            BluRayTrackPoint point = geometry.AtLayerFraction(0, i / 100.0);
            True(point.RadiusMm > lastRadius, "radius monotonicity");
            lastRadius = point.RadiusMm;
            // Independent monotonic bisection reference for the optimized inverse.
            double growth = geometry.EffectiveTrackPitchMicrometers / (2000 * Math.PI);
            double lo = disc.InnerRadiusMm, hi = disc.OuterRadiusMm;
            double primitive(double r) => (r * Math.Sqrt(r * r + growth * growth)
                + growth * growth * Math.Log((r + Math.Sqrt(r * r + growth * growth)) / growth)) / (2 * growth);
            double targetArc = geometry.TrackLengthPerLayerMm * i / 100;
            double initialArc = primitive(lo);
            for (int step = 0; step < 70; step++)
            {
                double middle = (lo + hi) / 2;
                if (primitive(middle) - initialArc < targetArc) lo = middle; else hi = middle;
            }
            Near((lo + hi) / 2, point.RadiusMm, 1e-10, "exact inverse matches bisection reference");
        }
        Near(Math.PI / 2, new BluRayTrackGeometry(disc with { StartAngleDegrees = 90 }).AtSector(0).AngleRadians,
            1e-12, "start angle");
        Throws<ArgumentOutOfRangeException>(() => geometry.AtSector(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => geometry.AtLayerFraction(1, 0));
    }

    [Fact]
    public static void TestStreaming()
    {
        BluRayDiscParameters disc = new();
        BluRayStreamingOptions options = new(disc, 0, 64);
        using MemoryStream output = new();
        Throws<InvalidOperationException>(() => BluRayStreamingGenerator.GenerateToStream(output, options, null));
        Equal(0L, output.Length, "missing mapping writes nothing");
        List<BluRayStreamingProgress> updates = [];
        BluRayStreamingSummary summary = BluRayStreamingGenerator.GenerateToStream(output, options,
            new BluRayConstantByteMapping(0xA6), new InlineProgress<BluRayStreamingProgress>(updates.Add));
        Equal(64L * 2048, summary.BytesWritten, "exact range length");
        byte[] payload = output.ToArray();
        for (int sector = 0; sector < 64; sector++)
        {
            byte[] frame = new byte[2052];
            BluRayEdc.WriteDataFrame(payload.AsSpan(sector * 2048, 2048), frame);
            // Independent bit-register implementation of §13.4, not the generator's helper.
            uint state = 0x8000U | ((disc.GetPhysicalSectorNumber((uint)sector) >> 5) & 0x7FFF);
            for (int i = 0; i < 2048; i++)
            {
                Equal((byte)0xA6, (byte)(frame[i] ^ (byte)state), "forward scrambled payload is explicit probe value");
                for (int bit = 0; bit < 8; bit++)
                {
                    uint feedback = ((state >> 15) ^ (state >> 14) ^ (state >> 12) ^ (state >> 3)) & 1;
                    state = ((state << 1) | feedback) & 0xFFFF;
                }
            }
        }
        True(payload.AsSpan(0, 2048).SequenceEqual(payload.AsSpan(2048, 2048)), "seed resets each sector");
        True(!payload.AsSpan(0, 2048).SequenceEqual(payload.AsSpan(32 * 2048, 2048)), "next cluster uses next seed");
        Equal(0L, updates[0].CompletedBytes, "initial progress");
        Equal(summary.BytesWritten, updates[^1].CompletedBytes, "final progress");

        using ForwardOnlyStream sequential = new();
        BluRayStreamingGenerator.GenerateToStream(sequential, new(disc, 0, 32), new BluRayConstantByteMapping(3));
        Equal(65536L, sequential.BytesWritten, "no seek is required");
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayStreamingGenerator.GenerateToStream(untouched,
            options, new BluRayConstantByteMapping(0), cancellationToken: cancel.Token));
        Equal(0L, untouched.Length, "pre-cancel does not write");
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: {expected} != {actual}"); }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double expected, double actual, double tolerance, string message)
        => True(Math.Abs(expected - actual) <= tolerance, message);
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    { public void Report(T value) => callback(value); }
    private sealed class ThrowingMapping : IBluRayPayloadMapping
    { public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination) => throw new IOException("test mapper failure"); }
    private sealed class ForwardOnlyStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
    }
}
