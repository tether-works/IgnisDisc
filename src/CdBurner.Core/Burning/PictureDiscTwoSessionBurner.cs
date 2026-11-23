using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Burning;

/// <summary>What a two-session picture-disc burn did.</summary>
public sealed record PictureDiscTwoSessionResult
{
    public bool Success { get; init; }

    /// <summary>True when the burn stopped before the first WRITE: the disc is as it was.</summary>
    public bool NothingWritten { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Sectors of the picture session (LBA 0 to the end of the picture zone).</summary>
    public int PictureSectors { get; init; }

    /// <summary>Where the files session was expected to start, from the disc type's measured session overhead.</summary>
    public int? PredictedFilesSessionStartLba { get; init; }

    /// <summary>Where the files session was written: the drive's next writable address after the picture session.</summary>
    public int? FilesSessionStartLba { get; init; }

    /// <summary>Sectors of the files session as built for <see cref="FilesSessionStartLba"/>.</summary>
    public int FilesSessionSectors { get; init; }

    /// <summary>The last sector a session may use plus one, as the blank disc reported it.</summary>
    public int CapacitySectors { get; init; }

    public DataBurnResult? PictureSession { get; init; }
    public DataBurnResult? FilesSession { get; init; }

    /// <summary>Something a successful burn still has to tell the user (for example: reload the disc).</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Burns a picture-disc image that has the picture from LBA 0 and the files at the outer end
/// (<see cref="PictureDiscFilesPlacement.OuterFiles"/>) as two sessions: first the picture zone, left open (session
/// closed, disc not finalized); then the files, rebuilt as an ISO 9660 + Joliet session for the drive's next writable
/// address and written as the second session, which closes the disc. A computer reads the last session, so it finds
/// the files; the picture's sectors are never written again.
/// <para>
/// The image's own files session was laid out for a start sector chosen when it was generated. Where a second session
/// really starts depends on the disc type (the drive leaves a lead-out/border and lead-in between sessions), so the
/// files are read out of the image and laid out again for the address the drive reports. Before anything is written,
/// that address is predicted from the measured overhead (<see cref="FilesSessionStartAfter"/>) and an image whose
/// files would no longer fit is refused.
/// </para>
/// <para>
/// UDF is not written: <see cref="IsoUdfImageBuilder"/> writes UDF only for a session at LBA 0, and Windows mounts
/// UDF 1.02 from fixed sectors of the first session, which here holds the picture.
/// </para>
/// </summary>
public sealed class PictureDiscTwoSessionBurner
{
    /// <summary>
    /// DVD-R (LG GP60NB50, 2026-11-10/11): a first session left open is never shorter than this in the table of contents
    /// (a 36,032-sector track was reported with its lead-out at 65,264).
    /// </summary>
    public const int DvdMinusRFirstSessionMinimumEndLba = 65_264;

    /// <summary>
    /// DVD-R: the second session starts this many sectors after the first one ends (93,952 after the 65,264 minimum;
    /// 131,152 after a 102,464-sector first track; measured 2026-11-10/11 with both writers).
    /// </summary>
    public const int DvdMinusRFirstSessionGapSectors = 28_688;

    /// <summary>
    /// DVD+R (LG GP60NB50, 2026-11-13/14): no minimum session length; the next session starts 2,048 sectors after the
    /// last one ends (17,472 after a 15,424-sector track; 104,480 after 102,432), track sizes padded to 16 sectors.
    /// </summary>
    public const int DvdPlusRSessionGapSectors = 2_048;

    /// <summary>
    /// Sectors <c>disc-art generate</c> keeps free between the picture and an outer files session on a DVD, so the
    /// image fits both DVD-R (the larger overhead, <see cref="DvdMinusRFirstSessionGapSectors"/>) and DVD+R.
    /// </summary>
    public const int DvdFilesSessionReserveSectors = DvdMinusRFirstSessionGapSectors;

    private const int Sector = OpticalConstants.DataSectorSize;
    private const int EccBlock = OpticalConstants.DvdEccBlockSectors;

    private readonly DataBurnService _service;

    /// <param name="service">Burns each session; give it the direct drive-command writer.</param>
    public PictureDiscTwoSessionBurner(DataBurnService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>
    /// Where the second session starts on a disc of <paramref name="type"/> whose first session (from LBA 0) ends at
    /// <paramref name="pictureEndLba"/>, from the overhead measured on a real drive; null for a disc type this burn does
    /// not cover.
    /// </summary>
    public static int? FilesSessionStartAfter(DiscMediaType type, int pictureEndLba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pictureEndLba);
        int end = AlignUp(pictureEndLba, EccBlock);
        return type switch
        {
            DiscMediaType.DvdMinusR => Math.Max(end, DvdMinusRFirstSessionMinimumEndLba) + DvdMinusRFirstSessionGapSectors,
            DiscMediaType.DvdPlusR => end + DvdPlusRSessionGapSectors,
            _ => null,
        };
    }

    /// <summary>Why a disc of <paramref name="type"/> cannot take the two sessions; null when it can.</summary>
    public static string? WhyNotSupported(DiscMediaType type) => type switch
    {
        DiscMediaType.DvdMinusR or DiscMediaType.DvdPlusR => null,
        _ => $"A picture with the files at the outer edge is burned as two sessions with direct drive commands, which "
            + $"covers blank single-layer DVD-R and DVD+R so far; this disc is {type.GetDisplayName()}.",
    };

    /// <summary>
    /// Burns <paramref name="imagePath"/> (laid out by <paramref name="layout"/>) to the blank disc in
    /// <paramref name="recorder"/> as two sessions. Speed, strict verify and BURN-Free come from
    /// <paramref name="options"/>; both sessions are always read back, the first is left open and the second closes the
    /// disc.
    /// </summary>
    public async Task<PictureDiscTwoSessionResult> BurnAsync(
        IDiscRecorderBackend recorder,
        string imagePath,
        PictureDiscLayout layout,
        DataBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new DataBurnOptions();

        // ---- checks: nothing is written until every one has passed ----
        if (options.Simulate)
        {
            return Refused("Test write (Simulate) is not supported when burning a two-session picture disc. Nothing was written to the disc.");
        }

        if (layout.Placement != PictureDiscFilesPlacement.OuterFiles || layout.Medium != PictureDiscMedium.Dvd)
        {
            return Refused("Only a DVD image with the picture inside and the files at the outer edge is burned in two sessions.");
        }

        if (layout.PictureStartLba != 0 || layout.PictureEndLba <= 0 || layout.PictureEndLba % EccBlock != 0
            || layout.FilesSessionStartLba < layout.PictureEndLba || layout.TotalSectors < layout.FilesSessionStartLba)
        {
            return Refused("The picture-disc description does not describe a picture from sector 0 followed by a files session.");
        }

        var image = new FileInfo(imagePath);
        if (!image.Exists)
        {
            return Refused($"The image was not found: {imagePath}");
        }

        if (image.Length != (long)layout.TotalSectors * Sector)
        {
            return Refused($"The image holds {image.Length:N0} bytes, but its description says {layout.TotalSectors:N0} sectors "
                + $"({(long)layout.TotalSectors * Sector:N0} bytes). Generate it again.");
        }

        CompilationDirectory root;
        IsoBuildOptions buildOptions;
        try
        {
            (root, buildOptions) = ReadFilesSession(imagePath, layout.FilesSessionStartLba);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return Refused($"The files session of the image could not be read at sector {layout.FilesSessionStartLba:N0}: {ex.Message}");
        }

        var blank = await MediaStatusWait.QueryWhenLoadedAsync(recorder, cancellationToken, _service.MediaLoadWait).ConfigureAwait(false);
        if (!blank.MediaPresent)
        {
            return Refused("No disc is in the drive.");
        }

        if (blank.IsLoading)
        {
            return Refused(MediaStatusWait.StillLoadingMessage);
        }

        blank = MediaStatusForWriting.Describe(_service.BurnEngine, recorder, blank);
        if (WhyNotSupported(blank.MediaType) is { } unsupported)
        {
            return Refused(unsupported);
        }

        if (!blank.IsBlank)
        {
            return Refused($"The {blank.MediaType.GetDisplayName()} in the drive is not blank. The picture session must start at sector 0 of a blank disc.");
        }

        int capacity = blank.FreeSectors > 0 ? blank.FreeSectors : blank.TotalSectors;
        int predicted = FilesSessionStartAfter(blank.MediaType, layout.PictureEndLba)!.Value;
        int predictedEnd = FilesSessionEnd(root, buildOptions, predicted);
        if (capacity <= 0 || predictedEnd > capacity)
        {
            return Refused(TooBigMessage(blank.MediaType, layout.PictureEndLba, predicted, predictedEnd, capacity))
                with { PredictedFilesSessionStartLba = predicted, CapacitySectors = capacity };
        }

        // ---- session 1: the picture zone, left open ----
        DataBurnResult picture;
        using (var pictureStream = new FileRangeStream(imagePath, 0, (long)layout.PictureEndLba * Sector))
        {
            picture = await _service.BurnStreamAsync(recorder, pictureStream,
                SessionOptions(options, closeDisc: false, new SectorRange(0, layout.PictureEndLba)),
                SessionProgress(progress, "Session 1 of 2 (picture): "), cancellationToken).ConfigureAwait(false);
        }

        var partial = new PictureDiscTwoSessionResult
        {
            PictureSectors = layout.PictureEndLba,
            PredictedFilesSessionStartLba = predicted,
            CapacitySectors = capacity,
            PictureSession = picture,
        };
        if (!picture.Success)
        {
            return partial with { ErrorMessage = "The picture session did not finish: " + picture.ErrorMessage };
        }

        // ---- the drive's next writable address; the files session is laid out for it ----
        var open = MediaStatusForWriting.Describe(_service.BurnEngine, recorder);
        if (open.IsBlank || !open.IsAppendable || open.NextWritableLba <= 0)
        {
            return partial with
            {
                ErrorMessage = "The picture was written, but the disc was not left open for the files session "
                    + $"(the drive reports {(open.IsBlank ? "a blank disc" : open.IsAppendable ? "no next writable address" : "a closed disc")}). "
                    + "The files were not written.",
            };
        }

        int start = open.NextWritableLba;
        // The disc's end as the blank disc reported it; the drive's free count after session 1 is a second opinion.
        int limit = open.FreeSectors > 0 ? Math.Min(capacity, (int)Math.Min(int.MaxValue, (long)start + open.FreeSectors)) : capacity;
        int end = FilesSessionEnd(root, buildOptions, start);
        if (start < layout.PictureEndLba || end > limit)
        {
            return partial with
            {
                FilesSessionStartLba = start,
                ErrorMessage = $"The picture session was written and left open, but the drive put the next session at sector {start:N0} "
                    + $"(expected {predicted:N0}), where the files ({end - start:N0} sectors) do not fit before sector {limit:N0}. "
                    + "The files were not written; the disc stays open.",
            };
        }

        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "Session 2 of 2 (files): Building files session",
            BufferLevelPercent = 100.0
        });

        string temp = Path.Combine(Path.GetTempPath(), $"ignisdisc-files-session-{Guid.NewGuid():N}.tmp");
        DataBurnResult files;
        try
        {
            await using var session = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await IsoUdfImageBuilder.BuildAsync(root, session, buildOptions, null, cancellationToken, start).ConfigureAwait(false);
            if (session.Length != (long)(FilesSessionEnd(root, buildOptions, start, align: false) - start) * Sector)
            {
                return partial with
                {
                    FilesSessionStartLba = start,
                    ErrorMessage = "The files session came out a different size than planned; the files were not written. The disc stays open.",
                };
            }

            session.Position = 0;
            files = await _service.BurnStreamAsync(recorder, session, SessionOptions(options, closeDisc: true),
                SessionProgress(progress, "Session 2 of 2 (files): "), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return partial with
            {
                FilesSessionStartLba = start,
                ErrorMessage = $"The picture session was written, but the files session could not be built: {ex.Message} The disc stays open.",
            };
        }

        var done = partial with
        {
            FilesSessionStartLba = start,
            FilesSessionSectors = end - start,
            FilesSession = files,
        };
        if (!files.Success)
        {
            return done with { ErrorMessage = "The picture session was written, but the files session did not finish: " + files.ErrorMessage };
        }

        // A DVD-R's table of contents can show the earlier session until the disc is reloaded (measured); DVD+R showed
        // the new session at once.
        return done with
        {
            Success = true,
            Warning = DataBurnService.JoinWarnings(files.Warning,
                blank.MediaType == DiscMediaType.DvdMinusR ? DataBurnService.AddedSessionHint : null),
        };
    }

    /// <summary>
    /// Reads the ISO 9660/Joliet session at <paramref name="sessionStartLba"/> of a whole-disc image into a compilation
    /// whose files read their bytes from the image (names, folders, dates and the hidden flag kept), and the build
    /// options to lay it out again (same label, ISO 9660 + Joliet, no UDF).
    /// </summary>
    /// <exception cref="InvalidDataException">There is no readable ISO 9660 session there.</exception>
    public static (CompilationDirectory Root, IsoBuildOptions Options) ReadFilesSession(string imagePath, int sessionStartLba)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        using var reader = IsoImageReader.FromFile(imagePath, sessionStartLba);
        if (!reader.HasIso9660)
        {
            throw new InvalidDataException("no ISO 9660 volume descriptor");
        }

        var root = CompilationDirectory.CreateRoot();
        CopyDirectory(reader.RootDirectory, root, imagePath, new FileInfo(imagePath).Length);
        if (!root.GetAllFilesRecursive().Any())
        {
            throw new InvalidDataException("the session holds no files");
        }

        var options = new IsoBuildOptions(
            VolumeLabel: string.IsNullOrWhiteSpace(reader.VolumeLabel) ? "DISC_ART" : reader.VolumeLabel,
            IncludeIso9660: true,
            IncludeJoliet: reader.HasJoliet,
            IncludeUdf: false);
        return (root, options);
    }

    private static void CopyDirectory(IsoDirectoryEntry source, CompilationDirectory target, string imagePath, long imageBytes)
    {
        foreach (var entry in source.Children)
        {
            if (entry is IsoDirectoryEntry directory)
            {
                var child = new CompilationDirectory(directory.Name, lastWriteTimeUtc: directory.RecordingTimeUtc,
                    attributes: directory.IsHidden ? FileAttributes.Hidden : FileAttributes.Normal);
                target.AddDirectory(child);
                CopyDirectory(directory, child, imagePath, imageBytes);
            }
            else if (entry is IsoFileEntry file)
            {
                long offset = (long)file.StartLba * Sector;
                if (file.SizeBytes < 0 || offset < 0 || offset + file.SizeBytes > imageBytes)
                {
                    throw new InvalidDataException($"{file.VirtualPath} points past the end of the image");
                }

                long length = file.SizeBytes;
                target.AddFile(new CompilationFile(file.Name, string.Empty, length,
                    streamFactory: () => new FileRangeStream(imagePath, offset, length),
                    lastWriteTimeUtc: file.RecordingTimeUtc,
                    attributes: file.IsHidden ? FileAttributes.Hidden : FileAttributes.Normal), DuplicateFilePolicy.ThrowError);
            }
        }
    }

    /// <summary>End (exclusive) of the files session built at <paramref name="start"/>, padded to an ECC block.</summary>
    private static int FilesSessionEnd(CompilationDirectory root, IsoBuildOptions options, int start, bool align = true)
    {
        int end = IsoUdfImageBuilder.CalculateTotalSectors(root, options, start);
        return align ? AlignUp(end, EccBlock) : end;
    }

    private static string TooBigMessage(DiscMediaType type, int pictureEnd, int start, int end, int capacity) =>
        $"The files no longer fit on this {type.GetDisplayName()}: after the picture (sectors 0 to {pictureEnd - 1:N0}) the drive "
        + $"starts the next session at sector {start:N0} ({start - pictureEnd:N0} sectors between sessions on this disc type), "
        + $"so the files would end at sector {end:N0}, but the disc has {capacity:N0}. "
        + "Generate the image again with fewer files or for this disc. Nothing was written to the disc.";

    private static DataBurnOptions SessionOptions(DataBurnOptions template, bool closeDisc, SectorRange? pictureZone = null) => new()
    {
        Speed = template.Speed,
        Simulate = false,
        VerifyAfterBurn = true,
        StrictVerify = template.StrictVerify,
        CloseDisc = closeDisc,
        VolumeLabel = template.VolumeLabel,
        OverwriteExistingData = false,
        DisableBufferUnderrunFree = template.DisableBufferUnderrunFree,
        PictureZone = pictureZone,
    };

    private static PictureDiscTwoSessionResult Refused(string message) => new()
    {
        Success = false,
        NothingWritten = true,
        ErrorMessage = message,
    };

    private static int AlignUp(int value, int align) => checked((value + align - 1) / align * align);

    private static IProgress<BurnProgressSnapshot>? SessionProgress(IProgress<BurnProgressSnapshot>? progress, string prefix) =>
        progress == null
            ? null
            : new InlineProgress(snap => progress.Report(new BurnProgressSnapshot
            {
                ElapsedTime = snap.ElapsedTime,
                RemainingTime = snap.RemainingTime,
                CurrentLba = snap.CurrentLba,
                TotalLba = snap.TotalLba,
                BufferLevelPercent = snap.BufferLevelPercent,
                CurrentSpeedMultiplier = snap.CurrentSpeedMultiplier,
                CurrentBytesPerSecond = snap.CurrentBytesPerSecond,
                CurrentActionDescription = string.IsNullOrWhiteSpace(snap.CurrentActionDescription)
                    ? prefix.TrimEnd(' ', ':')
                    : prefix + snap.CurrentActionDescription,
            }));

    /// <summary>Forwards on the caller's thread so snapshots keep their session order.</summary>
    private sealed class InlineProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }

    /// <summary>A read-only, seekable window of a file; opens the file itself and closes it when disposed.</summary>
    private sealed class FileRangeStream : Stream
    {
        private readonly FileStream _file;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public FileRangeStream(string path, long start, long length)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (start < 0 || length < 0 || start + length > _file.Length)
            {
                _file.Dispose();
                throw new InvalidDataException($"Bytes {start:N0} to {start + length:N0} lie outside {Path.GetFileName(path)}.");
            }

            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = _length - _position;
            if (left <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            int want = (int)Math.Min(buffer.Length, left);
            _file.Position = _start + _position;
            int read = _file.Read(buffer[..want]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
