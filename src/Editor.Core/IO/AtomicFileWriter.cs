namespace Editor.Core;

/// <summary>
/// Writes a file without truncating the destination until the new bytes are complete.
/// When atomic is false the bytes are written in place after an
/// access check, so a read-only or locked target still fails before truncation.
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content, bool atomic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var copy = content.ToArray();
        Write(full, atomic, stream => stream.Write(copy));
    }

    public static void Write(string path, bool atomic, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        EnsureReplaceable(full);

        if (!atomic)
        {
            using var direct = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
            write(direct);
            direct.Flush(flushToDisk: true);
            return;
        }

        var temp = Path.Combine(directory ?? ".", "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("n") + ".tmp");
        try
        {
            using (var staged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                write(staged);
                staged.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
                File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, full);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void EnsureReplaceable(string full)
    {
        if (!File.Exists(full))
            return;

        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new IOException($"Cannot save. The file is read-only: {full}");

        try
        {
            using var probe = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException($"Cannot save. The file is locked: {full}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Cannot save. Access is denied: {full}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
