using System.Diagnostics;
using FastPsd.Core.Psd;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace YMM4FastPsdShape.Rendering;

/// <summary>
/// GPU上のレイヤー画像の置き場所（描画デバイスごとに1つ）。
/// ・表示中のレイヤーは「使用中」として保持する。
/// ・非表示になったレイヤーもすぐには捨てず、上限まで残す（直前の差分に戻すときの待ち時間をなくすため）。
///   上限を超えたら、最後に使ったのが古いものから捨てる。
/// ・そのデバイスを使う描画クラスがなくなったら、しばらく待ってから全部捨てる。
/// </summary>
internal sealed class DeviceLayerCache
{
    /// <summary>使用中でないレイヤー画像を残しておく上限（デバイスごと）</summary>
    public static long IdleBudgetBytes = 400L * 1024 * 1024;
    const int IdleReleaseMilliseconds = 60_000;

    sealed class Item
    {
        public required ID2D1Bitmap1 Bitmap;
        public long Bytes;
        public int Pins;
        public long LastUse;
    }

    static readonly object RegistryLock = new();
    static readonly Dictionary<IntPtr, DeviceLayerCache> Registry = new();

    readonly IntPtr _device;
    readonly object _lock = new();
    readonly Dictionary<(PsdEntry, PsdLayer), Item> _items = new();
    long _idleBytes;
    int _users;
    long _useCounter;

    DeviceLayerCache(IntPtr device) { _device = device; }

    /// <summary>描画クラスが使い始めるときに呼ぶ</summary>
    public static DeviceLayerCache Attach(ID2D1Device device)
    {
        lock (RegistryLock)
        {
            if (!Registry.TryGetValue(device.NativePointer, out var c))
            {
                c = new DeviceLayerCache(device.NativePointer);
                Registry[device.NativePointer] = c;
            }
            lock (c._lock) c._users++;
            return c;
        }
    }

    /// <summary>描画クラスが破棄されるときに呼ぶ</summary>
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
    /// レイヤー画像を取得して「使用中」にする。まだGPUにないものは、PSDから並列に展開してから作る。
    /// 戻り値はレイヤー → 画像の対応。使い終わったら Unpin で返す。
    /// </summary>
    public Dictionary<PsdLayer, ID2D1Bitmap1> Pin(ID2D1DeviceContext dc, PsdEntry entry, IReadOnlyList<PsdLayer> layers, out LoadStats stats)
    {
        var sw = Stopwatch.StartNew();
        var result = new Dictionary<PsdLayer, ID2D1Bitmap1>(layers.Count);
        lock (_lock)
        {
            var missing = new List<PsdLayer>();
            foreach (var l in layers)
            {
                if (_items.TryGetValue((entry, l), out var it))
                {
                    if (it.Pins == 0) _idleBytes -= it.Bytes;
                    it.Pins++;
                    it.LastUse = ++_useCounter;
                    result[l] = it.Bitmap;
                }
                else missing.Add(l);
            }

            // 展開はCPUで並列に、GPUへの転送は呼び出し元のスレッドで1枚ずつ
            var decoded = new byte[missing.Count][];
            try
            {
                Parallel.For(0, missing.Count, i => decoded[i] = entry.Psd.DecodeBgra(missing[i]));
                for (int i = 0; i < missing.Count; i++)
                {
                    var l = missing[i];
                    var bmp = CreateBitmap(dc, decoded[i], l.Width, l.Height);
                    var it = new Item { Bitmap = bmp, Bytes = (long)l.Width * l.Height * 4, Pins = 1, LastUse = ++_useCounter };
                    _items[(entry, l)] = it;
                    result[l] = bmp;
                }
            }
            finally
            {
                foreach (var d in decoded) if (d != null) PsdFile.ReturnBuffer(d);
            }
            stats = new LoadStats(layers.Count, missing.Count, missing.Sum(l => (long)l.Width * l.Height * 4), sw.Elapsed);
        }
        return result;
    }

    /// <summary>「使用中」を解除する。上限を超えた分は古いものから捨てる。</summary>
    public void Unpin(PsdEntry entry, IEnumerable<PsdLayer> layers)
    {
        lock (_lock)
        {
            foreach (var l in layers)
            {
                if (!_items.TryGetValue((entry, l), out var it) || it.Pins == 0) continue;
                it.Pins--;
                if (it.Pins == 0)
                {
                    _idleBytes += it.Bytes;
                    if (entry.Retired) Remove((entry, l), it);
                }
            }
            if (_idleBytes <= IdleBudgetBytes) return;
            foreach (var (key, it) in _items.Where(kv => kv.Value.Pins == 0).OrderBy(kv => kv.Value.LastUse).ToList())
            {
                if (_idleBytes <= IdleBudgetBytes) break;
                Remove(key, it);
            }
        }
    }

    void Remove((PsdEntry, PsdLayer) key, Item it)
    {
        if (it.Pins == 0) _idleBytes -= it.Bytes;
        it.Bitmap.Dispose();
        _items.Remove(key);
    }

    /// <summary>閉じたPSDの画像を、使用中でないものから捨てる</summary>
    public static void PurgeAll(PsdEntry entry)
    {
        List<DeviceLayerCache> caches;
        lock (RegistryLock) caches = Registry.Values.ToList();
        foreach (var c in caches)
        {
            lock (c._lock)
            {
                foreach (var (key, it) in c._items.Where(kv => kv.Key.Item1 == entry && kv.Value.Pins == 0).ToList())
                    c.Remove(key, it);
            }
        }
    }

    static unsafe ID2D1Bitmap1 CreateBitmap(ID2D1DeviceContext dc, byte[] bgra, int w, int h)
    {
        var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        fixed (byte* p = bgra)
            return dc.CreateBitmap(new SizeI(w, h), (IntPtr)p, w * 4, props);
    }
}

internal readonly record struct LoadStats(int Requested, int Loaded, long LoadedBytes, TimeSpan Elapsed);
