using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FastPsd.Core.Psd;
using FastPsd.Core.Settings;
using FastPsd.Core.State;
using YMM4FastPsdShape.Rendering;
using YukkuriMovieMaker.Commons;

namespace YMM4FastPsdShape.UI;

/// <summary>
/// 図形アイテムの設定欄に出す画面。上段: プリセット、中段: レイヤーツリー、下段: 状態表示。
/// ツリーのチェックはこのアイテムの差分（変えたところだけ）として記録する。
/// 高速PSD立ち絵のレイヤー欄から、スライダー・口パク・目パチを除いたもの。
/// </summary>
internal sealed class LayerTreeEditor : UserControl, IPropertyEditorControl
{
    FastPsdShapeParameter? _owner;
    PsdEntry? _entry;
    ThumbnailSet? _thumbs;
    readonly Dictionary<int, LayerNodeViewModel> _nodes = new();
    readonly List<LayerNodeViewModel> _roots = new();
    bool _refreshQueued;
    bool _updatingPresetBox;
    string? _loadedPath;

    readonly TreeView _tree;
    readonly ComboBox _presetBox;
    readonly TextBlock _status;
    readonly LayerPreviewPopup _preview = new();

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;
    void IPropertyEditorControl.SetFocus() => _tree.Focus();

    public LayerTreeEditor()
    {
        Height = 460;
        var root = new DockPanel();

        // ---- プリセット ----
        var presetRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var label = new TextBlock { Text = "プリセット", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(label, Dock.Left);
        presetRow.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SmallButton("保存", "現在の状態に名前を付けて、プリセットとして保存します（同じPSDを使う図形で共通）", SavePreset));
        buttons.Children.Add(SmallButton("名前変更", "選んでいるプリセットの名前を変えます", RenamePreset));
        buttons.Children.Add(SmallButton("削除", "選んでいるプリセットを削除します", DeletePreset));
        buttons.Children.Add(SmallButton("立ち絵から取り込み", "高速PSD立ち絵で作ったプリセットを取り込みます（立ち絵側の設定は変更しません）", ImportPresets));
        buttons.Children.Add(SmallButton("リセット", "このアイテムで変更した内容をすべて取り消し、PSDの状態に戻します", ResetOverrides));
        DockPanel.SetDock(buttons, Dock.Right);
        presetRow.Children.Add(buttons);
        _presetBox = new ComboBox { MinWidth = 120, ToolTip = "選ぶと、その内容がこのアイテムにコピーされます（その後も自由に変更できます）" };
        _presetBox.SelectionChanged += (_, _) => { if (!_updatingPresetBox) Safe("プリセットの適用", ApplyPreset); };
        presetRow.Children.Add(_presetBox);
        DockPanel.SetDock(presetRow, Dock.Top);
        root.Children.Add(presetRow);

        // ---- 状態表示 ----
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        // ---- レイヤー ----
        _tree = new TreeView { ItemTemplate = NodeTemplate() };
        VirtualizingPanel.SetIsVirtualizing(_tree, true);
        VirtualizingPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_tree, true);
        root.Children.Add(_tree);

        // ---- サムネイルの拡大表示（カーソルを合わせるとすぐ右隣に出す） ----
        _tree.PreviewMouseMove += OnTreeMouseMove;
        _tree.MouseLeave += (_, _) => _preview.Hide();
        _tree.PreviewMouseWheel += (_, _) => _preview.Hide();
        _tree.PreviewMouseDown += (_, _) => _preview.Hide();

        Content = root;
        // 注意: Unloaded では接続を切らない（高速PSD立ち絵で確認済みの落とし穴）。
        // YMM4 の編集欄は一時的に画面から外して付け直すことがあり、そのとき SetBindings は呼び直されない。
        // 接続を切るのは ClearBindings（Detach）のときだけにし、ここでは共有イベントの購読だけを外す。
        Loaded += (_, _) => { SubscribeShared(); QueueRefresh(); };
        Unloaded += (_, _) => { _preview.Hide(); UnsubscribeShared(); };
    }

    Button SmallButton(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1) };
        b.Click += (_, _) => Safe(text, action);
        return b;
    }

    /// <summary>操作中のエラーを握りつぶさず、内容を表示する</summary>
    void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            ShapeLog.Error($"「{what}」でエラー owner={ShapeLog.Id(_owner)}", ex);
            MessageBox.Show(Window.GetWindow(this), $"「{what}」でエラーが発生しました。\n{ex.GetType().Name}: {ex.Message}", "高速PSD");
        }
    }

    // ------------------------------------------------------------------
    // 接続・切断
    // ------------------------------------------------------------------

    bool _sharedSubscribed;

    public void Attach(FastPsdShapeParameter owner)
    {
        Detach();
        ShapeLog.Info($"エディタ接続 {ShapeLog.Id(this)} owner={ShapeLog.Id(owner)} psd={owner.FilePath}");
        _owner = owner;
        owner.PropertyChanged += OnOwnerChanged;
        SubscribeShared();
        LoadPsd();
    }

    /// <summary>エディタを編集対象から切り離す（YMM4 の ClearBindings から呼ばれる）</summary>
    public void Detach()
    {
        _preview.Hide();
        if (_owner != null)
        {
            ShapeLog.Info($"エディタ切断 {ShapeLog.Id(this)} owner={ShapeLog.Id(_owner)}");
            _owner.PropertyChanged -= OnOwnerChanged;
        }
        UnsubscribeShared();
        _thumbs = null;
        if (_entry != null) PsdRegistry.Release(_entry);
        _entry = null;
        _owner = null;
        _loadedPath = null;
        // 切り離した後に古いツリーが操作できる状態で残らないようにする
        _nodes.Clear();
        _roots.Clear();
        _tree.ItemsSource = null;
        _status.Text = "";
    }

    void SubscribeShared()
    {
        if (_sharedSubscribed || _owner == null) return;
        ShapePresetStore.Changed += OnPresetsChanged;
        if (_thumbs != null) _thumbs.Updated += OnThumbsUpdated;
        _sharedSubscribed = true;
    }

    void UnsubscribeShared()
    {
        if (!_sharedSubscribed) return;
        ShapePresetStore.Changed -= OnPresetsChanged;
        if (_thumbs != null) _thumbs.Updated -= OnThumbsUpdated;
        _sharedSubscribed = false;
    }

    // どれも描画スレッドなどから来ることがあるので、画面のスレッドに移してから処理する
    void OnPresetsChanged(string path) => Dispatcher.BeginInvoke(QueueRefresh);
    void OnThumbsUpdated() => Dispatcher.BeginInvoke(ApplyThumbnails);

    void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FastPsdShapeParameter.FilePath)) Dispatcher.BeginInvoke(() => { if (_owner?.FilePath != _loadedPath) LoadPsd(); });
        else Dispatcher.BeginInvoke(QueueRefresh);
    }

    void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; Refresh(); });
    }

    void LoadPsd()
    {
        _preview.Hide();
        if (_thumbs != null && _sharedSubscribed) _thumbs.Updated -= OnThumbsUpdated;
        _thumbs = null;
        if (_entry != null) PsdRegistry.Release(_entry);
        _entry = null;
        _nodes.Clear();
        _roots.Clear();
        _tree.ItemsSource = null;

        var path = _owner?.FilePath;
        _loadedPath = path;
        if (string.IsNullOrEmpty(path))
        {
            _status.Text = "PSDファイルを選んでください。";
            RefreshPresets(null);
            return;
        }
        try
        {
            _entry = PsdRegistry.Acquire(path);
        }
        catch (Exception ex)
        {
            _status.Text = $"PSDを開けませんでした: {ex.Message}";
            ShapeLog.Error($"PSDを開けませんでした: {path}", ex);
            return;
        }

        foreach (var l in _entry.Psd.Roots) _roots.Add(BuildNode(l));
        _tree.ItemsSource = _roots;
        _thumbs = ThumbnailService.Get(_entry);
        if (_sharedSubscribed) _thumbs.Updated += OnThumbsUpdated;
        ApplyThumbnails();
        Refresh();
    }

    LayerNodeViewModel BuildNode(PsdLayer l)
    {
        var vm = new LayerNodeViewModel(l, OnToggle);
        _nodes[l.Index] = vm;
        foreach (var c in l.Children) vm.Children.Add(BuildNode(c));
        return vm;
    }

    void ApplyThumbnails()
    {
        if (_thumbs == null) return;
        lock (_thumbs.Images)
            foreach (var (index, img) in _thumbs.Images)
                if (_nodes.TryGetValue(index, out var vm) && vm.Thumbnail == null) vm.Thumbnail = img;
        UpdateStatus();
    }

    /// <summary>カーソルの下がレイヤーのサムネイルなら拡大表示を出し、それ以外なら消す</summary>
    void OnTreeMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.OriginalSource is Image img && img.DataContext is LayerNodeViewModel vm && !vm.IsGroup && _entry != null)
            _preview.Show(img, _entry.Psd, vm.Layer, vm.Thumbnail);
        else
            _preview.Hide();
    }

    // ------------------------------------------------------------------
    // 表示の更新
    // ------------------------------------------------------------------

    RuleOptions Rules => new() { Asterisk = _owner?.AsteriskRule ?? true, Exclamation = _owner?.ExclamationRule ?? true };

    void Refresh()
    {
        if (_owner == null) return;
        // PSD が外部で更新されていたら開き直す
        if (_entry != null && PsdRegistry.CheckStale(_entry)) { LoadPsd(); return; }
        if (_entry == null) return;
        var psd = _entry.Psd;
        var overrides = _owner.GetOverrides();

        var state = StateResolver.Resolve(new StateInput { Psd = psd, Rules = Rules, Overrides = [overrides] });
        var touched = OverridesEditor.TouchedKeys(overrides);
        bool excl = Rules.Exclamation;

        foreach (var l in psd.All)
        {
            if (!_nodes.TryGetValue(l.Index, out var vm)) continue;
            vm.SetCheckedSilently(state.Visible[l.Index]);
            vm.IsCheckEnabled = !(excl && l.Name.StartsWith('!'));
            vm.RowOpacity = l.Parent == null || state.EffectiveVisible[l.Parent.Index] ? 1.0 : 0.45;
            vm.NameWeight = touched.Contains(l.Key) ? FontWeights.Bold : FontWeights.Normal;
        }

        RefreshPresets(overrides);
        _planWarnings = FastPsd.Core.Compose.RenderPlanner.Build(psd, state, _owner.ApplyFolderClipping).Warnings;
        UpdateStatus();
    }

    IReadOnlyList<string> _planWarnings = [];

    void UpdateStatus()
    {
        if (_entry == null) return;
        var parts = new List<string>();
        if (_thumbs != null && !string.IsNullOrEmpty(_thumbs.Status)) parts.Add(_thumbs.Status);
        var warnings = _planWarnings.Concat(_entry.Psd.Warnings).Distinct().ToList();
        if (warnings.Count > 0) parts.Add("注意: " + string.Join(" / ", warnings.Take(4)) + (warnings.Count > 4 ? $" ほか{warnings.Count - 4}件" : ""));
        _status.Text = string.Join("\n", parts);
    }

    // ------------------------------------------------------------------
    // 差分の変更
    // ------------------------------------------------------------------

    void OnToggle(LayerNodeViewModel vm, bool value)
    {
        if (_entry == null || _owner == null) return;
        var next = OverridesEditor.SetVisible(_owner.GetOverrides(), _entry.Psd, vm.Layer, value, Rules, []);
        Commit(next);
    }

    void Commit(LayerOverrides next)
    {
        if (_owner == null) return;
        var json = next.IsEmpty ? "" : next.ToJson();
        ShapeLog.Info($"差分を変更 owner={ShapeLog.Id(_owner)} visible={next.Visible.Count} select={next.Select.Count}");
        BeginEdit?.Invoke(this, EventArgs.Empty);
        _owner.OverridesJson = json;
        EndEdit?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    void ResetOverrides()
    {
        if (_owner == null || _owner.GetOverrides().IsEmpty) return;
        if (MessageBox.Show(Window.GetWindow(this), "このアイテムで変更した内容をすべて取り消しますか？", "高速PSD", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        Commit(new LayerOverrides());
    }

    // ------------------------------------------------------------------
    // プリセット（PSDごと。同じPSDを使う図形で共通）
    // ------------------------------------------------------------------

    string? PsdPath => string.IsNullOrEmpty(_owner?.FilePath) ? null : _owner!.FilePath;

    List<Preset> CurrentPresets => PsdPath is { } p ? ShapePresetStore.Get(p).Presets : [];

    void RefreshPresets(LayerOverrides? current)
    {
        _updatingPresetBox = true;
        try
        {
            var presets = CurrentPresets;
            var names = presets.Select(p => p.Name).ToList();
            if (!_presetBox.Items.Cast<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(names))
            {
                _presetBox.Items.Clear();
                foreach (var n in names) _presetBox.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = n }, Tag = n });
            }
            // 今の差分と同じプリセットがあれば、それを選択表示にする
            var json = current?.ToJson();
            var match = json == null ? null : presets.FirstOrDefault(p => p.Overrides.ToJson() == json);
            _presetBox.SelectedItem = _presetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == match?.Name);
        }
        finally { _updatingPresetBox = false; }
    }

    string? SelectedPresetName => (_presetBox.SelectedItem as ComboBoxItem)?.Tag as string;

    void ApplyPreset()
    {
        if (SelectedPresetName is not { } name) return;
        var preset = CurrentPresets.FirstOrDefault(p => p.Name == name);
        if (preset != null) Commit(preset.Overrides.Clone());
    }

    void SavePreset()
    {
        if (_owner == null || _entry == null || PsdPath is not { } path) return;
        var name = InputDialog.Ask(this, "プリセットを保存", "プリセットの名前", SelectedPresetName ?? "");
        if (string.IsNullOrEmpty(name)) return;
        if (CurrentPresets.Any(p => p.Name == name) &&
            MessageBox.Show(Window.GetWindow(this), $"「{name}」を上書きしますか？", "高速PSD", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var overrides = _owner.GetOverrides().Clone();
        ShapePresetStore.Update(path, f =>
        {
            int idx = f.Presets.FindIndex(p => p.Name == name);
            var preset = new Preset { Name = name, Overrides = overrides };
            if (idx >= 0) f.Presets[idx] = preset;
            else f.Presets.Add(preset);
        });
        Refresh();
    }

    void RenamePreset()
    {
        if (PsdPath is not { } path || SelectedPresetName is not { } name) return;
        var newName = InputDialog.Ask(this, "プリセットの名前変更", "新しい名前", name);
        if (string.IsNullOrEmpty(newName) || newName == name) return;
        if (CurrentPresets.Any(p => p.Name == newName))
        {
            MessageBox.Show(Window.GetWindow(this), $"「{newName}」という名前のプリセットはすでにあります。", "高速PSD");
            return;
        }
        ShapePresetStore.Update(path, f =>
        {
            var p = f.Presets.FirstOrDefault(p => p.Name == name);
            if (p != null) p.Name = newName;
        });
        Refresh();
    }

    void DeletePreset()
    {
        if (PsdPath is not { } path || SelectedPresetName is not { } name) return;
        if (MessageBox.Show(Window.GetWindow(this), $"プリセット「{name}」を削除しますか？\n（このプリセットを使ったアイテムの表示は変わりません）", "高速PSD", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        ShapePresetStore.Update(path, f => f.Presets.RemoveAll(p => p.Name == name));
        Refresh();
    }

    void ImportPresets()
    {
        if (_entry == null || PsdPath is not { } path) return;
        var tachie = TachiePresetImport.ReadTachieFile(path);
        if (tachie == null || tachie.SettingSets.All(s => s.Presets.Count == 0))
        {
            MessageBox.Show(Window.GetWindow(this),
                $"このPSDには、高速PSD立ち絵のプリセットがありません。\n（{System.IO.Path.GetFileName(SettingsFile.PathFor(path))} が見つからないか、プリセットが保存されていません）", "高速PSD");
            return;
        }
        var dialog = new ImportDialog(tachie) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedSet is not { } set || dialog.SelectedPresets.Count == 0) return;
        var converted = dialog.SelectedPresets.Select(p => TachiePresetImport.Convert(p, _entry.Psd, set.SliderGroups)).ToList();
        List<string> names = [];
        ShapePresetStore.Update(path, f => names = TachiePresetImport.Merge(f, converted, dialog.Overwrite));
        ShapeLog.Info($"プリセットを取り込み 設定セット={set.Name} {names.Count}件 上書き={dialog.Overwrite}");
        Refresh();
        MessageBox.Show(Window.GetWindow(this), $"{names.Count}件のプリセットを取り込みました。\n" + string.Join("\n", names.Take(15)) + (names.Count > 15 ? $"\nほか{names.Count - 15}件" : ""), "高速PSD");
    }

    // ------------------------------------------------------------------

    static HierarchicalDataTemplate NodeTemplate() => (HierarchicalDataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <HierarchicalDataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" ItemsSource="{Binding Children}">
          <StackPanel Orientation="Horizontal" Opacity="{Binding RowOpacity}" Margin="{Binding RowMargin}">
            <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}" IsEnabled="{Binding IsCheckEnabled}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <TextBlock Text="&#xE8B7;" FontFamily="Segoe MDL2 Assets" Visibility="{Binding FolderIconVisibility}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <Image Source="{Binding Thumbnail}" MaxWidth="44" Height="22" Stretch="Uniform" Visibility="{Binding ThumbVisibility}" Margin="0,0,4,0" RenderOptions.BitmapScalingMode="HighQuality"/>
            <TextBlock Text="{Binding Name}" FontWeight="{Binding NameWeight}" VerticalAlignment="Center"/>
          </StackPanel>
        </HierarchicalDataTemplate>
        """);
}
