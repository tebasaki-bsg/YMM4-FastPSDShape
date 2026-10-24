using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FastPsd.Core.Psd;

namespace FastPsd.Core.Settings;

/// <summary>PSDと同じフォルダに置く「&lt;PSD名&gt;-ymmfastpsdshape.json」の中身（図形用のプリセット）</summary>
public sealed class ShapePresetFile
{
    public int Version { get; set; } = CurrentVersion;
    public const int CurrentVersion = 1;
    public List<Preset> Presets { get; set; } = [];

    public const string Suffix = "-ymmfastpsdshape.json";

    public static string PathFor(string psdPath)
    {
        var dir = System.IO.Path.GetDirectoryName(psdPath) ?? "";
        return System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(psdPath) + Suffix);
    }

    public static ShapePresetFile Load(string psdPath)
    {
        var path = PathFor(psdPath);
        if (!File.Exists(path)) return new ShapePresetFile();
        var json = File.ReadAllText(path);
        var f = JsonSerializer.Deserialize<ShapePresetFile>(json, SettingsJson.Indented) ?? new ShapePresetFile();
        f.Version = CurrentVersion;
        return f;
    }

    /// <summary>一時ファイルに書いてから置き換える（書き込み途中で壊れないように）</summary>
    public void Save(string psdPath)
    {
        var path = PathFor(psdPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Indented), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// 「-ymmfastpsdshape.json」をPSDごとに1つだけ読み込んで共有する（SettingsStore と同じ考え方）。
/// 外部での変更は 0.5 秒ごとに確認し、書き込みは「ディスクから読み直す → 変更 → 保存」をロックの中で行う。
/// </summary>
public static class ShapePresetStore
{
    sealed class Entry
    {
        public ShapePresetFile File = new();
        public DateTime JsonWriteTime;
        public long LastCheckTicks;
        public long Version;
        public readonly object Lock = new();
    }

    static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly long CheckIntervalTicks = Stopwatch.Frequency / 2;

    /// <summary>プリセットが変わったとき（保存、外部での編集）に通知する。引数はPSDのパス。</summary>
    public static event Action<string>? Changed;

    /// <summary>現在のプリセット（読み取り専用として扱うこと）</summary>
    public static ShapePresetFile Get(string psdPath)
    {
        var e = Entries.GetOrAdd(psdPath, Load);
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
        return e.File;
    }

    /// <summary>プリセットを変更して保存する。変更はディスクの最新内容に対して行う。</summary>
    public static void Update(string psdPath, Action<ShapePresetFile> change)
    {
        var e = Entries.GetOrAdd(psdPath, Load);
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

    static ShapePresetFile? SafeLoad(string psdPath)
    {
        try { return ShapePresetFile.Load(psdPath); }
        catch (Exception) { return null; }
    }

    static DateTime JsonTime(string psdPath)
    {
        try
        {
            var fi = new FileInfo(ShapePresetFile.PathFor(psdPath));
            return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }
}

/// <summary>高速PSD立ち絵（-ymmfastpsd.json）のプリセットを図形用に取り込む</summary>
public static class TachiePresetImport
{
    /// <summary>立ち絵側の設定ファイル（なければ null）。立ち絵側のファイルは読むだけで変更しない。</summary>
    public static SettingsFile? ReadTachieFile(string psdPath)
    {
        var path = SettingsFile.PathFor(psdPath);
        if (!File.Exists(path)) return null;
        return SettingsFile.Load(psdPath);
    }

    /// <summary>
    /// 立ち絵のプリセットを図形用に変換する。
    /// 図形にはスライダーがないので、スライダー化したフォルダでの選択（Select）は
    /// 「選んだ要素を表示し、同じフォルダの他の要素を非表示」に置き換える（見た目を同じにするため）。
    /// 「*」グループの選択は図形でも同じ意味になるので、そのまま残す。
    /// </summary>
    public static Preset Convert(Preset source, PsdFile psd, IEnumerable<string> sliderGroups)
    {
        var sliders = new HashSet<string>(sliderGroups, StringComparer.Ordinal);
        var o = new LayerOverrides();
        foreach (var (k, v) in source.Overrides.Visible) o.Visible[k] = v;
        foreach (var (groupKey, childKey) in source.Overrides.Select)
        {
            if (!sliders.Contains(groupKey))
            {
                o.Select[groupKey] = childKey;
                continue;
            }
            if (!psd.ByKey.TryGetValue(childKey, out var child)) continue;
            if ((child.Parent?.Key ?? "") != groupKey) continue;
            foreach (var s in child.Parent?.Children ?? psd.Roots)
                o.Visible[s.Key] = s == child;
        }
        return new Preset { Name = source.Name, Overrides = o };
    }

    /// <summary>
    /// 取り込んだプリセットを加える。同じ名前があるとき、overwrite なら置き換え、そうでなければ「名前 (2)」のように番号を付けて加える。
    /// 戻り値は加えた（置き換えた）プリセットの名前。
    /// </summary>
    public static List<string> Merge(ShapePresetFile file, IEnumerable<Preset> imports, bool overwrite)
    {
        var names = new List<string>();
        foreach (var p in imports)
        {
            var name = p.Name;
            if (overwrite)
            {
                int idx = file.Presets.FindIndex(x => x.Name == name);
                var copy = new Preset { Name = name, Overrides = p.Overrides.Clone() };
                if (idx >= 0) file.Presets[idx] = copy;
                else file.Presets.Add(copy);
            }
            else
            {
                if (file.Presets.Any(x => x.Name == name))
                {
                    for (int i = 2; ; i++)
                    {
                        var candidate = $"{p.Name} ({i})";
                        if (file.Presets.All(x => x.Name != candidate)) { name = candidate; break; }
                    }
                }
                file.Presets.Add(new Preset { Name = name, Overrides = p.Overrides.Clone() });
            }
            names.Add(name);
        }
        return names;
    }
}
