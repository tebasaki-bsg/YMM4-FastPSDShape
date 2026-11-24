using System.Text.Json;
using FastPsd.Core.Psd;
using FastPsd.Core.State;

namespace FastPsd.Core.Settings;

/// <summary>
/// YMM4 標準のPSD立ち絵の設定ファイル（&lt;PSD名&gt;-ymm.json）から、新しい設定セットを作る（v0.1.7）。
/// 元のファイルは読むだけ。標準の中身は YukkuriMovieMaker.Plugin.Tachie.Psd / FileSource.Psd（v4.56.1.0）で確認した形:
///  ・Presets: { Name, Layers（表示する要素のIDの全部。フォルダも含む）, LayerPaths? }
///  ・MouthAnimations: { Layers（開→閉。先頭がキー）, LayerPaths? }
///  ・MouthVowelAnimations: { Key, A, I, U, E, O, Silence, KeyPath, APath, … }
///  ・EyeAnimations: { Layers（開→閉。先頭がキー）, LayerPaths?, Offset, Interval }
/// 要素の探し方は標準と同じで、パス（"\u001c名前\u001d同名の何番目か" を上から並べたもの）、なければ ID
/// （"i{レイヤーID}"、レイヤーIDのないファイルは "n{何番目のレイヤーか}"）。
/// </summary>
public static class StandardImport
{
    public sealed class Result
    {
        public required SettingSet Set { get; init; }
        public int Presets, Lips, Vowels, Blinks;
        /// <summary>見つからずに飛ばした要素・設定の説明</summary>
        public List<string> Skipped { get; } = [];
        /// <summary>「!」の要素を隠しているプリセット（「!」ルールがオンだと隠せない）と、その要素</summary>
        public List<(string Preset, List<string> Elements)> HidesExclamation { get; } = [];
        /// <summary>同じフォルダの「*」の要素を2つ以上表示しているプリセット（「*」ルールがオンだと1つしか出ない）</summary>
        public List<(string Preset, List<string> Folders)> MultiAsterisk { get; } = [];
        public List<string> Renamed { get; } = [];
    }

    public static string PathFor(string psdPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(psdPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(psdPath) + "-ymm.json");

    /// <summary>要素の探し方（標準と同じ ID・パス）</summary>
    public sealed class Resolver
    {
        readonly Dictionary<string, PsdLayer> _byId = new(StringComparer.Ordinal);
        readonly Dictionary<string, PsdLayer> _byPath = new(StringComparer.Ordinal);

        public Resolver(PsdFile psd)
        {
            void Walk(List<PsdLayer> siblings, string parentPath)
            {
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var l in siblings) // 上から（標準の「同名の何番目か」と同じ並び）
                {
                    int n = seen.TryGetValue(l.Name, out var c) ? c : 0;
                    seen[l.Name] = n + 1;
                    var path = $"{parentPath}\u001c{l.Name}\u001d{n}";
                    _byPath[path] = l;
                    _byId[l.LayerId is uint id ? $"i{id}" : $"n{l.RecordIndex}"] = l;
                    if (l.IsGroup) Walk(l.Children, path);
                }
            }
            Walk(psd.Roots, "");
        }

        public PsdLayer? Find(string? id, string? path)
        {
            if (!string.IsNullOrEmpty(path) && _byPath.TryGetValue(path, out var p)) return p;
            return id != null && _byId.TryGetValue(id, out var l) ? l : null;
        }
    }

    public static Result Import(PsdFile psd, string json, string setName)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var resolver = new Resolver(psd);
        var set = new SettingSet { Name = setName, LipSensitivityPercent = 100 };
        var result = new Result { Set = set };

        static List<string?> Strings(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return [];
            return a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList();
        }
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

        /// <summary>Layers / LayerPaths を要素に。見つからないものは null（何番目かを保つ）</summary>
        List<PsdLayer?> ResolveList(JsonElement e)
        {
            var ids = Strings(e, "Layers");
            var paths = Strings(e, "LayerPaths");
            return ids.Select((id, i) => resolver.Find(id, i < paths.Count ? paths[i] : null)).ToList();
        }

        // ---- プリセット（PSDの保存状態との差に変換） ----
        var flip = FlipMap.For(psd);
        bool InFlip(PsdLayer l) { for (var x = l; x != null; x = x.Parent) if (flip.Flipped.Contains(x.Index)) return true; return false; }
        var baseState = StateResolver.Resolve(new StateInput { Psd = psd });
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Array(root, "Presets"))
        {
            var name = Str(p, "Name") ?? "";
            var resolved = ResolveList(p);
            int missing = resolved.Count(x => x == null);
            var shown = resolved.Where(x => x != null).Select(x => x!.Index).ToHashSet();
            var o = new LayerOverrides();
            foreach (var l in psd.All)
            {
                if (InFlip(l)) continue; // 反転用は通常用から決まる
                bool want = shown.Contains(l.Index);
                if (want != baseState.Visible[l.Index]) o.Visible[l.Key] = want;
            }
            // 「!」「*」ルールがオンのままだと再現できない要素を調べる
            var bang = psd.All.Where(l => !InFlip(l) && l.Name.StartsWith('!') && !shown.Contains(l.Index)).Select(l => l.Key).ToList();

            var multi = psd.All.Where(l => !InFlip(l) && l.Name.StartsWith('*') && shown.Contains(l.Index))
                .GroupBy(l => l.Parent).Where(g => g.Count() > 1).Select(g => g.Key?.Key ?? "（最上位）").ToList();

            var unique = name;
            for (int k = 2; !names.Add(unique); k++) unique = $"{name} ({k})";
            if (unique != name) result.Renamed.Add($"「{name}」→「{unique}」");
            if (bang.Count > 0) result.HidesExclamation.Add((unique, bang));
            if (multi.Count > 0) result.MultiAsterisk.Add((unique, multi));
            // 標準には「!」「*」ルールがないので、そのままでは同じ見た目にならないプリセットは、選んだときにアイテムのルールをオフにする
            set.Presets.Add(new Preset
            {
                Name = unique, Overrides = o,
                ExclamationRule = bang.Count > 0 ? false : null,
                AsteriskRule = multi.Count > 0 ? false : null,
            });
            result.Presets++;
            if (missing > 0) result.Skipped.Add($"プリセット「{unique}」: 見つからない要素 {missing}件");
        }

        // ---- 口パク（音量） ----
        foreach (var m in Array(root, "MouthAnimations"))
        {
            var frames = ResolveList(m);
            if (frames.Count == 0 || frames[0] == null) { result.Skipped.Add("口パク: キーの要素が見つからない"); continue; }
            var target = frames[0]!.Key;
            var keys = frames.Where(f => f != null).Select(f => f!.Key).Distinct().ToList();
            if (frames.Any(f => f == null)) result.Skipped.Add($"口パク（{target}）: 見つからないコマ {frames.Count(f => f == null)}件");
            if (keys.Count < 2) { result.Skipped.Add($"口パク（{target}）: コマが2枚未満"); continue; }
            if (set.LipSyncs.Any(x => x.TargetKey == target)) { result.Skipped.Add($"口パク（{target}）: 同じキーの2つ目以降"); continue; }
            set.LipSyncs.Add(new LipSyncSetting { TargetKey = target, Frames = keys });
            result.Lips++;
        }

        // ---- 母音口パク ----
        foreach (var v in Array(root, "MouthVowelAnimations"))
        {
            var key = resolver.Find(Str(v, "Key"), Str(v, "KeyPath"));
            if (key == null) { result.Skipped.Add("母音口パク: キーの要素が見つからない"); continue; }
            if (set.VowelLipSyncs.Any(x => x.TargetKey == key.Key)) { result.Skipped.Add($"母音口パク（{key.Key}）: 同じキーの2つ目以降"); continue; }
            var s = new VowelLipSyncSetting { TargetKey = key.Key };
            foreach (var (slot, field) in new[] { (Vowel.A, "A"), (Vowel.I, "I"), (Vowel.U, "U"), (Vowel.E, "E"), (Vowel.O, "O"), (Vowel.Silent, "Silence") })
            {
                var id = Str(v, field);
                if (string.IsNullOrEmpty(id)) continue;
                var l = resolver.Find(id, Str(v, field + "Path"));
                if (l == null) { result.Skipped.Add($"母音口パク（{key.Key}）: {field} の要素が見つからない"); continue; }
                s.Set(slot, l.Key);
            }
            if (!s.HasAny) { result.Skipped.Add($"母音口パク（{key.Key}）: 口が1つもない"); continue; }
            set.VowelLipSyncs.Add(s);
            result.Vowels++;
        }

        // ---- 目パチ ----
        foreach (var e in Array(root, "EyeAnimations"))
        {
            var frames = ResolveList(e);
            if (frames.Count == 0 || frames[0] == null) { result.Skipped.Add("目パチ: キーの要素が見つからない"); continue; }
            var target = frames[0]!.Key;
            var keys = frames.Where(f => f != null).Select(f => f!.Key).Distinct().ToList();
            if (frames.Any(f => f == null)) result.Skipped.Add($"目パチ（{target}）: 見つからないコマ {frames.Count(f => f == null)}件");
            if (keys.Count < 2) { result.Skipped.Add($"目パチ（{target}）: コマが2枚未満"); continue; }
            if (set.Blinks.Any(x => x.TargetKey == target)) { result.Skipped.Add($"目パチ（{target}）: 同じキーの2つ目以降"); continue; }
            double interval = Num(e, "Interval");
            set.Blinks.Add(new BlinkSetting
            {
                TargetKey = target,
                Frames = keys,
                // 標準は 0.3 秒で 開→閉→開。ここでは 0.3 秒 × 60fps をコマ数で割る（四捨五入）
                FrameLength = Math.Max(1, (int)Math.Round(0.3 * 60 / keys.Count, MidpointRounding.AwayFromZero)),
                // 間隔: 秒が指定されていれば固定、0（自動）なら標準の範囲のランダム 5〜10 秒。最初のまばたき（Offset）は使わない
                RandomInterval = !(interval > 0),
                IntervalMinSeconds = interval > 0 ? interval : 5,
                IntervalMaxSeconds = interval > 0 ? interval : 10,
            });
            result.Blinks++;
        }
        return result;
    }

    /// <summary>結果の説明（画面とログ用）</summary>
    public static string Summary(Result r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"設定セット「{r.Set.Name}」を作りました。");
        sb.AppendLine($"プリセット {r.Presets}件 / 口パク {r.Lips}件 / 母音口パク {r.Vowels}件 / 目パチ {r.Blinks}件（口パクの感度は 100%）");
        if (r.Renamed.Count > 0) sb.AppendLine("同じ名前のプリセットの名前を変えました: " + string.Join("、", r.Renamed));
        if (r.Skipped.Count > 0)
        {
            sb.AppendLine($"飛ばしたもの {r.Skipped.Count}件:");
            foreach (var s in r.Skipped.Take(20)) sb.AppendLine("  ・" + s);
            if (r.Skipped.Count > 20) sb.AppendLine($"  ほか {r.Skipped.Count - 20}件");
        }
        foreach (var (p, els) in r.HidesExclamation)
            sb.AppendLine($"プリセット「{p}」は「!」の要素（{string.Join("、", els.Take(3).Select(Last))}{(els.Count > 3 ? " など" : "")}）を隠しているので、選ぶとそのアイテムの「!」ルールをオフにします。");
        foreach (var (p, folders) in r.MultiAsterisk)
            sb.AppendLine($"プリセット「{p}」は同じフォルダの「*」の要素を2つ以上表示している（{string.Join("、", folders.Take(3))}）ので、選ぶとそのアイテムの「*」ルールをオフにします。");
        return sb.ToString().TrimEnd();
    }

    static string Last(string key) => key[(key.LastIndexOf('/') + 1)..];
}
