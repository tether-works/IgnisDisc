using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class StreamingWrite12Tests : IDisposable
{
    private const int Capacity = 12_219_392;
    private const int Speed6x = 26_973;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-stream12-" + Guid.NewGuid().ToString("N"));

    public StreamingWrite12Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void BuildWrite12_EncodesExactMmc5CdbBytes_AndStreamingBit()
    {
        byte[] nonStreaming = MmcCommandBuilder.BuildWrite12(0x01020304, 0x00000020, streaming: false);
        Assert.Equal(
            new byte[]
            {
                0xAA, 0x00,
                0x01, 0x02, 0x03, 0x04,
                0x00, 0x00, 0x00, 0x20,
                0x00,
                0x00
            },
            nonStreaming);

        byte[] streaming = MmcCommandBuilder.BuildWrite12(0x01020304, 0x00010203, streaming: true);
        Assert.Equal(
            new byte[]
            {
                0xAA, 0x00,
                0x01, 0x02, 0x03, 0x04,
                0x00, 0x01, 0x02, 0x03,
                0x80,
                0x00
            },
            streaming);

        Assert.Throws<ArgumentOutOfRangeException>(() => MmcCommandBuilder.BuildWrite12(0, -1, streaming: true));
    }

    [Fact]
    public void BdRe_DefaultStreamingOff_UsesWrite10()
    {
        var drive = CreateBdReDrive(formatted: true);
        byte[] image = CreateImage(96);

        var result = new NativeTaoDataWriter(drive, sleep: _ => { })
            .Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false));

        Assert.True(result.Success, result.Message);
        Assert.False(result.StreamingWriteUsed);
        Assert.Equal(0, drive.Write12CommandCount);
        Assert.Equal(0, drive.StreamingWrite12CommandCount);
        Assert.Contains(NativeWriteCommands.OpCodeWrite10, drive.CommandsReceived);
        Assert.DoesNotContain(MmcCommandBuilder.OpCodeWrite12, drive.CommandsReceived);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BdRe_WithStreamingOptIn_SendsWrite12WithStreamingBit80(bool formatted)
    {
        var drive = CreateBdReDrive(formatted);
        byte[] image = CreateImage(96);

        var result = new NativeTaoDataWriter(drive, sleep: _ => { })
            .Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false)
            {
                StreamingWrite = true
            });

        Assert.True(result.Success, result.Message);
        Assert.True(result.StreamingWriteUsed);
        Assert.True(drive.Write12CommandCount > 0);
        Assert.Equal(drive.Write12CommandCount, drive.StreamingWrite12CommandCount);
        Assert.All(drive.Write12Log, w => Assert.True(w.Streaming));
        Assert.Equal(96, drive.Write12Log.Sum(w => w.Count));
        Assert.DoesNotContain(NativeWriteCommands.OpCodeWrite10, drive.CommandsReceived);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void DvdRam_WithStreamingOptIn_SendsWrite12WithStreamingBit80()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdRam,
            CapacitySectors = 2_236_704,
            RamFormatted = true
        };
        byte[] image = CreateImage(64);

        var result = new NativeTaoDataWriter(drive, sleep: _ => { })
            .Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false)
            {
                StreamingWrite = true
            });

        Assert.True(result.Success, result.Message);
        Assert.True(result.StreamingWriteUsed);
        Assert.True(drive.Write12CommandCount > 0);
        Assert.All(drive.Write12Log, w => Assert.True(w.Streaming));
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void DvdPlusRw_EvenWithStreamingOptIn_KeepsWrite10()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdPlusRw,
            CapacitySectors = 2_295_104,
            BackgroundFormatStatus = 3
        };
        byte[] image = CreateImage(64);

        var result = new NativeTaoDataWriter(drive, sleep: _ => { })
            .Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false)
            {
                StreamingWrite = true
            });

        Assert.True(result.Success, result.Message);
        Assert.False(result.StreamingWriteUsed);
        Assert.Equal(0, drive.Write12CommandCount);
        Assert.Contains(NativeWriteCommands.OpCodeWrite10, drive.CommandsReceived);
    }

    [Fact]
    public void BdRe_WhenDriveRejectsWrite12_FallsBackToWrite10()
    {
        var drive = CreateBdReDrive(formatted: true);
        drive.RejectsWrite12 = true;
        byte[] image = CreateImage(96);

        var result = new NativeTaoDataWriter(drive, sleep: _ => { })
            .Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false)
            {
                StreamingWrite = true
            });

        Assert.True(result.Success, result.Message);
        Assert.False(result.StreamingWriteUsed);
        Assert.Equal(0, drive.Write12CommandCount);
        Assert.Contains(NativeWriteCommands.OpCodeWrite10, drive.CommandsReceived);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public async Task BdRe_EndToEndThroughDataBurnService_StreamsAndVerifies()
    {
        VirtualMmcDrive? seenDrive = null;
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            ConfigureVirtualDrive = d =>
            {
                seenDrive = d;
                d.WriteSpeedPerformanceResponse = Speeds(Speed6x);
            }
        };
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);

        byte[] image = IsoLikeImage(256, seed: 42);
        var burnResult = await new DataBurnService(engine).BurnStreamAsync(
            recorder,
            new MemoryStream(image),
            new DataBurnOptions { VerifyAfterBurn = true, StreamingWrite = true });

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.True(burnResult.Verified);
        Assert.NotNull(engine.LastResult);
        Assert.True(engine.LastResult!.StreamingWriteUsed);
        Assert.NotNull(seenDrive);
        Assert.True(seenDrive!.StreamingWrite12CommandCount > 0);
    }

    [Fact]
    public void Simulator_RefusesAStreamingWriteThatIsNotWholeClusters_LikeTheBp50nb40()
    {
        var drive = CreateBdReDrive(formatted: true);
        byte[] data = new byte[27 * 2048];

        var partial = drive.ExecuteCommand(NativeWriteCommands.BuildWrite12(0, 27, streaming: true), data,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));
        var plain = drive.ExecuteCommand(NativeWriteCommands.BuildWrite10(0, 27), data,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));

        Assert.True(partial.SenseInfo is { SenseKey: 0x05, Asc: 0x24 }, "a 27-sector streaming WRITE (12) must be refused");
        Assert.True(plain.IsSuccess, "the same 27 sectors without the Streaming bit are accepted");
    }

    [Theory]
    [InlineData(1_947)] // ends 27 sectors into a cluster, like the 3,644,283-sector film image of 2026-11-26
    [InlineData(1_000)]
    [InlineData(1_024)] // already whole clusters: no padding
    public void StreamingBdRe_PadsToWholeClusters_SoEveryWrite12IsAccepted(int imageSectors)
    {
        var drive = CreateBdReDrive(formatted: true);
        byte[] image = CreateImage(imageSectors);
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });

        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false, CloseDisc: true)
        {
            StreamingWrite = true
        });

        Assert.True(result.Success, result.Message);
        Assert.True(result.StreamingWriteUsed);
        Assert.All(drive.Write12Log, w => Assert.True(w.Lba % 32 == 0 && w.Count % 32 == 0, $"WRITE(12) {w.Lba}+{w.Count}"));
        int padded = (image.Length / 2048 + 31) / 32 * 32;
        Assert.Equal(padded, drive.Write12Log.Sum(w => w.Count));
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
    }

    private static VirtualMmcDrive CreateBdReDrive(bool formatted) => new()
    {
        Profile = NativeTaoDataWriter.ProfileBdRe,
        CapacitySectors = Capacity,
        BdReFormatted = formatted,
        FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(
            Capacity,
            formatted,
            (NativeWriteCommands.FormatTypeDefault, Capacity)),
        WriteSpeedPerformanceResponse = Speeds(Speed6x)
    };

    private static byte[] CreateImage(int sectors, int seed = 17)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] IsoLikeImage(int sectors, int seed)
    {
        byte[] data = CreateImage(sectors, seed);
        data[16 * 2048] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(data, 16 * 2048 + 1);
        return data;
    }

    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], Capacity - 1);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }
}
