using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FastPsd.Core.Psd;

/// <summary>
/// 画素の並べ替えと事前乗算。8bit の R・G・B・A の面から、事前乗算済みの BGRA を作る。
/// SSE2 が使えるときは 16 画素ずつまとめて計算する（結果はスカラー版と完全に同じ）。
/// </summary>
public static class PixelOps
{
    /// <summary>(c * a + 127) / 255 を丸めまで含めて正確に計算する</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Mul(byte c, int a) => (byte)((c * a + 127) / 255);

    /// <param name="a">透明度の面。null なら不透明</param>
    public static void InterleavePremultiply(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, ReadOnlySpan<byte> a, bool hasAlpha, Span<byte> dst, int n)
    {
        int i = 0;
        if (Sse2.IsSupported && n >= 16)
            i = InterleaveSse2(r, g, b, a, hasAlpha, dst, n);
        for (; i < n; i++)
        {
            int aa = hasAlpha ? a[i] : 255;
            int o = i * 4;
            dst[o] = Mul(b[i], aa);
            dst[o + 1] = Mul(g[i], aa);
            dst[o + 2] = Mul(r[i], aa);
            dst[o + 3] = (byte)aa;
        }
    }

    static unsafe int InterleaveSse2(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, ReadOnlySpan<byte> a, bool hasAlpha, Span<byte> dst, int n)
    {
        int i = 0;
        var c127 = Vector128.Create((ushort)127);
        var c1 = Vector128.Create((ushort)1);
        var zero = Vector128<byte>.Zero;
        var opaque = Vector128.Create((byte)255);
        fixed (byte* pr = r, pg = g, pb = b, pa = a, pd = dst)
        {
            for (; i + 16 <= n; i += 16)
            {
                var vr = Sse2.LoadVector128(pr + i);
                var vg = Sse2.LoadVector128(pg + i);
                var vb = Sse2.LoadVector128(pb + i);
                var va = hasAlpha ? Sse2.LoadVector128(pa + i) : opaque;
                if (hasAlpha)
                {
                    var alo = Sse2.UnpackLow(va, zero).AsUInt16();
                    var ahi = Sse2.UnpackHigh(va, zero).AsUInt16();
                    vr = MulVec(vr, alo, ahi, zero, c127, c1);
                    vg = MulVec(vg, alo, ahi, zero, c127, c1);
                    vb = MulVec(vb, alo, ahi, zero, c127, c1);
                }
                // B G R A の順に並べる
                var bg_lo = Sse2.UnpackLow(vb, vg);   // b0 g0 b1 g1 ...
                var bg_hi = Sse2.UnpackHigh(vb, vg);
                var ra_lo = Sse2.UnpackLow(vr, va);   // r0 a0 r1 a1 ...
                var ra_hi = Sse2.UnpackHigh(vr, va);
                byte* o = pd + (long)i * 4;
                Sse2.Store(o, Sse2.UnpackLow(bg_lo.AsUInt16(), ra_lo.AsUInt16()).AsByte());
                Sse2.Store(o + 16, Sse2.UnpackHigh(bg_lo.AsUInt16(), ra_lo.AsUInt16()).AsByte());
                Sse2.Store(o + 32, Sse2.UnpackLow(bg_hi.AsUInt16(), ra_hi.AsUInt16()).AsByte());
                Sse2.Store(o + 48, Sse2.UnpackHigh(bg_hi.AsUInt16(), ra_hi.AsUInt16()).AsByte());
            }
        }
        return i;
    }

    /// <summary>16 個の c に a を掛けて 255 で割る（丸め付き）。t/255 = (t + 1 + (t &gt;&gt; 8)) &gt;&gt; 8（t &lt; 65535 で正確）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> MulVec(Vector128<byte> c, Vector128<ushort> alo, Vector128<ushort> ahi, Vector128<byte> zero, Vector128<ushort> c127, Vector128<ushort> c1)
    {
        var lo = Sse2.UnpackLow(c, zero).AsUInt16();
        var hi = Sse2.UnpackHigh(c, zero).AsUInt16();
        var tlo = Sse2.Add(Sse2.MultiplyLow(lo, alo), c127);
        var thi = Sse2.Add(Sse2.MultiplyLow(hi, ahi), c127);
        tlo = Sse2.ShiftRightLogical(Sse2.Add(Sse2.Add(tlo, c1), Sse2.ShiftRightLogical(tlo, 8)), 8);
        thi = Sse2.ShiftRightLogical(Sse2.Add(Sse2.Add(thi, c1), Sse2.ShiftRightLogical(thi, 8)), 8);
        return Sse2.PackUnsignedSaturate(tlo.AsInt16(), thi.AsInt16());
    }
}
