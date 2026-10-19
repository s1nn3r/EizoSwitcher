using System.Diagnostics;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Pick one or more running programs (with icon, description and window title).</summary>
internal sealed class ProcessPickerForm : Form
{
    public sealed record Entry(string ExeName, string? Path, string Description, string WindowTitle);

    private readonly TextBox search_ = new() { PlaceholderText = L.T("Suchen …", "Search …"), Width = 280, Margin = new Padding(0, 4, 12, 4) };
    private readonly CheckBox windowsOnly_ = Ui.Check(L.T("Nur Programme mit Fenster", "Only programs with a window"), true);
    private readonly ListView list_ = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly ImageList images_ = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 20) };
    private List<Entry> all_ = [];

    public List<Entry> Selected { get; } = [];

    public ProcessPickerForm()
    {
        Text = L.T("Laufendes Programm hinzufügen", "Add running program");
        Icon = Ui.AppIcon(SystemInformation.IconSize);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(820, 580);
        MinimizeBox = false;
        ShowInTaskbar = false;

        list_.SmallImageList = images_;
        list_.Columns.Add(L.T("Programm", "Program"), 300);
        list_.Columns.Add(L.T("Programmdatei", "Executable"), 160);
        list_.Columns.Add(L.T("Fenster", "Window"), 300);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        top.Controls.AddRange([search_, windowsOnly_, Ui.Button(L.T("Aktualisieren", "Refresh"), Load)]);
        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = SystemColors.GrayText, Font = Ui.HintFont, Padding = new Padding(0, 0, 0, 6),
            Text = L.T("Mehrfachauswahl per Klick. Tipp: Starte das Programm vorher, damit es hier erscheint.",
                       "Click to select several. Tip: start the program first so it shows up here."),
        };

        var add = Ui.Button(L.T("Hinzufügen", "Add"), Accept, accent: true);
        var cancel = Ui.Button(L.T("Abbrechen", "Cancel"), () => { DialogResult = DialogResult.Cancel; Close(); });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.AddRange([cancel, add]);
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
        body.Controls.Add(list_);
        body.Controls.Add(bottom);
        body.Controls.Add(hint);
        body.Controls.Add(top);
        Controls.Add(body);

        search_.TextChanged += (_, _) => Filter();
        windowsOnly_.CheckedChanged += (_, _) => Filter();
        list_.DoubleClick += (_, _) => Accept();
        Shown += (_, _) => { Load(); search_.Focus(); };
    }

    private void Accept()
    {
        Selected.AddRange(list_.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag!));
        DialogResult = DialogResult.OK;
        Close();
    }

    private new void Load()
    {
        UseWaitCursor = true;
        Task.Run(() =>
        {
            int own = Environment.ProcessId;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id <= 4 || p.Id == own) continue;
                    string? path = ProcessWatcher.PathOf(p);
                    string exe = System.IO.Path.GetFileName(path) ?? p.ProcessName + ".exe";
                    string title = "";
                    try { title = p.MainWindowTitle; } catch (InvalidOperationException) { }
                    string key = path ?? exe;
                    if (entries.TryGetValue(key, out var known) && known.WindowTitle.Length >= title.Length) continue;
                    entries[key] = new Entry(exe, path, Shell.Describe(path, System.IO.Path.GetFileNameWithoutExtension(exe)), title);
                }
            }
            return entries.Values.OrderBy(e => e.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
        }).ContinueWith(t => BeginInvoke(() =>
        {
            all_ = t.Result;
            UseWaitCursor = false;
            Filter();
        }));
    }

    private void Filter()
    {
        string q = search_.Text.Trim();
        list_.BeginUpdate();
        list_.Items.Clear();
        foreach (var e in all_.Where(e => (!windowsOnly_.Checked || e.WindowTitle.Length > 0) &&
                                          (q.Length == 0 || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                         || e.ExeName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                         || e.WindowTitle.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            string key = e.Path ?? "";
            if (key.Length > 0 && !images_.Images.ContainsKey(key) && ExeIcons.For(e.Path, 20) is { } img) images_.Images.Add(key, img);
            list_.Items.Add(new ListViewItem([e.Description, e.ExeName, e.WindowTitle], key) { Tag = e, ToolTipText = e.Path ?? e.ExeName });
        }
        list_.EndUpdate();
    }
}
