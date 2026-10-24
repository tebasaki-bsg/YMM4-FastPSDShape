using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace YMM4FastPsdShape;

/// <summary>
/// プラグインのログ。
/// ・詳細ログ: %LOCALAPPDATA%\YMM4FastPsdShape\Logs\yyyyMMdd.log（設定画面でオン・オフ）
/// ・エラー: 上記に加えて YMM4 本体のログにも書く
/// 書き込みは別スレッドでまとめて行うので、描画を遅くしない。
/// </summary>
internal static class ShapeLog
{
    public static bool Verbose { get; set; } = false;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4FastPsdShape", "Logs");

    static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    static readonly Thread Writer;

    static ShapeLog()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "YMM4FastPsdShape log" };
        Writer.Start();
    }

    /// <summary>詳細ログ（Verbose がオンのときだけ）</summary>
    public static void Info(string message)
    {
        if (Verbose) Enqueue("INFO ", message);
    }

    /// <summary>エラー（常に記録し、YMM4 本体のログにも書く）</summary>
    public static void Error(string message, Exception? ex = null)
    {
        Enqueue("ERROR", ex == null ? message : $"{message}\n{ex}");
        try { YukkuriMovieMaker.Commons.Log.Default.Write("[高速PSD図形] " + message, ex ?? new Exception(message)); }
        catch { /* 本体のログに書けなくても続ける */ }
    }

    /// <summary>オブジェクトを見分けるための短いID（同じオブジェクトなら同じ値）</summary>
    public static string Id(object? o) => o == null ? "null" : $"{o.GetType().Name}#{RuntimeHelpers.GetHashCode(o):X}";

    static void Enqueue(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
        System.Diagnostics.Debug.WriteLine("[YMM4FastPsdShape] " + line);
        Queue.TryAdd(line);
    }

    static void WriteLoop()
    {
        var sb = new StringBuilder();
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            sb.Clear();
            sb.AppendLine(first);
            while (sb.Length < 1 << 16 && Queue.TryTake(out var more)) sb.AppendLine(more);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd}.log"), sb.ToString(), Encoding.UTF8);
            }
            catch { /* ログを書けなくても止めない */ }
        }
    }
}
