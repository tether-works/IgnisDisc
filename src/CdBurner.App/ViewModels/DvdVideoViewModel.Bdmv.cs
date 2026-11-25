using System.Collections.ObjectModel;
using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.Core.BluRayVideo;
using CdBurner.Core.Subtitles;

namespace CdBurner.App.ViewModels;

/// <summary>A value paired with the label shown in a ComboBox of the Blu-ray Video mode.</summary>
public sealed record BdmvChoice<T>(T Value, string Label);

/// <summary>
/// The "Blu-ray Video (BDMV)" mode of the DVD-Video screen: the same movie list is encoded as H.264 and AC-3 or LPCM,
/// muxed to a BDMV (or AVCHD) tree and packed on a UDF 2.50 image. Nothing is burned from this mode yet; the image is
/// the result.
/// </summary>
public partial class DvdVideoViewModel
{
    /// <summary>Name of the DVD-Video mode.</summary>
    public const string DvdVideoModeName = "DVD-Video";

    /// <summary>Name of the Blu-ray Video mode.</summary>
    public const string BluRayModeName = "Blu-ray Video (BDMV)";

    /// <summary>Said wherever the mode reports a result: what was checked, and what was not.</summary>
    public const string BdmvVerificationNote =
        "Checked against the Blu-ray file-format rules only. This has not been played on a Blu-ray player, so player compatibility is not claimed.";

    private string _selectedAuthoringMode = DvdVideoModeName;
    private BdmvDiscTarget _bdmvTarget = BdmvDiscTarget.BluRay;
    private BdmvVideoFormat _bdmvVideoFormat = BdmvVideoFormat.Hd1080p;
    private BdmvAudioCodec _bdmvAudioCodec = BdmvAudioCodec.Ac3;
    private int _bdmvVideoBitrateKbps = 15000;

    /// <summary>The modes of this screen.</summary>
    public IReadOnlyList<string> AuthoringModes { get; } = [DvdVideoModeName, BluRayModeName];

    /// <summary>The chosen mode: <see cref="DvdVideoModeName"/> or <see cref="BluRayModeName"/>.</summary>
    public string SelectedAuthoringMode
    {
        get => _selectedAuthoringMode;
        set
        {
            string mode = value == BluRayModeName ? BluRayModeName : DvdVideoModeName;
            if (SetProperty(ref _selectedAuthoringMode, mode))
            {
                OnPropertyChanged(nameof(IsBluRayMode));
                OnPropertyChanged(nameof(IsDvdMode));
                OnPropertyChanged(nameof(BdmvSourceSummary));
                BuildBdmvImageCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True while the Blu-ray Video mode is chosen.</summary>
    public bool IsBluRayMode => _selectedAuthoringMode == BluRayModeName;

    /// <summary>True while the DVD-Video mode is chosen (the DVD options are shown only then).</summary>
    public bool IsDvdMode => !IsBluRayMode;

    /// <summary>Disc types the mode can author for.</summary>
    public IReadOnlyList<BdmvChoice<BdmvDiscTarget>> AvailableBdmvTargets { get; } =
    [
        new(BdmvDiscTarget.BluRay, "Blu-ray disc (BD-R / BD-RE)"),
        new(BdmvDiscTarget.Avchd, "AVCHD on DVD (DVD-R / DVD+R DL)"),
    ];

    /// <summary>Picture formats.</summary>
    public IReadOnlyList<BdmvChoice<BdmvVideoFormat>> AvailableBdmvVideoFormats { get; } =
    [
        new(BdmvVideoFormat.Hd1080p, "1080p 23.976"),
        new(BdmvVideoFormat.Hd1080i, "1080i 25"),
        new(BdmvVideoFormat.Hd720p, "720p 23.976"),
    ];

    /// <summary>Audio encodings.</summary>
    public IReadOnlyList<BdmvChoice<BdmvAudioCodec>> AvailableBdmvAudioCodecs { get; } =
    [
        new(BdmvAudioCodec.Ac3, "Dolby Digital (AC-3)"),
        new(BdmvAudioCodec.Lpcm, "LPCM stereo"),
    ];

    /// <summary>Disc type of the tree to author.</summary>
    public BdmvDiscTarget BdmvTarget
    {
        get => _bdmvTarget;
        set
        {
            if (SetProperty(ref _bdmvTarget, value) && value == BdmvDiscTarget.Avchd && _bdmvVideoFormat == BdmvVideoFormat.Hd1080p)
            {
                // AVCHD on DVD is normally interlaced HD; the user can still choose another format.
                BdmvVideoFormat = BdmvVideoFormat.Hd1080i;
            }
        }
    }

    /// <summary>Picture format.</summary>
    public BdmvVideoFormat BdmvVideoFormat
    {
        get => _bdmvVideoFormat;
        set => SetProperty(ref _bdmvVideoFormat, value);
    }

    /// <summary>Audio encoding.</summary>
    public BdmvAudioCodec BdmvAudioCodec
    {
        get => _bdmvAudioCodec;
        set => SetProperty(ref _bdmvAudioCodec, value);
    }

    /// <summary>Average video bitrate in kbit/s.</summary>
    public int BdmvVideoBitrateKbps
    {
        get => _bdmvVideoBitrateKbps;
        set => SetProperty(ref _bdmvVideoBitrateKbps, Math.Clamp(value, 1000, 35000));
    }

    /// <summary>Subtitle files (.srt) turned into PGS tracks, in order.</summary>
    public ObservableCollection<string> BdmvSubtitleFiles { get; } = new();

    /// <summary>One line about the movies chosen for the Blu-ray Video mode.</summary>
    public string BdmvSourceSummary => SourceMediaFiles.Count == 0
        ? "No movies chosen yet. Add movie files; each becomes one title."
        : $"{SourceMediaFiles.Count} movie(s) will become {SourceMediaFiles.Count} title(s).";

    /// <summary>Replaces FFmpeg in tests.</summary>
    public IExternalToolRunner? BdmvToolRunner { get; set; }

    /// <summary>Replaces speech recognition in tests.</summary>
    public CdBurner.Core.DvdVideo.DvdSubtitleSourceResolver.SubtitleGenerator? BdmvSubtitleGenerator { get; set; }

    /// <summary>Asks for a .srt file and adds it as a subtitle track.</summary>
    public RelayCommand AddBdmvSubtitleCommand { get; private set; } = null!;

    /// <summary>Removes the subtitle track given as parameter.</summary>
    public RelayCommand RemoveBdmvSubtitleCommand { get; private set; } = null!;

    /// <summary>Authors the movies and builds the UDF 2.50 image (asks for the file name when no path is given).</summary>
    public AsyncRelayCommand BuildBdmvImageCommand { get; private set; } = null!;

    private void InitializeBdmvMode()
    {
        SourceMediaFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BdmvSourceSummary));
            BuildBdmvImageCommand?.RaiseCanExecuteChanged();
        };
        BdmvSubtitleFiles.CollectionChanged += (_, _) => RemoveBdmvSubtitleCommand?.RaiseCanExecuteChanged();
        AddBdmvSubtitleCommand = new RelayCommand(_ => PickBdmvSubtitle(), _ => !IsBusy);
        RemoveBdmvSubtitleCommand = new RelayCommand(p => { if (p is string s) BdmvSubtitleFiles.Remove(s); }, _ => !IsBusy);
        BuildBdmvImageCommand = new AsyncRelayCommand(async param =>
        {
            string? path = param as string;
            if (string.IsNullOrWhiteSpace(path) && System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Build Blu-ray Video Image",
                    Filter = "UDF 2.50 disc image (*.iso)|*.iso|All Files (*.*)|*.*",
                    FileName = $"{(string.IsNullOrWhiteSpace(VolumeLabel) ? "BDMV_DISC" : VolumeLabel)}.iso"
                };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    return;
                }
                path = dlg.FileName;
            }
            await BuildBdmvImageAsync(path);
        }, _ => IsBluRayMode && !IsBusy && SourceMediaFiles.Count > 0);
    }

    private void PickBdmvSubtitle()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a subtitle file for the Blu-ray Video",
            Filter = "SubRip subtitles (*.srt)|*.srt|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            AddBdmvSubtitleFile(dlg.FileName);
        }
    }

    /// <summary>Adds a .srt file as a subtitle track (up to <see cref="BdmvAuthoringService.MaxSubtitleTracks"/>); false when it is not usable.</summary>
    public bool AddBdmvSubtitleFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage = "The subtitle file was not found.";
            return false;
        }
        string full = Path.GetFullPath(path);
        if (BdmvSubtitleFiles.Count >= BdmvAuthoringService.MaxSubtitleTracks)
        {
            StatusMessage = $"At most {BdmvAuthoringService.MaxSubtitleTracks} subtitle tracks fit a Blu-ray Video title.";
            return false;
        }
        if (!BdmvSubtitleFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            BdmvSubtitleFiles.Add(full);
        }
        return true;
    }

    /// <summary>The authoring settings the screen currently stands for.</summary>
    public BdmvAuthoringOptions CreateBdmvOptions()
    {
        var options = new BdmvAuthoringOptions
        {
            Target = BdmvTarget,
            VolumeLabel = string.IsNullOrWhiteSpace(VolumeLabel) ? "BDMV_DISC" : VolumeLabel.Trim(),
            VideoFormat = BdmvVideoFormat,
            FrameRate = BdmvVideoFormat == BdmvVideoFormat.Hd1080i ? BdmvFrameRate.Fps25 : BdmvFrameRate.Fps23976,
            AudioCodec = BdmvAudioCodec,
            AudioChannels = 2,
            VideoBitrateKbps = BdmvVideoBitrateKbps,
            MaxVideoBitrateKbps = Math.Max(BdmvVideoBitrateKbps, 25000),
            ChapterIntervalSeconds = ChapterIntervalMinutes * 60,
            CustomFfmpegPath = string.IsNullOrWhiteSpace(CustomFfmpegPath) ? null : CustomFfmpegPath,
        };
        if (options.VolumeLabel.Length > 32)
        {
            options.VolumeLabel = options.VolumeLabel[..32];
        }
        foreach (string srt in BdmvSubtitleFiles)
        {
            options.Subtitles.Add(new BdmvSubtitleSpec(srt, "eng"));
        }
        if (GenerateSubtitles && options.Subtitles.Count < BdmvAuthoringService.MaxSubtitleTracks)
        {
            string language = SubtitleLanguage?.Length == 3 ? SubtitleLanguage.ToLowerInvariant() : "eng";
            options.Subtitles.Add(new BdmvSubtitleSpec(null, language, GenerateWithWhisper: true));
        }
        return options;
    }

    /// <summary>
    /// Authors the chosen movies as a Blu-ray Video (or AVCHD) tree in a temporary folder and packs it on a UDF 2.50
    /// image at <paramref name="outputPath"/>. Returns the image path, or an empty string when it failed (the status
    /// line says why).
    /// </summary>
    public async Task<string> BuildBdmvImageAsync(string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (SourceMediaFiles.Count == 0)
        {
            StatusMessage = "Add at least one movie file first.";
            return string.Empty;
        }
        var options = CreateBdmvOptions();
        string? problem = BdmvAuthoringService.ValidateOptions(options);
        if (problem != null)
        {
            StatusMessage = problem;
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetTempPath(), $"{options.VolumeLabel}_{DateTime.Now:yyyyMMddHHmmss}.iso");
        }
        outputPath = Path.GetFullPath(outputPath);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;
        IsBusy = true;
        BurnProgress = 0;
        string staging = Path.Combine(Path.GetTempPath(), "IgnisDisc_BdmvUi_" + Guid.NewGuid().ToString("N"));
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var service = new BdmvAuthoringService
            {
                ToolRunner = BdmvToolRunner ?? new ProcessToolRunner(),
                SubtitleGenerator = BdmvSubtitleGenerator,
            };
            var progress = new Progress<(double Percent, string Status)>(p =>
            {
                BurnProgress = p.Percent * 0.9;
                StatusMessage = p.Status;
            });
            var result = await service.AuthorAsync(SourceMediaFiles.ToList(), staging, options, progress, cts.Token);
            if (!result.Success)
            {
                StatusMessage = $"Blu-ray Video authoring failed: {result.ErrorMessage}";
                return string.Empty;
            }

            StatusMessage = "Packing the Blu-ray Video files on a UDF 2.50 image...";
            await BdmvAuthoringService.BuildImageAsync(staging, outputPath, options, null, cts.Token);
            BurnProgress = 100;
            LastBuiltIsoPath = outputPath;
            StatusMessage = $"Blu-ray Video image built: {Path.GetFileName(outputPath)}. {BdmvVerificationNote}";
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Blu-ray Video image build cancelled.";
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            StatusMessage = $"Error building the Blu-ray Video image: {ex.Message}";
            return string.Empty;
        }
        finally
        {
            _activeCts = null;
            IsBusy = false;
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
