using System.Diagnostics;
using System.Numerics;
using FastPsd.Core.Compose;
using FastPsd.Core.Psd;
using FastPsd.Core.State;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YMM4FastPsdShape.Rendering;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace YMM4FastPsdShape;

/// <summary>
/// 図形（高速PSD）の描画。YMM4 から描画のたびに Update が呼ばれる。
/// 表示状態の計算は軽いので毎回行い、状態が変わったときだけ合成し直す。
/// 合成結果は既定で1枚の画像に焼き付け、同じPSD・同じ表示状態のアイテムと共有する。
/// PSD・GPU上のレイヤー画像・焼き付けた画像はプラグイン全体で持つので、このクラスが作り直されても読み直しは起きない。
/// </summary>
internal sealed class FastPsdShapeSource : IShapeSource
{
    readonly IGraphicsDevicesAndContext _devices;
    readonly FastPsdShapeParameter _parameter;
    readonly DeviceLayerCache _layerCache;
    readonly BakedImageCache _bakedCache;
    readonly ID2D1Bitmap1 _empty;
    readonly Vortice.Direct2D1.Effects.AffineTransform2D _transform;
    readonly ID2D1Image _output;

    PsdEntry? _entry;
    string? _path;
    string? _loadError;
    long _lastLoadAttempt;

    ulong _lastHash;
    bool _lastFolderClipping;
    bool _lastBake;
    bool _hasState;

    // 焼き付けた画像（共有）
    BakedImage? _baked;
    BakedImageCache.Key _bakedKey;
    // 焼き付けないときの合成結果（コマンドリスト）と、使用中のレイヤー
    ComposeResult? _composed;
    IReadOnlyList<PsdLayer> _pinned = [];

    public ID2D1Image Output => _output;

    public FastPsdShapeSource(IGraphicsDevicesAndContext devices, FastPsdShapeParameter parameter)
    {
        _devices = devices;
        _parameter = parameter;
        try { _ = ShapePluginSettings.Default; } catch { /* 設定を読めなくても既定値で動かす */ }
        _layerCache = DeviceLayerCache.Attach(devices.D2D.Device);
        _bakedCache = BakedImageCache.Attach(devices.D2D.Device);
        _empty = devices.DeviceContext.CreateEmptyBitmap();
        _transform = new Vortice.Direct2D1.Effects.AffineTransform2D(devices.DeviceContext);
        _transform.SetInput(0, _empty, true);
        _output = _transform.Output; // Effect から取得した Output は必ず Dispose する
    }

    public void Update(TimelineItemSourceDescription desc)
    {
        try
        {
            UpdateCore();
        }
        catch (Exception ex)
        {
            ShapeLog.Error("描画に失敗しました", ex);
        }
    }

    void UpdateCore()
    {
        var p = _parameter;
        var path = p.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            // 毎フレーム呼ばれるので、表示しているものがあるときだけ片付ける
            if (_entry != null || _baked != null || _composed != null) ReleaseEntry();
            _path = null;
            return;
        }

        // ---- PSD の取得（変わったとき、外部で更新されたときだけ） ----
        if (_entry == null || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) || PsdRegistry.CheckStale(_entry))
        {
            // 読めなかったPSDは毎フレーム開き直さない（1秒に1回まで）
            if (_entry == null && path == _path && _loadError != null && Environment.TickCount64 - _lastLoadAttempt < 1000)
                return;
            ReleaseEntry();
            _path = path;
            _lastLoadAttempt = Environment.TickCount64;
            try
            {
                _entry = PsdRegistry.Acquire(path);
                _loadError = null;
            }
            catch (Exception ex)
            {
                if (_loadError != ex.Message) ShapeLog.Error($"PSDを開けませんでした: {path}", ex);
                _loadError = ex.Message;
                return;
            }
        }
        var entry = _entry!;
        var psd = entry.Psd;

        // ---- 表示状態の計算 ----
        var state = StateResolver.Resolve(new StateInput
        {
            Psd = psd,
            Rules = new RuleOptions { Asterisk = p.AsteriskRule, Exclamation = p.ExclamationRule },
            Overrides = [p.GetOverrides()],
        });
        bool clipping = p.ApplyFolderClipping;
        bool bake = ShapePluginSettings.Bake;
        if (_hasState && state.Hash == _lastHash && clipping == _lastFolderClipping && bake == _lastBake)
            return; // 見た目が変わらないので何もしない

        // ---- 合成し直す ----
        var sw = Stopwatch.StartNew();
        var plan = RenderPlanner.Build(psd, state, clipping);
        var bounds = plan.ContentBounds(psd.Width, psd.Height);
        string how;
        try
        {
            if (bounds.Width == 0)
            {
                ShowEmpty();
                how = "表示する画像なし";
            }
            else if (bake && TryShowBaked(entry, plan, bounds, new BakedImageCache.Key(entry, state.Hash, clipping), out how))
            {
            }
            else
            {
                ShowCommandList(entry, plan, psd.Width, psd.Height, out how);
            }
        }
        catch (Exception ex)
        {
            // 失敗した状態のまま毎フレーム合成し直さないよう、状態は記録する（表示を変えるか、PSDが更新されたら再挑戦）
            ShapeLog.Error($"合成に失敗しました: {path}", ex);
            ShowEmpty();
            how = "失敗";
        }
        _lastHash = state.Hash;
        _lastFolderClipping = clipping;
        _lastBake = bake;
        _hasState = true;
        ShapeLog.Info($"合成し直し source={ShapeLog.Id(this)} {how} 描画{plan.Layers.Count}枚 範囲={bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} 合計{sw.Elapsed.TotalMilliseconds:F1}ms 状態={state.Hash:X}");
    }

    Matrix3x2 Centering(int left, int top)
    {
        var psd = _entry!.Psd;
        // 標準のPSD図形と同じく、PSDの中心をアイテムの位置に合わせる
        return Matrix3x2.CreateTranslation(left + MathF.Floor(-psd.Width / 2f), top + MathF.Floor(-psd.Height / 2f));
    }

    /// <summary>合成結果を1枚の画像に焼き付けて表示する（同じ状態の画像があれば共有する）</summary>
    bool TryShowBaked(PsdEntry entry, RenderPlan plan, (int Left, int Top, int Width, int Height) b, BakedImageCache.Key key, out string how)
    {
        var dc = _devices.DeviceContext;
        int max = (int)dc.MaximumBitmapSize;
        if (b.Width > max || b.Height > max)
        {
            how = $"焼き付けなし（{b.Width}x{b.Height} が画像の上限 {max} を超える）";
            return false;
        }

        string detail = "";
        var image = _bakedCache.Acquire(key, () => Bake(entry, plan, b, out detail), out bool hit);
        if (image == null)
        {
            how = "焼き付けに失敗";
            return false;
        }
        // 新しい画像を設定してから、古いものを返す
        _transform.SetInput(0, image.Bitmap, true);
        _transform.TransformMatrix = Centering(image.Left, image.Top);
        ReleaseShown();
        _baked = image;
        _bakedKey = key;
        how = hit ? "焼き付け済みの画像を共有" : $"焼き付け {detail}";
        return true;
    }

    BakedImage? Bake(PsdEntry entry, RenderPlan plan, (int Left, int Top, int Width, int Height) b, out string detail)
    {
        var dc = _devices.DeviceContext;
        var sw = Stopwatch.StartNew();
        var bitmaps = _layerCache.Pin(dc, entry, plan.Layers, out var stats);
        double tLoad = sw.Elapsed.TotalMilliseconds;
        ID2D1Bitmap1? bmp = null;
        try
        {
            using var composed = D2DCompositor.Compose(dc, plan, bitmaps);
            var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
            bmp = dc.CreateBitmap(new SizeI(b.Width, b.Height), IntPtr.Zero, 0, props);
            dc.Target = bmp;
            dc.BeginDraw();
            dc.Clear(null);
            dc.DrawImage(composed.Image, new Vector2(-b.Left, -b.Top), null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            dc.EndDraw();
            dc.Target = null;
            detail = $"新規読込{stats.Loaded}枚 {stats.LoadedBytes / 1048576.0:F1}MB 読込{tLoad:F1}ms 重ね{sw.Elapsed.TotalMilliseconds - tLoad:F1}ms";
            var result = new BakedImage { Bitmap = bmp, Left = b.Left, Top = b.Top, Bytes = (long)b.Width * b.Height * 4 };
            bmp = null;
            return result;
        }
        catch (Exception ex)
        {
            dc.Target = null;
            ShapeLog.Error("合成結果の焼き付けに失敗しました", ex);
            detail = ex.Message;
            return null;
        }
        finally
        {
            bmp?.Dispose();
            // 焼き付けた後はレイヤー画像を使わないので返す（上限までは GPU に残るので、差分を戻したときも速い）
            _layerCache.Unpin(entry, plan.Layers);
        }
    }

    /// <summary>焼き付けずに、コマンドリストのまま表示する（毎フレーム GPU がレイヤーを重ねる）</summary>
    void ShowCommandList(PsdEntry entry, RenderPlan plan, int width, int height, out string how)
    {
        var dc = _devices.DeviceContext;
        var bitmaps = _layerCache.Pin(dc, entry, plan.Layers, out var stats);
        ComposeResult? composed = null;
        try
        {
            composed = D2DCompositor.Compose(dc, plan, bitmaps);
            // キャンバスの外にはみ出した部分は表示しない（Photoshop・標準のPSD図形と同じ）
            var clipped = dc.CreateCommandList();
            composed.Resources.Add(clipped);
            dc.Target = clipped;
            dc.BeginDraw();
            dc.PushAxisAlignedClip(new Vortice.RawRectF(0, 0, width, height), AntialiasMode.Aliased);
            dc.DrawImage(composed.Image, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
            dc.PopAxisAlignedClip();
            dc.EndDraw();
            dc.Target = null;
            clipped.Close();

            _transform.SetInput(0, clipped, true);
            _transform.TransformMatrix = Centering(0, 0);
        }
        catch
        {
            dc.Target = null;
            composed?.Dispose();
            _layerCache.Unpin(entry, plan.Layers);
            throw;
        }
        ReleaseShown();
        _composed = composed;
        _pinned = plan.Layers;
        how = $"コマンドリスト 新規読込{stats.Loaded}枚 {stats.LoadedBytes / 1048576.0:F1}MB {stats.Elapsed.TotalMilliseconds:F1}ms";
    }

    /// <summary>今表示しているもの（焼き付けた画像・コマンドリスト）を返す。入力の差し替えは呼び出し元で先に行う。</summary>
    void ReleaseShown()
    {
        if (_baked != null)
        {
            _bakedCache.Release(_bakedKey, _baked);
            _baked = null;
        }
        if (_composed != null)
        {
            _composed.Dispose();
            _composed = null;
        }
        if (_pinned.Count > 0 && _entry != null) _layerCache.Unpin(_entry, _pinned);
        _pinned = [];
    }

    void ShowEmpty()
    {
        _transform.SetInput(0, _empty, true);
        _transform.TransformMatrix = Matrix3x2.Identity;
        ReleaseShown();
    }

    void ReleaseEntry()
    {
        ShowEmpty();
        _hasState = false;
        if (_entry == null) return;
        PsdRegistry.Release(_entry);
        _entry = null;
    }

    public void Dispose()
    {
        _transform.SetInput(0, null, true); // Effect の Input は必ず null に戻す
        ReleaseShown();
        if (_entry != null)
        {
            PsdRegistry.Release(_entry);
            _entry = null;
        }
        _layerCache.Detach();
        _bakedCache.Detach();
        _output.Dispose();
        _transform.Dispose();
        _empty.Dispose();
    }
}
