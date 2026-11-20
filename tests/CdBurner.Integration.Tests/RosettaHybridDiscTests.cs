using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// <c>rosetta build | burn</c>: a hybrid Rosetta DVD-R (archive vault inside r = 38 mm, a zero gap, then the text
/// pages) through the command-line runner on a small test disc and the simulated drive. No physical drive is touched.
/// Tests that need PAR2 data return early where par2 is not installed (CI installs it).
/// </summary>
public sealed class RosettaHybridDiscTests : IDisposable
{
    private const int SmallDiscSectors = 16_384; // 32 MiB: about 8 MB inside 38 mm, so par2 stays quick
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-hybrid-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _devices;
    private readonly string? _par2 = Par2Runner.FindPar2Executable();

    public RosettaHybridDiscTests()
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

    // ---- plan and capacity -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_FullDvd_PagesStartAtTheSectorOf38mm()
    {
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--output", P("plan.iso"),
            "--title", "Plan", "--date", "2026-11-17", "--plan-only", "--json");
        Assert.True(code == 0, error + output);
        JsonElement plan = JsonDocument.Parse(output).RootElement.GetProperty("plan");

        // With constant linear density and constant track pitch the recorded length grows with the area covered, so the
        // sector at radius r is N (r^2 - r0^2) / (r1^2 - r0^2). dvd-5-120mm: N = 2,295,104, r0 = 24 mm, r1 = 58 mm.
        double expected = 2_295_104.0 * (38.0 * 38 - 24 * 24) / (58.0 * 58 - 24 * 24);
        long start = plan.GetProperty("pagesStartLba").GetInt64();
        Assert.Equal(0, start % 16); // whole ECC blocks (ECMA-267: 16 sectors)
        Assert.InRange(start, (long)expected - 32, (long)expected + 16);
        Assert.Equal(start - 1024, plan.GetProperty("vaultCeilingSectors").GetInt64()); // 2 MiB of 2,048-byte sectors
        Assert.Equal(2_295_104, plan.GetProperty("pagesEndLba").GetInt64());
        Assert.False(File.Exists(P("plan.iso")));
    }

    [Fact]
    public async Task Plan_CapacityComesFromTheLayout_AndFollowsThePagesRadius()
    {
        (int code, string output, _) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--output", P("plan.iso"),
            "--title", "Plan", "--date", "2026-11-17", "--plan-only", "--json");
        Assert.Equal(0, code);
        JsonElement at38 = JsonDocument.Parse(output).RootElement.GetProperty("plan");

        // Hobbyist page: 30 cells of 100 um across, 13 rows of 150 um plus the 3/8-cell rule band, 200 um gutters.
        // Tiles cannot cover more than the annulus between the pages' radii; ring quantisation loses some.
        double pageAreaMm2 = (30 * 0.100 + 0.200) * (13 * 0.150 + 3 * 0.150 / 8 + 0.200);
        double outer = at38.GetProperty("pagesOuterRadiusMm").GetDouble();
        double annulus = Math.PI * (outer * outer - 38.0 * 38.0);
        long capacity = at38.GetProperty("pageCapacity").GetInt64();
        Assert.InRange(capacity, (long)(0.85 * annulus / pageAreaMm2), (long)(annulus / pageAreaMm2) + 1);
        Assert.Equal(30 * 12, at38.GetProperty("charactersPerPage").GetInt32());
        Assert.Equal(capacity - at38.GetProperty("pagesUsed").GetInt64(), at38.GetProperty("pagesFree").GetInt64());

        (code, output, _) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--output", P("plan.iso"),
            "--title", "Plan", "--date", "2026-11-17", "--pages-radius", "42", "--plan-only", "--json");
        Assert.Equal(0, code);
        JsonElement at42 = JsonDocument.Parse(output).RootElement.GetProperty("plan");
        Assert.True(at42.GetProperty("vaultCeilingBytes").GetInt64() > at38.GetProperty("vaultCeilingBytes").GetInt64());
        Assert.True(at42.GetProperty("pageCapacity").GetInt64() < capacity);

        // The printed text carries the same layout numbers.
        (code, output, _) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--output", P("plan.iso"),
            "--title", "Plan", "--date", "2026-11-17", "--plan-only");
        Assert.Equal(0, code);
        Assert.Contains($"of {capacity:N0} pages used", output);
        Assert.Contains($"sectors 0 to {at38.GetProperty("vaultCeilingSectors").GetInt64() - 1:N0} at most", output);
    }

    // ---- the image -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Build_NoPageSectorInsideTheVolume_AndTheGapIsZero()
    {
        (string iso, JsonElement result) = await BuildAsync(redundancy: 0);
        byte[] image = await File.ReadAllBytesAsync(iso);
        long pagesStart = result.GetProperty("pagesStartLba").GetInt64();
        long pagesEnd = result.GetProperty("pagesEndLba").GetInt64();
        Assert.Equal(pagesEnd * 2048, image.LongLength);

        // ECMA-119 8.4: Primary Volume Descriptor at sector 16, "CD001" at byte 1, Volume Space Size (both-endian) at 80.
        ReadOnlySpan<byte> pvd = image.AsSpan(16 * 2048, 2048);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd.Slice(1, 5)));
        long isoEnd = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.True(isoEnd + 1024 <= pagesStart, $"ISO 9660 volume ends at {isoEnd}, pages start at {pagesStart}");

        // ECMA-167 3/10.2: Anchor Volume Descriptor Pointer (tag 2) at sector 256; the Main Volume Descriptor Sequence
        // extent at byte 16 (length, location). 3/10.5: Partition Descriptor (tag 5), starting location at byte 188,
        // length at 192.
        ReadOnlySpan<byte> anchor = image.AsSpan(256 * 2048, 2048);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(anchor));
        uint vdsLength = BinaryPrimitives.ReadUInt32LittleEndian(anchor.Slice(16, 4));
        uint vdsLocation = BinaryPrimitives.ReadUInt32LittleEndian(anchor.Slice(20, 4));
        long partitionEnd = -1;
        for (uint i = 0; i < vdsLength / 2048; i++)
        {
            ReadOnlySpan<byte> descriptor = image.AsSpan((int)((vdsLocation + i) * 2048), 2048);
            if (BinaryPrimitives.ReadUInt16LittleEndian(descriptor) == 5)
            {
                partitionEnd = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(188, 4))
                               + (long)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(192, 4));
            }
        }

        Assert.True(partitionEnd > 256, "no UDF partition descriptor found");
        Assert.True(partitionEnd + 1024 <= pagesStart, $"UDF partition ends at {partitionEnd}, pages start at {pagesStart}");

        // Every file the computer sees lies inside the volume.
        using (var reader = IsoImageReader.FromFile(iso))
        {
            Assert.NotEmpty(reader.AllFiles);
            foreach (IsoFileEntry file in reader.AllFiles)
            {
                Assert.True(file.StartLba + (file.SizeBytes + 2047) / 2048 <= isoEnd, file.VirtualPath);
            }
        }

        // Between the volume and the pages: only zero sectors, at least 2 MiB of them.
        long vaultEnd = Math.Max(isoEnd, partitionEnd);
        Assert.True(pagesStart - vaultEnd >= 1024);
        Assert.True(image.AsSpan((int)(vaultEnd * 2048), (int)((pagesStart - vaultEnd) * 2048)).IndexOfAnyExcept((byte)0) < 0);

        // The page zone is drawn (not zero), from its first ECC block to its last.
        Assert.True(image.AsSpan((int)(pagesStart * 2048), 16 * 2048).IndexOfAnyExcept((byte)0) >= 0);
        Assert.True(image.AsSpan((int)((pagesEnd - 16) * 2048), 16 * 2048).IndexOfAnyExcept((byte)0) >= 0);

        // The description file says where the pages begin, inside the block that holds r = 38 mm.
        using JsonDocument sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(iso + ".rosetta.json"));
        JsonElement pages = sidecar.RootElement.GetProperty("pages");
        Assert.Equal(pagesStart, pages.GetProperty("startLba").GetInt64());
        Assert.InRange(pages.GetProperty("startRadiusMm").GetDouble(), 37.5, 38.0);
        Assert.Empty(Directory.EnumerateDirectories(_dir, "*.rosetta-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Build_VaultHoldsTheSameText_ItsSha256_AndAPageIndex()
    {
        (string iso, JsonElement result) = await BuildAsync(redundancy: 0);
        Dictionary<string, byte[]> files = ReadFiles(iso);

        // The vault's text is the source text with line feeds for line ends.
        byte[] note = files["/ROSETTA/TEXT/a-note.txt"];
        Assert.Equal("First line.\nSecond line, with CRLF in the source.\n", Encoding.UTF8.GetString(note));
        byte[] poem = files["/ROSETTA/TEXT/b-poem.md"];
        Assert.Equal(File.ReadAllText(Path.Combine(TextFolder, "b-poem.md")), Encoding.UTF8.GetString(poem));

        // The SHA-256 rule the page index states, applied to the vault's own files: name without extension, LF, bytes, LF.
        using var stream = new MemoryStream();
        foreach ((string title, byte[] bytes) in new[] { ("a-note", note), ("b-poem", poem) })
        {
            stream.Write(Encoding.UTF8.GetBytes(title + "\n"));
            stream.Write(bytes);
            stream.WriteByte((byte)'\n');
        }

        string sha = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        Assert.Equal(sha, result.GetProperty("plan").GetProperty("textSha256").GetString());

        string index = Encoding.UTF8.GetString(files["/ROSETTA/PAGE-INDEX.TXT"]);
        Assert.Contains($"Text SHA-256: {sha}", index);
        Assert.Contains($"first 12 digits: {sha[..12]}", index);
        Assert.Contains("  p 0001  R00-S00  CONTENTS", index);
        Assert.Contains("ROSETTA/TEXT/a-note.txt", index);
        Assert.Contains("ROSETTA/TEXT/b-poem.md", index);

        using JsonDocument json = JsonDocument.Parse(files["/ROSETTA/PAGE-INDEX.JSON"]);
        JsonElement[] documents = json.RootElement.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(["a-note", "b-poem"], documents.Select(d => d.GetProperty("title").GetString()!).ToArray());
        int pages = json.RootElement.GetProperty("pages").GetArrayLength();
        Assert.Equal(result.GetProperty("plan").GetProperty("pagesUsed").GetInt32(), pages);
        Assert.Equal(pages, documents[^1].GetProperty("lastPage").GetInt32());

        // The archive's own manifest lists the text and the index.
        string manifest = Encoding.UTF8.GetString(files["/" + ArchiveManifest.ManifestFileName]);
        Assert.Contains("ROSETTA/TEXT/a-note.txt", manifest);
        Assert.Contains("ROSETTA/PAGE-INDEX.TXT", manifest);
        Assert.Contains("sub/data.bin", manifest);
    }

    // ---- burn and recover ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Burn_OnSimulatedDvdR_Verifies_AndRecoverChecksEveryFile()
    {
        if (_par2 == null) return;
        (string iso, JsonElement built) = await BuildAsync(redundancy: 10);
        Assert.True(built.GetProperty("recoverySectors").GetInt32() > 0);
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync("rosetta", "burn", "--drive", "VDRV-01", "--image", iso,
            "--log-dir", P("logs"), "--json");

        Assert.True(code == 0, error + output);
        using (JsonDocument burn = JsonDocument.Parse(output))
        {
            Assert.True(burn.RootElement.GetProperty("success").GetBoolean());
            Assert.True(burn.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal(new FileInfo(iso).Length / 2048, burn.RootElement.GetProperty("sectorsWritten").GetInt64());
        }

        (code, output, error) = await RunAsync("recover", "--drive", "VDRV-01", "--out", P("out"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.True(report.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("damagedFiles").GetArrayLength());
        Assert.True(report.RootElement.GetProperty("filesChecked").GetInt32() >= 7);
        AssertVaultMatchesSources(P("out"));
    }

    [Fact]
    public async Task DamagedVault_IsRepairedByRecover()
    {
        if (_par2 == null) return;
        (string iso, _) = await BuildAsync(redundancy: 10);
        IsoFileEntry data;
        IsoFileEntry text;
        using (var reader = IsoImageReader.FromFile(iso))
        {
            data = reader.AllFiles.Single(f => f.VirtualPath.EndsWith("/sub/data.bin", StringComparison.Ordinal));
            text = reader.AllFiles.Single(f => f.VirtualPath.EndsWith("/b-poem.md", StringComparison.Ordinal));
        }

        await using (var stream = new FileStream(iso, FileMode.Open, FileAccess.Write))
        {
            foreach (int lba in Enumerable.Range(data.StartLba + 5, 20).Append(text.StartLba))
            {
                stream.Position = lba * 2048L;
                stream.Write(new byte[2048]);
            }
        }

        (int code, string output, string error) = await RunAsync("recover", "--image", iso, "--out", P("out"), "--json");

        Assert.True(code == 0, error + output);
        using JsonDocument report = JsonDocument.Parse(output);
        string[] repaired = report.RootElement.GetProperty("repairedFiles").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(repaired, f => f.EndsWith("data.bin", StringComparison.Ordinal));
        Assert.Contains(repaired, f => f.EndsWith("b-poem.md", StringComparison.Ordinal));
        AssertVaultMatchesSources(P("out"));
    }

    // ---- CD-R hybrid -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_FullCd_PagesStartAt38mmWithCircMargin_AndAccepts50x75()
    {
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--media", "cd",
            "--text", WriteTexts(), "--output", P("plan.cue"), "--title", "CD Plan", "--date", "2026-11-20",
            "--plan-only", "--json");
        Assert.True(code == 0, error + output);
        JsonElement root = JsonDocument.Parse(output).RootElement;
        Assert.Equal("CD-R", root.GetProperty("media").GetString());
        Assert.True(root.GetProperty("cdInterleave").GetBoolean());
        JsonElement plan = root.GetProperty("plan");

        // cd-80: Sectors = 359,675, UsableSectors = 359,673, r0 = 24.825 mm, r1 = 58.0 mm; 2-sector CIRC lead-in pullback.
        long expectedAt38 = (long)Math.Floor(359_675.0 * (38.0 * 38 - 24.825 * 24.825) / (58.0 * 58 - 24.825 * 24.825));
        long start = plan.GetProperty("pagesStartLba").GetInt64();
        Assert.Equal(expectedAt38 - 2, start);
        Assert.Equal(start - 1024, plan.GetProperty("vaultCeilingSectors").GetInt64());
        Assert.Equal(359_673, plan.GetProperty("pagesEndLba").GetInt64());
        Assert.False(File.Exists(P("plan.cue")));

        // Minimum CD cell size 50 x 75 um is accepted.
        (code, output, error) = await RunAsync("rosetta", "build", "--media", "cd",
            "--cells", "ladder-50x75", "--text", WriteTexts(), "--output", P("plan50.cue"),
            "--title", "CD 50x75", "--date", "2026-11-20", "--plan-only", "--json");
        Assert.True(code == 0, error + output);
        JsonElement plan50 = JsonDocument.Parse(output).RootElement.GetProperty("plan");
        Assert.Equal(50.0, plan50.GetProperty("cells").GetProperty("widthUm").GetDouble());
        Assert.Equal(75.0, plan50.GetProperty("cells").GetProperty("heightUm").GetDouble());
        Assert.True(plan50.GetProperty("pageCapacity").GetInt64() > plan.GetProperty("pageCapacity").GetInt64());
    }

    [Fact]
    public async Task Build_Cd_NoPageSectorInsideTheVolume_AndTheGapIsZero()
    {
        (string cue, string bin, JsonElement result) = await BuildCdAsync(redundancy: 0, cells: "ladder-50x75");
        Assert.Equal("CD-R", result.GetProperty("media").GetString());
        Assert.True(File.Exists(cue));
        Assert.True(File.Exists(bin));

        long pagesStart = result.GetProperty("pagesStartLba").GetInt64();
        long pagesEnd = result.GetProperty("pagesEndLba").GetInt64();
        long vaultEnd = result.GetProperty("vaultEndLba").GetInt64();
        Assert.Equal(pagesEnd * 2352L, new FileInfo(bin).Length);

        // CUE has Track 1 MODE1/2352 and Track 2 AUDIO with INDEX 00 at pagesStart - 150, INDEX 01 at pagesStart.
        CueSheet sheet = CueSheetParser.ParseFile(cue);
        Assert.Equal(2, sheet.Tracks.Count);
        Assert.Equal(CueTrackDataType.Mode1_2352, sheet.Tracks[0].DataType);
        Assert.Equal(CueTrackDataType.Audio, sheet.Tracks[1].DataType);
        Assert.Equal(pagesStart - 150, sheet.Tracks[1].Indices.Single(i => i.IndexNumber == 0).Position.ToFrameCount());
        Assert.Equal(pagesStart, sheet.Tracks[1].Indices.Single(i => i.IndexNumber == 1).Position.ToFrameCount());

        // Extract Track 1 Mode 1 user sectors (0 .. pagesStart - 151) from the .bin and verify ISO 9660 + UDF + gap.
        byte[] binBytes = await File.ReadAllBytesAsync(bin);
        int mode1Sectors = (int)(pagesStart - 150);
        byte[] track1Iso = new byte[mode1Sectors * 2048];
        for (int lba = 0; lba < mode1Sectors; lba++)
        {
            bool ok = RawSectorCodec.Extract2048From2352(
                binBytes.AsSpan(lba * 2352, 2352),
                track1Iso.AsSpan(lba * 2048, 2048),
                out bool edcValid);
            Assert.True(ok && edcValid, $"Mode 1 sector {lba} EDC failed");
        }

        // ECMA-119 8.4: Primary Volume Descriptor at sector 16.
        ReadOnlySpan<byte> pvd = track1Iso.AsSpan(16 * 2048, 2048);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd.Slice(1, 5)));
        long isoEnd = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.True(isoEnd + 1024 <= pagesStart, $"ISO 9660 volume ends at {isoEnd}, pages start at {pagesStart}");

        // ECMA-167 3/10.2 & 3/10.5: UDF AVDP at sector 256 and Partition Descriptor.
        ReadOnlySpan<byte> anchor = track1Iso.AsSpan(256 * 2048, 2048);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(anchor));
        uint vdsLength = BinaryPrimitives.ReadUInt32LittleEndian(anchor.Slice(16, 4));
        uint vdsLocation = BinaryPrimitives.ReadUInt32LittleEndian(anchor.Slice(20, 4));
        long partitionEnd = -1;
        for (uint i = 0; i < vdsLength / 2048; i++)
        {
            ReadOnlySpan<byte> descriptor = track1Iso.AsSpan((int)((vdsLocation + i) * 2048), 2048);
            if (BinaryPrimitives.ReadUInt16LittleEndian(descriptor) == 5)
            {
                partitionEnd = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(188, 4))
                               + (long)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(192, 4));
            }
        }
        Assert.True(partitionEnd > 256, "no UDF partition descriptor found");
        Assert.True(partitionEnd + 1024 <= pagesStart, $"UDF partition ends at {partitionEnd}, pages start at {pagesStart}");

        // Every file lies strictly inside the clamped volume; vault holds text, page index, files and manifest.
        using (var ms = new MemoryStream(track1Iso, writable: false))
        using (var reader = IsoImageReader.FromStream(ms))
        {
            Assert.NotEmpty(reader.AllFiles);
            foreach (IsoFileEntry file in reader.AllFiles)
            {
                Assert.True(file.StartLba + (file.SizeBytes + 2047) / 2048 <= isoEnd, file.VirtualPath);
            }
            Assert.Contains(reader.AllFiles, f => f.VirtualPath == "/ROSETTA/TEXT/a-note.txt");
            Assert.Contains(reader.AllFiles, f => f.VirtualPath == "/ROSETTA/PAGE-INDEX.TXT");
            Assert.Contains(reader.AllFiles, f => f.VirtualPath == "/ROSETTA/PAGE-INDEX.JSON");
            Assert.Contains(reader.AllFiles, f => f.VirtualPath == "/sub/data.bin");
        }

        // Between vaultEnd and Track 2 INDEX 00: zero Mode 1 user sectors (incl. 150-sector Mode 1 post-gap).
        Assert.Equal(Math.Max(isoEnd, partitionEnd), vaultEnd);
        Assert.True(pagesStart - vaultEnd >= 1024);
        Assert.True(track1Iso.AsSpan((int)(vaultEnd * 2048)).IndexOfAnyExcept((byte)0) < 0);

        // Between Track 2 INDEX 00 and INDEX 01: 150 sectors of silent Red Book audio.
        Assert.True(binBytes.AsSpan((int)((pagesStart - 150) * 2352), 150 * 2352).IndexOfAnyExcept((byte)0) < 0);

        // Track 2 carries CD-DA picture bytes (palette bytes 0x0D / 0xAA in little-endian audio).
        Assert.True(binBytes.AsSpan((int)(pagesStart * 2352), (int)((pagesEnd - pagesStart) * 2352)).IndexOfAnyExcept((byte)0) >= 0);

        using JsonDocument sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(cue + ".rosetta.json"));
        Assert.Equal("CD-R", sidecar.RootElement.GetProperty("media").GetString());
        Assert.True(sidecar.RootElement.GetProperty("cdInterleave").GetBoolean());
        Assert.Equal(pagesStart, sidecar.RootElement.GetProperty("pages").GetProperty("startLba").GetInt64());
        Assert.Empty(Directory.EnumerateDirectories(_dir, "*.rosetta-work-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Burn_OnSimulatedCdR_VerifiesMixedModeCue_AndReadsBackPagesAndRim()
    {
        (string cue, _, JsonElement built) = await BuildCdAsync(redundancy: 0, cells: "hobbyist");
        long pagesStart = built.GetProperty("pagesStartLba").GetInt64();
        long pagesEnd = built.GetProperty("pagesEndLba").GetInt64();

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync("rosetta", "burn", "--drive", "VDRV-01", "--image", cue,
            "--log-dir", P("logs"), "--json");
        Assert.True(code == 0, error + output);
        using (JsonDocument burn = JsonDocument.Parse(output))
        {
            Assert.True(burn.RootElement.GetProperty("success").GetBoolean());
            Assert.True(burn.RootElement.GetProperty("verified").GetBoolean());
        }

        // Track 1 is Mode 1 data, Track 2 is Audio starting at pagesStart.
        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { false, true }, tracks.Select(t => t.IsAudio));
        Assert.Equal(0, tracks[0].StartLba);
        Assert.Equal(pagesStart, tracks[1].StartLba);
        Assert.Equal(pagesEnd - pagesStart, tracks[1].SectorCount);

        // verify --cue checks the mixed-mode disc against the CUE sheet.
        (code, output, error) = await RunAsync("verify", "--drive", "VDRV-01", "--cue", cue, "--log-dir", P("logs"), "--json");
        Assert.True(code == 0, error + output);
        Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("success").GetBoolean());

        // Read Track 2 back from the virtual disc and verify page 1 crosshair & rim tick pixels match RosettaBook.
        int trackSectors = (int)(pagesEnd - pagesStart);
        byte[] track2Bytes = new byte[trackSectors * 2352];
        for (int i = 0; i < trackSectors; i++)
        {
            medium.ReadSector((int)pagesStart + i, track2Bytes.AsSpan(i * 2352, 2352), 2352);
        }
        // Swap little-endian CD-DA samples to big-endian byte stream for palette lookup.
        CdAudioSamples.SwapByteOrder(track2Bytes);

        double startRadiusMm = built.GetProperty("plan").GetProperty("pagesStartRadiusMm").GetDouble();
        double outerRadiusMm = built.GetProperty("plan").GetProperty("pagesOuterRadiusMm").GetDouble();
        double pagesInnerMm = built.GetProperty("plan").GetProperty("pagesInnerRadiusMm").GetDouble();
        double rimInnerMm = built.GetProperty("plan").GetProperty("rimInnerRadiusMm").GetDouble();
        var full = new CdDiscParameters(24.825, 58.0, SmallDiscSectors);
        var trackParams = new CdDiscParameters(startRadiusMm, 58.0, SmallDiscSectors - pagesStart,
            StartAngleRadians: full.ThetaFromRadius(startRadiusMm), UsableSectors: trackSectors);
        var book = RosettaBook.Build(
            [
                new RosettaDocument("a-note", File.ReadAllText(Path.Combine(TextFolder, "a-note.txt")).Replace("\r\n", "\n", StringComparison.Ordinal)),
                new RosettaDocument("b-poem", File.ReadAllText(Path.Combine(TextFolder, "b-poem.md")).Replace("\r\n", "\n", StringComparison.Ordinal)),
            ],
            new RosettaArchiveOptions("CD Test", new DateOnly(2026, 11, 20), RosettaPresetCatalog.BuiltIn.Hobbyist,
                InnerRadiusMm: pagesInnerMm, OuterRadiusMm: outerRadiusMm + RosettaRimHeader.PageGutterMm + RosettaRimHeader.BandHeightMm,
                HasInnerFileVault: true, IncludeRimHeader: true));

        // Sample deinterleaved logical bytes across Track 2 and verify dark/light palette classification matches the book.
        long drawableEnd = CdCircEdgeZones.LogicalDrawableEndByte(track2Bytes.Length);
        int matchedBookInk = 0;
        int matchedRimInk = 0;
        int mismatched = 0;
        Func<double, double, byte> expectedSampler = RosettaDvdEncoder.CreateReadSideGrayscaleSampler(book, 0.0);
        for (long logical = CdCircEdgeZones.InnerEdgeZoneBytes; logical < drawableEnd; logical += 97)
        {
            long fileOffset = logical + CddaInterleaveTable.FileOffsetFor((int)(logical % CddaInterleaveTable.BytesPerFrame));
            if (fileOffset >= track2Bytes.Length) continue;
            byte palette = track2Bytes[fileOffset];
            Assert.True(palette is 0x0D or 0xAA, $"Unexpected palette byte 0x{palette:X2} at logical {logical}");

            (double rMm, double theta) = trackParams.PolarFromByte(logical);
            byte expected = expectedSampler(rMm * Math.Cos(theta), rMm * Math.Sin(theta));
            bool actualInk = palette == 0x0D;
            bool expectedInk = expected < 128;
            if (actualInk != expectedInk) mismatched++;
            else if (actualInk)
            {
                if (rMm >= rimInnerMm) matchedRimInk++;
                else matchedBookInk++;
            }
        }

        Assert.Equal(0, mismatched);
        Assert.True(matchedBookInk > 100, $"Expected >100 matched page ink samples, got {matchedBookInk}");
        Assert.True(matchedRimInk > 20, $"Expected >20 matched rim ink samples, got {matchedRimInk}");

        // recover --drive reads the mixed-mode CD's Mode 1 vault without tripping on Track 2 audio.
        (code, output, error) = await RunAsync("recover", "--drive", "VDRV-01", "--out", P("cd-out"), "--json");
        Assert.True(code == 0, error + output);
        JsonElement report = JsonDocument.Parse(output).RootElement;
        Assert.True(report.GetProperty("success").GetBoolean());
        Assert.Equal(0, report.GetProperty("unreadableSectors").GetInt32());
        AssertVaultMatchesSources(P("cd-out"));
    }

    [Fact]
    public async Task Burn_OnSimulatedCdR_WithPar2_RepairsDamagedTrack1()
    {
        if (_par2 == null) return;
        (string cue, string bin, JsonElement built) = await BuildCdAsync(redundancy: 10, cells: "hobbyist");
        Assert.True(built.GetProperty("recoverySectors").GetInt32() > 0);

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        (int code, string output, string error) = await RunAsync("rosetta", "burn", "--drive", "VDRV-01", "--image", cue,
            "--log-dir", P("logs"), "--json");
        Assert.True(code == 0, error + output);
        Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("verified").GetBoolean());

        // Damage Track 1 files in the .bin (beyond RSPC P/Q repair, i.e. corrupting sync + data) and recover via CUE.
        int dataLba;
        int textLba;
        long pagesStart = built.GetProperty("pagesStartLba").GetInt64();
        byte[] binBytes = await File.ReadAllBytesAsync(bin);
        byte[] track1Iso = new byte[(pagesStart - 150) * 2048];
        for (int lba = 0; lba < pagesStart - 150; lba++)
        {
            RawSectorCodec.Extract2048From2352(binBytes.AsSpan(lba * 2352, 2352), track1Iso.AsSpan(lba * 2048, 2048), out _);
        }
        using (var ms = new MemoryStream(track1Iso, writable: false))
        using (var reader = IsoImageReader.FromStream(ms))
        {
            dataLba = reader.AllFiles.Single(f => f.VirtualPath.EndsWith("/sub/data.bin", StringComparison.Ordinal)).StartLba;
            textLba = reader.AllFiles.Single(f => f.VirtualPath.EndsWith("/b-poem.md", StringComparison.Ordinal)).StartLba;
        }

        await using (var stream = new FileStream(bin, FileMode.Open, FileAccess.Write))
        {
            foreach (int lba in Enumerable.Range(dataLba + 5, 20).Append(textLba))
            {
                stream.Position = lba * 2352L;
                stream.Write(new byte[2352]);
            }
        }

        (code, output, error) = await RunAsync("recover", "--image", cue, "--out", P("cd-repaired"), "--json");
        Assert.True(code == 0, error + output);
        JsonElement report = JsonDocument.Parse(output).RootElement;
        string[] repaired = report.GetProperty("repairedFiles").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains(repaired, f => f.EndsWith("data.bin", StringComparison.Ordinal));
        Assert.Contains(repaired, f => f.EndsWith("b-poem.md", StringComparison.Ordinal));
        AssertVaultMatchesSources(P("cd-repaired"));
    }

    // ---- refusals --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cd_Sub50x75Presets_AndWrongExtension_AreRefused_WithTheReason()
    {
        (int code, _, string error) = await RunAsync("rosetta", "build", "--media", "cd",
            "--cells", "high-density", "--text", WriteTexts(), "--output", P("cd.cue"));
        Assert.Equal(1, code);
        Assert.Contains("50 x 75 um", error);

        (code, _, error) = await RunAsync("rosetta", "build", "--media", "cd",
            "--cells", "ladder-34x51", "--text", WriteTexts(), "--output", P("cd.cue"));
        Assert.Equal(1, code);
        Assert.Contains("50 x 75 um", error);

        (code, _, error) = await RunAsync("rosetta", "build", "--media", "cd",
            "--text", WriteTexts(), "--output", P("cd.iso"));
        Assert.Equal(1, code);
        Assert.Contains(".cue", error);

        (code, _, error) = await RunAsync("rosetta", "build", "--media", "dvd",
            "--no-interleave", "--text", WriteTexts(), "--output", P("dvd.iso"));
        Assert.Equal(1, code);
        Assert.Contains("CD-R", error);

        (code, _, error) = await RunAsync("rosetta", "burn", "--drive", "VDRV-01", "--image", P("cd.bin"));
        Assert.Equal(1, code);
        Assert.Contains(".cue", error);
    }

    [Fact]
    public async Task Build_Cd_NoInterleave_AndNoRim_PlacesPagesWithoutCircPullback()
    {
        string cue = P("cd-plain.cue");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--media", "cd", "--no-interleave", "--no-rim",
            "--text", WriteTexts(), "--output", cue, "--title", "No Interleave", "--date", "2026-11-20",
            "--disc-presets-file", WritePresets(), "--disc-preset", "small-cd", "--redundancy", "0", "--json");
        Assert.True(code == 0, error + output);

        JsonElement root = JsonDocument.Parse(output).RootElement;
        Assert.False(root.GetProperty("cdInterleave").GetBoolean());
        JsonElement plan = root.GetProperty("plan");
        Assert.False(plan.TryGetProperty("rimInnerRadiusMm", out _));

        long expectedAt38 = (long)Math.Floor(SmallDiscSectors * (38.0 * 38 - 24.825 * 24.825) / (58.0 * 58 - 24.825 * 24.825));
        Assert.Equal(expectedAt38, plan.GetProperty("pagesStartLba").GetInt64());
    }

    [Fact]
    public async Task FilesThatDoNotFitInsideThePagesRadius_AreRefused()
    {
        string files = P("big");
        Directory.CreateDirectory(files);
        await File.WriteAllBytesAsync(Path.Combine(files, "big.bin"), new byte[9 * 1024 * 1024]);

        (int code, _, string error) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--files", files,
            "--output", P("big.iso"), "--title", "Big", "--disc-presets-file", WritePresets(), "--disc-preset", "small-dvd",
            "--redundancy", "0");

        Assert.Equal(1, code);
        Assert.Contains("inside r = 38 mm", error);
        Assert.False(File.Exists(P("big.iso")));
    }

    [Fact]
    public async Task Burn_RefusesAnImageNotMadeByRosettaBuild()
    {
        string iso = P("other.iso");
        await File.WriteAllBytesAsync(iso, new byte[64 * 2048]);

        (int code, _, string error) = await RunAsync("rosetta", "burn", "--drive", "VDRV-01", "--image", iso);

        Assert.Equal(1, code);
        Assert.Contains("rosetta build", error);
    }

    [Fact]
    public async Task Help_ListsBuildAndBurn()
    {
        (int code, string output, _) = await RunAsync("rosetta", "--help");
        Assert.Equal(0, code);
        Assert.Contains("rosetta build", output);
        Assert.Contains("rosetta burn", output);

        (code, output, _) = await RunAsync("--help");
        Assert.Equal(0, code);
        Assert.Contains("rosetta build", output);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private string TextFolder => P("text");

    private string WriteTexts()
    {
        if (Directory.Exists(TextFolder)) return TextFolder;
        Directory.CreateDirectory(TextFolder);
        File.WriteAllText(Path.Combine(TextFolder, "a-note.txt"), "First line.\r\nSecond line, with CRLF in the source.\r\n");
        var poem = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            poem.Append("Line ").Append(i).Append(": the quick brown fox jumps over the lazy dog 0O 1Il 5S 8B.\n");
        }

        File.WriteAllText(Path.Combine(TextFolder, "b-poem.md"), poem.ToString());
        File.WriteAllText(Path.Combine(TextFolder, "ignored.bin"), "not text");
        return TextFolder;
    }

    private string FilesFolder
    {
        get
        {
            string folder = P("files");
            if (Directory.Exists(folder)) return folder;
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "readme.txt"), "extra file in the vault\n");
            byte[] data = new byte[400_000];
            new Random(103).NextBytes(data);
            File.WriteAllBytes(Path.Combine(folder, "sub", "data.bin"), data);
            return folder;
        }
    }

    private string WritePresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 4,
              "cdPresets": [
                { "id": "small-cd", "displayName": "Small CD", "description": "test", "sectors": {{SmallDiscSectors}},
                  "innerRadiusMm": 24.825, "outerRadiusMm": 58, "linearVelocityMmPerSecond": 1200 }
              ],
              "bluRayPresets": [],
              "dvdPresets": [
                { "id": "small-dvd", "displayName": "Small DVD", "description": "test", "totalSectors": {{SmallDiscSectors}},
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ]
            }
            """);
        return path;
    }

    private async Task<(string Cue, string Bin, JsonElement Result)> BuildCdAsync(int redundancy, string cells)
    {
        string cue = P("hybrid-cd.cue");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--media", "cd", "--cells", cells,
            "--text", WriteTexts(), "--files", FilesFolder, "--output", cue, "--title", "CD Test", "--date", "2026-11-20",
            "--disc-presets-file", WritePresets(), "--disc-preset", "small-cd",
            "--redundancy", redundancy.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json");
        Assert.True(code == 0, error + output);
        JsonElement json = JsonDocument.Parse(output).RootElement.Clone();
        return (json.GetProperty("cueSheetPath").GetString()!, json.GetProperty("binaryPath").GetString()!, json);
    }

    private async Task<(string Iso, JsonElement Result)> BuildAsync(int redundancy)
    {
        string iso = P("hybrid.iso");
        (int code, string output, string error) = await RunAsync("rosetta", "build", "--text", WriteTexts(), "--files", FilesFolder,
            "--output", iso, "--title", "Test Archive", "--date", "2026-11-17",
            "--disc-presets-file", WritePresets(), "--disc-preset", "small-dvd",
            "--redundancy", redundancy.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json");
        Assert.True(code == 0, error + output);
        return (iso, JsonDocument.Parse(output).RootElement.Clone());
    }

    private static Dictionary<string, byte[]> ReadFiles(string iso)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var reader = IsoImageReader.FromFile(iso);
        using var stream = File.OpenRead(iso);
        foreach (IsoFileEntry file in reader.AllFiles)
        {
            byte[] bytes = new byte[file.SizeBytes];
            stream.Position = file.StartLba * 2048L;
            stream.ReadExactly(bytes);
            files[file.VirtualPath] = bytes;
        }

        return files;
    }

    private void AssertVaultMatchesSources(string recovered)
    {
        Assert.Equal(
            "First line.\nSecond line, with CRLF in the source.\n",
            File.ReadAllText(Path.Combine(recovered, "ROSETTA", "TEXT", "a-note.txt")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(TextFolder, "b-poem.md")),
            File.ReadAllBytes(Path.Combine(recovered, "ROSETTA", "TEXT", "b-poem.md")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(FilesFolder, "sub", "data.bin")),
            File.ReadAllBytes(Path.Combine(recovered, "sub", "data.bin")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(FilesFolder, "readme.txt")),
            File.ReadAllBytes(Path.Combine(recovered, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(recovered, "ROSETTA", "PAGE-INDEX.TXT")));
        Assert.False(File.Exists(Path.Combine(recovered, "ROSETTA", "TEXT", "ignored.bin")));
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _devices, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
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
