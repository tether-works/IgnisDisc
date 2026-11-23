using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class LegacyImageValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignis-imgval-" + Guid.NewGuid().ToString("N"));

    public LegacyImageValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] MakeIso(int sectors, byte seed = 0)
    {
        byte[] iso = new byte[sectors * OpticalConstants.DataSectorSize];
        for (int i = 0; i < iso.Length; i++)
        {
            iso[i] = (byte)((i * 31 + (i / OpticalConstants.DataSectorSize) + seed) & 0xFF);
        }
        "CD001"u8.CopyTo(iso.AsSpan(16 * OpticalConstants.DataSectorSize + 1));
        iso[16 * OpticalConstants.DataSectorSize] = 1;
        return iso;
    }

    private static byte[] WrapRaw2448Mode1(byte[] iso)
    {
        int n = iso.Length / OpticalConstants.DataSectorSize;
        byte[] raw = new byte[n * 2448];
        for (int i = 0; i < n; i++)
        {
            Span<byte> sec = raw.AsSpan(i * 2448, 2448);
            sec[0] = 0x00;
            sec.Slice(1, 10).Fill(0xFF);
            sec[11] = 0x00;
            sec[15] = 0x01;
            iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).CopyTo(sec.Slice(16));
            sec.Slice(2352, 96).Fill(0xA5);
        }
        return raw;
    }

    private static void WriteMdsFile(
        string mdsPath,
        string mdfFileName,
        IReadOnlyList<(byte Mode, byte Subchannel, byte Point, ushort SectorSize, ulong StartOffset, uint Sectors)> tracks)
    {
        const int headerSize = 88;
        const int sessionSize = 24;
        const int trackSize = 80;
        const int extraSize = 8;
        const int footerSize = 16;

        int trackTableOffset = headerSize + sessionSize;
        int extraTableOffset = trackTableOffset + (tracks.Count * trackSize);
        int footerTableOffset = extraTableOffset + (tracks.Count * extraSize);
        int filenameOffset = footerTableOffset + footerSize;
        byte[] nameBytes = Encoding.ASCII.GetBytes(mdfFileName + "\0");

        byte[] mds = new byte[filenameOffset + nameBytes.Length];
        Span<byte> span = mds;
        "MEDIA DESCRIPTOR"u8.CopyTo(span.Slice(0, 16));
        span[16] = 1;
        span[17] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0); // CD
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), 1); // 1 session
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(80, 4), headerSize);

        Span<byte> sess = span.Slice(headerSize, sessionSize);
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(8, 2), 1);
        sess[10] = (byte)tracks.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(sess.Slice(14, 2), (ushort)tracks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(sess.Slice(20, 4), (uint)trackTableOffset);

        for (int i = 0; i < tracks.Count; i++)
        {
            var trk = tracks[i];
            int tOff = trackTableOffset + (i * trackSize);
            int eOff = extraTableOffset + (i * extraSize);
            span[tOff + 0] = trk.Mode;
            span[tOff + 1] = trk.Subchannel;
            span[tOff + 4] = trk.Point;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tOff + 12, 4), (uint)eOff);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(tOff + 16, 2), trk.SectorSize);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(tOff + 40, 8), trk.StartOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tOff + 48, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tOff + 52, 4), (uint)footerTableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(eOff + 4, 4), trk.Sectors);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(footerTableOffset, 4), (uint)filenameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(footerTableOffset + 4, 4), 0);
        nameBytes.CopyTo(span.Slice(filenameOffset));

        File.WriteAllBytes(mdsPath, mds);
    }

    private static void WriteMultiTrackNrg(
        string nrgPath,
        byte[] payload,
        IReadOnlyList<(ulong Offset, ulong Size, uint Mode, uint StartLba, ulong Sectors)> records,
        bool useEtnf = false)
    {
        using var fs = new FileStream(nrgPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(payload);
        ulong trailerOffset = (ulong)fs.Position;

        if (!useEtnf)
        {
            Span<byte> hdr = stackalloc byte[8];
            "ETN2"u8.CopyTo(hdr.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(4, 4), (uint)(records.Count * 32));
            fs.Write(hdr);

            Span<byte> rec = stackalloc byte[32];
            foreach (var r in records)
            {
                rec.Clear();
                BinaryPrimitives.WriteUInt64BigEndian(rec.Slice(0, 8), r.Offset);
                BinaryPrimitives.WriteUInt64BigEndian(rec.Slice(8, 8), r.Size);
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(16, 4), r.Mode);
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(20, 4), r.StartLba);
                BinaryPrimitives.WriteUInt64BigEndian(rec.Slice(24, 8), r.Sectors);
                fs.Write(rec);
            }

            Span<byte> footer = stackalloc byte[20];
            "END!"u8.CopyTo(footer.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(4, 4), 0);
            "NER5"u8.CopyTo(footer.Slice(8, 4));
            BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), trailerOffset);
            fs.Write(footer);
        }
        else
        {
            Span<byte> hdr = stackalloc byte[8];
            "ETNF"u8.CopyTo(hdr.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(4, 4), (uint)(records.Count * 20));
            fs.Write(hdr);

            Span<byte> rec = stackalloc byte[20];
            foreach (var r in records)
            {
                rec.Clear();
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(0, 4), (uint)r.Offset);
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(4, 4), (uint)r.Size);
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(8, 4), r.Mode);
                BinaryPrimitives.WriteUInt32BigEndian(rec.Slice(12, 4), r.StartLba);
                fs.Write(rec);
            }

            Span<byte> footer = stackalloc byte[16];
            "END!"u8.CopyTo(footer.Slice(0, 4));
            BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(4, 4), 0);
            "NERO"u8.CopyTo(footer.Slice(8, 4));
            BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(12, 4), (uint)trailerOffset);
            fs.Write(footer);
        }
    }

    [Theory]
    [InlineData("empty.iso")]
    [InlineData("empty.bin")]
    [InlineData("empty.img")]
    [InlineData("empty.mdf")]
    public void EmptyBinaryImages_ThrowInvalidDataException(string fileName)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(path));
    }

    [Fact]
    public void CloneCdCcd_MissingPayloadImg_ThrowsFileNotFoundException()
    {
        string ccd = Path.Combine(_dir, "orphan.ccd");
        File.WriteAllText(ccd, "[CloneCD]\nVersion=3\n[Track 1]\nMODE=1\nINDEX 1=0\n");
        Assert.Throws<FileNotFoundException>(() => LegacyDiscImageConverter.Inspect(ccd));
    }

    [Fact]
    public void CloneCdCcd_InvalidControlFile_ThrowsInvalidDataException()
    {
        string ccd = Path.Combine(_dir, "garbage.ccd");
        string img = Path.Combine(_dir, "garbage.img");
        File.WriteAllText(ccd, "not a clonecd file\n");
        File.WriteAllBytes(img, new byte[2048]);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(ccd));
    }

    [Fact]
    public void AlcoholMds_CorruptHeaderOrMissingMdf_ThrowsExpectedException()
    {
        string badSig = Path.Combine(_dir, "badsig.mds");
        File.WriteAllBytes(badSig, new byte[128]);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(badSig));

        string truncated = Path.Combine(_dir, "short.mds");
        File.WriteAllBytes(truncated, "MEDIA DESCRIPTOR"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(truncated));

        string validMissingMdf = Path.Combine(_dir, "missing.mds");
        WriteMdsFile(validMissingMdf, "missing.mdf", [(0xAA, 0x00, 1, 2048, 0, 10)]);
        Assert.Throws<FileNotFoundException>(() => LegacyDiscImageConverter.Inspect(validMissingMdf));
    }

    [Fact]
    public async Task AlcoholMds_ParsesTrackGeometry_AudioAndMixedMode()
    {
        // Audio-only MDS/MDF (mode 0xA9, 2352 bytes/sector)
        string audioMds = Path.Combine(_dir, "audio.mds");
        LegacyDiscImageConverter.CreateAlcoholMdsMdfImage(audioMds, new byte[2352 * 12], sectorSize: 2352, audio: true);
        var audioInfo = LegacyDiscImageConverter.Inspect(audioMds);
        Assert.True(audioInfo.IsAudioDisc);
        Assert.Equal(2352, audioInfo.SectorSizeBytes);
        Assert.Equal(12, audioInfo.SectorCount);
        Assert.Equal(1, audioInfo.TrackCount);

        // Mixed-mode MDS/MDF: Track 1 audio (4 raw sectors), Track 2 Mode 1 data (20 sectors @ 2048)
        byte[] audioBytes = new byte[2352 * 4];
        byte[] isoBytes = MakeIso(20);
        byte[] combined = [.. audioBytes, .. isoBytes];
        string mixedMds = Path.Combine(_dir, "mixed.mds");
        File.WriteAllBytes(Path.ChangeExtension(mixedMds, ".mdf"), combined);
        WriteMdsFile(
            mixedMds,
            "mixed.mdf",
            [
                (0xA9, 0x00, 1, 2352, 0, 4),
                (0xAA, 0x00, 2, 2048, (ulong)audioBytes.Length, 20)
            ]);

        var mixedInfo = LegacyDiscImageConverter.Inspect(mixedMds);
        Assert.False(mixedInfo.IsAudioDisc);
        Assert.Equal(2, mixedInfo.TrackCount);
        Assert.Equal(2048, mixedInfo.SectorSizeBytes);
        Assert.Equal(20, mixedInfo.SectorCount);
        Assert.Equal(audioBytes.Length, mixedInfo.PayloadOffsetBytes);

        string outIso = Path.Combine(_dir, "mixed_out.iso");
        await LegacyDiscImageConverter.ConvertToIsoAsync(mixedMds, outIso);
        Assert.Equal(isoBytes, File.ReadAllBytes(outIso));
    }

    [Fact]
    public void Nrg_RejectsGarbageAndOverflowAndNegativeOffsetBounds()
    {
        // 1. Garbage file with no NER5/NERO signature
        string garbage = Path.Combine(_dir, "garbage.nrg");
        File.WriteAllBytes(garbage, new byte[2048]);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(garbage));

        // 2. Garbage bytes with NER5 signature and plausible offset but no valid chunks
        string fakeNer5 = Path.Combine(_dir, "fakener5.nrg");
        byte[] fake = new byte[64];
        new Random(3).NextBytes(fake);
        "NER5"u8.CopyTo(fake.AsSpan(fake.Length - 12, 4));
        BinaryPrimitives.WriteUInt64BigEndian(fake.AsSpan(fake.Length - 8, 8), 16);
        File.WriteAllBytes(fakeNer5, fake);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(fakeNer5));

        // 3. NER5 footer offset > long.MaxValue (previously cast negatively!)
        string negFooter = Path.Combine(_dir, "negfooter.nrg");
        byte[] nf = new byte[64];
        "NER5"u8.CopyTo(nf.AsSpan(nf.Length - 12, 4));
        BinaryPrimitives.WriteUInt64BigEndian(nf.AsSpan(nf.Length - 8, 8), 0x8000_0000_0000_0010UL);
        File.WriteAllBytes(negFooter, nf);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(negFooter));

        // 4. ETN2 ulong off + sz wraparound overflow (0xFFFFFFFFFFFFF000 + 0x2000 == 0x1000)
        string etn2Overflow = Path.Combine(_dir, "etn2_overflow.nrg");
        WriteMultiTrackNrg(
            etn2Overflow,
            new byte[2048],
            [(0xFFFF_FFFF_FFFF_F000UL, 0x2000UL, 0U, 0U, 4UL)]);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(etn2Overflow));

        // 5. ETNF uint off + sz wraparound overflow (0xFFFFF000 + 0x2000 == 0x1000)
        string etnfOverflow = Path.Combine(_dir, "etnf_overflow.nrg");
        WriteMultiTrackNrg(
            etnfOverflow,
            new byte[2048],
            [(0xFFFF_F000UL, 0x2000UL, 0U, 0U, 4UL)],
            useEtnf: true);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(etnfOverflow));
    }

    [Fact]
    public async Task Nrg_ParsesMultiTrackEtn2_AndExtractsDataTrack()
    {
        byte[] audioTrack = new byte[2352 * 4];
        byte[] dataTrack = MakeIso(20);
        byte[] payload = [.. audioTrack, .. dataTrack];
        string nrg = Path.Combine(_dir, "multitrack.nrg");

        WriteMultiTrackNrg(
            nrg,
            payload,
            [
                (0UL, (ulong)audioTrack.Length, 7U, 0U, 4UL),
                ((ulong)audioTrack.Length, (ulong)dataTrack.Length, 0U, 4U, 20UL)
            ]);

        var info = LegacyDiscImageConverter.Inspect(nrg);
        Assert.False(info.IsAudioDisc);
        Assert.Equal(2, info.TrackCount);
        Assert.Equal(2048, info.SectorSizeBytes);
        Assert.Equal(20, info.SectorCount);
        Assert.Equal(audioTrack.Length, info.PayloadOffsetBytes);

        string outIso = Path.Combine(_dir, "multi_out.iso");
        await LegacyDiscImageConverter.ConvertToIsoAsync(nrg, outIso);
        Assert.Equal(dataTrack, File.ReadAllBytes(outIso));
    }

    [Fact]
    public void DiscJuggler_AudioOnly_ReportsNonZeroSectorCount_AndCorruptThrowsInvalidData()
    {
        string audioCdi = Path.Combine(_dir, "audio_count.cdi");
        LegacyDiscImageConverter.CreateDiscJugglerFile(audioCdi, new byte[2352 * 10], 2352, version: 3, audio: true);
        var info = LegacyDiscImageConverter.Inspect(audioCdi);
        Assert.True(info.IsAudioDisc);
        Assert.Equal(2352, info.SectorSizeBytes);
        Assert.Equal(10, info.SectorCount);
        Assert.Equal(23520, info.PayloadLengthBytes);

        // Truncate inside track descriptor so BinaryReader would previously throw EndOfStreamException
        string truncCdi = Path.Combine(_dir, "trunc_mid_track.cdi");
        byte[] valid = File.ReadAllBytes(audioCdi);
        // Keep a valid trailer pointing only 12 bytes before the trailer (past sessions+tracks+extraFlag, mid-marker)
        byte[] shortened = new byte[2352 + 12 + 8];
        Array.Copy(valid, 0, shortened, 0, 2352);
        // Copy first 12 bytes of descriptor (sessions, tracks, extraFlag, partial startMark)
        Array.Copy(valid, 2352 * 10, shortened, 2352, 12);
        BitConverter.GetBytes(0x80000005u).CopyTo(shortened, shortened.Length - 8);
        BitConverter.GetBytes(20u).CopyTo(shortened, shortened.Length - 4);
        File.WriteAllBytes(truncCdi, shortened);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(truncCdi));

        // Corrupt length > int.MaxValue so checked((int)length) would previously throw OverflowException
        string overflowCdi = Path.Combine(_dir, "overflow_len.cdi");
        byte[] ovf = (byte[])valid.Clone();
        // Descriptor starts at 2352 * 10; length field is at +2+2+4+10+10+4+1+19+4+2+4 = +62
        int descStart = 2352 * 10;
        BitConverter.GetBytes(0x9000_0000u).CopyTo(ovf, descStart + 62);
        File.WriteAllBytes(overflowCdi, ovf);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(overflowCdi));
    }

    [Fact]
    public async Task BinCue_2448ByteDivisibleByLcm2448And2352_ConvertsAll49SectorsToIso()
    {
        // 49 sectors: 49 * 2448 == 119,952 == 51 * 2352 == LCM(2448, 2352)
        byte[] iso = MakeIso(49);
        byte[] raw2448 = WrapRaw2448Mode1(iso);
        Assert.Equal(0, raw2448.Length % 2448);
        Assert.Equal(0, raw2448.Length % 2352);

        string binPath = Path.Combine(_dir, "lcm49.bin");
        string cuePath = Path.Combine(_dir, "lcm49.cue");
        string outIso = Path.Combine(_dir, "lcm49_out.iso");
        await File.WriteAllBytesAsync(binPath, raw2448);
        await File.WriteAllTextAsync(cuePath, "FILE \"lcm49.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

        int written = await LegacyDiscImageConverter.ConvertBinCueToIsoAsync(cuePath, outIso);
        Assert.Equal(49, written);
        Assert.Equal(iso, await File.ReadAllBytesAsync(outIso));
    }

    [Fact]
    public async Task RawSectorExtraction_EnforcesSyncAndModeAndForm2()
    {
        byte[] missingSync = new byte[2352];
        missingSync[15] = 1;
        string missingSyncBin = Path.Combine(_dir, "nosync.bin");
        string missingSyncCue = Path.Combine(_dir, "nosync.cue");
        await File.WriteAllBytesAsync(missingSyncBin, missingSync);
        await File.WriteAllTextAsync(missingSyncCue, "FILE \"nosync.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LegacyDiscImageConverter.ConvertBinCueToIsoAsync(missingSyncCue, Path.Combine(_dir, "nosync.iso")));

        byte[] badMode = new byte[2352];
        badMode.AsSpan(1, 10).Fill(0xFF);
        badMode[15] = 9;
        string badModeBin = Path.Combine(_dir, "badmode.bin");
        string badModeCue = Path.Combine(_dir, "badmode.cue");
        await File.WriteAllBytesAsync(badModeBin, badMode);
        await File.WriteAllTextAsync(badModeCue, "FILE \"badmode.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LegacyDiscImageConverter.ConvertBinCueToIsoAsync(badModeCue, Path.Combine(_dir, "badmode.iso")));

        byte[] form2 = new byte[2352];
        form2.AsSpan(1, 10).Fill(0xFF);
        form2[15] = 2;
        form2[18] = 0x20;
        form2[22] = 0x20;
        string form2Bin = Path.Combine(_dir, "form2.bin");
        string form2Cue = Path.Combine(_dir, "form2.cue");
        await File.WriteAllBytesAsync(form2Bin, form2);
        await File.WriteAllTextAsync(form2Cue, "FILE \"form2.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LegacyDiscImageConverter.ConvertBinCueToIsoAsync(form2Cue, Path.Combine(_dir, "form2.iso")));
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.OpenCueIsoUserDataStream(form2Cue));
    }
}
