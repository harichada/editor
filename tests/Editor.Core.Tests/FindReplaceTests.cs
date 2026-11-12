namespace Editor.Core.Tests;

public class FindReplaceTests
{
    [Theory]
    [MemberData(nameof(FeatureModes.All), MemberType = typeof(FeatureModes))]
    public void Find_and_replace_follow_the_feature_flag(EditorSettings settings)
    {
        using var document = TextDocument.FromText("Alpha alpha ALPHA", settings);
        var matches = document.Find("alpha", FindOptions.Plain(matchCase: false));
        var caseMatches = document.Find("alpha", FindOptions.Plain(matchCase: true));
        if (!settings.Features.FindReplace)
        {
            Assert.Empty(matches);
            Assert.Empty(caseMatches);
            Assert.Equal(0, document.ReplaceAll("alpha", "beta", FindOptions.Plain(matchCase: false)));
            Assert.Equal("Alpha alpha ALPHA", document.GetText());
            return;
        }

        Assert.Equal(3, matches.Count);
        Assert.Single(caseMatches);
        Assert.Equal(6, caseMatches[0].Start);
        var replaced = document.ReplaceAll("alpha", "beta", FindOptions.Plain(matchCase: true));
        Assert.Equal(1, replaced);
        Assert.Equal("Alpha beta ALPHA", document.GetText());
        Assert.True(document.Undo());
        Assert.Equal("Alpha alpha ALPHA", document.GetText());
    }

    [Fact]
    public void Replace_all_is_non_overlapping()
    {
        using var document = TextDocument.FromText("aaaa");
        var count = document.ReplaceAll("aa", "b", FindOptions.Plain(matchCase: true));
        Assert.Equal(2, count);
        Assert.Equal("bb", document.GetText());
    }

    [Fact]
    public void Empty_pattern_matches_nothing()
    {
        using var document = TextDocument.FromText("abc");
        Assert.Empty(document.Find("", FindOptions.Plain()));
    }

    [Fact]
    public void Regex_mode_is_rejected_until_m2()
    {
        var searcher = TextSearcherFactory.Create(new FindOptions(true, SearchMode.Regex));
        Assert.Equal("regex", searcher.Kind);
        Assert.Throws<NotSupportedException>(() => searcher.Find("a", "a", new FindOptions(true, SearchMode.Regex)));
    }

    [Fact]
    public void Find_spans_newlines_and_nuls()
    {
        using var document = TextDocument.FromText("a\0b\na\0b");
        var matches = document.Find("a\0b", FindOptions.Plain(matchCase: true));
        Assert.Equal(2, matches.Count);
    }
}
