using System.Globalization;
using System.Text;

namespace CdBurner.Core.Primitives;

/// <summary>
/// What the user is told, and must accept, before a CD-R is written past its rated capacity: the rated size against the
/// size of the program, the excess in megabytes and minutes:seconds, and the risk to the drive and the disc.
/// </summary>
/// <param name="NominalCapacitySectors">The disc's rated capacity in sectors (its ATIP lead-out start).</param>
/// <param name="ProgramSectors">Sectors the program needs.</param>
/// <param name="Policy">The overburn limit that applies.</param>
public sealed record CdOverburnWarning(int NominalCapacitySectors, int ProgramSectors, CdOverburnPolicy Policy)
{
    /// <summary>Sectors past the rated capacity.</summary>
    public int ExcessSectors => CdOverburnPolicy.ExcessSectors(ProgramSectors, NominalCapacitySectors);

    /// <summary>True when the program does not fit on the disc as rated, so overburning is needed.</summary>
    public bool NeedsOverburn => ExcessSectors > 0;

    /// <summary>True when even overburning cannot hold the program (over the limit or past 99:59:74).</summary>
    public bool ExceedsLimit => Policy.Check(ProgramSectors, NominalCapacitySectors) != null;

    public string Title => "Overburn a CD-R?";

    /// <summary>Capacity as "mm:ss (n sectors, x MB of data)".</summary>
    public static string DescribeSectors(int sectors) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{CdOverburnPolicy.FormatExcessTime(sectors)} ({sectors:N0} sectors, {sectors * 2048.0 / 1_000_000.0:F1} MB of data)");

    /// <summary>The full confirmation text: nominal capacity against image size, the excess and the hardware-limit risk.</summary>
    public string Message
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine("This program is larger than the disc's rated capacity.");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Rated disc capacity: {DescribeSectors(NominalCapacitySectors)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Image size: {DescribeSectors(ProgramSectors)}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Overburn excess: {ExcessSectors:N0} sectors = {CdOverburnPolicy.ExcessDataMegabytes(ExcessSectors):F1} MB or {CdOverburnPolicy.FormatExcessTime(ExcessSectors)} (mm:ss)");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Overburn limit: {Policy.MaxExtraSectors:N0} sectors ({CdOverburnPolicy.FormatExcessTime(Policy.MaxExtraSectors)}), and never past 99:59:74");
            sb.AppendLine();
            sb.AppendLine("Risk: this writes into the area the disc was not rated for. Many drives cannot do it: they may refuse, "
                + "stop part-way, or leave a disc that cannot be read. The end of the disc is also where discs are least reliable, "
                + "and some players cannot read the extra part. Try it only on a disc you can afford to lose.");
            if (ExceedsLimit)
            {
                sb.AppendLine();
                sb.AppendLine("This program is longer than the overburn limit, so it will be refused.");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
