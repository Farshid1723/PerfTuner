using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PerfTuner.App.ViewModels;

/// <summary>Visible when the bound number is 0 (for "nothing here yet" hints).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
