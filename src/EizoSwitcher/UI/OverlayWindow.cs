using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Black fullscreen window that blanks one display (works with every monitor). Click hides it.</summary>
internal sealed class OverlayWindow : Window
{
    public OverlayWindow(DisplayInfo display)
    {
        Title = "EizoSwitcher";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(display.X + 8, display.Y + 8);
        Width = 200;
        Height = 120;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

        var hint = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Opacity = 0.55,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(1.5) }],
        };
        hint.Children.Add(new TextBlock { Text = "EizoSwitcher", Foreground = Brushes.Gray, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center });
        hint.Children.Add(new TextBlock { Text = L.T("Bildschirm abgedunkelt · Klicken zum Einblenden", "Display blanked · click to show"), Foreground = Brushes.DimGray, FontSize = 12 });
        Content = hint;

        // FullScreen uses the monitor the window was positioned on
        Opened += (_, _) =>
        {
            WindowState = WindowState.FullScreen;
            DispatcherTimer.RunOnce(() => hint.Opacity = 0, TimeSpan.FromSeconds(3));
        };
        PointerPressed += (_, _) => Close();
    }
}

/// <summary>Small notification in the bottom-right corner that fades out by itself.</summary>
internal sealed class ToastWindow : Window
{
    private static ToastWindow? current_;

    public static void Show(string message)
    {
        current_?.Close();
        current_ = new ToastWindow(message);
        current_.Show();
    }

    private ToastWindow(string message)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];

        var icon = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://EizoSwitcher/Assets/EizoSwitcher.ico"))), Width = 28, Height = 28 };
        Content = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x28, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 18, 10),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #60000000"),
            Child = Ui.Row(12, icon, Ui.Column(1,
                new TextBlock { Text = "EizoSwitcher", Foreground = Brushes.Gray, FontSize = 11 },
                new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 13.5, MaxWidth = 420, TextWrapping = TextWrapping.Wrap })),
        };

        Opened += (_, _) =>
        {
            var screen = Screens.Primary;
            if (screen != null)
            {
                var wa = screen.WorkingArea;
                double s = screen.Scaling;
                Position = new PixelPoint(wa.Right - (int)(Bounds.Width * s) - (int)(12 * s), wa.Bottom - (int)(Bounds.Height * s) - (int)(12 * s));
            }
            Opacity = 1;
            DispatcherTimer.RunOnce(() => Opacity = 0, TimeSpan.FromSeconds(3.5));
            DispatcherTimer.RunOnce(() => { Close(); if (current_ == this) current_ = null; }, TimeSpan.FromSeconds(3.9));
        };
        PointerPressed += (_, _) => Close();
    }
}
