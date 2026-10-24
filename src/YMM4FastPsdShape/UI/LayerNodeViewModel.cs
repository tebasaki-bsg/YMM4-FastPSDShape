using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FastPsd.Core.Psd;

namespace YMM4FastPsdShape.UI;

/// <summary>レイヤーツリーの1行</summary>
internal sealed class LayerNodeViewModel : INotifyPropertyChanged
{
    readonly Action<LayerNodeViewModel, bool> _onToggle;
    bool _suppress;

    public LayerNodeViewModel(PsdLayer layer, Action<LayerNodeViewModel, bool> onToggle)
    {
        Layer = layer;
        _onToggle = onToggle;
    }

    public PsdLayer Layer { get; }
    public string Name => Layer.Name;
    public bool IsGroup => Layer.IsGroup;
    public ObservableCollection<LayerNodeViewModel> Children { get; } = [];
    public Visibility FolderIconVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>フォルダの行だけ下に1px空ける</summary>
    public Thickness RowMargin => IsGroup ? new Thickness(0, 0, 0, 1) : new Thickness(0);

    bool _isChecked;
    /// <summary>この要素自身の表示状態。ユーザーが切り替えると差分に記録する。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnChanged();
            if (!_suppress) _onToggle(this, value);
        }
    }

    public void SetCheckedSilently(bool value)
    {
        _suppress = true;
        try { IsChecked = value; }
        finally { _suppress = false; }
    }

    bool _isCheckEnabled = true;
    public bool IsCheckEnabled { get => _isCheckEnabled; set { if (_isCheckEnabled != value) { _isCheckEnabled = value; OnChanged(); } } }

    double _rowOpacity = 1;
    /// <summary>親フォルダが非表示のときは薄く表示する</summary>
    public double RowOpacity { get => _rowOpacity; set { if (_rowOpacity != value) { _rowOpacity = value; OnChanged(); } } }

    FontWeight _nameWeight = FontWeights.Normal;
    /// <summary>このアイテムで変更した要素は太字</summary>
    public FontWeight NameWeight { get => _nameWeight; set { if (_nameWeight != value) { _nameWeight = value; OnChanged(); } } }

    string _badge = "";
    /// <summary>口パク・目パチ・スライダーの印</summary>
    public string Badge { get => _badge; set { if (_badge != value) { _badge = value; OnChanged(); } } }

    ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set { if (_thumbnail != value) { _thumbnail = value; OnChanged(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
