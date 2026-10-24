using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace FastPsd.Core.Psd;

public sealed class PsdChannel
{
    /// <summary>0,1,2… = 色、-1 = 透明度、-2 = レイヤーマスク、-3 = 実ユーザーマスク</summary>
    public short Id;
    /// <summary>圧縮方式(2バイト)を含むデータ長</summary>
    public long Length;
    /// <summary>ファイル内オフセット（圧縮方式の位置）。解析時にはファイルを読まず、長さの累積から計算する。</summary>
    public long DataOffset;
}

public sealed class PsdMask
{
    public int Top, Left, Bottom, Right;
    public byte DefaultColor;
    public byte Flags;
    public bool Disabled => (Flags & 0x02) != 0;
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

/// <summary>PSD のレイヤーまたはフォルダ。Children は上から下の順。</summary>
public sealed class PsdLayer
{
    // ---- ファイル上の情報 ----
    public int RecordIndex;
    public int Top, Left, Bottom, Right;
    public PsdChannel[] Channels = [];
    public string BlendKey = "norm";
    public byte OpacityByte = 255;
    public byte ClippingByte;
    public byte Flags;
    public string Name = "";
    public PsdMask? Mask;
    public bool NonDefaultBlendRanges;
    /// <summary>0=通常, 1=開いたフォルダ, 2=閉じたフォルダ, 3=フォルダ終端</summary>
    public int SectionType;
    public string? SectionBlendKey;
    public byte FillOpacityByte = 255;
    public bool Knockout;
    public List<string> Keys = [];
    public List<string> Adjustments = [];
    public List<string> Effects = [];

    // ---- 木構造 ----
    public PsdLayer? Parent;
    public List<PsdLayer> Children = [];
    public int Depth;
    /// <summary>上から順に振った通し番号（フォルダを含む）。表示状態の配列の添字に使う。</summary>
    public int Index;
    /// <summary>レイヤーの識別子（パス＋同名の順番）。JSONやアイテムの差分で使う。</summary>
    public string Key = "";

    public bool IsGroup => SectionType is 1 or 2;
    public bool HiddenInFile => (Flags & 0x02) != 0;
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public long Area => (long)Width * Height;
    public bool IsClipped => ClippingByte != 0;
    public float Opacity => OpacityByte / 255f;
    public float FillOpacity => FillOpacityByte / 255f;
    public bool HasPixels => !IsGroup && Area > 0 && Channels.Any(c => c.Id >= -1 && c.Length > 2);

    public PsdBlendMode BlendMode => PsdBlendModes.FromKey(IsGroup ? (SectionBlendKey ?? BlendKey) : BlendKey);

    public string Path
    {
        get
        {
            var names = new List<string>();
            for (var l = this; l != null; l = l.Parent) names.Add(l.Name);
            names.Reverse();
            return string.Join("/", names);
        }
    }

    public override string ToString() => Key;
}

/// <summary>
/// PSD ファイル。構造（レイヤーの一覧と木）だけを先に読み、画素は必要になったレイヤーだけを読む。
/// ファイルは開いたままにし、複数スレッドから同時に画素を読める（RandomAccess）。
/// </summary>
public sealed class PsdFile : IDisposable
{
    public string FilePath { get; }
    public long FileSize { get; private set; }
    public DateTime LastWriteTimeUtc { get; private set; }
    public int Version, ChannelCount, Width, Height, Depth, ColorMode;
    /// <summary>ファイル順（下→上）のレコード。フォルダ終端も含む。</summary>
    public List<PsdLayer> Records { get; } = [];
    /// <summary>最上位の要素（上→下）</summary>
    public List<PsdLayer> Roots { get; } = [];
    /// <summary>フォルダ終端を除く全要素（上→下、Index の順）</summary>
    public List<PsdLayer> All { get; } = [];
    public Dictionary<string, PsdLayer> ByKey { get; } = new(StringComparer.Ordinal);
    public long MergedImageOffset;
    public bool HasMergedAlpha;
    public List<string> Warnings { get; } = [];

    readonly SafeFileHandle _handle;

    PsdFile(string path, SafeFileHandle handle)
    {
        FilePath = path;
        _handle = handle;
    }

    public static PsdFile Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        var psd = new PsdFile(path, handle);
        try
        {
            var fi = new FileInfo(path);
            psd.FileSize = fi.Length;
            psd.LastWriteTimeUtc = fi.LastWriteTimeUtc;
            psd.Parse();
            psd.BuildTree();
            return psd;
        }
        catch
        {
            psd.Dispose();
            throw;
        }
    }

    /// <summary>ファイルが開いた時点から変更されたか（サイズか更新日時が違う）</summary>
    public bool IsStale()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            return !fi.Exists || fi.Length != FileSize || fi.LastWriteTimeUtc != LastWriteTimeUtc;
        }
        catch { return true; }
    }

    public string ColorModeName => ColorMode switch
    {
        0 => "モノクロ2階調", 1 => "グレースケール", 2 => "インデックス", 3 => "RGB",
        4 => "CMYK", 7 => "マルチチャンネル", 8 => "ダブルトーン", 9 => "Lab", _ => $"不明({ColorMode})"
    };

    public bool CanDecodeColor => ColorMode is 1 or 3 && Depth is 8 or 16;

    // ------------------------------------------------------------------
    // 構造の解析
    // ------------------------------------------------------------------

    void Parse()
    {
        // ヘッダーからレイヤーレコードまでは先頭付近にまとまっているので、まとめて読む
        using var fs = new BufferedStream(new RandomAccessStream(_handle), 1 << 16);
        using var r = new BeReader(fs, leaveOpen: true);
        if (r.Ascii4() != "8BPS") throw new InvalidDataException("PSDファイルではありません");
        Version = r.U16();
        if (Version == 2) throw new NotSupportedException(".psb（ラージドキュメント形式）には対応していません");
        if (Version != 1) throw new InvalidDataException($"不明なPSDバージョン {Version}");
        r.Skip(6);
        ChannelCount = r.U16();
        Height = r.I32();
        Width = r.I32();
        Depth = r.U16();
        ColorMode = r.U16();
        r.Skip(r.U32()); // カラーモードデータ
        r.Skip(r.U32()); // 画像リソース

        uint lmLen = r.U32();
        long lmEnd = r.Position + lmLen;
        if (lmLen > 0)
        {
            uint liLen = r.U32();
            long liStart = r.Position;
            if (liLen > 0) ReadLayerInfo(r);
            r.Position = liStart + liLen;

            if (r.Position + 4 <= lmEnd) r.Skip(r.U32()); // グローバルレイヤーマスク情報

            while (r.Position + 12 <= lmEnd)
            {
                string sig = r.Ascii4();
                if (sig != "8BIM" && sig != "8B64") break;
                string key = r.Ascii4();
                uint len = r.U32();
                long start = r.Position;
                if ((key == "Lr16" || key == "Lr32") && Records.Count == 0) ReadLayerInfo(r);
                r.Position = start + len;
                if (len % 4 != 0) r.Skip(4 - len % 4);
            }
        }
        MergedImageOffset = lmEnd;
    }

    void ReadLayerInfo(BeReader r)
    {
        int rawCount = r.I16();
        HasMergedAlpha = rawCount < 0;
        int count = Math.Abs(rawCount);
        var layers = new List<PsdLayer>(count);
        for (int i = 0; i < count; i++) layers.Add(ReadLayerRecord(r, i));

        // チャンネル画像データはレイヤー順・チャンネル順に連続している。
        // ここではファイルを読まず、長さの累積からオフセットだけを計算する（HDDでの大量の読み取りを避けるため）。
        long p = r.Position;
        foreach (var l in layers)
            foreach (var c in l.Channels)
            {
                c.DataOffset = p;
                p += c.Length;
            }
        r.Position = p;
        Records.AddRange(layers);
    }

    PsdLayer ReadLayerRecord(BeReader r, int index)
    {
        var l = new PsdLayer { RecordIndex = index, Top = r.I32(), Left = r.I32(), Bottom = r.I32(), Right = r.I32() };
        int nch = r.U16();
        l.Channels = new PsdChannel[nch];
        for (int i = 0; i < nch; i++) l.Channels[i] = new PsdChannel { Id = r.I16(), Length = r.U32() };
        if (r.Ascii4() != "8BIM") throw new InvalidDataException($"レイヤー{index}のブレンドモードが不正です");
        l.BlendKey = r.Ascii4();
        l.OpacityByte = r.U8();
        l.ClippingByte = r.U8();
        l.Flags = r.U8();
        r.U8();

        uint extraLen = r.U32();
        long extraEnd = r.Position + extraLen;

        uint maskLen = r.U32();
        long maskStart = r.Position;
        if (maskLen >= 18)
            l.Mask = new PsdMask { Top = r.I32(), Left = r.I32(), Bottom = r.I32(), Right = r.I32(), DefaultColor = r.U8(), Flags = r.U8() };
        r.Position = maskStart + maskLen;

        uint brLen = r.U32();
        if (brLen > 0)
        {
            var br = r.Bytes((int)brLen);
            for (int i = 0; i + 3 < br.Length; i += 4)
                if (br[i] != 0 || br[i + 1] != 0 || br[i + 2] != 255 || br[i + 3] != 255) { l.NonDefaultBlendRanges = true; break; }
        }

        l.Name = r.PascalString(4);

        while (r.Position + 12 <= extraEnd)
        {
            string sig = r.Ascii4();
            if (sig != "8BIM" && sig != "8B64") break;
            string key = r.Ascii4();
            uint len = r.U32();
            long start = r.Position;
            l.Keys.Add(key);
            try { ReadLayerTaggedBlock(r, l, key, len); }
            catch (Exception ex) { Warnings.Add($"「{l.Name}」の '{key}' を読めませんでした（{ex.Message}）"); }
            r.Position = start + len;
        }
        r.Position = extraEnd;
        return l;
    }

    public static readonly HashSet<string> AdjustmentKeys =
    [
        "levl", "curv", "brit", "blnc", "hue2", "hue ", "selc", "mixr", "grdm", "phfl",
        "expA", "vibA", "thrs", "nvrt", "post", "blwh", "clrL", "SoCo", "GdFl", "PtFl",
    ];

    static readonly string[] EffectKeys = ["DrSh", "IrSh", "OrGl", "IrGl", "ebbl", "ChFX", "SoFi", "GrFl", "patternFill", "FrFX"];

    static void ReadLayerTaggedBlock(BeReader r, PsdLayer l, string key, uint len)
    {
        switch (key)
        {
            case "luni":
                var name = r.UnicodeString();
                if (!string.IsNullOrEmpty(name)) l.Name = name;
                break;
            case "lsct":
            case "lsdk":
                l.SectionType = (int)r.U32();
                if (len >= 12) { r.Ascii4(); l.SectionBlendKey = r.Ascii4(); }
                break;
            case "iOpa":
                l.FillOpacityByte = r.U8();
                break;
            case "knko":
                l.Knockout = r.U8() != 0;
                break;
            case "lfx2":
            case "lmfx":
                r.U32(); r.U32();
                var d = Descriptor.Read(r);
                bool master = !d.TryGetValue("masterFXSwitch", out var m) || m is true;
                if (!master) break;
                foreach (var k in EffectKeys)
                    if (d.TryGetValue(k, out var v) && v is Dictionary<string, object?> fx &&
                        (!fx.TryGetValue("enab", out var e) || e is true) && !l.Effects.Contains(k))
                        l.Effects.Add(k);
                break;
            default:
                if (AdjustmentKeys.Contains(key) && !l.Adjustments.Contains(key)) l.Adjustments.Add(key);
                break;
        }
    }

    void BuildTree()
    {
        var stack = new Stack<PsdLayer>();
        for (int i = Records.Count - 1; i >= 0; i--)
        {
            var l = Records[i];
            if (l.SectionType == 3)
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }
            var parent = stack.Count > 0 ? stack.Peek() : null;
            l.Parent = parent;
            l.Depth = stack.Count;
            (parent?.Children ?? Roots).Add(l);
            if (l.IsGroup) stack.Push(l);
        }

        // 上から順に通し番号とキーを付ける
        void Walk(List<PsdLayer> siblings, string prefix)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var l in siblings)
            {
                l.Index = All.Count;
                All.Add(l);
                string seg = EscapeKeySegment(l.Name);
                int n = seen.TryGetValue(seg, out var c) ? c : 0;
                seen[seg] = n + 1;
                if (n > 0) seg += "#" + n;
                l.Key = prefix.Length == 0 ? seg : prefix + "/" + seg;
                ByKey[l.Key] = l;
                if (l.IsGroup) Walk(l.Children, l.Key);
            }
        }
        Walk(Roots, "");
    }

    static string EscapeKeySegment(string name) =>
        name.Replace("\\", "\\\\").Replace("/", "\\/").Replace("#", "\\#");

    // ------------------------------------------------------------------
    // 画素の読み込み
    // ------------------------------------------------------------------

    /// <summary>
    /// レイヤーを BGRA（乗算済みアルファ, 8bit）に展開する。有効なレイヤーマスクは透明度に焼き込む。
    /// 戻り値の配列は ArrayPool から借りたもので、長さは Width*Height*4 以上。使い終わったら ReturnBuffer で返す。
    /// </summary>
    public byte[] DecodeBgra(PsdLayer l)
    {
        if (!CanDecodeColor) throw new NotSupportedException($"{ColorModeName} {Depth}bit の画素の読み込みには対応していません");
        int bps = Depth == 16 ? 2 : 1;
        int w = l.Width, h = l.Height, n = w * h;
        var outBuf = ArrayPool<byte>.Shared.Rent(n * 4);
        var planes = new Dictionary<short, byte[]>();
        try
        {
            foreach (var c in l.Channels)
            {
                if (c.Id < -2 || c.Length <= 2) continue;
                if (c.Id == -2 && (l.Mask == null || l.Mask.Disabled || l.Mask.Width == 0)) continue;
                int cw = c.Id == -2 ? l.Mask!.Width : w;
                int ch = c.Id == -2 ? l.Mask!.Height : h;
                planes[c.Id] = ReadPlane(c, cw, ch, bps);
            }
            planes.TryGetValue(-1, out var a);
            if (ColorMode == 3 && bps == 1 && planes.TryGetValue(0, out var r8) && planes.TryGetValue(1, out var g8) && planes.TryGetValue(2, out var b8))
            {
                // よくある形（8bit RGB）は SIMD でまとめて計算する
                PixelOps.InterleavePremultiply(r8, g8, b8, a ?? r8, a != null, outBuf, n);
            }
            else if (ColorMode == 3)
            {
                planes.TryGetValue(0, out var rr);
                planes.TryGetValue(1, out var gg);
                planes.TryGetValue(2, out var bb);
                for (int i = 0, s = 0, o = 0; i < n; i++, s += bps, o += 4)
                {
                    int aa = a != null ? a[s] : 255;
                    outBuf[o] = Mul(bb != null ? bb[s] : (byte)0, aa);
                    outBuf[o + 1] = Mul(gg != null ? gg[s] : (byte)0, aa);
                    outBuf[o + 2] = Mul(rr != null ? rr[s] : (byte)0, aa);
                    outBuf[o + 3] = (byte)aa;
                }
            }
            else
            {
                planes.TryGetValue(0, out var k);
                for (int i = 0, s = 0, o = 0; i < n; i++, s += bps, o += 4)
                {
                    int aa = a != null ? a[s] : 255;
                    byte v = Mul(k != null ? k[s] : (byte)0, aa);
                    outBuf[o] = v; outBuf[o + 1] = v; outBuf[o + 2] = v; outBuf[o + 3] = (byte)aa;
                }
            }
            if (planes.TryGetValue(-2, out var mask)) BakeMask(outBuf, l, mask, bps);
            return outBuf;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(outBuf);
            throw;
        }
        finally
        {
            foreach (var p in planes.Values) ArrayPool<byte>.Shared.Return(p);
        }
    }

    public static void ReturnBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    byte[] ReadPlane(PsdChannel c, int w, int h, int bps)
    {
        int len = (int)c.Length;
        var raw = ArrayPool<byte>.Shared.Rent(len);
        var plane = ArrayPool<byte>.Shared.Rent(w * h * bps);
        try
        {
            int read = RandomAccess.Read(_handle, raw.AsSpan(0, len), c.DataOffset);
            if (read < len) throw new EndOfStreamException("チャンネルデータが途中で終わっています");
            ushort compression = (ushort)((raw[0] << 8) | raw[1]);
            ChannelDecoder.Decode(raw.AsSpan(2, len - 2), compression, w, h, bps, plane.AsSpan(0, w * h * bps));
            return plane;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(plane);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    static void BakeMask(byte[] bgra, PsdLayer l, byte[] mask, int bps)
    {
        var m = l.Mask!;
        int w = l.Width, h = l.Height, mw = m.Width;
        for (int y = 0; y < h; y++)
        {
            int py = l.Top + y;
            for (int x = 0; x < w; x++)
            {
                int px = l.Left + x;
                int mv = (px >= m.Left && px < m.Right && py >= m.Top && py < m.Bottom)
                    ? mask[((py - m.Top) * mw + (px - m.Left)) * bps]
                    : m.DefaultColor;
                if (mv == 255) continue;
                int o = (y * w + x) * 4;
                bgra[o] = Mul(bgra[o], mv);
                bgra[o + 1] = Mul(bgra[o + 1], mv);
                bgra[o + 2] = Mul(bgra[o + 2], mv);
                bgra[o + 3] = Mul(bgra[o + 3], mv);
            }
        }
    }

    static byte Mul(byte c, int a) => (byte)((c * a + 127) / 255);

    /// <summary>PSD に保存された統合画像（Photoshop の合成結果）を、非乗算の RGBA で読む。テスト用。</summary>
    public byte[] DecodeMergedRgba()
    {
        if (!CanDecodeColor) throw new NotSupportedException();
        int bps = Depth == 16 ? 2 : 1;
        long fileLen = RandomAccess.GetLength(_handle);
        int len = (int)(fileLen - MergedImageOffset);
        if (len < 2) throw new InvalidDataException("統合画像がありません");
        var raw = new byte[len];
        RandomAccess.Read(_handle, raw, MergedImageOffset);
        ushort comp = (ushort)((raw[0] << 8) | raw[1]);
        int n = Width * Height, plane = n * bps;
        int colorCh = ColorMode == 3 ? 3 : 1;
        int chCount = Math.Min(ChannelCount, colorCh + 1);
        var planes = new byte[chCount][];
        int pos = 2;
        if (comp == 1 && len < 2 + ChannelCount * Height * 2) throw new InvalidDataException("統合画像がありません");
        if (comp == 0 && len < 2 + (long)plane * chCount) throw new InvalidDataException("統合画像がありません");
        if (comp == 1)
        {
            int rowsTotal = ChannelCount * Height;
            int dataPos = 2 + rowsTotal * 2;
            for (int c = 0; c < chCount; c++)
            {
                planes[c] = new byte[plane];
                for (int y = 0; y < Height; y++)
                {
                    int ri = c * Height + y;
                    int rl = (raw[2 + ri * 2] << 8) | raw[2 + ri * 2 + 1];
                    var row = planes[c].AsSpan(y * Width * bps, Width * bps);
                    PackBitsRow(raw.AsSpan(dataPos, rl), row);
                    dataPos += rl;
                }
            }
        }
        else if (comp == 0)
        {
            for (int c = 0; c < chCount; c++)
            {
                planes[c] = raw.AsSpan(pos, plane).ToArray();
                pos += plane;
            }
        }
        else throw new NotSupportedException($"統合画像の圧縮方式 {comp} には未対応");

        var rgba = new byte[n * 4];
        for (int i = 0, s = 0; i < n; i++, s += bps)
        {
            byte v0 = planes[0][s];
            rgba[i * 4] = v0;
            rgba[i * 4 + 1] = colorCh == 3 ? planes[1][s] : v0;
            rgba[i * 4 + 2] = colorCh == 3 ? planes[2][s] : v0;
            rgba[i * 4 + 3] = chCount > colorCh ? planes[colorCh][s] : (byte)255;
        }
        return rgba;
    }

    static void PackBitsRow(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0, o = 0;
        while (i < src.Length && o < dst.Length)
        {
            int n = (sbyte)src[i++];
            if (n >= 0)
            {
                int cnt = Math.Min(n + 1, Math.Min(src.Length - i, dst.Length - o));
                src.Slice(i, cnt).CopyTo(dst.Slice(o, cnt));
                i += n + 1; o += cnt;
            }
            else if (n != -128)
            {
                if (i >= src.Length) break;
                int cnt = Math.Min(1 - n, dst.Length - o);
                dst.Slice(o, cnt).Fill(src[i++]);
                o += cnt;
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
