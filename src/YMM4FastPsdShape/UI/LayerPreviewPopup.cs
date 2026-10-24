using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastPsd.Core.Psd;
using FastPsd.Core.Thumbnails;

namespace YMM4FastPsdShape.UI;

/// <summary>
/// レイヤーのサムネイルにカーソルを合わせたときに、右隣に出す拡大表示。
/// 画像は PSD からその場で作り（Size の枠に収まる大きさ）、メモリに残して使い回す。できるまでの間は小さいサムネイルを引き伸ばして出す。
/// ツリーの作りに依存しないので、高速PSD立ち絵にもそのまま流用できる。
/// </summary>
internal sealed class LayerPreviewPopup
{
    /// <summary>拡大表示の大きさ（枠の内側）。160px の約70%で、市松模様の 8px のマスがちょうど並ぶ値。</summary>
    public const int Size = 112;

    readonly Popup _popup;
    readonly Image _image;
    PsdFile? _psd;
    PsdLayer? _layer;
    int _request;

    public LayerPreviewPopup()
    {
        _image = new Image { Width = Size, Height = Size, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var border = new Border
        {
            Background = CheckerBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(1),
            Child = _image,
            IsHitTestVisible = false,
        };
        _popup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Right,
            HorizontalOffset = 4,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            StaysOpen = true,
            Focusable = false,
        };
    }

    /// <summary>target の右隣にレイヤーの拡大表示を出す。placeholder は画像ができるまでの代わり（小さいサムネイル）。</summary>
    public void Show(FrameworkElement target, PsdFile psd, PsdLayer layer, ImageSource? placeholder)
    {
        if (_popup.IsOpen && ReferenceEquals(_psd, psd) && ReferenceEquals(_layer, layer) && ReferenceEquals(_popup.PlacementTarget, target)) return;
        _psd = psd;
        _layer = layer;
        int request = ++_request;
        _popup.PlacementTarget = target;
        var cached = LayerPreviewCache.TryGet(psd, layer);
        _image.Source = cached ?? placeholder;
        _popup.IsOpen = true;
        if (cached != null) return;

        var dispatcher = _image.Dispatcher;
        _ = Task.Run(() =>
        {
            try
            {
                var img = LayerPreviewCache.GetOrCreate(psd, layer);
                dispatcher.BeginInvoke(() =>
                {
                    if (request == _request && _popup.IsOpen) _image.Source = img;
                });
            }
            catch (Exception ex)
            {
                // PSD が閉じられた・更新された直後などは作れないことがある。小さいサムネイルのまま表示を続ける。
                ShapeLog.Info($"拡大表示の画像を作れませんでした: {layer.Key} {ex.Message}");
            }
        });
    }

    public void Hide()
    {
        if (!_popup.IsOpen && _psd == null) return;
        _request++;
        _popup.IsOpen = false;
        _popup.PlacementTarget = null;
        _image.Source = null;
        _psd = null;
        _layer = null;
    }

    /// <summary>透明部分の市松模様（8px のマス、#666666 と #999999）</summary>
    static readonly Brush CheckerBrush = CreateChecker();

    static Brush CreateChecker()
    {
        var dark = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        var light = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        RenderOptions.SetEdgeMode(brush, EdgeMode.Aliased);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 拡大表示の画像のメモリ上の置き場所（プラグイン全体で共有）。
/// 1枚は最大 112×112 で約 50KB。上限を超えたら、最後に使ったのが古いものから捨てる。
/// </summary>
internal static class LayerPreviewCache
{
    const long BudgetBytes = 64L * 1024 * 1024;

    sealed class Item
    {
        public required BitmapSource Image;
        public long Bytes;
        public long LastUse;
    }

    static readonly object Lock = new();
    static readonly Dictionary<(PsdFile, int), Item> Items = new();
    static long _bytes;
    static long _counter;

    public static BitmapSource? TryGet(PsdFile psd, PsdLayer layer)
    {
        lock (Lock)
        {
            if (!Items.TryGetValue((psd, layer.Index), out var it)) return null;
            it.LastUse = ++_counter;
            return it.Image;
        }
    }

    /// <summary>PSD からレイヤーを展開し、Size の枠に収まる大きさに縮小して作る（どのスレッドからでも呼べる）</summary>
    public static BitmapSource GetOrCreate(PsdFile psd, PsdLayer layer)
    {
        var existing = TryGet(psd, layer);
        if (existing != null) return existing;

        var px = psd.DecodeBgra(layer);
        Thumbnail t;
        try { t = ThumbnailCache.Downscale(px, layer.Width, layer.Height, LayerPreviewPopup.Size); }
        finally { PsdFile.ReturnBuffer(px); }
        var bmp = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Pbgra32, null, t.Bgra, t.Width * 4);
        bmp.Freeze();

        lock (Lock)
        {
            if (Items.TryGetValue((psd, layer.Index), out var it)) return it.Image;
            var item = new Item { Image = bmp, Bytes = (long)t.Width * t.Height * 4, LastUse = ++_counter };
            Items[(psd, layer.Index)] = item;
            _bytes += item.Bytes;
            if (_bytes > BudgetBytes)
            {
                foreach (var (key, old) in Items.OrderBy(kv => kv.Value.LastUse).ToList())
                {
                    if (_bytes <= BudgetBytes) break;
                    if (old == item) continue;
                    Items.Remove(key);
                    _bytes -= old.Bytes;
                }
            }
            return bmp;
        }
    }
}
