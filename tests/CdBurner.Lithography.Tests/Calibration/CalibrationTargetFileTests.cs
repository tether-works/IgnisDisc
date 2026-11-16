using System.Security.Cryptography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Cd;

namespace CdBurner.Lithography.Tests.Calibration;

/// <summary>Writing a calibration target and its record to disk for each stage.</summary>
public sealed class CalibrationTargetFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-caltarget-" + Guid.NewGuid().ToString("N"));

    public CalibrationTargetFileTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Small discs with the radii of the golden cases, so a test writes megabytes and not a whole disc.
    private static CalibrationParameters SmallCd => new(CalibrationDiscKind.Cd, 24.5, 56.8, 1200, 1200);
    private static CalibrationParameters SmallDvd => new(CalibrationDiscKind.Dvd, 24, 58, 1600, 133.3);
    private static CalibrationParameters SmallBluRay => new(CalibrationDiscKind.BluRay, 24, 58, 256, 74.5);

    // A whole DVD-R: the coarse bands sit at fixed sector addresses and need the real capacity.
    private static CalibrationParameters FullDvd => new(CalibrationDiscKind.Dvd, 24, 58, 2_297_888, 133.3);

    [Theory]
    [InlineData(CalibrationStage.Coarse, CalibrationArtwork.AngleSweepBands, 13, 0.02, null)]
    [InlineData(CalibrationStage.Boundary, CalibrationArtwork.WavyBoundary, 16, 0.01, false)]
    [InlineData(CalibrationStage.Fine, CalibrationArtwork.ReferenceCross, 14, 0.02, null)]
    public void Each_stage_gets_its_artwork_schema_and_search_range(CalibrationStage stage, CalibrationArtwork artwork,
        int schemaVersion, double radiusRangeMm, bool? autoRange)
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(FullDvd, stage);

        Assert.Equal(artwork, session.Target.Artwork);
        Assert.Equal(schemaVersion, session.Target.SchemaVersion);
        Assert.Equal(stage, CalibrationTargetFile.StageOf(session.Target));
        Assert.Equal(radiusRangeMm, session.FitOptions!.RadiusRangeMm);
        Assert.Equal(autoRange, session.FitOptions.AutoRange);
        Assert.Equal(FullDvd, session.FitOptions.SearchCenter);
        Assert.Null(session.Photo);
    }

    [Fact]
    public void Dvd_target_file_holds_the_stream_bytes_and_a_record_that_reloads()
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(SmallDvd, CalibrationStage.Fine);
        string iso = Path.Combine(_tempDir, "sub", "fine.iso");

        CalibrationTargetFileResult result = CalibrationTargetFile.Write(session, iso);

        Assert.Equal(iso, result.ImagePath);
        Assert.Equal(1600L * 2048, result.ImageBytes);
        Assert.Equal(Path.Combine(_tempDir, "sub", "fine.calibration.json"), result.RecordPath);
        Assert.Null(result.CueSheetPath);
        using MemoryStream stream = new();
        CdBurner.Lithography.Dvd.DvdStreamingGenerator.GeneratePattern(session.Target, stream,
            CalibrationTargetFile.DvdOptions(session.Target));
        Assert.Equal(SHA256.HashData(stream.ToArray()), SHA256.HashData(File.ReadAllBytes(iso)));
        // The fine target draws the reference cross, so the image is not blank and not all ink.
        byte[] bytes = File.ReadAllBytes(iso);
        Assert.Contains(bytes, b => b != bytes[0]);

        CalibrationSession reloaded = CalibrationSessionJson.Load(result.RecordPath);
        Assert.Equal(session.Target.Id, reloaded.Target.Id);
        Assert.Equal(session.Target.Parameters, reloaded.Target.Parameters);
        Assert.Equal(CalibrationArtwork.ReferenceCross, reloaded.Target.Artwork);
    }

    [Fact]
    public void Cd_target_is_a_wave_file_with_cue_sheet_and_record()
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(SmallCd, CalibrationStage.Boundary);
        string wav = Path.Combine(_tempDir, "boundary.wav");

        CalibrationTargetFileResult result = CalibrationTargetFile.Write(session, wav);

        // RIFF/WAVE header (44 bytes) followed by 1,200 CD-DA sectors of 2,352 bytes.
        Assert.Equal(44 + 1200L * CdDiscParameters.BytesPerSector, result.ImageBytes);
        byte[] header = File.ReadAllBytes(wav).AsSpan(0, 12).ToArray();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal(Path.Combine(_tempDir, "boundary.cue"), result.CueSheetPath);
        Assert.True(File.Exists(result.CueSheetPath));
        Assert.True(File.Exists(result.RecordPath));
        // The audio after the header is the stream the golden test checks, in the same little-endian order.
        using MemoryStream stream = new();
        CdTrackGenerator.GeneratePatternToStream(session.Target, stream, interleave: true,
            audioByteOrder: CdAudioByteOrder.LittleEndian);
        Assert.Equal(stream.ToArray(), File.ReadAllBytes(wav).AsSpan(44).ToArray());
    }

    [Fact]
    public void Blu_ray_fine_target_writes_whole_clusters_and_its_record()
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(
            SmallBluRay with { DrawingEndSectorExclusive = 256, UsableSectors = 256 }, CalibrationStage.Fine);
        string iso = Path.Combine(_tempDir, "fine-bd.iso");

        CalibrationTargetFileResult result = CalibrationTargetFile.Write(session, iso);

        // 8 clusters of 32 sectors of 2,048 bytes.
        Assert.Equal(256L * 2048, result.ImageBytes);
        Assert.Equal(14, CalibrationSessionJson.Load(result.RecordPath).Target.SchemaVersion);
    }

    [Fact]
    public void Coarse_target_is_refused_on_a_disc_too_small_for_its_bands()
    {
        Assert.Throws<ArgumentException>(() => CalibrationTargetFile.CreateSession(SmallDvd, CalibrationStage.Coarse));
    }

    [Fact]
    public void Existing_files_are_kept_unless_overwrite_is_asked()
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(SmallDvd, CalibrationStage.Boundary);
        string iso = Path.Combine(_tempDir, "keep.iso");
        File.WriteAllText(iso, "earlier target");

        Assert.Throws<IOException>(() => CalibrationTargetFile.Write(session, iso));
        Assert.Equal("earlier target", File.ReadAllText(iso));
        Assert.False(File.Exists(CalibrationTargetFile.RecordPathFor(iso)));

        CalibrationTargetFileResult result = CalibrationTargetFile.Write(session, iso, overwrite: true);
        Assert.Equal(1600L * 2048, result.ImageBytes);
        Assert.True(File.Exists(result.RecordPath));
    }

    [Theory]
    [InlineData(CalibrationDiscKind.Dvd, "target.wav")]
    [InlineData(CalibrationDiscKind.BluRay, "target.raw")]
    [InlineData(CalibrationDiscKind.Cd, "target.iso")]
    public void Wrong_file_type_for_the_medium_is_refused_before_anything_is_written(CalibrationDiscKind kind, string name)
    {
        CalibrationParameters parameters = kind switch
        {
            CalibrationDiscKind.Cd => SmallCd,
            CalibrationDiscKind.Dvd => SmallDvd,
            _ => SmallBluRay,
        };
        CalibrationSession session = CalibrationTargetFile.CreateSession(parameters, CalibrationStage.Boundary);

        Assert.Throws<ArgumentException>(() => CalibrationTargetFile.Write(session, Path.Combine(_tempDir, name)));

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Cancelled_write_leaves_no_target_and_no_record()
    {
        CalibrationSession session = CalibrationTargetFile.CreateSession(SmallDvd, CalibrationStage.Boundary);
        string iso = Path.Combine(_tempDir, "cancelled.iso");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CalibrationTargetFile.Write(session, iso, cancellationToken: cancelled.Token));

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Dvd_sector_count_that_is_not_whole_ecc_blocks_is_refused()
    {
        // A DVD ECC block is 16 sectors (ECMA-267, clause 18); the encoder writes whole blocks only.
        CalibrationParameters parameters = SmallDvd with { Sectors = 1601 };

        Assert.ThrowsAny<ArgumentException>(() => CalibrationTargetFile.CreateSession(parameters, CalibrationStage.Boundary));
    }
}
