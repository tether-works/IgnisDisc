namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Physical CAV spin and spiral timing model for <see cref="VirtualMmcDrive"/> read-timing simulation:
/// models rotation period, spiral sectors-per-turn growth, seek latency, read-ahead cache hits, and optional
/// jitter or unsteady spin.
/// </summary>
public sealed class SimulatedDriveTimingModel
{
    /// <summary>Nominal rotation period in microseconds (default 28,701.6 us ≈ 2,090 rpm).</summary>
    public double RotationPeriodMicroseconds { get; init; } = 28_701.6;

    /// <summary>Start radius at LBA 0 of the simulated constant-pitch baseline spiral, in millimetres.</summary>
    public double InnerRadiusMm { get; init; } = 24.80;

    /// <summary>Radius squared growth per sector (mm² per sector) of the simulated baseline spiral.</summary>
    public double RadiusSquaredPerSectorMm2 { get; init; } = 0.007639437;

    /// <summary>Track length of one CD sector in millimetres (1,200 mm/s ÷ 75 sectors/s = 16.0 mm).</summary>
    public double SectorLengthMm { get; init; } = 16.0;

    /// <summary>Initial angular phase at time 0, in turns [0, 1).</summary>
    public double InitialPhaseTurns { get; init; } = 0.15;

    /// <summary>Number of sequential sectors cached ahead after each disc read.</summary>
    public int CacheAheadSectors { get; init; } = 32;

    /// <summary>Duration of a read served from the drive's read-ahead cache, in microseconds (&lt; 60 ms).</summary>
    public double CacheHitMicroseconds { get; init; } = 6_000.0;

    /// <summary>Minimum seek + command overhead for a non-cached disc read, in microseconds.</summary>
    public double SeekBaseMicroseconds { get; init; } = 65_000.0;

    /// <summary>Additional seek latency per sector of head travel, in microseconds.</summary>
    public double SeekPerSectorMicroseconds { get; init; } = 0.55;

    /// <summary>Optional Gaussian timing jitter (standard deviation in microseconds) on disc reads.</summary>
    public double JitterStdDevMicroseconds { get; init; }

    /// <summary>
    /// Optional fraction [0, 1] of non-cache disc reads that suffer an unsteady rotation jump of ~0.06 turns.
    /// </summary>
    public double UnsteadyCycleFraction { get; init; }

    /// <summary>Deterministic PRNG seed for noise and unsteady-cycle simulation.</summary>
    public int RandomSeed { get; init; } = 145;

    /// <summary>Optional smooth departure from the constant-pitch spiral, in turns as a function of LBA.</summary>
    public Func<double, double>? SmoothDepartureTurns { get; init; }

    /// <summary>True cumulative turns at <paramref name="lba"/> on the simulated disc.</summary>
    public double SpiralTurnsAt(double lba)
    {
        double baseTurns = SectorLengthMm
            * (Math.Sqrt((InnerRadiusMm * InnerRadiusMm) + (RadiusSquaredPerSectorMm2 * lba)) - InnerRadiusMm)
            / (Math.PI * RadiusSquaredPerSectorMm2);
        return baseTurns + (SmoothDepartureTurns?.Invoke(lba) ?? 0.0);
    }
}
