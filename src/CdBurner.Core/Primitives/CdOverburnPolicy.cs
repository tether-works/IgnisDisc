namespace CdBurner.Core.Primitives;

/// <summary>
/// Whether a CD-R burn may run past the disc's rated capacity, and how far. Overburning writes the lead-out later than
/// the position the disc's ATIP names; it is off unless the owner of the burn asks for it, and even then it never goes
/// past <see cref="MaxExtraSectors"/> beyond the ATIP lead-out or past the last address a CD can hold (99:59:74).
/// It applies to Disc-at-Once writing of CD-R only; every other disc type and write mode refuses it.
/// </summary>
/// <param name="Allowed">True only when the user explicitly opted in. The default instance is off.</param>
/// <param name="MaxExtraSectors">Most sectors the lead-out may start beyond the ATIP lead-out start.</param>
public sealed record CdOverburnPolicy(bool Allowed = false, int MaxExtraSectors = CdOverburnPolicy.DefaultMaxExtraSectors)
{
    /// <summary>Default ceiling: 3 minutes (13,500 sectors, about 31.6 MB of data or 3:00 of audio).</summary>
    public const int DefaultMaxExtraSectors = 3 * 60 * 75;

    /// <summary>Last absolute MSF address a CD can carry, 99:59:74, as a lead-out start LBA (449,999 frames minus the 150 of pre-gap).</summary>
    public const int HardCeilingLeadOutLba = (99 * 60 + 59) * 75 + 74 - 150;

    /// <summary>The policy every burn uses unless told otherwise: no overburning.</summary>
    public static CdOverburnPolicy Off { get; } = new(false);

    /// <summary>Overburning allowed up to <paramref name="maxMinutes"/> past the ATIP lead-out (a negative value counts as zero).</summary>
    public static CdOverburnPolicy AllowedForMinutes(double maxMinutes) =>
        new(true, (int)Math.Round(Math.Max(0.0, maxMinutes) * 60.0 * 75.0));

    /// <summary>
    /// The highest sector at which the lead-out may start: the ATIP lead-out start plus the allowance, never above
    /// <see cref="HardCeilingLeadOutLba"/>. Without opt-in it is the ATIP lead-out start itself.
    /// </summary>
    public int CeilingLeadOutLba(int atipLeadOutLba)
    {
        long extra = Allowed ? Math.Max(0, MaxExtraSectors) : 0;
        return (int)Math.Min((long)atipLeadOutLba + extra, HardCeilingLeadOutLba);
    }

    /// <summary>Sectors an image or program of <paramref name="leadOutLba"/> runs past the ATIP lead-out start (0 when it fits).</summary>
    public static int ExcessSectors(int leadOutLba, int atipLeadOutLba) => Math.Max(0, leadOutLba - atipLeadOutLba);

    /// <summary>Excess time as mm:ss (rounded up to a whole second), for warnings.</summary>
    public static string FormatExcessTime(int excessSectors)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, excessSectors) / 75.0);
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    /// <summary>Excess in megabytes of 2,048-byte data sectors (decimal MB, as disc labels count).</summary>
    public static double ExcessDataMegabytes(int excessSectors) => Math.Max(0, excessSectors) * 2048.0 / 1_000_000.0;

    /// <summary>
    /// Decides whether a program ending at <paramref name="leadOutLba"/> may be written on a disc whose ATIP lead-out
    /// starts at <paramref name="atipLeadOutLba"/>. Returns null when it may, otherwise the reason it may not.
    /// </summary>
    public string? Check(int leadOutLba, int atipLeadOutLba)
    {
        if (leadOutLba > HardCeilingLeadOutLba)
        {
            return $"The program ends at sector {leadOutLba:N0}, past the last address a CD can hold (99:59:74, sector {HardCeilingLeadOutLba:N0}); overburning cannot go that far.";
        }
        int excess = ExcessSectors(leadOutLba, atipLeadOutLba);
        if (excess == 0)
        {
            return null;
        }
        if (!Allowed)
        {
            return $"The program is {excess:N0} sectors ({FormatExcessTime(excess)}) longer than the disc's rated capacity. "
                 + "Overburning is off; turn it on explicitly to write past the rated capacity.";
        }
        int allowed = Math.Max(0, MaxExtraSectors);
        if (excess > allowed || leadOutLba > CeilingLeadOutLba(atipLeadOutLba))
        {
            return $"The program is {excess:N0} sectors ({FormatExcessTime(excess)}) past the rated capacity, more than the overburn limit of "
                 + $"{allowed:N0} sectors ({FormatExcessTime(allowed)}).";
        }
        return null;
    }
}
