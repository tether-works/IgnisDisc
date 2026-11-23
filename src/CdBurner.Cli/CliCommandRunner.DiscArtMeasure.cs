using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Geometry;

namespace CdBurner.Cli;

public static partial class CliCommandRunner
{
    private const string DiscArtMeasureUsage =
        "Usage: ignisdisc-cli disc-art measure --drive <id> --output <file.json> [--from <sector>] [--to <sector>] [--spacing <sectors>] [--json]";

    private static int HandleDiscArtMeasure(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string rawOutput = cmd.GetOption("output") ?? cmd.GetOption("out") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new CommandLineException("Missing required option: --output <file.json>");
        }

        int? fromSector = ParseOptionalNonNegativeInt(cmd, "from", "--from");
        int? toSector = ParseOptionalNonNegativeInt(cmd, "to", "--to");
        int? spacing = ParseOptionalPositiveInt(cmd, "spacing", "--spacing");
        if (fromSector.HasValue && toSector.HasValue && toSector.Value <= fromSector.Value)
        {
            throw new CommandLineException("--to must be greater than --from.");
        }

        var recorder = FindRecorder(deviceService, driveId);
        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            throw new CommandLineException("No disc is in the drive.");
        }

        if (status.IsBlank)
        {
            throw new CommandLineException("Cannot measure a blank disc: timing reads only work on sectors that have already been written.");
        }

        if (!status.MediaType.IsCd())
        {
            throw new CommandLineException($"'disc-art measure' currently supports CD media only (drive holds {status.MediaType.GetDisplayName()}).");
        }

        string outputPath = Path.GetFullPath(rawOutput);
        string capturePath = Path.ChangeExtension(outputPath, ".csv");

        IScsiTransport transport;
        bool ownsTransport = false;
        if (recorder is VirtualDiscRecorder virtualRecorder)
        {
            VirtualMmcDrive drive = virtualRecorder.CurrentMedium is { RecordedMmcDrive: { Recorded: true } recordedDrive }
                ? recordedDrive
                : NativeAudioBurnEngine.CreateVirtualDrive(virtualRecorder);
            virtualRecorder.ConfigureVirtualDrive?.Invoke(drive);
            if (drive.TimingModel is null)
            {
                throw new CommandLineException("The virtual drive has no timing model configured for read-timing measurement.");
            }

            transport = drive;
        }
        else
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new CommandLineException("Measuring a physical optical drive needs Windows.");
            }

            try
            {
                transport = Win32ScsiTransport.Open(NativeAudioBurnEngine.DevicePathFor(recorder));
                ownsTransport = true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                throw new CommandLineException($"Could not open the drive for read-timing measurement: {ex.Message}");
            }
        }

        TimingProbePlan plan;
        IReadOnlyList<TimingProbeSample> probed;
        try
        {
            if (!DiscTimingProbe.TryQueryReadableRanges(transport, out var ranges, out string? tocError))
            {
                throw new CommandLineException(tocError ?? "Could not read the disc's table of contents.");
            }

            if (!DiscTimingProbe.TryBuildPlan(ranges, fromSector, toSector, spacing, out TimingProbePlan? builtPlan, out string? planError)
                || builtPlan is null)
            {
                throw new CommandLineException(planError ?? "Could not plan the timing capture.");
            }

            plan = builtPlan;
            int lastDecile = -1;
            probed = DiscTimingProbe.Capture(transport, plan, progress: (done, total) =>
            {
                if (cmd.IsJson)
                {
                    return;
                }

                int decile = (int)(10L * done / Math.Max(1, total));
                if (decile != lastDecile)
                {
                    lastDecile = decile;
                    stderr.WriteLine($"  Measuring: {decile * 10}% ({done:N0}/{total:N0} reads)");
                }
            });
        }
        finally
        {
            if (ownsTransport)
            {
                transport.Dispose();
            }
        }

        var captureSamples = new TimingCaptureSample[probed.Count];
        for (int i = 0; i < probed.Count; i++)
        {
            TimingProbeSample s = probed[i];
            captureSamples[i] = new TimingCaptureSample(s.Index, s.Lba, s.DoneMicroseconds, s.TookMicroseconds, s.Ok);
        }

        SpiralTimingMeasurementResult result = SpiralTimingAnalyzer.Analyze(captureSamples);
        if (!result.Success || result.TurnCorrection is null)
        {
            throw new CommandLineException(result.RefusalReason ?? "Could not fit a steady spiral from the timing capture.");
        }

        try
        {
            SpiralTimingAnalyzer.WriteCaptureCsv(capturePath, captureSamples);
            SpiralTimingAnalyzer.WriteTurnMapJson(outputPath, result.TurnCorrection);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandLineException($"Could not write the turn table: {ex.Message}");
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                experimental = true,
                driveId = recorder.UniqueId,
                outputPath,
                capturePath,
                anchorSector = plan.AnchorLba,
                fromSector = result.FirstStationSector,
                toSector = result.LastStationSector,
                spacing = plan.Spacing,
                stationCount = result.StationCount,
                turnCount = result.TurnCount,
                innerRadiusMm = result.TurnCorrection.InnerRadiusMm,
                radiusSquaredPerSectorMm2 = result.TurnCorrection.RadiusSquaredPerSectorMm2,
                sectorLengthMm = result.TurnCorrection.SectorLengthMm,
                rotationPeriodMicroseconds = result.RotationPeriodMicroseconds,
                rotationRpm = result.RotationRpm,
                periodDriftPpm = result.PeriodDriftPpm,
                wholeTurnScatterDegrees = result.WholeTurnScatterDegrees,
                stationScatterMedianDegrees = result.StationScatterMedianDegrees,
                stationScatterWorstDegrees = result.StationScatterWorstDegrees,
                chainMarginTurns = result.ChainMarginTurns,
                totalReadings = result.TotalReadings,
                keptReadings = result.KeptReadings,
                droppedReadings = result.DroppedReadings,
                failedReadings = result.FailedReadings,
                cacheHitReadings = result.CacheHitReadings,
                unsteadyReadings = result.UnsteadyReadings,
                smoothingDegree = result.SmoothingDegree,
                smoothingLooErrorDegrees = result.SmoothingLooErrorDegrees,
                smoothingResidualDegrees = result.SmoothingResidualDegrees,
            });
        }
        else
        {
            stdout.WriteLine("Measured spiral turn table written (experimental, read-only).");
            stdout.WriteLine($"  Drive         : {recorder.UniqueId}");
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Range         : {result.FirstStationSector:N0} to {result.LastStationSector:N0} ({result.StationCount} stations, spacing {plan.Spacing:N0}, anchor {plan.AnchorLba:N0})"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Turn count    : {result.TurnCount:F4} turns"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Spin          : {result.RotationPeriodMicroseconds:F2} us ({result.RotationRpm:F1} rpm, drift {result.PeriodDriftPpm:F0} ppm)"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Scatter       : whole-turn {result.WholeTurnScatterDegrees:F2} deg, station median {result.StationScatterMedianDegrees:F2} deg (worst {result.StationScatterWorstDegrees:F2} deg)"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Readings      : {result.KeptReadings:N0}/{result.TotalReadings:N0} kept ({result.DroppedReadings:N0} dropped: {result.CacheHitReadings:N0} cache, {result.UnsteadyReadings:N0} unsteady, {result.FailedReadings:N0} failed)"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Chain margin  : {result.ChainMarginTurns:F3} turn"));
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Smoothing     : Chebyshev degree {result.SmoothingDegree} (LOO error {result.SmoothingLooErrorDegrees:F2} deg)"));
            stdout.WriteLine($"  Turn table    : {outputPath}");
            stdout.WriteLine($"  Capture file  : {capturePath}");
        }

        return 0;
    }

    private static int? ParseOptionalNonNegativeInt(CommandLineArgs cmd, string key, string label)
    {
        if (cmd.GetOption(key) is not { } raw)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
        {
            throw new CommandLineException($"{label} must be a non-negative integer sector number.");
        }

        return value;
    }

    private static int? ParseOptionalPositiveInt(CommandLineArgs cmd, string key, string label)
    {
        if (cmd.GetOption(key) is not { } raw)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
        {
            throw new CommandLineException($"{label} must be a positive integer sector count.");
        }

        return value;
    }
}
