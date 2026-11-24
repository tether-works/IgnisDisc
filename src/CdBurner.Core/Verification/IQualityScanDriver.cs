namespace CdBurner.Core.Verification;

/// <summary>
/// Vendor-specific or simulator backend capable of running an optical quality scan (C1/C2/CU, PIE/PIF/POF, LDC/BIS).
/// Implementations must gate hardware commands behind <see cref="QualityScanDriveAllowList"/> and ensure
/// any vendor scan-stop teardown executes unconditionally in a <c>finally</c> block.
/// </summary>
public interface IQualityScanDriver
{
    string DriverName { get; }

    /// <summary>
    /// True when executing vendor commands on real optical hardware (experimental until live scans are checked against QPxTool).
    /// </summary>
    bool IsExperimentalHardware { get; }

    Task<QualityScanReport> ScanAsync(
        QualityScanOptions options,
        IProgress<QualityScanProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
