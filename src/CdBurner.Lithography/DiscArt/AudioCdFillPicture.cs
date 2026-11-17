using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.DiscArt;

/// <summary>A picture for the audio CD picture session: its file bytes, where it came from, and its name on the disc.</summary>
/// <param name="Source">"chosen", "embedded", "file" or "coverartarchive".</param>
/// <param name="Origin">The file it was read from, or the release it was downloaded for.</param>
public sealed record AudioCdPicture(byte[] Bytes, string Source, string Origin)
{
    /// <summary>Name of the picture in the session's <c>PICTURES</c> folder, from its format (JPEG, PNG, GIF, WebP).</summary>
    public string FileNameOnDisc => FlacEncoder.DetectImageMimeType(Bytes) switch
    {
        "image/png" => "COVER.PNG",
        "image/gif" => "COVER.GIF",
        "image/webp" => "COVER.WEBP",
        _ => "COVER.JPG",
    };
}

/// <summary>
/// "Fill empty space with a picture" for audio CDs: after the music session (left as it is, so CD players see the same
/// tracks), a second session holds a small ISO 9660 + Joliet volume and then Mode 1 data sectors drawn as a picture to
/// the end of the disc. CD players read only the first session, and a data track is never played, so the picture is
/// never heard as noise. Off unless chosen.
/// <para>
/// The volume carries the Enhanced CD (Blue Book) folders, <c>CDPLUS/INFO.CDP</c> (album and track text, at session
/// sector 75) and <c>PICTURES/</c> with the picture file, but the session is written as Mode 1, not CD-ROM XA Mode 2
/// Form 1: <see cref="CdMode1Drawing"/> draws for the Mode 1 sector layout (user data after a 16-byte sync and header,
/// ECMA-130 clause 14), so the volume is not marked XA.
/// </para>
/// </summary>
public static class AudioCdFillPicture
{
    /// <summary>Shown next to the option on the audio CD screen (and printed by the CLI).</summary>
    public const string ScreenNote =
        "After the music, a hidden second session draws a picture on the rest of the disc. CD players read only the music "
        + "session, so the track list is unchanged and the picture is never played. The whole disc is written (longer "
        + "burn) and closed. Needs direct drive commands.";

    /// <summary>Word for the automatic picture (<c>--fill-picture auto</c>).</summary>
    public const string AutoSource = "auto";

    /// <summary>Zero sectors between the volume and the picture, so a reader running past the volume's end reads zeros.</summary>
    public const int GapSectors = 150;

    /// <summary>
    /// Sectors left unwritten before the lead-out. Windows' count of the disc and the drive's own can differ by a few
    /// sectors; two seconds keep the track inside the disc whichever count is used.
    /// </summary>
    public const int EndMarginSectors = 150;

    /// <summary>The smallest picture drawn: one minute of disc.</summary>
    public const int MinimumPictureSectors = 4_500;

    /// <summary>
    /// The drawing geometry counts sectors from absolute time 00:00:00, the start of the program area, which is LBA -150
    /// (ECMA-130 clause 22: LBA 0 is at 00:02:00). The same convention as picture tracks after a base CUE.
    /// </summary>
    public const int GeometryLbaOffset = 150;

    /// <summary>Pictures are scaled to at most this many pixels on the longer side before drawing.</summary>
    public const int MaxPictureSide = 2048;

    /// <summary>Largest picture file read.</summary>
    public const long MaxPictureFileBytes = 64L * 1024 * 1024;

    /// <summary>Picture file names looked for next to the audio files, in this order.</summary>
    public static readonly IReadOnlyList<string> CoverFileNames =
        ["cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png"];

    /// <summary>The nominal 80-minute CD geometry (built-in preset <c>cd-80</c>, ECMA-130: 1.2 m/s, 1.5 µm pitch).</summary>
    public static CdDiscParameters DefaultDisc()
    {
        CdDiscPresetDefinition preset = DiscPresetJsonStore.LoadBuiltIn().CdPresets
            .First(p => string.Equals(p.Id, "cd-80", StringComparison.OrdinalIgnoreCase));
        var disc = new CdDiscParameters(preset.InnerRadiusMm, preset.OuterRadiusMm, preset.Sectors,
            preset.LinearVelocityMmPerSecond, ImageOuterRadiusMm: preset.OuterRadiusMm, UsableSectors: preset.UsableSectors);
        disc.Validate();
        return disc;
    }

    /// <summary>A picture file the user chose. Throws <see cref="ArgumentException"/> when it is missing, too large or not a picture.</summary>
    public static AudioCdPicture LoadChosen(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new ArgumentException($"The picture was not found: {full}");
        }
        if (info.Length > MaxPictureFileBytes)
        {
            throw new ArgumentException($"The picture file is larger than {MaxPictureFileBytes / 1048576} MB: {full}");
        }
        var picture = new AudioCdPicture(File.ReadAllBytes(full), "chosen", full);
        _ = Decode(picture.Bytes); // refuse an unreadable picture before anything is written
        return picture;
    }

    /// <summary>
    /// The automatic picture from the burn's own files: cover art embedded in the source files (in track order), else
    /// <see cref="CoverFileNames"/> in the folders of the source files. Null when there is none.
    /// </summary>
    public static AudioCdPicture? FindLocalPicture(AudioCdCompilation compilation, IAudioDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var sources = compilation.Tracks
            .Select(t => t.SourceFilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string source in sources)
        {
            byte[]? art = ReadEmbeddedArt(source, decoder);
            if (art is { Length: > 0 } && FlacEncoder.DetectImageMimeType(art) != null && CanDecode(art))
            {
                return new AudioCdPicture(art, "embedded", source);
            }
        }

        foreach (string folder in sources.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string name in CoverFileNames)
            {
                string candidate = Path.Combine(folder, name);
                var info = new FileInfo(candidate);
                if (!info.Exists || info.Length == 0 || info.Length > MaxPictureFileBytes)
                {
                    continue;
                }
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // locked or unreadable: try the next name
                }
                if (CanDecode(bytes))
                {
                    return new AudioCdPicture(bytes, "file", info.FullName);
                }
            }
        }
        return null;
    }

    private static byte[]? ReadEmbeddedArt(string path, IAudioDecoder? decoder)
    {
        try
        {
            if (FlacFileCodec.IsFlacFile(path))
            {
                return FlacFileCodec.ProbeAudioFile(path).CoverArtBytes;
            }
            return decoder?.ProbeAudioFile(path).CoverArtBytes;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException
                                       or COMException or ArgumentException)
        {
            return null; // tags that cannot be read are no picture; the next source is tried
        }
    }

    /// <summary>
    /// The front cover from the Cover Art Archive for the album found by its tags (MusicBrainz release search on album
    /// title and artist; the first matching release that has a cover). Null when there is no match or no cover. This
    /// is an online lookup: call it only when the user asked for it.
    /// </summary>
    public static async Task<AudioCdPicture?> LookUpCoverAsync(
        HttpClient http, string? albumTitle, string? artist, string musicBrainzBaseUrl = MusicBrainzClient.DefaultBaseUrl,
        string coverArtBaseUrl = CoverArtArchiveClient.DefaultBaseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var client = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent, musicBrainzBaseUrl);
        IReadOnlyList<string> releases = await client.SearchReleaseIdsAsync(albumTitle, artist, 5, cancellationToken).ConfigureAwait(false);
        foreach (string release in releases.Take(3))
        {
            CoverArtImage? image = await CoverArtArchiveClient.FetchFrontAsync(http, release, MusicBrainzClient.DefaultUserAgent,
                coverArtBaseUrl, cancellationToken).ConfigureAwait(false);
            if (image != null && CanDecode(image.Bytes))
            {
                return new AudioCdPicture(image.Bytes, "coverartarchive", $"https://musicbrainz.org/release/{release}");
            }
        }
        return null;
    }

    /// <summary>
    /// The automatic picture: embedded cover art, then cover/folder pictures next to the files, then (only when
    /// <paramref name="lookupHttp"/> is given, i.e. the user allowed the online lookup) the Cover Art Archive. Returns the
    /// picture, or null and a plain note saying why there is none; the disc is then burned without the picture.
    /// </summary>
    public static async Task<(AudioCdPicture? Picture, string? Note)> FindAutoPictureAsync(
        AudioCdCompilation compilation, IAudioDecoder? decoder, HttpClient? lookupHttp,
        string musicBrainzBaseUrl = MusicBrainzClient.DefaultBaseUrl, string coverArtBaseUrl = CoverArtArchiveClient.DefaultBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (FindLocalPicture(compilation, decoder) is { } local)
        {
            return (local, null);
        }

        const string without = "The disc is burned without the picture.";
        if (lookupHttp == null)
        {
            return (null, "No picture fill: the files have no embedded cover art and there is no cover.jpg or folder.jpg next to "
                + $"them (allow the online cover lookup, or choose a picture). {without}");
        }

        try
        {
            AudioCdPicture? online = await LookUpCoverAsync(lookupHttp, compilation.AlbumTitle, compilation.AlbumPerformer,
                musicBrainzBaseUrl, coverArtBaseUrl, cancellationToken).ConfigureAwait(false);
            return online != null
                ? (online, null)
                : (null, "No picture fill: no embedded cover art, no cover.jpg or folder.jpg, and the Cover Art Archive has no "
                    + $"front cover for '{compilation.AlbumTitle}' by '{compilation.AlbumPerformer}'. {without}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, $"No picture fill: the online cover lookup failed ({ex.Message}). {without}");
        }
    }

    /// <summary>True when Windows Imaging can decode <paramref name="bytes"/> as a picture.</summary>
    public static bool CanDecode(byte[] bytes)
    {
        try
        {
            _ = Decode(bytes);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a picture file's bytes (JPEG, PNG, GIF, BMP, TIFF, and WebP where Windows has its codec) to 32-bit BGRA,
    /// scaled down to at most <see cref="MaxPictureSide"/> pixels on the longer side. Throws
    /// <see cref="ArgumentException"/> when the bytes are not a picture Windows can read.
    /// </summary>
    public static BgraRaster Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new ArgumentException("The picture file holds no picture.");
            }
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > BgraRaster.MaxPixels)
            {
                throw new ArgumentException("The picture size is not supported.");
            }
            double scale = Math.Min(1.0, (double)MaxPictureSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource scaled = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            byte[] pixels = new byte[checked(width * height * 4)];
            bgra.CopyPixels(pixels, width * 4, 0);
            var raster = new BgraRaster(width, height, pixels);
            raster.Validate();
            return raster;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or IOException
                                       or InvalidOperationException or OverflowException)
        {
            throw new ArgumentException($"The picture could not be read: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// The second session of an audio CD filled with a picture (see <see cref="AudioCdFillPicture"/>): built after the audio
/// is written, for the start address the drive gives, into a temporary file deleted when the burn closes it.
/// </summary>
public sealed class AudioCdPictureSession : IAudioCdSessionTwoImage
{
    private readonly AudioCdPicture _picture;
    private readonly BgraRaster _raster;
    private readonly BlueBookDiscInfo _info;
    private readonly string _volumeLabel;
    private readonly CdDiscParameters _disc;
    private readonly string _tempFolder;

    /// <param name="picture">The picture; decoded here, so an unreadable one is refused before any audio is written.</param>
    /// <param name="info">Album and track text for <c>CDPLUS/INFO.CDP</c>.</param>
    /// <param name="volumeLabel">Volume label of the session (ISO 9660 d-characters are enforced by the image builder).</param>
    /// <param name="disc">Disc geometry for the drawing; null for the nominal 80-minute CD.</param>
    /// <param name="tempFolder">Folder for the temporary image; null for the system temporary folder.</param>
    public AudioCdPictureSession(AudioCdPicture picture, BlueBookDiscInfo info, string? volumeLabel = null,
        CdDiscParameters? disc = null, string? tempFolder = null)
    {
        _picture = picture ?? throw new ArgumentNullException(nameof(picture));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _raster = AudioCdFillPicture.Decode(picture.Bytes);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "CD_EXTRA" : volumeLabel.Trim();
        _disc = disc ?? AudioCdFillPicture.DefaultDisc();
        _disc.Validate();
        _tempFolder = tempFolder ?? Path.GetTempPath();
        CompilationDirectory root = BuildTree(out IsoBuildOptions options);
        MinimumSectors = IsoUdfImageBuilder.CalculateTotalSectors(root, options, 0) + AudioCdFillPicture.GapSectors
            + AudioCdFillPicture.MinimumPictureSectors + AudioCdFillPicture.EndMarginSectors;
    }

    /// <summary>The picture drawn.</summary>
    public AudioCdPicture Picture => _picture;

    public long MinimumSectors { get; }

    /// <summary>Where the last built session starts (its first sector, the address the drive gave).</summary>
    public int? StartLba { get; private set; }

    /// <summary>Sectors of the ISO 9660 + Joliet volume in the last built session.</summary>
    public int VolumeSectors { get; private set; }

    /// <summary>First LBA of the picture in the last built session.</summary>
    public long PictureStartLba { get; private set; }

    /// <summary>LBA after the last picture sector.</summary>
    public long PictureEndLba { get; private set; }

    /// <summary>Sectors in the last built session image.</summary>
    public long ImageSectors { get; private set; }

    private CompilationDirectory BuildTree(out IsoBuildOptions options)
    {
        var root = new CompilationDirectory();
        BlueBookLayout.Apply(root, _info);
        var pictures = (CompilationDirectory)root.Children.First(c =>
            string.Equals(c.Name, BlueBookLayout.PicturesFolderName, StringComparison.OrdinalIgnoreCase));
        pictures.AddFile(new CompilationFile(_picture.FileNameOnDisc, _picture.Bytes));
        // Blue Book file placement (INFO.CDP at session sector 75) without the CD-ROM XA mark: the track is Mode 1.
        options = new IsoBuildOptions(VolumeLabel: _volumeLabel, IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false,
            PinnedFilePath: BlueBookLayout.InfoFilePath, PinnedFileSector: BlueBookLayout.InfoFileSessionSector);
        return root;
    }

    public Task<Stream> BuildAsync(int startLba, long sectorsAvailable, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Build(startLba, sectorsAvailable, progress, cancellationToken), cancellationToken);

    private Stream Build(int startLba, long sectorsAvailable, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        if (startLba <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startLba), "The picture session follows the audio session.");
        }

        CompilationDirectory root = BuildTree(out IsoBuildOptions options);
        using MemoryStream volume = IsoUdfImageBuilder.BuildToMemoryStream(root, options, startLba);
        int volumeSectors = checked((int)(volume.Length / IsoUdfImageBuilder.SectorSize));
        long pictureStart = (long)startLba + volumeSectors + AudioCdFillPicture.GapSectors;
        long pictureEnd = Math.Min((long)startLba + sectorsAvailable - AudioCdFillPicture.EndMarginSectors,
            _disc.OutputSectors - AudioCdFillPicture.GeometryLbaOffset);
        if (pictureEnd - pictureStart < AudioCdFillPicture.MinimumPictureSectors)
        {
            throw new ArgumentException(
                $"Only {Math.Max(0, pictureEnd - pictureStart):N0} sectors are left after the music for the picture; it needs at least {AudioCdFillPicture.MinimumPictureSectors:N0} (one minute of disc).");
        }

        long imageSectors = pictureEnd - startLba;
        string path = Path.Combine(_tempFolder, $"ignisdisc_cdfill_{Guid.NewGuid():N}.iso");
        if (DiscFillPicture.NotEnoughSpaceMessage(path, imageSectors) is { } space)
        {
            throw new IOException(space);
        }

        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            volume.Position = 0;
            volume.CopyTo(file);
            file.Write(new byte[AudioCdFillPicture.GapSectors * IsoUdfImageBuilder.SectorSize]);
            CdMode1Drawing.WriteSectors(_raster, file, _disc,
                pictureStart + AudioCdFillPicture.GeometryLbaOffset, pictureEnd + AudioCdFillPicture.GeometryLbaOffset,
                p => progress?.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = "Drawing the picture",
                    CurrentLba = (int)Math.Min(int.MaxValue, p.CompletedBytes / CdMode1Drawing.BytesPerSector),
                    TotalLba = (int)Math.Min(int.MaxValue, Math.Max(1, p.TotalBytes / CdMode1Drawing.BytesPerSector)),
                    BufferLevelPercent = 100.0
                }),
                cancellationToken, ToneProfile.Photo);
            file.Flush();
            file.Position = 0;
        }
        catch
        {
            file.Dispose(); // deletes the partial image
            throw;
        }

        StartLba = startLba;
        VolumeSectors = volumeSectors;
        PictureStartLba = pictureStart;
        PictureEndLba = pictureEnd;
        ImageSectors = imageSectors;
        return file;
    }
}
