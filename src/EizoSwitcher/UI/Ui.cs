using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace EizoSwitcher.UI;

/// <summary>Small factory helpers so the code-built UI stays readable.</summary>
internal static class Ui
{
    /// <summary>Screenshot mode: hides real serial numbers.</summary>
    public static bool Demo { get; set; }

    public static readonly IBrush Subtle = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
    public static readonly IBrush SubtleBorder = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    public static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));

    public static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://EizoSwitcher/Assets/EizoSwitcher.ico")));

    public static string Serial(string serial) => Demo && serial.Length > 0 ? "12345678" : serial;

    public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, bool wrap = false, double opacity = 1) =>
        new()
        {
            Text = text, FontSize = size, FontWeight = weight, Opacity = opacity,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static TextBlock Heading(string text) => Text(text, 18, FontWeight.SemiBold);
    public static TextBlock Hint(string text) => Text(text, 12.5, wrap: true, opacity: 0.72);

    public static Button Button(string text, Action click, bool accent = false)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = glyph, Width = 30, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static CheckBox Check(string text, bool value, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        if (tip != null) ToolTip.SetTip(c, tip);
        return c;
    }

    public static Border Card(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.AddRange(children);
        return new Border
        {
            Background = Subtle, BorderBrush = SubtleBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14), Child = panel,
        };
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    /// <summary>Scrollable tab page with a readable maximum width.</summary>
    public static ScrollViewer Page(params Control[] children)
    {
        var col = Column(14, children);
        col.Margin = new Thickness(24, 18, 24, 24);
        col.MaxWidth = 980;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        return new ScrollViewer { Content = col };
    }
}
