using System.Collections;
using System.Numerics;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Rosetta.Fonts;

namespace CdBurner.Lithography.Rosetta;

public readonly record struct RosettaCellReadback(
    int Row,
    int Column,
    char ExpectedChar,
    ulong ExpectedBits,
    ulong ObservedBits,
    ulong SampledMask)
{
    public int SampledPixelCount => BitOperations.PopCount(SampledMask);
    public int MismatchedSampledPixels => BitOperations.PopCount((ObservedBits ^ ExpectedBits) & SampledMask);
    public bool MatchesExpectedGlyph => MismatchedSampledPixels == 0 && SampledPixelCount >= 28;
}

public sealed record RosettaRungVerification(
    string RungId,
    string ZoneId,
    double CellWidthUm,
    double CellHeightUm,
    long LabelSampledSubpixels,
    int LabelUnsampledSubpixels,
    int LabelMismatchedSubpixels,
    long PageSampledSubpixels,
    int PageUnsampledSubpixels,
    int PageMismatchedSubpixels,
    int VerifiedGlyphCells,
    int MinSampledPixelsPerGlyphCell,
    int MismatchedGlyphCells,
    bool CrosshairsAndRuleMatch,
    IReadOnlyList<RosettaCellReadback> FailedCells)
{
    public bool AllGlyphBitmapsMatch =>
        LabelMismatchedSubpixels == 0
        && PageMismatchedSubpixels == 0
        && MismatchedGlyphCells == 0
        && CrosshairsAndRuleMatch
        && MinSampledPixelsPerGlyphCell >= 28;
}

public sealed record RosettaLadderVerificationReport(
    int TotalRungs,
    int PassedRungs,
    int InspectedEccBlocks,
    long ClassifiedPayloadBytes,
    IReadOnlyList<RosettaRungVerification> Rungs)
{
    public bool AllRungsMatch => PassedRungs == TotalRungs && TotalRungs > 0;
}

/// <summary>
/// Clean-room localized ECMA-267 DVD sector readback verifier for <see cref="RosettaLadderDisc"/>.
/// Unscrambles payload bytes in ECC blocks intersecting the 24 ladder rungs and coarse labels
/// and verifies every glyph bitmap against <see cref="Spleen5x8Font"/>.
/// </summary>
public static class RosettaLadderReadback
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const int BatchBlockLimit = 128;

    private static readonly int[] PayloadCenters = BuildPayloadCenters();
    private static readonly byte[] StandardToneTable = BuildToneTable(readCompatibility: false);
    private static readonly byte[] ReadCompatToneTable = BuildToneTable(readCompatibility: true);

    public static RosettaLadderVerificationReport VerifyIso(
        string isoPath,
        string? jsonManifestPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        string isoFullPath = Path.GetFullPath(isoPath);
        string manifestPath = string.IsNullOrWhiteSpace(jsonManifestPath)
            ? isoFullPath + ".json"
            : Path.GetFullPath(jsonManifestPath);

        RosettaLadderManifest manifest = RosettaLadderDisc.LoadManifestJson(manifestPath);
        using var fs = new FileStream(
            isoFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        return VerifyStream(fs, manifest, cancellationToken);
    }

    public static RosettaLadderVerificationReport VerifyStream(
        Stream seekableIsoStream,
        RosettaLadderManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seekableIsoStream);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!seekableIsoStream.CanRead || !seekableIsoStream.CanSeek)
        {
            throw new ArgumentException("ISO stream must be readable and seekable.", nameof(seekableIsoStream));
        }

        long requiredBytes = checked((long)manifest.FillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);
        if (seekableIsoStream.Length < requiredBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seekableIsoStream),
                seekableIsoStream.Length,
                $"ISO stream length ({seekableIsoStream.Length:N0}) is smaller than FillSectors ({requiredBytes:N0} bytes).");
        }

        var context = VerificationContext.Create(manifest);
        (BitArray activeBlocks, List<(uint StartBlock, int BlockCount)> runs) =
            RosettaLadderDisc.CollectActiveBlockRuns(context.Geometry!, manifest, maxGapBlocks: 64);

        int maxRunBlocks = Math.Min(BatchBlockLimit, runs.Count > 0 ? runs.Max(r => r.BlockCount) : 1);
        byte[] batchBuffer = new byte[checked(maxRunBlocks * DvdEccBlockEncoder.PayloadBytesPerBlock)];
        int inspectedBlocks = 0;

        for (int r = 0; r < runs.Count; r++)
        {
            (uint runStart, int runCount) = runs[r];
            int offset = 0;
            while (offset < runCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunkBlocks = Math.Min(BatchBlockLimit, runCount - offset);
                uint chunkStartBlock = runStart + (uint)offset;
                long byteOffset = checked((long)chunkStartBlock * DvdEccBlockEncoder.PayloadBytesPerBlock);
                int chunkBytes = checked(chunkBlocks * DvdEccBlockEncoder.PayloadBytesPerBlock);

                seekableIsoStream.Seek(byteOffset, SeekOrigin.Begin);
                seekableIsoStream.ReadExactly(batchBuffer.AsSpan(0, chunkBytes));

                ProcessBatch(context, activeBlocks, chunkStartBlock, chunkBlocks, batchBuffer, cancellationToken);
                for (int i = 0; i < chunkBlocks; i++)
                {
                    if (activeBlocks[(int)(chunkStartBlock + (uint)i)])
                    {
                        inspectedBlocks++;
                    }
                }
                offset += chunkBlocks;
            }
        }

        return BuildReport(context, manifest, inspectedBlocks);
    }

    /// <summary>
    /// Generates only the 16-sector ECC blocks intersecting the 24 rungs and 24 coarse labels
    /// via FastDispersionImageWriter and runs full readback verification in memory without writing 4.7 GB to disk.
    /// </summary>
    public static RosettaLadderVerificationReport VerifyInMemory(
        RosettaLadderLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        RosettaLadderManifest manifest = layout.Manifest;
        var context = VerificationContext.Create(manifest);
        (BitArray activeBlocks, List<(uint StartBlock, int BlockCount)> runs) =
            RosettaLadderDisc.CollectActiveBlockRuns(context.Geometry!, manifest, maxGapBlocks: 16);

        Func<DvdCodewordSample, byte> sampler = RosettaLadderDisc.CreateCodewordGrayscaleSampler(layout);
        var processorStream = new BlockBatchProcessorStream(context, activeBlocks, cancellationToken);

        for (int r = 0; r < runs.Count; r++)
        {
            (uint runStart, int runCount) = runs[r];
            cancellationToken.ThrowIfCancellationRequested();
            uint runStartLba = runStart * DvdEccBlockEncoder.SectorCount;
            uint runSectors = (uint)runCount * DvdEccBlockEncoder.SectorCount;

            MultiBlockSolveOptions solveOptions = RosettaLadderDisc.BuildSegmentSolveOptions(
                layout,
                runStartLba,
                runSectors);

            processorStream.Reset(runStart);
            FastDispersionImageWriter.WriteCodewordPatternStream(
                sampler,
                processorStream,
                solveOptions,
                cancellationToken: cancellationToken);
            processorStream.FlushBatch();
        }

        return BuildReport(context, manifest, processorStream.InspectedBlocks);
    }

    public static RosettaLadderVerificationReport VerifyCdTrack(
        string trackPath,
        string? jsonManifestPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackPath);
        string trackFullPath = Path.GetFullPath(trackPath);
        string manifestPath = string.IsNullOrWhiteSpace(jsonManifestPath)
            ? trackFullPath + ".json"
            : Path.GetFullPath(jsonManifestPath);

        RosettaLadderManifest manifest = RosettaLadderDisc.LoadManifestJson(manifestPath);
        using var fs = new FileStream(
            trackFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);

        Span<byte> signature = stackalloc byte[12];
        int signatureLength = fs.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        bool wave = Path.GetExtension(trackFullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || (signatureLength == 12 && signature[..4].SequenceEqual("RIFF"u8)
                && signature[8..].SequenceEqual("WAVE"u8));
        fs.Position = 0;

        long audioOffset = 0;
        long audioLength = fs.Length;
        if (wave)
        {
            (audioOffset, audioLength) = CdWaveFile.ReadAudioRange(fs);
        }

        return VerifyCdAudioRange(
            fs,
            audioOffset,
            audioLength,
            wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
            manifest,
            cancellationToken);
    }

    public static RosettaLadderVerificationReport VerifyCdStream(
        Stream seekableTrackStream,
        CdAudioByteOrder byteOrder,
        RosettaLadderManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seekableTrackStream);
        ArgumentNullException.ThrowIfNull(manifest);
        CdAudioSamples.Validate(byteOrder);
        if (!seekableTrackStream.CanRead || !seekableTrackStream.CanSeek)
        {
            throw new ArgumentException("Track stream must be readable and seekable.", nameof(seekableTrackStream));
        }

        return VerifyCdAudioRange(seekableTrackStream, 0, seekableTrackStream.Length, byteOrder, manifest, cancellationToken);
    }

    private static RosettaLadderVerificationReport VerifyCdAudioRange(
        Stream input,
        long audioOffset,
        long audioLength,
        CdAudioByteOrder byteOrder,
        RosettaLadderManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(manifest.Media, "CD-R", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected a CD-R ladder manifest (got '{manifest.Media}').", nameof(manifest));
        }

        CdDiscParameters cdParams = RosettaLadderDisc.BuildCdParameters(manifest);
        if (audioLength < cdParams.TotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                audioLength,
                $"CD track audio length ({audioLength:N0}) is smaller than FillSectors ({cdParams.TotalBytes:N0} bytes).");
        }

        var context = VerificationContext.CreateForCd(manifest);
        const int SectorsPerWindow = 448;
        const int WindowBytes = SectorsPerWindow * CdDiscParameters.BytesPerSector;
        int lookAhead = manifest.CdInterleave ? CddaInterleaveTable.MaximumFileOffset : 0;
        byte[] buffer = new byte[WindowBytes + lookAhead];
        bool littleEndian = byteOrder == CdAudioByteOrder.LittleEndian;
        int inspectedSectors = 0;
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

        for (int z = 0; z < manifest.Zones.Count; z++)
        {
            RosettaLadderZone zone = manifest.Zones[z];
            uint sectorCursor = zone.StartLba;
            while (sectorCursor < zone.EndLba)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunkSectors = (int)Math.Min(SectorsPerWindow, zone.EndLba - sectorCursor);
                long windowStart = (long)sectorCursor * CdDiscParameters.BytesPerSector;
                long remaining = audioLength - windowStart;
                int wanted = (int)Math.Min((long)(chunkSectors * CdDiscParameters.BytesPerSector) + lookAhead, Math.Max(0, remaining));
                if (wanted <= 0)
                {
                    break;
                }

                input.Position = audioOffset + windowStart;
                int bytesRead = input.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
                bytesRead &= ~1;
                if (littleEndian)
                {
                    CdAudioSamples.SwapByteOrder(buffer.AsSpan(0, bytesRead));
                }

                int zoneIndex = z;
                long baseSector = sectorCursor;
                Parallel.For(0, chunkSectors, parallelOptions, sectorOffset =>
                {
                    long sectorByte = (baseSector + sectorOffset) * CdDiscParameters.BytesPerSector;
                    long localClassified = 0;
                    RungAccumulator? lastAcc = null;
                    bool lastIsPage = false;
                    int lastSubIdx = -1;
                    int runSamples = 0;
                    int runDark = 0;

                    for (int b = 0; b < CdDiscParameters.BytesPerSector; b++)
                    {
                        long logical = sectorByte + b;
                        long local = manifest.CdInterleave
                            ? logical + CddaInterleaveTable.FileOffsetFor((int)(logical % CddaInterleaveTable.BytesPerFrame)) - windowStart
                            : logical - windowStart;
                        if ((ulong)local >= (ulong)bytesRead)
                        {
                            continue;
                        }

                        byte palette = buffer[local];
                        byte tone = palette == 0x0D ? (byte)2 : palette == 0xAA ? (byte)1 : (byte)0;
                        if (tone == 0)
                        {
                            continue;
                        }

                        localClassified++;
                        (double rMm, double theta) = cdParams.PolarFromByte(logical);
                        if (rMm < zone.InnerRadiusMm || rMm >= zone.OuterRadiusMm)
                        {
                            continue;
                        }

                        double cwRad = theta - (TwoPi * Math.Floor(theta / TwoPi));
                        if (cwRad < 0.0 || cwRad >= TwoPi)
                        {
                            cwRad = 0.0;
                        }

                        double relRad = cwRad - context.StartAngleRad;
                        if (relRad < 0.0)
                        {
                            relRad += TwoPi;
                        }
                        int k = (int)(relRad * context.InvSlotAngleRad);
                        if ((uint)k >= (uint)context.SlotCount)
                        {
                            k = context.SlotCount - 1;
                        }

                        RungAccumulator acc = context.Accumulators[(zoneIndex * context.SlotCount) + k];
                        AccumulateSample(
                            acc,
                            rMm,
                            cwRad,
                            tone,
                            ref lastAcc,
                            ref lastIsPage,
                            ref lastSubIdx,
                            ref runSamples,
                            ref runDark);
                    }

                    FlushSampleRun(lastAcc, lastIsPage, lastSubIdx, runSamples, runDark);
                    if (localClassified > 0)
                    {
                        Interlocked.Add(ref context.ClassifiedPayloadBytes, localClassified);
                    }
                });

                inspectedSectors += chunkSectors;
                sectorCursor += (uint)chunkSectors;
            }
        }

        return BuildReport(context, manifest, inspectedSectors);
    }

    private static void AccumulateSample(
        RungAccumulator acc,
        double rMm,
        double cwRad,
        byte tone,
        ref RungAccumulator? lastAcc,
        ref bool lastIsPage,
        ref int lastSubIdx,
        ref int runSamples,
        ref int runDark)
    {
        if (rMm < acc.MinInnerMm || rMm >= acc.MaxOuterMm)
        {
            return;
        }

        bool isPage = false;
        int subIdx = -1;
        bool matched = false;
        if (rMm >= acc.LabelInnerMm && rMm < acc.LabelOuterMm)
        {
            double delta = cwRad - acc.LabelStartRad;
            if (delta < 0.0) delta += TwoPi;
            if (delta < acc.LabelSpanRad)
            {
                int lx = (int)(delta * acc.LabelUGridScale);
                if (lx >= acc.LabelGridW) lx = acc.LabelGridW - 1;
                double yMm = acc.LabelHeightMm - (rMm - acc.LabelInnerMm);
                int ly = yMm <= 0.0 ? 0 : (int)(yMm * acc.LabelYGridScale);
                if (ly >= acc.LabelGridH) ly = acc.LabelGridH - 1;
                isPage = false;
                subIdx = (ly * acc.LabelGridW) + lx;
                matched = true;
            }
        }

        if (!matched && rMm >= acc.PageInnerMm && rMm < acc.PageOuterMm)
        {
            double delta = cwRad - acc.PageStartRad;
            if (delta < 0.0) delta += TwoPi;
            if (delta < acc.PageSpanRad)
            {
                int px = (int)(delta * acc.PageUGridScale);
                if (px >= acc.PageGridW) px = acc.PageGridW - 1;
                double yMm = acc.PageHeightMm - (rMm - acc.PageInnerMm);
                int py = yMm <= 0.0 ? 0 : (int)(yMm * acc.PageYGridScale);
                if (py >= acc.PageGridH) py = acc.PageGridH - 1;
                isPage = true;
                subIdx = (py * acc.PageGridW) + px;
                matched = true;
            }
        }

        if (!matched)
        {
            return;
        }

        if (ReferenceEquals(acc, lastAcc) && isPage == lastIsPage && subIdx == lastSubIdx)
        {
            runSamples++;
            if (tone == 2)
            {
                runDark++;
            }
        }
        else
        {
            FlushSampleRun(lastAcc, lastIsPage, lastSubIdx, runSamples, runDark);
            lastAcc = acc;
            lastIsPage = isPage;
            lastSubIdx = subIdx;
            runSamples = 1;
            runDark = tone == 2 ? 1 : 0;
        }
    }

    private static void FlushSampleRun(
        RungAccumulator? lastAcc,
        bool lastIsPage,
        int lastSubIdx,
        int runSamples,
        int runDark)
    {
        if (lastAcc is null)
        {
            return;
        }
        int[] sArr = lastIsPage ? lastAcc.PageSamples : lastAcc.LabelSamples;
        Interlocked.Add(ref sArr[lastSubIdx], runSamples);
        if (runDark > 0)
        {
            int[] dArr = lastIsPage ? lastAcc.PageDark : lastAcc.LabelDark;
            Interlocked.Add(ref dArr[lastSubIdx], runDark);
        }
    }

    private static bool SectorMayHit(
        RungAccumulator acc,
        double rStart,
        double rEnd,
        double cw0,
        double unwrappedSpan)
    {
        if (rEnd < acc.MinInnerMm || rStart >= acc.MaxOuterMm)
        {
            return false;
        }
        double d0 = cw0 - acc.BoxMinAngleRad;
        if (d0 < 0.0)
        {
            d0 += TwoPi;
        }
        return d0 <= acc.BoxTotalSpanRad || (d0 + unwrappedSpan) >= TwoPi;
    }

    private static void ProcessBatch(
        VerificationContext context,
        BitArray activeBlocks,
        uint batchStartBlock,
        int blockCount,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, blockCount, parallelOptions, blockOffset =>
        {
            uint blockIndex = batchStartBlock + (uint)blockOffset;
            if ((int)blockIndex < activeBlocks.Count && !activeBlocks[(int)blockIndex])
            {
                return;
            }

            uint blockStartLba = blockIndex * DvdEccBlockEncoder.SectorCount;
            long blockBaseBit = (long)blockStartLba * DvdEccBlockEncoder.ChannelBitsPerSector;
            int blockByteBase = blockOffset * DvdEccBlockEncoder.PayloadBytesPerBlock;
            long localClassified = 0;

            for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
            {
                int sectorCenterBase = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
                int sectorFirstBit = PayloadCenters[sectorCenterBase];
                int sectorMidIdx = DvdEccBlockEncoder.PayloadBytesPerSector / 2;
                int sectorMidBit = PayloadCenters[sectorCenterBase + sectorMidIdx];
                int sectorLastBit = PayloadCenters[sectorCenterBase + DvdEccBlockEncoder.PayloadBytesPerSector - 1];
                (double rStart, double aStart) = context.Geometry!.AtArcLengthFast(
                    (blockBaseBit + sectorFirstBit) * context.BitLengthMm);
                (double rMid, double aMid) = context.Geometry!.AtArcLengthFast(
                    (blockBaseBit + sectorMidBit) * context.BitLengthMm);
                (double rEnd, double aEnd) = context.Geometry!.AtArcLengthFast(
                    (blockBaseBit + sectorLastBit) * context.BitLengthMm);

                int zone0 = context.FindZoneIndex(rStart);
                int zone1 = context.FindZoneIndex(rEnd);
                if (zone0 < 0 && zone1 < 0)
                {
                    continue;
                }

                double cw0 = aStart - (TwoPi * Math.Floor(aStart / TwoPi));
                if (cw0 < 0.0 || cw0 >= TwoPi)
                {
                    cw0 = 0.0;
                }
                double rel0 = cw0 - context.StartAngleRad;
                if (rel0 < 0.0)
                {
                    rel0 += TwoPi;
                }
                int k0 = (int)(rel0 * context.InvSlotAngleRad);
                if ((uint)k0 >= (uint)context.SlotCount)
                {
                    k0 = context.SlotCount - 1;
                }

                double cw1 = aEnd - (TwoPi * Math.Floor(aEnd / TwoPi));
                if (cw1 < 0.0 || cw1 >= TwoPi)
                {
                    cw1 = 0.0;
                }
                double rel1 = cw1 - context.StartAngleRad;
                if (rel1 < 0.0)
                {
                    rel1 += TwoPi;
                }
                int k1 = (int)(rel1 * context.InvSlotAngleRad);
                if ((uint)k1 >= (uint)context.SlotCount)
                {
                    k1 = context.SlotCount - 1;
                }

                double unwrappedSpan = aEnd - aStart;
                bool sectorIntersectsRung = false;
                RungAccumulator? singleAcc = null;
                if (zone0 == zone1 && zone0 >= 0 && k0 == k1)
                {
                    RungAccumulator cand = context.Accumulators[(zone0 * context.SlotCount) + k0];
                    if (SectorMayHit(cand, rStart, rEnd, cw0, unwrappedSpan))
                    {
                        sectorIntersectsRung = true;
                        singleAcc = cand;
                    }
                }
                else
                {
                    if (zone0 >= 0)
                    {
                        if (SectorMayHit(context.Accumulators[(zone0 * context.SlotCount) + k0], rStart, rEnd, cw0, unwrappedSpan)
                            || (k1 != k0 && SectorMayHit(context.Accumulators[(zone0 * context.SlotCount) + k1], rStart, rEnd, cw0, unwrappedSpan)))
                        {
                            sectorIntersectsRung = true;
                        }
                    }
                    if (!sectorIntersectsRung && zone1 >= 0 && zone1 != zone0)
                    {
                        if (SectorMayHit(context.Accumulators[(zone1 * context.SlotCount) + k0], rStart, rEnd, cw0, unwrappedSpan)
                            || (k1 != k0 && SectorMayHit(context.Accumulators[(zone1 * context.SlotCount) + k1], rStart, rEnd, cw0, unwrappedSpan)))
                        {
                            sectorIntersectsRung = true;
                        }
                    }
                }

                if (!sectorIntersectsRung)
                {
                    continue;
                }

                // 2-segment piecewise linear interpolation across the 5.16 mm sector (< 0.15 nm curvature error).
                double invSpan0 = 1.0 / (sectorMidBit - sectorFirstBit);
                double dr0 = (rMid - rStart) * invSpan0;
                double da0 = (aMid - aStart) * invSpan0;
                double invSpan1 = 1.0 / (sectorLastBit - sectorMidBit);
                double dr1 = (rEnd - rMid) * invSpan1;
                double da1 = (aEnd - aMid) * invSpan1;

                uint psn = blockStartLba + context.PsnOffset + (uint)sector;
                ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(psn);
                int sectorOffset = blockByteBase + sectorCenterBase;
                byte[] toneTable = context.ReadCompatibility ? ReadCompatToneTable : StandardToneTable;
                bool noWrapInSector = aStart >= 0.0 && Math.Floor(aStart / TwoPi) == Math.Floor(aEnd / TwoPi);
                double wrapOffset = noWrapInSector ? TwoPi * Math.Floor(aStart / TwoPi) : 0.0;

                RungAccumulator? lastAcc = null;
                bool lastIsPage = false;
                int lastSubIdx = -1;
                int runSamples = 0;
                int runDark = 0;

                for (int byteIndex = 0; byteIndex < DvdEccBlockEncoder.PayloadBytesPerSector; byteIndex++)
                {
                    byte tone = toneTable[buffer[sectorOffset + byteIndex] ^ mask[byteIndex]];
                    if (tone == 0)
                    {
                        continue;
                    }

                    localClassified++;

                    int centerBit = PayloadCenters[sectorCenterBase + byteIndex];
                    double rMm;
                    double trackAngle;
                    if (byteIndex < sectorMidIdx)
                    {
                        int dBit = centerBit - sectorFirstBit;
                        rMm = rStart + (dBit * dr0);
                        trackAngle = aStart + (dBit * da0);
                    }
                    else
                    {
                        int dBit = centerBit - sectorMidBit;
                        rMm = rMid + (dBit * dr1);
                        trackAngle = aMid + (dBit * da1);
                    }

                    double cwRad;
                    if (noWrapInSector)
                    {
                        cwRad = trackAngle - wrapOffset;
                        if (cwRad >= TwoPi) cwRad = 0.0;
                    }
                    else
                    {
                        cwRad = trackAngle - (TwoPi * Math.Floor(trackAngle / TwoPi));
                        if (cwRad < 0.0 || cwRad >= TwoPi) cwRad = 0.0;
                    }

                    RungAccumulator acc;
                    if (singleAcc is not null)
                    {
                        acc = singleAcc;
                    }
                    else
                    {
                        int zoneIndex = context.FindZoneIndex(rMm);
                        if (zoneIndex < 0)
                        {
                            continue;
                        }

                        double relRad = cwRad - context.StartAngleRad;
                        if (relRad < 0.0)
                        {
                            relRad += TwoPi;
                        }
                        int k = (int)(relRad * context.InvSlotAngleRad);
                        if ((uint)k >= (uint)context.SlotCount)
                        {
                            k = context.SlotCount - 1;
                        }

                        acc = context.Accumulators[(zoneIndex * context.SlotCount) + k];
                    }

                    AccumulateSample(
                        acc,
                        rMm,
                        cwRad,
                        tone,
                        ref lastAcc,
                        ref lastIsPage,
                        ref lastSubIdx,
                        ref runSamples,
                        ref runDark);
                }

                FlushSampleRun(lastAcc, lastIsPage, lastSubIdx, runSamples, runDark);
            }

            if (localClassified > 0)
            {
                Interlocked.Add(ref context.ClassifiedPayloadBytes, localClassified);
            }
        });
    }

    private sealed class BlockBatchProcessorStream : Stream
    {
        private readonly VerificationContext _context;
        private readonly BitArray _activeBlocks;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _buffer = new byte[BatchBlockLimit * DvdEccBlockEncoder.PayloadBytesPerBlock];
        private int _bufferedBytes;
        private uint _batchStartBlock;

        public BlockBatchProcessorStream(
            VerificationContext context,
            BitArray activeBlocks,
            CancellationToken cancellationToken)
        {
            _context = context;
            _activeBlocks = activeBlocks;
            _cancellationToken = cancellationToken;
        }

        public int InspectedBlocks { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public void Reset(uint runStartBlock)
        {
            _batchStartBlock = runStartBlock;
            _bufferedBytes = 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int pos = 0;
            while (pos < buffer.Length)
            {
                int space = _buffer.Length - _bufferedBytes;
                int take = Math.Min(space, buffer.Length - pos);
                buffer.Slice(pos, take).CopyTo(_buffer.AsSpan(_bufferedBytes, take));
                _bufferedBytes += take;
                pos += take;

                if (_bufferedBytes == _buffer.Length)
                {
                    FlushBatch();
                }
            }
        }

        public void FlushBatch()
        {
            if (_bufferedBytes == 0)
            {
                return;
            }
            int blocks = _bufferedBytes / DvdEccBlockEncoder.PayloadBytesPerBlock;
            if (blocks > 0)
            {
                ProcessBatch(_context, _activeBlocks, _batchStartBlock, blocks, _buffer, _cancellationToken);
                _batchStartBlock += (uint)blocks;
                for (int i = 0; i < blocks; i++)
                {
                    int blkIdx = (int)(_batchStartBlock - (uint)blocks) + i;
                    if (blkIdx < _activeBlocks.Count && _activeBlocks[blkIdx])
                    {
                        InspectedBlocks++;
                    }
                }
            }
            _bufferedBytes = 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static RosettaLadderVerificationReport BuildReport(
        VerificationContext context,
        RosettaLadderManifest manifest,
        int inspectedBlocks)
    {
        Spleen5x8Font font = Spleen5x8Font.BuiltIn;
        IReadOnlyList<string> lines = manifest.TestTextLines;
        var results = new RosettaRungVerification[context.Accumulators.Length];
        int passedRungs = 0;

        for (int i = 0; i < context.Accumulators.Length; i++)
        {
            RungAccumulator acc = context.Accumulators[i];
            RosettaLadderRung rung = acc.Rung;

            long labelSampled = 0;
            int labelUnsampled = 0;
            int labelMismatched = 0;
            for (int gy = 0; gy < acc.LabelGridH; gy++)
            {
                for (int gx = 0; gx < acc.LabelGridW; gx++)
                {
                    int idx = (gy * acc.LabelGridW) + gx;
                    int s = acc.LabelSamples[idx];
                    if (s == 0)
                    {
                        labelUnsampled++;
                        continue;
                    }
                    labelSampled++;
                    bool observedInk = (acc.LabelDark[idx] * 2) >= s;
                    bool expectedInk = RosettaLadderDisc.IsExpectedLabelSubpixelInk(rung, gx, gy, font);
                    if (observedInk != expectedInk)
                    {
                        labelMismatched++;
                    }
                }
            }

            long pageSampled = 0;
            int pageUnsampled = 0;
            int pageMismatched = 0;
            for (int gy = 0; gy < acc.PageGridH; gy++)
            {
                for (int gx = 0; gx < acc.PageGridW; gx++)
                {
                    int idx = (gy * acc.PageGridW) + gx;
                    int s = acc.PageSamples[idx];
                    if (s == 0)
                    {
                        pageUnsampled++;
                        continue;
                    }
                    pageSampled++;
                    bool observedInk = (acc.PageDark[idx] * 2) >= s;
                    bool expectedInk = RosettaLadderDisc.IsExpectedPageSubpixelInk(rung, lines, gx, gy, font);
                    if (observedInk != expectedInk)
                    {
                        pageMismatched++;
                    }
                }
            }

            bool crosshairsAndRuleMatch = CheckCrosshairsAndRule(acc, rung);

            int verifiedCells = 0;
            int minSampled = int.MaxValue;
            int mismatchedCells = 0;
            var failedCells = new List<RosettaCellReadback>();

            // Evaluate Label cells (2 x 10).
            for (int row = 0; row < rung.Label.Rows && row < rung.Label.Lines.Count; row++)
            {
                string line = rung.Label.Lines[row];
                for (int col = 0; col < rung.Label.Columns && col < line.Length; col++)
                {
                    char ch = line[col];
                    if (ch == ' ')
                    {
                        continue;
                    }
                    EvaluateCell(
                        -1 - row,
                        col,
                        ch,
                        col * Spleen5x8Font.CellPixelWidth,
                        row * Spleen5x8Font.CellPixelHeight,
                        acc.LabelSamples,
                        acc.LabelDark,
                        acc.LabelGridW);
                }
            }

            // Evaluate Page header cells (row 0, cols 4..Columns-1).
            for (int hdrIdx = 0; hdrIdx < rung.HeaderText.Length && (4 + hdrIdx) < rung.Columns; hdrIdx++)
            {
                char ch = rung.HeaderText[hdrIdx];
                if (ch == ' ')
                {
                    continue;
                }
                int col = 4 + hdrIdx;
                EvaluateCell(
                    0,
                    col,
                    ch,
                    col * Spleen5x8Font.CellPixelWidth,
                    0,
                    acc.PageSamples,
                    acc.PageDark,
                    acc.PageGridW);
            }

            // Evaluate Page body lines (bodyRow 0..lines.Count-1).
            for (int bodyRow = 0; bodyRow < lines.Count && bodyRow < rung.Rows - 1; bodyRow++)
            {
                string line = lines[bodyRow];
                int startCol = bodyRow < 2 ? 4 : 0;
                int originGy = Spleen5x8Font.CellPixelHeight + 3 + (bodyRow * Spleen5x8Font.CellPixelHeight);
                for (int j = 0; j < line.Length; j++)
                {
                    int col = startCol + j;
                    if (col >= rung.Columns)
                    {
                        break;
                    }
                    char ch = line[j];
                    if (ch == ' ')
                    {
                        continue;
                    }
                    EvaluateCell(
                        bodyRow + 1,
                        col,
                        ch,
                        col * Spleen5x8Font.CellPixelWidth,
                        originGy,
                        acc.PageSamples,
                        acc.PageDark,
                        acc.PageGridW);
                }
            }

            if (verifiedCells == 0)
            {
                minSampled = 0;
            }

            var rungResult = new RosettaRungVerification(
                rung.RungId,
                rung.ZoneId,
                rung.CellWidthUm,
                rung.CellHeightUm,
                labelSampled,
                labelUnsampled,
                labelMismatched,
                pageSampled,
                pageUnsampled,
                pageMismatched,
                verifiedCells,
                minSampled,
                mismatchedCells,
                crosshairsAndRuleMatch,
                failedCells);

            if (rungResult.AllGlyphBitmapsMatch)
            {
                passedRungs++;
            }
            results[i] = rungResult;

            void EvaluateCell(int row, int col, char ch, int originGx, int originGy, int[] samples, int[] dark, int gridW)
            {
                ulong expectedBits = font.GetPackedGlyph(ch);
                ulong observedBits = 0;
                ulong sampledMask = 0;

                for (int py = 0; py < Spleen5x8Font.CellPixelHeight; py++)
                {
                    int rowBase = ((originGy + py) * gridW) + originGx;
                    for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
                    {
                        int s = samples[rowBase + px];
                        if (s <= 0)
                        {
                            continue;
                        }
                        int bit = (py * 8) + px;
                        sampledMask |= 1UL << bit;
                        if ((dark[rowBase + px] * 2) >= s)
                        {
                            observedBits |= 1UL << bit;
                        }
                    }
                }

                var cell = new RosettaCellReadback(row, col, ch, expectedBits, observedBits, sampledMask);
                verifiedCells++;
                if (cell.SampledPixelCount < minSampled)
                {
                    minSampled = cell.SampledPixelCount;
                }
                if (!cell.MatchesExpectedGlyph)
                {
                    mismatchedCells++;
                    failedCells.Add(cell);
                }
            }
        }

        return new RosettaLadderVerificationReport(
            results.Length,
            passedRungs,
            inspectedBlocks,
            context.ClassifiedPayloadBytes,
            results);
    }

    private static bool CheckCrosshairsAndRule(RungAccumulator acc, RosettaLadderRung rung)
    {
        if (!CheckCornerCrosshair(acc, 0, 0)
            || !CheckCornerCrosshair(acc, acc.PageGridW - 15, acc.PageGridH - 24))
        {
            return false;
        }

        int ruleGy = Spleen5x8Font.CellPixelHeight + 1; // gy = 9
        int sampledCols = 0;
        for (int col = 4; col < rung.Columns; col++)
        {
            int gx0 = col * Spleen5x8Font.CellPixelWidth;
            bool colSampled = false;
            for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
            {
                int idx = (ruleGy * acc.PageGridW) + gx0 + px;
                int s = acc.PageSamples[idx];
                if (s > 0)
                {
                    colSampled = true;
                    if ((acc.PageDark[idx] * 2) < s)
                    {
                        return false;
                    }
                }
            }
            if (colSampled)
            {
                sampledCols++;
            }
        }

        return sampledCols >= Math.Min(20, rung.Columns - 4);
    }

    private static bool CheckCornerCrosshair(RungAccumulator acc, int originGx, int originGy)
    {
        for (int cy = 0; cy < 3; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                int cellSamples = 0;
                int cellDark = 0;
                int gx0 = originGx + (cx * Spleen5x8Font.CellPixelWidth);
                int gy0 = originGy + (cy * Spleen5x8Font.CellPixelHeight);

                for (int py = 0; py < Spleen5x8Font.CellPixelHeight; py++)
                {
                    int rowBase = ((gy0 + py) * acc.PageGridW) + gx0;
                    for (int px = 0; px < Spleen5x8Font.CellPixelWidth; px++)
                    {
                        cellSamples += acc.PageSamples[rowBase + px];
                        cellDark += acc.PageDark[rowBase + px];
                    }
                }

                if (cellSamples == 0)
                {
                    return false;
                }
                bool observedDark = (cellDark * 2) >= cellSamples;
                bool expectedDark = cx == 1 || cy == 1;
                if (observedDark != expectedDark)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static int[] BuildPayloadCenters()
    {
        int[] centers = new int[DvdEccBlockEncoder.PayloadBytesPerBlock];
        for (int i = 0; i < centers.Length; i++)
        {
            centers[i] = checked(DvdEccBlockEncoder.PayloadDirectChannelOffset(i) + 8);
        }
        return centers;
    }

    private static byte[] BuildToneTable(bool readCompatibility)
    {
        byte[] table = new byte[256];
        for (int b = 0; b < 256; b++)
        {
            if (readCompatibility && b == 0x9B)
            {
                table[b] = 1;
            }
            else if (DvdGrayscalePalette.TryGetLuminance((byte)b, out byte level))
            {
                table[b] = level < 128 ? (byte)2 : (byte)1;
            }
        }
        return table;
    }

    private sealed class RungAccumulator
    {
        public readonly double LabelStartRad;
        public readonly double LabelSpanRad;
        public readonly double LabelUGridScale;
        public readonly double LabelHeightMm;
        public readonly double LabelYGridScale;
        public readonly double PageStartRad;
        public readonly double PageSpanRad;
        public readonly double PageUGridScale;
        public readonly double PageHeightMm;
        public readonly double PageYGridScale;

        public RungAccumulator(RosettaLadderRung rung)
        {
            Rung = rung;
            RosettaPolarBox label = rung.Label.Bounds;
            RosettaPolarBox page = rung.Page;
            MinInnerMm = Math.Min(label.InnerRadiusMm, page.InnerRadiusMm);
            MaxOuterMm = page.OuterRadiusMm;
            BoxMinAngleRad = label.StartAngleRadians;
            double endOffset = page.StartAngleRadians - BoxMinAngleRad;
            if (endOffset < 0.0)
            {
                endOffset += TwoPi;
            }
            BoxTotalSpanRad = endOffset + page.AngularSpanRadians;

            LabelGridW = RosettaLadderDisc.LabelColumns * Spleen5x8Font.CellPixelWidth;
            LabelGridH = RosettaLadderDisc.LabelRows * Spleen5x8Font.CellPixelHeight;
            PageGridW = rung.Columns * Spleen5x8Font.CellPixelWidth;
            PageGridH = (rung.Rows * Spleen5x8Font.CellPixelHeight) + 3;

            LabelInnerMm = label.InnerRadiusMm;
            LabelOuterMm = label.OuterRadiusMm;
            LabelStartRad = label.StartAngleRadians;
            LabelSpanRad = label.AngularSpanRadians;
            LabelUGridScale = (label.MidRadiusMm / label.WidthMm) * LabelGridW;
            LabelHeightMm = label.HeightMm;
            LabelYGridScale = LabelGridH / label.HeightMm;

            PageInnerMm = page.InnerRadiusMm;
            PageOuterMm = page.OuterRadiusMm;
            PageStartRad = page.StartAngleRadians;
            PageSpanRad = page.AngularSpanRadians;
            PageUGridScale = (page.MidRadiusMm / page.WidthMm) * PageGridW;
            PageHeightMm = page.HeightMm;
            PageYGridScale = PageGridH / page.HeightMm;

            LabelSamples = new int[LabelGridW * LabelGridH];
            LabelDark = new int[LabelGridW * LabelGridH];
            PageSamples = new int[PageGridW * PageGridH];
            PageDark = new int[PageGridW * PageGridH];
        }

        public RosettaLadderRung Rung { get; }
        public double MinInnerMm { get; }
        public double MaxOuterMm { get; }
        public double LabelInnerMm { get; }
        public double LabelOuterMm { get; }
        public double PageInnerMm { get; }
        public double PageOuterMm { get; }
        public double BoxMinAngleRad { get; }
        public double BoxTotalSpanRad { get; }
        public int LabelGridW { get; }
        public int LabelGridH { get; }
        public int PageGridW { get; }
        public int PageGridH { get; }
        public int[] LabelSamples { get; }
        public int[] LabelDark { get; }
        public int[] PageSamples { get; }
        public int[] PageDark { get; }
    }

    private sealed class VerificationContext
    {
        public long ClassifiedPayloadBytes;

        private VerificationContext(
            DvdTrackGeometry? geometry,
            double bitLengthMm,
            uint psnOffset,
            bool readCompatibility,
            double startAngleRad,
            int slotCount,
            IReadOnlyList<RosettaLadderZone> zones,
            RungAccumulator[] accumulators)
        {
            Geometry = geometry;
            BitLengthMm = bitLengthMm;
            PsnOffset = psnOffset;
            ReadCompatibility = readCompatibility;
            StartAngleRad = startAngleRad;
            SlotCount = slotCount;
            InvSlotAngleRad = slotCount / TwoPi;
            Zones = zones;
            Accumulators = accumulators;
        }

        public DvdTrackGeometry? Geometry { get; }
        public double BitLengthMm { get; }
        public uint PsnOffset { get; }
        public bool ReadCompatibility { get; }
        public double StartAngleRad { get; }
        public int SlotCount { get; }
        public double InvSlotAngleRad { get; }
        public IReadOnlyList<RosettaLadderZone> Zones { get; }
        public RungAccumulator[] Accumulators { get; }

        public int FindZoneIndex(double radiusMm)
        {
            for (int i = 0; i < Zones.Count; i++)
            {
                if (radiusMm >= Zones[i].InnerRadiusMm && radiusMm < Zones[i].OuterRadiusMm)
                {
                    return i;
                }
            }
            return -1;
        }

        public static VerificationContext Create(RosettaLadderManifest manifest)
        {
            double bitLengthMm = manifest.ChannelBitLengthNm * 1.0e-6;
            double trackLengthMm = manifest.TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector * bitLengthMm;
            DvdTrackGeometry geom = DvdTrackGeometry.Create(
                manifest.InnerRadiusMm,
                manifest.OuterRadiusMm,
                trackLengthMm,
                manifest.PitchLinear,
                manifest.PitchQuadratic,
                manifest.PitchCubic);

            bool readCompat = DvdReadCompatibilityModes.IsSupported(manifest.ReadCompatibility);
            double startRad = (manifest.StartAngleDegrees % 360.0) * DegreesToRadians;
            if (startRad < 0.0)
            {
                startRad += TwoPi;
            }

            int zoneCount = Math.Max(1, manifest.Zones.Count);
            int slotCount = Math.Max(1, manifest.Rungs.Count / zoneCount);
            var accumulators = new RungAccumulator[manifest.Rungs.Count];
            for (int i = 0; i < manifest.Rungs.Count; i++)
            {
                accumulators[i] = new RungAccumulator(manifest.Rungs[i]);
            }

            return new VerificationContext(
                geom,
                bitLengthMm,
                manifest.PsnOffset,
                readCompat,
                startRad,
                slotCount,
                manifest.Zones,
                accumulators);
        }

        public static VerificationContext CreateForCd(RosettaLadderManifest manifest)
        {
            double startRad = (manifest.StartAngleDegrees % 360.0) * DegreesToRadians;
            if (startRad < 0.0)
            {
                startRad += TwoPi;
            }

            int zoneCount = Math.Max(1, manifest.Zones.Count);
            int slotCount = Math.Max(1, manifest.Rungs.Count / zoneCount);
            var accumulators = new RungAccumulator[manifest.Rungs.Count];
            for (int i = 0; i < manifest.Rungs.Count; i++)
            {
                accumulators[i] = new RungAccumulator(manifest.Rungs[i]);
            }

            return new VerificationContext(
                null,
                0.0,
                0,
                false,
                startRad,
                slotCount,
                manifest.Zones,
                accumulators);
        }
    }
}
