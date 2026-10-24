using Vortice.Direct2D1;

namespace YMM4FastPsdShape.Rendering;

/// <summary>焼き付けた合成結果（1枚の画像）と、キャンバス上の位置</summary>
internal sealed class BakedImage
{
    public required ID2D1Bitmap1 Bitmap { get; init; }
    public required int Left { get; init; }
    public required int Top { get; init; }
    public long Bytes { get; init; }
    internal int Refs;
    internal long LastUse;
}

/// <summary>
/// 焼き付けた合成結果の置き場所（描画デバイスごとに1つ）。
/// 同じPSD・同じ表示状態のアイテムは1枚を共有するので、同じ差分のアイテムを何個置いても合成は1回で済む。
/// 使われなくなった画像もすぐには捨てず、上限まで残す（アイテムが現れ直したときや、差分を戻したときに使い回す）。
/// </summary>
internal sealed class BakedImageCache
{
    /// <summary>使用中でない画像を残しておく上限（デバイスごと）</summary>
    public static long IdleBudgetBytes = 256L * 1024 * 1024;
    const int IdleReleaseMilliseconds = 60_000;

    public readonly record struct Key(PsdEntry Entry, ulong StateHash, bool FolderClipping);

    static readonly object RegistryLock = new();
    static readonly Dictionary<IntPtr, BakedImageCache> Registry = new();

    readonly IntPtr _device;
    readonly object _lock = new();
    readonly Dictionary<Key, BakedImage> _items = new();
    long _idleBytes;
    int _users;
    long _useCounter;

    BakedImageCache(IntPtr device) { _device = device; }

    public static BakedImageCache Attach(ID2D1Device device)
    {
        lock (RegistryLock)
        {
            if (!Registry.TryGetValue(device.NativePointer, out var c))
            {
                c = new BakedImageCache(device.NativePointer);
                Registry[device.NativePointer] = c;
            }
            lock (c._lock) c._users++;
            return c;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _users--;
            if (_users > 0) return;
        }
        _ = Task.Delay(IdleReleaseMilliseconds).ContinueWith(_ =>
        {
            lock (RegistryLock)
            {
                lock (_lock)
                {
                    if (_users > 0) return;
                    foreach (var it in _items.Values) it.Bitmap.Dispose();
                    _items.Clear();
                    _idleBytes = 0;
                }
                if (Registry.TryGetValue(_device, out var cur) && cur == this) Registry.Remove(_device);
            }
        });
    }

    /// <summary>
    /// 画像を取得して「使用中」にする。なければ create で作る（作れなければ null）。
    /// hit には、作らずに済んだ（共有できた）かどうかを返す。使い終わったら Release で返す。
    /// </summary>
    public BakedImage? Acquire(Key key, Func<BakedImage?> create, out bool hit)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var it))
            {
                if (it.Refs == 0) _idleBytes -= it.Bytes;
                it.Refs++;
                it.LastUse = ++_useCounter;
                hit = true;
                return it;
            }
            hit = false;
            it = create();
            if (it == null) return null;
            it.Refs = 1;
            it.LastUse = ++_useCounter;
            _items[key] = it;
            return it;
        }
    }

    public void Release(Key key, BakedImage image)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(key, out var it) || it != image || it.Refs == 0) return;
            it.Refs--;
            if (it.Refs > 0) return;
            _idleBytes += it.Bytes;
            if (key.Entry.Retired) Remove(key, it);
            Trim();
        }
    }

    void Trim()
    {
        if (_idleBytes <= IdleBudgetBytes) return;
        foreach (var (key, it) in _items.Where(kv => kv.Value.Refs == 0).OrderBy(kv => kv.Value.LastUse).ToList())
        {
            if (_idleBytes <= IdleBudgetBytes) break;
            Remove(key, it);
        }
    }

    void Remove(Key key, BakedImage it)
    {
        if (it.Refs == 0) _idleBytes -= it.Bytes;
        it.Bitmap.Dispose();
        _items.Remove(key);
    }

    /// <summary>閉じたPSDの画像を、使用中でないものから捨てる</summary>
    public static void PurgeAll(PsdEntry entry)
    {
        List<BakedImageCache> caches;
        lock (RegistryLock) caches = Registry.Values.ToList();
        foreach (var c in caches)
        {
            lock (c._lock)
            {
                foreach (var (key, it) in c._items.Where(kv => kv.Key.Entry == entry && kv.Value.Refs == 0).ToList())
                    c.Remove(key, it);
            }
        }
    }
}
