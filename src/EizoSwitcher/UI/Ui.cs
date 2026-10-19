namespace EizoSwitcher.UI;

/// <summary>Combo box entry: a token (mode name, "@black", …) with a display text.</summary>
internal sealed record Opt(string Token, string Label)
{
    public override string ToString() => Label;
}

internal static class Ui
{
    /// <summary>Screenshot mode: hides real serial numbers.</summary>
    public static bool Demo { get; set; }

    public static readonly Font HeadingFont = new("Segoe UI Semibold", 14f);
    public static readonly Font SectionFont = new("Segoe UI Semibold", 10.5f);
    public static readonly Font HintFont = new("Segoe UI", 8.75f);

    public static string Serial(string serial) => Demo && serial.Length > 0 ? "12345678" : serial;

    /// <summary>Screenshot mode: replaces a serial number inside other text (e.g. profile file names).</summary>
    public static string Mask(string text, string serial)
    {
        if (!Demo) return text;
        foreach (var s in DemoSerials.Append(serial).Where(s => s.Length > 0)) text = text.Replace(s, "12345678");
        return text;
    }

    /// <summary>Screenshot mode: all serial numbers that must not appear anywhere.</summary>
    public static List<string> DemoSerials { get; } = [];

    public static Icon AppIcon(Size size)
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("EizoSwitcher.ico")!;
        return new Icon(s, size);
    }

    public static Button Button(string text, Action click, bool accent = false)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 4, 8, 4) };
        if (accent) b.Font = new Font(b.Font, FontStyle.Bold);
        b.Click += (_, _) => click();
        return b;
    }

    public static CheckBox Check(string text, bool value) =>
        new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };

    public static ComboBox Combo(IEnumerable<Opt> options, string? selected, int width = 240)
    {
        var list = options.ToList();
        if (selected != null && list.All(o => !o.Token.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            list.Add(new Opt(selected, selected + "  ⚠"));
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 3, 12, 3) };
        box.Items.AddRange([.. list]);
        box.SelectedItem = list.FirstOrDefault(o => o.Token.Equals(selected ?? "", StringComparison.OrdinalIgnoreCase)) ?? list[0];
        return box;
    }

    public static string Token(ComboBox box) => (box.SelectedItem as Opt)?.Token ?? "";

    public static FlowLayoutPanel Flow(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        p.Controls.AddRange(controls);
        return p;
    }

    public static Label Text(string text, Font? font = null, bool dim = false) => new()
    {
        Text = text, AutoSize = true, Font = font, Margin = new Padding(0, 6, 12, 2),
        ForeColor = dim ? SystemColors.GrayText : SystemColors.ControlText,
    };
}

/// <summary>Builds a scrollable settings page as a two-column table (label | editor).</summary>
internal sealed class PageBuilder
{
    private const int TextWidth = 760;
    private readonly TableLayoutPanel table_ = new()
    {
        ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Location = new Point(0, 0), Padding = new Padding(24, 14, 24, 24),
    };

    public PageBuilder()
    {
        table_.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table_.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    }

    public PageBuilder Heading(string text) => Span(new Label { Text = text, Font = Ui.HeadingFont, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });

    public PageBuilder Section(string text) => Span(new Label { Text = text, Font = Ui.SectionFont, AutoSize = true, Margin = new Padding(0, 18, 0, 2) });

    public PageBuilder Hint(string text, int indent = 0) => Span(new Label
    {
        Text = text, Font = Ui.HintFont, ForeColor = SystemColors.GrayText, AutoSize = true,
        MaximumSize = new Size(TextWidth - indent, 0), Margin = new Padding(indent, 1, 0, 4),
    });

    public PageBuilder Line(string label, Control editor, string? labelSub = null)
    {
        Control left = labelSub == null
            ? new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 24, 0) }
            : Ui.Flow(new Label { Text = label, AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 4, 8, 0) },
                      new Label { Text = labelSub, AutoSize = true, ForeColor = SystemColors.GrayText, Font = Ui.HintFont, Margin = new Padding(0, 6, 24, 0) });
        int row = table_.RowCount++;
        table_.Controls.Add(left, 0, row);
        table_.Controls.Add(editor, 1, row);
        return this;
    }

    public PageBuilder Span(Control c)
    {
        int row = table_.RowCount++;
        table_.Controls.Add(c, 0, row);
        table_.SetColumnSpan(c, 2);
        return this;
    }

    public Control Build() => new Panel { AutoScroll = true, Dock = DockStyle.Fill, Controls = { table_ } };
}
