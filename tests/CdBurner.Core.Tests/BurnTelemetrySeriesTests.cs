using System.IO;
using System.Xml.Linq;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Verification;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class BurnTelemetrySeriesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_TelemetryTests_" + Guid.NewGuid().ToString("N"));

    public BurnTelemetrySeriesTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Snapshot_ComputesMegabytesPerSecond_FromBytesPerSecond()
    {
        var snap = new BurnProgressSnapshot
        {
            CurrentLba = 1000,
            TotalLba = 2000,
            DeviceBufferFillPercent = 82.5,
            HostBufferFillPercent = 95.0,
            BufferLevelPercent = 82.5,
            CurrentSpeedMultiplier = 8.0,
            CurrentBytesPerSecond = 8 * 1024 * 1024
        };

        Assert.Equal(8.0, snap.CurrentSpeedMegabytesPerSecond, 3);
        Assert.Equal(82.5, snap.DeviceBufferFillPercent);
        Assert.Equal(95.0, snap.HostBufferFillPercent);
        Assert.Equal(50.0, snap.ProgressPercentage, 2);
    }

    [Fact]
    public void BoundedSeries_RemainsO1Memory_ForLargeBluRaySampleCounts_AndPreservesBufferDips()
    {
        var series = new BurnTelemetrySeries(maxPoints: 120);
        series.Reset("BD-R 128GB Burn", "VDRV-01", "BD-R");

        const int totalSamples = 50_000;
        const int totalLba = 62_500_000;
        for (int i = 0; i < totalSamples; i++)
        {
            bool isDip = i == 25_000;
            series.Record(new BurnProgressSnapshot
            {
                ElapsedTime = TimeSpan.FromSeconds(i * 0.1),
                CurrentLba = (int)((long)(i + 1) * totalLba / totalSamples),
                TotalLba = totalLba,
                DeviceBufferFillPercent = isDip ? 11.5 : 92.0,
                HostBufferFillPercent = isDip ? 7.0 : 98.0,
                BufferLevelPercent = isDip ? 11.5 : 92.0,
                CurrentSpeedMultiplier = 6.0,
                CurrentBytesPerSecond = (long)(6.0 * 4495.5 * 1024.0),
                CurrentActionDescription = "Writing data"
            });
        }

        var samples = series.GetSamples();
        var summary = series.GetSummary();

        Assert.Equal(totalSamples, series.TotalSamplesObserved);
        Assert.True(series.Count <= 120);
        Assert.True(samples.Count <= 120);
        Assert.True(series.DownsampleFactor > 1);
        Assert.Equal(11.5, summary.MinDeviceBufferPercent);
        Assert.Equal(7.0, summary.MinHostBufferPercent);
        Assert.Contains(samples, s => s.DeviceBufferFillPercent <= 11.5);
        Assert.Contains(samples, s => s.HostBufferFillPercent <= 7.0);
        Assert.Equal(totalLba, samples[^1].Lba);
    }

    [Fact]
    public void JsonExport_RoundTripsSamplesAndSummary()
    {
        var series = new BurnTelemetrySeries();
        series.Reset("Data CD", "HL-DT-ST GP60NB50", "CD-R");
        series.Record(new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(1),
            CurrentLba = 150,
            TotalLba = 300,
            DeviceBufferFillPercent = 87.5,
            HostBufferFillPercent = 75.0,
            CurrentSpeedMultiplier = 10.0,
            CurrentBytesPerSecond = 1_764_000,
            CurrentActionDescription = "Writing data"
        });
        series.Record(new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(2),
            CurrentLba = 300,
            TotalLba = 300,
            DeviceBufferFillPercent = 0.0,
            HostBufferFillPercent = 0.0,
            CurrentSpeedMultiplier = 10.0,
            CurrentBytesPerSecond = 1_764_000,
            CurrentActionDescription = "Done"
        });

        string path = Path.Combine(_tempDir, "telemetry.json");
        series.SaveJson(path);

        var loaded = BurnTelemetrySeries.LoadJson(path);
        Assert.Equal(BurnTelemetrySeries.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Data CD", loaded.Title);
        Assert.Equal("HL-DT-ST GP60NB50", loaded.Drive);
        Assert.Equal("CD-R", loaded.MediaType);
        Assert.Equal(2, loaded.Summary.TotalSamplesObserved);
        Assert.Equal(10.0, loaded.Summary.PeakSpeedX);
        Assert.Equal(0.0, loaded.Summary.MinDeviceBufferPercent);
        Assert.Equal(2, loaded.Samples.Count);
        Assert.Equal(87.5, loaded.Samples[0].DeviceBufferFillPercent);
        Assert.Equal(75.0, loaded.Samples[0].HostBufferFillPercent);
    }

    [Fact]
    public void SvgExport_ProducesStandaloneThemeNeutralValidXml_AcrossEdgeCases()
    {
        // Multi-sample with both buffers
        var series = new BurnTelemetrySeries();
        series.Reset("Audio <SAO> & CD", "Drive \"A\"", "CD-R");
        series.Record(new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(0.5),
            CurrentLba = 100,
            TotalLba = 400,
            DeviceBufferFillPercent = 90.0,
            HostBufferFillPercent = 80.0,
            CurrentSpeedMultiplier = 8.0,
            CurrentBytesPerSecond = 8 * 176_400
        });
        series.Record(new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(1.5),
            CurrentLba = 400,
            TotalLba = 400,
            DeviceBufferFillPercent = 50.0,
            HostBufferFillPercent = 25.0,
            CurrentSpeedMultiplier = 12.0,
            CurrentBytesPerSecond = 12 * 176_400
        });

        string svgPath = Path.Combine(_tempDir, "plot.svg");
        series.SaveSvg(svgPath);
        string svg = File.ReadAllText(svgPath);
        var doc = XDocument.Parse(svg);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", svg, StringComparison.Ordinal);
        Assert.Contains("Write Speed", svg);
        Assert.Contains("Drive Buffer (%)", svg);
        Assert.Contains("Host I/O Buffer (%)", svg);

        // Unsupported device buffer (all null)
        var noDeviceBuf = new BurnTelemetrySeries();
        noDeviceBuf.Record(new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(1),
            CurrentLba = 50,
            TotalLba = 100,
            DeviceBufferFillPercent = null,
            HostBufferFillPercent = 95.0,
            CurrentSpeedMultiplier = 4.0,
            CurrentBytesPerSecond = 4 * 176_400
        });
        string singleSvg = noDeviceBuf.ToSvg();
        XDocument.Parse(singleSvg);
        Assert.Contains("Drive Buffer (N/A)", singleSvg);
        Assert.Contains("<circle", singleSvg);

        // Empty series
        string emptySvg = new BurnTelemetrySeries().ToSvg();
        XDocument.Parse(emptySvg);
        Assert.Contains("No telemetry samples recorded", emptySvg);
    }
}
