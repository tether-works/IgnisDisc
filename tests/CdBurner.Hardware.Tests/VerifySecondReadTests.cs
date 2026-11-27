using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The image check (<c>verify --iso</c>) tells a sector the drive could not read from one that read back different
/// bytes, reads every failed sector a second time from the disc surface after moving the read head away before it
/// calls the disc bad, and names the file that owns a bad sector, also on a UDF 2.50 Blu-ray image.
/// </summary>
public class VerifySecondReadTests
{
    /// <summary>Reads a <see cref="VirtualMmcDrive"/> data disc through the real SCSI READ (10) path.</summary>
    private sealed class DriveReader(VirtualMmcDrive drive) : IBurnEngineBackend
    {
        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            PhysicalSectorReader.ReadDataSectorsAsync(drive, startLba, sectorCount, DriveCacheBypass.None, sectorConsumer, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsForVerifyAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken,
            bool strict = false, SectorRange? writtenRange = null) =>
            PhysicalSectorReader.ReadDataSectorsAsync(drive, startLba, sectorCount,
                strict ? DriveCacheBypass.EveryRead : DriveCacheBypass.RecentlyWritten, sectorConsumer, progress, cancellationToken,
                writtenRange?.Start, writtenRange?.Count);

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static byte[] BuildImage()
    {
        var rng = new Random(23);
        var root = CompilationDirectory.CreateRoot();
        var docs = root.AddDirectory("Docs");
        foreach (var (dir, name, size) in new[] { (root, "a.bin", 200_000), (docs, "report.pdf", 300_000), (docs, "notes.txt", 3_000) })
        {
            byte[] data = new byte[size];
            rng.NextBytes(data);
            dir.AddFile(name, data);
        }
        return IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions()).ToArray();
    }

    private static int FirstLbaOf(byte[] image, string path)
    {
        using var reader = IsoImageReader.FromStream(new MemoryStream(image));
        return reader.FindFile(path)!.StartLba;
    }

    private static VirtualMmcDrive DriveWith(byte[] image) => new() { DataDiscImage = (byte[])image.Clone() };

    /// <summary>
    /// READ (10) commands that reach a failing sector during the first pass (counted from the simulated drive's log):
    /// the 32-sector read and the single-sector read that narrows the failure down, then the one retry, which is
    /// again a read plus a narrowing read; the SCSI reader sends each command twice before it gives up. A sector that
    /// fails this many times fails the whole first pass; the next read is the second read.
    /// </summary>
    private const int FirstPassReadsOfAFailingSector = 8;

    /// <summary>
    /// Blanks the ISO 9660 volume descriptors (sectors 16 on that say CD001), leaving a UDF-only image like the ones
    /// many Blu-ray authoring tools write.
    /// </summary>
    private static void StripIso9660(byte[] image)
    {
        for (int lba = 16; lba < 32; lba++)
        {
            var sector = image.AsSpan(lba * 2048, 2048);
            if (sector.Slice(1, 5).SequenceEqual("CD001"u8))
            {
                sector.Clear();
            }
        }
    }

    private static async Task<VerificationResult> Verify(VirtualMmcDrive drive, byte[] image)
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        return await new DiscVerificationService().VerifyDiscAgainstStreamAsync(
            recorder, new DriveReader(drive), new MemoryStream(image), raw2352Mode: false);
    }

    /// <summary>SHA-256 of the image with <paramref name="change"/> applied to a copy.</summary>
    private static string Sha256Of(byte[] image, Action<byte[]> change)
    {
        byte[] copy = (byte[])image.Clone();
        change(copy);
        return Convert.ToHexString(SHA256.HashData(copy));
    }

    /// <summary>
    /// The second read of <paramref name="lba"/>: a forced (FUA) read of it after a one-sector forced read at the far
    /// end of the disc.
    /// </summary>
    private static void AssertSecondReadAfterSeekAway(VirtualMmcDrive drive, int lba, int totalSectors)
    {
        int far = lba >= totalSectors / 2 ? 0 : totalSectors - 1;
        var log = drive.Read10Log;
        int seekAway = log.FindLastIndex(r => r.Lba == far && r.Count == 1);
        Assert.True(seekAway >= 0, $"no one-sector read at the far end (LBA {far}) before the second read");
        Assert.True(log[seekAway].Fua, "the seek-away read was not forced from the disc");
        var after = log.Skip(seekAway + 1).Where(r => lba >= r.Lba && lba < r.Lba + r.Count).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, r => Assert.True(r.Fua, $"second read of LBA {r.Lba} had no FUA"));
    }

    [Fact]
    public async Task SectorTheDriveCannotRead_IsReportedAsUnreadable_AfterASecondRead_WithItsFile()
    {
        byte[] image = BuildImage();
        int bad = FirstLbaOf(image, "/Docs/report.pdf") + 3;
        var drive = DriveWith(image);
        drive.ReadErrorAtLba = bad;

        var result = await Verify(drive, image);

        Assert.False(result.IsMatch);
        Assert.Equal(new[] { bad }, result.UnreadableLbas);
        Assert.Empty(result.DifferingLbas);
        Assert.Equal(new[] { bad }, result.MismatchedLbas);
        Assert.Empty(result.RecoveredOnRereadLbas);
        Assert.Equal(new[] { "/Docs/report.pdf" }, result.CorruptedFiles);
        Assert.Contains("1 could not be read", result.ErrorMessage);
        Assert.Contains("after a second read", result.ErrorMessage);
        AssertSecondReadAfterSeekAway(drive, bad, image.Length / 2048);
        // An unreadable sector is compared as zeros, and the disc's hash says so.
        Assert.Equal(Sha256Of(image, c => Array.Clear(c, bad * 2048, 2048)), result.ActualSha256);
    }

    [Fact]
    public async Task SectorThatReadsBackDifferentBytes_IsReportedAsDiffering_NotUnreadable()
    {
        byte[] image = BuildImage();
        int bad = FirstLbaOf(image, "/a.bin") + 10;
        var drive = DriveWith(image);
        drive.ReadBackDifferenceAtLba = bad;

        var result = await Verify(drive, image);

        Assert.False(result.IsMatch);
        Assert.Equal(new[] { bad }, result.DifferingLbas);
        Assert.Empty(result.UnreadableLbas);
        Assert.Equal(new[] { bad }, result.MismatchedLbas);
        Assert.Equal(new[] { "/a.bin" }, result.CorruptedFiles);
        Assert.Contains("1 read back different data", result.ErrorMessage);
        AssertSecondReadAfterSeekAway(drive, bad, image.Length / 2048);
        // The simulated drive flips bit 0 of byte 100 of that sector.
        Assert.Equal(Sha256Of(image, c => c[bad * 2048 + 100] ^= 0x01), result.ActualSha256);
    }

    [Fact]
    public async Task SectorThatFailsOnlyTheFirstRead_PassesTheCheck_AndIsCountedAsAReadHiccup()
    {
        // As on the real Blu-ray: the first read of the sector fails, its immediate retry too, and it reads exactly
        // as the image once read again later.
        byte[] image = BuildImage();
        int hiccup = FirstLbaOf(image, "/Docs/report.pdf") + 20;
        var drive = DriveWith(image);
        drive.TransientReadErrorLbas[hiccup] = FirstPassReadsOfAFailingSector;

        var result = await Verify(drive, image);

        Assert.True(result.IsMatch, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
        Assert.Empty(result.MismatchedLbas);
        Assert.Empty(result.UnreadableLbas);
        Assert.Empty(result.CorruptedFiles);
        Assert.Equal(new[] { hiccup }, result.RecoveredOnRereadLbas);
        Assert.Equal(1, result.TransientSectorCount);
        Assert.Equal(result.ExpectedSha256, result.ActualSha256);
        Assert.Equal(result.ExpectedMd5, result.ActualMd5);
        Assert.Equal(result.ExpectedCrc32, result.ActualCrc32);
        Assert.Equal(0, drive.TransientReadErrorLbas[hiccup]);
        AssertSecondReadAfterSeekAway(drive, hiccup, image.Length / 2048);
    }

    [Fact]
    public async Task ReadHiccupAndRealDamage_OnlyTheDamageFails_AndTheDiscHashIsFromTheFinalReads()
    {
        byte[] image = BuildImage();
        int hiccup = FirstLbaOf(image, "/a.bin") + 5;
        int damaged = FirstLbaOf(image, "/Docs/report.pdf") + 7;
        var drive = DriveWith(image);
        drive.TransientReadErrorLbas[hiccup] = FirstPassReadsOfAFailingSector;
        drive.ReadBackDifferenceAtLba = damaged;

        var result = await Verify(drive, image);

        Assert.False(result.IsMatch);
        Assert.Equal(new[] { damaged }, result.MismatchedLbas);
        Assert.Equal(new[] { damaged }, result.DifferingLbas);
        Assert.Equal(new[] { hiccup }, result.RecoveredOnRereadLbas);
        Assert.Equal(new[] { "/Docs/report.pdf" }, result.CorruptedFiles);
        Assert.Contains("1 other sector(s) failed the first read but matched the image on the second", result.ErrorMessage);
        // The first pass compared zeros for the hiccup; the reported hash is of what the disc finally gave.
        Assert.Equal(Sha256Of(image, c => c[damaged * 2048 + 100] ^= 0x01), result.ActualSha256);
    }

    [Fact]
    public async Task UdfOnlyBluRayImage_NamesTheFilmFileThatOwnsAnUnreadableSector()
    {
        // A Blu-ray style UDF 2.50 image with no ISO 9660 tree at all.
        var rng = new Random(5);
        var root = CompilationDirectory.CreateRoot();
        var bdmv = root.AddDirectory("BDMV");
        var stream = bdmv.AddDirectory("STREAM");
        byte[] film = new byte[600_000];
        rng.NextBytes(film);
        stream.AddFile("00000.m2ts", film);
        bdmv.AddFile("index.bdmv", "INDX0200"u8.ToArray());
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(
            VolumeLabel: "FILM", IncludeJoliet: false, IncludeUdf: true, UdfRevision: 0x0250)).ToArray();
        StripIso9660(image);
        Assert.Throws<InvalidDataException>(() => IsoImageReader.FromStream(new MemoryStream(image)));

        var udfRoot = UdfTreeReader.TryRead(new MemoryStream(image));
        Assert.NotNull(udfRoot);
        int filmStart = udfRoot.SubDirectories.Single().SubDirectories.Single().Files.Single().StartLba;
        int bad = filmStart + 200;
        var drive = DriveWith(image);
        drive.ReadErrorAtLba = bad;

        var result = await Verify(drive, image);

        Assert.False(result.IsMatch);
        Assert.Equal(new[] { bad }, result.UnreadableLbas);
        Assert.Equal(new[] { "/BDMV/STREAM/00000.m2ts" }, result.CorruptedFiles);
        Assert.Contains("/BDMV/STREAM/00000.m2ts", result.ErrorMessage);
    }
}
