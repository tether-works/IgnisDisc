// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio tests/DiscImageStudio.ArchitectureTests/BluRaySidecarTests.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/tests/DiscImageStudio.ArchitectureTests/BluRaySidecarTests.cs
// IgnisDisc changes: namespace; the upstream entry point is an xUnit fact; the last test calls the image generator
// with the request upstream's bd-generate command builds, because the command module is not ported. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.BluRay.Encoding;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.BluRay;

public static class BluRayUpstreamSidecarTests
{
    [Fact]
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestRoundtrip(directory);
            TestRejection(directory);
            TestModuleWritesSidecar(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestRoundtrip(string directory)
    {
        BluRayDiscParameters disc = new(1024);
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
        string iso = Path.Combine(directory, "picture.iso");
        BluRayIsoMetadata metadata = BluRayIsoMetadata.FromPlan(
            plan,
            Path.Combine(directory, "source.png"),
            iso,
            invertMapping: true,
            new ToneProfile(0.75, ToneProfile.TwoLevels, -4, SrgbLinearization: true),
            DateTimeOffset.UtcNow);
        metadata.Save(iso);

        BluRayIsoMetadata loaded = BluRayIsoMetadata.TryLoad(iso)
            ?? throw new InvalidOperationException("BD sidecar did not load.");
        Require(loaded.TotalSectors == 1024, "sectors");
        Require(loaded.DrawingStartSector == 0 && loaded.DrawingEndSectorExclusive == 1024, "drawing bounds");
        Require(loaded.InvertMapping, "polarity");
        Require(loaded.Tone.SrgbLinearization && Math.Abs(loaded.Tone.DitherStrength - 0.75) < 1e-12,
            "tone profile");
        Require(loaded.IsoBytes == disc.ContentLength && loaded.ContentLength == disc.ContentLength,
            "content length");
        Require(loaded.VolumeLabel == "BD_IMAGE" && loaded.DataDirectory is null, "volume metadata");
    }

    private static void TestRejection(string directory)
    {
        string iso = Path.Combine(directory, "bad.iso");
        File.WriteAllText(BluRayIsoMetadata.PathFor(iso), "{ \"format\": \"other\", \"totalSectors\": 32 }");
        Require(BluRayIsoMetadata.TryLoad(iso) is null, "wrong format is ignored");

        BluRayIsoMetadata invalid = new(
            "source.png", iso, 0, 1000, 24, 58, false, ToneProfile.LegacyBinary,
            0, 1000, 24, 58, "BD_IMAGE", null, null, DateTimeOffset.UtcNow);
        try
        {
            invalid.Save(iso);
            throw new InvalidOperationException("BD sidecar accepted unaligned sectors.");
        }
        catch (InvalidDataException)
        {
        }

        File.WriteAllText(BluRayIsoMetadata.PathFor(iso), "not json");
        Require(BluRayIsoMetadata.TryLoad(iso) is null, "malformed JSON is ignored");
    }

    private static void TestModuleWritesSidecar(string directory)
    {
        string iso = Path.Combine(directory, "module.iso");
        BluRayImageGenerator generator = new((_, _) => new BluRayGray8Raster(8, 8, Enumerable.Repeat((byte)128, 64).ToArray()));
        // --total-sectors 32 --tone-dither 1 --tone-gamma true --invert true
        DiscImageResult result = generator.Generate(new BluRayImageRequest(Path.Combine(directory, "source.png"), iso,
            new BluRayDiscParameters(32), new ToneProfile(1, ToneProfile.TwoLevels, 0, true), InvertMapping: true));
        Require(result.OutputPath == Path.GetFullPath(iso), "module generation");
        Require(File.Exists(BluRayIsoMetadata.PathFor(iso)), "module writes the sidecar");
        BluRayIsoMetadata metadata = BluRayIsoMetadata.TryLoad(iso)
            ?? throw new InvalidOperationException("module sidecar did not load.");
        Require(metadata.InvertMapping && metadata.Tone.SrgbLinearization, "module polarity and gamma");
        Require(metadata.Tone.DitherStrength == 1, "module dither");
        Require(new FileInfo(iso).Length == 32L * 2048, "module ISO length");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Blu-ray sidecar: " + message + ".");
        }
    }
}
