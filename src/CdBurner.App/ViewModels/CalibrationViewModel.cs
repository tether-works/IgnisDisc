// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Imaging;

namespace CdBurner.App.ViewModels;

/// <summary>What a click on the photo adds.</summary>
public enum CalibrationClickMode
{
    /// <summary>A point on the physical outer edge of the disc (photo pixels).</summary>
    OuterRim,

    /// <summary>A point on the edge of the centre hole (photo pixels).</summary>
    CenterHole,

    /// <summary>A measurement point of the target's stage (stored in millimetres after rectification).</summary>
    Measure,
}

/// <summary>Which measurement the opened target takes.</summary>
public enum CalibrationMeasureStage
{
    /// <summary>The record's target has no measurement this screen can take.</summary>
    None,

    /// <summary>Coarse target: one click on each wide-band edge along one ray.</summary>
    BandEdges,

    /// <summary>Boundary target: points along light/dark boundaries, in separate segments.</summary>
    BoundarySegments,

    /// <summary>Fine target: points along one line of the reference cross.</summary>
    Trace,
}

/// <summary>Kind of a dot drawn over the photo.</summary>
public enum CalibrationMarkerKind { OuterRim, CenterHole, Measure, Outline }

/// <summary>A dot drawn over the photo, in photo pixels.</summary>
public sealed record CalibrationMarker(double X, double Y, CalibrationMarkerKind Kind, double Size)
{
    public double Left => X - Size / 2;
    public double Top => Y - Size / 2;
}

/// <summary>One fit candidate as listed on the screen.</summary>
public sealed record CalibrationCandidateRow(int Number, CalibrationFitCandidate Candidate, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Calibration screen: open the record written with the target, open a photo of the burned disc, mark the outer rim
/// and the centre hole, rectify, click the measurement points of the stage, fit and save the result per drive model
/// and media ID. Fitting and saving use the same library calls and candidate rule as
/// <c>ignisdisc-cli disc-art calibrate fit</c>, so a record saved here fits to the same numbers on the command line.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    /// <summary>Radius of the centre hole of a 12 cm or 8 cm disc (15 mm hole).</summary>
    public const double HoleRadiusMm = 7.5;

    /// <summary>Longest side of the working photo. Larger photos are scaled down when opened.</summary>
    public const int MaxPhotoSide = 4096;

    private const int MaxOutlinePoints = 128; // CalibrationPhotoSession accepts 5..128 per outline
    private const int OutlineDots = 90;

    private readonly List<CalibrationPoint> _outer = new();
    private readonly List<CalibrationPoint> _hole = new();
    private readonly List<CalibrationPoint> _trace = new();
    private readonly List<BoundarySegmentDraft> _segments = new();
    private readonly List<CalibrationRingObservation> _bandEdges = new(); // in click order

    private CalibrationSession? _record;
    private string? _recordPath;
    private CalibrationPhotoSession? _recordPhoto; // extra fields of the opened record, kept when saving back
    private byte[]? _photoPng;
    private int _photoWidth;
    private int _photoHeight;
    private double _physicalOuterRadiusMm = 60;
    private CalibrationPhotoTransform? _transform;
    private DiscPhotoRectificationKind _rectificationKind = DiscPhotoRectificationKind.Standard;
    private double _holeRadiusUsedMm = HoleRadiusMm;
    private CalibrationPhotoTransform? _transformBeforeEdit;
    private bool _rectificationConfirmed;
    private CalibrationClickMode _clickMode = CalibrationClickMode.OuterRim;
    private CalibrationRecordFit? _fit;
    private CalibrationCandidateRow? _selectedCandidate;
    private string _statusMessage = "Open the calibration record written with the target (.calibration.json), then a photo of the burned disc.";
    private string _rectifyMessage = string.Empty;
    private string _fitSummary = string.Empty;
    private string _driveModel = string.Empty;
    private string _mediaId = string.Empty;
    private string _storePath = CalibrationStore.GetDefaultPath();
    private bool _isBusy;

    private sealed class BoundarySegmentDraft(int id, List<CalibrationPoint> points)
    {
        public int Id { get; } = id;
        public List<CalibrationPoint> Points { get; } = points;
    }

    public CalibrationViewModel()
    {
        OpenRecordCommand = new RelayCommand(BrowseRecord);
        OpenPhotoCommand = new RelayCommand(BrowsePhoto);
        FindEdgesCommand = new AsyncRelayCommand(FindDiscEdgesAsync);
        RectifyCommand = new RelayCommand(() => Rectify());
        UndoCommand = new RelayCommand(Undo);
        NewSegmentCommand = new RelayCommand(StartNewSegment);
        FitCommand = new AsyncRelayCommand(FitAsync);
        SaveRecordCommand = new RelayCommand(BrowseSaveRecord);
        SaveToStoreCommand = new RelayCommand(() => SaveToStore());
    }

    public ICommand OpenRecordCommand { get; }
    public ICommand OpenPhotoCommand { get; }
    public AsyncRelayCommand FindEdgesCommand { get; }
    public ICommand RectifyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand NewSegmentCommand { get; }
    public AsyncRelayCommand FitCommand { get; }
    public ICommand SaveRecordCommand { get; }
    public ICommand SaveToStoreCommand { get; }

    public ObservableCollection<CalibrationMarker> Markers { get; } = new();
    public ObservableCollection<CalibrationCandidateRow> Candidates { get; } = new();

    public string? RecordPath => _recordPath;
    public bool HasRecord => _record is not null;
    public bool HasPhoto => _photoPng is not null;
    public byte[]? PhotoPng => _photoPng;
    public int PhotoPixelWidth => _photoWidth;
    public int PhotoPixelHeight => _photoHeight;
    public bool IsRectified => _rectificationConfirmed;
    public CalibrationPhotoTransform? Transform => _transform;
    public CalibrationRecordFit? LastFit => _fit;
    public int OuterPointCount => _outer.Count;
    public int HolePointCount => _hole.Count;

    public int MeasurePointCount => MeasureStage switch
    {
        CalibrationMeasureStage.BandEdges => _bandEdges.Count,
        CalibrationMeasureStage.BoundarySegments => _segments.Sum(s => s.Points.Count),
        CalibrationMeasureStage.Trace => _trace.Count,
        _ => 0,
    };

    public int BoundarySegmentCount => _segments.Count(s => s.Points.Count > 0);

    public CalibrationMeasureStage MeasureStage => _record is null ? CalibrationMeasureStage.None : StageOf(_record.Target);

    public string TargetSummary
    {
        get
        {
            if (_record is null) return "No record open.";
            CalibrationParameters p = _record.Target.Parameters;
            string media = p.Kind switch { CalibrationDiscKind.Cd => "CD", CalibrationDiscKind.Dvd => "DVD", _ => "BD" };
            return string.Create(CultureInfo.InvariantCulture,
                $"{media}, {CalibrationRecordFitter.StageName(_record.Target)} stage, target {_record.Target.Id}; generated with inner {p.InnerRadiusMm:0.####} mm, outer {p.OuterRadiusMm:0.####} mm");
        }
    }

    public string StageInstructions => MeasureStage switch
    {
        CalibrationMeasureStage.BandEdges =>
            $"Coarse target: along one straight line from the centre outwards, click each edge of the wide bands once ({BandEdgeCapacity} edges).",
        CalibrationMeasureStage.BoundarySegments =>
            "Boundary target: click points along a light/dark boundary. Start a new segment for each separate boundary.",
        CalibrationMeasureStage.Trace =>
            "Fine target: click points along one arm of the cross, from the inside outwards.",
        _ => _record is null ? "Open a record first." : "This record's target has no measurement this screen can take.",
    };

    public string PointCountsText =>
        FormattableString.Invariant($"Outer rim: {_outer.Count}   Centre hole: {_hole.Count}   Measurement: {MeasurePointCount}")
        + (MeasureStage == CalibrationMeasureStage.BoundarySegments
            ? FormattableString.Invariant($" in {BoundarySegmentCount} segment(s)") : string.Empty);

    public CalibrationClickMode ClickMode
    {
        get => _clickMode;
        set
        {
            if (SetProperty(ref _clickMode, value))
            {
                OnPropertyChanged(nameof(IsOuterRimMode));
                OnPropertyChanged(nameof(IsCenterHoleMode));
                OnPropertyChanged(nameof(IsMeasureMode));
            }
        }
    }

    public bool IsOuterRimMode
    {
        get => _clickMode == CalibrationClickMode.OuterRim;
        set { if (value) ClickMode = CalibrationClickMode.OuterRim; }
    }

    public bool IsCenterHoleMode
    {
        get => _clickMode == CalibrationClickMode.CenterHole;
        set { if (value) ClickMode = CalibrationClickMode.CenterHole; }
    }

    public bool IsMeasureMode
    {
        get => _clickMode == CalibrationClickMode.Measure;
        set { if (value) ClickMode = CalibrationClickMode.Measure; }
    }

    public CalibrationCandidateRow? SelectedCandidate
    {
        get => _selectedCandidate;
        set => SetProperty(ref _selectedCandidate, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string RectifyMessage
    {
        get => _rectifyMessage;
        private set => SetProperty(ref _rectifyMessage, value);
    }

    public string FitSummary
    {
        get => _fitSummary;
        private set => SetProperty(ref _fitSummary, value);
    }

    public string DriveModel
    {
        get => _driveModel;
        set => SetProperty(ref _driveModel, value ?? string.Empty);
    }

    public string MediaId
    {
        get => _mediaId;
        set => SetProperty(ref _mediaId, value ?? string.Empty);
    }

    /// <summary>Calibration store file; <c>%APPDATA%\IgnisDisc\disc-calibrations.json</c> unless changed.</summary>
    public string StorePath
    {
        get => _storePath;
        set => SetProperty(ref _storePath, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private int BandEdgeCapacity => _record is null ? 0 : CalibrationRingFitter.MeasurementCount(_record.Target);

    // ---------------------------------------------------------------- opening

    /// <summary>
    /// Opens a calibration record. Its target is what the fit is measured against; a photo and points saved in it
    /// are restored so the record can be continued or refitted.
    /// </summary>
    public bool OpenRecord(string path)
    {
        CalibrationSession session;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            session = CalibrationSessionJson.Load(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Could not open the record: {ex.Message}";
            return false;
        }

        _record = session;
        _recordPath = fullPath;
        _recordPhoto = session.Photo;
        ClearPhotoState();
        if (session.Photo is { } photo)
        {
            // A record from another tool may hold a picture this screen cannot show; its points can still be fitted
            // and saved back with the picture unchanged.
            byte[]? saved = null;
            try { saved = Convert.FromBase64String(photo.ImagePngBase64); }
            catch (FormatException) { }
            if (saved is not null && !TrySetPhoto(saved, out _)) _photoPng = saved;

            _physicalOuterRadiusMm = photo.PhysicalOuterRadiusMm;
            _outer.AddRange(photo.OuterBoundary);
            _hole.AddRange(photo.HoleBoundary);
            _trace.AddRange(photo.Traces.SelectMany(t => t.Points));
            foreach (CalibrationBoundarySegment segment in photo.BoundarySegments ?? [])
                _segments.Add(new BoundarySegmentDraft(segment.Id, segment.Points.ToList()));
            _bandEdges.AddRange(photo.RingObservations ?? []);
            _rectificationConfirmed = photo.RectificationConfirmed;
            if (photo.RectificationConfirmed)
            {
                DiscPhotoRectification rectification = DiscPhotoRectifier.Fit(_outer, _hole, _physicalOuterRadiusMm);
                CalibrationPhotoFitResult result = rectification.Fit;
                _rectificationKind = rectification.Kind;
                _holeRadiusUsedMm = rectification.HoleRadiusMm;
                _transform = result.Succeeded ? result.Transform : null;
                RectifyMessage = result.Succeeded
                    ? result.Message
                    : "The saved rectification could not be repeated; the saved points can be fitted, but new points need a new rectification.";
            }
        }

        ClickMode = _rectificationConfirmed && _transform is not null ? CalibrationClickMode.Measure : CalibrationClickMode.OuterRim;
        StatusMessage = $"Opened {Path.GetFileName(fullPath)}. {StageInstructions}";
        Changed(clearFit: true);
        OnPropertyChanged(nameof(RecordPath));
        OnPropertyChanged(nameof(HasRecord));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(StageInstructions));
        OnPropertyChanged(nameof(MeasureStage));
        return true;
    }

    /// <summary>
    /// Uses a new photo (PNG bytes). The outlines and measurement points of the previous photo are cleared: they
    /// belong to that picture.
    /// </summary>
    public bool LoadPhoto(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        ClearPhotoState();
        _recordPhoto = null;
        if (!TrySetPhoto(png, out string? error))
        {
            StatusMessage = error!;
            Changed(clearFit: true);
            return false;
        }

        ClickMode = CalibrationClickMode.OuterRim;
        StatusMessage = string.Create(CultureInfo.InvariantCulture,
            $"Photo opened ({_photoWidth} x {_photoHeight} pixels). Click at least five points spread around the outer rim, then at least five on the edge of the centre hole, then Rectify.");
        Changed(clearFit: true);
        return true;
    }

    private bool TrySetPhoto(byte[] png, out string? error)
    {
        if (!TryReadPngSize(png, out int width, out int height))
        {
            error = "The photo is not a PNG picture.";
            return false;
        }

        if (!CalibrationPhotoLimits.Contains(width, height)
            || png.Length > CalibrationPhotoLimits.MaxEncodedPhotoLength / 4 * 3)
        {
            error = "The photo is too large.";
            return false;
        }

        _photoPng = png;
        _photoWidth = width;
        _photoHeight = height;
        error = null;
        return true;
    }

    /// <summary>Reads width and height from a PNG's IHDR chunk (PNG specification, section 11.2.2).</summary>
    public static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png is null || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(signature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return false;
        uint w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    private void ClearPhotoState()
    {
        _photoPng = null;
        _photoWidth = _photoHeight = 0;
        _physicalOuterRadiusMm = 60;
        _outer.Clear();
        _hole.Clear();
        _trace.Clear();
        _segments.Clear();
        _bandEdges.Clear();
        _transform = null;
        _transformBeforeEdit = null;
        _rectificationConfirmed = false;
        _rectificationKind = DiscPhotoRectificationKind.Standard;
        _holeRadiusUsedMm = HoleRadiusMm;
        RectifyMessage = string.Empty;
    }

    // ---------------------------------------------------------------- finding the disc edges

    /// <summary>What the background search hands back: the cut-out photo and the outlines inside it.</summary>
    private sealed record EdgeSearch(byte[] CroppedPng, int Width, int Height, DiscPhotoDetection Found,
        CalibrationPoint[] Outer, CalibrationPoint[] Hole);

    /// <summary>
    /// Finds the outer rim and the centre hole in the open photo, cuts the photo down to the disc, marks both outlines
    /// and rectifies, so the person only has to check the drawn circles and click the measurement points. Falls back to
    /// the click workflow (photo and any points left as they were) when no clear rim is found.
    /// </summary>
    public async Task<bool> FindDiscEdgesAsync()
    {
        if (_photoPng is null)
        {
            StatusMessage = "Open a photo first.";
            return false;
        }

        byte[] png = _photoPng;
        IsBusy = true;
        StatusMessage = "Looking for the disc edges...";
        EdgeSearch? search;
        try
        {
            search = await Task.Run(() => SearchEdges(png));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException
            or OverflowException or OutOfMemoryException)
        {
            IsBusy = false;
            StatusMessage = "The disc edges could not be searched for automatically (" + ex.Message + "). Click the outer rim and the centre hole instead.";
            return false;
        }

        IsBusy = false;
        if (search is null)
        {
            StatusMessage = "No clear circular disc edge was found. Click at least five points around the outer rim and the centre hole instead, or retake the photo from straight above with the disc filling the frame.";
            return false;
        }

        // The cut-out is the photo from here on: outlines, points and the saved record all use its pixels.
        ClearPhotoState();
        _recordPhoto = null;
        _photoPng = search.CroppedPng;
        _photoWidth = search.Width;
        _photoHeight = search.Height;
        _outer.AddRange(search.Outer);
        _hole.AddRange(search.Hole);
        bool rectified = Rectify();
        string holeText = search.Hole.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $", hole edge at {search.Found.HoleFraction * _physicalOuterRadiusMm:0.#} mm from the centre.")
            : ", no hole edge found.";
        string found = string.Create(CultureInfo.InvariantCulture,
            $"Found the disc and cut the photo down to it ({search.Width} x {search.Height} pixels): rim from {search.Found.RimEdgePoints} edge readings, scatter {search.Found.RimScatterPixels:0.#} px")
            + holeText;
        if (rectified)
        {
            RectifyMessage = found + " " + RectifyMessage;
            StatusMessage = found + " " + StageInstructions;
        }
        else
        {
            ClickMode = CalibrationClickMode.OuterRim;
            StatusMessage = found + " Rectify did not accept the outlines (" + RectifyMessage + ") Correct them by clicking, or retake the photo.";
        }

        OnPropertyChanged(nameof(RectifyMessage));
        return rectified;
    }

    /// <summary>The slow part, run off the UI thread: decode, detect, cut out, re-encode.</summary>
    private static EdgeSearch? SearchEdges(byte[] png)
    {
        BgraRaster photo = PngRaster.Read(new MemoryStream(png, writable: false));
        DiscPhotoDetection? found = DiscPhotoDetector.Detect(photo);
        if (found is null) return null;
        (BgraRaster cropped, int left, int top) = DiscPhotoDetector.CropToDisc(photo, found);
        CalibrationPoint Shift(CalibrationPoint p) => new(p.X - left, p.Y - top);
        using var stream = new MemoryStream();
        PngRaster.Write(stream, cropped);
        return new EdgeSearch(stream.ToArray(), cropped.Width, cropped.Height, found,
            found.OuterPoints.Select(Shift).ToArray(), found.HolePoints.Select(Shift).ToArray());
    }

    // ---------------------------------------------------------------- clicking

    /// <summary>Adds a click at a photo pixel position, as the current mode says.</summary>
    public bool AddPoint(double pixelX, double pixelY)
    {
        if (_photoPng is null)
        {
            StatusMessage = "Open a photo first.";
            return false;
        }

        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)
            || pixelX < 0 || pixelY < 0 || pixelX >= _photoWidth || pixelY >= _photoHeight)
        {
            StatusMessage = "Click inside the photo.";
            return false;
        }

        var pixel = new CalibrationPoint(pixelX, pixelY);
        switch (_clickMode)
        {
            case CalibrationClickMode.OuterRim:
            case CalibrationClickMode.CenterHole:
                List<CalibrationPoint> outline = _clickMode == CalibrationClickMode.OuterRim ? _outer : _hole;
                if (outline.Count >= MaxOutlinePoints)
                {
                    StatusMessage = $"An outline holds at most {MaxOutlinePoints} points.";
                    return false;
                }

                outline.Add(pixel);
                DropRectification();
                StatusMessage = "Outline point added. Rectify again when both outlines are marked.";
                break;

            default:
                if (!TryAddMeasurement(pixel)) return false;
                break;
        }

        Changed(clearFit: true);
        return true;
    }

    private bool TryAddMeasurement(CalibrationPoint pixel)
    {
        if (_record is null)
        {
            StatusMessage = "Open the target's calibration record first.";
            return false;
        }

        if (!_rectificationConfirmed || _transform is null)
        {
            StatusMessage = "Rectify the photo before clicking measurement points.";
            return false;
        }

        CalibrationPoint mm;
        try
        {
            mm = _transform.SourceToDisc(pixel);
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }

        double radius = Math.Sqrt(mm.X * mm.X + mm.Y * mm.Y);
        if (radius > _physicalOuterRadiusMm || radius < HoleRadiusMm)
        {
            StatusMessage = "Click on the disc, between the centre hole and the outer rim.";
            return false;
        }

        switch (MeasureStage)
        {
            case CalibrationMeasureStage.Trace:
                _trace.Add(mm);
                break;
            case CalibrationMeasureStage.BoundarySegments:
                if (_segments.Count == 0) _segments.Add(new BoundarySegmentDraft(0, new List<CalibrationPoint>()));
                _segments[^1].Points.Add(mm);
                break;
            case CalibrationMeasureStage.BandEdges:
                if (_bandEdges.Count >= BandEdgeCapacity)
                {
                    StatusMessage = $"All {BandEdgeCapacity} band edges are marked. Undo to change one.";
                    return false;
                }

                _bandEdges.Add(new CalibrationRingObservation(0, radius, [mm]));
                NumberBandEdges();
                break;
            default:
                StatusMessage = StageInstructions;
                return false;
        }

        StatusMessage = string.Create(CultureInfo.InvariantCulture, $"Point added at {mm.X:0.00}, {mm.Y:0.00} mm (radius {radius:0.000} mm).");
        return true;
    }

    /// <summary>
    /// Numbers the band edges by radius. Upstream numbers the inner edges of the wide bands 0..n-1 and their outer
    /// edges n..2n-1 (CalibrationRingObservation), so from the centre out the order is 0, n, 1, n+1, ...
    /// </summary>
    private void NumberBandEdges()
    {
        int bands = BandEdgeCapacity / 2;
        var byRadius = _bandEdges.Select((edge, i) => (edge, i)).OrderBy(x => x.edge.RadiusMm).ToList();
        for (int k = 0; k < byRadius.Count; k++)
        {
            int index = k / 2 + (k % 2) * bands;
            _bandEdges[byRadius[k].i] = byRadius[k].edge with { RingIndex = index };
        }
    }

    /// <summary>Starts a new boundary segment (boundary targets only).</summary>
    public void StartNewSegment()
    {
        if (MeasureStage != CalibrationMeasureStage.BoundarySegments)
        {
            StatusMessage = "Segments are used on boundary targets only.";
            return;
        }

        if (_segments.Count > 0 && _segments[^1].Points.Count == 0)
        {
            StatusMessage = "The current segment is still empty.";
            return;
        }

        int id = _segments.Count == 0 ? 0 : _segments.Max(s => s.Id) + 1;
        _segments.Add(new BoundarySegmentDraft(id, new List<CalibrationPoint>()));
        ClickMode = CalibrationClickMode.Measure;
        StatusMessage = $"Segment {_segments.Count} started.";
        Changed(clearFit: false);
    }

    /// <summary>Removes the last point of the current mode (or an empty new boundary segment).</summary>
    public void Undo()
    {
        bool removed = false;
        switch (_clickMode)
        {
            case CalibrationClickMode.OuterRim:
            case CalibrationClickMode.CenterHole:
                List<CalibrationPoint> outline = _clickMode == CalibrationClickMode.OuterRim ? _outer : _hole;
                if (outline.Count > 0)
                {
                    outline.RemoveAt(outline.Count - 1);
                    DropRectification();
                    removed = true;
                }

                break;
            default:
                switch (MeasureStage)
                {
                    case CalibrationMeasureStage.Trace when _trace.Count > 0:
                        _trace.RemoveAt(_trace.Count - 1);
                        removed = true;
                        break;
                    case CalibrationMeasureStage.BoundarySegments when _segments.Count > 0:
                        if (_segments[^1].Points.Count == 0) _segments.RemoveAt(_segments.Count - 1);
                        else _segments[^1].Points.RemoveAt(_segments[^1].Points.Count - 1);
                        removed = true;
                        break;
                    case CalibrationMeasureStage.BandEdges when _bandEdges.Count > 0:
                        _bandEdges.RemoveAt(_bandEdges.Count - 1);
                        NumberBandEdges();
                        removed = true;
                        break;
                }

                break;
        }

        StatusMessage = removed ? "Last point removed." : "Nothing to undo in this mode.";
        if (removed) Changed(clearFit: true);
    }

    private void DropRectification()
    {
        if (_transform is not null) _transformBeforeEdit = _transform;
        _transform = null;
        _rectificationConfirmed = false;
        RectifyMessage = string.Empty;
    }

    // ---------------------------------------------------------------- rectifying

    /// <summary>
    /// Fits the photo's perspective to the two physical circles (outer rim, 120 mm; centre hole, 15 mm) marked on
    /// it. Measurement points taken before are moved to the new rectification.
    /// </summary>
    public bool Rectify()
    {
        if (_photoPng is null)
        {
            StatusMessage = "Open a photo first.";
            return false;
        }

        DiscPhotoRectification rectification = DiscPhotoRectifier.Fit(_outer, _hole, _physicalOuterRadiusMm);
        CalibrationPhotoFitResult result = rectification.Fit;
        RectifyMessage = result.Message;
        if (!result.Succeeded || result.Transform is null)
        {
            _transform = null;
            _rectificationConfirmed = false;
            StatusMessage = "Rectification failed: " + result.Message;
            Changed(clearFit: true);
            return false;
        }

        if (_transformBeforeEdit is { } previous) MoveMeasurements(previous, result.Transform);
        _transformBeforeEdit = null;
        _transform = result.Transform;
        _rectificationKind = rectification.Kind;
        _holeRadiusUsedMm = rectification.HoleRadiusMm;
        _rectificationConfirmed = true;
        ClickMode = CalibrationClickMode.Measure;
        StatusMessage = "Rectified. Check that the drawn outlines follow the disc edges, then click the measurement points.";
        Changed(clearFit: true);
        return true;
    }

    private void MoveMeasurements(CalibrationPhotoTransform from, CalibrationPhotoTransform to)
    {
        CalibrationPoint Move(CalibrationPoint mm) => to.SourceToDisc(from.DiscToSource(mm));
        for (int i = 0; i < _trace.Count; i++) _trace[i] = Move(_trace[i]);
        foreach (BoundarySegmentDraft segment in _segments)
            for (int i = 0; i < segment.Points.Count; i++) segment.Points[i] = Move(segment.Points[i]);
        for (int i = 0; i < _bandEdges.Count; i++)
        {
            if (_bandEdges[i].Points is not { Count: > 0 } points) continue;
            CalibrationPoint[] moved = points.Select(Move).ToArray();
            _bandEdges[i] = _bandEdges[i] with { Points = moved, RadiusMm = Math.Sqrt(moved[0].X * moved[0].X + moved[0].Y * moved[0].Y) };
        }

        if (_bandEdges.Count > 0) NumberBandEdges();
    }

    // ---------------------------------------------------------------- record, fit, store

    /// <summary>The record as it would be saved now: the opened target with this photo and these points.</summary>
    public CalibrationSession BuildSession()
    {
        if (_record is null) throw new InvalidOperationException("Open the target's calibration record first.");
        if (_photoPng is null) throw new InvalidOperationException("Open a photo of the burned disc first.");
        CalibrationTarget target = _record.Target;
        CalibrationMeasureStage stage = MeasureStage;
        IReadOnlyList<CalibrationCrossTrace> traces = stage == CalibrationMeasureStage.Trace
            ? [new CalibrationCrossTrace(0, _trace.ToArray())]
            : _recordPhoto?.Traces.Where(t => t.Points.Count == 0).ToArray() ?? [];
        IReadOnlyList<CalibrationBoundarySegment>? segments = stage == CalibrationMeasureStage.BoundarySegments
            ? _segments.Where(s => s.Points.Count > 0).Select(s => new CalibrationBoundarySegment(s.Id, s.Points.ToArray())).ToArray()
            : null;
        IReadOnlyList<CalibrationRingObservation>? rings = stage == CalibrationMeasureStage.BandEdges && _bandEdges.Count > 0
            ? _bandEdges.ToArray()
            : null;

        string base64 = Convert.ToBase64String(_photoPng);
        CalibrationPhotoSession photo = _recordPhoto is { } saved
            ? saved with
            {
                ImagePngBase64 = base64,
                OuterBoundary = _outer.ToArray(),
                HoleBoundary = _hole.ToArray(),
                RectificationConfirmed = _rectificationConfirmed,
                Traces = traces,
                RingObservations = rings,
                BoundarySegments = segments,
            }
            : new CalibrationPhotoSession(base64, _outer.ToArray(), _hole.ToArray(), _physicalOuterRadiusMm,
                _rectificationConfirmed, traces,
                IsCoarseMode: stage == CalibrationMeasureStage.BandEdges,
                RingObservations: rings, BoundarySegments: segments);
        // A confirmed fit saved by upstream belongs to the points it was made from.
        return _record with { Photo = photo, ConfirmedFit = null };
    }

    /// <summary>Fits the current points with the same call as <c>disc-art calibrate fit</c>.</summary>
    public async Task FitAsync()
    {
        CalibrationSession session;
        try
        {
            session = BuildSession();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        IsBusy = true;
        StatusMessage = "Fitting...";
        try
        {
            CalibrationRecordFit fit = await Task.Run(() => CalibrationRecordFitter.Fit(session));
            ApplyFit(fit);
        }
        catch (ArgumentException ex)
        {
            ClearFit();
            StatusMessage = "The fit could not run: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFit(CalibrationRecordFit fit)
    {
        ClearFit();
        _fit = fit;
        IReadOnlyList<CalibrationFitCandidate> candidates = fit.Result.Candidates;
        for (int i = 0; i < candidates.Count; i++)
        {
            CalibrationFitCandidate c = candidates[i];
            Candidates.Add(new CalibrationCandidateRow(i + 1, c, string.Create(CultureInfo.InvariantCulture,
                $"{i + 1}: inner {c.Parameters.InnerRadiusMm:0.######} mm, outer {c.Parameters.OuterRadiusMm:0.######} mm, deviation {c.RmsErrorMm:0.####} mm ({c.OutlierCount} outlier(s))")));
        }

        int? chosen = CalibrationRecordFitter.DefaultCandidateIndex(fit);
        SelectedCandidate = chosen is int index ? Candidates[index] : null;
        var lines = new List<string>
        {
            FormattableString.Invariant($"{fit.PointCount} point(s) fitted")
                + (fit.RadiusRangeMm > 0
                    ? FormattableString.Invariant($", search range +/-{fit.RadiusRangeMm:0.###} mm{(fit.RangeExpanded ? " (widened)" : string.Empty)}")
                    : string.Empty)
                + ".",
        };
        lines.AddRange(fit.Result.Warnings.Select(w => "Note: " + w));
        if (SelectedCandidate is { } row)
        {
            CalibrationParameters p = row.Candidate.Parameters;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Result: inner {p.InnerRadiusMm:R} mm, outer {p.OuterRadiusMm:R} mm; pitch {p.PitchLinear:R}, {p.PitchQuadratic:R}, {p.PitchCubic:R}"));
        }

        FitSummary = string.Join(Environment.NewLine, lines);
        StatusMessage = SelectedCandidate is not null
            ? "Fit done. Enter the drive model and media ID, then save."
            : candidates.Count > 0
                ? "The fit is flagged (see the notes). Check the points, or pick a candidate yourself before saving."
                : "The fit gave no result. Check the points.";
    }

    private void ClearFit()
    {
        _fit = null;
        Candidates.Clear();
        SelectedCandidate = null;
        FitSummary = string.Empty;
    }

    /// <summary>Saves the selected candidate for the drive model and media ID in the calibration store.</summary>
    public bool SaveToStore()
    {
        if (_record is null || _fit is null || SelectedCandidate is null)
        {
            StatusMessage = "Fit first and select a candidate.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DriveModel) || string.IsNullOrWhiteSpace(MediaId))
        {
            StatusMessage = "Enter both the drive model and the media ID (as the drive's media information shows it).";
            return false;
        }

        CalibrationFitCandidate chosen = SelectedCandidate.Candidate;
        try
        {
            string path = Path.GetFullPath(StorePath);
            CalibrationStore.Save(path, new CalibrationStoreEntry(DriveModel, MediaId, chosen.Parameters,
                DateTimeOffset.UtcNow, _record.Target.Id, CalibrationRecordFitter.StageName(_record.Target), chosen.RmsErrorMm));
            StatusMessage = $"Saved for drive '{DriveModel.Trim()}' and media '{MediaId.Trim()}' in {path}.";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException)
        {
            StatusMessage = "Could not save the calibration: " + ex.Message;
            return false;
        }
    }

    /// <summary>Saves the record with this photo and these points, for later work or for the command line.</summary>
    public bool SaveRecord(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            CalibrationSessionJson.Save(fullPath, BuildSession());
            _recordPath = fullPath;
            OnPropertyChanged(nameof(RecordPath));
            StatusMessage = $"Record saved: {fullPath}";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            StatusMessage = "Could not save the record: " + ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- dialogs (UI thread only)

    private void BrowseRecord()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open calibration record",
            Filter = "Calibration record (*.calibration.json)|*.calibration.json|JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog() == true) OpenRecord(dialog.FileName);
    }

    private void BrowsePhoto()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open photo of the burned disc",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            // The disc edges are found at once; clicking is only for correcting them.
            if (LoadPhoto(CalibrationPhotoFile.LoadAsPng(dialog.FileName, MaxPhotoSide))) _ = FindDiscEdgesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusMessage = "Could not open the photo: " + ex.Message;
        }
    }

    private void BrowseSaveRecord()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save calibration record",
            Filter = "Calibration record (*.calibration.json)|*.calibration.json",
            FileName = _recordPath is null ? "photo" + CalibrationTargetFile.RecordExtension : Path.GetFileName(_recordPath),
            InitialDirectory = _recordPath is null ? string.Empty : Path.GetDirectoryName(_recordPath) ?? string.Empty,
        };
        if (dialog.ShowDialog() == true) SaveRecord(dialog.FileName);
    }

    // ---------------------------------------------------------------- helpers

    private static CalibrationMeasureStage StageOf(CalibrationTarget target)
    {
        if (target.HasBoundaryPattern) return CalibrationMeasureStage.BoundarySegments;
        if (target.HasWideAddressBands) return CalibrationMeasureStage.BandEdges;
        if (target.HasCrossArms || target.HasSingleRadialLine) return CalibrationMeasureStage.Trace;
        return CalibrationMeasureStage.None;
    }

    private void Changed(bool clearFit)
    {
        if (clearFit) ClearFit();
        RebuildMarkers();
        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(PhotoPng));
        OnPropertyChanged(nameof(PhotoPixelWidth));
        OnPropertyChanged(nameof(PhotoPixelHeight));
        OnPropertyChanged(nameof(IsRectified));
        OnPropertyChanged(nameof(OuterPointCount));
        OnPropertyChanged(nameof(HolePointCount));
        OnPropertyChanged(nameof(MeasurePointCount));
        OnPropertyChanged(nameof(BoundarySegmentCount));
        OnPropertyChanged(nameof(PointCountsText));
    }

    private void RebuildMarkers()
    {
        Markers.Clear();
        if (_photoPng is null) return;
        double size = Math.Max(5, Math.Max(_photoWidth, _photoHeight) / 120.0);
        if (_transform is { } transform)
        {
            // A rim-only rectification did not use the hole outline, so no hole circle is drawn as if it had been.
            double[] drawn = _rectificationKind == DiscPhotoRectificationKind.RimOnly
                ? [_physicalOuterRadiusMm]
                : [_physicalOuterRadiusMm, _holeRadiusUsedMm];
            foreach (double radius in drawn)
            {
                for (int i = 0; i < OutlineDots; i++)
                {
                    double angle = 2 * Math.PI * i / OutlineDots;
                    if (TryToPixel(transform, new CalibrationPoint(radius * Math.Cos(angle), radius * Math.Sin(angle)), out CalibrationPoint p))
                        Markers.Add(new CalibrationMarker(p.X, p.Y, CalibrationMarkerKind.Outline, size / 2.5));
                }
            }
        }

        foreach (CalibrationPoint p in _outer) Markers.Add(new CalibrationMarker(p.X, p.Y, CalibrationMarkerKind.OuterRim, size));
        foreach (CalibrationPoint p in _hole) Markers.Add(new CalibrationMarker(p.X, p.Y, CalibrationMarkerKind.CenterHole, size));
        if (_transform is null) return;
        IEnumerable<CalibrationPoint> measured = _trace
            .Concat(_segments.SelectMany(s => s.Points))
            .Concat(_bandEdges.SelectMany(e => e.Points ?? []));
        foreach (CalibrationPoint mm in measured)
        {
            if (TryToPixel(_transform, mm, out CalibrationPoint p))
                Markers.Add(new CalibrationMarker(p.X, p.Y, CalibrationMarkerKind.Measure, size));
        }
    }

    private static bool TryToPixel(CalibrationPhotoTransform transform, CalibrationPoint mm, out CalibrationPoint pixel)
    {
        try
        {
            pixel = transform.DiscToSource(mm);
            return true;
        }
        catch (ArgumentException)
        {
            pixel = mm;
            return false;
        }
    }
}
