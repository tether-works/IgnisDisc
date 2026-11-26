using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// A new BD-RE as the LG BP50NB40 answers it (read-only probe, 2026-11-26): profile 43h, BD Write (0x0041) listed but
/// not current, READ CAPACITY 3/31/00, and READ FORMAT CAPACITIES offering format 00h with 11,826,176 blocks and the
/// type-dependent parameter 0x003000. The writer must format it (00h) and check 0x0041 again before the first WRITE.
/// </summary>
public sealed class UnformattedBdReTests
{
    private const int DiscBlocks = 12_219_392;     // current/maximum descriptor of the real reply
    private const int Default00hBlocks = 11_826_176; // format 00h as offered
    private const int Speed2x = 8_991;             // kB/s, the only speed the drive listed for this BD-RE

    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], DiscBlocks - 1);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static VirtualMmcDrive NewBdReLikeBp50nb40(params int[] acceptedTypeParameters) => new()
    {
        Profile = NativeTaoDataWriter.ProfileBdRe,
        CapacitySectors = Default00hBlocks,
        BdReFormatted = false,
        BdWriteNotCurrentUntilFormatted = true,
        AcceptedBdReDefaultTypeParameters = acceptedTypeParameters,
        FormatCapacitiesResponse = VirtualMmcDrive.Bp50nb40NewBdReFormatCapacities(),
        WriteSpeedPerformanceResponse = Speeds(Speed2x)
    };

    private static byte[] Image(int sectors)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(18).NextBytes(data);
        return data;
    }

    private static (NativeDataWriteResult Result, List<string> Notes) Write(VirtualMmcDrive drive, byte[] image)
    {
        var notes = new List<string>();
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { }, note: notes.Add);
        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false, CloseDisc: true));
        return (result, notes);
    }

    private static (bool Present, bool Current) BdWriteFeature(VirtualMmcDrive drive)
    {
        byte[] cdb = MmcCommandBuilder.BuildGetConfigurationCdb(MmcCommandBuilder.FeatureBdWrite, 64);
        cdb[1] = 0x02;
        byte[] reply = new byte[64];
        var r = drive.ExecuteCommand(cdb, reply, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.True(r.IsSuccess);
        bool present = r.DataTransferLength >= 12 && BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)) == MmcCommandBuilder.FeatureBdWrite;
        return (present, present && (reply[10] & 0x01) != 0);
    }

    [Fact]
    public void Simulator_AnswersANewBdReLikeTheBp50nb40()
    {
        var drive = NewBdReLikeBp50nb40(0x003000);

        Assert.Equal((true, false), BdWriteFeature(drive));
        byte[] capacity = new byte[8];
        var readCapacity = drive.ExecuteCommand(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, capacity, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.True(readCapacity.SenseInfo is { SenseKey: 0x03, Asc: 0x31, Ascq: 0x00 }, "READ CAPACITY should answer 3/31/00");

        var formats = MmcMediaReport.Query(drive, TimeSpan.FromSeconds(5)).FormattedCapacity!;
        Assert.Equal(FormattedCapacityState.Unformatted, formats.State);
        var offer00 = Assert.Single(formats.Formats, f => f.FormatType == 0x00);
        Assert.Equal(Default00hBlocks, offer00.Blocks);
        Assert.Equal(0x003000, offer00.TypeParameter);
        // The spare area of format 00h, in 64 KiB clusters: the parameter times 32 sectors is the difference.
        Assert.Equal(DiscBlocks - Default00hBlocks, offer00.TypeParameter * NativeTaoDataWriter.BdClusterSectors);
    }

    [Fact]
    public void NewBdRe_WithBdWriteNotCurrent_IsFormattedWithTheOfferedParameter_ThenWritten()
    {
        var drive = NewBdReLikeBp50nb40(0x003000);
        byte[] image = Image(1_000);

        var (result, notes) = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal(new byte[] { 0x00, 0x82, 0x00, 0x08, 0x00, 0xB4, 0x74, 0x00, 0x00, 0x00, 0x30, 0x00 }, drive.LastFormatParameters);
        Assert.Equal(NativeWriteCommands.FormatTypeDefault, result.BdFormatTypeSent);
        Assert.Equal(0x003000u, result.BdFormatTypeParameterSent);
        Assert.NotNull(result.FormatWait);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.Contains(notes, n => n.Contains("listed but not current"));
        Assert.Contains(notes, n => n.Contains("current after formatting"));
        Assert.Equal((true, true), BdWriteFeature(drive));
    }

    [Fact]
    public void DriveRefusingTheOfferedParameter_GetsTheBlockLengthOnce_AndNothingIsLost()
    {
        var drive = NewBdReLikeBp50nb40(0x000800);
        byte[] image = Image(640);

        var (result, notes) = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, drive.CommandsReceived.Count(c => c == NativeWriteCommands.OpCodeFormatUnit));
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal(0x000800u, result.BdFormatTypeParameterSent);
        Assert.Contains(notes, n => n.Contains("0x003000 refused"));
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void DriveRefusingBothParameters_StopsWithNothingWritten()
    {
        var drive = NewBdReLikeBp50nb40(0x123456);

        var (result, _) = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting, result.Message);
        Assert.Contains("refused to format", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void BdWriteStillNotCurrentAfterFormatting_StopsBeforeTheFirstWrite_AndSaysTheDiscIsFormatted()
    {
        var drive = NewBdReLikeBp50nb40(0x003000);
        drive.BdWriteStaysNotCurrent = true;

        var (result, _) = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting); // the format changed the disc
        Assert.Contains("formatted", result.Message);
        Assert.Contains("0x0041", result.Message);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void FormattedBdRe_WithBdWriteNotCurrent_IsStillRefusedBeforeAnything()
    {
        var drive = NewBdReLikeBp50nb40(0x003000);
        drive.BdReFormatted = true;
        drive.BdWriteStaysNotCurrent = true;
        drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(Default00hBlocks, formatted: true);

        var (result, _) = Write(drive, Image(320));

        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DriveThatDoesNotListBdWrite_NeverFormatsTheBdRe()
    {
        var drive = NewBdReLikeBp50nb40(0x003000);
        drive.OffersBdWrite = false;

        var (result, _) = Write(drive, Image(320));

        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.FormatUnitCount);
    }

    [Theory]
    [InlineData(0x003000, 0x003000u)]
    [InlineData(0x001000, 0x001000u)]
    [InlineData(0, 0x000800u)]
    public void DefaultFormatParameter_CopiesTheDrivesOffer_OrFallsBackToTheBlockLength(int offered, uint expected)
    {
        Assert.Equal(expected, NativeTaoDataWriter.BdReDefaultTypeParameter(offered));
    }
}
