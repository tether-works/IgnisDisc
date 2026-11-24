using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Audio;

/// <summary>
/// Input metadata and verification results formatted into an EAC-style extraction log by <see cref="RipLogFormatter"/>.
/// </summary>
public sealed record RipLogContext(
    AudioCdRipResult RipResult,
    DiscIdentifiers? DiscIds = null,
    string? AccurateRipStatus = null,
    IReadOnlyDictionary<int, AccurateRipTrackVerdict?>? AccurateRipVerdicts = null,
    string? CtdbStatus = null,
    (bool Matched, int Confidence, int TotalSubmissions)? CtdbDiscMatch = null,
    IReadOnlyDictionary<int, CtdbTrackVerdict?>? CtdbTrackVerdicts = null,
    CtdbRepairResult? CtdbRepair = null,
    string? IgnisDiscVersion = null,
    DateTimeOffset? TimestampUtc = null,
    byte[]? HmacKey = null,
    string? OutputFormatLabel = null);

/// <summary>
/// Formats EAC-style archival extraction logs (<c>&lt;Album&gt;.log</c>) with drive INQUIRY, read offset, extraction
/// settings, disc TOC, per-track peak/CRC/pre-gap/AccurateRip/CTDB details, and a SHA-256 + HMAC-SHA256 integrity footer.
/// </summary>
public static class RipLogFormatter
{
    private static readonly byte[] DefaultHmacKey = Encoding.UTF8.GetBytes("IgnisDisc-RipLog-Integrity-v1");
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gets a copy of the default domain-separated HMAC key used when no explicit key is supplied.</summary>
    internal static ReadOnlySpan<byte> ResolveHmacKey(byte[]? customKey) =>
        customKey is { Length: > 0 } ? customKey : DefaultHmacKey;

    /// <summary>
    /// Formats a complete EAC-style extraction log including the SHA-256 + HMAC-SHA256 self-integrity footer.
    /// </summary>
    public static string Format(RipLogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.RipResult);

        var rip = context.RipResult;
        string version = !string.IsNullOrWhiteSpace(context.IgnisDiscVersion)
            ? context.IgnisDiscVersion
            : GitHubUpdateChecker.GetCurrentAppVersion();
        DateTimeOffset timestamp = context.TimestampUtc ?? DateTimeOffset.UtcNow;

        var body = new StringBuilder();
        AppendLineLf(body, $"IgnisDisc Optical Studio V{version}");
        AppendLineLf(body, $"IgnisDisc extraction logfile from {timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        AppendLineLf(body);

        string driveLabel = BuildDriveLabel(rip);
        AppendLineLf(body, $"Used drive  : {driveLabel}");
        AppendLineLf(body);

        string albumTitle = !string.IsNullOrWhiteSpace(rip.CdText?.AlbumTitle) ? rip.CdText.AlbumTitle : "Unknown Album";
        string albumPerformer = !string.IsNullOrWhiteSpace(rip.CdText?.AlbumPerformer) ? rip.CdText.AlbumPerformer : "Unknown Artist";
        AppendLineLf(body, $"Album               : {albumTitle}");
        AppendLineLf(body, $"Artist              : {albumPerformer}");
        AppendLineLf(body, $"Catalog (MCN)       : {(string.IsNullOrWhiteSpace(rip.MediaCatalogNumber) ? "none" : rip.MediaCatalogNumber)}");
        AppendLineLf(body, $"MusicBrainz DiscID  : {context.DiscIds?.MusicBrainzId ?? "n/a"}");
        AppendLineLf(body, $"freedb ID           : {context.DiscIds?.FreeDbIdHex ?? "n/a"}");
        AppendLineLf(body, $"AccurateRip ID      : {context.DiscIds?.AccurateRipDatabasePath ?? "n/a"}");
        AppendLineLf(body, $"CTDB TOCID          : {context.DiscIds?.CtdbTocId ?? "n/a"}");
        AppendLineLf(body);

        string ripModeLabel = rip.Mode == AudioRipMode.ImageCue ? "Image + CUE (Single-file)" : "Track-by-track";
        string formatLabel = !string.IsNullOrWhiteSpace(context.OutputFormatLabel)
            ? context.OutputFormatLabel
            : InferFormatLabel(rip);
        string readModeLabel = rip.SecureModeUsed
            ? "Secure (C2 error pointers)"
            : "Burst / Plain read";
        string c2Label = rip.SecureModeUsed
            ? "Used"
            : (rip.SecureModeRequested ? "Unsupported by drive" : "Disabled");
        string offsetLabel = rip.ReadOffset?.BadgeText ?? $"{rip.ReadOffsetSamples:+0;-0;0} samples";
        if (rip.ReadOffset is { SourceDescription: { Length: > 0 } srcDesc } &&
            !offsetLabel.Contains(srcDesc, StringComparison.OrdinalIgnoreCase))
        {
            offsetLabel = $"{offsetLabel} ({srcDesc})";
        }

        int gapCount = rip.TrackInfos.Count(t => t.PreGapFrames > 0) + (rip.HiddenTrackOneAudioFrames > 0 ? 1 : 0);
        string gapDetectionLabel = rip.GapDetectionPerformed
            ? $"Q sub-channel binary search ({gapCount} pre-gap(s) detected)"
            : "None";

        bool anyPreEmph = rip.TrackInfos.Any(t => t.PreEmphasis);
        bool anyDeEmphApplied = rip.TrackInfos.Any(t => t.DeEmphasisApplied);
        string deEmphLabel = !anyPreEmph
            ? "None"
            : (anyDeEmphApplied ? "Applied (50/15 us)" : "Preserved in CUE (FLAGS PRE)");

        AppendLineLf(body, $"Rip mode            : {ripModeLabel}");
        AppendLineLf(body, $"Output format       : {formatLabel}");
        AppendLineLf(body, $"Read mode           : {readModeLabel}");
        AppendLineLf(body, $"C2 pointers         : {c2Label}");
        AppendLineLf(body, $"Read offset         : {offsetLabel}");
        AppendLineLf(body, $"Overread into Lead-In and Lead-Out : {(rip.OverreadIntoLeadInOut ? "Yes" : "No")}");
        AppendLineLf(body, $"Gap detection       : {gapDetectionLabel}");
        AppendLineLf(body, $"De-emphasis         : {deEmphLabel}");
        if (!string.IsNullOrWhiteSpace(rip.ImageFilePath))
        {
            AppendLineLf(body, $"Image file          : {Path.GetFileName(rip.ImageFilePath)}");
        }
        if (!string.IsNullOrWhiteSpace(rip.CueSheetPath))
        {
            AppendLineLf(body, $"CUE sheet           : {Path.GetFileName(rip.CueSheetPath)}");
        }
        AppendLineLf(body);

        AppendTocTable(body, rip);

        if (rip.HiddenTrackOneAudioFrames > 0)
        {
            AppendLineLf(body, "Hidden Track One Audio (HTOA)");
            string htoaLoc = rip.Mode == AudioRipMode.ImageCue
                ? $"{Path.GetFileName(rip.ImageFilePath ?? rip.RippedFilePaths.FirstOrDefault() ?? "image")} (offset 00:00.00)"
                : Path.GetFileName(rip.HiddenTrackOneAudioPath ?? "00 - Hidden Track One Audio.wav");
            AppendLineLf(body, $"     File/Source    {htoaLoc}");
            AppendLineLf(body, "     Start sector   0");
            AppendLineLf(body, $"     Length         {FormatEacMsf(rip.HiddenTrackOneAudioFrames)} ({rip.HiddenTrackOneAudioFrames} sectors)");
            AppendLineLf(body);
        }

        int imageStartLba = rip.Mode == AudioRipMode.ImageCue && rip.HiddenTrackOneAudioFrames == 0 && rip.TrackInfos.Count > 0
            ? rip.TrackInfos.Min(t => t.StartLba)
            : 0;

        foreach (var track in rip.TrackInfos)
        {
            AppendTrackSection(body, rip, track, imageStartLba, context);
        }

        if (context.CtdbStatus != null || context.CtdbRepair != null)
        {
            AppendLineLf(body, "CUETools Database (CTDB) summary");
            if (context.CtdbStatus != null)
            {
                AppendLineLf(body, $"     Status         {context.CtdbStatus}");
            }
            if (rip.CtdbDiscCrc is uint discCrc)
            {
                AppendLineLf(body, $"     Disc CRC32     {discCrc:X8}");
            }
            if (context.CtdbDiscMatch is { } dm)
            {
                string verdictText = dm.Matched
                    ? $"Accurate (confidence {dm.Confidence}/{dm.TotalSubmissions})"
                    : $"NOT accurate (0/{dm.TotalSubmissions} submissions match)";
                AppendLineLf(body, $"     Disc verdict   {verdictText}");
            }
            if (context.CtdbRepair is { } repair)
            {
                AppendLineLf(body, $"     Repair         {repair.StatusCode} - {repair.Message}");
                foreach (string line in repair.DescribeTracks())
                {
                    AppendLineLf(body, $"       {line}");
                }
            }
            AppendLineLf(body);
        }

        int badSectors = Math.Max(rip.BadSectorLbas.Count, rip.TrackInfos.Sum(t => t.UnreadableSectors));
        int suspectSectors = rip.TrackInfos.Sum(t => t.SuspectSectors);
        AppendLineLf(body, "End of status report");
        if (!rip.Success)
        {
            AppendLineLf(body, $"Status: Rip failed ({rip.ErrorMessage ?? "unknown error"})");
        }
        else if (badSectors == 0 && suspectSectors == 0)
        {
            AppendLineLf(body, "Status: No errors occurred");
        }
        else
        {
            AppendLineLf(body, $"Status: Completed with {badSectors} unreadable and {suspectSectors} suspect sector(s)");
        }

        string bodyText = body.ToString();
        byte[] bodyBytes = Utf8NoBom.GetBytes(bodyText);
        string shaHex = Convert.ToHexString(SHA256.HashData(bodyBytes));
        string hmacHex = Convert.ToHexString(HMACSHA256.HashData(ResolveHmacKey(context.HmacKey), bodyBytes));

        var full = new StringBuilder(bodyText.Length + 160);
        full.Append(bodyText);
        full.Append('\n');
        full.Append("==== Log checksum ").Append(hmacHex).Append(" ====\n");
        full.Append("SHA-256: ").Append(shaHex).Append('\n');
        return full.ToString().Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Formats and writes the extraction log to <paramref name="filePath"/> as UTF-8 without BOM.
    /// </summary>
    public static void WriteToFile(string filePath, RipLogContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, Format(context), Utf8NoBom);
    }

    /// <summary>
    /// Asynchronously formats and writes the extraction log to <paramref name="filePath"/> as UTF-8 without BOM.
    /// </summary>
    public static async Task WriteToFileAsync(string filePath, RipLogContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, Format(context), Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildDriveLabel(AudioCdRipResult rip)
    {
        string vendor = rip.DriveVendorId?.Trim() ?? string.Empty;
        string product = rip.DriveProductId?.Trim() ?? string.Empty;
        string rev = rip.DriveProductRevision?.Trim() ?? string.Empty;
        string id = rip.DriveUniqueId?.Trim() ?? string.Empty;

        string inquiry = string.Join(" ", new[] { vendor, product }.Where(s => s.Length > 0));
        if (rev.Length > 0)
        {
            inquiry = inquiry.Length > 0 ? $"{inquiry} (Rev {rev})" : $"Rev {rev}";
        }

        if (inquiry.Length == 0)
        {
            return id.Length > 0 ? id : "Unknown Optical Drive";
        }

        return id.Length > 0 ? $"{inquiry} [{id}]" : inquiry;
    }

    private static string InferFormatLabel(AudioCdRipResult rip)
    {
        string? samplePath = rip.ImageFilePath ?? rip.TrackInfos.FirstOrDefault()?.FilePath ?? rip.RippedFilePaths.FirstOrDefault();
        string ext = Path.GetExtension(samplePath ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".flac" => "FLAC",
            ".wav" => "WAV",
            ".mp3" => "MP3",
            ".opus" => "Opus",
            ".m4a" or ".aac" => "AAC (M4A)",
            _ => "WAV"
        };
    }

    private static void AppendTocTable(StringBuilder body, AudioCdRipResult rip)
    {
        IReadOnlyList<DiscTocTrack> tocTracks = rip.DiscTocTracks;
        if (tocTracks.Count == 0 && rip.TrackInfos.Count > 0)
        {
            tocTracks = rip.TrackInfos
                .Select(t => new DiscTocTrack(t.TrackNumber, t.StartLba, t.SectorCount, true, t.PreEmphasis, t.CopyPermitted, t.FourChannel))
                .ToList();
        }

        if (tocTracks.Count == 0)
        {
            return;
        }

        AppendLineLf(body, "TOC of the extracted CD");
        AppendLineLf(body);
        AppendLineLf(body, "     Track |   Start  |  Length  | Start sector | End sector ");
        AppendLineLf(body, "    ---------------------------------------------------------");
        foreach (var t in tocTracks.OrderBy(x => x.TrackNumber))
        {
            int endSector = t.StartLba + Math.Max(0, t.SectorCount) - 1;
            string trkLabel = t.IsAudio ? $"{t.TrackNumber,4}" : $"{t.TrackNumber,3}*";
            AppendLineLf(body, string.Create(
                CultureInfo.InvariantCulture,
                $"     {trkLabel}  | {FormatEacMsf(t.StartLba),8} | {FormatEacMsf(t.SectorCount),8} | {t.StartLba,9}    | {endSector,9}"));
        }

        if (tocTracks.Any(t => !t.IsAudio))
        {
            AppendLineLf(body, "    (* data track)");
        }

        AppendLineLf(body);
    }

    private static void AppendTrackSection(
        StringBuilder body,
        AudioCdRipResult rip,
        AudioTrackRipInfo track,
        int imageStartLba,
        RipLogContext context)
    {
        AppendLineLf(body, $"Track {track.TrackNumber,2}");
        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            AppendLineLf(body, $"     Title          {track.Title}");
        }
        if (!string.IsNullOrWhiteSpace(track.Performer))
        {
            AppendLineLf(body, $"     Performer      {track.Performer}");
        }

        string fileDisplay = rip.Mode == AudioRipMode.ImageCue
            ? $"{Path.GetFileName(track.FilePath)} (offset {FormatEacMsf(Math.Max(0, track.StartLba - imageStartLba))})"
            : Path.GetFileName(track.FilePath);
        AppendLineLf(body, $"     Filename       {fileDisplay}");
        AppendLineLf(body, $"     Start          {FormatEacMsf(track.StartLba)} (LBA {track.StartLba})");
        AppendLineLf(body, $"     Length         {FormatEacMsf(track.SectorCount)} ({track.SectorCount} sectors)");
        if (track.PreGapFrames > 0)
        {
            AppendLineLf(body, $"     Pre-gap length {FormatEacMsf(track.PreGapFrames)} ({track.PreGapFrames} frames)");
        }
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            AppendLineLf(body, $"     ISRC           {track.Isrc}");
        }

        var flags = new List<string>(3);
        if (track.CopyPermitted) flags.Add("DCP");
        if (track.FourChannel) flags.Add("4CH");
        if (track.PreEmphasis) flags.Add("PRE");
        if (flags.Count > 0)
        {
            AppendLineLf(body, $"     Flags          {string.Join(' ', flags)}");
        }

        double? peakPct = track.PeakLevelPercent ?? (track.ReplayGainTrackPeak is { } p ? p * 100.0 : null);
        AppendLineLf(body, peakPct.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"     Peak level     {peakPct.Value:F1} %")
            : "     Peak level     n/a");
        AppendLineLf(body, $"     Copy CRC       {track.Crc32:X8}");
        AppendLineLf(body, $"     Sector status  {track.UnreadableSectors} unreadable, {track.RecoveredSectors} C2 recovered, {track.SuspectSectors} suspect");

        string arLine = FormatAccurateRipTrackLine(track, context);
        AppendLineLf(body, $"     AccurateRip    {arLine}");

        if (context.CtdbStatus != null || (context.CtdbTrackVerdicts != null && context.CtdbTrackVerdicts.ContainsKey(track.TrackNumber)))
        {
            string ctdbLine = FormatCtdbTrackLine(track, context);
            AppendLineLf(body, $"     CTDB           {ctdbLine}");
        }

        AppendLineLf(body);
    }

    private static string FormatAccurateRipTrackLine(AudioTrackRipInfo track, RipLogContext context)
    {
        AccurateRipTrackVerdict? verdict = null;
        bool hasVerdict = context.AccurateRipVerdicts != null &&
                          context.AccurateRipVerdicts.TryGetValue(track.TrackNumber, out verdict);

        string status;
        if (!hasVerdict && context.AccurateRipStatus == null)
        {
            status = "Not checked";
        }
        else if (context.AccurateRipStatus is "unavailable" or "failed")
        {
            status = $"Lookup {context.AccurateRipStatus}";
        }
        else if (verdict == null)
        {
            status = "Track not present in AccurateRip database";
        }
        else if (verdict.IsAccurate)
        {
            status = $"Accurately ripped (confidence {verdict.Confidence}/{verdict.TotalSubmissions}, {(verdict.MatchedV2 ? "v2" : "v1")})";
        }
        else
        {
            status = $"Cannot be verified as accurate (0/{verdict.TotalSubmissions} submissions match)";
        }

        return $"{status}  [v1 {track.AccurateRipV1:x8}, v2 {track.AccurateRipV2:x8}]";
    }

    private static string FormatCtdbTrackLine(AudioTrackRipInfo track, RipLogContext context)
    {
        CtdbTrackVerdict? verdict = null;
        bool hasVerdict = context.CtdbTrackVerdicts != null &&
                          context.CtdbTrackVerdicts.TryGetValue(track.TrackNumber, out verdict);

        string status;
        if (!hasVerdict && context.CtdbStatus is "unavailable" or "failed")
        {
            status = $"Lookup {context.CtdbStatus}";
        }
        else if (verdict == null)
        {
            status = "Not in CTDB";
        }
        else if (verdict.IsAccurate)
        {
            status = $"Accurately ripped (confidence {verdict.Confidence}/{verdict.TotalSubmissions})";
        }
        else
        {
            status = $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
        }

        return $"{status}  [ctdb {track.CtdbTrackCrc:x8}]";
    }

    internal static string FormatEacMsf(int frames)
    {
        if (frames < 0)
        {
            frames = 0;
        }

        int f = frames % OpticalConstants.AudioFramesPerSecond;
        int totalSeconds = frames / OpticalConstants.AudioFramesPerSecond;
        int s = totalSeconds % 60;
        int m = totalSeconds / 60;
        return string.Create(CultureInfo.InvariantCulture, $"{m}:{s:D2}.{f:D2}");
    }

    private static void AppendLineLf(StringBuilder sb, string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            sb.Append(text);
        }
        sb.Append('\n');
    }
}
