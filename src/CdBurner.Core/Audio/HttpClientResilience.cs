using System.Diagnostics;
using System.Net;

namespace CdBurner.Core.Audio;

/// <summary>
/// Spaces requests to one web service at least <see cref="Interval"/> apart (MusicBrainz allows one request per second
/// per client) and supplies the wait used between retries. The clock and the wait are replaceable so tests run
/// without sleeping. One gate instance may be shared by several clients; it is safe to call from several threads.
/// </summary>
public sealed class RequestGate
{
    /// <summary>The gate the MusicBrainz and Cover Art Archive clients share by default: one request per second.</summary>
    public static RequestGate Shared { get; } = new(TimeSpan.FromSeconds(1));

    private readonly object _lock = new();
    private readonly Func<long> _nowMs;
    private long _nextSlotMs;

    public RequestGate(TimeSpan interval, Func<long>? nowMilliseconds = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval cannot be negative.");
        }

        Interval = interval;
        _nowMs = nowMilliseconds ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        DelayAsync = delay ?? ((span, token) => Task.Delay(span, token));
        _nextSlotMs = long.MinValue;
    }

    public TimeSpan Interval { get; }

    /// <summary>Waits for <paramref name="span"/>; used for the spacing and for retry back-off.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; }

    /// <summary>Returns when the caller may send its next request. Each caller reserves its own slot.</summary>
    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        long wait;
        lock (_lock)
        {
            long now = _nowMs();
            long slot = _nextSlotMs == long.MinValue ? now : Math.Max(now, _nextSlotMs);
            _nextSlotMs = slot + (long)Interval.TotalMilliseconds;
            wait = slot - now;
        }

        if (wait > 0)
        {
            await DelayAsync(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Retry and size-limit helpers shared by the online metadata and verification clients.</summary>
public static class HttpClientResilience
{
    /// <summary>Total tries for one request: the first plus two retries.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Longest wait honoured from a <c>Retry-After</c> header; a longer request is capped.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Wait used when a 429/503 response carries no usable <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(1100);

    public static bool IsThrottled(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    public static bool IsTransientServerError(HttpStatusCode status) =>
        status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Sends the request built by <paramref name="createRequest"/> (a request message cannot be sent twice, so each
    /// try builds a fresh one). When the answer is a status <paramref name="isRetryable"/> accepts, waits (the
    /// <c>Retry-After</c> header, capped at <see cref="MaxRetryDelay"/>, else <see cref="DefaultRetryDelay"/>) and tries
    /// again, up to <see cref="MaxAttempts"/> tries. The last response is returned whatever its status, so the caller
    /// decides what a final 429/503 means. The caller disposes the response.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> createRequest,
        HttpCompletionOption completion,
        Func<HttpStatusCode, bool> isRetryable,
        RequestGate? gate,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentNullException.ThrowIfNull(isRetryable);

        Func<TimeSpan, CancellationToken, Task> wait = delay ?? gate?.DelayAsync ?? ((span, token) => Task.Delay(span, token));
        for (int attempt = 1; ; attempt++)
        {
            if (gate != null)
            {
                await gate.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
            }

            using HttpRequestMessage request = createRequest();
            HttpResponseMessage response = await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if (attempt >= MaxAttempts || !isRetryable(response.StatusCode))
            {
                return response;
            }

            TimeSpan pause = ReadRetryAfter(response);
            response.Dispose();
            await wait(pause, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The wait asked for by <c>Retry-After</c> (whole seconds, or an HTTP date), clamped to zero..<see cref="MaxRetryDelay"/>;
    /// <see cref="DefaultRetryDelay"/> when the header is missing or unreadable.
    /// </summary>
    public static TimeSpan ReadRetryAfter(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var header = response.Headers.RetryAfter;
        TimeSpan? asked = header?.Delta ?? (header?.Date is { } date ? date - (now ?? DateTimeOffset.UtcNow) : null);
        if (asked is not { } span)
        {
            return DefaultRetryDelay;
        }

        if (span < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return span > MaxRetryDelay ? MaxRetryDelay : span;
    }

    /// <summary>
    /// Reads the whole response body but refuses more than <paramref name="maxBytes"/>: a declared
    /// <c>Content-Length</c> over the limit fails before any download, and a body without one is cut off at the limit.
    /// </summary>
    /// <exception cref="InvalidDataException">The body is larger than the limit.</exception>
    public static async Task<byte[]> ReadBodyLimitedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfDeclaredTooLarge(content, maxBytes);

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"The response is larger than the {maxBytes}-byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when the declared <c>Content-Length</c> exceeds the limit.</summary>
    public static void ThrowIfDeclaredTooLarge(HttpContent content, long maxBytes)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new InvalidDataException($"The response declares {length} bytes, more than the {maxBytes}-byte limit.");
        }
    }

    /// <summary>The HTTP error for a final unsuccessful status, carrying the status code for callers to inspect.</summary>
    public static HttpRequestException StatusError(string service, HttpStatusCode status) =>
        new($"{service} request failed with HTTP status code {(int)status}.", null, status);
}
