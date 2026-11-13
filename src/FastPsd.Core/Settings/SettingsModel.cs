using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastPsd.Core.Settings;

// 高速PSD立ち絵 v0.1.9 と同じ内容。図形は LayerOverrides と Preset を使い、
// 立ち絵の設定ファイル（SettingsFile）は「立ち絵から取り込み」で読むだけ。

/// <summary>
/// レイヤーの差分（上書き）。立ち絵アイテム・表情アイテム・プリセットが持つ。
/// 変更した項目だけを持ち、適用は StateResolver が行う。
/// </summary>
public sealed class LayerOverrides
{
    /// <summary>レイヤーキー → 表示するか</summary>
    public Dictionary<string, bool> Visible { get; set; } = new(StringComparer.Ordinal);

    /// <summary>フォルダのキー（最上位は ""）→ 表示する子のキー。「*」グループやスライダーの選択に使う。</summary>
    public Dictionary<string, string> Select { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsEmpty => Visible.Count == 0 && Select.Count == 0;

    public LayerOverrides Clone() => new()
    {
        Visible = new Dictionary<string, bool>(Visible, StringComparer.Ordinal),
        Select = new Dictionary<string, string>(Select, StringComparer.Ordinal),
    };

    public string ToJson() => JsonSerializer.Serialize(this, SettingsJson.Compact);

    public static LayerOverrides FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LayerOverrides();
        try { return JsonSerializer.Deserialize<LayerOverrides>(json, SettingsJson.Compact) ?? new LayerOverrides(); }
        catch (JsonException) { return new LayerOverrides(); }
    }
}

public sealed class Preset
{
    public string Name { get; set; } = "";
    public LayerOverrides Overrides { get; set; } = new();
    /// <summary>お気に入り（一覧の上に並べる）。古いファイルにはなく、false として読む。</summary>
    public bool Favorite { get; set; }

    /// <summary>
    /// 選んだときに、そのアイテムの「*」ルールをこの値にする（null なら変えない）。
    /// 立ち絵が標準から取り込んだプリセットで、「*」ルールのままでは標準と同じ見た目にならないものに付く（立ち絵 v0.1.7）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AsteriskRule { get; set; }

    /// <summary>選んだときに、そのアイテムの「!」ルールをこの値にする（null なら変えない）。AsteriskRule と同じく取り込みで使う。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExclamationRule { get; set; }
}

/// <summary>口パク：口の差分ごとの設定。Frames は開→閉の順（上が開いた口）。</summary>
public sealed class LipSyncSetting
{
    public string TargetKey { get; set; } = "";
    public List<string> Frames { get; set; } = [];
}

/// <summary>YMM4 から届く母音（TachieSourceDescription.MouthShape と同じ並び）</summary>
public enum Vowel
{
    Silent,
    A,
    I,
    U,
    E,
    O,
}

/// <summary>
/// 母音口パク：口の差分（対象）ごとの設定。あ・い・う・え・お・無音のそれぞれに口を1つずつ（キー。空なら未設定）。
/// 対象はフォルダでもレイヤーでもよい。レイヤーなら「常に開く」でその口を出す。
/// </summary>
public sealed class VowelLipSyncSetting
{
    public string TargetKey { get; set; } = "";
    public string A { get; set; } = "";
    public string I { get; set; } = "";
    public string U { get; set; } = "";
    public string E { get; set; } = "";
    public string O { get; set; } = "";
    public string Silent { get; set; } = "";

    public string Get(Vowel v) => v switch
    {
        Vowel.A => A, Vowel.I => I, Vowel.U => U, Vowel.E => E, Vowel.O => O, _ => Silent,
    };

    public void Set(Vowel v, string key)
    {
        switch (v)
        {
            case Vowel.A: A = key; break;
            case Vowel.I: I = key; break;
            case Vowel.U: U = key; break;
            case Vowel.E: E = key; break;
            case Vowel.O: O = key; break;
            default: Silent = key; break;
        }
    }

    /// <summary>あ・い・う・え・お・無音の順</summary>
    public static readonly Vowel[] SlotOrder = [Vowel.A, Vowel.I, Vowel.U, Vowel.E, Vowel.O, Vowel.Silent];

    [JsonIgnore]
    public bool HasAny => SlotOrder.Any(v => Get(v).Length > 0);
}

/// <summary>目パチ：目の差分ごとの設定。Frames は開→半→閉の順。</summary>
public sealed class BlinkSetting
{
    public string TargetKey { get; set; } = "";
    public List<string> Frames { get; set; } = [];
    /// <summary>1コマの長さ（動画のフレーム数）</summary>
    public int FrameLength { get; set; } = 1;
    /// <summary>true: ランダム（Min〜Max秒の間）、false: 固定（Min秒ごと）</summary>
    public bool RandomInterval { get; set; } = true;
    public double IntervalMinSeconds { get; set; } = 1.0;
    public double IntervalMaxSeconds { get; set; } = 2.0;
}

/// <summary>キャラごとの設定セット（同じPSDを使う別キャラとは共有しない）</summary>
public sealed class SettingSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>
    /// 口パクの感度（%）。YMM4標準のPSD立ち絵と同じ意味で、開き具合 = 声の大きさ × 10 × 感度 ÷ 100（0〜1に切り詰め）。
    /// 大きいほど開きやすい。既定の 100% なら声の大きさ 0.1 で全開。
    /// </summary>
    public double LipSensitivityPercent { get; set; } = 100;

    /// <summary>
    /// v0.1.0 の感度（「この声の大きさで全開」の値）。読み込み時に % に換算して消す。
    /// 換算: % = 10 ÷ 旧の値（例: 0.2 → 50%）。
    /// </summary>
    [JsonPropertyName("LipSensitivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyLipSensitivity { get; set; }
    public List<Preset> Presets { get; set; } = [];
    public List<LipSyncSetting> LipSyncs { get; set; } = [];
    /// <summary>母音口パク（v0.1.5 から。古いファイルにはなく、空として読む）</summary>
    public List<VowelLipSyncSetting> VowelLipSyncs { get; set; } = [];
    public List<BlinkSetting> Blinks { get; set; } = [];
    /// <summary>スライダー化したフォルダのキー</summary>
    public List<string> SliderGroups { get; set; } = [];

    /// <summary>一覧に出す順番：お気に入りが上。お気に入り同士・それ以外同士は保存した順。</summary>
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

    /// <summary>別の設定セットの中身を丸ごとコピーした新しい設定セットを作る（複製コマンド）</summary>
    public SettingSet DuplicateAs(string name)
    {
        var json = JsonSerializer.Serialize(this, SettingsJson.Compact);
        var copy = JsonSerializer.Deserialize<SettingSet>(json, SettingsJson.Compact)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name;
        return copy;
    }
}

/// <summary>PSDと同じフォルダに置く「&lt;PSD名&gt;-ymmfastpsd.json」の中身</summary>
public sealed class SettingsFile
{
    /// <summary>1: 口パクのコマが閉→開の順、2: 開→閉の順、3: 口パクの感度を % で持つ</summary>
    public int Version { get; set; } = CurrentVersion;
    public const int CurrentVersion = 3;
    public List<SettingSet> SettingSets { get; set; } = [];

    public SettingSet? Find(string? id) => id == null ? null : SettingSets.FirstOrDefault(s => s.Id == id);

    public static string PathFor(string psdPath)
    {
        var dir = System.IO.Path.GetDirectoryName(psdPath) ?? "";
        return System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(psdPath) + "-ymmfastpsd.json");
    }

    public static SettingsFile Load(string psdPath)
    {
        var path = PathFor(psdPath);
        if (!File.Exists(path)) return new SettingsFile();
        var json = File.ReadAllText(path);
        var f = JsonSerializer.Deserialize<SettingsFile>(json, SettingsJson.Indented) ?? new SettingsFile();
        if (f.Version < 2)
            foreach (var lip in f.SettingSets.SelectMany(s => s.LipSyncs)) lip.Frames.Reverse();
        foreach (var s in f.SettingSets)
        {
            if (s.LegacyLipSensitivity is double old)
                s.LipSensitivityPercent = old > 0 ? Math.Round(10.0 / old, 1) : 100;
            else if (f.Version < 3)
                s.LipSensitivityPercent = 50; // 旧形式で値がない＝旧の既定値 0.2
            s.LegacyLipSensitivity = null;
        }
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

public static class SettingsJson
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
