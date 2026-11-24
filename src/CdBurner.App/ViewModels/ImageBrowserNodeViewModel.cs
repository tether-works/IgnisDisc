using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.Core.Images;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View-model node representing a directory or file entry in the Disc Image Studio content browser tree.
/// </summary>
public sealed class ImageBrowserNodeViewModel : ObservableObject
{
    private readonly Action? _onCheckChanged;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isChecked;

    public ImageBrowserNodeViewModel(DiscImageEntryInfo entry, Action? onCheckChanged = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _onCheckChanged = onCheckChanged;
    }

    public DiscImageEntryInfo Entry { get; }

    public string Name => Entry.Name;
    public string RelativePath => Entry.RelativePath;
    public string VirtualPath => Entry.VirtualPath;
    public bool IsDirectory => Entry.IsDirectory;
    public long SizeBytes => Entry.SizeBytes;
    public DateTime TimestampUtc => Entry.TimestampUtc;
    public FileAttributes Attributes => Entry.Attributes;
    public string FlagsSummary => Entry.FlagsSummary;
    public int StartLba => Entry.StartLba;
    public string FileSystemSource => Entry.FileSystemSource;

    public string IconGlyph => Entry.IsDirectory ? "📁" : "📄";
    public string SizeDisplay => Entry.IsDirectory ? "<DIR>" : FormatBytes(Entry.SizeBytes);
    public string TimestampDisplay => Entry.TimestampUtc > DateTime.MinValue
        ? Entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
        : "-";
    public string StartLbaDisplay => $"LBA {Entry.StartLba:N0}";

    public ObservableCollection<ImageBrowserNodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onCheckChanged?.Invoke();
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }
        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }
        return $"{bytes:N0} B";
    }
}
