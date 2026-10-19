using System.Windows;

namespace EizoSwitcher.UI;

/// <summary>Light / dark / follow Windows – WPF's built-in Fluent theme (.NET 9+).</summary>
internal static class Theme
{
    public static void Apply(string theme)
    {
#pragma warning disable WPF0001 // ThemeMode is marked experimental
        Application.Current.ThemeMode = theme switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }
}
