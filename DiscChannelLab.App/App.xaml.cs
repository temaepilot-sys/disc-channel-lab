using System.Configuration;
using System.Data;
using System.Windows;

namespace Disc2Flac;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LanguageService.Instance.Load();
        ThemeService.Load();
        base.OnStartup(e);
    }
}
