using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Audio;

/// <summary>One drive in AccurateRip's list of read offsets.</summary>
/// <param name="Name">"VENDOR - PRODUCT" as the list writes it, e.g. "HL-DT-ST - DVDRAM GP60NB50".</param>
/// <param name="ReadOffsetSamples">Samples a ripper reads further on to get the true audio (+6 for the GP60NB50).</param>
/// <param name="Submissions">How many users submitted this offset (the list caps it at 255).</param>
public sealed record DriveOffsetEntry(string Name, int ReadOffsetSamples, int Submissions);

/// <summary>Outcome state of loading <c>DriveOffsets.bin</c> from local cache or AccurateRip.</summary>
public enum DriveOffsetDatabaseStatus
{
    FreshCache,
    Downloaded,
    StaleCacheFallback,
    Unavailable
}

/// <summary>Result of loading <c>DriveOffsets.bin</c>, including whether a stale cache fallback was used.</summary>
public sealed record DriveOffsetDatabaseLoadResult(
    IReadOnlyList<DriveOffsetEntry> Entries,
    DriveOffsetDatabaseStatus Status,
    DateTime? CacheTimestampUtc,
    string? ErrorMessage = null);

/// <summary>
/// AccurateRip's drive read-offset list (<c>DriveOffsets.bin</c>), downloaded when needed and cached; never bundled.
/// Layout, read from the file itself (339,204 bytes on 2026-11-11, exactly 4,916 records): 69-byte records, a
/// little-endian 16-bit read offset, the drive name in 33 bytes padded with zeros, a 32-bit submission count, then 30
/// bytes this code does not use.
/// </summary>
public static class AccurateRipDriveOffsets
{
    public const string Url = "http://www.accuraterip.com/accuraterip/DriveOffsets.bin";
    public const int RecordSize = 69;
    private const int NameSize = 33;

    /// <summary>A cached copy older than this is downloaded again (when the network allows).</summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

    /// <summary>Default local app data cache path for <c>DriveOffsets.bin</c>.</summary>
    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IgnisDisc",
        "cache",
        "DriveOffsets.bin");

    /// <summary>Parses the list. Records with no name are skipped; a trailing partial record is ignored.</summary>
    public static IReadOnlyList<DriveOffsetEntry> Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<DriveOffsetEntry>(data.Length / RecordSize);
        for (int offset = 0; offset + RecordSize <= data.Length; offset += RecordSize)
        {
            ReadOnlySpan<byte> record = data.Slice(offset, RecordSize);
            ReadOnlySpan<byte> nameBytes = record.Slice(2, NameSize);
            int end = nameBytes.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]).Trim();
            if (name.Length == 0)
            {
                continue;
            }
            entries.Add(new DriveOffsetEntry(
                name,
                BinaryPrimitives.ReadInt16LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record.Slice(2 + NameSize, 4))));
        }
        return entries;
    }

    private static string TrimField(string? value) =>
        (value ?? string.Empty).Replace('\0', ' ').Trim();

    /// <summary>The list's name for a drive: its INQUIRY vendor and product, trimmed, joined by " - ".</summary>
    public static string DriveName(string? vendor, string? product) =>
        $"{TrimField(vendor)} - {TrimField(product)}";

    /// <summary>Normalizes an INQUIRY vendor/product string by stripping nulls, collapsing whitespace, and uppercasing.</summary>
    public static string NormalizeInquiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string cleaned = value.Replace('\0', ' ');
        return string.Join(' ', cleaned.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The entry for a drive, matched on vendor and product (case and inner spacing ignored), or null.</summary>
    public static DriveOffsetEntry? Find(IEnumerable<DriveOffsetEntry> entries, string? vendor, string? product)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string cleanVendor = TrimField(vendor);
        string cleanProduct = TrimField(product);
        if (cleanVendor.Length == 0 && cleanProduct.Length == 0)
        {
            return null;
        }

        string wanted = NormalizeInquiry(DriveName(cleanVendor, cleanProduct));
        DriveOffsetEntry? match = entries
            .Where(e => NormalizeInquiry(e.Name) == wanted)
            .OrderByDescending(e => e.Submissions)
            .FirstOrDefault();

        if (match != null)
        {
            return match;
        }

        // Some USB/virtual bridges omit INQUIRY vendor and place both vendor and product into ProductId.
        if (cleanVendor.Length == 0 && cleanProduct.Length > 0)
        {
            string wantedProductOnly = NormalizeInquiry(cleanProduct);
            return entries
                .Where(e => NormalizeInquiry(e.Name.Replace(" - ", " ", StringComparison.Ordinal)) == wantedProductOnly)
                .OrderByDescending(e => e.Submissions)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// The list from <paramref name="cachePath"/>, downloaded first when the copy is missing or older than
    /// <see cref="MaxCacheAge"/>. A failed download falls back to an older copy; with none, the list is empty. Only
    /// cancellation throws: an unknown read offset just means no write-offset correction.
    /// </summary>
    public static async Task<IReadOnlyList<DriveOffsetEntry>> LoadAsync(HttpClient? http, string cachePath, CancellationToken ct = default)
    {
        var result = await LoadDatabaseAsync(http, cachePath, forceRefresh: false, ct).ConfigureAwait(false);
        return result.Entries;
    }

    /// <summary>
    /// Loads <c>DriveOffsets.bin</c> from <paramref name="cachePath"/>, downloading when missing, older than
    /// <see cref="MaxCacheAge"/>, or <paramref name="forceRefresh"/> is true. Offline failures fall back gracefully
    /// to an existing stale cache or an empty list without throwing.
    /// </summary>
    public static async Task<DriveOffsetDatabaseLoadResult> LoadDatabaseAsync(
        HttpClient? http,
        string cachePath,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ct.ThrowIfCancellationRequested();

        bool exists = File.Exists(cachePath);
        DateTime? lastWriteUtc = exists ? File.GetLastWriteTimeUtc(cachePath) : null;
        bool fresh = exists && !forceRefresh && DateTime.UtcNow - lastWriteUtc!.Value < MaxCacheAge;
        string? downloadError = null;

        if (!fresh && http != null)
        {
            try
            {
                byte[] body = await http.GetByteArrayAsync(Url, ct).ConfigureAwait(false);
                IReadOnlyList<DriveOffsetEntry> parsed = body.Length >= RecordSize ? Parse(body) : [];
                if (parsed.Count > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    string temp = cachePath + ".tmp";
                    await File.WriteAllBytesAsync(temp, body, ct).ConfigureAwait(false);
                    File.Move(temp, cachePath, overwrite: true);
                    return new DriveOffsetDatabaseLoadResult(
                        parsed,
                        DriveOffsetDatabaseStatus.Downloaded,
                        File.GetLastWriteTimeUtc(cachePath));
                }

                downloadError = "Downloaded AccurateRip drive offset list was empty or invalid.";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException or UnauthorizedAccessException
                                           or OperationCanceledException or InvalidOperationException)
            {
                downloadError = ex.Message;
            }
        }

        try
        {
            if (File.Exists(cachePath))
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false);
                IReadOnlyList<DriveOffsetEntry> cached = Parse(cachedBytes);
                if (cached.Count > 0)
                {
                    var status = fresh ? DriveOffsetDatabaseStatus.FreshCache : DriveOffsetDatabaseStatus.StaleCacheFallback;
                    return new DriveOffsetDatabaseLoadResult(cached, status, File.GetLastWriteTimeUtc(cachePath), downloadError);
                }

                return new DriveOffsetDatabaseLoadResult(
                    [],
                    DriveOffsetDatabaseStatus.Unavailable,
                    lastWriteUtc,
                    downloadError ?? "Cached AccurateRip drive offset database is empty or invalid.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or UnauthorizedAccessException)
        {
            downloadError ??= ex.Message;
        }

        return new DriveOffsetDatabaseLoadResult([], DriveOffsetDatabaseStatus.Unavailable, lastWriteUtc, downloadError);
    }
}

/// <summary>Source used to resolve a drive's audio read offset.</summary>
public enum ReadOffsetSource
{
    ExplicitOverride,
    KeyDiscCalibration,
    AccurateRipDatabase,
    DefaultZero
}

/// <summary>Resolved audio read offset, human-readable badge/source, and any fallback warning.</summary>
public sealed record ReadOffsetResolution(
    int OffsetSamples,
    ReadOffsetSource Source,
    string BadgeText,
    string SourceDescription,
    string? Warning = null,
    int? SubmissionsOrConfidence = null,
    bool UsedStaleCache = false,
    DriveOffsetDatabaseStatus DatabaseStatus = DriveOffsetDatabaseStatus.Unavailable);

/// <summary>
/// Resolves a drive's audio read offset with strict precedence:
/// explicit user override &gt; local key-disc calibration &gt; cached AccurateRip <c>DriveOffsets.bin</c> match
/// (normalized INQUIRY vendor/product) &gt; 0 (with clear warning).
/// </summary>
public static class ReadOffsetResolver
{
    /// <summary>Formats an offset with an explicit sign, e.g. <c>+6</c>, <c>-12</c>, <c>0</c>.</summary>
    public static string FormatSignedSamples(int samples) => samples.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    public static async Task<ReadOffsetResolution> ResolveAsync(
        IDiscRecorderBackend recorder,
        int? explicitOverride = null,
        string? storePath = null,
        string? cachePath = null,
        HttpClient? http = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ct.ThrowIfCancellationRequested();

        if (explicitOverride is int manual && !forceRefresh)
        {
            return FromExplicitOverride(manual);
        }

        string driveName = AccurateRipDriveOffsets.DriveName(recorder.VendorId, recorder.ProductId);

        // Keep virtual drives isolated from the host user's real offset store and cache unless custom paths are given.
        DriveOffsetStore.CalibratedReadOffset? calibrated = null;
        if (!recorder.IsVirtual || storePath != null)
        {
            calibrated = new DriveOffsetStore(storePath ?? DriveOffsetStore.DefaultPath).GetCalibratedReadOffset(driveName);
        }

        if (explicitOverride is int manualAfterPrep && !forceRefresh)
        {
            return FromExplicitOverride(manualAfterPrep);
        }

        if (calibrated != null && !forceRefresh)
        {
            return FromKeyDiscCalibration(calibrated);
        }

        DriveOffsetDatabaseLoadResult dbResult;
        if (recorder.IsVirtual && cachePath == null)
        {
            dbResult = new DriveOffsetDatabaseLoadResult([], DriveOffsetDatabaseStatus.Unavailable, null);
        }
        else
        {
            dbResult = await AccurateRipDriveOffsets.LoadDatabaseAsync(
                http,
                cachePath ?? AccurateRipDriveOffsets.DefaultCachePath,
                forceRefresh,
                ct).ConfigureAwait(false);
        }

        return ResolveFromData(recorder.VendorId, recorder.ProductId, explicitOverride, calibrated, dbResult);
    }

    public static ReadOffsetResolution ResolveFromData(
        string? vendorId,
        string? productId,
        int? explicitOverride,
        DriveOffsetStore.CalibratedReadOffset? calibrated,
        DriveOffsetDatabaseLoadResult dbResult)
    {
        ArgumentNullException.ThrowIfNull(dbResult);

        if (explicitOverride is int manual)
        {
            return FromExplicitOverride(manual) with { DatabaseStatus = dbResult.Status };
        }

        if (calibrated != null)
        {
            return FromKeyDiscCalibration(calibrated) with { DatabaseStatus = dbResult.Status };
        }

        DriveOffsetEntry? match = AccurateRipDriveOffsets.Find(dbResult.Entries, vendorId, productId);
        if (match != null)
        {
            bool stale = dbResult.Status == DriveOffsetDatabaseStatus.StaleCacheFallback;
            string sourceDesc = stale
                ? $"AccurateRip DB ({match.Submissions} submissions, stale cache)"
                : $"AccurateRip DB ({match.Submissions} submissions)";
            return new ReadOffsetResolution(
                match.ReadOffsetSamples,
                ReadOffsetSource.AccurateRipDatabase,
                $"{FormatSignedSamples(match.ReadOffsetSamples)} samples via AccurateRip DB",
                sourceDesc,
                Warning: null,
                SubmissionsOrConfidence: match.Submissions,
                UsedStaleCache: stale,
                DatabaseStatus: dbResult.Status);
        }

        string driveLabel = AccurateRipDriveOffsets.DriveName(vendorId, productId).Trim(' ', '-');
        string warning;
        if (dbResult.Entries.Count == 0)
        {
            warning = string.IsNullOrWhiteSpace(dbResult.ErrorMessage)
                ? "Drive read offset is unknown (the AccurateRip drive offset cache is empty and no key-disc calibration was found); using 0 samples. Refresh the offset database or run key-disc offset detection for AccurateRip-accurate rips."
                : $"Drive read offset is unknown (AccurateRip drive offset download failed: {dbResult.ErrorMessage}, and no cached list or key-disc calibration exists); using 0 samples.";
        }
        else
        {
            string label = string.IsNullOrWhiteSpace(driveLabel) ? "this drive" : $"'{driveLabel}'";
            warning = $"Drive {label} was not found in the AccurateRip offset database and has no local key-disc calibration; using 0 samples. Run key-disc offset detection or enter the drive's read offset manually.";
        }

        return new ReadOffsetResolution(
            0,
            ReadOffsetSource.DefaultZero,
            "0 samples (uncalibrated)",
            "default 0 (uncalibrated)",
            Warning: warning,
            UsedStaleCache: dbResult.Status == DriveOffsetDatabaseStatus.StaleCacheFallback,
            DatabaseStatus: dbResult.Status);
    }

    private static ReadOffsetResolution FromExplicitOverride(int samples)
    {
        int clamped = Math.Clamp(samples, -10000, 10000);
        return new ReadOffsetResolution(
            clamped,
            ReadOffsetSource.ExplicitOverride,
            $"{FormatSignedSamples(clamped)} samples (manual override)",
            "manual override");
    }

    private static ReadOffsetResolution FromKeyDiscCalibration(DriveOffsetStore.CalibratedReadOffset calibrated)
    {
        string sourceDesc = calibrated.Confidence > 0
            ? $"key-disc calibration (confidence {calibrated.Confidence})"
            : "key-disc calibration";
        return new ReadOffsetResolution(
            calibrated.ReadOffsetSamples,
            ReadOffsetSource.KeyDiscCalibration,
            $"{FormatSignedSamples(calibrated.ReadOffsetSamples)} samples via key-disc calibration",
            sourceDesc,
            SubmissionsOrConfidence: calibrated.Confidence > 0 ? calibrated.Confidence : null);
    }
}

/// <summary>
/// The write offset of a drive and how it was arrived at. Read offset R: a ripper reads R samples further on to get
/// the true audio. Combined offset C: how many samples later the audio reads back than it was sent, as the read-back
/// check after a direct burn measures it (C = W + R). Write offset W = C − R: how many samples later the audio lands
/// on the disc than it was sent. Sending it −W samples later puts it where an offset-corrected rip expects it.
/// </summary>
public sealed record WriteOffsetPlan(int? ReadOffset, string? ReadOffsetSource, int? CombinedOffset, int? WriteOffset, string Explanation)
{
    /// <summary>Samples to move the audio later before it is sent (0 when the write offset is unknown).</summary>
    public int ShiftSamples => -(WriteOffset ?? 0);

    public static WriteOffsetPlan Resolve(int? readOffset, string? readOffsetSource, int? combinedOffset, int? explicitWriteOffset)
    {
        if (explicitWriteOffset is int given)
        {
            return new WriteOffsetPlan(readOffset, readOffsetSource, combinedOffset, given,
                $"Write offset {given:+0;-0;0} samples, as given.");
        }
        if (readOffset is null)
        {
            return new WriteOffsetPlan(null, null, combinedOffset, null,
                "The drive's read offset is not known, so its write offset cannot be worked out; the audio is written as it is.");
        }
        if (combinedOffset is null)
        {
            return new WriteOffsetPlan(readOffset, readOffsetSource, null, null,
                $"Read offset {readOffset:+0;-0;0} ({readOffsetSource}); this drive's combined offset is not measured yet, so this burn is not corrected. The read-back check measures it, and later burns on this drive are corrected.");
        }
        int write = combinedOffset.Value - readOffset.Value;
        return new WriteOffsetPlan(readOffset, readOffsetSource, combinedOffset, write,
            $"Write offset {write:+0;-0;0} samples (combined {combinedOffset:+0;-0;0} measured on earlier burns, read {readOffset:+0;-0;0} from {readOffsetSource}).");
    }
}

/// <summary>
/// Per-drive combined write/read offsets and local key-disc read-offset calibrations stored on this computer
/// (JSON, one entry per drive name). Several operations may run at once on different drives, so every update takes an
/// exclusive lock file and replaces the file whole.
/// </summary>
public sealed class DriveOffsetStore
{
    public sealed record CalibratedReadOffset(int ReadOffsetSamples, int Confidence, DateTime MeasuredUtc);

    private sealed record Entry(
        int? CombinedOffsetSamples,
        int Measurements,
        DateTime? LastMeasuredUtc,
        int? ReadOffsetSamples,
        int ReadOffsetConfidence,
        DateTime? ReadOffsetMeasuredUtc);

    private readonly string _path;

    public DriveOffsetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "drive-offsets.json");

    /// <summary>The last combined offset measured for the drive, or null.</summary>
    public int? GetCombinedOffset(string driveName) =>
        TryFindEntry(Load(), driveName)?.CombinedOffsetSamples;

    /// <summary>The locally calibrated read offset from a key disc for the drive, or null.</summary>
    public CalibratedReadOffset? GetCalibratedReadOffset(string driveName)
    {
        var entry = TryFindEntry(Load(), driveName);
        return entry?.ReadOffsetSamples is int samples
            ? new CalibratedReadOffset(samples, entry.ReadOffsetConfidence, entry.ReadOffsetMeasuredUtc ?? DateTime.MinValue)
            : null;
    }

    /// <summary>Records a combined offset measurement. Returns the value it replaced when that differs (worth a note), else null.</summary>
    public int? RecordCombinedOffset(string driveName, int samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var guard = AcquireLock();
        var all = Load();
        all.TryGetValue(driveName, out var old);
        all[driveName] = new Entry(
            samples,
            (old?.Measurements ?? 0) + 1,
            DateTime.UtcNow,
            old?.ReadOffsetSamples,
            old?.ReadOffsetConfidence ?? 0,
            old?.ReadOffsetMeasuredUtc);
        WriteLocked(all);
        return old?.CombinedOffsetSamples is int prev && prev != samples ? prev : null;
    }

    /// <summary>Records a key-disc read-offset calibration for the drive. Returns the previous value if it differed, else null.</summary>
    public int? RecordCalibratedReadOffset(string driveName, int samples, int confidence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var guard = AcquireLock();
        var all = Load();
        string storageKey = ResolveExistingKey(all, driveName) ?? driveName;
        all.TryGetValue(storageKey, out var old);
        all[storageKey] = new Entry(
            old?.CombinedOffsetSamples,
            old?.Measurements ?? 0,
            old?.LastMeasuredUtc,
            samples,
            confidence,
            DateTime.UtcNow);
        WriteLocked(all);
        return old?.ReadOffsetSamples is int prev && prev != samples ? prev : null;
    }

    private static Entry? TryFindEntry(Dictionary<string, Entry> all, string driveName)
    {
        if (string.IsNullOrWhiteSpace(driveName))
        {
            return null;
        }
        if (all.TryGetValue(driveName, out var exact))
        {
            return exact;
        }
        string? key = ResolveExistingKey(all, driveName);
        return key != null ? all[key] : null;
    }

    private static string? ResolveExistingKey(Dictionary<string, Entry> all, string driveName)
    {
        if (all.ContainsKey(driveName))
        {
            return driveName;
        }
        string normalized = AccurateRipDriveOffsets.NormalizeInquiry(driveName);
        if (normalized.Length == 0)
        {
            return null;
        }
        foreach (string key in all.Keys)
        {
            if (AccurateRipDriveOffsets.NormalizeInquiry(key) == normalized)
            {
                return key;
            }
        }
        return null;
    }

    private void WriteLocked(Dictionary<string, Entry> all)
    {
        // Written by hand (no reflection), so it works the same in trimmed or AOT builds.
        var root = new JsonObject();
        foreach (var (name, e) in all.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var obj = new JsonObject();
            if (e.CombinedOffsetSamples is int combined)
            {
                obj["combinedOffsetSamples"] = combined;
                obj["measurements"] = e.Measurements;
                if (e.LastMeasuredUtc is DateTime lastMeasured)
                {
                    obj["lastMeasuredUtc"] = lastMeasured.ToString("O", CultureInfo.InvariantCulture);
                }
            }
            if (e.ReadOffsetSamples is int readOffset)
            {
                obj["readOffsetSamples"] = readOffset;
                obj["readOffsetConfidence"] = e.ReadOffsetConfidence;
                if (e.ReadOffsetMeasuredUtc is DateTime readMeasured)
                {
                    obj["readOffsetMeasuredUtc"] = readMeasured.ToString("O", CultureInfo.InvariantCulture);
                }
            }
            root[name] = obj;
        }
        string temp = _path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    private Dictionary<string, Entry> Load()
    {
        var all = new Dictionary<string, Entry>();
        try
        {
            if (!File.Exists(_path) || JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root)
            {
                return all;
            }
            foreach (var (name, node) in root)
            {
                if (node is not JsonObject o)
                {
                    continue;
                }

                int? combined = o["combinedOffsetSamples"]?.GetValue<int>();
                DateTime? lastMeasured = null;
                if (DateTime.TryParse(o["lastMeasuredUtc"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime when))
                {
                    lastMeasured = when;
                }

                int? readOffset = o["readOffsetSamples"]?.GetValue<int>();
                DateTime? readMeasured = null;
                if (DateTime.TryParse(o["readOffsetMeasuredUtc"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime readWhen))
                {
                    readMeasured = readWhen;
                }

                if (combined != null || readOffset != null)
                {
                    all[name] = new Entry(
                        combined,
                        o["measurements"]?.GetValue<int>() ?? (combined != null ? 1 : 0),
                        lastMeasured,
                        readOffset,
                        o["readOffsetConfidence"]?.GetValue<int>() ?? 0,
                        readMeasured);
                }
            }
            return all;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return new(); // a damaged file only means measuring again
        }
    }

    private FileStream AcquireLock()
    {
        string lockPath = _path + ".lock";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }
}
