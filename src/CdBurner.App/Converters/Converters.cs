using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;
using CdBurner.Core.Utilities;

namespace CdBurner.App.Converters;

/// <summary>
/// Converts between <see cref="NavigationTab"/> enum values and boolean for navigation rail toggle/radio buttons.
/// </summary>
public class NavigationTabToBooleanConverter : IValueConverter
{
    public static readonly NavigationTabToBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NavigationTab selectedTab && parameter != null)
        {
            if (parameter is NavigationTab targetTab)
            {
                return selectedTab == targetTab;
            }
            if (Enum.TryParse<NavigationTab>(parameter.ToString(), true, out var parsed))
            {
                return selectedTab == parsed;
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            if (parameter is NavigationTab targetTab)
            {
                return targetTab;
            }
            if (Enum.TryParse<NavigationTab>(parameter.ToString(), true, out var parsed))
            {
                return parsed;
            }
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a <see cref="CapacityStatus"/> enum into a Fluent Theme brush for capacity progress indicator.
/// </summary>
public class CapacityStatusToBrushConverter : IValueConverter
{
    public static readonly CapacityStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is CapacityStatus s ? s : CapacityStatus.FitsComfortably;

        string brushKey = status switch
        {
            CapacityStatus.FitsComfortably => "CapacitySafeBrush",
            CapacityStatus.NearCapacityWarning => "CapacityWarningBrush",
            CapacityStatus.OverburnRequired => "CapacityOverburnBrush",
            CapacityStatus.ExceedsMediaCapacity => "CapacityOverburnBrush",
            _ => "CapacitySafeBrush"
        };

        if (Application.Current?.Resources.Contains(brushKey) == true)
        {
            return (Brush)Application.Current.Resources[brushKey];
        }

        // Fallback brushes if Application.Current is null during testing
        return status switch
        {
            CapacityStatus.FitsComfortably => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)),
            CapacityStatus.NearCapacityWarning => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts media presence boolean to green (present) or gray (empty) indicator brush.
/// </summary>
public class MediaPresenceToBrushConverter : IValueConverter
{
    public static readonly MediaPresenceToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value is bool b && b;
        if (present)
        {
            if (Application.Current?.Resources.Contains("CapacitySafeBrush") == true)
            {
                return (Brush)Application.Current.Resources["CapacitySafeBrush"];
            }
            return new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        }

        if (Application.Current?.Resources.Contains("TextMutedBrush") == true)
        {
            return (Brush)Application.Current.Resources["TextMutedBrush"];
        }
        return new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts boolean to <see cref="Visibility"/> with static instance helper.
/// Also supports "Inverse" parameter and boolean target types.
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public static readonly BooleanToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        bool invert = parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        if (invert) b = !b;

        if (targetType == typeof(bool) || targetType == typeof(bool?))
        {
            return b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        if (value is bool b)
        {
            return invert ? !b : b;
        }

        bool vis = value is Visibility v && v == Visibility.Visible;
        return invert ? !vis : vis;
    }
}

/// <summary>
/// Inverts boolean to <see cref="Visibility"/> (true -> Collapsed, false -> Visible) or inverted boolean when targetType is bool.
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public static readonly InverseBooleanToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        if (targetType == typeof(bool) || targetType == typeof(bool?))
        {
            return !b;
        }

        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }

        return value is Visibility v && v != Visibility.Visible;
    }
}

/// <summary>
/// Inverts a boolean value (true &lt;-&gt; false) for OneWay properties (such as IsEnabled) or single-control TwoWay bindings.
/// For mutually exclusive <see cref="System.Windows.Controls.RadioButton"/> groups, use <see cref="RadioButtonBooleanConverter"/>.
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public static readonly InverseBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        return !b;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        return !b;
    }
}

/// <summary>
/// Converts a boolean property for mutually exclusive TwoWay <see cref="System.Windows.Controls.RadioButton"/> bindings.
/// Compares the source boolean against <c>ConverterParameter</c> ("True"/"False"/"Inverse", defaulting to true)
/// and returns <see cref="Binding.DoNothing"/> when unchecked so group updates never write stale values back.
/// </summary>
public class RadioButtonBooleanConverter : IValueConverter
{
    public static readonly RadioButtonBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool current = value is bool flag && flag;
        bool expected = ParseExpected(parameter);
        return current == expected;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked)
        {
            return ParseExpected(parameter);
        }

        return Binding.DoNothing;
    }

    private static bool ParseExpected(object? parameter)
    {
        if (parameter is bool b)
        {
            return b;
        }

        if (parameter is string s)
        {
            if (s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (bool.TryParse(s, out bool parsed))
            {
                return parsed;
            }
        }

        return true;
    }
}

/// <summary>
/// Converts null/non-null reference to <see cref="Visibility"/>.
/// Default: null -> Collapsed, non-null -> Visible.
/// Parameter "Inverse" or "HideWhenNotNull": null -> Visible, non-null -> Collapsed.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value == null;
        bool invert = parameter is string paramStr && (paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase) || paramStr.Equals("HideWhenNotNull", StringComparison.OrdinalIgnoreCase));

        if (invert)
        {
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts boolean state to green (success/valid) or red (error/invalid) brush.
/// </summary>
public class BooleanToBrushConverter : IValueConverter
{
    public static readonly BooleanToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isValid = value is bool b && b;
        if (isValid)
        {
            return Application.Current?.Resources["CapacitySafeBrush"] as Brush
                   ?? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        }

        return Application.Current?.Resources["CapacityOverburnBrush"] as Brush
               ?? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Formats a <see cref="FileSystemPreset"/> enum value into a readable display string.
/// </summary>
public class FileSystemPresetToStringConverter : IValueConverter
{
    public static readonly FileSystemPresetToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FileSystemPreset preset)
        {
            return preset switch
            {
                FileSystemPreset.IsoJolietUdf => "ISO 9660 + Joliet + UDF 1.02 (Universal)",
                FileSystemPreset.IsoOnlyLevel1 => "ISO 9660 Level 1 (Legacy DOS)",
                FileSystemPreset.UdfOnly201 => "UDF 2.01 + ISO (large files over 4 GB)",
                FileSystemPreset.DvdVideoBridge => "DVD-Video Bridge (UDF 1.02 + ISO 9660)",
                _ => preset.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts between <see cref="CdBurner.Core.Audio.AudioRecordingMode"/> enum values and boolean for recording mode radio buttons.
/// </summary>
public class AudioRecordingModeToBooleanConverter : IValueConverter
{
    public static readonly AudioRecordingModeToBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is CdBurner.Core.Audio.AudioRecordingMode mode && parameter != null)
        {
            if (parameter is CdBurner.Core.Audio.AudioRecordingMode targetMode)
            {
                return mode == targetMode;
            }
            if (Enum.TryParse<CdBurner.Core.Audio.AudioRecordingMode>(parameter.ToString(), true, out var parsed))
            {
                return mode == parsed;
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            if (parameter is CdBurner.Core.Audio.AudioRecordingMode targetMode)
            {
                return targetMode;
            }
            if (Enum.TryParse<CdBurner.Core.Audio.AudioRecordingMode>(parameter.ToString(), true, out var parsed))
            {
                return parsed;
            }
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a <see cref="SectorHealthStatus"/> into an indicator color brush for surface scanner visualization.
/// </summary>
public class SectorHealthStatusToBrushConverter : IValueConverter
{
    public static readonly SectorHealthStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is SectorHealthStatus s ? s : SectorHealthStatus.Good;
        return status switch
        {
            SectorHealthStatus.Good => Application.Current?.Resources["CapacitySafeBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)),
            SectorHealthStatus.Slow => Application.Current?.Resources["CapacityWarningBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            SectorHealthStatus.Damaged => Application.Current?.Resources["CapacityOverburnBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            _ => new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}



/// <summary>
/// Converts PNG bytes to a frozen <see cref="System.Windows.Media.Imaging.BitmapImage"/> (null or unreadable bytes give null).
/// </summary>
public class PngBytesToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or ArgumentException
            or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
