using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Verification;

/// <summary>
/// One telemetry sample captured during a burn operation.
/// </summary>
public sealed record BurnTelemetrySample(
    double ElapsedSeconds,
    int Lba,
    int TotalLba,
    double ProgressPercent,
    double? DeviceBufferFillPercent,
    double? HostBufferFillPercent,
    double SpeedX,
    double SpeedMegabytesPerSecond,
    string? Action = null);

/// <summary>
/// Aggregate statistics across all samples observed by a <see cref="BurnTelemetrySeries"/>.
/// </summary>
public sealed record BurnTelemetrySummary(
    long TotalSamplesObserved,
    int StoredPoints,
    int DownsampleFactor,
    double PeakSpeedX,
    double PeakSpeedMegabytesPerSecond,
    double AverageSpeedX,
    double? MinDeviceBufferPercent,
    double? MinHostBufferPercent);

/// <summary>
/// Serializable envelope for exported burn telemetry JSON files.
/// </summary>
public sealed record BurnTelemetryReport(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string? Title,
    string? Drive,
    string? MediaType,
    BurnTelemetrySummary Summary,
    IReadOnlyList<BurnTelemetrySample> Samples);

/// <summary>
/// Accumulates live burn progress snapshots in strictly bounded O(1) memory using bucket-accumulated stride halving,
/// preserving buffer-fill dips while supporting JSON and standalone theme-neutral SVG plot exports.
/// </summary>
public sealed class BurnTelemetrySeries : IProgress<BurnProgressSnapshot>
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultMaxPoints = 600;
    public const int MinimumMaxPoints = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly IProgress<BurnProgressSnapshot>? _inner;
    private readonly List<BurnTelemetrySample> _samples;

    private int _stride = 1;
    private int _bucketCount;
    private BurnTelemetrySample? _bucket;
    private double _bucketSpeedXSum;
    private double _bucketSpeedMbpsSum;
    private int _bucketSpeedCount;

    private long _totalSamplesObserved;
    private double _peakSpeedX;
    private double _peakSpeedMbps;
    private double _totalSpeedXSum;
    private long _totalSpeedSamples;
    private double? _minDeviceBuffer;
    private double? _minHostBuffer;

    private string? _title;
    private string? _drive;
    private string? _mediaType;
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;

    public BurnTelemetrySeries(int maxPoints = DefaultMaxPoints, IProgress<BurnProgressSnapshot>? inner = null)
    {
        MaxPoints = Math.Max(MinimumMaxPoints, maxPoints);
        _inner = inner;
        _samples = new List<BurnTelemetrySample>(MaxPoints);
    }

    /// <summary>Maximum number of stored points retained in memory regardless of burn length.</summary>
    public int MaxPoints { get; }

    public string? Title { get { lock (_lock) return _title; } set { lock (_lock) _title = value; } }
    public string? Drive { get { lock (_lock) return _drive; } set { lock (_lock) _drive = value; } }
    public string? MediaType { get { lock (_lock) return _mediaType; } set { lock (_lock) _mediaType = value; } }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return Math.Min(MaxPoints, _samples.Count + (_bucketCount > 0 ? 1 : 0));
            }
        }
    }

    public long TotalSamplesObserved { get { lock (_lock) return _totalSamplesObserved; } }
    public int DownsampleFactor { get { lock (_lock) return _stride; } }

    public void Reset(string? title = null, string? drive = null, string? mediaType = null)
    {
        lock (_lock)
        {
            _samples.Clear();
            _stride = 1;
            _bucketCount = 0;
            _bucket = null;
            _bucketSpeedXSum = 0;
            _bucketSpeedMbpsSum = 0;
            _bucketSpeedCount = 0;
            _totalSamplesObserved = 0;
            _peakSpeedX = 0;
            _peakSpeedMbps = 0;
            _totalSpeedXSum = 0;
            _totalSpeedSamples = 0;
            _minDeviceBuffer = null;
            _minHostBuffer = null;
            _title = title;
            _drive = drive;
            _mediaType = mediaType;
            _createdUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Report(BurnProgressSnapshot value)
    {
        if (value != null)
        {
            Record(value);
            _inner?.Report(value);
        }
    }

    public void Record(BurnProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        double speedX = double.IsFinite(snapshot.CurrentSpeedMultiplier) && snapshot.CurrentSpeedMultiplier >= 0
            ? snapshot.CurrentSpeedMultiplier
            : 0.0;
        double speedMbps = double.IsFinite(snapshot.CurrentSpeedMegabytesPerSecond) && snapshot.CurrentSpeedMegabytesPerSecond >= 0
            ? snapshot.CurrentSpeedMegabytesPerSecond
            : 0.0;
        double? deviceBuf = ClampNullablePercent(snapshot.DeviceBufferFillPercent);
        double? hostBuf = ClampNullablePercent(snapshot.HostBufferFillPercent);
        double elapsed = Math.Max(0.0, snapshot.ElapsedTime.TotalSeconds);
        int lba = Math.Max(0, snapshot.CurrentLba);
        int totalLba = Math.Max(0, snapshot.TotalLba);
        double progressPct = snapshot.ProgressPercentage;

        AddSample(new BurnTelemetrySample(
            Math.Round(elapsed, 3),
            lba,
            totalLba,
            Math.Round(progressPct, 2),
            deviceBuf,
            hostBuf,
            Math.Round(speedX, 3),
            Math.Round(speedMbps, 3),
            string.IsNullOrWhiteSpace(snapshot.CurrentActionDescription) ? null : snapshot.CurrentActionDescription));
    }

    public void AddSample(BurnTelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_lock)
        {
            _totalSamplesObserved++;
            if (sample.SpeedX > 0 && double.IsFinite(sample.SpeedX))
            {
                _peakSpeedX = Math.Max(_peakSpeedX, sample.SpeedX);
                _peakSpeedMbps = Math.Max(_peakSpeedMbps, sample.SpeedMegabytesPerSecond);
                _totalSpeedXSum += sample.SpeedX;
                _totalSpeedSamples++;
            }
            if (sample.DeviceBufferFillPercent is double db)
            {
                _minDeviceBuffer = _minDeviceBuffer is double curDb ? Math.Min(curDb, db) : db;
            }
            if (sample.HostBufferFillPercent is double hb)
            {
                _minHostBuffer = _minHostBuffer is double curHb ? Math.Min(curHb, hb) : hb;
            }

            if (_samples.Count == 0)
            {
                _samples.Add(sample);
                return;
            }

            AccumulateIntoBucket(sample);
            if (_bucketCount < _stride)
            {
                return;
            }

            var finalized = FinalizeBucket();
            _samples.Add(finalized);

            if (_samples.Count >= MaxPoints)
            {
                CompactInPlace();
            }
        }
    }

    public IReadOnlyList<BurnTelemetrySample> GetSamples()
    {
        lock (_lock)
        {
            return BuildCurrentSampleListLocked();
        }
    }

    public BurnTelemetrySummary GetSummary()
    {
        lock (_lock)
        {
            var list = BuildCurrentSampleListLocked();
            double avg = _totalSpeedSamples > 0 ? Math.Round(_totalSpeedXSum / _totalSpeedSamples, 3) : 0.0;
            return new BurnTelemetrySummary(
                _totalSamplesObserved,
                list.Count,
                _stride,
                Math.Round(_peakSpeedX, 3),
                Math.Round(_peakSpeedMbps, 3),
                avg,
                _minDeviceBuffer is double db ? Math.Round(db, 1) : null,
                _minHostBuffer is double hb ? Math.Round(hb, 1) : null);
        }
    }

    public BurnTelemetryReport CreateReport()
    {
        lock (_lock)
        {
            var list = BuildCurrentSampleListLocked();
            double avg = _totalSpeedSamples > 0 ? Math.Round(_totalSpeedXSum / _totalSpeedSamples, 3) : 0.0;
            var summary = new BurnTelemetrySummary(
                _totalSamplesObserved,
                list.Count,
                _stride,
                Math.Round(_peakSpeedX, 3),
                Math.Round(_peakSpeedMbps, 3),
                avg,
                _minDeviceBuffer is double db ? Math.Round(db, 1) : null,
                _minHostBuffer is double hb ? Math.Round(hb, 1) : null);

            return new BurnTelemetryReport(CurrentSchemaVersion, _createdUtc, _title, _drive, _mediaType, summary, list);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(CreateReport(), JsonOptions);

    public void SaveJson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureParentDirectory(path);
        WriteAtomic(path, ToJson());
    }

    public void SaveSvg(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureParentDirectory(path);
        WriteAtomic(path, ToSvg());
    }

    public static BurnTelemetryReport ParseJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        BurnTelemetryReport? report;
        try
        {
            report = JsonSerializer.Deserialize<BurnTelemetryReport>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid burn telemetry JSON: {ex.Message}", ex);
        }

        if (report == null || report.Samples == null)
        {
            throw new FormatException("Invalid burn telemetry JSON: missing samples.");
        }
        if (report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported telemetry schema version {report.SchemaVersion}.");
        }
        return report;
    }

    public static BurnTelemetryReport LoadJson(string path) => ParseJson(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>
    /// Exports a standalone, theme-neutral SVG plot showing instantaneous write speed (x and MB/s),
    /// drive hardware buffer fill (%), and host I/O buffer fill (%).
    /// </summary>
    public string ToSvg() => RenderSvg(CreateReport());

    public static string RenderSvg(BurnTelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        const int width = 840;
        const int height = 420;
        const int left = 64;
        const int right = 772;
        const int top = 74;
        const int bottom = 336;
        int plotW = right - left;
        int plotH = bottom - top;

        var samples = report.Samples;
        bool hasDeviceBuf = samples.Any(s => s.DeviceBufferFillPercent.HasValue);
        bool hasHostBuf = samples.Any(s => s.HostBufferFillPercent.HasValue);

        double maxTime = samples.Count > 0 ? samples.Max(s => s.ElapsedSeconds) : 0.0;
        bool useProgressX = maxTime <= 0.001 && samples.Count > 0;
        double xMin = 0.0;
        double xMax = useProgressX ? 100.0 : Math.Max(0.1, maxTime);

        double maxSpeedX = samples.Count > 0 ? samples.Max(s => s.SpeedX) : 0.0;
        double ySpeedMax = Math.Max(1.0, Math.Ceiling(maxSpeedX * 1.15));
        double maxSpeedMbps = samples.Count > 0 ? samples.Max(s => s.SpeedMegabytesPerSecond) : 0.0;
        double yMbpsRatio = maxSpeedX > 0.001 ? maxSpeedMbps / maxSpeedX : 0.0;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Consolas, 'Liberation Mono', monospace\" font-size=\"11\">\n");
        sb.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#FAFAFA\" stroke=\"#CBD5E1\" stroke-width=\"1\"/>\n");

        // Header
        string title = string.IsNullOrWhiteSpace(report.Title) ? "Optical Burn Telemetry" : report.Title;
        string meta = string.Join(" | ", new[]
        {
            report.Drive,
            report.MediaType,
            $"{report.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC"
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        sb.Append($"<text x=\"{left}\" y=\"24\" font-size=\"14\" font-weight=\"bold\" fill=\"#0F172A\">{Esc(title)}</text>\n");
        sb.Append($"<text x=\"{left}\" y=\"42\" fill=\"#475569\">{Esc(meta)}</text>\n");

        string stats = $"Peak {N(report.Summary.PeakSpeedX)}x ({N(report.Summary.PeakSpeedMegabytesPerSecond)} MB/s) | " +
                       $"Avg {N(report.Summary.AverageSpeedX)}x | " +
                       $"Drive Buf Min {(report.Summary.MinDeviceBufferPercent is double mdb ? $"{N(mdb)}%" : "N/A")} | " +
                       $"Host Buf Min {(report.Summary.MinHostBufferPercent is double mhb ? $"{N(mhb)}%" : "N/A")} | " +
                       $"Samples {report.Summary.TotalSamplesObserved:N0} (plotted {samples.Count})";
        sb.Append($"<text x=\"{left}\" y=\"59\" fill=\"#334155\">{Esc(stats)}</text>\n");

        // Plot frame & hairline grid
        sb.Append($"<rect x=\"{left}\" y=\"{top}\" width=\"{plotW}\" height=\"{plotH}\" fill=\"#FFFFFF\" stroke=\"#94A3B8\" stroke-width=\"1\"/>\n");
        for (int i = 0; i <= 4; i++)
        {
            double frac = i / 4.0;
            double y = bottom - frac * plotH;
            if (i is > 0 and < 4)
            {
                sb.Append($"<line x1=\"{left}\" y1=\"{N(y)}\" x2=\"{right}\" y2=\"{N(y)}\" stroke=\"#E2E8F0\" stroke-width=\"1\"/>\n");
            }
            double speedTick = frac * ySpeedMax;
            string leftLabel = yMbpsRatio > 0
                ? $"{N(speedTick)}x"
                : $"{N(speedTick)}x";
            sb.Append($"<text x=\"{left - 6}\" y=\"{N(y + 4)}\" text-anchor=\"end\" fill=\"#0284C7\">{Esc(leftLabel)}</text>\n");
            sb.Append($"<text x=\"{right + 6}\" y=\"{N(y + 4)}\" text-anchor=\"start\" fill=\"#475569\">{i * 25}%</text>\n");
        }

        for (int i = 0; i <= 4; i++)
        {
            double frac = i / 4.0;
            double x = left + frac * plotW;
            if (i is > 0 and < 4)
            {
                sb.Append($"<line x1=\"{N(x)}\" y1=\"{top}\" x2=\"{N(x)}\" y2=\"{bottom}\" stroke=\"#E2E8F0\" stroke-width=\"1\"/>\n");
            }
            double xv = xMin + frac * (xMax - xMin);
            string xLabel = useProgressX ? $"{N(xv)}%" : $"{N(xv)}s";
            sb.Append($"<text x=\"{N(x)}\" y=\"{bottom + 16}\" text-anchor=\"middle\" fill=\"#475569\">{Esc(xLabel)}</text>\n");
        }

        // Axis titles
        sb.Append($"<text x=\"16\" y=\"{(top + bottom) / 2}\" text-anchor=\"middle\" transform=\"rotate(-90 16 {(top + bottom) / 2})\" fill=\"#0284C7\">Write Speed (x)</text>\n");
        sb.Append($"<text x=\"{width - 14}\" y=\"{(top + bottom) / 2}\" text-anchor=\"middle\" transform=\"rotate(90 {width - 14} {(top + bottom) / 2})\" fill=\"#475569\">Buffer Fill (%)</text>\n");
        sb.Append($"<text x=\"{(left + right) / 2}\" y=\"{bottom + 34}\" text-anchor=\"middle\" fill=\"#475569\">{(useProgressX ? "Progress (%)" : "Elapsed Time (seconds)")}</text>\n");

        if (samples.Count == 0)
        {
            sb.Append($"<text x=\"{(left + right) / 2}\" y=\"{(top + bottom) / 2}\" text-anchor=\"middle\" fill=\"#64748B\">No telemetry samples recorded</text>\n");
        }
        else
        {
            double MapX(BurnTelemetrySample s, int idx)
            {
                double raw = useProgressX ? s.ProgressPercent : s.ElapsedSeconds;
                if (Math.Abs(xMax - xMin) <= 1e-9)
                {
                    return samples.Count == 1 ? left + plotW * 0.5 : left + (double)idx / (samples.Count - 1) * plotW;
                }
                return left + Math.Clamp((raw - xMin) / (xMax - xMin), 0.0, 1.0) * plotW;
            }

            double MapSpeedY(double speedX) =>
                bottom - Math.Clamp(speedX / ySpeedMax, 0.0, 1.0) * plotH;

            double MapBufferY(double pct) =>
                bottom - Math.Clamp(pct / 100.0, 0.0, 1.0) * plotH;

            // Host I/O buffer (amber dashed)
            if (hasHostBuf)
            {
                AppendSeries(sb, samples, s => s.HostBufferFillPercent, MapX, MapBufferY, "#D97706", "1.75", "5,3");
            }

            // Device buffer (green solid)
            if (hasDeviceBuf)
            {
                AppendSeries(sb, samples, s => s.DeviceBufferFillPercent, MapX, MapBufferY, "#16A34A", "2", null);
            }

            // Write speed (blue solid)
            AppendSeries(sb, samples, s => s.SpeedX, MapX, MapSpeedY, "#0284C7", "2.2", null);
        }

        // Legend
        int ly = height - 20;
        sb.Append($"<line x1=\"{left}\" y1=\"{ly - 4}\" x2=\"{left + 20}\" y2=\"{ly - 4}\" stroke=\"#0284C7\" stroke-width=\"2.2\"/>\n");
        sb.Append($"<text x=\"{left + 26}\" y=\"{ly}\" fill=\"#0F172A\">Write Speed (x / MB/s)</text>\n");

        int devX = left + 200;
        sb.Append($"<line x1=\"{devX}\" y1=\"{ly - 4}\" x2=\"{devX + 20}\" y2=\"{ly - 4}\" stroke=\"#16A34A\" stroke-width=\"2\"/>\n");
        sb.Append($"<text x=\"{devX + 26}\" y=\"{ly}\" fill=\"#0F172A\">{(hasDeviceBuf ? "Drive Buffer (%)" : "Drive Buffer (N/A)")}</text>\n");

        int hostX = left + 390;
        sb.Append($"<line x1=\"{hostX}\" y1=\"{ly - 4}\" x2=\"{hostX + 20}\" y2=\"{ly - 4}\" stroke=\"#D97706\" stroke-width=\"1.75\" stroke-dasharray=\"5,3\"/>\n");
        sb.Append($"<text x=\"{hostX + 26}\" y=\"{ly}\" fill=\"#0F172A\">{(hasHostBuf ? "Host I/O Buffer (%)" : "Host I/O Buffer (N/A)")}</text>\n");

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void AppendSeries(
        StringBuilder sb,
        IReadOnlyList<BurnTelemetrySample> samples,
        Func<BurnTelemetrySample, double?> selector,
        Func<BurnTelemetrySample, int, double> mapX,
        Func<double, double> mapY,
        string stroke,
        string strokeWidth,
        string? dashArray)
    {
        var points = new List<string>();
        for (int i = 0; i < samples.Count; i++)
        {
            if (selector(samples[i]) is double val && double.IsFinite(val))
            {
                points.Add($"{N(mapX(samples[i], i))},{N(mapY(val))}");
            }
            else if (points.Count > 0)
            {
                FlushSegment(sb, points, stroke, strokeWidth, dashArray);
                points.Clear();
            }
        }
        if (points.Count > 0)
        {
            FlushSegment(sb, points, stroke, strokeWidth, dashArray);
        }
    }

    private static void FlushSegment(StringBuilder sb, List<string> points, string stroke, string strokeWidth, string? dashArray)
    {
        if (points.Count == 1)
        {
            string[] xy = points[0].Split(',');
            sb.Append($"<circle cx=\"{xy[0]}\" cy=\"{xy[1]}\" r=\"3\" fill=\"{stroke}\"/>\n");
            return;
        }
        string dash = string.IsNullOrEmpty(dashArray) ? string.Empty : $" stroke-dasharray=\"{dashArray}\"";
        sb.Append($"<polyline fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"{dash} points=\"{string.Join(' ', points)}\"/>\n");
    }

    private void AccumulateIntoBucket(BurnTelemetrySample sample)
    {
        if (_bucketCount == 0 || _bucket == null)
        {
            _bucket = sample;
            _bucketCount = 1;
            _bucketSpeedXSum = sample.SpeedX > 0 ? sample.SpeedX : 0;
            _bucketSpeedMbpsSum = sample.SpeedMegabytesPerSecond > 0 ? sample.SpeedMegabytesPerSecond : 0;
            _bucketSpeedCount = sample.SpeedX > 0 ? 1 : 0;
            return;
        }

        _bucketCount++;
        if (sample.SpeedX > 0)
        {
            _bucketSpeedXSum += sample.SpeedX;
            _bucketSpeedMbpsSum += sample.SpeedMegabytesPerSecond;
            _bucketSpeedCount++;
        }

        _bucket = MergeSamples(_bucket, sample, _bucketSpeedXSum, _bucketSpeedMbpsSum, _bucketSpeedCount);
    }

    private BurnTelemetrySample FinalizeBucket()
    {
        var result = _bucket!;
        _bucket = null;
        _bucketCount = 0;
        _bucketSpeedXSum = 0;
        _bucketSpeedMbpsSum = 0;
        _bucketSpeedCount = 0;
        return result;
    }

    private void CompactInPlace()
    {
        // Keep index 0 (burn start) intact and merge adjacent pairs in the interior so capacity stays O(MaxPoints).
        int write = 1;
        for (int read = 1; read < _samples.Count; read += 2)
        {
            if (read + 1 < _samples.Count)
            {
                var a = _samples[read];
                var b = _samples[read + 1];
                double avgX = (a.SpeedX + b.SpeedX) * 0.5;
                double avgMbps = (a.SpeedMegabytesPerSecond + b.SpeedMegabytesPerSecond) * 0.5;
                _samples[write++] = new BurnTelemetrySample(
                    b.ElapsedSeconds,
                    b.Lba,
                    b.TotalLba,
                    b.ProgressPercent,
                    MinNullable(a.DeviceBufferFillPercent, b.DeviceBufferFillPercent),
                    MinNullable(a.HostBufferFillPercent, b.HostBufferFillPercent),
                    Math.Round(avgX, 3),
                    Math.Round(avgMbps, 3),
                    b.Action ?? a.Action);
            }
            else
            {
                _samples[write++] = _samples[read];
            }
        }
        _samples.RemoveRange(write, _samples.Count - write);
        _stride = Math.Min(1 << 20, _stride * 2);
    }

    private List<BurnTelemetrySample> BuildCurrentSampleListLocked()
    {
        var copy = new List<BurnTelemetrySample>(_samples.Count + 1);
        copy.AddRange(_samples);
        if (_bucketCount > 0 && _bucket != null)
        {
            if (copy.Count >= MaxPoints && copy.Count > 0)
            {
                copy[^1] = _bucket;
            }
            else
            {
                copy.Add(_bucket);
            }
        }
        return copy;
    }

    private static BurnTelemetrySample MergeSamples(
        BurnTelemetrySample current,
        BurnTelemetrySample next,
        double speedXSum,
        double speedMbpsSum,
        int speedCount)
    {
        double avgX = speedCount > 0 ? speedXSum / speedCount : next.SpeedX;
        double avgMbps = speedCount > 0 ? speedMbpsSum / speedCount : next.SpeedMegabytesPerSecond;
        return new BurnTelemetrySample(
            next.ElapsedSeconds,
            next.Lba,
            next.TotalLba,
            next.ProgressPercent,
            MinNullable(current.DeviceBufferFillPercent, next.DeviceBufferFillPercent),
            MinNullable(current.HostBufferFillPercent, next.HostBufferFillPercent),
            Math.Round(avgX, 3),
            Math.Round(avgMbps, 3),
            next.Action ?? current.Action);
    }

    private static double? MinNullable(double? a, double? b) => (a, b) switch
    {
        (double x, double y) => Math.Min(x, y),
        (double x, null) => x,
        (null, double y) => y,
        _ => null
    };

    private static double? ClampNullablePercent(double? value) =>
        value is double v && double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0.0, 100.0), 2) : null;

    private static void EnsureParentDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string N(double value) =>
        (double.IsFinite(value) ? value : 0.0).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string? value) =>
        SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
