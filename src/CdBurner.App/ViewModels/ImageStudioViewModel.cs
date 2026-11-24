using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing Disc Image Studio operations: burning .iso and .cue image archives to disc,
/// ripping optical media to .iso or raw .bin + companion .cue sheets, and running standalone SHA-256/sector verification.
/// </summary>
public class ImageStudioViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DataBurnService _dataBurnService;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly DiscRipperService _ripperService;
    private readonly DiscVerificationService _verificationService;
    private readonly DiscCopyService _copyService;
    private readonly BurnQueueService _burnQueueService;
    private readonly Action? _shutdownInitiator;
    private PostBurnAction _selectedPostBurnAction = PostBurnAction.None;

    private string _sourceImagePath = string.Empty;
    private string _detectedImageFormat = "No Image Loaded";
    private long _imageSizeBytes;
    private int _imageSectorCount;
    private bool _isLegacyImageLoaded;
    private bool _verifyAfterBurn = true;

    private string _ripTargetFilePath = string.Empty;
    private bool _ripRawBinCueMode = false;

    private IDiscRecorderBackend? _copySourceRecorder;
    private IDiscRecorderBackend? _copyTargetRecorder;
    private string _copySourceSummary = string.Empty;
    private int _copyCount = 1;
    private bool _copyRawCloneMode;
    private bool _copyVerifyEachDisc = true;
    private bool _copyKeepStagedImage;
    private DiscCopyResult? _lastCopyResult;

    private readonly DiscImageBrowserService _browserService = new();
    private DiscImageBrowseResult? _lastBrowseResult;
    private DiscImageExtractionResult? _lastExtractionResult;
    private string _imageBrowseSearchText = string.Empty;
    private ImageBrowserNodeViewModel? _selectedImageEntry;
    private string _imageBrowseSummary = "Load an image (.iso, .udf, .nrg, .mds, .ccd, .cdi, .cue/.bin) to browse its files.";
    private string? _extractOutputDirectory;
    private bool _extractOverwriteExisting;

    private double _deviceBufferPercent = 100.0;
    private double _softwareBufferPercent = 100.0;
    private string _currentSpeedDisplay = "Idle";

    private string _verificationStatusText = "Ready to verify";
    private string _calculatedSha256 = string.Empty;
    private string _discSha256 = string.Empty;
    private bool _isChecksumMatch;

    private IDiscRecorderBackend? _selectedRecorder;
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private CancellationTokenSource? _activeCts;
    private bool _isBusy;

    // Progress<T> delivers callbacks asynchronously (on thread-pool threads when there is no UI dispatcher), so a late
    // "Completed (100%)" could overwrite an operation's final status message. Each operation takes a new epoch; the
    // epoch check and the status write happen under one lock, and EndOperationProgress takes the same lock, so once it
    // returns no callback of that operation can still write.
    private int _progressEpoch;
    private readonly object _progressLock = new();
    private string _lastLoggedAction = string.Empty;

    private IProgress<BurnProgressSnapshot> CreateOperationProgress(Action<string> setStatus)
    {
        int epoch;
        lock (_progressLock)
        {
            epoch = ++_progressEpoch;
            _lastLoggedAction = string.Empty;
        }

        return new Progress<BurnProgressSnapshot>(p =>
        {
            lock (_progressLock)
            {
                if (!_isBusy || epoch != _progressEpoch) return;
                if (Telemetry.IsActive)
                {
                    Telemetry.RecordSnapshot(p);
                }
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                DeviceBufferPercent = Math.Clamp(p.DeviceBufferFillPercent ?? (p.BufferLevelPercent > 0 ? p.BufferLevelPercent : 100.0), 0.0, 100.0);
                SoftwareBufferPercent = Math.Clamp(p.HostBufferFillPercent ?? (p.BufferLevelPercent > 0 ? p.BufferLevelPercent : 100.0), 0.0, 100.0);
                if (p.CurrentSpeedMultiplier > 0 || p.CurrentBytesPerSecond > 0)
                {
                    double kbps = p.CurrentBytesPerSecond / 1024.0;
                    CurrentSpeedDisplay = kbps > 0
                        ? $"{p.CurrentSpeedMultiplier:F1}x ({kbps:N0} KB/s)"
                        : $"{p.CurrentSpeedMultiplier:F1}x";
                }
                setStatus($"{p.CurrentActionDescription} ({BurnProgress:F0}%)");
                if (!string.IsNullOrWhiteSpace(p.CurrentActionDescription) &&
                    !string.Equals(_lastLoggedAction, p.CurrentActionDescription, StringComparison.Ordinal))
                {
                    _lastLoggedAction = p.CurrentActionDescription;
                    AppendLog($"{p.CurrentActionDescription} [LBA {p.CurrentLba:N0}/{p.TotalLba:N0}, Buffer {DeviceBufferPercent:F0}%]");
                }
            }
        });
    }

    private void EndOperationProgress()
    {
        lock (_progressLock)
        {
            _progressEpoch++;
        }
    }

    /// <summary>
    /// Appends a timestamped entry to the ImgBurn-style hardware &amp; sector I/O event log.
    /// </summary>
    public void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess() && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                disp.BeginInvoke(() => OperationLogEntries.Add(entry));
                return;
            }
        }
        catch
        {
        }
        OperationLogEntries.Add(entry);
    }
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private int _selectedTabIndex;
    private BurnOperationResult? _lastBurnResult;
    private RipOperationResult? _lastRipResult;
    private VerificationResult? _lastVerificationResult;

    #region Properties

    /// <summary>
    /// Optical device enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical burn engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Disc ripper orchestration service.
    /// </summary>
    public DiscRipperService RipperService => _ripperService;

    /// <summary>
    /// Sector verification service.
    /// </summary>
    public DiscVerificationService VerificationService => _verificationService;

    /// <summary>
    /// File path of the source disc image (.iso, .cue, or .bin) to inspect, burn, or verify.
    /// </summary>
    public string SourceImagePath
    {
        get => _sourceImagePath;
        set
        {
            if (SetProperty(ref _sourceImagePath, value))
            {
                InspectImage();
                BurnImageToDiscCommand.RaiseCanExecuteChanged();
                VerifyDiscAgainstImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Format classification detected from source image file inspection (e.g. "ISO 9660 Image", "CDRWIN CUE Sheet").
    /// </summary>
    public string DetectedImageFormat
    {
        get => _detectedImageFormat;
        set => SetProperty(ref _detectedImageFormat, value);
    }

    /// <summary>
    /// Total byte length of the inspected disc image or referenced binary payload.
    /// </summary>
    public long ImageSizeBytes
    {
        get => _imageSizeBytes;
        set
        {
            if (SetProperty(ref _imageSizeBytes, value))
            {
                OnPropertyChanged(nameof(ImageSizeFormatted));
            }
        }
    }

    /// <summary>
    /// Total logical sector count required by the inspected disc image.
    /// </summary>
    public int ImageSectorCount
    {
        get => _imageSectorCount;
        set => SetProperty(ref _imageSectorCount, value);
    }

    /// <summary>
    /// Whether to automatically execute sector/checksum verification after burning completes.
    /// </summary>
    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    /// <summary>
    /// Burn with IgnisDisc's own drive commands instead of the Windows writer. Off by default, chosen per burn. Covers
    /// blank CD-R, CD-RW, DVD-R, DVD-RW, DVD+R (single and dual layer), DVD+RW, DVD-RAM, and single-layer BD-R and BD-RE so far; other discs are burned by the Windows writer as before. The speed comes from the
    /// drive's own list for the disc, and the disc is always read back afterwards.
    /// </summary>
    public bool UseDirectDriveCommands
    {
        get => _useDirectDriveCommands;
        set
        {
            _directDefault.UserChose();
            if (SetProperty(ref _useDirectDriveCommands, value))
            {
                UpdateCopySourceSummary();
            }
        }
    }

    private bool _useDirectDriveCommands;
    private readonly AutomaticChoice _directDefault = new();

    /// <summary>On by itself for a DVD-RW while the setting allows it, until the user sets the box (as on the Data Disc screen).</summary>
    public void ApplyDirectDefaultForDisc(MediaStatusDto? status, bool enabled)
    {
        if (_directDefault.Decide(_useDirectDriveCommands, enabled && DirectWritingDefault.Prefers(status)) is bool value)
        {
            SetProperty(ref _useDirectDriveCommands, value, nameof(UseDirectDriveCommands));
            UpdateCopySourceSummary();
        }
    }

    /// <summary>Folder for the command logs of direct burns; null for the default under %LOCALAPPDATA%.</summary>
    public string? DirectBurnLogDirectory { get; set; }

    /// <summary>Which writer burned the last disc made with <see cref="UseDirectDriveCommands"/>, and its log.</summary>
    public CdBurner.Hardware.NativeWriting.NativeDataBurnEngine? LastDirectBurnEngine { get; private set; }

    /// <summary>
    /// Target file path where ripped disc images will be saved (.iso or .bin).
    /// </summary>
    public string RipTargetFilePath
    {
        get => _ripTargetFilePath;
        set => SetProperty(ref _ripTargetFilePath, value);
    }

    /// <summary>
    /// When true, rips in raw 2352-byte sector mode (.bin + companion .cue);
    /// when false, rips in standard 2048-byte user data mode (.iso).
    /// </summary>
    public bool RipRawBinCueMode
    {
        get => _ripRawBinCueMode;
        set
        {
            if (SetProperty(ref _ripRawBinCueMode, value))
            {
                if (!string.IsNullOrWhiteSpace(_ripTargetFilePath))
                {
                    _ripTargetFilePath = Path.ChangeExtension(_ripTargetFilePath, value ? ".bin" : ".iso");
                    OnPropertyChanged(nameof(RipTargetFilePath));
                }
            }
        }
    }

    /// <summary>
    /// Status description of the standalone sector/checksum verification scan.
    /// </summary>
    public string VerificationStatusText
    {
        get => _verificationStatusText;
        set => SetProperty(ref _verificationStatusText, value);
    }

    /// <summary>
    /// Expected SHA-256 hexadecimal hash calculated from the source image.
    /// </summary>
    public string CalculatedSha256
    {
        get => _calculatedSha256;
        set => SetProperty(ref _calculatedSha256, value);
    }

    /// <summary>
    /// Actual SHA-256 hexadecimal hash calculated from sectors read back from optical media.
    /// </summary>
    public string DiscSha256
    {
        get => _discSha256;
        set => SetProperty(ref _discSha256, value);
    }

    /// <summary>
    /// Indicates whether the media checksum perfectly matches the source image checksum.
    /// </summary>
    public bool IsChecksumMatch
    {
        get => _isChecksumMatch;
        set => SetProperty(ref _isChecksumMatch, value);
    }

    /// <summary>
    /// Discovered optical recorder drives.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Currently selected optical recorder drive.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                if (_copySourceRecorder == null) CopySourceRecorder = value;
                if (_copyTargetRecorder == null) CopyTargetRecorder = value;
            }
        }
    }

    /// <summary>
    /// Indicates whether the currently inspected file is a legacy third-party disc image (.nrg, .mdf/.mds, .ccd/.img)
    /// that can be converted to a canonical .iso file or burned directly.
    /// </summary>
    public bool IsLegacyImageLoaded
    {
        get => _isLegacyImageLoaded;
        set
        {
            if (SetProperty(ref _isLegacyImageLoaded, value))
            {
                ConvertLegacyImageToIsoCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private string? _legacyConvertOutputPath;

    /// <summary>
    /// Explicit target output path (.iso or .bin/.cue) for legacy and image format conversions.
    /// If null or omitted, prompts via file dialog in interactive sessions or uses default converted extension.
    /// </summary>
    public string? LegacyConvertOutputPath
    {
        get => _legacyConvertOutputPath;
        set => SetProperty(ref _legacyConvertOutputPath, value);
    }

    /// <summary>
    /// Source optical drive for 1-Click Disc Copy operations.
    /// </summary>
    public IDiscRecorderBackend? CopySourceRecorder
    {
        get => _copySourceRecorder;
        set
        {
            var previous = _copySourceRecorder;
            if (SetProperty(ref _copySourceRecorder, value))
            {
                if (previous is VirtualDiscRecorder prevVirtual)
                {
                    prevVirtual.MediaChanged -= OnCopySourceMediaChanged;
                }
                if (value is VirtualDiscRecorder nextVirtual)
                {
                    nextVirtual.MediaChanged += OnCopySourceMediaChanged;
                }
                UpdateCopySourceSummary();
            }
        }
    }

    /// <summary>Plain-language explanation of what 1-Click Disc Copy will copy from the selected source drive.</summary>
    public string CopySourceSummary
    {
        get => _copySourceSummary;
        private set => SetProperty(ref _copySourceSummary, value);
    }

    /// <summary>Refreshes <see cref="CopySourceSummary"/> by inspecting the disc currently in <see cref="CopySourceRecorder"/>.</summary>
    public void UpdateCopySourceSummary()
    {
        CopySourceSummary = DiscCopyService.DescribeCopyPlan(CopySourceRecorder, UseDirectDriveCommands).SummaryText;
    }

    private void OnCopySourceMediaChanged(object? sender, EventArgs e) => UpdateCopySourceSummary();

    /// <summary>
    /// Target optical drive for 1-Click Disc Copy operations.
    /// </summary>
    public IDiscRecorderBackend? CopyTargetRecorder
    {
        get => _copyTargetRecorder;
        set => SetProperty(ref _copyTargetRecorder, value);
    }

    /// <summary>
    /// Number of sequential disc copies to produce in a batch (1..99).
    /// </summary>
    public int CopyCount
    {
        get => _copyCount;
        set => SetProperty(ref _copyCount, Math.Clamp(value, 1, 99));
    }

    /// <summary>
    /// When true, Disc Copy reads and writes raw 2,352-byte sectors (.bin + .cue).
    /// </summary>
    public bool CopyRawCloneMode
    {
        get => _copyRawCloneMode;
        set => SetProperty(ref _copyRawCloneMode, value);
    }

    /// <summary>
    /// When true, Disc Copy verifies each burned copy bit-for-bit against the staged image.
    /// </summary>
    public bool CopyVerifyEachDisc
    {
        get => _copyVerifyEachDisc;
        set => SetProperty(ref _copyVerifyEachDisc, value);
    }

    /// <summary>
    /// When true, Disc Copy keeps the temporary staged image file on disk after completion.
    /// </summary>
    public bool CopyKeepStagedImage
    {
        get => _copyKeepStagedImage;
        set => SetProperty(ref _copyKeepStagedImage, value);
    }

    /// <summary>
    /// Result of the most recent 1-Click Disc Copy operation.
    /// </summary>
    public DiscCopyResult? LastCopyResult
    {
        get => _lastCopyResult;
        set => SetProperty(ref _lastCopyResult, value);
    }

    /// <summary>Live burn telemetry recorder and SVG/JSON plot exporter.</summary>
    public BurnTelemetryMonitor Telemetry { get; } = new();

    /// <summary>
    /// Hardware drive buffer fill level percentage (0.0 .. 100.0).
    /// </summary>
    public double DeviceBufferPercent
    {
        get => _deviceBufferPercent;
        set => SetProperty(ref _deviceBufferPercent, value);
    }

    /// <summary>
    /// Host software ring-buffer fill level percentage (0.0 .. 100.0).
    /// </summary>
    public double SoftwareBufferPercent
    {
        get => _softwareBufferPercent;
        set => SetProperty(ref _softwareBufferPercent, value);
    }

    /// <summary>
    /// Formatted real-time write/read speed telemetry string (e.g. "16.0x (22,160 KB/s)").
    /// </summary>
    public string CurrentSpeedDisplay
    {
        get => _currentSpeedDisplay;
        set => SetProperty(ref _currentSpeedDisplay, value);
    }

    /// <summary>
    /// Timestamped hardware, SCSI, and sector I/O event log entries (ImgBurn-style telemetry drawer).
    /// </summary>
    public ObservableCollection<string> OperationLogEntries { get; } = new();

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set => SetProperty(ref _selectedWriteSpeed, value);
    }

    /// <summary>
    /// Indicates whether an active operation can be cancelled.
    /// </summary>
    public bool CanCancel => _isBusy;

    /// <summary>
    /// Indicates whether an asynchronous operation is in progress.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                BurnImageToDiscCommand.RaiseCanExecuteChanged();
                RipDiscToImageCommand.RaiseCanExecuteChanged();
                VerifyDiscAgainstImageCommand.RaiseCanExecuteChanged();
                ConvertLegacyImageToIsoCommand?.RaiseCanExecuteChanged();
                CopyDiscCommand?.RaiseCanExecuteChanged();
                RunBurnQueueCommand?.RaiseCanExecuteChanged();
                ExtractSelectedCommand?.RaiseCanExecuteChanged();
                ExtractAllCommand?.RaiseCanExecuteChanged();
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Real-time status text displayed on the UI.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Real-time progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Active sub-tab index in Disc Image Studio (0 = Burn Disc Image, 1 = Rip Disc to Image,
    /// 2 = Standalone Verifier, 3 = 1-Click Disc Copy, 4 = Burn Queue).
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    /// <summary>
    /// Formatted image size string in megabytes or gigabytes.
    /// </summary>
    public string ImageSizeFormatted
    {
        get
        {
            if (_imageSizeBytes <= 0) return "0 MB";
            double mb = _imageSizeBytes / (1024.0 * 1024.0);
            if (mb >= 1024)
            {
                return $"{(mb / 1024.0):F2} GB ({_imageSizeBytes:N0} bytes)";
            }
            return $"{mb:F1} MB ({_imageSizeBytes:N0} bytes)";
        }
    }

    /// <summary>
    /// Result of the most recent burn operation.
    /// </summary>
    public BurnOperationResult? LastBurnResult
    {
        get => _lastBurnResult;
        set => SetProperty(ref _lastBurnResult, value);
    }

    /// <summary>
    /// Result of the most recent rip operation.
    /// </summary>
    public RipOperationResult? LastRipResult
    {
        get => _lastRipResult;
        set => SetProperty(ref _lastRipResult, value);
    }

    /// <summary>
    /// Result of the most recent disc verification operation.
    /// </summary>
    /// <summary>
    /// Sequential burn queue items scheduled for batch burning.
    /// </summary>
    public ObservableCollection<BurnQueueItem> BurnQueueItems { get; } = new();

    /// <summary>
    /// Action to execute automatically after a burn or queue completes.
    /// </summary>
    public PostBurnAction SelectedPostBurnAction
    {
        get => _selectedPostBurnAction;
        set => SetProperty(ref _selectedPostBurnAction, value);
    }

    /// <summary>
    /// Available after-burn action options.
    /// </summary>
    public IReadOnlyList<PostBurnActionOption> AvailablePostBurnActions { get; } = Enum.GetValues<PostBurnAction>()
        .Select(a => new PostBurnActionOption(a, PostBurnActionExecutor.GetDisplayName(a)))
        .ToList();

    public VerificationResult? LastVerificationResult
    {
        get => _lastVerificationResult;
        set => SetProperty(ref _lastVerificationResult, value);
    }

    /// <summary>
    /// Root tree nodes displayed in the Browse &amp; Extract tab (filtered by <see cref="ImageBrowseSearchText"/>).
    /// </summary>
    public ObservableCollection<ImageBrowserNodeViewModel> ImageTreeRoots { get; } = new();

    /// <summary>
    /// Live search substring used to filter entries in the disc image content tree.
    /// </summary>
    public string ImageBrowseSearchText
    {
        get => _imageBrowseSearchText;
        set
        {
            if (SetProperty(ref _imageBrowseSearchText, value ?? string.Empty))
            {
                ApplyImageBrowseFilter();
            }
        }
    }

    /// <summary>
    /// Currently highlighted node in the disc image content tree.
    /// </summary>
    public ImageBrowserNodeViewModel? SelectedImageEntry
    {
        get => _selectedImageEntry;
        set
        {
            if (SetProperty(ref _selectedImageEntry, value))
            {
                OnPropertyChanged(nameof(SelectedImageEntryDetails));
                ExtractSelectedCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Formatted metadata summary for the currently selected node in the disc image tree.
    /// </summary>
    public string SelectedImageEntryDetails => SelectedImageEntry == null
        ? "Select a file or folder in the tree, or check items to extract."
        : $"{SelectedImageEntry.VirtualPath}  |  {SelectedImageEntry.SizeDisplay}  |  {SelectedImageEntry.TimestampDisplay}  |  {SelectedImageEntry.FlagsSummary}  |  {SelectedImageEntry.StartLbaDisplay}  |  {SelectedImageEntry.FileSystemSource}";

    /// <summary>
    /// Summary banner describing the active filesystem and entry counts of the browsed image.
    /// </summary>
    public string ImageBrowseSummary
    {
        get => _imageBrowseSummary;
        private set => SetProperty(ref _imageBrowseSummary, value);
    }

    /// <summary>
    /// Result of the most recent content browse operation.
    /// </summary>
    public DiscImageBrowseResult? LastBrowseResult
    {
        get => _lastBrowseResult;
        private set
        {
            if (SetProperty(ref _lastBrowseResult, value))
            {
                ExtractAllCommand?.RaiseCanExecuteChanged();
                ExtractSelectedCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Result of the most recent file extraction operation.
    /// </summary>
    public DiscImageExtractionResult? LastExtractionResult
    {
        get => _lastExtractionResult;
        private set => SetProperty(ref _lastExtractionResult, value);
    }

    /// <summary>
    /// Optional explicit output folder for extraction (used by automated callers or when bypassing the folder picker).
    /// </summary>
    public string? ExtractOutputDirectory
    {
        get => _extractOutputDirectory;
        set => SetProperty(ref _extractOutputDirectory, value);
    }

    /// <summary>
    /// Whether extraction should overwrite existing files in the destination folder.
    /// </summary>
    public bool ExtractOverwriteExisting
    {
        get => _extractOverwriteExisting;
        set => SetProperty(ref _extractOverwriteExisting, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Opens a file dialog to browse for a source image (.iso, .cue, or .bin).
    /// </summary>
    public RelayCommand BrowseImageCommand { get; }

    /// <summary>
    /// Inspects the current source image file and extracts format headers, size, and sector count.
    /// </summary>
    public RelayCommand InspectImageCommand { get; }

    /// <summary>
    /// Burns the selected source image file to optical disc.
    /// </summary>
    public AsyncRelayCommand BurnImageToDiscCommand { get; }

    /// <summary>
    /// Rips the inserted optical disc to an ISO or BIN/CUE disc image file.
    /// </summary>
    public AsyncRelayCommand RipDiscToImageCommand { get; }

    /// <summary>
    /// Compares sectors on the inserted optical disc bit-for-bit against the source image.
    /// </summary>
    public AsyncRelayCommand VerifyDiscAgainstImageCommand { get; }

    /// <summary>
    /// Opens a save file dialog to select the destination path for ripped disc images.
    /// </summary>
    public RelayCommand BrowseRipTargetFileCommand { get; }

    /// <summary>
    /// Cancels any active image burn, rip, or verification operation.
    /// </summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Converts a loaded legacy image (.nrg, .mdf/.mds, .ccd/.img) to a standard 2,048-byte Mode 1 .iso file.
    /// </summary>
    public AsyncRelayCommand ConvertLegacyImageToIsoCommand { get; }

    /// <summary>
    /// Executes a 1-Click Disc-to-Disc Copy (single-drive swap or drive-to-drive) with optional multi-copy batching.
    /// </summary>
    /// <summary>
    /// Adds an image to the sequential burn queue.
    /// </summary>
    public RelayCommand AddToBurnQueueCommand { get; }

    /// <summary>
    /// Removes an item from the sequential burn queue.
    /// </summary>
    public RelayCommand RemoveFromBurnQueueCommand { get; }

    /// <summary>
    /// Clears all items from the sequential burn queue.
    /// </summary>
    public RelayCommand ClearBurnQueueCommand { get; }

    /// <summary>
    /// Executes all items in the sequential burn queue on the selected drive.
    /// </summary>
    public AsyncRelayCommand RunBurnQueueCommand { get; }

    public AsyncRelayCommand CopyDiscCommand { get; }

    /// <summary>Re-inspects the disc in <see cref="CopySourceRecorder"/> and updates <see cref="CopySourceSummary"/>.</summary>
    public RelayCommand RefreshCopySourceCommand { get; }

    /// <summary>
    /// Clears all entries in the hardware/sector event log drawer.
    /// </summary>
    public RelayCommand ClearOperationLogCommand { get; }

    /// <summary>
    /// Reloads the directory tree from the current <see cref="SourceImagePath"/>.
    /// </summary>
    public RelayCommand LoadImageBrowserCommand { get; }

    /// <summary>
    /// Clears the directory tree search filter.
    /// </summary>
    public RelayCommand ClearImageSearchCommand { get; }

    /// <summary>
    /// Extracts checked or selected entries from the loaded disc image.
    /// </summary>
    public AsyncRelayCommand ExtractSelectedCommand { get; }

    /// <summary>
    /// Extracts all files and folders from the loaded disc image.
    /// </summary>
    public AsyncRelayCommand ExtractAllCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="ImageStudioViewModel"/>.
    /// </summary>
    public ImageStudioViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null,
        Action? shutdownInitiator = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _shutdownInitiator = shutdownInitiator;
        _dataBurnService = new DataBurnService(_burnEngine);
        _audioBurnService = new AudioCdBurnService(_burnEngine, new MediaFoundationAudioDecoder());
        _ripperService = new DiscRipperService();
        _verificationService = new DiscVerificationService();
        _copyService = new DiscCopyService(_burnEngine, _audioBurnService.AudioDecoder);
        _burnQueueService = new BurnQueueService(_burnEngine, _dataBurnService);

        try
        {
            _ripTargetFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DiscImage.iso");
        }
        catch
        {
            _ripTargetFilePath = Path.Combine(Path.GetTempPath(), "DiscImage.iso");
        }

        BrowseImageCommand = new RelayCommand(BrowseImage);
        InspectImageCommand = new RelayCommand(() => InspectImage());
        BurnImageToDiscCommand = new AsyncRelayCommand(async p => await BurnImageToDiscAsync(p as string), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceImagePath));
        RipDiscToImageCommand = new AsyncRelayCommand(async p => await RipDiscToImageAsync(p as string), _ => !IsBusy);
        VerifyDiscAgainstImageCommand = new AsyncRelayCommand(async p => await VerifyDiscAgainstImageAsync(p as string), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceImagePath));
        BrowseRipTargetFileCommand = new RelayCommand(BrowseRipTargetFile);
        ConvertLegacyImageToIsoCommand = new AsyncRelayCommand(async p => await ConvertLegacyImageToIsoAsync(p as string), _ => !IsBusy && IsLegacyImageLoaded);
        CopyDiscCommand = new AsyncRelayCommand(async _ => await CopyDiscAsync(), _ => !IsBusy);
        RefreshCopySourceCommand = new RelayCommand(_ => UpdateCopySourceSummary());
        AddToBurnQueueCommand = new RelayCommand(p => AddToBurnQueue(p));
        RemoveFromBurnQueueCommand = new RelayCommand(p => RemoveFromBurnQueue(p));
        ClearBurnQueueCommand = new RelayCommand(() =>
        {
            BurnQueueItems.Clear();
            RunBurnQueueCommand?.RaiseCanExecuteChanged();
        });
        RunBurnQueueCommand = new AsyncRelayCommand(
            async p => await RunBurnQueueAsync(p as string),
            _ => !IsBusy && BurnQueueItems.Count > 0);
        BurnQueueItems.CollectionChanged += (_, _) => RunBurnQueueCommand.RaiseCanExecuteChanged();
        ClearOperationLogCommand = new RelayCommand(() => OperationLogEntries.Clear());
        LoadImageBrowserCommand = new RelayCommand(() => RefreshImageBrowserTree(logWarnings: true));
        ClearImageSearchCommand = new RelayCommand(() => ImageBrowseSearchText = string.Empty);
        ExtractSelectedCommand = new AsyncRelayCommand(
            async p => await ExtractSelectedEntriesAsync(p as string),
            _ => !IsBusy && LastBrowseResult != null && (SelectedImageEntry != null || HasAnyCheckedBrowserNode()));
        ExtractAllCommand = new AsyncRelayCommand(
            async p => await ExtractAllEntriesAsync(p as string),
            _ => !IsBusy && LastBrowseResult != null && (LastBrowseResult.FileCount > 0 || LastBrowseResult.DirectoryCount > 0));
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => IsBusy);

        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        // Pre-select physical optical recorder if available
        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();
        CopySourceRecorder = SelectedRecorder;
        CopyTargetRecorder = SelectedRecorder;
        UpdateCopySourceSummary();
        _deviceService.DeviceChanged += (_, e) =>
        {
            if (string.Equals(e.UniqueId, CopySourceRecorder?.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                UpdateCopySourceSummary();
            }
        };
        AppendLog("Disc Image Studio initialized. Ready for ISO/CUE/NRG/MDF/CCD mastering & 1-Click Disc Copy.");
    }

    #region Image Inspection Operations

    /// <summary>
    /// Inspects the source image file and updates format, byte size, and sector metrics.
    /// </summary>
    public void InspectImage(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _sourceImagePath = path;
            OnPropertyChanged(nameof(SourceImagePath));
        }

        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            DetectedImageFormat = "No Image Loaded";
            ImageSizeBytes = 0;
            ImageSectorCount = 0;
            IsLegacyImageLoaded = false;
            ClearImageBrowserTree();
            return;
        }

        string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
        IsLegacyImageLoaded = LegacyDiscImageConverter.IsLegacyImageFile(SourceImagePath) || ext is ".iso" or ".cue";
        if (ext is ".iso" or ".udf")
        {
            var fi = new FileInfo(SourceImagePath);
            ImageSizeBytes = fi.Length;
            ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.DataSectorSize);
            DetectedImageFormat = ext == ".udf" ? "UDF Disc Image" : "ISO 9660 Image";

            if (ext == ".iso")
            {
                try
                {
                    using var reader = IsoImageReader.FromFile(SourceImagePath);
                    if (reader.HasJoliet)
                    {
                        DetectedImageFormat = "ISO 9660 / Joliet Image";
                    }
                }
                catch
                {
                    // Fallback to standard ISO 9660 or pure UDF
                }
            }
        }
        else if (ext == ".cue")
        {
            DetectedImageFormat = "CDRWIN CUE Sheet";
            try
            {
                var cueSheet = CueSheetParser.ParseFile(SourceImagePath);
                string cueDir = Path.GetDirectoryName(SourceImagePath) ?? "";
                long totalBytes = 0;
                int totalSectors = 0;

                foreach (var file in cueSheet.Files)
                {
                    string refPath = Path.IsPathRooted(file.FileName) ? file.FileName : Path.Combine(cueDir, file.FileName);
                    if (File.Exists(refPath))
                    {
                        var fi = new FileInfo(refPath);
                        bool isAudioTrack = file.Tracks.Any(t => t.DataType == CueTrackDataType.Audio);
                        string refExt = Path.GetExtension(refPath);
                        bool isCompressedOrWaveAudio = isAudioTrack && !string.Equals(refExt, ".bin", StringComparison.OrdinalIgnoreCase);

                        if (isCompressedOrWaveAudio)
                        {
                            try
                            {
                                var meta = _audioBurnService.AudioDecoder.ProbeAudioFile(refPath);
                                if (meta.TotalRedBookBytes > 0)
                                {
                                    totalBytes += meta.TotalRedBookBytes;
                                    totalSectors += OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize);
                                    continue;
                                }
                            }
                            catch
                            {
                            }
                        }

                        totalBytes += fi.Length;
                        bool isRaw = file.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352 || t.DataType == CueTrackDataType.Audio);
                        int secSize = isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
                        totalSectors += OpticalConstants.BytesToSectors(fi.Length, secSize);
                    }
                }

                if (totalBytes == 0)
                {
                    var fi = new FileInfo(SourceImagePath);
                    totalBytes = fi.Length;
                }

                if (totalSectors == 0)
                {
                    int maxLba = 0;
                    foreach (var f in cueSheet.Files)
                    {
                        foreach (var t in f.Tracks)
                        {
                            foreach (var idx in t.Indices)
                            {
                                // INDEX positions are frame offsets within the file (no 150-frame lead-in).
                                if (idx.Position.ToFrameCount() > maxLba) maxLba = idx.Position.ToFrameCount();
                            }
                        }
                    }
                    totalSectors = maxLba > 0 ? maxLba : OpticalConstants.BytesToSectors(totalBytes, OpticalConstants.RawSectorSize);
                }

                ImageSizeBytes = totalBytes;
                ImageSectorCount = totalSectors;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to parse CUE sheet: {ex.Message}";
            }
        }
        else if (LegacyDiscImageConverter.IsLegacyImageFile(SourceImagePath))
        {
            try
            {
                var legacyInfo = LegacyDiscImageConverter.Inspect(SourceImagePath);
                DetectedImageFormat = legacyInfo.FormatName;
                ImageSizeBytes = legacyInfo.PayloadLengthBytes;
                ImageSectorCount = legacyInfo.SectorCount;
                AppendLog($"Inspected legacy image '{Path.GetFileName(SourceImagePath)}': {legacyInfo.FormatName}, {legacyInfo.SectorCount:N0} sectors ({legacyInfo.SectorSizeBytes} B/sector).");
            }
            catch (Exception ex)
            {
                var fi = new FileInfo(SourceImagePath);
                ImageSizeBytes = fi.Length;
                ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.DataSectorSize);
                DetectedImageFormat = "Legacy Disc Image";
                StatusMessage = $"Failed to inspect legacy image header: {ex.Message}";
            }
        }
        else
        {
            var fi = new FileInfo(SourceImagePath);
            ImageSizeBytes = fi.Length;
            ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.DataSectorSize);
            DetectedImageFormat = "Generic Disc Image";
        }

        OnPropertyChanged(nameof(ImageSizeFormatted));
        RefreshImageBrowserTree(logWarnings: false);
    }

    /// <summary>
    /// Opens an image file in Disc Image Studio and logs its filesystem directory/file listing or track layout
    /// so users launching with <c>--browse-image</c> can inspect the contents immediately.
    /// </summary>
    public void BrowseImageContents(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SelectedTabIndex = 0;
        SourceImagePath = path;

        if (!File.Exists(path))
        {
            StatusMessage = $"Image file not found: '{path}'.";
            AppendLog(StatusMessage);
            return;
        }

        if (LastBrowseResult != null)
        {
            var result = LastBrowseResult;
            AppendLog($"Browsing {result.FormatName} '{Path.GetFileName(path)}' (Label: '{result.VolumeLabel}', FS: {result.ActiveFileSystem}, ISO9660={result.HasIso9660}, Joliet={result.HasJoliet}, UDF={result.HasUdf}): {result.DirectoryCount} folder(s), {result.FileCount} file(s).");
            const int maxLoggedEntries = 200;
            int logged = 0;
            foreach (var entry in result.Entries)
            {
                if (logged++ >= maxLoggedEntries) break;
                if (entry.IsDirectory)
                {
                    AppendLog($"  [DIR]  {entry.VirtualPath}");
                }
                else
                {
                    AppendLog($"  [FILE] {entry.VirtualPath} ({entry.SizeBytes:N0} B)");
                }
            }
            if (result.Entries.Count > maxLoggedEntries)
            {
                AppendLog($"  ... and {result.Entries.Count - maxLoggedEntries} more entries.");
            }
            StatusMessage = $"Browsing '{Path.GetFileName(path)}': {result.FileCount} file(s), {result.DirectoryCount} folder(s).";
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cue")
        {
            try
            {
                var cue = CueSheetParser.ParseFile(path);
                int trackCount = cue.Files.Sum(f => f.Tracks.Count);
                AppendLog($"Browsing CUE sheet '{Path.GetFileName(path)}': {cue.Files.Count} file(s), {trackCount} track(s).");
                foreach (var file in cue.Files)
                {
                    foreach (var track in file.Tracks)
                    {
                        AppendLog($"  [TRACK {track.TrackNumber:D2}] {track.DataType} ({file.FileName})");
                    }
                }
                StatusMessage = $"Browsing '{Path.GetFileName(path)}': {trackCount} track(s).";
                return;
            }
            catch (Exception ex)
            {
                AppendLog($"Could not parse CUE sheet '{Path.GetFileName(path)}': {ex.Message}");
            }
        }
        else if (LegacyDiscImageConverter.IsLegacyImageFile(path))
        {
            try
            {
                var info = LegacyDiscImageConverter.Inspect(path);
                AppendLog($"Browsing {info.FormatName} '{Path.GetFileName(path)}': {info.TrackCount} track(s), {info.SectorCount:N0} sectors ({info.SectorSizeBytes} B/sector).");
                StatusMessage = $"Browsing '{Path.GetFileName(path)}': {info.FormatName}, {info.TrackCount} track(s).";
                return;
            }
            catch (Exception ex)
            {
                AppendLog($"Could not inspect image '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        StatusMessage = $"Loaded image '{Path.GetFileName(path)}' ({DetectedImageFormat}).";
    }

    private void BrowseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Disc Image to Open",
            Filter = "All Supported Disc Images (*.iso;*.udf;*.cue;*.bin;*.nrg;*.cdi;*.mdf;*.mds;*.ccd;*.img)|*.iso;*.udf;*.cue;*.bin;*.nrg;*.cdi;*.mdf;*.mds;*.ccd;*.img|ISO & UDF Images (*.iso;*.udf)|*.iso;*.udf|CUE Sheets (*.cue)|*.cue|Legacy Images (*.nrg;*.cdi;*.mdf;*.mds;*.ccd;*.img)|*.nrg;*.cdi;*.mdf;*.mds;*.ccd;*.img|Raw Binary Images (*.bin)|*.bin|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            SourceImagePath = dlg.FileName;
        }
    }

    /// <summary>
    /// Converts a loaded disc image (.iso, .cue, .nrg, .mdf/.mds, .ccd/.img) to standard ISO (.iso) or CDRWIN (.bin + .cue).
    /// </summary>
    public async Task<string?> ConvertLegacyImageAsync(string? outputPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            StatusMessage = Strings.StatusImageStudioSourceDiscImageFileDoesNot;
            return null;
        }

        string sourceExt = Path.GetExtension(SourceImagePath).ToLowerInvariant();
        string? effectiveOutputPath = outputPath ?? LegacyConvertOutputPath;

        if (string.IsNullOrWhiteSpace(effectiveOutputPath))
        {
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var sfd = new SaveFileDialog();
                if (sourceExt == ".cue")
                {
                    sfd.Title = "Convert CUE Sheet to Standard ISO";
                    sfd.Filter = "Standard ISO 9660 Image (*.iso)|*.iso";
                    sfd.FileName = Path.ChangeExtension(Path.GetFileName(SourceImagePath), ".iso");
                }
                else if (sourceExt == ".iso")
                {
                    sfd.Title = "Convert ISO to CDRWIN BIN/CUE";
                    sfd.Filter = "CDRWIN Binary Image (*.bin)|*.bin|Companion CUE Sheet (*.cue)|*.cue";
                    sfd.FileName = Path.ChangeExtension(Path.GetFileName(SourceImagePath), ".bin");
                }
                else
                {
                    sfd.Title = "Convert Disc Image to ISO or BIN/CUE";
                    sfd.Filter = "Standard ISO 9660 Image (*.iso)|*.iso|CDRWIN Binary Image (*.bin)|*.bin|Companion CUE Sheet (*.cue)|*.cue";
                    sfd.FileName = Path.ChangeExtension(Path.GetFileName(SourceImagePath), ".iso");
                }

                if (sfd.ShowDialog() != true || string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    return null;
                }
                effectiveOutputPath = sfd.FileName;
            }
            else
            {
                effectiveOutputPath = sourceExt == ".iso"
                    ? Path.ChangeExtension(SourceImagePath, ".converted.bin")
                    : Path.ChangeExtension(SourceImagePath, ".converted.iso");
            }
        }

        string targetExt = Path.GetExtension(effectiveOutputPath).ToLowerInvariant();
        bool targetIsBinCue = targetExt is ".bin" or ".cue";

        IsBusy = true;
        BurnProgress = 0;
        AppendLog($"Converting image '{Path.GetFileName(SourceImagePath)}' -> '{Path.GetFileName(effectiveOutputPath)}'...");

        try
        {
            var progress = CreateOperationProgress(text => StatusMessage = text);

            if (targetIsBinCue)
            {
                string outputBinPath = targetExt == ".cue"
                    ? Path.ChangeExtension(effectiveOutputPath, ".bin")
                    : effectiveOutputPath;
                string outputCuePath = targetExt == ".bin"
                    ? Path.ChangeExtension(effectiveOutputPath, ".cue")
                    : effectiveOutputPath;

                StatusMessage = $"Converting '{Path.GetFileName(SourceImagePath)}' to BIN/CUE...";
                var doubleProgress = new Progress<double>(fraction =>
                {
                    BurnProgress = Math.Min(100.0, fraction * 100.0);
                });

                int sectors = await LegacyDiscImageConverter.ConvertImageToBinCueAsync(
                    SourceImagePath,
                    outputBinPath,
                    outputCuePath,
                    doubleProgress,
                    ct);

                EndOperationProgress();
                BurnProgress = 100.0;
                StatusMessage = $"Converted to '{Path.GetFileName(outputBinPath)}' and '{Path.GetFileName(outputCuePath)}' ({sectors:N0} sectors).";
                AppendLog($"Conversion complete: {sectors:N0} sectors written to '{outputBinPath}' and companion '{outputCuePath}'.");
                return effectiveOutputPath;
            }
            else
            {
                StatusMessage = $"Converting '{Path.GetFileName(SourceImagePath)}' to standard ISO...";
                if (sourceExt == ".cue")
                {
                    var doubleProgress = new Progress<double>(fraction =>
                    {
                        BurnProgress = Math.Min(100.0, fraction * 100.0);
                    });

                    int sectors = await LegacyDiscImageConverter.ConvertBinCueToIsoAsync(
                        SourceImagePath,
                        effectiveOutputPath,
                        doubleProgress,
                        ct);

                    EndOperationProgress();
                    BurnProgress = 100.0;
                    StatusMessage = $"Converted to '{Path.GetFileName(effectiveOutputPath)}' ({sectors:N0} Mode 1 sectors).";
                    AppendLog($"Conversion complete: {sectors:N0} sectors written to '{effectiveOutputPath}'.");
                    return effectiveOutputPath;
                }
                else
                {
                    int sectors = await LegacyDiscImageConverter.ConvertToIsoAsync(
                        SourceImagePath,
                        effectiveOutputPath,
                        progress,
                        ct);

                    EndOperationProgress();
                    BurnProgress = 100.0;
                    StatusMessage = $"Converted to '{Path.GetFileName(effectiveOutputPath)}' ({sectors:N0} Mode 1 sectors).";
                    AppendLog($"Conversion complete: {sectors:N0} sectors written to '{effectiveOutputPath}'.");
                    return effectiveOutputPath;
                }
            }
        }
        catch (Exception ex)
        {
            EndOperationProgress();
            StatusMessage = $"Conversion failed: {ex.Message}";
            AppendLog($"Conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Converts a loaded legacy image (.nrg, .mdf/.mds, .ccd/.img) or CUE sheet to a standard 2,048-byte Mode 1 .iso file.
    /// </summary>
    public Task<string?> ConvertLegacyImageToIsoAsync(string? outputIsoPath = null, CancellationToken ct = default) =>
        ConvertLegacyImageAsync(outputIsoPath, ct);

    private void BrowseRipTargetFile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Select Destination Path for Ripped Disc Image",
            Filter = RipRawBinCueMode
                ? "Raw Disc Image (*.bin)|*.bin|All Files (*.*)|*.*"
                : "Standard ISO Image (*.iso)|*.iso|All Files (*.*)|*.*",
            FileName = Path.GetFileName(RipTargetFilePath)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            RipTargetFilePath = dlg.FileName;
        }
    }

    #endregion

    #region Burn Queue Operations

    /// <summary>
    /// Adds an image file or explicit queue item to the sequential burn queue.
    /// </summary>
    public void AddToBurnQueue(object? parameter = null)
    {
        if (parameter is BurnQueueItem directItem)
        {
            BurnQueueItems.Add(directItem);
            AppendLog($"Added '{Path.GetFileName(directItem.ImagePath)}' to burn queue.");
            return;
        }

        string? path = parameter as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = SourceImagePath;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select Disc Image to Queue",
                    Filter = "Disc Images (*.iso;*.nrg;*.cdi;*.mdf;*.ccd;*.bin)|*.iso;*.nrg;*.cdi;*.mdf;*.ccd;*.bin|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    path = dlg.FileName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var item = new BurnQueueItem(
                ImagePath: path,
                WriteSpeedMultiplier: SelectedWriteSpeed?.Multiplier,
                VerifyAfterBurn: VerifyAfterBurn,
                CloseDisc: true);

            BurnQueueItems.Add(item);
            AppendLog($"Queued '{Path.GetFileName(path)}' (Queue length: {BurnQueueItems.Count}).");
        }
    }

    /// <summary>
    /// Removes a specified item (or the last item) from the burn queue.
    /// </summary>
    public void RemoveFromBurnQueue(object? parameter = null)
    {
        if (parameter is BurnQueueItem item && BurnQueueItems.Contains(item))
        {
            BurnQueueItems.Remove(item);
            AppendLog($"Removed '{Path.GetFileName(item.ImagePath)}' from burn queue.");
        }
        else if (BurnQueueItems.Count > 0)
        {
            var last = BurnQueueItems[^1];
            BurnQueueItems.RemoveAt(BurnQueueItems.Count - 1);
            AppendLog($"Removed '{Path.GetFileName(last.ImagePath)}' from burn queue.");
        }
    }

    /// <summary>
    /// Sequentially executes all items in the burn queue.
    /// </summary>
    public async Task<BurnQueueResult> RunBurnQueueAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (BurnQueueItems.Count == 0)
        {
            StatusMessage = Strings.StatusImageStudioBurnQueueIsEmpty;
            return new BurnQueueResult { Success = false, ErrorMessage = StatusMessage };
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = Strings.StatusImageStudioNoOpticalRecorderDriveSelectedOr;
            return new BurnQueueResult { Success = false, ErrorMessage = StatusMessage };
        }

        // Auto-mount blank media in virtual drive if empty
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(CreateBlankVirtualMediumFor(new MediaStatusDto { UsedSectors = 2000 }));
            }
        }

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Starting burn queue ({BurnQueueItems.Count} items) on {recorder.VolumePath} ({recorder.ProductId})...";
        AppendLog($"Burn queue started: {BurnQueueItems.Count} items on {recorder.UniqueId}.");

        try
        {
            var progress = CreateOperationProgress(text => StatusMessage = text);

            Func<int, BurnQueueItem, CancellationToken, Task<bool>>? promptCallback = null;
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                promptCallback = async (index, nextItem, cancellationToken) =>
                {
                    AppendLog($"Prompting for disc swap before queue item #{index + 1} ('{Path.GetFileName(nextItem.ImagePath)}')...");
                    var res = await ShowMessageOnUiThreadAsync(
                        Strings.Format(nameof(Strings.DialogQueueInsertDiscBody), index + 1, BurnQueueItems.Count, Path.GetFileName(nextItem.ImagePath)),
                        System.Windows.MessageBoxButton.OKCancel,
                        System.Windows.MessageBoxImage.Information);
                    return res == System.Windows.MessageBoxResult.OK;
                };
            }
            else if (recorder is VirtualDiscRecorder vRecorder)
            {
                promptCallback = (index, nextItem, cancellationToken) =>
                {
                    vRecorder.InsertMedia(CreateBlankVirtualMediumFor(new MediaStatusDto { UsedSectors = 2000 }));
                    return Task.FromResult(true);
                };
            }

            var queueResult = await _burnQueueService.ExecuteQueueAsync(
                recorder,
                BurnQueueItems.ToList(),
                promptForNextDiscAsync: promptCallback,
                progress: progress,
                postBurnAction: SelectedPostBurnAction,
                continueOnError: false,
                cancellationToken: linkedToken);

            EndOperationProgress();

            if (queueResult.Success)
            {
                StatusMessage = $"Burn queue completed successfully ({queueResult.CompletedItems}/{queueResult.TotalItems} images in {queueResult.TotalTime:mm\\:ss}).";
                BurnProgress = 100.0;
                AppendLog(StatusMessage);
            }
            else
            {
                StatusMessage = $"Burn queue finished with errors: {queueResult.ErrorMessage} ({queueResult.CompletedItems}/{queueResult.TotalItems} completed).";
                AppendLog(StatusMessage);
            }
            CompletionNotice.Show(queueResult.Success ? "Burn queue finished" : "Burn queue stopped", StatusMessage, queueResult.Success);

            return queueResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusImageStudioBurnQueueCancelledByUser;
            AppendLog(StatusMessage);
            return new BurnQueueResult { Success = false, ErrorMessage = StatusMessage, Items = BurnQueueItems.ToList() };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Burn queue error: {ex.Message}";
            AppendLog(StatusMessage);
            return new BurnQueueResult { Success = false, ErrorMessage = ex.Message, Items = BurnQueueItems.ToList() };
        }
        finally
        {
            Telemetry.EndSession();
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
            UpdateCopySourceSummary();
        }
    }

    #endregion

    #region Burning Operations

    /// <summary>
    /// Cancels any actively executing image burn, rip, or verification operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = Strings.StatusImageStudioCancellingOperation;
            try
            {
                _activeCts.Cancel();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Burns the current source image to the target optical recorder drive.
    /// </summary>
    public async Task<BurnOperationResult> BurnImageToDiscAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            StatusMessage = Strings.StatusImageStudioSourceDiscImageFileDoesNot;
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        bool replaceAllowed = false; // true only after the user saw the "will replace files" warning and clicked Burn
        if (string.IsNullOrWhiteSpace(driveId) && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            var confirmed = CdBurner.App.Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                AvailableRecorders,
                SelectedRecorder,
                $"Burn Disc Image ({Path.GetFileName(SourceImagePath)})",
                UseDirectDriveCommands ? new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine) : null);

            if (confirmed == null)
            {
                StatusMessage = Strings.StatusImageStudioImageBurnCancelledByUser;
                var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
                LastBurnResult = cancelled;
                return cancelled;
            }

            SelectedRecorder = confirmed;
            replaceAllowed = true;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = Strings.StatusImageStudioNoOpticalRecorderDriveSelectedOr;
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        // Auto-mount blank media in virtual drive if empty
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                // Size by sector count, not file bytes: a raw 2352-byte CD image is ~15% larger than the data it holds.
                vRec.InsertMedia(CreateBlankVirtualMediumFor(new MediaStatusDto { UsedSectors = ImageSectorCount }));
            }
        }

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        Telemetry.BeginSession($"Disc Image ({Path.GetFileName(SourceImagePath)})", recorder.VolumePath, recorder.QueryMediaStatus().MediaType.GetDisplayName());
        StatusMessage = $"Burning image '{Path.GetFileName(SourceImagePath)}' to {recorder.VolumePath} ({recorder.ProductId})...";

        try
        {
            var progress = CreateOperationProgress(text => StatusMessage = text);

            string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
            BurnOperationResult burnResult;

            // Direct drive commands cover data images (ISO and legacy images); a CUE image keeps the Windows path.
            var directEngine = UseDirectDriveCommands && ext != ".cue"
                ? new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine, DirectBurnLogDirectory)
                : null;
            LastDirectBurnEngine = directEngine;
            IBurnEngineBackend dataEngine = directEngine ?? _burnEngine;

            bool hasPictureZone = false;
            DataBurnResult? dataPictureResult = null;
            if (ext == ".iso")
            {
                hasPictureZone = DiscArtSidecarInfo.TryReadPictureZone(SourceImagePath, out SectorRange pictureZone, out _);
                var dataService = directEngine != null ? new DataBurnService(directEngine) : _dataBurnService;
                var dataResult = await dataService.BurnIsoFileAsync(
                    recorder,
                    SourceImagePath,
                    new DataBurnOptions
                    {
                        VolumeLabel = Path.GetFileNameWithoutExtension(SourceImagePath),
                        CloseDisc = true,
                        Speed = SelectedWriteSpeed,
                        OverwriteExistingData = replaceAllowed,
                        VerifyAfterBurn = hasPictureZone && (VerifyAfterBurn || directEngine != null),
                        PictureZone = hasPictureZone ? pictureZone : null
                    },
                    progress,
                    linkedToken);
                dataPictureResult = hasPictureZone ? dataResult : null;

                burnResult = dataResult.Success
                    ? BurnOperationResult.Succeeded(dataResult.SectorsWritten, dataResult.TotalTime, dataResult.AverageSpeedMultiplier)
                    : BurnOperationResult.Failed(dataResult.ErrorMessage ?? "Burn failed.");
            }
            else if (ext == ".cue")
            {
                burnResult = await BurnCueSheetAsync(recorder, SourceImagePath, progress, linkedToken);
            }
            else if (LegacyDiscImageConverter.IsLegacyImageFile(SourceImagePath))
            {
                var legacyInfo = LegacyDiscImageConverter.Inspect(SourceImagePath);
                AppendLog($"Burning legacy {legacyInfo.FormatName} ({legacyInfo.SectorCount:N0} sectors) to {recorder.UniqueId}...");

                // Stream the 2048-byte user data straight from the image (no in-memory copy: DVD images exceed 2 GB).
                await using var isoMem = LegacyDiscImageConverter.OpenIsoUserDataStream(legacyInfo);

                burnResult = await dataEngine.BurnDataStreamAsync(
                    recorder,
                    isoMem,
                    new DataBurnOptions
                    {
                        VolumeLabel = Path.GetFileNameWithoutExtension(SourceImagePath),
                        CloseDisc = true,
                        Speed = SelectedWriteSpeed,
                        OverwriteExistingData = replaceAllowed
                    },
                    progress,
                    linkedToken);
            }
            else // generic stream (non-legacy extension): refuse raw 2,352-byte or unaligned streams
            {
                await using var fs = new FileStream(SourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length % OpticalConstants.DataSectorSize != 0 || LegacyDiscImageConverter.HasRawCdSyncPattern(fs))
                {
                    burnResult = BurnOperationResult.Failed(
                        $"Cannot burn '{Path.GetFileName(SourceImagePath)}' as a 2,048-byte data image: raw 2,352-byte or unaligned binary streams cannot be written as ISO user data.");
                }
                else
                {
                    burnResult = await dataEngine.BurnDataStreamAsync(
                        recorder,
                        fs,
                        new DataBurnOptions { VolumeLabel = Path.GetFileNameWithoutExtension(SourceImagePath), CloseDisc = true, Speed = SelectedWriteSpeed, OverwriteExistingData = replaceAllowed },
                        progress,
                        linkedToken);
                }
            }

            EndOperationProgress();
            LastBurnResult = burnResult;

            if (burnResult.Success)
            {
                string how = directEngine?.LastSummary is { } summary ? " " + summary : "";
                StatusMessage = $"Image burned successfully ({burnResult.SectorsWritten:N0} sectors, {burnResult.TotalTime}).{how}";
                BurnProgress = 100.0;

                // Automatic verification; a direct burn is always read back.
                bool postBurnAllowed = true;
                if (dataPictureResult is { HasPictureZone: true } picResult)
                {
                    if (!string.IsNullOrWhiteSpace(picResult.VerificationNote))
                    {
                        StatusMessage = $"{picResult.VerificationNote}{how}";
                    }
                }
                else if (VerifyAfterBurn || directEngine?.LastResult is { DiscRecorded: true })
                {
                    StatusMessage = Strings.StatusImageStudioBurnCompleteVerifyingDiscBitFor;
                    var verifyResult = await VerifyDiscAgainstImageAsync(recorder.UniqueId, linkedToken);
                    StatusMessage = VerificationStatusText + how;
                    if (linkedToken.IsCancellationRequested)
                    {
                        var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
                        LastBurnResult = cancelled;
                        return cancelled;
                    }
                    if (!verifyResult.IsMatch && !verifyResult.IsSkipped)
                    {
                        postBurnAllowed = false;
                        burnResult = BurnOperationResult.Failed(StatusMessage);
                        LastBurnResult = burnResult;
                    }
                }

                // After verification, never before it: ejecting first would leave nothing to verify.
                // Never run after-burn actions when verification failed or was cancelled.
                if (postBurnAllowed)
                {
                    PostBurnActionExecutor.Execute(SelectedPostBurnAction, recorder, shutdownInitiator: _shutdownInitiator);
                }
            }
            else
            {
                StatusMessage = $"Burn failed: {burnResult.ErrorMessage}";
            }
            CompletionNotice.Show(burnResult.Success ? "Image burn finished" : "Image burn failed", StatusMessage, burnResult.Success);

            return burnResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusImageStudioImageBurnCancelledByUser;
            var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
            LastBurnResult = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Burn error: {ex.Message}";
            var fail = BurnOperationResult.Failed(ex.Message);
            LastBurnResult = fail;
            return fail;
        }
        finally
        {
            Telemetry.EndSession();
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
            UpdateCopySourceSummary();
        }
    }

    private async Task<BurnOperationResult> BurnCueSheetAsync(
        IDiscRecorderBackend recorder,
        string cuePath,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        var cueSheet = CueSheetParser.ParseFile(cuePath);
        string cueDir = Path.GetDirectoryName(cuePath) ?? "";

        // Check if Audio CUE sheet
        bool hasAudio = cueSheet.Files.Any(f => f.Tracks.Any(t => t.DataType == CueTrackDataType.Audio));
        if (hasAudio)
        {
            var compilation = new AudioCdCompilation
            {
                AlbumTitle = cueSheet.Title ?? Path.GetFileNameWithoutExtension(cuePath),
                AlbumPerformer = cueSheet.Performer ?? "Various Artists",
                Songwriter = cueSheet.Songwriter,
                RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                IncludeCdText = !string.IsNullOrEmpty(cueSheet.Title) || !string.IsNullOrEmpty(cueSheet.Performer)
            };

            compilation.ImportCueSheet(cueSheet, cueDir, _audioBurnService.AudioDecoder);

            return await _audioBurnService.BurnAsync(
                recorder,
                compilation,
                new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = compilation.IncludeCdText, Speed = SelectedWriteSpeed },
                progress,
                ct);
        }

        // Binary / Data CUE sheet
        var firstFile = cueSheet.Files.FirstOrDefault();
        if (firstFile == null)
        {
            return BurnOperationResult.Failed("CUE sheet contains no file entries.");
        }

        string binPath = Path.IsPathRooted(firstFile.FileName) ? firstFile.FileName : Path.Combine(cueDir, firstFile.FileName);
        if (!File.Exists(binPath))
        {
            return BurnOperationResult.Failed($"Referenced binary file not found: '{binPath}'.");
        }

        bool isRaw = firstFile.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352);
        int sectorSize = isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

        if (recorder is VirtualDiscRecorder vRec && vRec.CurrentMedium != null)
        {
            vRec.CurrentMedium.Raw2352Mode = isRaw;
            await using var binStream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] sectorBuf = new byte[sectorSize];
            int lba = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = 0;
                while (bytesRead < sectorSize)
                {
                    int r = await binStream.ReadAsync(sectorBuf.AsMemory(bytesRead, sectorSize - bytesRead), ct);
                    if (r == 0) break;
                    bytesRead += r;
                }

                if (bytesRead == 0) break;
                if (bytesRead < sectorSize)
                {
                    Array.Clear(sectorBuf, bytesRead, sectorSize - bytesRead);
                }

                vRec.CurrentMedium.WriteSector(lba++, sectorBuf, sectorSize);
            }

            vRec.CurrentMedium.UsedSectors = lba;
            var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = lba };
            foreach (var trk in firstFile.Tracks)
            {
                // Track start = INDEX 01 (INDEX 00 is the pre-gap), as a frame offset within the file.
                var startIndex = trk.Indices.FirstOrDefault(i => i.IndexNumber == 1) ?? trk.Indices.FirstOrDefault();
                int trkStart = startIndex?.Position.ToFrameCount() ?? 0;
                session.Tracks.Add(new VirtualTrack
                {
                    TrackNumber = trk.TrackNumber,
                    StartLba = trkStart,
                    SectorCount = Math.Max(0, lba - trkStart),
                    IsAudio = false,
                    Title = trk.Title,
                    Performer = trk.Performer
                });
            }
            vRec.CurrentMedium.Sessions.Add(session);
            vRec.CurrentMedium.IsFinalized = true;

            return BurnOperationResult.Succeeded(lba, TimeSpan.FromMilliseconds(50), 24.0);
        }

        // Physical / non-virtual stream burn fallback: extract 2,048-byte user data (refuses Form 2 / audio)
        try
        {
            await using var stream = LegacyDiscImageConverter.OpenCueIsoUserDataStream(cuePath);
            return await _burnEngine.BurnDataStreamAsync(recorder, stream, new DataBurnOptions { CloseDisc = true, Speed = SelectedWriteSpeed }, progress, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or FileNotFoundException)
        {
            return BurnOperationResult.Failed(ex.Message);
        }
    }

    #endregion

    #region Ripping Operations

    /// <summary>
    /// Alias for <see cref="RipDiscToImageAsync"/>.
    /// </summary>
    public Task<RipOperationResult> RipDiscToIsoAsync(
        string? driveId = null,
        string? targetPath = null,
        bool? rawMode = null,
        CancellationToken ct = default)
        => RipDiscToImageAsync(driveId, targetPath, rawMode, ct);

    /// <summary>
    /// Rips the inserted optical disc to an ISO or BIN/CUE disc image file.
    /// </summary>
    public async Task<RipOperationResult> RipDiscToImageAsync(
        string? driveId = null,
        string? targetPath = null,
        bool? rawMode = null,
        CancellationToken ct = default)
    {
        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = Strings.StatusImageStudioNoOpticalRecorderDriveSelectedOr;
            var fail = RipOperationResult.Failed(StatusMessage);
            LastRipResult = fail;
            return fail;
        }

        string outputPath = !string.IsNullOrWhiteSpace(targetPath)
            ? targetPath
            : (!string.IsNullOrWhiteSpace(RipTargetFilePath) ? RipTargetFilePath : Path.Combine(Path.GetTempPath(), "ripped_disc.iso"));

        bool raw = rawMode ?? (RipRawBinCueMode || outputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Ripping disc from {recorder.UniqueId} to '{Path.GetFileName(outputPath)}'...";

        try
        {
            var progress = CreateOperationProgress(text => StatusMessage = text);

            RipOperationResult ripResult;
            if (raw)
            {
                string companionCue = Path.ChangeExtension(outputPath, ".cue");
                ripResult = await _ripperService.RipToBinCueAsync(recorder, _burnEngine, outputPath, companionCue, progress: progress, ct: linkedToken);
            }
            else
            {
                ripResult = await _ripperService.RipToIsoAsync(recorder, _burnEngine, outputPath, progress: progress, ct: linkedToken);
            }

            EndOperationProgress();
            LastRipResult = ripResult;

            if (ripResult.Success)
            {
                StatusMessage = $"Disc ripped successfully ({ripResult.SectorsRead:N0} sectors, {ripResult.TotalTime}).";
                BurnProgress = 100.0;
            }
            else
            {
                StatusMessage = $"Rip failed: {ripResult.ErrorMessage}";
            }

            return ripResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusImageStudioDiscRipCancelledByUser;
            var fail = RipOperationResult.Failed(StatusMessage);
            LastRipResult = fail;
            return fail;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rip error: {ex.Message}";
            var fail = RipOperationResult.Failed(ex.Message);
            LastRipResult = fail;
            return fail;
        }
        finally
        {
            Telemetry.EndSession();
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
            UpdateCopySourceSummary();
        }
    }

    #endregion

    #region Verification Operations

    /// <summary>
    /// Compares sectors on the inserted optical disc bit-for-bit against the source image.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstImageAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            VerificationStatusText = "Source disc image file does not exist.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            return fail;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            VerificationStatusText = "No optical recorder drive selected or available.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            return fail;
        }

        bool ownsOperation = _activeCts == null;
        if (ownsOperation)
        {
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsBusy = true;
        }
        var linkedToken = _activeCts!.Token;

        BurnProgress = 0;
        VerificationStatusText = $"Verifying {recorder.UniqueId} bit-for-bit against '{Path.GetFileName(SourceImagePath)}'...";

        try
        {
            var progress = CreateOperationProgress(text => VerificationStatusText = text);

            string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
            VerificationResult result;

            if (ext == ".iso")
            {
                result = await _verificationService.VerifyDiscAgainstIsoFileAsync(
                    recorder,
                    _burnEngine,
                    SourceImagePath,
                    progress: progress,
                    ct: linkedToken);
            }
            else if (ext == ".cue")
            {
                result = await VerifyCueSheetAsync(recorder, SourceImagePath, progress, linkedToken);
            }
            else if (LegacyDiscImageConverter.IsLegacyImageFile(SourceImagePath))
            {
                var legacyInfo = LegacyDiscImageConverter.Inspect(SourceImagePath);
                await using var isoMem = LegacyDiscImageConverter.OpenIsoUserDataStream(legacyInfo);

                result = await _verificationService.VerifyDiscAgainstStreamAsync(
                    recorder,
                    _burnEngine,
                    isoMem,
                    raw2352Mode: false,
                    progress: progress,
                    ct: linkedToken);
            }
            else // .bin or raw
            {
                await using var fs = new FileStream(SourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                result = await _verificationService.VerifyDiscAgainstStreamAsync(
                    recorder,
                    _burnEngine,
                    fs,
                    raw2352Mode: true,
                    progress: progress,
                    ct: linkedToken);
            }

            EndOperationProgress();
            LastVerificationResult = result;
            CalculatedSha256 = result.ExpectedSha256;
            DiscSha256 = result.ActualSha256;
            IsChecksumMatch = result.IsMatch;

            if (result.IsSkipped)
            {
                VerificationStatusText = $"Verification skipped: {result.ErrorMessage}";
                BurnProgress = 100.0;
            }
            else if (result.IsMatch)
            {
                VerificationStatusText = $"Verification PASSED: SHA-256 Checksums match bit-for-bit ({result.TotalSectorsVerified:N0} sectors verified).";
                BurnProgress = 100.0;
            }
            else
            {
                VerificationStatusText = $"Verification FAILED: {result.MismatchedLbas.Count} sector mismatch(es). {result.ErrorMessage}";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            VerificationStatusText = "Verification cancelled by user.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            IsChecksumMatch = false;
            return fail;
        }
        catch (Exception ex)
        {
            VerificationStatusText = $"Verification error: {ex.Message}";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = ex.Message };
            LastVerificationResult = fail;
            IsChecksumMatch = false;
            return fail;
        }
        finally
        {
            if (ownsOperation)
            {
                _activeCts?.Dispose();
                _activeCts = null;
                IsBusy = false;
            }
        }
    }

    private async Task<VerificationResult> VerifyCueSheetAsync(
        IDiscRecorderBackend recorder,
        string cuePath,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        var cueSheet = CueSheetParser.ParseFile(cuePath);
        string cueDir = Path.GetDirectoryName(cuePath) ?? "";

        // An audio CUE (.flac/.wav/.mp3 tracks, or audio in .bin) cannot be compared sector-for-sector: the files are
        // decoded/resampled, tracks are padded to sector boundaries, pre-gaps are generated, and audio reads carry the
        // drive's read offset and no EDC. Comparing would read a compressed .flac as raw sectors and always "fail".
        if (cueSheet.Files.Any(f => f.Tracks.Any(t => t.DataType == CueTrackDataType.Audio)))
        {
            return new VerificationResult
            {
                IsMatch = false,
                IsSkipped = true,
                ErrorMessage = "Audio CD tracks can't be verified bit-for-bit against the source files. Rip the disc with AccurateRip to check it."
            };
        }

        var firstFile = cueSheet.Files.FirstOrDefault();

        if (firstFile == null)
        {
            return new VerificationResult { IsMatch = false, ErrorMessage = "CUE sheet has no files to verify against." };
        }

        string binPath = Path.IsPathRooted(firstFile.FileName) ? firstFile.FileName : Path.Combine(cueDir, firstFile.FileName);
        if (!File.Exists(binPath))
        {
            return new VerificationResult { IsMatch = false, ErrorMessage = $"Referenced binary file '{binPath}' not found." };
        }

        bool isRaw = firstFile.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352 || t.DataType == CueTrackDataType.Audio);
        await using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        return await _verificationService.VerifyDiscAgainstStreamAsync(
            recorder,
            _burnEngine,
            fs,
            raw2352Mode: isRaw,
            progress: progress,
            ct: ct);
    }

    private IDiscRecorderBackend? ResolveRecorder(string? driveId = null)
    {
        if (SelectedRecorder != null && (string.IsNullOrWhiteSpace(driveId) || string.Equals(SelectedRecorder.UniqueId, driveId, StringComparison.OrdinalIgnoreCase)))
        {
            return SelectedRecorder;
        }

        if (!string.IsNullOrWhiteSpace(driveId))
        {
            var found = _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                SelectedRecorder = found;
                return found;
            }
        }

        if (SelectedRecorder != null)
        {
            return SelectedRecorder;
        }

        var defaultRec = _deviceService.GetRecorders().FirstOrDefault(r => !r.IsVirtual)
                         ?? _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                         ?? _deviceService.GetRecorders().FirstOrDefault();
        if (defaultRec != null)
        {
            SelectedRecorder = defaultRec;
        }
        return defaultRec;
    }

    #endregion

    #region 1-Click Disc Copy & Multi-Copy Batch Operations

    /// <summary>
    /// Executes a 1-Click Disc-to-Disc Copy from <paramref name="sourceDriveId"/> (or <see cref="CopySourceRecorder"/>)
    /// to <paramref name="targetDriveId"/> (or <see cref="CopyTargetRecorder"/>) for <see cref="CopyCount"/> copies.
    /// </summary>
    public async Task<DiscCopyResult> CopyDiscAsync(
        string? sourceDriveId = null,
        string? targetDriveId = null,
        Func<int, CancellationToken, Task<bool>>? onPrepareTargetDiscAsync = null,
        CancellationToken ct = default)
    {
        var sourceRec = !string.IsNullOrWhiteSpace(sourceDriveId)
            ? _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, sourceDriveId, StringComparison.OrdinalIgnoreCase))
            : (CopySourceRecorder ?? SelectedRecorder);

        var targetRec = !string.IsNullOrWhiteSpace(targetDriveId)
            ? _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, targetDriveId, StringComparison.OrdinalIgnoreCase))
            : (CopyTargetRecorder ?? SelectedRecorder);

        if (sourceRec == null || targetRec == null)
        {
            StatusMessage = Strings.StatusImageStudioSourceOrTargetOpticalDriveIs;
            var fail = DiscCopyResult.Failed(StatusMessage, CopyCount);
            LastCopyResult = fail;
            return fail;
        }

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Copying disc from {sourceRec.UniqueId} to {targetRec.UniqueId} ({CopyCount} copy/copies)...";
        AppendLog($"Starting 1-Click Disc Copy: Source={sourceRec.UniqueId} -> Target={targetRec.UniqueId}, Copies={CopyCount}, RawClone={CopyRawCloneMode}.");

        try
        {
            var progress = CreateOperationProgress(text => StatusMessage = text);

            // Capture the source geometry before any single-drive swap replaces the source disc.
            var sourceStatus = sourceRec.QueryMediaStatus();
            bool singleDrive = DiscCopyService.IsSameDrive(sourceRec, targetRec);

            Func<int, CancellationToken, Task<bool>> prepareCallback = async (copyIdx, token) =>
            {
                if (onPrepareTargetDiscAsync != null)
                {
                    return await onPrepareTargetDiscAsync(copyIdx, token);
                }

                // For virtual target drives, automatically mount a fresh blank disc sized for the source before each copy
                if (targetRec is VirtualDiscRecorder vTarget)
                {
                    var st = vTarget.QueryMediaStatus();
                    if (singleDrive || copyIdx > 1 || !st.MediaPresent || !st.IsBlank)
                    {
                        var blank = CreateBlankVirtualMediumFor(sourceStatus);
                        vTarget.InsertMedia(blank);
                        AppendLog($"Mounted blank virtual {blank.MediaType.GetDisplayName()} in {vTarget.UniqueId} for Copy #{copyIdx}.");
                    }
                    return true;
                }

                // Physical drives: drive-to-drive copy #1 goes onto the blank already in the target drive.
                // Single-drive copies (source still loaded) and every later copy need the user to swap discs.
                if (!singleDrive && copyIdx == 1)
                {
                    return true;
                }

                return await PromptForBlankTargetDiscAsync(targetRec, copyIdx, CopyCount, singleDrive, token);
            };

            var directDataWriter = UseDirectDriveCommands
                ? new CdBurner.Hardware.NativeWriting.NativeDataBurnEngine(_burnEngine, DirectBurnLogDirectory)
                : null;

            var copyResult = await _copyService.CopyDiscAsync(
                sourceRec,
                targetRec,
                new DiscCopyOptions
                {
                    CopyCount = CopyCount,
                    RawCloneMode = CopyRawCloneMode,
                    VerifyEachCopy = CopyVerifyEachDisc,
                    KeepStagedImage = CopyKeepStagedImage,
                    WriteSpeed = SelectedWriteSpeed,
                    OnPrepareTargetDiscAsync = prepareCallback,
                    // A mixed-mode or Enhanced CD's data is copied only by the direct writer, and only when the user turned it on.
                    MixedModeWriter = UseDirectDriveCommands
                        ? new CdBurner.Hardware.NativeWriting.NativeAudioBurnEngine(DirectBurnLogDirectory)
                        : null,
                    DataSessionWriter = directDataWriter,
                    // Source audio that still reads wrong: ask before writing (as for damaged FLAC files). Unattended, the copy stops.
                    ConfirmSuspectAudioAsync = CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession
                        ? async (problem, _) =>
                        {
                            AppendLog(problem);
                            var answer = await ShowMessageOnUiThreadAsync(
                                problem + Environment.NewLine + Environment.NewLine
                                    + Strings.DialogCopySuspectAudioQuestion,
                                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                            return answer == System.Windows.MessageBoxResult.Yes;
                        }
                        : null
                },
                progress,
                linkedToken);

            EndOperationProgress();
            LastCopyResult = copyResult;

            if (copyResult.Success)
            {
                BurnProgress = 100.0;
                StatusMessage = $"Disc Copy complete: {copyResult.CopiesCompleted}/{copyResult.CopiesRequested} copy/copies burned ({copyResult.TotalSectorsWritten:N0} total sectors)."
                    + (copyResult.CopiedDataTrack ? " The data track was copied with the audio." : string.Empty)
                    + (copyResult.CopiedDataSession ? " Both sessions (audio and data) were copied." : string.Empty)
                    + (copyResult.WarningMessage is { } copyWarning ? $" {copyWarning}" : string.Empty);
                AppendLog(StatusMessage);
            }
            else
            {
                StatusMessage = $"Disc Copy failed: {copyResult.ErrorMessage}";
                AppendLog(StatusMessage);
            }
            CompletionNotice.Show(copyResult.Success ? "Disc copy finished" : "Disc copy failed", StatusMessage, copyResult.Success);

            return copyResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusImageStudioDiscCopyCancelledByUser;
            AppendLog(StatusMessage);
            var cancelled = DiscCopyResult.Failed(StatusMessage, CopyCount);
            LastCopyResult = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Disc Copy error: {ex.Message}";
            AppendLog(StatusMessage);
            var fail = DiscCopyResult.Failed(ex.Message, CopyCount);
            LastCopyResult = fail;
            return fail;
        }
        finally
        {
            Telemetry.EndSession();
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
            UpdateCopySourceSummary();
        }
    }

    /// <summary>
    /// Picks the smallest blank virtual medium that can hold a copy of the source disc
    /// (CD-R 700 MB, DVD-R 4.7 GB, or DVD+R DL 8.5 GB).
    /// </summary>
    public static VirtualDiscMedium CreateBlankVirtualMediumFor(MediaStatusDto sourceStatus) =>
        VirtualDiscMedium.CreateBlankFor(sourceStatus);

    /// <summary>
    /// Ejects the target drive and asks the user to insert a blank disc for <paramref name="copyIndex"/>, then waits for the
    /// drive to report blank (or rewritable) media. Returns false if the user cancels. Without an interactive desktop it
    /// cannot prompt, so it proceeds only if blank media is already present.
    /// </summary>
    private async Task<bool> PromptForBlankTargetDiscAsync(
        IDiscRecorderBackend targetRec,
        int copyIndex,
        int copyCount,
        bool singleDrive,
        CancellationToken ct)
    {
        static bool IsUsableBlank(MediaStatusDto st) => st.MediaPresent && (st.IsBlank || st.IsRewritable);

        if (!CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            bool ready = IsUsableBlank(targetRec.QueryMediaStatus());
            if (!ready) AppendLog($"Copy #{copyIndex}: no blank disc in {targetRec.UniqueId} and no interactive session to prompt for one.");
            return ready;
        }

        try
        {
            targetRec.EjectMedia();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not eject {targetRec.UniqueId}: {ex.Message}");
        }

        string reason = singleDrive && copyIndex == 1
            ? "The source disc has been read and ejected."
            : $"Copy #{copyIndex - 1} is finished and has been ejected.";
        string message = Strings.Format(nameof(Strings.DialogInsertBlankForCopyBody), reason, targetRec.VolumePath, copyIndex, copyCount);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            AppendLog($"Waiting for blank disc in {targetRec.UniqueId} for Copy #{copyIndex}...");

            var answer = await ShowMessageOnUiThreadAsync(message, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information);
            if (answer != System.Windows.MessageBoxResult.OK)
            {
                return false;
            }

            // Give the drive time to spin up and read the new disc.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (IsUsableBlank(targetRec.QueryMediaStatus()))
                {
                    return true;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            message = Strings.Format(nameof(Strings.DialogNoBlankDetectedBody), targetRec.VolumePath, copyIndex, copyCount);
        }
    }

    private static Task<System.Windows.MessageBoxResult> ShowMessageOnUiThreadAsync(
        string message,
        System.Windows.MessageBoxButton buttons,
        System.Windows.MessageBoxImage icon)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        System.Windows.MessageBoxResult Show() =>
            System.Windows.MessageBox.Show(message, Strings.DialogDiscCopyTitle, buttons, icon);

        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return Task.FromResult(Show());
        }

        return dispatcher.InvokeAsync(Show).Task;
    }

    #endregion

    #region Image Content Browser & Extraction Operations

    private readonly List<ImageBrowserNodeViewModel> _allBrowserRoots = new();

    private void ClearImageBrowserTree()
    {
        _allBrowserRoots.Clear();
        ImageTreeRoots.Clear();
        SelectedImageEntry = null;
        LastBrowseResult = null;
        ImageBrowseSummary = "Load an image (.iso, .udf, .nrg, .mds, .ccd, .cdi, .cue/.bin) to browse its files.";
    }

    /// <summary>
    /// Refreshes the read-only directory tree from <see cref="SourceImagePath"/>.
    /// </summary>
    public void RefreshImageBrowserTree(bool logWarnings = true)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            ClearImageBrowserTree();
            return;
        }

        if (_browserService.TryBrowse(SourceImagePath, out var browse) && browse != null)
        {
            LastBrowseResult = browse;
            _allBrowserRoots.Clear();
            foreach (var child in browse.Root.Children)
            {
                _allBrowserRoots.Add(BuildNodeViewModel(child));
            }
            SelectedImageEntry = null;
            ApplyImageBrowseFilter();

            string vol = string.IsNullOrWhiteSpace(browse.VolumeLabel) ? "(untitled)" : browse.VolumeLabel;
            ImageBrowseSummary = $"{browse.ActiveFileSystem}  ·  {browse.FileCount:N0} file(s), {browse.DirectoryCount:N0} folder(s)  ·  Volume: {vol}";
            if (logWarnings)
            {
                foreach (string w in browse.Warnings)
                {
                    AppendLog($"Browse warning: {w}");
                }
            }
        }
        else
        {
            _allBrowserRoots.Clear();
            ImageTreeRoots.Clear();
            SelectedImageEntry = null;
            LastBrowseResult = null;
            ImageBrowseSummary = "No readable ISO 9660, Joliet, or UDF filesystem found in this image.";
        }
    }

    private ImageBrowserNodeViewModel BuildNodeViewModel(DiscImageTreeNode node)
    {
        var vm = new ImageBrowserNodeViewModel(node.Entry, () => ExtractSelectedCommand?.RaiseCanExecuteChanged());
        foreach (var child in node.Children)
        {
            vm.Children.Add(BuildNodeViewModel(child));
        }
        return vm;
    }

    private bool HasAnyCheckedBrowserNode()
    {
        static bool AnyChecked(IEnumerable<ImageBrowserNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsChecked || AnyChecked(n.Children))
                {
                    return true;
                }
            }
            return false;
        }

        return AnyChecked(_allBrowserRoots);
    }

    private List<string> CollectCheckedOrSelectedEntryPaths()
    {
        var paths = new List<string>();
        void WalkChecked(IEnumerable<ImageBrowserNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsChecked)
                {
                    paths.Add(n.RelativePath);
                }
                else
                {
                    WalkChecked(n.Children);
                }
            }
        }

        WalkChecked(_allBrowserRoots);
        if (paths.Count == 0 && SelectedImageEntry != null && !string.IsNullOrWhiteSpace(SelectedImageEntry.RelativePath))
        {
            paths.Add(SelectedImageEntry.RelativePath);
        }
        return paths;
    }

    private void ApplyImageBrowseFilter()
    {
        string query = _imageBrowseSearchText.Trim();
        ImageTreeRoots.Clear();

        if (string.IsNullOrEmpty(query))
        {
            foreach (var root in _allBrowserRoots)
            {
                ImageTreeRoots.Add(root);
            }
            return;
        }

        foreach (var root in _allBrowserRoots)
        {
            var filtered = FilterNode(root, query);
            if (filtered != null)
            {
                ImageTreeRoots.Add(filtered);
            }
        }
    }

    private ImageBrowserNodeViewModel? FilterNode(ImageBrowserNodeViewModel source, string query)
    {
        bool selfMatches = source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           source.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);

        var matchingChildren = new List<ImageBrowserNodeViewModel>();
        foreach (var child in source.Children)
        {
            var filteredChild = FilterNode(child, query);
            if (filteredChild != null)
            {
                matchingChildren.Add(filteredChild);
            }
        }

        if (!selfMatches && matchingChildren.Count == 0)
        {
            return null;
        }

        var copy = new ImageBrowserNodeViewModel(source.Entry, () => ExtractSelectedCommand?.RaiseCanExecuteChanged())
        {
            IsChecked = source.IsChecked,
            IsSelected = source.IsSelected,
            IsExpanded = matchingChildren.Count > 0
        };
        foreach (var child in matchingChildren)
        {
            copy.Children.Add(child);
        }
        return copy;
    }

    /// <summary>
    /// Safely extracts the checked or currently selected entry/folder from <see cref="SourceImagePath"/>.
    /// </summary>
    public Task<DiscImageExtractionResult?> ExtractSelectedEntriesAsync(
        string? outputDirectory = null,
        CancellationToken ct = default)
    {
        List<string> selectedPaths = CollectCheckedOrSelectedEntryPaths();
        if (selectedPaths.Count == 0)
        {
            StatusMessage = "Select or check at least one file or folder to extract.";
            return Task.FromResult<DiscImageExtractionResult?>(null);
        }

        return ExtractEntriesInternalAsync(selectedPaths, outputDirectory, ct);
    }

    /// <summary>
    /// Safely extracts all entries from <see cref="SourceImagePath"/> into a target directory.
    /// </summary>
    public Task<DiscImageExtractionResult?> ExtractAllEntriesAsync(
        string? outputDirectory = null,
        CancellationToken ct = default) =>
        ExtractEntriesInternalAsync(entryPaths: null, outputDirectory, ct);

    private async Task<DiscImageExtractionResult?> ExtractEntriesInternalAsync(
        IReadOnlyList<string>? entryPaths,
        string? outputDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            StatusMessage = Strings.StatusImageStudioSourceDiscImageFileDoesNot;
            return null;
        }

        string? targetDir = outputDirectory ?? ExtractOutputDirectory;
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var dlg = new OpenFolderDialog
                {
                    Title = entryPaths == null
                        ? "Select Folder to Extract All Image Contents"
                        : "Select Folder to Extract Selected Entries"
                };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName))
                {
                    return null;
                }
                targetDir = dlg.FolderName;
            }
            else
            {
                string stem = Path.GetFileNameWithoutExtension(SourceImagePath);
                string parent = Path.GetDirectoryName(SourceImagePath) ?? Path.GetTempPath();
                targetDir = Path.Combine(parent, stem + "_extracted");
            }
        }

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken effectiveCt = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        LastExtractionResult = null;
        StatusMessage = entryPaths == null
            ? $"Extracting all files from '{Path.GetFileName(SourceImagePath)}'..."
            : $"Extracting {entryPaths.Count} selected item(s) from '{Path.GetFileName(SourceImagePath)}'...";
        AppendLog(StatusMessage);

        int epoch;
        lock (_progressLock)
        {
            epoch = ++_progressEpoch;
        }

        IProgress<DiscImageExtractionProgress> progress = new Progress<DiscImageExtractionProgress>(p =>
        {
            lock (_progressLock)
            {
                if (!_isBusy || epoch != _progressEpoch)
                {
                    return;
                }

                double pct = p.TotalBytes > 0
                    ? Math.Min(100.0, (double)p.BytesCopied / p.TotalBytes * 100.0)
                    : (p.TotalFiles > 0 ? Math.Min(100.0, (double)p.FilesCompleted / p.TotalFiles * 100.0) : 100.0);
                BurnProgress = pct;
                StatusMessage = $"Extracting '{p.CurrentEntryPath}' ({p.FilesCompleted}/{p.TotalFiles})...";
            }
        });

        try
        {
            var options = new DiscImageExtractionOptions(
                OutputDirectory: targetDir,
                EntryPaths: entryPaths,
                OverwriteExisting: ExtractOverwriteExisting);

            DiscImageExtractionResult result = await Task.Run(
                () => _browserService.ExtractAsync(SourceImagePath, options, progress, effectiveCt),
                effectiveCt);

            EndOperationProgress();
            BurnProgress = 100.0;
            LastExtractionResult = result;
            StatusMessage = $"Extracted {result.ExtractedFileCount:N0} file(s) ({result.ExtractedBytes:N0} bytes) to '{result.OutputDirectory}'.";
            AppendLog(StatusMessage);
            return result;
        }
        catch (OperationCanceledException)
        {
            EndOperationProgress();
            StatusMessage = "Image extraction cancelled.";
            AppendLog(StatusMessage);
            return null;
        }
        catch (Exception ex)
        {
            EndOperationProgress();
            StatusMessage = $"Extraction failed: {ex.Message}";
            AppendLog(StatusMessage);
            return null;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    #endregion
}
