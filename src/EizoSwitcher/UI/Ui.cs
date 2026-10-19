using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using WpfUi = Wpf.Ui.Controls;

namespace EizoSwitcher.UI;

/// <summary>Combo box entry: a token (mode name, "@black", …) with a display text.</summary>
internal sealed record Opt(string Token, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Factory helpers so the code-built WPF-UI pages stay readable.</summary>
internal static class Ui
{
    /// <summary>Screenshot mode: hides real serial numbers.</summary>
    public static bool Demo { get; set; }
    public static List<string> DemoSerials { get; } = [];

    public static string Serial(string serial) => Demo && serial.Length > 0 ? "12345678" : serial;

    public static string Mask(string text, string serial = "")
    {
        if (!Demo) return text;
        foreach (var s in DemoSerials.Append(serial).Where(s => s.Length > 0)) text = text.Replace(s, "12345678");
        return text;
    }

    public static ImageSource AppIcon { get; } = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/EizoSwitcher.ico"));

    public static System.Drawing.Icon TrayIcon(System.Drawing.Size size)
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("EizoSwitcher.ico")!;
        return new System.Drawing.Icon(s, size);
    }

    // ------------------------------------------------------------ text

    public static WpfUi.TextBlock Title(string text) =>
        new() { Text = text, FontTypography = FontTypography.Title, Margin = new Thickness(0, 0, 0, 4) };

    public static WpfUi.TextBlock Section(string text) =>
        new() { Text = text, FontTypography = FontTypography.BodyStrong, Margin = new Thickness(2, 18, 0, 6) };

    public static WpfUi.TextBlock Body(string text, bool secondary = false, bool wrap = true)
    {
        var t = new WpfUi.TextBlock { Text = text, FontTypography = FontTypography.Body, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        if (secondary) t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    public static WpfUi.TextBlock Caption(string text, bool wrap = true)
    {
        var t = new WpfUi.TextBlock { Text = text, FontTypography = FontTypography.Caption, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    // ------------------------------------------------------------ controls

    public static WpfUi.Button Button(string text, Action click, SymbolRegular? icon = null, bool primary = false)
    {
        var b = new WpfUi.Button
        {
            Content = text,
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 0),
        };
        if (icon != null) b.Icon = new SymbolIcon { Symbol = icon.Value };
        b.Click += (_, _) => click();
        return b;
    }

    public static WpfUi.Button IconButton(SymbolRegular icon, string tip, Action click)
    {
        var b = new WpfUi.Button { Icon = new SymbolIcon { Symbol = icon }, Appearance = ControlAppearance.Transparent, Padding = new Thickness(6), ToolTip = tip, BorderThickness = new Thickness(0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static ToggleSwitch Toggle(bool value, Action changed)
    {
        var t = new ToggleSwitch { IsChecked = value };
        t.Checked += (_, _) => changed();
        t.Unchecked += (_, _) => changed();
        return t;
    }

    public static ComboBox Combo(IEnumerable<Opt> options, string? selected, double width, Action? changed = null)
    {
        var list = options.ToList();
        if (selected != null && list.All(o => !o.Token.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Opt(selected, selected + "  ⚠"));
        var box = new ComboBox { ItemsSource = list, Width = width, MinWidth = 0 };
        box.SelectedItem = list.FirstOrDefault(o => o.Token.Equals(selected ?? "", StringComparison.OrdinalIgnoreCase)) ?? list[0];
        if (changed != null) box.SelectionChanged += (_, _) => changed();
        return box;
    }

    public static string Token(ComboBox box) => (box.SelectedItem as Opt)?.Token ?? "";

    /// <summary>A row like in the Windows 11 settings: icon, title, description – control on the right.</summary>
    public static CardControl Setting(SymbolRegular icon, string title, string? description, object? content)
    {
        var header = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(Body(title));
        if (!string.IsNullOrEmpty(description)) header.Children.Add(Caption(description));
        return new CardControl
        {
            Icon = new SymbolIcon { Symbol = icon },
            Header = header,
            Content = content,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && p.Children.Count > 0) fe.Margin = new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(c);
        }
        return p;
    }

    /// <summary>Scrollable page with a readable maximum width.</summary>
    public static ScrollViewer Page(params UIElement[] children)
    {
        var col = new StackPanel { Margin = new Thickness(32, 20, 32, 32), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in children) col.Children.Add(c);
        return new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
