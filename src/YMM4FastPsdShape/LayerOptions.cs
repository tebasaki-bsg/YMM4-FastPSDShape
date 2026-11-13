using System.ComponentModel.DataAnnotations;
using FastPsd.Core.State;

namespace YMM4FastPsdShape;

/// <summary>反転（v0.1.4。高速PSD立ち絵と同じ並び）。保存される値は変えないこと。</summary>
public enum FlipSetting
{
    [Display(Name = "なし", Order = 0)]
    None = 0,
    [Display(Name = "左右", Order = 1)]
    X = 1,
    [Display(Name = "上下", Order = 2)]
    Y = 2,
    [Display(Name = "上下左右", Order = 3)]
    XY = 3,
}

/// <summary>アイテムごとのレイヤーの扱い（レイヤーツリーと描画が使う）</summary>
internal interface ILayerOptionsOwner
{
    FlipSetting Flip { get; }
    bool AsteriskRule { get; set; }
    bool ExclamationRule { get; set; }
    bool FlipRule { get; }
    bool ApplyFolderClipping { get; }
}

internal static class LayerOptions
{
    public static FlipMode ToMode(this FlipSetting f) => f switch
    {
        FlipSetting.X => FlipMode.X,
        FlipSetting.Y => FlipMode.Y,
        FlipSetting.XY => FlipMode.XY,
        _ => FlipMode.None,
    };

    public static RuleOptions GetRules(this ILayerOptionsOwner? o) => o == null
        ? new RuleOptions()
        : new RuleOptions { Asterisk = o.AsteriskRule, Exclamation = o.ExclamationRule, Flip = o.FlipRule };

    public const string FlipDescription =
        "図形を反転します。「:flip」ルールがオンなら、名前の末尾が「:flipx」（左右）「:flipy」（上下）「:flipxy」（上下左右）の反転用の要素に入れ替えます。" +
        "YMM4 本体の「左右反転」とは別のものなので、一緒に使わないでください（両方オンにすると元の向きに戻ります）";
}
