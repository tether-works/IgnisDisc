using System.Diagnostics;

namespace CdBurner.Hardware.Scsi;

/// <summary>A contiguous readable sector range from the CD table of contents.</summary>
public readonly record struct ReadableSectorRange(int StartLba, int SectorCount, bool IsAudio)
{
    public int EndLbaExclusive => StartLba + SectorCount;
}

/// <summary>An alternating anchor/station read plan for mapping a CD's spiral.</summary>
public sealed record TimingProbePlan(
    int AnchorLba,
    int FromSector,
    int ToSector,
    int Spacing,
    IReadOnlyList<int> PlannedLbas,
    int StationCount);

/// <summary>One completed <c>READ CD</c> timing measurement.</summary>
public readonly record struct TimingProbeSample(
    int Index,
    int Lba,
    double DoneMicroseconds,
    double TookMicroseconds,
    bool Ok);

/// <summary>
/// Read-only CD spiral timing capture over <see cref="IScsiTransport"/>: reads the TOC, plans stations with
/// offsets 0, 1, 2, 4, ..., 512 alternating with a fixed anchor sector, and times one-sector <c>READ CD</c>
/// commands (6 s timeout, no writes, no <c>SET CD SPEED</c>).
/// </summary>
public static class DiscTimingProbe
{
    public const int MinimumReadableSectors = 20_000;
    public const int DefaultFromOffset = 2_000;
    public const int DefaultSpacing = 3_000;
    public const int MaxStationOffset = 512;
    public const int WarmupReadCount = 20;
    public static readonly TimeSpan ReadCommandTimeout = TimeSpan.FromSeconds(6);

    private const int TaoRunOutSectors = 2;
    private const int InterSessionGap = 11_400;
    private const int RawSectorBytes = 2352;

    /// <summary>Reads the CD's table of contents (<c>READ TOC</c> format 0) and returns its readable track ranges.</summary>
    public static bool TryQueryReadableRanges(
        IScsiTransport transport,
        out IReadOnlyList<ReadableSectorRange> ranges,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(transport);
        byte[] buffer = new byte[4 + (8 * 100)];
        var result = transport.ExecuteCommand(
            MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 0, (ushort)buffer.Length),
            buffer,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            ReadCommandTimeout);

        if (!result.IsSuccess || result.DataTransferLength < 12)
        {
            ranges = [];
            error = $"Could not read the disc's table of contents: {result.SenseInfo?.ToString() ?? result.ErrorMessage}.";
            return false;
        }

        var toc = MmcResponseParser.ParseToc(buffer.AsSpan(0, (int)Math.Min(result.DataTransferLength, (uint)buffer.Length)));
        var list = new List<ReadableSectorRange>();
        for (int i = 0; i + 1 < toc.Count; i++)
        {
            var cur = toc[i];
            var next = toc[i + 1];
            bool audio = (cur.AdrControl & 0x04) == 0;
            int end = next.StartLba;
            bool nextIsDataAfterAudio = audio && next.TrackNumber != 0xAA && (next.AdrControl & 0x04) != 0;
            if (nextIsDataAfterAudio && end - cur.StartLba > InterSessionGap)
            {
                end -= InterSessionGap;
            }

            if (!audio && end - cur.StartLba > TaoRunOutSectors)
            {
                end -= TaoRunOutSectors;
            }

            if (cur.TrackNumber != 0xAA && end > cur.StartLba && cur.StartLba >= 0)
            {
                list.Add(new ReadableSectorRange(cur.StartLba, end - cur.StartLba, audio));
            }
        }

        if (list.Count == 0)
        {
            ranges = [];
            error = "The disc's table of contents lists no readable tracks.";
            return false;
        }

        ranges = list;
        error = null;
        return true;
    }

    /// <summary>Validates readable capacity and constructs an alternating anchor/station read plan.</summary>
    public static bool TryBuildPlan(
        IReadOnlyList<ReadableSectorRange> ranges,
        int? fromSector,
        int? toSector,
        int? spacing,
        out TimingProbePlan? plan,
        out string? refusalReason)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        plan = null;

        long totalReadable = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            totalReadable += ranges[i].SectorCount;
        }

        if (totalReadable < MinimumReadableSectors)
        {
            refusalReason = $"The disc holds only {totalReadable:N0} readable sectors; at least {MinimumReadableSectors:N0} are needed to measure the spiral.";
            return false;
        }

        int step = spacing ?? DefaultSpacing;
        if (step <= 0)
        {
            refusalReason = "--spacing must be a positive number of sectors.";
            return false;
        }

        int minLba = ranges[0].StartLba;
        int maxLba = ranges[^1].EndLbaExclusive - 1;
        int maxStationStart = maxLba - MaxStationOffset;
        if (maxStationStart <= minLba)
        {
            refusalReason = "The readable area is too short for station offsets up to 512 sectors.";
            return false;
        }

        int from;
        if (fromSector is int f)
        {
            if (f < minLba || f > maxStationStart)
            {
                refusalReason = $"--from sector {f:N0} is outside the readable range ({minLba:N0} to {maxStationStart:N0}).";
                return false;
            }

            from = f;
        }
        else
        {
            from = minLba + DefaultFromOffset;
            if (from >= maxStationStart)
            {
                from = minLba;
            }
        }

        int to;
        if (toSector is int t)
        {
            if (t <= from || t > maxStationStart)
            {
                refusalReason = $"--to sector {t:N0} must be between {from + 1:N0} and {maxStationStart:N0}.";
                return false;
            }

            to = t;
        }
        else
        {
            to = Math.Max(from, maxStationStart - DefaultFromOffset);
            if ((to - from) / step < 4 && maxStationStart > from)
            {
                to = maxStationStart;
            }
        }

        var stations = new List<int>();
        for (int s = from; s <= to; s += step)
        {
            if (IsRangeReadable(ranges, s, MaxStationOffset + 1))
            {
                stations.Add(s);
            }
        }

        if (stations.Count < 4)
        {
            refusalReason = $"Only {stations.Count} readable stations fall in {from:N0}..{to:N0} at spacing {step:N0}; at least 4 are needed.";
            return false;
        }

        int anchor = ChooseAnchorSector(ranges, (from + to) / 2);
        var lbas = new List<int>((stations.Count * 11 * 2) + 1);
        for (int i = 0; i < stations.Count; i++)
        {
            int s = stations[i];
            lbas.Add(anchor);
            lbas.Add(s);
            for (int off = 1; off <= MaxStationOffset; off *= 2)
            {
                lbas.Add(anchor);
                lbas.Add(s + off);
            }
        }

        lbas.Add(anchor);
        plan = new TimingProbePlan(anchor, stations[0], stations[^1], step, lbas, stations.Count);
        refusalReason = null;
        return true;
    }

    /// <summary>Executes the read-only capture against <paramref name="transport"/>.</summary>
    public static IReadOnlyList<TimingProbeSample> Capture(
        IScsiTransport transport,
        TimingProbePlan plan,
        Func<double>? readClockMicroseconds = null,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(plan);

        Func<double> clock = readClockMicroseconds ?? CreateDefaultClock(transport);
        byte[] buffer = new byte[RawSectorBytes];
        int count = plan.PlannedLbas.Count;

        for (int i = 0; i < WarmupReadCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = ReadOneSector(transport, plan.PlannedLbas[i % count], buffer);
        }

        double[] doneUs = new double[count];
        double[] tookUs = new double[count];
        bool[] ok = new bool[count];

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double t0 = clock();
            ok[i] = ReadOneSector(transport, plan.PlannedLbas[i], buffer);
            double t1 = clock();
            doneUs[i] = t1;
            tookUs[i] = t1 - t0;
            progress?.Invoke(i + 1, count);
        }

        double origin = doneUs[0];
        var samples = new TimingProbeSample[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = new TimingProbeSample(i, plan.PlannedLbas[i], doneUs[i] - origin, tookUs[i], ok[i]);
        }

        return samples;
    }

    private static Func<double> CreateDefaultClock(IScsiTransport transport)
    {
        if (transport is Simulation.VirtualMmcDrive { TimingModel: not null } virtualDrive)
        {
            return () => virtualDrive.SimulatedClockMicroseconds;
        }

        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        return () => Stopwatch.GetTimestamp() * usPerTick;
    }

    private static bool ReadOneSector(IScsiTransport transport, int lba, byte[] buffer)
    {
        byte[] cdb = MmcCommandBuilder.BuildReadCdCdb(lba, 1, expectedSectorType: 0, mainChannelSelectionBits: 0x10);
        return transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ReadCommandTimeout).IsSuccess;
    }

    private static bool IsRangeReadable(IReadOnlyList<ReadableSectorRange> ranges, int startLba, int sectorCount)
    {
        int end = startLba + sectorCount;
        for (int i = 0; i < ranges.Count; i++)
        {
            ReadableSectorRange r = ranges[i];
            if (startLba >= r.StartLba && end <= r.EndLbaExclusive)
            {
                return true;
            }
        }

        return false;
    }

    private static int ChooseAnchorSector(IReadOnlyList<ReadableSectorRange> ranges, int target)
    {
        int best = ranges[0].StartLba;
        int bestDist = int.MaxValue;
        for (int i = 0; i < ranges.Count; i++)
        {
            ReadableSectorRange r = ranges[i];
            if (r.SectorCount <= 0)
            {
                continue;
            }

            int clamped = Math.Clamp(target, r.StartLba, r.EndLbaExclusive - 1);
            int dist = Math.Abs(clamped - target);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = clamped;
            }
        }

        return best;
    }
}
