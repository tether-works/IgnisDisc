using System.Diagnostics;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Burning;

/// <summary>
/// Reads the media status and, while the drive is still loading a disc (<see cref="MediaStatusDto.IsLoading"/>),
/// asks again until it has settled or the wait runs out. Used by the burn and erase paths so a burn started right
/// after closing the tray does not refuse a blank disc as "finished".
/// </summary>
public static class MediaStatusWait
{
    /// <summary>How long a burn waits for a disc that is still loading before it gives up.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(20);

    /// <summary>How often the status is read again while waiting.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Message for a disc that was still loading when the wait ran out.</summary>
    public const string StillLoadingMessage =
        "The drive is still reading the disc. Wait a few seconds and try again; the disc was not changed.";

    /// <summary>
    /// Returns the media status once the disc is no longer loading, or the last (still loading) status when
    /// <paramref name="maxWait"/> runs out. Returns at once when the first read is not loading.
    /// </summary>
    public static async Task<MediaStatusDto> QueryWhenLoadedAsync(
        IDiscRecorderBackend recorder,
        CancellationToken cancellationToken = default,
        TimeSpan? maxWait = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var status = recorder.QueryMediaStatus();
        if (!status.IsLoading)
        {
            return status;
        }

        TimeSpan limit = maxWait ?? DefaultMaxWait;
        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        var clock = Stopwatch.StartNew();
        while (status.IsLoading && clock.Elapsed < limit)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            status = recorder.QueryMediaStatus();
        }

        return status;
    }
}
