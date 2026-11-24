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

    /// <summary>一覧に出す順番：お気に入りが上。お気に入り同士・それ以外同士は保存した順（v0.1.4。立ち絵と同じ）。</summary>
    public IEnumerable<Preset> PresetsInDisplayOrder() =>
        Presets.Where(p => p.Favorite).Concat(Presets.Where(p => !p.Favorite));

    /// <summary>名前で指定したプリセットのお気に入りを切り替える。見つかれば切り替え後の値を返す。</summary>
    public bool? ToggleFavorite(string name)
    {
        var p = Presets.FirstOrDefault(x => x.Name == name);
        if (p == null) return null;
        p.Favorite = !p.Favorite;
        return p.Favorite;
    }

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
    /// お気に入りと「*」「!」ルールの指定も引き継ぐ（v0.1.4）。
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
        return new Preset
        {
            Name = source.Name, Overrides = o,
            Favorite = source.Favorite, AsteriskRule = source.AsteriskRule, ExclamationRule = source.ExclamationRule,
        };
    }

    /// <summary>
    /// 取り込んだプリセットを加える。同じ名前があるとき、overwrite なら置き換え、そうでなければ「名前 (2)」のように番号を付けて加える。
    /// 戻り値は加えた（置き換えた）プリセットの名前。
    /// お気に入りとルールの指定も加える。置き換えるときは、図形側でお気に入りにしていたら、そのまま残す。
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
                var copy = CopyOf(p, name);
                if (idx >= 0)
                {
                    copy.Favorite |= file.Presets[idx].Favorite;
                    file.Presets[idx] = copy;
                }
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
                file.Presets.Add(CopyOf(p, name));
            }
            names.Add(name);
        }
        return names;
    }

    static Preset CopyOf(Preset p, string name) => new()
    {
        Name = name, Overrides = p.Overrides.Clone(),
        Favorite = p.Favorite, AsteriskRule = p.AsteriskRule, ExclamationRule = p.ExclamationRule,
    };
}

/// <summary>
/// YMM4 標準のPSD立ち絵の設定（&lt;PSD名&gt;-ymm.json）のプリセットを図形に取り込む（v1.1.0）。
/// 読み方は高速PSD立ち絵の「標準から取り込み」（<see cref="StandardImport"/>。立ち絵と同じソース）と同じ。
/// 図形には口パク・目パチがないので、プリセットだけを使う（結果の説明にも書かない）。
/// 「!」「*」ルールのままでは標準と同じ見た目にならないプリセットには、選んだときにアイテムのルールをオフにする指定が付いている。
/// </summary>
public static class ShapeStandardImport
{
    /// <summary>取り込み元のプリセット（設定ファイルの中で名前が重なるものは「名前 (2)」にしてある）</summary>
    public static StandardImport.Result Read(PsdFile psd, string json) => StandardImport.Import(psd, json, "");

    /// <summary>
    /// 取り込んだ結果の説明（画面とログ用）。imported は取り込んだプリセットの（取り込み元での名前, 図形の一覧での名前）。
    /// </summary>
    public static string Summary(StandardImport.Result r, IReadOnlyList<(string Source, string Final)> imported)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{imported.Count}件のプリセットを取り込みました。");
        foreach (var (src, fin) in imported.Take(15)) sb.AppendLine(src == fin ? $"  ・{fin}" : $"  ・{src} → {fin}");
        if (imported.Count > 15) sb.AppendLine($"  ほか {imported.Count - 15}件");
        var chosen = imported.ToDictionary(x => x.Source, x => x.Final, StringComparer.Ordinal);

        // 見つからなかった要素は、取り込んだプリセットの分だけ
        var skipped = r.Skipped.Where(s => chosen.Keys.Any(k => s.StartsWith($"プリセット「{k}」", StringComparison.Ordinal))).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine($"PSD に見つからない要素がありました（その要素は飛ばしています）:");
            foreach (var s in skipped.Take(15)) sb.AppendLine("  ・" + s);
            if (skipped.Count > 15) sb.AppendLine($"  ほか {skipped.Count - 15}件");
        }
        foreach (var (p, els) in r.HidesExclamation)
            if (chosen.TryGetValue(p, out var fin))
                sb.AppendLine($"プリセット「{fin}」は「!」の要素（{string.Join("、", els.Take(3).Select(Last))}{(els.Count > 3 ? " など" : "")}）を隠しているので、選ぶとそのアイテムの「!」ルールをオフにします。");
        foreach (var (p, folders) in r.MultiAsterisk)
            if (chosen.TryGetValue(p, out var fin))
                sb.AppendLine($"プリセット「{fin}」は同じフォルダの「*」の要素を2つ以上表示している（{string.Join("、", folders.Take(3))}）ので、選ぶとそのアイテムの「*」ルールをオフにします。");
        return sb.ToString().TrimEnd();
    }

    /// <summary>取り込み元のプリセットの名前の横に出す、選んだときに変わるルールの説明（なければ空）</summary>
    public static string RuleNote(Preset p)
    {
        var parts = new List<string>();
        if (p.ExclamationRule == false) parts.Add("「!」");
        if (p.AsteriskRule == false) parts.Add("「*」");
        return parts.Count == 0 ? "" : $"（選ぶと{string.Join("", parts)}ルールをオフ）";
    }

    static string Last(string key) => key[(key.LastIndexOf('/') + 1)..];
}
