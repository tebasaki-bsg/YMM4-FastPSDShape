using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
/// 高速PSD立ち絵のプリセットを取り込むダイアログ。
/// 設定セット（キャラ）を選び、取り込むプリセットにチェックを入れる。同じ名前があるときの扱いも選ぶ。
/// </summary>
internal sealed class ImportDialog : Window
{
    sealed class Row
    {
        public required Preset Preset { get; init; }
        public bool IsChecked { get; set; } = true;
        public string Name => Preset.Name;
    }

    readonly SettingsFile _file;
    readonly ComboBox _sets;
    readonly ObservableCollection<Row> _rows = [];
    readonly RadioButton _overwrite;

    public SettingSet? SelectedSet => _sets.SelectedIndex >= 0 ? _file.SettingSets[_sets.SelectedIndex] : null;
    public List<Preset> SelectedPresets => _rows.Where(r => r.IsChecked).Select(r => r.Preset).ToList();
    public bool Overwrite => _overwrite.IsChecked == true;

    public ImportDialog(SettingsFile file)
    {
        _file = file;
        Title = "高速PSD立ち絵のプリセットを取り込む";
        Width = 420;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "設定セット（キャラ）", Margin = new Thickness(0, 0, 0, 4) });
        _sets = new ComboBox();
        foreach (var s in file.SettingSets) _sets.Items.Add(new TextBlock { Text = $"{s.Name}（プリセット {s.Presets.Count}件）" });
        _sets.SelectionChanged += (_, _) => FillRows();
        top.Children.Add(_sets);
        top.Children.Add(new TextBlock
        {
            Text = "取り込むプリセットにチェックを入れてください。スライダー化したフォルダでの選択は、図形では「選んだ要素だけ表示」に置き換えます。",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 8, 0, 4),
        });
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

        // プリセットのある最初の設定セットを選んでおく
        int first = file.SettingSets.FindIndex(s => s.Presets.Count > 0);
        _sets.SelectedIndex = Math.Max(0, first);
    }

    void FillRows()
    {
        _rows.Clear();
        foreach (var p in SelectedSet?.Presets ?? []) _rows.Add(new Row { Preset = p });
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
          <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}" Content="{Binding Name}" VerticalAlignment="Center"/>
        </DataTemplate>
        """);
}
