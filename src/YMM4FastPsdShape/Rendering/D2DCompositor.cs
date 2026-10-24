using System.Numerics;
using FastPsd.Core.Compose;
using FastPsd.Core.Psd;
using Vortice.Direct2D1;
using D2DBlend = Vortice.Direct2D1.BlendMode;

namespace YMM4FastPsdShape.Rendering;

/// <summary>合成結果（コマンドリスト）と、それが参照しているエフェクトなどの後片付け用リスト</summary>
internal sealed class ComposeResult : IDisposable
{
    public required ID2D1Image Image { get; init; }
    public List<IDisposable> Resources { get; } = [];

    public void Dispose()
    {
        for (int i = Resources.Count - 1; i >= 0; i--) Resources[i].Dispose();
        Resources.Clear();
    }
}

/// <summary>
/// RenderPlan を Direct2D のコマンドリストに変換する。
/// コマンドリストは描画手順を記録するだけなので、作成は軽い。実際の描画は YMM4 が出力を使うときに GPU で行われる。
/// </summary>
internal static class D2DCompositor
{
    public static ComposeResult Compose(ID2D1DeviceContext6 dc, RenderPlan plan, IReadOnlyDictionary<PsdLayer, ID2D1Bitmap1> bitmaps)
    {
        var resources = new List<IDisposable>();
        var builder = new Builder(dc, bitmaps, resources);
        var image = builder.BuildList(plan.Ops);
        var result = new ComposeResult { Image = image };
        result.Resources.AddRange(resources);
        return result;
    }

    sealed class Builder(ID2D1DeviceContext6 dc, IReadOnlyDictionary<PsdLayer, ID2D1Bitmap1> bitmaps, List<IDisposable> resources)
    {
        readonly record struct Prepared(RenderOp Op, ID2D1Image Image, Vector2 Offset);

        /// <summary>手順の列を1枚の画像（コマンドリスト）にする。入れ子の画像は先に作る（描画中に別の描画は始められないため）。</summary>
        public ID2D1Image BuildList(List<RenderOp> ops)
        {
            var prepared = ops.Select(Prepare).ToList();
            var cl = dc.CreateCommandList();
            resources.Add(cl);
            dc.Target = cl;
            dc.BeginDraw();
            dc.Clear(null);
            foreach (var p in prepared) Draw(p, atop: false);
            dc.EndDraw();
            dc.Target = null;
            cl.Close();
            return cl;
        }

        Prepared Prepare(RenderOp op)
        {
            ID2D1Image img;
            Vector2 offset = Vector2.Zero;
            switch (op)
            {
                case LayerOp l:
                    img = bitmaps[l.Layer];
                    offset = new Vector2(l.Layer.Left, l.Layer.Top);
                    break;
                case GroupOp g:
                    img = BuildList(g.Children);
                    break;
                case ClipOp c:
                    img = BuildClip(c);
                    break;
                default:
                    throw new NotSupportedException(op.GetType().Name);
            }
            if (op.Opacity < 0.999f)
            {
                var e = new Vortice.Direct2D1.Effects.Opacity(dc) { Value = op.Opacity };
                e.SetInput(0, img, true);
                var output = e.Output;
                resources.Add(e);
                resources.Add(output);
                img = output;
            }
            return new Prepared(op, img, offset);
        }

        /// <summary>
        /// クリッピング：土台を描き、上に重ねるレイヤーを土台の透明度の範囲だけに描く。
        /// すべて通常合成なら SourceAtop で正確に描ける。
        /// 通常以外のブレンドが含まれる場合は、土台の上でブレンドしてから土台の透明度で切り抜く（縁がわずかに異なる場合がある）。
        /// </summary>
        ID2D1Image BuildClip(ClipOp c)
        {
            var basePrep = Prepare(c.Base);
            var clipped = c.Clipped.Select(Prepare).ToList();
            bool allNormal = clipped.All(p => p.Op.Blend == PsdBlendMode.Normal);

            var cl = dc.CreateCommandList();
            resources.Add(cl);
            dc.Target = cl;
            dc.BeginDraw();
            dc.Clear(null);
            Draw(basePrep, atop: false);
            foreach (var p in clipped) Draw(p, atop: allNormal);
            dc.EndDraw();
            dc.Target = null;
            cl.Close();
            if (allNormal) return cl;

            var masked = dc.CreateCommandList();
            resources.Add(masked);
            dc.Target = masked;
            dc.BeginDraw();
            dc.Clear(null);
            dc.DrawImage(cl, null, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            dc.DrawImage(basePrep.Image, basePrep.Offset, null, InterpolationMode.NearestNeighbor, CompositeMode.DestinationIn);
            dc.EndDraw();
            dc.Target = null;
            masked.Close();
            return masked;
        }

        void Draw(Prepared p, bool atop)
        {
            var mode = p.Op.Blend;
            if (mode is PsdBlendMode.Normal or PsdBlendMode.PassThrough)
            {
                dc.DrawImage(p.Image, p.Offset, null, InterpolationMode.NearestNeighbor,
                    atop ? CompositeMode.SourceAtop : CompositeMode.SourceOver);
                return;
            }
            dc.BlendImage(p.Image, ToD2D(mode), p.Offset, null, InterpolationMode.NearestNeighbor);
        }
    }

    static D2DBlend ToD2D(PsdBlendMode m) => m switch
    {
        PsdBlendMode.Dissolve => D2DBlend.Dissolve,
        PsdBlendMode.Darken => D2DBlend.Darken,
        PsdBlendMode.Multiply => D2DBlend.Multiply,
        PsdBlendMode.ColorBurn => D2DBlend.ColorBurn,
        PsdBlendMode.LinearBurn => D2DBlend.LinearBurn,
        PsdBlendMode.DarkerColor => D2DBlend.DarkerColor,
        PsdBlendMode.Lighten => D2DBlend.Lighten,
        PsdBlendMode.Screen => D2DBlend.Screen,
        PsdBlendMode.ColorDodge => D2DBlend.ColorDodge,
        PsdBlendMode.LinearDodge => D2DBlend.LinearDodge,
        PsdBlendMode.LighterColor => D2DBlend.LighterColor,
        PsdBlendMode.Overlay => D2DBlend.Overlay,
        PsdBlendMode.SoftLight => D2DBlend.SoftLight,
        PsdBlendMode.HardLight => D2DBlend.HardLight,
        PsdBlendMode.VividLight => D2DBlend.VividLight,
        PsdBlendMode.LinearLight => D2DBlend.LinearLight,
        PsdBlendMode.PinLight => D2DBlend.PinLight,
        PsdBlendMode.HardMix => D2DBlend.HardMix,
        PsdBlendMode.Difference => D2DBlend.Difference,
        PsdBlendMode.Exclusion => D2DBlend.Exclusion,
        PsdBlendMode.Subtract => D2DBlend.Subtract,
        PsdBlendMode.Divide => D2DBlend.Division,
        PsdBlendMode.Hue => D2DBlend.Hue,
        PsdBlendMode.Saturation => D2DBlend.Saturation,
        PsdBlendMode.Color => D2DBlend.Color,
        PsdBlendMode.Luminosity => D2DBlend.Luminosity,
        _ => D2DBlend.Multiply,
    };
}
