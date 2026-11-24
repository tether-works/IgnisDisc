using System.Buffers.Binary;
using System.IO;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public sealed class BufferCapacityTelemetryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_HwTelemetryTests_" + Guid.NewGuid().ToString("N"));

    public BufferCapacityTelemetryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void BuildReadBufferCapacityCdb_MatchesMmc5Layout()
    {
        // MMC-5 section 6.22 READ BUFFER CAPACITY (OpCode 0x5C), byte 1 bit 0 = Block, bytes 7..8 = Allocation Length
        Assert.Equal(
            new byte[] { 0x5C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x00 },
            MmcCommandBuilder.BuildReadBufferCapacityCdb(block: false, allocationLength: 12));

        Assert.Equal(
            new byte[] { 0x5C, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x00 },
            MmcCommandBuilder.BuildReadBufferCapacityCdb(block: true, allocationLength: 12));
    }

    [Fact]
    public void TryParseBufferCapacity_ParsesByteAndBlockResponses_AndRejectsInvalidPayloads()
    {
        // 4 MiB buffer with 1 MiB blank => 75.0% occupied
        byte[] byteResp = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(byteResp, 10);
        byteResp[3] = 0x00;
        BinaryPrimitives.WriteUInt32BigEndian(byteResp.AsSpan(4, 4), 4u * 1024u * 1024u);
        BinaryPrimitives.WriteUInt32BigEndian(byteResp.AsSpan(8, 4), 1u * 1024u * 1024u);

        Assert.True(MmcResponseParser.TryParseBufferCapacity(byteResp, out var capBytes));
        Assert.False(capBytes.IsBlockUnit);
        Assert.Equal(4u * 1024u * 1024u, capBytes.TotalLength);
        Assert.Equal(1u * 1024u * 1024u, capBytes.BlankLength);
        Assert.Equal(3u * 1024u * 1024u, capBytes.UsedLength);
        Assert.Equal(75.0, capBytes.FillPercent!.Value, 2);

        // Block units: 1000 blocks total, 250 blank => 75.0%
        byte[] blockResp = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(blockResp, 10);
        blockResp[3] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(blockResp.AsSpan(4, 4), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(blockResp.AsSpan(8, 4), 250);

        Assert.True(MmcResponseParser.TryParseBufferCapacity(blockResp, out var capBlocks));
        Assert.True(capBlocks.IsBlockUnit);
        Assert.Equal(75.0, capBlocks.FillPercent!.Value, 2);

        // Blank > Total clamps safely to 0%
        byte[] overBlank = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(overBlank, 10);
        BinaryPrimitives.WriteUInt32BigEndian(overBlank.AsSpan(4, 4), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(overBlank.AsSpan(8, 4), 1500);
        Assert.True(MmcResponseParser.TryParseBufferCapacity(overBlank, out var clamped));
        Assert.Equal(0.0, clamped.FillPercent!.Value, 2);

        // Total == 0 returns null FillPercent
        byte[] zeroTotal = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(zeroTotal, 10);
        Assert.True(MmcResponseParser.TryParseBufferCapacity(zeroTotal, out var zeroCap));
        Assert.Null(zeroCap.FillPercent);

        // Too short or invalid data length rejected
        Assert.False(MmcResponseParser.TryParseBufferCapacity(new byte[11], out _));
        byte[] badHeader = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(badHeader, 4);
        Assert.False(MmcResponseParser.TryParseBufferCapacity(badHeader, out _));
    }

    [Fact]
    public void VirtualMmcDrive_ReadBufferCapacity_SupportsByteBlockOverridesAndRejectsSwitch()
    {
        var drive = new VirtualMmcDrive
        {
            BufferCapacityBytes = 4 * 1024 * 1024,
            SimulatedBufferFillPercent = 62.5
        };

        byte[] buf = new byte[12];
        var resBytes = drive.ExecuteCommand(MmcCommandBuilder.BuildReadBufferCapacityCdb(block: false), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(2));
        Assert.True(resBytes.IsSuccess);
        Assert.True(MmcResponseParser.TryParseBufferCapacity(buf, out var capBytes));
        Assert.False(capBytes.IsBlockUnit);
        Assert.Equal(62.5, capBytes.FillPercent!.Value, 1);

        var resBlocks = drive.ExecuteCommand(MmcCommandBuilder.BuildReadBufferCapacityCdb(block: true), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(2));
        Assert.True(resBlocks.IsSuccess);
        Assert.True(MmcResponseParser.TryParseBufferCapacity(buf, out var capBlocks));
        Assert.True(capBlocks.IsBlockUnit);
        Assert.Equal(2048u, capBlocks.TotalLength);
        Assert.Equal(62.5, capBlocks.FillPercent!.Value, 1);

        drive.RejectsReadBufferCapacity = true;
        var rejected = drive.ExecuteCommand(MmcCommandBuilder.BuildReadBufferCapacityCdb(), buf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(2));
        Assert.False(rejected.IsSuccess);
        Assert.Equal((byte?)0x05, rejected.SenseInfo?.SenseKey);
        Assert.Equal((byte?)0x20, rejected.SenseInfo?.Asc);
    }

    [Fact]
    public void NativeTaoDataWriter_SamplesDualBuffersAndSpeed_AndFallsBackGracefullyWhen0x5CUnsupported()
    {
        byte[] image = new byte[400 * NativeTaoDataWriter.SectorSize];
        new Random(42).NextBytes(image);

        // Supported 0x5C drive
        var supportedDrive = new VirtualMmcDrive
        {
            Profile = NativeSaoAudioWriter.ProfileCdR,
            BufferCapacityBytes = 2 * 1024 * 1024,
            SimulatedBufferFillPercent = 62.5
        };
        var supportedSnapshots = new List<BurnProgressSnapshot>();
        var writerOk = new NativeTaoDataWriter(supportedDrive);
        var resultOk = writerOk.Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false),
            new SyncProgress(supportedSnapshots.Add));

        Assert.True(resultOk.Success, resultOk.Message);
        Assert.NotEmpty(supportedSnapshots);
        Assert.Contains(supportedSnapshots, s => s.DeviceBufferFillPercent is double db && Math.Abs(db - 62.5) < 0.5);
        Assert.Contains(supportedSnapshots, s => s.HostBufferFillPercent.HasValue);
        Assert.Contains(supportedSnapshots, s => s.CurrentLba > 0 && double.IsFinite(s.CurrentSpeedMultiplier));
        Assert.True(supportedDrive.ReadBufferCapacityCommands >= 1);

        // Unsupported 0x5C drive: latches off after 5/20/00 and finishes burn cleanly
        var unsupportedDrive = new VirtualMmcDrive
        {
            Profile = NativeSaoAudioWriter.ProfileCdR,
            RejectsReadBufferCapacity = true
        };
        var notes = new List<string>();
        var unsupportedSnapshots = new List<BurnProgressSnapshot>();
        var writerFallback = new NativeTaoDataWriter(unsupportedDrive, notes.Add);
        var resultFallback = writerFallback.Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false),
            new SyncProgress(unsupportedSnapshots.Add));

        Assert.True(resultFallback.Success, resultFallback.Message);
        Assert.Equal(1, unsupportedDrive.ReadBufferCapacityCommands);
        Assert.Contains(notes, n => n.Contains("0x5C", StringComparison.OrdinalIgnoreCase));
        Assert.All(unsupportedSnapshots.Where(s => s.CurrentActionDescription == "Writing data"), s => Assert.Null(s.DeviceBufferFillPercent));
        Assert.Contains(unsupportedSnapshots, s => s.HostBufferFillPercent.HasValue);
    }

    [Fact]
    public async Task NativeAudioBurnEngine_SamplesDualBuffersAndSpeed_AndHandlesUnsupported0x5C()
    {
        byte[] pcm = new byte[320 * OpticalConstants.RawSectorSize];
        new Random(7).NextBytes(pcm);
        string wavPath = Path.Combine(_tempDir, "track01.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm);

        var track = new AudioTrackSource
        {
            TrackNumber = 1,
            FilePath = wavPath,
            Title = "Track 1",
            Performer = "Artist",
            TotalPcmBytes = pcm.Length,
            PcmStreamFactory = () => WavFileCodec.OpenRedBookPcmStream(wavPath)
        };

        using var recorder = new VirtualDiscRecorder();
        using var medium1 = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium1);

        var snapshotsOk = new List<BurnProgressSnapshot>();
        var engineOk = new NativeAudioBurnEngine(Path.Combine(_tempDir, "logs1"))
        {
            ConfigureVirtualDrive = d => d.SimulatedBufferFillPercent = 75.0
        };
        var burnOk = await engineOk.BurnAudioTracksAsync(recorder, [track], new AudioBurnOptions { UseDiscAtOnce = true },
            new SyncProgress(snapshotsOk.Add), CancellationToken.None);

        Assert.True(burnOk.Success, burnOk.ErrorMessage);
        Assert.Contains(snapshotsOk, s => s.DeviceBufferFillPercent is double db && Math.Abs(db - 75.0) < 0.5);
        Assert.Contains(snapshotsOk, s => s.HostBufferFillPercent.HasValue);

        using var medium2 = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium2);
        VirtualMmcDrive? capturedDrive = null;
        var snapshotsUnsupported = new List<BurnProgressSnapshot>();
        var engineFallback = new NativeAudioBurnEngine(Path.Combine(_tempDir, "logs2"))
        {
            ConfigureVirtualDrive = d =>
            {
                d.RejectsReadBufferCapacity = true;
                capturedDrive = d;
            }
        };
        var burnFallback = await engineFallback.BurnAudioTracksAsync(recorder, [track], new AudioBurnOptions { UseDiscAtOnce = true },
            new SyncProgress(snapshotsUnsupported.Add), CancellationToken.None);

        Assert.True(burnFallback.Success, burnFallback.ErrorMessage);
        Assert.NotNull(capturedDrive);
        Assert.Equal(1, capturedDrive!.ReadBufferCapacityCommands);
        Assert.All(snapshotsUnsupported.Where(s => s.CurrentActionDescription == "Writing audio"), s => Assert.Null(s.DeviceBufferFillPercent));
    }

    private sealed class SyncProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }
}
