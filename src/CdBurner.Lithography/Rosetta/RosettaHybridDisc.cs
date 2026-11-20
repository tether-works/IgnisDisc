using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;

namespace CdBurner.Lithography.Rosetta;

/// <summary>Everything needed for one hybrid Rosetta DVD image.</summary>
/// <param name="TextFolder">Folder whose <c>.txt</c> and <c>.md</c> files (UTF-8, top level only, in name order) become
/// the pages and the vault's text.</param>
/// <param name="Disc">DVD geometry, from a preset or a saved calibration.</param>
/// <param name="OutputPath">The <c>.iso</c> to write.</param>
/// <param name="Title">Archive title (contents page, page headers, rim).</param>
/// <param name="Date">Archive date (contents page, rim).</param>
/// <param name="Preset">Glyph cell preset of the pages.</param>
/// <param name="FilesFolder">Optional extra files for the vault, kept under their own relative paths.</param>
/// <param name="PagesInnerRadiusMm">Radius where the pages begin; the vault and its gap end before the sector at this
/// radius.</param>
/// <param name="RedundancyPercent">Minimum PAR2 recovery data, percent of the vault's files (0: none).</param>
/// <param name="FillWithRecoveryData">Grow the recovery data until the vault reaches its ceiling.</param>
/// <param name="IncludeRimHeader">Title, date, SHA-256 prefix and angle ticks at the rim.</param>
/// <param name="Par2Path">par2 program; needed when <paramref name="RedundancyPercent"/> is above 0.</param>
public sealed record RosettaHybridRequest(
    string TextFolder,
    CalibrationParameters Disc,
    string OutputPath,
    string Title,
    DateOnly Date,
    RosettaPagePreset Preset,
    string? FilesFolder = null,
    double PagesInnerRadiusMm = RosettaHybridDisc.DefaultPagesInnerRadiusMm,
    int RedundancyPercent = 10,
    bool FillWithRecoveryData = true,
    bool IncludeRimHeader = true,
    double StartAngleDegrees = 0,
    string? Par2Path = null,
    bool Overwrite = false);

/// <summary>
/// The sector and page plan of a hybrid disc, from the geometry model and the page layout. Every capacity here is what
/// this layout gives on this disc geometry; none is taken from a report.
/// </summary>
public sealed record RosettaHybridPlan(
    long DiscSectors,
    int PagesStartLba,
    int PagesEndLba,
    double PagesStartRadiusMm,
    double PagesInnerRadiusMm,
    double PagesOuterRadiusMm,
    double? RimInnerRadiusMm,
    int VaultCeilingSectors,
    long VaultInputBytes,
    int VaultInputSectors,
    string PresetId,
    string PresetName,
    double CellWidthUm,
    double CellHeightUm,
    int Columns,
    int BodyRows,
    long PageCapacity,
    int PagesUsed,
    int DocumentCount,
    string TextSha256,
    IReadOnlyList<RosettaDocumentRange> Documents)
{
    /// <summary>Bytes the vault may take in all (files, manifests, recovery data and file system).</summary>
    public long VaultCeilingBytes => (long)VaultCeilingSectors * IsoUdfImageBuilder.SectorSize;

    /// <summary>Characters one page holds: columns times body rows (the first row is the page header).</summary>
    public int CharactersPerPage => Columns * BodyRows;

    /// <summary>Pages still free on this layout.</summary>
    public long PagesFree => PageCapacity - PagesUsed;
}

/// <summary>What a finished hybrid image holds.</summary>
public sealed record RosettaHybridResult(
    RosettaHybridPlan Plan,
    PictureDiscLayout Layout,
    string OutputPath,
    string SidecarPath,
    string VolumeLabel,
    string SetId,
    int RecoverySectors,
    int? RecoveryBlocks,
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// A hybrid Rosetta DVD: the archive vault (text, page index, optional files, manifests and PAR2 recovery data) as an
/// ISO 9660 + Joliet + UDF volume from LBA 0, clamped so that it and a zero gap of at least 2 MiB end before the sector
/// at the pages' inner radius (38 mm by default); then the microscope-readable text pages to the end of the disc, with
/// the rim header (title, date, SHA-256 prefix) at the edge. A computer sees only the vault; no page sector lies inside
/// the volume.
/// <para>
/// The image is built in <c>&lt;output&gt;.partial</c> and renamed when complete; staging happens in a work folder next to
/// the output that is always removed.
/// </para>
/// </summary>
public static class RosettaHybridDisc
{
    /// <summary>Default radius where the pages begin.</summary>
    public const double DefaultPagesInnerRadiusMm = 38.0;

    /// <summary>Folder on the disc that holds the text and the page index.</summary>
    public const string VaultFolderName = "ROSETTA";

    /// <summary>Folder under <see cref="VaultFolderName"/> holding the text files.</summary>
    public const string TextFolderName = "TEXT";

    public const string PageIndexTextFileName = "PAGE-INDEX.TXT";
    public const string PageIndexJsonFileName = "PAGE-INDEX.JSON";

    /// <summary>Suffix of the description file written next to the image.</summary>
    public const string SidecarSuffix = ".rosetta.json";

    private const int Align = OpticalConstants.DvdEccBlockSectors;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The plan, without staging the vault or drawing any page.</summary>
    public static RosettaHybridPlan Plan(RosettaHybridRequest request) => Prepare(request).Plan;

    /// <summary>
    /// Computes the disc sector and page layout plan for a disc geometry and Rosetta preset before any text folder
    /// is chosen, using the exact same <see cref="DiscTrack"/> and <see cref="RosettaBook"/> layout math as
    /// <see cref="Plan(RosettaHybridRequest)"/>.
    /// </summary>
    public static RosettaHybridPlan PlanLayout(
        CalibrationParameters disc,
        RosettaPagePreset preset,
        string? title = null,
        DateOnly? date = null,
        double pagesInnerRadiusMm = DefaultPagesInnerRadiusMm,
        bool includeRimHeader = true,
        double startAngleDegrees = 0)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(preset);
        string resolvedTitle = string.IsNullOrWhiteSpace(title) ? "Rosetta Archive" : title.Trim();
        DateOnly resolvedDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return PrepareCore(
            disc,
            preset,
            resolvedTitle,
            resolvedDate,
            Array.Empty<VaultText>(),
            filesFolder: null,
            pagesInnerRadiusMm,
            includeRimHeader,
            startAngleDegrees,
            outputPath: string.Empty).Plan;
    }

    /// <summary>Stages the vault (with PAR2 when asked), writes it, the gap and the pages into the image.</summary>
    public static async Task<RosettaHybridResult> GenerateAsync(
        RosettaHybridRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Prepared p = Prepare(request);
        if (request.RedundancyPercent > 0 && string.IsNullOrWhiteSpace(request.Par2Path))
        {
            throw new ArgumentException(
                "PAR2 recovery data needs par2 (install it with 'ignisdisc-cli install-par2'), or use --redundancy 0 for manifests only.");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string outputDir = Path.GetDirectoryName(p.OutputPath)!;
        Directory.CreateDirectory(outputDir);
        string work = Path.Combine(outputDir, Path.GetFileName(p.OutputPath) + ".rosetta-work-" + Guid.NewGuid().ToString("N")[..8]);
        string partial = p.OutputPath + ".partial";
        string sidecarPath = p.OutputPath + SidecarSuffix;
        bool finished = false;
        bool moved = false;
        try
        {
            // 1. The vault's own files: the text as hashed, and the page index.
            Report(progress, stopwatch, "vault", "Writing the text and page index…", 0);
            string source = Path.Combine(work, "src");
            var sources = WriteVaultSources(p, source);
            if (p.FilesFolder is { } files)
            {
                sources.AddRange(ListFolder(files));
            }

            // 2. Manifests and PAR2, through the archive stager, with the ceiling the radius allows.
            ArchivePlan archivePlan = ArchiveSpanPlanner.Plan(sources, DiscMediaType.DvdMinusR, request.RedundancyPercent,
                setId: null, fillFreeSpace: request.FillWithRecoveryData && request.RedundancyPercent > 0,
                ceilingSectorsOverride: p.Plan.VaultCeilingSectors);
            if (archivePlan.Discs.Count != 1)
            {
                throw new ArgumentException(
                    $"The vault's files do not fit inside r = {request.PagesInnerRadiusMm:0.###} mm: the archive needs " +
                    $"{archivePlan.Discs.Count} discs of {p.Plan.VaultCeilingBytes / 1048576.0:N1} MB. Leave out files or move the pages outward.");
            }

            var stageProgress = new Relay<string>(message => Report(progress, stopwatch, "vault", message, null));
            ArchiveStagedDisc staged = (await ArchiveStager.StageDiscsAsync(archivePlan, Path.Combine(work, "stage"),
                request.RedundancyPercent > 0 ? request.Par2Path : null, stageProgress, cancellationToken).ConfigureAwait(false)).Single();
            cancellationToken.ThrowIfCancellationRequested();

            // 3. The vault's volume, clamped before the pages.
            string label = ArchiveManifest.VolumeLabel(archivePlan.SetId, 1);
            var isoOptions = new IsoBuildOptions(VolumeLabel: label, IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true);
            CompilationDirectory root = CompilationFolderImporter.ImportAsRoot(staged.DiscRoot, strict: true);
            PictureDiscLayout layout;
            try
            {
                layout = PictureDiscLayoutPlanner.PlanInnerBefore(root, isoOptions, PictureDiscMedium.Dvd,
                    p.Plan.PagesStartLba, p.Plan.PagesEndLba);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("The vault does not end before the pages. " + ex.Message);
            }

            // 4. Vault, zero gap, pages: one pass, the pages streamed straight into the image.
            MultiBlockSolveOptions solve = RosettaDvdEncoder.BuildSolveOptions(p.Encode with
            {
                StartLba = (uint)layout.PictureStartLba,
                FillSectors = (uint)layout.PictureSectorCount,
            });
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan))
            {
                Report(progress, stopwatch, "compose", "Writing the vault…", 0);
                PictureDiscImageComposer.Compose(root, isoOptions, layout, zone =>
                {
                    RosettaDvdEncoder.WriteStream(p.Book, zone, solve, value =>
                    {
                        if (value.CompletedBlocks == 1 || value.CompletedBlocks % 256 == 0 || value.CompletedBlocks == value.TotalBlocks)
                        {
                            Report(progress, stopwatch, "pages", "Drawing the pages…", value.Fraction);
                        }
                    }, cancellationToken);
                }, output);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, p.OutputPath, request.Overwrite);
            moved = true;
            long bytes = new FileInfo(p.OutputPath).Length;
            var result = new RosettaHybridResult(p.Plan, layout, p.OutputPath, sidecarPath, label, archivePlan.SetId,
                staged.RecoverySectors, staged.Recovery?.RecoveryBlocks, bytes, stopwatch.Elapsed);
            WriteSidecar(sidecarPath, request, result);
            finished = true;
            Report(progress, stopwatch, "complete", "Hybrid Rosetta image completed.", 1);
            return result;
        }
        finally
        {
            if (!finished)
            {
                // A half-written image never sits under the final name, and an image never stays without its description.
                TryDeleteFile(partial);
                if (moved)
                {
                    TryDeleteFile(p.OutputPath);
                    TryDeleteFile(sidecarPath);
                }
            }

            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// The SHA-256 of a text set as the rim and contents page show it: for each document in order, its title, a line
    /// feed, its text with line ends as line feeds, and a line feed, hashed as one UTF-8 stream.
    /// </summary>
    public static string TextSha256(IReadOnlyList<RosettaDocument> documents) => RosettaBook.ComputeTextSha256Hex(documents);

    // ----------------------------------------------------------------------------------------------------------------
    // Planning
    // ----------------------------------------------------------------------------------------------------------------

    private sealed record Prepared(
        RosettaHybridPlan Plan,
        RosettaBook Book,
        RosettaDvdEncodeOptions Encode,
        IReadOnlyList<VaultText> Texts,
        string OutputPath,
        string? FilesFolder);

    private sealed record VaultText(string FileName, RosettaDocument Document);

    private static Prepared Prepare(RosettaHybridRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Disc);
        ArgumentNullException.ThrowIfNull(request.Preset);
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Give the archive a title.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath)
            || !string.Equals(Path.GetExtension(request.OutputPath), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The hybrid disc is written as an .iso file.");
        }

        string output = Path.GetFullPath(request.OutputPath);
        if (!request.Overwrite && File.Exists(output))
        {
            throw new ArgumentException($"The file already exists: {output} (use --overwrite to replace it).");
        }

        if (request.RedundancyPercent is < 0 or > 100)
        {
            throw new ArgumentException("The redundancy must be 0 to 100 percent.");
        }

        string? filesFolder = null;
        if (request.FilesFolder is not null)
        {
            filesFolder = Path.GetFullPath(request.FilesFolder);
            if (!Directory.Exists(filesFolder))
            {
                throw new ArgumentException($"The files folder was not found: {filesFolder}");
            }

            if (Directory.Exists(Path.Combine(filesFolder, VaultFolderName)) || File.Exists(Path.Combine(filesFolder, VaultFolderName)))
            {
                throw new ArgumentException(
                    $"The files folder has its own '{VaultFolderName}' entry at the top, a name the vault uses for the text. Rename or move it.");
            }

            if (IsInside(output, filesFolder))
            {
                throw new ArgumentException("The output file must not be inside the files folder.");
            }
        }

        IReadOnlyList<VaultText> texts = LoadTexts(request.TextFolder);
        return PrepareCore(
            request.Disc,
            request.Preset,
            request.Title.Trim(),
            request.Date,
            texts,
            filesFolder,
            request.PagesInnerRadiusMm,
            request.IncludeRimHeader,
            request.StartAngleDegrees,
            output);
    }

    private static Prepared PrepareCore(
        CalibrationParameters disc,
        RosettaPagePreset preset,
        string title,
        DateOnly date,
        IReadOnlyList<VaultText> texts,
        string? filesFolder,
        double pagesInnerRadiusMm,
        bool includeRimHeader,
        double startAngleDegrees,
        string outputPath)
    {
        if (disc.Kind != CalibrationDiscKind.Dvd)
        {
            throw new ArgumentException(
                "A hybrid Rosetta disc is DVD-R only for now. CD-R comes after the CD port, with glyph cells of at least 50 x 75 um.");
        }

        if (!double.IsFinite(pagesInnerRadiusMm) || !double.IsFinite(startAngleDegrees))
        {
            throw new ArgumentException("The radius and start angle must be finite numbers.");
        }

        RosettaDvdEncodeOptions encode;
        try
        {
            disc.Validate();
            encode = RosettaDvdEncodeOptions.FromCalibration(disc, startAngleDegrees);
            if (preset.CellSize.WidthUm <= 0 || preset.Columns <= 0)
            {
                throw new ArgumentException("The cell preset is not valid.");
            }
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message);
        }

        // One geometry: the encoder's own track model places every sector, so the clamp uses it too.
        var geometry = new DiscTrack(encode);
        uint outputSectors = encode.UsableSectors ?? encode.TotalSectors;
        int pagesEnd = checked((int)(outputSectors / Align * Align));
        if (pagesInnerRadiusMm <= encode.InnerRadiusMm || pagesInnerRadiusMm >= encode.OuterRadiusMm)
        {
            throw new ArgumentException(
                $"The pages must start between the disc's inner and outer radius ({encode.InnerRadiusMm:0.###} to {encode.OuterRadiusMm:0.###} mm).");
        }

        // The page zone starts on the ECC block that holds the pages' inner radius, so no page pixel is lost.
        long lbaAtRadius = geometry.LbaAtRadius(pagesInnerRadiusMm);
        int pagesStart = checked((int)(lbaAtRadius / Align * Align));
        int vaultCeiling = pagesStart - PictureDiscLayoutPlanner.MinimumGapSectors;
        if (vaultCeiling < 512)
        {
            throw new ArgumentException(
                $"Only {Math.Max(0, vaultCeiling):N0} sectors lie inside r = {pagesInnerRadiusMm:0.###} mm after the 2 MiB gap; move the pages outward.");
        }

        if (pagesEnd - pagesStart < Align)
        {
            throw new ArgumentException("No page sector fits between the pages' inner radius and the end of the disc.");
        }

        double pagesOuter = Math.Min(encode.OuterRadiusMm, geometry.RadiusAtLba(pagesEnd));
        RosettaBook book;
        try
        {
            book = RosettaBook.Build(
                texts.Select(t => t.Document).ToList(),
                new RosettaArchiveOptions(
                    title,
                    date,
                    preset,
                    InnerRadiusMm: pagesInnerRadiusMm,
                    OuterRadiusMm: pagesOuter,
                    HasInnerFileVault: true,
                    IncludeRimHeader: includeRimHeader));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException("The text does not fit on the pages: " + ex.Message);
        }

        // What the vault must hold before manifests, recovery data and file system: an early, plain refusal.
        long inputBytes = texts.Sum(t => (long)StrictUtf8.GetByteCount(t.Document.Text)) + EstimatePageIndexBytes(book);
        int inputSectors = texts.Sum(t => SectorsOf(StrictUtf8.GetByteCount(t.Document.Text))) + 2 * SectorsOf(EstimatePageIndexBytes(book) / 2);
        if (filesFolder is not null)
        {
            foreach (string file in Directory.EnumerateFiles(filesFolder, "*", SearchOption.AllDirectories))
            {
                long size = new FileInfo(file).Length;
                inputBytes += size;
                inputSectors = checked(inputSectors + SectorsOf(size));
            }
        }

        if (inputSectors > vaultCeiling)
        {
            throw new ArgumentException(
                $"The vault's files take {inputBytes / 1048576.0:N1} MB, more than the {vaultCeiling * 2048L / 1048576.0:N1} MB " +
                $"that fit inside r = {pagesInnerRadiusMm:0.###} mm with the 2 MiB gap.");
        }

        RosettaBand body = book.Bands[^1];
        var plan = new RosettaHybridPlan(
            DiscSectors: encode.TotalSectors,
            PagesStartLba: pagesStart,
            PagesEndLba: pagesEnd,
            PagesStartRadiusMm: geometry.RadiusAtLba(pagesStart),
            PagesInnerRadiusMm: pagesInnerRadiusMm,
            PagesOuterRadiusMm: book.RimHeader is { } rim ? rim.InnerRadiusMm - RosettaRimHeader.PageGutterMm : pagesOuter,
            RimInnerRadiusMm: book.RimHeader?.InnerRadiusMm,
            VaultCeilingSectors: vaultCeiling,
            VaultInputBytes: inputBytes,
            VaultInputSectors: inputSectors,
            PresetId: preset.Id,
            PresetName: preset.Name,
            CellWidthUm: preset.CellSize.WidthUm,
            CellHeightUm: preset.CellSize.HeightUm,
            Columns: body.Preset.Columns,
            BodyRows: body.Preset.BodyRows,
            PageCapacity: book.TotalPhysicalTiles,
            PagesUsed: book.TotalPages,
            DocumentCount: texts.Count,
            TextSha256: book.TextSha256Hex,
            Documents: book.DocumentRanges);
        return new Prepared(plan, book, encode, texts, outputPath, filesFolder);
    }

    private static IReadOnlyList<VaultText> LoadTexts(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            throw new ArgumentException($"The text folder was not found: {folder}");
        }

        var files = Directory.EnumerateFiles(Path.GetFullPath(folder), "*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new ArgumentException($"The text folder has no .txt or .md files: {folder}");
        }

        var texts = new List<VaultText>(files.Count);
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, skip, bytes.Length - skip);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException($"'{Path.GetFileName(file)}' is not UTF-8 text. Save it as UTF-8 and try again.");
            }

            // Line ends become line feeds, exactly as the SHA-256 counts them, so the vault's file is the hashed bytes.
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string name = Path.GetFileName(file);
            texts.Add(new VaultText(name, new RosettaDocument(Path.GetFileNameWithoutExtension(name), text)));
        }

        return texts;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Vault files
    // ----------------------------------------------------------------------------------------------------------------

    private static List<ArchiveSourceFile> WriteVaultSources(Prepared p, string sourceRoot)
    {
        string textDir = Path.Combine(sourceRoot, VaultFolderName, TextFolderName);
        Directory.CreateDirectory(textDir);
        var list = new List<ArchiveSourceFile>();
        foreach (VaultText text in p.Texts)
        {
            string path = Path.Combine(textDir, text.FileName);
            File.WriteAllBytes(path, StrictUtf8.GetBytes(text.Document.Text));
            list.Add(new ArchiveSourceFile(path, $"{VaultFolderName}/{TextFolderName}/{text.FileName}", new FileInfo(path).Length));
        }

        string indexText = Path.Combine(sourceRoot, VaultFolderName, PageIndexTextFileName);
        File.WriteAllText(indexText, BuildPageIndexText(p.Book, p.Plan, p.Texts), StrictUtf8);
        list.Add(new ArchiveSourceFile(indexText, $"{VaultFolderName}/{PageIndexTextFileName}", new FileInfo(indexText).Length));

        string indexJson = Path.Combine(sourceRoot, VaultFolderName, PageIndexJsonFileName);
        File.WriteAllText(indexJson, BuildPageIndexJson(p.Book, p.Plan, p.Texts), StrictUtf8);
        list.Add(new ArchiveSourceFile(indexJson, $"{VaultFolderName}/{PageIndexJsonFileName}", new FileInfo(indexJson).Length));
        return list;
    }

    private static IEnumerable<ArchiveSourceFile> ListFolder(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string full = Path.GetFullPath(file);
            yield return new ArchiveSourceFile(full, Path.GetRelativePath(folder, full).Replace('\\', '/'), new FileInfo(full).Length);
        }
    }

    private static string PageTitle(RosettaBook book, int pageNumber)
        => book.Titles[book.Pages[pageNumber - 1].TitleSourceIndex];

    private static string Tile(RosettaBook book, int pageNumber)
    {
        (int ring, int sector) = book.GetTile(pageNumber);
        return Geometry.PolarPageLayout.PageLabel(ring, sector);
    }

    private static string BuildPageIndexText(RosettaBook book, RosettaHybridPlan plan, IReadOnlyList<VaultText> texts)
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        Line("ROSETTA PAGE INDEX");
        Line();
        Line($"Title: {book.ArchiveTitle}");
        Line($"Date: {book.ArchiveDate:yyyy-MM-dd}");
        Line($"Text SHA-256: {book.TextSha256Hex}");
        Line($"The rim and page R00-S00 show its first 12 digits: {book.TextSha256Prefix}");
        Line();
        Line($"How the SHA-256 is computed: for each file in {VaultFolderName}/{TextFolderName}, in the order listed below,");
        Line("take the file name without its extension, a line feed (0x0A), the file's bytes, and another line feed;");
        Line("hash all of it as one stream with SHA-256.");
        Line();
        Line(string.Create(CultureInfo.InvariantCulture,
            $"Pages: {plan.PagesUsed} of {plan.PageCapacity} tiles, glyph cells {plan.CellWidthUm:0.###} x {plan.CellHeightUm:0.###} um ({plan.PresetName}), {plan.Columns} columns x {plan.BodyRows} text rows."));
        Line(string.Create(CultureInfo.InvariantCulture,
            $"Rings are counted outward from radius {plan.PagesInnerRadiusMm:0.###} mm; sectors clockwise as seen from the shiny side."));
        Line();
        Line("Documents (first and last page, first and last tile, file):");
        for (int i = 0; i < book.DocumentRanges.Count; i++)
        {
            RosettaDocumentRange range = book.DocumentRanges[i];
            string file = $"{VaultFolderName}/{TextFolderName}/{texts[i].FileName}";
            if (range.LastPageNumber < range.FirstPageNumber)
            {
                Line($"  (no pages)  {file}");
                continue;
            }

            Line(string.Create(CultureInfo.InvariantCulture,
                $"  p {range.FirstPageNumber:D4}-{range.LastPageNumber:D4}  {Tile(book, range.FirstPageNumber)} to {Tile(book, range.LastPageNumber)}  {file}"));
        }

        Line();
        Line("Every page:");
        for (int page = 1; page <= book.TotalPages; page++)
        {
            Line(string.Create(CultureInfo.InvariantCulture, $"  p {page:D4}  {Tile(book, page)}  {PageTitle(book, page)}"));
        }

        return sb.ToString();
    }

    private static string BuildPageIndexJson(RosettaBook book, RosettaHybridPlan plan, IReadOnlyList<VaultText> texts)
    {
        var doc = new
        {
            format = "ignisdisc-rosetta-page-index-1",
            title = book.ArchiveTitle,
            date = book.ArchiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            textSha256 = book.TextSha256Hex,
            presetId = plan.PresetId,
            cellWidthUm = plan.CellWidthUm,
            cellHeightUm = plan.CellHeightUm,
            columns = plan.Columns,
            bodyRows = plan.BodyRows,
            pagesInnerRadiusMm = plan.PagesInnerRadiusMm,
            pageCapacity = plan.PageCapacity,
            pagesUsed = plan.PagesUsed,
            documents = book.DocumentRanges.Select((range, i) => new
            {
                title = range.Title,
                file = $"{VaultFolderName}/{TextFolderName}/{texts[i].FileName}",
                firstPage = range.FirstPageNumber,
                lastPage = range.LastPageNumber,
            }),
            pages = Enumerable.Range(1, book.TotalPages).Select(page => new
            {
                page,
                tile = Tile(book, page),
                title = PageTitle(book, page),
            }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>A generous size for the two index files, for the early fit check only.</summary>
    private static long EstimatePageIndexBytes(RosettaBook book) => 4096 + (long)book.TotalPages * 200;

    // ----------------------------------------------------------------------------------------------------------------
    // Description file
    // ----------------------------------------------------------------------------------------------------------------

    private static void WriteSidecar(string path, RosettaHybridRequest request, RosettaHybridResult result)
    {
        RosettaHybridPlan plan = result.Plan;
        var doc = new
        {
            format = "ignisdisc-rosetta-hybrid-1",
            media = "DVD-R",
            title = request.Title.Trim(),
            date = request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            textSha256 = plan.TextSha256,
            setId = result.SetId,
            volumeLabel = result.VolumeLabel,
            disc = request.Disc,
            startAngleDegrees = request.StartAngleDegrees,
            rimHeader = request.IncludeRimHeader,
            vault = new
            {
                volumeEndLba = result.Layout.VolumeEndLba,
                ceilingSectors = plan.VaultCeilingSectors,
                gapStartLba = result.Layout.GapStartLba,
                gapEndLba = result.Layout.GapEndLba,
                recoverySectors = result.RecoverySectors,
                recoveryBlocks = result.RecoveryBlocks,
            },
            pages = new
            {
                startLba = result.Layout.PictureStartLba,
                endLba = result.Layout.PictureEndLba,
                startRadiusMm = plan.PagesStartRadiusMm,
                innerRadiusMm = plan.PagesInnerRadiusMm,
                outerRadiusMm = plan.PagesOuterRadiusMm,
                rimInnerRadiusMm = plan.RimInnerRadiusMm,
                presetId = plan.PresetId,
                cellWidthUm = plan.CellWidthUm,
                cellHeightUm = plan.CellHeightUm,
                capacity = plan.PageCapacity,
                used = plan.PagesUsed,
            },
            totalSectors = result.Layout.TotalSectors,
            createdUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>LBA and radius on the encoder's track model (constant linear density, the disc's pitch terms).</summary>
    private sealed class DiscTrack
    {
        private readonly DvdTrackGeometry _geometry;
        private readonly double _sectorLengthMm;

        public DiscTrack(RosettaDvdEncodeOptions encode)
        {
            _sectorLengthMm = DvdEccBlockEncoder.ChannelBitsPerSector * encode.ChannelBitLengthNm * 1e-6;
            _geometry = DvdTrackGeometry.Create(encode.InnerRadiusMm, encode.OuterRadiusMm,
                encode.TotalSectors * _sectorLengthMm, encode.PitchLinear, encode.PitchQuadratic, encode.PitchCubic);
        }

        public long LbaAtRadius(double radiusMm) => (long)Math.Floor(_geometry.ArcLengthAtRadius(radiusMm) / _sectorLengthMm);

        public double RadiusAtLba(long lba) => _geometry.RadiusAtArcLength(Math.Min(lba * _sectorLengthMm, _geometry.TrackLengthMm));
    }

    private static int SectorsOf(long bytes) => checked((int)((bytes + IsoUdfImageBuilder.SectorSize - 1) / IsoUdfImageBuilder.SectorSize));

    private static bool IsInside(string path, string folder)
    {
        string root = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(IProgress<DiscJobProgress>? progress, Stopwatch stopwatch, string stage, string message, double? fraction)
        => progress?.Report(new DiscJobProgress("rosetta", stage, message,
            fraction is { } f ? Math.Clamp(f, 0, 1) : null, null, null, stopwatch.Elapsed));

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
