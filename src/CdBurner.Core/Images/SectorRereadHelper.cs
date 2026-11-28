using System.Text;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Images;

/// <summary>
/// Shared second-read and failure-reporting helpers used by both <see cref="DiscVerificationService"/>
/// and post-burn verification in <see cref="Burning.DataBurnService"/>.
/// </summary>
internal static class SectorRereadHelper
{
    /// <summary>
    /// At most this many failed sectors get a second read (each keeps its expected 2 KiB in memory until then);
    /// past it the disc is badly damaged and the rest are reported from the first read.
    /// </summary>
    public const int MaxSectorsToReread = 4096;

    /// <summary>Failed sectors this close together are read again as one run.</summary>
    private const int RereadMergeGapSectors = 32;

    /// <summary>
    /// A sector that failed the first read. <see cref="Expected"/> and the read bytes are kept only for sectors that
    /// will be read a second time (null past <see cref="MaxSectorsToReread"/>, or for raw reads).
    /// </summary>
    internal sealed class FailedSector(int lba, bool unreadable, byte[]? expected, byte[]? firstRead)
    {
        public int Lba { get; } = lba;
        public byte[]? Expected { get; } = expected;
        public bool FirstReadUnreadable { get; } = unreadable;
        public byte[]? FirstRead { get; } = firstRead;

        /// <summary>State after the latest read: unreadable, or the bytes read (null when not kept).</summary>
        public bool Unreadable { get; set; } = unreadable;
        public byte[]? Read { get; set; } = firstRead;

        public bool Reread { get; set; }
        public bool Recovered { get; set; }

        /// <summary>The disc gave something else on the second read than on the first.</summary>
        public bool ChangedOnReread =>
            Recovered || Unreadable != FirstReadUnreadable
            || (Read != null && FirstRead != null && !Read.AsSpan().SequenceEqual(FirstRead));
    }

    /// <summary>
    /// Reads every failed sector again from the disc surface (FUA on every read), run by run, each run after a
    /// one-sector read at the far end of the verified range [<paramref name="firstVerifiedLba"/>, <paramref name="lastVerifiedLba"/>].
    /// Updates each <see cref="FailedSector"/> in place.
    /// </summary>
    public static async Task RereadFailedSectorsAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int firstVerifiedLba,
        int lastVerifiedLba,
        List<FailedSector> sectors,
        List<string> errors,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        var byLba = sectors.ToDictionary(f => f.Lba);
        int midLba = firstVerifiedLba + (lastVerifiedLba - firstVerifiedLba + 1) / 2;
        int index = 0;
        while (index < sectors.Count)
        {
            ct.ThrowIfCancellationRequested();

            int runStart = sectors[index].Lba;
            int runEnd = runStart;
            int next = index + 1;
            while (next < sectors.Count && sectors[next].Lba - runEnd <= RereadMergeGapSectors)
            {
                runEnd = sectors[next].Lba;
                next++;
            }

            int farLba = runStart >= midLba ? firstVerifiedLba : lastVerifiedLba;
            try
            {
                if (farLba < runStart || farLba > runEnd)
                {
                    await BatchedSectorReader.ReadRangeAsync(
                        engine, recorder, farLba, 1, raw2352Mode: false, maxRetriesPerSector: 0,
                        onSector: (_, _) => ValueTask.CompletedTask,
                        onUnreadable: _ => ValueTask.FromResult(true),
                        cancellationToken: ct, readFromDisc: true, strict: true).ConfigureAwait(false);
                }

                await BatchedSectorReader.ReadRangeAsync(
                    engine, recorder, runStart, runEnd - runStart + 1, raw2352Mode: false, maxRetriesPerSector: 1,
                    onSector: (lba, data) =>
                    {
                        if (byLba.TryGetValue(lba, out var sector))
                        {
                            sector.Reread = true;
                            sector.Recovered = sector.Expected is { } expected && data.Span.SequenceEqual(expected);
                            sector.Unreadable = false;
                            sector.Read = sector.Recovered ? null : data.ToArray();
                        }
                        return ValueTask.CompletedTask;
                    },
                    onUnreadable: lba =>
                    {
                        if (byLba.TryGetValue(lba, out var sector))
                        {
                            sector.Reread = true;
                            sector.Recovered = false;
                            sector.Unreadable = true;
                            sector.Read = null;
                        }
                        return ValueTask.FromResult(true);
                    },
                    cancellationToken: ct, readFromDisc: true, strict: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The sectors keep their first-read result; the report says the second read could not run.
                errors.Add($"The second read of sectors {runStart}-{runEnd} failed: {ex.Message}");
            }

            index = next;
            onProgress?.Invoke(index, sectors.Count);
        }
    }

    /// <summary>The image files that own any of <paramref name="lbas"/> (in LBA order), each named once.</summary>
    public static List<string> MapCorruptedFiles(IReadOnlyList<int> lbas, IReadOnlyList<LbaFileExtentMap>? fileMap)
    {
        var files = new List<string>();
        if (fileMap == null || fileMap.Count == 0 || lbas.Count == 0)
        {
            return files;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LbaFileExtentMap? last = null;
        foreach (int lba in lbas)
        {
            // Failed sectors usually come in runs inside one file: check the last match before searching the map.
            if (last != null && last.ContainsLba(lba))
            {
                continue;
            }
            foreach (var extent in fileMap)
            {
                if (extent.ContainsLba(lba))
                {
                    last = extent;
                    if (seen.Add(extent.FilePath))
                    {
                        files.Add(extent.FilePath);
                    }
                }
            }
        }
        return files;
    }

    /// <summary>
    /// Formats a verification failure message that names unreadable and differing sectors separately with their
    /// affected files, plus any sectors that recovered on the second read.
    /// </summary>
    public static string DescribeFailure(
        int failedCount,
        IReadOnlyList<int> unreadableLbas,
        IReadOnlyList<int> differingLbas,
        int recoveredCount,
        int notReread,
        bool secondReadEnabled,
        IReadOnlyList<LbaFileExtentMap>? fileMap,
        IReadOnlyList<string> secondReadErrors)
    {
        var text = new StringBuilder();
        text.Append($"Verification failed: {failedCount} sector(s) bad");
        text.Append(secondReadEnabled && notReread < failedCount ? " after a second read" : string.Empty);
        text.Append($" ({unreadableLbas.Count} could not be read, {differingLbas.Count} read back different data from the image).");
        if (recoveredCount > 0)
        {
            text.Append($" {recoveredCount} other sector(s) failed the first read but matched the image on the second.");
        }
        if (secondReadEnabled && notReread > 0)
        {
            text.Append($" {notReread} sector(s) were not read a second time");
            text.Append(failedCount + recoveredCount > MaxSectorsToReread
                ? $" (only the first {MaxSectorsToReread:N0} are)."
                : ".");
        }

        AppendCategoryDetails(text, "Unreadable", unreadableLbas, fileMap);
        AppendCategoryDetails(text, "Different data", differingLbas, fileMap);

        foreach (string error in secondReadErrors)
        {
            text.Append(' ').Append(error).Append('.');
        }
        return text.ToString();
    }

    private static void AppendCategoryDetails(
        StringBuilder text,
        string label,
        IReadOnlyList<int> lbas,
        IReadOnlyList<LbaFileExtentMap>? fileMap)
    {
        if (lbas.Count == 0)
        {
            return;
        }

        text.Append(' ').Append(label).Append(": ");
        text.Append(lbas.Count == 1
            ? $"sector {lbas[0]}"
            : $"{lbas.Count} sector(s) starting at sector {lbas[0]}");

        if (fileMap is { Count: > 0 })
        {
            var files = MapCorruptedFiles(lbas, fileMap);
            text.Append(files.Count == 0
                ? " (in the file system area, outside any file)"
                : $" — affected file(s): {string.Join(", ", files.Take(10))}{(files.Count > 10 ? ", ..." : string.Empty)}");
        }

        text.Append('.');
    }
}
