using System.Text.Json;

namespace CdBurner.Lithography.Geometry;

/// <summary>
/// A measured correction to the ideal constant-pitch spiral of one disc: for a list of sector addresses, how many
/// turns the real spiral is ahead of (positive) or behind (negative) the constant-pitch spiral described by
/// <see cref="InnerRadiusMm"/> and <see cref="RadiusSquaredPerSectorMm2"/>.
/// <para>
/// Why a table and not coefficients: a real recordable disc measured by read timing was up to most of a turn away from
/// the best constant-pitch spiral and still about half a turn away from the best cubic-pitch one, so no short formula
/// describes it. The table is interpolated with a cubic through neighbouring stations; outside the measured range the
/// last slope is continued in a straight line.
/// </para>
/// Instances are immutable and safe to read from several threads.
/// </summary>
public sealed class MeasuredTurnCorrection
{
    private readonly double[] _sector;
    private readonly double[] _turns;
    private readonly double[] _slope;   // turns per sector at each station

    /// <summary>Creates a correction table.</summary>
    /// <param name="innerRadiusMm">Radius of sector 0 of the constant-pitch spiral the table refers to.</param>
    /// <param name="radiusSquaredPerSectorMm2">Growth of radius² per sector of that spiral (mm² per sector).</param>
    /// <param name="sectorLengthMm">Track length of one sector the measurement assumed (CD: linear velocity / 75).</param>
    /// <param name="stationSectors">Sector addresses of the stations, strictly ascending, at least four.</param>
    /// <param name="correctionTurns">Measured turns minus constant-pitch turns at each station.</param>
    /// <param name="addressOffsetSectors">Added to an address before the table and the spiral are asked for its
    /// angle: the distance, in sectors, between where a drawn byte lands on the disc and where the drive reported that
    /// address while the disc was measured. Found once per drive by looking at a burned test picture.</param>
    public MeasuredTurnCorrection(
        double innerRadiusMm,
        double radiusSquaredPerSectorMm2,
        double sectorLengthMm,
        IReadOnlyList<double> stationSectors,
        IReadOnlyList<double> correctionTurns,
        double addressOffsetSectors = 0.0)
    {
        ArgumentNullException.ThrowIfNull(stationSectors);
        ArgumentNullException.ThrowIfNull(correctionTurns);
        RequirePositive(innerRadiusMm, nameof(innerRadiusMm));
        RequirePositive(radiusSquaredPerSectorMm2, nameof(radiusSquaredPerSectorMm2));
        RequirePositive(sectorLengthMm, nameof(sectorLengthMm));
        if (!double.IsFinite(addressOffsetSectors))
        {
            throw new ArgumentOutOfRangeException(nameof(addressOffsetSectors), addressOffsetSectors, "The offset must be a finite number of sectors.");
        }

        if (stationSectors.Count != correctionTurns.Count)
        {
            throw new ArgumentException("There must be one correction value per station.", nameof(correctionTurns));
        }

        if (stationSectors.Count < 4)
        {
            throw new ArgumentException("A correction table needs at least four stations.", nameof(stationSectors));
        }

        _sector = new double[stationSectors.Count];
        _turns = new double[stationSectors.Count];
        for (int i = 0; i < _sector.Length; i++)
        {
            double sector = stationSectors[i];
            double turns = correctionTurns[i];
            if (!double.IsFinite(sector) || !double.IsFinite(turns))
            {
                throw new ArgumentException($"Station {i} is not a finite number.", nameof(stationSectors));
            }

            if (i > 0 && sector <= _sector[i - 1])
            {
                throw new ArgumentException($"Station addresses must rise: station {i} ({sector}) is not above station {i - 1} ({_sector[i - 1]}).", nameof(stationSectors));
            }

            _sector[i] = sector;
            _turns[i] = turns;
        }

        // Slope at each station from the parabola through it and its two neighbours (one-sided at the ends).
        _slope = new double[_sector.Length];
        int last = _sector.Length - 1;
        for (int i = 1; i < last; i++)
        {
            double h0 = _sector[i] - _sector[i - 1];
            double h1 = _sector[i + 1] - _sector[i];
            double d0 = (_turns[i] - _turns[i - 1]) / h0;
            double d1 = (_turns[i + 1] - _turns[i]) / h1;
            _slope[i] = ((d0 * h1) + (d1 * h0)) / (h0 + h1);
        }

        _slope[0] = EndSlope(_sector[0], _sector[1], _sector[2], _turns[0], _turns[1], _turns[2]);
        _slope[last] = EndSlope(_sector[last], _sector[last - 1], _sector[last - 2], _turns[last], _turns[last - 1], _turns[last - 2]);

        InnerRadiusMm = innerRadiusMm;
        RadiusSquaredPerSectorMm2 = radiusSquaredPerSectorMm2;
        SectorLengthMm = sectorLengthMm;
        AddressOffsetSectors = addressOffsetSectors;
    }

    /// <summary>Radius of sector 0 of the constant-pitch spiral the table refers to, in millimetres.</summary>
    public double InnerRadiusMm { get; }

    /// <summary>Growth of radius² per sector of that spiral, in mm² per sector.</summary>
    public double RadiusSquaredPerSectorMm2 { get; }

    /// <summary>Track length of one sector the measurement assumed, in millimetres.</summary>
    public double SectorLengthMm { get; }

    /// <summary>Sectors added to an address before its angle is looked up.</summary>
    public double AddressOffsetSectors { get; }

    /// <summary>Number of stations in the table.</summary>
    public int StationCount => _sector.Length;

    internal IReadOnlyList<double> StationSectors => _sector;
    internal IReadOnlyList<double> CorrectionTurns => _turns;

    /// <summary>First and last measured sector address.</summary>
    public (double First, double Last) MeasuredRange => (_sector[0], _sector[^1]);

    /// <summary>Radius of the constant-pitch spiral at a sector address, in millimetres.</summary>
    public double OuterRadiusMmAt(double sector)
        => Math.Sqrt((InnerRadiusMm * InnerRadiusMm) + (RadiusSquaredPerSectorMm2 * sector));

    /// <summary>The same table with another address offset.</summary>
    public MeasuredTurnCorrection WithAddressOffset(double addressOffsetSectors)
        => new(InnerRadiusMm, RadiusSquaredPerSectorMm2, SectorLengthMm, _sector, _turns, addressOffsetSectors);

    /// <summary>Correction in turns at a sector address (no address offset applied).</summary>
    public double TurnsAt(double sector)
    {
        int last = _sector.Length - 1;
        if (sector <= _sector[0])
        {
            return _turns[0] + (_slope[0] * (sector - _sector[0]));
        }

        if (sector >= _sector[last])
        {
            return _turns[last] + (_slope[last] * (sector - _sector[last]));
        }

        int lo = 0;
        int hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >>> 1;
            if (_sector[mid] <= sector) lo = mid; else hi = mid;
        }

        double h = _sector[hi] - _sector[lo];
        double t = (sector - _sector[lo]) / h;
        double t2 = t * t;
        double t3 = t2 * t;
        // Cubic Hermite: passes through both stations with the slopes found above.
        return (((2 * t3) - (3 * t2) + 1) * _turns[lo])
            + ((t3 - (2 * t2) + t) * h * _slope[lo])
            + (((-2 * t3) + (3 * t2)) * _turns[hi])
            + ((t3 - t2) * h * _slope[hi]);
    }

    /// <summary>Reads a table written by the read-timing measurement (JSON).</summary>
    public static MeasuredTurnCorrection Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;
            double[] stations = [.. root.GetProperty("stationSectors").EnumerateArray().Select(e => e.GetDouble())];
            double[] turns = [.. root.GetProperty("correctionTurns").EnumerateArray().Select(e => e.GetDouble())];
            return new MeasuredTurnCorrection(
                root.GetProperty("innerRadiusMm").GetDouble(),
                root.GetProperty("radiusSquaredPerSectorMm2").GetDouble(),
                root.GetProperty("sectorLengthMm").GetDouble(),
                stations,
                turns);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a turn map: {ex.Message}", ex);
        }
    }

    private static double EndSlope(double x0, double x1, double x2, double y0, double y1, double y2)
    {
        // Derivative at x0 of the parabola through the three points.
        double h1 = x1 - x0;
        double h2 = x2 - x0;
        double d1 = (y1 - y0) / h1;
        double d2 = (y2 - y0) / h2;
        return ((d1 * h2) - (d2 * h1)) / (h2 - h1);
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number greater than zero.");
        }
    }
}
