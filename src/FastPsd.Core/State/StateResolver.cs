using FastPsd.Core.Psd;
using FastPsd.Core.Settings;

namespace FastPsd.Core.State;

public enum MouthMode
{
    Normal,
    AlwaysOpen,
    AlwaysClosed,
}

public sealed class RuleOptions
{
    /// <summary>「*」ルール：同じフォルダ内の「*」で始まる要素は1つだけ表示</summary>
    public bool Asterisk { get; init; } = true;
    /// <summary>「!」ルール：「!」で始まる要素は常に表示</summary>
    public bool Exclamation { get; init; } = true;
}

/// <summary>表示状態の計算に必要なもの（すべて「その時刻」の値だけで決まる）</summary>
public sealed class StateInput
{
    public required PsdFile Psd { get; init; }
    public RuleOptions Rules { get; init; } = new();
    public SettingSet? Settings { get; init; }
    /// <summary>上書きの列。立ち絵アイテム → 表情・ボイスアイテム（レイヤー番号の小さい順）の順に並べる。</summary>
    public IReadOnlyList<LayerOverrides> Overrides { get; init; } = [];
    public MouthMode Mouth { get; init; } = MouthMode.Normal;
    /// <summary>声の大きさ（0〜1）。セリフがないときは負の値。</summary>
    public double VoiceVolume { get; init; } = -1;
    /// <summary>立ち絵アイテムの先頭からのフレーム数</summary>
    public long ItemFrame { get; init; }
    public int Fps { get; init; } = 60;
    /// <summary>目パチの乱数の種（立ち絵アイテムごと）</summary>
    public int BlinkSeed { get; init; }
}

/// <summary>計算結果。Visible は PsdFile.All の Index ごとの「その要素自身の」表示フラグ。</summary>
public sealed class VisibilityState
{
    public required bool[] Visible { get; init; }
    /// <summary>親フォルダも含めて表示されるか</summary>
    public required bool[] EffectiveVisible { get; init; }
    /// <summary>実際に見えるかどうかの指紋。同じなら合成し直す必要はない。</summary>
    public required ulong Hash { get; init; }
}

public static class StateResolver
{
    public static VisibilityState Resolve(StateInput input)
    {
        var psd = input.Psd;
        var all = psd.All;
        var vis = new bool[all.Count];
        for (int i = 0; i < all.Count; i++) vis[i] = !all[i].HiddenInFile;

        var sliderGroups = new HashSet<string>(input.Settings?.SliderGroups ?? [], StringComparer.Ordinal);
        var ctx = new Ctx(psd, vis, input.Rules, sliderGroups);

        // 1) PSDの保存状態を「*」ルールに合わせて正規化（同じフォルダで複数表示されていたら一番上だけ残す）
        if (input.Rules.Asterisk) ctx.NormalizeAsterisk();

        // 2) 上書きを順に適用（後の方が優先）
        foreach (var o in input.Overrides) ctx.Apply(o);

        // 3) 「!」ルール
        if (input.Rules.Exclamation)
            foreach (var l in all)
                if (l.Name.StartsWith('!')) vis[l.Index] = true;

        // 4) 口パク・目パチ（対象の差分が実際に表示されているときだけ）
        var s = input.Settings;
        if (s != null)
        {
            foreach (var lip in s.LipSyncs)
            {
                if (lip.Frames.Count < 2) continue;
                // 対象（フォルダ）が表示中、または対象がコマの1つで、いずれかのコマが表示中なら動かす
                // 動かす条件:
                //  ・対象（フォルダ／口パクを設定したパーツ）が表示中
                //  ・または、対象がコマの1つで、いずれかのコマが表示中（PSDの状態で閉じた口が出ている場合など）
                // ただし、アイテムで対象以外のコマを明示的に選んでいるときは、その選択を優先して口パクを止める
                // （例: 口Aに口パクを設定し、閉じのコマに口Bを使っている場合に、表情として口Bを選んだら口Bのまま）。
                // 「常に開く／常に閉じる」はアイテムでの明示的な指定なので、選択より優先する。
                bool explicitOther = input.Mouth == MouthMode.Normal &&
                    lip.Frames.Any(f => f != lip.TargetKey && ctx.IsExplicitlyShown(f) && ctx.IsEffectivelyVisible(f));
                bool active = !explicitOther &&
                    (ctx.IsEffectivelyVisible(lip.TargetKey) ||
                     (lip.Frames.Contains(lip.TargetKey) && lip.Frames.Any(ctx.IsEffectivelyVisible)));
                if (!active) continue;
                int open = LipFrameIndex(lip.Frames.Count, input.Mouth, input.VoiceVolume, s.LipSensitivity);
                ctx.ShowOnlyFrame(lip.Frames, lip.Frames.Count - 1 - open); // コマは開→閉の順
            }
            foreach (var blink in s.Blinks)
            {
                if (blink.Frames.Count < 2) continue;
                // 対象（フォルダ）が表示中、または対象がコマの1つで、開いた目（1枚目）が表示中なら動かす
                bool active = blink.Frames.Contains(blink.TargetKey)
                    ? ctx.IsEffectivelyVisible(blink.Frames[0])
                    : ctx.IsEffectivelyVisible(blink.TargetKey);
                if (!active) continue;
                int idx = BlinkFrameIndex(blink, input.ItemFrame, input.Fps, input.BlinkSeed);
                ctx.ShowOnlyFrame(blink.Frames, idx);
            }
        }

        var eff = new bool[all.Count];
        ulong hash = 1469598103934665603UL;
        foreach (var l in all)
        {
            bool e = vis[l.Index] && (l.Parent == null || eff[l.Parent.Index]);
            eff[l.Index] = e;
            if (e) hash = (hash ^ (ulong)(l.Index + 1)) * 1099511628211UL;
        }
        return new VisibilityState { Visible = vis, EffectiveVisible = eff, Hash = hash };
    }

    /// <summary>口の開き具合の段階。0〜感度の範囲をコマ数で均等に分ける。0 = 閉、コマ数-1 = 全開。</summary>
    public static int LipFrameIndex(int frameCount, MouthMode mode, double volume, double sensitivity)
    {
        if (mode == MouthMode.AlwaysClosed) return 0;
        if (mode == MouthMode.AlwaysOpen) return frameCount - 1;
        if (volume <= 0 || sensitivity <= 0) return 0;
        int idx = (int)Math.Floor(volume * frameCount / sensitivity);
        return Math.Clamp(idx, 0, frameCount - 1);
    }

    /// <summary>
    /// 目パチのコマ番号。0 = 通常（開）。まばたきの開始時刻は乱数の種と時刻だけから決まるので、
    /// どの順番でフレームが描画されても同じ結果になる。
    /// </summary>
    public static int BlinkFrameIndex(BlinkSetting b, long itemFrame, int fps, int seed)
    {
        int n = b.Frames.Count;
        int frameLen = Math.Max(1, b.FrameLength);
        long duration = (long)n * frameLen;
        double minSec = Math.Max(0.05, b.IntervalMinSeconds);
        double maxSec = b.RandomInterval ? Math.Max(minSec, b.IntervalMaxSeconds) : minSec;
        if (itemFrame < 0) return 0;

        long start = 0;
        for (long k = 0; ; k++)
        {
            double u = b.RandomInterval ? Random01(seed, k) : 0;
            long interval = Math.Max(duration + 1, (long)Math.Round((minSec + (maxSec - minSec) * u) * fps));
            start += interval;
            if (itemFrame < start) return 0;
            if (itemFrame < start + duration) return (int)((itemFrame - start) / frameLen);
        }
    }

    static double Random01(int seed, long k)
    {
        // SplitMix64
        ulong z = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)k * 0xBF58476D1CE4E5B9UL + 0x94D049BB133111EBUL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    sealed class Ctx(PsdFile psd, bool[] vis, RuleOptions rules, HashSet<string> sliderGroups)
    {
        public void NormalizeAsterisk()
        {
            void Walk(List<PsdLayer> siblings)
            {
                bool found = false;
                foreach (var l in siblings)
                {
                    if (l.Name.StartsWith('*'))
                    {
                        if (vis[l.Index])
                        {
                            if (found) vis[l.Index] = false;
                            found = true;
                        }
                    }
                    if (l.IsGroup) Walk(l.Children);
                }
            }
            Walk(psd.Roots);
        }

        /// <summary>アイテムの差分で明示的に表示した要素（口パクより選択を優先する判定に使う）</summary>
        readonly HashSet<int> _explicitShown = [];

        public bool IsExplicitlyShown(string key) => psd.ByKey.TryGetValue(key, out var l) && _explicitShown.Contains(l.Index);

        public void Apply(LayerOverrides o)
        {
            foreach (var (key, v) in o.Visible)
                if (psd.ByKey.TryGetValue(key, out var l)) { SetVisible(l, v); if (v) _explicitShown.Add(l.Index); else _explicitShown.Remove(l.Index); }
            foreach (var (groupKey, childKey) in o.Select)
            {
                if (!psd.ByKey.TryGetValue(childKey, out var child)) continue;
                var parentKey = child.Parent?.Key ?? "";
                if (parentKey != groupKey) continue;
                Select(child);
                _explicitShown.Add(child.Index);
            }
        }

        List<PsdLayer> Siblings(PsdLayer l) => l.Parent?.Children ?? psd.Roots;

        bool IsSliderParent(PsdLayer l) => sliderGroups.Contains(l.Parent?.Key ?? "");

        /// <summary>表示・非表示を設定する。表示にするとき、排他のグループなら兄弟を非表示にする。</summary>
        public void SetVisible(PsdLayer l, bool v)
        {
            vis[l.Index] = v;
            if (!v) return;
            if (IsSliderParent(l))
            {
                foreach (var s in Siblings(l)) if (s != l) vis[s.Index] = false;
            }
            else if (rules.Asterisk && l.Name.StartsWith('*'))
            {
                foreach (var s in Siblings(l)) if (s != l && s.Name.StartsWith('*')) vis[s.Index] = false;
            }
        }

        /// <summary>グループの中から1つを選ぶ（スライダー・「*」グループ）</summary>
        public void Select(PsdLayer child)
        {
            if (IsSliderParent(child))
                foreach (var s in Siblings(child)) vis[s.Index] = s == child;
            else
                SetVisible(child, true);
        }

        public bool IsEffectivelyVisible(string key)
        {
            if (!psd.ByKey.TryGetValue(key, out var l)) return false;
            for (var x = l; x != null; x = x.Parent)
                if (!vis[x.Index]) return false;
            return true;
        }

        public void ShowOnlyFrame(List<string> frames, int idx)
        {
            for (int i = 0; i < frames.Count; i++)
                if (psd.ByKey.TryGetValue(frames[i], out var l)) vis[l.Index] = false;
            if (idx >= 0 && idx < frames.Count && psd.ByKey.TryGetValue(frames[idx], out var sel))
                vis[sel.Index] = true;
        }
    }
}
