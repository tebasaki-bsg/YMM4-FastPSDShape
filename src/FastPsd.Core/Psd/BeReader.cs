using System.Buffers.Binary;
using System.Text;

namespace FastPsd.Core.Psd;

/// <summary>ビッグエンディアンのバイナリリーダー（PSDは全てビッグエンディアン）</summary>
public sealed class BeReader : IDisposable
{
    /// <summary>日本語版Photoshopの旧形式レイヤー名（Pascal文字列）用</summary>
    public static readonly Encoding ShiftJis;

    static BeReader()
    {
        // フィールド初期化子は静的コンストラクタ本体より先に走るため、ここで登録してから取得する
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
    }

    readonly Stream _s;
    readonly bool _leaveOpen;
    readonly byte[] _b = new byte[8];

    public BeReader(Stream s, bool leaveOpen = false)
    {
        _s = s;
        _leaveOpen = leaveOpen;
    }

    public static BeReader FromBytes(byte[] data) => new(new MemoryStream(data, writable: false));

    public long Position { get => _s.Position; set => _s.Position = value; }
    public long Length => _s.Length;
    public long Remaining => _s.Length - _s.Position;

    void Fill(int n) => _s.ReadExactly(_b, 0, n);

    public byte U8()
    {
        int v = _s.ReadByte();
        if (v < 0) throw new EndOfStreamException();
        return (byte)v;
    }

    public ushort U16() { Fill(2); return BinaryPrimitives.ReadUInt16BigEndian(_b); }
    public short I16() { Fill(2); return BinaryPrimitives.ReadInt16BigEndian(_b); }
    public uint U32() { Fill(4); return BinaryPrimitives.ReadUInt32BigEndian(_b); }
    public int I32() { Fill(4); return BinaryPrimitives.ReadInt32BigEndian(_b); }
    public long I64() { Fill(8); return BinaryPrimitives.ReadInt64BigEndian(_b); }
    public double F64() { Fill(8); return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_b)); }

    public string Ascii4() { Fill(4); return Encoding.ASCII.GetString(_b, 0, 4); }

    public byte[] Bytes(int n)
    {
        var a = new byte[n];
        _s.ReadExactly(a);
        return a;
    }

    public void ReadInto(Span<byte> dst) => _s.ReadExactly(dst);

    public void Skip(long n)
    {
        if (n > 0) _s.Seek(n, SeekOrigin.Current);
    }

    /// <summary>長さ1バイト＋本体。全体の長さが pad の倍数になるまでパディング。</summary>
    public string PascalString(int pad)
    {
        int len = U8();
        var bytes = Bytes(len);
        int total = 1 + len;
        int rem = total % pad;
        if (rem != 0) Skip(pad - rem);
        return ShiftJis.GetString(bytes);
    }

    /// <summary>文字数(u32)＋UTF-16BE</summary>
    public string UnicodeString()
    {
        uint count = U32();
        if (count == 0) return "";
        if (count > 1_000_000) throw new InvalidDataException("Unicode文字列が長すぎます");
        var bytes = Bytes((int)count * 2);
        return Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (!_leaveOpen) _s.Dispose();
    }
}
