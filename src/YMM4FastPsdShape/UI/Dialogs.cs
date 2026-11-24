using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FastPsd.Core.Psd;
using FastPsd.Core.Settings;

namespace YMM4FastPsdShape.UI;

/// <summary>名前を入力するだけの小さなダイアログ</summary>
internal sealed class InputDialog : Window
{
    readonly TextBox _box;
    public string Result => _box.Text.Trim();

    InputDialog(string title, string message, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(12), MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = initial };
        panel.Children.Add(_box);
        panel.Children.Add(Buttons(this, () => Result.Length > 0));
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(DependencyObject? owner, string title, string message, string initial = "")
    {
        var d = new InputDialog(title, message, initial) { Owner = owner == null ? null : GetWindow(owner) };
        return d.ShowDialog() == true ? d.Result : null;
    }

    internal static StackPanel Buttons(Window w, Func<bool> canOk)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { if (canOk()) w.DialogResult = true; };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80 };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        return row;
    }
}

/// <summary>
/// プリセットを取り込むダイアログ。設定セット（キャラ）を選び、取り込むプリセットにチェックを入れる。同じ名前があるときの扱いも選ぶ。
/// 設定セットの一覧には、高速PSD立ち絵（-ymmfastpsd.json）の設定セットのあとに「（標準PSD立ち絵）」を並べる（v1.1.0）。
/// 「（標準PSD立ち絵）」を選ぶと、YMM4 標準のPSD立ち絵の設定（PSD名-ymm.json）のプリセットを並べる。
/// PSD の横になければ「ファイルを選ぶ…」で選んでもらう。
/// </summary>
internal sealed class ImportDialog : Window
{
    sealed class Row
    {
        public required Preset Preset { get; init; }
        public bool IsChecked { get; set; } = true;
        public string Name => Preset.Name;
        /// <summary>名前の横に薄く出す説明（標準から取り込むとき、選ぶと変わるルール）</summary>
        public string Note { get; init; } = "";
    }

    public const string StandardSetName = "（標準PSD立ち絵）";

    readonly SettingsFile? _file;
    readonly int _tachieSetCount;
    readonly string _psdPath;
    readonly PsdFile _psd;
    readonly ComboBox _sets;
    readonly TextBlock _note;
    readonly StackPanel _standardRow;
    readonly TextBlock _standardSource;
    readonly ObservableCollection<Row> _rows = [];
    readonly RadioButton _overwrite;

    /// <summary>標準の設定ファイル（読み込んだら入る）。読めなかったときは StandardError に理由</summary>
    public StandardImport.Result? StandardResult { get; private set; }
    public string? StandardPath { get; private set; }
    string? _standardError;

    /// <summary>「（標準PSD立ち絵）」を選んでいるか</summary>
    public bool IsStandard => _sets.SelectedIndex == _tachieSetCount;
    /// <summary>選んでいる高速PSD立ち絵の設定セット（「（標準PSD立ち絵）」のときは null）</summary>
    public SettingSet? SelectedSet => _file != null && _sets.SelectedIndex >= 0 && _sets.SelectedIndex < _tachieSetCount ? _file.SettingSets[_sets.SelectedIndex] : null;
    public List<Preset> SelectedPresets => _rows.Where(r => r.IsChecked).Select(r => r.Preset).ToList();
    public bool Overwrite => _overwrite.IsChecked == true;

    /// <param name="tachie">高速PSD立ち絵の設定ファイル（なければ null）</param>
    public ImportDialog(SettingsFile? tachie, string psdPath, PsdFile psd)
    {
        _file = tachie;
        _tachieSetCount = tachie?.SettingSets.Count ?? 0;
        _psdPath = psdPath;
        _psd = psd;
        Title = "プリセットを取り込む";
        Width = 440;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "設定セット（キャラ）", Margin = new Thickness(0, 0, 0, 4) });
        _sets = new ComboBox();
        foreach (var s in tachie?.SettingSets ?? []) _sets.Items.Add(new TextBlock { Text = $"{s.Name}（プリセット {s.Presets.Count}件）" });
        _sets.Items.Add(new TextBlock { Text = StandardSetName });
        _sets.SelectionChanged += (_, _) => FillRows();
        top.Children.Add(_sets);

        // 「（標準PSD立ち絵）」のときだけ出す行：取り込み元のファイル名と「ファイルを選ぶ…」
        _standardRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _standardSource = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var pick = new Button { Content = "ファイルを選ぶ…", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        pick.Click += (_, _) => PickStandardFile();
        _standardRow.Children.Add(_standardSource);
        _standardRow.Children.Add(pick);
        top.Children.Add(_standardRow);

        _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 4) };
        top.Children.Add(_note);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel();
        var allRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var all = new Button { Content = "すべて選ぶ", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
        all.Click += (_, _) => SetAll(true);
        var none = new Button { Content = "すべて外す", Padding = new Thickness(8, 1, 8, 1) };
        none.Click += (_, _) => SetAll(false);
        allRow.Children.Add(all);
        allRow.Children.Add(none);
        bottom.Children.Add(allRow);
        bottom.Children.Add(new TextBlock { Text = "同じ名前のプリセットがあるとき", Margin = new Thickness(0, 10, 0, 2) });
        var rename = new RadioButton { Content = "名前の後ろに番号を付けて追加する（例: 笑顔 (2)）", GroupName = "conflict", IsChecked = true };
        _overwrite = new RadioButton { Content = "上書きする", GroupName = "conflict", Margin = new Thickness(0, 2, 0, 0) };
        bottom.Children.Add(rename);
        bottom.Children.Add(_overwrite);
        bottom.Children.Add(InputDialog.Buttons(this, Validate));
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var list = new ListBox { ItemsSource = _rows, ItemTemplate = RowTemplate() };
        root.Children.Add(list);
        Content = root;

        // PSD の横に標準の設定ファイルがあれば読んでおく（なければ「ファイルを選ぶ…」で選んでもらう）
        var beside = StandardImport.PathFor(psdPath);
        if (System.IO.File.Exists(beside)) LoadStandard(beside);

        // プリセットのある最初の設定セットを選んでおく（立ち絵になければ「（標準PSD立ち絵）」）
        int first = tachie?.SettingSets.FindIndex(s => s.Presets.Count > 0) ?? -1;
        _sets.SelectedIndex = first >= 0 ? first : _tachieSetCount;
    }

    void LoadStandard(string path)
    {
        StandardPath = path;
        try
        {
            StandardResult = ShapeStandardImport.Read(_psd, System.IO.File.ReadAllText(path));
            _standardError = null;
        }
        catch (Exception ex)
        {
            StandardResult = null;
            _standardError = ex.Message;
        }
    }

    void PickStandardFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "標準のPSD立ち絵の設定ファイルを選んでください",
            InitialDirectory = System.IO.Path.GetDirectoryName(StandardPath ?? _psdPath) ?? "",
            Filter = "標準のPSD立ち絵の設定 (*-ymm.json)|*-ymm.json|JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadStandard(dlg.FileName);
        FillRows();
    }

    void FillRows()
    {
        _rows.Clear();
        if (IsStandard)
        {
            _standardRow.Visibility = Visibility.Visible;
            var name = StandardPath == null ? null : System.IO.Path.GetFileName(StandardPath);
            _standardSource.Text = StandardPath == null
                ? $"{System.IO.Path.GetFileName(StandardImport.PathFor(_psdPath))} が PSD と同じフォルダにありません。"
                : _standardError != null ? $"{name} を読み込めませんでした（{_standardError}）"
                : StandardResult!.Set.Presets.Count == 0 ? $"{name} にはプリセットがありません。"
                : $"取り込み元: {name}（プリセット {StandardResult.Set.Presets.Count}件）";
            _note.Text = "取り込むプリセットにチェックを入れてください。標準には「!」「*」ルールがないため、そのままでは標準と同じ見た目にならないプリセットは、選んだときにアイテムのルールをオフにします（名前の横に表示）。";
            foreach (var p in StandardResult?.Set.Presets ?? []) _rows.Add(new Row { Preset = p, Note = ShapeStandardImport.RuleNote(p) });
        }
        else
        {
            _standardRow.Visibility = Visibility.Collapsed;
            _note.Text = "取り込むプリセットにチェックを入れてください。スライダー化したフォルダでの選択は、図形では「選んだ要素だけ表示」に置き換えます。";
            foreach (var p in SelectedSet?.Presets ?? []) _rows.Add(new Row { Preset = p });
        }
    }

    void SetAll(bool value)
    {
        var copy = _rows.ToList();
        _rows.Clear();
        foreach (var r in copy) { r.IsChecked = value; _rows.Add(r); }
    }

    bool Validate()
    {
        if (SelectedPresets.Count > 0) return true;
        MessageBox.Show(this, "取り込むプリセットを1つ以上選んでください。", Title);
        return false;
    }

    static DataTemplate RowTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Orientation="Horizontal">
            <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}" Content="{Binding Name}" VerticalAlignment="Center"/>
            <TextBlock Text="{Binding Note}" Opacity="0.7" Margin="6,0,0,0" VerticalAlignment="Center"/>
          </StackPanel>
        </DataTemplate>
        """);
}
