using System.Diagnostics;
using FastPsd.Core.Compose;
using FastPsd.Core.Psd;
using FastPsd.Core.Settings;
using FastPsd.Core.State;

// 使い方: FastPsd.Core.Tests <rules.psd> [合成テスト用PSDのフォルダ] [速度確認用PSD]
if (args.Length >= 3 && args[0] == "--render")
{
    // デバッグ用: PSD を CPU 版で合成して PPM（白背景）に書き出す
    using var rp = PsdFile.Open(args[1]);
    var rs = StateResolver.Resolve(new StateInput { Psd = rp, Rules = new RuleOptions { Asterisk = false, Exclamation = false } });
    var rplan = RenderPlanner.Build(rp, rs);
    void Dump(List<RenderOp> ops, string ind)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case LayerOp l: Console.WriteLine($"{ind}Layer {l.Layer.Key} op={l.Opacity:F2} {l.Blend}"); break;
                case GroupOp g: Console.WriteLine($"{ind}Group {g.Group?.Key} op={g.Opacity:F2} {g.Blend}"); Dump(g.Children, ind + "  "); break;
                case ClipOp c: Console.WriteLine($"{ind}Clip op={c.Opacity:F2} {c.Blend}"); Dump([c.Base], ind + "  base "); Dump(c.Clipped, ind + "  clip "); break;
            }
        }
    }
    Dump(rplan.Ops, "");
    var img = new CpuCompositor(rp).Render(rplan);
    using var o = File.Create(args[2]);
    var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{rp.Width} {rp.Height}\n255\n");
    o.Write(header);
    for (int i = 0; i < rp.Width * rp.Height; i++)
    {
        float a = img[i * 4 + 3];
        for (int k = 0; k < 3; k++) o.WriteByte((byte)Math.Clamp(MathF.Round((img[i * 4 + k] + 1 - a) * 255), 0, 255));
    }
    return 0;
}

int failures = 0;
void Check(bool cond, string name)
{
    if (cond) Console.WriteLine($"  OK   {name}");
    else { Console.WriteLine($"  FAIL {name}"); failures++; }
}

string rulesPath = args.Length > 0 ? args[0] : "rules.psd";
using (var psd = PsdFile.Open(rulesPath))
{
    Console.WriteLine("[キー]");
    Check(psd.ByKey.ContainsKey("!体/体"), "フォルダの中のキー");
    Check(psd.ByKey.ContainsKey("口/*口A/閉"), "入れ子のキー");
    Check(psd.ByKey.ContainsKey("重複") && psd.ByKey.ContainsKey("重複#1"), "同名レイヤーは #1 で区別");
    Check(psd.ByKey["重複"].Index < psd.ByKey["重複#1"].Index, "同名は上から #なし, #1");

    bool V(VisibilityState s, string key) => s.EffectiveVisible[psd.ByKey[key].Index];
    VisibilityState R(RuleOptions? rules = null, SettingSet? set = null, IReadOnlyList<LayerOverrides>? ov = null,
                      double vol = -1, MouthMode mouth = MouthMode.Normal) =>
        StateResolver.Resolve(new StateInput
        {
            Psd = psd, Rules = rules ?? new RuleOptions(), Settings = set, Overrides = ov ?? [],
            VoiceVolume = vol, Mouth = mouth, Fps = 30,
        });

    Console.WriteLine("[ルール]");
    var s0 = R();
    Check(V(s0, "目/*通常目") && !V(s0, "目/*閉じ目") && !V(s0, "目/*ジト目"), "「*」: 一番上の表示中だけ残る");
    Check(V(s0, "!体") && V(s0, "!体/体"), "「!」: 非表示のフォルダも表示");
    Check(V(s0, "!輪郭"), "「!」: 非表示のレイヤーも表示");
    var sOff = R(new RuleOptions { Asterisk = false, Exclamation = false });
    Check(V(sOff, "目/*通常目") && V(sOff, "目/*閉じ目"), "「*」オフ: 両方表示のまま");
    Check(!V(sOff, "!体") && !V(sOff, "!輪郭"), "「!」オフ: 非表示のまま");

    Console.WriteLine("[上書き]");
    var o1 = new LayerOverrides(); o1.Visible["目/*ジト目"] = true;
    var s1 = R(ov: [o1]);
    Check(V(s1, "目/*ジト目") && !V(s1, "目/*通常目") && !V(s1, "目/*閉じ目"), "「*」要素を表示すると兄弟が消える");
    var o2 = new LayerOverrides(); o2.Select["口"] = "口/*口B";
    var s2 = R(ov: [o2]);
    Check(V(s2, "口/*口B") && !V(s2, "口/*口A"), "選択（Select）");
    var o3 = new LayerOverrides(); o3.Visible["目/*閉じ目"] = true;
    var s3 = R(ov: [o1, o3]);
    Check(V(s3, "目/*閉じ目") && !V(s3, "目/*ジト目"), "後の上書きが勝つ");
    var o4 = new LayerOverrides(); o4.Visible["!輪郭"] = false;
    Check(V(R(ov: [o4]), "!輪郭"), "「!」は上書きで消せない");
    var json = o1.ToJson();
    Check(LayerOverrides.FromJson(json).Visible["目/*ジト目"], "差分のJSON往復");

    Console.WriteLine("[スライダー]");
    var slider = new SettingSet { SliderGroups = ["腕"] };
    var o5 = new LayerOverrides(); o5.Select["腕"] = "腕/腕2";
    var s5 = R(set: slider, ov: [o5]);
    Check(V(s5, "腕/腕2") && !V(s5, "腕/腕0") && !V(s5, "腕/腕1"), "スライダーは選んだ1つだけ表示");
    var s5b = R(set: slider);
    Check(V(s5b, "腕/腕0") && V(s5b, "腕/腕1"), "選択がなければPSDの状態のまま");

    Console.WriteLine("[口パク]");
    var lip = new SettingSet { LipSensitivity = 0.2, LipSyncs = [new LipSyncSetting { TargetKey = "口/*口A", Frames = ["口/*口A/開", "口/*口A/半", "口/*口A/閉"] }] };
    Check(V(R(set: lip, vol: -1), "口/*口A/閉"), "セリフなし → 閉");
    Check(V(R(set: lip, vol: 0.05), "口/*口A/閉"), "0.05 → 閉（0.067未満）");
    Check(V(R(set: lip, vol: 0.1), "口/*口A/半") && !V(R(set: lip, vol: 0.1), "口/*口A/閉"), "0.1 → 半");
    Check(V(R(set: lip, vol: 0.15), "口/*口A/開"), "0.15 → 開");
    Check(V(R(set: lip, vol: 0.9), "口/*口A/開"), "感度以上 → 開");
    Check(V(R(set: lip, vol: 0.15, mouth: MouthMode.AlwaysClosed), "口/*口A/閉"), "常に閉じる");
    Check(V(R(set: lip, vol: -1, mouth: MouthMode.AlwaysOpen), "口/*口A/開"), "常に開く");
    var sLipB = R(set: lip, ov: [o2], vol: 0.15);
    Check(!V(sLipB, "口/*口A/開") && V(sLipB, "口/*口B"), "対象の差分が非表示なら動かない");

    // 対象をフォルダではなく「開いた口」のレイヤーにした場合（ユーザーが右クリックしたのが *開く などの場合）
    var lipOnLayer = new SettingSet { LipSensitivity = 0.2, LipSyncs = [new LipSyncSetting { TargetKey = "口/*口A/開", Frames = ["口/*口A/開", "口/*口A/半", "口/*口A/閉"] }] };
    Check(V(R(set: lipOnLayer, vol: 0.15), "口/*口A/開"), "対象がコマの1つ: 閉じた口が表示中でも動く");
    Check(V(R(set: lipOnLayer, vol: -1), "口/*口A/閉"), "対象がコマの1つ: セリフなし → 閉");
    var blinkOnLayer = new SettingSet { Blinks = [new BlinkSetting { TargetKey = "目/*通常目", Frames = ["目/*通常目", "目/*ジト目", "目/*閉じ目"], RandomInterval = false, IntervalMinSeconds = 1, FrameLength = 1 }] };
    var stB = StateResolver.Resolve(new StateInput { Psd = psd, Settings = blinkOnLayer, Fps = 30, ItemFrame = 32 });
    Check(stB.EffectiveVisible[psd.ByKey["目/*閉じ目"].Index], "目パチ: 対象がコマの1つ（開いた目が表示中）なら動く");
    var stB2 = StateResolver.Resolve(new StateInput { Psd = psd, Settings = blinkOnLayer, Fps = 30, ItemFrame = 32, Overrides = [o1] });
    Check(stB2.EffectiveVisible[psd.ByKey["目/*ジト目"].Index] && !stB2.EffectiveVisible[psd.ByKey["目/*閉じ目"].Index], "目パチ: 開いた目以外を選んでいると動かない");

    // 平らに並んだ口（口A＝開、口B＝閉）で、口Aに口パクを設定した場合。ここでは 目/*通常目＝口A、目/*閉じ目＝口B に見立てる
    var flat = new SettingSet { LipSensitivity = 0.2, LipSyncs = [new LipSyncSetting { TargetKey = "目/*通常目", Frames = ["目/*通常目", "目/*閉じ目"] }] };
    var selB = new LayerOverrides(); selB.Select["目"] = "目/*閉じ目";
    var selA = new LayerOverrides(); selA.Select["目"] = "目/*通常目";
    Check(V(R(set: flat, vol: 0.15), "目/*通常目"), "口A/口B: 選択なし・声あり → 口A（開）");
    Check(V(R(set: flat, vol: -1), "目/*閉じ目"), "口A/口B: 選択なし・声なし → 口B（閉）");
    Check(V(R(set: flat, ov: [selB], vol: 0.15), "目/*閉じ目") && !V(R(set: flat, ov: [selB], vol: 0.15), "目/*通常目"), "口Bを明示的に選ぶ → 声があっても口Bのまま");
    Check(V(R(set: flat, ov: [selA], vol: -1), "目/*閉じ目"), "口Aを選ぶ → 口パクは動く");
    Check(V(R(set: flat, ov: [selB, selA], vol: 0.15), "目/*通常目"), "立ち絵で口B、表情で口A → 表情が優先され口パクが動く");
    Check(V(R(set: flat, ov: [selB], vol: -1, mouth: MouthMode.AlwaysOpen), "目/*通常目"), "口Bを選んでいても「常に開く」は優先");

    Console.WriteLine("[目パチ]");
    var fixedBlink = new BlinkSetting { Frames = ["a", "b", "c"], FrameLength = 2, RandomInterval = false, IntervalMinSeconds = 1 };
    int[] expect = [0, 0, 0, 1, 1, 2, 2, 0];
    long[] frames = [0, 29, 30, 32, 33, 34, 35, 36];
    bool okFixed = true;
    for (int i = 0; i < frames.Length; i++)
        okFixed &= StateResolver.BlinkFrameIndex(fixedBlink, frames[i], 30, 0) == expect[i];
    Check(okFixed, "固定1秒・1コマ2フレーム: 30フレーム目から 開→半→閉");
    var rnd = new BlinkSetting { Frames = ["a", "b", "c"], FrameLength = 1 };
    var starts = new List<long>();
    for (long f = 0; f < 30 * 600; f++)
        if (StateResolver.BlinkFrameIndex(rnd, f, 30, 42) == 0 && StateResolver.BlinkFrameIndex(rnd, f + 1, 30, 42) != 0)
            starts.Add(f + 1);
    var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
    Check(gaps.Count > 250 && gaps.All(g => g >= 30 && g <= 60), $"ランダム1〜2秒: 間隔が30〜60フレーム（{gaps.Count}回, {gaps.Min()}〜{gaps.Max()}）");
    bool same = Enumerable.Range(0, 3000).All(f => StateResolver.BlinkFrameIndex(rnd, f, 30, 42) == StateResolver.BlinkFrameIndex(rnd, f, 30, 42));
    bool differs = Enumerable.Range(0, 3000).Any(f => StateResolver.BlinkFrameIndex(rnd, f, 30, 42) != StateResolver.BlinkFrameIndex(rnd, f, 30, 7));
    Check(same && differs, "同じ種なら同じ、種が違えばずれる");

    Console.WriteLine("[ツリーでの編集]");
    var none = new HashSet<string>();
    var e1 = OverridesEditor.SetVisible(new LayerOverrides(), psd, psd.ByKey["目/*ジト目"], true, new RuleOptions(), none);
    Check(e1.Select.TryGetValue("目", out var sel1) && sel1 == "目/*ジト目" && e1.Visible.Count == 0, "「*」要素のチェックは選択として記録");
    var s1e = R(ov: [e1]);
    Check(V(s1e, "目/*ジト目") && !V(s1e, "目/*通常目"), "選択が反映される");
    var e2 = OverridesEditor.SetVisible(e1, psd, psd.ByKey["目/*ジト目"], false, new RuleOptions(), none);
    Check(!e2.Select.ContainsKey("目") && e2.Visible["目/*ジト目"] == false, "外すと選択が消えて非表示が記録される");
    var e3 = OverridesEditor.SetVisible(new LayerOverrides(), psd, psd.ByKey["重複"], false, new RuleOptions(), none);
    Check(!V(R(ov: [e3]), "重複") && V(R(ov: [e3]), "重複#1"), "通常レイヤーの非表示");
    var e4 = OverridesEditor.SelectIndex(new LayerOverrides(), psd, "腕", 2);
    var s4 = R(set: slider, ov: [e4]);
    Check(e4.Select["腕"] == "腕/腕2" && V(s4, "腕/腕2") && !V(s4, "腕/腕0"), "スライダー2 = 上から3番目");
    Check(OverridesEditor.SelectedIndex(psd, R(set: slider, ov: [OverridesEditor.SelectIndex(new LayerOverrides(), psd, "腕", 1)]), "腕") == 1, "スライダーの番号の往復");

    Console.WriteLine("[サムネイル]");
    var big = psd.All.First(l => l.HasPixels);
    var px = psd.DecodeBgra(big);
    var th = FastPsd.Core.Thumbnails.ThumbnailCache.Downscale(px, big.Width, big.Height, 4);
    PsdFile.ReturnBuffer(px);
    Check(th.Width == 4 && th.Height == 4 && th.Bgra[3] == 255, "縮小（8x8 → 4x4、不透明のまま）");
    var thumbDir = Path.Combine(Path.GetTempPath(), "fastpsd-thumbs-" + Guid.NewGuid().ToString("N"));
    FastPsd.Core.Thumbnails.ThumbnailCache.Directory = thumbDir;
    try
    {
        var key = FastPsd.Core.Thumbnails.ThumbnailCache.ContentKey(rulesPath);
        var built = FastPsd.Core.Thumbnails.ThumbnailCache.Build(psd, 2, null, CancellationToken.None);
        FastPsd.Core.Thumbnails.ThumbnailCache.Save(key, built);
        var loaded = FastPsd.Core.Thumbnails.ThumbnailCache.TryLoad(key);
        Check(loaded != null && loaded.Count == built.Count && loaded.All(kv => kv.Value.Bgra.SequenceEqual(built[kv.Key].Bgra)), $"キャッシュの保存と読み込み（{built.Count}枚）");
        Check(FastPsd.Core.Thumbnails.ThumbnailCache.Clear() > 0 && FastPsd.Core.Thumbnails.ThumbnailCache.TryLoad(key) == null, "キャッシュの削除");
    }
    finally { try { Directory.Delete(thumbDir, true); } catch { } }

    Console.WriteLine("[図形: プリセットの取り込み]");
    // 立ち絵でスライダー化したフォルダの選択は、図形では「選んだ要素だけ表示」に置き換えて同じ見た目にする
    var tp = new Preset { Name = "腕2と口B" };
    tp.Overrides.Select["腕"] = "腕/腕2";
    tp.Overrides.Select["口"] = "口/*口B";
    tp.Overrides.Visible["重複"] = false;
    var conv = TachiePresetImport.Convert(tp, psd, ["腕"]);
    var tachieState = R(set: slider, ov: [tp.Overrides]);
    var shapeState = R(ov: [conv.Overrides]);
    Check(tachieState.EffectiveVisible.SequenceEqual(shapeState.EffectiveVisible), "変換後の見た目が立ち絵と同じ");
    Check(!conv.Overrides.Select.ContainsKey("腕") && conv.Overrides.Visible["腕/腕2"] && !conv.Overrides.Visible["腕/腕0"], "スライダーの選択は表示・非表示に置き換え");
    Check(conv.Overrides.Select["口"] == "口/*口B", "「*」グループの選択はそのまま");
    Check(!R(ov: [tp.Overrides]).EffectiveVisible.SequenceEqual(tachieState.EffectiveVisible), "（参考）変換しないと見た目が変わる");
    Check(tp.Overrides.Select.ContainsKey("腕"), "元のプリセットは変更しない");

    var shapeFile = new ShapePresetFile();
    shapeFile.Presets.Add(new Preset { Name = "笑顔" });
    var added = TachiePresetImport.Merge(shapeFile, [new Preset { Name = "笑顔" }, new Preset { Name = "怒り" }], overwrite: false);
    Check(added.SequenceEqual(["笑顔 (2)", "怒り"]) && shapeFile.Presets.Count == 3, "同じ名前は番号を付けて追加");
    var o9 = new LayerOverrides(); o9.Visible["重複"] = false;
    var replaced = TachiePresetImport.Merge(shapeFile, [new Preset { Name = "笑顔", Overrides = o9 }], overwrite: true);
    Check(replaced.SequenceEqual(["笑顔"]) && shapeFile.Presets.Count == 3 && shapeFile.Presets[0].Overrides.Visible.ContainsKey("重複"), "上書きを選ぶと置き換え");

    var tmpShape = Path.Combine(Path.GetTempPath(), $"fastpsdshape-test-{Guid.NewGuid():N}.psd");
    try
    {
        Check(ShapePresetFile.PathFor(tmpShape).EndsWith("-ymmfastpsdshape.json"), "図形の設定ファイル名");
        ShapePresetStore.Update(tmpShape, f => f.Presets.Add(new Preset { Name = "A", Overrides = o9 }));
        Check(ShapePresetFile.Load(tmpShape).Presets is [{ Name: "A" }] && ShapePresetStore.Get(tmpShape).Presets.Count == 1, "図形のプリセットの保存と読み込み");
        File.WriteAllText(SettingsFile.PathFor(tmpShape), """{"Version":2,"SettingSets":[{"Id":"x","Name":"キャラA","SliderGroups":["腕"],"Presets":[{"Name":"p","Overrides":{"Visible":{},"Select":{"腕":"腕/腕1"}}}]}]}""");
        var tf = TachiePresetImport.ReadTachieFile(tmpShape);
        Check(tf != null && tf.SettingSets[0].Presets[0].Overrides.Select["腕"] == "腕/腕1", "立ち絵の設定ファイルを読む");
        Check(TachiePresetImport.ReadTachieFile(tmpShape + "x.psd") == null, "立ち絵の設定ファイルがなければ null");
    }
    finally
    {
        try { File.Delete(ShapePresetFile.PathFor(tmpShape)); } catch { }
        try { File.Delete(SettingsFile.PathFor(tmpShape)); } catch { }
    }

    Console.WriteLine("[図形: 合成の範囲]");
    var bplan = RenderPlanner.Build(psd, R());
    var bb = bplan.ContentBounds(psd.Width, psd.Height);
    int el = bplan.Layers.Min(l => l.Left), er = bplan.Layers.Max(l => l.Right);
    Check(bb.Left == el && bb.Width == er - el && bb.Height > 0, $"表示中のレイヤーを囲む範囲（{bb.Left},{bb.Top} {bb.Width}x{bb.Height}）");
    Check(new RenderPlan().ContentBounds(64, 64).Width == 0, "描くものがなければ大きさ0");
    var outside = new RenderPlan(); outside.Layers.Add(new PsdLayer { Left = -10, Top = -5, Right = 100, Bottom = 30 });
    Check(outside.ContentBounds(64, 64) == (0, 0, 64, 30), "キャンバスの外は切り取る");

    Console.WriteLine("[図形: 展開の SIMD]");
    {
        int n = 256 * 256 + 7;
        var rr = new byte[n]; var gg = new byte[n]; var bb2 = new byte[n]; var aa = new byte[n];
        var rnd2 = new Random(1);
        for (int i = 0; i < n; i++) { rr[i] = (byte)(i & 255); aa[i] = (byte)((i >> 8) & 255); gg[i] = (byte)rnd2.Next(256); bb2[i] = (byte)rnd2.Next(256); }
        foreach (bool hasA in new[] { true, false })
        {
            var d = new byte[n * 4];
            PixelOps.InterleavePremultiply(rr, gg, bb2, aa, hasA, d, n);
            bool okAll = true;
            for (int i = 0; i < n && okAll; i++)
            {
                int a2 = hasA ? aa[i] : 255;
                okAll = d[i * 4] == (byte)((bb2[i] * a2 + 127) / 255) && d[i * 4 + 1] == (byte)((gg[i] * a2 + 127) / 255)
                     && d[i * 4 + 2] == (byte)((rr[i] * a2 + 127) / 255) && d[i * 4 + 3] == a2;
            }
            Check(okAll, $"全ての色×透明度の組で従来の計算と一致（透明度{(hasA ? "あり" : "なし")}）");
        }
    }

    Console.WriteLine("[設定ファイル]");
    var tmpPsd = Path.Combine(Path.GetTempPath(), $"fastpsd-test-{Guid.NewGuid():N}.psd");
    try
    {
        Check(SettingsFile.PathFor(tmpPsd).EndsWith("-ymmfastpsd.json"), "ファイル名の接尾辞");
        SettingsStore.Update(tmpPsd, f => f.SettingSets.Add(new SettingSet { Name = "A" }));
        SettingsStore.Update(tmpPsd, f => f.SettingSets.Add(f.SettingSets[0].DuplicateAs("B")));
        var (file, _) = SettingsStore.Get(tmpPsd);
        Check(file.SettingSets.Count == 2 && file.SettingSets[0].Id != file.SettingSets[1].Id, "複製は別IDの設定セットになる");
        Check(SettingsFile.Load(tmpPsd).SettingSets.Count == 2, "ディスクに保存されている");
        File.WriteAllText(SettingsFile.PathFor(tmpPsd), """{"Version":1,"SettingSets":[{"Id":"x","Name":"old","LipSyncs":[{"TargetKey":"t","Frames":["閉","半","開"]}]}]}""");
        var migrated = SettingsFile.Load(tmpPsd);
        Check(migrated.Version == 2 && migrated.SettingSets[0].LipSyncs[0].Frames.SequenceEqual(["開", "半", "閉"]), "旧形式（閉→開）の口パクは開→閉に並べ替えて読む");
    }
    finally { try { File.Delete(SettingsFile.PathFor(tmpPsd)); } catch { } }
}

// ---- 合成テスト：PSD内の統合画像（Photoshopの合成結果）と比べる ----
if (args.Length > 1 && Directory.Exists(args[1]))
{
    Console.WriteLine("[合成（Photoshopの統合画像との比較）]");
    Console.WriteLine("  一致率 = 色の差が4以下の画素の割合（白背景に重ねて比較）");
    int pass = 0, total = 0;
    foreach (var f in Directory.EnumerateFiles(args[1], "*.psd", SearchOption.AllDirectories).OrderBy(x => x))
    {
        PsdFile? psd = null;
        try
        {
            psd = PsdFile.Open(f);
            if (!psd.CanDecodeColor || psd.All.Count == 0 || psd.Depth != 8) continue;
            var state = StateResolver.Resolve(new StateInput { Psd = psd, Rules = new RuleOptions { Asterisk = false, Exclamation = false } });
            var plan = RenderPlanner.Build(psd, state);
            var ours = new CpuCompositor(psd).Render(plan);
            var merged = psd.DecodeMergedRgba();
            int n = psd.Width * psd.Height, good = 0;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float a = ours[i * 4 + 3];
                int worst = 0;
                for (int k = 0; k < 3; k++)
                {
                    float c = ours[i * 4 + k] + (1 - a); // 白背景
                    int d = Math.Abs((int)MathF.Round(c * 255) - merged[i * 4 + k]);
                    worst = Math.Max(worst, d);
                    sum += d;
                }
                if (worst <= 4) good++;
            }
            double rate = good * 100.0 / n;
            total++;
            bool ok = rate >= 99.0;
            if (ok) pass++;
            var name = Path.GetRelativePath(args[1], f);
            var notes = plan.Warnings.Count > 0 ? "  ※" + string.Join(" / ", plan.Warnings) : "";
            Console.WriteLine($"  {(ok ? "OK  " : "差異")} {rate,6:F2}%  平均差 {sum / n / 3,5:F2}  {name}{notes}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  SKIP {Path.GetFileName(f)}: {ex.Message}");
        }
        finally { psd?.Dispose(); }
    }
    Console.WriteLine($"  → {pass}/{total} 件が一致率99%以上");
}

// ---- 速度の確認 ----
if (args.Length > 2 && File.Exists(args[2]))
{
    Console.WriteLine("[速度]");
    var sw = Stopwatch.StartNew();
    using var psd = PsdFile.Open(args[2]);
    var tOpen = sw.Elapsed;
    sw.Restart();
    VisibilityState? st = null;
    for (int i = 0; i < 100; i++)
        st = StateResolver.Resolve(new StateInput { Psd = psd, ItemFrame = i, Fps = 60, VoiceVolume = 0.1 });
    var tResolve = sw.Elapsed / 100;
    sw.Restart();
    RenderPlan? plan = null;
    for (int i = 0; i < 100; i++) plan = RenderPlanner.Build(psd, st!);
    var tPlan = sw.Elapsed / 100;
    sw.Restart();
    Parallel.ForEach(plan!.Layers, l => PsdFile.ReturnBuffer(psd.DecodeBgra(l)));
    var tDecode = sw.Elapsed;
    Console.WriteLine($"  構造の読み込み {tOpen.TotalMilliseconds:F1} ms / 要素 {psd.All.Count}");
    Console.WriteLine($"  表示状態の計算 {tResolve.TotalMilliseconds:F3} ms（1フレームあたり）");
    Console.WriteLine($"  合成手順の作成 {tPlan.TotalMilliseconds:F3} ms（1フレームあたり）");
    Console.WriteLine($"  表示中 {plan.Layers.Count} 枚の展開 {tDecode.TotalMilliseconds:F1} ms");
}

Console.WriteLine(failures == 0 ? "\nすべて成功" : $"\n失敗 {failures} 件");
return failures == 0 ? 0 : 1;
