namespace Editor.Core.Tests;

public class UndoAndEditTests
{
    [Fact]
    public void Undo_redo_across_paste_and_line_ending_conversion()
    {
        using var document = TextDocument.FromBytes("a\r\nb\r\n"u8.ToArray());
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        session.InsertText("PASTE");
        Assert.Equal("a\r\nb\r\nPASTE", document.GetText());
        document.ConvertLineEndings(LineEndingKind.Lf);
        Assert.Equal("a\nb\nPASTE", document.GetText());

        Assert.True(session.Undo());
        Assert.Equal("a\r\nb\r\nPASTE", document.GetText());
        Assert.True(session.Undo());
        Assert.Equal("a\r\nb\r\n", document.GetText());
        Assert.False(document.IsModified);
        Assert.False(session.Undo());

        Assert.True(session.Redo());
        Assert.Equal("a\r\nb\r\nPASTE", document.GetText());
        Assert.True(session.Redo());
        Assert.Equal("a\nb\nPASTE", document.GetText());
    }

    [Fact]
    public void Paste_over_selection_is_one_undo()
    {
        using var document = TextDocument.FromText("abcdef");
        var session = new EditingSession(document);
        session.SetCaret(0, 2, extend: false);
        session.SetCaret(0, 5, extend: true);
        Assert.Equal("cde", session.GetSelectedText());
        session.InsertText("XYZ");
        Assert.Equal("abXYZf", document.GetText());
        session.Undo();
        Assert.Equal("abcdef", document.GetText());
    }

    [Fact]
    public void Backspace_deletes_one_grapheme_and_joins_lines()
    {
        using var document = TextDocument.FromText("a😀\nb");
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        session.Move(CaretMove.Up, false);
        session.Move(CaretMove.LineEnd, false);
        session.Backspace();
        Assert.Equal("a\nb", document.GetText());
        session.Move(CaretMove.DocumentStart, false);
        session.Move(CaretMove.Down, false);
        session.Backspace();
        Assert.Equal("ab", document.GetText());
    }

    [Fact]
    public void Random_ascii_edits_match_a_string_model()
    {
        var random = new Random(42);
        var model = "hello\nworld\n";
        using var document = TextDocument.FromText(model);
        for (var step = 0; step < 200; step++)
        {
            var at = random.Next(model.Length + 1);
            if (random.Next(3) == 0 && model.Length > 0)
            {
                var count = random.Next(1, Math.Min(4, model.Length - at + 1));
                model = model.Remove(at, count);
                document.DeleteUtf16(at, count);
            }
            else
            {
                var insert = random.Next(4) switch
                {
                    0 => "X",
                    1 => "\n",
                    2 => "\r\n",
                    _ => "yz"
                };
                model = model.Insert(at, insert);
                document.InsertAtUtf16(at, insert);
            }
            Assert.Equal(model, document.GetText());
        }

        while (document.Undo())
        {
        }
        Assert.Equal("hello\nworld\n", document.GetText());
        Assert.False(document.IsModified);
    }

    [Fact]
    public void Insert_does_not_split_a_multibyte_character()
    {
        using var document = TextDocument.FromText("éé");
        var middle = document.ByteLength / 2;
        document.InsertAtByteOffset(middle, "X");
        var text = document.GetText();
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Contains("X", text);
        Assert.Equal(3, new System.Globalization.StringInfo(text).LengthInTextElements);
    }
}
