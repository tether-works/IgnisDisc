// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Presets;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.App.ViewModels;

/// <summary>One entry of the disc picker: a built-in or user preset, or a saved calibration.</summary>
public sealed record DiscArtDiscChoice(string Text, CalibrationParameters Parameters, string Source)
{
    public override string ToString() => Text;
}

/// <summary>
/// Disc Art screen: one PNG picture placed on a disc (scale, move, rotate, fit to the drawn ring), tone, the disc
/// (preset or saved calibration), optional files on the same DVD or BD, an art band, a placement preview, the image
/// generation, a read-back preview of the result and the burn. The request is built exactly as
/// <c>ignisdisc-cli disc-art generate</c> builds it, so the same choices give the same image bytes.
/// </summary>
public sealed class DiscArtViewModel : ObservableObject
{
    /// <summary>Width and height of the placement preview in pixels; it shows a whole 120 mm disc.</summary>
    public const int PreviewPixels = 360;

    /// <summary>Radius the placement preview shows: the physical edge of a 12 cm disc.</summary>
    public const double PreviewDiscRadiusMm = 60;

    /// <summary>Radius of the centre hole (15 mm hole).</summary>
    public const double HoleRadiusMm = 7.5;

    /// <summary>Read-back preview size in pixels.</summary>
    public const int ReadbackPixels = 512;

    // Fixed colours of the placement preview (not themed: they describe the disc, not the window).
    public static readonly uint BlankDiscColor = 0xFF8A8A85;   // disc surface the picture does not reach
    public static readonly uint OutsideColor = 0x00000000;     // around the disc and in the centre hole

    private readonly Func<string, Task<BurnOperationResult>>? _burnImage;
    private readonly IDiscDeviceService? _deviceService;
    private readonly IBurnEngineBackend? _burnEngine;
    private readonly Func<IReadOnlyList<IDiscRecorderBackend>>? _getAvailableRecorders;
    private readonly Func<IDiscRecorderBackend?>? _getSelectedRecorder;
    private readonly Action<IDiscRecorderBackend>? _setSelectedRecorder;
    private readonly Func<WriteSpeedDescriptor?>? _getWriteSpeed;
    private readonly Func<bool>? _getDisableBurnFree;
    private readonly Func<PostBurnAction>? _getPostBurnAction;
    private readonly string? _userPresetsPath;
    private readonly string _calibrationStorePath;
    private DiscPresetJsonDocument? _presets;

    private DiscArtMedium _medium = DiscArtMedium.Dvd;
    private DiscArtDiscChoice? _selectedDisc;
    private string _picturePath = string.Empty;
    private BgraRaster? _picture;
    private double _scale = 1;
    private double _offsetXMm;
    private double _offsetYMm;
    private double _rotationDegrees;
    private bool _fitToRing;
    private bool _preGapSetFitToRing;
    private double _ditherStrength = ToneProfile.Photo.DitherStrength;
    private double _brightness = ToneProfile.Photo.Brightness;
    private bool _fourLevels;
    private bool _srgbLinearization;
    private string _filesFolder = string.Empty;
    private bool _filesOuter;
    private string _artInnerRadiusText = string.Empty;
    private string _artOuterRadiusText = string.Empty;
    private string _volumeLabel = "DISC_ART";
    private string _titleRingText = string.Empty;
    private string _titleRingDateText = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string _titleRingHeightText = string.Empty;
    private string _baseCuePath = string.Empty;
    private bool _hidePictureInPreGap;
    private string _preGapSecondsText = DiscArtImageBuilder.CdPreGapDefaultSeconds.ToString(CultureInfo.InvariantCulture);
    private string _outputPath = string.Empty;
    private string _planSummary = string.Empty;
    private string _statusMessage = "Open a PNG picture, choose the disc and place the picture.";
    private byte[]? _placementPreviewPng;
    private byte[]? _readbackPreviewPng;
    private DiscArtResult? _lastResult;
    private DiscArtPlacement? _placement;
    private bool _placementValid;
    private int _placementGeneration;
    private bool _isBusy;
    private double _progress;
    private CancellationTokenSource? _cts;

    /// <param name="burnImage">Burns a finished .iso (the app hands it to the Disc Image screen); null hides single-session burning.</param>
    /// <param name="userPresetsPath">Presets file merged over the built-in ones (null: the default file).</param>
    /// <param name="calibrationStorePath">Saved calibrations (null: the default file).</param>
    /// <param name="deviceService">Optical device service for two-session outer-files burning.</param>
    /// <param name="burnEngine">Fallback burn engine backend for two-session outer-files burning.</param>
    public DiscArtViewModel(Func<string, Task<BurnOperationResult>>? burnImage = null,
        string? userPresetsPath = null, string? calibrationStorePath = null,
        IDiscDeviceService? deviceService = null, IBurnEngineBackend? burnEngine = null,
        Func<IReadOnlyList<IDiscRecorderBackend>>? getAvailableRecorders = null,
        Func<IDiscRecorderBackend?>? getSelectedRecorder = null,
        Action<IDiscRecorderBackend>? setSelectedRecorder = null,
        Func<WriteSpeedDescriptor?>? getWriteSpeed = null,
        Func<bool>? getDisableBurnFree = null,
        Func<PostBurnAction>? getPostBurnAction = null,
        string? directBurnLogDirectory = null)
    {
        _burnImage = burnImage;
        _userPresetsPath = userPresetsPath;
        _calibrationStorePath = calibrationStorePath ?? CalibrationStore.GetDefaultPath();
        _deviceService = deviceService;
        _burnEngine = burnEngine;
        _getAvailableRecorders = getAvailableRecorders;
        _getSelectedRecorder = getSelectedRecorder;
        _setSelectedRecorder = setSelectedRecorder;
        _getWriteSpeed = getWriteSpeed;
        _getDisableBurnFree = getDisableBurnFree;
        _getPostBurnAction = getPostBurnAction;
        DirectBurnLogDirectory = directBurnLogDirectory;

        OpenPictureCommand = new RelayCommand(BrowsePicture, () => !IsBusy);
        ChooseFilesFolderCommand = new RelayCommand(BrowseFilesFolder, () => !IsBusy && CanCarryFiles);
        ClearFilesFolderCommand = new RelayCommand(() => FilesFolder = string.Empty, () => !IsBusy);
        ChooseBaseCueCommand = new RelayCommand(BrowseBaseCue, () => !IsBusy && IsCd);
        ClearBaseCueCommand = new RelayCommand(() => BaseCuePath = string.Empty, () => !IsBusy);
        ChooseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        PhotoToneCommand = new RelayCommand(() => ApplyTone(ToneProfile.Photo));
        BinaryToneCommand = new RelayCommand(() => ApplyTone(ToneProfile.LegacyBinary));
        ResetPlacementCommand = new RelayCommand(ResetPlacement);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy);
        BurnCommand = new AsyncRelayCommand(() => BurnAsync(), () => !IsBusy && CanBurn);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);

        ReloadDiscChoices();
    }

    private bool CanBurn => _burnImage is not null || (_deviceService is not null && _burnEngine is not null);

    /// <summary>Optional directory where direct-drive SCSI logs are saved during two-session burns.</summary>
    public string? DirectBurnLogDirectory { get; set; }

    /// <summary>Result of the most recent two-session outer-files burn, or null.</summary>
    public PictureDiscTwoSessionResult? LastTwoSessionResult { get; private set; }

    /// <summary>Cancels an active generation or two-session burn.</summary>
    public void CancelActiveOperation()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }
    }

    public ICommand OpenPictureCommand { get; }
    public ICommand ChooseFilesFolderCommand { get; }
    public ICommand ClearFilesFolderCommand { get; }
    public ICommand ChooseBaseCueCommand { get; }
    public ICommand ClearBaseCueCommand { get; }
    public ICommand ChooseOutputCommand { get; }
    public ICommand PhotoToneCommand { get; }
    public ICommand BinaryToneCommand { get; }
    public ICommand ResetPlacementCommand { get; }
    public AsyncRelayCommand GenerateCommand { get; }
    public AsyncRelayCommand BurnCommand { get; }
    public ICommand CancelCommand { get; }

    public ObservableCollection<DiscArtDiscChoice> DiscChoices { get; } = new();

    /// <summary>The latest placement preview work; tests await it instead of waiting a fixed time.</summary>
    public Task PlacementTask { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------------------------------------------------------
    // Disc
    // ---------------------------------------------------------------------------------------------------------------

    public DiscArtMedium Medium
    {
        get => _medium;
        set
        {
            if (!SetProperty(ref _medium, value)) return;
            if (value != DiscArtMedium.Cd)
            {
                FourLevels = false; // DVD and BD pictures have two levels
                HidePictureInPreGap = false;
                BaseCuePath = string.Empty;
            }
            if (value == DiscArtMedium.Cd)
            {
                FilesFolder = string.Empty;
                ArtInnerRadiusText = string.Empty;
                ArtOuterRadiusText = string.Empty;
            }

            OnPropertyChanged(nameof(IsCd));
            OnPropertyChanged(nameof(IsDvd));
            OnPropertyChanged(nameof(IsBluRay));
            OnPropertyChanged(nameof(CanCarryFiles));
            OnPropertyChanged(nameof(CanDrawTitleRing));
            OnPropertyChanged(nameof(TitleRingUnavailableReason));
            OnPropertyChanged(nameof(TitleRingToolTip));
            OnPropertyChanged(nameof(CanUsePreGap));
            OnPropertyChanged(nameof(CanEditPreGapSeconds));
            OnPropertyChanged(nameof(OutputHint));
            ReloadDiscChoices();
        }
    }

    public bool IsCd { get => _medium == DiscArtMedium.Cd; set { if (value) Medium = DiscArtMedium.Cd; } }
    public bool IsDvd { get => _medium == DiscArtMedium.Dvd; set { if (value) Medium = DiscArtMedium.Dvd; } }
    public bool IsBluRay { get => _medium == DiscArtMedium.BluRay; set { if (value) Medium = DiscArtMedium.BluRay; } }

    /// <summary>Files go on DVD and BD picture discs; a CD picture track carries none.</summary>
    public bool CanCarryFiles => _medium != DiscArtMedium.Cd;

    /// <summary>A title ring is drawn on DVD picture discs only so far.</summary>
    public bool CanDrawTitleRing => _medium == DiscArtMedium.Dvd;

    /// <summary>Reason the title ring boxes are disabled on CD and BD; empty on DVD.</summary>
    public string TitleRingUnavailableReason => _medium == DiscArtMedium.Dvd
        ? string.Empty
        : "A title ring is drawn on DVD picture discs only so far.";

    public string TitleRingToolTip => _medium == DiscArtMedium.Dvd
        ? "Title drawn in large letters around the outer edge of the picture zone (DVD only)."
        : TitleRingUnavailableReason;

    public string OutputHint => _medium == DiscArtMedium.Cd
        ? "CD: a .wav picture track with its CUE sheet."
        : "DVD / BD: an .iso image.";

    public DiscArtDiscChoice? SelectedDisc
    {
        get => _selectedDisc;
        set
        {
            if (SetProperty(ref _selectedDisc, value)) InvalidatePlacement();
        }
    }

    /// <summary>Lists the presets (built-in and the user's file) and the saved calibrations for the chosen disc type.
    /// The default preset of the type is selected, as on the command line.</summary>
    public void ReloadDiscChoices()
    {
        DiscChoices.Clear();
        CalibrationDiscKind kind = KindOf(_medium);
        try
        {
            _presets ??= DiscArtPresets.Load(_userPresetsPath);
            switch (kind)
            {
                case CalibrationDiscKind.Cd:
                    foreach (CdDiscPresetDefinition p in _presets.CdPresets)
                        DiscChoices.Add(new DiscArtDiscChoice(p.DisplayName, DiscArtPresets.ToParameters(p), PresetSource(p.Id)));
                    break;
                case CalibrationDiscKind.Dvd:
                    foreach (DvdDiscPresetDefinition p in _presets.DvdPresets)
                        DiscChoices.Add(new DiscArtDiscChoice(p.DisplayName, DiscArtPresets.ToParameters(p), PresetSource(p.Id)));
                    break;
                default:
                    foreach (BluRayDiscPresetDefinition p in _presets.BluRayPresets ?? [])
                    {
                        try
                        {
                            DiscChoices.Add(new DiscArtDiscChoice(p.DisplayName, DiscArtPresets.ToParameters(p), PresetSource(p.Id)));
                        }
                        catch (ArgumentException ex)
                        {
                            StatusMessage = $"BD preset '{p.Id}' was skipped: {ex.Message}";
                        }
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusMessage = "The disc presets could not be read: " + ex.Message;
        }

        try
        {
            foreach (CalibrationStoreEntry entry in CalibrationStore.Load(_calibrationStorePath).Entries)
            {
                if (entry.Parameters.Kind != kind) continue;
                DiscChoices.Add(new DiscArtDiscChoice(
                    $"Saved calibration: {entry.DriveModel} / {entry.MediaId}", entry.Parameters,
                    $"saved calibration for drive '{entry.DriveModel}' and media '{entry.MediaId}'"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException)
        {
            StatusMessage = "The saved calibrations could not be read: " + ex.Message;
        }

        string defaultSource = PresetSource(DiscArtPresets.DefaultPresetId(kind));
        SelectedDisc = DiscChoices.FirstOrDefault(c => string.Equals(c.Source, defaultSource, StringComparison.OrdinalIgnoreCase))
            ?? DiscChoices.FirstOrDefault();
    }

    /// <summary>Selects a preset by its id (as given to <c>--disc-preset</c>); false when the list has no such preset.</summary>
    public bool SelectPreset(string presetId)
    {
        DiscArtDiscChoice? choice = DiscChoices.FirstOrDefault(
            c => string.Equals(c.Source, PresetSource(presetId), StringComparison.OrdinalIgnoreCase));
        if (choice is null) return false;
        SelectedDisc = choice;
        return true;
    }

    private static string PresetSource(string id) => $"disc preset '{id}'";

    // ---------------------------------------------------------------------------------------------------------------
    // Picture and placement
    // ---------------------------------------------------------------------------------------------------------------

    public string PicturePath => _picturePath;
    public bool HasPicture => _picture is not null;

    /// <summary>Opens a PNG picture; false (with the reason in <see cref="StatusMessage"/>) when it cannot be read.</summary>
    public bool OpenPicture(string path)
    {
        BgraRaster picture;
        string full;
        try
        {
            full = Path.GetFullPath(path);
            picture = PngRaster.Load(full);
            picture.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            StatusMessage = "The picture could not be read as a PNG: " + ex.Message;
            return false;
        }

        _picture = picture;
        _picturePath = full;
        OnPropertyChanged(nameof(PicturePath));
        OnPropertyChanged(nameof(HasPicture));
        StatusMessage = string.Create(CultureInfo.InvariantCulture,
            $"Picture: {Path.GetFileName(full)} ({picture.Width} x {picture.Height} pixels).");
        InvalidatePlacement();
        return true;
    }

    public double Scale
    {
        get => _scale;
        set { if (SetProperty(ref _scale, Math.Clamp(value, 0.1, 4))) RefreshPreview(); }
    }

    public double OffsetXMm
    {
        get => _offsetXMm;
        set { if (SetProperty(ref _offsetXMm, Math.Clamp(value, -60, 60))) RefreshPreview(); }
    }

    public double OffsetYMm
    {
        get => _offsetYMm;
        set { if (SetProperty(ref _offsetYMm, Math.Clamp(value, -60, 60))) RefreshPreview(); }
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set { if (SetProperty(ref _rotationDegrees, Math.Clamp(value, -180, 180))) RefreshPreview(); }
    }

    /// <summary>Size the picture to the drawn ring (its edges touch the ring's outer radius) instead of the disc.</summary>
    public bool FitToRing
    {
        get => _fitToRing;
        set
        {
            if (SetProperty(ref _fitToRing, value))
            {
                _preGapSetFitToRing = false;
                RefreshPreview();
            }
        }
    }

    /// <summary>Moves the picture by a drag on the preview, given in preview pixels.</summary>
    public void MoveByPreviewPixels(double dx, double dy)
    {
        double mmPerPixel = 2 * PreviewDiscRadiusMm / PreviewPixels;
        OffsetXMm = Math.Round(_offsetXMm + dx * mmPerPixel, 1);
        OffsetYMm = Math.Round(_offsetYMm + dy * mmPerPixel, 1);
    }

    public DiscArtTransform Transform => new(_scale, _offsetXMm, _offsetYMm, _rotationDegrees,
        _fitToRing ? DiscArtFit.Ring : DiscArtFit.Disc);

    private void ResetPlacement()
    {
        Scale = 1;
        OffsetXMm = 0;
        OffsetYMm = 0;
        RotationDegrees = 0;
        FitToRing = false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Tone
    // ---------------------------------------------------------------------------------------------------------------

    public double DitherStrength
    {
        get => _ditherStrength;
        set { if (SetProperty(ref _ditherStrength, Math.Clamp(Math.Round(value, 2), 0, 1))) OnPropertyChanged(nameof(ToneSummary)); }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            if (SetProperty(ref _brightness, Math.Clamp(Math.Round(value), ToneProfile.MinimumBrightness, ToneProfile.MaximumBrightness)))
                OnPropertyChanged(nameof(ToneSummary));
        }
    }

    /// <summary>Four grey levels instead of two (CD only).</summary>
    public bool FourLevels
    {
        get => _fourLevels;
        set
        {
            if (SetProperty(ref _fourLevels, value && _medium == DiscArtMedium.Cd)) OnPropertyChanged(nameof(ToneSummary));
        }
    }

    public bool SrgbLinearization
    {
        get => _srgbLinearization;
        set { if (SetProperty(ref _srgbLinearization, value)) OnPropertyChanged(nameof(ToneSummary)); }
    }

    public ToneProfile Tone => new(_ditherStrength, _fourLevels ? ToneProfile.FourLevels : ToneProfile.TwoLevels,
        _brightness, _srgbLinearization);

    public string ToneSummary => Tone == ToneProfile.Photo ? "Tone: photo (same as --tone photo)."
        : Tone == ToneProfile.LegacyBinary ? "Tone: black and white (same as --tone binary)."
        : string.Create(CultureInfo.InvariantCulture,
            $"Tone: dither {_ditherStrength:0.00}, brightness {_brightness:+0;-0;0}, {(_fourLevels ? 4 : 2)} levels{(_srgbLinearization ? ", sRGB linear" : "")}.");

    public void ApplyTone(ToneProfile tone)
    {
        DitherStrength = tone.DitherStrength;
        Brightness = tone.Brightness;
        FourLevels = tone.Levels == ToneProfile.FourLevels;
        SrgbLinearization = tone.SrgbLinearization;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Files, art band, output
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Folder whose files go on the same DVD or BD; empty for a picture-only disc.</summary>
    public string FilesFolder
    {
        get => _filesFolder;
        set
        {
            if (SetProperty(ref _filesFolder, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasFiles));
                InvalidatePlacement();
            }
        }
    }

    public bool HasFiles => _filesFolder.Length > 0;

    /// <summary>Files at the outer end of the disc (picture inside). DVD-R and DVD+R burn in two sessions.</summary>
    public bool FilesOuter
    {
        get => _filesOuter;
        set { if (SetProperty(ref _filesOuter, value)) InvalidatePlacement(); }
    }

    /// <summary>Inner radius of the art band in mm; empty draws from where the picture can start.</summary>
    public string ArtInnerRadiusText
    {
        get => _artInnerRadiusText;
        set { if (SetProperty(ref _artInnerRadiusText, value?.Trim() ?? string.Empty)) InvalidatePlacement(); }
    }

    /// <summary>Outer radius of the art band in mm; empty draws to the end of the disc.</summary>
    public string ArtOuterRadiusText
    {
        get => _artOuterRadiusText;
        set { if (SetProperty(ref _artOuterRadiusText, value?.Trim() ?? string.Empty)) InvalidatePlacement(); }
    }

    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value ?? string.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Title ring (DVD only)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Title drawn in large letters in the outer edge of the picture zone (DVD only); empty omits the ring.</summary>
    public string TitleRingText
    {
        get => _titleRingText;
        set { if (SetProperty(ref _titleRingText, value ?? string.Empty)) InvalidatePlacement(); }
    }

    /// <summary>Date shown after the title (yyyy-MM-dd); empty or "none" omits the date. Defaults to today.</summary>
    public string TitleRingDateText
    {
        get => _titleRingDateText;
        set { if (SetProperty(ref _titleRingDateText, value ?? string.Empty)) InvalidatePlacement(); }
    }

    /// <summary>Radial height of the title ring in mm (2 to 10); empty uses the default 3 mm.</summary>
    public string TitleRingHeightText
    {
        get => _titleRingHeightText;
        set { if (SetProperty(ref _titleRingHeightText, value?.Trim() ?? string.Empty)) InvalidatePlacement(); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Audio CD CUE and pre-gap picture (CD only)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Audio or mixed-mode CUE sheet to combine with a CD picture track; empty builds a picture-only disc.</summary>
    public string BaseCuePath
    {
        get => _baseCuePath;
        set
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _baseCuePath, trimmed)) return;
            if (trimmed.Length == 0)
            {
                HidePictureInPreGap = false;
            }

            OnPropertyChanged(nameof(HasBaseCue));
            OnPropertyChanged(nameof(CanUsePreGap));
            OnPropertyChanged(nameof(CanEditPreGapSeconds));
            InvalidatePlacement();
        }
    }

    public bool HasBaseCue => _baseCuePath.Length > 0;

    public bool CanUsePreGap => IsCd && HasBaseCue;

    public bool CanEditPreGapSeconds => CanUsePreGap && _hidePictureInPreGap;

    /// <summary>Experimental, off by default: hides the CD picture in track 1's pre-gap (INDEX 00) instead of appending it as the last track.</summary>
    public bool HidePictureInPreGap
    {
        get => _hidePictureInPreGap;
        set
        {
            if (!SetProperty(ref _hidePictureInPreGap, value)) return;
            if (value)
            {
                // Match the CLI default (--fit ring when --pregap-art is set without --fit).
                if (!_fitToRing)
                {
                    _fitToRing = true;
                    _preGapSetFitToRing = true;
                    OnPropertyChanged(nameof(FitToRing));
                }
            }
            else if (_preGapSetFitToRing)
            {
                _preGapSetFitToRing = false;
                if (_fitToRing)
                {
                    _fitToRing = false;
                    OnPropertyChanged(nameof(FitToRing));
                }
            }

            OnPropertyChanged(nameof(CanEditPreGapSeconds));
            InvalidatePlacement();
        }
    }

    /// <summary>Length in seconds of the pre-gap picture (60 to 1200, default 300).</summary>
    public string PreGapSecondsText
    {
        get => _preGapSecondsText;
        set { if (SetProperty(ref _preGapSecondsText, value?.Trim() ?? string.Empty)) InvalidatePlacement(); }
    }

    /// <summary>Experimental warning printed alongside pre-gap art on both CLI and UI.</summary>
    public string PreGapWarning => DiscArtImageBuilder.CdPreGapWarning;

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value?.Trim() ?? string.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // State shown on the screen
    // ---------------------------------------------------------------------------------------------------------------

    public string PlanSummary
    {
        get => _planSummary;
        private set => SetProperty(ref _planSummary, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>The placed picture on a 120 mm disc, <see cref="PreviewPixels"/> square (PNG).</summary>
    public byte[]? PlacementPreviewPng
    {
        get => _placementPreviewPng;
        private set => SetProperty(ref _placementPreviewPng, value);
    }

    /// <summary>What a drive would draw from the generated image (DVD and CD), <see cref="ReadbackPixels"/> square (PNG).</summary>
    public byte[]? ReadbackPreviewPng
    {
        get => _readbackPreviewPng;
        private set => SetProperty(ref _readbackPreviewPng, value);
    }

    public DiscArtResult? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            GenerateCommand.RaiseCanExecuteChanged();
            BurnCommand.RaiseCanExecuteChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Request (the same as the command line builds)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The request for the current choices. Throws <see cref="ArgumentException"/> with a plain reason when a
    /// choice is missing or not a number.</summary>
    public DiscArtRequest BuildRequest(string? outputPath = null)
    {
        DiscArtTitleRingRequest? titleRing = BuildTitleRing();
        if (_picture is null && titleRing is null) throw new ArgumentException("Open a PNG picture first.");
        if (_selectedDisc is null) throw new ArgumentException("Choose a disc preset or a saved calibration.");
        string output = outputPath ?? _outputPath;
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose where to save the image.");

        bool files = CanCarryFiles && _filesFolder.Length > 0;
        string? baseCue = IsCd && _baseCuePath.Length > 0 ? Path.GetFullPath(_baseCuePath) : null;
        int? preGapSeconds = ParsePreGapSeconds(baseCue);
        return new DiscArtRequest(
            _medium,
            _picture is null ? string.Empty : _picturePath,
            _selectedDisc.Parameters,
            Path.GetFullPath(output),
            Tone,
            0,
            files ? Path.GetFullPath(_filesFolder) : null,
            files && _filesOuter ? PictureDiscFilesPlacement.OuterFiles : PictureDiscFilesPlacement.InnerFiles,
            string.IsNullOrWhiteSpace(_volumeLabel) ? "DISC_ART" : _volumeLabel.Trim(),
            baseCue,
            CdInterleave: true,
            DvdReadCompatibility: false,
            Overwrite: false,
            Transform: _picture is null ? null : Transform,
            ArtInnerRadiusMm: ParseRadius(_artInnerRadiusText, "inner"),
            ArtOuterRadiusMm: ParseRadius(_artOuterRadiusText, "outer"),
            TitleRing: titleRing,
            CdPreGapSeconds: preGapSeconds);
    }

    private DiscArtTitleRingRequest? BuildTitleRing()
    {
        if (!CanDrawTitleRing || string.IsNullOrWhiteSpace(_titleRingText)) return null;

        string trimmedDate = _titleRingDateText.Trim();
        DateOnly? date;
        if (trimmedDate.Length == 0 || string.Equals(trimmedDate, "none", StringComparison.OrdinalIgnoreCase))
        {
            date = null;
        }
        else if (DateOnly.TryParseExact(trimmedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDate))
        {
            date = parsedDate;
        }
        else
        {
            throw new ArgumentException($"Invalid title ring date '{trimmedDate}'. Expected yyyy-MM-dd, none, or empty.");
        }

        string trimmedHeight = _titleRingHeightText.Trim();
        double height = DiscArtTitleRingRequest.DefaultHeightMm;
        if (trimmedHeight.Length > 0
            && !double.TryParse(trimmedHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out height)
            && !double.TryParse(trimmedHeight, NumberStyles.Float, CultureInfo.CurrentCulture, out height))
        {
            throw new ArgumentException($"The title ring height '{trimmedHeight}' is not a number of millimetres.");
        }

        return new DiscArtTitleRingRequest(_titleRingText.Trim(), date, height);
    }

    private int? ParsePreGapSeconds(string? baseCue)
    {
        if (!IsCd || !_hidePictureInPreGap) return null;
        if (baseCue is null)
        {
            throw new ArgumentException("Choose the audio CD's CUE sheet first (a pre-gap picture goes before track 1).");
        }

        string trimmed = _preGapSecondsText.Trim();
        if (trimmed.Length == 0) return DiscArtImageBuilder.CdPreGapDefaultSeconds;
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds is < DiscArtImageBuilder.CdPreGapMinimumSeconds or > DiscArtImageBuilder.CdPreGapMaximumSeconds)
        {
            throw new ArgumentException(
                $"Invalid pre-gap length '{trimmed}'. Expected whole seconds from {DiscArtImageBuilder.CdPreGapMinimumSeconds} to {DiscArtImageBuilder.CdPreGapMaximumSeconds}.");
        }

        return seconds;
    }

    private double? ParseRadius(string text, string which)
    {
        if (!CanCarryFiles || text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            throw new ArgumentException($"The art band's {which} radius '{text}' is not a number of millimetres.");
        }

        return value;
    }

    private string PreviewOutputPath() => Path.Combine(Path.GetTempPath(),
        _medium == DiscArtMedium.Cd ? "ignisdisc-disc-art-preview.wav" : "ignisdisc-disc-art-preview.iso");

    // ---------------------------------------------------------------------------------------------------------------
    // Placement preview
    // ---------------------------------------------------------------------------------------------------------------

    private void InvalidatePlacement()
    {
        _placementValid = false;
        RefreshPreview();
    }

    private bool HasPendingTitleRing => CanDrawTitleRing && !string.IsNullOrWhiteSpace(_titleRingText);

    /// <summary>Starts drawing the placement preview. The plan is made again only when the disc, files or band changed;
    /// a result that a newer change has overtaken is dropped.</summary>
    public Task RefreshPreview()
    {
        int generation = ++_placementGeneration;
        if (_selectedDisc is null || (_picture is null && !HasPendingTitleRing))
        {
            PlacementPreviewPng = null;
            PlanSummary = string.Empty;
            return PlacementTask = Task.CompletedTask;
        }

        DiscArtRequest request;
        try
        {
            request = BuildRequest(PreviewOutputPath());
        }
        catch (ArgumentException ex)
        {
            PlanSummary = ex.Message;
            PlacementPreviewPng = null;
            return PlacementTask = Task.CompletedTask;
        }

        DiscArtPlacement? known = _placementValid ? _placement : null;
        BgraRaster? picture = _picture;
        PlacementTask = RunPreviewAsync(generation, request, known, picture);
        return PlacementTask;
    }

    private async Task RunPreviewAsync(int generation, DiscArtRequest request, DiscArtPlacement? known, BgraRaster? picture)
    {
        try
        {
            (DiscArtPlacement placement, byte[] png) = await Task.Run(() =>
            {
                DiscArtPlacement p = known ?? DiscArtImageBuilder.Placement(request);
                BgraRaster view = RenderPlacementCore(picture, request.Transform, p, request.TitleRing);
                using var stream = new MemoryStream();
                PngRaster.Write(stream, view);
                return (p, stream.ToArray());
            });
            if (generation != _placementGeneration) return;
            _placement = placement;
            _placementValid = true;
            PlacementPreviewPng = png;
            PlanSummary = DescribePlan(placement.Plan);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            if (generation != _placementGeneration) return;
            _placementValid = false;
            PlacementPreviewPng = null;
            PlanSummary = ex.Message;
        }
    }

    /// <summary>
    /// The placement preview: a 120 mm disc seen from the label side, <see cref="PreviewPixels"/> square. Inside the
    /// drawn ring each pixel is the placed picture as the encoder receives it (from <see cref="DiscArtCanvas.Render"/>,
    /// over white, which is what the encoder draws where the picture is transparent); the rest of the disc is
    /// <see cref="BlankDiscColor"/>; around the disc and in the centre hole it is transparent.
    /// </summary>
    public static BgraRaster RenderPlacement(BgraRaster picture, DiscArtTransform transform, DiscArtPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(transform);
        return RenderPlacementCore(picture, transform, placement, null);
    }

    private static BgraRaster RenderPlacementCore(BgraRaster? picture, DiscArtTransform? transform,
        DiscArtPlacement placement, DiscArtTitleRingRequest? titleRing)
    {
        const int n = PreviewPixels;
        BgraRaster? canvas = picture is not null
            ? DiscArtCanvas.Render(picture, transform ?? DiscArtTransform.Default, placement.PictureRadiusMm,
                placement.Plan.PictureOuterRadiusMm, n)
            : null;
        RosettaTitleRing? ring = placement.Plan.TitleRing is { } ringPlan && titleRing is not null
            ? new RosettaTitleRing(titleRing.Title, titleRing.Date, ringPlan.InnerRadiusMm, ringPlan.OuterRadiusMm)
            : null;
        double ringInner = placement.Plan.PictureInnerRadiusMm;
        double ringOuter = placement.Plan.PictureOuterRadiusMm;
        double mmPerPixel = 2 * PreviewDiscRadiusMm / n;
        double canvasPixelsPerMm = n / (2 * placement.PictureRadiusMm);
        byte[] output = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            double my = (y + 0.5 - n / 2.0) * mmPerPixel;
            for (int x = 0; x < n; x++)
            {
                double mx = (x + 0.5 - n / 2.0) * mmPerPixel;
                double r = Math.Sqrt(mx * mx + my * my);
                int o = (y * n + x) * 4;
                if (r > PreviewDiscRadiusMm || r < HoleRadiusMm)
                {
                    WriteColor(output, o, OutsideColor);
                    continue;
                }

                if (ring is not null && r >= ring.InnerRadiusMm && r <= ring.OuterRadiusMm)
                {
                    bool ink = ring.IsInk(r, Math.Atan2(my, mx));
                    WriteColor(output, o, ink ? 0xFF000000 : 0xFFFFFFFF);
                    continue;
                }

                if (canvas is null || r < ringInner || r > ringOuter || (ring is not null && r > ring.InnerRadiusMm))
                {
                    WriteColor(output, o, BlankDiscColor);
                    continue;
                }

                int cx = (int)Math.Floor(mx * canvasPixelsPerMm + n / 2.0);
                int cy = (int)Math.Floor(my * canvasPixelsPerMm + n / 2.0);
                if (cx < 0 || cy < 0 || cx >= n || cy >= n)
                {
                    WriteColor(output, o, 0xFFFFFFFF);
                    continue;
                }

                int c = (cy * n + cx) * 4;
                double alpha = canvas.Pixels[c + 3] / 255.0;
                for (int k = 0; k < 3; k++)
                {
                    output[o + k] = (byte)Math.Round(canvas.Pixels[c + k] * alpha + 255 * (1 - alpha));
                }

                output[o + 3] = 255;
            }
        }

        return new BgraRaster(n, n, output);
    }

    private static void WriteColor(byte[] pixels, int offset, uint argb)
    {
        pixels[offset] = (byte)argb;
        pixels[offset + 1] = (byte)(argb >> 8);
        pixels[offset + 2] = (byte)(argb >> 16);
        pixels[offset + 3] = (byte)(argb >> 24);
    }

    private static string DescribePlan(DiscArtPlan plan)
    {
        var text = new System.Text.StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture,
            $"Picture: {plan.PictureInnerRadiusMm:0.#} to {plan.PictureOuterRadiusMm:0.#} mm, sectors {plan.PictureStartLba:N0} to {plan.PictureEndLba - 1:N0}. Image: {plan.TotalSectors:N0} sectors."));
        if (plan.CdPreGapSectors is { } preGapSectors)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" Pre-gap: track 1 INDEX 00 (LBA 0 to {preGapSectors - 1:N0}); track 1 starts at LBA {preGapSectors:N0}. {DiscArtImageBuilder.CdPreGapWarning}"));
        }
        else if (plan.BaseCueSectors is { } baseSectors)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" Base CUE: existing tracks take {baseSectors:N0} sectors; the picture is the last track."));
        }

        if (plan.FilesLayout is { } layout)
        {
            text.Append(layout.Placement == PictureDiscFilesPlacement.InnerFiles
                ? $" Files: sectors 0 to {layout.VolumeEndLba - 1:N0} (inside)."
                : layout.Medium == PictureDiscMedium.Dvd
                    ? $" Files: sectors {layout.FilesSessionStartLba:N0} to {layout.VolumeEndLba - 1:N0} (outside; two sessions on blank DVD-R / DVD+R)."
                    : $" Files: sectors {layout.FilesSessionStartLba:N0} to {layout.VolumeEndLba - 1:N0} (outside; two sessions cover DVD-R and DVD+R only so far).");
        }

        if (plan.Band is { } band)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" Band saves {band.SavedSecondsAt1x / 60:0.0} min of writing at 1x."));
        }

        if (plan.TitleRing is { } ring)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" Title ring: \"{ring.Text}\" in r = {ring.InnerRadiusMm:0.###} to {ring.OuterRadiusMm:0.###} mm (sectors {ring.StartLba:N0} to {ring.EndLba - 1:N0})."));
        }

        return text.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Generate, read-back preview, burn
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the image with <see cref="DiscArtImageBuilder.Generate"/> (an existing file of the same name is
    /// replaced, as the save dialog already asked), then draws the read-back preview for DVD and CD.</summary>
    public async Task GenerateAsync()
    {
        DiscArtRequest request;
        try
        {
            request = BuildRequest() with { Overwrite = true };
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        LastResult = null;
        ReadbackPreviewPng = null;
        StatusMessage = "Generating the picture disc image...";
        var progress = new Progress<DiscJobProgress>(p =>
        {
            if (p.Fraction is { } f) Progress = Math.Round(f * 100, 1);
            StatusMessage = p.Message;
        });
        try
        {
            DiscArtResult result = await Task.Run(() => DiscArtImageBuilder.Generate(request, progress, token), token);
            LastResult = result;
            Progress = 100;
            StatusMessage = $"Image written: {result.OutputPath} ({result.BytesWritten:N0} bytes).";
            if (result.Plan.CdPreGapSectors is not null)
            {
                StatusMessage += $" CUE: {result.CueSheetPath}. {DiscArtImageBuilder.CdPreGapWarning}";
            }
            if (request.Medium == DiscArtMedium.BluRay)
            {
                StatusMessage += " A read-back preview is not available for BD pictures yet.";
                return;
            }

            string previewPath = result.OutputPath + ".preview.png";
            try
            {
                await Task.Run(() => DiscArtImageBuilder.Preview(result.OutputPath, previewPath, ReadbackPixels, token), token);
                ReadbackPreviewPng = await File.ReadAllBytesAsync(previewPath, token);
            }
            finally
            {
                TryDelete(previewPath);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Generating was cancelled; no image was left behind.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            StatusMessage = "The image could not be made: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Burns the generated image. Single-session DVD and BD images go to the Disc Image screen's burn (target
    /// choice, verify after burn); DVD images with the files at the outer edge burn in two sessions through
    /// <see cref="PictureDiscTwoSessionService"/>. A CD picture track and unsupported outer-files media are refused.</summary>
    public Task<BurnOperationResult> BurnAsync() => BurnAsync(driveId: null);

    public async Task<BurnOperationResult> BurnAsync(string? driveId)
    {
        if (!CanBurn)
        {
            return Refuse("Burning is not available here.");
        }

        if (_lastResult is not { } result || !File.Exists(result.OutputPath))
        {
            return Refuse("Generate the image first.");
        }

        if (result.Plan.Medium == DiscArtMedium.Cd)
        {
            return Refuse("A CD picture track is written with Session-at-Once by the direct drive writer. Burn its CUE sheet with: "
                + $"ignisdisc-cli disc-art burn --drive <id> --image \"{result.CueSheetPath ?? result.OutputPath}\"");
        }

        if (result.Plan.FilesLayout is { Placement: PictureDiscFilesPlacement.OuterFiles } outer)
        {
            return await BurnOuterFilesTwoSessionAsync(result.OutputPath, outer, driveId);
        }

        if (_burnImage is null)
        {
            return Refuse("Burning is not available here.");
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Burning {Path.GetFileName(result.OutputPath)}...";
            BurnOperationResult burn = await _burnImage(result.OutputPath);
            StatusMessage = burn.Success ? "Picture disc burned." : "Burn did not finish: " + burn.ErrorMessage;
            return burn;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<BurnOperationResult> BurnOuterFilesTwoSessionAsync(string imagePath, PictureDiscLayout outer, string? driveId)
    {
        if (PictureDiscTwoSessionService.WhyLayoutNotSupported(outer) is { } layoutError)
        {
            return Refuse(layoutError);
        }

        if (_deviceService is null || _burnEngine is null)
        {
            return Refuse("Burning is not available here.");
        }

        IDiscRecorderBackend? recorder;
        if (string.IsNullOrWhiteSpace(driveId) && Views.BurnTargetDialog.IsInteractiveSession)
        {
            var recorders = _getAvailableRecorders?.Invoke() ?? _deviceService.GetRecorders();
            recorder = Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                recorders,
                _getSelectedRecorder?.Invoke(),
                $"Burn Two-Session Picture Disc ({Path.GetFileName(imagePath)})",
                new NativeDataBurnEngine(_burnEngine, DirectBurnLogDirectory));

            if (recorder is null)
            {
                StatusMessage = "Burn cancelled.";
                return BurnOperationResult.CancelledResult(StatusMessage);
            }

            _setSelectedRecorder?.Invoke(recorder);
        }
        else
        {
            recorder = ResolveRecorder(driveId);
            if (recorder is null)
            {
                return Refuse("No optical recorder drive selected or available.");
            }
        }

        if (recorder is VirtualDiscRecorder vRec && Views.BurnTargetDialog.IsInteractiveSession)
        {
            var status = vRec.QueryMediaStatus();
            if (!status.MediaPresent)
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            }
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsBusy = true;
        Progress = 0;
        LastTwoSessionResult = null;
        StatusMessage = "Burning in two sessions: the picture first (left open), then the files at the drive's next writable address...";

        var progress = new ClosableProgress<BurnProgressSnapshot>(snap =>
        {
            if (!_isBusy) return;
            if (snap.TotalLba > 0)
            {
                Progress = Math.Round(Math.Clamp((double)snap.CurrentLba / snap.TotalLba * 100.0, 0, 100), 1);
                if (!string.IsNullOrWhiteSpace(snap.CurrentActionDescription))
                {
                    StatusMessage = $"{snap.CurrentActionDescription} ({Progress:F0}%)";
                }
            }
            else if (!string.IsNullOrWhiteSpace(snap.CurrentActionDescription))
            {
                StatusMessage = snap.CurrentActionDescription;
            }
        });

        try
        {
            var options = new DataBurnOptions
            {
                Speed = _getWriteSpeed?.Invoke(),
                StrictVerify = VerifyPreferences.StrictByDefault,
                DisableBufferUnderrunFree = _getDisableBurnFree?.Invoke() ?? false,
            };
            var service = new PictureDiscTwoSessionService(_burnEngine, DirectBurnLogDirectory);
            PictureDiscTwoSessionResult twoSession = await service.BurnAsync(recorder, imagePath, outer, options, progress, token);
            progress.Close();
            LastTwoSessionResult = twoSession;

            if (twoSession.Success)
            {
                Progress = 100;
                string summary = $"Two-session picture disc burned and verified: picture (sectors 0 to {twoSession.PictureSectors - 1:N0}), "
                    + $"files (session 2, sectors {twoSession.FilesSessionStartLba:N0} to {twoSession.FilesSessionStartLba + twoSession.FilesSessionSectors - 1:N0}).";
                if (!string.IsNullOrWhiteSpace(twoSession.Warning))
                {
                    summary += " " + twoSession.Warning;
                }

                StatusMessage = summary;
                PostBurnActionExecutor.Execute(_getPostBurnAction?.Invoke() ?? PostBurnAction.None, recorder);
                CompletionNotice.Show("Two-session picture disc burned", StatusMessage, success: true);

                int totalSectors = twoSession.PictureSectors + twoSession.FilesSessionSectors;
                TimeSpan totalTime = (twoSession.PictureSession?.TotalTime ?? TimeSpan.Zero)
                    + (twoSession.FilesSession?.TotalTime ?? TimeSpan.Zero);
                return new BurnOperationResult
                {
                    Success = true,
                    Status = BurnStatus.Succeeded,
                    SectorsWritten = totalSectors,
                    TotalTime = totalTime,
                    WarningMessage = twoSession.Warning,
                };
            }

            if (token.IsCancellationRequested
                || twoSession.PictureSession?.Status == BurnStatus.Cancelled
                || twoSession.FilesSession?.Status == BurnStatus.Cancelled)
            {
                StatusMessage = "Two-session burn was cancelled."
                    + (twoSession.NothingWritten ? " Nothing was written to the disc." : " The picture session was already written; the disc stays open.");
                CompletionNotice.Show("Two-session burn cancelled", StatusMessage, success: false);
                return BurnOperationResult.CancelledResult(StatusMessage);
            }

            string error = PictureDiscTwoSessionService.FormatError(twoSession) ?? "Burn failed.";
            StatusMessage = error;
            CompletionNotice.Show("Two-session burn failed", StatusMessage, success: false);
            return BurnOperationResult.Failed(error);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            StatusMessage = "Two-session burn was cancelled.";
            return BurnOperationResult.CancelledResult(StatusMessage);
        }
        finally
        {
            progress.Close();
            IsBusy = false;
        }
    }

    private IDiscRecorderBackend? ResolveRecorder(string? driveId)
    {
        if (_deviceService is null) return null;
        if (!string.IsNullOrWhiteSpace(driveId))
        {
            return _deviceService.GetRecorderById(driveId);
        }

        if (_getSelectedRecorder?.Invoke() is { } selected)
        {
            return selected;
        }

        var recorders = _getAvailableRecorders?.Invoke() ?? _deviceService.GetRecorders();
        return recorders.FirstOrDefault(r => !r.IsVirtual) ?? recorders.FirstOrDefault();
    }

    private BurnOperationResult Refuse(string message)
    {
        StatusMessage = message;
        return BurnOperationResult.Failed(message);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dialogs
    // ---------------------------------------------------------------------------------------------------------------

    private void BrowsePicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures (*.png)|*.png", Title = "Open picture" };
        if (dialog.ShowDialog() == true) OpenPicture(dialog.FileName);
    }

    private void BrowseFilesFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with the files for the disc" };
        if (dialog.ShowDialog() == true) FilesFolder = dialog.FolderName;
    }

    private void BrowseBaseCue()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "CUE sheets (*.cue)|*.cue", Title = "Audio CD CUE sheet" };
        if (dialog.ShowDialog() == true) BaseCuePath = dialog.FileName;
    }

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save picture disc image",
            Filter = _medium == DiscArtMedium.Cd ? "CD picture track (*.wav)|*.wav" : "Disc image (*.iso)|*.iso",
            FileName = _medium == DiscArtMedium.Cd ? "disc-art.wav" : "disc-art.iso",
        };
        if (dialog.ShowDialog() == true) OutputPath = dialog.FileName;
    }

    private static CalibrationDiscKind KindOf(DiscArtMedium medium) => medium switch
    {
        DiscArtMedium.Cd => CalibrationDiscKind.Cd,
        DiscArtMedium.Dvd => CalibrationDiscKind.Dvd,
        _ => CalibrationDiscKind.BluRay,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
