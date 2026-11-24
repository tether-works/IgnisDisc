using System.Buffers.Binary;
using System.Security.Cryptography;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class DiscManifestCheckTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Dispose_BeforeFinish_DisposesAllIncrementalHashInstances()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("alpha.bin", new byte[4096]);
        root.AddFile("beta.bin", new byte[4096]);
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root, new IsoBuildOptions(FixedTimestampUtc: FixedTime, WriteSha256Manifest: true)).ToArray();

        using var reader = IsoImageReader.FromStream(new MemoryStream(image, writable: false));
        var check = DiscManifestCheck.Prepare(reader)!;
        var hashers = check.HashersForTesting.ToList();
        Assert.Equal(2, hashers.Count);

        // Feed only one sector, then abort before Finish().
        check.AddSector(0, image.AsSpan(0, OpticalConstants.DataSectorSize));
        check.Dispose();

        foreach (IncrementalHash hasher in hashers)
        {
            Assert.Throws<ObjectDisposedException>(() => hasher.AppendData(ReadOnlySpan<byte>.Empty));
        }

        Assert.Throws<ObjectDisposedException>(() => check.AddSector(1, image.AsSpan(2048, 2048)));
        Assert.Throws<ObjectDisposedException>(() => check.MarkUnreadable(1));
        Assert.Throws<ObjectDisposedException>(() => check.Finish());

        // Calling Dispose again is safe.
        check.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UdfOnlyLongFileNameOver64Chars_IsLookedUpAndVerifiedCleanly(bool includeJoliet)
    {
        // Exceeds Joliet's 64-character limit: on ISO+Joliet+UDF it is truncated in Joliet; on ISO+UDF it is UDF-only.
        string longName = "01 - A very long archival track title exceeding sixty-four characters in length (2026).bin";
        Assert.True(longName.Length > 64);

        byte[] payload = new byte[5000];
        new Random(1234).NextBytes(payload);

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("short.txt", "hello"u8.ToArray());
        root.AddFile(longName, payload);

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(
                IncludeJoliet: includeJoliet,
                IncludeUdf: true,
                FixedTimestampUtc: FixedTime,
                WriteSha256Manifest: true,
                WriteBlake3Manifest: true)).ToArray();

        // 1. Intact disc passes and verifies all files with zero FilesNotLookedUp.
        int sectors = image.Length / OpticalConstants.DataSectorSize;
        using (var reader = IsoImageReader.FromStream(new MemoryStream(image, writable: false)))
        using (var check = DiscManifestCheck.Prepare(reader)!)
        {
            for (int lba = 0; lba < sectors; lba++)
            {
                check.AddSector(lba, image.AsSpan(lba * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize));
            }
            var ok = check.Finish();
            Assert.True(ok.Passed);
            Assert.Equal(0, ok.FilesNotLookedUp);
            Assert.Equal(2, ok.FilesChecked);
            Assert.Empty(ok.MismatchedFiles);
            Assert.Empty(ok.MissingFiles);
        }

        // 2. Corrupting a byte in the >64-char UDF-only file is caught by DiscManifestCheck.
        byte[] tampered = (byte[])image.Clone();
        using (var udfStream = new MemoryStream(tampered, writable: false))
        {
            var udfRoot = UdfTreeReader.TryRead(udfStream)!;
            var longEntry = udfRoot.Files.Single(f => f.Name == longName);
            tampered[longEntry.StartLba * OpticalConstants.DataSectorSize + 123] ^= 0xFF;
        }

        using (var reader = IsoImageReader.FromStream(new MemoryStream(tampered, writable: false)))
        using (var check = DiscManifestCheck.Prepare(reader)!)
        {
            for (int lba = 0; lba < sectors; lba++)
            {
                check.AddSector(lba, tampered.AsSpan(lba * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize));
            }
            var fail = check.Finish();
            Assert.False(fail.Passed);
            Assert.Equal(0, fail.FilesNotLookedUp);
            Assert.Contains("/" + longName, fail.MismatchedFiles);
        }
    }

    [Fact]
    public void DamagedUdfTree_LeavesLongFileInFilesNotLookedUp_AndPassedIsFalse()
    {
        string longName = "02 - Another track title exceeding sixty-four characters for UDF-only lookup fallback test.bin";
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(longName, new byte[4096]);

        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(
                IncludeJoliet: true,
                IncludeUdf: true,
                FixedTimestampUtc: FixedTime,
                WriteSha256Manifest: true)).ToArray();

        // Keep AVDP at LBA 256 intact so IsoImageReader.HasUdf stays true, but destroy VDS copies so UdfTreeReader fails.
        uint mainVds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        uint resVds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 28));
        Array.Clear(image, (int)mainVds * 2048, 16 * 2048);
        Array.Clear(image, (int)resVds * 2048, 16 * 2048);

        using var reader = IsoImageReader.FromStream(new MemoryStream(image, writable: false));
        Assert.True(reader.HasUdf);
        using var check = DiscManifestCheck.Prepare(reader)!;
        var result = check.Finish();

        Assert.Equal(1, result.FilesNotLookedUp);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ManifestExceedingMaxSizeCap_ThrowsInvalidDataException()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", "abc"u8.ToArray());
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(
            root, new IsoBuildOptions(FixedTimestampUtc: FixedTime, WriteSha256Manifest: true)).ToArray();

        // Inflate SHA256SUMS DataLength in the Joliet root directory extent past MaxManifestSizeBytes.
        using var probe = IsoImageReader.FromStream(new MemoryStream(image, writable: false));
        uint jolietRootLba = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(17 * 2048 + 156 + 2, 4));
        int dirOff = (int)jolietRootLba * 2048;
        int pos = 0;
        bool patched = false;
        while (pos < 2048)
        {
            int len = image[dirOff + pos];
            if (len == 0) break;
            int idLen = image[dirOff + pos + 32];
            if (idLen == "SHA256SUMS".Length * 2)
            {
                uint oversized = (uint)(DiscManifestCheck.MaxManifestSizeBytes + 2048);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dirOff + pos + 10, 4), oversized);
                BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + pos + 14, 4), oversized);
                patched = true;
                break;
            }
            pos += len;
        }
        Assert.True(patched);

        using var reader = IsoImageReader.FromStream(new MemoryStream(image, writable: false));
        Assert.Throws<InvalidDataException>(() => DiscManifestCheck.Prepare(reader));
    }
}
