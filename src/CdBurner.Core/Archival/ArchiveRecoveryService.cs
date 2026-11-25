using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival.Rs03;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>What a recovery run found and fixed.</summary>
/// <param name="OutputFolder">Folder the disc's files were copied (and repaired) into.</param>
/// <param name="UnreadableSectors">Sectors that could not be read (zero-filled in the copy).</param>
/// <param name="DamagedFiles">Files that had unreadable sectors or could not be copied whole, before repair.</param>
/// <param name="HadRecoveryData">The disc carried PAR2 recovery data.</param>
/// <param name="RepairAttempted">par2 was run.</param>
/// <param name="FilesChecked">Files checked against the disc's manifest after repair.</param>
/// <param name="StillMissing">Files the manifest lists that are still missing.</param>
/// <param name="StillCorrupt">Files whose checksum still does not match.</param>
/// <param name="Problem">Why recovery could not run or finish, in plain words; null when it ran.</param>
public sealed record ArchiveRecoveryReport(
    string OutputFolder,
    IReadOnlyList<int> UnreadableSectors,
    IReadOnlyList<string> DamagedFiles,
    bool HadRecoveryData,
    bool RepairAttempted,
    int FilesChecked,
    IReadOnlyList<string> StillMissing,
    IReadOnlyList<string> StillCorrupt,
    string? Problem,
    IReadOnlyList<string>? RejectedPaths = null,
    IReadOnlyList<string>? UdfSkippedEntries = null,
    int RawRecoveredSectors = 0,
    int Rs03RecoveredSectors = 0)
{
    /// <summary>Every file the manifest lists is present and matches its checksum.</summary>
    public bool FullyRecovered => Problem == null && FilesChecked > 0 && StillMissing.Count == 0 && StillCorrupt.Count == 0;

    /// <summary>
    /// Both checksum lists on the disc (manifest and index) read back whole. When either is damaged, a file that is
    /// not flagged may simply not have been checked, so no file is reported as repaired.
    /// </summary>
    public bool ChecksumListsIntact => FilesChecked > 0 && !StillCorrupt.Any(entry =>
        entry.StartsWith(ArchiveManifest.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
        entry.StartsWith(ArchiveManifest.IndexFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Damaged files that now match their checksum (empty when the checksum lists cannot be trusted).</summary>
    public IReadOnlyList<string> RepairedFiles => ChecksumListsIntact
        ? DamagedFiles.Where(f =>
            !StillMissing.Contains(f) &&
            !StillCorrupt.Contains(f) &&
            (RejectedPaths == null || !RejectedPaths.Contains(f))).ToList()
        : Array.Empty<string>();

    /// <summary>A short report for people, one fact per line.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            FullyRecovered
                ? (DamagedFiles.Count == 0 ? "Result: every file read correctly; nothing needed repair." : "Result: every file recovered.")
                : "Result: NOT fully recovered.",
            $"Copy written to: {OutputFolder}",
            $"Unreadable sectors: {UnreadableSectors.Count:N0}",
        };
        if (RawRecoveredSectors > 0)
        {
            lines.Add($"Sectors repaired via raw P/Q parity: {RawRecoveredSectors:N0}");
        }
        if (Rs03RecoveredSectors > 0)
        {
            lines.Add($"Sectors repaired via RS03 parity: {Rs03RecoveredSectors:N0}");
        }
        lines.Add($"Damaged files before repair: {DamagedFiles.Count:N0}");
        lines.AddRange(DamagedFiles.Select(f => "  " + f + (RepairedFiles.Contains(f) ? "  (repaired)" : "  (NOT repaired)")));
        lines.Add($"Recovery data on the disc: {(HadRecoveryData ? "yes" : "no")}");
        if (FilesChecked > 0 && !ChecksumListsIntact)
        {
            lines.Add("The disc's checksum lists are damaged themselves, so no file can be confirmed as intact.");
        }
        lines.Add($"Files checked against the manifest: {FilesChecked:N0}");
        lines.AddRange(StillMissing.Select(f => "  missing: " + f));
        lines.AddRange(StillCorrupt.Select(f => "  still damaged: " + f));
        if (Problem != null)
        {
            lines.Add("Problem: " + Problem);
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

/// <summary>
/// Gets an archive disc's files back from an image of it (read with retries, unreadable sectors zero-filled):
/// copies every file out, rebuilds damaged ones from the disc's PAR2 recovery data, then checks everything against
/// the disc's manifest. Never reports a file as repaired unless its checksum matches.
/// </summary>
public static class ArchiveRecoveryService
{
    public static async Task<ArchiveRecoveryReport> RecoverFromDiscAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend burnEngine,
        string outputFolder,
        string? par2Path,
        string? tempImagePath = null,
        bool keepImage = false,
        int maxRetriesPerSector = 5,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(burnEngine);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetriesPerSector);

        string imagePath = string.IsNullOrWhiteSpace(tempImagePath)
            ? outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".read.iso"
            : tempImagePath;

        try
        {
            progress?.Report($"Reading the disc in {recorder.UniqueId} (up to {maxRetriesPerSector} retries per bad sector, raw P/Q fallback enabled)...");
            var rip = await new DiscRipperService().RipToIsoAsync(
                recorder,
                burnEngine,
                imagePath,
                new RipOptions
                {
                    MaxRetriesPerSector = maxRetriesPerSector,
                    ContinueOnUnreadableSector = true,
                    RawSectorSalvage = true
                },
                ct: cancellationToken).ConfigureAwait(false);

            if (!File.Exists(imagePath))
            {
                return new ArchiveRecoveryReport(
                    outputFolder,
                    rip.BadSectorLbas,
                    Array.Empty<string>(),
                    false,
                    false,
                    0,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    "the disc could not be read (" + (rip.ErrorMessage ?? "unknown error") + ").",
                    RawRecoveredSectors: rip.RawRecoveredSectorsCount);
            }

            var report = await RecoverFromImageAsync(
                imagePath,
                rip.BadSectorLbas,
                outputFolder,
                par2Path,
                progress,
                cancellationToken).ConfigureAwait(false);

            return report with { RawRecoveredSectors = rip.RawRecoveredSectorsCount };
        }
        finally
        {
            if (!keepImage && File.Exists(imagePath))
            {
                try { File.Delete(imagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static async Task<ArchiveRecoveryReport> RecoverFromImageAsync(
        string imagePath,
        IReadOnlyCollection<int> unreadableSectors,
        string outputFolder,
        string? par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(unreadableSectors);

        if (Directory.Exists(outputFolder) && Directory.EnumerateFileSystemEntries(outputFolder).Any())
        {
            throw new IOException($"Output folder '{outputFolder}' is not empty.");
        }
        Directory.CreateDirectory(outputFolder);

        var bad = new SortedSet<int>(unreadableSectors);
        var damaged = new List<string>();
        var rejectedPaths = new List<string>();
        IReadOnlyList<string> udfSkipped = Array.Empty<string>();
        int rs03RecoveredSectors = 0;
        string effectiveImagePath = imagePath;
        string? tempRs03Image = null;
        string? tempRs03Ecc = null;

        try
        {
            string? sidecarEcc = File.Exists(imagePath + ".ecc")
                ? imagePath + ".ecc"
                : (File.Exists(Path.ChangeExtension(imagePath, ".ecc")) ? Path.ChangeExtension(imagePath, ".ecc") : null);
            bool hasAugmentedRs03 = sidecarEcc == null && Rs03Codec.TryInspectAugmentedImage(imagePath, out _, out _);

            if (sidecarEcc != null || hasAugmentedRs03)
            {
                tempRs03Image = Path.Combine(Path.GetTempPath(), $"ignis_rs03_rec_{Guid.NewGuid():N}.iso");
                File.Copy(imagePath, tempRs03Image, overwrite: true);
                if (sidecarEcc != null)
                {
                    tempRs03Ecc = tempRs03Image + ".ecc";
                    File.Copy(sidecarEcc, tempRs03Ecc, overwrite: true);
                }

                progress?.Report("Checking RS03 parity and repairing damaged sectors...");
                var rs03Repair = await Rs03Codec.RepairAsync(
                    tempRs03Image,
                    tempRs03Ecc,
                    new Rs03RepairOptions { UnreadableSectors = unreadableSectors },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                rs03RecoveredSectors = rs03Repair.RepairedDataSectors;
                if (rs03Repair.Success)
                {
                    bad.RemoveWhere(lba => lba >= 0 && lba < rs03Repair.Layout.DataSectors);
                }
                if (rs03Repair.Mode == Rs03Mode.AugmentedImage)
                {
                    long prefixBytes = (rs03Repair.Layout.DataSectors - 1) * Rs03Constants.SectorSize + rs03Repair.Layout.InLastBytes;
                    await using var trimFs = new FileStream(tempRs03Image, FileMode.Open, FileAccess.Write, FileShare.None);
                    if (trimFs.Length > prefixBytes)
                    {
                        trimFs.SetLength(prefixBytes);
                    }
                }
                effectiveImagePath = tempRs03Image;
            }

            ArchiveRecoveryReport Fail(string problem) => new(outputFolder, bad.ToList(), damaged, false, false, 0,
                Array.Empty<string>(), Array.Empty<string>(), problem, rejectedPaths, udfSkipped,
                Rs03RecoveredSectors: rs03RecoveredSectors);

            await using (var image = new FileStream(effectiveImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false))
        {
            var files = ReadFileList(image, out string? listProblem, out int? descriptorCopyLba, out udfSkipped);
            foreach (string skippedEntry in udfSkipped)
            {
                progress?.Report($"Skipped unreadable UDF entry: {skippedEntry}");
            }
            if (descriptorCopyLba is int copyLba)
            {
                progress?.Report($"The volume descriptor at sector 16 is unreadable; using its copy at sector {copyLba:N0}.");
            }
            if (files == null)
            {
                return Fail("the disc's file list could not be read (" + listProblem + "). PAR2 needs the file list to know where each file is.");
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = file.VirtualPath.TrimStart('/');
                string? destination = ArchiveManifest.ResolveInsideRoot(outputFolder, relative);
                if (destination == null)
                {
                    rejectedPaths.Add(relative);
                    damaged.Add(relative);
                    progress?.Report($"Rejected unsafe path in disc file list: {relative}");
                    continue; // a name that would land outside the folder is never written
                }

                progress?.Report($"Copying {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                long sectors = file.SizeBytes > 0
                    ? (file.SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize
                    : 0;
                bool hitBadSector = false;
                if (file.StartLba >= 0 && sectors > 0)
                {
                    int endLba = (int)Math.Min(int.MaxValue, (long)file.StartLba + sectors - 1);
                    hitBadSector = bad.GetViewBetween(file.StartLba, endLba).Count > 0;
                }
                bool copiedWhole = await CopyFileAsync(image, file, destination, cancellationToken).ConfigureAwait(false);
                if (hitBadSector || !copiedWhole)
                {
                    damaged.Add(relative);
                }
            }
        }

        string recoveryFolder = Path.Combine(outputFolder, ArchiveManifest.RecoveryFolderName);
        bool hasRecovery = Directory.Exists(recoveryFolder) && Directory.EnumerateFiles(recoveryFolder, "*.par2").Any();
        bool repairAttempted = false;
        IReadOnlyList<string> backups = Array.Empty<string>();
        string? repairError = null;

        // Images made elsewhere come without a list of unreadable sectors: the manifest finds those files instead.
        // A manifest that is itself damaged (or missing) is something par2 can rebuild, so it counts too.
        bool manifestReadable = true;
        try
        {
            var before = await ArchiveManifest.VerifyAsync(outputFolder, null, cancellationToken).ConfigureAwait(false);
            foreach (string path in before.Corrupt.Concat(before.Missing))
            {
                if (!damaged.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    damaged.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException or IOException)
        {
            manifestReadable = false;
        }

        if ((damaged.Count > 0 || !manifestReadable) && hasRecovery && par2Path != null)
        {
            progress?.Report($"Rebuilding {damaged.Count} damaged file(s) from the recovery data...");
            var repair = await Par2Runner.RepairAsync(par2Path, outputFolder, cancellationToken).ConfigureAwait(false);
            repairAttempted = true;
            backups = repair.BackupFiles;
            repairError = repair.ExitCode == 0 ? null : repair.ErrorOutput;
        }

        ArchiveVerificationResult verification;
        try
        {
            progress?.Report("Checking every file against the disc's manifest...");
            verification = await ArchiveManifest.VerifyAsync(outputFolder, null, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new ArchiveRecoveryReport(outputFolder, bad.ToList(), damaged, hasRecovery, repairAttempted, 0,
                Array.Empty<string>(), Array.Empty<string>(),
                "the disc has no IgnisDisc archive manifest, so the files cannot be checked.",
                rejectedPaths, udfSkipped, Rs03RecoveredSectors: rs03RecoveredSectors);
        }

        var stillCorrupt = verification.Corrupt.ToList();
        foreach (string rejected in rejectedPaths)
        {
            if (!stillCorrupt.Any(c => c.StartsWith(rejected, StringComparison.OrdinalIgnoreCase)) &&
                !verification.Missing.Contains(rejected, StringComparer.OrdinalIgnoreCase))
            {
                stillCorrupt.Add($"{rejected} (rejected: path points outside the disc)");
            }
        }

        string? problem = null;
        if (!verification.IsIntact || stillCorrupt.Count > 0)
        {
            problem = rejectedPaths.Count > 0 && verification.IsIntact
                ? "the disc's file list contains an unsafe path that points outside the output folder."
                : !hasRecovery
                ? "the disc has no recovery data, so damaged files cannot be rebuilt."
                : par2Path == null
                    ? "par2 is not installed (Settings > optional tools, or par2.exe on PATH), so damaged files were not rebuilt."
                    : repairError != null && repairError.Contains("pathlength", StringComparison.OrdinalIgnoreCase)
                        ? "par2 cannot handle a file path longer than 260 characters; recover into a shorter folder, such as C:\\Rescued."
                        : "there was more damage than the recovery data can rebuild.";
        }
        else
        {
            // par2 keeps damaged originals as name.1; once every file checks out they are only clutter.
            foreach (string backup in backups)
            {
                try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        return new ArchiveRecoveryReport(outputFolder, bad.ToList(), damaged, hasRecovery, repairAttempted,
            verification.FilesChecked, verification.Missing, stillCorrupt, problem, rejectedPaths, udfSkipped,
            Rs03RecoveredSectors: rs03RecoveredSectors);
        }
        finally
        {
            if (tempRs03Image != null && File.Exists(tempRs03Image))
            {
                try { File.Delete(tempRs03Image); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (tempRs03Ecc != null && File.Exists(tempRs03Ecc))
            {
                try { File.Delete(tempRs03Ecc); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static IReadOnlyList<IsoFileEntry>? ReadFileList(Stream image, out string? problem, out int? descriptorCopyLba) =>
        ReadFileList(image, out problem, out descriptorCopyLba, out _);

    /// <summary>
    /// The disc's files with their full names. The UDF tree has them all (ISO 9660 has 8.3 names and Joliet stops at
    /// 64 characters, and a disc burned as ISO + UDF has no Joliet at all), so it is used whenever it lists at least
    /// as many files as the ISO 9660 / Joliet tree. When sector 16 is unreadable, the ISO 9660 tree is found through
    /// a copy of its volume descriptor (archive discs carry one per recovery zone); <paramref name="descriptorCopyLba"/>
    /// then says which copy was used.
    /// </summary>
    internal static IReadOnlyList<IsoFileEntry>? ReadFileList(
        Stream image, out string? problem, out int? descriptorCopyLba, out IReadOnlyList<string> udfSkipped)
    {
        problem = null;
        descriptorCopyLba = null;
        var skipped = new List<string>();
        image.Position = 0;
        var udfRoot = UdfTreeReader.TryRead(image, skipped);
        udfSkipped = skipped;
        List<IsoFileEntry>? udfFiles = udfRoot == null ? null : Flatten(udfRoot).ToList();

        List<IsoFileEntry>? isoFiles = null;
        try
        {
            image.Position = 0;
            using var reader = IsoImageReader.FromDamagedStream(image, leaveOpen: true);
            isoFiles = reader.AllFiles.ToList();
            descriptorCopyLba = reader.DescriptorCopyLba;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            problem = ex.Message;
        }

        if (udfFiles != null && (isoFiles == null || udfFiles.Count >= isoFiles.Count))
        {
            return udfFiles;
        }
        return isoFiles;
    }

    private static IEnumerable<IsoFileEntry> Flatten(IsoDirectoryEntry directory) =>
        directory.Files.Concat(directory.SubDirectories.SelectMany(Flatten));

    /// <summary>
    /// Copies a file's sectors straight from the image (no whole-file buffer, so files over 2 GB work). A file that
    /// runs past the end of the image is padded with zeros to its full length, so par2 can line its blocks up.
    /// </summary>
    private static async Task<bool> CopyFileAsync(Stream image, IsoFileEntry file, string destination, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        if (file.StartLba < 0 || file.SizeBytes < 0)
        {
            if (file.SizeBytes > 0)
            {
                output.SetLength(file.SizeBytes);
            }
            return false;
        }

        long start = (long)file.StartLba * OpticalConstants.DataSectorSize;
        long available = Math.Clamp(image.Length - start, 0, file.SizeBytes);
        byte[] buffer = new byte[1 << 20];
        long copied = 0;
        image.Position = start;
        while (copied < available)
        {
            int n = await image.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, available - copied)), ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            copied += n;
        }
        if (copied < file.SizeBytes)
        {
            output.SetLength(file.SizeBytes);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Reconstructs a completely lost or shattered archive disc (data disc <c>1..N</c> or parity disc
    /// <c>N+1..N+K</c>) from remaining discs and parity disc(s) using bounded O(1)-memory Reed-Solomon streaming,
    /// verifies SHA-256 parity across every restored file, and writes manifests and verification scripts.
    /// </summary>
    public static async Task<ArchiveRebuildReport> RebuildMissingDiscAsync(
        IReadOnlyList<string> availableDiscPaths,
        int missingDiscNumber,
        string outputFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availableDiscPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(missingDiscNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        var targets = new Dictionary<int, string> { [missingDiscNumber] = outputFolder };
        var reports = await RebuildMissingDiscsAsync(availableDiscPaths, targets, progress, cancellationToken).ConfigureAwait(false);
        return reports[0];
    }

    /// <summary>
    /// Reconstructs one or more completely lost archive discs (up to <c>K</c> discs total) in a single
    /// bounded O(1)-memory streaming pass from the remaining discs and parity disc(s).
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveRebuildReport>> RebuildMissingDiscsAsync(
        IReadOnlyList<string> availableDiscPaths,
        IReadOnlyDictionary<int, string> targetDiscOutputs,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(availableDiscPaths);
        ArgumentNullException.ThrowIfNull(targetDiscOutputs);
        if (availableDiscPaths.Count == 0)
        {
            throw new ArgumentException("At least one surviving disc path must be provided.", nameof(availableDiscPaths));
        }
        if (targetDiscOutputs.Count == 0)
        {
            throw new ArgumentException("At least one target missing disc must be specified.", nameof(targetDiscOutputs));
        }

        foreach (var (discNum, outDir) in targetDiscOutputs)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(discNum, 1, nameof(targetDiscOutputs));
            ArgumentException.ThrowIfNullOrWhiteSpace(outDir, nameof(targetDiscOutputs));
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                throw new IOException($"Output folder '{outDir}' is not empty.");
            }
        }

        var openedSources = new List<ArchiveParitySet.IArchiveDiscSource>(availableDiscPaths.Count);
        try
        {
            var indexedSources = new Dictionary<int, (ArchiveParitySet.IArchiveDiscSource Source, ParitySetDocument? ParityDoc)>();
            ParitySetDocument? referenceParityDoc = null;
            string? referenceSetId = null;

            foreach (string rawPath in availableDiscPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                var source = ArchiveParitySet.OpenDiscSource(rawPath);
                openedSources.Add(source);

                ParitySetDocument? parityDoc = null;
                int? discNum = null;
                string? setId = null;

                string? parityJson = await source.ReadTextIfExistsAsync(ArchiveParitySet.ParitySetFileName, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(parityJson))
                {
                    try
                    {
                        parityDoc = ArchiveParitySet.ParseJson(parityJson);
                        if (parityDoc != null && parityDoc.ThisDiscNumber >= 1)
                        {
                            discNum = parityDoc.ThisDiscNumber;
                            setId = parityDoc.SetId;
                        }
                    }
                    catch (JsonException)
                    {
                        parityDoc = null;
                    }
                }

                if (discNum == null)
                {
                    string? indexJson = await source.ReadTextIfExistsAsync(ArchiveManifest.IndexFileName, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(indexJson))
                    {
                        try
                        {
                            var idx = JsonSerializer.Deserialize<ArchiveIndex>(indexJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (idx != null && idx.DiscNumber >= 1)
                            {
                                discNum = idx.DiscNumber;
                                setId = idx.SetId;
                            }
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }

                if (discNum == null)
                {
                    continue;
                }

                if (referenceSetId == null && !string.IsNullOrWhiteSpace(setId))
                {
                    referenceSetId = setId;
                }
                else if (referenceSetId != null && !string.IsNullOrWhiteSpace(setId) &&
                         !string.Equals(referenceSetId, setId, StringComparison.Ordinal))
                {
                    return BuildFailureReports(referenceSetId, targetDiscOutputs, referenceParityDoc,
                        $"Surviving discs belong to different archive sets ('{referenceSetId}' vs '{setId}').");
                }

                if (parityDoc != null)
                {
                    if (referenceParityDoc == null)
                    {
                        referenceParityDoc = parityDoc;
                    }
                    else if (!string.Equals(referenceParityDoc.SetId, parityDoc.SetId, StringComparison.Ordinal) ||
                             referenceParityDoc.DataDiscCount != parityDoc.DataDiscCount ||
                             referenceParityDoc.ParityDiscCount != parityDoc.ParityDiscCount ||
                             referenceParityDoc.StripeSizeBytes != parityDoc.StripeSizeBytes ||
                             referenceParityDoc.MaxDataStreamBytes != parityDoc.MaxDataStreamBytes)
                    {
                        return BuildFailureReports(referenceSetId ?? string.Empty, targetDiscOutputs, referenceParityDoc,
                            "Parity discs disagree on archive set geometry.");
                    }
                }

                if (!targetDiscOutputs.ContainsKey(discNum.Value) && !indexedSources.ContainsKey(discNum.Value))
                {
                    indexedSources[discNum.Value] = (source, parityDoc);
                }
            }

            if (referenceParityDoc == null)
            {
                return BuildFailureReports(referenceSetId ?? string.Empty, targetDiscOutputs, null,
                    $"No valid {ArchiveParitySet.ParitySetFileName} metadata was found on the provided discs.");
            }

            if (referenceParityDoc.DataDiscCount < 1 || referenceParityDoc.ParityDiscCount < 1 ||
                referenceParityDoc.StripeSizeBytes <= 0 || referenceParityDoc.SectorSizeBytes <= 0 ||
                referenceParityDoc.DataDiscs.Count != referenceParityDoc.DataDiscCount)
            {
                return BuildFailureReports(referenceParityDoc.SetId, targetDiscOutputs, referenceParityDoc,
                    $"{ArchiveParitySet.ParitySetFileName} contains invalid layout metadata.");
            }

            foreach (int targetDisc in targetDiscOutputs.Keys)
            {
                if (targetDisc < 1 || targetDisc > referenceParityDoc.TotalDiscCount)
                {
                    return BuildFailureReports(referenceParityDoc.SetId, targetDiscOutputs, referenceParityDoc,
                        $"Requested disc {targetDisc} is outside archive set range 1..{referenceParityDoc.TotalDiscCount}.");
                }
            }

            var excludedDiscs = new HashSet<int>(targetDiscOutputs.Keys);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attemptResult = await TryRebuildPassAsync(
                    referenceParityDoc,
                    indexedSources,
                    excludedDiscs,
                    targetDiscOutputs,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (attemptResult.CorruptDiscToExclude is int badDisc)
                {
                    excludedDiscs.Add(badDisc);
                    foreach (string outDir in targetDiscOutputs.Values)
                    {
                        CleanDirectoryContents(outDir);
                    }
                    continue;
                }

                return attemptResult.Reports;
            }
        }
        finally
        {
            foreach (var source in openedSources)
            {
                source.Dispose();
            }
        }
    }

    private static async Task<(IReadOnlyList<ArchiveRebuildReport> Reports, int? CorruptDiscToExclude)> TryRebuildPassAsync(
        ParitySetDocument parityDoc,
        IReadOnlyDictionary<int, (ArchiveParitySet.IArchiveDiscSource Source, ParitySetDocument? ParityDoc)> indexedSources,
        IReadOnlySet<int> excludedDiscs,
        IReadOnlyDictionary<int, string> targetDiscOutputs,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        int n = parityDoc.DataDiscCount;
        int k = parityDoc.ParityDiscCount;
        int stripeSize = parityDoc.StripeSizeBytes;
        long totalStripes = parityDoc.MaxDataStreamBytes / stripeSize;
        var orderedDataDiscs = parityDoc.DataDiscs.OrderBy(d => d.DiscNumber).ToList();

        var survivingDataIndices = new List<int>(n);
        var missingDataIndices = new List<int>();
        for (int j = 0; j < n; j++)
        {
            int discNum = j + 1;
            if (!excludedDiscs.Contains(discNum) &&
                indexedSources.TryGetValue(discNum, out var entry) &&
                CanReadAllFiles(entry.Source, orderedDataDiscs[j].Files))
            {
                survivingDataIndices.Add(j);
            }
            else
            {
                missingDataIndices.Add(j);
            }
        }

        var survivingParity = new List<(int SlotIndex, int DiscNumber, ArchiveParitySet.IArchiveDiscSource Source, ParitySetDocument Doc)>();
        for (int p = 0; p < k; p++)
        {
            int discNum = n + 1 + p;
            if (!excludedDiscs.Contains(discNum) &&
                indexedSources.TryGetValue(discNum, out var entry) &&
                entry.ParityDoc != null &&
                CanReadAllParityChunks(entry.Source, entry.ParityDoc.ParityPayload))
            {
                survivingParity.Add((p, discNum, entry.Source, entry.ParityDoc));
            }
        }

        int neededParity = missingDataIndices.Count;
        if (neededParity > survivingParity.Count)
        {
            return (BuildFailureReports(
                parityDoc.SetId,
                targetDiscOutputs,
                parityDoc,
                $"Cannot rebuild missing disc(s): {neededParity} data disc(s) unavailable, but only {survivingParity.Count} valid parity disc(s) available."),
                null);
        }

        var selectedParity = survivingParity.Take(neededParity).ToList();
        var selectedParitySlots = selectedParity.Select(x => x.SlotIndex).ToList();
        var targetParitySlots = targetDiscOutputs.Keys
            .Where(d => d > n)
            .OrderBy(d => d)
            .Select(d => (DiscNumber: d, SlotIndex: d - n - 1, OutputFolder: targetDiscOutputs[d]))
            .ToList();

        var codec = new CrossDiscReedSolomon(n, k);
        byte[,] recoveryMatrix = neededParity > 0
            ? codec.BuildRecoveryMatrix(missingDataIndices, selectedParitySlots)
            : new byte[0, 0];

        var dataReaders = new Dictionary<int, ArchiveParitySet.DiscContainerReader>();
        var parityReaders = new List<ParityChunkReader>(selectedParity.Count);
        var targetDataWriters = new Dictionary<int, ArchiveParitySet.DiscContainerWriter>();
        var targetParityWriters = new Dictionary<int, ArchiveStager.ParityChunkWriter>();

        try
        {
            foreach (int j in survivingDataIndices)
            {
                int discNum = j + 1;
                dataReaders[j] = new ArchiveParitySet.DiscContainerReader(
                    indexedSources[discNum].Source,
                    orderedDataDiscs[j].Files,
                    parityDoc.SectorSizeBytes,
                    verifySha256: true);
            }

            foreach (var sp in selectedParity)
            {
                parityReaders.Add(new ParityChunkReader(sp.DiscNumber, sp.Source, sp.Doc.ParityPayload));
            }

            foreach (var (discNum, outDir) in targetDiscOutputs)
            {
                Directory.CreateDirectory(outDir);
                if (discNum <= n)
                {
                    targetDataWriters[discNum] = new ArchiveParitySet.DiscContainerWriter(
                        outDir,
                        orderedDataDiscs[discNum - 1].Files,
                        parityDoc.SectorSizeBytes);
                }
                else
                {
                    var chunkPaths = ArchiveParitySet.GetChunkRelativePaths(parityDoc.MaxDataStreamBytes);
                    targetParityWriters[discNum] = new ArchiveStager.ParityChunkWriter(outDir, chunkPaths, parityDoc.MaxDataStreamBytes);
                }
            }

            byte[] readBuffer = new byte[stripeSize];
            byte[][] residualBuffers = new byte[neededParity][];
            byte[][] rebuiltDataBuffers = new byte[neededParity][];
            for (int r = 0; r < neededParity; r++)
            {
                residualBuffers[r] = new byte[stripeSize];
                rebuiltDataBuffers[r] = new byte[stripeSize];
            }
            byte[][] targetParityBuffers = new byte[targetParitySlots.Count][];
            for (int tp = 0; tp < targetParitySlots.Count; tp++)
            {
                targetParityBuffers[tp] = new byte[stripeSize];
            }

            for (long stripe = 0; stripe < totalStripes; stripe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stripe % 256 == 0)
                {
                    progress?.Report($"Reconstructing missing disc(s) ({stripe + 1}/{totalStripes} stripes)...");
                }

                for (int r = 0; r < neededParity; r++)
                {
                    await parityReaders[r].ReadStripeAsync(residualBuffers[r], cancellationToken).ConfigureAwait(false);
                }
                for (int tp = 0; tp < targetParitySlots.Count; tp++)
                {
                    Array.Clear(targetParityBuffers[tp]);
                }

                foreach (int j in survivingDataIndices)
                {
                    await dataReaders[j].ReadStripeAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                    for (int r = 0; r < neededParity; r++)
                    {
                        CrossDiscReedSolomon.MultiplyXor(codec.GetCoefficient(selectedParitySlots[r], j), readBuffer, residualBuffers[r]);
                    }
                    for (int tp = 0; tp < targetParitySlots.Count; tp++)
                    {
                        CrossDiscReedSolomon.MultiplyXor(codec.GetCoefficient(targetParitySlots[tp].SlotIndex, j), readBuffer, targetParityBuffers[tp]);
                    }
                }

                for (int c = 0; c < neededParity; c++)
                {
                    Array.Clear(rebuiltDataBuffers[c]);
                    for (int r = 0; r < neededParity; r++)
                    {
                        CrossDiscReedSolomon.MultiplyXor(recoveryMatrix[c, r], residualBuffers[r], rebuiltDataBuffers[c]);
                    }

                    int discNum = missingDataIndices[c] + 1;
                    if (targetDataWriters.TryGetValue(discNum, out var writer))
                    {
                        await writer.WriteStripeAsync(rebuiltDataBuffers[c], cancellationToken).ConfigureAwait(false);
                    }
                    for (int tp = 0; tp < targetParitySlots.Count; tp++)
                    {
                        CrossDiscReedSolomon.MultiplyXor(
                            codec.GetCoefficient(targetParitySlots[tp].SlotIndex, missingDataIndices[c]),
                            rebuiltDataBuffers[c],
                            targetParityBuffers[tp]);
                    }
                }

                for (int tp = 0; tp < targetParitySlots.Count; tp++)
                {
                    await targetParityWriters[targetParitySlots[tp].DiscNumber].WriteAsync(targetParityBuffers[tp], cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var (j, reader) in dataReaders)
            {
                await reader.VerifyRemainingZeroByteFilesAsync(cancellationToken).ConfigureAwait(false);
                if (reader.HashMismatchPath != null)
                {
                    int corruptDiscNum = j + 1;
                    if (neededParity + 1 <= survivingParity.Count)
                    {
                        return (Array.Empty<ArchiveRebuildReport>(), corruptDiscNum);
                    }
                    return (BuildFailureReports(
                        parityDoc.SetId,
                        targetDiscOutputs,
                        parityDoc,
                        $"Surviving disc {corruptDiscNum} failed SHA-256 verification on '{reader.HashMismatchPath}'."),
                        null);
                }
            }

            foreach (var pr in parityReaders)
            {
                if (!pr.VerifyCompletedHashes())
                {
                    if (neededParity <= survivingParity.Count - 1)
                    {
                        return (Array.Empty<ArchiveRebuildReport>(), pr.DiscNumber);
                    }
                    return (BuildFailureReports(
                        parityDoc.SetId,
                        targetDiscOutputs,
                        parityDoc,
                        $"Parity disc {pr.DiscNumber} failed SHA-256 payload verification."),
                        null);
                }
            }

            var plan = ArchiveParitySet.ToArchivePlan(parityDoc);
            var catalog = ArchiveParitySet.ToCatalogEntries(parityDoc);
            var sourceDiscsUsed = survivingDataIndices.Select(j => j + 1).Concat(selectedParity.Select(p => p.DiscNumber)).OrderBy(x => x).ToList();
            var reports = new List<ArchiveRebuildReport>(targetDiscOutputs.Count);

            foreach (var (discNum, outDir) in targetDiscOutputs.OrderBy(kv => kv.Key))
            {
                if (discNum <= n)
                {
                    var writer = targetDataWriters[discNum];
                    await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);

                    string? problem = writer.FailedFiles.Count > 0
                        ? $"{writer.FailedFiles.Count} restored file(s) failed SHA-256 verification."
                        : null;

                    if (problem == null)
                    {
                        var discPlan = plan.Discs.Single(d => d.DiscNumber == discNum);
                        await ArchiveManifest.WriteAsync(outDir, plan, discPlan, cancellationToken, catalog).ConfigureAwait(false);
                    }

                    reports.Add(new ArchiveRebuildReport(
                        SetId: parityDoc.SetId,
                        RebuiltDiscNumber: discNum,
                        OutputFolder: outDir,
                        FilesRestored: writer.VerifiedFiles.Count,
                        BytesRestored: writer.PayloadBytesRestored,
                        SourceDiscNumbersUsed: sourceDiscsUsed,
                        VerifiedFiles: writer.VerifiedFiles,
                        FailedFiles: writer.FailedFiles,
                        IsParityDisc: false,
                        Problem: problem));
                }
                else
                {
                    var pWriter = targetParityWriters[discNum];
                    ParityPayloadInfo payloadInfo = await pWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    int slotIdx = discNum - n - 1;
                    var rebuiltDoc = parityDoc with
                    {
                        ThisDiscNumber = discNum,
                        ParitySlotIndex = slotIdx,
                        ParityPayload = payloadInfo
                    };
                    await ArchiveManifest.WriteParityDiscAsync(outDir, plan, rebuiltDoc, catalog, cancellationToken).ConfigureAwait(false);

                    var verifiedList = payloadInfo.Chunks.Select(c => c.RelativePath)
                        .Append(ArchiveParitySet.ParitySetFileName)
                        .Append(ArchiveCatalog.CatalogFileName)
                        .Append(ArchiveCatalog.HtmlIndexFileName)
                        .ToList();

                    reports.Add(new ArchiveRebuildReport(
                        SetId: parityDoc.SetId,
                        RebuiltDiscNumber: discNum,
                        OutputFolder: outDir,
                        FilesRestored: verifiedList.Count,
                        BytesRestored: payloadInfo.TotalSizeBytes,
                        SourceDiscNumbersUsed: sourceDiscsUsed,
                        VerifiedFiles: verifiedList,
                        FailedFiles: Array.Empty<string>(),
                        IsParityDisc: true,
                        Problem: null));
                }
            }

            return (reports, null);
        }
        finally
        {
            foreach (var r in dataReaders.Values) await r.DisposeAsync().ConfigureAwait(false);
            foreach (var pr in parityReaders) await pr.DisposeAsync().ConfigureAwait(false);
            foreach (var w in targetDataWriters.Values) await w.DisposeAsync().ConfigureAwait(false);
            foreach (var pw in targetParityWriters.Values) await pw.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool CanReadAllFiles(ArchiveParitySet.IArchiveDiscSource source, IReadOnlyList<ParitySetFileEntry> files)
    {
        foreach (var f in files)
        {
            if (source.GetFileLength(f.RelativePath) != f.SizeBytes)
            {
                return false;
            }
        }
        return true;
    }

    private static bool CanReadAllParityChunks(ArchiveParitySet.IArchiveDiscSource source, ParityPayloadInfo payload)
    {
        if (payload?.Chunks == null || payload.Chunks.Count == 0)
        {
            return false;
        }
        foreach (var c in payload.Chunks)
        {
            if (source.GetFileLength(c.RelativePath) != c.SizeBytes)
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<ArchiveRebuildReport> BuildFailureReports(
        string setId,
        IReadOnlyDictionary<int, string> targets,
        ParitySetDocument? doc,
        string problem)
    {
        int dataCount = doc?.DataDiscCount ?? int.MaxValue;
        return targets.OrderBy(kv => kv.Key)
            .Select(kv => new ArchiveRebuildReport(
                SetId: setId,
                RebuiltDiscNumber: kv.Key,
                OutputFolder: kv.Value,
                FilesRestored: 0,
                BytesRestored: 0,
                SourceDiscNumbersUsed: Array.Empty<int>(),
                VerifiedFiles: Array.Empty<string>(),
                FailedFiles: Array.Empty<string>(),
                IsParityDisc: kv.Key > dataCount,
                Problem: problem))
            .ToList();
    }

    private static void CleanDirectoryContents(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (string dir in Directory.EnumerateDirectories(folder))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class ParityChunkReader : IAsyncDisposable
    {
        private readonly ArchiveParitySet.IArchiveDiscSource _source;
        private readonly ParityPayloadInfo _payload;
        private readonly IncrementalHash _totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private int _chunkIndex;
        private long _chunkBytesRead;
        private Stream? _stream;
        private IncrementalHash? _chunkHash;
        private bool _anyMismatch;

        public int DiscNumber { get; }

        public ParityChunkReader(int discNumber, ArchiveParitySet.IArchiveDiscSource source, ParityPayloadInfo payload)
        {
            DiscNumber = discNumber;
            _source = source;
            _payload = payload;
        }

        public async Task ReadStripeAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            int filled = 0;
            while (filled < destination.Length)
            {
                if (_stream == null)
                {
                    if (_chunkIndex >= _payload.Chunks.Count)
                    {
                        destination.Span[filled..].Clear();
                        return;
                    }
                    var chunk = _payload.Chunks[_chunkIndex];
                    _stream = _source.OpenRead(chunk.RelativePath);
                    _chunkBytesRead = 0;
                    _chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }

                var currentChunk = _payload.Chunks[_chunkIndex];
                long remaining = currentChunk.SizeBytes - _chunkBytesRead;
                if (remaining <= 0)
                {
                    await CloseCurrentChunkAsync(currentChunk).ConfigureAwait(false);
                    _chunkIndex++;
                    continue;
                }

                int toRead = (int)Math.Min(remaining, destination.Length - filled);
                int read = await _stream.ReadAsync(destination.Slice(filled, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of parity chunk '{currentChunk.RelativePath}'.");
                }

                var span = destination.Span.Slice(filled, read);
                _chunkHash!.AppendData(span);
                _totalHash.AppendData(span);
                filled += read;
                _chunkBytesRead += read;

                if (_chunkBytesRead >= currentChunk.SizeBytes)
                {
                    await CloseCurrentChunkAsync(currentChunk).ConfigureAwait(false);
                    _chunkIndex++;
                }
            }
        }

        public bool VerifyCompletedHashes()
        {
            if (_anyMismatch || _chunkIndex < _payload.Chunks.Count)
            {
                return false;
            }
            string totalActual = Convert.ToHexStringLower(_totalHash.GetHashAndReset());
            return string.Equals(totalActual, _payload.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private async Task CloseCurrentChunkAsync(ParityPayloadChunk chunk)
        {
            string actual = Convert.ToHexStringLower(_chunkHash!.GetHashAndReset());
            if (!string.Equals(actual, chunk.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _anyMismatch = true;
            }
            _chunkHash.Dispose();
            _chunkHash = null;
            if (_stream != null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
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
}
