using CdBurner.Lithography.Calibration;

namespace CdBurner.Lithography.Tests.Calibration;

/// <summary>The store of fitted parameters per drive model and media ID.</summary>
public sealed class CalibrationStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-calstore-" + Guid.NewGuid().ToString("N"));

    public CalibrationStoreTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string StorePath => Path.Combine(_tempDir, "nested", CalibrationStore.FileName);

    private static CalibrationParameters Dvd(double inner, double outer)
        => new(CalibrationDiscKind.Dvd, inner, outer, 2_297_888, 133.3, -3.9e-5, 6.9e-5, -4.6e-5);

    private static CalibrationStoreEntry Entry(string drive, string media, CalibrationParameters parameters)
        => new(drive, media, parameters, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), "target-1", "fine", 0.05);

    [Fact]
    public void Missing_file_is_an_empty_store()
    {
        CalibrationStoreDocument document = CalibrationStore.Load(StorePath);

        Assert.Equal(CalibrationStore.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Empty(document.Entries);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Saved_entry_is_found_again_with_every_digit()
    {
        CalibrationParameters parameters = Dvd(23.99144094121467, 57.964899248503315);

        CalibrationStore.Save(StorePath, Entry("HL-DT-ST DVDRAM GP60NB50", "MCC 03RG20", parameters));
        CalibrationStoreEntry? found = CalibrationStore.Find(CalibrationStore.Load(StorePath),
            "HL-DT-ST DVDRAM GP60NB50", "MCC 03RG20");

        Assert.NotNull(found);
        Assert.Equal(parameters, found.Parameters);
        Assert.Equal("fine", found.Stage);
        Assert.Equal("target-1", found.TargetId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
    }

    [Fact]
    public void Key_ignores_case_and_surrounding_spaces()
    {
        CalibrationStore.Save(StorePath, Entry("  HL-DT-ST DVDRAM GP60NB50 ", "MCC 03RG20", Dvd(24, 58)));
        CalibrationStoreDocument document = CalibrationStore.Load(StorePath);

        Assert.NotNull(CalibrationStore.Find(document, "hl-dt-st dvdram gp60nb50", " mcc 03rg20 "));
        Assert.Null(CalibrationStore.Find(document, "HL-DT-ST DVDRAM GP60NB50", "MCC 02RG20"));
        Assert.Null(CalibrationStore.Find(document, "Other drive", "MCC 03RG20"));
        Assert.Equal("HL-DT-ST DVDRAM GP60NB50", document.Entries[0].DriveModel);
    }

    [Fact]
    public void Saving_the_same_drive_and_media_replaces_only_that_entry()
    {
        CalibrationStore.Save(StorePath, Entry("Drive A", "MCC 03RG20", Dvd(24, 58)));
        CalibrationStore.Save(StorePath, Entry("Drive B", "MCC 03RG20", Dvd(24.1, 57.9)));
        CalibrationStore.Save(StorePath, Entry("drive a", "mcc 03rg20", Dvd(23.99, 57.96)));

        CalibrationStoreDocument document = CalibrationStore.Load(StorePath);

        Assert.Equal(2, document.Entries.Count);
        Assert.Equal(23.99, CalibrationStore.Find(document, "Drive A", "MCC 03RG20")!.Parameters.InnerRadiusMm);
        Assert.Equal(24.1, CalibrationStore.Find(document, "Drive B", "MCC 03RG20")!.Parameters.InnerRadiusMm);
    }

    [Fact]
    public void Same_drive_and_media_id_keep_one_entry_per_disc_type()
    {
        CalibrationParameters cd = new(CalibrationDiscKind.Cd, 24.5, 56.8, 359_849, 1200);
        CalibrationStore.Save(StorePath, Entry("Drive A", "97m24s01f", cd));
        CalibrationStore.Save(StorePath, Entry("Drive A", "97m24s01f", Dvd(24, 58)));

        CalibrationStoreDocument document = CalibrationStore.Load(StorePath);

        Assert.Equal(2, document.Entries.Count);
        Assert.Equal(cd, CalibrationStore.Find(document, "Drive A", "97m24s01f", CalibrationDiscKind.Cd)!.Parameters);
        Assert.Equal(CalibrationDiscKind.Dvd,
            CalibrationStore.Find(document, "Drive A", "97m24s01f", CalibrationDiscKind.Dvd)!.Parameters.Kind);
        Assert.Null(CalibrationStore.Find(document, "Drive A", "97m24s01f", CalibrationDiscKind.BluRay));
    }

    [Theory]
    [InlineData("", "MCC 03RG20")]
    [InlineData("Drive A", "   ")]
    [InlineData("Drive\u0007A", "MCC 03RG20")]
    public void Unusable_keys_are_refused_and_nothing_is_written(string drive, string media)
    {
        Assert.Throws<ArgumentException>(() => CalibrationStore.Save(StorePath, Entry(drive, media, Dvd(24, 58))));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Invalid_parameters_are_refused_and_the_store_is_unchanged()
    {
        CalibrationStore.Save(StorePath, Entry("Drive A", "MCC 03RG20", Dvd(24, 58)));
        string before = File.ReadAllText(StorePath);

        Assert.Throws<InvalidDataException>(() =>
            CalibrationStore.Save(StorePath, Entry("Drive A", "MCC 03RG20", Dvd(58, 24))));

        Assert.Equal(before, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[],\"extra\":true}")]
    public void Damaged_store_is_an_error_and_is_not_overwritten(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);

        Assert.Throws<InvalidDataException>(() => CalibrationStore.Load(StorePath));
        Assert.Throws<InvalidDataException>(() => CalibrationStore.Save(StorePath, Entry("Drive A", "MCC 03RG20", Dvd(24, 58))));
        Assert.Equal(content, File.ReadAllText(StorePath));
    }
}
