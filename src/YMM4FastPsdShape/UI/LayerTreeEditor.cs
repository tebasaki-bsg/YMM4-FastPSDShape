using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// v0.1.4 で立ち絵 v0.1.9 の中クリック（それ1つだけ）・プリセットのお気に入り・反転の表示・プリセットのルール指定を取り入れた。
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
        _presetBox = new ComboBox
        {
            MinWidth = 120,
            ItemTemplate = PresetTemplate(),
            ToolTip = "選ぶと、その内容がこのアイテムにコピーされます（その後も自由に変更できます）。\n名前の左の星をクリックすると、お気に入りとして一覧の上に並びます。",
        };
        _presetBox.SelectionChanged += (_, _) => { if (!_updatingPresetBox) Safe("プリセットの適用", ApplyPreset); };
        // 一覧の中の星は、項目の選択より先に受け取る（選ばず・閉じずに、お気に入りだけ切り替える）
        _presetBox.PreviewMouseLeftButtonDown += OnPresetPreviewMouseDown;
        _presetBox.PreviewMouseLeftButtonUp += OnPresetPreviewMouseUp;
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
        _tree.PreviewMouseDown += OnTreeMiddleDown; // 中クリックで「それ1つだけ」（v0.1.4）

        Content = root;
        // 注意: Unloaded では接続を切らない（高速PSD立ち絵で確認済みの落とし穴）。
        // YMM4 の編集欄は一時的に画面から外して付け直すことがあり、そのとき SetBindings は呼び直されない。
        // 接続を切るのは ClearBindings（Detach）のときだけにし、ここでは共有イベントの購読だけを外す。
        Loaded += (_, _) => { ApplyPresetItemStyle(); SubscribeShared(); QueueRefresh(); };
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

    /// <summary>このアイテムのルール</summary>
    RuleOptions Rules => _owner.GetRules();

    /// <summary>相手のいる反転用の要素（「:flip」ルールがオンのときだけ）。薄く表示して操作できないようにする</summary>
    IReadOnlySet<int> FlipCounterparts => _entry != null && Rules.Flip ? FlipMap.For(_entry.Psd).Flipped : EmptyIndexSet;
    static readonly IReadOnlySet<int> EmptyIndexSet = new HashSet<int>();

    void Refresh()
    {
        if (_owner == null) return;
        // PSD が外部で更新されていたら開き直す
        if (_entry != null && PsdRegistry.CheckStale(_entry)) { LoadPsd(); return; }
        if (_entry == null) return;
        var psd = _entry.Psd;
        var overrides = _owner.GetOverrides();

        var rules = Rules;
        var state = StateResolver.Resolve(new StateInput { Psd = psd, Rules = rules, Overrides = [overrides], Flip = _owner.Flip.ToMode() });
        // 通常用のチェックは入れ替えの前の状態（操作するのは通常用）、反転用は入れ替えたあとの状態を見せる
        var flipped = FlipCounterparts;
        var shownCheck = state.BeforeFlip ?? state.Visible;
        var touched = OverridesEditor.TouchedKeys(overrides);
        bool excl = rules.Exclamation;

        foreach (var l in psd.All)
        {
            if (!_nodes.TryGetValue(l.Index, out var vm)) continue;
            bool isFlip = flipped.Contains(l.Index) || HasFlipAncestor(l, flipped);
            vm.SetCheckedSilently(isFlip ? state.Visible[l.Index] : shownCheck[l.Index]);
            vm.IsCheckEnabled = !isFlip && !(excl && l.Name.StartsWith('!'));
            vm.RowOpacity = isFlip ? 0.35 : l.Parent == null || state.EffectiveVisible[l.Parent.Index] || (state.BeforeFlip != null && IsEffective(state.BeforeFlip, l.Parent)) ? 1.0 : 0.45;
            vm.NameWeight = touched.Contains(l.Key) ? FontWeights.Bold : FontWeights.Normal;
        }

        RefreshPresets(overrides);
        _planWarnings = FastPsd.Core.Compose.RenderPlanner.Build(psd, state, _owner.ApplyFolderClipping).Warnings;
        UpdateStatus();
    }

    IReadOnlyList<string> _planWarnings = [];

    static bool HasFlipAncestor(PsdLayer l, IReadOnlySet<int> flipped)
    {
        for (var p = l.Parent; p != null; p = p.Parent)
            if (flipped.Contains(p.Index)) return true;
        return false;
    }

    static bool IsEffective(bool[] vis, PsdLayer l)
    {
        for (var x = l; x != null; x = x.Parent)
            if (!vis[x.Index]) return false;
        return true;
    }

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
        if (FlipCounterparts.Contains(vm.Layer.Index) || HasFlipAncestor(vm.Layer, FlipCounterparts)) { QueueRefresh(); return; } // 反転用は操作できない
        var next = OverridesEditor.SetVisible(_owner.GetOverrides(), _entry.Psd, vm.Layer, value, Rules, []);
        Commit(next);
    }

    /// <summary>中クリック：その要素をオンにし、同じフォルダのほかの要素をすべてオフにする（「*」に似た動き）</summary>
    void OnTreeMiddleDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        var vm = FindNode(e.OriginalSource as DependencyObject);
        if (vm == null || _entry == null || _owner == null) return;
        e.Handled = true;
        Safe("それ1つだけにする", () =>
        {
            var flipped = FlipCounterparts;
            if (flipped.Contains(vm.Layer.Index) || HasFlipAncestor(vm.Layer, flipped)) return; // 反転用は操作できない
            var overrides = _owner.GetOverrides();
            var rules = Rules;
            // 反転用への入れ替えの前の状態で判断する（反転は付けずに計算）
            var state = StateResolver.Resolve(new StateInput { Psd = _entry.Psd, Rules = rules, Overrides = [overrides] });
            var next = OverridesEditor.Solo(overrides, _entry.Psd, vm.Layer, rules, [], flipped, state.Visible);
            if (next == null) return; // すでにそれ1つだけ
            ShapeLog.Info($"中クリックで「{vm.Layer.Key}」だけにする");
            Commit(next);
        });
    }

    static LayerNodeViewModel? FindNode(DependencyObject? d)
    {
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as TreeViewItem)?.DataContext as LayerNodeViewModel;
    }

    /// <summary>差分を書き込む。alsoChange はルールの切り替えなど、同じ「元に戻す」の単位にまとめたい変更</summary>
    void Commit(LayerOverrides next, Action? alsoChange = null)
    {
        if (_owner == null) return;
        var json = next.IsEmpty ? "" : next.ToJson();
        ShapeLog.Info($"差分を変更 owner={ShapeLog.Id(_owner)} visible={next.Visible.Count} select={next.Select.Count}");
        BeginEdit?.Invoke(this, EventArgs.Empty);
        alsoChange?.Invoke();
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
            var file = PsdPath is { } path ? ShapePresetStore.Get(path) : null;
            var presets = file?.Presets ?? [];
            // お気に入りが上（お気に入り同士・それ以外同士は保存した順）
            var entries = file?.PresetsInDisplayOrder().Select(p => new PresetListItem { Name = p.Name, Favorite = p.Favorite }).ToList() ?? [];
            var shown = _presetBox.Items.OfType<PresetListItem>().ToList();
            if (!shown.Select(e => (e.Name, e.Favorite)).SequenceEqual(entries.Select(e => (e.Name, e.Favorite))))
            {
                bool wasOpen = _presetBox.IsDropDownOpen;
                _presetBox.Items.Clear();
                foreach (var e in entries) _presetBox.Items.Add(e);
                if (wasOpen && !_presetBox.IsDropDownOpen) _presetBox.IsDropDownOpen = true; // 星を切り替えても一覧は開いたまま
            }
            // 今の差分と同じプリセットがあれば、それを選択表示にする
            var json = current?.ToJson();
            var match = json == null ? null : presets.FirstOrDefault(p => p.Overrides.ToJson() == json);
            _presetBox.SelectedItem = _presetBox.Items.OfType<PresetListItem>().FirstOrDefault(i => i.Name == match?.Name);
        }
        finally { _updatingPresetBox = false; }
    }

    string? SelectedPresetName => (_presetBox.SelectedItem as PresetListItem)?.Name;

    void ApplyPreset()
    {
        if (SelectedPresetName is not { } name) return;
        var preset = CurrentPresets.FirstOrDefault(p => p.Name == name);
        if (preset == null) return;
        // 立ち絵から取り込んだプリセットには、選んだときにアイテムのルールを変える指定があることがある（「!」の要素を隠すものなど）
        Commit(preset.Overrides.Clone(), () =>
        {
            if (_owner is not { } o) return;
            if (preset.AsteriskRule is bool a && o.AsteriskRule != a) { o.AsteriskRule = a; ShapeLog.Info($"プリセット「{preset.Name}」で「*」ルールを{(a ? "オン" : "オフ")}"); }
            if (preset.ExclamationRule is bool e && o.ExclamationRule != e) { o.ExclamationRule = e; ShapeLog.Info($"プリセット「{preset.Name}」で「!」ルールを{(e ? "オン" : "オフ")}"); }
        });
    }

    // ---- お気に入り（v0.1.4。高速PSD立ち絵と同じ見た目と操作） ----

    /// <summary>星を押したときのプリセット（離したときに同じ星なら切り替える）</summary>
    PresetListItem? _starPressed;

    void OnPresetPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _starPressed = null;
        if (FavoriteStarInList(e.OriginalSource) is not { DataContext: PresetListItem item }) return;
        _starPressed = item;
        e.Handled = true; // 項目の選択・一覧を閉じる動作をさせない
    }

    void OnPresetPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var pressed = _starPressed;
        _starPressed = null;
        if (FavoriteStarInList(e.OriginalSource) is not { DataContext: PresetListItem item }) return;
        e.Handled = true;
        if (pressed == null || pressed.Name != item.Name || PsdPath is not { } path) return;
        Safe("お気に入りの切り替え", () =>
        {
            bool? now = null;
            ShapePresetStore.Update(path, f => now = f.ToggleFavorite(item.Name));
            ShapeLog.Info($"プリセットのお気に入り「{item.Name}」→ {now}");
            QueueRefresh(); // 一覧の並べ替えは、このクリックの処理が終わってから行う
        });
    }

    /// <summary>
    /// クリックされた要素が、一覧（ドロップダウン）の中の星なら、その星を返す。
    /// 閉じた欄の星は表示だけなので null（ふつうに一覧を開く）。
    /// </summary>
    static FrameworkElement? FavoriteStarInList(object source)
    {
        FrameworkElement? star = null;
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.Tag as string == FavoriteStarTag) star = fe;
            if (d is ComboBoxItem) return star;
            if (d is ComboBox) return null;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    const string FavoriteStarTag = "FavoriteStar";
    bool _presetItemStyleApplied;

    /// <summary>
    /// 一覧の項目の左の余白を、動く立ち絵の星の位置にそろえる（星の枠はその位置から始まる）。
    /// 見た目は YMM4 のテーマの項目スタイルを引き継ぎ、左の余白だけ変える。テーマは画面に載ってから読めるので Loaded で行う。
    /// </summary>
    void ApplyPresetItemStyle()
    {
        if (_presetItemStyleApplied) return;
        _presetItemStyleApplied = true;
        var baseStyle = TryFindResource(typeof(ComboBoxItem)) as Style;
        var pad = new Thickness(4, 1, 4, 1);
        for (var st = baseStyle; st != null; st = st.BasedOn)
        {
            var setter = st.Setters.OfType<Setter>().FirstOrDefault(x => x.Property == PaddingProperty);
            if (setter?.Value is Thickness t) { pad = t; break; }
        }
        var style = new Style(typeof(ComboBoxItem), baseStyle);
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(PresetStarLeft, pad.Top, pad.Right, pad.Bottom)));
        _presetBox.ItemContainerStyle = style;
    }

    /// <summary>項目の左端から星の枠まで（動く立ち絵の画面〔拡大率125%〕で、項目の左端から星の見える端まで 6px）</summary>
    const double PresetStarLeft = 4.5;

    /// <summary>
    /// プリセットの一覧の項目（閉じた欄にも同じものを使う）。
    /// 星は 15×15 の枠に横幅 13.6 の五角の星、星の枠から名前まで 6（動く立ち絵の画面〔拡大率125%〕で、星 17px・星から次の要素まで 8px）。
    /// 星の当たり判定は周り 4 ずつ広げる（見た目の位置は変えない）。
    /// オン: 黄色（#FFE061 → #D7A548）、オフ: 文字の色。閉じた欄では、お気に入りでなければ星を出さない（場所は空けておく）。
    /// </summary>
    static DataTemplate PresetTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <StackPanel Orientation="Horizontal">
                <Border x:Name="StarHit" Tag="FavoriteStar" Background="Transparent" Margin="-4,-4,2,-4" Padding="4" VerticalAlignment="Center">
                    <Path x:Name="Star" Width="15" Height="15" Stretch="None" SnapsToDevicePixels="False"
                          Data="STAR_GEOMETRY"
                          Fill="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
                </Border>
                <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
            </StackPanel>
            <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding Favorite}" Value="True">
                    <Setter TargetName="Star" Property="Fill">
                        <Setter.Value>
                            <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                <GradientStop Color="#FFE061" Offset="0"/>
                                <GradientStop Color="#D7A548" Offset="1"/>
                            </LinearGradientBrush>
                        </Setter.Value>
                    </Setter>
                </DataTrigger>
                <MultiDataTrigger>
                    <MultiDataTrigger.Conditions>
                        <Condition Binding="{Binding Favorite}" Value="False"/>
                        <Condition Binding="{Binding RelativeSource={RelativeSource AncestorType=ComboBoxItem}}" Value="{x:Null}"/>
                    </MultiDataTrigger.Conditions>
                    <Setter TargetName="StarHit" Property="Visibility" Value="Hidden"/>
                </MultiDataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
        """.Replace("STAR_GEOMETRY", StarGeometry));

    /// <summary>15×15 の枠に収めた横幅 13.6 の五角の星（上が頂点、内側の半径は外側の 0.42 倍）</summary>
    const string StarGeometry = "M7.50,1.03 L9.27,5.75 L14.30,5.97 L10.36,9.11 L11.70,13.97 L7.50,11.19 L3.30,13.97 L4.64,9.11 L0.70,5.97 L5.73,5.75 Z";

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
            // 上書きのときは、お気に入りの印と、選んだときのルールの指定（立ち絵から取り込んだもの）を引き継ぐ。並び順もそのまま
            int idx = f.Presets.FindIndex(p => p.Name == name);
            var old = idx >= 0 ? f.Presets[idx] : null;
            var preset = new Preset
            {
                Name = name, Overrides = overrides, Favorite = old?.Favorite ?? false,
                AsteriskRule = old?.AsteriskRule, ExclamationRule = old?.ExclamationRule,
            };
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
