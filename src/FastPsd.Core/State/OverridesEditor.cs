using FastPsd.Core.Psd;
using FastPsd.Core.Settings;

namespace FastPsd.Core.State;

/// <summary>
/// レイヤーツリーでのクリックを、アイテムの差分（LayerOverrides）への変更に変換する。
/// 差分には「その操作」だけを記録し、PSDの状態や他のアイテムの差分には手を付けない。
/// </summary>
public static class OverridesEditor
{
    /// <summary>レイヤーのチェックを切り替えたときの新しい差分を返す（元の差分は変更しない）</summary>
    public static LayerOverrides SetVisible(LayerOverrides current, PsdFile psd, PsdLayer layer, bool visible, RuleOptions rules, ICollection<string> sliderGroups)
    {
        var o = current.Clone();
        string parentKey = layer.Parent?.Key ?? "";
        bool exclusive = sliderGroups.Contains(parentKey) || (rules.Asterisk && layer.Name.StartsWith('*'));
        if (exclusive && visible)
        {
            // 排他のグループでは「どれを選んだか」として1つの値で持つ（後から重ねた選択で丸ごと置き換わる）
            o.Select[parentKey] = layer.Key;
            o.Visible.Remove(layer.Key);
            foreach (var s in layer.Parent?.Children ?? psd.Roots)
                if (s != layer) o.Visible.Remove(s.Key);
        }
        else
        {
            o.Visible[layer.Key] = visible;
            if (!visible && o.Select.TryGetValue(parentKey, out var sel) && sel == layer.Key) o.Select.Remove(parentKey);
        }
        return o;
    }

    /// <summary>スライダーで番号を選んだときの新しい差分（0 = グループ内の一番上の要素）</summary>
    public static LayerOverrides SelectIndex(LayerOverrides current, PsdFile psd, string groupKey, int index)
    {
        var children = groupKey.Length == 0 ? psd.Roots : psd.ByKey.TryGetValue(groupKey, out var g) ? g.Children : null;
        if (children == null || children.Count == 0) return current;
        index = Math.Clamp(index, 0, children.Count - 1);
        var o = current.Clone();
        o.Select[groupKey] = children[index].Key;
        foreach (var c in children) o.Visible.Remove(c.Key);
        return o;
    }

    /// <summary>そのグループで今表示されている要素の番号（なければ -1）</summary>
    public static int SelectedIndex(PsdFile psd, VisibilityState state, string groupKey)
    {
        var children = groupKey.Length == 0 ? psd.Roots : psd.ByKey.TryGetValue(groupKey, out var g) ? g.Children : null;
        if (children == null) return -1;
        for (int i = 0; i < children.Count; i++)
            if (state.Visible[children[i].Index]) return i;
        return -1;
    }

    /// <summary>この差分が直接触っているレイヤーのキー（ツリーで太字にする用）</summary>
    public static HashSet<string> TouchedKeys(LayerOverrides o)
    {
        var set = new HashSet<string>(o.Visible.Keys, StringComparer.Ordinal);
        foreach (var v in o.Select.Values) set.Add(v);
        return set;
    }
}
