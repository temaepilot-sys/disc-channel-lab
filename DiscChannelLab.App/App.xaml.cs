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
        var migration = AppDataPaths.Current.ImportLegacySettings();
        LanguageService.Instance.Load();
        ThemeService.Load();
        if (migration.Errors.Count != 0)
        {
            try
            {
                var log = new AppLog();
                foreach (var message in migration.Errors) log.Write("LEGACY SETTINGS IMPORT: " + message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        base.OnStartup(e);
    }
}
