using System.Net;

namespace CdBurner.Core.Audio;

/// <summary>A front cover image downloaded from the Cover Art Archive.</summary>
public sealed record CoverArtImage(byte[] Bytes, string MimeType);

/// <summary>Downloads the front cover of a MusicBrainz release from the Cover Art Archive.</summary>
public static class CoverArtArchiveClient
{
    public const string DefaultBaseUrl = "https://coverartarchive.org/";

    /// <summary>
    /// Fetches <c>{base}release/{id}/front-500</c> (redirects are followed by the HttpClient). Returns null when the
    /// release has no cover (404), when the body is not a JPEG, PNG, GIF or WebP image, or when it is larger than a
    /// FLAC file can embed. HTTP 429 and 503 are retried after the <c>Retry-After</c> wait; if they persist the call throws
    /// <see cref="HttpRequestException"/> with that status. Requests are spaced by <paramref name="gate"/> (default
    /// <see cref="RequestGate.Shared"/>). Other network failures throw as <see cref="HttpClient"/> does.
    /// </summary>
    /// <exception cref="ArgumentException">The release ID is not a GUID (it is part of the URL, so it is checked).</exception>
    public static async Task<CoverArtImage?> FetchFrontAsync(
        HttpClient http,
        string releaseId,
        string? userAgent = null,
        string baseUrl = DefaultBaseUrl,
        CancellationToken cancellationToken = default,
        RequestGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!Guid.TryParseExact(releaseId?.Trim(), "D", out Guid id))
        {
            throw new ArgumentException("The release ID must be a MusicBrainz release GUID.", nameof(releaseId));
        }

        string root = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        string url = $"{root}release/{id:D}/front-500";
        using var response = await HttpClientResilience.SendWithRetryAsync(
            http,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                }

                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            HttpClientResilience.IsThrottled,
            gate ?? RequestGate.Shared,
            delay: null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && FlacEncoder.CoverArtSizeProblem((int)Math.Min(length, int.MaxValue)) != null)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > FlacEncoder.MaxCoverArtBytes)
            {
                return null;
            }
        }

        byte[] bytes = buffer.ToArray();
        string? mime = FlacEncoder.DetectImageMimeType(bytes);
        return mime == null ? null : new CoverArtImage(bytes, mime);
    }
}
