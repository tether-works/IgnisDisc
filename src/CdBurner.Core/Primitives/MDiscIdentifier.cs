namespace CdBurner.Core.Primitives;

/// <summary>
/// Recognises M-DISC media from the manufacturer ID the drive reads off the disc. M-DISC is not a separate MMC
/// profile: a DVD M-DISC reports as DVD±R and a BD M-DISC as BD-R, so the media ID is the only sign.
/// </summary>
public static class MDiscIdentifier
{
    /// <summary>
    /// Media ID prefixes that belong to M-DISC media. Millenniata made only M-DISC, so its whole code counts.
    /// Verbatim-made M-DISC BD-R carries Verbatim's own codes: read from a real disc on an LG BP50NB40 (2026-11-26) it
    /// gives VERBAT-IMe, the same media ID and Disc Information as a plain Verbatim BD-R. So VERBAT-IMe is never added:
    /// a Verbatim M-DISC BD-R cannot be told apart by the drive, and an ordinary Verbatim BD-R must not be called archival.
    /// </summary>
    public static IReadOnlyList<string> KnownMediaIdPrefixes { get; } = ["MILLEN"];

    /// <summary>True when the media ID (raw code or the readable summary that starts with it) is a known M-DISC code.</summary>
    public static bool IsMDisc(string? mediaManufacturerId)
    {
        if (string.IsNullOrWhiteSpace(mediaManufacturerId))
        {
            return false;
        }

        string id = mediaManufacturerId.TrimStart();
        foreach (string prefix in KnownMediaIdPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
