using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Adds a CD picture track (a WAV made by the picture-track generator) as the last audio track of an audio or
/// mixed-mode CUE sheet, so it is written by the same Session-at-Once path as the rest of the disc
/// (<c>burn-audio --tracks &lt;cue&gt; --dao --native</c>) and checked by <c>verify --cue</c>.
/// <para>
/// The picture must be the last track: its pattern is laid out from the radius where the track starts outward, and
/// no later track may push it. The track gets no pre-gap of its own beyond what the writer adds, so its first sample
/// is the first sample of the drawing.
/// </para>
/// </summary>
public static class PictureTrackCue
{
    private const int MaxTracks = 99;

    /// <summary>
    /// Reads <paramref name="baseCuePath"/>, appends <paramref name="pictureWavPath"/> as one more audio track and
    /// writes the result to <paramref name="outputCuePath"/>. FILE names are rewritten relative to the output CUE's
    /// folder, so the new sheet can live elsewhere. Returns the written sheet.
    /// </summary>
    public static CueSheet WriteWithPictureLast(string baseCuePath, string pictureWavPath, string outputCuePath,
        string? title = "Picture")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCuePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureWavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCuePath);
        string baseFull = Path.GetFullPath(baseCuePath);
        string outputFull = Path.GetFullPath(outputCuePath);
        string wavFull = Path.GetFullPath(pictureWavPath);
        if (string.Equals(baseFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write the new CUE sheet to a different file than the one it starts from.", nameof(outputCuePath));

        ValidatePictureWave(wavFull);
        CueSheet source = CueSheetParser.ParseFile(baseFull);
        string baseDir = Path.GetDirectoryName(baseFull)!;
        string outputDir = Path.GetDirectoryName(outputFull)!;
        var files = source.Files
            .Select(f => f with { FileName = Relative(outputDir, Path.GetFullPath(Path.Combine(baseDir, f.FileName))) })
            .ToList();
        CueSheet sheet = AppendAsLastTrack(source with { Files = files }, Relative(outputDir, wavFull), title);
        Directory.CreateDirectory(outputDir);
        CueSheetWriter.WriteToFile(sheet, outputFull);
        return sheet;
    }

    /// <summary>Returns <paramref name="sheet"/> with one more FILE … WAVE entry holding an audio track numbered
    /// after the current last track, INDEX 01 at the start of the file.</summary>
    public static CueSheet AppendAsLastTrack(CueSheet sheet, string pictureFileName, string? title = "Picture")
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureFileName);
        IReadOnlyList<CueTrack> tracks = sheet.Tracks;
        if (tracks.Count == 0)
            throw new ArgumentException("The CUE sheet has no tracks; a picture track goes after at least one.", nameof(sheet));
        int number = tracks.Max(t => t.TrackNumber) + 1;
        if (number > MaxTracks)
            throw new ArgumentException($"A CD holds at most {MaxTracks} tracks; there is no room for the picture track.", nameof(sheet));

        CueTrack picture = new(number, CueTrackDataType.Audio, Title: title,
            Indices: new[] { new CueIndex(1, MsfAddress.FromFrameCount(0)) });
        var files = sheet.Files.ToList();
        files.Add(new CueFileEntry(pictureFileName, "WAVE", new[] { picture }));
        return sheet with { Files = files };
    }

    /// <summary>
    /// Experimental: reads <paramref name="baseCuePath"/>, puts <paramref name="pictureWavPath"/> in track 1's pre-gap
    /// (INDEX 00 at the start of the picture file, INDEX 01 at the start of track 1's own audio, the layout EAC writes
    /// for hidden audio before track 1) and writes the result to <paramref name="outputCuePath"/>. On the disc the
    /// writer keeps its two seconds of silence before LBA 0, so the picture starts at LBA 0 and track 1 right after
    /// it. FILE names are rewritten relative to the output CUE's folder. Returns the written sheet.
    /// </summary>
    /// <exception cref="ArgumentException">Track 1 is a data track, or already has a pre-gap, INDEX 00 or audio before
    /// its INDEX 01.</exception>
    public static CueSheet WriteWithPictureInPreGap(string baseCuePath, string pictureWavPath, string outputCuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCuePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureWavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCuePath);
        string baseFull = Path.GetFullPath(baseCuePath);
        string outputFull = Path.GetFullPath(outputCuePath);
        string wavFull = Path.GetFullPath(pictureWavPath);
        if (string.Equals(baseFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write the new CUE sheet to a different file than the one it starts from.", nameof(outputCuePath));

        ValidatePictureWave(wavFull);
        CueSheet source = CueSheetParser.ParseFile(baseFull);
        string baseDir = Path.GetDirectoryName(baseFull)!;
        string outputDir = Path.GetDirectoryName(outputFull)!;
        var files = source.Files
            .Select(f => f with { FileName = Relative(outputDir, Path.GetFullPath(Path.Combine(baseDir, f.FileName))) })
            .ToList();
        CueSheet sheet = PlaceInTrackOnePreGap(source with { Files = files }, Relative(outputDir, wavFull));
        Directory.CreateDirectory(outputDir);
        CueSheetWriter.WriteToFile(sheet, outputFull);
        return sheet;
    }

    /// <summary>Returns <paramref name="sheet"/> with <paramref name="pictureFileName"/> as track 1's dedicated pre-gap
    /// file (INDEX 00 at 00:00:00 of that file).</summary>
    public static CueSheet PlaceInTrackOnePreGap(CueSheet sheet, string pictureFileName)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureFileName);
        if (sheet.Files.Count == 0 || sheet.Files[0].Tracks.Count == 0)
            throw new ArgumentException("The CUE sheet has no tracks; the picture goes in front of track 1.", nameof(sheet));

        CueTrack first = sheet.Files[0].Tracks[0];
        if (!first.IsAudio)
            throw new ArgumentException("Track 1 is a data track; a picture in the pre-gap needs an audio CD that starts with music.", nameof(sheet));
        CueIndex? index01 = first.Indices.FirstOrDefault(i => i.IndexNumber == 1);
        if (first.PreGap is not null || first.SplitIndex00 is not null || first.Indices.Any(i => i.IndexNumber == 0)
            || index01 is null || index01.Position.ToFrameCount() != 0)
            throw new ArgumentException("Track 1 already has a pre-gap or audio in front of it (PREGAP, INDEX 00 or INDEX 01 after the start of its file); there is no room for the picture.", nameof(sheet));

        var files = sheet.Files.ToList();
        var tracks = files[0].Tracks.ToList();
        tracks[0] = first with { SplitIndex00 = new CueSplitIndex00(MsfAddress.FromFrameCount(0), pictureFileName, "WAVE") };
        files[0] = files[0] with { Tracks = tracks };
        return sheet with { Files = files };
    }

    /// <summary>The picture WAV must be canonical CD audio (PCM, 44,100 Hz, 16-bit stereo) in whole 2,352-byte
    /// sectors, as the generator writes it; anything else would shift or resample the drawing.</summary>
    private static void ValidatePictureWave(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Picture track not found.", path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[44];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || !header[..4].SequenceEqual("RIFF"u8) || !header[8..16].SequenceEqual("WAVEfmt "u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header[22..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 44100
            || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 16
            || !header[36..40].SequenceEqual("data"u8))
            throw new InvalidDataException($"'{path}' is not a picture track: expected a 44.1 kHz 16-bit stereo PCM WAV with a 44-byte header.");
        uint dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        if (dataBytes == 0 || dataBytes % OpticalConstants.RawSectorSize != 0 || 44L + dataBytes > stream.Length)
            throw new InvalidDataException($"'{path}' does not hold whole 2,352-byte CD sectors of audio.");
    }

    private static string Relative(string fromDirectory, string target) => Path.GetRelativePath(fromDirectory, target);
}
