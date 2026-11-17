using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Lithography.Calibration;

namespace CdBurner.Lithography.DiscArt;

/// <summary>
/// The <c>&lt;image&gt;.disc-art.json</c> file written next to a picture-disc image. It holds everything the read-back
/// preview needs (disc parameters, tone, start angle, sector plan), so a preview never depends on other inputs.
/// </summary>
internal sealed record DiscArtSidecar(
    string Format,
    DiscArtMedium Medium,
    string PicturePath,
    CalibrationParameters Parameters,
    ToneProfile Tone,
    double StartAngleDegrees,
    bool ReadCompatibility,
    bool CdInterleave,
    string? FilesPlacement,
    string? VolumeLabel,
    DiscArtPlan Plan,
    DateTimeOffset CreatedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiscArtTransform? Transform = null)
{
    public const string FormatId = "ignisdisc-disc-art-v1";
    public const string Suffix = ".disc-art.json";

    private static readonly JsonSerializerOptions JsonOptions = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string PathFor(string imagePath) => Path.GetFullPath(imagePath) + Suffix;

    public static void Write(string path, DiscArtSidecar sidecar)
        => File.WriteAllText(path, JsonSerializer.Serialize(sidecar, JsonOptions));

    /// <summary>Reads the sidecar of <paramref name="imagePath"/>; explains in plain words when it is missing or unusable.</summary>
    public static DiscArtSidecar Read(string imagePath)
    {
        string path = PathFor(imagePath);
        if (!File.Exists(path))
        {
            throw new ArgumentException(
                $"There is no picture-disc description next to the image ({Path.GetFileName(path)}). "
                + "A preview only works for an image made by 'disc-art generate'.");
        }

        DiscArtSidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<DiscArtSidecar>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is not readable: {ex.Message}");
        }

        if (sidecar is null || !string.Equals(sidecar.Format, FormatId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is not a picture-disc description (expected format {FormatId}).");
        }

        return sidecar;
    }
}
