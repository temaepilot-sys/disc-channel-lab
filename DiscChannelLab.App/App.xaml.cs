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
        if (e.Args is ["--sofa-probe", var sofa, var report])
        {
            try { SpatialJson.Save(report, SofaLibrary.Inspect(sofa, CancellationToken.None)); Shutdown(0); }
            catch { Shutdown(2); }
            return;
        }
        var spatial = AppDataPaths.Current.ImportSpatialSettings();
        var migration = AppDataPaths.Current.ImportLegacySettings();
        var headphones = AppDataPaths.Current.ImportHeadphoneSettings();
        LanguageService.Instance.Load();
        ThemeService.Load();
        if (migration.Errors.Count != 0 || headphones.Errors.Count != 0 || spatial.Errors.Count != 0)
        {
            try
            {
                var log = new AppLog();
                foreach (var message in migration.Errors.Concat(headphones.Errors).Concat(spatial.Errors)) log.Write("LEGACY SETTINGS IMPORT: " + message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        base.OnStartup(e);
    }
}
