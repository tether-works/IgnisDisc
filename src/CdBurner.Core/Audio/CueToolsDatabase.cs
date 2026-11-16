using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using CdBurner.Core.Images;

namespace CdBurner.Core.Audio;

/// <summary>
/// Incremental CUETools Database (CTDB) CRC-32 checksum calculator over 16-bit stereo PCM audio.
/// <para>
/// CTDB hashes the interior of the disc's audio data with a 10-sector (5,880-sample) lead-in exclusion,
/// a 10..19.999-sector tail exclusion so the hashed span is an exact multiple of 10 sectors (23,520 bytes),
/// and a per-track slice that combines across all audio tracks via <see cref="Crc32Algorithm.Combine"/>.
/// Algorithms reimplemented from CUETools (<c>https://github.com/gchudov/cuetools.net</c>, GPL-2.0-or-later).
/// </para>
/// </summary>
public sealed class CtdbChecksum
{
    /// <summary>Stereo 16-bit PCM sample pairs per Red Book audio sector (44,100 Hz / 75 sectors/s).</summary>
    public const int SamplesPerSector = 588;

    /// <summary>Number of sectors per CTDB stride.</summary>
    public const int StrideSectors = 10;

    /// <summary>Stereo sample pairs per CTDB stride (5,880 stereo samples = 23,520 bytes).</summary>
    public const int StrideSamples = SamplesPerSector * StrideSectors;

    private readonly long _startByte;
    private readonly long _endByte;
    private long _bytesSeen;
    private readonly Crc32Algorithm _crc32 = new();

    private CtdbChecksum(long localStartSample, long localEndSample)
    {
        _startByte = localStartSample * 4;
        _endByte = localEndSample * 4;
    }

    /// <summary>
    /// Computes the half-open whole-disc sample window <c>[StartSample, EndSample)</c> covered by CTDB CRC-32.
    /// Returns <c>(0, 0)</c> when the disc has fewer than <c>2 * StrideSamples</c> stereo samples.
    /// </summary>
    public static (long StartSample, long EndSample) GetDiscSampleWindow(long totalDiscAudioSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDiscAudioSamples);

        if (totalDiscAudioSamples < 2L * StrideSamples)
        {
            return (0, 0);
        }

        long suffix = StrideSamples + (totalDiscAudioSamples % StrideSamples);
        long end = totalDiscAudioSamples - suffix;
        return end > StrideSamples ? (StrideSamples, end) : (0, 0);
    }

    /// <summary>
    /// Computes the track-local half-open sample window <c>[LocalStartSample, LocalEndSample)</c> covered by
    /// CTDB CRC-32 for a track starting at <paramref name="trackStartSampleInDisc"/>.
    /// </summary>
    public static (long LocalStartSample, long LocalEndSample) GetTrackSampleWindow(
        long trackStartSampleInDisc,
        long trackTotalSamples,
        long totalDiscAudioSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trackStartSampleInDisc);
        ArgumentOutOfRangeException.ThrowIfNegative(trackTotalSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(totalDiscAudioSamples);

        var (discStart, discEnd) = GetDiscSampleWindow(totalDiscAudioSamples);
        if (discEnd <= discStart)
        {
            return (0, 0);
        }

        long localStart = Math.Clamp(discStart - trackStartSampleInDisc, 0, trackTotalSamples);
        long localEnd = Math.Clamp(discEnd - trackStartSampleInDisc, localStart, trackTotalSamples);
        return (localStart, localEnd);
    }

    /// <summary>Creates an incremental CTDB checksum calculator for one track in a full-disc audio layout.</summary>
    public static CtdbChecksum ForTrack(long trackStartSampleInDisc, long trackTotalSamples, long totalDiscAudioSamples)
    {
        var (localStart, localEnd) = GetTrackSampleWindow(trackStartSampleInDisc, trackTotalSamples, totalDiscAudioSamples);
        return new CtdbChecksum(localStart, localEnd);
    }

    /// <summary>Creates an incremental CTDB checksum calculator for a whole-disc contiguous audio stream.</summary>
    public static CtdbChecksum ForDisc(long totalDiscAudioSamples)
    {
        var (start, end) = GetDiscSampleWindow(totalDiscAudioSamples);
        return new CtdbChecksum(start, end);
    }

    /// <summary>Number of PCM bytes covered by this checksum window.</summary>
    public long CoveredBytes => _endByte - _startByte;

    /// <summary>CTDB CRC-32 value of the covered PCM bytes appended so far.</summary>
    public uint Value => _crc32.GetCurrentHashAsUInt32();

    /// <summary>Appends PCM bytes. Chunks need not be sample- or sector-aligned.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        long chunkStart = _bytesSeen;
        long chunkEnd = chunkStart + pcm.Length;
        _bytesSeen = chunkEnd;

        if (_endByte <= _startByte)
        {
            return;
        }

        long overlapStart = Math.Max(chunkStart, _startByte);
        long overlapEnd = Math.Min(chunkEnd, _endByte);
        if (overlapEnd > overlapStart)
        {
            int offset = (int)(overlapStart - chunkStart);
            int count = (int)(overlapEnd - overlapStart);
            _crc32.Append(pcm.Slice(offset, count));
        }
    }

    /// <summary>
    /// Combines per-track CTDB CRC-32 values in disc audio order into the whole-disc CTDB CRC-32
    /// without re-reading the audio files.
    /// </summary>
    public static uint CombineTrackCrcs(
        IReadOnlyList<AudioTrackRipInfo> trackInfos,
        IReadOnlyList<long> trackStartSamplesByAudioIndex,
        long totalDiscAudioSamples)
    {
        ArgumentNullException.ThrowIfNull(trackInfos);
        ArgumentNullException.ThrowIfNull(trackStartSamplesByAudioIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(totalDiscAudioSamples);

        uint acc = 0u;
        foreach (var info in trackInfos.OrderBy(t => t.AudioTrackIndex))
        {
            if (info.AudioTrackIndex < 0 || info.AudioTrackIndex >= trackStartSamplesByAudioIndex.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(trackInfos),
                    $"AudioTrackIndex {info.AudioTrackIndex} is out of range for {trackStartSamplesByAudioIndex.Count} audio tracks.");
            }

            long trackStart = trackStartSamplesByAudioIndex[info.AudioTrackIndex];
            long nextTrackStart = info.AudioTrackIndex + 1 < trackStartSamplesByAudioIndex.Count
                ? trackStartSamplesByAudioIndex[info.AudioTrackIndex + 1]
                : totalDiscAudioSamples;
            long trackTotalSamples = Math.Max(0, nextTrackStart - trackStart);

            var (localStart, localEnd) = GetTrackSampleWindow(trackStart, trackTotalSamples, totalDiscAudioSamples);
            long coveredBytes = (localEnd - localStart) * 4;
            if (coveredBytes > 0)
            {
                acc = Crc32Algorithm.Combine(acc, info.CtdbTrackCrc, coveredBytes);
            }
        }

        return acc;
    }
}

/// <summary>One submission entry returned by the CUETools Database (<c>lookup2.php</c>).</summary>
public sealed record CtdbEntry(
    long Id,
    uint Crc32,
    int Confidence,
    IReadOnlyList<uint> TrackCrcs,
    int? Npar = null,
    int? RawStride = null,
    string? HasParity = null,
    string? ParityBase64 = null,
    string? SyndromeBase64 = null,
    string? Toc = null);

/// <summary>Result of checking one ripped track against the CUETools Database entries.</summary>
public sealed record CtdbTrackVerdict(bool IsAccurate, int Confidence, int TotalSubmissions);

/// <summary>Aggregated disc and per-track verification summary against the CUETools Database.</summary>
public sealed record CtdbVerificationSummary(
    bool DiscMatched,
    int DiscConfidence,
    int TotalSubmissions,
    IReadOnlyList<CtdbTrackVerdict> Tracks);

/// <summary>
/// Client and XML parser for the CUETools Database (<c>db.cuetools.net/lookup2.php</c>).
/// Protocol and matching semantics derived from CUETools (<c>https://github.com/gchudov/cuetools.net</c>, GPL-2.0-or-later).
/// </summary>
public static class CueToolsDatabase
{
    /// <summary>Default CUETools Database endpoint.</summary>
    public const string DefaultBaseUrl = "http://db.cuetools.net/";

    /// <summary>Builds the <c>lookup2.php</c> query URI for <paramref name="ids"/>.</summary>
    public static Uri BuildLookupUri(DiscIdentifiers ids, string baseUrl = DefaultBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (string.IsNullOrWhiteSpace(ids.CtdbToc))
        {
            throw new ArgumentException("DiscIdentifiers.CtdbToc must not be empty.", nameof(ids));
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must not be empty.", nameof(baseUrl));
        }

        return new Uri(
            $"{baseUrl.TrimEnd('/')}/lookup2.php?version=3&ctdb=1&metadata=none&fuzzy=0&toc={Uri.EscapeDataString(ids.CtdbToc)}");
    }

    /// <summary>Parses a CTDB v3 XML response string into <see cref="CtdbEntry"/> records.</summary>
    public static IReadOnlyList<CtdbEntry> ParseXml(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, CreateSafeXmlSettings());
        return ParseXmlCore(xmlReader);
    }

    /// <summary>Parses a CTDB v3 XML response stream into <see cref="CtdbEntry"/> records.</summary>
    public static IReadOnlyList<CtdbEntry> ParseXml(Stream xmlStream)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);
        using var xmlReader = XmlReader.Create(xmlStream, CreateSafeXmlSettings());
        return ParseXmlCore(xmlReader);
    }

    private static XmlReaderSettings CreateSafeXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    private static IReadOnlyList<CtdbEntry> ParseXmlCore(XmlReader reader)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Invalid CTDB XML response: {ex.Message}", ex);
        }

        var root = doc.Root;
        if (root == null || root.Name.LocalName != "ctdb")
        {
            throw new InvalidDataException("CTDB XML response is missing the <ctdb> root element.");
        }

        int? defaultNpar = ParseOptionalInt(root.Attribute("npar")?.Value, "npar");
        var entries = new List<CtdbEntry>();

        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName != "entry")
            {
                continue;
            }

            string? crcRaw = element.Attribute("crc32")?.Value;
            if (string.IsNullOrWhiteSpace(crcRaw) ||
                !uint.TryParse(crcRaw.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc32))
            {
                throw new InvalidDataException($"CTDB <entry> has missing or invalid crc32 attribute '{crcRaw}'.");
            }

            string? confRaw = element.Attribute("confidence")?.Value;
            if (string.IsNullOrWhiteSpace(confRaw) ||
                !int.TryParse(confRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int confidence) ||
                confidence < 0)
            {
                throw new InvalidDataException($"CTDB <entry> has missing or invalid confidence attribute '{confRaw}'.");
            }

            long id = ParseOptionalLong(element.Attribute("id")?.Value, "id") ?? 0;
            int? npar = ParseOptionalInt(element.Attribute("npar")?.Value, "npar") ?? defaultNpar;
            int? rawStride = ParseOptionalInt(element.Attribute("stride")?.Value, "stride");

            uint[] trackCrcs = Array.Empty<uint>();
            string? trackCrcsRaw = element.Attribute("trackcrcs")?.Value;
            if (!string.IsNullOrWhiteSpace(trackCrcsRaw))
            {
                string[] tokens = trackCrcsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                trackCrcs = new uint[tokens.Length];
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (!uint.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint tcrc))
                    {
                        throw new InvalidDataException($"CTDB <entry> has invalid trackcrcs token '{tokens[i]}'.");
                    }
                    trackCrcs[i] = tcrc;
                }
            }

            entries.Add(new CtdbEntry(
                Id: id,
                Crc32: crc32,
                Confidence: confidence,
                TrackCrcs: trackCrcs,
                Npar: npar,
                RawStride: rawStride,
                HasParity: element.Attribute("hasparity")?.Value,
                ParityBase64: element.Attribute("parity")?.Value,
                SyndromeBase64: element.Attribute("syndrome")?.Value,
                Toc: element.Attribute("toc")?.Value));
        }

        return entries;
    }

    private static int? ParseOptionalInt(string? raw, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException($"CTDB attribute '{attributeName}' is not a valid integer: '{raw}'.");
        }
        return value;
    }

    private static long? ParseOptionalLong(string? raw, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            throw new InvalidDataException($"CTDB attribute '{attributeName}' is not a valid integer: '{raw}'.");
        }
        return value;
    }

    /// <summary>
    /// Matches one ripped audio track against CTDB entries. Falls back to whole-disc CRC matching for legacy
    /// entries without <c>trackcrcs</c> when <paramref name="discCtdbCrc"/> is supplied.
    /// </summary>
    public static CtdbTrackVerdict MatchTrack(
        IReadOnlyList<CtdbEntry> entries,
        int audioTrackIndex,
        uint trackCtdbCrc,
        uint? discCtdbCrc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int matched = 0;
        int total = 0;

        foreach (var entry in entries)
        {
            if (entry.TrackCrcs.Count > 0)
            {
                if (audioTrackIndex >= 0 && audioTrackIndex < entry.TrackCrcs.Count)
                {
                    total += entry.Confidence;
                    if (entry.TrackCrcs[audioTrackIndex] == trackCtdbCrc)
                    {
                        matched += entry.Confidence;
                    }
                }
            }
            else if (discCtdbCrc.HasValue)
            {
                total += entry.Confidence;
                if (entry.Crc32 == discCtdbCrc.Value)
                {
                    matched += entry.Confidence;
                }
            }
        }

        return new CtdbTrackVerdict(matched > 0, matched, total);
    }

    /// <summary>Matches the whole-disc CTDB CRC-32 against all entries.</summary>
    public static (bool Matched, int Confidence, int TotalSubmissions) MatchDisc(
        IReadOnlyList<CtdbEntry> entries,
        uint? discCtdbCrc)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int total = 0;
        int matched = 0;
        foreach (var entry in entries)
        {
            total += entry.Confidence;
            if (discCtdbCrc is uint crc && entry.Crc32 == crc)
            {
                matched += entry.Confidence;
            }
        }

        return (matched > 0, matched, total);
    }

    /// <summary>
    /// Fetches and parses CTDB entries for <paramref name="ids"/>. Returns an empty list on HTTP 404.
    /// </summary>
    public static async Task<IReadOnlyList<CtdbEntry>> FetchAsync(
        HttpClient httpClient,
        DiscIdentifiers ids,
        string? userAgent = null,
        string baseUrl = DefaultBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(ids);

        var uri = BuildLookupUri(ids, baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? MusicBrainzClient.DefaultUserAgent : userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<CtdbEntry>();
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return ParseXml(stream);
    }
}
