// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Iso;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Imaging;
using CdBurner.Lithography.Presets;

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
    private double _ditherStrength = ToneProfile.Photo.DitherStrength;
    private double _brightness = ToneProfile.Photo.Brightness;
    private bool _fourLevels;
    private bool _srgbLinearization;
    private string _filesFolder = string.Empty;
    private bool _filesOuter;
    private string _artInnerRadiusText = string.Empty;
    private string _artOuterRadiusText = string.Empty;
    private string _volumeLabel = "DISC_ART";
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

    /// <param name="burnImage">Burns a finished .iso (the app hands it to the Disc Image screen); null hides burning.</param>
    /// <param name="userPresetsPath">Presets file merged over the built-in ones (null: the default file).</param>
    /// <param name="calibrationStorePath">Saved calibrations (null: the default file).</param>
    public DiscArtViewModel(Func<string, Task<BurnOperationResult>>? burnImage = null,
        string? userPresetsPath = null, string? calibrationStorePath = null)
    {
        _burnImage = burnImage;
        _userPresetsPath = userPresetsPath;
        _calibrationStorePath = calibrationStorePath ?? CalibrationStore.GetDefaultPath();

        OpenPictureCommand = new RelayCommand(BrowsePicture, () => !IsBusy);
        ChooseFilesFolderCommand = new RelayCommand(BrowseFilesFolder, () => !IsBusy && CanCarryFiles);
        ClearFilesFolderCommand = new RelayCommand(() => FilesFolder = string.Empty, () => !IsBusy);
        ChooseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        PhotoToneCommand = new RelayCommand(() => ApplyTone(ToneProfile.Photo));
        BinaryToneCommand = new RelayCommand(() => ApplyTone(ToneProfile.LegacyBinary));
        ResetPlacementCommand = new RelayCommand(ResetPlacement);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy);
        BurnCommand = new AsyncRelayCommand(BurnAsync, () => !IsBusy && _burnImage is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);

        ReloadDiscChoices();
    }

    public ICommand OpenPictureCommand { get; }
    public ICommand ChooseFilesFolderCommand { get; }
    public ICommand ClearFilesFolderCommand { get; }
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
            if (value != DiscArtMedium.Cd) FourLevels = false; // DVD and BD pictures have two levels
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
            OnPropertyChanged(nameof(OutputHint));
            ReloadDiscChoices();
        }
    }

    public bool IsCd { get => _medium == DiscArtMedium.Cd; set { if (value) Medium = DiscArtMedium.Cd; } }
    public bool IsDvd { get => _medium == DiscArtMedium.Dvd; set { if (value) Medium = DiscArtMedium.Dvd; } }
    public bool IsBluRay { get => _medium == DiscArtMedium.BluRay; set { if (value) Medium = DiscArtMedium.BluRay; } }

    /// <summary>Files go on DVD and BD picture discs; a CD picture track carries none.</summary>
    public bool CanCarryFiles => _medium != DiscArtMedium.Cd;

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
        set { if (SetProperty(ref _fitToRing, value)) RefreshPreview(); }
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

    /// <summary>Files at the outer end of the disc (picture inside). Such an image cannot be burned yet.</summary>
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
        if (_picture is null) throw new ArgumentException("Open a PNG picture first.");
        if (_selectedDisc is null) throw new ArgumentException("Choose a disc preset or a saved calibration.");
        string output = outputPath ?? _outputPath;
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose where to save the image.");

        bool files = CanCarryFiles && _filesFolder.Length > 0;
        return new DiscArtRequest(
            _medium,
            _picturePath,
            _selectedDisc.Parameters,
            Path.GetFullPath(output),
            Tone,
            0,
            files ? Path.GetFullPath(_filesFolder) : null,
            files && _filesOuter ? PictureDiscFilesPlacement.OuterFiles : PictureDiscFilesPlacement.InnerFiles,
            string.IsNullOrWhiteSpace(_volumeLabel) ? "DISC_ART" : _volumeLabel.Trim(),
            null,
            CdInterleave: true,
            DvdReadCompatibility: false,
            Overwrite: false,
            Transform: Transform,
            ArtInnerRadiusMm: ParseRadius(_artInnerRadiusText, "inner"),
            ArtOuterRadiusMm: ParseRadius(_artOuterRadiusText, "outer"));
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

    /// <summary>Starts drawing the placement preview. The plan is made again only when the disc, files or band changed;
    /// a result that a newer change has overtaken is dropped.</summary>
    public Task RefreshPreview()
    {
        int generation = ++_placementGeneration;
        if (_picture is null || _selectedDisc is null)
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
        BgraRaster picture = _picture;
        PlacementTask = RunPreviewAsync(generation, request, known, picture);
        return PlacementTask;
    }

    private async Task RunPreviewAsync(int generation, DiscArtRequest request, DiscArtPlacement? known, BgraRaster picture)
    {
        try
        {
            (DiscArtPlacement placement, byte[] png) = await Task.Run(() =>
            {
                DiscArtPlacement p = known ?? DiscArtImageBuilder.Placement(request);
                BgraRaster view = RenderPlacement(picture, request.Transform!, p);
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
        const int n = PreviewPixels;
        BgraRaster canvas = DiscArtCanvas.Render(picture, transform, placement.PictureRadiusMm,
            placement.Plan.PictureOuterRadiusMm, n);
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

                if (r < ringInner || r > ringOuter)
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
        if (plan.FilesLayout is { } layout)
        {
            text.Append(layout.Placement == PictureDiscFilesPlacement.InnerFiles
                ? $" Files: sectors 0 to {layout.VolumeEndLba - 1:N0} (inside)."
                : $" Files: sectors {layout.FilesSessionStartLba:N0} to {layout.VolumeEndLba - 1:N0} (outside; cannot be burned yet).");
        }

        if (plan.Band is { } band)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" Band saves {band.SavedSecondsAt1x / 60:0.0} min of writing at 1x."));
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

    /// <summary>Burns the generated image. DVD and BD images go to the Disc Image screen's burn (target choice,
    /// verify after burn). An image with the files at the outer end, and a CD picture track, are refused here.</summary>
    public async Task<BurnOperationResult> BurnAsync()
    {
        if (_burnImage is null)
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

        if (result.Plan.FilesLayout?.Placement == PictureDiscFilesPlacement.OuterFiles)
        {
            return Refuse("This image has the files at the outer edge. Burning it needs two sessions, which IgnisDisc "
                + "cannot write yet; in one pass the files would not show up on a computer. Generate it again with the files inside.");
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
