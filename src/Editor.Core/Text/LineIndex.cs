namespace Editor.Core;

/// <summary>
/// Lazy line-start index. Open and first-page layout only scan the bytes they display.
/// Edits before the scanned frontier invalidate the index; edits past it leave the prefix intact.
/// </summary>
internal sealed class LineIndex
{
    private readonly List<long> _starts = [0];
    private long _scannedTo;
    private bool _complete;

    public long ScannedTo => _scannedTo;
    public bool IsComplete => _complete;
    public int StartCount => _starts.Count;

    public int CompleteLineCount(long documentLength)
    {
        if (_complete && _scannedTo >= documentLength)
            return Math.Max(1, _starts.Count);
        return Math.Max(0, _starts.Count - 1);
    }

    public long LineStart(int line) => _starts[line];

    public long LineBoundary(int line, long documentLength)
    {
        if (line + 1 < _starts.Count)
            return _starts[line + 1];
        return _complete ? documentLength : _scannedTo;
    }

    public void Reset()
    {
        _starts.Clear();
        _starts.Add(0);
        _scannedTo = 0;
        _complete = false;
    }

    public void NoteInsert(long at)
    {
        if (at >= _scannedTo)
        {
            if (_complete)
                _complete = false;
            return;
        }
        Reset();
    }

    public void NoteDelete(long at)
    {
        if (at >= _scannedTo)
            return;
        Reset();
    }

    public void Ensure(int completeLines, long documentLength, long scanLimit, LineBreakFinder find)
    {
        if (completeLines < 1)
            completeLines = 1;
        if (documentLength < 0)
            documentLength = 0;

        var guard = 0;
        while (CompleteLineCount(documentLength) < completeLines && !_complete && guard++ < 8_000_000)
        {
            if (_scannedTo >= documentLength)
            {
                _scannedTo = documentLength;
                _complete = true;
                break;
            }

            if (scanLimit < documentLength && _scannedTo >= scanLimit)
                break;

            var searchEnd = scanLimit < documentLength ? Math.Min(documentLength, scanLimit) : documentLength;
            if (!find(_scannedTo, searchEnd, out var lineBreak) || lineBreak.Start < _scannedTo)
            {
                if (searchEnd >= documentLength)
                {
                    _scannedTo = documentLength;
                    _complete = true;
                }
                break;
            }

            var next = lineBreak.Start + lineBreak.ByteLength;
            if (next <= _scannedTo)
                break;
            _starts.Add(next);
            _scannedTo = next;
            if (_scannedTo >= documentLength)
            {
                _complete = true;
                break;
            }
        }
    }
}

internal delegate bool LineBreakFinder(long from, long end, out LineBreak found);
