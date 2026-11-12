namespace Editor.Core;

internal readonly record struct Piece(PieceSource Source, long Start, long Length, BufferEncoding Encoding)
{
    public Piece Slice(long offset, long length)
    {
        if ((ulong)offset > (ulong)Length || (ulong)length > (ulong)(Length - offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new Piece(Source, Start + offset, length, Encoding);
    }
}

internal readonly record struct PieceSpan(Piece Piece, long DocumentOffset, long SourceOffset, long Length);

/// <summary>
/// Piece table over an immutable original buffer and an append-only add buffer.
/// A prefix-sum index makes locate logarithmic in the piece count, which stays
/// tiny for a single insert into an unmodified file.
/// </summary>
internal sealed class PieceTable
{
    private readonly List<Piece> _pieces = new();
    private readonly List<long> _ends = new();

    public long Length => _ends.Count == 0 ? 0 : _ends[^1];
    public int PieceCount => _pieces.Count;

    public void Clear()
    {
        _pieces.Clear();
        _ends.Clear();
    }

    public void Load(ReadOnlySpan<Piece> pieces)
    {
        _pieces.Clear();
        foreach (var piece in pieces)
        {
            if (piece.Length > 0)
                _pieces.Add(piece);
        }
        RebuildEnds();
    }

    public Piece[] ToArray() => _pieces.ToArray();

    public void RetagOriginal(BufferEncoding encoding)
    {
        for (var i = 0; i < _pieces.Count; i++)
        {
            if (_pieces[i].Source == PieceSource.Original)
                _pieces[i] = _pieces[i] with { Encoding = encoding };
        }
    }

    public void Insert(long offset, Piece piece)
    {
        if (piece.Length == 0)
            return;
        if (offset < 0 || offset > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (_pieces.Count == 0)
        {
            _pieces.Add(piece);
            RebuildEnds();
            return;
        }

        if (offset == Length)
        {
            _pieces.Add(piece);
            RebuildEnds();
            return;
        }

        var (index, into) = Locate(offset);
        if (into == 0)
        {
            _pieces.Insert(index, piece);
        }
        else
        {
            var current = _pieces[index];
            var left = current.Slice(0, into);
            var right = current.Slice(into, current.Length - into);
            _pieces[index] = left;
            _pieces.Insert(index + 1, piece);
            _pieces.Insert(index + 2, right);
        }
        RebuildEnds();
    }

    public void Delete(long offset, long length)
    {
        if (length == 0)
            return;
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var end = offset + length;
        var (startIndex, startInto) = Locate(offset);
        int endIndex;
        long endInto;
        if (end == Length)
        {
            endIndex = _pieces.Count - 1;
            endInto = _pieces[^1].Length;
        }
        else
        {
            (endIndex, endInto) = Locate(end);
            if (endInto == 0)
            {
                endIndex--;
                endInto = _pieces[endIndex].Length;
            }
        }

        if (startIndex == endIndex)
        {
            var piece = _pieces[startIndex];
            var leftLength = startInto;
            var rightStart = endInto;
            if (leftLength == 0 && rightStart == piece.Length)
            {
                _pieces.RemoveAt(startIndex);
            }
            else if (leftLength == 0)
            {
                _pieces[startIndex] = piece.Slice(rightStart, piece.Length - rightStart);
            }
            else if (rightStart == piece.Length)
            {
                _pieces[startIndex] = piece.Slice(0, leftLength);
            }
            else
            {
                _pieces[startIndex] = piece.Slice(0, leftLength);
                _pieces.Insert(startIndex + 1, piece.Slice(rightStart, piece.Length - rightStart));
            }
        }
        else
        {
            var startPiece = _pieces[startIndex];
            var endPiece = _pieces[endIndex];
            _pieces[startIndex] = startInto == 0
                ? startPiece.Slice(0, 0)
                : startPiece.Slice(0, startInto);
            _pieces[endIndex] = endInto == endPiece.Length
                ? endPiece.Slice(0, 0)
                : endPiece.Slice(endInto, endPiece.Length - endInto);
            for (var i = startIndex + 1; i < endIndex; i++)
                _pieces[i] = _pieces[i].Slice(0, 0);
            _pieces.RemoveAll(static piece => piece.Length == 0);
        }

        RebuildEnds();
    }

    public void CopyTo(long offset, Span<byte> destination, IByteSource original, AddBuffer add)
    {
        if (destination.Length == 0)
            return;
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var (index, into) = Locate(offset);
        var filled = 0;
        while (filled < destination.Length)
        {
            var piece = _pieces[index];
            var count = (int)Math.Min(piece.Length - into, destination.Length - filled);
            var slice = destination.Slice(filled, count);
            if (piece.Source == PieceSource.Original)
                original.CopyTo(piece.Start + into, slice);
            else
                add.CopyTo(piece.Start + into, slice);
            filled += count;
            index++;
            into = 0;
        }
    }

    public bool TryGetSpan(long offset, out PieceSpan span)
    {
        if (offset < 0 || offset >= Length || _pieces.Count == 0)
        {
            span = default;
            return false;
        }

        var (index, into) = Locate(offset);
        var piece = _pieces[index];
        span = new PieceSpan(piece, offset, piece.Start + into, piece.Length - into);
        return true;
    }

    private (int Index, long Into) Locate(long offset)
    {
        var lo = 0;
        var hi = _pieces.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var start = mid == 0 ? 0 : _ends[mid - 1];
            var end = _ends[mid];
            if (offset < start)
                hi = mid - 1;
            else if (offset >= end)
                lo = mid + 1;
            else
                return (mid, offset - start);
        }

        throw new InvalidOperationException("Piece table locate failed.");
    }

    private void RebuildEnds()
    {
        _ends.Clear();
        long end = 0;
        foreach (var piece in _pieces)
        {
            end += piece.Length;
            _ends.Add(end);
        }
    }
}

internal sealed class AddBuffer
{
    private byte[] _data = new byte[256];
    public long Length { get; private set; }

    public long Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return Length;
        var start = Length;
        Ensure(Length + bytes.Length);
        bytes.CopyTo(_data.AsSpan((int)Length, bytes.Length));
        Length += bytes.Length;
        return start;
    }

    public void Truncate(long length)
    {
        if (length < 0 || length > Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
    }

    public void CopyTo(long offset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return;
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _data.AsSpan((int)offset, destination.Length).CopyTo(destination);
    }

    private void Ensure(long needed)
    {
        if (needed <= _data.Length)
            return;
        if (needed > int.MaxValue)
            throw new NotSupportedException("The add buffer cannot exceed 2 GB in this version.");
        var size = Math.Max(needed, (long)_data.Length * 2);
        if (size > int.MaxValue)
            size = int.MaxValue;
        var next = new byte[(int)size];
        _data.AsSpan(0, (int)Length).CopyTo(next);
        _data = next;
    }
}
