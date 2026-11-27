using System.Buffers.Binary;
using CdBurner.Core.BluRayVideo;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Subtitles;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Blu-ray Video (BDMV / AVCHD) authoring. FFmpeg is replaced by a runner that writes a hand-built transport stream
/// (known picture times, key frames, PCRs), so every number the muxer and the file writers produce can be compared
/// with what the stream really contains. <see cref="BdmvStructuralValidator"/> reads the result back without using
/// any writer code.
/// </summary>
public sealed class BdmvAuthoringAndValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BdmvTests_" + Guid.NewGuid().ToString("N"));

    public BdmvAuthoringAndValidatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ----- fixtures -------------------------------------------------------------------------------------------

    private sealed class FakeFfmpeg(int frames) : IExternalToolRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        /// <summary>The video stream line the input report gives (FFmpeg's own wording); SDR H.264 unless set.</summary>
        public string VideoStreamLine { get; init; } = "  Stream #0:0: Video: h264 (High), yuv420p(tv, bt709, progressive), 1920x1080, 25 fps";

        /// <summary>False makes an FFmpeg build without the zimg filters.</summary>
        public bool HasZscale { get; init; } = true;

        public IReadOnlyList<string> Encode => Calls.Single(c => c.Contains("-c:v"));

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onOutputLine, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToList());
            if (arguments.Contains("-encoders"))
            {
                onOutputLine?.Invoke(" V....D libx264              libx264 H.264 / AVC");
                onOutputLine?.Invoke(" A....D ac3                  ATSC A/52A (AC-3)");
                onOutputLine?.Invoke(" A....D pcm_bluray           PCM signed 16|20|24-bit big-endian for Blu-ray media");
                return Task.FromResult(0);
            }
            if (arguments.Contains("-filters"))
            {
                onOutputLine?.Invoke(" .S tonemap           V->V       Conversion to/from different dynamic ranges.");
                if (HasZscale)
                {
                    onOutputLine?.Invoke(" .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.");
                }
                return Task.FromResult(0);
            }
            if (HdrToneMapping.IsProbe(arguments))
            {
                onOutputLine?.Invoke("Input #0, matroska,webm, from 'movie.mkv':");
                onOutputLine?.Invoke(VideoStreamLine);
                onOutputLine?.Invoke("At least one output file must be specified");
                return Task.FromResult(1); // FFmpeg exits 1 when given no output
            }
            File.WriteAllBytes(arguments[^1], SyntheticTransportStream.Create(frames));
            return Task.FromResult(0);
        }
    }

    private string MakeMovie(string name = "movie.mp4")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    private string FakeFfmpegExe()
    {
        string path = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private static Dictionary<string, byte[]> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private async Task<(BdmvAuthoringResult Result, string Root)> AuthorAsync(
        BdmvAuthoringOptions options, int frames = 96, IReadOnlyList<string>? movies = null, string rootName = "out")
    {
        options.CustomFfmpegPath = FakeFfmpegExe();
        string root = Path.Combine(_dir, rootName);
        var service = new BdmvAuthoringService { ToolRunner = new FakeFfmpeg(frames) };
        var result = await service.AuthorAsync(movies ?? [MakeMovie()], root, options);
        return (result, root);
    }

    // ----- known answers written out by hand from the format layouts --------------------------------------------

    [Fact]
    public void IndexBdmv_OneTitle_MatchesTheHandWrittenBytes()
    {
        byte[] actual = BdmvBinaryWriters.WriteIndex(BdmvDiscTarget.BluRay, BdmvVideoFormat.Hd1080p, BdmvFrameRate.Fps23976, 1);
        string expected =
            "494E4458" + "30323030" +          // INDX 0200
            "0000004E" + "00000000" +          // index table at byte 78, no extension data
            new string('0', 48) +              // 24 reserved bytes
            "00000022" + "00" + "61" + new string('0', 64) + // app info: length 34, 2D, 1080p (6) at 23.976 (1), 32 bytes provider data
            "00000026" +                       // index table length: 12 + 12 + 2 + 12
            "400000000000000000000000" +       // first playback: HDMV object 0
            "400000000000000000000000" +       // top menu: HDMV object 0
            "0001" +                           // one title
            "400000000000000000000000";        // title 1: HDMV object 0
        Assert.Equal(expected, Convert.ToHexString(actual));
    }

    [Fact]
    public void MovieObjectBdmv_OneTitle_MatchesTheHandWrittenBytes()
    {
        byte[] actual = BdmvBinaryWriters.WriteMovieObject(BdmvDiscTarget.BluRay, 1);
        string expected =
            "4D4F424A" + "30323030" + "00000000" + new string('0', 56) + // MOBJ 0200, no extension, padding to byte 40
            "00000016" + "00000000" +          // length 22 after this field, reserved
            "0001" +                           // one movie object
            "0000" + "0001" +                  // flags, one command
            "228000000000000000000000";        // PlayPL playlist 0: 1 operand, branch/play, immediate, option 0
        Assert.Equal(expected, Convert.ToHexString(actual));
    }

    [Fact]
    public void DiscId_HasTheBdidLayoutAndIsStable()
    {
        byte[] a = BdmvBinaryWriters.WriteDiscId(BdmvDiscTarget.BluRay, "seed");
        byte[] b = BdmvBinaryWriters.WriteDiscId(BdmvDiscTarget.BluRay, "seed");
        byte[] c = BdmvBinaryWriters.WriteDiscId(BdmvDiscTarget.BluRay, "other");
        Assert.Equal(60, a.Length);
        Assert.Equal("BDID0200", System.Text.Encoding.ASCII.GetString(a, 0, 8));
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(8)));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Crc32Mpeg_MatchesTheKnownCheckValue()
    {
        // CRC-32/MPEG-2 of "123456789" is 0x0376E6E7 (from the CRC catalogue).
        Assert.Equal(0x0376E6E7u, BdmvStructuralValidator.Crc32Mpeg("123456789"u8));
    }

    // ----- entry-point map --------------------------------------------------------------------------------------

    [Fact]
    public void EntryPointMap_RoundTripsPointsAcrossCoarseBoundaries()
    {
        // Times chosen to cross the 2^19-tick (11.65 s) coarse step and packet numbers to cross 2^17 (131,072).
        long[] ptsSeconds = [2, 7, 13, 14, 100, 101, 3000, 3001];
        long[] spns = [0, 5, 131_071, 131_072, 131_073, 300_000, 1L << 24, (1L << 24) + 9];
        var points = ptsSeconds.Select((s, i) => new BdmvEntryPoint(s * 90_000 + i * 11, spns[i])).ToList();
        var streams = new BdmvClipStreams(0x1011, 0x1100, 3, BdmvAudioCodec.Ac3, "eng", [], 0x0100, BdmvVideoFormat.Hd1080p, BdmvFrameRate.Fps23976);
        var facts = new BdmvStreamFacts(1L << 25, 90_000, 3_100 * 45_000L, 5_000_000, points);
        byte[] clpi = BdmvBinaryWriters.WriteClipInfo(BdmvDiscTarget.BluRay, streams, facts);

        uint cpi = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(16));
        int ep = (int)cpi + 6;
        ulong packed = ((ulong)BinaryPrimitives.ReadUInt16BigEndian(clpi.AsSpan(ep + 4)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(ep + 6));
        int coarseCount = (int)((packed >> 18) & 0xFFFF);
        int fineCount = (int)(packed & 0x3FFFF);
        Assert.Equal(points.Count, fineCount);
        Assert.True(coarseCount > 1 && coarseCount < fineCount, "coarse entries group fine entries");
        int start = ep + (int)BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(ep + 10));
        uint fineStart = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(start));

        var decoded = new List<(long Pts, long Spn)>();
        for (int j = 0; j < fineCount; j++)
        {
            uint fine = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan((int)(start + fineStart + 4 * j)));
            int owner = -1;
            for (int i = 0; i < coarseCount; i++)
            {
                uint a = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(start + 4 + 8 * i));
                if (a >> 14 <= j) owner = i;
            }
            uint ca = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(start + 4 + 8 * owner));
            uint cspn = BinaryPrimitives.ReadUInt32BigEndian(clpi.AsSpan(start + 8 + 8 * owner));
            long pts = (((long)(ca & 0x3FFF) & ~1L) << 18) + ((long)((fine >> 17) & 0x7FF) << 8);
            long spn = (cspn & ~0x1FFFFL) + (fine & 0x1FFFF);
            decoded.Add((pts, spn));
        }

        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal((points[i].Pts90k >> 1) >> 8 << 8, decoded[i].Pts);
            Assert.Equal(points[i].SourcePacketNumber, decoded[i].Spn);
        }
    }

    // ----- BDAV muxing ------------------------------------------------------------------------------------------

    [Fact]
    public void Mux_WritesAlignedUnitsOf192BytePacketsWithSteadyArrivalTimes()
    {
        var streams = new BdmvClipStreams(0x1011, 0x1100, 3, BdmvAudioCodec.Ac3, "eng", [], 0x0100, BdmvVideoFormat.Hd1080p, BdmvFrameRate.Fps23976);
        using var input = new MemoryStream(SyntheticTransportStream.Create(100));
        using var output = new MemoryStream();
        var facts = BdavTsMuxer.Mux(input, output, streams, [], BdmvFrameRate.Fps23976);

        byte[] data = output.ToArray();
        Assert.Equal(facts.SourcePackets * 192, data.Length);
        Assert.Equal(0, data.Length % 6144);
        long previous = -1;
        for (int i = 0; i < facts.SourcePackets; i++)
        {
            Assert.Equal(0x47, data[i * 192 + 4]);
            uint header = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 192));
            Assert.Equal(0u, header >> 30);
            long atc = header & ((1L << 30) - 1);
            if (previous >= 0)
            {
                long step = (atc - previous) & ((1L << 30) - 1);
                Assert.InRange(step, 0, 100_000);   // under 3.7 ms between packets: no jumps, wrap handled
            }
            previous = atc;
        }

        // 100 pictures, a key frame every 24: pictures 0, 24, 48, 72, 96.
        Assert.Equal(5, facts.EntryPoints.Count);
        Assert.Equal(SyntheticTransportStream.PictureTime90k(0) >> 1, facts.StartPts45k);
        Assert.Equal(SyntheticTransportStream.PictureTime90k(99) >> 1, facts.EndPts45k - BdmvBinaryWriters.FrameDuration45k(BdmvFrameRate.Fps23976));
    }

    [Fact]
    public void Mux_DropsForeignPidsAndRewritesTheProgramMap()
    {
        var streams = new BdmvClipStreams(0x1011, 0x1100, 3, BdmvAudioCodec.Lpcm, "eng", [(0x1200, "eng")], 0x0100, BdmvVideoFormat.Hd1080p, BdmvFrameRate.Fps23976);
        using var input = new MemoryStream(SyntheticTransportStream.Create(30));
        using var output = new MemoryStream();
        BdavTsMuxer.Mux(input, output, streams, [new List<PgsDisplaySet>()], BdmvFrameRate.Fps23976);
        byte[] data = output.ToArray();

        var pids = new HashSet<int>();
        byte[]? pmt = null;
        for (int i = 0; i < data.Length / 192; i++)
        {
            int pid = ((data[i * 192 + 5] & 0x1F) << 8) | data[i * 192 + 6];
            pids.Add(pid);
            if (pid == 0x0100 && pmt == null)
            {
                pmt = data.AsSpan(i * 192 + 4, 188).ToArray();
            }
        }
        Assert.DoesNotContain(0x11, pids);          // the service-description table of the input is gone
        Assert.Equal(new[] { 0, 0x100, 0x1011, 0x1100, 0x1FFF }.Where(pids.Contains).OrderBy(x => x), pids.OrderBy(x => x));
        Assert.NotNull(pmt);
        int sectionLength = ((pmt![6] & 0x0F) << 8) | pmt[7];
        Assert.Equal(0u, BdmvStructuralValidator.Crc32Mpeg(pmt.AsSpan(5, 3 + sectionLength)));
        // Streams: video 0x1B, LPCM 0x80, graphics 0x90.
        string hex = Convert.ToHexString(pmt.AsSpan(5, 3 + sectionLength));
        Assert.Contains("1BF011F000", hex);
        Assert.Contains("80F100F000", hex);
        Assert.Contains("90F200F000", hex);
    }

    [Fact]
    public void Mux_RefusesAStreamWithoutKeyFrames()
    {
        var streams = new BdmvClipStreams(0x1011, 0x1100, 3, BdmvAudioCodec.Ac3, "eng", [], 0x0100, BdmvVideoFormat.Hd1080p, BdmvFrameRate.Fps23976);
        using var input = new MemoryStream(SyntheticTransportStream.Create(20, keyInterval: 0));
        Assert.Throws<InvalidDataException>(() => BdavTsMuxer.Mux(input, new MemoryStream(), streams, [], BdmvFrameRate.Fps23976));
    }

    // ----- whole trees ------------------------------------------------------------------------------------------

    [Fact]
    public async Task BluRayTree_HasEveryFileAndPassesTheStructuralValidator()
    {
        var (result, root) = await AuthorAsync(new BdmvAuthoringOptions { VolumeLabel = "TEST_BD" });
        Assert.True(result.Success, result.ErrorMessage);

        var tree = ReadTree(root);
        foreach (string expected in new[]
        {
            "BDMV/index.bdmv", "BDMV/MovieObject.bdmv", "BDMV/PLAYLIST/00000.mpls", "BDMV/CLIPINF/00000.clpi", "BDMV/STREAM/00000.m2ts",
            "BDMV/BACKUP/index.bdmv", "BDMV/BACKUP/MovieObject.bdmv", "BDMV/BACKUP/PLAYLIST/00000.mpls", "BDMV/BACKUP/CLIPINF/00000.clpi",
            "CERTIFICATE/id.bdmv", "CERTIFICATE/BACKUP/id.bdmv",
        })
        {
            Assert.True(tree.ContainsKey(expected), $"{expected} missing");
        }
        Assert.Equal(tree.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), result.GeneratedFiles.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        var validation = BdmvStructuralValidator.Validate(tree, avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
        var clip = Assert.Single(result.Clips);
        Assert.Equal(4, clip.EntryPoints);
        Assert.Equal(validation.Scans["00000"].Packets, clip.SourcePackets);
        Assert.Equal("INDX0200", System.Text.Encoding.ASCII.GetString(tree["BDMV/index.bdmv"], 0, 8));
    }

    [Fact]
    public async Task AvchdTree_UsesTheShortNamesAndVersion0100()
    {
        var (result, root) = await AuthorAsync(new BdmvAuthoringOptions { Target = BdmvDiscTarget.Avchd, VideoFormat = BdmvVideoFormat.Hd1080i, FrameRate = BdmvFrameRate.Fps25 });
        Assert.True(result.Success, result.ErrorMessage);
        var tree = ReadTree(root);
        Assert.True(tree.ContainsKey("BDMV/STREAM/00000.MTS"));
        Assert.True(tree.ContainsKey("BDMV/CLIPINF/00000.CPI"));
        Assert.True(tree.ContainsKey("BDMV/PLAYLIST/00000.MPL"));
        Assert.Equal("INDX0100", System.Text.Encoding.ASCII.GetString(tree["BDMV/index.bdmv"], 0, 8));
        var validation = BdmvStructuralValidator.Validate(tree, avchd: true);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task SeveralMovies_BecomeSeveralClipsPlaylistsAndTitles()
    {
        var movies = new[] { MakeMovie("a.mp4"), MakeMovie("b.mp4"), MakeMovie("c.mp4") };
        var (result, root) = await AuthorAsync(new BdmvAuthoringOptions(), frames: 50, movies: movies);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Clips.Count);
        var tree = ReadTree(root);
        foreach (string n in new[] { "00000", "00001", "00002" })
        {
            Assert.True(tree.ContainsKey($"BDMV/STREAM/{n}.m2ts") && tree.ContainsKey($"BDMV/CLIPINF/{n}.clpi") && tree.ContainsKey($"BDMV/PLAYLIST/{n}.mpls"));
        }
        var validation = BdmvStructuralValidator.Validate(tree, avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task Chapters_AreMarkedEveryIntervalInsideThePlayItem()
    {
        // 96 pictures at 23.976 fps is 4 s: too short for a 1 s interval to leave room before the end guard, so use a longer clip.
        var (result, root) = await AuthorAsync(new BdmvAuthoringOptions { ChapterIntervalSeconds = 10 }, frames: 24 * 40);
        Assert.True(result.Success, result.ErrorMessage);
        byte[] mpls = File.ReadAllBytes(Path.Combine(root, "BDMV", "PLAYLIST", "00000.mpls"));
        int mark = (int)BinaryPrimitives.ReadUInt32BigEndian(mpls.AsSpan(12));
        int count = BinaryPrimitives.ReadUInt16BigEndian(mpls.AsSpan(mark + 4));
        Assert.Equal(4, count);   // at 0, 10, 20, 30 s of a 40 s clip (the one at 40 s would be past the end)
        var times = Enumerable.Range(0, count).Select(k => BinaryPrimitives.ReadUInt32BigEndian(mpls.AsSpan(mark + 6 + 14 * k + 4))).ToList();
        for (int k = 1; k < count; k++)
        {
            Assert.Equal(10 * 45_000u, times[k] - times[k - 1]);
        }
    }

    [Fact]
    public async Task OptionsAndInputs_AreCheckedBeforeAnythingIsWritten()
    {
        var service = new BdmvAuthoringService { ToolRunner = new FakeFfmpeg(10) };
        var missing = await service.AuthorAsync([Path.Combine(_dir, "nope.mp4")], Path.Combine(_dir, "o1"), new BdmvAuthoringOptions());
        Assert.False(missing.Success);
        Assert.Contains("not found", missing.ErrorMessage);

        Assert.Contains("frame rate", BdmvAuthoringService.ValidateOptions(new BdmvAuthoringOptions { VideoFormat = BdmvVideoFormat.Hd1080p, FrameRate = BdmvFrameRate.Fps25 }));
        Assert.Contains("stereo", BdmvAuthoringService.ValidateOptions(new BdmvAuthoringOptions { AudioCodec = BdmvAudioCodec.Lpcm, AudioChannels = 6 }));
        Assert.Contains("subtitle", BdmvAuthoringService.ValidateOptions(new BdmvAuthoringOptions { Subtitles = { new BdmvSubtitleSpec(null) } }));
        Assert.Null(BdmvAuthoringService.ValidateOptions(new BdmvAuthoringOptions()));

        var options = new BdmvAuthoringOptions { CustomFfmpegPath = FakeFfmpegExe(), CapacityBytes = 50_000 };
        var tooBig = await new BdmvAuthoringService { ToolRunner = new FakeFfmpeg(96) }.AuthorAsync([MakeMovie()], Path.Combine(_dir, "o2"), options);
        Assert.False(tooBig.Success);
        Assert.Contains("GB", tooBig.ErrorMessage);
    }

    [Fact]
    public async Task TheTranscodeCommand_AsksForBlurayCompatibleStreams()
    {
        var fake = new FakeFfmpeg(40);
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = FakeFfmpegExe() };
        var result = await new BdmvAuthoringService { ToolRunner = fake }.AuthorAsync([MakeMovie()], Path.Combine(_dir, "cmd"), options);
        Assert.True(result.Success, result.ErrorMessage);
        var args = fake.Encode;
        string joined = string.Join(' ', args);
        Assert.Contains("bluray-compat=1", joined);
        Assert.Contains("open-gop=0", joined);
        Assert.Contains("vbv-bufsize=30000", joined);
        Assert.Contains("-level:v 4.1", joined);
        Assert.Contains("fps=24000/1001", joined);
        Assert.Contains("-streamid 0:4113 -streamid 1:4352", joined);   // 0x1011 and 0x1100
        Assert.Contains("-c:a ac3", joined);
        Assert.DoesNotContain("tonemap", joined);                        // SDR source: no tone mapping
        Assert.DoesNotContain(fake.Calls, c => c.Contains("-filters"));
    }

    // ----- HDR sources ----------------------------------------------------------------------------------------

    /// <summary>FFmpeg's own report line for a 4K HDR10 film (2026-11-26).</summary>
    private const string Hdr10Line = "  Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x1608 [SAR 1:1 DAR 160:67], 23.98 fps, 23.98 tbr, 1k tbn (default)";

    [Theory]
    [InlineData(Hdr10Line, VideoTransfer.Pq, "bt2020nc")]
    [InlineData("  Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/arib-std-b67), 3840x2160, 50 fps", VideoTransfer.Hlg, "bt2020nc")]
    [InlineData("  Stream #0:0(eng): Video: hevc (Main 10), yuv420p10le(tv, bt2020c/bt2020/smpte2084, progressive), 3840x2160", VideoTransfer.Pq, "bt2020c")]
    [InlineData("  Stream #0:0: Video: h264 (High), yuv420p(tv, bt709, progressive), 1920x1080, 25 fps", VideoTransfer.Sdr, null)]
    [InlineData("  Stream #0:0: Video: mpeg4 (Simple Profile), yuv420p, 640x480, 25 fps", VideoTransfer.Sdr, null)]
    public void InputReport_GivesTheTransferAndMatrix(string line, VideoTransfer transfer, string? matrix)
    {
        var info = HdrToneMapping.ParseFfmpegInputReport(["Input #0, matroska,webm, from 'm.mkv':", "  Duration: 03:06:35.54", line,
            "  Stream #0:1(eng): Audio: eac3, 48000 Hz, 7.1, fltp, 1024 kb/s (default)"]);
        Assert.Equal(transfer, info.Transfer);
        Assert.Equal(matrix, info.Matrix);
    }

    [Fact]
    public void InputReport_WithoutVideo_IsSdr() =>
        Assert.False(HdrToneMapping.ParseFfmpegInputReport(["  Stream #0:0: Audio: flac, 44100 Hz, stereo"]).IsHdr);

    [Fact]
    public void ToneMapFilter_ConvertsPqAndHlg_AndLeavesSdrAlone()
    {
        Assert.Null(HdrToneMapping.ToneMapFilter(new VideoColorInfo(VideoTransfer.Sdr, null)));
        string pq = HdrToneMapping.ToneMapFilter(new VideoColorInfo(VideoTransfer.Pq, "bt2020nc"))!;
        Assert.StartsWith("zscale=tin=smpte2084:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,", pq);
        Assert.Contains("zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv", pq);
        Assert.Contains("tin=arib-std-b67:min=bt2020nc", HdrToneMapping.ToneMapFilter(new VideoColorInfo(VideoTransfer.Hlg, null)));
    }

    [Fact]
    public void FilterListing_NeedsBothZscaleAndTonemap()
    {
        string[] full = [" .S tonemap           V->V       Conversion to/from different dynamic ranges.",
                         " .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.",
                         " .. tonemap_opencl    V->V       Perform HDR to SDR conversion with tonemapping."];
        Assert.True(HdrToneMapping.FilterListingHasToneMapping(full));
        Assert.False(HdrToneMapping.FilterListingHasToneMapping([full[0], full[2]])); // tonemap_opencl is not zscale
    }

    [Fact]
    public async Task HdrMovie_IsToneMappedAfterScalingAndBeforeTheBars()
    {
        var fake = new FakeFfmpeg(40) { VideoStreamLine = Hdr10Line };
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = FakeFfmpegExe() };

        var result = await new BdmvAuthoringService { ToolRunner = fake }.AuthorAsync([MakeMovie()], Path.Combine(_dir, "hdr"), options);

        Assert.True(result.Success, result.ErrorMessage);
        string vf = fake.Encode[fake.Encode.ToList().IndexOf("-vf") + 1];
        int scale = vf.IndexOf("scale=1920:1080:force_original_aspect_ratio=decrease", StringComparison.Ordinal);
        int tone = vf.IndexOf("tonemap=tonemap=hable", StringComparison.Ordinal);
        int pad = vf.IndexOf("pad=1920:1080", StringComparison.Ordinal);
        Assert.True(scale >= 0 && scale < tone && tone < pad, vf);
        Assert.Contains("tin=smpte2084:min=bt2020nc", vf);
        Assert.Contains(result.LogNotes, n => n.Contains("tone-mapped"));
    }

    [Fact]
    public async Task HdrMovie_WithAnFfmpegWithoutZscale_StopsBeforeEncoding()
    {
        var fake = new FakeFfmpeg(40) { VideoStreamLine = Hdr10Line, HasZscale = false };
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = FakeFfmpegExe() };

        var result = await new BdmvAuthoringService { ToolRunner = fake }.AuthorAsync([MakeMovie()], Path.Combine(_dir, "nozimg"), options);

        Assert.False(result.Success);
        Assert.Contains("HDR", result.ErrorMessage);
        Assert.Contains("washed out", result.ErrorMessage);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("-c:v"));
    }

    [Fact]
    public async Task RealFfmpegOutput_PassesTheSameStructuralChecks_WhenAnEncoderIsInstalled()
    {
        // Uses the FFmpeg on this machine when it has libx264 (skips silently otherwise): real encoder output, real PCRs,
        // real key-frame flags, so the muxer is checked against a stream it did not make up.
        string? ffmpeg = Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG") ?? FfmpegLocator.FindFfmpegExecutable();
        if (ffmpeg == null || !File.Exists(ffmpeg))
        {
            return;
        }
        var runner = new ProcessToolRunner();
        var listing = new List<string>();
        if (await runner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], _dir, listing.Add, CancellationToken.None) != 0
            || !listing.Any(l => l.Contains("libx264")) || !listing.Any(l => l.Contains(" ac3 ")))
        {
            return;
        }

        string movie = Path.Combine(_dir, "real.mp4");
        int code = await runner.RunAsync(ffmpeg,
            ["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=3",
             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", movie],
            _dir, null, CancellationToken.None);
        Assert.Equal(0, code);

        string srt = Path.Combine(_dir, "real.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nReal stream subtitle\n");
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = ffmpeg, VideoBitrateKbps = 4000, MaxVideoBitrateKbps = 8000 };
        options.Subtitles.Add(new BdmvSubtitleSpec(srt, "eng"));
        string root = Path.Combine(_dir, "real-out");
        var result = await new BdmvAuthoringService().AuthorAsync([movie], root, options);
        Assert.True(result.Success, result.ErrorMessage);

        var validation = BdmvStructuralValidator.Validate(ReadTree(root), avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
        var scan = validation.Scans["00000"];
        Assert.True(scan.VideoPictures.Count(v => v.RandomAccess) >= 3, "x264 with one key frame per second gives at least 3 entry points in 3 s");
        Assert.Equal(new byte[] { 0x16, 0x17, 0x14, 0x15, 0x80, 0x16, 0x80 }, scan.Segments.Select(s => s.Type).ToArray());
        // FFmpeg's pictures start after time zero; the cue at 1 s must show 1 s after the first picture, not 1 s after zero.
        long firstPicture = scan.VideoPictures.Min(v => v.Pts90k);
        Assert.Equal(firstPicture + 90_000, scan.Segments.First(s => s.Type == 0x16).Pts90k);
    }

    [Fact]
    public void FirstVideoPts_IsReadFromTheStreamAndThePositionIsRestored()
    {
        using var ts = new MemoryStream(SyntheticTransportStream.Create(10));
        ts.Position = 188 * 3;
        Assert.Equal(SyntheticTransportStream.PictureTime90k(0), BdavTsMuxer.FindFirstVideoPts(ts, 0x1011));
        Assert.Equal(188 * 3, ts.Position);
        Assert.Null(BdavTsMuxer.FindFirstVideoPts(ts, 0x1234));
    }

    // ----- subtitles --------------------------------------------------------------------------------------------

    [Fact]
    public async Task SrtSubtitles_BecomePgsStreamsWithCorrectSegmentsAndTimes()
    {
        string srt = Path.Combine(_dir, "movie.srt");
        File.WriteAllText(srt, "1\n00:00:01,500 --> 00:00:02,500\nHello there\n\n2\n00:00:03,000 --> 00:00:03,900\nSecond line\nwith two rows\n");
        var options = new BdmvAuthoringOptions();
        options.Subtitles.Add(new BdmvSubtitleSpec(srt, "eng"));
        var (result, root) = await AuthorAsync(options, frames: 24 * 6);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, Assert.Single(result.Clips).Subtitles);

        var validation = BdmvStructuralValidator.Validate(ReadTree(root), avchd: false);
        Assert.True(validation.Errors.Count == 0, string.Join(Environment.NewLine, validation.Errors));
        var segments = validation.Scans["00000"].Segments;

        // Each cue: composition, window, palette, object, end (to show), then composition and end (to clear).
        var types = segments.Select(s => s.Type).ToList();
        Assert.Equal(new byte[] { 0x16, 0x17, 0x14, 0x15, 0x80, 0x16, 0x80, 0x16, 0x17, 0x14, 0x15, 0x80, 0x16, 0x80 }, types);

        var shows = segments.Where(s => s.Type == 0x16).ToList();
        long offset = SyntheticTransportStream.PictureTime90k(0);   // the stream's pictures start at 1 s; cue times count from the movie start`r`n        Assert.Equal(1_500L * 90 + offset, shows[0].Pts90k);
        Assert.Equal(2_500L * 90 + offset, shows[1].Pts90k);
        Assert.Equal(3_000L * 90 + offset, shows[2].Pts90k);
        Assert.Equal(3_900L * 90 + offset, shows[3].Pts90k);
        Assert.Equal(0x80, shows[0].Body[7]);    // epoch start
        Assert.Equal(1, shows[0].Body[10]);      // one composition object
        Assert.Equal(0x00, shows[1].Body[7]);    // normal state, no objects: clears the picture
        Assert.Equal(0, shows[1].Body[10]);
        Assert.All(shows, s => Assert.NotNull(s.Dts90k));

        // Decode the object bitmap with an independent reader of the run-length code.
        var ods = segments.First(s => s.Type == 0x15);
        int width = (ods.Body[7] << 8) | ods.Body[8], height = (ods.Body[9] << 8) | ods.Body[10];
        var pixels = DecodePgsRle(ods.Body.AsSpan(11), width, height);
        Assert.InRange(width, 100, 1900);
        Assert.InRange(height, 20, 200);
        Assert.Contains(pixels, p => p == 1);    // white text
        Assert.Contains(pixels, p => p == 2);    // black outline
        Assert.All(pixels, p => Assert.InRange(p, 0, 3));
        // The window holds the picture; the composition places the object inside the 1920x1080 plane.
        var wds = segments.First(s => s.Type == 0x17);
        Assert.Equal(width, (wds.Body[6] << 8) | wds.Body[7]);
        int x = (shows[0].Body[15] << 8) | shows[0].Body[16], y = (shows[0].Body[17] << 8) | shows[0].Body[18];
        Assert.True(x + width <= 1920 && y + height <= 1080);

        // Time order: within a display set nothing is presented after its composition.
        foreach (var set in SplitDisplaySets(segments))
        {
            long pcsPts = set[0].Pts90k;
            Assert.All(set, s => Assert.True(s.Pts90k <= pcsPts, "segments are ready by the composition time"));
            Assert.True(set[0].Dts90k <= pcsPts);
        }
    }

    [Fact]
    public async Task WhisperSubtitles_UseTheSpeechServiceResult()
    {
        string made = Path.Combine(_dir, "made.srt");
        File.WriteAllText(made, "1\n00:00:02,000 --> 00:00:03,000\nGenerated words\n");
        var options = new BdmvAuthoringOptions { CustomFfmpegPath = FakeFfmpegExe() };
        options.Subtitles.Add(new BdmvSubtitleSpec(null, "eng", GenerateWithWhisper: true));
        var service = new BdmvAuthoringService
        {
            ToolRunner = new FakeFfmpeg(96),
            SubtitleGenerator = (movie, progress, ct) => Task.FromResult(new SubtitleGenerationResult(true, made, 1, TimeSpan.Zero, null, false, "test", null)),
        };
        var result = await service.AuthorAsync([MakeMovie()], Path.Combine(_dir, "w"), options);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, Assert.Single(result.Clips).Subtitles);
        Assert.Contains(result.LogNotes, n => n.Contains("machine-generated"));
    }

    [Fact]
    public void PgsRle_RoundTripsRunsOfEveryLength_AndLargeObjectsSplitAcrossSegments()
    {
        // Rows with runs of 1, 2, 3, 63, 64, 300 and 5,000 pixels of colour 0 and of colour 2.
        int[] runs = [1, 2, 3, 63, 64, 300, 5_000];
        int width = 5_000 + 5;
        var rows = new List<byte[]>();
        foreach (int run in runs)
        {
            foreach (byte colour in new byte[] { 0, 2 })
            {
                var row = new byte[width];
                Array.Fill(row, colour, 0, run);
                row[run] = 1;
                rows.Add(row);
            }
        }
        byte[] pixels = rows.SelectMany(r => r).ToArray();
        byte[] rle = PgsSubtitleEncoder.EncodeRle(pixels, width, rows.Count);
        Assert.Equal(pixels, DecodePgsRle(rle, width, rows.Count));

        // A noisy 1900x500 picture does not compress: its coded size exceeds one segment (65,535 bytes).
        var rng = new Random(11);
        byte[] noise = new byte[1900 * 500];
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)rng.Next(1, 4);
        var bodies = PgsSubtitleEncoder.ObjectBodies(noise, 1900, 500).ToList();
        Assert.True(bodies.Count >= 2);
        Assert.All(bodies, b => Assert.True(b.Length <= PgsSubtitleEncoder.MaxSegmentBody));
        Assert.Equal(0x80, bodies[0][3] & 0x80);
        Assert.Equal(0, bodies[0][3] & 0x40);
        Assert.Equal(0x40, bodies[^1][3] & 0xC0);
        int declared = (bodies[0][4] << 16) | (bodies[0][5] << 8) | bodies[0][6];
        var joined = bodies[0].Skip(11).Concat(bodies.Skip(1).SelectMany(b => b.Skip(4))).ToArray();
        Assert.Equal(declared - 4, joined.Length);
        Assert.Equal(noise, DecodePgsRle(joined, 1900, 500));
    }

    [Fact]
    public void SupFile_HasTheDocumentedHeaderPerSegment()
    {
        var cues = new[] { new SrtCue(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), "Line") };
        var sets = PgsSubtitleEncoder.Encode(cues, 1920, 1080, BdmvFrameRate.Fps23976);
        Assert.Equal(2, sets.Count);
        using var ms = new MemoryStream();
        PgsSubtitleEncoder.WriteSup(sets, ms);
        byte[] sup = ms.ToArray();
        int at = 0, segmentCount = 0;
        long firstPts = -1;
        while (at < sup.Length)
        {
            Assert.Equal((byte)'P', sup[at]);
            Assert.Equal((byte)'G', sup[at + 1]);
            if (firstPts < 0) firstPts = BinaryPrimitives.ReadUInt32BigEndian(sup.AsSpan(at + 2));
            int size = BinaryPrimitives.ReadUInt16BigEndian(sup.AsSpan(at + 11));
            at += 13 + size;
            segmentCount++;
        }
        Assert.Equal(sup.Length, at);
        Assert.Equal(sets.Sum(s => s.Segments.Count), segmentCount);
        Assert.Equal(4 * 90_000, firstPts);
    }

    // ----- UDF 2.50 packaging -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(BdmvDiscTarget.BluRay)]
    [InlineData(BdmvDiscTarget.Avchd)]
    public async Task Image_IsUdf250_AndEveryFileReadsBackFromTheUdfSide(BdmvDiscTarget target)
    {
        var options = new BdmvAuthoringOptions { Target = target, VolumeLabel = "BD_TEST" };
        if (target == BdmvDiscTarget.Avchd)
        {
            options.VideoFormat = BdmvVideoFormat.Hd1080i;
            options.FrameRate = BdmvFrameRate.Fps25;
        }
        var (result, root) = await AuthorAsync(options);
        Assert.True(result.Success, result.ErrorMessage);

        string iso = Path.Combine(_dir, "disc.iso");
        await BdmvAuthoringService.BuildImageAsync(root, iso, options);
        var udf = new UdfTestWalker(File.ReadAllBytes(iso));

        Assert.Equal(0x0250, udf.Revision);
        Assert.Equal("NSR03", udf.NsrIdentifier);
        Assert.Equal(2, udf.PartitionMapCount);          // physical partition + metadata partition
        Assert.Equal(1, udf.FsdPartitionReference);      // the file set lives in the metadata partition
        Assert.True(udf.UsedExtendedFileEntries);
        Assert.NotNull(udf.MetadataFileData);
        Assert.Equal(udf.MetadataFileData, udf.MetadataMirrorData);

        var tree = ReadTree(root);
        Assert.Equal(tree.Keys.Select(k => "/" + k).OrderBy(k => k, StringComparer.Ordinal), udf.Files.Keys);
        foreach (var (path, data) in tree)
        {
            Assert.True(data.AsSpan().SequenceEqual(udf.Files["/" + path]), $"{path} differs on the UDF side");
        }
        Assert.Contains("/BDMV", udf.Directories);
        Assert.Contains("/CERTIFICATE/BACKUP", udf.Directories);

        // The files read back from the image still pass the structural check.
        var fromImage = BdmvStructuralValidator.Validate(udf.Files.ToDictionary(kv => kv.Key.TrimStart('/'), kv => kv.Value), avchd: target == BdmvDiscTarget.Avchd);
        Assert.True(fromImage.Errors.Count == 0, string.Join(Environment.NewLine, fromImage.Errors));
    }

    [Fact]
    public async Task Image_RefusesAFolderThatWasNotAuthored()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            BdmvAuthoringService.BuildImageAsync(Path.Combine(_dir, "empty"), Path.Combine(_dir, "x.iso"), new BdmvAuthoringOptions()));
    }

    // ----- the validator must be able to fail -------------------------------------------------------------------

    private async Task<Dictionary<string, byte[]>> GoodTreeAsync()
    {
        var (result, root) = await AuthorAsync(new BdmvAuthoringOptions(), rootName: "good");
        Assert.True(result.Success, result.ErrorMessage);
        var tree = ReadTree(root);
        Assert.Empty(BdmvStructuralValidator.Validate(tree, false).Errors);
        return tree;
    }

    private static Dictionary<string, byte[]> Damaged(Dictionary<string, byte[]> tree, string path, Action<byte[]> damage)
    {
        var copy = tree.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        damage(copy[path]);
        return copy;
    }

    [Fact]
    public async Task Validator_CatchesABrokenEntryPoint()
    {
        var tree = await GoodTreeAsync();
        var bad = Damaged(tree, "BDMV/CLIPINF/00000.clpi", b =>
        {
            int cpi = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));
            int start = cpi + 6 + (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(cpi + 6 + 10));
            b[start + 4 + 8 + 7] ^= 0x04;      // second coarse entry: flip a bit of its packet number
        });
        Assert.NotEmpty(BdmvStructuralValidator.Validate(bad, false).Errors);
    }

    [Fact]
    public async Task Validator_CatchesAnArrivalTimeThatRunsBackwards()
    {
        var tree = await GoodTreeAsync();
        var bad = Damaged(tree, "BDMV/STREAM/00000.m2ts", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(192 * 40), 0x3FFF_0000));
        Assert.Contains(BdmvStructuralValidator.Validate(bad, false).Errors, e => e.Contains("arrival time"));
    }

    [Fact]
    public async Task Validator_CatchesALostPacketThroughContinuityAndSize()
    {
        var tree = await GoodTreeAsync();
        var copy = tree.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        copy["BDMV/STREAM/00000.m2ts"] = tree["BDMV/STREAM/00000.m2ts"].AsSpan(0, tree["BDMV/STREAM/00000.m2ts"].Length - 192).ToArray();
        Assert.Contains(BdmvStructuralValidator.Validate(copy, false).Errors, e => e.Contains("6,144") || e.Contains("packet count"));

        var bad = Damaged(tree, "BDMV/STREAM/00000.m2ts", b => b[192 * 60 + 4 + 3] = (byte)((b[192 * 60 + 4 + 3] & 0xF0) | ((b[192 * 60 + 4 + 3] + 5) & 0x0F)));
        Assert.Contains(BdmvStructuralValidator.Validate(bad, false).Errors, e => e.Contains("continuity") || e.Contains("CRC") || e.Contains("PES"));
    }

    [Fact]
    public async Task Validator_CatchesAPlayItemThatEndsBeforeItStarts()
    {
        var tree = await GoodTreeAsync();
        var bad = Damaged(tree, "BDMV/PLAYLIST/00000.mpls", b =>
        {
            int item = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)) + 10;
            (b[item + 2 + 12 + 3], b[item + 2 + 16 + 3]) = (b[item + 2 + 16 + 3], b[item + 2 + 12 + 3]);
            b[item + 2 + 16] = 0; b[item + 2 + 12] = 0x7F;
        });
        Assert.Contains(BdmvStructuralValidator.Validate(bad, false).Errors, e => e.Contains("IN time"));
    }

    [Fact]
    public async Task Validator_CatchesAMissingBackupAndAWrongPlaylistNumber()
    {
        var tree = await GoodTreeAsync();
        var noBackup = tree.Where(kv => !kv.Key.StartsWith("BDMV/BACKUP/index", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(BdmvStructuralValidator.Validate(noBackup, false).Errors, e => e.Contains("BACKUP"));

        var bad = Damaged(tree, "BDMV/MovieObject.bdmv", b => b[50 + 4 + 7] = 9);   // PlayPL 9
        Assert.Contains(BdmvStructuralValidator.Validate(bad, false).Errors, e => e.Contains("playlist"));
    }

    // ----- helpers ----------------------------------------------------------------------------------------------

    private static List<List<BdmvStructuralValidator.ScannedSegment>> SplitDisplaySets(IReadOnlyList<BdmvStructuralValidator.ScannedSegment> segments)
    {
        var sets = new List<List<BdmvStructuralValidator.ScannedSegment>>();
        foreach (var s in segments)
        {
            if (s.Type == 0x16)
            {
                sets.Add(new List<BdmvStructuralValidator.ScannedSegment>());
            }
            sets[^1].Add(s);
        }
        return sets;
    }

    /// <summary>
    /// Decodes the PGS run-length code from its description: a non-zero byte is one pixel; 00 00 ends a line; 00 followed
    /// by 00LLLLLL / 01LLLLLL LLLLLLLL is a run of colour 0 (6 / 14 bits); 10LLLLLL CC / 11LLLLLL LLLLLLLL CC a run of colour CC.
    /// </summary>
    private static byte[] DecodePgsRle(ReadOnlySpan<byte> data, int width, int height)
    {
        var pixels = new byte[width * height];
        int i = 0, x = 0, y = 0;
        while (i < data.Length && y < height)
        {
            byte b = data[i++];
            if (b != 0)
            {
                pixels[y * width + x++] = b;
                continue;
            }
            byte f = data[i++];
            if (f == 0)
            {
                Assert.Equal(width, x);
                x = 0;
                y++;
                continue;
            }
            int run;
            byte colour = 0;
            switch (f >> 6)
            {
                case 0: run = f & 0x3F; break;
                case 1: run = ((f & 0x3F) << 8) | data[i++]; break;
                case 2: run = f & 0x3F; colour = data[i++]; break;
                default: run = ((f & 0x3F) << 8) | data[i++]; colour = data[i++]; break;
            }
            for (int k = 0; k < run; k++)
            {
                pixels[y * width + x++] = colour;
            }
        }
        Assert.Equal(height, y);
        Assert.Equal(data.Length, i);
        return pixels;
    }
}
