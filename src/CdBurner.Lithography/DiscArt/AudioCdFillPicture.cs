using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Lithography.Calibration;
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

    /// <summary>Converts a saved CD calibration to the geometry parameters used to draw session 2.</summary>
    public static CdDiscParameters ToCdDiscParameters(CalibrationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Kind != CalibrationDiscKind.Cd)
        {
            throw new ArgumentException("Expected a CD calibration.", nameof(parameters));
        }

        var disc = new CdDiscParameters(
            parameters.InnerRadiusMm,
            parameters.OuterRadiusMm,
            parameters.Sectors,
            parameters.LinearDensity,
            ImageOuterRadiusMm: parameters.OuterRadiusMm,
            PitchLinear: parameters.PitchLinear,
            PitchQuadratic: parameters.PitchQuadratic,
            PitchCubic: parameters.PitchCubic,
            UsableSectors: parameters.UsableSectors);
        disc.Validate();
        return disc;
    }

    /// <summary>Drive model string as saved in <see cref="CalibrationStore"/> for <paramref name="recorder"/>.</summary>
    public static string DriveModelName(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return $"{recorder.VendorId} {recorder.ProductId}".Trim();
    }

    /// <summary>
    /// Picks the CD drawing geometry for a drive and disc: a calibration saved for <paramref name="driveModel"/> and
    /// <paramref name="mediaId"/> in <paramref name="calibrationStorePath"/> (default store when null) if one exists,
    /// otherwise the built-in 80-minute preset (<see cref="DefaultDisc"/>).
    /// </summary>
    public static CdDiscParameters ResolveDisc(
        string? driveModel,
        string? mediaId,
        string? calibrationStorePath,
        out string? calibrationSource)
    {
        calibrationSource = null;
        if (IsValidStoreKey(driveModel) && IsValidStoreKey(mediaId))
        {
            string path = Path.GetFullPath(string.IsNullOrWhiteSpace(calibrationStorePath)
                ? CalibrationStore.GetDefaultPath()
                : calibrationStorePath);
            try
            {
                CalibrationStoreDocument document = CalibrationStore.Load(path);
                CalibrationStoreEntry? entry = FindCalibration(document, driveModel!, mediaId!);
                if (entry is not null)
                {
                    calibrationSource = $"saved calibration for drive '{entry.DriveModel}' and media '{entry.MediaId}'";
                    return ToCdDiscParameters(entry.Parameters);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Trace.TraceWarning($"Ignoring unreadable calibration store '{path}': {ex.Message}");
            }
        }

        return DefaultDisc();
    }

    private static CalibrationStoreEntry? FindCalibration(CalibrationStoreDocument document, string driveModel, string mediaId)
    {
        CalibrationStoreEntry? exact = CalibrationStore.Find(document, driveModel, mediaId, CalibrationDiscKind.Cd);
        if (exact is not null)
        {
            return exact;
        }

        // Media info displays e.g. "97m26s00f (FORNET - Long Strategy ...)"; a user may have saved just the raw ATIP code.
        int paren = mediaId.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
        {
            string rawCode = mediaId[..paren].Trim();
            if (IsValidStoreKey(rawCode))
            {
                return CalibrationStore.Find(document, driveModel, rawCode, CalibrationDiscKind.Cd);
            }
        }

        return null;
    }

    private static bool IsValidStoreKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= CalibrationStore.MaxKeyLength && !trimmed.Any(char.IsControl);
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
        string coverArtBaseUrl = CoverArtArchiveClient.DefaultBaseUrl, CancellationToken cancellationToken = default,
        RequestGate? gate = null, TimeSpan? lookupTimeout = null, TimeSpan? releaseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        // The whole lookup is bounded; a cover fetch that hangs or is throttled costs only its own share of that time.
        using var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookupCts.CancelAfter(lookupTimeout ?? DefaultLookupTimeout);
        CancellationToken lookupToken = lookupCts.Token;

        try
        {
            var client = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent, musicBrainzBaseUrl, gate);
            IReadOnlyList<string> releases = await client.SearchReleaseIdsAsync(albumTitle, artist, 5, lookupToken).ConfigureAwait(false);
            foreach (string release in releases.Take(3))
            {
                using var releaseCts = CancellationTokenSource.CreateLinkedTokenSource(lookupToken);
                releaseCts.CancelAfter(releaseTimeout ?? DefaultReleaseTimeout);
                try
                {
                    CoverArtImage? image = await CoverArtArchiveClient.FetchFrontAsync(http, release, MusicBrainzClient.DefaultUserAgent,
                        coverArtBaseUrl, releaseCts.Token, gate).ConfigureAwait(false);
                    if (image != null && CanDecode(image.Bytes))
                    {
                        return new AudioCdPicture(image.Bytes, "coverartarchive", $"https://musicbrainz.org/release/{release}");
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    // throttled even after the retries: try the next release
                }
                catch (OperationCanceledException) when (!lookupToken.IsCancellationRequested)
                {
                    // this release's own time ran out: try the next release
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lookupCts.IsCancellationRequested)
        {
            return null; // the whole lookup timed out: no picture, the disc is burned without one
        }
    }

    /// <summary>Longest an online cover lookup (search plus up to three cover downloads) may take.</summary>
    public static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longest one release's cover download may take, retries included.</summary>
    public static readonly TimeSpan DefaultReleaseTimeout = TimeSpan.FromSeconds(25);

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
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new ArgumentException("The picture file holds no picture.");
            }
            BitmapSource frame = decoder.Frames[0];
            long pixelCount = (long)frame.PixelWidth * frame.PixelHeight;
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || pixelCount > BgraRaster.MaxPixels || pixelCount > PngRaster.MaxDecodedPixels)
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
    /// <param name="discFrom">Plain description of where <paramref name="disc"/> came from (null uses default/custom).</param>
    public AudioCdPictureSession(AudioCdPicture picture, BlueBookDiscInfo info, string? volumeLabel = null,
        CdDiscParameters? disc = null, string? tempFolder = null, string? discFrom = null)
    {
        _picture = picture ?? throw new ArgumentNullException(nameof(picture));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _raster = AudioCdFillPicture.Decode(picture.Bytes);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "CD_EXTRA" : volumeLabel.Trim();
        _disc = disc ?? AudioCdFillPicture.DefaultDisc();
        _disc.Validate();
        _tempFolder = tempFolder ?? Path.GetTempPath();
        DiscFrom = !string.IsNullOrWhiteSpace(discFrom)
            ? discFrom
            : disc is null || disc == AudioCdFillPicture.DefaultDisc() ? "disc preset 'cd-80'" : "custom disc geometry";
        CompilationDirectory root = BuildTree(out IsoBuildOptions options);
        MinimumSectors = IsoUdfImageBuilder.CalculateTotalSectors(root, options, 0) + AudioCdFillPicture.GapSectors
            + AudioCdFillPicture.MinimumPictureSectors + AudioCdFillPicture.EndMarginSectors;
    }

    /// <summary>The picture drawn.</summary>
    public AudioCdPicture Picture => _picture;

    /// <summary>Where the disc drawing geometry came from (for example the built-in preset or a saved calibration).</summary>
    public string DiscFrom { get; }

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

    private readonly record struct SessionLayout(
        CompilationDirectory Root,
        IsoBuildOptions Options,
        int VolumeSectors,
        long PictureStartLba,
        long PictureEndLba,
        long ImageSectors);

    private SessionLayout PlanLayout(int startLba, long sectorsAvailable)
    {
        if (startLba <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startLba), "The picture session follows the audio session.");
        }

        CompilationDirectory root = BuildTree(out IsoBuildOptions options);
        int volumeSectors = IsoUdfImageBuilder.CalculateTotalSectors(root, options, startLba) - startLba;
        long pictureStart = (long)startLba + volumeSectors + AudioCdFillPicture.GapSectors;
        long pictureEnd = Math.Min((long)startLba + sectorsAvailable - AudioCdFillPicture.EndMarginSectors,
            _disc.OutputSectors - AudioCdFillPicture.GeometryLbaOffset);
        if (pictureEnd - pictureStart < AudioCdFillPicture.MinimumPictureSectors)
        {
            throw new ArgumentException(
                $"Only {Math.Max(0, pictureEnd - pictureStart):N0} sectors are left after the music for the picture; it needs at least {AudioCdFillPicture.MinimumPictureSectors:N0} (one minute of disc).");
        }

        long imageSectors = pictureEnd - startLba;
        return new SessionLayout(root, options, volumeSectors, pictureStart, pictureEnd, imageSectors);
    }

    private string TempImagePath() => Path.Combine(_tempFolder, $"ignisdisc_cdfill_{Guid.NewGuid():N}.iso");

    public string? PreflightError(int startLba, long sectorsAvailable)
    {
        SessionLayout layout;
        try
        {
            layout = PlanLayout(startLba, sectorsAvailable);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        return DiscFillPicture.NotEnoughSpaceMessage(TempImagePath(), layout.ImageSectors);
    }

    public Task<Stream> BuildAsync(int startLba, long sectorsAvailable, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Build(startLba, sectorsAvailable, progress, cancellationToken), cancellationToken);

    private Stream Build(int startLba, long sectorsAvailable, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        SessionLayout layout = PlanLayout(startLba, sectorsAvailable);
        string path = TempImagePath();
        if (DiscFillPicture.NotEnoughSpaceMessage(path, layout.ImageSectors) is { } space)
        {
            throw new IOException(space);
        }

        using MemoryStream volume = IsoUdfImageBuilder.BuildToMemoryStream(layout.Root, layout.Options, startLba);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            volume.Position = 0;
            volume.CopyTo(file);
            file.Write(new byte[AudioCdFillPicture.GapSectors * IsoUdfImageBuilder.SectorSize]);
            CdMode1Drawing.WriteSectors(_raster, file, _disc,
                layout.PictureStartLba + AudioCdFillPicture.GeometryLbaOffset,
                layout.PictureEndLba + AudioCdFillPicture.GeometryLbaOffset,
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
        VolumeSectors = layout.VolumeSectors;
        PictureStartLba = layout.PictureStartLba;
        PictureEndLba = layout.PictureEndLba;
        ImageSectors = layout.ImageSectors;
        return file;
    }
}
