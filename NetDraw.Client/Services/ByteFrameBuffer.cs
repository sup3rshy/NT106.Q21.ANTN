namespace NetDraw.Client.Services;

/// <summary>
/// Bytewise frame buffer for the client read-loop. Mirrors the server's buffer in
/// NetDraw.Server.ByteFrameBuffer. The client must not feed raw bytes into a UTF-8
/// Decoder anymore — once binary frames (magic 0xFE) start arriving, decoding them
/// as UTF-8 corrupts the decoder state for every JSON line that follows. The buffer
/// keeps bytes verbatim until a complete JSON line (newline-delimited) or a complete
/// binary frame (length-prefixed) is in hand, at which point UTF-8 decoding only
/// touches the JSON slice — never raw frames.
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

    public void Clear() => _length = 0;

    private void EnsureCapacity(int required)
    {
        if (required <= _data.Length) return;
        int newSize = _data.Length;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _data, newSize);
    }
}
