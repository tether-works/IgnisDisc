using System.Buffers.Binary;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class NativeClosingAvdpAlignmentTests
{
    private static byte[] BuildUnalignedUdfImage(int alignMultiple)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("hello.txt", "UDF closing anchor alignment check"u8.ToArray());
        // Add an extra file if needed so TotalSectors is never already a multiple of alignMultiple
        for (int extra = 0; extra < 4; extra++)
        {
            byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(VolumeLabel: "ALIGN_TEST", IncludeUdf: true)).ToArray();
            if ((image.Length / 2048) % alignMultiple != 0)
            {
                return image;
            }
            root.AddFile($"pad_{extra}.bin", new byte[2048]);
        }
        throw new InvalidOperationException("Could not produce unaligned test image.");
    }

    private static byte[] Speeds(int capacity, int kbps)
    {
        byte[] r = new byte[8 + 16];
        BinaryPrimitives.WriteUInt32BigEndian(r, 20u);
        var d = r.AsSpan(8, 16);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], (uint)(capacity - 1));
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kbps);
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kbps);
        return r;
    }

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusR, 16, 11_080)]
    [InlineData(NativeTaoDataWriter.ProfileDvdPlusR, 16, 11_080)]
    [InlineData(NativeTaoDataWriter.ProfileBdR, 32, 26_973)]
    public void UdfImage_PaddedToTrackAlignment_PlacesClosingAvdpAtAlignedLastSector(ushort profile, int alignSectors, int speedKbps)
    {
        byte[] image = BuildUnalignedUdfImage(alignSectors);
        int imageSectors = image.Length / 2048;
        int expectedTrackSectors = (imageSectors + alignSectors - 1) / alignSectors * alignSectors;
        Assert.True(expectedTrackSectors > imageSectors);

        var drive = new VirtualMmcDrive
        {
            Profile = profile,
            CapacitySectors = 2_298_496,
            WriteSpeedPerformanceResponse = Speeds(2_298_496, speedKbps)
        };

        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false, CloseDisc: true));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(drive.DataDiscImage);
        Assert.Equal(expectedTrackSectors * 2048, drive.DataDiscImage!.Length);

        // Verify the very last sector of the track carries a valid UDF closing AVDP for TagLocation == expectedTrackSectors - 1
        int lastLba = expectedTrackSectors - 1;
        ReadOnlySpan<byte> lastSector = drive.DataDiscImage.AsSpan(lastLba * 2048, 2048);
        Assert.Equal((ushort)0x0002, BinaryPrimitives.ReadUInt16LittleEndian(lastSector));
        Assert.Equal((uint)lastLba, BinaryPrimitives.ReadUInt32LittleEndian(lastSector.Slice(12, 4)));
        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4) sum = (byte)(sum + lastSector[i]);
        }
        Assert.Equal(lastSector[4], sum);

        // Verify UDF CRC-ITU-T over bytes 16..511 matches descriptor CRC at bytes 8..9
        ushort crc = 0;
        foreach (byte b in lastSector.Slice(16, 496))
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }
        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(lastSector.Slice(8, 2)), crc);

        // Verify UdfTreeReader reads the file cleanly from the aligned disc image
        using var ms = new MemoryStream(drive.DataDiscImage, writable: false);
        var udfRoot = UdfTreeReader.TryRead(ms);
        Assert.NotNull(udfRoot);
        var helloEntry = udfRoot!.Files.Single(f => f.Name == "hello.txt");
        byte[] readBack = new byte[helloEntry.SizeBytes];
        drive.DataDiscImage.AsSpan(helloEntry.StartLba * 2048, (int)helloEntry.SizeBytes).CopyTo(readBack);
        Assert.Equal("UDF closing anchor alignment check"u8.ToArray(), readBack);
    }
}
