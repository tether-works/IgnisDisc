// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdBurner.Lithography.Calibration;

/// <summary>
/// One saved calibration: the fitted parameters for one drive model writing one kind of blank disc.
/// <see cref="MediaId"/> is the disc's manufacturer code (ATIP on CD-R/RW, ADIP or pre-pit media ID on DVD, disc ID
/// on BD-R) as IgnisDisc's media information shows it.
/// </summary>
public sealed record CalibrationStoreEntry(
    string DriveModel, string MediaId, CalibrationParameters Parameters, DateTimeOffset SavedUtc,
    string TargetId, string Stage, double RmsErrorMm);

/// <summary>The stored file: a version number and the entries, newest write last.</summary>
public sealed record CalibrationStoreDocument(int SchemaVersion, IReadOnlyList<CalibrationStoreEntry> Entries);

/// <summary>
/// Calibration results kept per drive model and media ID in one JSON file
/// (<c>%APPDATA%\IgnisDisc\disc-calibrations.json</c> by default). The pair is the key because track geometry is a
/// property of the blank disc and the drive that writes it together; drive model and media ID are compared without
/// regard to case or surrounding spaces.
/// </summary>
public static class CalibrationStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "disc-calibrations.json";
    public const int MaxKeyLength = 128;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string GetDefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IgnisDisc", FileName);

    /// <summary>Trims a drive model or media ID and rejects text that cannot be a key.</summary>
    public static string NormalizeKey(string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException($"The {what} is empty.");
        if (trimmed.Length > MaxKeyLength)
            throw new ArgumentException($"The {what} is longer than {MaxKeyLength} characters.");
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException($"The {what} contains control characters.");
        return trimmed;
    }

    /// <summary>Reads the store; a missing file is an empty store. Unknown properties and individual malformed entries
    /// are skipped so valid entries remain usable; if the primary file is unreadable, a <c>.bak</c> copy is used if valid.</summary>
    public static CalibrationStoreDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        lock (GetPathLock(fullPath))
        {
            return LoadCore(fullPath);
        }
    }

    private static CalibrationStoreDocument LoadCore(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return new CalibrationStoreDocument(CurrentSchemaVersion, []);
        }

        try
        {
            return LoadFromSingleFile(fullPath);
        }
        catch (InvalidDataException primaryError)
        {
            string backupPath = fullPath + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    CalibrationStoreDocument recovered = LoadFromSingleFile(backupPath);
                    Trace.TraceWarning($"Calibration store '{fullPath}' was damaged ({primaryError.Message}); recovered from '{backupPath}'.");
                    return recovered;
                }
                catch (Exception bakEx) when (bakEx is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"Calibration store '{fullPath}' and backup '{backupPath}' could not be read: {bakEx.Message}");
                }
            }

            throw;
        }
    }

    private static CalibrationStoreDocument LoadFromSingleFile(string filePath)
    {
        string json;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length > MaxFileBytes)
                throw new InvalidDataException($"The calibration store is too large: {filePath}");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            json = reader.ReadToEnd();
        }

        using JsonDocument doc = ParseJsonDocument(json, filePath);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The calibration store is empty or incomplete: {filePath}");

        if (!root.TryGetProperty("schemaVersion", out JsonElement versionEl)
            || versionEl.ValueKind != JsonValueKind.Number
            || !versionEl.TryGetInt32(out int schemaVersion))
        {
            throw new InvalidDataException($"The calibration store is empty or incomplete: {filePath}");
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The calibration store has schema version {schemaVersion}; this version reads {CurrentSchemaVersion}: {filePath}");
        }

        if (!root.TryGetProperty("entries", out JsonElement entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The calibration store is empty or incomplete: {filePath}");

        var validEntries = new List<CalibrationStoreEntry>();
        foreach (JsonElement element in entriesEl.EnumerateArray())
        {
            try
            {
                CalibrationStoreEntry? entry = element.Deserialize<CalibrationStoreEntry>(JsonOptions);
                if (entry is null)
                {
                    Trace.TraceWarning($"Skipped null calibration entry in '{filePath}'.");
                    continue;
                }

                Validate(entry, filePath);
                validEntries.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                Trace.TraceWarning($"Skipped invalid calibration entry in '{filePath}': {ex.Message}");
            }
        }

        return new CalibrationStoreDocument(CurrentSchemaVersion, validEntries);
    }

    private static JsonDocument ParseJsonDocument(string json, string filePath)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The calibration store is not valid JSON: {filePath} ({ex.Message})", ex);
        }
    }

    /// <summary>The saved calibration for a drive model and media ID, or null.</summary>
    public static CalibrationStoreEntry? Find(CalibrationStoreDocument document, string driveModel, string mediaId,
        CalibrationDiscKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        string drive = NormalizeKey(driveModel, "drive model");
        string media = NormalizeKey(mediaId, "media ID");
        return document.Entries.LastOrDefault(entry => SameKey(entry, drive, media)
            && (kind is null || entry.Parameters.Kind == kind));
    }

    /// <summary>
    /// Adds the entry, replacing an earlier one for the same drive model, media ID and disc type, and writes the
    /// file atomically through a temporary file with a <c>.bak</c> backup. Concurrent calls on the same store path
    /// are serialized so updates do not overwrite each other.
    /// </summary>
    public static CalibrationStoreDocument Save(string path, CalibrationStoreEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entry);
        string fullPath = Path.GetFullPath(path);
        CalibrationStoreEntry normalized = entry with
        {
            DriveModel = NormalizeKey(entry.DriveModel, "drive model"),
            MediaId = NormalizeKey(entry.MediaId, "media ID"),
        };
        Validate(normalized, fullPath);

        lock (GetPathLock(fullPath))
        {
            CalibrationStoreDocument current = LoadCore(fullPath);
            List<CalibrationStoreEntry> entries = current.Entries
                .Where(existing => !(SameKey(existing, normalized.DriveModel, normalized.MediaId)
                    && existing.Parameters.Kind == normalized.Parameters.Kind))
                .ToList();
            entries.Add(normalized);
            CalibrationStoreDocument updated = new(CurrentSchemaVersion, entries);

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(updated, JsonOptions);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string bakTemp = fullPath + "." + Guid.NewGuid().ToString("N") + ".bak.tmp";
            try
            {
                WriteAtomicText(temporary, json);
                File.Move(temporary, fullPath, overwrite: true);
                TryWriteBackup(fullPath, bakTemp, json);
            }
            finally
            {
                TryDeleteFile(temporary);
                TryDeleteFile(bakTemp);
            }

            return updated;
        }
    }

    private static object GetPathLock(string fullPath)
        => PathLocks.GetOrAdd(fullPath, static _ => new object());

    private static void WriteAtomicText(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void TryWriteBackup(string fullPath, string bakTemp, string json)
    {
        try
        {
            WriteAtomicText(bakTemp, json);
            File.Move(bakTemp, fullPath + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not update calibration store backup '{fullPath}.bak': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool SameKey(CalibrationStoreEntry entry, string driveModel, string mediaId)
        => string.Equals(entry.DriveModel?.Trim(), driveModel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.MediaId?.Trim(), mediaId, StringComparison.OrdinalIgnoreCase);

    private static void Validate(CalibrationStoreEntry entry, string path)
    {
        if (entry is null || entry.Parameters is null || entry.DriveModel is null || entry.MediaId is null
            || entry.TargetId is null || entry.Stage is null
            || !double.IsFinite(entry.RmsErrorMm) || entry.RmsErrorMm < 0)
            throw new InvalidDataException($"The calibration store holds an incomplete entry: {path}");
        try
        {
            NormalizeKey(entry.DriveModel, "drive model");
            NormalizeKey(entry.MediaId, "media ID");
            entry.Parameters.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"The calibration store holds an invalid entry: {path} ({ex.Message})", ex);
        }
    }
}
