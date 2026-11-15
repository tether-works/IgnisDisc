using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Dvd;

/// <summary>
/// The periodic-9b-16-v1 read-compatibility rule, checked against its description: at offsets 0, 16, ..., 2032 of a
/// 2048-byte sector, a picture-owned byte whose scrambled value Q is not A5 or 9B gets Q = 9B, which is stored as
/// 9B XOR mask(PSN). Nothing else changes.
/// </summary>
public sealed class DvdReadCompatibilityGuardTests : IDisposable
{
    private const int SectorBytes = 2048;
    private const byte White = 0x92;   // scrambled byte of a white picture word
    private const byte Black = 0xA5;   // scrambled byte of a black picture word
    private const byte Guard = 0x9B;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-dvdguard-" + Guid.NewGuid().ToString("N"));

    public DvdReadCompatibilityGuardTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A sector whose scrambled bytes all equal <paramref name="scrambled"/>.</summary>
    private static byte[] SectorWithScrambledValue(byte scrambled, ReadOnlySpan<byte> mask)
    {
        byte[] payload = new byte[SectorBytes];
        for (int i = 0; i < SectorBytes; i++) payload[i] = (byte)(scrambled ^ mask[i]);
        return payload;
    }

    private static byte[] Filled(byte value)
    {
        byte[] bytes = new byte[SectorBytes];
        Array.Fill(bytes, value);
        return bytes;
    }

    [Theory]
    [InlineData(0x030000u)]   // first data sector of a DVD
    [InlineData(0x030010u)]   // next ECC block: another scrambler mask
    [InlineData(0x0300F7u)]
    public void All_white_owned_sector_changes_exactly_128_bytes(uint psn)
    {
        byte[] mask = DvdDataFrameBuilder.PayloadScramblerMask(psn).ToArray();
        byte[] before = SectorWithScrambledValue(White, mask);
        byte[] after = (byte[])before.Clone();

        int changed = DvdReadCompatibilityGuard.ApplySector(after, mask, Filled(1));

        Assert.Equal(128, changed);
        int differing = 0;
        for (int offset = 0; offset < SectorBytes; offset++)
        {
            byte scrambled = (byte)(after[offset] ^ mask[offset]);
            if (offset % 16 == 0)
            {
                Assert.Equal(Guard, scrambled);
                Assert.Equal((byte)(Guard ^ mask[offset]), after[offset]);
            }
            else
            {
                Assert.Equal(White, scrambled);
            }
            if (after[offset] != before[offset]) differing++;
        }
        Assert.Equal(128, differing);
    }

    [Fact]
    public void Scrambled_a5_and_9b_are_left_alone()
    {
        byte[] mask = DvdDataFrameBuilder.PayloadScramblerMask(0x30000).ToArray();
        foreach (byte kept in new[] { Black, Guard })
        {
            byte[] before = SectorWithScrambledValue(kept, mask);
            byte[] after = (byte[])before.Clone();

            Assert.Equal(0, DvdReadCompatibilityGuard.ApplySector(after, mask, Filled(1)));
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void Every_other_scrambled_value_at_a_guarded_offset_becomes_9b()
    {
        // A mask that is not a real scrambler sequence: the rule must use whatever mask it is given.
        byte[] mask = new byte[SectorBytes];
        for (int i = 0; i < SectorBytes; i++) mask[i] = (byte)((i * 31) + 7);
        byte[] before = new byte[SectorBytes];
        for (int i = 0; i < SectorBytes; i++) before[i] = (byte)(((i / 16) + (i % 16)) ^ mask[i]);   // Q = 0..127 at guarded offsets
        byte[] after = (byte[])before.Clone();

        int changed = DvdReadCompatibilityGuard.ApplySector(after, mask, Filled(1));

        Assert.Equal(128, changed);   // 0..127 contains neither A5 nor 9B
        for (int offset = 0; offset < SectorBytes; offset++)
        {
            if (offset % 16 == 0) Assert.Equal(Guard, (byte)(after[offset] ^ mask[offset]));
            else Assert.Equal(before[offset], after[offset]);
        }
    }

    [Fact]
    public void Bytes_the_picture_does_not_own_never_change()
    {
        byte[] mask = DvdDataFrameBuilder.PayloadScramblerMask(0x30000).ToArray();

        // Nothing owned: a sector of file data stays as it is.
        byte[] fileSector = new byte[SectorBytes];
        new Random(91).NextBytes(fileSector);
        byte[] untouched = (byte[])fileSector.Clone();
        Assert.Equal(0, DvdReadCompatibilityGuard.ApplySector(untouched, mask, new byte[SectorBytes]));
        Assert.Equal(fileSector, untouched);

        // Picture owns only the second half: the first half stays, the second half gets 64 guard bytes.
        byte[] ownership = new byte[SectorBytes];
        Array.Fill(ownership, (byte)1, 1024, 1024);
        byte[] before = SectorWithScrambledValue(White, mask);
        byte[] after = (byte[])before.Clone();
        Assert.Equal(64, DvdReadCompatibilityGuard.ApplySector(after, mask, ownership));
        Assert.Equal(before[..1024], after[..1024]);
        for (int offset = 1024; offset < SectorBytes; offset += 16)
            Assert.Equal(Guard, (byte)(after[offset] ^ mask[offset]));
    }

    [Fact]
    public void Wrong_buffer_lengths_are_rejected()
    {
        byte[] ok = new byte[SectorBytes];
        Assert.Throws<ArgumentException>(() => DvdReadCompatibilityGuard.ApplySector(new byte[2047], ok, ok));
        Assert.Throws<ArgumentException>(() => DvdReadCompatibilityGuard.ApplySector(new byte[SectorBytes], new byte[2049], ok));
        Assert.Throws<ArgumentException>(() => DvdReadCompatibilityGuard.ApplySector(new byte[SectorBytes], ok, new byte[16]));
    }

    private string WriteSolidPng(string name, byte gray, byte alpha)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
            pixels[i + 3] = alpha;
        }
        string path = Path.Combine(_tempDir, name);
        PngRaster.SaveBgra32(path, new BgraRaster(16, 16, pixels));
        return path;
    }

    private static byte[] Generate(string png, bool readCompatibility, out long changes)
    {
        using MemoryStream output = new();
        FastDvdStreamSummary summary = FastDispersionImageWriter.WriteStream(
            png, output, DvdPictureGoldenTests.GoldenOptions(160, readCompatibility));
        changes = summary.CompatibilityChanges;
        return output.ToArray();
    }

    [Fact]
    public void Generator_changes_128_bytes_in_every_sector_of_an_all_white_picture()
    {
        string white = WriteSolidPng("white.png", 255, 255);

        byte[] plain = Generate(white, readCompatibility: false, out long plainChanges);
        byte[] guarded = Generate(white, readCompatibility: true, out long changes);

        Assert.Equal(0, plainChanges);
        Assert.Equal(160L * 128, changes);
        Assert.Equal(160 * SectorBytes, guarded.Length);
        for (int sector = 0; sector < 160; sector++)
        {
            ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(0x30000u + (uint)sector);
            int differing = 0;
            for (int offset = 0; offset < SectorBytes; offset++)
            {
                int index = (sector * SectorBytes) + offset;
                Assert.Equal(White, (byte)(plain[index] ^ mask[offset]));
                if (guarded[index] == plain[index]) continue;
                differing++;
                Assert.Equal(0, offset % 16);
                Assert.Equal(Guard, (byte)(guarded[index] ^ mask[offset]));
            }
            Assert.Equal(128, differing);
        }
    }

    [Fact]
    public void Generator_leaves_sectors_alone_where_the_picture_is_transparent()
    {
        string transparent = WriteSolidPng("transparent.png", 255, 0);

        byte[] plain = Generate(transparent, readCompatibility: false, out _);
        byte[] guarded = Generate(transparent, readCompatibility: true, out long changes);

        Assert.Equal(0, changes);
        Assert.Equal(plain, guarded);
        Assert.All(guarded, value => Assert.Equal(0, value));
    }
}
