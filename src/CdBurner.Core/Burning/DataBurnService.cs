global using DataBurnStatus = CdBurner.Core.Abstractions.BurnStatus;
using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Burning;

/// <summary>
/// High-level orchestration service for data disc and ISO image burning, capacity validation,
/// post-burn sector verification, and hardware media ejection.
/// </summary>
public class DataBurnService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly BookTypeService _bookTypeService;

    /// <summary>
    /// Shown when a burn would replace the files on a used rewritable disc and the caller did not allow it.
    /// </summary>
    public const string ReplaceExistingDataMessage =
        "This rewritable disc already has files on it, and burning will replace all of them. " +
        "Confirm that you want to replace them (command line: add --overwrite), or erase the disc first.";

    /// <summary>
    /// Shown when a burn targets a finalized sequential rewritable disc (such as CD-RW or sequential DVD-RW)
    /// that cannot be overwritten in place and must be erased first.
    /// </summary>
    public const string EraseRequiredMessage =
        "This rewritable disc already has finalized data on it and must be erased before burning again " +
        "(Drive Utilities -> Erase, or ignisdisc-cli erase --drive <id>).";

    /// <summary>
    /// Gets the underlying burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Gets the DVD Book Type service used for optional pre-burn bitsetting.
    /// </summary>
    public BookTypeService BookTypeService => _bookTypeService;

    /// <summary>
    /// Initializes a new instance of <see cref="DataBurnService"/> with the specified burning engine backend.
    /// </summary>
    /// <param name="burnEngine">The backend engine executing data streams against recorders.</param>
    /// <param name="bookTypeService">Optional Book Type service for pre-burn bitsetting.</param>
    public DataBurnService(IBurnEngineBackend burnEngine, BookTypeService? bookTypeService = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _bookTypeService = bookTypeService ?? new BookTypeService();
    }

    /// <summary>
    /// How long a burn waits for a disc the drive is still loading (<see cref="MediaStatusDto.IsLoading"/>) before
    /// it refuses with <see cref="MediaStatusWait.StillLoadingMessage"/>.
    /// </summary>
    public TimeSpan MediaLoadWait { get; init; } = MediaStatusWait.DefaultMaxWait;

    /// <summary>
    /// Compiles a virtual file system hierarchy from a <see cref="CompilationDirectory"/> into an ISO/UDF image stream
    /// and burns it to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        CompilationDirectory rootDirectory,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        burnOptions ??= new DataBurnOptions();
        buildOptions ??= new IsoBuildOptions(VolumeLabel: burnOptions.VolumeLabel ?? "CDBURNER_DISC");

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate media presence & state
        var mediaStatus = await MediaStatusWait.QueryWhenLoadedAsync(recorder, cancellationToken, MediaLoadWait);
        if (!mediaStatus.MediaPresent)
        {
            return DataBurnResult.Failed("No optical media present in recorder.");
        }

        if (mediaStatus.IsLoading)
        {
            return DataBurnResult.Failed(MediaStatusWait.StillLoadingMessage);
        }

        // The engine that writes may read the disc itself (a DVD-RW Windows calls overwrite-only can be open).
        mediaStatus = MediaStatusForWriting.Describe(_burnEngine, recorder, mediaStatus);

        if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
        {
            return DataBurnResult.Failed("Mounted media is not writable (disc is finalized or not appendable).");
        }

        if (mediaStatus.RequiresEraseToWrite)
        {
            return DataBurnResult.Failed(EraseRequiredMessage);
        }

        // 2. Build ISO image in memory
        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "BuildingFileSystem",
            CurrentLba = 0,
            TotalLba = 100,
            BufferLevelPercent = 100.0
        });

        cancellationToken.ThrowIfCancellationRequested();

        long estimatedPayloadBytes = rootDirectory.GetAllFilesRecursive().Sum(f => f.SizeBytes);
        Stream imageStream = estimatedPayloadBytes > 128L * 1024 * 1024
            ? new FileStream(
                Path.Combine(Path.GetTempPath(), $"cdburner_iso_{Guid.NewGuid():N}.tmp"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 65536,
                options: FileOptions.DeleteOnClose | FileOptions.SequentialScan | FileOptions.Asynchronous)
            : new MemoryStream();

        // When appending a session, the file system must be laid out relative to the drive's next writable address,
        // otherwise every directory and file pointer in the new session is off by the size of the earlier sessions.
        int sessionStartLba = (!mediaStatus.IsBlank && mediaStatus.IsAppendable && mediaStatus.NextWritableLba > 0)
            ? mediaStatus.NextWritableLba
            : 0;

        using (imageStream)
        {
            await IsoUdfImageBuilder.BuildAsync(rootDirectory, imageStream, buildOptions, null, cancellationToken, sessionStartLba);
            imageStream.Position = 0;

            // 3. Verify media capacity
            int requiredSectors = OpticalConstants.BytesToSectors(imageStream.Length, OpticalConstants.DataSectorSize);
            if (mediaStatus.FreeSectors > 0 && requiredSectors > mediaStatus.FreeSectors)
            {
                return DataBurnResult.Failed(
                    $"Compilation size ({requiredSectors} sectors) exceeds available media capacity ({mediaStatus.FreeSectors} sectors).");
            }

            // 4. Burn stream
            var result = await ExecuteBurnPipelineAsync(recorder, imageStream, burnOptions, requiredSectors, progress, cancellationToken);
            if (result.Success)
            {
                result.Warning = JoinWarnings(result.Warning, ComposeBurnWarning(
                    DescribeShortenedNames(rootDirectory, buildOptions, sessionStartLba),
                    sessionAdded: sessionStartLba > 0,
                    virtualDrive: recorder.IsVirtual));
            }
            return result;
        }
    }

    /// <summary>
    /// Shown after a session was added to a disc. A drive can keep reporting the disc as it was before the burn
    /// until the disc is reloaded (measured on a DVD-R: Windows went on showing only the first session).
    /// </summary>
    public const string AddedSessionHint =
        "If Windows still shows only the earlier files, take the disc out and put it back in: the new session is on the disc.";

    /// <summary>What a successful burn still has to tell the user; null when there is nothing.</summary>
    public static string? ComposeBurnWarning(string? shortenedNames, bool sessionAdded, bool virtualDrive)
    {
        string? reinsert = sessionAdded && !virtualDrive ? AddedSessionHint : null;
        return shortenedNames != null && reinsert != null ? shortenedNames + " " + reinsert : shortenedNames ?? reinsert;
    }

    /// <summary>Joliet holds 64 UTF-16 characters per name.</summary>
    public const int JolietNameLimit = 64;

    /// <summary>
    /// Says which names the disc cannot hold in full. Only UDF keeps long names, and a disc left open for more
    /// sessions, or a session added to one, is written without UDF: Windows then reads the Joliet names, which stop
    /// at <see cref="JolietNameLimit"/> characters. Null when every name fits or the disc carries UDF.
    /// </summary>
    public static string? DescribeShortenedNames(CompilationDirectory rootDirectory, IsoBuildOptions buildOptions, int sessionStartLba)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(buildOptions);
        bool udfOnDisc = buildOptions.IncludeUdf && sessionStartLba == 0;
        if (udfOnDisc || !buildOptions.IncludeJoliet)
        {
            return null;
        }

        int count = 0;
        string? example = null;
        var pending = new Stack<CompilationDirectory>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().Children)
            {
                if (child.Name.Length > JolietNameLimit)
                {
                    count++;
                    example ??= child.VirtualPath;
                }
                if (child is CompilationDirectory directory)
                {
                    pending.Push(directory);
                }
            }
        }

        if (count == 0)
        {
            return null;
        }

        string why = sessionStartLba > 0 ? "a session added to a disc" : "a disc without UDF (for example one left open for more sessions)";
        return $"{count} name(s) longer than {JolietNameLimit} characters were shortened on the disc, for example \"{example}\". " +
               $"Only UDF holds longer names, and {why} is read through Joliet. The contents of the files are complete.";
    }

    /// <summary>
    /// Overload accepting <see cref="IsoBuildOptions"/> before <see cref="DataBurnOptions"/>.
    /// </summary>
    public Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        CompilationDirectory rootDirectory,
        IsoBuildOptions? buildOptions,
        DataBurnOptions? burnOptions,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return BurnCompilationAsync(recorder, rootDirectory, burnOptions, buildOptions, progress, cancellationToken);
    }

    /// <summary>
    /// Compiles and burns a full <see cref="DataCompilation"/> project to the target recorder.
    /// </summary>
    public Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        DataCompilation compilation,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        burnOptions ??= new DataBurnOptions { VolumeLabel = compilation.VolumeLabel };
        buildOptions ??= new IsoBuildOptions(VolumeLabel: compilation.VolumeLabel);
        return BurnCompilationAsync(recorder, compilation.Root, burnOptions, buildOptions, progress, cancellationToken);
    }

    /// <summary>
    /// Burns an existing <c>.iso</c> disk image file directly to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnIsoFileAsync(
        IDiscRecorderBackend recorder,
        string isoFilePath,
        DataBurnOptions? burnOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoFilePath);

        if (!File.Exists(isoFilePath))
        {
            return DataBurnResult.Failed($"ISO image file not found: {isoFilePath}");
        }

        if (burnOptions?.PictureZone == null && DiscArtSidecarInfo.TryReadPictureZone(isoFilePath, out SectorRange pictureZone, out _))
        {
            burnOptions = WithPictureZone(burnOptions, pictureZone);
        }

        await using var fileStream = new FileStream(isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await BurnStreamAsync(recorder, fileStream, burnOptions, progress, cancellationToken);
    }

    private static DataBurnOptions WithPictureZone(DataBurnOptions? options, SectorRange pictureZone) =>
        options == null
            ? new DataBurnOptions { PictureZone = pictureZone }
            : new DataBurnOptions
            {
                Speed = options.Speed,
                Simulate = options.Simulate,
                EjectWhenDone = options.EjectWhenDone,
                EjectAfterBurn = options.EjectAfterBurn,
                VerifyAfterBurn = options.VerifyAfterBurn,
                StrictVerify = options.StrictVerify,
                CloseDisc = options.CloseDisc,
                VolumeLabel = options.VolumeLabel,
                OverwriteExistingData = options.OverwriteExistingData,
                LayerBreakSectors = options.LayerBreakSectors,
                DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
                CdRomXaMode2Form1 = options.CdRomXaMode2Form1,
                StreamingWrite = options.StreamingWrite,
                RequestedBookType = options.RequestedBookType,
                RequireBookType = options.RequireBookType,
                PictureZone = pictureZone
            };

    /// <summary>
    /// Burns an arbitrary data image stream to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions? burnOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(imageStream);
        burnOptions ??= new DataBurnOptions();

        cancellationToken.ThrowIfCancellationRequested();

        var mediaStatus = await MediaStatusWait.QueryWhenLoadedAsync(recorder, cancellationToken, MediaLoadWait);
        if (!mediaStatus.MediaPresent)
        {
            return DataBurnResult.Failed("No optical media present in recorder.");
        }

        if (mediaStatus.IsLoading)
        {
            return DataBurnResult.Failed(MediaStatusWait.StillLoadingMessage);
        }

        // The engine that writes may read the disc itself (a DVD-RW Windows calls overwrite-only can be open).
        mediaStatus = MediaStatusForWriting.Describe(_burnEngine, recorder, mediaStatus);

        if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
        {
            return DataBurnResult.Failed("Mounted media is not writable (disc is finalized or not appendable).");
        }

        if (mediaStatus.RequiresEraseToWrite)
        {
            return DataBurnResult.Failed(EraseRequiredMessage);
        }

        long streamLength = imageStream.CanSeek ? (imageStream.Length - imageStream.Position) : -1;
        int estimatedSectors = streamLength >= 0
            ? OpticalConstants.BytesToSectors(streamLength, OpticalConstants.DataSectorSize)
            : 0;

        if (estimatedSectors > 0 && mediaStatus.FreeSectors > 0 && estimatedSectors > mediaStatus.FreeSectors)
        {
            return DataBurnResult.Failed(
                $"Image size ({estimatedSectors} sectors) exceeds available media capacity ({mediaStatus.FreeSectors} sectors).");
        }

        return await ExecuteBurnPipelineAsync(recorder, imageStream, burnOptions, estimatedSectors, progress, cancellationToken);
    }

    private async Task<DataBurnResult> ExecuteBurnPipelineAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions burnOptions,
        int estimatedSectors,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        bool ejectRequested = burnOptions.EjectWhenDone || burnOptions.EjectAfterBurn;
        bool verifyRequested = burnOptions.VerifyAfterBurn;
        long initialStreamPosition = imageStream.CanSeek ? imageStream.Position : 0;
        var preBurnStatus = MediaStatusForWriting.Describe(_burnEngine, recorder);
        if (preBurnStatus.HasDataThatBurningReplaces && !burnOptions.OverwriteExistingData)
        {
            return DataBurnResult.Failed(ReplaceExistingDataMessage);
        }

        // Replacing the data of an overwrite-only disc (DVD+RW, DVD-RAM) writes from sector 0, whatever the disc says
        // about appending: Windows' writer forces the overwrite and the direct writer starts at 0.
        bool replacesFromStart = preBurnStatus.IsOverwriteOnly && burnOptions.OverwriteExistingData;
        int burnStartLba = (!replacesFromStart && !preBurnStatus.IsBlank && preBurnStatus.IsAppendable && preBurnStatus.NextWritableLba > 0)
            ? preBurnStatus.NextWritableLba
            : 0;

        var bookTypeOutcome = _bookTypeService.ApplyPreBurnBookType(
            recorder,
            preBurnStatus.MediaType,
            burnOptions.RequestedBookType,
            burnOptions.RequireBookType,
            burnOptions.Simulate);
        if (!bookTypeOutcome.ShouldProceed)
        {
            return DataBurnResult.Failed(bookTypeOutcome.ErrorMessage ?? "Book type setting failed.");
        }

        // When verification is requested, defer media ejection until verification finishes
        var engineOptions = new DataBurnOptions
        {
            Speed = burnOptions.Speed,
            Simulate = burnOptions.Simulate,
            EjectWhenDone = verifyRequested ? false : ejectRequested,
            VerifyAfterBurn = false,
            StrictVerify = burnOptions.StrictVerify,
            CloseDisc = burnOptions.CloseDisc,
            VolumeLabel = burnOptions.VolumeLabel,
            OverwriteExistingData = burnOptions.OverwriteExistingData,
            LayerBreakSectors = burnOptions.LayerBreakSectors,
            DisableBufferUnderrunFree = burnOptions.DisableBufferUnderrunFree,
            CdRomXaMode2Form1 = burnOptions.CdRomXaMode2Form1,
            StreamingWrite = burnOptions.StreamingWrite,
            RequestedBookType = burnOptions.RequestedBookType,
            RequireBookType = burnOptions.RequireBookType
        };

        BurnOperationResult burnResult;
        try
        {
            burnResult = await _burnEngine.BurnDataStreamAsync(
                recorder,
                imageStream,
                engineOptions,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException oce)
        {
            return DataBurnResult.Cancelled(oce.Message);
        }

        if (!burnResult.Success || burnResult.Status == BurnStatus.Cancelled)
        {
            return DataBurnResult.FromBurnResult(burnResult);
        }

        // A disc that was to stay open but can now only be written over: Windows' writer formats a blank DVD-RW into
        // restricted overwrite mode before writing (measured on a real drive), so it never holds a second session.
        string? leftOpenNote = null;
        if (!burnOptions.CloseDisc && !burnOptions.Simulate && !preBurnStatus.IsOverwriteOnly
            && MediaStatusForWriting.Describe(_burnEngine, recorder).IsOverwriteOnly)
        {
            leftOpenNote = NotLeftOpenMessage;
        }

        bool hasPictureZone = burnOptions.PictureZone is { Count: > 0 };
        bool verified = false;
        bool? filesCompared = burnOptions.Simulate ? null : false;
        bool? pictureAreaCompared = hasPictureZone && !burnOptions.Simulate ? false : null;
        string? verificationNote = null;
        if (verifyRequested && !burnOptions.Simulate)
        {
            int sectorsWritten = burnResult.SectorsWritten > 0 ? burnResult.SectorsWritten : estimatedSectors;
            try
            {
                if (burnOptions.PictureZone is { Count: > 0 } picZone)
                {
                    int picStartRel = Math.Clamp(picZone.Start, 0, sectorsWritten);
                    int picEndRel = Math.Clamp(picZone.Start + picZone.Count, picStartRel, sectorsWritten);
                    bool hasNonPictureSectors = picStartRel > 0 || picEndRel < sectorsWritten;
                    if (!hasNonPictureSectors)
                    {
                        verified = false;
                        filesCompared = false;
                        pictureAreaCompared = false;
                        verificationNote = FormatPictureOnlySummary(burnResult);
                    }
                    else
                    {
                        var verifyResult = await VerifyNonPictureRangesAsync(
                            recorder,
                            imageStream,
                            sectorsWritten,
                            initialStreamPosition,
                            burnStartLba,
                            picStartRel,
                            picEndRel,
                            burnOptions.StrictVerify,
                            progress,
                            cancellationToken);
                        if (!verifyResult.Success)
                        {
                            return verificationResultToFailure(verifyResult.ErrorMessage, cancellationToken);
                        }
                        verified = true;
                        filesCompared = true;
                        pictureAreaCompared = false;
                        verificationNote = JoinNotes(verifyResult.Note, "Picture area was not compared.");
                    }
                }
                else
                {
                    var verifyResult = await VerifyBurnedDiscAsync(
                        recorder,
                        imageStream,
                        sectorsWritten,
                        initialStreamPosition,
                        burnStartLba,
                        burnOptions.StrictVerify,
                        progress,
                        cancellationToken);
                    verificationNote = verifyResult.Note;
                    if (!verifyResult.Success)
                    {
                        return verificationResultToFailure(verifyResult.ErrorMessage, cancellationToken);
                    }
                    verified = true;
                    filesCompared = true;
                }
            }
            catch (OperationCanceledException oce)
            {
                return DataBurnResult.Cancelled(oce.Message);
            }
        }
        else if (hasPictureZone && !burnOptions.Simulate)
        {
            verificationNote = "Picture area was not compared.";
        }

        if (ejectRequested)
        {
            try
            {
                recorder.EjectMedia();
            }
            catch
            {
            }
        }

        var succeeded = new DataBurnResult
        {
            Success = true,
            Status = BurnStatus.Succeeded,
            SectorsWritten = burnResult.SectorsWritten,
            TotalTime = burnResult.TotalTime,
            AverageSpeedMultiplier = burnResult.AverageSpeedMultiplier,
            WritesAccepted = burnResult.WritesAccepted,
            SessionClosed = burnResult.SessionClosed,
            TocChecked = burnResult.TocChecked,
            Verified = verified,
            VerificationNote = verificationNote,
            BookTypeNote = bookTypeOutcome.Note,
            HasPictureZone = hasPictureZone,
            FilesCompared = filesCompared,
            PictureAreaCompared = pictureAreaCompared,
            Warning = JoinWarnings(leftOpenNote, bookTypeOutcome.IsWarning ? bookTypeOutcome.Note : null)
        };
        return succeeded;
    }

    private static DataBurnResult verificationResultToFailure(string? errorMessage, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? DataBurnResult.Cancelled(errorMessage ?? "Post-burn verification was cancelled.")
            : DataBurnResult.Failed(errorMessage ?? "Post-burn verification failed.");

    private static string FormatPictureOnlySummary(BurnOperationResult burnResult)
    {
        if (burnResult.WritesAccepted && burnResult.SessionClosed && burnResult.TocChecked)
        {
            return "All writes accepted, session closed, table of contents matches; picture area was not compared.";
        }
        if (burnResult.WritesAccepted && burnResult.SessionClosed)
        {
            return "All writes accepted, session closed; picture area was not compared.";
        }
        return "Picture area was not compared.";
    }

    private static string? JoinNotes(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second :
        string.IsNullOrWhiteSpace(second) ? first :
        $"{first} {second}";

    /// <summary>
    /// Shown when a burn asked to leave the disc open, but the disc came out overwrite-only. Windows' writer does this
    /// to every blank DVD-RW (it formats it into restricted overwrite mode first); direct drive commands keep a DVD-RW
    /// sequential after a full erase.
    /// </summary>
    public const string NotLeftOpenMessage =
        "The disc could not be left open for more sessions: Windows' writer formatted this DVD-RW for overwriting. " +
        "The files are on it, but a later burn replaces them instead of adding a session.";

    /// <summary>Joins two notes for the user, either of which may be missing.</summary>
    public static string? JoinWarnings(string? first, string? second) =>
        first == null ? second : second == null ? first : first + " " + second;

    /// <summary>
    /// Reads the burned sectors back and compares each one with the image (roadmap M1.3). The drive is told to read
    /// the disc itself, not its cache (<see cref="IBurnEngineBackend.ReadSectorsForVerifyAsync"/>). The disc's file
    /// system is parsed from its own sectors, so bad sectors are reported with the files that own them, and when the
    /// disc carries a root SHA256SUMS/BLAKE3SUMS every listed file is checked against it in the same pass.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage, string? Note)> VerifyBurnedDiscAsync(
        IDiscRecorderBackend recorder,
        Stream sourceStream,
        int sectorCount,
        long initialStreamPosition,
        int startLba,
        bool strict,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        // 1. The file system and hash lists, read from the disc's own sectors (small random reads, off this thread).
        var (fileMap, manifestCheck, manifestError) = await Task.Run(
            () => ReadDiscLayout(recorder, startLba, sectorCount, strict, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var _ = manifestCheck;
        if (manifestError != null)
        {
            return (false, manifestError, null);
        }

        // 2. One pass over the disc: compare every sector with the image and feed the hash-list check.
        if (sourceStream.CanSeek)
        {
            sourceStream.Position = initialStreamPosition;
        }

        byte[] expected = new byte[OpticalConstants.DataSectorSize];
        var mismatchedLbas = new List<int>();
        var ripResult = await _burnEngine.ReadSectorsForVerifyAsync(
            recorder,
            startLba,
            sectorCount,
            sectorConsumer: async (lba, sectorData) =>
            {
                int read = await ReadSectorFullyAsync(sourceStream, expected, cancellationToken).ConfigureAwait(false);
                if (read < expected.Length)
                {
                    Array.Clear(expected, read, expected.Length - read);
                }
                if (!sectorData.Span.SequenceEqual(expected))
                {
                    mismatchedLbas.Add(lba);
                }
                manifestCheck?.AddSector(lba, sectorData.Span);
            },
            progress,
            cancellationToken,
            strict).ConfigureAwait(false);

        if (ripResult.Note != null)
        {
            notes.Add(ripResult.Note);
        }

        if (!ripResult.Success)
        {
            if (ripResult.Exception is OperationCanceledException oce)
            {
                throw oce;
            }
            string where = ripResult.BadSectorsList.Count > 0 ? DescribeFiles(ripResult.BadSectorsList, fileMap) : string.Empty;
            return (false, (ripResult.ErrorMessage ?? "Failed to read burned sectors during verification.") + where, null);
        }

        if (mismatchedLbas.Count > 0)
        {
            return (false,
                $"Verification failed: {mismatchedLbas.Count} sector(s) on the disc differ from the image, starting at sector {mismatchedLbas[0]}." +
                DescribeFiles(mismatchedLbas, fileMap), null);
        }

        if (manifestCheck != null)
        {
            var check = manifestCheck.Finish();
            string lists = string.Join(" and ", check.ManifestNames);
            if (!check.Passed)
            {
                var bad = check.MismatchedFiles.Concat(check.MissingFiles.Select(p => p + " (missing)")).ToList();
                if (bad.Count == 0 && check.FilesNotLookedUp > 0)
                {
                    return (false,
                        $"Verification failed: {check.FilesNotLookedUp} file(s) listed in {lists} could not be looked up in the disc's UDF file system.",
                        null);
                }
                return (false,
                    $"Verification failed: {bad.Count} file(s) on the disc do not match its {lists}: " +
                    string.Join(", ", bad.Take(10)) + (bad.Count > 10 ? ", ..." : "") +
                    (check.FilesNotLookedUp > 0 ? $" ({check.FilesNotLookedUp} additional file(s) could not be looked up)" : "") + ".", null);
            }
            notes.Add($"All {check.FilesChecked} file(s) match the disc's {lists}.");
        }

        return (true, null, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>
    /// Compares every sector outside the picture zone <c>[picStartRel, picEndRel)</c> against the image and checks any
    /// root SHA256SUMS/BLAKE3SUMS manifest without reading back or comparing the picture zone itself.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage, string? Note)> VerifyNonPictureRangesAsync(
        IDiscRecorderBackend recorder,
        Stream sourceStream,
        int sectorCount,
        long initialStreamPosition,
        int startLba,
        int picStartRel,
        int picEndRel,
        bool strict,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var (fileMap, manifestCheck, manifestError) = await Task.Run(
            () => ReadDiscLayout(recorder, startLba, sectorCount, strict, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var _ = manifestCheck;
        if (manifestError != null)
        {
            return (false, manifestError, null);
        }

        var ranges = new List<(int OffsetRel, int Count)>(2);
        if (picStartRel > 0)
        {
            ranges.Add((0, picStartRel));
        }
        if (picEndRel < sectorCount)
        {
            ranges.Add((picEndRel, sectorCount - picEndRel));
        }

        byte[] expected = new byte[OpticalConstants.DataSectorSize];
        var mismatchedLbas = new List<int>();
        string? cacheNote = null;

        foreach (var (offsetRel, count) in ranges)
        {
            if (sourceStream.CanSeek)
            {
                sourceStream.Position = initialStreamPosition + (long)offsetRel * OpticalConstants.DataSectorSize;
            }

            var ripResult = await _burnEngine.ReadSectorsForVerifyAsync(
                recorder,
                startLba + offsetRel,
                count,
                sectorConsumer: async (lba, sectorData) =>
                {
                    int read = await ReadSectorFullyAsync(sourceStream, expected, cancellationToken).ConfigureAwait(false);
                    if (read < expected.Length)
                    {
                        Array.Clear(expected, read, expected.Length - read);
                    }
                    if (!sectorData.Span.SequenceEqual(expected))
                    {
                        mismatchedLbas.Add(lba);
                    }
                    manifestCheck?.AddSector(lba, sectorData.Span);
                },
                progress,
                cancellationToken,
                strict).ConfigureAwait(false);

            if (cacheNote == null && ripResult.Note != null)
            {
                cacheNote = ripResult.Note;
                notes.Add(ripResult.Note);
            }

            if (!ripResult.Success)
            {
                if (ripResult.Exception is OperationCanceledException oce)
                {
                    throw oce;
                }
                string where = ripResult.BadSectorsList.Count > 0 ? DescribeFiles(ripResult.BadSectorsList, fileMap) : string.Empty;
                return (false, (ripResult.ErrorMessage ?? "Failed to read burned sectors during verification.") + where, null);
            }
        }

        if (mismatchedLbas.Count > 0)
        {
            return (false,
                $"Verification failed: {mismatchedLbas.Count} sector(s) on the disc differ from the image, starting at sector {mismatchedLbas[0]}." +
                DescribeFiles(mismatchedLbas, fileMap), null);
        }

        if (manifestCheck != null)
        {
            var check = manifestCheck.Finish();
            string lists = string.Join(" and ", check.ManifestNames);
            if (!check.Passed)
            {
                var bad = check.MismatchedFiles.Concat(check.MissingFiles.Select(p => p + " (missing)")).ToList();
                if (bad.Count == 0 && check.FilesNotLookedUp > 0)
                {
                    return (false,
                        $"Verification failed: {check.FilesNotLookedUp} file(s) listed in {lists} could not be looked up in the disc's UDF file system.",
                        null);
                }
                return (false,
                    $"Verification failed: {bad.Count} file(s) on the disc do not match its {lists}: " +
                    string.Join(", ", bad.Take(10)) + (bad.Count > 10 ? ", ..." : "") +
                    (check.FilesNotLookedUp > 0 ? $" ({check.FilesNotLookedUp} additional file(s) could not be looked up)" : "") + ".", null);
            }
            notes.Add($"All {check.FilesChecked} file(s) match the disc's {lists}.");
        }

        return (true, null, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>
    /// Parses the burned file system from the disc's sectors. Returns the file extents (to name bad sectors) and the
    /// hash-list check, or an error if the disc has a hash list that cannot be read. A disc whose file system cannot
    /// be parsed (e.g. a non-ISO image) is still verified sector by sector, just without file names.
    /// </summary>
    private (IReadOnlyList<LbaFileExtentMap>? FileMap, DiscManifestCheck? Check, string? Error) ReadDiscLayout(
        IDiscRecorderBackend recorder, int startLba, int sectorCount, bool strict, CancellationToken cancellationToken)
    {
        string? ReadSectors(int lba, int count, byte[] buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int filled = 0;
            var result = _burnEngine.ReadSectorsForVerifyAsync(recorder, lba, count, (_, data) =>
            {
                data.Span.CopyTo(buffer.AsSpan(filled * OpticalConstants.DataSectorSize));
                filled++;
                return ValueTask.CompletedTask;
            }, null, cancellationToken, strict).GetAwaiter().GetResult();
            return result.Success && filled == count ? null : result.ErrorMessage ?? "read failed";
        }

        IsoImageReader reader;
        try
        {
            reader = IsoImageReader.FromStream(new SectorReadStream(ReadSectors, startLba + sectorCount), leaveOpen: false, startLba);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return (null, null, null);
        }

        using (reader)
        {
            var map = LbaFileExtentMap.FromIsoReader(reader);
            try
            {
                return (map, DiscManifestCheck.Prepare(reader), null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return (map, null, $"Verification failed: the hash list on the disc could not be read ({ex.Message}).");
            }
        }
    }

    private static string DescribeFiles(IReadOnlyList<int> lbas, IReadOnlyList<LbaFileExtentMap>? fileMap)
    {
        if (fileMap == null || fileMap.Count == 0)
        {
            return string.Empty;
        }
        var files = new List<string>();
        foreach (int lba in lbas)
        {
            foreach (var extent in fileMap)
            {
                if (extent.SectorCount > 0 && extent.ContainsLba(lba) && !files.Contains(extent.FilePath))
                {
                    files.Add(extent.FilePath);
                }
            }
        }
        return files.Count == 0
            ? " The damage is in the file system area, outside any file."
            : " Affected file(s): " + string.Join(", ", files.Take(10)) + (files.Count > 10 ? ", ..." : "") + ".";
    }

    private static async ValueTask<int> ReadSectorFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}

/// <summary>
/// Detailed result of a data disc burning operation, including verification status.
/// </summary>
public class DataBurnResult : BurnOperationResult
{
    /// <summary>
    /// Indicates whether post-burn verification was performed and passed successfully.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>
    /// What the post-burn check found worth saying when it passed, e.g. how many files matched the disc's SHA256SUMS,
    /// or that the drive would not skip its cache. Null when there is nothing to add.
    /// </summary>
    public string? VerificationNote { get; init; }

    /// <summary>
    /// Something the burn did that the user did not ask for and should know about, although the burn succeeded:
    /// names shortened on the disc. Null when there is nothing to say.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>
    /// Outcome of an optional pre-burn Book Type step (applied, skipped on unsupported media/drive, or null when not requested).
    /// </summary>
    public string? BookTypeNote { get; init; }

    /// <summary>
    /// Optional path to the temporary or source ISO image file burned.
    /// </summary>
    public string? ImagePath { get; init; }

    /// <summary>True when a picture zone was excluded from read-back comparison.</summary>
    public bool HasPictureZone { get; init; }

    /// <summary>Whether files on the disc were read back and compared (null when simulated).</summary>
    public bool? FilesCompared { get; init; }

    /// <summary>Whether the picture zone on the disc was compared (null when the burn has no picture zone).</summary>
    public bool? PictureAreaCompared { get; init; }

    public static DataBurnResult Succeeded(
        int sectorsWritten,
        TimeSpan duration,
        double avgSpeed = 0.0,
        bool verified = false,
        string? verificationNote = null) => new()
    {
        Success = true,
        Status = BurnStatus.Succeeded,
        SectorsWritten = sectorsWritten,
        TotalTime = duration,
        AverageSpeedMultiplier = avgSpeed,
        Verified = verified,
        VerificationNote = verificationNote
    };

    public static new DataBurnResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        Status = ex is OperationCanceledException ? BurnStatus.Cancelled : BurnStatus.Failed,
        ErrorMessage = error,
        Exception = ex
    };

    public static DataBurnResult Cancelled(string error = "Burn operation was cancelled.") => new()
    {
        Success = false,
        Status = BurnStatus.Cancelled,
        ErrorMessage = error
    };

    public static DataBurnResult FromBurnResult(BurnOperationResult result, bool verified = false) => new()
    {
        Success = result.Success,
        Status = result.Status != BurnStatus.NotStarted
            ? result.Status
            : (result.Success ? BurnStatus.Succeeded : (result.Exception is OperationCanceledException ? BurnStatus.Cancelled : BurnStatus.Failed)),
        ErrorMessage = result.ErrorMessage,
        SectorsWritten = result.SectorsWritten,
        TotalTime = result.TotalTime,
        AverageSpeedMultiplier = result.AverageSpeedMultiplier,
        WritesAccepted = result.WritesAccepted,
        SessionClosed = result.SessionClosed,
        TocChecked = result.TocChecked,
        Exception = result.Exception,
        Verified = verified
    };
}
