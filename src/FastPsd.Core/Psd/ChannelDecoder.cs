using System.IO.Compression;

namespace FastPsd.Core.Psd;

/// <summary>PSD のチャンネルデータ（RAW / RLE(PackBits) / ZIP / ZIP+予測）の展開</summary>
public static class ChannelDecoder
{
    /// <param name="data">圧縮方式の2バイトの後ろから始まるデータ</param>
    /// <param name="dst">width*height*bytesPerSample の出力先</param>
    /// <returns>予測の復元まで完了したか（32bitの予測付きZIPは未対応で false）</returns>
    public static bool Decode(ReadOnlySpan<byte> data, ushort compression, int width, int height, int bytesPerSample, Span<byte> dst)
    {
        int rowBytes = width * bytesPerSample;
        switch (compression)
        {
            case 0:
                {
                    int n = Math.Min(data.Length, dst.Length);
                    data[..n].CopyTo(dst);
                    return true;
                }
            case 1:
                DecodeRle(data, height, rowBytes, dst);
                return true;
            case 2:
            case 3:
                {
                    Inflate(data, dst);
                    if (compression == 3)
                    {
                        if (bytesPerSample == 1) UndoPrediction8(dst, width, height);
                        else if (bytesPerSample == 2) UndoPrediction16(dst, width, height);
                        else return false;
                    }
                    return true;
                }
            default:
                throw new InvalidDataException($"不明な圧縮方式 {compression}");
        }
    }

    static void DecodeRle(ReadOnlySpan<byte> data, int height, int rowBytes, Span<byte> dst)
    {
        int src = height * 2; // 各行の圧縮後バイト数（u16）の表を飛ばす
        for (int y = 0; y < height; y++)
        {
            int rowLen = (data[y * 2] << 8) | data[y * 2 + 1];
            var row = data.Slice(src, rowLen);
            var outRow = dst.Slice(y * rowBytes, rowBytes);
            PackBits(row, outRow);
            src += rowLen;
        }
    }

    /// <summary>PackBits の展開。ポインタで直接読み書きする（範囲外は読み書きしない）。</summary>
    static unsafe void PackBits(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        fixed (byte* ps = src, pd = dst)
        {
            byte* s = ps, se = ps + src.Length;
            byte* d = pd, de = pd + dst.Length;
            while (s < se && d < de)
            {
                int n = (sbyte)*s++;
                if (n >= 0)
                {
                    long cnt = Math.Min(n + 1, Math.Min(se - s, de - d));
                    Buffer.MemoryCopy(s, d, cnt, cnt);
                    s += n + 1;
                    d += cnt;
                }
                else if (n != -128)
                {
                    if (s >= se) break;
                    long cnt = Math.Min(1 - n, de - d);
                    byte v = *s++;
                    if (cnt <= 8) { for (long k = 0; k < cnt; k++) d[k] = v; }
                    else System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned(d, v, (uint)cnt);
                    d += cnt;
                }
            }
        }
    }

    static unsafe void Inflate(ReadOnlySpan<byte> data, Span<byte> dst)
    {
        fixed (byte* p = data)
        {
            using var ms = new UnmanagedMemoryStream(p, data.Length);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            int total = 0;
            while (total < dst.Length)
            {
                int n = z.Read(dst[total..]);
                if (n == 0) break;
                total += n;
            }
        }
    }

    static void UndoPrediction8(Span<byte> buf, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var row = buf.Slice(y * width, width);
            for (int x = 1; x < width; x++) row[x] += row[x - 1];
        }
    }

    static void UndoPrediction16(Span<byte> buf, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var row = buf.Slice(y * width * 2, width * 2);
            ushort prev = (ushort)((row[0] << 8) | row[1]);
            for (int x = 1; x < width; x++)
            {
                ushort v = (ushort)(((row[x * 2] << 8) | row[x * 2 + 1]) + prev);
                row[x * 2] = (byte)(v >> 8);
                row[x * 2 + 1] = (byte)v;
                prev = v;
            }
        }
    }
}
