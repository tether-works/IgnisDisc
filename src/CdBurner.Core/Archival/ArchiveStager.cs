using System.Security.Cryptography;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>
/// Stages archival disc files into structured staging directories with manifests and PAR2 recovery data.
/// </summary>
public static class ArchiveStager
{
    /// <summary>
    /// Stages an archive plan onto local disk, creating disc01, disc02, etc. directories.
    /// </summary>
    /// <param name="plan">The archive plan describing disc layouts.</param>
    /// <param name="stagingRoot">Destination root folder for staging.</param>
    /// <param name="par2Path">Optional path to par2cmdline executable. Required if plan.RedundancyPercent > 0.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of staged disc directory paths.</returns>
    /// <exception cref="InvalidOperationException">Thrown if redundancy is requested but par2Path is null.</exception>
    /// <exception cref="IOException">Thrown if any target disc directory already exists and is non-empty.</exception>
    public static async Task<IReadOnlyList<string>> StageAsync(
        ArchivePlan plan,
        string stagingRoot,
        string? par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staged = await StageDiscsAsync(plan, stagingRoot, par2Path, progress, cancellationToken).ConfigureAwait(false);
        return staged.Select(d => d.DiscRoot).ToList();
    }

    /// <summary>
    /// Stages the plan like <see cref="StageAsync"/> and reports, per disc, the recovery settings used and the
    /// sectors taken. With <see cref="ArchivePlan.FillFreeSpace"/>, each disc's recovery data is sized to fill it up
    /// to <see cref="ArchivePlan.EffectiveCeilingSectors"/>; without it, recovery data is sized to the requested
    /// redundancy percentage while still using 2,048-byte-aligned blocks, one recovery volume, and the ceiling check.
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveStagedDisc>> StageDiscsAsync(
        ArchivePlan plan,
        string stagingRoot,
        string? par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        if (plan.RedundancyPercent > 0 && par2Path == null)
        {
            throw new InvalidOperationException(
                "PAR2 recovery data requires par2cmdline (par2.exe). Install it or use redundancy 0.");
        }

        if (plan.ParityDiscCount > 0 && plan.Discs.Count + plan.ParityDiscCount > CrossDiscReedSolomon.MaxTotalDiscs)
        {
            throw new InvalidOperationException(
                $"Cross-disc Reed-Solomon parity supports at most {CrossDiscReedSolomon.MaxTotalDiscs} total discs ({plan.Discs.Count} data + {plan.ParityDiscCount} parity).");
        }

        foreach (var disc in plan.Discs)
        {
            string discFolderName = $"disc{disc.DiscNumber:D2}";
            string discDir = Path.Combine(stagingRoot, discFolderName);
            if (Directory.Exists(discDir) && Directory.EnumerateFileSystemEntries(discDir).Any())
            {
                throw new IOException($"Target staging directory '{discDir}' already exists and is non-empty.");
            }
        }

        if (plan.ParityDiscCount > 0 && plan.Discs.Count > 0)
        {
            for (int p = 0; p < plan.ParityDiscCount; p++)
            {
                int discNum = plan.Discs.Count + 1 + p;
                string discDir = Path.Combine(stagingRoot, $"disc{discNum:D2}");
                if (Directory.Exists(discDir) && Directory.EnumerateFileSystemEntries(discDir).Any())
                {
                    throw new IOException($"Target staging directory '{discDir}' already exists and is non-empty.");
                }
            }
        }

        // Every disc carries the same list of the whole set, so every source file is hashed once before staging.
        progress?.Report("Cataloging the whole set...");
        var catalog = await ArchiveCatalog.BuildEntriesAsync(plan, progress, cancellationToken).ConfigureAwait(false);

        var stagedDiscs = new List<ArchiveStagedDisc>(plan.Discs.Count + (plan.Discs.Count > 0 ? plan.ParityDiscCount : 0));
        var createdDiscDirs = new List<string>(plan.Discs.Count + (plan.Discs.Count > 0 ? plan.ParityDiscCount : 0));
        var dataDiscDescriptors = new List<ParitySetDataDisc>(plan.Discs.Count);
        string parentScratch = Path.Combine(stagingRoot, ".par2-scratch");

        try
        {
            for (int d = 0; d < plan.Discs.Count; d++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var disc = plan.Discs[d];
                string discFolderName = $"disc{disc.DiscNumber:D2}";
                string discRoot = Path.Combine(stagingRoot, discFolderName);

                ValidateNoPathCollisions(disc);

                Directory.CreateDirectory(discRoot);
                createdDiscDirs.Add(discRoot);

                var fileEntries = new List<ParitySetFileEntry>(disc.Files.Count);
                for (int f = 0; f < disc.Files.Count; f++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = disc.Files[f];
                    string destFilePath = ArchiveManifest.ResolveInsideRoot(discRoot, file.RelativePath)
                        ?? throw new InvalidOperationException($"Refusing to stage '{file.RelativePath}': the path points outside the disc folder.");

                    string? destDir = Path.GetDirectoryName(destFilePath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        if (File.Exists(destDir))
                        {
                            throw new InvalidOperationException(
                                $"Cannot stage '{file.RelativePath}' on disc {disc.DiscNumber}: a file already exists at directory path '{destDir}'.");
                        }
                        Directory.CreateDirectory(destDir);
                    }

                    if (File.Exists(destFilePath) || Directory.Exists(destFilePath))
                    {
                        throw new InvalidOperationException(
                            $"Cannot stage '{file.RelativePath}' on disc {disc.DiscNumber}: another entry collides with its path on a case-insensitive filesystem.");
                    }

                    progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: copying {file.RelativePath}");
                    File.Copy(file.FullPath, destFilePath, overwrite: false);

                    DateTime lastWriteUtc = File.GetLastWriteTimeUtc(file.FullPath);
                    File.SetLastWriteTimeUtc(destFilePath, lastWriteUtc);
                }

                progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: writing manifest...");
                var index = await ArchiveManifest.WriteAsync(discRoot, plan, disc, cancellationToken, catalog).ConfigureAwait(false);

                // The catalog hashed the sources, the manifest hashed the copies: a difference means a source file
                // changed while it was being staged, and the catalog on every disc would then be wrong.
                var cataloged = catalog.Where(e => e.DiscNumber == disc.DiscNumber).ToDictionary(e => e.Path, e => e.Sha256, StringComparer.Ordinal);
                var manifestByPath = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in index.Files)
                {
                    manifestByPath[entry.RelativePath] = entry.Sha256;
                    if (cataloged.TryGetValue(entry.RelativePath, out string? catalogHash) &&
                        !string.Equals(catalogHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"'{entry.RelativePath}' changed while it was being staged (its copy does not match the catalog). Stage again once nothing writes to the source folder.");
                    }
                }

                if (plan.ParityDiscCount > 0)
                {
                    foreach (var file in disc.Files)
                    {
                        string norm = file.RelativePath.Replace('\\', '/');
                        string stagedFilePath = ArchiveManifest.ResolveInsideRoot(discRoot, norm)!;
                        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(stagedFilePath);
                        fileEntries.Add(new ParitySetFileEntry(norm, file.SizeBytes, manifestByPath[norm], lastWriteUtc));
                    }

                    long discStreamBytes = ArchiveParitySet.ComputeDataDiscStreamBytes(disc.Files.Select(f => f.SizeBytes));
                    dataDiscDescriptors.Add(new ParitySetDataDisc(disc.DiscNumber, discStreamBytes, fileEntries));
                }

                Par2RecoveryLayout? layout = null;
                long budget = 0;
                if (plan.RedundancyPercent > 0)
                {
                    string scratchDir = Path.Combine(parentScratch, discFolderName);
                    try
                    {
                        var sources = ListSourceFiles(discRoot);
                        long freeSectors = RecoveryBudgetSectors(plan.EffectiveCeilingSectors, discRoot);
                        int dataSectors = SectorsUnder(discRoot);
                        budget = plan.FillFreeSpace
                            ? freeSectors
                            : Math.Min(freeSectors, (long)Math.Round(dataSectors * (plan.RedundancyPercent / 100.0)));
                        layout = Par2RecoverySizer.Plan(sources, budget);
                        if (layout == null && !plan.FillFreeSpace)
                        {
                            long oneBlock = Par2RecoverySizer.MinimumBudgetSectors(sources);
                            if (freeSectors >= oneBlock)
                            {
                                budget = oneBlock;
                                layout = Par2RecoverySizer.Plan(sources, budget);
                            }
                        }

                        if (layout != null)
                        {
                            string fillDetail = plan.FillFreeSpace ? "to fill the disc " : string.Empty;
                            progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: generating PAR2 recovery data " +
                                             $"{fillDetail}({layout.RecoveryBlocks:N0} blocks of {layout.BlockSize / 1024.0:N0} KB, " +
                                             $"{layout.RedundancyPercent:N0} %)...");
                            layout = await CreateFittingAsync(par2Path!, discRoot, layout, plan.EffectiveCeilingSectors, scratchDir, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(scratchDir))
                            {
                                Directory.Delete(scratchDir, recursive: true);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                int recoverySectors = SectorsUnder(Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName));
                stagedDiscs.Add(new ArchiveStagedDisc(
                    discRoot,
                    layout,
                    recoverySectors,
                    SectorsUnder(discRoot),
                    layout != null ? budget : 0,
                    DiscNumber: disc.DiscNumber,
                    IsParityDisc: false));
            }

            if (plan.ParityDiscCount > 0 && plan.Discs.Count > 0)
            {
                await StageParityDiscsAsync(
                    plan,
                    stagingRoot,
                    par2Path,
                    parentScratch,
                    catalog,
                    dataDiscDescriptors,
                    stagedDiscs,
                    createdDiscDirs,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (string dir in createdDiscDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                }
            }
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(parentScratch) && !Directory.EnumerateFileSystemEntries(parentScratch).Any())
                {
                    Directory.Delete(parentScratch, recursive: false);
                }
            }
            catch
            {
            }
        }

        return stagedDiscs;
    }

    private static void ValidateNoPathCollisions(ArchiveDiscPlan disc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ArchiveManifest.ManifestFileName,
            ArchiveManifest.IndexFileName,
            ArchiveManifest.VerifyPowerShellFileName,
            ArchiveManifest.VerifyBatchFileName,
            ArchiveManifest.RecoveryReadmeFileName,
            ArchiveCatalog.CatalogFileName,
            ArchiveCatalog.HtmlIndexFileName,
            ArchiveManifest.RecoveryFolderName,
            ArchiveParitySet.ParitySetFileName,
            ArchiveParitySet.ParityPayloadFileName
        };

        foreach (var file in disc.Files)
        {
            string normalized = file.RelativePath.Replace('\\', '/').TrimStart('/');
            if (!seen.Add(normalized))
            {
                throw new InvalidOperationException(
                    $"Cannot stage '{file.RelativePath}' on disc {disc.DiscNumber}: its path collides (ignoring letter case) with another file or reserved disc entry.");
            }
        }
    }

    /// <summary>Files par2 will protect: everything on the disc so far (data and manifests).</summary>
    internal static List<Par2SourceFile> ListSourceFiles(string discRoot) =>
        Directory.EnumerateFiles(discRoot, "*", SearchOption.AllDirectories)
            .Select(path => new Par2SourceFile(Path.GetRelativePath(discRoot, path).Replace('\\', '/'), new FileInfo(path).Length))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Room for recovery data: the ceiling minus the image the disc makes with the recovery files in it, minus
    /// those files. The image size comes from the image builder's own layout (ISO 9660 + Joliet + UDF, as
    /// <c>burn-data --udf</c> writes it), so the file system's share is exact rather than estimated. The recovery
    /// volume is later cut into one volume per zone (<see cref="Par2VolumeSplitter"/>); each cut can cost up to one
    /// sector of rounding, which is kept back here.
    /// </summary>
    public static long RecoveryBudgetSectors(int ceilingSectors, string discRoot)
    {
        // The files par2 will write once cut into zones, at a size near what they will have: the layout barely
        // depends on it (the descriptor copies of the archive layout are counted in the file system's share).
        long room = Math.Max(0, ceilingSectors - SectorsUnder(discRoot)) * (long)OpticalConstants.DataSectorSize;
        long part = Math.Max(0, room - 4096) / Par2VolumeSplitter.ZoneCount;
        var placeholders = new List<(string, long)> { ("recovery.par2", 4096L) };
        for (int i = 0; i < Par2VolumeSplitter.ZoneCount; i++)
        {
            placeholders.Add(($"recovery.vol{i * 10_000:D5}+10000.par2", part));
        }
        long placeholderSectors = placeholders.Sum(p => (p.Item2 + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
        long fileSystem = ImageSectors(discRoot, placeholders) - SectorsUnder(discRoot) - placeholderSectors;
        return ceilingSectors - SectorsUnder(discRoot) - fileSystem - (Par2VolumeSplitter.ZoneCount - 1);
    }

    /// <summary>Sectors the disc folder's image takes (plus optional extra RECOVERY files), from the builder's layout.</summary>
    public static int ImageSectors(string discRoot, IReadOnlyList<(string Name, long Size)>? extraRecoveryFiles = null)
    {
        var root = CompilationDirectory.CreateRoot();
        CompilationFolderImporter.Import(root, discRoot, strict: true);
        if (extraRecoveryFiles != null)
        {
            var recovery = root.AddDirectory(ArchiveManifest.RecoveryFolderName);
            foreach (var (name, size) in extraRecoveryFiles)
            {
                recovery.AddFile(new CompilationFile(name, string.Empty, size));
            }
        }
        return IsoUdfImageBuilder.CalculateTotalSectors(root);
    }

    /// <summary>
    /// Creates the recovery data, cuts its volume into one per zone (the image builder spreads them over the inner,
    /// middle and outer part of the disc) and checks the disc's image fits the ceiling. The size formula is exact for
    /// par2cmdline 1.4.0; should another version write more, the excess blocks are dropped and it is created again.
    /// </summary>
    private static async Task<Par2RecoveryLayout> CreateFittingAsync(
        string par2Path, string discRoot, Par2RecoveryLayout layout, int ceilingSectors, string scratchDir, CancellationToken ct)
    {
        string recoveryFolder = Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName);
        for (int attempt = 0; ; attempt++)
        {
            await Par2Runner.CreateAsync(par2Path, discRoot, layout, scratchDir, ct).ConfigureAwait(false);
            Par2VolumeSplitter.Split(recoveryFolder);
            int image = ImageSectors(discRoot);
            if (image <= ceilingSectors)
            {
                return layout with { RecoverySectors = SectorsUnder(recoveryFolder) };
            }

            long excessBytes = (image - ceilingSectors) * (long)OpticalConstants.DataSectorSize;
            int fewer = (int)(excessBytes / layout.BlockSize) + 2;
            if (attempt == 2 || layout.RecoveryBlocks <= fewer)
            {
                throw new InvalidOperationException(
                    $"The disc image would take {image:N0} sectors with its recovery data, more than the {ceilingSectors:N0} allowed.");
            }

            Directory.Delete(recoveryFolder, recursive: true);
            layout = layout with { RecoveryBlocks = layout.RecoveryBlocks - fewer };
        }
    }

    private static async Task StageParityDiscsAsync(
        ArchivePlan plan,
        string stagingRoot,
        string? par2Path,
        string parentScratch,
        IReadOnlyList<ArchiveCatalogEntry> catalog,
        IReadOnlyList<ParitySetDataDisc> dataDiscDescriptors,
        List<ArchiveStagedDisc> stagedDiscs,
        List<string> createdDiscDirs,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        int dataCount = plan.Discs.Count;
        int parityCount = plan.ParityDiscCount;
        int stripeSize = ArchiveParitySet.DefaultStripeSizeBytes;
        long maxStreamBytes = ArchiveParitySet.AlignUpToStripe(
            dataDiscDescriptors.Max(d => d.DataStreamBytes),
            stripeSize);
        long totalStripes = maxStreamBytes / stripeSize;

        var chunkPaths = ArchiveParitySet.GetChunkRelativePaths(maxStreamBytes);
        var parityRoots = new string[parityCount];
        var chunkWriters = new ParityChunkWriter[parityCount];
        var readers = new ArchiveParitySet.DiscContainerReader[dataCount];
        var sources = new ArchiveParitySet.IArchiveDiscSource[dataCount];

        try
        {
            for (int p = 0; p < parityCount; p++)
            {
                int discNum = dataCount + 1 + p;
                string parityRoot = Path.Combine(stagingRoot, $"disc{discNum:D2}");
                Directory.CreateDirectory(parityRoot);
                createdDiscDirs.Add(parityRoot);
                parityRoots[p] = parityRoot;
                chunkWriters[p] = new ParityChunkWriter(parityRoot, chunkPaths, maxStreamBytes);
            }

            for (int j = 0; j < dataCount; j++)
            {
                sources[j] = ArchiveParitySet.OpenDiscSource(stagedDiscs[j].DiscRoot);
                readers[j] = new ArchiveParitySet.DiscContainerReader(
                    sources[j],
                    dataDiscDescriptors[j].Files,
                    OpticalConstants.DataSectorSize,
                    verifySha256: false);
            }

            var codec = new CrossDiscReedSolomon(dataCount, parityCount);
            byte[] dataBuffer = new byte[stripeSize];
            byte[][] parityBuffers = new byte[parityCount][];
            for (int p = 0; p < parityCount; p++)
            {
                parityBuffers[p] = new byte[stripeSize];
            }

            for (long stripe = 0; stripe < totalStripes; stripe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stripe % 256 == 0)
                {
                    progress?.Report($"Generating cross-disc Reed-Solomon parity ({stripe + 1}/{totalStripes} stripes)...");
                }

                for (int p = 0; p < parityCount; p++)
                {
                    Array.Clear(parityBuffers[p]);
                }

                for (int j = 0; j < dataCount; j++)
                {
                    await readers[j].ReadStripeAsync(dataBuffer, cancellationToken).ConfigureAwait(false);
                    for (int p = 0; p < parityCount; p++)
                    {
                        CrossDiscReedSolomon.MultiplyXor(codec.GetCoefficient(p, j), dataBuffer, parityBuffers[p]);
                    }
                }

                for (int p = 0; p < parityCount; p++)
                {
                    await chunkWriters[p].WriteAsync(parityBuffers[p], cancellationToken).ConfigureAwait(false);
                }
            }

            for (int p = 0; p < parityCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int discNum = dataCount + 1 + p;
                string discFolderName = $"disc{discNum:D2}";
                string parityRoot = parityRoots[p];

                ParityPayloadInfo payloadInfo = await chunkWriters[p].CompleteAsync(cancellationToken).ConfigureAwait(false);
                var doc = new ParitySetDocument(
                    SchemaVersion: ArchiveParitySet.CurrentSchemaVersion,
                    SetId: plan.SetId,
                    Algorithm: CrossDiscReedSolomon.AlgorithmName,
                    MediaType: plan.MediaType.ToString(),
                    DataDiscCount: dataCount,
                    ParityDiscCount: parityCount,
                    TotalDiscCount: plan.TotalDiscCount,
                    ThisDiscNumber: discNum,
                    ParitySlotIndex: p,
                    SectorSizeBytes: OpticalConstants.DataSectorSize,
                    StripeSizeBytes: stripeSize,
                    MaxDataStreamBytes: maxStreamBytes,
                    RedundancyPercent: plan.RedundancyPercent,
                    ParityPayload: payloadInfo,
                    DataDiscs: dataDiscDescriptors);

                progress?.Report($"Staging parity disc {discNum}/{plan.TotalDiscCount}: writing PARITY-SET.JSON and manifests...");
                await ArchiveManifest.WriteParityDiscAsync(parityRoot, plan, doc, catalog, cancellationToken).ConfigureAwait(false);

                Par2RecoveryLayout? layout = null;
                long budget = 0;
                if (plan.RedundancyPercent > 0)
                {
                    string scratchDir = Path.Combine(parentScratch, discFolderName);
                    try
                    {
                        var par2Sources = ListSourceFiles(parityRoot);
                        long freeSectors = RecoveryBudgetSectors(plan.EffectiveCeilingSectors, parityRoot);
                        int dataSectors = SectorsUnder(parityRoot);
                        budget = plan.FillFreeSpace
                            ? freeSectors
                            : Math.Min(freeSectors, (long)Math.Round(dataSectors * (plan.RedundancyPercent / 100.0)));
                        layout = Par2RecoverySizer.Plan(par2Sources, budget);
                        if (layout == null && !plan.FillFreeSpace)
                        {
                            long oneBlock = Par2RecoverySizer.MinimumBudgetSectors(par2Sources);
                            if (freeSectors >= oneBlock)
                            {
                                budget = oneBlock;
                                layout = Par2RecoverySizer.Plan(par2Sources, budget);
                            }
                        }

                        if (layout != null)
                        {
                            string fillDetail = plan.FillFreeSpace ? "to fill the disc " : string.Empty;
                            progress?.Report($"Staging parity disc {discNum}/{plan.TotalDiscCount}: generating PAR2 recovery data " +
                                             $"{fillDetail}({layout.RecoveryBlocks:N0} blocks of {layout.BlockSize / 1024.0:N0} KB, " +
                                             $"{layout.RedundancyPercent:N0} %)...");
                            layout = await CreateFittingAsync(par2Path!, parityRoot, layout, plan.EffectiveCeilingSectors, scratchDir, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(scratchDir))
                            {
                                Directory.Delete(scratchDir, recursive: true);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                int recoverySectors = SectorsUnder(Path.Combine(parityRoot, ArchiveManifest.RecoveryFolderName));
                stagedDiscs.Add(new ArchiveStagedDisc(
                    parityRoot,
                    layout,
                    recoverySectors,
                    SectorsUnder(parityRoot),
                    layout != null ? budget : 0,
                    DiscNumber: discNum,
                    IsParityDisc: true));
            }
        }
        finally
        {
            for (int j = 0; j < readers.Length; j++)
            {
                if (readers[j] != null)
                {
                    await readers[j].DisposeAsync().ConfigureAwait(false);
                }
                sources[j]?.Dispose();
            }
            for (int p = 0; p < chunkWriters.Length; p++)
            {
                if (chunkWriters[p] != null)
                {
                    await chunkWriters[p].DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    internal sealed class ParityChunkWriter : IAsyncDisposable
    {
        private readonly string _root;
        private readonly IReadOnlyList<string> _chunkPaths;
        private readonly long _totalExpectedBytes;
        private readonly List<ParityPayloadChunk> _completedChunks = new();
        private readonly IncrementalHash _totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private int _chunkIndex;
        private long _chunkBytesWritten;
        private long _totalBytesWritten;
        private FileStream? _stream;
        private IncrementalHash? _chunkHash;

        public ParityChunkWriter(string root, IReadOnlyList<string> chunkPaths, long totalExpectedBytes)
        {
            _root = root;
            _chunkPaths = chunkPaths;
            _totalExpectedBytes = totalExpectedBytes;
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                if (_stream == null)
                {
                    OpenNextChunk();
                }

                long chunkCap = ArchiveParitySet.MaxParityChunkBytes - _chunkBytesWritten;
                int toWrite = (int)Math.Min(chunkCap, buffer.Length - offset);
                await _stream!.WriteAsync(buffer.Slice(offset, toWrite), cancellationToken).ConfigureAwait(false);
                var span = buffer.Span.Slice(offset, toWrite);
                _chunkHash!.AppendData(span);
                _totalHash.AppendData(span);

                offset += toWrite;
                _chunkBytesWritten += toWrite;
                _totalBytesWritten += toWrite;

                if (_chunkBytesWritten >= ArchiveParitySet.MaxParityChunkBytes && _chunkIndex + 1 < _chunkPaths.Count)
                {
                    await CloseCurrentChunkAsync().ConfigureAwait(false);
                    _chunkIndex++;
                }
            }
        }

        public async Task<ParityPayloadInfo> CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stream == null && _completedChunks.Count == 0 && _chunkPaths.Count > 0)
            {
                // Zero-byte payload (e.g., archive containing only 0-byte files).
                OpenNextChunk();
            }
            if (_stream != null)
            {
                await CloseCurrentChunkAsync().ConfigureAwait(false);
            }

            string totalSha = Convert.ToHexStringLower(_totalHash.GetHashAndReset());
            return new ParityPayloadInfo(_completedChunks, _totalBytesWritten, totalSha);
        }

        private void OpenNextChunk()
        {
            string rel = _chunkPaths[_chunkIndex];
            string full = ArchiveManifest.ResolveInsideRoot(_root, rel)
                ?? throw new InvalidOperationException($"Invalid parity chunk path '{rel}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            _stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            _chunkBytesWritten = 0;
            _chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private async Task CloseCurrentChunkAsync()
        {
            if (_stream != null)
            {
                await _stream.FlushAsync().ConfigureAwait(false);
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
            string chunkSha = Convert.ToHexStringLower(_chunkHash!.GetHashAndReset());
            _chunkHash.Dispose();
            _chunkHash = null;
            _completedChunks.Add(new ParityPayloadChunk(_chunkPaths[_chunkIndex], _chunkBytesWritten, chunkSha));
        }

        public async ValueTask DisposeAsync()
        {
            _chunkHash?.Dispose();
            _chunkHash = null;
            _totalHash.Dispose();
            if (_stream != null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }
    }

    private static int SectorsUnder(string folder) =>
        !Directory.Exists(folder)
            ? 0
            : (int)Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(f => (new FileInfo(f).Length + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
}
