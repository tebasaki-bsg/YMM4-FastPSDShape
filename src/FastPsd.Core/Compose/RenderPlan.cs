using FastPsd.Core.Psd;
using FastPsd.Core.State;

namespace FastPsd.Core.Compose;

/// <summary>合成の手順。下から上の順に並べ、キャンバス座標で描く。</summary>
public abstract class RenderOp
{
    public float Opacity = 1f;
    public PsdBlendMode Blend = PsdBlendMode.Normal;
}

/// <summary>画像レイヤーを1枚描く</summary>
public sealed class LayerOp : RenderOp
{
    public required PsdLayer Layer;
}

/// <summary>中身を別の画像に合成してから、まとめて重ねる（「通常」のフォルダ）</summary>
public sealed class GroupOp : RenderOp
{
    public PsdLayer? Group;
    public List<RenderOp> Children = [];
}

/// <summary>
/// クリッピング。Base を描き、その上に Clipped を Base の透明度の範囲だけ重ねる。
/// できた画像を Base の不透明度・ブレンドモードで重ねる。
/// </summary>
public sealed class ClipOp : RenderOp
{
    public required RenderOp Base;
    public List<RenderOp> Clipped = [];
}

public sealed class RenderPlan
{
    public List<RenderOp> Ops = [];
    /// <summary>描く画像レイヤーの一覧（読み込みが必要なもの）</summary>
    public List<PsdLayer> Layers = [];
    public List<string> Warnings = [];

    /// <summary>
    /// 描く画像レイヤーが占める範囲（キャンバスの外は切り取る）。描くものがなければ幅・高さが 0。
    /// 合成結果を1枚の画像に焼き付けるときの大きさに使う。
    /// </summary>
    public (int Left, int Top, int Width, int Height) ContentBounds(int canvasWidth, int canvasHeight)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in Layers)
        {
            l = Math.Min(l, x.Left); t = Math.Min(t, x.Top);
            r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom);
        }
        l = Math.Max(l, 0); t = Math.Max(t, 0);
        r = Math.Min(r, canvasWidth); b = Math.Min(b, canvasHeight);
        if (Layers.Count == 0 || r <= l || b <= t) return (0, 0, 0, 0);
        return (l, t, r - l, b - t);
    }
}

public static class RenderPlanner
{
    /// <param name="applyFolderClipping">フォルダ自体のクリッピングを適用するか（CLIP STUDIO PAINT 準拠）。既定は Photoshop 準拠で無視。</param>
    public static RenderPlan Build(PsdFile psd, VisibilityState state, bool applyFolderClipping = false)
    {
        var plan = new RenderPlan();
        var warned = new HashSet<string>();
        void Warn(string w) { if (warned.Add(w)) plan.Warnings.Add(w); }

        List<RenderOp> BuildSiblings(List<PsdLayer> siblingsTopDown)
        {
            var ops = new List<RenderOp>();
            RenderOp? baseOp = null;          // 直前の（クリッピングの土台になれる）要素
            bool baseVisible = false;
            bool hasBase = false;            // 下に土台になれる要素があったか（非表示でも）
            for (int i = siblingsTopDown.Count - 1; i >= 0; i--)
            {
                var l = siblingsTopDown[i];
                bool clipped = l.IsClipped && (!l.IsGroup || applyFolderClipping);
                bool visible = state.EffectiveVisible[l.Index];

                if (clipped && !hasBase)
                    clipped = false; // 一番下のクリッピングは通常扱い

                if (clipped)
                {
                    if (!baseVisible || !visible) continue;
                    var op = BuildNode(l);
                    if (op == null) continue;
                    if (baseOp is not ClipOp clip)
                    {
                        // 土台を ClipOp に包み直す
                        int idx = ops.LastIndexOf(baseOp!);
                        clip = new ClipOp { Base = baseOp!, Opacity = baseOp!.Opacity, Blend = baseOp.Blend };
                        baseOp.Opacity = 1f;
                        baseOp.Blend = PsdBlendMode.Normal;
                        ops[idx] = clip;
                        baseOp = clip;
                    }
                    clip.Clipped.Add(op);
                    continue;
                }

                baseOp = null;
                baseVisible = false;
                hasBase = true;
                if (!visible) continue;
                var node = BuildNode(l);
                if (node == null) continue;
                ops.Add(node);
                baseOp = node;
                baseVisible = true;
            }
            // 中身がすべて通常合成の通過フォルダは展開して並べる（Flatten の対象）
            return ops;
        }

        RenderOp? BuildNode(PsdLayer l)
        {
            if (l.IsGroup)
            {
                var children = BuildSiblings(l.Children);
                if (children.Count == 0) return null;
                if (l.Mask != null && !l.Mask.Disabled) Warn("フォルダのレイヤーマスクは未対応です");
                if (l.Effects.Count > 0) Warn("レイヤー効果は未対応のため表示しません");
                if (l.Knockout) Warn("ノックアウトは未対応です");
                // フォルダの「塗り」は、効果がない場合は不透明度と同じように中身全体にかかる
                return new GroupOp { Group = l, Children = children, Opacity = l.Opacity * l.FillOpacity, Blend = l.BlendMode };
            }
            if (l.Adjustments.Count > 0)
            {
                Warn("調整レイヤー・塗りつぶしレイヤーは未対応のため表示しません");
                return null;
            }
            if (l.Effects.Count > 0) Warn("レイヤー効果は未対応のため表示しません");
            if (l.NonDefaultBlendRanges) Warn("ブレンド条件は未対応です");
            if (l.Knockout) Warn("ノックアウトは未対応です");
            if (!l.HasPixels) return null;
            plan.Layers.Add(l);
            return new LayerOp { Layer = l, Opacity = l.Opacity * l.FillOpacity, Blend = l.BlendMode };
        }

        plan.Ops = Flatten(BuildSiblings(psd.Roots));
        return plan;
    }

    /// <summary>
    /// 結果が変わらないフォルダを展開して、中間画像を減らす。
    /// ・通過フォルダで不透明度100% → 中身をそのまま親に並べる
    /// ・通常フォルダで不透明度100%、中身がすべて通常合成 → 同じ結果になるので展開
    /// </summary>
    static List<RenderOp> Flatten(List<RenderOp> ops)
    {
        var result = new List<RenderOp>(ops.Count);
        foreach (var op in ops)
        {
            switch (op)
            {
                case GroupOp g:
                    g.Children = Flatten(g.Children);
                    bool full = g.Opacity >= 0.999f;
                    if (full && (g.Blend == PsdBlendMode.PassThrough ||
                                 (g.Blend == PsdBlendMode.Normal && g.Children.All(IsNormal))))
                        result.AddRange(g.Children);
                    else
                    {
                        if (g.Blend == PsdBlendMode.PassThrough) g.Blend = PsdBlendMode.Normal; // 不透明度付きの通過フォルダは近似
                        result.Add(g);
                    }
                    break;
                case ClipOp c:
                    c.Base = Flatten([c.Base]) is [var single] ? single : new GroupOp { Children = Flatten([c.Base]) };
                    c.Clipped = Flatten(c.Clipped);
                    result.Add(c);
                    break;
                default:
                    result.Add(op);
                    break;
            }
        }
        return result;
    }

    static bool IsNormal(RenderOp op) => op.Blend == PsdBlendMode.Normal;
}
