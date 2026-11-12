namespace Editor.Core.Tests;

public class GraphemeAndNavigationTests
{
    [Fact]
    public void Clusters_cover_zwj_combining_marks_and_surrogate_pairs()
    {
        Assert.Equal(3, GraphemeNavigator.ClusterCount("a😀b"));
        Assert.Equal(1, GraphemeNavigator.ClusterCount("👨‍👩‍👧‍👦"));
        Assert.Equal(2, GraphemeNavigator.ClusterCount("e\u0301x"));
        Assert.Equal(1, GraphemeNavigator.ClusterCount("🇺🇸"));
        Assert.Equal(1, GraphemeNavigator.ClusterCount("👍🏻"));

        Assert.Equal(3, GraphemeNavigator.Next("a😀b", 1));
        Assert.Equal(1, GraphemeNavigator.Previous("a😀b", 3));
        Assert.Equal("👨‍👩‍👧‍👦".Length, GraphemeNavigator.Next("👨‍👩‍👧‍👦", 0));
        Assert.Equal(0, GraphemeNavigator.Previous("👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦".Length));
        Assert.Equal(2, GraphemeNavigator.Next("e\u0301x", 0));
    }

    [Fact]
    public void Caret_moves_by_grapheme_and_not_by_code_unit()
    {
        using var document = TextDocument.FromText("a😀b");
        var session = new EditingSession(document);
        session.Move(CaretMove.Right, false);
        Assert.Equal(1, session.CaretColumn);
        session.Move(CaretMove.Right, false);
        Assert.Equal(3, session.CaretColumn);
        session.Move(CaretMove.Left, false);
        Assert.Equal(1, session.CaretColumn);
        session.Move(CaretMove.LineEnd, false);
        session.Move(CaretMove.Left, false);
        Assert.Equal(3, session.CaretColumn);
    }

    [Fact]
    public void Horizontal_movement_does_not_split_a_line_break()
    {
        using var document = TextDocument.FromText("a\r\nb");
        var session = new EditingSession(document);
        session.Move(CaretMove.Right, false);
        session.Move(CaretMove.Right, false);
        Assert.Equal(1, session.CaretLine);
        Assert.Equal(0, session.CaretColumn);
        session.Move(CaretMove.Left, false);
        Assert.Equal(0, session.CaretLine);
        Assert.Equal(1, session.CaretColumn);
    }

    [Fact]
    public void Word_and_vertical_movement()
    {
        using var document = TextDocument.FromText("one two\nalpha");
        var session = new EditingSession(document);
        session.Move(CaretMove.WordRight, false);
        Assert.Equal(4, session.CaretColumn);
        session.Move(CaretMove.LineEnd, false);
        session.Move(CaretMove.WordLeft, false);
        Assert.Equal(4, session.CaretColumn);
        session.Move(CaretMove.DocumentEnd, false);
        session.Move(CaretMove.Up, false);
        Assert.Equal(0, session.CaretLine);
    }

    [Fact]
    public void Jumps_in_a_large_file_do_not_scan_from_the_start()
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-jump-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wide.txt");
        var line = System.Text.Encoding.UTF8.GetBytes(new string('a', 79) + "\n");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var i = 0; i < 30_000; i++)
                stream.Write(line);
        }

        using var document = TextDocument.Open(path);
        var scanned = document.ScannedBytes;
        var end = document.JumpToEnd();
        var far = document.JumpToLine(20_000);
        var fraction = document.JumpToFraction(0.9);
        Assert.Equal(scanned, document.ScannedBytes);
        Assert.False(document.IsLineIndexComplete);
        Assert.False(end.Exact);
        Assert.True(end.BytesTouched < 64 * 1024);
        Assert.True(far.BytesTouched < 64 * 1024);
        Assert.True(fraction.BytesTouched < 64 * 1024);
        Assert.Equal(document.ByteLength, end.CaretByte);
        Assert.True(end.LineStart >= document.ByteLength - line.Length);
        Assert.True(end.Preview.Length == 0 || end.Preview.StartsWith("a"));

        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        Assert.True(session.HasRemoteCaret);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(session.GoToLine(18_000));
        Assert.False(document.IsLineIndexComplete);
    }

    [Theory]
    [MemberData(nameof(FeatureModes.All), MemberType = typeof(FeatureModes))]
    public void Go_to_line_and_status_follow_flags(EditorSettings settings)
    {
        using var document = TextDocument.FromText("a\nbb\nccc", settings);
        var enabled = document.TryGoToLine(2, out var line);
        var status = document.TryGetStatus(1, 1);
        if (!settings.Features.GoToLine)
            Assert.False(enabled);
        else
        {
            Assert.True(enabled);
            Assert.Equal(1, line);
        }

        if (!settings.Features.StatusBar)
            Assert.Null(status);
        else
        {
            Assert.NotNull(status);
            Assert.Equal(2, status.Value.Line);
            Assert.Equal(2, status.Value.Column);
            Assert.Contains("UTF-8", status.Value.Encoding);
        }
    }

    [Fact]
    public void Go_to_line_clamps_past_the_end()
    {
        using var document = TextDocument.FromText("a\nb");
        Assert.True(document.TryGoToLine(50, out var line));
        Assert.Equal(1, line);
        Assert.True(document.TryGoToLine(1, out line));
        Assert.Equal(0, line);
    }

    [Fact]
    public void Tab_display_column_uses_stops_of_eight()
    {
        Assert.Equal(8, DisplayColumn.Get("\t", 1));
        Assert.Equal(9, DisplayColumn.Get("\ta", 2));
        Assert.Equal(1, DisplayColumn.Utf16At("\ta", 8));
    }
}
