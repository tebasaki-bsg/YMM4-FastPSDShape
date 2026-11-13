using System.Runtime.CompilerServices;
using FastPsd.Core.Psd;

namespace FastPsd.Core.State;

/// <summary>反転（立ち絵アイテム・表情・ボイスアイテムの「反転」）</summary>
public enum FlipMode
{
    None,
    /// <summary>左右反転（「:flipx」を使う）</summary>
    X,
    /// <summary>上下反転（「:flipy」を使う）</summary>
    Y,
    /// <summary>上下左右反転（「:flipxy」だけを使う。:flipx・:flipy では代わりにしない）</summary>
    XY,
}

/// <summary>
/// 「:flip」ルール（PSDTool の反転レイヤー指定と同じ）。
/// 名前の末尾が「:flipx」「:flipy」「:flipxy」の要素は、反転したときだけ使う反転用。
/// 「体:flipx:flipy」のように並べて共用できる。相手（通常用）は、同じフォルダで末尾を除いた名前が同じ要素のうち、上から最初の1つ。
/// 相手がいなければ普通の要素として扱う。PSDTool の layertree.ts（parseToken・registerFlippingGroup・doFlip）に合わせた。
/// </summary>
public sealed class FlipMap
{
    public sealed record Pair(PsdLayer Normal, PsdLayer Flipped, bool X, bool Y, bool XY)
    {
        public bool UsedBy(FlipMode m) => m switch { FlipMode.X => X, FlipMode.Y => Y, FlipMode.XY => XY, _ => false };
    }

    /// <summary>深い要素から順（PSDTool と同じ。内側の入れ替えを済ませてから外側のフォルダを写す）</summary>
    public IReadOnlyList<Pair> Pairs { get; }

    /// <summary>相手のいる反転用の要素（PsdLayer.Index）</summary>
    public IReadOnlySet<int> Flipped { get; }

    static readonly ConditionalWeakTable<PsdFile, FlipMap> Cache = new();

    public static FlipMap For(PsdFile psd) => Cache.GetValue(psd, p => new FlipMap(p));

    FlipMap(PsdFile psd)
    {
        var pairs = new List<Pair>();
        void Walk(List<PsdLayer> siblings)
        {
            foreach (var l in siblings)
            {
                Walk(l.Children);
                var (name, x, y, xy) = ParseTokens(l.Name);
                if (!x && !y && !xy) continue;
                var normal = siblings.FirstOrDefault(s => s.Name == name);
                if (normal == null || normal == l) continue;
                pairs.Add(new Pair(normal, l, x, y, xy));
            }
        }
        Walk(psd.Roots);
        Pairs = pairs;
        Flipped = pairs.Select(p => p.Flipped.Index).ToHashSet();
    }

    /// <summary>名前の末尾の「:flipx」「:flipy」「:flipxy」を外した名前と、付いていた種類</summary>
    public static (string Name, bool X, bool Y, bool XY) ParseTokens(string name)
    {
        bool x = false, y = false, xy = false;
        var parts = name.Split(':').ToList();
        while (parts.Count > 1)
        {
            switch (parts[^1])
            {
                case "flipx": x = true; break;
                case "flipy": y = true; break;
                case "flipxy": xy = true; break;
                default: return (string.Join(':', parts), x, y, xy);
            }
            parts.RemoveAt(parts.Count - 1);
        }
        return (string.Join(':', parts), x, y, xy);
    }

    /// <summary>from の中身のオン・オフを、名前の対応（キーの後ろの部分）で to の中身に写す</summary>
    public static void CopyChildren(PsdFile psd, bool[] vis, PsdLayer from, PsdLayer to)
    {
        void Walk(PsdLayer t)
        {
            foreach (var c in t.Children)
            {
                var rel = c.Key[(to.Key.Length + 1)..];
                if (psd.ByKey.TryGetValue(from.Key + "/" + rel, out var src)) vis[c.Index] = vis[src.Index];
                Walk(c);
            }
        }
        Walk(to);
    }
}
