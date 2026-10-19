using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using EizoSwitcher.UI;

namespace EizoSwitcher;

public sealed class App : Application
{
    public static string[] Args { get; set; } = [];

    public override void Initialize()
    {
        Name = "EizoSwitcher";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // follows the OS light/dark setting
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            int i = Array.IndexOf(Args, "--screenshots");
            if (i >= 0) Screenshots.Run(desktop, i + 1 < Args.Length ? Args[i + 1] : "screenshots");
            else _ = new AppController(this, desktop, Args.Contains("--updated"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
