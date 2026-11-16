using System.Net;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Unit tests for CUETools Database (CTDB) TOCID / <c>toc</c> formatting, sample-window CRC-32 calculation,
/// GF(2) CRC-32 combination, XML parsing/XXE rejection, and HTTP lookup behavior.
/// </summary>
public sealed class CueToolsDatabaseTests
{
    private static byte[] GeneratePcm(int sectors, int mul, int add)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)((i * mul + add) & 0xFF);
        }
        return pcm;
    }

    private static uint ReferenceCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    [Fact]
    public void Calculate_RealCuTools12TrackDisc_MatchesReferenceTocIdAndToc()
    {
        int[] starts = [0, 16275, 48458, 65109, 81318, 108824, 125249, 148395, 168148, 183895, 232497, 247290];
        const int leadOut = 262524;

        var toc = new List<DiscTocTrack>(starts.Length);
        for (int i = 0; i < starts.Length; i++)
        {
            int next = i + 1 < starts.Length ? starts[i + 1] : leadOut;
            toc.Add(new DiscTocTrack(i + 1, starts[i], next - starts[i], IsAudio: true));
        }

        var ids = DiscIdCalculator.Calculate(toc);

        Assert.NotNull(ids);
        Assert.Equal("MTBsx7wnJY_jwpAmVGABgA9eaIM-", ids.CtdbTocId);
        Assert.Equal("0:16275:48458:65109:81318:108824:125249:148395:168148:183895:232497:247290:262524", ids.CtdbToc);
    }

    [Fact]
    public void Calculate_HtoaEnhancedCdMixedModeAndSingleTrack_ProduceExpectedTocIdAndToc()
    {
        int[] starts = [0, 16275, 48458, 65109, 81318, 108824, 125249, 148395, 168148, 183895, 232497, 247290];
        const int leadOut = 262524;

        // 1. HTOA: shift all 12 tracks by +450 sectors -> CtdbTocId stays identical
        var htoaToc = new List<DiscTocTrack>(starts.Length);
        for (int i = 0; i < starts.Length; i++)
        {
            int next = i + 1 < starts.Length ? starts[i + 1] : leadOut;
            htoaToc.Add(new DiscTocTrack(i + 1, starts[i] + 450, next - starts[i], IsAudio: true));
        }

        var htoaIds = DiscIdCalculator.Calculate(htoaToc);
        Assert.NotNull(htoaIds);
        Assert.Equal("MTBsx7wnJY_jwpAmVGABgA9eaIM-", htoaIds.CtdbTocId);

        // 2. Enhanced CD: Session 2 data track appended after 11,400-sector gap
        var enhancedToc = new List<DiscTocTrack>(starts.Length + 1);
        for (int i = 0; i < starts.Length; i++)
        {
            int next = i + 1 < starts.Length ? starts[i + 1] : leadOut;
            enhancedToc.Add(new DiscTocTrack(i + 1, starts[i], next - starts[i], IsAudio: true));
        }
        enhancedToc.Add(new DiscTocTrack(13, 273924, 20000, IsAudio: false));

        var enhancedIds = DiscIdCalculator.Calculate(enhancedToc);
        Assert.NotNull(enhancedIds);
        Assert.Equal("MTBsx7wnJY_jwpAmVGABgA9eaIM-", enhancedIds.CtdbTocId);
        Assert.EndsWith(":-273924:293924", enhancedIds.CtdbToc, StringComparison.Ordinal);

        // 3. Mixed-Mode CD: Track 1 data (0..10000), Track 2 audio (10150..20150), Track 3 audio (20150..35150)
        var mixedToc = new[]
        {
            new DiscTocTrack(1, 0, 10000, IsAudio: false),
            new DiscTocTrack(2, 10150, 10000, IsAudio: true),
            new DiscTocTrack(3, 20150, 15000, IsAudio: true)
        };
        var mixedIds = DiscIdCalculator.Calculate(mixedToc);
        Assert.NotNull(mixedIds);
        Assert.Equal("-0:10150:20150:35150", mixedIds.CtdbToc);
        Assert.Equal("RbpfJP6CSBGDCpq9MHzSVNHAppQ-", mixedIds.CtdbTocId);

        // 4. Single audio track (0..15000)
        var singleIds = DiscIdCalculator.Calculate(new[] { new DiscTocTrack(1, 0, 15000, IsAudio: true) });
        Assert.NotNull(singleIds);
        Assert.Equal("0:15000", singleIds.CtdbToc);
        Assert.Equal("3jw0IVQ8WaW.63.KRdk6YFsBvyo-", singleIds.CtdbTocId);
    }

    [Fact]
    public void Checksum_ThreeTrackDiscWithRemainder_MatchesReferenceAndCombine()
    {
        byte[] t1 = GeneratePcm(23, 7, 3);
        byte[] t2 = GeneratePcm(15, 11, 5);
        byte[] t3 = GeneratePcm(27, 13, 9);

        long s1 = 23L * CtdbChecksum.SamplesPerSector;
        long s2 = 15L * CtdbChecksum.SamplesPerSector;
        long s3 = 27L * CtdbChecksum.SamplesPerSector;
        long totalSamples = s1 + s2 + s3;

        var c1 = CtdbChecksum.ForTrack(0, s1, totalSamples);
        var c2 = CtdbChecksum.ForTrack(s1, s2, totalSamples);
        var c3 = CtdbChecksum.ForTrack(s1 + s2, s3, totalSamples);
        c1.Append(t1);
        c2.Append(t2);
        c3.Append(t3);

        Assert.Equal(0x422E8520u, c1.Value);
        Assert.Equal(0xF2D20CD0u, c2.Value);
        Assert.Equal(0x7A821DF7u, c3.Value);

        // Independent check against bit-by-bit CRC32 of the covered byte slices
        int strideBytes = CtdbChecksum.StrideSamples * 4;
        Assert.Equal(ReferenceCrc32(t1.AsSpan(strideBytes)), c1.Value);
        Assert.Equal(ReferenceCrc32(t2.AsSpan()), c2.Value);
        Assert.Equal(ReferenceCrc32(t3.AsSpan(0, 12 * OpticalConstants.RawSectorSize)), c3.Value);

        byte[] whole = [.. t1, .. t2, .. t3];
        var disc = CtdbChecksum.ForDisc(totalSamples);
        disc.Append(whole);
        Assert.Equal(0xB4EE5755u, disc.Value);

        // Out-of-order TrackInfos still combine to the exact disc CRC
        var trackInfos = new[]
        {
            new AudioTrackRipInfo(3, "t3.wav", 0, 0, 0, AudioTrackIndex: 2, UnreadableSectors: 0, CtdbTrackCrc: c3.Value),
            new AudioTrackRipInfo(1, "t1.wav", 0, 0, 0, AudioTrackIndex: 0, UnreadableSectors: 0, CtdbTrackCrc: c1.Value),
            new AudioTrackRipInfo(2, "t2.wav", 0, 0, 0, AudioTrackIndex: 1, UnreadableSectors: 0, CtdbTrackCrc: c2.Value)
        };
        uint combined = CtdbChecksum.CombineTrackCrcs(trackInfos, [0, s1, s1 + s2], totalSamples);
        Assert.Equal(0xB4EE5755u, combined);
    }

    [Fact]
    public void Checksum_TwoTrackExactMultipleOfTenAndSingleTrack_MatchReference()
    {
        byte[] t1 = GeneratePcm(24, 5, 1);
        byte[] t2 = GeneratePcm(26, 17, 2);
        long s1 = 24L * CtdbChecksum.SamplesPerSector;
        long s2 = 26L * CtdbChecksum.SamplesPerSector;
        long total = s1 + s2;

        var c1 = CtdbChecksum.ForTrack(0, s1, total);
        var c2 = CtdbChecksum.ForTrack(s1, s2, total);
        c1.Append(t1);
        c2.Append(t2);

        Assert.Equal(0x0C406894u, c1.Value);
        Assert.Equal(0xCDE83013u, c2.Value);

        byte[] whole = [.. t1, .. t2];
        var disc = CtdbChecksum.ForDisc(total);
        disc.Append(whole);
        Assert.Equal(0x9F0AE2AAu, disc.Value);
        Assert.Equal(0x9F0AE2AAu, CtdbChecksum.CombineTrackCrcs(
            [
                new AudioTrackRipInfo(1, "t1.wav", 0, 0, 0, 0, 0, CtdbTrackCrc: c1.Value),
                new AudioTrackRipInfo(2, "t2.wav", 0, 0, 0, 1, 0, CtdbTrackCrc: c2.Value)
            ],
            [0, s1],
            total));

        // 1 track (33 sectors; prefix 10, suffix 13, covers [10..20))
        byte[] singlePcm = GeneratePcm(33, 19, 7);
        long singleSamples = 33L * CtdbChecksum.SamplesPerSector;
        var single = CtdbChecksum.ForTrack(0, singleSamples, singleSamples);
        single.Append(singlePcm);
        Assert.Equal(0x1F587DEEu, single.Value);
    }

    [Fact]
    public void Checksum_ShortDiscAndUnalignedStreamingChunks_AreHandled()
    {
        // Disc shorter than 20 sectors (< 11,760 stereo samples) yields empty window and 0 CRC
        Assert.Equal((0L, 0L), CtdbChecksum.GetDiscSampleWindow(19L * CtdbChecksum.SamplesPerSector));
        var shortDisc = CtdbChecksum.ForDisc(19L * CtdbChecksum.SamplesPerSector);
        shortDisc.Append(GeneratePcm(19, 3, 1));
        Assert.Equal(0L, shortDisc.CoveredBytes);
        Assert.Equal(0u, shortDisc.Value);

        // Arbitrary unaligned chunks match single-span Append
        byte[] singlePcm = GeneratePcm(33, 19, 7);
        long singleSamples = 33L * CtdbChecksum.SamplesPerSector;
        var chunked = CtdbChecksum.ForTrack(0, singleSamples, singleSamples);
        int[] chunkSizes = [1, 3, 7, 13, 4096];
        int pos = 0;
        int idx = 0;
        while (pos < singlePcm.Length)
        {
            int take = Math.Min(chunkSizes[idx++ % chunkSizes.Length], singlePcm.Length - pos);
            chunked.Append(singlePcm.AsSpan(pos, take));
            pos += take;
        }
        Assert.Equal(0x1F587DEEu, chunked.Value);
    }

    [Fact]
    public void BuildLookupUri_ParseXml_AndMatching_HandleModernLegacy404AndDtd()
    {
        var ids = new DiscIdentifiers("mb", 0x12345678u, 1u, 2u, 2, "abc", "0:10000:20000");
        Uri uri = CueToolsDatabase.BuildLookupUri(ids);
        Assert.Equal("http://db.cuetools.net/lookup2.php?version=3&ctdb=1&metadata=none&fuzzy=0&toc=0%3A10000%3A20000", uri.AbsoluteUri);

        const string xml = """
            <ctdb npar="8">
              <entry id="101" crc32="9f0ae2aa" confidence="5" trackcrcs="0c406894 cde83013" hasparity="1" stride="5880" toc="0:10000:20000" parity="AAAA" syndrome="BBBB" />
              <entry id="102" crc32="11223344" confidence="2" />
            </ctdb>
            """;

        var entries = CueToolsDatabase.ParseXml(xml);
        Assert.Equal(2, entries.Count);
        Assert.Equal(101L, entries[0].Id);
        Assert.Equal(0x9F0AE2AAu, entries[0].Crc32);
        Assert.Equal(5, entries[0].Confidence);
        Assert.Equal(8, entries[0].Npar);
        Assert.Equal(5880, entries[0].RawStride);
        Assert.Equal("1", entries[0].HasParity);
        Assert.Equal("AAAA", entries[0].ParityBase64);
        Assert.Equal("BBBB", entries[0].SyndromeBase64);
        Assert.Equal("0:10000:20000", entries[0].Toc);
        Assert.Equal([0x0C406894u, 0xCDE83013u], entries[0].TrackCrcs);

        // Track match uses trackcrcs for entry 101 and falls back to disc CRC for legacy entry 102 when disc CRC is provided
        var track1 = CueToolsDatabase.MatchTrack(entries, 0, 0x0C406894u, 0x9F0AE2AAu);
        Assert.True(track1.IsAccurate);
        Assert.Equal(5, track1.Confidence);
        Assert.Equal(7, track1.TotalSubmissions);

        // Partial rip (discCtdbCrc == null) skips legacy entry without trackcrcs
        var partialTrack1 = CueToolsDatabase.MatchTrack(entries, 0, 0x0C406894u, discCtdbCrc: null);
        Assert.True(partialTrack1.IsAccurate);
        Assert.Equal(5, partialTrack1.Confidence);
        Assert.Equal(5, partialTrack1.TotalSubmissions);

        var discMatch = CueToolsDatabase.MatchDisc(entries, 0x9F0AE2AAu);
        Assert.True(discMatch.Matched);
        Assert.Equal(5, discMatch.Confidence);
        Assert.Equal(7, discMatch.TotalSubmissions);

        // DTD / XXE is prohibited
        const string xxeXml = """<?xml version="1.0"?><!DOCTYPE ctdb [<!ENTITY xxe "bad">]><ctdb/>""";
        Assert.Throws<InvalidDataException>(() => CueToolsDatabase.ParseXml(xxeXml));
    }

    [Fact]
    public async Task FetchAsync_ReturnsEmptyOn404_AndParsesXmlOn200()
    {
        var ids = new DiscIdentifiers("mb", 0x12345678u, 1u, 2u, 1, "abc", "0:15000");

        using var client404 = new HttpClient(new StubHandler(HttpStatusCode.NotFound, string.Empty));
        var empty = await CueToolsDatabase.FetchAsync(client404, ids);
        Assert.Empty(empty);

        const string body = """<ctdb><entry id="1" crc32="1f587dee" confidence="3" trackcrcs="1f587dee" /></ctdb>""";
        using var client200 = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var entries = await CueToolsDatabase.FetchAsync(client200, ids);
        Assert.Single(entries);
        Assert.Equal(0x1F587DEEu, entries[0].Crc32);
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/xml")
            });
        }
    }
}
