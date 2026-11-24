using System.Diagnostics;
using CdBurner.Core.Primitives;
using CdBurner.Core.Verification;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.Scsi.QualityScan;

/// <summary>
/// Deterministic virtual quality-scan driver operating against <see cref="VirtualDiscRecorder"/> and
/// <see cref="VirtualDiscMedium"/> for hardware-free CD (C1/C2/CU), DVD (PIE/PIF/POF), and BD (LDC/BIS) testing.
/// </summary>
public sealed class VirtualQualityScanDriver : IQualityScanDriver
{
    private readonly VirtualDiscRecorder _recorder;

    public string DriverName => nameof(VirtualQualityScanDriver);
    public bool IsExperimentalHardware => false;

    public VirtualQualityScanDriver(VirtualDiscRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public Task<QualityScanReport> ScanAsync(
        QualityScanOptions options,
        IProgress<QualityScanProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();

        var medium = _recorder.CurrentMedium;
        if (medium == null)
        {
            return Task.FromResult(new QualityScanReport(
                QualityScanReport.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                IsSupported: true,
                Status: QualityScanStatus.UnsupportedMedia,
                IsExperimentalHardwareScan: false,
                StatusMessage: "No virtual medium is inserted.",
                DriveVendor: _recorder.VendorId,
                DriveProduct: _recorder.ProductId,
                DriveRevision: _recorder.ProductRevision,
                DriverName: DriverName,
                Protocol: QualityScanProtocol.VirtualSimulator,
                MediaKind: QualityScanMediaKind.Cd,
                MediaType: null,
                MediaId: null,
                TotalDiscSectors: 0,
                Summary: QualityScanSummary.Empty(QualityScanGrade.NotEvaluated, "No virtual medium is inserted."),
                Samples: Array.Empty<QualityScanIntervalSample>(),
                Elapsed: clock.Elapsed));
        }

        QualityScanMediaKind mediaKind = MapMediaKind(medium.MediaType);
        if (medium.IsBlank || medium.UsedSectors <= 0)
        {
            return Task.FromResult(new QualityScanReport(
                QualityScanReport.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                IsSupported: true,
                Status: QualityScanStatus.UnsupportedMedia,
                IsExperimentalHardwareScan: false,
                StatusMessage: "Virtual medium is blank; write or mount recorded media before scanning.",
                DriveVendor: _recorder.VendorId,
                DriveProduct: _recorder.ProductId,
                DriveRevision: _recorder.ProductRevision,
                DriverName: DriverName,
                Protocol: QualityScanProtocol.VirtualSimulator,
                MediaKind: mediaKind,
                MediaType: medium.MediaType.ToString(),
                MediaId: null,
                TotalDiscSectors: medium.TotalCapacitySectors,
                Summary: QualityScanSummary.Empty(QualityScanGrade.NotEvaluated, "Virtual medium is blank."),
                Samples: Array.Empty<QualityScanIntervalSample>(),
                Elapsed: clock.Elapsed));
        }

        int recordedSectors = Math.Clamp(medium.UsedSectors, 1, medium.TotalCapacitySectors);
        int startLba = Math.Clamp(options.StartLba, 0, recordedSectors - 1);
        int endLba = options.EndLba.HasValue
            ? Math.Clamp(options.EndLba.Value, startLba + 1, recordedSectors)
            : recordedSectors;

        int step = mediaKind switch
        {
            QualityScanMediaKind.Dvd => Math.Max(16, (endLba - startLba) > 16_384 ? 128 : 16),
            QualityScanMediaKind.BluRay => Math.Max(32, (endLba - startLba) > 32_768 ? 256 : 32),
            _ => 75
        };

        double baseSpeedX = options.ReadSpeedMultiplier is int s && s > 0
            ? s
            : mediaKind switch
            {
                QualityScanMediaKind.Dvd => 8.0,
                QualityScanMediaKind.BluRay => 4.0,
                _ => 24.0
            };
        double oneXKb = mediaKind switch
        {
            QualityScanMediaKind.Dvd => 1385.0,
            QualityScanMediaKind.BluRay => 4500.0,
            _ => 150.0
        };

        var samples = new List<QualityScanIntervalSample>();
        int totalToScan = Math.Max(1, endLba - startLba);
        for (int lba = startLba; lba < endLba;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int nextLba = Math.Min(endLba, lba + step);
            int count = nextLba - lba;

            int badCount = 0;
            foreach (int bad in medium.SimulatedBadLbas)
            {
                if (bad >= lba && bad < nextLba)
                {
                    badCount++;
                }
            }

            double radialFrac = (double)lba / Math.Max(1, endLba);
            double speedX = Math.Round(baseSpeedX * (0.45 + (0.55 * radialFrac)), 2);
            double speedKb = Math.Round(speedX * oneXKb, 1);
            int baselineWave = ((lba / step) % 5) + (radialFrac > 0.8 ? 4 : 1);

            QualityScanIntervalSample sample = mediaKind switch
            {
                QualityScanMediaKind.Dvd => new QualityScanIntervalSample(
                    lba, nextLba, count, speedX, speedKb,
                    Pie: (baselineWave * 3) + (badCount * 190),
                    Pif: badCount > 0 ? Math.Min(12, badCount * 3) : ((lba / step) % 11 == 0 ? 1 : 0),
                    Poe: badCount * 2,
                    Pof: badCount > 1 ? 1 : 0),
                QualityScanMediaKind.BluRay => new QualityScanIntervalSample(
                    lba, nextLba, count, speedX, speedKb,
                    Ldc: (baselineWave * 5) + (badCount * 220),
                    Bis: badCount > 0 ? badCount * 18 : ((lba / step) % 9 == 0 ? 2 : 0)),
                _ => new QualityScanIntervalSample(
                    lba, nextLba, count, speedX, speedKb,
                    C1: baselineWave + (badCount * 140),
                    C2: badCount > 0 ? badCount * 6 : 0,
                    Cu: badCount > 1 ? 1 : 0)
            };

            samples.Add(sample);
            lba = nextLba;

            double pct = Math.Clamp(100.0 * (lba - startLba) / totalToScan, 0.0, 100.0);
            progress?.Report(new QualityScanProgressSnapshot(pct, lba, endLba, sample, $"Simulated {mediaKind} quality scan: {pct:F0}%"));
        }

        var summary = QualityScanSummary.FromSamples(mediaKind, endLba - startLba, samples);
        return Task.FromResult(new QualityScanReport(
            QualityScanReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            IsSupported: true,
            Status: QualityScanStatus.Completed,
            IsExperimentalHardwareScan: false,
            StatusMessage: "Simulated quality scan completed on virtual drive.",
            DriveVendor: _recorder.VendorId,
            DriveProduct: _recorder.ProductId,
            DriveRevision: _recorder.ProductRevision,
            DriverName: DriverName,
            Protocol: QualityScanProtocol.VirtualSimulator,
            MediaKind: mediaKind,
            MediaType: medium.MediaType.ToString(),
            MediaId: null,
            TotalDiscSectors: medium.TotalCapacitySectors,
            Summary: summary,
            Samples: samples,
            Elapsed: clock.Elapsed));
    }

    private static QualityScanMediaKind MapMediaKind(DiscMediaType mediaType) => mediaType switch
    {
        DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusR
            or DiscMediaType.DvdPlusRw or DiscMediaType.DvdMinusRDualLayer
            or DiscMediaType.DvdPlusRDualLayer or DiscMediaType.DvdRam
            or DiscMediaType.DvdRom => QualityScanMediaKind.Dvd,
        DiscMediaType.BdR or DiscMediaType.BdRe or DiscMediaType.BdRDualLayer
            or DiscMediaType.BdReDualLayer or DiscMediaType.BdxlRTripleLayer
            or DiscMediaType.BdxlRQuadLayer or DiscMediaType.BdxlReTripleLayer
            or DiscMediaType.BdRom => QualityScanMediaKind.BluRay,
        _ => QualityScanMediaKind.Cd
    };
}
