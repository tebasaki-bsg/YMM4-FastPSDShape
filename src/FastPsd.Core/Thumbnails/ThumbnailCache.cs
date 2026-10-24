using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FastPsd.Core.Psd;

namespace FastPsd.Core.Thumbnails;

/// <summary>1レイヤー分のサムネイル（乗算済み BGRA）</summary>
public sealed record Thumbnail(int Width, int Height, byte[] Bgra);

/// <summary>
/// レイヤーツリー用のサムネイル。初回は全レイヤーを展開・縮小して作り、ディスクに保存する。
/// 2回目以降はキャッシュから読む。キャッシュは PSD の中身（サイズと先頭・末尾のハッシュ）で識別するので、
/// PSD を移動・名前変更しても使える。
/// </summary>
public static class ThumbnailCache
{
    public const int Size = 40;
    const uint Magic = 0x48545046; // "FPTH"
    const int FormatVersion = 1;

    /// <summary>キャッシュの保存場所（プラグイン設定で変更できる）</summary>
    public static string Directory { get; set; } = DefaultDirectory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4FastPsdShape", "Cache");

    /// <summary>
    /// 自分のキャッシュにないときに読みに行く場所（読むだけ）。
    /// 高速PSD立ち絵のキャッシュは同じ形式なので、立ち絵で使ったPSDはサムネイルを作り直さずに済む。
    /// </summary>
    public static List<string> FallbackDirectories { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4FastPsd", "Cache"),
    ];

    /// <summary>PSD の中身を表すキー（サイズ＋先頭と末尾 64KB のハッシュ）</summary>
    public static string ContentKey(string psdPath)
    {
        using var fs = new FileStream(psdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        const int chunk = 64 * 1024;
        var buf = new byte[Math.Min(len, chunk) * 2 + 8];
        int n = fs.Read(buf, 0, (int)Math.Min(len, chunk));
        if (len > chunk)
        {
            fs.Position = Math.Max(chunk, len - chunk);
            n += fs.Read(buf, n, (int)Math.Min(len - chunk, chunk));
        }
        BitConverter.TryWriteBytes(buf.AsSpan(n, 8), len);
        var hash = SHA256.HashData(buf.AsSpan(0, n + 8));
        return Convert.ToHexString(hash, 0, 16);
    }

    static string PathFor(string key) => Path.Combine(Directory, key + ".thumbs");

    /// <summary>キャッシュから読む。なければ null。</summary>
    public static Dictionary<int, Thumbnail>? TryLoad(string key)
    {
        var r = TryLoadFrom(PathFor(key), touch: true);
        if (r != null) return r;
        foreach (var dir in FallbackDirectories)
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase)) continue;
            r = TryLoadFrom(Path.Combine(dir, key + ".thumbs"), touch: false); // 他のプラグインのファイルには触らない
            if (r != null) return r;
        }
        return null;
    }

    static Dictionary<int, Thumbnail>? TryLoadFrom(string path, bool touch)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8);
            if (br.ReadUInt32() != Magic || br.ReadInt32() != FormatVersion) return null;
            int count = br.ReadInt32();
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            using var zr = new BinaryReader(z);
            var result = new Dictionary<int, Thumbnail>(count);
            for (int i = 0; i < count; i++)
            {
                int index = zr.ReadInt32(), w = zr.ReadInt32(), h = zr.ReadInt32();
                var data = zr.ReadBytes(w * h * 4);
                result[index] = new Thumbnail(w, h, data);
            }
            if (touch) File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string key, IReadOnlyDictionary<int, Thumbnail> thumbs)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
            bw.Write(Magic);
            bw.Write(FormatVersion);
            bw.Write(thumbs.Count);
            bw.Flush();
            using var z = new ZLibStream(fs, CompressionLevel.Fastest);
            using var zw = new BinaryWriter(z);
            foreach (var (index, t) in thumbs)
            {
                zw.Write(index); zw.Write(t.Width); zw.Write(t.Height);
                zw.Write(t.Bgra, 0, t.Width * t.Height * 4);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>キャッシュをすべて削除する。削除したバイト数を返す。</summary>
    public static long Clear()
    {
        long total = 0;
        if (!System.IO.Directory.Exists(Directory)) return 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.thumbs"))
        {
            try { total += new FileInfo(f).Length; File.Delete(f); } catch { }
        }
        return total;
    }

    public static long TotalBytes()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        return System.IO.Directory.EnumerateFiles(Directory, "*.thumbs").Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
    }

    /// <summary>全レイヤーのサムネイルを作る（並列）。進み具合は progress に (済, 全体) で通知する。</summary>
    public static Dictionary<int, Thumbnail> Build(PsdFile psd, int maxDegree, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var targets = psd.All.Where(l => l.HasPixels).ToList();
        var result = new Dictionary<int, Thumbnail>(targets.Count);
        int done = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = ct };
        Parallel.ForEach(targets, po, l =>
        {
            var bgra = psd.DecodeBgra(l);
            try
            {
                var t = Downscale(bgra, l.Width, l.Height, Size);
                lock (result) result[l.Index] = t;
            }
            finally { PsdFile.ReturnBuffer(bgra); }
            int d = Interlocked.Increment(ref done);
            if (d % 32 == 0 || d == targets.Count) progress?.Report((d, targets.Count));
        });
        return result;
    }

    /// <summary>長辺を size に収める縮小（面積平均）。乗算済みのまま平均するので縁がにじまない。</summary>
    public static Thumbnail Downscale(byte[] bgra, int w, int h, int size)
    {
        double scale = Math.Min(1.0, (double)size / Math.Max(w, h));
        int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
        var outBuf = new byte[tw * th * 4];
        for (int ty = 0; ty < th; ty++)
        {
            int y0 = ty * h / th, y1 = Math.Max(y0 + 1, (ty + 1) * h / th);
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = tx * w / tw, x1 = Math.Max(x0 + 1, (tx + 1) * w / tw);
                long b = 0, g = 0, r = 0, a = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        int o = (row + x) * 4;
                        b += bgra[o]; g += bgra[o + 1]; r += bgra[o + 2]; a += bgra[o + 3];
                        n++;
                    }
                }
                int d = (ty * tw + tx) * 4;
                outBuf[d] = (byte)(b / n); outBuf[d + 1] = (byte)(g / n); outBuf[d + 2] = (byte)(r / n); outBuf[d + 3] = (byte)(a / n);
            }
        }
        return new Thumbnail(tw, th, outBuf);
    }
}
