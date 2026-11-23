using System.Text.Json;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Iso;

/// <summary>
/// Reads the <c>&lt;image&gt;.disc-art.json</c> sidecar written next to a picture-disc image without referencing
/// the lithography assembly, so burn and CUE-import services know which sectors hold a surface picture.
/// </summary>
public static class DiscArtSidecarInfo
{
    public const string FormatId = "ignisdisc-disc-art-v1";
    public const string Suffix = ".disc-art.json";

    /// <summary>
    /// Reads the picture zone of a generated ISO image from its <c>.disc-art.json</c> sidecar. When the image holds
    /// no files (<c>plan.files == null</c>), the whole image is treated as the picture zone so empty padding before a
    /// band is not read back as files; when files are present, only <c>[pictureStartLba, pictureEndLba)</c> is excluded.
    /// </summary>
    public static bool TryReadPictureZone(string imagePath, out SectorRange pictureZone, out bool hasFiles)
    {
        pictureZone = default;
        hasFiles = false;
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        string sidecarPath = Path.GetFullPath(imagePath) + Suffix;
        if (!File.Exists(sidecarPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            JsonElement root = doc.RootElement;
            if (!IsValidSidecar(root, out JsonElement plan))
            {
                return false;
            }

            if (!TryGetInt32(plan, "totalSectors", out int totalSectors) || totalSectors <= 0
                || !TryGetInt32(plan, "pictureStartLba", out int picStart) || picStart < 0
                || !TryGetInt32(plan, "pictureEndLba", out int picEnd) || picEnd <= picStart || picEnd > totalSectors)
            {
                return false;
            }

            hasFiles = (plan.TryGetProperty("filesLayout", out JsonElement filesLayout) && filesLayout.ValueKind == JsonValueKind.Object)
                || (plan.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Object);
            pictureZone = hasFiles
                ? new SectorRange(picStart, picEnd - picStart)
                : new SectorRange(0, totalSectors);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="audioPath"/> has a <c>.disc-art.json</c> sidecar for a CD picture track.</summary>
    public static bool IsPictureTrack(string audioPath)
    {
        if (!TryOpenSidecar(audioPath, out JsonDocument? doc, out JsonElement root, out JsonElement plan))
        {
            return false;
        }

        using (doc)
        {
            if (root.TryGetProperty("medium", out JsonElement medium)
                && medium.ValueKind == JsonValueKind.String
                && !string.Equals(medium.GetString(), "cd", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool hasPreGap = plan.TryGetProperty("cdPreGapSectors", out JsonElement preGap)
                && preGap.ValueKind == JsonValueKind.Number
                && preGap.TryGetInt64(out long sectors)
                && sectors > 0;
            return !hasPreGap;
        }
    }

    /// <summary>True when <paramref name="audioPath"/> has a <c>.disc-art.json</c> sidecar for a track 1 pre-gap picture.</summary>
    public static bool IsPreGapPicture(string audioPath)
    {
        if (!TryOpenSidecar(audioPath, out JsonDocument? doc, out _, out JsonElement plan))
        {
            return false;
        }

        using (doc)
        {
            return plan.TryGetProperty("cdPreGapSectors", out JsonElement preGap)
                && preGap.ValueKind == JsonValueKind.Number
                && preGap.TryGetInt64(out long sectors)
                && sectors > 0;
        }
    }

    private static bool TryOpenSidecar(string path, out JsonDocument? doc, out JsonElement root, out JsonElement plan)
    {
        doc = null;
        root = default;
        plan = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string sidecarPath = Path.GetFullPath(path) + Suffix;
        if (!File.Exists(sidecarPath))
        {
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            root = doc.RootElement;
            if (!IsValidSidecar(root, out plan))
            {
                doc.Dispose();
                doc = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            doc?.Dispose();
            doc = null;
            return false;
        }
    }

    private static bool IsValidSidecar(JsonElement root, out JsonElement plan)
    {
        plan = default;
        return root.TryGetProperty("format", out JsonElement format)
            && string.Equals(format.GetString(), FormatId, StringComparison.Ordinal)
            && root.TryGetProperty("plan", out plan)
            && plan.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetInt32(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out JsonElement prop)
            || prop.ValueKind != JsonValueKind.Number
            || !prop.TryGetInt64(out long raw)
            || raw < int.MinValue || raw > int.MaxValue)
        {
            return false;
        }

        value = (int)raw;
        return true;
    }
}
