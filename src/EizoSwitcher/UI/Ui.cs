using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EizoSwitcher.UI;

/// <summary>Combo box entry: a token (mode name, "@black", …) with a display text. Dim = shown greyed out.</summary>
internal sealed record Opt(string Token, string Label, bool Dim = false)
{
    /// <summary>Not an entry: draws a separator line in combo boxes.</summary>
    public static readonly Opt Separator = new("\u0000separator", "");

    public override string ToString() => Label;
}

/// <summary>Glyphs of the "Segoe Fluent Icons" font (Windows 11; "Segoe MDL2 Assets" on Windows 10 has the same code points).</summary>
internal static class Icons
{
    public const string Apps = "", Game = "", Home = "", Display = "", Settings = "";
    public const string Add = "", Folder = "", Delete = "", Up = "", Down = "";
    public const string Refresh = "", Color = "", Power = "", Wand = "", Moon = "";
    public const string Document = "", Globe = "", Theme = "", Bell = "", Timer = "";
    public const string Download = "", Sync = "", Info = "", Heart = "", FullScreen = "";
    public const string Window = "", Library = "", Blocked = "", Search = "", Play = "";
    public const string Check = "", Repeat = "", Pause = "", Close = "", Reset = "";
}

/// <summary>Factory helpers on top of WPF's built-in Fluent theme (no extra UI library).</summary>
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

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Icon(string glyph, double size = 16) =>
        new() { Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static void Secondary(FrameworkElement e) => e.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

    // ------------------------------------------------------------ text

    public static TextBlock Title(string text) =>
        new() { Text = text, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    public static TextBlock Section(string text) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 20, 0, 8) };

    public static TextBlock Body(string text, bool secondary = false, bool wrap = true)
    {
        var t = new TextBlock { Text = text, FontSize = 14, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        if (secondary) Secondary(t);
        return t;
    }

    public static TextBlock Strong(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };

    public static TextBlock Caption(string text, bool wrap = true)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        Secondary(t);
        return t;
    }

    // ------------------------------------------------------------ controls

    public static Button Button(string text, Action click, string? icon = null, bool primary = false)
    {
        var content = icon == null ? (object)text : Row(8, Icon(icon, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = Icon(glyph, 14), ToolTip = tip, Padding = new Thickness(8), Margin = new Thickness(0, 0, 2, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static Style? toggleStyle_;

    /// <summary>A Windows 11 style on/off switch (a CheckBox with its own template).</summary>
    public static CheckBox Toggle(bool value, Action changed)
    {
        toggleStyle_ ??= (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <Setter Property="Cursor" Value="Hand"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <Grid Background="Transparent" Width="40" Height="20" VerticalAlignment="Center">
                      <Border x:Name="Track" CornerRadius="10" BorderThickness="1"
                              BorderBrush="{DynamicResource ControlStrongStrokeColorDefaultBrush}"
                              Background="{DynamicResource ControlAltFillColorSecondaryBrush}"/>
                      <Ellipse x:Name="Knob" Width="12" Height="12" HorizontalAlignment="Left" Margin="4,0,0,0"
                               Fill="{DynamicResource TextFillColorSecondaryBrush}"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Track" Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}"/>
                        <Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource AccentFillColorDefaultBrush}"/>
                        <Setter TargetName="Knob" Property="Fill" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}"/>
                        <Setter TargetName="Knob" Property="Margin" Value="24,0,0,0"/>
                      </Trigger>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Knob" Property="Width" Value="14"/>
                        <Setter TargetName="Knob" Property="Height" Value="14"/>
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.5"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
        var t = new CheckBox { IsChecked = value, Style = toggleStyle_ };
        t.Checked += (_, _) => changed();
        t.Unchecked += (_, _) => changed();
        return t;
    }

    public static ComboBox Combo(IEnumerable<Opt> options, string? selected, double width, Action? changed = null)
    {
        var list = options.ToList();
        if (selected != null && list.All(o => !o.Token.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Opt(selected, selected + "  ⚠"));
        var box = new ComboBox { Width = width, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        ComboBoxItem? first = null, chosen = null;
        foreach (var o in list)
        {
            if (ReferenceEquals(o, Opt.Separator))
            {
                if (box.Items.Count > 0) box.Items.Add(new Separator());
                continue;
            }
            var item = new ComboBoxItem { Content = o.Label, Tag = o };
            if (o.Dim) item.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            box.Items.Add(item);
            first ??= item;
            if (chosen == null && o.Token.Equals(selected ?? "", StringComparison.OrdinalIgnoreCase)) chosen = item;
        }
        box.SelectedItem = chosen ?? first;
        if (changed != null) box.SelectionChanged += (_, _) => changed();
        return box;
    }

    public static string Token(ComboBox box) => ((box.SelectedItem as ComboBoxItem)?.Tag as Opt)?.Token ?? "";

    /// <summary>Modes enabled on the monitor first, then a separator and the disabled ones (greyed out).</summary>
    public static IEnumerable<Opt> ModeOptions(IReadOnlyList<string> modes, IReadOnlyList<bool> enabled)
    {
        bool On(int i) => i >= enabled.Count || enabled[i];
        for (int i = 0; i < modes.Count; i++)
            if (On(i)) yield return new Opt(modes[i], modes[i]);
        if (Enumerable.Range(0, modes.Count).Any(i => !On(i)))
        {
            yield return Opt.Separator;
            for (int i = 0; i < modes.Count; i++)
                if (!On(i)) yield return new Opt(modes[i], modes[i], Dim: true);
        }
    }

    /// <summary>Card surface like in the Windows 11 settings.</summary>
    public static Border Card(UIElement content, Thickness? padding = null)
    {
        var b = new Border
        {
            Child = content, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4),
        };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    /// <summary>A row like in the Windows 11 settings: icon, title, description – control on the right.</summary>
    public static Border Setting(string icon, string title, string? description, object? content)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Icon(icon, 18));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Body(title));
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Caption(description));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (content is UIElement e)
        {
            if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(e, 2);
            grid.Children.Add(e);
        }
        else if (content is string s)
        {
            var c = Caption(s, false);
            Grid.SetColumn(c, 2);
            grid.Children.Add(c);
        }
        return Card(grid);
    }

    /// <summary>Collapsible card (Fluent Expander inside a card).</summary>
    public static Border Expander(string icon, object header, UIElement content, bool expanded = false)
    {
        var head = header is string s ? (UIElement)Body(s) : (UIElement)header;
        var exp = new System.Windows.Controls.Expander
        {
            Header = Row(14, Icon(icon, 18), head),
            Content = new Border { Child = content, Padding = new Thickness(40, 10, 0, 4) },
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return Card(exp, new Thickness(12, 8, 12, 8));
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
        var col = new StackPanel { Margin = new Thickness(32, 24, 32, 32), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in children) col.Children.Add(c);
        return new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
