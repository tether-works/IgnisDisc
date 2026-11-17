using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Disc Art screen. The only code here turns a drag on the placement preview into a move and the mouse wheel into a
/// scale; everything else is bound to <see cref="DiscArtViewModel"/>.
/// </summary>
public partial class DiscArtView : UserControl
{
    private Point? _dragFrom;

    public DiscArtView()
    {
        InitializeComponent();
    }

    private void DiscSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DiscArtViewModel { HasPicture: true, IsBusy: false }) return;
        _dragFrom = e.GetPosition(DiscSurface);
        DiscSurface.CaptureMouse();
        e.Handled = true;
    }

    private void DiscSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || DataContext is not DiscArtViewModel vm) return;
        // The preview is shown at its own pixel size, so the drag distance is in preview pixels.
        Point now = e.GetPosition(DiscSurface);
        vm.MoveByPreviewPixels(now.X - from.X, now.Y - from.Y);
        _dragFrom = now;
    }

    private void DiscSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        DiscSurface.ReleaseMouseCapture();
    }

    private void DiscSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not DiscArtViewModel { HasPicture: true, IsBusy: false } vm) return;
        vm.Scale = Math.Round(vm.Scale * (e.Delta > 0 ? 1.05 : 1 / 1.05), 3);
        e.Handled = true;
    }
}
