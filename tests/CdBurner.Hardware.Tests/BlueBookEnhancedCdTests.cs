using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The Enhanced CD (Blue Book) layout of session 2 on the simulated drive, checked only through drive commands against
/// what the structure must be:
/// <list type="bullet">
/// <item>session 2's lead-in names a CD-ROM XA session: READ TOC format 2, point A0h, PSEC = 20h (MMC-3 Table 237: disc
/// type 00h CD-DA/CD-ROM, 10h CD-I, 20h CD-ROM XA); session 1 stays CD-DA;</item>
/// <item>its track is Mode 2: READ TRACK INFORMATION byte 6 data mode 2 (MMC-5 6.27.3.4), READ CD returns Form 1
/// sectors (sector type 4) with header mode byte 02h, the sub-header twice (CD-ROM XA: file, channel, submode with the
/// Data bit 08h, coding) and a valid EDC over bytes 16-2071 (ECMA-130 14.3, computed here independently), and refuses
/// them as Mode 1 (5/64/00);</item>
/// <item>the ISO 9660 volume is XA: "CD-XA001" at byte 1024 of the Primary Volume Descriptor and the 14-byte XA record
/// in each directory record (genisoimage rock.c gen_xa; attributes from libcdio xa.h: 8D55h directory, 0D55h file);</item>
/// <item><c>CDPLUS/INFO.CDP</c> and <c>PICTURES/</c> sit next to the user's folders, and INFO.CDP is found the two ways
/// cdrkit's icedax (cd_extra.c, toc.c) looks for it: at session start + 75, and through the path table.</item>
/// </list>
/// </summary>
public sealed class BlueBookEnhancedCdTests : IDisposable
{
    private const int AudioSectorSize = 2352;
    private const int TrackSectors = 300;
    private const int AudioLeadOutLba = 750;
    private const int SessionTwoStartLba = AudioLeadOutLba + 6_750 + 4_500 + 150;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_bluebook_{Guid.NewGuid():N}");

    public BlueBookEnhancedCdTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly BlueBookDiscInfo Info = new("Night Album", "The Players",
        new[] { new BlueBookTrackInfo(1, "One", "The Players"), new BlueBookTrackInfo(2, "Two", "Guest Singer") });

    private static byte[] Content(int seed, int bytes)
    {
        byte[] data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static readonly Dictionary<string, byte[]> UserFiles = new()
    {
        ["ARCHIVE/NOTES.TXT"] = Content(1, 3_000),
        ["ARCHIVE/SCANS/PAGE1.BIN"] = Content(2, 2_048 * 5),
        ["README.TXT"] = Content(3, 700)
    };

    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], 359_849);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    /// <summary>A CD-R with two audio tracks written Session-at-Once and left open for another session.</summary>
    private static VirtualMmcDrive DiscWithOpenAudioSession()
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, WriteSpeedPerformanceResponse = Speeds(1764) };
        var audio = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, TrackSectors, 150), new SaoTrack(2, TrackSectors, 150, PreGapAudioSectors: 150) },
            i => new MemoryStream(Content(10 + i, (TrackSectors + (i == 1 ? 150 : 0)) * AudioSectorSize)),
            CdTextPacks: null,
            MediaCatalogNumber: null,
            TestWrite: false) { LeaveSessionOpen = true });
        Assert.True(audio.Success, audio.Message);
        return drive;
    }

    private static MemoryStream BlueBookImage(int startLba)
    {
        var root = CompilationDirectory.CreateRoot();
        foreach (var (path, data) in UserFiles)
        {
            int slash = path.LastIndexOf('/');
            var directory = slash < 0 ? root : root.AddDirectory(path[..slash]);
            directory.AddFile(new CompilationFile(path[(slash + 1)..], data));
        }
        BlueBookLayout.Apply(root, Info);
        var options = BlueBookLayout.WithBlueBook(new IsoBuildOptions(VolumeLabel: "EXTRA", IncludeUdf: false));
        return IsoUdfImageBuilder.BuildToMemoryStream(root, options, startLba);
    }

    private static ScsiExecutionResult Send(VirtualMmcDrive drive, byte[] cdb, byte[] buffer) =>
        drive.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));

    private static byte[] Execute(VirtualMmcDrive drive, byte[] cdb, int length)
    {
        byte[] buffer = new byte[length];
        var r = Send(drive, cdb, buffer);
        Assert.True(r.IsSuccess, $"command 0x{cdb[0]:X2} failed: {r.SenseInfo}");
        return buffer.AsSpan(0, (int)r.DataTransferLength).ToArray();
    }

    private static byte[] ReadSector(VirtualMmcDrive drive, int lba) => Execute(drive, MmcCommandBuilder.BuildRead10Cdb(lba, 1, forceUnitAccess: true), 2048);

    /// <summary>The Blue Book data session written to the open audio disc at the drive's own next writable address.</summary>
    private static (VirtualMmcDrive Drive, int Nwa, NativeDataWriteResult Result) BurnBlueBookSession()
    {
        var drive = DiscWithOpenAudioSession();
        byte[] info = Execute(drive, MmcCommandBuilder.BuildReadDiscInformationCdb(34), 34);
        int nwa = BinaryPrimitives.ReadInt32BigEndian(Execute(drive, NativeWriteCommands.BuildReadTrackInformation(info[6]), 36).AsSpan(12));
        Assert.Equal(SessionTwoStartLba, nwa);

        using var image = BlueBookImage(nwa);
        var result = new NativeTaoDataWriter(drive, sleep: _ => { }).Write(
            new NativeDataWriteRequest(image, 0, image.Length, TestWrite: false, CloseDisc: true, SessionStartLba: nwa) { Mode2Form1 = true });
        return (drive, nwa, result);
    }

    [Fact]
    public void BlueBookSession_IsACdRomXaMode2Form1SessionAfterTheAudio()
    {
        var (drive, nwa, result) = BurnBlueBookSession();
        Assert.True(result.Success, result.Message);
        Assert.Equal("track-at-once, mode 2 form 1 (CD-ROM XA)", result.WriteMode);
        Assert.Contains("Data mode 2 (CD-ROM XA).", result.Message);

        // Two sessions, the disc closed.
        byte[] info = Execute(drive, MmcCommandBuilder.BuildReadDiscInformationCdb(34), 34);
        Assert.Equal(2, info[2] & 0x03);
        Assert.Equal(2, info[4]);

        // READ TOC format 2: per session, A0h PMIN = first track, PSEC = disc type.
        byte[] toc = Execute(drive, MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x02, 1, 2048), 2048);
        int length = BinaryPrimitives.ReadUInt16BigEndian(toc) + 2;
        Assert.Equal(1, toc[2]);
        Assert.Equal(2, toc[3]);
        var a0 = new Dictionary<int, (int FirstTrack, int DiscType)>();
        var tracks = new Dictionary<int, (int Session, int Control)>();
        for (int at = 4; at + 11 <= length; at += 11)
        {
            int session = toc[at], adr = toc[at + 1] >> 4, control = toc[at + 1] & 0x0F, point = toc[at + 3];
            if (adr == 1 && point == 0xA0) a0[session] = (toc[at + 8], toc[at + 9]);
            if (adr == 1 && point is >= 1 and <= 99) tracks[point] = (session, control);
        }
        Assert.Equal((1, 0x00), a0[1]);         // session 1: CD-DA
        Assert.Equal((3, 0x20), a0[2]);         // session 2: CD-ROM XA, from track 3
        Assert.Equal((1, 0), (tracks[1].Session, tracks[1].Control & 0x04));
        Assert.Equal((1, 0), (tracks[2].Session, tracks[2].Control & 0x04));
        Assert.Equal((2, 4), (tracks[3].Session, tracks[3].Control & 0x04));

        // READ TRACK INFORMATION of track 3: data track (track mode 4), data mode 2.
        byte[] track3 = Execute(drive, NativeWriteCommands.BuildReadTrackInformation(3), 36);
        Assert.Equal(nwa, BinaryPrimitives.ReadInt32BigEndian(track3.AsSpan(8)));
        Assert.Equal(4, track3[5] & 0x0F);
        Assert.Equal(2, track3[6] & 0x0F);

        // READ CD of the volume descriptor sector as Form 1 with everything (F8h): Mode 2 header and XA sub-header.
        int pvdLba = nwa + 16;
        byte[] raw = Execute(drive, MmcCommandBuilder.BuildReadCdCdb(pvdLba, 1, expectedSectorType: 4, mainChannelSelectionBits: 0xF8), 2352);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }, raw[..12]);
        var msf = Msf.FromLba(pvdLba);
        Assert.Equal(new byte[] { Bcd(msf.Minute), Bcd(msf.Second), Bcd(msf.Frame), 0x02 }, raw[12..16]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x00 }, raw[16..24]);
        Assert.Equal(ReadSector(drive, pvdLba), raw[24..2072]);
        Assert.Equal(Edc(raw.AsSpan(16, 2056)), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(2072)));

        // The same sector as user data (10h) gives the 2,048 bytes; as Mode 1 (sector type 2) it is refused.
        Assert.Equal(raw[24..2072], Execute(drive, MmcCommandBuilder.BuildReadCdCdb(pvdLba, 1, expectedSectorType: 4, mainChannelSelectionBits: 0x10), 2048));
        var asMode1 = Send(drive, MmcCommandBuilder.BuildReadCdCdb(pvdLba, 1, expectedSectorType: 2, mainChannelSelectionBits: 0x10), new byte[2048]);
        Assert.False(asMode1.IsSuccess);
        Assert.Equal((byte)0x05, asMode1.SenseInfo!.SenseKey);
        Assert.Equal((byte)0x64, asMode1.SenseInfo!.Asc);
    }

    [Fact]
    public void BlueBookSession_VolumeIsXa_WithCdplusInfoAndPicturesNextToTheUserFolders()
    {
        var (drive, nwa, result) = BurnBlueBookSession();
        Assert.True(result.Success, result.Message);

        byte[] pvd = ReadSector(drive, nwa + 16);
        Assert.Equal("\u0001CD001", Encoding.ASCII.GetString(pvd, 0, 6));
        Assert.Equal("CD-XA001", Encoding.ASCII.GetString(pvd, 1024, 8));

        // Root directory (record at PVD byte 156: extent at 158, little-endian; size at 166).
        var root = ReadDirectory(drive, BinaryPrimitives.ReadInt32LittleEndian(pvd.AsSpan(158)), BinaryPrimitives.ReadInt32LittleEndian(pvd.AsSpan(166)));
        Assert.Equal(new[] { "\0", "\u0001", "ARCHIVE", "CDPLUS", "PICTURES", "README.TXT;1" }, root.Select(r => r.Name).ToArray());
        foreach (var record in root)
        {
            Assert.Equal("XA", Encoding.ASCII.GetString(record.SystemUse, 6, 2));
            Assert.Equal(record.IsDirectory ? 0x8D55 : 0x0D55, BinaryPrimitives.ReadUInt16BigEndian(record.SystemUse.AsSpan(4)));
        }

        var cdplus = root.Single(r => r.Name == "CDPLUS");
        var infoRecord = ReadDirectory(drive, cdplus.Extent, cdplus.Size).Single(r => r.Name == "INFO.CDP;1");
        Assert.Equal(0x0D55, BinaryPrimitives.ReadUInt16BigEndian(infoRecord.SystemUse.AsSpan(4)));
        Assert.Equal(nwa + BlueBookLayout.InfoFileSessionSector, infoRecord.Extent);

        var pictures = root.Single(r => r.Name == "PICTURES");
        Assert.Equal(new[] { "\0", "\u0001" }, ReadDirectory(drive, pictures.Extent, pictures.Size).Select(r => r.Name).ToArray());

        // The user's files read back unchanged through the volume's own records.
        var archive = ReadDirectory(drive, root.Single(r => r.Name == "ARCHIVE").Extent, root.Single(r => r.Name == "ARCHIVE").Size);
        var notes = archive.Single(r => r.Name == "NOTES.TXT;1");
        Assert.Equal(UserFiles["ARCHIVE/NOTES.TXT"], ReadFile(drive, notes.Extent, notes.Size));

        // Windows reads the Joliet tree: the existing reader still finds every file there.
        using var stream = AbsoluteStream(nwa, Enumerable.Range(0, result.DataSectorsWritten).SelectMany(i => ReadSector(drive, nwa + i)).ToArray());
        using var iso = IsoImageReader.FromStream(stream, leaveOpen: true, sessionStartLba: nwa);
        Assert.True(iso.HasJoliet);
        Assert.NotNull(iso.FindFile("/CDPLUS/INFO.CDP"));
        Assert.NotNull(iso.FindFile("/ARCHIVE/SCANS/PAGE1.BIN"));
    }

    [Fact]
    public void InfoCdp_IsFoundAndParsedTheWayCdrkitReadsIt()
    {
        var (drive, nwa, result) = BurnBlueBookSession();
        Assert.True(result.Success, result.Message);

        // cd_extra.c Read_CD_Extra_Info: the sector at session start + 75 starts with "CD".
        int infoLba = nwa + 75;
        byte[] info = ReadSector(drive, infoLba);
        Assert.Equal((byte)'C', info[0]);
        Assert.Equal((byte)'D', info[1]);

        // cd_extra.c Read_CD_Extra_File: PVD byte 148 (type M path table, big-endian), entry "CDPLUS" (name length at
        // byte 0, extent big-endian at 2), then "INFO.CDP;1" in that directory (extent big-endian at record byte 6).
        byte[] pvd = ReadSector(drive, nwa + 16);
        byte[] pathTable = ReadSector(drive, BinaryPrimitives.ReadInt32BigEndian(pvd.AsSpan(148)));
        int p = 0, cdplusExtent = -1;
        while (p + 8 < pathTable.Length && pathTable[p] != 0)
        {
            int nameLength = pathTable[p];
            if (nameLength == 6 && Encoding.ASCII.GetString(pathTable, p + 8, 6) == "CDPLUS")
            {
                cdplusExtent = BinaryPrimitives.ReadInt32BigEndian(pathTable.AsSpan(p + 2));
                break;
            }
            p += 8 + nameLength + (nameLength & 1);
        }
        Assert.True(cdplusExtent > nwa);
        byte[] directory = ReadSector(drive, cdplusExtent);
        int fileExtent = -1;
        for (int q = 0; q + 33 < directory.Length && directory[q] != 0; q += directory[q])
        {
            if (directory[q + 32] == 10 && Encoding.ASCII.GetString(directory, q + 33, 10) == "INFO.CDP;1")
            {
                fileExtent = BinaryPrimitives.ReadInt32BigEndian(directory.AsSpan(q + 6));
            }
        }
        Assert.Equal(infoLba, fileExtent);

        // toc.c dump_extra_info: 10-byte language entries from byte 48 until a zero byte; position FFFFFFFFh means the
        // sector after INFO.CDP.
        Assert.Equal("en", Encoding.ASCII.GetString(info, 48, 2));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(50)));
        int subInfoLength = (int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(54));
        Assert.Equal(0, info[58]);

        // cd_extra.c Read_Subinfo: version at 8-11, packet count at 44-45, packets from 46 (id, length, text, even).
        byte[] sub = ReadSector(drive, infoLba + 1);
        Assert.Equal("0100", Encoding.ASCII.GetString(sub, 8, 4));
        int count = (sub[44] << 8) | sub[45];
        string? album = null, albumPerformer = null;
        var titles = new Dictionary<int, string>();
        var performers = new Dictionary<int, string>();
        int track = 0xFF, at = 46;
        for (int n = 0; n < count; n++)
        {
            int id = sub[at], len = sub[at + 1];
            string text = Encoding.Latin1.GetString(sub, at + 2, len);
            switch (id)
            {
                case 0x01: track = 10 * (sub[at + 2] - '0') + (sub[at + 3] - '0'); break;
                case 0x02: album = text; break;
                case 0x06: titles[track] = text; break;
                case 0x08 when track == 0: albumPerformer = text; break;
                case 0x08: performers[track] = text; break;
            }
            at += 2 + len + (len & 1);
        }
        Assert.Equal(subInfoLength, at);
        Assert.Equal("Night Album", album);
        Assert.Equal("The Players", albumPerformer);
        Assert.Equal("One", titles[1]);
        Assert.Equal("Two", titles[2]);
        Assert.Equal("Guest Singer", performers[2]);
    }

    [Fact]
    public void Mode2Form1WriteParameters_AreDataBlockType10_SessionFormatXa_WithADataSubHeader()
    {
        var drive = DiscWithOpenAudioSession();
        byte[] sense = Execute(drive, MmcCommandBuilder.BuildModeSense10Cdb(WriteParametersPage.PageCode, 255, disableBlockDescriptors: true), 255);
        var page = WriteParametersPage.FromModeSense10(sense)!.ForTrackAtOnceData(testWrite: false, bufferUnderrunFree: false, closeDisc: true, mode2Form1: true);
        var bytes = page.Bytes;

        // MMC-5 7.5.4 Write Parameters page: write type 1 (TAO) in byte 2, track mode 4 in byte 3, data block type
        // 10 (Mode 2 Form 1, 2,048 bytes) in byte 4, session format 20h (CD-ROM XA) in byte 8, sub-header in 48-51.
        Assert.Equal(1, bytes[2] & 0x0F);
        Assert.Equal(4, bytes[3] & 0x0F);
        Assert.Equal(10, bytes[4] & 0x0F);
        Assert.Equal(0x20, bytes[8]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x08, 0x00 }, bytes.Slice(48, 4).ToArray());

        // Mode 1 stays as it was.
        var mode1 = WriteParametersPage.FromModeSense10(sense)!.ForTrackAtOnceData(false, false, true);
        Assert.Equal(8, mode1.Bytes[4] & 0x0F);
        Assert.Equal(0x00, mode1.Bytes[8]);
    }

    [Fact]
    public void DataSession_WithoutTheOption_StaysMode1()
    {
        var drive = DiscWithOpenAudioSession();
        int nwa = SessionTwoStartLba;
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("README.TXT", UserFiles["README.TXT"]));
        using var image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(VolumeLabel: "EXTRA", IncludeUdf: false), nwa);
        var result = new NativeTaoDataWriter(drive, sleep: _ => { }).Write(
            new NativeDataWriteRequest(image, 0, image.Length, TestWrite: false, CloseDisc: true, SessionStartLba: nwa));
        Assert.True(result.Success, result.Message);
        Assert.Equal("track-at-once, mode 1", result.WriteMode);

        Assert.Equal(1, Execute(drive, NativeWriteCommands.BuildReadTrackInformation(3), 36)[6] & 0x0F);
        Assert.Equal(1, drive.DataSessions[^1].DataMode);
        byte[] raw = Execute(drive, MmcCommandBuilder.BuildReadCdCdb(nwa + 16, 1, expectedSectorType: 2, mainChannelSelectionBits: 0xF8), 2352);
        Assert.Equal(0x01, raw[15]);
        byte[] pvd = ReadSector(drive, nwa + 16);
        Assert.NotEqual("CD-XA001", Encoding.ASCII.GetString(pvd, 1024, 8));
    }

    [Fact]
    public void Options_AreOffByDefault()
    {
        Assert.False(new AudioBurnOptions().BlueBookLayout);
        Assert.False(new DataBurnOptions().CdRomXaMode2Form1);
        Assert.False(new IsoBuildOptions().CdRomXa);
        Assert.Null(new IsoBuildOptions().PinnedFilePath);
        Assert.False(new NativeDataWriteRequest(new MemoryStream(new byte[2048]), 0, 2048, TestWrite: false).Mode2Form1);
    }

    [Fact]
    public void Mode2Form1_OnADvd_IsRefusedBeforeWriting()
    {
        var drive = new VirtualMmcDrive { Profile = NativeTaoDataWriter.ProfileDvdMinusR };
        using var image = new MemoryStream(new byte[2048 * 32]);
        var result = new NativeTaoDataWriter(drive, sleep: _ => { }).Write(
            new NativeDataWriteRequest(image, 0, image.Length, TestWrite: false) { Mode2Form1 = true });
        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("CD format", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void Apply_RefusesAUserFolderNamedCdplus_AndKeepsTheUsersPictures()
    {
        var clash = CompilationDirectory.CreateRoot();
        clash.AddDirectory("cdplus");
        Assert.Throws<InvalidOperationException>(() => BlueBookLayout.Apply(clash, Info));

        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("PICTURES").AddFile(new CompilationFile("COVER.JPG", new byte[] { 0xFF, 0xD8, 0xFF }));
        BlueBookLayout.Apply(root, Info);
        Assert.NotNull(root.FindFileByVirtualPath("/PICTURES/COVER.JPG"));
        Assert.NotNull(root.FindFileByVirtualPath("/CDPLUS/INFO.CDP"));
    }

    [Fact]
    public async Task EnhancedCdBurn_WithTheBlueBookOption_WritesSessionTwoAsMode2_AndWithoutItAsMode1()
    {
        foreach (bool blueBook in new[] { false, true })
        {
            string folder = Path.Combine(_tempDir, $"data_{blueBook}");
            Directory.CreateDirectory(Path.Combine(folder, "ARCHIVE"));
            File.WriteAllBytes(Path.Combine(folder, "ARCHIVE", "NOTES.TXT"), UserFiles["ARCHIVE/NOTES.TXT"]);
            string wav = Path.Combine(_tempDir, $"01_{blueBook}.wav");
            WavFileCodec.WriteWavFile(wav, Content(5, 300 * AudioSectorSize));
            var compilation = new AudioCdCompilation { AlbumTitle = "Night Album", AlbumPerformer = "The Players", RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
            compilation.AddTrack(new AudioCdTrackItem(1, wav, "One", "The Players", PreGapFrames: 0));

            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
            recorder.InsertMedia(medium);
            string logs = Path.Combine(_tempDir, $"logs_{blueBook}");
            Directory.CreateDirectory(logs);
            VirtualMmcDrive? dataDrive = null;
            var service = new AudioCdBurnService(new NativeAudioBurnEngine(logs) { Wait = _ => { } },
                recoveryDataEngine: new NativeDataBurnEngine(new VirtualBurnEngine(recorder), logs) { Wait = _ => { }, ConfigureVirtualDrive = d => dataDrive = d });

            var result = await service.BurnAsync(recorder, compilation, new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                DataSessionFolderPath = folder,
                BlueBookLayout = blueBook
            });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(dataDrive);
            var session = dataDrive!.DataSessions[^1];
            Assert.Equal(blueBook ? 2 : 1, session.DataMode);
            Assert.Equal(blueBook ? 0x20 : 0x00, session.SessionFormat);

            int nwa = service.LastDataSessionStartLba!.Value;
            using var stream = AbsoluteStream(nwa, dataDrive.ReadSessionData(session));
            using var iso = IsoImageReader.FromStream(stream, leaveOpen: true, sessionStartLba: nwa);
            Assert.NotNull(iso.FindFile("/ARCHIVE/NOTES.TXT"));
            Assert.Equal(blueBook, iso.FindFile("/CDPLUS/INFO.CDP") != null);
        }
    }

    private sealed record DirectoryRecord(string Name, int Extent, int Size, bool IsDirectory, byte[] SystemUse);

    /// <summary>ECMA-119 9.1 directory records of one directory, read sector by sector; the System Use field follows
    /// the identifier and its padding byte (present when the identifier length is even).</summary>
    private static List<DirectoryRecord> ReadDirectory(VirtualMmcDrive drive, int extent, int size)
    {
        var records = new List<DirectoryRecord>();
        for (int s = 0; s < (size + 2047) / 2048; s++)
        {
            byte[] sector = ReadSector(drive, extent + s);
            for (int at = 0; at < 2048 && sector[at] != 0; at += sector[at])
            {
                int length = sector[at], nameLength = sector[at + 32];
                int systemUse = 33 + nameLength + (nameLength % 2 == 0 ? 1 : 0);
                records.Add(new DirectoryRecord(
                    Encoding.ASCII.GetString(sector, at + 33, nameLength),
                    BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(at + 2)),
                    BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(at + 10)),
                    (sector[at + 25] & 0x02) != 0,
                    sector.AsSpan(at + systemUse, length - systemUse).ToArray()));
            }
        }
        return records;
    }

    /// <summary>A stream addressed from LBA 0 holding one session's sectors from <paramref name="startLba"/>, as the ISO
    /// reader expects; the sectors before it read as zeros.</summary>
    private static MemoryStream AbsoluteStream(int startLba, byte[] session)
    {
        byte[] disc = new byte[(long)startLba * 2048 + session.Length];
        session.CopyTo(disc, (long)startLba * 2048);
        return new MemoryStream(disc, writable: false);
    }

    private static byte[] ReadFile(VirtualMmcDrive drive, int extent, int size) =>
        Enumerable.Range(0, (size + 2047) / 2048).SelectMany(i => ReadSector(drive, extent + i)).Take(size).ToArray();

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    /// <summary>CD-ROM EDC (ECMA-130 14.3): CRC with P(x) = (x^16 + x^15 + x^2 + 1)(x^16 + x^2 + x + 1), least
    /// significant bit first, register starting at zero; 0xD8018001 is that polynomial bit-reversed.</summary>
    private static uint Edc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xD8018001u : crc >> 1;
            }
        }
        return crc;
    }
}
