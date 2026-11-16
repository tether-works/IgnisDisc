// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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

    /// <summary>Reads the store; a missing file is an empty store. A damaged file is an error, never silently
    /// replaced, so saved calibrations are not lost by a later write.</summary>
    public static CalibrationStoreDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new CalibrationStoreDocument(CurrentSchemaVersion, []);
        if (new FileInfo(fullPath).Length > MaxFileBytes)
            throw new InvalidDataException($"The calibration store is too large: {fullPath}");
        CalibrationStoreDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CalibrationStoreDocument>(File.ReadAllText(fullPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The calibration store is not valid JSON: {fullPath} ({ex.Message})", ex);
        }

        if (document is null || document.Entries is null)
            throw new InvalidDataException($"The calibration store is empty or incomplete: {fullPath}");
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"The calibration store has schema version {document.SchemaVersion}; this version reads {CurrentSchemaVersion}: {fullPath}");
        foreach (CalibrationStoreEntry entry in document.Entries) Validate(entry, fullPath);
        return document;
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
    /// file through a temporary file so a crash cannot leave half a store. Two programs saving at the same moment
    /// are not coordinated: the later write wins.
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
        CalibrationStoreDocument current = Load(fullPath);
        List<CalibrationStoreEntry> entries = current.Entries
            .Where(existing => !(SameKey(existing, normalized.DriveModel, normalized.MediaId)
                && existing.Parameters.Kind == normalized.Parameters.Kind))
            .ToList();
        entries.Add(normalized);
        CalibrationStoreDocument updated = new(CurrentSchemaVersion, entries);

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return updated;
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
