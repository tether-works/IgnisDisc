using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using CdBurner.Core.Audio;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Answers like db.cuetools.net (one entry with parity for <c>cleanPcm</c>), p.cuetools.net (its parity file, honouring
/// the byte range) and accuraterip.com (one pressing whose v2 checksums are <c>accurateRipV2</c>).
/// </summary>
internal sealed class FakeCtdbServer : HttpMessageHandler
{
    private readonly string _xml;
    private readonly byte[] _parityFile;
    private readonly byte[] _dbar;

    public FakeCtdbServer(byte[] cleanPcm, string discCrcHex, IReadOnlyList<uint> accurateRipV2)
    {
        const int stride = CtdbParityLayout.Stride;
        const int npar = 16;
        var accumulator = new CtdbSyndromeAccumulator(new CtdbParityLayout(cleanPcm.Length / 4), npar);
        accumulator.Append(cleanPcm);
        ushort[] syndromes = accumulator.GetShiftedSyndromes(0, npar);

        _parityFile = new byte[npar * stride * 2];
        for (int i = 0; i < npar; i++)
        {
            for (int c = 0; c < stride; c++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_parityFile.AsSpan(2 * (i * stride + c)), syndromes[c * npar + i]);
            }
        }

        byte[] inline = new byte[2 * npar];
        for (int i = 0; i < npar; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(inline.AsSpan(2 * i), syndromes[i]);
        }

        _xml = $"""<ctdb><entry id="77" crc32="{discCrcHex.ToLowerInvariant()}" confidence="9" npar="{npar}" stride="5880" hasparity="http://p.cuetools.net/77" syndrome="{Convert.ToBase64String(inline)}" /></ctdb>""";

        _dbar = new byte[13 + accurateRipV2.Count * 9];
        _dbar[0] = (byte)accurateRipV2.Count;
        for (int t = 0; t < accurateRipV2.Count; t++)
        {
            _dbar[13 + t * 9] = 5;
            BinaryPrimitives.WriteUInt32LittleEndian(_dbar.AsSpan(14 + t * 9), accurateRipV2[t]);
        }
    }

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        HttpResponseMessage response;
        switch (request.RequestUri!.Host)
        {
            case "db.cuetools.net":
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_xml, Encoding.UTF8, "application/xml") };
                break;
            case "p.cuetools.net":
                var range = request.Headers.Range!.Ranges.Single();
                int from = (int)range.From!.Value;
                int to = (int)Math.Min(range.To!.Value, _parityFile.Length - 1);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_parityFile, from, to - from + 1) };
                break;
            case "www.accuraterip.com":
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_dbar) };
                break;
            default:
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
                break;
        }

        return Task.FromResult(response);
    }
}
