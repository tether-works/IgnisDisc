namespace CdBurner.Core.Verification;

/// <summary>
/// Coordinates optical quality scans through an <see cref="IQualityScanDriver"/> and attaches completed
/// longitudinal scan summaries to archive <see cref="DiscHealthCertificate"/> files (<c>HEALTH.json</c>).
/// </summary>
public sealed class QualityScanService
{
    public async Task<QualityScanReport> RunScanAsync(
        IQualityScanDriver driver,
        QualityScanOptions? options = null,
        IProgress<QualityScanProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return await driver.ScanAsync(options ?? new QualityScanOptions(), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DiscHealthCertificate> AttachToHealthCertificateAsync(
        string healthJsonPath,
        QualityScanReport report,
        CancellationToken cancellationToken = default) =>
        DiscHealthCertificate.AttachQualityScanAsync(healthJsonPath, report, cancellationToken);
}
