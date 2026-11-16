// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio tests/DiscImageStudio.ArchitectureTests/BluRayImageMappingTests.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/tests/DiscImageStudio.ArchitectureTests/BluRayImageMappingTests.cs
// IgnisDisc changes: namespace; each upstream test method is an xUnit fact instead of a step of a console runner; the
// loader test writes its PNG with IgnisDisc's PNG writer, and the checks of upstream's image codec limits and
// header-only source check are left out (that codec is not ported). See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.BluRay.Encoding;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.BluRay;

public static class BluRayUpstreamImageMappingTests
{

    [Fact]
    public static void TestRasterAndInjection()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8];
        BluRayGray8Raster raster = new(4, 2, pixels);
        pixels[0] = 99;
        Require(raster.Pixels[0] == 1, "the raster owns an immutable source snapshot");
        Require(raster.SampleDiscPoint(-43.5, 14.5, 58) == 1
            && raster.SampleDiscPoint(43.5, -14.5, 58) == 8,
            "nearest sampling preserves orientation and rectangular aspect ratio");
        Require(raster.SampleDiscPoint(0, 50, 58) == 255
            && raster.SampleDiscPoint(60, 0, 58) == 255, "letterbox and outside-image areas are white");
        Throws<ArgumentOutOfRangeException>(() => new BluRayGray8Raster(0, 2, []));
        Throws<ArgumentException>(() => new BluRayGray8Raster(2, 2, [1]));
        Throws<ArgumentNullException>(() => new BluRayImagePayloadMapping(new(), raster, null!));
        Throws<ArgumentNullException>(() => BluRayImageLoader.CreateMapping("missing.png", new(), null!));
        BluRayImagePayloadMapping mapping = new(new(), raster, new TestIntensityMapping());
        Throws<ArgumentOutOfRangeException>(() => mapping.GetPosition(0, -1));
        Throws<ArgumentOutOfRangeException>(() => mapping.GetPosition(0, 2048));
        Throws<ArgumentOutOfRangeException>(() => mapping.GetPosition(BluRayDiscParameters.SectorsPerLayer, 0));
        Throws<ArgumentException>(() => mapping.FillScrambledPayload(new(0, 0, new()), new byte[2048]));
        Throws<ArgumentException>(() => mapping.FillScrambledPayload(new(0, 0x100000, new()), new byte[1]));
    }

    [Fact]
    public static void TestEncoderPositions()
    {
        BluRayDiscParameters disc = new();
        BluRayImagePayloadMapping mapping = new(disc, new BluRayGray8Raster(1, 1, [128]), new TestIntensityMapping());
        byte[] zeroPayload = new byte[BluRayEccEncoder.PayloadBytes];
        byte[] baseline = BluRayEccEncoder.Encode(zeroPayload, disc.GetPhysicalSectorNumber(0));
        (uint Sector, int Offset)[] probes = [(0, 0), (0, 215), (0, 216), (1, 0), (5, 1379), (16, 2047), (31, 0), (31, 2047)];
        foreach ((uint sector, int offset) in probes)
        {
            byte[] marker = (byte[])zeroPayload.Clone();
            marker[sector * 2048 + offset] = 0x80;
            byte[] encoded = BluRayEccEncoder.Encode(marker, disc.GetPhysicalSectorNumber(0));
            BluRayPayloadBytePosition position = mapping.GetPosition(sector, offset);
            int index = position.RecordingFrame * BluRayEccEncoder.EccBytesPerFrame + position.EccColumn;
            Require((encoded[index] ^ baseline[index]) == 0x80,
                $"payload {sector}:{offset} follows actual LDC interleave and BIS multiplexing");
            Require(position.EccColumn is not (38 or 77 or 116) && position.RecordingFrame < 432,
                "payload positions never claim BIS or LDC parity cells");

            // Locate the middle input bit through the real dc-control assembler, independently
            // of the sampling code's count formula. Modulation position remains nominal.
            byte[] frameMarker = new byte[BluRayEccEncoder.EccBytesPerFrame];
            frameMarker[position.EccColumn] = 0x08;
            byte[] assembled = BluRayRecordingFrameEncoder.CreateDataBits(frameMarker, new byte[28]);
            int middleBit = Array.IndexOf(assembled, (byte)1);
            Require(middleBit >= 0 && assembled.Count(value => value == 1) == 1, "midpoint marker stays unique");
            double expected = 2760 + position.RecordingFrame * 1932 + 30 + middleBit * 1.5;
            Require(position.NominalChannelBitPosition == expected, "nominal midpoint includes inserted dc-control bits");
        }

        BluRayPayloadBytePosition first = mapping.GetPosition(0, 0);
        BluRayPayloadBytePosition next = mapping.GetPosition(32, 0);
        Require(next.NominalChannelBitPosition - first.NominalChannelBitPosition
            == BluRayDiscParameters.ChannelBitsPerRecordingUnit, "cluster progression includes complete run-in/run-out spacing");
        Require(first.TrackPoint.RadiusMm >= disc.InnerRadiusMm && next.TrackPoint.RadiusMm > first.TrackPoint.RadiusMm,
            "nominal payload positions progress outward on the single-layer spiral");
        Require(new BluRayTrackGeometry(disc).AtSector(0.1).AngleRadians < 0,
            "forward recording follows clockwise disc-fixed coordinates with positive Y up");
        BluRayDiscParameters topDisc = disc with { StartAngleDegrees = 90 };
        BluRayImagePayloadMapping topMapping = new(topDisc, new BluRayGray8Raster(1, 2, [11, 22]), new TestIntensityMapping());
        byte[] topPayload = new byte[2048];
        topMapping.FillScrambledPayload(new(0, topDisc.GetPhysicalSectorNumber(0), topDisc), topPayload);
        Require(topPayload[0] == (11 ^ 0xa7), "a 90-degree source position samples the top of the image");
    }

    [Fact]
    public static void TestProceduralMapping()
    {
        BluRayDiscParameters disc = new(TotalSectors: 96, StartAngleDegrees: 27);
        BluRayGray8Raster raster = new(2, 2, [0, 50, 128, 255]);
        BluRayImagePayloadMapping image = new(disc, raster, new BluRayRunLengthMapping());
        BluRayImagePayloadMapping pattern = new(disc, (x, y) => raster.SampleDiscPoint(x, y, disc.OuterRadiusMm),
            new BluRayRunLengthMapping());
        byte[] rasterBytes = new byte[2048];
        byte[] patternBytes = new byte[2048];
        foreach (uint sector in new[] { 0U, 32U, 64U, 95U })
        {
            BluRaySectorContext context = new(sector, disc.GetPhysicalSectorNumber(sector), disc);
            image.FillScrambledPayload(context, rasterBytes);
            pattern.FillScrambledPayload(context, patternBytes);
            Require(rasterBytes.SequenceEqual(patternBytes), "procedural target uses the exact raster sampling coordinates and polarity");
        }
        BluRayPayloadBytePosition point = image.GetPosition(64, 0);
        double sectorPosition = point.NominalChannelBitPosition / (BluRayDiscParameters.ChannelBitsPerRecordingUnit / 32.0);
        Require(point.TrackPoint == new BluRayTrackGeometry(disc).AtSector(sectorPosition),
            "all sampled positions use configured total sectors");
        Require(point.TrackPoint.RadiusMm > 45, "custom-capacity image reaches the outer disc instead of using nominal-capacity denominator");
    }

    [Fact]
    public static void TestStreaming()
    {
        BluRayDiscParameters disc = new();
        TestIntensityMapping strategy = new();
        BluRayImagePayloadMapping mapping = new(disc, new BluRayGray8Raster(1, 1, [90]), strategy);
        using MemoryStream output = new();
        BluRayStreamingSummary result = BluRayStreamingGenerator.GenerateToStream(output, new(disc), mapping);
        Require(result.BytesWritten == 65536 && strategy.CallCount == 65536, "every payload byte receives an injected mapping call");
        byte[] bytes = output.ToArray();
        byte[] mask = BluRayScrambler.CreateMask(disc.GetPhysicalSectorNumber(0), 2048);
        for (int index = 0; index < bytes.Length; index++)
            Require((bytes[index] ^ mask[index % 2048]) == (90 ^ 0xa7), "stream preserves the injected post-scramble target");
        Require(strategy.LastPosition is { Sector: 31, ByteOffset: 2047 }, "strategy receives the exact addressed payload position");
    }

    [Fact]
    public static void TestRunLengthMapping()
    {
        BluRayRunLengthMapping normal = new();
        BluRayRunLengthMapping inverted = new(invert: true);
        Require(!normal.Invert && inverted.Invert, "mapping captures its polarity");
        for (int luminance = 0; luminance <= 255; luminance++)
        {
            byte expected = luminance < 128 ? (byte)0xAA : (byte)0x44;
            Require(normal.Map((byte)luminance, default) == expected,
                "binary threshold assigns 0..127 to AA and 128..255 to 44");
            Require(inverted.Map((byte)luminance, default) == normal.Map((byte)(255 - luminance), default),
                "inversion exchanges both endpoint patterns");
        }

        // A mixed image must still select the right Q at each physical position after
        // streaming, even at the final cluster and after polarity reversal.
        BluRayDiscParameters disc = new();
        BluRayGray8Raster raster = new(2, 2, [0, 128, 127, 255]);
        uint start = disc.TotalSectors - 32;
        foreach (BluRayRunLengthMapping strategy in new[] { normal, inverted })
        {
            BluRayImagePayloadMapping mapping = new(disc, raster, strategy);
            using MemoryStream output = new();
            BluRayStreamingGenerator.GenerateToStream(output, new(disc, start, 32), mapping);
            byte[] bytes = output.ToArray();
            byte[] ecc = BluRayEccEncoder.Encode(bytes, disc.GetPhysicalSectorNumber(start));
            int threeT = 0, sixT = 0;
            for (int index = 0; index < bytes.Length; index++)
            {
                BluRayPayloadBytePosition position = mapping.GetPosition(start + (uint)(index / 2048), index % 2048);
                byte sample = raster.SampleDiscPoint(position.TrackPoint.Xmm, position.TrackPoint.Ymm, disc.OuterRadiusMm);
                byte expected = strategy.Map(sample, position);
                Require(ecc[position.RecordingFrame * 155 + position.EccColumn] == expected,
                    "mixed-image Q survives drive scrambling and actual ECC interleave");
                if (expected == BluRayRunLengthMapping.ThreeTByte) threeT++;
                else sixT++;
            }
            Require(threeT > 0 && sixT > 0, "the end-to-end image exercises both patterns");
        }
    }

    [Fact]
    public static void TestRunLengthEncoding()
    {
        BluRayDiscParameters disc = new();
        foreach (uint start in new[] { 0U, disc.TotalSectors - 32 })
        {
            long[][] counts = new long[2][];
            for (int side = 0; side < 2; side++)
            {
                using MemoryStream output = new();
                BluRayImagePayloadMapping mapping = new(disc, new BluRayGray8Raster(1, 1, [(byte)(side * 255)]), new BluRayRunLengthMapping());
                BluRayStreamingGenerator.GenerateToStream(output, new(disc, start, 32), mapping);
                BluRayRecordingUnitResult encoded = BluRayRecordingUnitEncoder.Encode(output.ToArray(), disc.GetPhysicalSectorNumber(start));
                ReadOnlySpan<byte> bits = encoded.ModulationBits.AsSpan(BluRayRecordingUnitEncoder.RunInChannelBits,
                    BluRayRecordingUnitEncoder.PhysicalClusterChannelBits);
                counts[side] = new long[10];
                int previous = -1;
                for (int i = 0; i < bits.Length; i++)
                {
                    if (bits[i] == 0) continue;
                    if (previous >= 0)
                    {
                        int run = i - previous;
                        Require(run is >= 2 and <= 9, "generated physical clusters retain legal run lengths including sync");
                        counts[side][run]++;
                    }
                    previous = i;
                }
            }
            Require(counts[0][3] > 250_000 && counts[0][3] > 10 * counts[1][3],
                "dark endpoint favours absolute 3T counts through the complete encoding chain");
            Require(counts[1][6] > 110_000 && counts[1][6] > 10 * counts[0][6],
                "light endpoint favours absolute 6T counts through the complete encoding chain");
        }
    }

    [Fact]
    public static void TestImageLoader()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-bd-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "rgba.png");
            byte[] bgra =
            [
                0, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 128,
                0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
            ];
            PngRaster.SaveBgra32(path, new BgraRaster(3, 2, bgra));
            BluRayGray8Raster raster = BluRayImageLoader.Load(path);
            Require(raster.Width == 3 && raster.Height == 2 && raster.Pixels.SequenceEqual(new byte[] { 0, 255, 127, 54, 182, 18 }),
                "loader preserves dimensions, uses weighted RGB, and composites alpha over white");
            using (FileStream unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(unlocked.Length > 0, "loader releases the source file");
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            Throws<OperationCanceledException>(() => BluRayImageLoader.Load(path, canceled.Token));
            BluRayImagePayloadMapping mapping = BluRayImageLoader.CreateMapping(path, new(), new TestIntensityMapping());
            Require(mapping.GetPosition(0, 0).PhysicalSectorNumber == 0x100000, "file loader creates a ready mapping with supplied strategy");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Arbitrary test transformation; this is deliberately not a physical black/white calibration.
    private sealed class TestIntensityMapping : IBluRayLuminanceMapping
    {
        public int CallCount { get; private set; }
        public BluRayPayloadBytePosition LastPosition { get; private set; }
        public byte Map(byte luminance, BluRayPayloadBytePosition position)
        {
            CallCount++;
            LastPosition = position;
            return (byte)(luminance ^ 0xa7);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Blu-ray image: expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray image: " + message);
    }
}
