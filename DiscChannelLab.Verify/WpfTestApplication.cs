using System.Reflection;
using System.Windows;

internal static class WpfTestApplication
{
    public static void SuppressStartupWindow(Application app)
    {
        // .NET 10 rejects null through StartupUri's public setter. These tests create
        // and own their windows; the resource-only App must not create a second one.
        var field = typeof(Application).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(Uri) && f.Name.Contains("startup", StringComparison.OrdinalIgnoreCase));
        field.SetValue(app, null);
    }
}
