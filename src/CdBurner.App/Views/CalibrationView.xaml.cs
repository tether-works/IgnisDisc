using System.Windows.Controls;
using System.Windows.Input;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Calibration screen. The only code here turns a click on the photo into a photo pixel position.
/// </summary>
public partial class CalibrationView : UserControl
{
    public CalibrationView()
    {
        InitializeComponent();
    }

    private void PhotoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CalibrationViewModel vm) return;
        // PhotoSurface is laid out in photo pixels inside the Viewbox, so its own coordinates are pixel positions.
        System.Windows.Point position = e.GetPosition(PhotoSurface);
        vm.AddPoint(position.X, position.Y);
        e.Handled = true;
    }
}
