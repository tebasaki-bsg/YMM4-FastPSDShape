using FastPsd.Core.Psd;

namespace YMM4FastPsdShape.Rendering;

/// <summary>開いているPSD 1つ分。同じPSDを使う図形はすべてこれを共有する。</summary>
internal sealed class PsdEntry
{
    public required string Path { get; init; }
    public required PsdFile Psd { get; init; }
    /// <summary>PSDが更新されて開き直したら別の PsdEntry になる。古いものは参照がなくなったら閉じる。</summary>
    public bool Retired { get; set; }
    public int RefCount;
    public long LastStaleCheckTicks = Environment.TickCount64;
}

/// <summary>
/// プラグイン全体でPSDを共有する。描画クラス（ShapeSource）とは寿命を分けているので、
/// YMM4が描画クラスを作り直しても、PSDの解析をやり直さずに済む。
/// </summary>
internal static class PsdRegistry
{
    static readonly object Lock = new();
    static readonly Dictionary<string, PsdEntry> Current = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>参照のなくなったPSDを閉じるまでの猶予（図形のない位置から戻ったときに開き直さないため）</summary>
    const int IdleCloseMilliseconds = 60_000;

    /// <summary>PSDを取得する（参照を1つ増やす）。読めなければ例外。</summary>
    public static PsdEntry Acquire(string path)
    {
        lock (Lock)
        {
            if (Current.TryGetValue(path, out var e) && !e.Retired)
            {
                e.RefCount++;
                return e;
            }
            var psd = PsdFile.Open(path);
            e = new PsdEntry { Path = path, Psd = psd, RefCount = 1 };
            Current[path] = e;
            return e;
        }
    }

    public static void Release(PsdEntry e)
    {
        lock (Lock)
        {
            e.RefCount--;
            if (e.RefCount > 0) return;
            if (e.Retired)
            {
                Close(e);
                return;
            }
        }
        // 最新のPSDは少し待ってから閉じる
        _ = Task.Delay(IdleCloseMilliseconds).ContinueWith(_ =>
        {
            lock (Lock)
            {
                if (e.RefCount > 0) return;
                if (Current.TryGetValue(e.Path, out var cur) && cur == e) Current.Remove(e.Path);
                Close(e);
            }
        });
    }

    /// <summary>PSDファイルが更新されていたら、古いものを引退させる（2秒に1回だけ確認）。更新されていたら true。</summary>
    public static bool CheckStale(PsdEntry e)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref e.LastStaleCheckTicks) < 2000) return e.Retired;
        Interlocked.Exchange(ref e.LastStaleCheckTicks, now);
        if (e.Retired) return true;
        if (!e.Psd.IsStale()) return false;
        lock (Lock)
        {
            e.Retired = true;
            if (Current.TryGetValue(e.Path, out var cur) && cur == e) Current.Remove(e.Path);
        }
        return true;
    }

    static void Close(PsdEntry e)
    {
        DeviceLayerCache.PurgeAll(e);
        BakedImageCache.PurgeAll(e);
        e.Psd.Dispose();
    }
}
