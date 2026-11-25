using System.Buffers.Binary;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Subtitles;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Verifies DVD-Video multi-audio (up to 8 AC-3 streams, 0x80..0x87) and multi-language subtitle
/// (up to 8 SPU streams, 0x20..0x27) authoring across <see cref="DvdVideoProject"/>,
/// <see cref="Mpeg2Muxer"/>, <see cref="DvdNavPackWriter"/>, <see cref="DvdIfoGenerator"/>,
/// <see cref="FfmpegDvdTranscodeBuilder"/>, and <see cref="DvdVideoAuthoringService"/>.
/// </summary>
public sealed class DvdMultiTrackAuthoringTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_MultiTrack_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

    [Theory]
    [InlineData("en", "eng", "en")]
    [InlineData("eng", "eng", "en")]
    [InlineData("fre", "fra", "fr")]
    [InlineData("fra", "fra", "fr")]
    [InlineData("ger", "deu", "de")]
    [InlineData("deu", "deu", "de")]
    [InlineData("spa", "spa", "es")]
    [InlineData("jpn", "jpn", "ja")]
    public void LanguageCodes_NormalizeIso639Part2_AndMapToIfoPart1(string input, string expectedPart2, string expectedPart1)
    {
        Assert.Equal(expectedPart2, DvdLanguageCodes.NormalizeIso639Part2(input));
        Assert.Equal(expectedPart1, DvdLanguageCodes.ToIso639Part1(input));
    }

    [Fact]
    public void LanguageCodes_ParseTrackSpec_HandlesWindowsDrivePathsAndRejectsBarePaths()
    {
        Assert.True(DvdLanguageCodes.TryParseTrackSpec(@"eng:C:\media\main.ac3", out string lang, out string path));
        Assert.Equal("eng", lang);
        Assert.Equal(@"C:\media\main.ac3", path);

        Assert.True(DvdLanguageCodes.TryParseTrackSpec("es:subs_es.srt", out lang, out path));
        Assert.Equal("spa", lang);
        Assert.Equal("subs_es.srt", path);

        Assert.False(DvdLanguageCodes.TryParseTrackSpec(@"C:\media\main.ac3", out _, out _));
        Assert.False(DvdLanguageCodes.TryParseTrackSpec("english:main.ac3", out _, out _));
        Assert.False(DvdLanguageCodes.TryParseTrackSpec("eng:", out _, out _));
    }

    [Fact]
    public void Ifo_EncodesUpToEightAudioAndEightSubtitleStreams_WithLanguageTagsAndPgcBitmasks()
    {
        var vobus = new[] { new DvdVobu(0, 90_000, 135_000), new DvdVobu(16, 135_000, 180_000) };
        DvdAudioStreamInfo[] audios =
        [
            new("eng", 6, DvdAudioContentKind.Normal),
            new("eng", 2, DvdAudioContentKind.DirectorsComments),
            new("spa", 6, DvdAudioContentKind.Normal),
            new("fra", 2, DvdAudioContentKind.Normal),
            new("deu", 2, DvdAudioContentKind.Normal),
            new("jpn", 2, DvdAudioContentKind.Normal),
            new("ita", 2, DvdAudioContentKind.Normal),
            new("por", 2, DvdAudioContentKind.VisuallyImpaired)
        ];
        DvdSubtitleStreamInfo[] subtitles =
        [
            new("eng", 0x01),
            new("spa", 0x01),
            new("fra", 0x01),
            new("deu", 0x01),
            new("jpn", 0x01),
            new("ita", 0x01),
            new("por", 0x01),
            new("rus", 0x01)
        ];

        byte[] vts = DvdIfoGenerator.BuildVtsIfo(
            1,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            titleVobTotalSectors: 32,
            vobus: vobus,
            audioStreams: audios,
            subtitleStreams: subtitles);

        byte[] vmg = DvdIfoGenerator.BuildVmgIfo(
            1,
            DvdVideoStandard.Ntsc,
            DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 0,
            titleAudioStreams: [audios],
            titleSubtitleStreams: [subtitles]);

        // VTSI_MAT audio attributes (0x203 = count, 0x204 + 8*i)
        Assert.Equal(8, vts[0x203]);
        Assert.Equal(new byte[] { 0x04, 0x05, (byte)'e', (byte)'n', 0x00, 0x01 }, vts.AsSpan(0x204, 6).ToArray()); // 5.1 (6-1=5), eng, Normal
        Assert.Equal(new byte[] { 0x04, 0x01, (byte)'e', (byte)'n', 0x00, 0x03 }, vts.AsSpan(0x204 + 8, 6).ToArray()); // 2ch, eng, Director's comments
        Assert.Equal(new byte[] { 0x04, 0x05, (byte)'e', (byte)'s', 0x00, 0x01 }, vts.AsSpan(0x204 + 16, 6).ToArray()); // 5.1, spa
        Assert.Equal(new byte[] { 0x04, 0x01, (byte)'p', (byte)'t', 0x00, 0x02 }, vts.AsSpan(0x204 + 56, 6).ToArray()); // 2ch, por, Visually impaired

        // VTSI_MAT subpicture attributes (0x255 = count, 0x256 + 6*i)
        Assert.Equal(8, vts[0x255]);
        string[] expectedSub2 = ["en", "es", "fr", "de", "ja", "it", "pt", "ru"];
        for (int s = 0; s < 8; s++)
        {
            int off = 0x256 + 6 * s;
            Assert.Equal(0x01, vts[off]);
            Assert.Equal((byte)expectedSub2[s][0], vts[off + 2]);
            Assert.Equal((byte)expectedSub2[s][1], vts[off + 3]);
            Assert.Equal(0x01, vts[off + 5]);
        }

        // VTS_PGC audio_control[8] (0x0C + 2*i) and subp_control[32] (0x1C + 4*i)
        int pgcit = (int)U32(vts, 0xCC) * 2048;
        int pgc = pgcit + (int)U32(vts, pgcit + 12);
        for (int a = 0; a < 8; a++)
        {
            Assert.Equal((ushort)(0x8000 | (a << 8)), U16(vts, pgc + 0x0C + 2 * a));
        }
        for (int s = 0; s < 8; s++)
        {
            uint expected = 0x80000000u | ((uint)s << 24) | ((uint)s << 16) | ((uint)s << 8) | (uint)s;
            Assert.Equal(expected, U32(vts, pgc + 0x1C + 4 * s));
        }

        // VIDEO_TS.IFO VTS_ATRT mirrors all 8 audio + 8 subpicture attributes
        int atrt = (int)U32(vmg, 0xD0) * 2048;
        int entry = atrt + (int)U32(vmg, atrt + 8);
        Assert.Equal(8, vmg[entry + 267]);
        Assert.Equal((byte)'f', vmg[entry + 268 + 8 * 3 + 2]); // fra audio 4
        Assert.Equal((byte)'r', vmg[entry + 268 + 8 * 3 + 3]);
        Assert.Equal(8, vmg[entry + 349]);
        Assert.Equal((byte)'r', vmg[entry + 350 + 6 * 7 + 2]); // rus sub 8
        Assert.Equal((byte)'u', vmg[entry + 350 + 6 * 7 + 3]);
    }

    [Fact]
    public void Mpeg2Muxer_MuxesEightAc3StreamsAndEightSpuStreams_WithDistinctSubstreamIdsAndSynciOffsets()
    {
        var audioStreams = Enumerable.Range(0, 8)
            .Select(i => new Mpeg2AudioMuxStream(i, i == 0 ? "eng" : "spa", Channels: i == 0 ? 6 : 2))
            .ToList();

        byte[] spuPixels = new byte[8 * 2];
        var subtitleStreams = Enumerable.Range(0, 8)
            .Select(i => new Mpeg2SubtitleMuxStream(
                i,
                "eng",
                [(90_000L + i * 1000L, DvdSubtitleWriter.EncodeSpu(spuPixels, 8, 2, 0, 0, TimeSpan.FromSeconds(1)))]))
            .ToList();

        byte[] vob = Mpeg2Muxer.MuxProgramStream(
            audioStreams,
            subtitleStreams,
            new Mpeg2MuxOptions(DvdVideoStandard.Ntsc, DvdAspectRatio.Widescreen16x9, VideoSectorCount: 16));

        var inspected = Mpeg2Muxer.InspectProgramStream(vob);
        Assert.Equal(new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87 }, inspected.AudioSubstreamIds);
        Assert.Equal(new byte[] { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27 }, inspected.SubtitleSubstreamIds);
        Assert.True(inspected.NavPackCount >= 1);

        // Verify DSI_SYNCI a_synca[0..7] and sp_synca[0..7] in sector 0 NAV pack
        int synci = DvdNavPackWriter.DsiOffset + 32 + 148 + 54 + 168;
        for (int a = 0; a < 8; a++)
        {
            ushort audioOffset = U16(vob, synci + 2 * a);
            Assert.True(audioOffset is > 0 and < 0x3FFF, $"a_synca[{a}] was {audioOffset}");
        }
        for (int s = 0; s < 8; s++)
        {
            uint spuOffset = U32(vob, synci + 16 + 4 * s);
            Assert.True((spuOffset & 0x80000000u) != 0, $"sp_synca[{s}] missing present bit");
            Assert.True((spuOffset & 0x7FFFFFFFu) > 0, $"sp_synca[{s}] sector offset was 0");
        }
    }

    [Fact]
    public void FfmpegDvdTranscodeBuilder_MapsMultiAudioAndMultiSubtitleWithIso639Part2Metadata()
    {
        var args = FfmpegDvdTranscodeBuilder.ForNtsc("movie.mkv", "out.vob")
            .WithAudioTracks(
            [
                new DvdMuxAudioInput(null, 0, "eng", Channels: 6, BitrateKbps: 448),
                new DvdMuxAudioInput("commentary.ac3", 0, "spa", Channels: 2, BitrateKbps: 192)
            ])
            .WithSubtitleTracks(
            [
                new DvdMuxSubtitleInput("en.idx", "eng"),
                new DvdMuxSubtitleInput("fr.idx", "fre")
            ])
            .BuildArgumentList();

        string joined = string.Join(" ", args);
        Assert.Contains("-i movie.mkv -i commentary.ac3 -i en.idx -i fr.idx", joined);
        Assert.Contains("-map 0:v:0 -map 0:a:0 -map 1:a:0 -map 2:s:0 -map 3:s:0", joined);
        Assert.Contains("-c:a:0 ac3 -b:a:0 448k -ar:a:0 48000 -ac:a:0 6 -metadata:s:a:0 language=eng", joined);
        Assert.Contains("-c:a:1 ac3 -b:a:1 192k -ar:a:1 48000 -ac:a:1 2 -metadata:s:a:1 language=spa", joined);
        Assert.Contains("-c:s:0 copy -metadata:s:s:0 language=eng -c:s:1 copy -metadata:s:s:1 language=fra", joined);
    }

    [Fact]
    public async Task AuthoringService_CombinesExternalSrtAndWhisperSubtitles_AndEightAudioStreams_EndToEnd()
    {
        Directory.CreateDirectory(_tempDir);
        string movie = Path.Combine(_tempDir, "Feature.mp4");
        File.WriteAllBytes(movie, new byte[64 * 2048]);

        string[] srtLanguages = ["spa", "fra", "deu", "ita", "jpn", "por", "rus"];
        var externalSrts = new List<string>();
        for (int i = 0; i < srtLanguages.Length; i++)
        {
            string srt = Path.Combine(_tempDir, $"sub_{srtLanguages[i]}.srt");
            File.WriteAllText(srt, $"1\n00:00:01,000 --> 00:00:02,000\nLine {srtLanguages[i]}\n");
            externalSrts.Add(srt);
        }

        string whisperSrt = Path.Combine(_tempDir, "whisper_eng.srt");
        File.WriteAllText(whisperSrt, "1\n00:00:01,500 --> 00:00:02,500\nWhisper English\n");

        var project = new DvdVideoProject(new DvdAuthoringOptions
        {
            UseFfmpegWhenAvailable = false,
            AutoCreatePlayMenu = false,
            GenerateSubtitles = true,
            SubtitleLanguage = "eng"
        });
        var title = project.AddTitle(movie, "Multi-Track Feature");

        string[] audioLangs = ["eng", "eng", "spa", "fra", "deu", "ita", "jpn", "por"];
        for (int a = 0; a < 8; a++)
        {
            title.AddAudioTrack(new DvdAudioTrackSpec(
                SourcePath: null,
                Language: audioLangs[a],
                Channels: a == 0 ? 6 : 2,
                ContentKind: a == 1 ? DvdAudioContentKind.DirectorsComments : DvdAudioContentKind.Normal));
        }

        for (int s = 0; s < srtLanguages.Length; s++)
        {
            title.AddSubtitleTrack(new DvdSubtitleTrackSpec(externalSrts[s], srtLanguages[s]));
        }
        // 8th subtitle stream generated by speech recognition
        title.AddSubtitleTrack(new DvdSubtitleTrackSpec(null, "eng", GenerateWithWhisper: true));

        Assert.Throws<InvalidOperationException>(() => title.AddAudioTrack(new DvdAudioTrackSpec()));
        Assert.Throws<InvalidOperationException>(() => title.AddSubtitleTrack(new DvdSubtitleTrackSpec(externalSrts[0], "eng")));

        var service = new DvdVideoAuthoringService
        {
            SubtitleGenerator = (_, _, _) => Task.FromResult(
                new SubtitleGenerationResult(true, whisperSrt, 1, TimeSpan.FromSeconds(1), null, false, "processor", null))
        };

        string outDir = Path.Combine(_tempDir, "DiscOut");
        var result = await service.AuthorAsync(project, outDir);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ValidationResult?.IsValid);

        byte[] vob = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VTS_01_1.VOB"));
        var inspection = Mpeg2Muxer.InspectProgramStream(vob);
        Assert.Equal(new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87 }, inspection.AudioSubstreamIds);
        Assert.Equal(new byte[] { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27 }, inspection.SubtitleSubstreamIds);

        byte[] vtsIfo = File.ReadAllBytes(Path.Combine(result.VideoTsDirectory, "VTS_01_0.IFO"));
        Assert.Equal(8, vtsIfo[0x203]);
        Assert.Equal(8, vtsIfo[0x255]);
        Assert.Equal(0x05, vtsIfo[0x204 + 1]); // stream 0: 5.1 (6-1=5)
        Assert.Equal(0x03, vtsIfo[0x204 + 8 + 5]); // stream 1: Director's comments
        Assert.Equal((byte)'e', vtsIfo[0x256 + 6 * 7 + 2]); // 8th subtitle: Whisper English
        Assert.Equal((byte)'n', vtsIfo[0x256 + 6 * 7 + 3]);
    }
}
