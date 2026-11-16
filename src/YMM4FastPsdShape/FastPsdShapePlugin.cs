using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

namespace YMM4FastPsdShape;

/// <summary>YMM4 高速PSD図形プラグイン（図形の種類「高速PSD」）</summary>
[YukkuriMovieMaker.Plugin.PluginDetails(AuthorName = AuthorName, ContentId = ContentId)]
public sealed class FastPsdShapePlugin : IShapePlugin
{
    const string AuthorName = "Tebasaki";

    /// <summary>
    /// ニコニコの親作品登録に使う作品ID（v1.0.0）。プラグインごとに違う ID なので、ほかのプラグインに写さないこと。
    /// </summary>
    const string ContentId = "sm47054167";

    /// <summary>
    /// 使用素材一覧・親作品一覧に出す作者名と作品ID。
    /// YMM4 は図形などのプラグインではクラスの属性ではなく、このプロパティ（IPlugin.Details）を読む。
    /// </summary>
    public YukkuriMovieMaker.Plugin.PluginDetailsAttribute Details { get; } = new() { AuthorName = AuthorName, ContentId = ContentId };

    /// <summary>PSD のドラッグ＆ドロップを受け取る準備（v0.2.0。YMM4 の起動時にプラグインが作られたときに1回だけ）</summary>
    public FastPsdShapePlugin() => PsdDropHandler.Install();

    public string Name => "高速PSD";

    // AviUtl 向けの出力には対応しない
    public bool IsExoShapeSupported => false;
    public bool IsExoMaskSupported => false;

    /// <summary>標準の「PSD」と同じ「ファイル」の分類に、そのすぐ後ろに並べる</summary>
    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ShapeGroupFileName;
    public int DefaultOrder => 521;

    public IShapeParameter CreateShapeParameter(SharedDataStore? sharedData) => new FastPsdShapeParameter(sharedData);
}
