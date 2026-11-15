// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Core/DiscPresetJsonStore.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Core/DiscPresetJsonStore.cs
// IgnisDisc change: namespace CdBurner.Lithography.Presets, English diagnostic messages, %APPDATA%\IgnisDisc default path, Stream overload, and pitch validation via DiscPresetGeometry. See THIRD-PARTY-NOTICES.md.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Lithography.Geometry;

namespace CdBurner.Lithography.Presets;

public sealed record CdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double LinearVelocityMmPerSecond = 1_200,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? UsableSectors = null);

public sealed record DvdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm = 133.3,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    uint? UsableSectors = null);

public sealed record BluRayDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    bool InvertMapping = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Contributor = null,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    uint? UsableSectors = null);

public sealed record DiscPresetJsonDocument(
    int SchemaVersion,
    IReadOnlyList<CdDiscPresetDefinition> CdPresets,
    IReadOnlyList<DvdDiscPresetDefinition> DvdPresets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledCdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledDvdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FallbackLanguage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BluRayDiscPresetDefinition>? BluRayPresets = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledBluRayPresetIds = null);

public static class DiscPresetJsonStore
{
    public const int CurrentSchemaVersion = 4;

    /// <summary>Schema 3 was written by a build that dropped <c>linearVelocityMmPerSecond</c>
    /// and <c>channelBitLengthNm</c> from the preset shape. Schema 2 predates the BD catalog.
    /// Both stay readable so a version bump cannot strand presets a user already saved.
    /// Schema 4 adds <c>bluRayPresets</c>.</summary>
    public const int InterimSchemaVersion = 3;
    public const int LegacySchemaVersion = 2;

    public const string FileName = "disc-presets.json";
    public const int BluRaySectorsPerCluster = 32;
    public const int BluRaySectorsPerLayer = 12_219_392;

    private const string BuiltInResourceName = "CdBurner.Lithography.Presets.BuiltInDiscPresets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string GetDefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IgnisDisc",
            FileName);

    public static DiscPresetJsonDocument LoadBuiltIn()
    {
        using Stream stream = typeof(DiscPresetJsonStore).Assembly
            .GetManifestResourceStream(BuiltInResourceName)
            ?? throw new InvalidOperationException("Built-in disc presets embedded resource is missing.");
        return Deserialize(stream, "built-in disc presets");
    }

    public static DiscPresetJsonDocument CreateInitialUserDocument()
        => new(
            CurrentSchemaVersion,
            [
                new CdDiscPresetDefinition(
                    "user-custom-cd",
                    "Custom CD",
                    "User preset; edit or duplicate to add custom CD parameters.",
                    359_849,
                    24.5,
                    56.8,
                    1_200),
            ],
            [
                new DvdDiscPresetDefinition(
                    "user-custom-dvd",
                    "Custom DVD",
                    "User preset; edit or duplicate to add custom DVD parameters.",
                    2_295_104,
                    24.0,
                    58.0,
                    133.3),
            ],
            DisabledCdPresetIds: [],
            DisabledDvdPresetIds: [],
            BluRayPresets:
            [
                new BluRayDiscPresetDefinition(
                    "user-custom-bd",
                    "Custom Blu-ray",
                    "User preset; edit or duplicate to add custom Blu-ray parameters.",
                    12_219_392,
                    24.0,
                    58.0),
            ],
            DisabledBluRayPresetIds: []);

    public static DiscPresetJsonDocument LoadOrCreate(
        string path,
        DiscPresetJsonDocument initialUserDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialUserDocument);

        if (!File.Exists(path))
        {
            Save(path, initialUserDocument);
        }

        return Load(path);
    }

    public static DiscPresetJsonDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Deserialize(stream, Path.GetFileName(path));
    }

    public static DiscPresetJsonDocument Load(Stream stream, string sourceName = FileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        return Deserialize(stream, sourceName);
    }

    public static DiscPresetJsonDocument Merge(
        DiscPresetJsonDocument builtIn,
        DiscPresetJsonDocument user)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(user);
        Validate(builtIn);
        Validate(user);

        HashSet<string> disabledCdIds = new(
            user.DisabledCdPresetIds ?? [],
            StringComparer.Ordinal);
        HashSet<string> disabledDvdIds = new(
            user.DisabledDvdPresetIds ?? [],
            StringComparer.Ordinal);
        HashSet<string> disabledBluRayIds = new(
            user.DisabledBluRayPresetIds ?? [],
            StringComparer.Ordinal);
        IReadOnlyList<BluRayDiscPresetDefinition> builtInBluRay = builtIn.BluRayPresets ?? [];
        IReadOnlyList<BluRayDiscPresetDefinition> userBluRay = user.BluRayPresets ?? [];
        DiscPresetJsonDocument merged = new(
            CurrentSchemaVersion,
            MergePresets(
                builtIn.CdPresets,
                user.CdPresets,
                disabledCdIds,
                value => value.Id),
            MergePresets(
                builtIn.DvdPresets,
                user.DvdPresets,
                disabledDvdIds,
                value => value.Id),
            FallbackLanguage: builtIn.FallbackLanguage,
            BluRayPresets: MergePresets(
                builtInBluRay,
                userBluRay,
                disabledBluRayIds,
                value => value.Id),
            DisabledBluRayPresetIds: null);
        Validate(merged);
        return merged;
    }

    public static void Save(string path, DiscPresetJsonDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        // Older schemas are migrated on write; the BD catalog starts empty until merged
        // with the built-in presets on the next load.
        DiscPresetJsonDocument migrated = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            BluRayPresets = document.BluRayPresets ?? [],
        };
        Validate(migrated);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Preset JSON path must include a directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, migrated, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(DiscPresetJsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is not (LegacySchemaVersion or InterimSchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported schemaVersion={document.SchemaVersion}; supported versions are {LegacySchemaVersion}, {InterimSchemaVersion}, and {CurrentSchemaVersion}.");
        }

        if (document.SchemaVersion == CurrentSchemaVersion && document.BluRayPresets is null)
        {
            throw new InvalidDataException("schemaVersion 4 must include a bluRayPresets list.");
        }

        if (document.CdPresets is null)
        {
            throw new InvalidDataException("cdPresets cannot be null.");
        }

        if (document.DvdPresets is null)
        {
            throw new InvalidDataException("dvdPresets cannot be null.");
        }

        ValidateUniqueIds(document.CdPresets.Select(value => value?.Id), "CD");
        ValidateUniqueIds(document.DvdPresets.Select(value => value?.Id), "DVD");
        ValidateUniqueIds((document.BluRayPresets ?? []).Select(value => value?.Id), "Blu-ray");
        ValidateUniqueIds(document.DisabledCdPresetIds ?? [], "disabled CD");
        ValidateUniqueIds(document.DisabledDvdPresetIds ?? [], "disabled DVD");
        ValidateUniqueIds(document.DisabledBluRayPresetIds ?? [], "disabled Blu-ray");
        if (document.FallbackLanguage is not null)
        {
            ValidateText(document.FallbackLanguage, "document", "fallbackLanguage");
        }

        foreach (string id in document.DisabledCdPresetIds ?? [])
        {
            ValidateText(id, "disabled CD", "id");
        }

        foreach (string id in document.DisabledDvdPresetIds ?? [])
        {
            ValidateText(id, "disabled DVD", "id");
        }

        foreach (string id in document.DisabledBluRayPresetIds ?? [])
        {
            ValidateText(id, "disabled Blu-ray", "id");
        }

        foreach (CdDiscPresetDefinition? preset in document.CdPresets)
        {
            if (preset is null)
            {
                throw new InvalidDataException("CD preset cannot be null.");
            }

            ValidateText(preset.Id, "CD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(preset.DisplayNameResourceKey, preset.Id, "displayNameResourceKey");
            ValidateOptionalText(preset.DescriptionResourceKey, preset.Id, "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "CD");
            if (preset.Sectors <= 0)
            {
                throw InvalidValue(preset.Id, "sectors", "must be greater than 0");
            }

            ValidateUsableSectors(preset.Id, preset.Sectors, preset.UsableSectors, 1);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            ValidatePositiveFinite(preset.Id, "linearVelocityMmPerSecond", preset.LinearVelocityMmPerSecond);
        }

        foreach (DvdDiscPresetDefinition? preset in document.DvdPresets)
        {
            if (preset is null)
            {
                throw new InvalidDataException("DVD preset cannot be null.");
            }

            ValidateText(preset.Id, "DVD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(preset.DisplayNameResourceKey, preset.Id, "displayNameResourceKey");
            ValidateOptionalText(preset.DescriptionResourceKey, preset.Id, "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "DVD");
            if (preset.TotalSectors == 0)
            {
                throw InvalidValue(preset.Id, "totalSectors", "must be greater than 0");
            }

            ValidateUsableSectors(preset.Id, preset.TotalSectors, preset.UsableSectors, 16);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            ValidatePositiveFinite(preset.Id, "channelBitLengthNm", preset.ChannelBitLengthNm);
        }

        foreach (BluRayDiscPresetDefinition? preset in document.BluRayPresets ?? [])
        {
            if (preset is null)
            {
                throw new InvalidDataException("Blu-ray preset cannot be null.");
            }

            ValidateText(preset.Id, "Blu-ray", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(preset.DisplayNameResourceKey, preset.Id, "displayNameResourceKey");
            ValidateOptionalText(preset.DescriptionResourceKey, preset.Id, "descriptionResourceKey");
            ValidateOptionalText(preset.Contributor, preset.Id, "contributor");
            ValidateReservedId(preset.Id, "Blu-ray");
            if (preset.TotalSectors == 0
                || preset.TotalSectors > BluRaySectorsPerLayer
                || preset.TotalSectors % BluRaySectorsPerCluster != 0)
            {
                throw InvalidValue(
                    preset.Id,
                    "totalSectors",
                    $"must be a multiple of {BluRaySectorsPerCluster} and not exceed {BluRaySectorsPerLayer:N0}");
            }

            ValidateUsableSectors(preset.Id, preset.TotalSectors, preset.UsableSectors, BluRaySectorsPerCluster);
            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePitch(preset.Id, preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            if (preset.OuterRadiusMm > 60)
            {
                throw InvalidValue(preset.Id, "outerRadiusMm", "must not exceed 60");
            }
        }
    }

    private static void ValidateUsableSectors(string presetId, long total, long? usable, int alignment)
    {
        if (usable is { } value && (value <= 0 || value > total || value % alignment != 0))
        {
            throw InvalidValue(
                presetId,
                "usableSectors",
                $"must be greater than 0, not exceed total sectors, and be a multiple of {alignment}");
        }
    }

    private static DiscPresetJsonDocument Deserialize(Stream stream, string sourceName)
    {
        try
        {
            DiscPresetJsonDocument? document = JsonSerializer.Deserialize<DiscPresetJsonDocument>(
                stream,
                JsonOptions);
            if (document is null)
            {
                throw new InvalidDataException($"{sourceName} cannot be empty.");
            }

            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{sourceName} is malformed (line {exception.LineNumber + 1}, column {exception.BytePositionInLine + 1}): {exception.Message}",
                exception);
        }
    }

    private static IReadOnlyList<TPreset> MergePresets<TPreset>(
        IReadOnlyList<TPreset> builtIn,
        IReadOnlyList<TPreset> user,
        IReadOnlySet<string> disabledIds,
        Func<TPreset, string> getId)
    {
        Dictionary<string, TPreset> userById = user.ToDictionary(getId, StringComparer.Ordinal);
        HashSet<string> builtInIds = new(builtIn.Select(getId), StringComparer.Ordinal);
        List<TPreset> merged = [];

        foreach (TPreset preset in builtIn)
        {
            string id = getId(preset);
            if (!disabledIds.Contains(id))
            {
                merged.Add(userById.TryGetValue(id, out TPreset? replacement) ? replacement : preset);
            }
        }

        foreach (TPreset preset in user)
        {
            string id = getId(preset);
            if (!builtInIds.Contains(id) && !disabledIds.Contains(id))
            {
                merged.Add(preset);
            }
        }

        return merged;
    }

    private static void ValidateUniqueIds(IEnumerable<string?> ids, string family)
    {
        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (string? id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && !uniqueIds.Add(id))
            {
                throw new InvalidDataException($"Duplicate {family} preset id '{id}'.");
            }
        }
    }

    private static void ValidateReservedId(string id, string family)
    {
        if (string.Equals(id, "__manual__", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{family} preset id '__manual__' is reserved for unsaved manual state.");
        }
    }

    private static void ValidatePitch(string id, double linear, double quadratic, double cubic)
    {
        try
        {
            DiscPresetGeometry.ValidateNormalizedPitchCoefficients(linear, quadratic, cubic);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Preset '{id}' has invalid pitch coefficients: {exception.Message}", exception);
        }
    }

    private static void ValidateText(string? value, string presetId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidValue(presetId, fieldName, "cannot be empty or whitespace");
        }
    }

    private static void ValidateOptionalText(string? value, string presetId, string fieldName)
    {
        if (value is not null)
        {
            ValidateText(value, presetId, fieldName);
        }
    }

    private static void ValidateRadiusRange(
        string presetId,
        double innerRadius,
        double outerRadius)
    {
        ValidatePositiveFinite(presetId, "innerRadiusMm", innerRadius);
        ValidatePositiveFinite(presetId, "outerRadiusMm", outerRadius);
        if (outerRadius <= innerRadius)
        {
            throw InvalidValue(presetId, "outerRadiusMm", "must be greater than innerRadiusMm");
        }
    }

    private static void ValidatePositiveFinite(string presetId, string fieldName, double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw InvalidValue(presetId, fieldName, "must be a finite number greater than 0");
        }
    }

    private static InvalidDataException InvalidValue(
        string presetId,
        string fieldName,
        string requirement)
        => new($"Preset '{presetId}' field '{fieldName}' {requirement}.");
}
