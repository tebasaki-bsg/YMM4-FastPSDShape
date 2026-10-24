using Microsoft.Win32.SafeHandles;

namespace FastPsd.Core.Psd;

/// <summary>
/// ファイルハンドルを閉じずに使える読み取り専用ストリーム（RandomAccess で位置を指定して読む）。
/// FileStream は Dispose でハンドルを閉じてしまうため、開いたままにしたいハンドルにはこちらを使う。
/// </summary>
internal sealed class RandomAccessStream(SafeFileHandle handle) : Stream
{
    long _pos;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => RandomAccess.GetLength(handle);
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = RandomAccess.Read(handle, buffer, _pos);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => Length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
