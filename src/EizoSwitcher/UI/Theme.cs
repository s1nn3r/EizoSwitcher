using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EizoSwitcher.UI;

/// <summary>Light / dark / follow Windows.</summary>
internal static class Theme
{
    public static string Current { get; private set; } = "";

    public static void Apply(string theme)
    {
        Current = theme;
        var t = theme switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(t, WindowBackdropType.Mica, true);
    }

    /// <summary>Called for every new window: follows Windows' setting changes if "System" is selected.</summary>
    public static void Attach(Window window)
    {
        if (Current is not ("light" or "dark")) SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
    }
}
