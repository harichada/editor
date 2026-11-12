namespace Editor.Core.Tests;

public class AtomicSaveTests
{
    [Theory]
    [MemberData(nameof(FeatureModes.All), MemberType = typeof(FeatureModes))]
    public void Successful_save_leaves_no_temp_and_writes_bytes(EditorSettings settings)
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-atomic-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        using var document = TextDocument.FromText("hello", settings);
        document.Save(path);
        Assert.Equal("hello"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void Readonly_target_leaves_original_intact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-ro-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        File.WriteAllBytes(path, "original"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var document = TextDocument.FromText("changed");
            var error = Assert.ThrowsAny<IOException>(() => document.Save(path));
            Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Locked_target_leaves_original_intact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-lock-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        File.WriteAllBytes(path, "original"u8.ToArray());
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var document = TextDocument.FromText("changed");
        Assert.ThrowsAny<IOException>(() => document.Save(path));
        locked.Dispose();
        Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Direct_write_still_refuses_a_readonly_target()
    {
        var settings = EditorSettings.WithFeature(FeatureNames.AtomicSave, false);
        var directory = Path.Combine(Path.GetTempPath(), "editor-direct-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        File.WriteAllBytes(path, "original"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var document = TextDocument.FromText("changed", settings);
            Assert.ThrowsAny<IOException>(() => document.Save(path));
            Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Mapped_edit_saves_without_keeping_the_previous_undo_chain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-map-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "large.txt");
        var size = (int)TextDocument.MapThreshold + 4096;
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 80 == 79 ? '\n' : 'a');
        File.WriteAllBytes(path, bytes);

        using (var document = TextDocument.Open(path))
        {
            Assert.True(document.ByteLength >= TextDocument.MapThreshold);
            var session = new EditingSession(document);
            session.InsertText("Z");
            document.Save(path);
            Assert.False(document.CanUndo);
            Assert.StartsWith("Z", document.GetLineContent(0));
        }

        var saved = File.ReadAllBytes(path);
        Assert.Equal((byte)'Z', saved[0]);
        Assert.True(saved.Length > size / 2);
    }

    [Fact]
    public void Opened_file_round_trips_after_undo_to_the_original_bytes()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x00 };
        var source = TestFiles.NewFile(bytes);
        using var document = TextDocument.Open(source);
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        session.InsertText("Z");
        session.Undo();
        var dest = TestFiles.NewFile([]);
        document.Save(dest);
        Assert.Equal(TestFiles.Hash(bytes), TestFiles.HashFile(dest));
    }
}
