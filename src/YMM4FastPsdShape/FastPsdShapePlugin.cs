using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

namespace YMM4FastPsdShape;

/// <summary>YMM4 高速PSD図形プラグイン（図形の種類「高速PSD」）</summary>
[YukkuriMovieMaker.Plugin.PluginDetails(AuthorName = "Tebasaki")]
public sealed class FastPsdShapePlugin : IShapePlugin
{
    public string Name => "高速PSD";

    // AviUtl 向けの出力には対応しない
    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    /// <summary>標準の「PSD」と同じ「ファイル」の分類に、そのすぐ後ろに並べる</summary>
    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ShapeGroupFileName;
    public int DefaultOrder => 521;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData) => new FastPsdShapeParameter(sharedData);
}
