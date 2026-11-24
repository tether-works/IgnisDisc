using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CdBurner.Core.Primitives;

namespace CdBurner.App.Views;

/// <summary>
/// Modal warning shown before a CD-R is written past its rated capacity. It lists the rated capacity against the image
/// size, the excess (MB and mm:ss) and the risk of the drive's own limit, and defaults to Cancel: the burn goes ahead only
/// when the user presses Overburn.
/// </summary>
public sealed class OverburnConfirmationDialog : Window
{
    private OverburnConfirmationDialog(CdOverburnWarning warning)
    {
        Title = warning.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var text = new TextBlock
        {
            Text = warning.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        AutomationProperties.SetName(text, "Overburn warning");

        var overburn = new Button { Content = "Overburn", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsEnabled = !warning.ExceedsLimit };
        overburn.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsDefault = true, IsCancel = true };
        AutomationProperties.SetName(overburn, "Overburn");
        AutomationProperties.SetName(cancel, "Cancel");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(overburn);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(text);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Shows the warning; true only when the user pressed Overburn.</summary>
    public static bool Confirm(CdOverburnWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        var dialog = new OverburnConfirmationDialog(warning);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }
        return dialog.ShowDialog() == true;
    }
}
