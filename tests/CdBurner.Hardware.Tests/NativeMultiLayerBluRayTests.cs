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

/// <summary>
/// Direct writing to multi-layer Blu-ray on the simulated drive: BD-R DL (50 GB), BDXL triple and quad layer (100 and
/// 128 GB) and multi-layer BD-RE. Expected figures come from MMC-6 (T10/1836-D rev 2g): Tables 441-443 for the BD Disc
/// Information bytes, Table 242 for the FORMAT UNIT types (30h, 31h, 32h) and Tables 243/247/250 for the sub-types, and from
/// the disc capacities in sectors that the BD specifications give. Simulator only: none of this has run on a real disc.
/// </summary>
public sealed class NativeMultiLayerBluRayTests : IDisposable
{
    private const int BdR50 = 24_438_784;     // BD-R DL: 2 x 12,219,392 sectors
    private const int Bdxl100 = 48_878_592;   // BDXL triple layer: 3 x 16,292,864
    private const int Bdxl128 = 62_500_864;   // BDXL quad layer: 4 x 15,625,216
    private const int BdRe50 = 23_652_352;    // BD-RE DL with its default spare areas
    private const int Speed6x = 26_973;       // kB/s, 6x Blu-ray as drives list it

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeMultiLayerBluRayTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 41)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] IsoLikeImage(int sectors, int seed)
    {
        byte[] data = Image(sectors, seed);
        data[16 * 2048] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(data, 16 * 2048 + 1);
        return data;
    }

    private static byte[] Speeds(int capacity, params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], (uint)(capacity - 1));
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static VirtualMmcDrive BlankBdR(int capacity, int layers, bool withFormatOffers = false)
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileBdR,
            CapacitySectors = capacity,
            BdLayerCount = layers,
            WriteSpeedPerformanceResponse = Speeds(capacity, Speed6x)
        };
        if (withFormatOffers)
        {
            drive.BdRFormatOffers = new[]
            {
                (NativeWriteCommands.FormatTypeDefault, capacity, 0x000800),
                (NativeWriteCommands.FormatTypeBdRSpareAreas, capacity - 131_072, 0x000103)
            };
        }
        return drive;
    }

    private static VirtualMmcDrive BdRe(int capacity, int layers, bool formatted, params (byte Type, int Blocks)[] offers)
    {
        var all = new List<(byte Type, int Blocks)> { (NativeWriteCommands.FormatTypeDefault, capacity) };
        all.AddRange(offers);
        return new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileBdRe,
            CapacitySectors = capacity,
            BdLayerCount = layers,
            BdReFormatted = formatted,
            FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(capacity, formatted, all.ToArray()),
            WriteSpeedPerformanceResponse = Speeds(capacity, Speed6x)
        };
    }

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, BdFormatMode format = BdFormatMode.Automatic,
        bool closeDisc = true)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false, closeDisc) { BdFormat = format });
    }

    // ---- BD Disc Information (MMC-6 Tables 441 to 443) ----

    private static byte[] DiReply(string discType, byte layers, int units)
    {
        byte[] r = new byte[4100];
        r[0] = 0x10; r[1] = 0x02; // data length 4,098
        for (int i = 0; i < units; i++)
        {
            int at = 4 + 112 * i;
            r[at] = (byte)'D'; r[at + 1] = (byte)'I';
            r[at + 3] = layers;
            Encoding.ASCII.GetBytes(discType).CopyTo(r, at + 8);
            Encoding.ASCII.GetBytes("VERBAT").CopyTo(r, at + 100);
            Encoding.ASCII.GetBytes("IMr").CopyTo(r, at + 106);
        }
        return r;
    }

    [Theory]
    [InlineData("BDR", 2, true)]
    [InlineData("BDR", 3, true)]
    [InlineData("BDR", 4, true)]
    [InlineData("BDW", 2, false)]
    public void DiscInformation_ReadsTypeLayersAndMediaId_FromTheDiUnits(string type, int layers, bool recordable)
    {
        var info = BdDiscInformation.Parse(DiReply(type, (byte)layers, units: 2));

        Assert.NotNull(info);
        Assert.Equal(type, info!.DiscType);
        Assert.Equal(layers, info.LayerCount);
        Assert.Equal(2, info.UnitCount);
        Assert.Equal("VERBAT", info.ManufacturerId);
        Assert.Equal("IMr", info.MediaTypeId);
        Assert.Equal(recordable, info.IsRecordable);
        Assert.Equal(!recordable, info.IsRewritable);
    }

    [Fact]
    public void DiscInformation_WithoutTheDiIdentifier_OrTooShort_IsNotParsed()
    {
        byte[] noId = DiReply("BDR", 2, 1);
        noId[4] = (byte)'X';

        Assert.Null(BdDiscInformation.Parse(noId));
        Assert.Null(BdDiscInformation.Parse(new byte[100]));
    }

    [Fact]
    public void DiscInformation_IgnoresLayerCountsOutsideOneToFour()
    {
        var info = BdDiscInformation.Parse(DiReply("BDR", 9, 1));

        Assert.NotNull(info);
        Assert.Equal(0, info!.LayerCount);
    }

    [Theory]
    [InlineData(BdR50, 2, 12_219_392L)]
    [InlineData(Bdxl100, 3, 16_292_864L)]
    [InlineData(Bdxl128, 4, 15_625_216L)]
    public void SplitCapacity_GivesEqualWholeClusterLayers_ThatAddUpToTheDisc(int total, int layers, long perLayer)
    {
        var split = BdDiscInformation.SplitCapacity(total, layers, NativeTaoDataWriter.BdClusterSectors);

        Assert.Equal(Enumerable.Repeat(perLayer, layers), split);
        Assert.Equal(total, split.Sum());
        Assert.All(split, l => Assert.Equal(0, l % 32));
    }

    [Fact]
    public void SplitCapacity_PutsAnyRemainderOnTheLastLayer()
    {
        var split = BdDiscInformation.SplitCapacity(1_000_100, 3, 32);

        // 1,000,100 / 3 = 333,366 rounded down to a cluster multiple is 333,344 (10,417 x 32); the last layer takes the rest.
        Assert.Equal(new[] { 333_344L, 333_344L, 333_412L }, split);
    }

    [Fact]
    public void ClusterAlignment_Uses64BitMath_NearTheIntLimit()
    {
        // int.MaxValue + 31 does not fit in 32 bits; the aligned figure is 2,147,483,648.
        Assert.Equal(2_147_483_648L, NativeTaoDataWriter.AlignToBdCluster(int.MaxValue));
        Assert.Equal(1024L, NativeTaoDataWriter.AlignToBdCluster(1000));
        Assert.Equal(0L, NativeTaoDataWriter.AlignToBdCluster(0));
    }

    // ---- Writing: BD-R DL and BDXL ----

    [Theory]
    [InlineData(BdR50, 2)]
    [InlineData(Bdxl100, 3)]
    [InlineData(Bdxl128, 4)]
    public void MultiLayerBdR_IsWrittenInWholeClusters_AndFinalizedWith110b(int capacity, int layers)
    {
        var drive = BlankBdR(capacity, layers);
        byte[] image = Image(1000);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.True(result.IsBluRay);
        Assert.Equal(layers, result.BdLayerCount);
        Assert.Equal(Enumerable.Repeat((long)capacity / layers, layers), result.BdLayerSectors);
        Assert.Equal(24, result.PaddingSectors);
        Assert.NotEmpty(drive.BdWrites);
        Assert.All(drive.BdWrites, w =>
        {
            Assert.Equal(0, w.Lba % 32);
            Assert.Equal(0, w.Sectors % 32);
        });
        Assert.Equal(1024, drive.BdWrites.Sum(w => w.Sectors));
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.Equal(NativeWriteCommands.CloseFunctionFinalizeBdR, drive.LastCloseFunction);
        Assert.Equal(2, drive.DiscStatus);
        Assert.Null(result.BdFormatTypeSent);
    }

    [Fact]
    public void BdxlQuadLayer_CapacityInBytes_ExceedsWhatAnIntHolds()
    {
        var drive = BlankBdR(Bdxl128, 4);

        var result = Write(drive, Image(320));

        Assert.True(result.Success, result.Message);
        long bytes = result.BdLayerSectors.Sum() * 2048L;
        Assert.Equal(128_001_769_472L, bytes);
        Assert.True(bytes > int.MaxValue);
    }

    [Fact]
    public void MultiLayerBdR_ThatTheImageDoesNotFit_IsRefusedBeforeAnyWrite()
    {
        var drive = BlankBdR(2048, 2); // far smaller than any disc, just to make the image not fit
        var result = Write(drive, Image(3000));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
        Assert.Contains("does not fit", result.Message);
    }

    [Fact]
    public void DriveThatGivesNoDiscInformation_IsStillWritten_WithTheLayerCountUnknown()
    {
        var drive = BlankBdR(BdR50, 2);
        drive.RejectsBdDiscInformation = true;

        var result = Write(drive, Image(320));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.BdLayerCount);
        Assert.Single(result.BdLayerSectors); // the whole capacity counted as one layer
        Assert.Equal(BdR50, result.BdLayerSectors[0]);
    }

    // ---- FORMAT UNIT on BD-R: 32h with spare areas, 00h drive default ----

    [Fact]
    public void BdR_SpareAreaFormat_SendsType32h_Subtype01b_WithTheOfferedBlocks()
    {
        var drive = BlankBdR(BdR50, 2, withFormatOffers: true);

        var result = Write(drive, Image(640), BdFormatMode.SpareAreas);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NativeWriteCommands.FormatTypeBdRSpareAreas, result.BdFormatTypeSent);
        // MMC-6 6.4: FOV|Immed, descriptor length 8, blocks, type 32h << 2 | sub-type 01b = 0xC9, type parameter as offered.
        byte[] expected = new byte[12];
        expected[1] = 0x82;
        expected[3] = 8;
        BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(4), BdR50 - 131_072);
        expected[8] = 0xC9;
        expected[10] = 0x01;
        expected[11] = 0x03;
        Assert.Equal(expected, drive.LastFormatParameters);
        Assert.Equal(1, drive.FormatUnitCount);
        // The spare areas came out of the data zone: free blocks are what the format left.
        Assert.Equal(BdR50 - 131_072, result.BdLayerSectors.Sum());
        Assert.True(drive.BdRFormatted);
    }

    [Fact]
    public void BdR_DefaultFormat_SendsType00h_Subtype01b()
    {
        var drive = BlankBdR(Bdxl100, 3, withFormatOffers: true);

        var result = Write(drive, Image(640), BdFormatMode.DriveDefault);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NativeWriteCommands.FormatTypeDefault, result.BdFormatTypeSent);
        Assert.Equal(0x01, drive.LastFormatParameters![8]); // type 00h, sub-type 01b
        Assert.Equal(0x08, drive.LastFormatParameters[10]); // block length 0x000800
        Assert.Equal(Bdxl100, result.BdLayerSectors.Sum());  // 00h keeps the whole data zone
    }

    [Fact]
    public void BdR_AutomaticFormat_SendsNoFormatUnit()
    {
        var drive = BlankBdR(BdR50, 2, withFormatOffers: true);

        var result = Write(drive, Image(640));

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.False(drive.BdRFormatted);
    }

    [Fact]
    public void BdR_AlreadyFormatted_IsNotFormattedAgain()
    {
        // READ FORMAT CAPACITIES says the current descriptor is "formatted" (type 10b): nothing to format.
        var drive = BlankBdR(BdR50, 2);
        drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(BdR50, formatted: true);

        var result = Write(drive, Image(64), BdFormatMode.SpareAreas);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Null(result.BdFormatTypeSent);
    }

    [Fact]
    public void BdR_NoSpareAreaFormat_IsRefusedBeforeAnyCommandThatChangesTheDisc()
    {
        var drive = BlankBdR(BdR50, 2, withFormatOffers: true);

        var result = Write(drive, Image(640), BdFormatMode.NoSpareAreas);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("no BD-R format without spare areas", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void BdR_SpareAreaFormat_WhenTheDriveOffersNo32h_IsRefusedUntouched()
    {
        var drive = BlankBdR(BdR50, 2, withFormatOffers: true);
        drive.BdRFormatOffers = new[] { (NativeWriteCommands.FormatTypeDefault, BdR50, 0x000800) };

        var result = Write(drive, Image(640), BdFormatMode.SpareAreas);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("32h", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
    }

    [Fact]
    public void BdR_FormatRefusedByTheDrive_StopsBeforeTheFirstWrite()
    {
        var drive = BlankBdR(BdR50, 2, withFormatOffers: true);
        drive.RejectsFormatUnit = true;

        var result = Write(drive, Image(640), BdFormatMode.SpareAreas);

        Assert.False(result.Success);
        Assert.Contains("refused to format", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- Multi-layer BD-RE: formatting with 00h, 30h and 31h ----

    [Fact]
    public void BdReDl_Unformatted_IsFormattedWith00h_ThenWrittenFrom0()
    {
        var drive = BdRe(BdRe50, 2, formatted: false);
        byte[] image = Image(777);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.BdLayerCount);
        Assert.Equal(NativeWriteCommands.FormatTypeDefault, result.BdFormatTypeSent);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeCloseTrackSession, drive.CommandsReceived);
    }

    [Fact]
    public void BdReDl_SpareAreaFormat_SendsType30h_WithTheOfferedBlocks()
    {
        const int spareBlocks = BdRe50 - 20_480 * 32; // MMC-6 Table 248: 20,480 clusters of spare area on a dual layer disc
        var drive = BdRe(BdRe50, 2, formatted: false, (NativeWriteCommands.FormatTypeBdReSpareAreas, spareBlocks));

        var result = Write(drive, Image(500), BdFormatMode.SpareAreas);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NativeWriteCommands.FormatTypeBdReSpareAreas, drive.LastBdFormatType);
        Assert.Equal(0xC0, drive.LastFormatParameters![8]); // 30h << 2, sub-type 00b (quick reformat / no certification)
        Assert.Equal(spareBlocks, BinaryPrimitives.ReadInt32BigEndian(drive.LastFormatParameters.AsSpan(4)));
        Assert.Equal(spareBlocks, result.BdLayerSectors.Sum());
    }

    [Fact]
    public void BdReXl_NoSpareFormat_SendsType31h_WithBlockLength2048()
    {
        var drive = BdRe(Bdxl100, 3, formatted: false, (NativeWriteCommands.FormatTypeBdReNoSpareAreas, Bdxl100));

        var result = Write(drive, Image(500), BdFormatMode.NoSpareAreas);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.BdLayerCount);
        Assert.Equal(0xC4, drive.LastFormatParameters![8]); // 31h << 2
        Assert.Equal(0x08, drive.LastFormatParameters[10]);
        Assert.Equal(Bdxl100, result.BdLayerSectors.Sum());
    }

    [Fact]
    public void BdRe_NoSpareFormat_WhenNotOffered_IsRefusedUntouched()
    {
        var drive = BdRe(BdRe50, 2, formatted: false);

        var result = Write(drive, Image(500), BdFormatMode.NoSpareAreas);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("31h", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
    }

    // ---- Engine through DataBurnService, with the read-back check ----

    private NativeDataBurnEngine Engine(BdFormatMode format = BdFormatMode.Automatic) => new(logDirectory: _dir)
    {
        BdFormat = format,
        ConfigureVirtualDrive = d => d.WriteSpeedPerformanceResponse = Speeds(d.CapacitySectors ?? 0, Speed6x)
    };

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    private static byte[] OnDisc(VirtualDiscMedium medium, int sectors)
    {
        byte[] data = new byte[sectors * 2048];
        medium.ReadSectors(0, sectors, data, 2048);
        return data;
    }

    [Theory]
    [InlineData(DiscMediaType.BdRDualLayer, 2)]
    [InlineData(DiscMediaType.BdxlRTripleLayer, 3)]
    [InlineData(DiscMediaType.BdxlRQuadLayer, 4)]
    [InlineData(DiscMediaType.BdReDualLayer, 2)]
    [InlineData(DiscMediaType.BdxlReTripleLayer, 3)]
    public async Task MultiLayerBluRay_IsWrittenDirectlyAndReadBackCorrectly(DiscMediaType type, int layers)
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankBluRay(type);
        var recorder = Recorder(medium);
        byte[] image = IsoLikeImage(2000, seed: 9);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.True(engine.LastResult!.IsBluRay);
        Assert.Equal(layers, engine.LastResult.BdLayerCount);
        Assert.Equal(medium.TotalCapacitySectors, engine.LastResult.BdLayerSectors.Sum());
        Assert.True(OnDisc(medium, 2000).SequenceEqual(image));
    }

    [Theory]
    [InlineData(DiscMediaType.BdRDualLayer, BdFormatMode.SpareAreas, NativeWriteCommands.FormatTypeBdRSpareAreas)]
    [InlineData(DiscMediaType.BdxlRTripleLayer, BdFormatMode.DriveDefault, NativeWriteCommands.FormatTypeDefault)]
    [InlineData(DiscMediaType.BdxlRQuadLayer, BdFormatMode.SpareAreas, NativeWriteCommands.FormatTypeBdRSpareAreas)]
    public async Task MultiLayerBdR_FormatChoice_ReachesTheDrive_ThroughTheEngine(DiscMediaType type, BdFormatMode mode, byte expectedType)
    {
        var engine = Engine(mode);
        using var medium = VirtualDiscMedium.CreateBlankBluRay(type);
        var recorder = Recorder(medium);
        byte[] image = IsoLikeImage(1500, seed: 12);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Equal(expectedType, engine.LastResult!.BdFormatTypeSent);
        Assert.True(OnDisc(medium, 1500).SequenceEqual(image));
    }

    [Fact]
    public void RunLog_NamesTheLayersAndTheirCapacities()
    {
        var drive = BlankBdR(BdR50, 2);
        var notes = new List<string>();
        var writer = new NativeTaoDataWriter(drive, notes.Add, _ => { });
        byte[] image = Image(320);

        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, false));

        Assert.True(result.Success, result.Message);
        Assert.Contains(notes, n => n.Contains("2 layer(s)") && n.Contains("12,219,392") && n.Contains("24,438,784"));
    }
}
