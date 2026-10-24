using System.ComponentModel.DataAnnotations;
using FastPsd.Core.Settings;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;

namespace YMM4FastPsdShape;

/// <summary>図形アイテム（種類「高速PSD」）に保存する設定</summary>
public sealed class FastPsdShapeParameter(SharedDataStore? sharedData = null) : ShapeParameterBase(sharedData), IFileItem
{
    [Display(Name = "PSDファイル", Description = "表示するPSDファイル（.psb には対応していません）", Order = 0)]
    [FileSelector(FileGroupType.None, CustomFilterName = "PSDファイル", CustomFilterValue = "*.psd", FileGroup = FileGroupType.ImageItem, FileType = FileType.画像, PropertyEditorSize = PropertyEditorSize.FullWidth)]
    public string? FilePath { get => filePath; set => Set(ref filePath, value); }
    string? filePath;

    [Display(Name = "「*」ルール", Description = "同じフォルダ内で名前が「*」で始まる要素は、1つだけ表示します", Order = 10)]
    [ToggleSlider]
    public bool AsteriskRule { get => asteriskRule; set => Set(ref asteriskRule, value); }
    bool asteriskRule = true;

    [Display(Name = "「!」ルール", Description = "名前が「!」で始まる要素は、常に表示します", Order = 11)]
    [ToggleSlider]
    public bool ExclamationRule { get => exclamationRule; set => Set(ref exclamationRule, value); }
    bool exclamationRule = true;

    [Display(Name = "フォルダのクリッピング", Description = "オフ: Photoshop準拠（フォルダ自体のクリッピングを無視）\nオン: CLIP STUDIO PAINT準拠（適用）", Order = 12)]
    [ToggleSlider]
    public bool ApplyFolderClipping { get => applyFolderClipping; set => Set(ref applyFolderClipping, value); }
    bool applyFolderClipping;

    /// <summary>
    /// レイヤーの差分（変更した部分だけ）を JSON で持つ。
    /// YMM4 がプロジェクトに保存しやすいよう、文字列にしている。編集はレイヤーツリーの画面から行う。
    /// </summary>
    [Display(Name = "レイヤー", Description = "レイヤーの表示とプリセット", Order = 20)]
    [UI.LayerTreeEditor]
    public string OverridesJson { get => overridesJson; set => Set(ref overridesJson, value ?? ""); }
    string overridesJson = "";

    LayerOverrides? cache;
    string? cacheJson;

    /// <summary>差分を取り出す（毎フレーム呼ばれるので、JSON が変わったときだけ読み直す）</summary>
    public LayerOverrides GetOverrides()
    {
        var json = overridesJson;
        var c = cache;
        if (c == null || !ReferenceEquals(json, cacheJson))
        {
            c = LayerOverrides.FromJson(json);
            cache = c;
            cacheJson = json;
        }
        return c;
    }

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices) => new FastPsdShapeSource(devices, this);

    public override IEnumerable<string> CreateShapeItemExoFilter(int keyFrameIndex, ExoOutputDescription desc) => [];

    public override IEnumerable<string> CreateMaskExoFilter(int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskParameters) => [];

    protected override IEnumerable<IAnimatable> GetAnimatables() => [];

    // ---- 図形の種類を切り替えて戻ったときに設定を残す ----

    sealed class DataStore(FastPsdShapeParameter p)
    {
        public string? FilePath { get; } = p.FilePath;
        public string OverridesJson { get; } = p.OverridesJson;
        public bool AsteriskRule { get; } = p.AsteriskRule;
        public bool ExclamationRule { get; } = p.ExclamationRule;
        public bool ApplyFolderClipping { get; } = p.ApplyFolderClipping;

        public void ApplyTo(FastPsdShapeParameter p)
        {
            p.FilePath = FilePath;
            p.OverridesJson = OverridesJson;
            p.AsteriskRule = AsteriskRule;
            p.ExclamationRule = ExclamationRule;
            p.ApplyFolderClipping = ApplyFolderClipping;
        }
    }

    protected override void LoadSharedData(SharedDataStore store) => store.Load<DataStore>()?.ApplyTo(this);

    protected override void SaveSharedData(SharedDataStore store) => store.Save(new DataStore(this));

    // ---- 素材の一覧・ファイルの置き換え ----

    public IEnumerable<string> GetFiles()
    {
        if (string.IsNullOrEmpty(FilePath)) yield break;
        yield return FilePath;
        var json = ShapePresetFile.PathFor(FilePath);
        if (File.Exists(json)) yield return json;
    }

    public void ReplaceFile(string from, string to)
    {
        if (string.Equals(FilePath, from, StringComparison.OrdinalIgnoreCase)) FilePath = to;
    }

    public IEnumerable<TimelineResource> GetResources()
    {
        if (TimelineResource.TryParseFromPath(FilePath, TimelineResourceType.Image, out var resource))
            yield return resource;
    }
}
