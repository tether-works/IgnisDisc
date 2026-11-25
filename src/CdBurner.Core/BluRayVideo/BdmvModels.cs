namespace CdBurner.Core.BluRayVideo;

/// <summary>The disc the BDMV tree is meant for; decides the UDF packaging and the stream-file names.</summary>
public enum BdmvDiscTarget
{
    /// <summary>BD-R / BD-RE: BDMV tree on a UDF 2.50 volume, <c>.m2ts</c> / <c>.clpi</c> / <c>.mpls</c> names.</summary>
    BluRay,

    /// <summary>AVCHD on DVD-R / DVD+R DL: the same tree with 8.3 names (<c>.MTS</c> / <c>.CPI</c> / <c>.MPL</c>) and version 0100 files.</summary>
    Avchd,
}

/// <summary>Audio encoding of the main track.</summary>
public enum BdmvAudioCodec
{
    /// <summary>Dolby Digital (stream type 0x81).</summary>
    Ac3,

    /// <summary>Uncompressed 16-bit 48 kHz stereo (stream type 0x80, FFmpeg's <c>pcm_bluray</c>).</summary>
    Lpcm,
}

/// <summary>Frame rates a Blu-ray video stream may use, with the code used in the BDMV files.</summary>
public enum BdmvFrameRate
{
    /// <summary>24000/1001 frames per second (code 1).</summary>
    Fps23976 = 1,

    /// <summary>24 frames per second (code 2).</summary>
    Fps24 = 2,

    /// <summary>25 frames per second (code 3).</summary>
    Fps25 = 3,

    /// <summary>30000/1001 frames per second (code 4).</summary>
    Fps2997 = 4,

    /// <summary>50 frames per second (code 6).</summary>
    Fps50 = 6,

    /// <summary>60000/1001 frames per second (code 7).</summary>
    Fps5994 = 7,
}

/// <summary>Picture size and scan of the video stream, with the format code used in the BDMV files.</summary>
public enum BdmvVideoFormat
{
    /// <summary>1920x1080 interlaced (code 4).</summary>
    Hd1080i = 4,

    /// <summary>1280x720 progressive (code 5).</summary>
    Hd720p = 5,

    /// <summary>1920x1080 progressive (code 6).</summary>
    Hd1080p = 6,
}

/// <summary>One subtitle track (a .srt file rendered to PGS bitmaps).</summary>
/// <param name="SourcePath">The .srt file; null with <paramref name="GenerateWithWhisper"/> makes the subtitles by speech recognition.</param>
/// <param name="Language">Three-letter ISO 639-2 language code written into the playlist and clip information.</param>
/// <param name="GenerateWithWhisper">Make the .srt with the speech-recognition service when <paramref name="SourcePath"/> is empty.</param>
public sealed record BdmvSubtitleSpec(string? SourcePath, string Language = "eng", bool GenerateWithWhisper = false);

/// <summary>Settings for <see cref="BdmvAuthoringService"/>.</summary>
public sealed class BdmvAuthoringOptions
{
    /// <summary>Disc type: BD-R/BD-RE or AVCHD on DVD.</summary>
    public BdmvDiscTarget Target { get; set; } = BdmvDiscTarget.BluRay;

    /// <summary>Volume label of the UDF image.</summary>
    public string VolumeLabel { get; set; } = "BDMV_DISC";

    /// <summary>Picture size and scan.</summary>
    public BdmvVideoFormat VideoFormat { get; set; } = BdmvVideoFormat.Hd1080p;

    /// <summary>Frame rate of the video stream.</summary>
    public BdmvFrameRate FrameRate { get; set; } = BdmvFrameRate.Fps23976;

    /// <summary>Main audio encoding.</summary>
    public BdmvAudioCodec AudioCodec { get; set; } = BdmvAudioCodec.Ac3;

    /// <summary>Audio channels (2 or 6; LPCM is stereo only).</summary>
    public int AudioChannels { get; set; } = 2;

    /// <summary>Language of the audio track (ISO 639-2).</summary>
    public string AudioLanguage { get; set; } = "eng";

    /// <summary>Video bitrate in kbit/s (average).</summary>
    public int VideoBitrateKbps { get; set; } = 15000;

    /// <summary>Highest video bitrate in kbit/s (the buffer limit of the encoder).</summary>
    public int MaxVideoBitrateKbps { get; set; } = 25000;

    /// <summary>A chapter mark every this many seconds; 0 gives one mark at the start only.</summary>
    public int ChapterIntervalSeconds { get; set; } = 300;

    /// <summary>Subtitle tracks (at most 8), burned to PGS bitmap streams.</summary>
    public IList<BdmvSubtitleSpec> Subtitles { get; } = new List<BdmvSubtitleSpec>();

    /// <summary>Path of an FFmpeg executable; null uses the one found on the machine.</summary>
    public string? CustomFfmpegPath { get; set; }

    /// <summary>Capacity the finished tree must fit in, in bytes; 0 picks the single-layer disc of <see cref="Target"/>.</summary>
    public long CapacityBytes { get; set; }

    /// <summary>Time stamped into the UDF image; null uses the build time.</summary>
    public DateTime? FixedTimestampUtc { get; set; }
}

/// <summary>What was found out about one authored clip.</summary>
/// <param name="ClipName">Five-digit clip number, e.g. <c>00000</c>.</param>
/// <param name="SourcePackets">Number of 192-byte source packets in the stream file.</param>
/// <param name="StartPts45k">First presentation time (45 kHz).</param>
/// <param name="EndPts45k">End of the last frame (45 kHz).</param>
/// <param name="EntryPoints">Number of random-access points (IDR pictures) in the EP map.</param>
/// <param name="Subtitles">Number of PGS tracks in the clip.</param>
public sealed record BdmvClipSummary(
    string ClipName,
    long SourcePackets,
    long StartPts45k,
    long EndPts45k,
    int EntryPoints,
    int Subtitles);

/// <summary>Result of <see cref="BdmvAuthoringService.AuthorAsync"/>.</summary>
public sealed class BdmvAuthoringResult
{
    /// <summary>True when the tree was written.</summary>
    public bool Success { get; init; }

    /// <summary>Why authoring failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Folder that holds <c>BDMV</c> and <c>CERTIFICATE</c>.</summary>
    public string OutputRootDirectory { get; init; } = string.Empty;

    /// <summary>The <c>BDMV</c> folder.</summary>
    public string BdmvDirectory { get; init; } = string.Empty;

    /// <summary>Disc type the tree is meant for.</summary>
    public BdmvDiscTarget Target { get; init; }

    /// <summary>Sum of all file sizes.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Every file written, relative to <see cref="OutputRootDirectory"/>, with '/' separators.</summary>
    public IReadOnlyList<string> GeneratedFiles { get; init; } = Array.Empty<string>();

    /// <summary>One entry per clip.</summary>
    public IReadOnlyList<BdmvClipSummary> Clips { get; init; } = Array.Empty<BdmvClipSummary>();

    /// <summary>Plain-language notes about choices made on the way.</summary>
    public IReadOnlyList<string> LogNotes { get; init; } = Array.Empty<string>();
}
