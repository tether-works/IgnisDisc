using System.Globalization;
using System.Text.Json;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;

namespace CdBurner.Cli;

/// <summary>
/// Disc image inspection/conversion, offline DVD authoring, jewel case covers, and audio planning CLI commands:
/// <c>image-info</c>, <c>convert-image</c>, <c>author-dvd</c>, <c>jewel-cover</c>, and <c>audio-plan</c>,
/// plus shared helpers used by <c>burn-audio</c> and <c>burn-dvd</c>.
/// </summary>
public static partial class CliCommandRunner
{
    private static int HandleImageInfo(CommandLineArgs cmd, TextWriter stdout)
    {
        string? imagePath = cmd.GetOption("image") ?? cmd.GetOption("input");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new CommandLineException("Missing required option: --image <file>");
        }

        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{fullPath}'.", fullPath);
        }

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext == ".cue")
        {
            var cue = CueSheetParser.ParseFile(fullPath);
            var trackEntries = new List<object>();
            foreach (var file in cue.Files)
            {
                foreach (var trk in file.Tracks)
                {
                    var idx01 = trk.Indices.FirstOrDefault(idx => idx.IndexNumber == 1)?.Position;
                    trackEntries.Add(new
                    {
                        number = trk.TrackNumber,
                        dataType = trk.DataType.ToString(),
                        isAudio = trk.IsAudio,
                        file = file.FileName,
                        title = trk.Title,
                        performer = trk.Performer,
                        isrc = trk.Isrc,
                        index01 = idx01?.ToString()
                    });
                }
            }

            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    imagePath = fullPath,
                    formatName = "CDRWIN CUE Sheet",
                    title = cue.Title,
                    performer = cue.Performer,
                    catalog = cue.Catalog,
                    trackCount = trackEntries.Count,
                    isAudioDisc = trackEntries.Count > 0 && cue.Files.SelectMany(f => f.Tracks).All(t => t.IsAudio),
                    tracks = trackEntries
                });
                return 0;
            }

            stdout.WriteLine($"Image File:     {fullPath}");
            stdout.WriteLine("Format:         CDRWIN CUE Sheet");
            if (!string.IsNullOrWhiteSpace(cue.Title)) stdout.WriteLine($"Title:          {cue.Title}");
            if (!string.IsNullOrWhiteSpace(cue.Performer)) stdout.WriteLine($"Performer:      {cue.Performer}");
            stdout.WriteLine($"Tracks:         {trackEntries.Count}");
            return 0;
        }

        LegacyDiscImageInfo info;
        try
        {
            info = LegacyDiscImageConverter.Inspect(fullPath);
        }
        catch (NotSupportedException ex)
        {
            throw new CommandLineException(ex.Message);
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                imagePath = info.SourceFilePath,
                binaryPayloadPath = info.BinaryPayloadPath,
                formatName = info.FormatName,
                payloadOffsetBytes = info.PayloadOffsetBytes,
                payloadLengthBytes = info.PayloadLengthBytes,
                sectorSizeBytes = info.SectorSizeBytes,
                sectorCount = info.SectorCount,
                isRaw2352 = info.IsRaw2352,
                isAudioDisc = info.IsAudioDisc,
                trackCount = info.TrackCount
            });
            return 0;
        }

        stdout.WriteLine($"Image File:     {info.SourceFilePath}");
        stdout.WriteLine($"Format:         {info.FormatName}");
        stdout.WriteLine($"Payload:        {info.BinaryPayloadPath}");
        stdout.WriteLine($"Payload Offset: {info.PayloadOffsetBytes:N0} bytes");
        stdout.WriteLine($"Payload Size:   {FormatBytes(info.PayloadLengthBytes)} ({info.PayloadLengthBytes:N0} bytes)");
        stdout.WriteLine($"Sector Size:    {info.SectorSizeBytes} bytes ({(info.IsRaw2352 ? "raw 2352" : "cooked")})");
        stdout.WriteLine($"Sectors:        {info.SectorCount:N0}");
        stdout.WriteLine($"Tracks:         {info.TrackCount}");
        stdout.WriteLine($"Content Kind:   {(info.IsAudioDisc ? "Audio CD" : "Data")}");
        return 0;
    }

    private static async Task<int> HandleConvertImageAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string inputPath = Path.GetFullPath(cmd.GetRequiredOption("input", "--input <file>"));
        string outputPath = Path.GetFullPath(cmd.GetRequiredOption("output", "--output <file>"));
        string? cueOption = cmd.GetOption("cue");
        bool overwrite = cmd.HasFlag("overwrite");

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Source disc image file not found: '{inputPath}'.", inputPath);
        }

        string srcExt = Path.GetExtension(inputPath).ToLowerInvariant();
        string dstExt = Path.GetExtension(outputPath).ToLowerInvariant();

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException("Output path must differ from input path.");
        }

        var progress = cmd.IsJson ? null : new SubtitleStepProgress(stderr);
        IProgress<double>? pctProgress = progress == null
            ? null
            : new Progress<double>(f => progress.Report((f * 100.0, $"Converting image... {f * 100.0:0.0}%")));

        int sectorsWritten;
        string? primaryOutput = outputPath;
        string? cueOutput = null;

        if (srcExt == ".cue" && dstExt == ".iso")
        {
            EnsureOutputCanBeWritten(outputPath, overwrite);
            sectorsWritten = await LegacyDiscImageConverter.ConvertBinCueToIsoAsync(
                inputPath, outputPath, pctProgress).ConfigureAwait(false);
        }
        else if (dstExt is ".bin" or ".cue" &&
                 srcExt is ".iso" or ".nrg" or ".cdi" or ".mdf" or ".mds" or ".ccd" or ".img" or ".bin")
        {
            string binPath = dstExt == ".cue"
                ? Path.ChangeExtension(outputPath, ".bin")
                : outputPath;
            string cuePath = !string.IsNullOrWhiteSpace(cueOption)
                ? Path.GetFullPath(cueOption)
                : (dstExt == ".cue" ? outputPath : Path.ChangeExtension(outputPath, ".cue"));

            if (string.Equals(binPath, inputPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cuePath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException("Output BIN/CUE path must differ from input path.");
            }

            EnsureOutputCanBeWritten(binPath, overwrite);
            EnsureOutputCanBeWritten(cuePath, overwrite);

            sectorsWritten = await LegacyDiscImageConverter.ConvertImageToBinCueAsync(
                inputPath, binPath, cuePath, pctProgress).ConfigureAwait(false);
            primaryOutput = binPath;
            cueOutput = cuePath;
        }
        else if (dstExt == ".iso" &&
                 srcExt is ".nrg" or ".cdi" or ".mdf" or ".mds" or ".ccd" or ".img" or ".bin")
        {
            EnsureOutputCanBeWritten(outputPath, overwrite);
            var sectorProgress = progress == null
                ? null
                : new Progress<BurnProgressSnapshot>(s => progress.Report((s.ProgressPercentage, s.CurrentActionDescription)));
            sectorsWritten = await LegacyDiscImageConverter.ConvertToIsoAsync(
                inputPath, outputPath, sectorProgress).ConfigureAwait(false);
        }
        else
        {
            throw new CommandLineException(
                $"Unsupported conversion from '{srcExt}' to '{dstExt}'. Supported targets: legacy/BIN/CUE -> .iso, or ISO/legacy/BIN -> .bin/.cue.");
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                source = inputPath,
                output = primaryOutput,
                cueOutput,
                sectorsWritten,
                bytesWritten = (long)sectorsWritten * OpticalConstants.DataSectorSize
            });
            return 0;
        }

        stdout.WriteLine("Disc image conversion completed:");
        stdout.WriteLine($"  Source:  {inputPath}");
        stdout.WriteLine($"  Output:  {primaryOutput}");
        if (cueOutput != null)
        {
            stdout.WriteLine($"  CUE:     {cueOutput}");
        }
        stdout.WriteLine($"  Sectors: {sectorsWritten:N0}");
        return 0;
    }

    internal static DvdAuthoringOptions BuildDvdAuthoringOptions(CommandLineArgs cmd, string volumeLabel)
    {
        string? standardStr = cmd.GetOption("standard");
        DvdVideoStandard standard = DvdVideoStandard.Ntsc;
        if (!string.IsNullOrWhiteSpace(standardStr))
        {
            standard = standardStr.Trim().ToLowerInvariant() switch
            {
                "ntsc" => DvdVideoStandard.Ntsc,
                "pal" => DvdVideoStandard.Pal,
                _ => throw new CommandLineException($"Invalid --standard '{standardStr}'. Use ntsc or pal.")
            };
        }

        string? aspectStr = cmd.GetOption("aspect");
        DvdAspectRatio aspect = DvdAspectRatio.Widescreen16x9;
        if (!string.IsNullOrWhiteSpace(aspectStr))
        {
            aspect = aspectStr.Trim().ToLowerInvariant() switch
            {
                "16:9" or "16x9" or "wide" or "widescreen" => DvdAspectRatio.Widescreen16x9,
                "4:3" or "4x3" or "full" or "fullscreen" => DvdAspectRatio.Standard4x3,
                _ => throw new CommandLineException($"Invalid --aspect '{aspectStr}'. Use 16:9 or 4:3.")
            };
        }

        bool autoCreatePlayMenu = !cmd.HasFlag("no-menu");
        string? menuOpt = cmd.GetOption("menu");
        if (!string.IsNullOrWhiteSpace(menuOpt))
        {
            bool menuOn = menuOpt.Trim().ToLowerInvariant() switch
            {
                "on" or "true" => true,
                "off" or "false" => false,
                _ => throw new CommandLineException($"Invalid --menu '{menuOpt}'. Use on or off.")
            };
            if (cmd.HasFlag("no-menu") && menuOn)
            {
                throw new CommandLineException("Cannot combine --menu on with --no-menu.");
            }
            autoCreatePlayMenu = menuOn;
        }

        int menuAutoPlaySeconds = 0;
        string? menuAutoPlayRaw = cmd.GetOption("menu-autoplay");
        if (!string.IsNullOrWhiteSpace(menuAutoPlayRaw))
        {
            string trimmed = menuAutoPlayRaw.Trim().ToLowerInvariant();
            if (trimmed is "never" or "off")
            {
                menuAutoPlaySeconds = 0;
            }
            else if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out menuAutoPlaySeconds) ||
                     menuAutoPlaySeconds < 0 || menuAutoPlaySeconds > 254)
            {
                throw new CommandLineException($"Invalid --menu-autoplay '{menuAutoPlayRaw}'. Use 'never' or an integer from 0 to 254 seconds.");
            }
        }

        DvdAfterTitleAction afterTitleAction = DvdAfterTitleAction.PlayNextTitle;
        string? afterTitleRaw = cmd.GetOption("after-title");
        if (!string.IsNullOrWhiteSpace(afterTitleRaw))
        {
            afterTitleAction = afterTitleRaw.Trim().ToLowerInvariant() switch
            {
                "next" or "play-next" => DvdAfterTitleAction.PlayNextTitle,
                "menu" or "title-menu" => DvdAfterTitleAction.ReturnToMenu,
                _ => throw new CommandLineException($"Invalid --after-title '{afterTitleRaw}'. Use next or menu.")
            };
        }

        int chapterSeconds = 300;
        string? chaptersRaw = cmd.GetOption("chapters");
        if (!string.IsNullOrWhiteSpace(chaptersRaw))
        {
            string trimmed = chaptersRaw.Trim().ToLowerInvariant();
            if (trimmed is "none" or "off")
            {
                chapterSeconds = 0;
            }
            else if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out chapterSeconds) ||
                     chapterSeconds < 0 || chapterSeconds > 3600)
            {
                throw new CommandLineException($"Invalid --chapters '{chaptersRaw}'. Use 'none' or an integer from 0 to 3600 seconds.");
            }
        }

        bool subtitlesOnByDefault = false;
        string? subsDefaultRaw = cmd.GetOption("subtitles-default");
        if (!string.IsNullOrWhiteSpace(subsDefaultRaw))
        {
            subtitlesOnByDefault = subsDefaultRaw.Trim().ToLowerInvariant() switch
            {
                "on" or "true" => true,
                "off" or "false" => false,
                _ => throw new CommandLineException($"Invalid --subtitles-default '{subsDefaultRaw}'. Use on or off.")
            };
        }

        DvdSubtitleSize subtitleSize = DvdSubtitleSize.Medium;
        string? subtitleSizeRaw = cmd.GetOption("subtitle-size");
        if (!string.IsNullOrWhiteSpace(subtitleSizeRaw))
        {
            subtitleSize = subtitleSizeRaw.Trim().ToLowerInvariant() switch
            {
                "small" => DvdSubtitleSize.Small,
                "medium" => DvdSubtitleSize.Medium,
                "large" => DvdSubtitleSize.Large,
                _ => throw new CommandLineException($"Invalid --subtitle-size '{subtitleSizeRaw}'. Use small, medium, or large.")
            };
        }

        bool fitToDisc = true;
        int videoBitrateKbps = 6000;
        int maxVideoBitrateKbps = 8000;
        string? videoBitrateRaw = cmd.GetOption("video-bitrate");
        if (!string.IsNullOrWhiteSpace(videoBitrateRaw))
        {
            string trimmed = videoBitrateRaw.Trim().ToLowerInvariant();
            if (trimmed == "fit")
            {
                fitToDisc = true;
            }
            else if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kbps) &&
                     kbps >= DvdVideoAuthoringService.MinimumVideoKbps && kbps <= 9000)
            {
                videoBitrateKbps = kbps;
                maxVideoBitrateKbps = Math.Clamp(Math.Max(8000, (int)(kbps * 1.25)), kbps, 9200);
                fitToDisc = cmd.HasFlag("fit-to-disc");
            }
            else
            {
                throw new CommandLineException($"Invalid --video-bitrate '{videoBitrateRaw}'. Use 'fit' or an integer from {DvdVideoAuthoringService.MinimumVideoKbps} to 9000 kbps.");
            }
        }

        string? ffmpegOpt = cmd.GetOption("ffmpeg");
        var options = new DvdAuthoringOptions
        {
            VolumeLabel = volumeLabel,
            MenuTitle = cmd.GetOption("menu-title") ?? "DVD Video",
            AutoCreatePlayMenu = autoCreatePlayMenu,
            MenuAutoPlaySeconds = menuAutoPlaySeconds,
            AfterTitleAction = afterTitleAction,
            Standard = standard,
            AspectRatio = aspect,
            ChapterIntervalSeconds = chapterSeconds,
            SubtitlesOnByDefault = subtitlesOnByDefault,
            SubtitleSize = subtitleSize,
            FitToDisc = fitToDisc,
            VideoBitrateKbps = videoBitrateKbps,
            MaxVideoBitrateKbps = maxVideoBitrateKbps,
            DualLayer = cmd.HasFlag("dual-layer"),
            UseFfmpegWhenAvailable = !cmd.HasFlag("no-ffmpeg"),
            CustomFfmpegPath = ffmpegOpt
        };
        string? titleNamesRaw = cmd.GetOption("title-names");
        if (!string.IsNullOrEmpty(titleNamesRaw))
        {
            foreach (string part in titleNamesRaw.Split(';'))
            {
                options.OrderedTitleNames.Add(part.Trim());
            }
        }
        ApplySubtitleOptions(cmd, options);
        return options;
    }

    private static (CompilationDirectory Root, DvdVideoValidationResult Validation) CompileDvdDirectory(
        string sourceRootDirectory,
        string effectiveLabel)
    {
        var root = CompilationDirectory.CreateRoot();
        CompilationDirectory videoTsDir;

        string folderName = Path.GetFileName(sourceRootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(folderName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            videoTsDir = root.AddDirectory("VIDEO_TS");
            PopulateDirectoryFromDisk(videoTsDir, sourceRootDirectory, skipDvdIgnoredFiles: true);

            string? parent = Path.GetDirectoryName(Path.GetFullPath(sourceRootDirectory));
            if (!string.IsNullOrEmpty(parent))
            {
                string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
                if (Directory.Exists(siblingAudioTs))
                {
                    var audioTs = root.AddDirectory("AUDIO_TS");
                    PopulateDirectoryFromDisk(audioTs, siblingAudioTs, skipDvdIgnoredFiles: true);
                }
            }
        }
        else
        {
            string childVideoTs = Path.Combine(sourceRootDirectory, "VIDEO_TS");
            if (Directory.Exists(childVideoTs))
            {
                videoTsDir = root.AddDirectory("VIDEO_TS");
                PopulateDirectoryFromDisk(videoTsDir, childVideoTs, skipDvdIgnoredFiles: true);

                string childAudioTs = Path.Combine(sourceRootDirectory, "AUDIO_TS");
                if (Directory.Exists(childAudioTs))
                {
                    var audioTs = root.AddDirectory("AUDIO_TS");
                    PopulateDirectoryFromDisk(audioTs, childAudioTs, skipDvdIgnoredFiles: true);
                }
            }
            else
            {
                videoTsDir = root.AddDirectory("VIDEO_TS");
                PopulateDirectoryFromDisk(videoTsDir, sourceRootDirectory, skipDvdIgnoredFiles: true);
            }
        }

        if (root.FindDirectoryByVirtualPath("/AUDIO_TS") == null)
        {
            root.AddDirectory("AUDIO_TS");
        }

        var validator = new DvdVideoValidator();
        bool hasAudioTs = root.FindDirectoryByVirtualPath("/AUDIO_TS") != null;
        var validation = validator.ValidateDirectory(videoTsDir, hasAudioTs);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"DVD-Video structure validation failed: {string.Join("; ", validation.Errors)}");
        }

        var planner = new DvdVideoLayoutPlanner();
        planner.ApplyToCompilation(videoTsDir, startLba: 0);
        return (root, validation);
    }

    private static async Task<int> HandleAuthorDvdAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string input = cmd.GetOption("input") ?? cmd.GetOption("video-ts")
            ?? throw new CommandLineException("Missing required option: --input <folder|movies>");
        string output = Path.GetFullPath(cmd.GetRequiredOption("output", "--output <folder|file.iso>"));
        bool overwrite = cmd.HasFlag("overwrite");

        var pathTokens = input.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allTokensExist = pathTokens.Length > 0 && pathTokens.All(p => File.Exists(p) || Directory.Exists(p));
        if (!File.Exists(input) && !Directory.Exists(input) && !allTokensExist)
        {
            throw new DirectoryNotFoundException($"DVD-Video input path does not exist: '{input}'.");
        }

        bool outputIsIso = string.Equals(Path.GetExtension(output), ".iso", StringComparison.OrdinalIgnoreCase);
        if (outputIsIso)
        {
            EnsureOutputCanBeWritten(output, overwrite);
        }
        else if (Directory.Exists(output))
        {
            string existingVideoTs = Path.Combine(output, "VIDEO_TS");
            if (!overwrite && Directory.Exists(existingVideoTs) && Directory.EnumerateFileSystemEntries(existingVideoTs).Any())
            {
                throw new CommandLineException(
                    $"Target folder '{output}' already contains authored VIDEO_TS files. Pass --overwrite to replace them.");
            }
        }

        var analysis = DvdInputNormalizer.AnalyzeInput(input);
        if (!analysis.CanAuthorOrBurn)
        {
            throw new CommandLineException($"Input cannot be authored to DVD-Video: {string.Join("; ", analysis.NormalizationNotes)}");
        }

        string effectiveLabel = !string.IsNullOrWhiteSpace(cmd.GetOption("label"))
            ? cmd.GetOption("label")!
            : (analysis.SuggestedVolumeLabel ?? "DVD_VIDEO");
        var authorOptions = BuildDvdAuthoringOptions(cmd, effectiveLabel);

        string stagingRoot = outputIsIso
            ? Path.Combine(Path.GetTempPath(), "IgnisDisc_CliAuthor_" + Guid.NewGuid().ToString("N"))
            : output;

        try
        {
            DvdAuthoringResult? authResult = null;
            string compiledSourceRoot;
            if (analysis.RequiresAuthoring)
            {
                var authorService = new DvdVideoAuthoringService();
                authResult = await authorService.AuthorAsync(
                    input,
                    stagingRoot,
                    authorOptions,
                    cmd.IsJson ? null : new SubtitleStepProgress(stderr)).ConfigureAwait(false);
                if (!authResult.Success)
                {
                    throw new InvalidOperationException($"DVD-Video authoring failed: {authResult.ErrorMessage}");
                }

                compiledSourceRoot = authResult.OutputRootDirectory;
            }
            else
            {
                compiledSourceRoot = analysis.ExistingVideoTsPath ?? input;
                if (!outputIsIso)
                {
                    CopyPreparedDvdTreeToOutput(compiledSourceRoot, output, overwrite);
                    compiledSourceRoot = output;
                }
            }

            var (root, validation) = CompileDvdDirectory(compiledSourceRoot, effectiveLabel);
            long totalBytes = root.GetAllFilesRecursive().Sum(f => f.SizeBytes);
            int totalSectors = (int)root.GetAllFilesRecursive().Sum(f => (f.SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);

            if (outputIsIso)
            {
                string? isoDir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(isoDir) && !Directory.Exists(isoDir))
                {
                    Directory.CreateDirectory(isoDir);
                }

                var buildOptions = new IsoBuildOptions(
                    VolumeLabel: effectiveLabel,
                    IncludeIso9660: true,
                    IncludeUdf: true,
                    IncludeJoliet: false,
                    UdfRevision: 0x0102);

                await IsoUdfImageBuilder.BuildToFileAsync(root, output, buildOptions).ConfigureAwait(false);
                totalBytes = new FileInfo(output).Length;
                totalSectors = (int)(totalBytes / OpticalConstants.DataSectorSize);
            }

            if (cmd.IsJson)
            {
                WriteJsonResult(stdout, new
                {
                    success = true,
                    output,
                    outputKind = outputIsIso ? "iso" : "folder",
                    volumeLabel = effectiveLabel,
                    standard = authorOptions.Standard.ToString(),
                    aspectRatio = authorOptions.AspectRatio == DvdAspectRatio.Widescreen16x9 ? "16:9" : "4:3",
                    menuCreated = authResult != null ? authorOptions.AutoCreatePlayMenu : false,
                    autoCreatePlayMenu = authorOptions.AutoCreatePlayMenu,
                    menuAutoPlaySeconds = authorOptions.MenuAutoPlaySeconds,
                    afterTitleAction = authorOptions.AfterTitleAction.ToString(),
                    chapterIntervalSeconds = authorOptions.ChapterIntervalSeconds,
                    subtitlesOnByDefault = authorOptions.SubtitlesOnByDefault,
                    subtitleSize = authorOptions.SubtitleSize.ToString(),
                    fitToDisc = authorOptions.FitToDisc,
                    videoBitrateKbps = authorOptions.VideoBitrateKbps,
                    dualLayer = authorOptions.DualLayer,
                    titleCount = authResult?.TitleCount ?? Math.Max(validation.TitleSetNumbers.Count, 1),
                    validatedFiles = validation.FileCount,
                    totalSectors,
                    totalBytes,
                    logNotes = authResult?.LogNotes ?? Array.Empty<string>()
                });
                return 0;
            }

            stdout.WriteLine("DVD-Video authoring completed (no disc written):");
            stdout.WriteLine($"  Output:       {output} ({(outputIsIso ? "ISO image" : "VIDEO_TS folder")})");
            stdout.WriteLine($"  Volume Label: {effectiveLabel}");
            stdout.WriteLine($"  Standard:     {authorOptions.Standard} ({(authorOptions.AspectRatio == DvdAspectRatio.Widescreen16x9 ? "16:9" : "4:3")})");
            stdout.WriteLine($"  Size:         {FormatBytes(totalBytes)} ({totalSectors:N0} sectors)");
            return 0;
        }
        finally
        {
            if (outputIsIso && Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, recursive: true); } catch { }
            }
        }
    }

    private static void CopyPreparedDvdTreeToOutput(string existingVideoTsOrRoot, string outputFolder, bool overwrite)
    {
        string folderName = Path.GetFileName(existingVideoTsOrRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string srcVideoTs = string.Equals(folderName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            ? existingVideoTsOrRoot
            : Path.Combine(existingVideoTsOrRoot, "VIDEO_TS");
        if (!Directory.Exists(srcVideoTs))
        {
            srcVideoTs = existingVideoTsOrRoot;
        }

        string destVideoTs = Path.Combine(outputFolder, "VIDEO_TS");
        string destAudioTs = Path.Combine(outputFolder, "AUDIO_TS");
        Directory.CreateDirectory(destVideoTs);
        Directory.CreateDirectory(destAudioTs);

        foreach (string file in Directory.GetFiles(srcVideoTs, "*", SearchOption.TopDirectoryOnly))
        {
            string destFile = Path.Combine(destVideoTs, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);
        }
    }

    internal static (AudioCdCompilation Compilation, string? LoadNote) BuildAudioCompilation(
        CommandLineArgs cmd,
        IAudioDecoder? audioDecoder,
        TextWriter stderr)
    {
        string? tracksRaw = cmd.GetOption("tracks");
        string? tracksFile = cmd.GetOption("tracks-file");

        if (string.IsNullOrWhiteSpace(tracksRaw) && string.IsNullOrWhiteSpace(tracksFile))
        {
            throw new CommandLineException("Missing audio tracks: provide --tracks <file1,file2,...> or --tracks-file <manifest.json>.");
        }

        string? titleOpt = cmd.GetOption("title");
        string? artistOpt = cmd.GetOption("artist");
        string? songwriterOpt = cmd.GetOption("songwriter");
        string? upcOpt = cmd.GetOption("upc");
        bool dao = cmd.HasFlag("dao");
        bool noCdText = cmd.HasFlag("no-cd-text");
        bool peakNormalize = cmd.HasFlag("normalize") || cmd.HasFlag("peak-normalize");
        bool rmsLevel = cmd.HasFlag("rms-level");
        string? loudnessRaw = cmd.GetOption("loudness");
        string? crossfadeRaw = cmd.GetOption("crossfade");
        string? pregapRaw = cmd.GetOption("pregap");

        LoudnessNormalizationMode loudnessMode = LoudnessNormalizationMode.Off;
        if (!string.IsNullOrWhiteSpace(loudnessRaw))
        {
            loudnessMode = loudnessRaw.Trim().ToLowerInvariant() switch
            {
                "off" or "none" => LoudnessNormalizationMode.Off,
                "track" or "per-track" or "pertrack" => LoudnessNormalizationMode.PerTrack,
                "album" or "per-album" or "peralbum" => LoudnessNormalizationMode.PerAlbum,
                _ => throw new CommandLineException($"Invalid --loudness '{loudnessRaw}'. Use track, album, or off.")
            };
        }

        double crossfadeSeconds = 0.0;
        if (!string.IsNullOrWhiteSpace(crossfadeRaw))
        {
            if (!double.TryParse(crossfadeRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out crossfadeSeconds) ||
                crossfadeSeconds < 0.0 || crossfadeSeconds > 10.0)
            {
                throw new CommandLineException($"Invalid --crossfade '{crossfadeRaw}'. Use a duration from 0 to 10 seconds.");
            }
            if (crossfadeSeconds > 0.0 && !dao)
            {
                throw new CommandLineException("--crossfade requires Disc-at-Once gapless mode (--dao).");
            }
        }

        int defaultPreGapFrames = dao ? 0 : OpticalConstants.CdLeadInFrames;
        bool explicitPreGap = false;
        if (!string.IsNullOrWhiteSpace(pregapRaw))
        {
            if (!int.TryParse(pregapRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out defaultPreGapFrames) ||
                defaultPreGapFrames < 0 || defaultPreGapFrames > 4500)
            {
                throw new CommandLineException($"Invalid --pregap '{pregapRaw}'. Use frames from 0 to 4500 (75 frames = 1 second).");
            }
            explicitPreGap = true;
        }

        if (!string.IsNullOrWhiteSpace(upcOpt) &&
            (upcOpt.Trim().Length != 13 || !upcOpt.Trim().All(char.IsAsciiDigit)))
        {
            throw new CommandLineException("--upc must be exactly 13 ASCII decimal digits.");
        }

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = titleOpt ?? "Audio CD",
            AlbumPerformer = artistOpt ?? "Various Artists",
            Songwriter = songwriterOpt,
            UpcEan = upcOpt?.Trim(),
            RecordingMode = dao ? AudioRecordingMode.DiscAtOnceGapless : AudioRecordingMode.TrackAtOnce,
            IncludeCdText = !noCdText && (!string.IsNullOrEmpty(titleOpt) || !string.IsNullOrEmpty(artistOpt) || !string.IsNullOrEmpty(songwriterOpt)),
            NormalizeAllTracks = peakNormalize,
            UseRmsLoudnessLeveling = rmsLevel,
            LoudnessNormalization = loudnessMode,
            CrossfadeSeconds = crossfadeSeconds,
            DefaultPreGapFrames = explicitPreGap ? defaultPreGapFrames : OpticalConstants.CdLeadInFrames
        };

        if (!string.IsNullOrWhiteSpace(tracksFile))
        {
            LoadTracksFromJsonManifest(Path.GetFullPath(tracksFile), compilation, artistOpt, titleOpt, explicitPreGap ? defaultPreGapFrames : null, peakNormalize, noCdText, audioDecoder, stderr);
        }

        if (!string.IsNullOrWhiteSpace(tracksRaw))
        {
            var trackFiles = tracksRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (trackFiles.Length == 0 && compilation.TrackCount == 0)
            {
                throw new CommandLineException("No track files provided in --tracks argument.");
            }

            foreach (var trackPath in trackFiles)
            {
                if (!File.Exists(trackPath))
                {
                    throw new FileNotFoundException($"Audio track file not found: {trackPath}", trackPath);
                }

                if (string.Equals(Path.GetExtension(trackPath), ".cue", StringComparison.OrdinalIgnoreCase))
                {
                    compilation.RecordingMode = AudioRecordingMode.DiscAtOnceGapless;
                    compilation.ImportCueSheet(trackPath, audioDecoder);
                    if (!noCdText) compilation.IncludeCdText = true;
                    continue;
                }

                if (AudioPlaylistParser.IsPlaylistFile(trackPath))
                {
                    compilation.ImportPlaylist(trackPath, audioDecoder);
                    if (!noCdText) compilation.IncludeCdText = true;
                    continue;
                }

                AddSingleAudioTrack(
                    compilation,
                    trackPath,
                    titleOverride: null,
                    performerOverride: null,
                    artistGlobalOverride: artistOpt,
                    titleGlobalOverride: titleOpt,
                    songwriterOverride: null,
                    composerOverride: null,
                    isrcOverride: null,
                    preGapOverride: explicitPreGap ? defaultPreGapFrames : null,
                    normalizeOverride: peakNormalize,
                    noCdText: noCdText,
                    audioDecoder: audioDecoder,
                    stderr: stderr);
            }
        }

        if (compilation.TrackCount == 0)
        {
            throw new CommandLineException("No audio tracks were loaded from --tracks / --tracks-file.");
        }

        if (noCdText)
        {
            compilation.IncludeCdText = false;
        }

        string? note = !compilation.IncludeCdText && !noCdText
            ? "Note: no titles or artists were found in the tracks' tags and none were given (--title, --artist), so the disc gets no CD-Text."
            : null;
        return (compilation, note);
    }

    private static void LoadTracksFromJsonManifest(
        string manifestPath,
        AudioCdCompilation compilation,
        string? cliArtist,
        string? cliTitle,
        int? cliPreGapFrames,
        bool cliNormalize,
        bool noCdText,
        IAudioDecoder? audioDecoder,
        TextWriter stderr)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Track manifest file not found: '{manifestPath}'.", manifestPath);
        }

        AudioTracksManifestDto? dto;
        try
        {
            string json = File.ReadAllText(manifestPath);
            dto = JsonSerializer.Deserialize<AudioTracksManifestDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            throw new CommandLineException($"Could not parse --tracks-file JSON '{manifestPath}': {ex.Message}");
        }

        if (dto == null || dto.Tracks == null || dto.Tracks.Count == 0)
        {
            throw new CommandLineException($"Track manifest '{manifestPath}' contains no tracks.");
        }

        if (string.IsNullOrEmpty(cliTitle) && !string.IsNullOrWhiteSpace(dto.AlbumTitle))
        {
            compilation.AlbumTitle = dto.AlbumTitle.Trim();
            if (!noCdText) compilation.IncludeCdText = true;
        }
        if (string.IsNullOrEmpty(cliArtist) && !string.IsNullOrWhiteSpace(dto.AlbumPerformer))
        {
            compilation.AlbumPerformer = dto.AlbumPerformer.Trim();
            if (!noCdText) compilation.IncludeCdText = true;
        }
        if (string.IsNullOrWhiteSpace(compilation.Songwriter) && !string.IsNullOrWhiteSpace(dto.Songwriter))
        {
            compilation.Songwriter = dto.Songwriter.Trim();
        }
        compilation.Composer ??= dto.Composer?.Trim();
        compilation.Arranger ??= dto.Arranger?.Trim();
        compilation.Message ??= dto.Message?.Trim();
        if (string.IsNullOrWhiteSpace(compilation.UpcEan) && !string.IsNullOrWhiteSpace(dto.UpcEan))
        {
            string upc = dto.UpcEan.Trim();
            if (upc.Length != 13 || !upc.All(char.IsAsciiDigit))
            {
                throw new CommandLineException("Manifest field 'upcEan' must be 13 ASCII decimal digits.");
            }
            compilation.UpcEan = upc;
        }
        if (!noCdText && dto.IncludeCdText.HasValue)
        {
            compilation.IncludeCdText = dto.IncludeCdText.Value;
        }

        string baseDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        foreach (var entry in dto.Tracks)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                throw new CommandLineException($"Each entry in '{manifestPath}' must specify a non-empty 'path'.");
            }

            string resolvedPath = Path.IsPathRooted(entry.Path)
                ? entry.Path
                : Path.GetFullPath(Path.Combine(baseDir, entry.Path));
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Track file referenced in '{manifestPath}' not found: '{resolvedPath}'.", resolvedPath);
            }

            if (entry.PreGapFrames.HasValue && (entry.PreGapFrames.Value < 0 || entry.PreGapFrames.Value > 4500))
            {
                throw new CommandLineException($"Track '{entry.Path}' preGapFrames must be between 0 and 4500.");
            }

            AddSingleAudioTrack(
                compilation,
                resolvedPath,
                titleOverride: entry.Title,
                performerOverride: entry.Performer,
                artistGlobalOverride: cliArtist,
                titleGlobalOverride: cliTitle,
                songwriterOverride: entry.Songwriter,
                composerOverride: entry.Composer,
                isrcOverride: entry.Isrc,
                preGapOverride: entry.PreGapFrames ?? cliPreGapFrames,
                normalizeOverride: entry.Normalize ?? cliNormalize,
                noCdText: noCdText,
                audioDecoder: audioDecoder,
                stderr: stderr);
        }
    }

    private static void AddSingleAudioTrack(
        AudioCdCompilation compilation,
        string trackPath,
        string? titleOverride,
        string? performerOverride,
        string? artistGlobalOverride,
        string? titleGlobalOverride,
        string? songwriterOverride,
        string? composerOverride,
        string? isrcOverride,
        int? preGapOverride,
        bool normalizeOverride,
        bool noCdText,
        IAudioDecoder? audioDecoder,
        TextWriter stderr)
    {
        string trackTitle = !string.IsNullOrWhiteSpace(titleOverride)
            ? titleOverride.Trim()
            : Path.GetFileNameWithoutExtension(trackPath);
        string trackPerformer = !string.IsNullOrWhiteSpace(performerOverride)
            ? performerOverride.Trim()
            : (artistGlobalOverride ?? compilation.AlbumPerformer);
        string? trackSongwriter = songwriterOverride;
        string? trackComposer = composerOverride;
        string? trackIsrc = isrcOverride;
        int durationFrames = 0;

        if (!string.IsNullOrWhiteSpace(titleOverride) || !string.IsNullOrWhiteSpace(performerOverride))
        {
            if (!noCdText) compilation.IncludeCdText = true;
        }

        try
        {
            var meta = ProbeAudioMetadata(trackPath, audioDecoder);
            if (meta != null)
            {
                if (string.IsNullOrWhiteSpace(titleOverride) && !string.IsNullOrWhiteSpace(meta.Title))
                {
                    trackTitle = meta.Title;
                    if (!noCdText) compilation.IncludeCdText = true;
                }
                if (string.IsNullOrWhiteSpace(performerOverride) && string.IsNullOrEmpty(artistGlobalOverride) && !string.IsNullOrWhiteSpace(meta.Artist))
                {
                    trackPerformer = meta.Artist;
                    if (!noCdText) compilation.IncludeCdText = true;
                }
                if (string.IsNullOrEmpty(titleGlobalOverride) && compilation.AlbumTitle == "Audio CD" && !string.IsNullOrWhiteSpace(meta.Album))
                {
                    compilation.AlbumTitle = meta.Album;
                    if (!noCdText) compilation.IncludeCdText = true;
                }
                if (string.IsNullOrEmpty(artistGlobalOverride) && compilation.AlbumPerformer == "Various Artists" && !string.IsNullOrWhiteSpace(meta.AlbumArtist ?? meta.Artist))
                {
                    compilation.AlbumPerformer = (meta.AlbumArtist ?? meta.Artist)!;
                }
                trackSongwriter ??= meta.Songwriter;
                trackComposer ??= meta.Composer;
                trackIsrc ??= meta.Isrc;
                durationFrames = meta.TotalFrames;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            stderr.WriteLine($"Warning: could not read tags from '{Path.GetFileName(trackPath)}' ({ex.Message}); its CD-Text title will be the file name.");
        }

        int nextTrackNum = compilation.TrackCount + 1;
        int preGap = preGapOverride ?? (nextTrackNum == 1 ? OpticalConstants.CdLeadInFrames : 0);
        if (nextTrackNum == 1 && preGapOverride == null)
        {
            preGap = OpticalConstants.CdLeadInFrames;
        }

        var item = new AudioCdTrackItem(
            TrackNumber: nextTrackNum,
            SourceFilePath: trackPath,
            Title: trackTitle,
            Performer: trackPerformer,
            Isrc: trackIsrc,
            PreGapFrames: preGap,
            NormalizeAudio: normalizeOverride)
        {
            Songwriter = trackSongwriter,
            Composer = trackComposer,
            DurationFrames = durationFrames
        };

        compilation.AddTrack(item);
    }

    private static AudioFormatMetadata? ProbeAudioMetadata(string trackPath, IAudioDecoder? audioDecoder)
    {
        if (audioDecoder != null)
        {
            return audioDecoder.ProbeAudioFile(trackPath);
        }

        return FlacFileCodec.IsFlacFile(trackPath)
            ? FlacFileCodec.ProbeAudioFile(trackPath)
            : WavFileCodec.ProbeAudioFile(trackPath);
    }

    private static int HandleJewelCover(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        string outputPath = Path.GetFullPath(cmd.GetRequiredOption("output", "--output <file.html>"));
        EnsureOutputCanBeWritten(outputPath, cmd.HasFlag("overwrite"));

        using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var (compilation, note) = BuildAudioCompilation(cmd, mfDecoder, stderr);
        if (note != null && !cmd.IsJson)
        {
            stderr.WriteLine(note);
        }

        JewelCaseCoverGenerator.SavePrintableCoverHtml(compilation, outputPath);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                output = outputPath,
                albumTitle = compilation.AlbumTitle,
                albumPerformer = compilation.AlbumPerformer,
                trackCount = compilation.TrackCount,
                totalDuration = compilation.TotalDurationMsf.ToString(),
                totalFrames = compilation.TotalFrames
            });
            return 0;
        }

        stdout.WriteLine($"Jewel case cover saved: {outputPath}");
        stdout.WriteLine($"  Album:  {compilation.AlbumTitle} - {compilation.AlbumPerformer}");
        stdout.WriteLine($"  Tracks: {compilation.TrackCount} ({compilation.TotalDurationMsf})");
        return 0;
    }

    private static int HandleAudioPlan(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr)
    {
        int targetMinutes = 80;
        string? mediaRaw = cmd.GetOption("media");
        if (!string.IsNullOrWhiteSpace(mediaRaw))
        {
            targetMinutes = mediaRaw.Trim() switch
            {
                "74" => 74,
                "80" => 80,
                _ => throw new CommandLineException($"Invalid --media '{mediaRaw}'. Use 74 or 80 minutes.")
            };
        }

        using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var (compilation, note) = BuildAudioCompilation(cmd, mfDecoder, stderr);
        if (note != null && !cmd.IsJson)
        {
            stderr.WriteLine(note);
        }

        return WriteAudioPlanOutput(compilation, targetMinutes, cmd.IsJson, stdout);
    }

    internal static int WriteAudioPlanOutput(AudioCdCompilation compilation, int targetMinutes, bool isJson, TextWriter stdout)
    {
        bool fitsSelected = targetMinutes == 74 ? compilation.FitsOn74MinMedia : compilation.FitsOn80MinMedia;
        int capacityFrames = targetMinutes == 74 ? AudioCdCompilation.Capacity74MinFrames : AudioCdCompilation.Capacity80MinFrames;
        compilation.ValidateCapacity(capacityFrames, out string? capacityWarning);

        if (isJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                planOnly = true,
                albumTitle = compilation.AlbumTitle,
                albumPerformer = compilation.AlbumPerformer,
                recordingMode = compilation.RecordingMode.ToString(),
                includeCdText = compilation.IncludeCdText,
                peakNormalize = compilation.NormalizeAllTracks,
                rmsLevel = compilation.UseRmsLoudnessLeveling,
                loudnessMode = compilation.LoudnessNormalization.ToString(),
                crossfadeSeconds = compilation.CrossfadeSeconds,
                defaultPreGapFrames = compilation.DefaultPreGapFrames,
                upcEan = compilation.UpcEan,
                trackCount = compilation.TrackCount,
                totalFrames = compilation.TotalFrames,
                totalDuration = compilation.TotalDurationMsf.ToString(),
                totalDurationSeconds = Math.Round(compilation.TotalDuration.TotalSeconds, 3),
                fits74Min = compilation.FitsOn74MinMedia,
                fits80Min = compilation.FitsOn80MinMedia,
                targetMediaMinutes = targetMinutes,
                fitsSelectedMedia = fitsSelected,
                capacityWarning,
                tracks = compilation.Tracks.Select(t => new
                {
                    number = t.TrackNumber,
                    path = t.SourceFilePath,
                    title = t.Title,
                    performer = t.Performer,
                    isrc = t.Isrc,
                    durationFrames = t.DurationFrames,
                    duration = t.DurationMsf.ToString(),
                    preGapFrames = t.PreGapFrames,
                    normalize = t.NormalizeAudio || compilation.NormalizeAllTracks
                })
            });
            return 0;
        }

        stdout.WriteLine("Audio CD Plan:");
        stdout.WriteLine($"  Album:       {compilation.AlbumTitle} - {compilation.AlbumPerformer}");
        stdout.WriteLine($"  Mode:        {compilation.RecordingMode} (CD-Text: {(compilation.IncludeCdText ? "On" : "Off")})");
        stdout.WriteLine($"  Processing:  Peak={compilation.NormalizeAllTracks}, RMS={compilation.UseRmsLoudnessLeveling}, Loudness={compilation.LoudnessNormalization}, Crossfade={compilation.CrossfadeSeconds:0.##}s");
        stdout.WriteLine($"  Tracks:      {compilation.TrackCount}");
        stdout.WriteLine($"  Duration:    {compilation.TotalDurationMsf} ({compilation.TotalFrames:N0} frames)");
        stdout.WriteLine($"  Fits 74/80m: {(compilation.FitsOn74MinMedia ? "Yes" : "No")} / {(compilation.FitsOn80MinMedia ? "Yes" : "No")}");
        foreach (var trk in compilation.Tracks)
        {
            stdout.WriteLine($"    {trk.TrackNumber,2}. {trk.Title} ({trk.Performer}) [{trk.DurationMsf}, pre-gap {trk.PreGapFrames}f]");
        }
        return 0;
    }

    private static void EnsureOutputCanBeWritten(string outputPath, bool overwrite)
    {
        if (File.Exists(outputPath) && !overwrite)
        {
            throw new CommandLineException(
                $"Output file '{outputPath}' already exists. Pass --overwrite to replace it.");
        }
    }

    private sealed class AudioTracksManifestDto
    {
        public string? AlbumTitle { get; set; }
        public string? AlbumPerformer { get; set; }
        public string? Songwriter { get; set; }
        public string? Composer { get; set; }
        public string? Arranger { get; set; }
        public string? Message { get; set; }
        public string? UpcEan { get; set; }
        public bool? IncludeCdText { get; set; }
        public List<AudioTrackManifestEntryDto>? Tracks { get; set; }
    }

    private sealed class AudioTrackManifestEntryDto
    {
        public string? Path { get; set; }
        public string? Title { get; set; }
        public string? Performer { get; set; }
        public string? Songwriter { get; set; }
        public string? Composer { get; set; }
        public string? Isrc { get; set; }
        public int? PreGapFrames { get; set; }
        public bool? Normalize { get; set; }
    }
}
