using CdBurner.Core.Images;

namespace CdBurner.Core.Audio;

/// <summary>Outcome of trying to repair a rip from CUETools Database (CTDB) parity.</summary>
public enum CtdbRepairStatus
{
    /// <summary>The rip already matches a CTDB entry; nothing to repair.</summary>
    NotNeeded,

    /// <summary>
    /// The rip was corrected to match a CTDB entry and AccurateRip confirms every corrected track.
    /// The ripped files were replaced with the corrected audio.
    /// </summary>
    Repaired,

    /// <summary>
    /// The corrected audio matches a CTDB entry, but AccurateRip could not confirm it (no AccurateRip data for the
    /// corrected tracks, or the lookup failed). The ripped files are unchanged; corrected copies were saved in
    /// <see cref="CtdbRepairResult.CorrectedFolder"/>.
    /// </summary>
    CorrectedNotConfirmed,

    /// <summary>The corrected audio matches CTDB but AccurateRip says it is wrong. Nothing was changed.</summary>
    AccurateRipMismatch,

    /// <summary>The rip differs from CTDB in more places than the parity can correct. Nothing was changed.</summary>
    TooMuchDamage,

    /// <summary>No CTDB entry for this disc offers parity data.</summary>
    NoParity,

    /// <summary>The rip cannot be checked against parity (for example, not every audio track was ripped).</summary>
    NotPossible,

    /// <summary>A download or file error stopped the repair. Nothing was changed.</summary>
    Failed
}

/// <summary>One track whose samples were corrected.</summary>
/// <param name="TrackNumber">Disc track number.</param>
/// <param name="CorrectedSamples">Number of 16-bit channel samples that were changed.</param>
/// <param name="OriginalPath">The ripped WAV file.</param>
/// <param name="CorrectedPath">Where the corrected audio is (equal to <paramref name="OriginalPath"/> once repaired).</param>
/// <param name="AccurateRip">AccurateRip verdict for the corrected audio, if AccurateRip was reached.</param>
/// <param name="CorrectedInfo">Checksums of the corrected audio.</param>
public sealed record CtdbCorrectedTrack(
    int TrackNumber,
    int CorrectedSamples,
    string OriginalPath,
    string CorrectedPath,
    AccurateRipTrackVerdict? AccurateRip,
    AudioTrackRipInfo CorrectedInfo);

/// <summary>Result of <see cref="CtdbRepairService.RepairAsync"/>.</summary>
public sealed record CtdbRepairResult(
    CtdbRepairStatus Status,
    string Message,
    long? EntryId = null,
    int EntryConfidence = 0,
    int OffsetSamples = 0,
    int CorrectedSamples = 0,
    IReadOnlyList<CtdbCorrectedTrack>? Tracks = null,
    string? CorrectedFolder = null,
    bool AccurateRipContacted = false,
    AudioCdRipResult? UpdatedRip = null)
{
    /// <summary>Corrected tracks (empty unless the audio was corrected).</summary>
    public IReadOnlyList<CtdbCorrectedTrack> CorrectedTracks => Tracks ?? Array.Empty<CtdbCorrectedTrack>();

    /// <summary>One line per corrected track for text reports.</summary>
    public IEnumerable<string> DescribeTracks() => CorrectedTracks.Select(t =>
        $"Track {t.TrackNumber:D2}: {t.CorrectedSamples} sample(s) corrected, AccurateRip " +
        (t.AccurateRip is { IsAccurate: true } v
            ? $"accurate (confidence {v.Confidence}/{v.TotalSubmissions})"
            : t.AccurateRip is { TotalSubmissions: > 0 } w
                ? $"NOT accurate (0/{w.TotalSubmissions} submissions match)"
                : "has no data"));

    /// <summary>Short machine-readable status for reports and JSON (for example <c>repaired</c>).</summary>
    public string StatusCode => Status switch
    {
        CtdbRepairStatus.NotNeeded => "not-needed",
        CtdbRepairStatus.Repaired => "repaired",
        CtdbRepairStatus.CorrectedNotConfirmed => "corrected-not-confirmed",
        CtdbRepairStatus.AccurateRipMismatch => "accuraterip-mismatch",
        CtdbRepairStatus.TooMuchDamage => "too-much-damage",
        CtdbRepairStatus.NoParity => "no-parity",
        CtdbRepairStatus.NotPossible => "not-possible",
        _ => "failed"
    };
}

/// <summary>
/// Repairs a ripped audio CD from CUETools Database parity, then confirms the result with AccurateRip.
/// <para>
/// Steps: (1) read the rip once and compute the Reed-Solomon syndromes of every column; (2) for each CTDB entry
/// with parity, best confidence first, find the alignment (read offset difference) from the entry's inline
/// syndrome; (3) download the entry's syndromes and correct each column (at most npar/2 wrong samples per
/// column); (4) accept the correction only if the corrected audio reproduces the entry's CTDB CRC-32; (5) look up
/// the corrected tracks in AccurateRip. A rip is reported as <see cref="CtdbRepairStatus.Repaired"/> and its
/// files are changed only when AccurateRip then matches every corrected track.
/// </para>
/// File formats and the parity layout follow CUETools (<c>https://github.com/gchudov/cuetools.net</c>,
/// GPL-2.0-or-later); the decoder is a standard Berlekamp-Massey / Forney Reed-Solomon decoder.
/// </summary>
public static class CtdbRepairService
{
    /// <summary>Name of the folder, next to the ripped files, that receives corrected copies AccurateRip could not confirm.</summary>
    public const string CorrectedFolderName = "CTDB corrected";

    private const int ReadChunkBytes = 1 << 16;

    /// <summary>
    /// Tries to repair <paramref name="rip"/> against <paramref name="entries"/> (already fetched from CTDB for the
    /// disc whose <c>toc</c> string is <paramref name="ctdbToc"/>).
    /// </summary>
    /// <param name="httpClient">Used to download parity files.</param>
    /// <param name="rip">A successful rip of every audio track (its <see cref="AudioCdRipResult.CtdbDiscCrc"/> is set).</param>
    /// <param name="entries">CTDB entries for the disc.</param>
    /// <param name="ctdbToc">The disc's CTDB <c>toc</c> string, to skip entries for a different layout.</param>
    /// <param name="fetchAccurateRip">
    /// Returns the AccurateRip pressings for the disc. Called at most once, and only after a correction has been
    /// found. Null when AccurateRip may not be contacted.
    /// </param>
    /// <param name="baseUrl">CTDB server, for relative parity paths.</param>
    /// <param name="userAgent">User-Agent for parity downloads.</param>
    /// <param name="cancellationToken">Cancels the repair; files are left unchanged.</param>
    public static async Task<CtdbRepairResult> RepairAsync(
        HttpClient httpClient,
        AudioCdRipResult rip,
        IReadOnlyList<CtdbEntry> entries,
        string? ctdbToc,
        Func<CancellationToken, Task<IReadOnlyList<AccurateRipPressing>>>? fetchAccurateRip,
        string baseUrl = CueToolsDatabase.DefaultBaseUrl,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(rip);
        ArgumentNullException.ThrowIfNull(entries);

        if (!rip.Success || rip.CtdbDiscCrc is not uint discCrc)
        {
            return new CtdbRepairResult(CtdbRepairStatus.NotPossible,
                "Repair from CTDB parity needs a complete rip of every audio track.");
        }

        if (rip.Mode == AudioRipMode.ImageCue)
        {
            return new CtdbRepairResult(CtdbRepairStatus.NotPossible,
                "Repair from CTDB parity is only supported for track-by-track WAV rips.");
        }

        if (entries.Any(e => e.Crc32 == discCrc))
        {
            return new CtdbRepairResult(CtdbRepairStatus.NotNeeded, "The rip already matches the CUETools database.");
        }

        var tracks = rip.TrackInfos.OrderBy(t => t.AudioTrackIndex).ToList();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].AudioTrackIndex != i || !File.Exists(tracks[i].FilePath))
            {
                return new CtdbRepairResult(CtdbRepairStatus.NotPossible,
                    "Repair from CTDB parity needs the WAV file of every audio track.");
            }
        }

        var candidates = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.HasParity)
                && CueToolsDatabase.ResolveParityUri(e, baseUrl) != null
                && !string.IsNullOrWhiteSpace(e.SyndromeBase64)
                && (e.RawStride ?? CtdbChecksum.StrideSamples) == CtdbChecksum.StrideSamples
                && (e.Toc == null || ctdbToc == null || string.Equals(e.Toc, ctdbToc, StringComparison.Ordinal)))
            .OrderByDescending(e => e.Confidence)
            .ToList();
        if (candidates.Count == 0)
        {
            return new CtdbRepairResult(CtdbRepairStatus.NoParity,
                "No CUETools database entry for this disc has parity data, so the rip cannot be repaired.");
        }

        DiscAudio disc;
        CtdbSyndromeAccumulator accumulator;
        try
        {
            disc = DiscAudio.Open(tracks);
            accumulator = new CtdbSyndromeAccumulator(new CtdbParityLayout(disc.TotalSamples));
            await disc.ReadAsync(null, (_, _, chunk) => accumulator.Append(chunk), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or UnauthorizedAccessException)
        {
            return new CtdbRepairResult(CtdbRepairStatus.NotPossible, $"The ripped files could not be read for repair: {ex.Message}");
        }

        CtdbRepairResult? best = null;
        IReadOnlyList<AccurateRipPressing>? pressings = null;
        string? accurateRipError = null;
        bool accurateRipContacted = false;

        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CtdbRepairResult attempt;
            try
            {
                attempt = await TryEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                           or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                attempt = new CtdbRepairResult(CtdbRepairStatus.Failed,
                    $"Repair from CTDB entry {entry.Id} failed: {ex.Message}", entry.Id, entry.Confidence,
                    AccurateRipContacted: accurateRipContacted);
            }

            if (attempt.Status is CtdbRepairStatus.Repaired or CtdbRepairStatus.CorrectedNotConfirmed)
            {
                return attempt;
            }

            if (best == null || Rank(attempt.Status) > Rank(best.Status))
            {
                best = attempt;
            }
        }

        return best!;

        async Task<CtdbRepairResult> TryEntryAsync(CtdbEntry entry)
        {
            ushort[] inline = CueToolsDatabase.DecodeEntrySyndrome(entry)
                ?? throw new InvalidDataException("The entry has no usable inline syndrome.");
            var layout = accumulator.Layout;

            if (!TryFindShift(accumulator, inline, out int shift))
            {
                return new CtdbRepairResult(CtdbRepairStatus.TooMuchDamage,
                    $"The rip differs from CTDB entry {entry.Id} by more than its parity can correct; not repaired.",
                    entry.Id, entry.Confidence, AccurateRipContacted: accurateRipContacted);
            }

            int npar = Math.Min(entry.Npar ?? inline.Length, CtdbParityLayout.MaxNpar);
            npar -= npar % 2;
            if (npar < 2)
            {
                throw new InvalidDataException($"CTDB entry {entry.Id} declares too few parity values ({entry.Npar}).");
            }

            ushort[] remote = await CueToolsDatabase.FetchParitySyndromesAsync(
                httpClient, entry, npar, userAgent, baseUrl, cancellationToken).ConfigureAwait(false);
            ushort[] local = accumulator.GetShiftedSyndromes(shift, npar);

            var corrections = new List<(long Symbol, ushort Value)>();
            int badColumns = DecodeColumns(local, remote, npar, layout, shift, corrections);
            if (badColumns > 0)
            {
                return new CtdbRepairResult(CtdbRepairStatus.TooMuchDamage,
                    $"The rip differs from CTDB entry {entry.Id} in more places than its parity can correct " +
                    $"({badColumns} of {CtdbParityLayout.Stride} parity columns have more than {npar / 2} wrong samples); not repaired.",
                    entry.Id, entry.Confidence, -shift / 2, AccurateRipContacted: accurateRipContacted);
            }

            corrections.Sort((a, b) => a.Symbol.CompareTo(b.Symbol));
            var check = await disc.ChecksumCorrectedAsync(corrections, layout, shift, cancellationToken).ConfigureAwait(false);
            if (check.WindowCrc != entry.Crc32)
            {
                return new CtdbRepairResult(CtdbRepairStatus.TooMuchDamage,
                    $"Correcting the rip from CTDB entry {entry.Id} did not reproduce the entry's checksum; " +
                    "the damage is beyond what the parity can fix. Not repaired.",
                    entry.Id, entry.Confidence, -shift / 2, AccurateRipContacted: accurateRipContacted);
            }

            if (corrections.Count == 0)
            {
                // Same audio at a different alignment: nothing to correct, the rip matches this entry at that offset.
                return new CtdbRepairResult(CtdbRepairStatus.NotNeeded,
                    $"The rip matches CTDB entry {entry.Id} at a read offset {-shift / 2:+#;-#;0} samples away; nothing to repair.",
                    entry.Id, entry.Confidence, -shift / 2, AccurateRipContacted: accurateRipContacted);
            }

            // Confirm with AccurateRip before calling anything repaired.
            if (pressings == null && accurateRipError == null)
            {
                if (fetchAccurateRip == null)
                {
                    accurateRipError = "AccurateRip may not be contacted";
                }
                else
                {
                    accurateRipContacted = true;
                    try
                    {
                        pressings = await fetchAccurateRip(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
                                                   or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        accurateRipError = $"AccurateRip lookup failed: {ex.Message}";
                    }
                }
            }

            var changed = check.Tracks.Where(t => t.CorrectedSymbols > 0).ToList();
            var verdicts = changed.ToDictionary(
                t => t.Index,
                t => pressings is { Count: > 0 }
                    ? AccurateRipDatabase.Match(pressings, t.Index, t.Info.AccurateRipV1, t.Info.AccurateRipV2)
                    : null);

            bool allConfirmed = verdicts.Values.All(v => v is { IsAccurate: true });
            bool contradicted = verdicts.Values.Any(v => v is { IsAccurate: false, TotalSubmissions: > 0 });
            int offsetSamples = -shift / 2;
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tracks[0].FilePath))!, CorrectedFolderName);

            if (contradicted)
            {
                return new CtdbRepairResult(CtdbRepairStatus.AccurateRipMismatch,
                    $"CTDB parity corrected {corrections.Count} samples, but AccurateRip does not match the corrected " +
                    "audio; the rip was left unchanged and is not repaired.",
                    entry.Id, entry.Confidence, offsetSamples, corrections.Count,
                    changed.Select(t => ToCorrectedTrack(t, verdicts[t.Index], t.Info.FilePath)).ToList(),
                    AccurateRipContacted: accurateRipContacted);
            }

            var written = disc.WriteCorrectedCopies(changed, corrections, folder);
            if (allConfirmed)
            {
                var repairedTracks = new List<CtdbCorrectedTrack>();
                foreach (var t in changed)
                {
                    File.Move(written[t.Index], tracks[t.Index].FilePath, overwrite: true);
                    repairedTracks.Add(ToCorrectedTrack(t, verdicts[t.Index], tracks[t.Index].FilePath));
                }

                TryDeleteEmptyDirectory(folder);
                return new CtdbRepairResult(CtdbRepairStatus.Repaired,
                    $"Repaired {corrections.Count} samples in {changed.Count} track(s) from CTDB parity; " +
                    "AccurateRip confirms the corrected audio.",
                    entry.Id, entry.Confidence, offsetSamples, corrections.Count, repairedTracks, null,
                    accurateRipContacted, BuildUpdatedRip(rip, check));
            }

            string reason = accurateRipError ?? "AccurateRip has no data for the corrected track(s)";
            return new CtdbRepairResult(CtdbRepairStatus.CorrectedNotConfirmed,
                $"CTDB parity corrected {corrections.Count} samples in {changed.Count} track(s) and the result matches CTDB, " +
                $"but it is not confirmed ({reason}). The ripped files were left unchanged; corrected copies are in '{folder}'.",
                entry.Id, entry.Confidence, offsetSamples, corrections.Count,
                changed.Select(t => ToCorrectedTrack(t, verdicts[t.Index], written[t.Index])).ToList(),
                folder, accurateRipContacted);
        }
    }

    /// <summary>
    /// Decodes every column's error syndromes (local XOR stored) and appends the corrections as
    /// (disc symbol index, XOR value). Returns the number of columns with more damage than npar/2.
    /// </summary>
    internal static int DecodeColumns(
        ushort[] local,
        ushort[] remote,
        int npar,
        CtdbParityLayout layout,
        int shift,
        List<(long Symbol, ushort Value)> corrections)
    {
        int badColumns = 0;
        Span<ushort> error = stackalloc ushort[npar];
        Span<int> rows = stackalloc int[npar / 2];
        Span<ushort> values = stackalloc ushort[npar / 2];
        for (int column = 0; column < CtdbParityLayout.Stride; column++)
        {
            for (int i = 0; i < npar; i++)
            {
                error[i] = (ushort)(local[column * npar + i] ^ remote[column * npar + i]);
            }

            if (!CtdbReedSolomon.TryDecode(error, layout.RowCount, rows, values, out int count))
            {
                badColumns++;
                continue;
            }

            for (int e = 0; e < count; e++)
            {
                corrections.Add((layout.SymbolIndex(column, rows[e], shift), values[e]));
            }
        }

        return badColumns;
    }

    /// <summary>
    /// Finds the alignment of the rip against an entry from the entry's column-0 syndromes. Prefers an exact match,
    /// then the alignment that needs the fewest corrections (fewer than npar/2), then the smallest offset.
    /// Returns the shift in symbols.
    /// </summary>
    public static bool TryFindShift(CtdbSyndromeAccumulator accumulator, ushort[] inline, out int shift)
    {
        int npar = Math.Min(inline.Length, accumulator.Npar);
        int rowCount = accumulator.Layout.RowCount;
        var error = new ushort[npar];
        var rows = new int[npar / 2];
        var values = new ushort[npar / 2];

        int bestShift = 0;
        int bestErrors = int.MaxValue;

        bool Consider(int candidate)
        {
            ushort[] local = accumulator.GetShiftedSyndromes(candidate, npar, 0, 1);
            for (int i = 0; i < npar; i++)
            {
                error[i] = (ushort)(local[i] ^ inline[i]);
            }

            // Accept at most npar/2 - 1 fixes in column 0, as CUETools does: almost any random syndrome fits a
            // locator of degree npar/2, so that many "errors" says nothing about whether the alignment is right.
            if (!CtdbReedSolomon.TryDecode(error, rowCount, rows, values, out int count, npar / 2 - 1))
            {
                return false;
            }

            if (count < bestErrors || (count == bestErrors && Math.Abs(candidate) < Math.Abs(bestShift)))
            {
                bestErrors = count;
                bestShift = candidate;
            }

            return count == 0;
        }

        if (!Consider(0))
        {
            for (int distance = 2; distance < CtdbParityLayout.Stride; distance += 2)
            {
                if (Consider(-distance) | Consider(distance))
                {
                    break;
                }
            }
        }

        shift = bestShift;
        return bestErrors != int.MaxValue;
    }

    private static int Rank(CtdbRepairStatus status) => status switch
    {
        CtdbRepairStatus.NotNeeded => 5,
        CtdbRepairStatus.AccurateRipMismatch => 4,
        CtdbRepairStatus.TooMuchDamage => 3,
        CtdbRepairStatus.Failed => 2,
        _ => 1
    };

    private static CtdbCorrectedTrack ToCorrectedTrack(TrackCheck t, AccurateRipTrackVerdict? verdict, string path) =>
        new(t.Info.TrackNumber, t.CorrectedSymbols, t.OriginalPath, path, verdict, t.Info with { FilePath = path });

    private static AudioCdRipResult BuildUpdatedRip(AudioCdRipResult rip, CorrectedChecksums check)
    {
        var byIndex = check.Tracks.ToDictionary(t => t.Index);
        var infos = rip.TrackInfos
            .Select(info => byIndex.TryGetValue(info.AudioTrackIndex, out var t) && t.CorrectedSymbols > 0
                ? t.Info with { FilePath = info.FilePath, ReplayGainTrackGain = null, ReplayGainTrackPeak = null }
                : info)
            .ToList();

        double? albumGain = rip.ReplayGainAlbumGain;
        double? albumPeak = rip.ReplayGainAlbumPeak;
        if (rip.ReplayGainAlbumGain != null || rip.TrackInfos.Any(i => i.ReplayGainTrackGain != null))
        {
            // Corrected samples can change a track's peak, so measure ReplayGain again for the whole album.
            var results = new List<ReplayGainResult>();
            for (int i = 0; i < infos.Count; i++)
            {
                using var s = new FileStream(infos[i].FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var rg = ReplayGainCalculator.CalculateTrackReplayGain(s);
                results.Add(rg);
                infos[i] = infos[i] with { ReplayGainTrackGain = rg.GainDb, ReplayGainTrackPeak = rg.PeakAmplitude };
            }

            var album = ReplayGainCalculator.CalculateAlbumReplayGain(results);
            albumGain = album.AlbumGainDb;
            albumPeak = album.AlbumPeakAmplitude;
        }

        var ordered = infos.OrderBy(i => i.AudioTrackIndex).ToList();
        uint discCrc = CtdbChecksum.CombineTrackCrcs(ordered, check.TrackStartSamples, check.TotalSamples);
        return AudioCdRipResult.Succeeded(
            rip.RippedFilePaths,
            rip.TotalSectorsRead,
            rip.TotalTime,
            rip.CueSheetPath,
            rip.CdText,
            infos,
            rip.BadSectorLbas,
            rip.SecureModeUsed,
            albumGain,
            albumPeak,
            rip.RecoveryRepair,
            rip.HiddenTrackOneAudioPath,
            discCrc);
    }

    private static void TryDeleteEmptyDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException)
        {
            // An empty leftover folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record TrackCheck(int Index, string OriginalPath, int CorrectedSymbols, AudioTrackRipInfo Info);

    private sealed record CorrectedChecksums(
        uint WindowCrc,
        IReadOnlyList<TrackCheck> Tracks,
        IReadOnlyList<long> TrackStartSamples,
        long TotalSamples);

    /// <summary>The ripped tracks in disc order, read as one continuous stream of PCM bytes.</summary>
    private sealed class DiscAudio
    {
        private readonly List<AudioTrackRipInfo> _tracks;
        private readonly long[] _dataOffsets;
        private readonly long[] _dataLengths;
        private readonly long[] _startBytes;

        private DiscAudio(List<AudioTrackRipInfo> tracks, long[] offsets, long[] lengths)
        {
            _tracks = tracks;
            _dataOffsets = offsets;
            _dataLengths = lengths;
            _startBytes = new long[tracks.Count];
            long total = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                _startBytes[i] = total;
                total += lengths[i];
            }

            TotalBytes = total;
        }

        public long TotalBytes { get; }

        public long TotalSamples => TotalBytes / 4;

        public static DiscAudio Open(List<AudioTrackRipInfo> tracks)
        {
            var offsets = new long[tracks.Count];
            var lengths = new long[tracks.Count];
            for (int i = 0; i < tracks.Count; i++)
            {
                using var fs = new FileStream(tracks[i].FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                (offsets[i], lengths[i]) = WavFileCodec.LocateRedBookPcmData(fs);
            }

            return new DiscAudio(tracks, offsets, lengths);
        }

        /// <summary>Reads every track in order, applying <paramref name="corrections"/> (sorted by symbol) on the way.</summary>
        public async Task ReadAsync(
            IReadOnlyList<(long Symbol, ushort Value)>? corrections,
            Action<int, long, ReadOnlySpan<byte>> onChunk,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[ReadChunkBytes];
            int next = 0;
            for (int t = 0; t < _tracks.Count; t++)
            {
                await using var fs = new FileStream(
                    _tracks[t].FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, ReadChunkBytes, useAsync: true);
                fs.Position = _dataOffsets[t];
                long remaining = _dataLengths[t];
                long discByte = _startBytes[t];
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int read = 0;
                    while (read < want)
                    {
                        int n = await fs.ReadAsync(buffer.AsMemory(read, want - read), cancellationToken).ConfigureAwait(false);
                        if (n == 0)
                        {
                            throw new InvalidDataException($"'{_tracks[t].FilePath}' ended early.");
                        }

                        read += n;
                    }

                    if (corrections != null)
                    {
                        long firstSymbol = discByte / 2;
                        long endSymbol = firstSymbol + read / 2;
                        while (next < corrections.Count && corrections[next].Symbol < endSymbol)
                        {
                            var (symbol, value) = corrections[next++];
                            int b = (int)(2 * (symbol - firstSymbol));
                            buffer[b] ^= (byte)value;
                            buffer[b + 1] ^= (byte)(value >> 8);
                        }
                    }

                    onChunk(t, discByte, buffer.AsSpan(0, read));
                    discByte += read;
                    remaining -= read;
                }
            }
        }

        /// <summary>Checksums of the corrected audio: the CTDB window CRC and per-track AccurateRip, CRC-32 and CTDB values.</summary>
        public async Task<CorrectedChecksums> ChecksumCorrectedAsync(
            IReadOnlyList<(long Symbol, ushort Value)> corrections,
            CtdbParityLayout layout,
            int shift,
            CancellationToken cancellationToken)
        {
            long windowStart = 2L * (CtdbParityLayout.Stride + shift);
            long windowEnd = windowStart + 2L * layout.RowCount * CtdbParityLayout.Stride;
            var windowCrc = new Crc32Algorithm();
            int count = _tracks.Count;
            var trackStarts = _startBytes.Select(b => b / 4).ToList();
            var ar = new AccurateRipChecksum[count];
            var crc = new Crc32Algorithm[count];
            var ctdb = new CtdbChecksum[count];
            var correctedPerTrack = new int[count];
            for (int t = 0; t < count; t++)
            {
                long samples = _dataLengths[t] / 4;
                ar[t] = new AccurateRipChecksum(samples, t == 0, t == count - 1);
                crc[t] = new Crc32Algorithm();
                ctdb[t] = CtdbChecksum.ForTrack(trackStarts[t], samples, TotalSamples);
            }

            foreach (var (symbol, _) in corrections)
            {
                correctedPerTrack[TrackOfByte(2 * symbol)]++;
            }

            await ReadAsync(corrections, (t, discByte, chunk) =>
            {
                ar[t].Append(chunk);
                crc[t].Append(chunk);
                ctdb[t].Append(chunk);
                long s = Math.Max(discByte, windowStart);
                long e = Math.Min(discByte + chunk.Length, windowEnd);
                if (e > s)
                {
                    windowCrc.Append(chunk.Slice((int)(s - discByte), (int)(e - s)));
                }
            }, cancellationToken).ConfigureAwait(false);

            var checks = new List<TrackCheck>(count);
            for (int t = 0; t < count; t++)
            {
                var info = _tracks[t] with
                {
                    Crc32 = crc[t].GetCurrentHashAsUInt32(),
                    AccurateRipV1 = ar[t].V1,
                    AccurateRipV2 = ar[t].V2,
                    CtdbTrackCrc = ctdb[t].Value
                };
                checks.Add(new TrackCheck(t, _tracks[t].FilePath, correctedPerTrack[t], info));
            }

            return new CorrectedChecksums(windowCrc.GetCurrentHashAsUInt32(), checks, trackStarts, TotalSamples);
        }

        /// <summary>Copies each changed track into <paramref name="folder"/> and patches the corrected samples in the copy.</summary>
        public Dictionary<int, string> WriteCorrectedCopies(
            IReadOnlyList<TrackCheck> changed,
            IReadOnlyList<(long Symbol, ushort Value)> corrections,
            string folder)
        {
            Directory.CreateDirectory(folder);
            var written = new Dictionary<int, string>();
            var pair = new byte[2];
            try
            {
                foreach (var t in changed)
                {
                    string source = _tracks[t.Index].FilePath;
                    string target = Path.Combine(folder, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: true);
                    written[t.Index] = target;

                    long first = _startBytes[t.Index];
                    long end = first + _dataLengths[t.Index];
                    using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    foreach (var (symbol, value) in corrections)
                    {
                        long b = 2 * symbol;
                        if (b < first || b >= end)
                        {
                            continue;
                        }

                        fs.Position = _dataOffsets[t.Index] + (b - first);
                        fs.ReadExactly(pair);
                        pair[0] ^= (byte)value;
                        pair[1] ^= (byte)(value >> 8);
                        fs.Position -= 2;
                        fs.Write(pair);
                    }
                }
            }
            catch
            {
                // Never leave half-patched copies behind.
                foreach (string path in written.Values)
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }

                TryDeleteEmptyDirectory(folder);
                throw;
            }

            return written;
        }

        private int TrackOfByte(long discByte)
        {
            for (int t = _startBytes.Length - 1; t >= 0; t--)
            {
                if (discByte >= _startBytes[t])
                {
                    return t;
                }
            }

            return 0;
        }
    }
}
