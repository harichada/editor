using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace Editor.Core;

internal interface IByteSource : IDisposable
{
    long Length { get; }
    void CopyTo(long offset, Span<byte> destination);
}

internal sealed class EmptyByteSource : IByteSource
{
    public static EmptyByteSource Instance { get; } = new();
    public long Length => 0;
    public void CopyTo(long offset, Span<byte> destination)
    {
        if (destination.Length == 0 && offset == 0)
            return;
        throw new ArgumentOutOfRangeException(nameof(offset));
    }
    public void Dispose() { }
}

internal sealed class OwnedByteSource : IByteSource
{
    private readonly byte[] _data;
    public OwnedByteSource(byte[] data) => _data = data;
    public long Length => _data.LongLength;
    public void CopyTo(long offset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return;
        if (offset < 0 || offset + destination.Length > _data.LongLength)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _data.AsSpan((int)offset, destination.Length).CopyTo(destination);
    }
    public void Dispose() { }
}

/// <summary>
/// File-backed source. Pages are faulted only when read, so opening a large file
/// does not copy it into the managed heap.
/// </summary>
internal sealed class MappedByteSource : IByteSource
{
    private readonly FileStream _stream;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private bool _disposed;

    private MappedByteSource(FileStream stream, MemoryMappedFile map, MemoryMappedViewAccessor view, long length)
    {
        _stream = stream;
        _map = map;
        _view = view;
        Length = length;
    }

    public long Length { get; }

    public static MappedByteSource Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            var length = stream.Length;
            var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new MappedByteSource(stream, map, view, length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void CopyTo(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length == 0)
            return;
        if (offset < 0 || offset + destination.Length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var temp = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            var filled = 0;
            while (filled < destination.Length)
            {
                var n = Math.Min(destination.Length - filled, 64 * 1024);
                _view.ReadArray(offset + filled, temp, 0, n);
                temp.AsSpan(0, n).CopyTo(destination.Slice(filled, n));
                filled += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _view.Dispose();
        _map.Dispose();
        _stream.Dispose();
    }
}
