namespace NetDraw.Server;

/// <summary>
/// Bytewise replacement for the StringBuilder buffer the framer used before binary frames
/// existed. The framer has to peek raw bytes (binary payloads can include 0x0A freely and
/// must never reach a UTF-8 decoder), so the buffer holds bytes until a whole JSON line or
/// a whole binary frame is in hand.
/// </summary>
internal sealed class ByteFrameBuffer
{
    private byte[] _data = new byte[8192];
    private int _length;

    public int Length => _length;

    public byte this[int index] => _data[index];

    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(_length + bytes.Length);
        bytes.CopyTo(_data.AsSpan(_length));
        _length += bytes.Length;
    }

    public ReadOnlySpan<byte> AsSpan(int start, int count) => _data.AsSpan(start, count);

    public int IndexOf(byte target, int start)
    {
        var slice = _data.AsSpan(start, _length - start);
        int rel = slice.IndexOf(target);
        return rel < 0 ? -1 : start + rel;
    }

    public void Consume(int count)
    {
        if (count <= 0) return;
        if (count >= _length) { _length = 0; return; }
        Buffer.BlockCopy(_data, count, _data, 0, _length - count);
        _length -= count;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _data.Length) return;
        int newSize = _data.Length;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _data, newSize);
    }
}
