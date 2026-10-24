using System.Collections.Concurrent;
using System.Diagnostics;

namespace FastPsd.Core.Settings;

/// <summary>
/// 「-ymmfastpsd.json」をPSDごとに1つだけ読み込んで共有する。
/// 描画のたびに呼ばれても重くならないよう、ファイルの更新確認は一定間隔に間引く。
/// 書き込みは「ディスクから読み直す → 変更 → 保存」をロックの中で行い、
/// 同じPSDを使う別キャラの設定セットを上書きで消さないようにする。
/// </summary>
public static class SettingsStore
{
    sealed class Entry
    {
        public SettingsFile File = new();
        public DateTime JsonWriteTime;
        public long LastCheckTicks;
        public long Version;
        public readonly object Lock = new();
    }

    static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly long CheckIntervalTicks = Stopwatch.Frequency / 2; // 0.5秒

    /// <summary>設定ファイルが変わったとき（このプロセスでの保存、外部での編集）に通知する。引数はPSDのパス。</summary>
    public static event Action<string>? Changed;

    /// <summary>現在の設定（読み取り専用として扱うこと）と、その版数</summary>
    public static (SettingsFile File, long Version) Get(string psdPath)
    {
        var e = Entries.GetOrAdd(psdPath, p => Load(p));
        long now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref e.LastCheckTicks) > CheckIntervalTicks)
        {
            Interlocked.Exchange(ref e.LastCheckTicks, now);
            var t = JsonTime(psdPath);
            if (t != e.JsonWriteTime)
            {
                lock (e.Lock)
                {
                    if (t != e.JsonWriteTime)
                    {
                        TryReload(psdPath, e);
                        Changed?.Invoke(psdPath);
                    }
                }
            }
        }
        return (e.File, Interlocked.Read(ref e.Version));
    }

    /// <summary>設定を変更して保存する。変更はディスクの最新内容に対して行う。</summary>
    public static void Update(string psdPath, Action<SettingsFile> change)
    {
        var e = Entries.GetOrAdd(psdPath, p => Load(p));
        lock (e.Lock)
        {
            var latest = SafeLoad(psdPath) ?? e.File;
            change(latest);
            latest.Save(psdPath);
            e.File = latest;
            e.JsonWriteTime = JsonTime(psdPath);
            Interlocked.Increment(ref e.Version);
        }
        Changed?.Invoke(psdPath);
    }

    static Entry Load(string psdPath)
    {
        var e = new Entry { LastCheckTicks = Stopwatch.GetTimestamp() };
        TryReload(psdPath, e);
        return e;
    }

    static void TryReload(string psdPath, Entry e)
    {
        var f = SafeLoad(psdPath);
        if (f != null) e.File = f;
        e.JsonWriteTime = JsonTime(psdPath);
        Interlocked.Increment(ref e.Version);
    }

    static SettingsFile? SafeLoad(string psdPath)
    {
        try { return SettingsFile.Load(psdPath); }
        catch (Exception) { return null; }
    }

    static DateTime JsonTime(string psdPath)
    {
        try
        {
            var fi = new FileInfo(SettingsFile.PathFor(psdPath));
            return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }
}
