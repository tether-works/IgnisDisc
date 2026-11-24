using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdBurner.Core.Verification;

/// <summary>Optical medium family evaluated during a quality scan.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityScanMediaKind
{
    Cd,
    Dvd,
    BluRay
}

/// <summary>Vendor or simulator protocol used to collect quality metrics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityScanProtocol
{
    None,
    MediaTekF3,
    MediaTekDf,
    VirtualSimulator
}

/// <summary>Execution outcome of a quality scan request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityScanStatus
{
    Completed,
    UnsupportedDrive,
    UnsupportedMedia,
    Cancelled,
    Failed
}

/// <summary>Overall disc signal/ECC quality grade derived from standard Red Book / ECMA-267 / BD limits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityScanGrade
{
    NotEvaluated,
    Excellent,
    Good,
    Marginal,
    Poor
}

/// <summary>Supported vendor quality-scan driver families.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityScanDriverFamily
{
    MediaTekLiteOn
}

/// <summary>One allow-list rule matching an optical drive's INQUIRY vendor and product prefix.</summary>
public sealed record QualityScanAllowListEntry(
    QualityScanDriverFamily Family,
    string VendorId,
    string ProductPrefix,
    string Notes);

/// <summary>Successful allow-list match binding an INQUIRY identity to a quality-scan driver family.</summary>
public sealed record QualityScanAllowListMatch(
    QualityScanDriverFamily Family,
    string VendorId,
    string ProductId,
    string MatchedPrefix);

/// <summary>
/// Strict INQUIRY allow-list gating vendor quality-scan CDBs.
/// Unlisted drives must never receive vendor-specific opcodes (0xF3, 0xDF, 0xE9, 0xEB, 0x3B, 0x3C).
/// </summary>
public static class QualityScanDriveAllowList
{
    public static IReadOnlyList<QualityScanAllowListEntry> Entries { get; } =
    [
        new(QualityScanDriverFamily.MediaTekLiteOn, "ATAPI", "iHAS", "Lite-On SATA DVD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "ATAPI", "iHBS", "Lite-On SATA BD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "iHAS", "Lite-On DVD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "iHBS", "Lite-On BD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "LH-", "Lite-On LH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW LH-", "Lite-On LH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DH-", "Lite-On DH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW DH-", "Lite-On DH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "SOHW-", "Lite-On SOHW-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW SOHW-", "Lite-On SOHW-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVD+RW SOHW-", "Lite-On SOHW-series DVD+RW writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "SHW-", "Lite-On SHW-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW SHW-", "Lite-On SHW-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "SHM-", "Lite-On SHM-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW SHM-", "Lite-On SHM-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "SH-", "Lite-On SH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW SH-", "Lite-On SH-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "DVDRW LDW-", "Lite-On LDW-series DVD writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITE-ON", "LTR-", "Lite-On LTR-series CD-RW writers"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITEON", "iHAS", "Lite-On DVD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "LITEON", "iHBS", "Lite-On BD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "PLDS", "iHAS", "PLDS/Lite-On DVD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "PLDS", "iHBS", "PLDS/Lite-On BD writers (MediaTek)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "ASUS", "DRW-24B", "ASUS DRW-24B* (MediaTek OEM)"),
        new(QualityScanDriverFamily.MediaTekLiteOn, "ASUS", "DRW-2014", "ASUS DRW-2014* (MediaTek OEM)")
    ];

    /// <summary>
    /// Evaluates trimmed INQUIRY <paramref name="vendorId"/> and <paramref name="productId"/> against the allow-list.
    /// Returns null when the drive is not explicitly allow-listed.
    /// </summary>
    public static QualityScanAllowListMatch? Evaluate(string? vendorId, string? productId)
    {
        string vendor = NormalizeInquiryField(vendorId);
        string product = NormalizeInquiryField(productId);
        if (vendor.Length == 0 || product.Length == 0)
        {
            return null;
        }

        foreach (QualityScanAllowListEntry entry in Entries)
        {
            if (string.Equals(vendor, entry.VendorId, StringComparison.OrdinalIgnoreCase) &&
                product.StartsWith(entry.ProductPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new QualityScanAllowListMatch(entry.Family, vendor, product, entry.ProductPrefix);
            }
        }

        return null;
    }

    public static bool IsSupported(string? vendorId, string? productId) => Evaluate(vendorId, productId) != null;

    private static string NormalizeInquiryField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim(' ', '\0', '\t', '\r', '\n');
}

/// <summary>Configuration options for an optical disc quality scan.</summary>
public sealed record QualityScanOptions
{
    /// <summary>Optional target read speed multiplier (e.g. 8 for 8x). Null keeps the drive default.</summary>
    public int? ReadSpeedMultiplier { get; init; }

    /// <summary>When true, bypasses 0xF3 and uses the legacy 0xDF MediaTek protocol directly on CD.</summary>
    public bool ForceLegacyProtocol { get; init; }

    /// <summary>Starting LBA for the scan (default 0).</summary>
    public int StartLba { get; init; }

    /// <summary>Exclusive or inclusive upper LBA limit override; null scans the full recorded capacity.</summary>
    public int? EndLba { get; init; }
}

/// <summary>Per-interval quality measurement collected during an optical quality scan.</summary>
public sealed record QualityScanIntervalSample(
    int StartLba,
    int EndLba,
    int SectorCount,
    double ReadSpeedX,
    double ReadSpeedKbPerSec,
    int? C1 = null,
    int? C2 = null,
    int? Cu = null,
    int? Pie = null,
    int? Pif = null,
    int? Poe = null,
    int? Pof = null,
    int? Ldc = null,
    int? Bis = null);

/// <summary>Real-time progress snapshot emitted while a quality scan is running.</summary>
public sealed record QualityScanProgressSnapshot(
    double ProgressPercent,
    int CurrentLba,
    int TotalSectors,
    QualityScanIntervalSample? LatestSample,
    string StatusText);

/// <summary>Aggregate metrics and standard-threshold grade for a completed quality scan.</summary>
public sealed record QualityScanSummary(
    int TotalSectorsScanned,
    int IntervalCount,
    double AverageSpeedX,
    double MaxSpeedX,
    long TotalC1,
    long TotalC2,
    long TotalCu,
    int MaxC1PerSecond,
    int MaxC2PerSecond,
    int MaxCuPerSecond,
    double AverageC1PerSecond,
    double AverageC2PerSecond,
    long TotalPie,
    long TotalPif,
    long TotalPof,
    int MaxPiePer8Ecc,
    int MaxPifPerEcc,
    double AveragePiePer8Ecc,
    double AveragePifPerEcc,
    long TotalLdc,
    long TotalBis,
    int MaxLdcPerCluster,
    int MaxBisPerCluster,
    double AverageLdcPerCluster,
    double AverageBisPerCluster,
    QualityScanGrade Grade,
    string GradeReason)
{
    public const int CdRedBookC1Limit = 220;
    public const int DvdEcmaPie8EccLimit = 280;
    public const int DvdEcmaPifLimit = 4;
    public const int BdLdcClusterLimit = 300;
    public const int BdBisClusterLimit = 30;

    public static QualityScanSummary Empty(QualityScanGrade grade = QualityScanGrade.NotEvaluated, string reason = "No quality intervals scanned.") =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, grade, reason);

    /// <summary>Computes aggregate totals, peaks, averages, and standard-threshold grade from interval samples.</summary>
    public static QualityScanSummary FromSamples(
        QualityScanMediaKind mediaKind,
        int totalSectorsScanned,
        IReadOnlyList<QualityScanIntervalSample> samples)
    {
        if (samples.Count == 0 || totalSectorsScanned <= 0)
        {
            return Empty();
        }

        double avgSpeedX = Math.Round(samples.Average(s => s.ReadSpeedX), 2);
        double maxSpeedX = Math.Round(samples.Max(s => s.ReadSpeedX), 2);

        switch (mediaKind)
        {
            case QualityScanMediaKind.Cd:
            {
                long totC1 = 0, totC2 = 0, totCu = 0;
                int maxC1 = 0, maxC2 = 0, maxCu = 0;
                foreach (var s in samples)
                {
                    int c1 = s.C1 ?? 0;
                    int c2 = s.C2 ?? 0;
                    int cu = s.Cu ?? 0;
                    totC1 += c1;
                    totC2 += c2;
                    totCu += cu;
                    if (c1 > maxC1) maxC1 = c1;
                    if (c2 > maxC2) maxC2 = c2;
                    if (cu > maxCu) maxCu = cu;
                }

                double seconds = Math.Max(1.0, totalSectorsScanned / 75.0);
                double avgC1 = Math.Round(totC1 / seconds, 2);
                double avgC2 = Math.Round(totC2 / seconds, 2);

                QualityScanGrade grade;
                string reason;
                if (totCu > 0 || maxC2 > 50 || totC2 > 200 || maxC1 > 500)
                {
                    grade = QualityScanGrade.Poor;
                    reason = $"CD exceeds safe limits (max C1 {maxC1}/s, total C2 {totC2}, CU {totCu}).";
                }
                else if (totC2 > 0 || maxC1 > CdRedBookC1Limit)
                {
                    grade = QualityScanGrade.Marginal;
                    reason = $"CD shows elevated errors (max C1 {maxC1}/s vs {CdRedBookC1Limit}/s Red Book limit, total C2 {totC2}).";
                }
                else if (maxC1 > 50)
                {
                    grade = QualityScanGrade.Good;
                    reason = $"CD within Red Book limits (max C1 {maxC1}/s <= {CdRedBookC1Limit}/s, C2 0, CU 0).";
                }
                else
                {
                    grade = QualityScanGrade.Excellent;
                    reason = $"CD clean (max C1 {maxC1}/s, C2 0, CU 0).";
                }

                return new QualityScanSummary(
                    totalSectorsScanned, samples.Count, avgSpeedX, maxSpeedX,
                    totC1, totC2, totCu, maxC1, maxC2, maxCu, avgC1, avgC2,
                    0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0,
                    grade, reason);
            }

            case QualityScanMediaKind.Dvd:
            {
                long totPie = 0, totPif = 0, totPof = 0;
                int maxPif = 0;
                int maxPie8 = 0;
                long windowPie = 0;
                int windowStartLba = samples[0].StartLba;

                foreach (var s in samples)
                {
                    int pie = s.Pie ?? 0;
                    int pif = s.Pif ?? 0;
                    int pof = s.Pof ?? 0;
                    totPie += pie;
                    totPif += pif;
                    totPof += pof;
                    if (pif > maxPif) maxPif = pif;

                    windowPie += pie;
                    if (s.EndLba - windowStartLba >= 128)
                    {
                        if (windowPie > maxPie8)
                        {
                            maxPie8 = (int)Math.Min(int.MaxValue, windowPie);
                        }
                        windowPie = 0;
                        windowStartLba = s.EndLba;
                    }
                }
                if (windowPie > maxPie8)
                {
                    maxPie8 = (int)Math.Min(int.MaxValue, windowPie);
                }

                double eccBlocks = Math.Max(1.0, totalSectorsScanned / 16.0);
                double eightEccBlocks = Math.Max(1.0, totalSectorsScanned / 128.0);
                double avgPie8 = Math.Round(totPie / eightEccBlocks, 2);
                double avgPif = Math.Round(totPif / eccBlocks, 3);

                QualityScanGrade grade;
                string reason;
                if (totPof > 0 || maxPie8 > DvdEcmaPie8EccLimit || maxPif > DvdEcmaPifLimit)
                {
                    grade = QualityScanGrade.Poor;
                    reason = $"DVD exceeds ECMA-267/-349 limits (max PIE/8ECC {maxPie8} vs {DvdEcmaPie8EccLimit}, max PIF {maxPif} vs {DvdEcmaPifLimit}, POF {totPof}).";
                }
                else if (maxPie8 > 140 || maxPif > 2)
                {
                    grade = QualityScanGrade.Marginal;
                    reason = $"DVD approaching ECMA limits (max PIE/8ECC {maxPie8}, max PIF {maxPif}).";
                }
                else if (maxPie8 > 40 || maxPif > 0)
                {
                    grade = QualityScanGrade.Good;
                    reason = $"DVD within ECMA-267/-349 limits (max PIE/8ECC {maxPie8} <= {DvdEcmaPie8EccLimit}, max PIF {maxPif} <= {DvdEcmaPifLimit}).";
                }
                else
                {
                    grade = QualityScanGrade.Excellent;
                    reason = $"DVD clean (max PIE/8ECC {maxPie8}, max PIF 0, POF 0).";
                }

                return new QualityScanSummary(
                    totalSectorsScanned, samples.Count, avgSpeedX, maxSpeedX,
                    0, 0, 0, 0, 0, 0, 0, 0,
                    totPie, totPif, totPof, maxPie8, maxPif, avgPie8, avgPif,
                    0, 0, 0, 0, 0, 0,
                    grade, reason);
            }

            default: // BluRay
            {
                long totLdc = 0, totBis = 0;
                int maxLdc = 0, maxBis = 0;
                foreach (var s in samples)
                {
                    int ldc = s.Ldc ?? 0;
                    int bis = s.Bis ?? 0;
                    totLdc += ldc;
                    totBis += bis;
                    if (ldc > maxLdc) maxLdc = ldc;
                    if (bis > maxBis) maxBis = bis;
                }

                double clusters = Math.Max(1.0, totalSectorsScanned / 32.0);
                double avgLdc = Math.Round(totLdc / clusters, 2);
                double avgBis = Math.Round(totBis / clusters, 3);

                QualityScanGrade grade;
                string reason;
                if (maxLdc > BdLdcClusterLimit || maxBis > BdBisClusterLimit)
                {
                    grade = QualityScanGrade.Poor;
                    reason = $"BD exceeds thresholds (max LDC {maxLdc}/cluster, max BIS {maxBis}/cluster).";
                }
                else if (maxLdc > 150 || maxBis > 15)
                {
                    grade = QualityScanGrade.Marginal;
                    reason = $"BD elevated error counts (max LDC {maxLdc}/cluster, max BIS {maxBis}/cluster).";
                }
                else if (maxLdc > 50 || maxBis > 3)
                {
                    grade = QualityScanGrade.Good;
                    reason = $"BD healthy (max LDC {maxLdc}/cluster, max BIS {maxBis}/cluster).";
                }
                else
                {
                    grade = QualityScanGrade.Excellent;
                    reason = $"BD clean (max LDC {maxLdc}/cluster, max BIS {maxBis}/cluster).";
                }

                return new QualityScanSummary(
                    totalSectorsScanned, samples.Count, avgSpeedX, maxSpeedX,
                    0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, 0,
                    totLdc, totBis, maxLdc, maxBis, avgLdc, avgBis,
                    grade, reason);
            }
        }
    }
}

/// <summary>
/// Full optical disc quality scan report with JSON serialization, datasheet-style SVG graph export,
/// and plain-language summary.
/// </summary>
public sealed record QualityScanReport(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    bool IsSupported,
    QualityScanStatus Status,
    bool IsExperimentalHardwareScan,
    string? StatusMessage,
    string DriveVendor,
    string DriveProduct,
    string DriveRevision,
    string? DriverName,
    QualityScanProtocol Protocol,
    QualityScanMediaKind MediaKind,
    string? MediaType,
    string? MediaId,
    int TotalDiscSectors,
    QualityScanSummary Summary,
    IReadOnlyList<QualityScanIntervalSample> Samples,
    TimeSpan Elapsed)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static QualityScanReport CreateUnsupported(
        string vendor,
        string product,
        string revision,
        string reason,
        QualityScanMediaKind mediaKind = QualityScanMediaKind.Cd) => new(
        SchemaVersion: CurrentSchemaVersion,
        CreatedUtc: DateTimeOffset.UtcNow,
        IsSupported: false,
        Status: QualityScanStatus.UnsupportedDrive,
        IsExperimentalHardwareScan: false,
        StatusMessage: reason,
        DriveVendor: vendor,
        DriveProduct: product,
        DriveRevision: revision,
        DriverName: null,
        Protocol: QualityScanProtocol.None,
        MediaKind: mediaKind,
        MediaType: null,
        MediaId: null,
        TotalDiscSectors: 0,
        Summary: QualityScanSummary.Empty(QualityScanGrade.NotEvaluated, reason),
        Samples: Array.Empty<QualityScanIntervalSample>(),
        Elapsed: TimeSpan.Zero);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static QualityScanReport Parse(string json)
    {
        QualityScanReport? report;
        try
        {
            report = JsonSerializer.Deserialize<QualityScanReport>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a quality scan report: {ex.Message}", ex);
        }

        if (report == null)
        {
            throw new FormatException("Not a quality scan report: empty payload.");
        }
        if (report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported quality scan schema version {report.SchemaVersion}.");
        }
        return report;
    }

    public static QualityScanReport Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public Task SaveJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureParentDirectory(path);
        WriteAtomic(path, ToJson());
        return Task.CompletedTask;
    }

    public Task SaveSvgAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureParentDirectory(path);
        WriteAtomic(path, ToSvg());
        return Task.CompletedTask;
    }

    public string Describe()
    {
        if (!IsSupported || Status == QualityScanStatus.UnsupportedDrive)
        {
            return $"Quality scan unsupported for '{DriveVendor} {DriveProduct}': {StatusMessage}";
        }
        if (Status != QualityScanStatus.Completed)
        {
            return $"Quality scan {Status}: {StatusMessage}";
        }

        string exp = IsExperimentalHardwareScan
            ? " [EXPERIMENTAL/UNVERIFIED: hardware scan pending live comparison against QPxTool]"
            : " [SIMULATOR]";
        return $"{MediaKind} quality scan ({Protocol}): {Summary.Grade} - {Summary.GradeReason} "
             + $"{Summary.TotalSectorsScanned:N0} sectors at {Summary.AverageSpeedX:F1}x avg.{exp}";
    }

    /// <summary>
    /// Exports a flat-carbon datasheet-style SVG plot of primary/secondary ECC errors and read speed vs. LBA,
    /// with spec limit lines and hatched out-of-spec band.
    /// </summary>
    public string ToSvg()
    {
        const int width = 840;
        const int height = 430;
        const int left = 64;
        const int right = 768;
        const int errTop = 64;
        const int errBottom = 252;
        const int spdTop = 278;
        const int spdBottom = 366;

        int plotW = right - left;
        int errH = errBottom - errTop;
        int spdH = spdBottom - spdTop;

        (string primaryLabel, string secondaryLabel, double specLimit, string limitLabel) = MediaKind switch
        {
            QualityScanMediaKind.Dvd => ("PIE", "PIF", (double)QualityScanSummary.DvdEcmaPie8EccLimit, $"LIMIT PIE {QualityScanSummary.DvdEcmaPie8EccLimit}/8ECC"),
            QualityScanMediaKind.BluRay => ("LDC", "BIS", (double)QualityScanSummary.BdLdcClusterLimit, $"LIMIT LDC {QualityScanSummary.BdLdcClusterLimit}"),
            _ => ("C1 (BLER)", "C2 (E22)", (double)QualityScanSummary.CdRedBookC1Limit, $"LIMIT C1 {QualityScanSummary.CdRedBookC1Limit}/s")
        };

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"JetBrains Mono, Cascadia Mono, monospace\" font-size=\"11\">\n");
        sb.Append("<defs>\n");
        sb.Append("  <pattern id=\"overLimitHatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">\n");
        sb.Append("    <line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#3D3C38\" stroke-width=\"1\"/>\n");
        sb.Append("  </pattern>\n");
        sb.Append("</defs>\n");

        sb.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#0D0D0C\"/>\n");
        sb.Append($"<rect x=\"12\" y=\"12\" width=\"{width - 24}\" height=\"{height - 24}\" fill=\"#131311\" stroke=\"#282825\" stroke-width=\"1\"/>\n");
        // Reticle corner marks on primary metrology panel
        sb.Append("<path d=\"M12,24 L12,12 L24,12\" fill=\"none\" stroke=\"#F04E23\" stroke-width=\"1.5\"/>\n");
        sb.Append($"<path d=\"M{width - 24},{height - 12} L{width - 12},{height - 12} L{width - 12},{height - 24}\" fill=\"none\" stroke=\"#F04E23\" stroke-width=\"1.5\"/>\n");

        string title = Status == QualityScanStatus.Completed
            ? $"{MediaKind} quality scan: {Summary.Grade} ({Summary.GradeReason})"
            : $"{MediaKind} quality scan: {Status} ({StatusMessage ?? "no data"})";
        string subtitle = $"{DriveVendor} {DriveProduct} · {Protocol} · {Summary.TotalSectorsScanned:N0} LBAs · {CreatedUtc:yyyy-MM-dd HH:mm} UTC"
            + (IsExperimentalHardwareScan ? " · EXPERIMENTAL (UNVERIFIED)" : " · SIMULATED");

        sb.Append($"<text x=\"{left}\" y=\"32\" font-family=\"IBM Plex Sans, Segoe UI, sans-serif\" font-size=\"14\" font-weight=\"700\" fill=\"#EAE6DF\">{Esc(title)}</text>\n");
        sb.Append($"<text x=\"{left}\" y=\"48\" font-size=\"10\" fill=\"#A6A29A\">{Esc(subtitle)}</text>\n");

        // Error plot frame + horizontal hairline grid
        sb.Append($"<rect x=\"{left}\" y=\"{errTop}\" width=\"{plotW}\" height=\"{errH}\" fill=\"#0D0D0C\" stroke=\"#282825\" stroke-width=\"1\"/>\n");
        for (int g = 1; g <= 3; g++)
        {
            double gy = errTop + errH * (g / 4.0);
            sb.Append($"<line x1=\"{left}\" y1=\"{N(gy)}\" x2=\"{right}\" y2=\"{N(gy)}\" stroke=\"#282825\" stroke-width=\"1\"/>\n");
        }

        double maxErr = Math.Max(specLimit * 1.1, 10.0);
        if (Samples.Count > 0)
        {
            double observedMax = Samples.Max(s => Math.Max(PrimaryError(s), SecondaryError(s)));
            if (observedMax > maxErr)
            {
                maxErr = Math.Ceiling(observedMax * 1.15);
            }
        }

        // Spec limit line + hatched region above limit
        double limitY = errBottom - Math.Clamp(specLimit / maxErr, 0, 1) * errH;
        if (limitY > errTop)
        {
            sb.Append($"<rect x=\"{left}\" y=\"{errTop}\" width=\"{plotW}\" height=\"{N(limitY - errTop)}\" fill=\"url(#overLimitHatch)\" opacity=\"0.55\"/>\n");
        }
        sb.Append($"<line x1=\"{left}\" y1=\"{N(limitY)}\" x2=\"{right}\" y2=\"{N(limitY)}\" stroke=\"#FF9E22\" stroke-width=\"1\" stroke-dasharray=\"5,3\"/>\n");
        sb.Append($"<text x=\"{right - 4}\" y=\"{N(Math.Max(errTop + 11, limitY - 4))}\" text-anchor=\"end\" font-size=\"9\" fill=\"#FF9E22\">{Esc(limitLabel)}</text>\n");

        sb.Append($"<text x=\"{left - 6}\" y=\"{errTop + 10}\" text-anchor=\"end\" font-size=\"10\" fill=\"#858178\">{N(maxErr)}</text>\n");
        sb.Append($"<text x=\"{left - 6}\" y=\"{errBottom}\" text-anchor=\"end\" font-size=\"10\" fill=\"#858178\">0</text>\n");
        sb.Append($"<text x=\"{left}\" y=\"{errTop - 6}\" font-size=\"10\" fill=\"#A6A29A\">{Esc(primaryLabel)} / {Esc(secondaryLabel)} (counts per interval)</text>\n");

        // Speed plot frame
        sb.Append($"<rect x=\"{left}\" y=\"{spdTop}\" width=\"{plotW}\" height=\"{spdH}\" fill=\"#0D0D0C\" stroke=\"#282825\" stroke-width=\"1\"/>\n");
        double maxSpeed = Samples.Count > 0 ? Math.Max(1.0, Math.Ceiling(Samples.Max(s => s.ReadSpeedX) * 1.15)) : 16.0;
        sb.Append($"<line x1=\"{left}\" y1=\"{N(spdTop + spdH * 0.5)}\" x2=\"{right}\" y2=\"{N(spdTop + spdH * 0.5)}\" stroke=\"#282825\" stroke-width=\"1\"/>\n");
        sb.Append($"<text x=\"{left - 6}\" y=\"{spdTop + 10}\" text-anchor=\"end\" font-size=\"10\" fill=\"#858178\">{N(maxSpeed)}x</text>\n");
        sb.Append($"<text x=\"{left - 6}\" y=\"{spdBottom}\" text-anchor=\"end\" font-size=\"10\" fill=\"#858178\">0x</text>\n");
        sb.Append($"<text x=\"{left}\" y=\"{spdTop - 6}\" font-size=\"10\" fill=\"#A6A29A\">Read speed (x)</text>\n");

        if (Samples.Count > 0)
        {
            int minLba = Samples[0].StartLba;
            int maxLba = Math.Max(minLba + 1, Math.Max(TotalDiscSectors, Samples[^1].EndLba));
            double span = maxLba - minLba;

            double MapX(QualityScanIntervalSample s, int i)
            {
                if (Samples.Count == 1) return left + plotW * 0.5;
                double frac = Math.Clamp((s.EndLba - minLba) / span, 0.0, 1.0);
                if (frac <= 0 && i > 0) frac = (double)i / (Samples.Count - 1);
                return left + frac * plotW;
            }

            var primaryPts = new List<string>(Samples.Count);
            var secondaryPts = new List<string>(Samples.Count);
            var speedPts = new List<string>(Samples.Count);
            for (int i = 0; i < Samples.Count; i++)
            {
                var s = Samples[i];
                double x = MapX(s, i);
                double pY = errBottom - Math.Clamp(PrimaryError(s) / maxErr, 0.0, 1.0) * errH;
                double sY = errBottom - Math.Clamp(SecondaryError(s) / maxErr, 0.0, 1.0) * errH;
                double vY = spdBottom - Math.Clamp(s.ReadSpeedX / maxSpeed, 0.0, 1.0) * spdH;
                primaryPts.Add($"{N(x)},{N(pY)}");
                secondaryPts.Add($"{N(x)},{N(sY)}");
                speedPts.Add($"{N(x)},{N(vY)}");
            }

            AppendPolyline(sb, primaryPts, "#F04E23", "1.75");
            AppendPolyline(sb, secondaryPts, "#FF5F56", "1.5");
            AppendPolyline(sb, speedPts, "#A6A29A", "1.5");

            sb.Append($"<text x=\"{left}\" y=\"{spdBottom + 14}\" font-size=\"9\" fill=\"#858178\">LBA {minLba:N0}</text>\n");
            sb.Append($"<text x=\"{right}\" y=\"{spdBottom + 14}\" text-anchor=\"end\" font-size=\"9\" fill=\"#858178\">LBA {maxLba:N0}</text>\n");
        }
        else
        {
            sb.Append($"<text x=\"{left + 12}\" y=\"{errTop + 24}\" font-size=\"11\" fill=\"#858178\">No interval samples recorded.</text>\n");
        }

        // Right-end inline legend + Document-control footer
        int ly = height - 24;
        sb.Append($"<line x1=\"{left}\" y1=\"{ly - 4}\" x2=\"{left + 16}\" y2=\"{ly - 4}\" stroke=\"#F04E23\" stroke-width=\"2\"/>\n");
        sb.Append($"<text x=\"{left + 22}\" y=\"{ly}\" font-size=\"10\" fill=\"#EAE6DF\">{Esc(primaryLabel)}</text>\n");
        sb.Append($"<line x1=\"{left + 130}\" y1=\"{ly - 4}\" x2=\"{left + 146}\" y2=\"{ly - 4}\" stroke=\"#FF5F56\" stroke-width=\"2\"/>\n");
        sb.Append($"<text x=\"{left + 152}\" y=\"{ly}\" font-size=\"10\" fill=\"#EAE6DF\">{Esc(secondaryLabel)}</text>\n");
        sb.Append($"<line x1=\"{left + 260}\" y1=\"{ly - 4}\" x2=\"{left + 276}\" y2=\"{ly - 4}\" stroke=\"#A6A29A\" stroke-width=\"2\"/>\n");
        sb.Append($"<text x=\"{left + 282}\" y=\"{ly}\" font-size=\"10\" fill=\"#EAE6DF\">Read Speed</text>\n");

        string docStatus = IsExperimentalHardwareScan ? "EXPERIMENTAL" : "SIMULATED";
        sb.Append($"<text x=\"{right}\" y=\"{ly}\" text-anchor=\"end\" font-size=\"9\" fill=\"#858178\">TW-IGN-FIG-001 · REV 1.0 · {CreatedUtc:yyyy-MM-dd} · {docStatus}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private double PrimaryError(QualityScanIntervalSample s) => MediaKind switch
    {
        QualityScanMediaKind.Dvd => s.Pie ?? 0,
        QualityScanMediaKind.BluRay => s.Ldc ?? 0,
        _ => s.C1 ?? 0
    };

    private double SecondaryError(QualityScanIntervalSample s) => MediaKind switch
    {
        QualityScanMediaKind.Dvd => s.Pif ?? 0,
        QualityScanMediaKind.BluRay => s.Bis ?? 0,
        _ => s.C2 ?? 0
    };

    private static void AppendPolyline(StringBuilder sb, List<string> pts, string stroke, string width)
    {
        if (pts.Count == 1)
        {
            string[] xy = pts[0].Split(',');
            sb.Append($"<rect x=\"{xy[0]}\" y=\"{xy[1]}\" width=\"3\" height=\"3\" fill=\"{stroke}\"/>\n");
            return;
        }
        sb.Append($"<polyline fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{width}\" points=\"{string.Join(' ', pts)}\"/>\n");
    }

    private static void EnsureParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string N(double value) =>
        (double.IsFinite(value) ? value : 0.0).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string? value) =>
        SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
