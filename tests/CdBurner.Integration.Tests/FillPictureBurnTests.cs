using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// <c>burn-data</c> and <c>burn-dvd</c> with <c>--fill-picture</c> on the simulated drive, with a tiny DVD-R (4,096
/// sectors) and a matching disc preset so a whole disc is drawn in about a second. What the drive recorded is read back:
/// the files byte for byte with Core's ISO reader, the volume ends from the descriptors themselves (ECMA-119 and
/// ECMA-167 offsets), and the picture sectors against a picture-only <c>disc-art generate</c> of the same disc.
/// No physical drive is touched.
/// </summary>
public sealed class FillPictureBurnTests : IDisposable
{
    private const int Sector = 2048;
    private const int TinySectors = 4096;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-fill-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _devices;

    public FillPictureBurnTests()
    {
        Directory.CreateDirectory(_dir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _devices = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 4,
              "cdPresets": [],
              "dvdPresets": [
                { "id": "tiny-dvd", "displayName": "Tiny DVD", "description": "test", "totalSectors": {{TinySectors}},
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": []
            }
            """);
        return path;
    }

    /// <summary>64 x 64, left half black, right half white.</summary>
    private string WritePicture()
    {
        string path = P("picture.png");
        if (File.Exists(path)) return path;
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < size / 2 ? 0 : 255);
                pixels[o + 3] = 255;
            }

        PngRaster.SaveBgra32(path, new BgraRaster(size, size, pixels));
        return path;
    }

    private static Dictionary<string, byte[]> SourceFiles()
    {
        static byte[] Random(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        return new Dictionary<string, byte[]>
        {
            ["README.TXT"] = "files inside, picture outside"u8.ToArray(),
            ["PHOTOS/ONE.BIN"] = Random(150_000, 11),
            ["PHOTOS/DEEP/TWO.BIN"] = Random(70_001, 12),
        };
    }

    private string WriteFiles()
    {
        string root = P("files");
        foreach ((string relative, byte[] content) in SourceFiles())
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return root;
    }

    private (string Folder, Dictionary<string, byte[]> Files) WriteVideoTs()
    {
        string dvdDir = P("DvdSource");
        string videoTs = Path.Combine(dvdDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        byte[] vmg = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vts = DvdVideoValidator.CreateSyntheticVtsIfo();
        var vob = new byte[40 * Sector];
        new Random(7).NextBytes(vob);
        var files = new Dictionary<string, byte[]>
        {
            ["VIDEO_TS.IFO"] = vmg,
            ["VIDEO_TS.VOB"] = new byte[Sector],
            ["VIDEO_TS.BUP"] = (byte[])vmg.Clone(),
            ["VTS_01_0.IFO"] = vts,
            ["VTS_01_0.VOB"] = new byte[Sector],
            ["VTS_01_1.VOB"] = vob,
            ["VTS_01_0.BUP"] = (byte[])vts.Clone(),
        };
        foreach ((string name, byte[] content) in files)
        {
            File.WriteAllBytes(Path.Combine(videoTs, name), content);
        }

        return (dvdDir, files);
    }

    private VirtualDiscMedium InsertTinyDvdR()
    {
        // A DVD-R that holds 4,096 sectors: the simulator's write, close and read-back paths are those of the 4.7 GB disc.
        var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusR, TinySectors);
        _recorder.InsertMedia(medium);
        return medium;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static byte[] ReadDisc(VirtualDiscMedium medium)
    {
        using Stream stream = medium.CreateReadStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static ReadOnlySpan<byte> SectorAt(byte[] disc, long lba) => disc.AsSpan(checked((int)(lba * Sector)), Sector);

    /// <summary>ECMA-119 8.4.8: the Primary Volume Descriptor (LBA 16, type 1, "CD001") holds the Volume Space Size, both-byte
    /// order, at byte positions 81-88 (offset 80, little-endian first).</summary>
    private static long Iso9660VolumeSpaceSize(byte[] disc)
    {
        ReadOnlySpan<byte> pvd = SectorAt(disc, 16);
        Assert.Equal(1, pvd[0]);
        Assert.True(pvd.Slice(1, 5).SequenceEqual("CD001"u8));
        uint little = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.Equal(little, BinaryPrimitives.ReadUInt32BigEndian(pvd.Slice(84, 4)));
        return little;
    }

    /// <summary>
    /// ECMA-167 3/10.2: the Anchor Volume Descriptor Pointer at LBA 256 (tag identifier 2) gives the Main Volume
    /// Descriptor Sequence extent at offset 16 (length, location). 3/10.5: its Partition Descriptor (tag identifier 5)
    /// holds Partition Starting Location at offset 188 and Partition Length at offset 192. Returns the partition end.
    /// </summary>
    private static long UdfPartitionEnd(byte[] disc)
    {
        ReadOnlySpan<byte> avdp = SectorAt(disc, 256);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(avdp));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(avdp.Slice(16, 4));
        uint location = BinaryPrimitives.ReadUInt32LittleEndian(avdp.Slice(20, 4));
        for (long lba = location; lba < location + (length + Sector - 1) / Sector; lba++)
        {
            ReadOnlySpan<byte> d = SectorAt(disc, lba);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (tag == 5)
            {
                return (long)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(188, 4)) + BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(192, 4));
            }

            if (tag == 8) break; // Terminating Descriptor
        }

        throw new Xunit.Sdk.XunitException("No UDF Partition Descriptor in the main sequence.");
    }

    /// <summary>The picture sectors a picture-only disc of the same preset has at the same addresses.</summary>
    private async Task<byte[]> PictureOnlyDisc()
    {
        string iso = P("picture-only.iso");
        (int code, string output, string error) = await RunAsync("disc-art", "generate", "--media", "dvd",
            "--picture", WritePicture(), "--output", iso, "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd");
        Assert.True(code == 0, error + output);
        return File.ReadAllBytes(iso);
    }

    /// <summary>The checks every filled disc must pass, from what the simulated drive recorded.</summary>
    private async Task AssertFilledDisc(VirtualDiscMedium medium, JsonElement fill, bool expectJoliet,
        IReadOnlyDictionary<string, byte[]> files, string pathPrefix)
    {
        Assert.True(fill.GetProperty("filled").GetBoolean());
        long pictureStart = fill.GetProperty("pictureStartLba").GetInt64();
        long pictureEnd = fill.GetProperty("pictureEndLba").GetInt64();
        Assert.Equal(TinySectors, pictureEnd);
        Assert.Equal(0, pictureStart % 16); // ECC block (ECMA-267 16 sectors)

        // Written in full, in one session, and closed.
        Assert.True(medium.IsFinalized, "a filled disc is closed");
        Assert.Single(medium.Sessions);
        Assert.Equal(TinySectors, medium.UsedSectors);
        byte[] disc = ReadDisc(medium);
        Assert.Equal((long)TinySectors * Sector, disc.Length);

        // Files byte for byte.
        string iso = P("readback.iso");
        File.WriteAllBytes(iso, disc);
        using (IsoImageReader reader = IsoImageReader.FromFile(iso))
        {
            Assert.True(reader.HasIso9660);
            Assert.True(reader.HasUdf);
            Assert.Equal(expectJoliet, reader.HasJoliet);
            Assert.Equal(files.Count, reader.AllFiles.Count);
            foreach ((string relative, byte[] content) in files)
            {
                Assert.Equal(content, reader.ReadFileBytes(pathPrefix + relative));
            }

            foreach (IsoFileEntry file in reader.AllFiles)
            {
                long last = file.StartLba + Math.Max(0, (file.SizeBytes + Sector - 1) / Sector - 1);
                Assert.True(last < pictureStart, $"{file.VirtualPath} ends at sector {last}, picture starts at {pictureStart}");
            }
        }

        // No picture sector inside either file system's volume, and zeros between the volume and the picture.
        long volumeEnd = Math.Max(Iso9660VolumeSpaceSize(disc), UdfPartitionEnd(disc));
        Assert.True(volumeEnd + 1024 <= pictureStart, $"volume ends at {volumeEnd}, picture starts at {pictureStart}");
        for (long lba = volumeEnd + 1; lba < pictureStart; lba++) // volumeEnd itself may hold UDF's closing anchor
        {
            Assert.True(SectorAt(disc, lba).IndexOfAnyExcept((byte)0) < 0, $"sector {lba} in the gap is not zero");
        }

        // The picture is the disc-art picture: the same bytes as a picture-only disc of the same preset at those sectors.
        byte[] pictureOnly = await PictureOnlyDisc();
        Assert.True(disc.AsSpan((int)(pictureStart * Sector)).SequenceEqual(pictureOnly.AsSpan((int)(pictureStart * Sector))),
            "the picture ring differs from the picture-only disc");
        Assert.True(disc.AsSpan((int)(pictureStart * Sector)).IndexOfAnyExcept((byte)0) >= 0, "the picture zone is drawn");
    }

    [Fact]
    public async Task BurnData_FillPicture_on_a_simulated_DvdR_verifies_files_inside_picture_outside()
    {
        VirtualDiscMedium medium = InsertTinyDvdR();

        (int code, string output, string error) = await RunAsync("burn-data", "--drive", "VDRV-01", "--input", WriteFiles(),
            "--label", "FILLED", "--fill-picture", WritePicture(), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-dvd", "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean());
        Assert.Equal(TinySectors, root.GetProperty("sectorsWritten").GetInt64());
        await AssertFilledDisc(medium, root.GetProperty("fillPicture"), expectJoliet: true, SourceFiles(), "/");
        // The temporary image and its description are deleted after the burn (only this class makes such files, and its
        // tests run one after another).
        string[] leftovers = Directory.GetFiles(Path.GetTempPath(), "ignisdisc_fill_*.iso*")
            .Where(f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-2)).ToArray();
        Assert.True(leftovers.Length == 0, string.Join(", ", leftovers));
    }

    [Fact]
    public async Task BurnDvd_FillPicture_on_a_simulated_DvdR_verifies_video_ts_inside_picture_outside()
    {
        VirtualDiscMedium medium = InsertTinyDvdR();
        (string folder, Dictionary<string, byte[]> files) = WriteVideoTs();

        (int code, string output, string error) = await RunAsync("burn-dvd", "--drive", "VDRV-01", "--video-ts", folder,
            "--label", "FILLED_DVD", "--fill-picture", WritePicture(), "--disc-presets-file", WritePresets(),
            "--disc-preset", "tiny-dvd", "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean());
        await AssertFilledDisc(medium, root.GetProperty("fillPicture"), expectJoliet: false, files, "/VIDEO_TS/");
    }

    [Fact]
    public async Task Without_the_option_nothing_is_drawn_and_the_screens_start_with_it_off()
    {
        VirtualDiscMedium medium = InsertTinyDvdR();

        (int code, string output, string error) = await RunAsync("burn-data", "--drive", "VDRV-01", "--input", WriteFiles(), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.True(!doc.RootElement.TryGetProperty("fillPicture", out JsonElement fill) || fill.ValueKind == JsonValueKind.Null);
        // Only the files were written: the disc is far from full.
        Assert.True(medium.UsedSectors < TinySectors / 2, $"{medium.UsedSectors} sectors written");
    }

    [Theory]
    [InlineData("append")]
    [InlineData("no-close")]
    [InlineData("auto")]
    [InlineData("cd")]
    [InlineData("used")]
    public async Task Fill_is_refused_with_nothing_written(string why)
    {
        VirtualDiscMedium medium = why == "cd" ? VirtualDiscMedium.CreateBlankCdR700() : new VirtualDiscMedium(DiscMediaType.DvdMinusR, TinySectors);
        _recorder.InsertMedia(medium);
        string files = WriteFiles();
        if (why == "used")
        {
            // An open disc with a first session: a filled disc cannot be added to it.
            (int first, string o, string e) = await RunAsync("burn-data", "--drive", "VDRV-01", "--input", files, "--no-close");
            Assert.True(first == 0, e + o);
        }

        int usedBefore = medium.UsedSectors;
        var args = new List<string> { "burn-data", "--drive", "VDRV-01", "--input", files,
            "--fill-picture", why == "auto" ? "auto" : WritePicture(), "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd" };
        if (why == "append") args.Add("--append");
        if (why == "no-close") args.Add("--no-close");

        (int code, string output, string error) = await RunAsync(args.ToArray());

        Assert.NotEqual(0, code);
        string text = output + error;
        Assert.Contains(why switch
        {
            "append" => "cannot be added to a disc as a new session",
            "no-close" => "is closed when it is written",
            "auto" => "give the picture to draw",
            "cd" => "single-layer DVD and BD discs only",
            _ => "already holds data",
        }, text);
        Assert.Equal(usedBefore, medium.UsedSectors);
    }

    [Fact]
    public async Task BurnDvd_auto_without_ffmpeg_burns_without_the_picture_and_says_so()
    {
        VirtualDiscMedium medium = InsertTinyDvdR();
        (string folder, _) = WriteVideoTs();
        CliCommandRunner.FillFfmpegFinderOverride.Value = _ => null;
        try
        {
            (int code, string output, string error) = await RunAsync("burn-dvd", "--drive", "VDRV-01", "--video-ts", folder,
                "--fill-picture", "auto", "--disc-presets-file", WritePresets(), "--disc-preset", "tiny-dvd", "--json");

            Assert.True(code == 0, error + output);
            using JsonDocument doc = JsonDocument.Parse(output);
            JsonElement fill = doc.RootElement.GetProperty("fillPicture");
            Assert.False(fill.GetProperty("filled").GetBoolean());
            Assert.Contains("FFmpeg", fill.GetProperty("skipped").GetString());
            Assert.True(medium.UsedSectors < TinySectors / 2, "burned without the picture");
        }
        finally
        {
            CliCommandRunner.FillFfmpegFinderOverride.Value = null;
        }
    }

    [Fact]
    public void Main_movie_is_the_title_set_with_the_most_video()
    {
        string videoTs = Path.Combine(_dir, "M", "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_0.VOB"), new byte[900]); // a menu, never the movie
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_01_1.VOB"), new byte[100]);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_02_1.VOB"), new byte[300]);
        File.WriteAllBytes(Path.Combine(videoTs, "VTS_02_2.VOB"), new byte[300]);

        Assert.Equal(Path.Combine(videoTs, "VTS_02_1.VOB"), DiscFillPicture.FindMainMovie(Path.Combine(_dir, "M")));
        Assert.Equal(Path.Combine(videoTs, "VTS_02_1.VOB"), DiscFillPicture.FindMainMovie(videoTs));
        Assert.Equal(400.0, DiscFillPicture.FrameTimeSeconds(1200));
        Assert.Equal(45.0, DiscFillPicture.FrameTimeSeconds(90));
        Assert.Equal(60.0, DiscFillPicture.FrameTimeSeconds(0)); // running time unknown
    }

    [Fact]
    public void Fit_to_disc_cuts_the_outer_edge_on_an_ecc_block_and_keeps_the_geometry()
    {
        CalibrationParameters dvd = DiscFillPicture.PresetDisc(DiscArtMedium.Dvd);
        Assert.Equal(2_295_104, dvd.OutputSectors); // dvd-5-120mm, the DVD+R single-layer capacity

        CalibrationParameters fitted = DiscFillPicture.FitToDisc(dvd, 2_000_007);
        Assert.Equal(2_000_000, fitted.OutputSectors); // whole 16-sector ECC blocks only
        Assert.Equal(dvd.Sectors, fitted.Sectors);
        Assert.Equal(dvd.OuterRadiusMm, fitted.OuterRadiusMm);
        Assert.Same(dvd, DiscFillPicture.FitToDisc(dvd, 2_298_496)); // a DVD-R holds more: unchanged
    }

    // App screens ----------------------------------------------------------------------------------------------------

    private static readonly CalibrationParameters TinyDvd = new(CalibrationDiscKind.Dvd, 24, 58, TinySectors, 133.3);

    private static VirtualDiscMedium InsertInto(DiscDeviceManager devices, VirtualDiscMedium medium)
    {
        devices.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01").InsertMedia(medium);
        return medium;
    }

    [Fact]
    public void The_picture_fill_is_off_on_both_screens_until_chosen()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var engine = new VirtualBurnEngine();
        var data = new DataBurnViewModel(devices, engine);
        var dvd = new DvdVideoViewModel(devices, engine);

        Assert.False(data.FillWithPicture);
        Assert.False(dvd.FillWithPicture);
        Assert.Contains("closed", data.FillPictureNote);
        Assert.Contains("longer", data.FillPictureNote);
        Assert.Equal(data.FillPictureNote, dvd.FillPictureNote);
    }

    [Fact]
    public async Task Data_screen_fill_burns_a_closed_disc_with_the_files_inside()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        VirtualDiscMedium medium = InsertInto(devices, new VirtualDiscMedium(DiscMediaType.DvdMinusR, TinySectors));
        var vm = new DataBurnViewModel(devices, new VirtualBurnEngine()) { VolumeLabel = "FILLED" };
        foreach ((string relative, byte[] content) in SourceFiles())
        {
            vm.AddFile(relative.Replace('/', '_'), content);
        }

        vm.FillWithPicture = true;
        vm.FillPicturePath = WritePicture();
        vm.FillDiscGeometry = TinyDvd;

        var result = await vm.StartDataBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage + " / " + vm.StatusMessage);
        Assert.True(medium.IsFinalized);
        Assert.Equal(TinySectors, medium.UsedSectors);
        byte[] disc = ReadDisc(medium);
        string iso = P("vm-readback.iso");
        File.WriteAllBytes(iso, disc);
        using IsoImageReader reader = IsoImageReader.FromFile(iso);
        foreach ((string relative, byte[] content) in SourceFiles())
        {
            Assert.Equal(content, reader.ReadFileBytes("/" + relative.Replace('/', '_')));
        }

        // The picture-only disc of the same geometry has the same bytes from where the picture starts on this one.
        byte[] pictureOnly = await PictureOnlyDisc();
        long volumeEnd = Math.Max(Iso9660VolumeSpaceSize(disc), UdfPartitionEnd(disc));
        long pictureStart = (volumeEnd + 1024 + 15) / 16 * 16;
        Assert.True(disc.AsSpan((int)(pictureStart * Sector)).SequenceEqual(pictureOnly.AsSpan((int)(pictureStart * Sector))));
    }

    [Fact]
    public async Task Data_screen_refuses_the_fill_on_an_open_disc_or_without_a_picture_and_writes_nothing()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        VirtualDiscMedium medium = InsertInto(devices, new VirtualDiscMedium(DiscMediaType.DvdMinusR, TinySectors));
        var vm = new DataBurnViewModel(devices, new VirtualBurnEngine()) { CloseDisc = false, FillWithPicture = true, FillDiscGeometry = TinyDvd };
        vm.AddFile("A.TXT", "a"u8.ToArray());
        vm.FillPicturePath = WritePicture();

        var open = await vm.StartDataBurnAsync("VDRV-01");
        Assert.False(open.Success);
        Assert.Contains("closed when it is written", vm.StatusMessage);

        vm.CloseDisc = true;
        vm.FillPicturePath = null;
        var noPicture = await vm.StartDataBurnAsync("VDRV-01");
        Assert.False(noPicture.Success);
        Assert.Contains("Choose the PNG picture", vm.StatusMessage);
        Assert.Equal(0, medium.UsedSectors);
        Assert.True(medium.IsBlank);
    }

    private sealed class SingleDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
