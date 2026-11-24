using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CdBurner.Lithography.Rosetta;

/// <summary>Physical cell dimensions in micrometres for Rosetta optical text pages.</summary>
public readonly record struct RosettaCellSize(double WidthUm, double HeightUm)
{
    public double WidthMeters => WidthUm * 1.0e-6;
    public double HeightMeters => HeightUm * 1.0e-6;
    public double AreaUm2 => WidthUm * HeightUm;
    public double SpleenPixelWidthUm => WidthUm / 5.0;
    public double SpleenPixelHeightUm => HeightUm / 8.0;
    public double UnifontPixelWidthUm => WidthUm / 8.0;
    public double UnifontPixelHeightUm => HeightUm / 16.0;
    public bool IsCdCompatible => WidthUm >= 50.0 && HeightUm >= 75.0;
}

/// <summary>Page grid and gutter geometry for one Rosetta text preset or ladder rung.</summary>
public sealed record RosettaPagePreset(
    string Id,
    string Name,
    string TargetMedia,
    RosettaCellSize CellSize,
    int Columns,
    int Rows,
    double RadialGutterUm,
    double TangentialGutterUm,
    bool CdSupported)
{
    public const int MinColumns = 10;
    public const int MaxColumns = 16384; // 2 * MaxColumns <= ushort.MaxValue so LineSlice.CharLength never overflows.
    public const int MinRows = 8;
    public const int MaxRows = 16384;    // Rows - 1 <= ushort.MaxValue so PageEntry.LineCount never overflows.

    private readonly int _columns = ValidateColumns(Columns);
    private readonly int _rows = ValidateRows(Rows);

    public int Columns
    {
        get => _columns;
        init => _columns = ValidateColumns(value);
    }

    public int Rows
    {
        get => _rows;
        init => _rows = ValidateRows(value);
    }

    private static int ValidateColumns(int columns)
        => columns is >= MinColumns and <= MaxColumns
            ? columns
            : throw new ArgumentOutOfRangeException(nameof(Columns), columns,
                $"Rosetta preset columns ({columns}) must be between {MinColumns} and {MaxColumns}.");

    private static int ValidateRows(int rows)
        => rows is >= MinRows and <= MaxRows
            ? rows
            : throw new ArgumentOutOfRangeException(nameof(Rows), rows,
                $"Rosetta preset rows ({rows}) must be between {MinRows} and {MaxRows}.");

    // 3 base pixels (cellHeight / 8) between header row 0 and body row 0:
    // 1 blank pixel + 1-pixel horizontal rule + 1 blank pixel.
    public double RuleBandHeightMeters => 3.0 * (CellSize.HeightMeters / 8.0);
    public double PageTangentialWidthMeters => Columns * CellSize.WidthMeters;
    public double PageRadialHeightMeters => (Rows * CellSize.HeightMeters) + RuleBandHeightMeters;
    public double RadialGutterMeters => RadialGutterUm * 1.0e-6;
    public double TangentialGutterMeters => TangentialGutterUm * 1.0e-6;
    public int BodyRows => Rows - 1;

    /// <summary>Creates a <see cref="Geometry.PolarPageLayout"/> covering the specified radius band in meters.</summary>
    public Geometry.PolarPageLayout CreateLayout(double innerRadiusMeters, double outerRadiusMeters)
        => new(
            innerRadiusMeters,
            outerRadiusMeters,
            PageRadialHeightMeters,
            PageTangentialWidthMeters,
            RadialGutterMeters,
            TangentialGutterMeters,
            Columns,
            Rows);
}

/// <summary>Catalog of Rosetta cell-size presets and DVD resolution ladder rungs loaded from JSON data.</summary>
public sealed class RosettaPresetCatalog
{
    private const string EmbeddedResourceName = "CdBurner.Lithography.Rosetta.Presets.rosetta-cell-presets.json";
    private static readonly Lazy<RosettaPresetCatalog> BuiltInLazy = new(LoadBuiltIn);

    private RosettaPresetCatalog(
        IReadOnlyList<RosettaPagePreset> presets,
        IReadOnlyList<RosettaPagePreset> ladder)
    {
        Presets = presets;
        Ladder = ladder;
        Hobbyist = FindByIdOrName(presets, "hobbyist")
            ?? throw new InvalidDataException("Rosetta preset catalog is missing the 'hobbyist' preset.");
        HighDensity = FindByIdOrName(presets, "high-density")
            ?? throw new InvalidDataException("Rosetta preset catalog is missing the 'high-density' preset.");
    }

    public static RosettaPresetCatalog BuiltIn => BuiltInLazy.Value;

    public IReadOnlyList<RosettaPagePreset> Presets { get; }
    public IReadOnlyList<RosettaPagePreset> Ladder { get; }
    public RosettaPagePreset Hobbyist { get; }
    public RosettaPagePreset HighDensity { get; }

    /// <summary>Finds a preset or ladder rung by id or display name (case-insensitive).</summary>
    public RosettaPagePreset GetPreset(string idOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrName);
        return FindByIdOrName(Presets, idOrName)
            ?? FindByIdOrName(Ladder, idOrName)
            ?? throw new KeyNotFoundException($"Unknown Rosetta preset '{idOrName}'.");
    }

    /// <summary>Loads and validates a preset catalog from a UTF-8 JSON stream.</summary>
    public static RosettaPresetCatalog Load(Stream utf8JsonStream)
    {
        ArgumentNullException.ThrowIfNull(utf8JsonStream);
        using JsonDocument doc = JsonDocument.Parse(utf8JsonStream);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Rosetta preset catalog root must be a JSON object.");
        }

        if (!root.TryGetProperty("schemaVersion", out JsonElement versionEl)
            || !versionEl.TryGetInt32(out int schemaVersion)
            || schemaVersion != 1)
        {
            throw new InvalidDataException("Rosetta preset catalog must specify schemaVersion 1.");
        }

        if (!root.TryGetProperty("presets", out JsonElement presetsEl) || presetsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Rosetta preset catalog must contain a 'presets' array.");
        }
        if (!root.TryGetProperty("ladder", out JsonElement ladderEl) || ladderEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Rosetta preset catalog must contain a 'ladder' array.");
        }

        var presets = new List<RosettaPagePreset>();
        foreach (JsonElement item in presetsEl.EnumerateArray())
        {
            presets.Add(ParsePreset(item, isLadderRung: false));
        }

        var ladder = new List<RosettaPagePreset>();
        foreach (JsonElement item in ladderEl.EnumerateArray())
        {
            ladder.Add(ParsePreset(item, isLadderRung: true));
        }

        if (presets.Count == 0)
        {
            throw new InvalidDataException("Rosetta preset catalog 'presets' array must not be empty.");
        }
        if (ladder.Count == 0)
        {
            throw new InvalidDataException("Rosetta preset catalog 'ladder' array must not be empty.");
        }

        return new RosettaPresetCatalog(presets, ladder);
    }

    private static RosettaPresetCatalog LoadBuiltIn()
    {
        Assembly assembly = typeof(RosettaPresetCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{EmbeddedResourceName}'.");
        return Load(stream);
    }

    private static RosettaPagePreset ParsePreset(JsonElement item, bool isLadderRung)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each Rosetta preset entry must be a JSON object.");
        }

        double cellWidthUm = ReadPositiveDouble(item, "cellWidthUm");
        double cellHeightUm = ReadPositiveDouble(item, "cellHeightUm");
        int columns = ReadInt32(item, "columns");
        int rows = ReadInt32(item, "rows");
        double radialGutterUm = ReadNonNegativeDouble(item, "radialGutterUm");
        double tangentialGutterUm = ReadNonNegativeDouble(item, "tangentialGutterUm");

        if (columns is < RosettaPagePreset.MinColumns or > RosettaPagePreset.MaxColumns)
        {
            throw new InvalidDataException(
                $"Rosetta preset columns ({columns}) must be between {RosettaPagePreset.MinColumns} and {RosettaPagePreset.MaxColumns}.");
        }
        if (rows is < RosettaPagePreset.MinRows or > RosettaPagePreset.MaxRows)
        {
            throw new InvalidDataException(
                $"Rosetta preset rows ({rows}) must be between {RosettaPagePreset.MinRows} and {RosettaPagePreset.MaxRows}.");
        }

        var cellSize = new RosettaCellSize(cellWidthUm, cellHeightUm);
        bool cdSupported = item.TryGetProperty("cdSupported", out JsonElement cdEl)
            ? ReadBoolean(cdEl, "cdSupported")
            : cellSize.IsCdCompatible;

        string id = item.TryGetProperty("id", out JsonElement idEl)
            ? ReadNonEmptyString(idEl, "id")
            : isLadderRung
                ? string.Create(CultureInfo.InvariantCulture, $"ladder-{cellWidthUm:0.##}x{cellHeightUm:0.##}")
                : throw new InvalidDataException("Rosetta preset is missing 'id'.");

        string name = item.TryGetProperty("name", out JsonElement nameEl)
            ? ReadNonEmptyString(nameEl, "name")
            : isLadderRung
                ? string.Create(CultureInfo.InvariantCulture, $"{cellWidthUm:0.##}x{cellHeightUm:0.##} um")
                : throw new InvalidDataException("Rosetta preset is missing 'name'.");

        string targetMedia = item.TryGetProperty("targetMedia", out JsonElement mediaEl)
            ? ReadNonEmptyString(mediaEl, "targetMedia")
            : (cdSupported ? "CD-R / DVD-R" : "DVD-R");

        return new RosettaPagePreset(
            id,
            name,
            targetMedia,
            cellSize,
            columns,
            rows,
            radialGutterUm,
            tangentialGutterUm,
            cdSupported);
    }

    private static double ReadPositiveDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el) || !el.TryGetDouble(out double value) || !double.IsFinite(value) || value <= 0.0)
        {
            throw new InvalidDataException($"Rosetta preset field '{name}' must be a finite positive number.");
        }
        return value;
    }

    private static double ReadNonNegativeDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el) || !el.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0.0)
        {
            throw new InvalidDataException($"Rosetta preset field '{name}' must be a finite non-negative number.");
        }
        return value;
    }

    private static int ReadInt32(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el) || !el.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"Rosetta preset field '{name}' must be an integer.");
        }
        return value;
    }

    private static bool ReadBoolean(JsonElement el, string name)
        => el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Rosetta preset field '{name}' must be a boolean.")
        };

    private static string ReadNonEmptyString(JsonElement el, string name)
    {
        string? value = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Rosetta preset field '{name}' must be a non-empty string.");
        }
        return value;
    }

    private static RosettaPagePreset? FindByIdOrName(IReadOnlyList<RosettaPagePreset> list, string idOrName)
    {
        for (int i = 0; i < list.Count; i++)
        {
            RosettaPagePreset item = list[i];
            if (string.Equals(item.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }
        return null;
    }
}
