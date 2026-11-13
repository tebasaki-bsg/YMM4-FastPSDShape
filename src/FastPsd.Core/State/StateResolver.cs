using FastPsd.Core.Psd;
using FastPsd.Core.Settings;

namespace FastPsd.Core.State;

// 高速PSD立ち絵 v0.1.9 と同じ内容。図形は口パク・目パチを使わない（Settings を渡さない）。

/// <summary>表情・ボイスアイテムの「口パク」</summary>
public enum MouthMode
{
    /// <summary>音量の口パクを使う（同じ口に母音口パクだけがあれば母音口パク）。既定。</summary>
    VolumePriority,
    /// <summary>母音口パクを使う（同じ口に音量の口パクだけがあれば音量の口パク）</summary>
    VowelPriority,
    AlwaysOpen,
    AlwaysClosed,
}

public sealed class RuleOptions
{
    /// <summary>「*」ルール：同じフォルダ内の「*」で始まる要素は1つだけ表示</summary>
    public bool Asterisk { get; init; } = true;
    /// <summary>「!」ルール：「!」で始まる要素は常に表示</summary>
    public bool Exclamation { get; init; } = true;
    /// <summary>「:flip」ルール：名前の末尾が「:flipx」「:flipy」「:flipxy」の要素は反転したときだけ使う（FlipMap）</summary>
    public bool Flip { get; init; } = true;
}

/// <summary>表示状態の計算に必要なもの（すべて「その時刻」の値だけで決まる）</summary>
public sealed class StateInput
{
    public required PsdFile Psd { get; init; }
    public RuleOptions Rules { get; init; } = new();
    public SettingSet? Settings { get; init; }
    /// <summary>
    /// 上書きの列（後の方が優先）。YMM4 では1つだけ渡す: レイヤーを変更した表情・ボイスアイテムがあればその一番上、なければ立ち絵アイテム。
    /// </summary>
    public IReadOnlyList<LayerOverrides> Overrides { get; init; } = [];
    public MouthMode Mouth { get; init; } = MouthMode.VolumePriority;
    /// <summary>声の大きさ（0〜1）。セリフがないときは負の値。</summary>
    public double VoiceVolume { get; init; } = -1;
    /// <summary>反転。「:flip」ルールがオンなら、表示中の要素を反転用に入れ替える（画像そのものの反転は描画側で行う）</summary>
    public FlipMode Flip { get; init; } = FlipMode.None;
    /// <summary>YMM4 から届いた母音（「ん」などは無音）</summary>
    public Vowel Vowel { get; init; } = Vowel.Silent;
    /// <summary>立ち絵アイテムの先頭からのフレーム数</summary>
    public long ItemFrame { get; init; }
    public int Fps { get; init; } = 60;
    /// <summary>目パチの乱数の種（立ち絵アイテムごと）</summary>
    public int BlinkSeed { get; init; }
    /// <summary>口パクの判定内容を VisibilityState.LipTrace に残す（調査用。通常は false）</summary>
    public bool TraceLip { get; init; }
}

/// <summary>口パク1件分の判定内容（調査用）</summary>
/// <param name="Active">口パクを動かしたか（対象がオンのときだけ動く）</param>
/// <param name="Open">開き具合の段階（0 = 閉、コマ数-1 = 全開）。動かさなかったときは -1</param>
/// <param name="ShownKey">表示したコマ</param>
/// <param name="VowelUsed">母音口パクなら、口を選ぶのに使った母音の欄（未設定の母音で無音の口を使ったなら Silent）。音量の口パクなら null。</param>
public sealed record LipTrace(string TargetKey, int FrameCount, bool Active, int Open, string? ShownKey, Vowel? VowelUsed = null)
{
    public bool IsVowel => Open == VowelOpenMarker;
    /// <summary>同じ口を扱う、優先するほうの口パクがあるので使わなかった</summary>
    public bool Yielded { get; init; }
    /// <summary>母音口パクの Open に入れる値（段階はない）</summary>
    public const int VowelOpenMarker = -2;
}

/// <summary>計算結果。Visible は PsdFile.All の Index ごとの「その要素自身の」表示フラグ。</summary>
public sealed class VisibilityState
{
    public required bool[] Visible { get; init; }
    /// <summary>親フォルダも含めて表示されるか</summary>
    public required bool[] EffectiveVisible { get; init; }
    /// <summary>実際に見えるかどうかの指紋。同じなら合成し直す必要はない。</summary>
    public required ulong Hash { get; init; }
    /// <summary>口パクの判定内容（StateInput.TraceLip のときだけ）</summary>
    public IReadOnlyList<LipTrace>? LipTrace { get; init; }
    /// <summary>反転用への入れ替えの前の Visible（入れ替えたときだけ。レイヤー欄で通常用の状態を見せるのに使う）</summary>
    public bool[]? BeforeFlip { get; init; }
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

        // 「:flip」ルール: 保存状態で反転用が表示されていたら、通常用に移す（PSDTool の doFlip(false) と同じ）
        var flipMap = input.Rules.Flip ? FlipMap.For(psd) : null;
        if (flipMap != null)
            foreach (var pair in flipMap.Pairs)
            {
                if (!vis[pair.Flipped.Index]) continue;
                FlipMap.CopyChildren(psd, vis, pair.Flipped, pair.Normal);
                vis[pair.Normal.Index] = true;
                vis[pair.Flipped.Index] = false;
            }

        // 2) 上書きを順に適用（後の方が優先）。反転用の要素は、通常用から決まるので上書きでは変えない
        foreach (var o in input.Overrides) ctx.Apply(o);
        if (flipMap != null)
            foreach (var i in flipMap.Flipped) vis[i] = false;

        // 3) 「!」ルール（反転用の要素は除く。PSDTool でも反転では非表示にされる）
        if (input.Rules.Exclamation)
            foreach (var l in all)
                if (l.Name.StartsWith('!') && !(flipMap?.Flipped.Contains(l.Index) ?? false)) vis[l.Index] = true;

        // 4) 口パク・目パチ（対象の差分が実際に表示されているときだけ）
        var s = input.Settings;
        var lipTrace = input.TraceLip ? new List<LipTrace>() : null;
        if (s != null)
        {
            // 動かす条件（口パク・母音口パク・目パチ共通）: 設定した差分（対象）がオンのときだけ。
            // コマとして使われている差分がオンかどうかは関係ない（標準のPSD立ち絵と同じ考え方）。
            // 判定は、アニメーションを当てる前の状態（PSDの保存状態＋アイテムの差分＋「!」ルール）で行う。
            var onBefore = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool On(string key)
            {
                if (!onBefore.TryGetValue(key, out var v)) onBefore[key] = v = ctx.IsEffectivelyVisible(key);
                return v;
            }
            foreach (var l in s.LipSyncs) On(l.TargetKey);
            foreach (var v in s.VowelLipSyncs) On(v.TargetKey);
            foreach (var b in s.Blinks) On(b.TargetKey);

            bool preferVowel = input.Mouth == MouthMode.VowelPriority;
            // 音量の口パクと母音口パクが同じ口を扱い（対象が同じ、または使う口が1つでも重なる）、両方の対象がオンのときは、どちらか一方だけを使う。
            // 母音優先なら母音口パク、それ以外（音量優先・常に開く・常に閉じる）は音量の口パク。片方の対象だけがオンなら、そちらを使う。
            var lips = s.LipSyncs.Where(l => l.Frames.Count >= 2).ToList();
            var vowels = s.VowelLipSyncs.Where(v => v.HasAny).Select(v => (Setting: v, Frames: VowelFrames(psd, v))).Where(v => v.Frames.Count > 0).ToList();
            // 譲ったほう → 優先するほうが使う口（譲ったほうの口のうち、これ以外は隠す。口が2つ出ないように）
            var yieldLips = new Dictionary<LipSyncSetting, HashSet<string>>();
            var yieldVowels = new Dictionary<VowelLipSyncSetting, HashSet<string>>();
            static void AddWinner<T>(Dictionary<T, HashSet<string>> d, T loser, string target, List<string> frames) where T : notnull
            {
                if (!d.TryGetValue(loser, out var keep)) d[loser] = keep = new HashSet<string>(StringComparer.Ordinal);
                keep.Add(target);
                keep.UnionWith(frames);
            }
            static bool Overlaps(string t1, List<string> f1, string t2, List<string> f2)
            {
                if (t1 == t2) return true;
                var a = new HashSet<string>(f1, StringComparer.Ordinal) { t1 };
                return a.Contains(t2) || f2.Any(a.Contains);
            }
            foreach (var l in lips)
                foreach (var v in vowels)
                {
                    if (!On(l.TargetKey) || !On(v.Setting.TargetKey)) continue;
                    if (!Overlaps(l.TargetKey, l.Frames, v.Setting.TargetKey, v.Frames)) continue;
                    if (preferVowel) AddWinner(yieldLips, l, v.Setting.TargetKey, v.Frames);
                    else AddWinner(yieldVowels, v.Setting, l.TargetKey, l.Frames);
                }

            foreach (var lip in lips)
            {
                if (yieldLips.TryGetValue(lip, out var lipKeep))
                {
                    ctx.HideOthers(lip.Frames, lipKeep);
                    lipTrace?.Add(new LipTrace(lip.TargetKey, lip.Frames.Count, false, -1, null) { Yielded = true });
                    continue;
                }
                if (!On(lip.TargetKey))
                {
                    lipTrace?.Add(new LipTrace(lip.TargetKey, lip.Frames.Count, false, -1, null));
                    continue;
                }
                int open = LipFrameIndex(lip.Frames.Count, input.Mouth, input.VoiceVolume, s.LipSensitivityPercent);
                ctx.ShowOnlyFrame(lip.Frames, lip.Frames.Count - 1 - open); // コマは開→閉の順
                lipTrace?.Add(new LipTrace(lip.TargetKey, lip.Frames.Count, true, open, lip.Frames[lip.Frames.Count - 1 - open]));
            }

            foreach (var (vowel, frames) in vowels)
            {
                if (yieldVowels.TryGetValue(vowel, out var vowelKeep))
                {
                    ctx.HideOthers(frames, vowelKeep);
                    lipTrace?.Add(new LipTrace(vowel.TargetKey, frames.Count, false, LipTrace.VowelOpenMarker, null) { Yielded = true });
                    continue;
                }
                if (!On(vowel.TargetKey))
                {
                    lipTrace?.Add(new LipTrace(vowel.TargetKey, frames.Count, false, LipTrace.VowelOpenMarker, null));
                    continue;
                }
                var (key, used) = VowelMouth(psd, vowel, input.Mouth, input.VoiceVolume, input.Vowel);
                if (key != null) ctx.ShowOnlyFrame(frames, frames.IndexOf(key));
                lipTrace?.Add(new LipTrace(vowel.TargetKey, frames.Count, true, LipTrace.VowelOpenMarker, key, used));
            }

            foreach (var blink in s.Blinks)
            {
                if (blink.Frames.Count < 2 || !On(blink.TargetKey)) continue;
                int idx = BlinkFrameIndex(blink, input.ItemFrame, input.Fps, input.BlinkSeed);
                ctx.ShowOnlyFrame(blink.Frames, idx);
            }
        }

        // 5) 反転: 表示中の通常用を反転用に入れ替える（口パク・目パチのあと。フォルダは中身のオン・オフも写す）
        bool[]? beforeFlip = null;
        if (flipMap != null && input.Flip != FlipMode.None)
        {
            beforeFlip = (bool[])vis.Clone();
            foreach (var pair in flipMap.Pairs)
            {
                if (!pair.UsedBy(input.Flip) || !vis[pair.Normal.Index]) continue;
                FlipMap.CopyChildren(psd, vis, pair.Normal, pair.Flipped);
                vis[pair.Flipped.Index] = true;
                vis[pair.Normal.Index] = false;
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
        return new VisibilityState { Visible = vis, EffectiveVisible = eff, Hash = hash, LipTrace = lipTrace, BeforeFlip = beforeFlip };
    }

    /// <summary>
    /// 口の開き具合の段階（0 = 閉、コマ数-1 = 全開）。YMM4標準のPSD立ち絵と同じ計算:
    ///   開き具合 = 声の大きさ × 10 × 感度(%) ÷ 100 を 0〜1 に切り詰め（セリフなしは 0）
    ///   開→閉に並べたコマの番号 = (int)(コマ数 × (1 - 開き具合)) を 0〜コマ数-1 に切り詰め
    /// </summary>
    public static int LipFrameIndex(int frameCount, MouthMode mode, double volume, double sensitivityPercent)
    {
        if (frameCount <= 0) return 0;
        double rate = mode switch
        {
            MouthMode.AlwaysClosed => 0,
            MouthMode.AlwaysOpen => 1,
            _ => volume < 0 ? 0 : Math.Clamp(volume * 10 * sensitivityPercent / 100, 0, 1),
        };
        int frameFromOpen = Math.Clamp((int)(frameCount * (1 - rate)), 0, frameCount - 1);
        return frameCount - 1 - frameFromOpen;
    }

    /// <summary>
    /// 母音口パクで切り替える口の一覧（重なりなし）。対象がレイヤーならそれも含める（「常に開く」などで出すため）。
    /// PSDにない口は除く。
    /// </summary>
    public static List<string> VowelFrames(PsdFile psd, VowelLipSyncSetting v)
    {
        var list = new List<string>();
        if (psd.ByKey.TryGetValue(v.TargetKey, out var t) && !t.IsGroup) list.Add(v.TargetKey);
        foreach (var slot in VowelLipSyncSetting.SlotOrder)
        {
            var k = v.Get(slot);
            if (k.Length > 0 && psd.ByKey.ContainsKey(k) && !list.Contains(k)) list.Add(k);
        }
        return list;
    }

    /// <summary>
    /// 母音口パクで出す口と、それを選んだ欄。null なら口を切り替えない（対象がフォルダで、使える口がないとき）。
    ///  ・常に開く: 対象のレイヤー（対象がフォルダなら「あ」の口）
    ///  ・常に閉じる・セリフの外・無音: 無音の口
    ///  ・あ〜お: その母音の口。未設定なら無音の口
    ///  ・無音の口も未設定なら対象のレイヤー（対象がフォルダなら切り替えない）
    /// </summary>
    public static (string? Key, Vowel? Used) VowelMouth(PsdFile psd, VowelLipSyncSetting v, MouthMode mode, double volume, Vowel vowel)
    {
        bool targetIsLayer = psd.ByKey.TryGetValue(v.TargetKey, out var t) && !t.IsGroup;
        string? Has(Vowel slot) { var k = v.Get(slot); return k.Length > 0 && psd.ByKey.ContainsKey(k) ? k : null; }
        (string?, Vowel?) Fallback()
        {
            if (Has(Vowel.Silent) is { } s) return (s, Vowel.Silent);
            return (targetIsLayer ? v.TargetKey : null, null);
        }

        if (mode == MouthMode.AlwaysOpen)
        {
            if (targetIsLayer) return (v.TargetKey, null);
            if (Has(Vowel.A) is { } a) return (a, Vowel.A);
            return Fallback();
        }
        if (mode == MouthMode.AlwaysClosed || volume < 0 || vowel == Vowel.Silent) return Fallback();
        if (Has(vowel) is { } k) return (k, vowel);
        return Fallback();
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

        public void Apply(LayerOverrides o)
        {
            foreach (var (key, v) in o.Visible)
                if (psd.ByKey.TryGetValue(key, out var l)) SetVisible(l, v);
            foreach (var (groupKey, childKey) in o.Select)
            {
                if (!psd.ByKey.TryGetValue(childKey, out var child)) continue;
                var parentKey = child.Parent?.Key ?? "";
                if (parentKey != groupKey) continue;
                Select(child);
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

        /// <summary>frames のうち keep にないものを隠す</summary>
        public void HideOthers(List<string> frames, HashSet<string> keep)
        {
            foreach (var f in frames)
                if (!keep.Contains(f) && psd.ByKey.TryGetValue(f, out var l)) vis[l.Index] = false;
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
