using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Archival;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-end CLI recover and ArchiveRecoveryService tests verifying that failed cooked CD data sectors
/// are repaired via raw 2,352-byte read + SectorParityCorrector before PAR2 fallback.
/// </summary>
public sealed class ModePage01ArchiveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis-mp01-rec-" + Guid.NewGuid().ToString("N")[..8]);

    public ModePage01ArchiveRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task CliRecover_CorruptedMode1Sector_RepairsViaRawParityBeforePar2()
    {
        string srcDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(srcDir);
        byte[] payload = new byte[16_384];
        new Random(2026).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(srcDir, "critical.bin"), payload);

        var plan = ArchiveSpanPlanner.Plan(srcDir, DiscMediaType.CdR, redundancyPercent: 0, setId: "mp01test", ceilingSectorsOverride: 2_000);
        var staged = (await ArchiveStager.StageDiscsAsync(plan, Path.Combine(_root, "staging"), par2Path: null)).Single();

        string isoPath = Path.Combine(_root, "archive.iso");
        int built = await CliCommandRunner.RunAsync(
            ["build-iso", "--input", staged.DiscRoot, "--output", isoPath, "--udf", "--label", "ARCHIVE01", "--fixed-time", "1767225600"],
            new StringWriter(), new StringWriter(),
            new DiscDeviceManager(enablePhysicalEnumeration: false), new VirtualBurnEngine());
        Assert.Equal(0, built);

        byte[] isoBytes = File.ReadAllBytes(isoPath);
        int fileStartLba;
        using (var isoStream = File.OpenRead(isoPath))
        {
            fileStartLba = UdfTreeReader.TryRead(isoStream)!
                .Files.Single(f => f.Name.Equals("critical.bin", StringComparison.OrdinalIgnoreCase)).StartLba;
        }

        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = (VirtualDiscRecorder)devices.GetRecorderById("VDRV-01")!;
        using var medium = VirtualDiscMedium.FromExistingFile(isoPath, DiscMediaType.CdR);
        byte[] raw = RawSectorCodec.Convert2048To2352Mode1(isoBytes.AsSpan((fileStartLba + 1) * 2048, 2048), fileStartLba + 1);
        raw[16 + 500] ^= 0x3C;
        medium.SimulatedCorruptedRawSectors[fileStartLba + 1] = raw;
        recorder.InsertMedia(medium);

        string outDir = Path.Combine(_root, "recovered");
        var stdout = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(
            ["recover", "--drive", "VDRV-01", "--out", outDir, "--retries", "1", "--json"],
            stdout, new StringWriter(), devices, new VirtualBurnEngine());

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("unreadableSectors").GetInt32());
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(outDir, "critical.bin")));
    }
}
