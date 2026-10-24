using FastPsd.Core.Psd;

namespace FastPsd.Core.Compose;

/// <summary>
/// RenderPlan を CPU で合成する参照実装（テスト用）。GPU 版と同じ手順をたどり、
/// PSD に保存された Photoshop の合成結果と比べて、手順（フォルダ・クリッピング・不透明度・ブレンド）の正しさを確かめる。
/// バッファは乗算済み RGBA の float。
/// </summary>
public sealed class CpuCompositor
{
    readonly PsdFile _psd;
    readonly int _w, _h;
    readonly Dictionary<PsdLayer, float[]> _layerCache = new();

    public CpuCompositor(PsdFile psd)
    {
        _psd = psd;
        _w = psd.Width;
        _h = psd.Height;
    }

    public float[] Render(RenderPlan plan)
    {
        var canvas = new float[_w * _h * 4];
        foreach (var op in plan.Ops) Draw(canvas, op, atop: false);
        return canvas;
    }

    /// <summary>非乗算の RGBA(8bit) に変換</summary>
    public static byte[] ToStraightRgba(float[] c)
    {
        var o = new byte[c.Length];
        for (int i = 0; i < c.Length; i += 4)
        {
            float a = c[i + 3];
            o[i + 3] = (byte)Math.Clamp(MathF.Round(a * 255), 0, 255);
            if (a <= 0) continue;
            for (int k = 0; k < 3; k++) o[i + k] = (byte)Math.Clamp(MathF.Round(c[i + k] / a * 255), 0, 255);
        }
        return o;
    }

    void Draw(float[] dst, RenderOp op, bool atop)
    {
        float[] src = op switch
        {
            LayerOp l => LayerCanvas(l.Layer),
            GroupOp g => RenderChildren(g.Children),
            ClipOp c => RenderClip(c),
            _ => throw new NotSupportedException(),
        };
        Composite(dst, src, op.Opacity, op.Blend, atop);
    }

    float[] RenderChildren(List<RenderOp> ops)
    {
        var buf = new float[_w * _h * 4];
        foreach (var op in ops) Draw(buf, op, atop: false);
        return buf;
    }

    float[] RenderClip(ClipOp c)
    {
        var buf = new float[_w * _h * 4];
        Draw(buf, c.Base, atop: false);
        foreach (var op in c.Clipped) Draw(buf, op, atop: true);
        return buf;
    }

    float[] LayerCanvas(PsdLayer l)
    {
        if (_layerCache.TryGetValue(l, out var cached)) return cached;
        var buf = new float[_w * _h * 4];
        var bgra = _psd.DecodeBgra(l);
        try
        {
            for (int y = 0; y < l.Height; y++)
            {
                int cy = l.Top + y;
                if (cy < 0 || cy >= _h) continue;
                for (int x = 0; x < l.Width; x++)
                {
                    int cx = l.Left + x;
                    if (cx < 0 || cx >= _w) continue;
                    int s = (y * l.Width + x) * 4, d = (cy * _w + cx) * 4;
                    buf[d] = bgra[s + 2] / 255f;
                    buf[d + 1] = bgra[s + 1] / 255f;
                    buf[d + 2] = bgra[s] / 255f;
                    buf[d + 3] = bgra[s + 3] / 255f;
                }
            }
        }
        finally { PsdFile.ReturnBuffer(bgra); }
        _layerCache[l] = buf;
        return buf;
    }

    /// <summary>W3C Compositing and Blending の式。atop のときは結果の透明度を下地の透明度に揃える。</summary>
    static void Composite(float[] dst, float[] src, float opacity, PsdBlendMode mode, bool atop)
    {
        Span<float> cs = stackalloc float[3];
        Span<float> cb = stackalloc float[3];
        Span<float> b = stackalloc float[3];
        for (int i = 0; i < dst.Length; i += 4)
        {
            float as_ = src[i + 3] * opacity;
            if (as_ <= 0) continue;
            float ab = dst[i + 3];
            for (int k = 0; k < 3; k++)
            {
                cs[k] = src[i + k] / src[i + 3];
                cb[k] = ab > 0 ? dst[i + k] / ab : 0;
            }
            BlendColors(mode, cb, cs, b);
            float fa = atop ? ab : 1f;
            float fb = 1f - as_;
            for (int k = 0; k < 3; k++)
            {
                float mixed = (1 - ab) * cs[k] + ab * b[k];
                dst[i + k] = as_ * fa * mixed + ab * fb * cb[k];
            }
            dst[i + 3] = atop ? ab : as_ + ab * (1 - as_);
        }
    }

    static void BlendColors(PsdBlendMode mode, ReadOnlySpan<float> cb, ReadOnlySpan<float> cs, Span<float> r)
    {
        switch (mode)
        {
            case PsdBlendMode.Hue: SetLum(SetSat(cs, Sat(cb), r), Lum(cb), r); return;
            case PsdBlendMode.Saturation: SetLum(SetSat(cb, Sat(cs), r), Lum(cb), r); return;
            case PsdBlendMode.Color: SetLum(cs, Lum(cb), r); return;
            case PsdBlendMode.Luminosity: SetLum(cb, Lum(cs), r); return;
            case PsdBlendMode.DarkerColor: { var src = Lum(cs) < Lum(cb) ? cs : cb; src.CopyTo(r); return; }
            case PsdBlendMode.LighterColor: { var src = Lum(cs) > Lum(cb) ? cs : cb; src.CopyTo(r); return; }
        }
        for (int k = 0; k < 3; k++) r[k] = Separable(mode, cb[k], cs[k]);
    }

    static float Separable(PsdBlendMode mode, float b, float s) => mode switch
    {
        PsdBlendMode.Multiply => b * s,
        PsdBlendMode.Screen => b + s - b * s,
        PsdBlendMode.Darken => MathF.Min(b, s),
        PsdBlendMode.Lighten => MathF.Max(b, s),
        PsdBlendMode.ColorDodge => b <= 0 ? 0 : s >= 1 ? 1 : MathF.Min(1, b / (1 - s)),
        PsdBlendMode.ColorBurn => b >= 1 ? 1 : s <= 0 ? 0 : 1 - MathF.Min(1, (1 - b) / s),
        PsdBlendMode.LinearDodge => MathF.Min(1, b + s),
        PsdBlendMode.LinearBurn => MathF.Max(0, b + s - 1),
        PsdBlendMode.Overlay => HardLight(s, b),
        PsdBlendMode.HardLight => HardLight(b, s),
        PsdBlendMode.SoftLight => SoftLight(b, s),
        PsdBlendMode.Difference => MathF.Abs(b - s),
        PsdBlendMode.Exclusion => b + s - 2 * b * s,
        PsdBlendMode.Subtract => MathF.Max(0, b - s),
        PsdBlendMode.Divide => s <= 0 ? (b > 0 ? 1 : 0) : MathF.Min(1, b / s),
        PsdBlendMode.VividLight => s <= 0.5f
            ? (s <= 0 ? 0 : 1 - MathF.Min(1, (1 - b) / (2 * s)))
            : (s >= 1 ? 1 : MathF.Min(1, b / (2 * (1 - s)))),
        PsdBlendMode.LinearLight => Math.Clamp(b + 2 * s - 1, 0, 1),
        PsdBlendMode.PinLight => s <= 0.5f ? MathF.Min(b, 2 * s) : MathF.Max(b, 2 * s - 1),
        PsdBlendMode.HardMix => b + s >= 1 ? 1 : 0,
        _ => s,
    };

    static float HardLight(float b, float s) => s <= 0.5f ? b * 2 * s : b + (2 * s - 1) - b * (2 * s - 1);

    static float SoftLight(float b, float s)
    {
        if (s <= 0.5f) return b - (1 - 2 * s) * b * (1 - b);
        float d = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : MathF.Sqrt(b);
        return b + (2 * s - 1) * (d - b);
    }

    static float Lum(ReadOnlySpan<float> c) => 0.3f * c[0] + 0.59f * c[1] + 0.11f * c[2];
    static float Sat(ReadOnlySpan<float> c) => MathF.Max(c[0], MathF.Max(c[1], c[2])) - MathF.Min(c[0], MathF.Min(c[1], c[2]));

    static void SetLum(ReadOnlySpan<float> c, float l, Span<float> r)
    {
        float d = l - Lum(c);
        float r0 = c[0] + d, r1 = c[1] + d, r2 = c[2] + d;
        float lum = 0.3f * r0 + 0.59f * r1 + 0.11f * r2;
        float n = MathF.Min(r0, MathF.Min(r1, r2)), x = MathF.Max(r0, MathF.Max(r1, r2));
        if (n < 0) { r0 = lum + (r0 - lum) * lum / (lum - n); r1 = lum + (r1 - lum) * lum / (lum - n); r2 = lum + (r2 - lum) * lum / (lum - n); }
        if (x > 1) { r0 = lum + (r0 - lum) * (1 - lum) / (x - lum); r1 = lum + (r1 - lum) * (1 - lum) / (x - lum); r2 = lum + (r2 - lum) * (1 - lum) / (x - lum); }
        r[0] = r0; r[1] = r1; r[2] = r2;
    }

    static ReadOnlySpan<float> SetSat(ReadOnlySpan<float> c, float s, Span<float> r)
    {
        float max = MathF.Max(c[0], MathF.Max(c[1], c[2])), min = MathF.Min(c[0], MathF.Min(c[1], c[2]));
        for (int k = 0; k < 3; k++)
            r[k] = max > min ? (c[k] - min) * s / (max - min) : 0;
        return r;
    }
}
