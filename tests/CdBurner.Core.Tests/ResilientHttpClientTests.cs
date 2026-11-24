using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Offline tests for the rate limit, 429/503 retries, size limits and tolerant parsing of the MusicBrainz, Cover Art
/// Archive, CTDB and AccurateRip clients. Every request goes to a scripted handler; waits go to a fake clock, so no
/// test sleeps or touches the network.
/// </summary>
public class ResilientHttpClientTests
{
    private const string ReleaseId = "f7cba0e8-61f5-41c9-b203-3c1bbf1b7db2";
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    /// <summary>A gate on a fake clock: every wait is recorded and advances the clock instead of sleeping.</summary>
    private sealed class FakeGate
    {
        private long _nowMs;
        public List<TimeSpan> Waits { get; } = [];
        public RequestGate Gate { get; }

        public FakeGate(TimeSpan interval) =>
            Gate = new RequestGate(interval, () => _nowMs, (span, _) =>
            {
                Waits.Add(span);
                _nowMs += (long)span.TotalMilliseconds;
                return Task.CompletedTask;
            });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RetryAfter(HttpStatusCode status, int seconds)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static string SearchBody(params string[] ids) =>
        "{\"releases\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"title\":\"Album\",\"score\":100}}")) + "]}";

    // ---- RequestGate ----

    [Fact]
    public async Task RequestGate_SpacesRequestsByTheIntervalAndNeverWaitsForTheFirst()
    {
        var fake = new FakeGate(TimeSpan.FromSeconds(1));

        await fake.Gate.WaitTurnAsync(CancellationToken.None);
        await fake.Gate.WaitTurnAsync(CancellationToken.None);
        await fake.Gate.WaitTurnAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], fake.Waits);
    }

    // ---- MusicBrainz ----

    [Fact]
    public async Task MusicBrainz_Search_HonoursRetryAfterOn503ThenSucceeds()
    {
        var handler = new ScriptedHandler((_, n) => n == 1
            ? RetryAfter(HttpStatusCode.ServiceUnavailable, 4)
            : Json(HttpStatusCode.OK, SearchBody(ReleaseId)));
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);
        var client = new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate);

        IReadOnlyList<string> ids = await client.SearchReleaseIdsAsync("Album", "Artist");

        Assert.Equal([ReleaseId], ids);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(4), fake.Waits);
    }

    [Fact]
    public async Task MusicBrainz_Search_RetriesOn429AndGivesUpAfterThreeTriesWithThatStatus()
    {
        var handler = new ScriptedHandler((_, _) => RetryAfter(HttpStatusCode.TooManyRequests, 2));
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);
        var client = new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchReleaseIdsAsync("Album", "Artist"));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(HttpClientResilience.MaxAttempts, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], fake.Waits);
    }

    [Fact]
    public async Task MusicBrainz_RetryAfterIsCappedSoAServerCannotStallTheClient()
    {
        var handler = new ScriptedHandler((_, n) => n == 1
            ? RetryAfter(HttpStatusCode.TooManyRequests, 3600)
            : Json(HttpStatusCode.OK, SearchBody()));
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);

        await new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate).SearchReleaseIdsAsync("Album", "Artist");

        Assert.Equal([HttpClientResilience.MaxRetryDelay], fake.Waits);
    }

    [Fact]
    public async Task MusicBrainz_WithoutRetryAfterWaitsTheDefaultDelay()
    {
        var handler = new ScriptedHandler((_, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(HttpStatusCode.OK, SearchBody()));
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);

        await new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate).SearchReleaseIdsAsync("Album", "Artist");

        Assert.Equal([HttpClientResilience.DefaultRetryDelay], fake.Waits);
    }

    [Fact]
    public async Task MusicBrainz_RequestsAreOneSecondApartAcrossLookupsOnTheSameGate()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, SearchBody()));
        var fake = new FakeGate(TimeSpan.FromSeconds(1));
        using var http = new HttpClient(handler);
        var client = new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate);

        await client.SearchReleaseIdsAsync("Album", "Artist");
        await client.SearchReleaseIdsAsync("Album", "Artist");
        await client.LookupDiscIdAsync("arIS30RPWowvwNEqsqdDnZzDGhk-");

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], fake.Waits);
    }

    [Fact]
    public async Task MusicBrainz_ServerErrorOtherThan429And503IsNotRetried()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => new MusicBrainzClient(http, "Test/1.0", gate: fake.Gate).SearchReleaseIdsAsync("Album", "Artist"));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MusicBrainz_LookupSkipsReleaseItemsWithoutAnIdAndKeepsTheValidOnes()
    {
        const string json = """
        { "releases": [
            { "title": "No id here" },
            42,
            { "id": 7, "title": "Numeric id" },
            { "id": "rel-good", "title": "Good Album", "artist-credit": [ { "name": "Band", "joinphrase": "" } ] }
        ] }
        """;
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, json));
        using var http = new HttpClient(handler);
        var client = new MusicBrainzClient(http, "Test/1.0", gate: new FakeGate(TimeSpan.Zero).Gate);

        var releases = await client.LookupDiscIdAsync("arIS30RPWowvwNEqsqdDnZzDGhk-");

        var release = Assert.Single(releases);
        Assert.Equal("rel-good", release.ReleaseId);
        Assert.Equal("Good Album", release.Title);
    }

    // ---- Cover Art Archive ----

    [Fact]
    public async Task CoverArt_RetriesOn503WithRetryAfterThenReturnsTheImage()
    {
        var handler = new ScriptedHandler((_, n) => n == 1
            ? RetryAfter(HttpStatusCode.ServiceUnavailable, 3)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        var fake = new FakeGate(TimeSpan.Zero);
        using var http = new HttpClient(handler);

        var image = await CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId, "Test/1.0", gate: fake.Gate);

        Assert.NotNull(image);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(3)], fake.Waits);
    }

    [Fact]
    public async Task CoverArt_ThrowsWithTheStatusWhen429Persists()
    {
        var handler = new ScriptedHandler((_, _) => RetryAfter(HttpStatusCode.TooManyRequests, 1));
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId, gate: new FakeGate(TimeSpan.Zero).Gate));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(HttpClientResilience.MaxAttempts, handler.Requests.Count);
    }

    // ---- CTDB ----

    private static readonly DiscIdentifiers Ids = new("mb", 0x12345678u, 1u, 2u, 1, "abc", "0:15000");

    [Fact]
    public async Task Ctdb_FetchSkipsAMalformedEntryAndKeepsTheValidOnes()
    {
        const string body = """
        <ctdb>
          <entry id="1" crc32="1f587dee" confidence="3" trackcrcs="1f587dee" />
          <entry id="2" crc32="not-hex" confidence="9" />
          <entry id="3" crc32="0c406894" confidence="-4" />
          <entry id="4" crc32="0c406894" confidence="2" trackcrcs="zz" />
          <entry id="5" crc32="cde83013" confidence="7" trackcrcs="cde83013" />
        </ctdb>
        """;
        using var http = new HttpClient(new ScriptedHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") }));

        var entries = await CueToolsDatabase.FetchAsync(http, Ids);

        Assert.Equal([1L, 5L], entries.Select(e => e.Id));
        Assert.Equal([0x1F587DEEu, 0xCDE83013u], entries.Select(e => e.Crc32));
    }

    [Fact]
    public async Task Ctdb_FetchRefusesABodyOverTheSizeLimitWithoutContentLength()
    {
        var huge = new byte[(int)CueToolsDatabase.MaxResponseBytes + 1];
        Array.Fill(huge, (byte)' ');
        using var http = new HttpClient(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new NoLengthContent(huge)
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => CueToolsDatabase.FetchAsync(http, Ids));
    }

    [Fact]
    public async Task Ctdb_FetchRefusesADeclaredLengthOverTheLimitBeforeReadingTheBody()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = CueToolsDatabase.MaxResponseBytes + 1;
        using var http = new HttpClient(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await Assert.ThrowsAsync<InvalidDataException>(() => CueToolsDatabase.FetchAsync(http, Ids));
    }

    [Fact]
    public async Task Ctdb_FetchStopsWhenTheCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var http = new HttpClient(new CancelAwareHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CueToolsDatabase.FetchAsync(http, Ids, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Ctdb_FetchCancelsWhileTheBodyIsStillDownloading()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(cts))
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CueToolsDatabase.FetchAsync(http, Ids, cancellationToken: cts.Token));
    }

    // ---- AccurateRip ----

    // One-track dBAR: header (track count, three disc IDs) then confidence, CRC v1, frame-450 CRC.
    private static byte[] OnePressingDbar(byte confidence, uint crc)
    {
        var bytes = new List<byte> { 1 };
        bytes.AddRange(BitConverter.GetBytes(0x11111111u));
        bytes.AddRange(BitConverter.GetBytes(0x22222222u));
        bytes.AddRange(BitConverter.GetBytes(0x33333333u));
        bytes.Add(confidence);
        bytes.AddRange(BitConverter.GetBytes(crc));
        bytes.AddRange(BitConverter.GetBytes(0u));
        return bytes.ToArray();
    }

    [Fact]
    public async Task AccurateRip_SendsAUserAgentAndRetriesATransient5xx()
    {
        var handler = new ScriptedHandler((_, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(OnePressingDbar(5, 0xAABBCCDDu)) });
        var waits = new List<TimeSpan>();
        using var http = new HttpClient(handler);

        var pressings = await AccurateRipDatabase.FetchAsync(
            http, Ids, retryDelay: (span, _) => { waits.Add(span); return Task.CompletedTask; }, userAgent: "Test/1.0");

        var pressing = Assert.Single(pressings);
        Assert.Equal(0xAABBCCDDu, pressing.Tracks[0].Crc);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(waits);
        Assert.All(handler.Requests, r => Assert.Equal("Test/1.0", r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task AccurateRip_GivesUpOnAPersistent5xxAfterThreeTries()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => AccurateRipDatabase.FetchAsync(
            http, Ids, retryDelay: (_, _) => Task.CompletedTask));

        Assert.Equal(HttpClientResilience.MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task AccurateRip_ADefaultUserAgentIsSentWhenNoneIsGiven()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        Assert.Empty(await AccurateRipDatabase.FetchAsync(http, Ids));

        Assert.StartsWith("IgnisDisc/", Assert.Single(handler.Requests).Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task AccurateRip_RefusesABodyOverTheSizeLimit()
    {
        var huge = new byte[(int)AccurateRipDatabase.MaxResponseBytes + 1];
        using var http = new HttpClient(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new NoLengthContent(huge)
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => AccurateRipDatabase.FetchAsync(http, Ids));
    }

    // ---- test doubles ----

    /// <summary>Streams its bytes without declaring a length, like a chunked response.</summary>
    private sealed class NoLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CancelAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<ctdb/>") });
        }
    }

    /// <summary>Yields a few XML bytes, then cancels the caller's token and waits, as a stalled download would.</summary>
    private sealed class StallingStream(CancellationTokenSource cts) : Stream
    {
        private readonly byte[] _head = Encoding.UTF8.GetBytes("<ctdb>");
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent < _head.Length)
            {
                int n = Math.Min(buffer.Length, _head.Length - _sent);
                _head.AsMemory(_sent, n).CopyTo(buffer);
                _sent += n;
                return n;
            }

            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
