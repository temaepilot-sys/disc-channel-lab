using System.Drawing;
using Forms = System.Windows.Forms;

namespace DiscChannelLab.Visualizer;

public sealed class BrowserSelectionDialog : Forms.Form
{
    private readonly string _preferencePath;
    private readonly bool _japanese;
    private readonly Forms.ListBox _browsers = new() { Dock = Forms.DockStyle.Fill, DisplayMember = nameof(ViewerBrowser.Name), IntegralHeight = false };
    private readonly Forms.Label _path = new() { Dock = Forms.DockStyle.Fill, AutoEllipsis = true };
    private readonly Forms.Label _message = new() { Dock = Forms.DockStyle.Fill };
    private readonly Forms.Button _open = new() { AutoSize = true, Enabled = false };
    public ViewerBrowser? SelectedBrowser => _browsers.SelectedItem as ViewerBrowser;
    private string T(string en, string ja) => _japanese ? ja : en;

    public BrowserSelectionDialog(string preferencePath, bool japanese)
    {
        _preferencePath = preferencePath; _japanese = japanese;
        Text = T("3D Viewer — Choose browser", "3Dビュー — ブラウザーを選択");
        Size = new Size(660, 450); MinimumSize = new Size(550, 400);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 24, 33); ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 10); MinimizeBox = false; MaximizeBox = false;
        var layout = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Forms.Padding(18) };
        foreach (var height in new[] { 42f, 0, 48, 44, 36, 44 }) layout.RowStyles.Add(height == 0 ? new(Forms.SizeType.Percent, 100) : new(Forms.SizeType.Absolute, height));
        layout.Controls.Add(new Forms.Label { Text = T("Choose the browser for the 3D viewer.", "3Dビューに使用するブラウザーを選んでください。"), Dock = Forms.DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        _browsers.BackColor = Color.FromArgb(31, 42, 55); _browsers.ForeColor = Color.Gainsboro; _browsers.ItemHeight = 28;
        layout.Controls.Add(_browsers, 0, 1); layout.Controls.Add(_path, 0, 2);
        layout.Controls.Add(new Forms.Label { Dock = Forms.DockStyle.Fill, Text = T("Uses a separate viewer profile. Your usual signed-in browser profile is not opened.", "ビューアー専用のプロファイルを使用し、普段のログイン済みプロファイルは開きません。") }, 0, 3);
        _message.ForeColor = Color.LightSalmon; layout.Controls.Add(_message, 0, 4);
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.RightToLeft };
        var cancel = new Forms.Button { Text = T("Cancel", "キャンセル"), AutoSize = true, DialogResult = Forms.DialogResult.Cancel };
        var refresh = new Forms.Button { Text = T("Detect again", "再検出"), AutoSize = true };
        var browse = new Forms.Button { Text = T("Browse…", "参照…"), AutoSize = true };
        _open.Text = T("Open 3D viewer", "3Dビューを開く");
        foreach (var button in new[] { _open, cancel, browse, refresh })
        { button.FlatStyle = Forms.FlatStyle.Flat; button.BackColor = Color.FromArgb(42, 62, 82); buttons.Controls.Add(button); }
        layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
        CancelButton = cancel; AcceptButton = _open;
        _browsers.SelectedIndexChanged += (_, _) =>
        {
            _open.Enabled = SelectedBrowser is not null;
            _path.Text = SelectedBrowser?.Executable ?? "";
            _message.Text = "";
        };
        _open.Click += (_, _) =>
        {
            if (SelectedBrowser is not { } selected) return;
            try { BrowserPreference.Save(_preferencePath, selected); DialogResult = Forms.DialogResult.OK; Close(); }
            catch (IOException e) { _message.Text = e.Message; }
            catch (UnauthorizedAccessException e) { _message.Text = e.Message; }
        };
        refresh.Click += (_, _) => Detect(SelectedBrowser?.Executable ?? BrowserPreference.Load(_preferencePath));
        browse.Click += (_, _) =>
        {
            using var dialog = new Forms.OpenFileDialog { Filter = "Browser (*.exe)|*.exe", Title = T("Choose a Firefox- or Chromium-based browser", "Firefox系／Chromium系ブラウザーを選択") };
            if (dialog.ShowDialog(this) != Forms.DialogResult.OK) return;
            if (BrowserCatalog.FromPath(dialog.FileName) is not { } browser)
            { _message.Text = T("Choose a supported browser executable (Firefox, LibreWolf, Chrome, Brave, etc.).", "対応ブラウザーのEXEを選んでください（Firefox、LibreWolf、Chrome、Brave等）。"); return; }
            var item = _browsers.Items.Cast<ViewerBrowser>().FirstOrDefault(b => string.Equals(b.Executable, browser.Executable, StringComparison.OrdinalIgnoreCase));
            if (item is null) { _browsers.Items.Add(browser); item = browser; }
            _browsers.SelectedItem = item;
        };
        Detect(BrowserPreference.Load(_preferencePath));
    }

    private void Detect(string? preferred)
    {
        _browsers.Items.Clear();
        foreach (var browser in BrowserCatalog.Detect(preferred)) _browsers.Items.Add(browser);
        // No first-launch browser is selected implicitly, and a missing saved browser never falls back to Edge.
        _browsers.SelectedItem = _browsers.Items.Cast<ViewerBrowser>().FirstOrDefault(b => string.Equals(b.Executable, preferred, StringComparison.OrdinalIgnoreCase));
        if (_browsers.Items.Count == 0) _message.Text = T("No supported browser found. Use Browse for a portable browser.", "対応ブラウザーが見つかりません。ポータブル版は「参照」で指定できます。");
        else if (preferred is not null && SelectedBrowser is null) _message.Text = T("The saved browser is unavailable. Choose another browser.", "前回のブラウザーが見つかりません。別のブラウザーを選択してください。");
    }

    public static Task<ViewerBrowser?> ChooseAsync(string preferencePath, bool japanese)
    {
        var completion = new TaskCompletionSource<ViewerBrowser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
                Forms.Application.EnableVisualStyles();
                using var dialog = new BrowserSelectionDialog(preferencePath, japanese);
                completion.SetResult(dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedBrowser : null);
            }
            catch (Exception e) { completion.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
