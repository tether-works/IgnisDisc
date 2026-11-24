using System.Text.Json;
using System.Xml.Linq;
using CdBurner.Core.Verification;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class QualityScanModelAndReportTests : IDisposable
{
    private readonly string _tempDir;

    public QualityScanModelAndReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-qscan-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("ATAPI", "iHAS124   F", true)]
    [InlineData("ATAPI   ", "iHBS212   2", true)]
    [InlineData("LITE-ON", "DVDRW LH-20A1P", true)]
    [InlineData("LITE-ON", "DVDRW SOHW-1693S", true)]
    [InlineData("LITE-ON", "LTR-52327S", true)]
    [InlineData("ASUS", "DRW-24B1ST", true)]
    [InlineData("ASUS", "DRW-2014L1T", true)]
    [InlineData("HL-DT-ST", "DVDRAM GP60NB50", false)]
    [InlineData("HL-DT-ST", "BD-RE BP50NB40", false)]
    [InlineData("HL-DT-ST", "BD-RE WH16NS58", false)]
    [InlineData("PIONEER", "DVD-RW  DVR-111", false)]
    [InlineData("PLEXTOR", "DVDR   PX-716A", false)]
    [InlineData("TSSTcorp", "BDDVDW SE-506CB", false)]
    [InlineData("IGNIS", "VIRTUAL DVD/CD-RW", false)]
    [InlineData("", "", false)]
    public void AllowList_MatchesOnlyExactAllowedVendorAndProductPrefixes(string vendor, string product, bool expectedSupported)
    {
        var match = QualityScanDriveAllowList.Evaluate(vendor, product);
        Assert.Equal(expectedSupported, match != null);
        Assert.Equal(expectedSupported, QualityScanDriveAllowList.IsSupported(vendor, product));
    }

    [Fact]
    public void Summary_EvaluatesCdRedBook_DvdEcma267_And_BdThresholds()
    {
        var cdClean = QualityScanSummary.FromSamples(QualityScanMediaKind.Cd, 150,
        [
            new(0, 75, 75, 24.0, 3600, C1: 8, C2: 0, Cu: 0),
            new(75, 150, 75, 24.0, 3600, C1: 12, C2: 0, Cu: 0)
        ]);
        Assert.Equal(QualityScanGrade.Excellent, cdClean.Grade);
        Assert.Equal(20, cdClean.TotalC1);
        Assert.Equal(12, cdClean.MaxC1PerSecond);

        var cdMarginal = QualityScanSummary.FromSamples(QualityScanMediaKind.Cd, 150,
        [
            new(0, 75, 75, 24.0, 3600, C1: 235, C2: 1, Cu: 0),
            new(75, 150, 75, 24.0, 3600, C1: 10, C2: 0, Cu: 0)
        ]);
        Assert.Equal(QualityScanGrade.Marginal, cdMarginal.Grade);

        var cdPoor = QualityScanSummary.FromSamples(QualityScanMediaKind.Cd, 75,
        [
            new(0, 75, 75, 24.0, 3600, C1: 40, C2: 2, Cu: 1)
        ]);
        Assert.Equal(QualityScanGrade.Poor, cdPoor.Grade);

        // DVD: 8 ECC blocks = 128 sectors; sum of PIE over 128 sectors > 280 => Poor.
        var dvdPoor = QualityScanSummary.FromSamples(QualityScanMediaKind.Dvd, 256,
        [
            new(0, 64, 64, 8.0, 11080, Pie: 150, Pif: 1, Pof: 0),
            new(64, 128, 64, 8.0, 11080, Pie: 145, Pif: 2, Pof: 0),
            new(128, 256, 128, 8.0, 11080, Pie: 20, Pif: 0, Pof: 0)
        ]);
        Assert.Equal(295, dvdPoor.MaxPiePer8Ecc);
        Assert.Equal(2, dvdPoor.MaxPifPerEcc);
        Assert.Equal(QualityScanGrade.Poor, dvdPoor.Grade);

        var bdGood = QualityScanSummary.FromSamples(QualityScanMediaKind.BluRay, 64,
        [
            new(0, 32, 32, 4.0, 18000, Ldc: 60, Bis: 2),
            new(32, 64, 32, 4.0, 18000, Ldc: 45, Bis: 1)
        ]);
        Assert.Equal(QualityScanGrade.Good, bdGood.Grade);
        Assert.Equal(60, bdGood.MaxLdcPerCluster);
        Assert.Equal(2, bdGood.MaxBisPerCluster);
    }

    [Fact]
    public async Task Report_RoundTripsJson_ExportsValidDatasheetSvg_And_AttachesLongitudinallyToHealthJson()
    {
        var samples = new List<QualityScanIntervalSample>
        {
            new(0, 75, 75, 16.0, 2400, C1: 12, C2: 0, Cu: 0),
            new(75, 150, 75, 18.0, 2700, C1: 250, C2: 3, Cu: 0)
        };
        var summary = QualityScanSummary.FromSamples(QualityScanMediaKind.Cd, 150, samples);
        var report = new QualityScanReport(
            QualityScanReport.CurrentSchemaVersion,
            new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero),
            IsSupported: true,
            Status: QualityScanStatus.Completed,
            IsExperimentalHardwareScan: true,
            StatusMessage: "Experimental scan",
            DriveVendor: "ATAPI",
            DriveProduct: "iHAS124   F",
            DriveRevision: "CL9E",
            DriverName: "MediaTekLiteOnQualityDriver",
            Protocol: QualityScanProtocol.MediaTekF3,
            MediaKind: QualityScanMediaKind.Cd,
            MediaType: "0x0009",
            MediaId: "97m26s07f",
            TotalDiscSectors: 150,
            Summary: summary,
            Samples: samples,
            Elapsed: TimeSpan.FromSeconds(1));

        string jsonPath = Path.Combine(_tempDir, "scan.json");
        string svgPath = Path.Combine(_tempDir, "scan.svg");
        await report.SaveJsonAsync(jsonPath);
        await report.SaveSvgAsync(svgPath);

        var loaded = QualityScanReport.Load(jsonPath);
        Assert.Equal(report.DriveProduct, loaded.DriveProduct);
        Assert.Equal(report.Summary.TotalC1, loaded.Summary.TotalC1);
        Assert.Equal(2, loaded.Samples.Count);
        Assert.Contains("EXPERIMENTAL/UNVERIFIED", loaded.Describe());

        string svg = await File.ReadAllTextAsync(svgPath);
        var xml = XDocument.Parse(svg);
        Assert.Equal("svg", xml.Root?.Name.LocalName);
        Assert.Contains("LIMIT C1 220/s", svg);
        Assert.Contains("TW-IGN-FIG-001", svg);
        Assert.Contains("#0D0D0C", svg);
        Assert.Contains("#F04E23", svg);

        // Longitudinal attachment to HEALTH.json preserves schema 1 compatibility when absent and appends when attached.
        var cert = new DiscHealthCertificate(
            DiscHealthCertificate.CurrentSchemaVersion,
            new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero),
            "set1",
            1,
            "VOL1",
            "ATAPI iHAS124 F",
            "CdR",
            "97m26s07f",
            150,
            16.0,
            true,
            "ok",
            [new TransferCurvePoint(0, 16), new TransferCurvePoint(100, 16)],
            null);
        string healthJsonPath = Path.Combine(_tempDir, "disc01.HEALTH.json");
        cert.Save(healthJsonPath, Path.Combine(_tempDir, "disc01.HEALTH.svg"));

        using (var baseDoc = JsonDocument.Parse(File.ReadAllText(healthJsonPath)))
        {
            Assert.False(baseDoc.RootElement.TryGetProperty("qualityScans", out _));
        }

        await DiscHealthCertificate.AttachQualityScanAsync(healthJsonPath, report);
        var secondReport = report with
        {
            CreatedUtc = report.CreatedUtc.AddDays(30),
            Summary = summary with { TotalC1 = 310, MaxC1PerSecond = 280, Grade = QualityScanGrade.Marginal }
        };
        var updated = await DiscHealthCertificate.AttachQualityScanAsync(healthJsonPath, secondReport);

        Assert.NotNull(updated.QualityScans);
        Assert.Equal(2, updated.QualityScans!.Count);
        Assert.Equal(262, updated.QualityScans[0].TotalPrimaryErrors);
        Assert.Equal(310, updated.QualityScans[1].TotalPrimaryErrors);

        var reloadedCert = DiscHealthCertificate.Load(healthJsonPath);
        Assert.Equal(2, reloadedCert.QualityScans!.Count);
    }
}
