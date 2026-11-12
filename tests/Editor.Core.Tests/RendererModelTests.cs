namespace Editor.Core.Tests;

public class RendererModelTests
{
    [Fact]
    public void First_page_does_not_index_the_rest_of_the_file()
    {
        var text = string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"line-{i:0000}-padding"));
        using var document = TextDocument.FromText(text);
        var page = document.LayoutFirstPage(30, 80);
        Assert.Equal(30, page.Lines.Count);
        Assert.Equal("line-0000-padding", page.Lines[0].Text);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(document.ScannedBytes < document.ByteLength / 2);
    }

    [Fact]
    public void Long_line_layout_returns_only_the_visible_cells()
    {
        var line = new string('a', 50) + "😀" + new string('b', 5000);
        var cells = MonospaceLayout.VisibleCells(line, firstColumn: 48, columnCount: 6);
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.True(cell.Column < 54));
        Assert.Contains(cells, cell => cell.Text == "😀");
        Assert.True(cells.Count < 20);
    }

    [Fact]
    public void Viewport_plan_asks_only_for_visible_lines()
    {
        var plan = ViewportLayout.Plan(scrollLine: 100, viewportLines: 40, completeLines: 10, indexComplete: false, estimatedTotal: 100_000);
        Assert.Equal(100, plan.FirstLine);
        Assert.Equal(40, plan.LineCount);
        Assert.Equal(100_000, plan.EstimatedTotalLines);
    }

    [Fact]
    public void Text_pattern_reads_selection_caret_and_grapheme_movement()
    {
        using var document = TextDocument.FromText("a😀\ncd");
        var session = new EditingSession(document);
        session.Move(CaretMove.Right, false);
        session.Move(CaretMove.Right, true);
        var pattern = new TextPatternModel(session);
        Assert.Equal("a😀\ncd", pattern.GetText(0, -1));
        var selection = pattern.GetSelection();
        Assert.Equal("😀", pattern.GetText(selection.Start, selection.End - selection.Start));
        Assert.Equal(selection.End, pattern.GetCaret());
        var moved = pattern.Move(1, PatternUnit.Character, 1);
        Assert.Equal(1 + "😀".Length, moved);
        var visible = pattern.GetVisibleRange(0, 1);
        Assert.Equal("a😀", pattern.GetText(visible.Start, visible.End - visible.Start));
    }

    [Fact]
    public void Composition_update_complete_and_cancel()
    {
        using var document = TextDocument.FromText("go");
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, false);
        var input = new TextInputModel(session);
        input.BeginComposition();
        input.UpdateComposition("ni");
        Assert.True(input.IsComposing);
        Assert.Equal("goni", document.GetText());
        input.UpdateComposition("你");
        input.CompleteComposition();
        Assert.False(input.IsComposing);
        Assert.Equal("go你", document.GetText());
        Assert.True(document.Undo());
        Assert.Equal("go", document.GetText());
        Assert.True(document.Redo());

        input.BeginComposition();
        input.UpdateComposition("x");
        input.CancelComposition();
        Assert.Equal("go你", document.GetText());
        Assert.False(input.IsComposing);
    }

    [Fact]
    public void Emoji_panel_commit_inserts_one_cluster()
    {
        using var document = TextDocument.FromText("");
        var session = new EditingSession(document);
        var input = new TextInputModel(session);
        input.ApplyTextUpdate(0, 0, "👨‍👩‍👧‍👦");
        Assert.Equal("👨‍👩‍👧‍👦", document.GetText());
        Assert.Equal(1, GraphemeNavigator.ClusterCount(document.GetText()));
        Assert.Equal(document.GetText().Length, session.CaretColumn);
        input.ApplyTextUpdate(session.CaretUtf16, session.CaretUtf16, "👍");
        Assert.Equal(2, GraphemeNavigator.ClusterCount(document.GetText()));
    }

    [Fact]
    public void Hit_test_snaps_to_the_emoji_boundary()
    {
        var line = "a😀b";
        Assert.Equal(0, MonospaceLayout.HitTest(line, pixelX: 0.2, charWidth: 1));
        Assert.Equal(1, MonospaceLayout.HitTest(line, pixelX: 1.0, charWidth: 1));
        Assert.Equal(3, MonospaceLayout.HitTest(line, pixelX: 2.0, charWidth: 1));
        Assert.NotEqual(2, MonospaceLayout.HitTest(line, pixelX: 1.4, charWidth: 1));
    }
}
