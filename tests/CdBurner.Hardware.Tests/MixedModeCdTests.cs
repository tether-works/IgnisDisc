using System.Buffers.Binary;
using CdBurner.Core.Images;
using CdBurner.Core.Verification;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Mixed-mode CDs (a Mode 1 data track, then audio) written Session-at-Once: data form 10h with 2,048-byte sectors
/// for the data, a two-second post-gap after it, audio as usual (libburn's cookbook; the Yellow Book).
/// </summary>
public class MixedModeCdTests
{
    private const int DataSectors = 400;
    private const int AudioSectors = 300;

    private static byte[] Image(int seed = 7)
    {
        var image = new byte[DataSectors * 2048];
        new Random(seed).NextBytes(image);
        return image;
    }

    private static byte[] Pcm(int seed)
    {
        var pcm = new byte[AudioSectors * 2352];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static NativeSaoWriteRequest Request(Func<byte[]>? image = null, int shift = 0) => new(
        new[] { new SaoTrack(1, DataSectors, 150, IsData: true), new SaoTrack(2, AudioSectors, 150), new SaoTrack(3, AudioSectors) },
        i => new MemoryStream(i == 0 ? (image ?? (() => Image()))() : Pcm(i)),
        null, null, TestWrite: false)
    { ShiftSamples = shift };

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request) =>
        new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);

    [Fact]
    public void Layout_PutsATwoSecondPostGapAfterTheDataTrack()
    {
        var layout = SaoAudioLayout.Create(Request().Tracks);

        var data = layout.Tracks[0];
        Assert.True(data.IsData);
        Assert.Equal(-150, data.PreGapLba);
        Assert.Equal(0, data.Index01Lba);
        Assert.Equal(150, data.PostGapSectors);
        Assert.Equal(DataSectors + 150, data.EndLba);
        Assert.Equal(DataSectors + 150, layout.AudioStartLba);          // track 2's pre-gap follows the post-gap
        Assert.Equal(DataSectors + 300, layout.Tracks[1].Index01Lba);
        Assert.Equal(0x40, data.CueControl);
    }

    [Fact]
    public void CueSheet_MarksTheDataTrack_WithControl41hAndDataForm10h()
    {
        var layout = SaoAudioLayout.Create(Request().Tracks);
        byte[] sheet = SaoCueSheetBuilder.Build(layout, null, null);
        var entries = Enumerable.Range(0, sheet.Length / 8).Select(i => sheet.AsSpan(i * 8, 8).ToArray()).ToList();

        Assert.Equal(0x41, entries[0][0]);                                   // lead-in: the first track's control (data)
        Assert.Equal([0x41, 0x01, 0x00, 0x10], entries[1][..4]);             // track 1 INDEX 00, Mode 1
        Assert.Equal([0x41, 0x01, 0x01, 0x10], entries[2][..4]);             // track 1 INDEX 01, Mode 1
        Assert.Equal([0x01, 0x02, 0x00, 0x00], entries[3][..4]);             // track 2 INDEX 00, audio
        Assert.Equal([0x01, 0xAA, 0x01, 0x01], entries[^1][..4]);            // lead-out: the last track's control (audio)
    }

    [Fact]
    public void MixedModeDisc_IsWritten_AndBothPartsReadBackCorrectly()
    {
        var drive = new VirtualMmcDrive { ReadOffsetSamples = 30 };

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.Equal(150 + DataSectors + 150, result.DataSectorsWritten);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        Assert.Equal(30, result.Verification.OffsetSamples);                 // the audio offset is found after the data
        Assert.Equal(0x40, drive.RecordedTracks[0].CueControl);
        Assert.Equal(0x00, drive.RecordedTracks[1].CueControl);

        // The drive recorded real Mode 1 sectors: the data reads back through the sector codec, address included.
        byte[] raw = drive.ProgramAreaWritten.GetBuffer().AsSpan(150 * 2352, 2352).ToArray();
        Assert.Equal(Image().AsSpan(0, 2048).ToArray(), RawSectorCodec.Extract2048From2352(raw, out bool valid));
        Assert.True(valid);
    }

    [Fact]
    public void DataThatDiffersOnTheDisc_FailsTheCheck_AndSaysWhere()
    {
        int opens = 0;
        byte[] Changing() => opens++ == 0 ? Image() : Image(seed: 8); // the check reads a different image than was written

        var result = Run(new VirtualMmcDrive(), Request(Changing));

        Assert.False(result.Success);
        Assert.Contains("data track sectors read back differently, the first at LBA 0", result.Message);
    }

    [Theory]
    [InlineData(-30, 12)]
    [InlineData(30, -12)]
    [InlineData(-6, 6)]
    [InlineData(-700, 24)]
    [InlineData(700, -24)]
    public void WriteOffsetShift_MovesAudioPartOnly_SoDiscHasByteExactDataAndSampleExactAudio(int writeOffset, int readOffset)
    {
        var plain = new VirtualMmcDrive();
        Assert.True(Run(plain, Request(shift: 0)).Success);
        byte[] expected = plain.RecordedProgram();

        var moved = new VirtualMmcDrive { WriteOffsetSamples = writeOffset, ReadOffsetSamples = readOffset };
        var notes = new List<string>();
        var result = new NativeSaoAudioWriter(moved, notes.Add, _ => { }).Write(Request(shift: -writeOffset));

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        Assert.Equal(writeOffset + readOffset, result.Verification.OffsetSamples);
        Assert.Contains(notes, n => n.Contains("shifting the audio part"));

        int dataBytes = (150 + DataSectors + 150) * 2352;
        byte[] actual = moved.RecordedProgram();
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(actual.AsSpan(0, dataBytes).SequenceEqual(expected.AsSpan(0, dataBytes)));

        byte[] image = Image();
        Assert.Equal(image.AsSpan(0, 2048).ToArray(), RawSectorCodec.Extract2048From2352(actual.AsSpan(150 * 2352, 2352), out bool validFirst));
        Assert.True(validFirst);
        Assert.Equal(image.AsSpan((DataSectors - 1) * 2048, 2048).ToArray(),
            RawSectorCodec.Extract2048From2352(actual.AsSpan((150 + DataSectors - 1) * 2352, 2352), out bool validLast));
        Assert.True(validLast);

        int tailSilenceBytes = Math.Max(0, -writeOffset) * 4;
        int exactAudioBytes = actual.Length - dataBytes - tailSilenceBytes;
        Assert.True(actual.AsSpan(dataBytes, exactAudioBytes).SequenceEqual(expected.AsSpan(dataBytes, exactAudioBytes)));
        if (tailSilenceBytes > 0)
        {
            Assert.True(actual.AsSpan(actual.Length - tailSilenceBytes).IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [Fact]
    public void Mode2XaMixedDisc_WithWriteOffsetCompensation_KeepsSubheadersByteExact_AndAudioSampleExact()
    {
        byte[] xaImage = BuildInterleavedMode2Xa2336(DataSectors);
        NativeSaoWriteRequest XaRequest(int shift) => new(
            new[]
            {
                new SaoTrack(1, DataSectors, 150, DataMode: SaoDataMode.Mode2Xa),
                new SaoTrack(2, AudioSectors, 150)
            },
            i => new MemoryStream(i == 0 ? xaImage : Pcm(i)),
            null, null, TestWrite: false)
        { ShiftSamples = shift };

        var plain = new VirtualMmcDrive();
        Assert.True(Run(plain, XaRequest(shift: 0)).Success);
        byte[] expected = plain.RecordedProgram();

        var moved = new VirtualMmcDrive { WriteOffsetSamples = -24, ReadOffsetSamples = 12 };
        var result = Run(moved, XaRequest(shift: 24));

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        Assert.Equal(-12, result.Verification.OffsetSamples);

        int dataBytes = (150 + DataSectors + 150) * 2352;
        byte[] actual = moved.RecordedProgram();
        Assert.True(actual.AsSpan(0, dataBytes).SequenceEqual(expected.AsSpan(0, dataBytes)));
        Assert.True(actual.AsSpan(dataBytes, actual.Length - dataBytes - 24 * 4).SequenceEqual(
            expected.AsSpan(dataBytes, expected.Length - dataBytes - 24 * 4)));
    }

    [Fact]
    public void ShiftLongerThanAudioPart_StopsBeforeWriting_AndLeavesDiscBlank()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, Request(shift: (150 + 2 * AudioSectors) * 588));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
        Assert.False(drive.Recorded);
    }

    [Fact]
    public void OnlyTrackOneCanBeData()
    {
        var tracks = new[] { new SaoTrack(1, AudioSectors, 150), new SaoTrack(2, DataSectors, IsData: true) };

        var ex = Assert.Throws<ArgumentException>(() => SaoAudioLayout.Create(tracks));
        Assert.Contains("only track 1", ex.Message);
    }

    [Fact]
    public void TheDiscCheck_ReadsAMixedModeDiscCleanly()
    {
        var drive = new VirtualMmcDrive();
        Assert.True(Run(drive, Request()).Success);

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict);
        Assert.False(report.Tracks[0].IsAudio);
        Assert.True(report.Tracks[1].IsAudio);
    }

    private static byte[] BuildInterleavedMode2Xa2336(int sectors)
    {
        var rng = new Random(42);
        var buf = new byte[sectors * 2336];
        for (int i = 0; i < sectors; i++)
        {
            var slot = buf.AsSpan(i * 2336, 2336);
            bool form2 = i % 3 == 1;
            byte file = (byte)(1 + (i % 4));
            byte channel = (byte)(i % 7);
            byte submode = form2 ? (byte)0x64 : (byte)(i == sectors - 1 ? 0x89 : 0x08);
            byte coding = form2 ? (byte)0x05 : (byte)0x00;
            slot[0] = slot[4] = file;
            slot[1] = slot[5] = channel;
            slot[2] = slot[6] = submode;
            slot[3] = slot[7] = coding;
            rng.NextBytes(slot.Slice(8, form2 ? 2324 : 2048));
        }
        return buf;
    }

    [Fact]
    public void Mode2XaMixedDisc_WritesForm1AndForm2WithSubheaders_FullToc_TrackInfo_AndHealthScan()
    {
        byte[] xaImage = BuildInterleavedMode2Xa2336(DataSectors);
        var drive = new VirtualMmcDrive { ReadOffsetSamples = 24 };
        var request = new NativeSaoWriteRequest(
            new[]
            {
                new SaoTrack(1, DataSectors, 150, DataMode: SaoDataMode.Mode2Xa),
                new SaoTrack(2, AudioSectors, 150)
            },
            i => new MemoryStream(i == 0 ? xaImage : Pcm(i)),
            null, null, TestWrite: false);

        var result = Run(drive, request);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        Assert.Equal(150 + DataSectors + 150, result.DataSectorsWritten);
        Assert.Equal(24, result.Verification.OffsetSamples);

        byte[] program = drive.ProgramAreaWritten.ToArray();
        // Pre-gap (LBA -150..-1) and post-gap (LBA 400..549) carry canonical XA Form 1 Data empty sub-header 00 00 08 00.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x00 }, program.AsSpan(16, 8).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x00 }, program.AsSpan((150 + DataSectors) * 2352 + 16, 8).ToArray());

        // Full TOC Point 0xA0 PSEC == 0x20 (CD-ROM XA).
        byte[] fullToc = new byte[512];
        var tocRes = drive.ExecuteCommand(MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x02, 0, (ushort)fullToc.Length), fullToc, 2, TimeSpan.FromSeconds(5));
        Assert.True(tocRes.IsSuccess);
        int tocLen = BinaryPrimitives.ReadUInt16BigEndian(fullToc) + 2;
        byte[]? pointA0 = Enumerable.Range(0, (tocLen - 4) / 11)
            .Select(i => fullToc.AsSpan(4 + i * 11, 11).ToArray())
            .FirstOrDefault(e => e[3] == 0xA0);
        Assert.NotNull(pointA0);
        Assert.Equal(0x20, pointA0![9]);

        // READ TRACK INFORMATION track 1 Data Mode == 0x02 (Mode 2 XA).
        byte[] trackInfo = new byte[36];
        byte[] rtiCdb = [MmcCommandBuilder.OpCodeReadTrackInformation, 0x01, 0, 0, 0, 1, 0, 0, 36, 0];
        var rtiRes = drive.ExecuteCommand(rtiCdb, trackInfo, 2, TimeSpan.FromSeconds(5));
        Assert.True(rtiRes.IsSuccess);
        Assert.Equal(0x02, trackInfo[6] & 0x0F);

        // DiscHealthScanner reads Mode 2 XA + audio cleanly.
        var report = new DiscHealthScanner(drive).Scan();
        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict);
        Assert.False(report.Tracks[0].IsAudio);
        Assert.True(report.Tracks[1].IsAudio);
    }

    [Fact]
    public void Mode2XaMixedDisc_FallsBackToCdDaLeadIn_WhenDriveRejectsXaLeadInDataForm()
    {
        byte[] xaImage = BuildInterleavedMode2Xa2336(DataSectors);
        var drive = new VirtualMmcDrive { RejectsXaLeadInDataForm = true };
        var request = new NativeSaoWriteRequest(
            new[]
            {
                new SaoTrack(1, DataSectors, 150, DataMode: SaoDataMode.Mode2Xa),
                new SaoTrack(2, AudioSectors, 150)
            },
            i => new MemoryStream(i == 0 ? xaImage : Pcm(i)),
            null, null, TestWrite: false);

        var result = Run(drive, request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(CueSheetVariant.CdDaLeadInForXa, result.CueSheetVariantAccepted);
        Assert.Equal(0x01, drive.RecordedCueSheet![3]);
    }

    [Fact]
    public void Mode2XaVerifier_CatchesSubheaderDifferenceAtExactLba()
    {
        byte[] written = BuildInterleavedMode2Xa2336(DataSectors);
        byte[] tampered = (byte[])written.Clone();
        int badSector = 17;
        tampered[badSector * 2336 + 1] ^= 0x04; // channel byte differs on read-back
        int opens = 0;
        var request = new NativeSaoWriteRequest(
            new[]
            {
                new SaoTrack(1, DataSectors, 150, DataMode: SaoDataMode.Mode2Xa),
                new SaoTrack(2, AudioSectors, 150)
            },
            i => new MemoryStream(i == 0 ? (opens++ == 0 ? written : tampered) : Pcm(i)),
            null, null, TestWrite: false);

        var result = Run(new VirtualMmcDrive(), request);

        Assert.False(result.Success);
        Assert.Contains($"1 of {DataSectors + 150} data track sectors read back differently, the first at LBA {badSector}.", result.Message);
    }
}
