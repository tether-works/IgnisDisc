using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd;

namespace CdBurner.Lithography.Tests.Calibration;

/// <summary>
/// The ported calibration targets against the upstream golden hashes (Fixtures/UpstreamGoldens, recorded from
/// DiscImageStudio at the pinned commit by the small host in scripts/Make-LithographyGoldens.ps1). Each test makes
/// the calls that host makes for the recorded command line.
/// </summary>
public sealed class CalibrationTargetGoldenTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");

    private static (string Sha256, long Bytes) Golden(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "upstream-goldens.json")));
        JsonElement output = document.RootElement.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("outputs").EnumerateArray())
            .Single(o => o.GetProperty("file").GetString() == file);
        return (output.GetProperty("sha256").GetString()!, output.GetProperty("bytes").GetInt64());
    }

    private static void AssertGolden(string goldenFile, MemoryStream actual)
    {
        (string sha, long bytes) = Golden(goldenFile);
        Assert.Equal(bytes, actual.Length);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(actual.ToArray())).ToLowerInvariant());
    }

    // golden-harness calibration-target <medium> WavyBoundary <r0> <r1> <sectors> <output>
    private static CalibrationTarget Target(CalibrationDiscKind kind, double inner, double outer, long sectors)
    {
        double density = kind switch
        {
            CalibrationDiscKind.Cd => CdDiscParameters.StandardLinearVelocityMmPerSecond,
            CalibrationDiscKind.Dvd => DvdStreamingOptions.StandardChannelBitLengthNm,
            _ => CalibrationParameters.BluRayChannelBitLengthNm,
        };
        CalibrationTarget target = CalibrationTarget.Create(
            new CalibrationParameters(kind, inner, outer, sectors, density), artwork: CalibrationArtwork.WavyBoundary);
        Assert.Equal(16, target.SchemaVersion);
        return target;
    }

    [Fact]
    public void Cd_boundary_target_matches_the_upstream_golden_hash()
    {
        CalibrationTarget target = Target(CalibrationDiscKind.Cd, 24.5, 56.8, 1200);
        using MemoryStream output = new();

        CdTrackGenerator.GeneratePatternToStream(target, output, interleave: true,
            audioByteOrder: CdAudioByteOrder.LittleEndian);

        // Upstream's 1,332 zero placeholders are palette bytes now; see CdPictureTrackTests.AsUpstreamWrote.
        using MemoryStream upstream = new(Tests.Cd.CdPictureTrackTests.AsUpstreamWrote(output.ToArray(), 0));
        AssertGolden("cd-calibration.raw", upstream);
    }

    [Fact]
    public void Dvd_boundary_target_matches_the_upstream_golden_hash()
    {
        CalibrationTarget target = Target(CalibrationDiscKind.Dvd, 24, 58, 1600);
        using MemoryStream output = new();

        DvdStreamingGenerator.GeneratePattern(target, output, CalibrationTargetFile.DvdOptions(target));

        AssertGolden("dvd-calibration.iso", output);
    }

    [Fact]
    public void Blu_ray_boundary_target_matches_the_upstream_golden_hash()
    {
        CalibrationTarget target = Target(CalibrationDiscKind.BluRay, 24, 58, 256);
        BluRayDiscParameters disc = CalibrationTargetFile.BluRayParameters(target.Parameters);
        using MemoryStream output = new();

        BluRayStreamingGenerator.GenerateToStream(output, new BluRayStreamingOptions(disc, 0, disc.OutputSectors),
            CalibrationTargetFile.BluRayPatternMapping(target));

        AssertGolden("bd-calibration.iso", output);
    }
}
