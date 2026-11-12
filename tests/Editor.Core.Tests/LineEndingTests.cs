namespace Editor.Core.Tests;

public class LineEndingTests
{
    [Theory]
    [InlineData("a\r\nb\r\n", LineEndingKind.Crlf, false)]
    [InlineData("a\nb\n", LineEndingKind.Lf, false)]
    [InlineData("a\rb\r", LineEndingKind.Cr, false)]
    [InlineData("a\nb\r\nc\rd", LineEndingKind.Crlf, true)]
    [InlineData("no-break", LineEndingKind.Crlf, false)]
    public void Detection_and_unmodified_bytes(string text, LineEndingKind preferred, bool mixed)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var source = TestFiles.NewFile(bytes);
        var dest = TestFiles.NewFile([]);
        using var document = TextDocument.Open(source);
        Assert.Equal(preferred, document.LineEndings.Preferred);
        Assert.Equal(mixed, document.LineEndings.IsMixed);
        Assert.Equal(text, document.GetText());
        document.Save(dest);
        Assert.Equal(TestFiles.Hash(bytes), TestFiles.HashFile(dest));
    }

    [Fact]
    public void Explicit_conversion_is_undoable_and_preserves_text()
    {
        var bytes = "a\r\nb\r\n"u8.ToArray();
        using var document = TextDocument.FromBytes(bytes);
        document.ConvertLineEndings(LineEndingKind.Lf);
        Assert.Equal("a\nb\n", document.GetText());
        Assert.Equal(LineEndingKind.Lf, document.LineEndings.Preferred);
        Assert.False(document.LineEndings.IsMixed);
        Assert.True(document.Undo());
        Assert.Equal("a\r\nb\r\n", document.GetText());
        Assert.Equal(LineEndingKind.Crlf, document.LineEndings.Preferred);
        Assert.False(document.IsModified);
        Assert.True(document.Redo());
        Assert.Equal("a\nb\n", document.GetText());
    }

    [Fact]
    public void Mixed_endings_survive_an_edit_until_conversion()
    {
        using var document = TextDocument.FromBytes("a\nb\r\n"u8.ToArray());
        Assert.True(document.LineEndings.IsMixed);
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        session.InsertText("Z");
        Assert.Equal("a\nb\r\nZ", document.GetText());
        document.ConvertLineEndings(LineEndingKind.Lf);
        Assert.Equal("a\nb\nZ", document.GetText());
        Assert.False(document.LineEndings.IsMixed);
    }

    [Fact]
    public void Enter_uses_preferred_ending()
    {
        using var document = TextDocument.FromBytes("a\nb"u8.ToArray());
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        session.InsertNewLine();
        Assert.Equal("a\nb\n", document.GetText());
    }
}
