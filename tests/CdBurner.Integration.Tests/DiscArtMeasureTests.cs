using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Geometry;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-end <c>disc-art measure</c> tests against <see cref="VirtualMmcDrive"/> with
/// <see cref="SimulatedDriveTimingModel"/>. No physical drive is touched.
/// </summary>
public sealed class DiscArtMeasureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-measure-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _devices;

    public DiscArtMeasureTests()
    {
        Directory.CreateDirectory(_dir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _devices = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static VirtualDiscMedium CreateRecordedCd(int sectors)
    {
        var medium = new VirtualDiscMedium(
            DiscMediaType.CdR,
            Math.Max(359_675, sectors),
            isBlank: false,
            raw2352Mode: true)
        {
            IsFinalized = true,
            UsedSectors = sectors,
        };
        var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = sectors };
        session.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = sectors, IsAudio = true });
        medium.Sessions.Add(session);
        return medium;
    }

    private static double ConstantSpiralTurns(double innerRadiusMm, double perSectorMm2, double sectorLengthMm, double sector)
        => sectorLengthMm * (Math.Sqrt((innerRadiusMm * innerRadiusMm) + (perSectorMm2 * sector)) - innerRadiusMm) / (Math.PI * perSectorMm2);

    [Fact]
    public async Task Measure_on_simulated_drive_returns_simulated_spiral_and_sends_read_only_commands()
    {
        const int sectors = 350_000;
        const double r0 = 24.78;
        const double k = 0.00768;
        var model = new SimulatedDriveTimingModel
        {
            RotationPeriodMicroseconds = 28_701.6,
            InnerRadiusMm = r0,
            RadiusSquaredPerSectorMm2 = k,
            SectorLengthMm = 16.0,
            JitterStdDevMicroseconds = 35.0,
            SmoothDepartureTurns = lba =>
            {
                double u = (lba - 2_000.0) / (344_000.0 - 2_000.0);
                return 0.25 * Math.Sin(2.0 * Math.PI * u);
            },
        };

        using var medium = CreateRecordedCd(sectors);
        VirtualMmcDrive? observedDrive = null;
        _recorder.InsertMedia(medium);
        _recorder.ConfigureVirtualDrive = d =>
        {
            d.TimingModel = model;
            observedDrive = d;
        };

        string outJson = P("measured.turnmap.json");
        (int code, string stdout, string stderr) = await RunAsync(
            "disc-art", "measure",
            "--drive", "VDRV-01",
            "--output", outJson,
            "--from", "2000",
            "--to", "344000",
            "--spacing", "3000",
            "--json");

        Assert.True(code == 0, $"Expected success, got code {code}: stdout={stdout} stderr={stderr}");
        Assert.True(File.Exists(outJson), "Turn map JSON was not written.");
        string captureCsv = P("measured.turnmap.csv");
        Assert.True(File.Exists(captureCsv), "Capture CSV was not written beside the turn table.");

        Assert.NotNull(observedDrive);
        // Only READ TOC (0x43) and READ CD (0xBE) are sent: no writes, no SET CD SPEED (0xBB).
        Assert.All(observedDrive!.CommandsReceived, op => Assert.True(op is 0x43 or 0xBE, $"Unexpected SCSI opcode 0x{op:X2}."));
        Assert.DoesNotContain((byte)0xBB, observedDrive.CommandsReceived);

        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("experimental").GetBoolean());
        Assert.Equal(outJson, root.GetProperty("outputPath").GetString());
        Assert.Equal(captureCsv, root.GetProperty("capturePath").GetString());
        Assert.True(root.GetProperty("chainMarginTurns").GetDouble() < 0.1);
        Assert.True(root.GetProperty("droppedReadings").GetInt32() > 0);

        double expectedTurns = model.SpiralTurnsAt(344_000) - model.SpiralTurnsAt(2_000);
        double reportedTurns = root.GetProperty("turnCount").GetDouble();
        Assert.True(
            Math.Abs(reportedTurns - expectedTurns) < 0.003,
            $"Reported turns {reportedTurns:F4} vs simulated {expectedTurns:F4}.");

        MeasuredTurnCorrection loaded = MeasuredTurnCorrection.Load(outJson);
        Assert.Equal(114, loaded.StationCount); // 115 planned stations minus 170,000 (the anchor)
        var errs = new List<double>();
        for (double a = 2_000; a <= 344_000; a += 1_000)
        {
            double pred = ConstantSpiralTurns(loaded.InnerRadiusMm, loaded.RadiusSquaredPerSectorMm2, loaded.SectorLengthMm, a)
                + loaded.TurnsAt(a);
            errs.Add(pred - model.SpiralTurnsAt(a));
        }

        double mean = errs.Average();
        double worstDeg = errs.Max(e => Math.Abs(e - mean) * 360.0);
        Assert.True(worstDeg < 1.0, $"Worst error across the simulated disc was {worstDeg:F3} deg.");
    }

    [Fact]
    public async Task Small_disc_under_twenty_thousand_readable_sectors_is_refused_writing_nothing()
    {
        using var medium = CreateRecordedCd(6_000);
        _recorder.InsertMedia(medium);
        _recorder.ConfigureVirtualDrive = d => d.TimingModel = new SimulatedDriveTimingModel();

        string outJson = P("small.turnmap.json");
        (int code, string stdout, _) = await RunAsync(
            "disc-art", "measure",
            "--drive", "VDRV-01",
            "--output", outJson,
            "--json");

        Assert.Equal(1, code);
        Assert.False(File.Exists(outJson));
        Assert.False(File.Exists(P("small.turnmap.csv")));
        Assert.Contains("20,000", stdout);
    }

    [Fact]
    public async Task Unsteady_simulated_spin_is_refused_writing_nothing()
    {
        using var medium = CreateRecordedCd(60_000);
        _recorder.InsertMedia(medium);
        _recorder.ConfigureVirtualDrive = d => d.TimingModel = new SimulatedDriveTimingModel
        {
            UnsteadyCycleFraction = 0.30,
        };

        string outJson = P("unsteady.turnmap.json");
        (int code, string stdout, _) = await RunAsync(
            "disc-art", "measure",
            "--drive", "VDRV-01",
            "--output", outJson,
            "--from", "2000",
            "--to", "50000",
            "--spacing", "3000",
            "--json");

        Assert.Equal(1, code);
        Assert.False(File.Exists(outJson));
        Assert.False(File.Exists(P("unsteady.turnmap.csv")));
        Assert.Contains("steady", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Virtual_drive_timing_model_serves_adjacent_sectors_from_cache_under_sixty_ms()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = 0x0009,
            TimingModel = new SimulatedDriveTimingModel
            {
                RotationPeriodMicroseconds = 28_701.6,
                CacheAheadSectors = 32,
                CacheHitMicroseconds = 6_000.0,
            }
        };
        drive.LoadDataSessions([(0, new byte[40_000 * 2048])], null);

        byte[] buf = new byte[2352];
        double t0 = drive.SimulatedClockMicroseconds;
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(10_000, 1, 0, 0x10), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(6)).IsSuccess);
        double t1 = drive.SimulatedClockMicroseconds;
        Assert.True(t1 - t0 > 60_000.0, "Cold seek read should exceed 60 ms.");

        // Adjacent sector falls inside the read-ahead cache and completes in < 60 ms.
        Assert.True(drive.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(10_001, 1, 0, 0x10), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(6)).IsSuccess);
        double t2 = drive.SimulatedClockMicroseconds;
        Assert.True(t2 - t1 < 60_000.0, $"Cache hit took {t2 - t1} us; expected < 60,000 us.");
    }

    [Fact]
    public async Task Cli_help_documents_disc_art_measure_as_experimental()
    {
        (int code, string stdout, _) = await RunAsync("disc-art", "--help");
        Assert.Equal(0, code);
        Assert.Contains("disc-art measure", stdout);
        Assert.Contains("Experimental", stdout, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SingleDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
