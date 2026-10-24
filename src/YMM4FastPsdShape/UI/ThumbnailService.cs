using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastPsd.Core.Thumbnails;
using YMM4FastPsdShape.Rendering;

namespace YMM4FastPsdShape.UI;

/// <summary>1つのPSDのサムネイル一式（レイヤーの通し番号 → 画像）</summary>
internal sealed class ThumbnailSet
{
    public Dictionary<int, BitmapSource> Images { get; } = new();
    public bool IsReady { get; set; }
    public string Status { get; set; } = "";
    /// <summary>画像が揃ったとき、進み具合が変わったときに通知（どのスレッドからでも来る）</summary>
    public event Action? Updated;
    internal void RaiseUpdated() => Updated?.Invoke();
}

/// <summary>
/// サムネイルを PSD ごとに1回だけ作って共有する。ディスクキャッシュがあればそれを読み、なければ裏で作って保存する。
/// </summary>
internal static class ThumbnailService
{
    static readonly object Lock = new();
    static readonly Dictionary<(string Path, DateTime Time), ThumbnailSet> Sets = new();

    public static ThumbnailSet Get(PsdEntry entry)
    {
        var id = (entry.Path.ToLowerInvariant(), entry.Psd.LastWriteTimeUtc);
        lock (Lock)
        {
            if (Sets.TryGetValue(id, out var s)) return s;
            s = new ThumbnailSet { Status = "サムネイルを準備中…" };
            Sets[id] = s;
            var psd = entry.Psd;
            _ = Task.Run(() => Build(entry, s));
            return s;
        }
    }

    static void Build(PsdEntry entry, ThumbnailSet set)
    {
        // 作成中に PSD が閉じられないよう参照を持つ
        PsdEntry? held = null;
        try
        {
            held = PsdRegistry.Acquire(entry.Path);
            var psd = held.Psd;
            ShapePluginSettings.ApplyCacheDirectory();
            var sw = Stopwatch.StartNew();
            var key = ThumbnailCache.ContentKey(entry.Path);
            var thumbs = ThumbnailCache.TryLoad(key);
            if (thumbs == null)
            {
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    set.Status = $"サムネイルを作成中… {p.Done}/{p.Total}";
                    set.RaiseUpdated();
                });
                thumbs = ThumbnailCache.Build(psd, Math.Max(1, Environment.ProcessorCount / 2), progress, CancellationToken.None);
                try { ThumbnailCache.Save(key, thumbs); } catch (Exception ex) { Debug.WriteLine($"[YMM4FastPsdShape] サムネイルを保存できませんでした: {ex.Message}"); }
            }
            var images = new Dictionary<int, BitmapSource>(thumbs.Count);
            foreach (var (index, t) in thumbs)
            {
                var bmp = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Pbgra32, null, t.Bgra, t.Width * 4);
                bmp.Freeze();
                images[index] = bmp;
            }
            lock (set.Images)
                foreach (var (k, v) in images) set.Images[k] = v;
            set.IsReady = true;
            set.Status = "";
            ShapeLog.Info($"サムネイル {images.Count}枚 {sw.ElapsedMilliseconds}ms 保存先={ThumbnailCache.Directory}");
        }
        catch (Exception ex)
        {
            set.Status = $"サムネイルを作れませんでした: {ex.Message}";
            ShapeLog.Error($"サムネイルを作れませんでした: {entry.Path}", ex);
        }
        finally
        {
            if (held != null) PsdRegistry.Release(held);
        }
        set.RaiseUpdated();
    }
}
