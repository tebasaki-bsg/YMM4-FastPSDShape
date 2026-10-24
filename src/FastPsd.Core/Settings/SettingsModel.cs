using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastPsd.Core.Settings;

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
}

/// <summary>口パク：口の差分ごとの設定。Frames は開→閉の順（上が開いた口）。</summary>
public sealed class LipSyncSetting
{
    public string TargetKey { get; set; } = "";
    public List<string> Frames { get; set; } = [];
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
    /// <summary>口パクの感度（この音量で全開とみなす）</summary>
    public double LipSensitivity { get; set; } = 0.2;
    public List<Preset> Presets { get; set; } = [];
    public List<LipSyncSetting> LipSyncs { get; set; } = [];
    public List<BlinkSetting> Blinks { get; set; } = [];
    /// <summary>スライダー化したフォルダのキー</summary>
    public List<string> SliderGroups { get; set; } = [];

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
    /// <summary>1: 口パクのコマが閉→開の順、2: 開→閉の順</summary>
    public int Version { get; set; } = CurrentVersion;
    public const int CurrentVersion = 2;
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
