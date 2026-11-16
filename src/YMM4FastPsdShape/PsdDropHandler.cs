using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.ViewModels;

namespace YMM4FastPsdShape;

/// <summary>
/// PSD をタイムラインにドラッグ＆ドロップしたとき、画像アイテムではなく高速PSDの図形アイテムとして置く（v0.2.0）。
///
/// YMM4 にはドロップを差し替えるプラグインの仕組みがないので、ウィンドウに届くドロップ（PreviewDrop。トンネルなので
/// YMM4 のタイムラインの DropFileTrigger より先に届く）を見て、タイムラインへの PSD のドロップならこちらで置き、
/// 残りのファイルは YMM4 の「ファイルを追加」コマンドに渡す。
/// 置く位置は YMM4 の標準のドロップと同じ（再生位置と、画面に見えている一番上のレイヤー。そこから空きを探す）。
/// 長さ・位置などは YMM4 の「図形アイテムの既定値」を使う。
///
/// どのタイムラインに置くかを知るため、タイムラインの画面（TimelineViewModel、public）の非公開のフィールド scene を読む。
/// YMM4 の更新で読めなくなったら何もせず（YMM4 の標準どおり画像アイテムになる）、ログに残す。
/// </summary>
internal static class PsdDropHandler
{
    static bool _installed;
    static bool _reportedBroken;

    /// <summary>プラグインを読み込んだときに1回だけ呼ぶ</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewDropEvent, new DragEventHandler(OnPreviewDrop));
        }
        catch (Exception ex)
        {
            ShapeLog.Error("PSD のドラッグ＆ドロップの準備に失敗しました", ex);
        }
    }

    static readonly FieldInfo? SceneField =
        typeof(TimelineViewModel).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic);

    static void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || !ShapePluginSettings.DropPsdAsShape) return;
        try
        {
            var handler = DataObjectFormatHandler.FromDataObject(e.Data);
            if (!handler.HasFiles()) return;
            var files = handler.GetFiles().ToArray();
            if (!files.Any(IsPsd)) return;

            // ドロップ先が、YMM4 がファイルのドロップを受け付けているタイムラインの部分か
            if (FindTimelineDropTarget(e.OriginalSource as DependencyObject) is not { } target) return;
            if (target.DataContext is not TimelineViewModel vm) return;
            if (!vm.AsyncAwaitStatus.CanAddItem) return; // 処理中は YMM4 に任せる（YMM4 も追加しない）

            if (SceneField?.GetValue(vm) is not Scene scene)
            {
                if (!_reportedBroken)
                {
                    _reportedBroken = true;
                    ShapeLog.Error("YMM4 のタイムラインの情報を読めませんでした（YMM4 の更新で中身が変わった可能性があります）。PSD はこれまでどおり画像アイテムとして置かれます");
                }
                return;
            }

            e.Handled = true; // YMM4 のドロップの処理（画像アイテムとして置く）を止める
            e.Effects = DragDropEffects.Copy;
            Window.GetWindow(target)?.Activate();
            Place(target, vm, scene, files);
        }
        catch (Exception ex)
        {
            ShapeLog.Error("PSD のドラッグ＆ドロップでエラーが発生しました", ex);
        }
    }

    static bool IsPsd(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path);

    static void Place(DependencyObject target, TimelineViewModel vm, Scene scene, string[] files)
    {
        var timeline = scene.Timeline;
        int frame = timeline.CurrentFrame;
        int layerHeight = Math.Max(1, SettingsBase<YMMSettings>.Default.LayerHeight);
        int layer = (int)(vm.Viewport.Value.Top / layerHeight);
        bool horizontal = SettingsBase<YMMSettings>.Default.GetAddItemsOrientation().ToString() == "Horizontal";

        // PSD はこちらで置く（並べ方は YMM4 と同じ。「元に戻す」は全部で1回分）
        int delta = 0;
        var psds = files.Where(IsPsd).ToList();
        foreach (var path in psds)
        {
            var item = CreateItem(path);
            timeline.TryAddItems([item], frame + delta, layer);
            if (horizontal) delta += item.Length;
        }
        scene.Scenes.UndoRedoManager.Record();
        ShapeLog.Info($"PSD を高速PSDの図形アイテムとして置きました（{psds.Count}件、フレーム {frame}、レイヤー {layer}）");

        // 残りのファイルは YMM4 の「ファイルを追加」に渡す（YMM4 のドロップと同じ処理になる）
        var others = files.Where(f => !IsPsd(f)).ToArray();
        if (others.Length == 0) return;
        var command = SettingsBase<CommandSettings>.Default[Ymm4Enum.ByName("AddFileItem", CommandType.AddFileItem)];
        var parameter = new AddFileItemCommandParameter(frame + delta, layer,
            others.Select(f => new FileItemGroup.GroupedFile(FileGroupType.None, f)).ToArray());
        if (command != null && target is IInputElement input && command.CanExecute(parameter, input))
            command.Execute(parameter, input);
        else
            ShapeLog.Error($"PSD 以外のファイル（{others.Length}件）を YMM4 に渡せませんでした");
    }

    static ShapeItem CreateItem(string path)
    {
        // 長さ・位置・拡大率などは「図形アイテムの既定値」に従う
        var item = (ShapeItem)SettingsBase<ItemSettings>.Default.DefaultItems.ShapeItem.GetClone();
        item.ShapeType2 = typeof(FastPsdShapePlugin);
        item.ShapeParameter = new FastPsdShapeParameter(null) { FilePath = path };
        return item;
    }

    /// <summary>OriginalSource から上へたどり、YMM4 のファイルのドロップ（DropFileTrigger）が付いた要素を探す</summary>
    static FrameworkElement? FindTimelineDropTarget(DependencyObject? d)
    {
        for (; d != null; d = Parent(d))
            if (d is FrameworkElement fe && HasDropFileTrigger(fe)) return fe;
        return null;
    }

    static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>
    /// Microsoft.Xaml.Behaviors の Interaction.Triggers（添付プロパティ）に DropFileTrigger があるか。
    /// DLL を参照しないで済むよう、要素に直接設定された値から型の名前で探す。
    /// </summary>
    static bool HasDropFileTrigger(DependencyObject d)
    {
        var en = d.GetLocalValueEnumerator();
        while (en.MoveNext())
        {
            if (en.Current.Value is not IEnumerable list || en.Current.Value is string) continue;
            if (en.Current.Value.GetType().Name != "TriggerCollection") continue;
            foreach (var t in list)
                if (t?.GetType().Name == "DropFileTrigger") return true;
        }
        return false;
    }
}
