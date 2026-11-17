using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.App.Mvvm;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Hardware.Audio;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Format choices shared by the ripper and the convert screen: the MP3 quality list and the translation of the
/// checked formats, MP3 quality and bitrate box into output targets.
/// </summary>
internal static partial class AudioTargetChoices
{
    public const string DefaultMp3Quality = "320 kbit/s";

    public static IReadOnlyList<string> Mp3Qualities { get; } =
    [
        "320 kbit/s",
        "256 kbit/s",
        "192 kbit/s",
        "128 kbit/s",
        "VBR V0 (about 245 kbit/s)",
        "VBR V2 (about 190 kbit/s)"
    ];

    [GeneratedRegex(@"^(?<kbps>\d+) kbit/s$")]
    private static partial Regex ConstantBitrate();

    [GeneratedRegex(@"^VBR V(?<q>\d)\b")]
    private static partial Regex VariableBitrate();

    /// <summary>The MP3 target for one entry of <see cref="Mp3Qualities"/>; null for any other text.</summary>
    public static AudioExportTarget? Mp3Target(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
        {
            return null;
        }

        var constant = ConstantBitrate().Match(choice);
        if (constant.Success)
        {
            return new AudioExportTarget(AudioRipFormat.Mp3, int.Parse(constant.Groups["kbps"].Value, CultureInfo.InvariantCulture));
        }

        var variable = VariableBitrate().Match(choice);
        return variable.Success
            ? new AudioExportTarget(AudioRipFormat.Mp3, 0, int.Parse(variable.Groups["q"].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// Fills <paramref name="targets"/> from the checked formats. The bitrate text applies to Opus and AAC (empty = the
    /// format's default); MP3 uses <paramref name="mp3Quality"/>. Returns a plain-language problem, or null.
    /// </summary>
    public static string? BuildTargets(IReadOnlyList<AudioRipFormat> formats, string mp3Quality, string? bitrateText, List<AudioExportTarget> targets)
    {
        targets.Clear();
        int bitrate = 0;
        bool usesBitrate = formats.Contains(AudioRipFormat.Opus) || formats.Contains(AudioRipFormat.Aac);
        if (usesBitrate && !string.IsNullOrWhiteSpace(bitrateText) &&
            (!int.TryParse(bitrateText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0))
        {
            return $"Invalid bitrate '{bitrateText}'. Enter kbit/s as a whole number or leave it empty.";
        }

        foreach (var format in formats)
        {
            switch (format)
            {
                case AudioRipFormat.Mp3:
                    var mp3 = Mp3Target(mp3Quality);
                    if (mp3 == null)
                    {
                        return $"Unknown MP3 quality '{mp3Quality}'.";
                    }

                    targets.Add(mp3);
                    break;
                case AudioRipFormat.Opus:
                case AudioRipFormat.Aac:
                    targets.Add(new AudioExportTarget(format, bitrate));
                    break;
                default:
                    targets.Add(new AudioExportTarget(format));
                    break;
            }
        }

        return null;
    }
}

/// <summary>
/// The "Convert Audio" tab: converts existing audio files (WAV, FLAC, MP3, AAC, Ogg, Opus, WMA, AIFF) to FLAC, MP3,
/// Opus or AAC with ffmpeg, the same work as the <c>convert-audio</c> command. The original files are never changed.
/// </summary>
public sealed class AudioConvertViewModel : ObservableObject
{
    private readonly AppSettingsService? _settingsService;
    private bool _toFlac;
    private bool _toMp3;
    private bool _toOpus;
    private bool _toAac;
    private string _selectedMp3Quality = AudioTargetChoices.DefaultMp3Quality;
    private string _bitrateKbps = string.Empty;
    private string _outputFolder;
    private string _namingTemplate = string.Empty;
    private bool _overwrite;
    private bool _recursive = true;
    private bool _isBusy;
    private string _statusMessage = "Add files or a folder, choose the formats and an output folder.";
    private string _report = string.Empty;
    private string? _selectedInput;
    private CancellationTokenSource? _cts;

    public AudioConvertViewModel(AppSettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        string saved = settingsService?.CurrentSettings.Mp3Quality ?? string.Empty;
        if (AudioTargetChoices.Mp3Qualities.Contains(saved))
        {
            _selectedMp3Quality = saved;
        }

        try
        {
            _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "CdBurner_ConvertedAudio");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
        {
            _outputFolder = Path.Combine(Path.GetTempPath(), "CdBurner_ConvertedAudio");
        }

        AddFilesCommand = new RelayCommand(_ => BrowseForFiles(), _ => !IsBusy);
        AddFolderCommand = new RelayCommand(_ => BrowseForFolder(), _ => !IsBusy);
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected(), _ => !IsBusy && SelectedInput != null);
        ClearCommand = new RelayCommand(_ => Inputs.Clear(), _ => !IsBusy && Inputs.Count > 0);
        BrowseOutputFolderCommand = new RelayCommand(_ => BrowseForOutputFolder(), _ => !IsBusy);
        ConvertCommand = new AsyncRelayCommand(_ =>
        {
            ConvertTask = ConvertAsync();
            return ConvertTask;
        }, _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        Inputs.CollectionChanged += (_, _) => ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Files and folders to convert.</summary>
    public ObservableCollection<string> Inputs { get; } = new();

    /// <summary>The entry highlighted in the list; <see cref="RemoveSelectedCommand"/> removes it.</summary>
    public string? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (SetProperty(ref _selectedInput, value))
            {
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether folders in <see cref="Inputs"/> are searched including their subfolders.</summary>
    public bool Recursive
    {
        get => _recursive;
        set => SetProperty(ref _recursive, value);
    }

    /// <summary>Write FLAC files.</summary>
    public bool ConvertToFlac
    {
        get => _toFlac;
        set => SetProperty(ref _toFlac, value);
    }

    /// <summary>Write MP3 files.</summary>
    public bool ConvertToMp3
    {
        get => _toMp3;
        set
        {
            if (SetProperty(ref _toMp3, value))
            {
                OnPropertyChanged(nameof(Mp3Selected));
            }
        }
    }

    /// <summary>Write Opus files.</summary>
    public bool ConvertToOpus
    {
        get => _toOpus;
        set
        {
            if (SetProperty(ref _toOpus, value))
            {
                OnPropertyChanged(nameof(BitrateApplies));
            }
        }
    }

    /// <summary>Write AAC files.</summary>
    public bool ConvertToAac
    {
        get => _toAac;
        set
        {
            if (SetProperty(ref _toAac, value))
            {
                OnPropertyChanged(nameof(BitrateApplies));
            }
        }
    }

    /// <summary>True when MP3 is checked, so the quality box applies.</summary>
    public bool Mp3Selected => _toMp3;

    /// <summary>True when Opus or AAC is checked, so the bitrate box applies.</summary>
    public bool BitrateApplies => _toOpus || _toAac;

    /// <summary>MP3 qualities offered (same list as the ripper).</summary>
    public IReadOnlyList<string> Mp3QualityChoices => AudioTargetChoices.Mp3Qualities;

    /// <summary>The chosen MP3 quality. Unknown text is ignored.</summary>
    public string SelectedMp3Quality
    {
        get => _selectedMp3Quality;
        set
        {
            if (value != null && AudioTargetChoices.Mp3Qualities.Contains(value))
            {
                SetProperty(ref _selectedMp3Quality, value);
            }
        }
    }

    /// <summary>Bitrate in kbit/s for Opus and AAC; empty uses each format's default.</summary>
    public string BitrateKbps
    {
        get => _bitrateKbps;
        set => SetProperty(ref _bitrateKbps, value ?? string.Empty);
    }

    /// <summary>Folder that receives the converted files.</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set => SetProperty(ref _outputFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Optional naming template read from each file's tags. Empty keeps each file's folder structure under the folder
    /// it was added from.
    /// </summary>
    public string NamingTemplate
    {
        get => _namingTemplate;
        set
        {
            if (SetProperty(ref _namingTemplate, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(NamingProblem));
            }
        }
    }

    /// <summary>What is wrong with <see cref="NamingTemplate"/>, or null when it is empty or usable.</summary>
    public string? NamingProblem => string.IsNullOrWhiteSpace(_namingTemplate) ? null : AudioNamingTemplate.Validate(_namingTemplate);

    /// <summary>Replace files that already exist in the output folder (they are skipped otherwise).</summary>
    public bool Overwrite
    {
        get => _overwrite;
        set => SetProperty(ref _overwrite, value);
    }

    /// <summary>True while a conversion runs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddFilesCommand.RaiseCanExecuteChanged();
                AddFolderCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
                ClearCommand.RaiseCanExecuteChanged();
                BrowseOutputFolderCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>One-line state or progress.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Result of the last conversion: one line per file and the warnings.</summary>
    public string Report
    {
        get => _report;
        private set => SetProperty(ref _report, value);
    }

    /// <summary>The most recent conversion, so tests can await it.</summary>
    public Task? ConvertTask { get; private set; }

    public RelayCommand AddFilesCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand BrowseOutputFolderCommand { get; }

    public AsyncRelayCommand ConvertCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Adds files or folders to <see cref="Inputs"/>, skipping blanks and entries already listed.</summary>
    public void AddInputs(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !Inputs.Contains(path.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                Inputs.Add(path.Trim());
            }
        }
    }

    private void RemoveSelected()
    {
        if (SelectedInput != null)
        {
            Inputs.Remove(SelectedInput);
        }
    }

    private void BrowseForFiles()
    {
        string patterns = string.Join(";", AudioFileConverter.SupportedInputExtensions.Select(e => "*" + e));
        var dlg = new OpenFileDialog
        {
            Title = "Select Audio Files to Convert",
            Filter = $"Audio files ({patterns})|{patterns}|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            AddInputs(dlg.FileNames);
        }
    }

    private void BrowseForFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select a Folder of Audio Files to Convert" };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            AddInputs([dlg.FolderName]);
        }
    }

    private void BrowseForOutputFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Output Folder for Converted Files",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            OutputFolder = dlg.FolderName;
        }
    }

    private List<AudioRipFormat> CheckedFormats()
    {
        var formats = new List<AudioRipFormat>();
        if (_toFlac) formats.Add(AudioRipFormat.Flac);
        if (_toMp3) formats.Add(AudioRipFormat.Mp3);
        if (_toOpus) formats.Add(AudioRipFormat.Opus);
        if (_toAac) formats.Add(AudioRipFormat.Aac);
        return formats;
    }

    private void Reject(string problem)
    {
        StatusMessage = problem;
        Report = "Conversion not started: " + problem;
    }

    /// <summary>
    /// Converts <see cref="Inputs"/> to the checked formats. Everything is checked before the first file is touched;
    /// a file that fails is reported and the next one is converted.
    /// </summary>
    public async Task ConvertAsync()
    {
        if (IsBusy)
        {
            return;
        }

        Report = string.Empty;
        var formats = CheckedFormats();
        if (formats.Count == 0)
        {
            Reject("Choose at least one output format.");
            return;
        }

        if (Inputs.Count == 0)
        {
            Reject("Add files or a folder to convert.");
            return;
        }

        var targets = new List<AudioExportTarget>();
        string? problem = AudioTargetChoices.BuildTargets(formats, SelectedMp3Quality, BitrateKbps, targets);
        if (problem != null)
        {
            Reject(problem);
            return;
        }

        string? naming = string.IsNullOrWhiteSpace(NamingTemplate) ? null : NamingTemplate;
        string? customFfmpeg = _settingsService?.CurrentSettings.FfmpegExecutablePath;
        string? ffmpeg = FfmpegLocator.FindFfmpegExecutable(string.IsNullOrWhiteSpace(customFfmpeg) ? null : customFfmpeg);
        if (ffmpeg == null)
        {
            Reject(AudioRipFormats.MissingFfmpegMessage(targets[0].Format));
            return;
        }

        var options = new AudioConvertOptions(targets, OutputFolder, naming, ffmpeg, Overwrite);
        problem = AudioFileConverter.CheckPrerequisites(options);
        if (problem != null)
        {
            Reject(problem);
            return;
        }

        var paths = Inputs.ToList();
        bool recursive = Recursive;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        StatusMessage = "Looking for audio files...";
        var progress = new ClosableProgress<string>(message =>
        {
            if (_isBusy) StatusMessage = message;
        });

        try
        {
            var inputs = await Task.Run(() => AudioFileConverter.CollectInputs(paths, recursive), token);
            if (inputs.Count == 0)
            {
                Reject("No audio files found (supported: " + string.Join(" ", AudioFileConverter.SupportedInputExtensions) +
                       "). Turn on Include subfolders to search inside folders.");
                return;
            }

            StatusMessage = $"Converting {inputs.Count} file(s)...";
            var result = await AudioFileConverter.ConvertAsync(inputs, options, progress, token);
            progress.Close(); // a late progress report must not overwrite the final status line
            Report = BuildReport(result);
            int failed = result.Files.Count(f => f.Error != null);
            int skipped = result.Files.Count(f => f.Skipped);
            StatusMessage = $"Converted {result.Converted} file(s); {skipped} skipped, {failed} failed.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Conversion cancelled. Files finished before the cancel are kept.";
            Report = "Conversion cancelled by user.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Conversion failed: {ex.Message}";
            Report = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            progress.Close();
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private static string BuildReport(AudioConvertResult result)
    {
        int failed = result.Files.Count(f => f.Error != null);
        int skipped = result.Files.Count(f => f.Skipped);
        var sb = new StringBuilder();
        sb.AppendLine($"Converted {result.Converted} file(s); {skipped} skipped, {failed} failed.");
        foreach (var file in result.Files)
        {
            string label = AudioRipFormats.DisplayName(file.Format);
            if (file.Error != null)
            {
                sb.AppendLine($"  failed [{label}] {file.Input}: {file.Error}");
            }
            else if (file.Skipped)
            {
                sb.AppendLine($"  skipped [{label}] {file.Input}: {file.Output} already exists (turn on Replace existing files to overwrite it)");
            }
            else
            {
                sb.AppendLine($"  OK [{label}] {file.Input} -> {file.Output}");
            }
        }

        foreach (string warning in result.Warnings)
        {
            sb.AppendLine($"  Warning: {warning}");
        }

        return sb.ToString().TrimEnd();
    }
}
