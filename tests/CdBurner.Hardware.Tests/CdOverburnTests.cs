using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Opt-in CD-R overburning on the simulated drive. The expected numbers come from the Red Book / MMC addressing, not
/// from the code: absolute MSF 99:59:74 is (99 x 60 + 59) x 75 + 74 = 449,999 frames, which is LBA 449,849 once the 150-frame
/// lead-in offset is taken off; an 80-minute CD-R's ATIP lead-out starts at 79:59:74, LBA 359,849.
/// </summary>
public sealed class CdOverburnTests
{
    private const byte OpWrite = 0x2A, OpCueSheet = 0x5D;
    private const int Atip80MinLeadOutLba = (79 * 60 + 59) * 75 + 74 - 150; // 359,849

    // A small simulated disc keeps the tests fast: its ATIP lead-out is at 00:10:00 (LBA 600).
    private static VirtualMmcDrive SmallDisc(ushort profile = NativeSaoAudioWriter.ProfileCdR, int driveOverburnSectors = 0) => new()
    {
        Profile = profile,
        LastLeadOutStart = new Msf(0, 10, 0),
        OverburnLimitSectors = driveOverburnSectors
    };

    private static byte[] Pcm(int track, int sectors) =>
        Enumerable.Range(0, sectors * 2352).Select(i => (byte)(i * 7 + track * 31)).ToArray();

    /// <summary>Two tracks of <paramref name="sectors"/> each with a 150-sector first pre-gap and a second with none:
    /// the lead-out starts at LBA 2 x sectors, so 300 each ends at 600 (the ATIP) and 375 each at 750 (150 past it).</summary>
    private static NativeSaoWriteRequest Request(int sectors, CdOverburnPolicy? overburn, bool testWrite = false) => new(
        new[] { new SaoTrack(1, sectors, 150), new SaoTrack(2, sectors, 0) },
        i => new MemoryStream(Pcm(i + 1, sectors)),
        CdTextPacks: null,
        MediaCatalogNumber: null,
        TestWrite: testWrite)
    {
        Overburn = overburn
    };

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request, List<string>? notes = null) =>
        new NativeSaoAudioWriter(drive, notes == null ? null : notes.Add, sleep: _ => { }).Write(request);

    // ---- The policy numbers ----

    [Fact]
    public void Policy_IsOffByDefault_WithAThreeMinuteCeiling()
    {
        Assert.False(new CdOverburnPolicy().Allowed);
        Assert.False(CdOverburnPolicy.Off.Allowed);
        Assert.Equal(13_500, CdOverburnPolicy.DefaultMaxExtraSectors); // 3 min x 60 s x 75 sectors
        Assert.Equal(13_500, CdOverburnPolicy.AllowedForMinutes(3).MaxExtraSectors);
    }

    [Fact]
    public void HardCeiling_Is_99_59_74()
    {
        Assert.Equal(449_849, CdOverburnPolicy.HardCeilingLeadOutLba);
        Assert.Equal("99:59:74", Msf.FromLba(CdOverburnPolicy.HardCeilingLeadOutLba).ToString());
    }

    [Fact]
    public void Ceiling_IsTheAtipLeadOutPlusTheAllowance_NeverPastTheHardCeiling()
    {
        Assert.Equal(Atip80MinLeadOutLba + 13_500, new CdOverburnPolicy(true).CeilingLeadOutLba(Atip80MinLeadOutLba));
        Assert.Equal(Atip80MinLeadOutLba, CdOverburnPolicy.Off.CeilingLeadOutLba(Atip80MinLeadOutLba));
        Assert.Equal(449_849, new CdOverburnPolicy(true, 1_000_000).CeilingLeadOutLba(Atip80MinLeadOutLba));
        Assert.Equal(449_849, new CdOverburnPolicy(true, int.MaxValue).CeilingLeadOutLba(440_000));
    }

    [Fact]
    public void Check_AcceptsUpToTheCeilingAndRefusesOneSectorMore()
    {
        var on = new CdOverburnPolicy(true);
        Assert.Null(on.Check(Atip80MinLeadOutLba, Atip80MinLeadOutLba));
        Assert.Null(on.Check(Atip80MinLeadOutLba + 13_500, Atip80MinLeadOutLba));
        Assert.NotNull(on.Check(Atip80MinLeadOutLba + 13_501, Atip80MinLeadOutLba));
        Assert.NotNull(CdOverburnPolicy.Off.Check(Atip80MinLeadOutLba + 1, Atip80MinLeadOutLba));
        Assert.Contains("99:59:74", new CdOverburnPolicy(true, int.MaxValue).Check(449_850, Atip80MinLeadOutLba));
    }

    [Fact]
    public void Excess_IsReportedAsTimeAndMegabytes()
    {
        Assert.Equal("03:00", CdOverburnPolicy.FormatExcessTime(13_500));
        Assert.Equal("00:01", CdOverburnPolicy.FormatExcessTime(1));
        Assert.Equal(27.648, CdOverburnPolicy.ExcessDataMegabytes(13_500), 3); // 13,500 x 2,048 bytes
    }

    // ---- Disc-at-Once writer ----

    [Fact]
    public void WithoutOptIn_AProgramPastTheAtipIsRefusedBeforeAnythingIsSent()
    {
        var drive = SmallDisc(driveOverburnSectors: 10_000); // even a drive that would take it
        var result = Run(drive, Request(375, overburn: null));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.DoesNotContain(OpCueSheet, drive.CommandsReceived);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
        Assert.False(drive.Recorded);
    }

    [Fact]
    public void WithOptIn_AProgramWithinTheCeilingIsWrittenInFullAndLogged()
    {
        var drive = SmallDisc(driveOverburnSectors: 300);
        var notes = new List<string>();

        var result = Run(drive, Request(375, new CdOverburnPolicy(true, MaxExtraSectors: 300)), notes);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.True(drive.Recorded);
        Assert.Equal(750, drive.RecordedLeadOutLba); // 150 sectors past the ATIP lead-out at LBA 600
        Assert.Equal(900, result.AudioSectorsWritten); // 150 pre-gap + 375 + 375 sectors, from LBA -150 to 750
        Assert.Contains(notes, n => n.StartsWith("ATIP capacity: lead-out starts 00:10:00 (sector 600)"));
        Assert.Contains(notes, n => n.Contains("WARNING: overburning 150 sectors (00:02)"));
        Assert.Contains(notes, n => n.StartsWith("Sent 900 sectors in total"));
    }

    [Fact]
    public void WithOptIn_AProgramWithinTheAtipNeedsNoOverburnAndSaysNothingAboutIt()
    {
        var drive = SmallDisc();
        var notes = new List<string>();

        var result = Run(drive, Request(300, new CdOverburnPolicy(true)), notes); // ends exactly at the ATIP lead-out, LBA 600

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(notes, n => n.Contains("overburning"));
        Assert.Contains(notes, n => n.StartsWith("ATIP capacity:"));
    }

    [Fact]
    public void WithOptIn_AProgramPastTheLimitIsRefusedBeforeAnythingIsSent()
    {
        var drive = SmallDisc(driveOverburnSectors: 10_000);
        var result = Run(drive, Request(375, new CdOverburnPolicy(true, MaxExtraSectors: 149))); // 150 are needed

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("overburn limit", result.Message);
        Assert.DoesNotContain(OpCueSheet, drive.CommandsReceived);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    [Fact]
    public void ProgramPast_99_59_74_IsRefusedWhateverTheLimit()
    {
        // 449,850 sectors would put the lead-out one frame past the last CD address. The audio is never opened.
        // The layout itself refuses it ("longer than any CD"), before the policy is even asked.
        var drive = new VirtualMmcDrive();
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, 449_850, 150) },
            _ => throw new InvalidOperationException("The audio must not be read."),
            CdTextPacks: null, MediaCatalogNumber: null, TestWrite: false)
        {
            Overburn = new CdOverburnPolicy(true, int.MaxValue)
        };

        var result = Run(drive, request);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("longer than any CD", result.Message);
        Assert.DoesNotContain(OpCueSheet, drive.CommandsReceived);
    }

    [Fact]
    public void DriveThatWillNotRecordPastTheAtip_StopsBeforeTheFirstWrite()
    {
        var drive = SmallDisc(driveOverburnSectors: 0); // the host allows it, the drive's cue sheet check does not
        var result = Run(drive, Request(375, new CdOverburnPolicy(true, 300)));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("rejected every cue sheet layout", result.Message);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    [Fact]
    public void DriveThatOnlyTakesPartOfTheOverburn_RefusesTheRest()
    {
        var drive = SmallDisc(driveOverburnSectors: 100); // 150 are needed
        var result = Run(drive, Request(375, new CdOverburnPolicy(true, 300)));

        Assert.True(result.StoppedBeforeWriting);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    [Fact]
    public void CdRw_RefusesOverburnEvenForAProgramThatFits()
    {
        var drive = SmallDisc(NativeSaoAudioWriter.ProfileCdRw, driveOverburnSectors: 300);
        var result = Run(drive, Request(300, new CdOverburnPolicy(true)));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("only allowed on a CD-R", result.Message);
        Assert.Equal(0, drive.CommandsReceived.Count(c => c == OpWrite));
    }

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusR)]
    [InlineData(NativeTaoDataWriter.ProfileDvdPlusR)]
    [InlineData(NativeTaoDataWriter.ProfileBdR)]
    public void DvdAndBluRay_RefuseOverburnInTheDiscAtOnceWriter(ushort profile)
    {
        var drive = SmallDisc(profile, driveOverburnSectors: 300);
        var result = Run(drive, Request(375, new CdOverburnPolicy(true, 300)));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    // ---- Track-at-Once data writer ----

    private static NativeDataWriteResult WriteData(VirtualMmcDrive drive, int sectors, CdOverburnPolicy? overburn)
    {
        byte[] image = new byte[sectors * 2048];
        new Random(5).NextBytes(image);
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false) { Overburn = overburn });
    }

    [Fact]
    public void DataWriter_WithoutOptIn_KeepsRefusingAnImageThatDoesNotFit()
    {
        var drive = SmallDisc(driveOverburnSectors: 1_000);
        var result = WriteData(drive, 600, overburn: null);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DataWriter_TrackAtOnce_CannotOverburn_EvenWithOptIn()
    {
        var drive = SmallDisc(driveOverburnSectors: 1_000);
        var result = WriteData(drive, 600, new CdOverburnPolicy(true));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("Disc-at-Once", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DataWriter_WithOptIn_StillWritesAnImageThatFits_AndLogsTheExactSectorCount()
    {
        var drive = SmallDisc();
        var notes = new List<string>();
        byte[] image = new byte[400 * 2048];
        new Random(9).NextBytes(image);
        var writer = new NativeTaoDataWriter(drive, notes.Add, sleep: _ => { });

        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false)
        {
            Overburn = new CdOverburnPolicy(true)
        });

        Assert.True(result.Success, result.Message);
        Assert.Contains(notes, n => n.StartsWith("ATIP capacity:"));
        Assert.Contains(notes, n => n.StartsWith("Sent exactly 400 sectors"));
    }

    [Theory]
    [InlineData(NativeSaoAudioWriter.ProfileCdRw)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusR)]
    [InlineData(NativeTaoDataWriter.ProfileDvdPlusR)]
    [InlineData(NativeTaoDataWriter.ProfileBdR)]
    public void DataWriter_RefusesOverburnOnAnythingButCdR(ushort profile)
    {
        var drive = SmallDisc(profile);
        var result = WriteData(drive, 400, new CdOverburnPolicy(true));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("only allowed on a CD-R", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- Whole burn through the engine ----

    private static List<AudioTrackSource> Tracks(int sectors) =>
    [
        new AudioTrackSource { TrackNumber = 1, Title = "One", Performer = "Artist", PreGapFrames = 150, TotalPcmBytes = (long)sectors * 2352, PcmStreamFactory = () => new MemoryStream(Pcm(1, sectors)) },
        new AudioTrackSource { TrackNumber = 2, Title = "Two", Performer = "Artist", TotalPcmBytes = (long)sectors * 2352, PcmStreamFactory = () => new MemoryStream(Pcm(2, sectors)) }
    ];

    private static VirtualDiscRecorder SmallCdR()
    {
        // 700 sectors: the virtual drive reports its ATIP lead-out at LBA 699.
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(new VirtualDiscMedium(DiscMediaType.CdR, 700, isBlank: true));
        return recorder;
    }

    private static async Task<(BurnOperationResult Result, NativeAudioBurnEngine Engine, VirtualDiscRecorder Recorder)> Burn(
        CdOverburnPolicy policy, int driveOverburnSectors, int trackSectors)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var recorder = SmallCdR();
        var engine = new NativeAudioBurnEngine(dir)
        {
            Wait = _ => { },
            ConfigureVirtualDrive = d => d.OverburnLimitSectors = driveOverburnSectors
        };
        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(trackSectors),
            new AudioBurnOptions { UseDiscAtOnce = true, Overburn = policy }, null, CancellationToken.None);
        return (result, engine, recorder);
    }

    [Fact]
    public async Task Engine_DefaultRefusesAnOverlongProgram_AndWritesNothing()
    {
        var (result, engine, recorder) = await Burn(CdOverburnPolicy.Off, driveOverburnSectors: 1_000, trackSectors: 450); // ends at 750 > 699

        Assert.False(result.Success);
        Assert.Contains("does not fit", result.ErrorMessage);
        Assert.True(engine.LastResult!.StoppedBeforeWriting);
        Assert.True(recorder.CurrentMedium!.IsBlank);
        recorder.Dispose();
    }

    [Fact]
    public async Task Engine_OverburnWithinTheCeiling_WritesAndVerifiesTheWholeProgram()
    {
        var (result, engine, recorder) = await Burn(new CdOverburnPolicy(true, 300), driveOverburnSectors: 300, trackSectors: 450);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.LastResult!.Verification!.Passed);
        var medium = recorder.CurrentMedium!;
        Assert.False(medium.IsBlank);
        Assert.Equal(1_050, result.SectorsWritten); // 150 + 450 + 450 from LBA -150 to 900
        Assert.Equal(900, medium.TotalCapacitySectors); // the disc now holds what was recorded past its rated size
        string log = await File.ReadAllTextAsync(engine.LastLogPath!);
        Assert.Contains("ATIP capacity: lead-out starts", log);
        Assert.Contains("overburning 201 sectors", log);
        Assert.Contains("Sent 1,050 sectors in total", log);
        recorder.Dispose();
    }

    [Fact]
    public async Task Engine_OverburnAboveTheCeiling_IsRefused()
    {
        var (result, engine, recorder) = await Burn(new CdOverburnPolicy(true, 200), driveOverburnSectors: 1_000, trackSectors: 450); // 201 needed

        Assert.False(result.Success);
        Assert.Contains("overburn limit", result.ErrorMessage);
        Assert.True(recorder.CurrentMedium!.IsBlank);
        recorder.Dispose();
    }

    [Fact]
    public async Task Engine_OverburnOnTrackAtOnceAudio_IsRefused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var recorder = SmallCdR();
        var engine = new NativeAudioBurnEngine(dir) { Wait = _ => { } };

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(300),
            new AudioBurnOptions { UseDiscAtOnce = false, Overburn = new CdOverburnPolicy(true) }, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Disc-at-Once", result.ErrorMessage);
        Assert.True(recorder.CurrentMedium!.IsBlank);
    }
}
