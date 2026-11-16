using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Audio;

/// <summary>
/// Sub-channel properties discovered for a single Red Book audio track.
/// </summary>
public sealed record DetectedTrackSubcode(
    int TrackNumber,
    int? Index00Lba,
    bool PreEmphasis,
    bool CopyPermitted,
    string? Isrc);

/// <summary>
/// Disc-wide Media Catalog Number and per-track sub-channel properties detected from Q sub-channel data.
/// </summary>
public sealed record DiscSubcodeMap(
    string? MediaCatalogNumber,
    IReadOnlyDictionary<int, DetectedTrackSubcode> Tracks);

/// <summary>
/// Detects per-track INDEX 00 pre-gap boundaries, control flags (PRE/DCP), Media Catalog Number, and ISRC codes
/// from Q sub-channel frames using binary search across inter-track boundaries.
/// </summary>
public static class AudioGapDetector
{
    private static readonly int[] ProbeOffsets = { 0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5 };

    public static DiscSubcodeMap Detect(
        IReadOnlyList<AudioDiscoveredTrack> orderedAudioTracks,
        IAudioSubChannelSession? session,
        Action<bool>? configureBcd = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedAudioTracks);

        string? mcn = NormalizeMcn(session?.ReadMediaCatalogNumber());

        if (session != null && configureBcd != null)
        {
            var t10 = orderedAudioTracks.FirstOrDefault(t => t.TrackNumber >= 10);
            if (t10 != null)
            {
                int min10 = t10.StartLba;
                int max10 = t10.StartLba + Math.Max(0, t10.SectorCount - 1);
                int target10 = Math.Min(t10.StartLba + 4, max10);
                if (TryProbePosition(session, target10, min10, max10, out var probe10, out _))
                {
                    int expectedBcd = ((t10.TrackNumber / 10) << 4) | (t10.TrackNumber % 10);
                    if (probe10.TrackNumber == expectedBcd && probe10.TrackNumber != t10.TrackNumber)
                    {
                        configureBcd(true);
                    }
                }
            }
        }

        var tracks = new Dictionary<int, DetectedTrackSubcode>(orderedAudioTracks.Count);
        for (int i = 0; i < orderedAudioTracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = orderedAudioTracks[i];

            int minTrackLba = t.StartLba;
            int maxTrackLba = t.StartLba + Math.Max(0, t.SectorCount - 1);
            int insideTarget = Math.Min(t.StartLba + 4, maxTrackLba);
            QSubChannelFrame insideFrame = default;
            bool gotInside = session != null &&
                             TryProbePosition(session, insideTarget, minTrackLba, maxTrackLba, out insideFrame, out _);

            bool preEmphasis = t.PreEmphasis ||
                               (gotInside && insideFrame.TrackNumber == t.TrackNumber && insideFrame.PreEmphasis);
            bool copyPermitted = t.CopyPermitted ||
                                 (gotInside && insideFrame.TrackNumber == t.TrackNumber && insideFrame.CopyPermitted);
            string? isrc = !string.IsNullOrWhiteSpace(t.Isrc) ? t.Isrc : session?.ReadTrackIsrc(t.TrackNumber);

            int? index00Lba = null;
            if (i >= 1 && orderedAudioTracks[i - 1].TrackNumber == t.TrackNumber - 1 && session != null)
            {
                var prev = orderedAudioTracks[i - 1];
                int searchMin = prev.StartLba + 1;
                int searchMax = t.StartLba - 1;
                if (searchMin <= searchMax &&
                    TryProbePosition(session, searchMax, searchMin, searchMax, out var endFrame, out int endActual) &&
                    endFrame.TrackNumber >= t.TrackNumber)
                {
                    int lo = searchMin;
                    int hi = endActual;
                    int near = Math.Max(searchMin, t.StartLba - 225);
                    if (near > lo &&
                        TryProbePosition(session, near, searchMin, hi, out var nearFrame, out int nearActual) &&
                        nearFrame.TrackNumber < t.TrackNumber)
                    {
                        lo = nearActual + 1;
                    }

                    int? firstIndex00Lba = (endFrame.TrackNumber == t.TrackNumber && endFrame.IndexNumber == 0)
                        ? endActual
                        : null;

                    while (lo <= hi)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int mid = lo + ((hi - lo) / 2);
                        if (!TryProbePosition(session, mid, lo, hi, out var q, out int actual))
                        {
                            break;
                        }

                        if (q.TrackNumber > t.TrackNumber || (q.TrackNumber == t.TrackNumber && q.IndexNumber >= 1))
                        {
                            hi = actual - 1;
                        }
                        else if (q.TrackNumber == t.TrackNumber && q.IndexNumber == 0)
                        {
                            firstIndex00Lba = firstIndex00Lba.HasValue
                                ? Math.Min(firstIndex00Lba.Value, actual)
                                : actual;
                            hi = actual - 1;
                        }
                        else
                        {
                            lo = actual + 1;
                        }
                    }

                    index00Lba = firstIndex00Lba;
                }
            }

            tracks[t.TrackNumber] = new DetectedTrackSubcode(t.TrackNumber, index00Lba, preEmphasis, copyPermitted, isrc);
        }

        return new DiscSubcodeMap(mcn, tracks);
    }

    internal static string? NormalizeMcn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string s = raw.Trim();
        if (s.Length == 12 && s.All(char.IsAsciiDigit))
        {
            s = "0" + s;
        }

        return s.Length == 13 && s.All(char.IsAsciiDigit) && s.Any(c => c != '0')
            ? s
            : null;
    }

    private static bool TryProbePosition(
        IAudioSubChannelSession session,
        int targetLba,
        int minLba,
        int maxLba,
        out QSubChannelFrame frame,
        out int actualLba)
    {
        for (int i = 0; i < ProbeOffsets.Length; i++)
        {
            int candidate = targetLba + ProbeOffsets[i];
            if (candidate < minLba || candidate > maxLba)
            {
                continue;
            }

            if (session.TryReadQSubChannel(candidate, out frame) && frame.IsPosition)
            {
                actualLba = candidate;
                return true;
            }
        }

        frame = default;
        actualLba = targetLba;
        return false;
    }
}
