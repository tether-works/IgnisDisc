// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Disc Image Studio contributors
// Ported to IgnisDisc from DiscImageStudio src/DiscImageStudio.Cd/CdDataImageGenerator.cs at commit 619ec7505ac8955f35fe2f353ebda74b6ea90dda:
// https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/blob/619ec7505ac8955f35fe2f353ebda74b6ea90dda/src/DiscImageStudio.Cd/CdDataImageGenerator.cs
// IgnisDisc changes: namespace and class name; only the drawing part is ported (payload-to-disc byte map, scramble
// compensation, drawing sectors for an LBA range, read-back of those sectors). Upstream's ISO 9660 layout
// (DiscImageStudio.Iso) is not ported: IgnisDisc's own image builder will place file systems next to the drawing.
// The drawing arithmetic is unchanged. See THIRD-PARTY-NOTICES.md.

using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Cd;

/// <summary>
/// Produces cooked 2048-byte Mode 1 sectors that draw a picture. The recording backend supplies headers, EDC/ECC,
/// scrambling and CIRC. Only the drawing payload is compensated for those reversible byte transformations, so after
/// the drive scrambles a sector its user-data bytes are the CD palette bytes (<see cref="CdToneMapping"/>).
/// </summary>
public static class CdMode1Drawing
{
    public const int BytesPerSector = 2048;
    private const int PayloadOffset = 16;
    private static readonly byte[] ScrambleMask = BuildScrambleMask();
    private static readonly int[] PhysicalOffsets = BuildPhysicalOffsets();

    /// <summary>
    /// Writes the drawing sectors <paramref name="firstLba"/> .. <paramref name="endLbaExclusive"/> - 1 (LBAs counted
    /// from the start of <paramref name="parameters"/>' geometry) to <paramref name="output"/>, 2048 bytes each.
    /// </summary>
    public static CdGenerationSummary WriteSectors(BgraRaster picture, Stream output, CdDiscParameters parameters,
        long firstLba, long endLbaExclusive, Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default, ToneProfile? tone = null, bool useSimdSampling = true,
        bool useParallelSampling = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("CD data output must be writable.", nameof(output));
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (firstLba < 0 || endLbaExclusive <= firstLba || endLbaExclusive > parameters.OutputSectors)
            throw new ArgumentOutOfRangeException(nameof(endLbaExclusive),
                "The drawing range must be a non-empty range of sectors inside the geometry.");
        cancellationToken.ThrowIfCancellationRequested();
        CdImageSampler sampler = new(RasterImage.FromRaster(picture), parameters);
        ToneProfile effectiveTone = (tone ?? ToneProfile.LegacyBinary).Normalized();
        bool sampleFour = useSimdSampling && sampler.SupportsSimd;
        long drawingSectors = endLbaExclusive - firstLba;
        long totalBytes = checked(drawingSectors * BytesPerSector);
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long written = 0;
        long nextProgress = 0;

        void Write(ReadOnlySpan<byte> sector)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(sector);
            written += sector.Length;
            if (written < nextProgress) return;
            progress?.Invoke(new CdProgress(written, totalBytes, stopwatch.Elapsed));
            nextProgress = written + Math.Max(BytesPerSector, totalBytes / 200);
        }

        if (useParallelSampling)
        {
            // Only immutable raster sampling and tone conversion run on workers.
            // Stream writes and progress remain on the caller.
            CdBlockPipeline.Run(drawingSectors, BytesPerSector,
                (first, buffer, sectorCount) =>
                {
                    for (int index = 0; index < sectorCount; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FillDrawingSector(sampler, effectiveTone, sampleFour, firstLba + first + index,
                            buffer.AsSpan(index * BytesPerSector, BytesPerSector));
                    }
                },
                (_, buffer, sectorCount) =>
                {
                    for (int index = 0; index < sectorCount; index++)
                        Write(buffer.AsSpan(index * BytesPerSector, BytesPerSector));
                }, cancellationToken);
        }
        else
        {
            byte[] sector = new byte[BytesPerSector];
            for (long lba = firstLba; lba < endLbaExclusive; lba++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FillDrawingSector(sampler, effectiveTone, sampleFour, lba, sector);
                Write(sector);
            }
        }
        output.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(written, totalBytes, stopwatch.Elapsed));
        return new CdGenerationSummary("raster", "direct-burn-stream", drawingSectors, written, false, stopwatch.Elapsed);
    }

    private static void FillDrawingSector(CdImageSampler sampler, ToneProfile tone, bool sampleFour,
        long lba, Span<byte> sector)
    {
        Span<long> positions = stackalloc long[4];
        int offset = 0;
        if (sampleFour)
        {
            for (; offset <= BytesPerSector - 4; offset += 4)
            {
                // CIRC delays and the audio pair swap make these physical
                // positions non-contiguous even within one Mode 1 payload.
                for (int lane = 0; lane < 4; lane++)
                    positions[lane] = PhysicalByteForPayload(lba, offset + lane);
                uint samples = sampler.SampleFour(positions[0], positions[1], positions[2], positions[3]);
                for (int lane = 0; lane < 4; lane++)
                {
                    sector[offset + lane] = (byte)(CdToneMapping.Map((byte)samples, tone, positions[lane])
                        ^ ScrambleMask[offset + lane + PayloadOffset - 12]);
                    samples >>= 8;
                }
            }
        }
        for (; offset < BytesPerSector; offset++)
        {
            long physicalByte = PhysicalByteForPayload(lba, offset);
            sector[offset] = (byte)(CdToneMapping.Map(sampler.Sample(physicalByte), tone, physicalByte)
                ^ ScrambleMask[offset + PayloadOffset - 12]);
        }
    }

    /// <summary>
    /// Locates a payload byte in the existing CD drawing coordinate convention. ECMA-130
    /// clauses 14–17: 16-byte header, scrambling, adjacent byte swap, then CIRC. Invert
    /// the shared logical-to-file delay map to sample the byte's eventual disc location.
    /// </summary>
    public static long PhysicalByteForPayload(long lba, int payloadOffset)
    {
        if (lba < 0 || (uint)payloadOffset >= BytesPerSector) throw new ArgumentOutOfRangeException(nameof(payloadOffset));
        long frameByte = checked(lba * CdDiscParameters.BytesPerSector + PayloadOffset + payloadOffset) ^ 1L;
        return frameByte + PhysicalOffsets[frameByte % CddaInterleaveTable.BytesPerFrame];
    }

    private static int[] BuildPhysicalOffsets()
    {
        int[] inverse = new int[CddaInterleaveTable.BytesPerFrame];
        for (int logical = 0; logical < inverse.Length; logical++)
        {
            int displacement = CddaInterleaveTable.FileOffsetFor(logical);
            inverse[(logical + displacement) % inverse.Length] = -displacement;
        }
        return inverse;
    }

    // ECMA-130 Annex B: x^15 + x + 1, seed 1 at byte 12, least significant bit first.
    // https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf
    private static byte[] BuildScrambleMask()
    {
        byte[] mask = new byte[CdDiscParameters.BytesPerSector - 12];
        int state = 1;
        for (int index = 0; index < mask.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                mask[index] |= (byte)((state & 1) << bit);
                state = (state >> 1) | (((state ^ (state >> 1)) & 1) << 14);
            }
        }
        return mask;
    }

    /// <summary>
    /// Read-back of drawing sectors: <paramref name="sectors"/> holds 2048-byte user data of the LBAs
    /// <paramref name="firstLba"/> .. <paramref name="endLbaExclusive"/> - 1, in order. Returns
    /// <paramref name="outputSize"/>² BGRA pixels as the disc would show them.
    /// </summary>
    public static byte[] RenderSectors(Stream sectors, long firstLba, long endLbaExclusive, CdDiscParameters actual,
        int outputSize, int byteStep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sectors);
        ValidatePreview(actual, outputSize, byteStep);
        if (firstLba < 0 || endLbaExclusive <= firstLba || endLbaExclusive > actual.OutputSectors)
            throw new ArgumentOutOfRangeException(nameof(endLbaExclusive),
                "The drawing range must be a non-empty range of sectors inside the geometry.");
        byte[] sector = new byte[BytesPerSector];
        DataPreview preview = new(outputSize, actual);
        for (long lba = firstLba; lba < endLbaExclusive; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sectors.ReadExactly(sector);
            for (int offset = 0; offset < BytesPerSector; offset += byteStep)
            {
                long physicalByte = PhysicalByteForPayload(lba, offset);
                if (physicalByte < 0) continue;
                preview.Splat(actual, physicalByte, (byte)(sector[offset] ^ ScrambleMask[offset + PayloadOffset - 12]));
            }
        }
        return preview.CreatePixels();
    }

    private static void ValidatePreview(CdDiscParameters parameters, int size, int interval)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (size is < 64 or > 8192) throw new ArgumentOutOfRangeException(nameof(size), "Preview size must be 64..8192.");
        if (interval <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    private sealed class DataPreview(int size, CdDiscParameters actual)
    {
        private readonly long[] _sum = new long[checked(size * size)];
        private readonly int[] _count = new int[checked(size * size)];
        internal void Splat(CdDiscParameters parameters, long physicalByte, byte palette)
        {
            (double x, double y) = parameters.ImagePointFromByte(physicalByte, size);
            int px = (int)Math.Round(x), py = (int)Math.Round(y);
            if ((uint)px >= size || (uint)py >= size) return;
            int index = py * size + px;
            _sum[index] += CdToneMapping.DisplayGray(palette);
            _count[index]++;
        }
        internal byte[] CreatePixels()
        {
            byte[] pixels = new byte[checked(size * size * 4)];
            Array.Fill(pixels, (byte)255);
            for (int index = 0; index < _count.Length; index++)
            {
                if (_count[index] == 0) continue;
                byte gray = (byte)((_sum[index] + _count[index] / 2) / _count[index]);
                pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = gray;
            }
            CdPreviewMask.Apply(pixels, size, actual);
            return pixels;
        }
    }
}
