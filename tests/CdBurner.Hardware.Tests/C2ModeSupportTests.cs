using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class C2ModeSupportTests
{
    private static VirtualMmcDrive BurnAudioDrive(SimulatedC2Mode mode, int sectors = 300, int marginalLba = 20)
    {
        var drive = new VirtualMmcDrive { C2Mode = mode, MarginalBadReads = 1 };
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(9911).NextBytes(pcm);
        var request = new NativeSaoWriteRequest(
            [new SaoTrack(1, sectors, 150)],
            _ => new MemoryStream(pcm),
            null,
            null,
            TestWrite: false);
        var result = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);
        Assert.True(result.Success, result.Message);
        drive.MarginalLbas.Add(marginalLba);
        return drive;
    }

    [Theory]
    [InlineData(SimulatedC2Mode.Mode294)]
    [InlineData(SimulatedC2Mode.Mode296Header)]
    [InlineData(SimulatedC2Mode.Mode296PioneerFooter)]
    public async Task ProbeAndReadAudioWithC2_SupportsMode294AndBothMode296Layouts(SimulatedC2Mode mode)
    {
        const int sectors = 300;
        const int marginalLba = 20;
        using var drive = BurnAudioDrive(mode, sectors, marginalLba);

        Assert.True(PhysicalSectorReader.ProbeC2Support(drive, probeLba: 0));

        var firstPass = new Dictionary<int, int>();
        var firstResult = await PhysicalSectorReader.ReadAudioWithC2Async(
            drive,
            0,
            sectors,
            (lba, _, c2Bytes) =>
            {
                firstPass[lba] = c2Bytes;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(firstResult.Success, firstResult.ErrorMessage);
        Assert.Equal(sectors, firstPass.Count);
        Assert.Equal(1, firstPass[marginalLba]);
        Assert.All(firstPass.Where(kv => kv.Key != marginalLba), kv => Assert.Equal(0, kv.Value));

        // Second read after MarginalBadReads=1 must return clean data with 0 C2 flags.
        var secondPass = new Dictionary<int, int>();
        var secondResult = await PhysicalSectorReader.ReadAudioWithC2Async(
            drive,
            0,
            sectors,
            (lba, _, c2Bytes) =>
            {
                secondPass[lba] = c2Bytes;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(secondResult.Success, secondResult.ErrorMessage);
        Assert.Equal(0, secondPass[marginalLba]);
    }

    [Fact]
    public void ProbeC2Support_RejectsC2Pointers_ReturnsFalse()
    {
        using var drive = BurnAudioDrive(SimulatedC2Mode.Mode294);
        drive.RejectsC2Pointers = true;

        Assert.False(PhysicalSectorReader.ProbeC2Support(drive, probeLba: 0));
    }

    [Fact]
    public void CountC2Bytes_DistinguishesHeaderAndPioneerFooter296Layouts()
    {
        // Standard MMC-6 header: 2-byte error summary/count in [0..1], 294-byte bitmap in [2..295].
        byte[] headerWithCount = new byte[296];
        headerWithCount[0] = 0x00;
        headerWithCount[1] = 0x03; // block error count = 3 (not bitwise OR)
        headerWithCount[2 + 10] = 0b0000_0111; // 3 bits in bitmap
        Assert.Equal(3, PhysicalSectorReader.CountC2Bytes(headerWithCount));

        // Standard MMC-6 header: bitwise OR in byte 0, bitmap at [2..295].
        byte[] headerWithOr = new byte[296];
        headerWithOr[0] = 0x80;
        headerWithOr[2 + 25] = 0x80;
        Assert.Equal(1, PhysicalSectorReader.CountC2Bytes(headerWithOr));

        // Pioneer footer: 294-byte bitmap in [0..293], bitwise OR in [294].
        byte[] pioneerFooter = new byte[296];
        pioneerFooter[25] = 0x80;
        pioneerFooter[294] = 0x80;
        Assert.Equal(1, PhysicalSectorReader.CountC2Bytes(pioneerFooter));

        // Clean 296-byte block: 0 errors regardless of layout.
        Assert.Equal(0, PhysicalSectorReader.CountC2Bytes(new byte[296]));
    }
}
