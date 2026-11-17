using System.Net;
using System.Net.Http.Headers;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Tests for repairing rips from CUETools Database (CTDB) parity: the GF(2^16) field, the syndrome definition
/// (checked against CUETools' own encoder and parity-to-syndrome conversion), the Reed-Solomon decoder,
/// re-alignment for read offsets, the parity download, and end-to-end repair confirmed by AccurateRip.
/// </summary>
public sealed class CtdbRepairTests : IDisposable
{
    private const int Stride = CtdbParityLayout.Stride;
    private const int SectorBytes = OpticalConstants.RawSectorSize;
    private const string ParityUrl = "http://p.cuetools.net/424242";
    private const string Toc = "0:100:220:307";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ctdb-repair-" + Guid.NewGuid().ToString("N"));

    public CtdbRepairTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- Field and syndrome definition against CUETools' published constants and algorithms -------------

    [Fact]
    public void Generator_polynomial_matches_the_constants_hard_coded_in_CUETools()
    {
        // CUETools.CDRepair/CDRepair.cs ProcessStride16 hard-codes log(encodeGx[i]) for npar = 16.
        int[] expectedLogs =
        {
            0x59f1, 0x608f, 0x918b, 0x4487, 0xa151, 0xc074, 0x4178, 0x4730,
            0x4187, 0xc092, 0xa17e, 0x44c3, 0x91d6, 0x60e9, 0x5a5a, 0x0078
        };

        ushort[] gx = UpstreamMakeEncodeGx(16);
        Assert.Equal(expectedLogs, gx.Select(c => CtdbGalois16.Log(c)).ToArray());
    }

    [Fact]
    public void Field_is_generated_by_alpha_with_order_65535()
    {
        var seen = new bool[65536];
        for (int i = 0; i < CtdbGalois16.Order; i++)
        {
            ushort v = CtdbGalois16.Pow(i);
            Assert.False(seen[v]);
            seen[v] = true;
        }

        Assert.Equal(1, CtdbGalois16.Pow(CtdbGalois16.Order));
        Assert.Equal(CtdbGalois16.Pow(17), CtdbGalois16.Mul(CtdbGalois16.Pow(10), CtdbGalois16.Pow(7)));
        Assert.Equal(CtdbGalois16.Pow(3), CtdbGalois16.Div(CtdbGalois16.Pow(10), CtdbGalois16.Pow(7)));
    }

    [Fact]
    public void Syndromes_equal_CUETools_parity_converted_to_syndromes()
    {
        // CUETools computes systematic RS parity per column (RsEncode16) and stores
        // ParityToSyndrome.Parity2Syndrome(parity). Our accumulator computes syndromes directly; both must agree.
        const int rows = 7;
        int totalSamples = (rows + 2) * CtdbChecksum.StrideSamples + 300;
        byte[] pcm = RandomPcm(totalSamples * 4, seed: 11);
        var acc = new CtdbSyndromeAccumulator(new CtdbParityLayout(totalSamples));
        acc.Append(pcm);
        ushort[] ours = acc.GetShiftedSyndromes(0, 16);

        ushort[] gx = UpstreamMakeEncodeGx(16);
        foreach (int column in new[] { 0, 1, 777, Stride - 1 })
        {
            var data = new ushort[rows];
            for (int k = 0; k < rows; k++)
            {
                data[k] = Symbol(pcm, Stride + column + (long)k * Stride);
            }

            ushort[] parity = UpstreamRsEncode16(data, gx);
            for (int x = 0; x < 16; x++)
            {
                ushort expected = 0;
                for (int x1 = 0; x1 < 16; x1++)
                {
                    if (parity[x1] != 0)
                    {
                        expected ^= CtdbGalois16.Pow(CtdbGalois16.Log(parity[x1]) - (long)(1 + x1) * x);
                    }
                }

                Assert.Equal(expected, ours[column * 16 + x]);
            }
        }
    }

    [Fact]
    public void Inline_syndrome_of_a_real_CTDB_entry_decodes_little_endian()
    {
        // From a db.cuetools.net lookup2.php response (entry 2080034, npar 8); its parity file's first value of each
        // syndrome block was checked to equal these values.
        var entry = new CtdbEntry(2080034, 0x43965b0f, 3, Array.Empty<uint>(), Npar: 8, RawStride: 5880,
            HasParity: "http://p.cuetools.net/2080034", SyndromeBase64: "BFxUV9eRGaHRNst7/M4rzA==");

        ushort[]? syn = CueToolsDatabase.DecodeEntrySyndrome(entry);

        Assert.Equal(new ushort[] { 0x5c04, 0x5754, 0x91d7, 0xa119, 0x36d1, 0x7bcb, 0xcefc, 0xcc2b }, syn);
        Assert.Equal(new Uri("http://p.cuetools.net/2080034"), CueToolsDatabase.ResolveParityUri(entry));
    }

    [Theory]
    [InlineData("http://evil.example/parity")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://cuetools.net.evil.example/1")]
    public void Parity_address_outside_cuetools_is_refused(string address)
    {
        var entry = new CtdbEntry(1, 0, 1, Array.Empty<uint>(), HasParity: address, SyndromeBase64: "AAAAAA==");
        Assert.Null(CueToolsDatabase.ResolveParityUri(entry));
    }

    // ---- Decoder ----------------------------------------------------------------------------------------

    /// <summary>Rows per column on a 74-minute disc (74 × 60 × 44,100 × 2 / 11,760 − 2).</summary>
    private const int FullDiscRows = 33298;

    [Theory]
    [InlineData(16, 1, 640)]
    [InlineData(16, 5, 640)]
    [InlineData(16, 8, 640)]
    [InlineData(8, 4, 640)]
    [InlineData(16, 1, FullDiscRows)]
    [InlineData(16, 2, FullDiscRows)]
    [InlineData(16, 8, FullDiscRows)]
    [InlineData(8, 3, FullDiscRows)]
    public void Decoder_finds_every_error_up_to_half_the_syndromes(int npar, int errors, int rowCount)
    {
        var rng = new Random(npar * 100 + errors + rowCount);
        for (int trial = 0; trial < 20; trial++)
        {
            var positions = Enumerable.Range(0, rowCount).OrderBy(_ => rng.Next()).Take(errors).ToArray();
            var magnitudes = positions.Select(_ => (ushort)rng.Next(1, 65536)).ToArray();
            ushort[] syn = ErrorSyndromes(positions, magnitudes, rowCount, npar);

            var rows = new int[npar / 2];
            var values = new ushort[npar / 2];
            Assert.True(CtdbReedSolomon.TryDecode(syn, rowCount, rows, values, out int count));
            Assert.Equal(errors, count);
            var found = rows.Take(count).Zip(values.Take(count)).OrderBy(p => p.First).ToArray();
            var expected = positions.Zip(magnitudes).OrderBy(p => p.First).ToArray();
            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public void Decoder_refuses_damage_beyond_half_the_syndromes()
    {
        var rng = new Random(5);
        const int rowCount = 640;
        int refused = 0;
        for (int trial = 0; trial < 50; trial++)
        {
            var positions = Enumerable.Range(0, rowCount).OrderBy(_ => rng.Next()).Take(9).ToArray();
            var magnitudes = positions.Select(_ => (ushort)rng.Next(1, 65536)).ToArray();
            ushort[] syn = ErrorSyndromes(positions, magnitudes, rowCount, 16);
            if (!CtdbReedSolomon.TryDecode(syn, rowCount, new int[8], new ushort[8], out _))
            {
                refused++;
            }
        }

        // A wrong locator with all roots inside the column is possible but rare; the repair also checks the CRC.
        Assert.Equal(50, refused);
    }

    [Fact]
    public void Alignment_limit_rejects_random_syndromes_on_a_full_length_disc()
    {
        // On a full CD about 1 random 8-syndrome vector in 350 fits 4 "errors" inside the column; over ~11,760 trial
        // alignments that is dozens of false matches. So the alignment search accepts at most 3 (npar/2 - 1), as
        // CUETools does, and random vectors must not pass that limit.
        var rng = new Random(9);
        var syn = new ushort[8];
        int fitsHalf = 0;
        for (int trial = 0; trial < 3000; trial++)
        {
            for (int i = 0; i < 8; i++)
            {
                syn[i] = (ushort)rng.Next(1, 65536);
            }

            Assert.False(CtdbReedSolomon.TryDecode(syn, FullDiscRows, new int[4], new ushort[4], out _, maxErrors: 3));
            if (CtdbReedSolomon.TryDecode(syn, FullDiscRows, new int[4], new ushort[4], out _))
            {
                fitsHalf++;
            }
        }

        Assert.True(fitsHalf > 0, "expected some random vectors to fit npar/2 errors, which is why the limit exists");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(1176)]
    [InlineData(-1176)]
    [InlineData(Stride - 2)]
    [InlineData(-(Stride - 2))]
    public void Shifted_syndromes_equal_syndromes_computed_directly_at_that_alignment(int shift)
    {
        const int rows = 5;
        int totalSamples = (rows + 2) * CtdbChecksum.StrideSamples + 1234;
        byte[] pcm = RandomPcm(totalSamples * 4, seed: 3);
        var acc = new CtdbSyndromeAccumulator(new CtdbParityLayout(totalSamples), 8);
        acc.Append(pcm.AsSpan(0, 1001));
        acc.Append(pcm.AsSpan(1001)); // odd split inside a symbol
        ushort[] shifted = acc.GetShiftedSyndromes(shift, 8);

        foreach (int column in new[] { 0, 5, 6000, Stride - 1 })
        {
            for (int i = 0; i < 8; i++)
            {
                ushort direct = 0;
                for (int k = 0; k < rows; k++)
                {
                    direct = (ushort)(CtdbGalois16.Mul(direct, CtdbGalois16.Pow(i)) ^ Symbol(pcm, Stride + shift + column + (long)k * Stride));
                }

                Assert.Equal(direct, shifted[column * 8 + i]);
            }
        }
    }

    // ---- Parity download --------------------------------------------------------------------------------

    [Fact]
    public async Task Parity_download_requests_only_the_needed_syndromes_and_checks_them_against_the_entry()
    {
        var disc = BuildDisc(seed: 1);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16, entryNpar: 8);
        var handler = new ParityHandler(file);
        using var http = new HttpClient(handler);

        ushort[] syn = await CueToolsDatabase.FetchParitySyndromesAsync(http, entry, 8);

        Assert.Equal(new RangeItemHeaderValue(0, 8 * Stride * 2 - 1).ToString(), handler.LastRange?.Ranges.Single().ToString());
        Assert.Equal(Stride * 8, syn.Length);
        var acc = Accumulate(disc.Clean, 8);
        Assert.Equal(acc.GetShiftedSyndromes(0, 8), syn);

        var wrongEntry = entry with { SyndromeBase64 = Convert.ToBase64String(new byte[16]) };
        await Assert.ThrowsAsync<InvalidDataException>(() => CueToolsDatabase.FetchParitySyndromesAsync(http, wrongEntry, 8));
    }

    // ---- End to end ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Rip_with_injected_errors_is_repaired_to_an_AccurateRip_match()
    {
        var disc = BuildDisc(seed: 2);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16);
        byte[] damaged = (byte[])disc.Clean.Clone();
        ZeroSectors(damaged, firstSector: 130, count: 3);        // three unreadable sectors in track 2
        damaged[SectorBytes * 250 + 17] ^= 0x40;                 // one flipped bit in track 3
        var rip = WriteRip(damaged, disc.TrackSectors);
        int arCalls = 0;
        using var http = new HttpClient(new ParityHandler(file));

        var result = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc, _ =>
        {
            arCalls++;
            return Task.FromResult(CleanPressings(disc));
        });

        Assert.Equal(CtdbRepairStatus.Repaired, result.Status);
        Assert.Equal(1, arCalls);
        Assert.True(result.AccurateRipContacted);
        Assert.Equal(new[] { 2, 3 }, result.CorrectedTracks.Select(t => t.TrackNumber).ToArray());
        Assert.All(result.CorrectedTracks, t => Assert.True(t.AccurateRip!.IsAccurate));
        Assert.Equal(disc.Clean, ReadRip(rip));
        Assert.Equal(entry.Crc32, result.UpdatedRip!.CtdbDiscCrc);
        Assert.False(Directory.Exists(Path.Combine(_dir, CtdbRepairService.CorrectedFolderName)));
        var cleanAr = CleanPressings(disc)[0].Tracks;
        foreach (var info in result.UpdatedRip.TrackInfos)
        {
            Assert.Equal(cleanAr[info.AudioTrackIndex].Crc, info.AccurateRipV2);
        }
    }

    [Fact]
    public async Task Damage_above_tolerance_is_reported_and_the_rip_is_left_unchanged()
    {
        var disc = BuildDisc(seed: 4);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16);
        byte[] damaged = (byte[])disc.Clean.Clone();
        ZeroSectors(damaged, firstSector: 20, count: 100);       // 10 rows: 10 wrong samples in every column
        var rip = WriteRip(damaged, disc.TrackSectors);
        using var http = new HttpClient(new ParityHandler(file));

        var result = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc,
            _ => Task.FromResult(CleanPressings(disc)));

        Assert.Equal(CtdbRepairStatus.TooMuchDamage, result.Status);
        Assert.NotEqual("repaired", result.StatusCode);
        Assert.Contains("not repaired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(damaged, ReadRip(rip));
        Assert.Null(result.UpdatedRip);
        Assert.False(Directory.Exists(Path.Combine(_dir, CtdbRepairService.CorrectedFolderName)));
    }

    [Fact]
    public async Task Without_AccurateRip_data_the_fix_is_saved_aside_and_not_called_repaired()
    {
        var disc = BuildDisc(seed: 5);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16);
        byte[] damaged = (byte[])disc.Clean.Clone();
        ZeroSectors(damaged, firstSector: 40, count: 2);
        var rip = WriteRip(damaged, disc.TrackSectors);
        using var http = new HttpClient(new ParityHandler(file));

        var result = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc,
            _ => Task.FromResult<IReadOnlyList<AccurateRipPressing>>(Array.Empty<AccurateRipPressing>()));

        Assert.Equal(CtdbRepairStatus.CorrectedNotConfirmed, result.Status);
        Assert.Equal(damaged, ReadRip(rip));
        var corrected = Assert.Single(result.CorrectedTracks);
        Assert.Equal(Path.Combine(_dir, CtdbRepairService.CorrectedFolderName, Path.GetFileName(rip.TrackInfos[0].FilePath)), corrected.CorrectedPath);
        byte[] fixedTrack = WavFileCodec.DecodeToRedBookPcm(corrected.CorrectedPath);
        Assert.Equal(disc.Clean.AsSpan(0, disc.TrackSectors[0] * SectorBytes).ToArray(), fixedTrack);
    }

    [Fact]
    public async Task When_AccurateRip_disagrees_with_the_fix_nothing_is_changed()
    {
        var disc = BuildDisc(seed: 6);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16);
        byte[] damaged = (byte[])disc.Clean.Clone();
        ZeroSectors(damaged, firstSector: 150, count: 1);
        var rip = WriteRip(damaged, disc.TrackSectors);
        using var http = new HttpClient(new ParityHandler(file));
        var otherPressing = new AccurateRipPressing(1, 2, 3, new[]
        {
            new AccurateRipTrackEntry(9, 0x11111111, 0), new AccurateRipTrackEntry(9, 0x22222222, 0), new AccurateRipTrackEntry(9, 0x33333333, 0)
        });

        var result = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc,
            _ => Task.FromResult<IReadOnlyList<AccurateRipPressing>>(new[] { otherPressing }));

        Assert.Equal(CtdbRepairStatus.AccurateRipMismatch, result.Status);
        Assert.Equal(damaged, ReadRip(rip));
        Assert.False(Directory.Exists(Path.Combine(_dir, CtdbRepairService.CorrectedFolderName)));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-667)]
    public async Task Rip_read_at_a_different_offset_than_the_entry_is_still_repaired(int entryStartsLaterBy)
    {
        var disc = BuildDisc(seed: 7);
        byte[] entryAudio = Shift(disc.Clean, disc.Source, entryStartsLaterBy);
        var (entry, file) = BuildEntry(entryAudio, npar: 8, entryNpar: 8);
        byte[] damaged = (byte[])disc.Clean.Clone();
        ZeroSectors(damaged, firstSector: 60, count: 1);
        var rip = WriteRip(damaged, disc.TrackSectors);
        using var http = new HttpClient(new ParityHandler(file));

        var result = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc,
            _ => Task.FromResult(CleanPressings(disc)));

        Assert.Equal(CtdbRepairStatus.Repaired, result.Status);
        Assert.Equal(-entryStartsLaterBy, result.OffsetSamples);
        Assert.Equal(disc.Clean, ReadRip(rip));
    }

    [Fact]
    public async Task Matching_rip_and_partial_rip_need_no_download()
    {
        var disc = BuildDisc(seed: 8);
        var (entry, file) = BuildEntry(disc.Clean, npar: 16);
        var rip = WriteRip(disc.Clean, disc.TrackSectors);
        var handler = new ParityHandler(file);
        using var http = new HttpClient(handler);

        var same = await CtdbRepairService.RepairAsync(http, rip, new[] { entry }, Toc, null);
        var partial = await CtdbRepairService.RepairAsync(http, AudioCdRipResult.Succeeded(
            rip.RippedFilePaths, 0, TimeSpan.Zero, trackInfos: rip.TrackInfos, ctdbDiscCrc: null), new[] { entry }, Toc, null);

        Assert.Equal(CtdbRepairStatus.NotNeeded, same.Status);
        Assert.Equal(CtdbRepairStatus.NotPossible, partial.Status);
        Assert.Equal(0, handler.Requests);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private sealed record Disc(byte[] Clean, byte[] Source, int SourceStartBytes, int[] TrackSectors);

    /// <summary>Three tracks (100, 120, 87 sectors = 30 rows + 7 sectors) of deterministic noise.</summary>
    private static Disc BuildDisc(int seed)
    {
        int[] sectors = { 100, 120, 87 };
        int bytes = sectors.Sum() * SectorBytes;
        const int margin = 8 * SectorBytes; // room to shift the "database" audio by up to 8 sectors either way
        byte[] source = RandomPcm(bytes + 2 * margin, seed);
        return new Disc(source.AsSpan(margin, bytes).ToArray(), source, margin, sectors);
    }

    private static byte[] Shift(byte[] clean, byte[] source, int samples)
    {
        int start = (source.Length - clean.Length) / 2 + samples * 4;
        return source.AsSpan(start, clean.Length).ToArray();
    }

    private static byte[] RandomPcm(int bytes, int seed)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static ushort Symbol(byte[] pcm, long index) => (ushort)(pcm[2 * index] | (pcm[2 * index + 1] << 8));

    private static void ZeroSectors(byte[] pcm, int firstSector, int count) =>
        Array.Clear(pcm, firstSector * SectorBytes, count * SectorBytes);

    private static CtdbSyndromeAccumulator Accumulate(byte[] pcm, int npar)
    {
        var acc = new CtdbSyndromeAccumulator(new CtdbParityLayout(pcm.Length / 4), npar);
        acc.Append(pcm);
        return acc;
    }

    /// <summary>A CTDB entry and its parity file (index-major syndromes) for <paramref name="audio"/>.</summary>
    private static (CtdbEntry Entry, byte[] File) BuildEntry(byte[] audio, int npar, int? entryNpar = null)
    {
        var acc = Accumulate(audio, npar);
        ushort[] syn = acc.GetShiftedSyndromes(0, npar);
        var file = new byte[npar * Stride * 2];
        for (int i = 0; i < npar; i++)
        {
            for (int c = 0; c < Stride; c++)
            {
                ushort v = syn[c * npar + i];
                file[2 * (i * Stride + c)] = (byte)v;
                file[2 * (i * Stride + c) + 1] = (byte)(v >> 8);
            }
        }

        int inlineCount = entryNpar ?? npar;
        string inline = Convert.ToBase64String(file.Where((_, idx) => idx % (Stride * 2) < 2).Take(2 * inlineCount).ToArray());
        var crc = CtdbChecksum.ForDisc(audio.Length / 4);
        crc.Append(audio);
        var entry = new CtdbEntry(424242, crc.Value, 7, Array.Empty<uint>(), Npar: entryNpar ?? npar, RawStride: 5880,
            HasParity: ParityUrl, SyndromeBase64: inline, Toc: Toc);
        return (entry, file);
    }

    private AudioCdRipResult WriteRip(byte[] pcm, int[] trackSectors)
    {
        var infos = new List<AudioTrackRipInfo>();
        var starts = new List<long>();
        long totalSamples = pcm.Length / 4;
        int offset = 0;
        for (int t = 0; t < trackSectors.Length; t++)
        {
            int length = trackSectors[t] * SectorBytes;
            var slice = pcm.AsSpan(offset, length);
            string path = Path.Combine(_dir, $"{t + 1:D2} - Artist - Track.wav");
            WavFileCodec.WriteWavFile(path, slice.ToArray());
            var ar = new AccurateRipChecksum(length / 4, t == 0, t == trackSectors.Length - 1);
            ar.Append(slice);
            var ctdb = CtdbChecksum.ForTrack(offset / 4, length / 4, totalSamples);
            ctdb.Append(slice);
            infos.Add(new AudioTrackRipInfo(t + 1, path, 0, ar.V1, ar.V2, t, 0, CtdbTrackCrc: ctdb.Value));
            starts.Add(offset / 4);
            offset += length;
        }

        uint discCrc = CtdbChecksum.CombineTrackCrcs(infos, starts, totalSamples);
        return AudioCdRipResult.Succeeded(infos.Select(i => i.FilePath).ToList(), pcm.Length / SectorBytes, TimeSpan.Zero,
            trackInfos: infos, ctdbDiscCrc: discCrc);
    }

    private static byte[] ReadRip(AudioCdRipResult rip) =>
        rip.TrackInfos.OrderBy(t => t.AudioTrackIndex).SelectMany(t => WavFileCodec.DecodeToRedBookPcm(t.FilePath)).ToArray();

    private static IReadOnlyList<AccurateRipPressing> CleanPressings(Disc disc)
    {
        var entries = new List<AccurateRipTrackEntry>();
        int offset = 0;
        for (int t = 0; t < disc.TrackSectors.Length; t++)
        {
            int length = disc.TrackSectors[t] * SectorBytes;
            var ar = new AccurateRipChecksum(length / 4, t == 0, t == disc.TrackSectors.Length - 1);
            ar.Append(disc.Clean.AsSpan(offset, length));
            entries.Add(new AccurateRipTrackEntry(12, ar.V2, 0));
            offset += length;
        }

        return new[] { new AccurateRipPressing(1, 2, 3, entries) };
    }

    private static ushort[] ErrorSyndromes(int[] positions, ushort[] magnitudes, int rowCount, int npar)
    {
        var syn = new ushort[npar];
        for (int i = 0; i < npar; i++)
        {
            for (int e = 0; e < positions.Length; e++)
            {
                syn[i] ^= CtdbGalois16.Mul(magnitudes[e], CtdbGalois16.Pow((long)i * (rowCount - 1 - positions[e])));
            }
        }

        return syn;
    }

    /// <summary>Port of CUETools.Parity Galois.makeEncodeGx (generator Π(x + α^k), k = 0..npar-1).</summary>
    private static ushort[] UpstreamMakeEncodeGx(int npar)
    {
        var gx = new ushort[npar];
        gx[npar - 1] = 1;
        for (int kou = 0; kou < npar; kou++)
        {
            ushort ex = CtdbGalois16.Pow(kou);
            for (int j = 0; j < npar - 1; j++)
            {
                gx[j] = (ushort)(CtdbGalois16.Mul(gx[j], ex) ^ gx[j + 1]);
            }

            gx[npar - 1] = CtdbGalois16.Mul(gx[npar - 1], ex);
        }

        return gx;
    }

    /// <summary>Port of CUETools.Parity RsEncode16.encode (systematic parity register).</summary>
    private static ushort[] UpstreamRsEncode16(ushort[] data, ushort[] gx)
    {
        int npar = gx.Length;
        var wr = new ushort[npar];
        foreach (ushort d in data)
        {
            ushort ib = (ushort)(wr[0] ^ d);
            for (int i = 0; i < npar - 1; i++)
            {
                wr[i] = (ushort)(wr[i + 1] ^ CtdbGalois16.Mul(ib, gx[i]));
            }

            wr[npar - 1] = CtdbGalois16.Mul(ib, gx[npar - 1]);
        }

        return wr;
    }

    /// <summary>Serves a parity file at <see cref="ParityUrl"/>, honouring a single byte range like the real server.</summary>
    private sealed class ParityHandler(byte[] file) : HttpMessageHandler
    {
        public RangeHeaderValue? LastRange { get; private set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri != new Uri(ParityUrl))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            LastRange = request.Headers.Range;
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range?.From is long from)
            {
                long to = Math.Min(range.To ?? file.Length - 1, file.Length - 1);
                var part = new ByteArrayContent(file, (int)from, (int)(to - from + 1));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
        }
    }
}
