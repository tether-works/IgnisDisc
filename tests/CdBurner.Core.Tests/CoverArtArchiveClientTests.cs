using System.Net;
using System.Text;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

public class CoverArtArchiveClientTests
{
    private const string ReleaseId = "f7cba0e8-61f5-41c9-b203-3c1bbf1b7db2";

    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.UserAgent.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task FetchFrontAsync_RequestsFront500AndReturnsPng()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        using var http = new HttpClient(handler);

        var image = await CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId, "TestAgent/1.0");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Png, image.Bytes);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://coverartarchive.org/release/{ReleaseId}/front-500", request.Uri.AbsoluteUri);
        Assert.Contains("TestAgent/1.0", request.UserAgent);
    }

    [Fact]
    public async Task FetchFrontAsync_ReturnsNullOn404()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId));
    }

    [Fact]
    public async Task FetchFrontAsync_ReturnsNullForABodyThatIsNotAnImage()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not found</html>", Encoding.UTF8, "text/html")
        }));

        Assert.Null(await CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId));
    }

    [Fact]
    public async Task FetchFrontAsync_ThrowsOnServerError()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(() => CoverArtArchiveClient.FetchFrontAsync(http, ReleaseId));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task FetchFrontAsync_RejectsAnIdThatIsNotAGuid(string id)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => CoverArtArchiveClient.FetchFrontAsync(http, id));
        Assert.Empty(handler.Requests);
    }
}
