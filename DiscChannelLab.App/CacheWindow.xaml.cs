using System.Windows;

namespace Disc2Flac;

public partial class CacheWindow : Window
{
    private readonly MainViewModel _model;

    public CacheWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        Activated += (_, _) => _model.RefreshCacheControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await _model.RefreshCacheSizeAsync();
    private async void ClearCache_Click(object sender, RoutedEventArgs e) => await _model.ClearCachesAsync();
    private async void RefreshCache_Click(object sender, RoutedEventArgs e) => await _model.RefreshCacheSizeAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
