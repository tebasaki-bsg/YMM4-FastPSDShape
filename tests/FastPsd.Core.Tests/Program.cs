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
                      double vol = -1, MouthMode mouth = MouthMode.VolumePriority) =>
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

    // 口パクは図形にはないので、ここでは確かめない（Core は高速PSD立ち絵 v0.1.9 と同じ。そちらのテストで確認済み）

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

    Console.WriteLine("[図形: 中クリック（それ1つだけ）]");
    {
        var noFlip = new HashSet<int>();
        LayerOverrides? Solo(LayerOverrides cur, string key, RuleOptions? rules = null)
        {
            rules ??= new RuleOptions();
            var st = StateResolver.Resolve(new StateInput { Psd = psd, Rules = rules, Overrides = [cur] });
            return OverridesEditor.Solo(cur, psd, psd.ByKey[key], rules, [], noFlip, st.Visible);
        }
        bool Own(LayerOverrides o, string key, RuleOptions? rules = null) =>
            StateResolver.Resolve(new StateInput { Psd = psd, Rules = rules ?? new RuleOptions(), Overrides = [o] }).Visible[psd.ByKey[key].Index];

        var solo1 = Solo(new LayerOverrides(), "重複")!;
        Check(Own(solo1, "重複") && !Own(solo1, "重複#1") && !Own(solo1, "目") && !Own(solo1, "口") && !Own(solo1, "腕"), "同じ階層のほかの要素（フォルダも）をオフ");
        Check(Own(solo1, "!輪郭") && Own(solo1, "!体"), "「!」ルールがオンなら「!」の要素は表示のまま");
        Check(Solo(solo1, "重複") == null, "すでにそれ1つだけなら何もしない");
        var solo2 = Solo(new LayerOverrides(), "目/*ジト目")!;
        Check(Own(solo2, "目/*ジト目") && !Own(solo2, "目/*通常目") && !Own(solo2, "目/*閉じ目") && solo2.Select.ContainsKey("目"), "「*」のグループでは選択として記録");
        var solo3 = Solo(new LayerOverrides(), "口/*口A/半")!;
        Check(Own(solo3, "口/*口A/半") && !Own(solo3, "口/*口A/開") && !Own(solo3, "口/*口A/閉") && Own(solo3, "口/*口A"), "フォルダの中でも同じ。親フォルダは変えない");
        var hideMouth = new LayerOverrides(); hideMouth.Visible["口"] = false;
        var solo4 = Solo(hideMouth, "口/*口A/開")!;
        Check(Own(solo4, "口/*口A/開") && !Own(solo4, "口") && !V(R(ov: [solo4]), "口/*口A/開"), "親フォルダが非表示ならそのまま（見えないまま）");
        var noBang = new RuleOptions { Exclamation = false };
        var solo6 = Solo(new LayerOverrides(), "重複", noBang)!;
        Check(!Own(solo6, "!輪郭", noBang), "「!」ルールがオフなら「!」の要素もオフ");
    }

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

    // お気に入りとルールの指定の引き継ぎ（v0.1.4）
    var favSrc = new Preset { Name = "星", Favorite = true, ExclamationRule = false };
    var favConv = TachiePresetImport.Convert(favSrc, psd, []);
    Check(favConv.Favorite && favConv.ExclamationRule == false && favConv.AsteriskRule == null, "取り込みでお気に入りとルールの指定を引き継ぐ");
    var favFile = new ShapePresetFile();
    TachiePresetImport.Merge(favFile, [favConv, new Preset { Name = "普通" }], overwrite: false);
    Check(favFile.Presets[0].Favorite && favFile.Presets[0].ExclamationRule == false && !favFile.Presets[1].Favorite && favFile.Presets[1].ExclamationRule == null, "追加でもお気に入りとルールの指定を持つ");
    TachiePresetImport.Merge(favFile, [new Preset { Name = "星 (2)", Favorite = true }], overwrite: false);
    Check(favFile.Presets.Count == 3 && favFile.Presets[2].Favorite, "番号を付けて追加してもお気に入りは残る");
    favFile.Presets[1].Favorite = true; // 図形側でお気に入りにしたもの
    TachiePresetImport.Merge(favFile, [new Preset { Name = "普通", AsteriskRule = false }], overwrite: true);
    Check(favFile.Presets[1].Favorite && favFile.Presets[1].AsteriskRule == false, "上書きでも、図形側のお気に入りは残し、ルールの指定は取り込んだものにする");
    TachiePresetImport.Merge(favFile, [new Preset { Name = "星" }], overwrite: true);
    Check(favFile.Presets[0].Favorite && favFile.Presets[0].ExclamationRule == null, "上書き: 取り込むほうにルールの指定がなければ消える（内容と一緒に置き換え）");

    Console.WriteLine("[図形: 標準のPSD立ち絵から取り込み]");
    {
        // 高速PSD立ち絵の「標準から取り込み」のテストと同じ -ymm.json を作る（読み方は立ち絵と同じ StandardImport）
        string Id(string key) { var l = psd.ByKey[key]; return l.LayerId is uint id ? $"i{id}" : $"n{l.RecordIndex}"; }
        var baseVis = StateResolver.Resolve(new StateInput { Psd = psd, Rules = new RuleOptions { Asterisk = false, Exclamation = false } });
        // プリセット: 保存状態から 目/*通常目 → 目/*ジト目 に変えたもの（全部の表示要素を並べる）
        var shownKeys = psd.All.Where(l => baseVis.Visible[l.Index]).Select(l => l.Key).Where(k => k != "目/*通常目" && k != "目/*閉じ目").Append("目/*ジト目").ToList();
        var presetLayers = string.Join(",", shownKeys.Select(k => $"\"{Id(k)}\""));
        var stdJson = $$"""
        {
          "Presets": [
            { "Name": "ジト目", "Layers": [{{presetLayers}}] },
            { "Name": "ジト目", "Layers": [{{presetLayers}}, "n99999"] },
            { "Name": "笑顔", "Layers": [{{presetLayers}}] }
          ],
          "EyeAnimations": [
            { "Layers": ["{{Id("目/*通常目")}}", "{{Id("目/*閉じ目")}}"], "Offset": 0, "Interval": 0 }
          ]
        }
        """;
        var imp = ShapeStandardImport.Read(psd, stdJson);
        var ps = imp.Set.Presets;
        Check(ps.Count == 3 && ps[0].Name == "ジト目" && ps[1].Name == "ジト目 (2)", "プリセットを読む（設定ファイルの中で重なる名前は (2) を付ける）");
        var pst = R(ov: [ps[0].Overrides], rules: new RuleOptions { Exclamation = false });
        Check(V(pst, "目/*ジト目") && !V(pst, "目/*通常目") && !V(pst, "目/*閉じ目"), "PSD の保存状態との差に変換");
        Check(ps[0].ExclamationRule == false, "「!」の要素を隠すプリセットは、選んだときに「!」ルールをオフにする指定を付ける");
        Check(ShapeStandardImport.RuleNote(ps[0]) == "（選ぶと「!」ルールをオフ）" && ShapeStandardImport.RuleNote(new Preset { Name = "x" }) == "", "一覧に出すルールの説明");

        // 図形の一覧に加える（「笑顔」はすでにある → 番号を付けて追加）。お気に入り・ルールの指定は立ち絵からの取り込みと同じ扱い
        var file = new ShapePresetFile();
        file.Presets.Add(new Preset { Name = "笑顔", Favorite = true });
        var chosen = new List<Preset> { ps[1], ps[2] };
        var names = TachiePresetImport.Merge(file, chosen, overwrite: false);
        Check(names.SequenceEqual(["ジト目 (2)", "笑顔 (2)"]) && file.Presets.Count == 3 && file.Presets[1].ExclamationRule == false, "同じ名前は番号を付けて追加し、ルールの指定も持つ");
        var summary = ShapeStandardImport.Summary(imp, chosen.Select((p, i) => (p.Name, names[i])).ToList());
        Check(summary.StartsWith("2件のプリセットを取り込みました。") && summary.Contains("笑顔 → 笑顔 (2)"), "結果の説明: 件数と、名前を変えたもの");
        Check(summary.Contains("プリセット「ジト目 (2)」: 見つからない要素 1件") && !summary.Contains("プリセット「ジト目」:"), "結果の説明: 見つからない要素は取り込んだプリセットの分だけ");
        Check(summary.Contains("プリセット「笑顔 (2)」は「!」の要素") && !summary.Contains("プリセット「ジト目」は"), "結果の説明: ルールを変えるのは取り込んだプリセットの分だけ（図形の一覧での名前で）");
        Check(!summary.Contains("口パク") && !summary.Contains("目パチ"), "結果の説明: 口パク・目パチのことは書かない（図形にはないので取り込まない）");
        var names2 = TachiePresetImport.Merge(file, [ps[2]], overwrite: true);
        Check(names2.SequenceEqual(["笑顔"]) && file.Presets[0].Favorite && file.Presets[0].ExclamationRule == false, "上書き: 図形側のお気に入りは残し、ルールの指定は取り込んだものにする");
    }

    Console.WriteLine("[図形: お気に入り]");
    {
        var ff = new ShapePresetFile { Presets = [new Preset { Name = "A" }, new Preset { Name = "B" }, new Preset { Name = "C" }, new Preset { Name = "D" }] };
        Check(string.Concat(ff.PresetsInDisplayOrder().Select(p => p.Name)) == "ABCD", "お気に入りなし → 保存した順");
        Check(ff.ToggleFavorite("C") == true && ff.ToggleFavorite("B") == true, "お気に入りにする");
        Check(string.Concat(ff.PresetsInDisplayOrder().Select(p => p.Name)) == "BCAD", "お気に入りが上、それぞれの中は保存した順");
        Check(ff.ToggleFavorite("C") == false && string.Concat(ff.PresetsInDisplayOrder().Select(p => p.Name)) == "BACD", "お気に入りを外すと元の位置に戻る");
        Check(ff.ToggleFavorite("なし") == null, "ないプリセットは何もしない");
        Check(string.Concat(ff.Presets.Select(p => p.Name)) == "ABCD", "保存の順番そのものは変えない");
    }

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
        // 立ち絵 v0.1.9 の形式（お気に入り・ルールの指定あり）
        File.WriteAllText(SettingsFile.PathFor(tmpShape), """{"Version":3,"SettingSets":[{"Id":"x","Name":"キャラA","Presets":[{"Name":"p","Overrides":{"Visible":{},"Select":{}},"Favorite":true,"ExclamationRule":false}]}]}""");
        var tf3 = TachiePresetImport.ReadTachieFile(tmpShape)!.SettingSets[0].Presets[0];
        Check(tf3.Favorite && tf3.ExclamationRule == false && tf3.AsteriskRule == null, "立ち絵のお気に入りとルールの指定を読む");

        // v0.1.3 以前の図形のファイル（お気に入りなし）
        File.WriteAllText(ShapePresetFile.PathFor(tmpShape), """{"Version":1,"Presets":[{"Name":"old","Overrides":{"Visible":{},"Select":{}}}]}""");
        var oldShape = ShapePresetFile.Load(tmpShape);
        Check(!oldShape.Presets[0].Favorite && oldShape.Presets[0].ExclamationRule == null, "v0.1.3 以前の図形のファイルはお気に入りなし・ルールの指定なしとして読む");
        ShapePresetStore.Update(tmpShape, f => f.ToggleFavorite("old"));
        var savedShape = File.ReadAllText(ShapePresetFile.PathFor(tmpShape));
        Check(ShapePresetFile.Load(tmpShape).Presets[0].Favorite && !savedShape.Contains("Rule"), "お気に入りはファイルに保存され、ルールの指定がなければ書かない");
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
        Check(migrated.Version == SettingsFile.CurrentVersion && migrated.SettingSets[0].LipSyncs[0].Frames.SequenceEqual(["開", "半", "閉"]), "旧形式（閉→開）の口パクは開→閉に並べ替えて読む");
    }
    finally { try { File.Delete(SettingsFile.PathFor(tmpPsd)); } catch { } }
}

// ---- 「:flip」ルール（rules.psd と同じフォルダの flip.psd。make_flip_psd.py で作る） ----
var flipPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulesPath)) ?? ".", "flip.psd");
if (File.Exists(flipPath))
{
    using var fp = PsdFile.Open(flipPath);
    Console.WriteLine("[「:flip」ルール]");
    VisibilityState F(FlipMode mode, bool rule = true, LayerOverrides? ov = null) =>
        StateResolver.Resolve(new StateInput
        {
            Psd = fp, Rules = new RuleOptions { Flip = rule }, Flip = mode,
            Overrides = ov == null ? [] : [ov],
        });
    bool FV(VisibilityState st, string key) => st.EffectiveVisible[fp.ByKey[key].Index];

    Check(FlipMap.ParseTokens("体:flipx:flipy") == ("体", true, true, false) && FlipMap.ParseTokens("a:b:flipxy") == ("a:b", false, false, true)
          && FlipMap.ParseTokens("目") == ("目", false, false, false), "名前の末尾の読み取り");
    var map = FlipMap.For(fp);
    Check(map.Pairs.Count == 6 && !map.Flipped.Contains(fp.ByKey["孤立:flipx"].Index), $"対応づけ（{map.Pairs.Count}組。相手のない「孤立:flipx」は普通の要素）");

    var n0 = F(FlipMode.None);
    Check(FV(n0, "服/上") && !FV(n0, "服/上:flipx") && FV(n0, "!丸") && !FV(n0, "!丸:flipx"), "反転なし: 反転用は非表示（「!」で始まっていても）");
    Check(FV(n0, "孤立:flipx"), "反転なし: 相手のない「:flipx」は普通に表示");
    Check(FV(n0, "保存反転") && !FV(n0, "保存反転:flipx"), "保存時に反転用だけが表示 → 通常用に移す");
    Check(n0.BeforeFlip == null, "反転なしなら入れ替えの前の状態は持たない");

    var x = F(FlipMode.X);
    Check(!FV(x, "服/上") && FV(x, "服/上:flipx") && FV(x, "服/上:flipx/柄A") && !FV(x, "服/上:flipx/柄B"), "左右反転: フォルダを入れ替え、中身のオン・オフも写す");
    Check(!FV(x, "!丸") && FV(x, "!丸:flipx"), "左右反転: 「!」の要素も入れ替える");
    Check(!FV(x, "顔") && FV(x, "顔:flipx:flipy") && !FV(x, "顔:flipxy"), "左右反転: 「:flipx:flipy」を使う");
    Check(FV(x, "保存反転:flipx") && !FV(x, "保存反転"), "左右反転: 保存時に反転用だった要素も反転用で出る");
    Check(x.BeforeFlip != null && x.BeforeFlip[fp.ByKey["服/上"].Index] && x.Hash != n0.Hash, "入れ替えの前の状態を残す（レイヤー欄用）");
    var y = F(FlipMode.Y);
    Check(FV(y, "顔:flipx:flipy") && !FV(y, "顔") && FV(y, "服/上") && !FV(y, "服/上:flipx"), "上下反転: 「:flipy」のある要素だけ入れ替える");
    var xy = F(FlipMode.XY);
    Check(FV(xy, "顔:flipxy") && !FV(xy, "顔:flipx:flipy") && !FV(xy, "顔") && FV(xy, "服/上"), "上下左右反転: 「:flipxy」だけを使う");

    var ovB = new LayerOverrides(); ovB.Visible["服/上/柄B"] = true; ovB.Visible["服/上:flipx"] = true;
    var xb = F(FlipMode.X, ov: ovB);
    Check(FV(xb, "服/上:flipx/柄B") && FV(xb, "服/上:flipx/柄A"), "通常用で変えた中身が反転用に写る（反転用への上書きは無視）");
    Check(!FV(F(FlipMode.None, ov: ovB), "服/上:flipx"), "反転なしなら、反転用を上書きしても表示しない");
    var hideUp = new LayerOverrides(); hideUp.Visible["服/上"] = false;
    Check(!FV(F(FlipMode.X, ov: hideUp), "服/上") && !FV(F(FlipMode.X, ov: hideUp), "服/上:flipx"), "通常用が非表示なら反転用も出さない");

    var off = F(FlipMode.X, rule: false);
    Check(FV(off, "服/上") && !FV(off, "服/上:flipx") && FV(off, "!丸:flipx") && FV(off, "!丸"), "ルールがオフ: 入れ替えず、反転用も普通の要素（「!」で表示）");

    // 中クリックは反転用の要素に触らない
    var fr = new RuleOptions();
    var fst = StateResolver.Resolve(new StateInput { Psd = fp, Rules = fr });
    var soloFlip = OverridesEditor.Solo(new LayerOverrides(), fp, fp.ByKey["顔"], fr, [], map.Flipped, fst.Visible)!;
    Check(!soloFlip.Visible.ContainsKey("顔:flipx:flipy") && !soloFlip.Visible.ContainsKey("顔:flipxy") && soloFlip.Visible["服"] == false && soloFlip.Visible["孤立:flipx"] == false,
        "中クリック: 反転用は触らず、相手のない「:flipx」は普通の要素としてオフ");
    var soloX = F(FlipMode.X, ov: soloFlip);
    Check(FV(soloX, "顔:flipx:flipy") && !FV(soloX, "服") && !FV(soloX, "口"), "中クリックのあと左右反転: 反転用の顔だけが出る");
}

// ---- フォルダのマスク・通過フォルダの不透明度（rules.psd と同じフォルダの mask.psd。make_mask_psd.py で作る。v0.2.1、高速PSD立ち絵 v0.1.11 と同じ） ----
var maskPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulesPath)) ?? ".", "mask.psd");
if (File.Exists(maskPath))
{
    using var mp = PsdFile.Open(maskPath);
    Console.WriteLine("[フォルダのマスク・通過フォルダの不透明度]");
    var ms = StateResolver.Resolve(new StateInput { Psd = mp, Rules = new RuleOptions(), Overrides = [] });
    var mplan = RenderPlanner.Build(mp, ms);
    Check(mplan.Warnings.Count == 0, $"未対応の注意が出ない（{string.Join("、", mplan.Warnings)}）");
    Check(mplan.Layers.Count(l => l.IsGroup) == 2 && mp.ByKey["A"].HasFolderMask && mp.ByKey["B"].HasFolderMask && !mp.ByKey["C"].HasFolderMask,
          "マスクのあるフォルダ2つの画像を GPU に置く");
    Check(mp.ByKey["A"].ImageWidth == 8 && mp.ByKey["A"].ImageLeft == 0 && mp.ByKey["B"].ImageLeft == 28 && mp.ByKey["B"].ImageWidth == 4,
          "フォルダの画像の範囲はマスクの範囲");
    Check(mplan.ContentBounds(mp.Width, mp.Height) == (0, 0, 48, 8), "マスクは描く範囲に数えない");
    Check(mplan.Ops.Count(o => o is PassThroughMaskOp) == 1, "マスク付きの通過フォルダは下の絵ごと1つの手順にする");

    var mpx = CpuCompositor.ToStraightRgba(new CpuCompositor(mp).Render(mplan));
    bool Near(int x, double r, double g, double b)
    {
        int i = x * 4; // 1行目で見る（どの行も同じ）
        return Math.Abs(mpx[i] - r) <= 2 && Math.Abs(mpx[i + 1] - g) <= 2 && Math.Abs(mpx[i + 2] - b) <= 2 && mpx[i + 3] == 255;
    }
    double half = 128 / 255.0, gray = 128;
    Check(Near(1, 255, 0, 0), "通常フォルダのマスク: 255 の所は中身のまま");
    Check(Near(5, 255 * half + gray * (1 - half), gray * (1 - half), gray * (1 - half)), "通常フォルダのマスク: 128 の所は半分");
    Check(Near(10, gray, gray, gray), "通常フォルダのマスク（外は黒）: 範囲の外は消える");
    Check(Near(20, 0, 0, gray), "通過フォルダのマスク（外は白）: 範囲の外は中身（乗算）が効く");
    Check(Near(30, gray, gray, gray), "通過フォルダのマスク: 0 の所は下の絵のまま");
    double g1 = 255 * half + gray * (1 - half), rb1 = gray * (1 - half);
    Check(Near(35, rb1, g1, rb1), "通過フォルダの不透明度: 中の要素に掛ける（緑）");
    Check(Near(45, 255 * half + rb1 * (1 - half), 255 * half + g1 * (1 - half), 255 * half + rb1 * (1 - half)),
          "通過フォルダの不透明度: 重なった所は、それぞれ薄くして重ねる（PSDToolKit と同じ）");

    var hideA = new LayerOverrides(); hideA.Visible["A"] = false;
    var hplan = RenderPlanner.Build(mp, StateResolver.Resolve(new StateInput { Psd = mp, Overrides = [hideA] }));
    Check(!hplan.Layers.Contains(mp.ByKey["A"]), "非表示のフォルダのマスクは読み込まない");
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
