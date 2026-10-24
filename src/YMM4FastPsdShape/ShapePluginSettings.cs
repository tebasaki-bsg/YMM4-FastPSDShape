using System.Windows;
using System.Windows.Controls;
using FastPsd.Core.Thumbnails;
using YukkuriMovieMaker.Plugin;

namespace YMM4FastPsdShape;

/// <summary>プラグイン全体の設定（YMM4 の「設定」→「図形」→「高速PSD」）</summary>
public sealed class ShapePluginSettings : SettingsBase<ShapePluginSettings>
{
    public override SettingsCategory Category => SettingsCategory.Shape;
    public override string Name => "高速PSD";
    public override bool HasSettingView => true;
    public override object SettingView => new SettingsView(this);

    /// <summary>
    /// 合成結果を1枚の画像に焼き付けるか。
    /// オン: 表示が変わったときに1回だけ重ねる（毎フレームのGPUの負荷が小さい）。
    /// オフ: 高速PSD立ち絵と同じく、毎フレームGPUがレイヤーを重ねる（比較用）。
    /// </summary>
    public bool BakeComposite { get => bakeComposite; set { if (Set(ref bakeComposite, value)) Bake = value; } }
    bool bakeComposite = true;

    /// <summary>描画スレッドから読む値（設定の読み込み前でも使えるように静的に持つ）</summary>
    internal static volatile bool Bake = true;

    /// <summary>詳細ログを出力するか（不具合の調査用）</summary>
    public bool VerboseLog { get => verboseLog; set { if (Set(ref verboseLog, value)) ShapeLog.Verbose = value; } }
    bool verboseLog = false;

    /// <summary>サムネイルキャッシュの保存場所。空なら既定（%LOCALAPPDATA%\YMM4FastPsdShape\Cache）</summary>
    public string CacheDirectory { get => cacheDirectory; set => Set(ref cacheDirectory, value); }
    string cacheDirectory = "";

    /// <summary>
    /// YMM4 が設定を読み込んだ直後に呼ぶ。この時点ではまだ Default が設定されていない（静的初期化の途中）ので、
    /// Default ではなく自分自身の値を使う。
    /// </summary>
    public override void Initialize()
    {
        Apply(CacheDirectory);
        ShapeLog.Verbose = VerboseLog;
        Bake = BakeComposite;
    }

    public static void ApplyCacheDirectory()
    {
        try { Apply(Default?.CacheDirectory); }
        catch (Exception) { Apply(null); } // 設定を読めなくてもサムネイルは既定の場所で作る
    }

    static void Apply(string? dir) =>
        ThumbnailCache.Directory = string.IsNullOrWhiteSpace(dir) ? ThumbnailCache.DefaultDirectory : dir;

    sealed class SettingsView : UserControl
    {
        public SettingsView(ShapePluginSettings settings)
        {
            var panel = new StackPanel { Margin = new Thickness(8) };

            var bake = new CheckBox { Content = "合成結果を1枚の画像にまとめる（おすすめ）", IsChecked = settings.BakeComposite };
            bake.Checked += (_, _) => settings.BakeComposite = true;
            bake.Unchecked += (_, _) => settings.BakeComposite = false;
            panel.Children.Add(bake);
            panel.Children.Add(Note("オン: 表示を切り替えたときに1回だけレイヤーを重ね、その画像を使い回します。同じPSD・同じ表示のアイテムは1枚を共有します。\n" +
                                    "オフ: 毎フレーム、GPUでレイヤーを重ねます（高速PSD立ち絵と同じ方式。速さの比較用）。"));

            panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
            panel.Children.Add(new TextBlock { Text = "サムネイルキャッシュの保存場所（空欄で既定の場所）", Margin = new Thickness(0, 0, 0, 4) });
            var box = new TextBox { Text = settings.CacheDirectory };
            box.LostFocus += (_, _) => { settings.CacheDirectory = box.Text.Trim(); ApplyCacheDirectory(); };
            panel.Children.Add(box);
            panel.Children.Add(Note($"既定の場所: {ThumbnailCache.DefaultDirectory}\n高速PSD立ち絵で作ったサムネイルがあれば、それも読み込みます（立ち絵側のファイルは変更しません）。"));

            var size = new TextBlock { Margin = new Thickness(0, 4, 0, 4) };
            void UpdateSize() { ApplyCacheDirectory(); size.Text = $"現在のキャッシュ: {ThumbnailCache.TotalBytes() / 1048576.0:F1} MB"; }
            UpdateSize();
            panel.Children.Add(size);
            var clear = new Button { Content = "キャッシュを削除", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 2, 12, 2) };
            clear.Click += (_, _) =>
            {
                var bytes = ThumbnailCache.Clear();
                UpdateSize();
                MessageBox.Show($"{bytes / 1048576.0:F1} MB を削除しました。サムネイルは次にレイヤー画面を開いたときに作り直されます。", "高速PSD");
            };
            panel.Children.Add(clear);

            panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
            var verbose = new CheckBox { Content = "詳細ログを出力する（不具合の調査用。合成にかかった時間も記録します）", IsChecked = settings.VerboseLog };
            verbose.Checked += (_, _) => settings.VerboseLog = true;
            verbose.Unchecked += (_, _) => settings.VerboseLog = false;
            panel.Children.Add(verbose);
            panel.Children.Add(Note($"ログの場所: {ShapeLog.Directory}（エラーはYMM4本体のログにも出ます）"));
            var open = new Button { Content = "ログフォルダを開く", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 2, 12, 2) };
            open.Click += (_, _) =>
            {
                System.IO.Directory.CreateDirectory(ShapeLog.Directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ShapeLog.Directory, UseShellExecute = true });
            };
            panel.Children.Add(open);
            Content = panel;
        }

        static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 4), TextWrapping = TextWrapping.Wrap };
    }
}
