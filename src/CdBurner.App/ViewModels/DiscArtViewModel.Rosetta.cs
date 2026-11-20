// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Globalization;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Burning;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Rosetta;

namespace CdBurner.App.ViewModels;

public sealed partial class DiscArtViewModel
{
    private int _selectedWorkspaceTab;
    private IReadOnlyList<RosettaPresetChoice> _rosettaPresets = Array.Empty<RosettaPresetChoice>();
    private RosettaPresetChoice _selectedRosettaPreset = null!;
    private string _rosettaTitle = "Rosetta Archive";
    private string _rosettaDateText = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private string _rosettaTextFolder = string.Empty;
    private string _rosettaFilesFolder = string.Empty;
    private string _rosettaPagesInnerRadiusText = RosettaHybridDisc.DefaultPagesInnerRadiusMm.ToString(CultureInfo.InvariantCulture);
    private bool _rosettaIncludeRimHeader = true;
    private int _rosettaRedundancyPercent;
    private string _rosettaOutputPath = string.Empty;
    private string _rosettaCapacitySummary = string.Empty;
    private string _rosettaVaultSummary = string.Empty;
    private string _rosettaConstraintMessage = string.Empty;
    private RosettaHybridPlan? _lastRosettaPlan;
    private RosettaHybridResult? _lastRosettaResult;

    public ICommand RosettaChooseTextFolderCommand { get; private set; } = null!;
    public ICommand RosettaClearTextFolderCommand { get; private set; } = null!;
    public ICommand RosettaChooseFilesFolderCommand { get; private set; } = null!;
    public ICommand RosettaClearFilesFolderCommand { get; private set; } = null!;
    public ICommand RosettaChooseOutputCommand { get; private set; } = null!;
    public AsyncRelayCommand RosettaGenerateCommand { get; private set; } = null!;
    public AsyncRelayCommand RosettaBurnCommand { get; private set; } = null!;

    private void InitializeRosetta()
    {
        _rosettaPresets = RosettaUiPresets.BuildChoices();
        _selectedRosettaPreset = _rosettaPresets[0];
        _rosettaRedundancyPercent = Par2Runner.FindPar2Executable() is not null ? 10 : 0;

        RosettaChooseTextFolderCommand = new RelayCommand(BrowseRosettaTextFolder, () => !IsBusy);
        RosettaClearTextFolderCommand = new RelayCommand(() => RosettaTextFolder = string.Empty, () => !IsBusy);
        RosettaChooseFilesFolderCommand = new RelayCommand(BrowseRosettaFilesFolder, () => !IsBusy && IsDvd);
        RosettaClearFilesFolderCommand = new RelayCommand(() => RosettaFilesFolder = string.Empty, () => !IsBusy);
        RosettaChooseOutputCommand = new RelayCommand(BrowseRosettaOutput, () => !IsBusy && IsDvd);
        RosettaGenerateCommand = new AsyncRelayCommand(GenerateRosettaAsync, () => !IsBusy && CanGenerateRosetta);
        RosettaBurnCommand = new AsyncRelayCommand(BurnRosettaAsync, () => !IsBusy && CanBurnRosetta);

        RefreshRosettaPlan();
    }

    public int SelectedWorkspaceTab
    {
        get => _selectedWorkspaceTab;
        set => SetProperty(ref _selectedWorkspaceTab, value);
    }

    public IReadOnlyList<RosettaPresetChoice> RosettaPresets => _rosettaPresets;

    public RosettaPresetChoice SelectedRosettaPreset
    {
        get => _selectedRosettaPreset;
        set
        {
            if (value is null) return;
            if (!RosettaUiPresets.IsPresetAllowedForMedium(value, _medium))
            {
                RosettaConstraintMessage = RosettaUiPresets.DisallowedReason(value, _medium) ?? string.Empty;
                OnPropertyChanged(nameof(SelectedRosettaPreset));
                NotifyRosettaPresetSelectionProperties();
                return;
            }

            if (!SetProperty(ref _selectedRosettaPreset, value)) return;
            RosettaConstraintMessage = string.Empty;
            NotifyRosettaPresetSelectionProperties();
            RefreshRosettaPlan();
        }
    }

    public bool IsRosettaHobbyistSelected
    {
        get => string.Equals(_selectedRosettaPreset.Id, RosettaUiPresets.HobbyistId, StringComparison.Ordinal);
        set { if (value) SelectRosettaPreset(RosettaUiPresets.HobbyistId); }
    }

    public bool IsRosettaHighDensitySelected
    {
        get => string.Equals(_selectedRosettaPreset.Id, RosettaUiPresets.HighDensityId, StringComparison.Ordinal);
        set { if (value) SelectRosettaPreset(RosettaUiPresets.HighDensityId); }
    }

    public bool IsRosettaExperimentalBluRaySelected
    {
        get => string.Equals(_selectedRosettaPreset.Id, RosettaUiPresets.ExperimentalBluRayId, StringComparison.Ordinal);
        set { if (value) SelectRosettaPreset(RosettaUiPresets.ExperimentalBluRayId); }
    }

    public bool CanSelectRosettaHighDensity => _medium != DiscArtMedium.Cd;
    public bool CanSelectRosettaExperimentalBluRay => _medium == DiscArtMedium.BluRay;
    public bool CanGenerateRosetta => _medium == DiscArtMedium.Dvd && _selectedDisc is not null;
    public bool CanBurnRosetta => CanBurn && _lastRosettaResult is { } res && File.Exists(res.OutputPath);

    public string RosettaHobbyistLabel => GetPresetChoice(RosettaUiPresets.HobbyistId).DisplayName;
    public string RosettaHighDensityLabel => GetPresetChoice(RosettaUiPresets.HighDensityId).DisplayName;
    public string RosettaExperimentalBluRayLabel => GetPresetChoice(RosettaUiPresets.ExperimentalBluRayId).DisplayName;

    public string RosettaHobbyistToolTip => GetPresetChoice(RosettaUiPresets.HobbyistId).ToolTipText;
    public string RosettaHighDensityToolTip =>
        RosettaUiPresets.DisallowedReason(GetPresetChoice(RosettaUiPresets.HighDensityId), _medium)
        ?? GetPresetChoice(RosettaUiPresets.HighDensityId).ToolTipText;
    public string RosettaExperimentalBluRayToolTip =>
        RosettaUiPresets.DisallowedReason(GetPresetChoice(RosettaUiPresets.ExperimentalBluRayId), _medium)
        ?? GetPresetChoice(RosettaUiPresets.ExperimentalBluRayId).ToolTipText;

    public string RosettaStartingPointStatus => RosettaUiPresets.StartingPointStatus;
    public string RosettaPresetMicroscopeSummary => _selectedRosettaPreset.MicroscopeSummary;
    public string RosettaMediaAdvice => RosettaUiPresets.GetMediaAdvice(_medium);

    public string RosettaMediumAvailabilityText => _medium switch
    {
        DiscArtMedium.Dvd => "DVD-R hybrid Rosetta builder ready (ISO 9660 + Joliet + UDF vault inside pages radius, 2 MiB zero gap, then microscope pages).",
        DiscArtMedium.Cd => "Layout preview only: hybrid Rosetta disc generation is DVD-R only until the CD port. CD-R cells must never be below 50 x 75 um.",
        _ => "Layout preview only (unmeasured): hybrid Rosetta disc generation is DVD-R only for now; BD-R contrast/polarity are unmeasured and require coaxial light.",
    };

    public string RosettaConstraintMessage
    {
        get => _rosettaConstraintMessage;
        private set => SetProperty(ref _rosettaConstraintMessage, value);
    }

    public string RosettaTitle
    {
        get => _rosettaTitle;
        set { if (SetProperty(ref _rosettaTitle, value ?? string.Empty)) RefreshRosettaPlan(); }
    }

    public string RosettaDateText
    {
        get => _rosettaDateText;
        set { if (SetProperty(ref _rosettaDateText, value ?? string.Empty)) RefreshRosettaPlan(); }
    }

    public string RosettaTextFolder
    {
        get => _rosettaTextFolder;
        set { if (SetProperty(ref _rosettaTextFolder, value?.Trim() ?? string.Empty)) RefreshRosettaPlan(); }
    }

    public string RosettaFilesFolder
    {
        get => _rosettaFilesFolder;
        set { if (SetProperty(ref _rosettaFilesFolder, value?.Trim() ?? string.Empty)) RefreshRosettaPlan(); }
    }

    public string RosettaPagesInnerRadiusText
    {
        get => _rosettaPagesInnerRadiusText;
        set { if (SetProperty(ref _rosettaPagesInnerRadiusText, value?.Trim() ?? string.Empty)) RefreshRosettaPlan(); }
    }

    public bool RosettaIncludeRimHeader
    {
        get => _rosettaIncludeRimHeader;
        set { if (SetProperty(ref _rosettaIncludeRimHeader, value)) RefreshRosettaPlan(); }
    }

    public int RosettaRedundancyPercent
    {
        get => _rosettaRedundancyPercent;
        set => SetProperty(ref _rosettaRedundancyPercent, Math.Clamp(value, 0, 100));
    }

    public string RosettaOutputPath
    {
        get => _rosettaOutputPath;
        set => SetProperty(ref _rosettaOutputPath, value?.Trim() ?? string.Empty);
    }

    public string RosettaCapacitySummary
    {
        get => _rosettaCapacitySummary;
        private set => SetProperty(ref _rosettaCapacitySummary, value);
    }

    public string RosettaVaultSummary
    {
        get => _rosettaVaultSummary;
        private set => SetProperty(ref _rosettaVaultSummary, value);
    }

    public RosettaHybridPlan? LastRosettaPlan
    {
        get => _lastRosettaPlan;
        private set => SetProperty(ref _lastRosettaPlan, value);
    }

    public RosettaHybridResult? LastRosettaResult
    {
        get => _lastRosettaResult;
        private set
        {
            if (SetProperty(ref _lastRosettaResult, value))
            {
                OnPropertyChanged(nameof(CanBurnRosetta));
                RosettaBurnCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Selects a Rosetta preset by id or name; returns false and records the media constraint if disallowed.</summary>
    public bool SelectRosettaPreset(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return false;
        RosettaPresetChoice? match = _rosettaPresets.FirstOrDefault(p =>
            string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Preset.Id, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Preset.Name, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;

        if (!RosettaUiPresets.IsPresetAllowedForMedium(match, _medium))
        {
            RosettaConstraintMessage = RosettaUiPresets.DisallowedReason(match, _medium) ?? string.Empty;
            NotifyRosettaPresetSelectionProperties();
            return false;
        }

        SelectedRosettaPreset = match;
        return true;
    }

    private RosettaPresetChoice GetPresetChoice(string id)
        => _rosettaPresets.First(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private void OnMediumChangedRosetta()
    {
        if (!RosettaUiPresets.IsPresetAllowedForMedium(_selectedRosettaPreset, _medium))
        {
            string? reason = RosettaUiPresets.DisallowedReason(_selectedRosettaPreset, _medium);
            _selectedRosettaPreset = GetPresetChoice(RosettaUiPresets.HobbyistId);
            OnPropertyChanged(nameof(SelectedRosettaPreset));
            RosettaConstraintMessage = reason ?? string.Empty;
        }
        else
        {
            RosettaConstraintMessage = string.Empty;
        }

        OnPropertyChanged(nameof(CanSelectRosettaHighDensity));
        OnPropertyChanged(nameof(CanSelectRosettaExperimentalBluRay));
        OnPropertyChanged(nameof(CanGenerateRosetta));
        OnPropertyChanged(nameof(RosettaHighDensityToolTip));
        OnPropertyChanged(nameof(RosettaExperimentalBluRayToolTip));
        OnPropertyChanged(nameof(RosettaMediaAdvice));
        OnPropertyChanged(nameof(RosettaMediumAvailabilityText));
        NotifyRosettaPresetSelectionProperties();
        RosettaGenerateCommand?.RaiseCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
        RefreshRosettaPlan();
    }

    private void NotifyRosettaPresetSelectionProperties()
    {
        OnPropertyChanged(nameof(IsRosettaHobbyistSelected));
        OnPropertyChanged(nameof(IsRosettaHighDensitySelected));
        OnPropertyChanged(nameof(IsRosettaExperimentalBluRaySelected));
        OnPropertyChanged(nameof(RosettaPresetMicroscopeSummary));
    }

    /// <summary>
    /// Computes the live page and vault capacity directly from <see cref="RosettaHybridDisc.Plan"/> /
    /// <see cref="RosettaHybridDisc.PlanLayout"/> on DVD-R and <see cref="Lithography.Geometry.PolarPageLayout"/>
    /// (via <see cref="RosettaBook.Build"/>) on CD-R and BD-R.
    /// </summary>
    public void RefreshRosettaPlan()
    {
        if (_rosettaPresets.Count == 0) return;
        OnPropertyChanged(nameof(CanGenerateRosetta));
        RosettaGenerateCommand?.RaiseCanExecuteChanged();

        if (_selectedDisc is null)
        {
            LastRosettaPlan = null;
            RosettaCapacitySummary = "Choose a disc preset or saved calibration to compute layout capacity.";
            RosettaVaultSummary = string.Empty;
            return;
        }

        try
        {
            double pagesInnerRadiusMm = ParseRosettaInnerRadiusMm();
            string title = string.IsNullOrWhiteSpace(_rosettaTitle) ? "Rosetta Archive" : _rosettaTitle.Trim();
            DateOnly date = ParseRosettaDate();
            RosettaPagePreset preset = _selectedRosettaPreset.Preset;

            if (_medium == DiscArtMedium.Dvd)
            {
                RosettaHybridPlan plan;
                bool hasTextFolder = !string.IsNullOrWhiteSpace(_rosettaTextFolder);
                if (hasTextFolder)
                {
                    string previewIso = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-plan-" + Guid.NewGuid().ToString("N")[..8] + ".iso");
                    plan = RosettaHybridDisc.Plan(BuildRosettaRequestCore(previewIso, title, date, pagesInnerRadiusMm, overwrite: true, forPlanOnly: true));
                }
                else
                {
                    plan = RosettaHybridDisc.PlanLayout(
                        _selectedDisc.Parameters,
                        preset,
                        title,
                        date,
                        pagesInnerRadiusMm,
                        _rosettaIncludeRimHeader);
                }

                LastRosettaPlan = plan;
                long freeChars = plan.PagesFree * plan.CharactersPerPage;
                string rimNote = plan.RimInnerRadiusMm is { } rim
                    ? string.Create(CultureInfo.InvariantCulture, $" (rim header from r = {rim:0.###} mm)")
                    : " (no rim header)";
                string sourceNote = hasTextFolder
                    ? string.Create(CultureInfo.InvariantCulture, $"{plan.PagesUsed:N0} of {plan.PageCapacity:N0} pages used ({plan.DocumentCount} docs, {plan.PagesFree:N0} free, ~{freeChars:N0} free chars)")
                    : string.Create(CultureInfo.InvariantCulture, $"{plan.PageCapacity:N0} tiles ({plan.PagesFree:N0} body pages free after {plan.PagesUsed} front-matter pages, ~{freeChars:N0} chars)");
                RosettaCapacitySummary = string.Create(CultureInfo.InvariantCulture,
                    $"Layout capacity ({_selectedRosettaPreset.DisplayName}): {sourceNote}; {plan.Columns} x {plan.BodyRows} chars/page ({plan.CharactersPerPage:N0}), {plan.CellWidthUm:0.##} x {plan.CellHeightUm:0.##} um cells across r = {plan.PagesInnerRadiusMm:0.###} to {plan.PagesOuterRadiusMm:0.###} mm{rimNote}. Computed from PolarPageLayout / RosettaHybridDisc.Plan.");
                RosettaVaultSummary = string.Create(CultureInfo.InvariantCulture,
                    $"Inner vault ceiling: {plan.VaultCeilingBytes / 1048576.0:N1} MB ({plan.VaultCeilingSectors:N0} sectors inside r = {plan.PagesInnerRadiusMm:0.###} mm after 2 MiB gap; index/input: {plan.VaultInputBytes / 1048576.0:N2} MB).");
                return;
            }

            LastRosettaPlan = null;
            CalibrationParameters disc = _selectedDisc.Parameters;
            if (pagesInnerRadiusMm <= disc.InnerRadiusMm || pagesInnerRadiusMm >= disc.OuterRadiusMm)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The pages must start between the disc's inner and outer radius ({disc.InnerRadiusMm:0.###} to {disc.OuterRadiusMm:0.###} mm)."));
            }

            RosettaBook book = RosettaBook.Build(
                Array.Empty<RosettaDocument>(),
                new RosettaArchiveOptions(
                    title,
                    date,
                    preset,
                    InnerRadiusMm: pagesInnerRadiusMm,
                    OuterRadiusMm: disc.OuterRadiusMm,
                    HasInnerFileVault: false,
                    IncludeRimHeader: _rosettaIncludeRimHeader));
            RosettaBand body = book.Bands[^1];
            int charsPerPage = body.Preset.Columns * body.Preset.BodyRows;
            long freePages = book.TotalPhysicalTiles - book.TotalPages;
            double pagesOuter = book.RimHeader is { } rimHeader
                ? rimHeader.InnerRadiusMm - RosettaRimHeader.PageGutterMm
                : disc.OuterRadiusMm;
            string mediumStatus = _medium == DiscArtMedium.Cd
                ? "CD-R layout preview only (hybrid Rosetta builder awaits the CD port)"
                : "BD-R layout preview only (contrast/polarity unmeasured; hybrid Rosetta builder is DVD-R only)";
            RosettaCapacitySummary = string.Create(CultureInfo.InvariantCulture,
                $"{mediumStatus}: {book.TotalPhysicalTiles:N0} tiles ({freePages:N0} body pages free after {book.TotalPages} front-matter pages, ~{freePages * charsPerPage:N0} chars), {body.Preset.Columns} x {body.Preset.BodyRows} chars/page ({charsPerPage:N0}), {preset.CellSize.WidthUm:0.##} x {preset.CellSize.HeightUm:0.##} um cells across r = {pagesInnerRadiusMm:0.###} to {pagesOuter:0.###} mm. Computed from PolarPageLayout.");
            RosettaVaultSummary = _medium == DiscArtMedium.Cd
                ? "CD-R hybrid file vault is not available yet (awaiting CD picture port; minimum cell floor is 50 x 75 um)."
                : "BD-R hybrid file vault is not available yet (DVD-R only for now; BD-R requires coaxial epi-illumination).";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            LastRosettaPlan = null;
            RosettaCapacitySummary = ex.Message;
            RosettaVaultSummary = string.Empty;
        }
    }

    public RosettaHybridRequest BuildRosettaRequest(string? outputPath = null)
    {
        if (_medium != DiscArtMedium.Dvd)
        {
            throw new ArgumentException(
                "A hybrid Rosetta disc is DVD-R only for now. CD-R comes after the CD port, with glyph cells of at least 50 x 75 um.");
        }

        if (_selectedDisc is null) throw new ArgumentException("Choose a disc preset or saved calibration.");
        string output = outputPath ?? _rosettaOutputPath;
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose where to save the Rosetta .iso file.");
        string title = string.IsNullOrWhiteSpace(_rosettaTitle)
            ? throw new ArgumentException("Give the Rosetta archive a title.")
            : _rosettaTitle.Trim();

        return BuildRosettaRequestCore(
            output,
            title,
            ParseRosettaDate(),
            ParseRosettaInnerRadiusMm(),
            overwrite: true,
            forPlanOnly: false);
    }

    private RosettaHybridRequest BuildRosettaRequestCore(
        string outputPath,
        string title,
        DateOnly date,
        double pagesInnerRadiusMm,
        bool overwrite,
        bool forPlanOnly)
    {
        string? par2 = _rosettaRedundancyPercent > 0 && !forPlanOnly ? Par2Runner.FindPar2Executable() : null;
        return new RosettaHybridRequest(
            _rosettaTextFolder,
            _selectedDisc!.Parameters,
            Path.GetFullPath(outputPath),
            title,
            date,
            _selectedRosettaPreset.Preset,
            FilesFolder: string.IsNullOrWhiteSpace(_rosettaFilesFolder) ? null : Path.GetFullPath(_rosettaFilesFolder),
            PagesInnerRadiusMm: pagesInnerRadiusMm,
            RedundancyPercent: _rosettaRedundancyPercent,
            FillWithRecoveryData: _rosettaRedundancyPercent > 0,
            IncludeRimHeader: _rosettaIncludeRimHeader,
            StartAngleDegrees: 0,
            Par2Path: par2,
            Overwrite: overwrite);
    }

    public async Task GenerateRosettaAsync()
    {
        RosettaHybridRequest request;
        try
        {
            request = BuildRosettaRequest();
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
        LastRosettaResult = null;
        StatusMessage = "Generating the hybrid Rosetta DVD-R image...";
        var progress = new Progress<DiscJobProgress>(p =>
        {
            if (p.Fraction is { } f) Progress = Math.Round(f * 100, 1);
            StatusMessage = p.Message;
        });

        try
        {
            RosettaHybridResult result = await RosettaHybridDisc.GenerateAsync(request, progress, token);
            LastRosettaResult = result;
            LastRosettaPlan = result.Plan;
            Progress = 100;
            StatusMessage = $"Rosetta image written: {result.OutputPath} ({result.BytesWritten:N0} bytes, {result.Plan.PagesUsed:N0} of {result.Plan.PageCapacity:N0} pages used).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Rosetta generation was cancelled; no image was left behind.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            StatusMessage = "The Rosetta image could not be made: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<BurnOperationResult> BurnRosettaAsync()
    {
        if (!CanBurn)
        {
            return Refuse("Burning is not available here.");
        }

        if (_lastRosettaResult is not { } result || !File.Exists(result.OutputPath))
        {
            return Refuse("Generate the Rosetta image first.");
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
            StatusMessage = burn.Success ? "Rosetta disc burned." : "Burn did not finish: " + burn.ErrorMessage;
            return burn;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private double ParseRosettaInnerRadiusMm()
    {
        if (string.IsNullOrWhiteSpace(_rosettaPagesInnerRadiusText))
        {
            return RosettaHybridDisc.DefaultPagesInnerRadiusMm;
        }

        if (!double.TryParse(_rosettaPagesInnerRadiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.TryParse(_rosettaPagesInnerRadiusText, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            throw new ArgumentException($"The pages inner radius '{_rosettaPagesInnerRadiusText}' is not a number of millimetres.");
        }

        return value;
    }

    private DateOnly ParseRosettaDate()
    {
        string trimmed = _rosettaDateText.Trim();
        if (trimmed.Length == 0)
        {
            return DateOnly.FromDateTime(DateTime.Now);
        }

        if (!DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            throw new ArgumentException($"Invalid Rosetta archive date '{trimmed}'. Expected yyyy-MM-dd.");
        }

        return parsed;
    }

    private void BrowseRosettaTextFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with UTF-8 .txt / .md files for Rosetta pages" };
        if (dialog.ShowDialog() == true) RosettaTextFolder = dialog.FolderName;
    }

    private void BrowseRosettaFilesFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Optional extra files for the inner Rosetta vault" };
        if (dialog.ShowDialog() == true) RosettaFilesFolder = dialog.FolderName;
    }

    private void BrowseRosettaOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save hybrid Rosetta DVD-R image",
            Filter = "Disc image (*.iso)|*.iso",
            FileName = "rosetta-archive.iso",
        };
        if (dialog.ShowDialog() == true) RosettaOutputPath = dialog.FileName;
    }
}
