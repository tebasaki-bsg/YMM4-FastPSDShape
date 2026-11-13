namespace YMM4FastPsdShape.UI;

/// <summary>プリセットの一覧の1項目（名前と、お気に入りかどうか）。高速PSD立ち絵と同じ。</summary>
public sealed class PresetListItem
{
    public required string Name { get; init; }
    public bool Favorite { get; init; }
}
