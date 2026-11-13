namespace YMM4FastPsdShape;

/// <summary>
/// YMM4 本体の列挙型を、番号ではなく名前で扱う（v0.1.4。高速PSD立ち絵 v0.1.9 と同じ）。
/// C# では列挙型の値が数値としてプラグインの DLL に埋め込まれるため、YMM4 の更新で列挙型の途中に項目が足されると、
/// 古いプラグインの数値が別の項目を指してしまう（YMM4 v4.57.0.0 で CommandType に実際に起きた。開発ノート15節）。
/// 実行時の YMM4 の列挙型を名前で引けば、番号がずれても正しい項目を使える。
/// </summary>
internal static class Ymm4Enum
{
    /// <summary>実行中の YMM4 で、その名前の項目の値。見つからなければ fallback（ビルドした YMM4 の値）を返し、ログに残す。</summary>
    public static T ByName<T>(string name, T fallback) where T : struct, Enum
    {
        if (Enum.TryParse<T>(name, out var v) && Enum.IsDefined(typeof(T), v) && v.ToString() == name) return v;
        ShapeLog.Error($"YMM4 の {typeof(T).Name} に「{name}」が見つかりません。ビルドした YMM4 の値 {Convert.ToInt64(fallback)} を使います");
        return fallback;
    }
}
