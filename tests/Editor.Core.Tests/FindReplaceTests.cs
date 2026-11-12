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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void Find_and_replace_agree_across_window_boundaries(int window)
    {
        using var document = TextDocument.FromText("aaaXaaaXaaa\nzzXaXa");
        var full = document.Find("aXa", FindOptions.Plain(matchCase: true), default, 10_000);
        var streamed = document.Find("aXa", FindOptions.Plain(matchCase: true), default, window);
        Assert.Equal(full, streamed);
        Assert.NotEmpty(streamed);

        var count = document.ReplaceAll("aXa", "Q", FindOptions.Plain(matchCase: true), default, window);
        Assert.Equal(full.Count, count);
        Assert.Equal("aaQaQaa\nzzXQ", document.GetText());
        Assert.True(document.Undo());
        Assert.Equal("aaaXaaaXaaa\nzzXaXa", document.GetText());
    }

    [Fact]
    public void Find_next_wraps_to_the_first_match()
    {
        using var document = TextDocument.FromText("abXab");
        var options = FindOptions.Plain(matchCase: true);
        Assert.Equal(3, document.FindNext("ab", options, 2, default, 2)!.Value.Start);
        Assert.Equal(0, document.FindNext("ab", options, 4, default, 2)!.Value.Start);
    }

    [Fact]
    public void Cancelled_find_throws_instead_of_returning_a_partial_list()
    {
        using var document = TextDocument.FromText("abcdefghij");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            document.Find("ab", FindOptions.Plain(matchCase: true), cancelled.Token, 4));

        using var during = new CancellationTokenSource();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() =>
            StreamingTextSearch.FindAll(
                (_, _) =>
                {
                    if (++calls == 2)
                        during.Cancel();
                    return new StreamingTextSearch.DecodedWindow("abcd", 4);
                },
                byteLength: 64,
                pattern: "ab",
                comparison: StringComparison.Ordinal,
                windowChars: 4,
                during.Token));
    }

    [Fact]
    public void Regex_on_the_document_is_rejected()
    {
        using var document = TextDocument.FromText("abc");
        Assert.Throws<NotSupportedException>(() =>
            document.Find("a", new FindOptions(true, SearchMode.Regex)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Multibyte_character_split_by_a_chunk_is_found_once(int windowBytes)
    {
        using var document = TextDocument.FromText("aaéXé");
        var matches = document.Find("é", FindOptions.Plain(matchCase: true), default, windowBytes);
        Assert.Equal(2, matches.Count);
        Assert.Equal(2, matches[0].Start);
        Assert.Equal(4, matches[1].Start);
        Assert.All(matches, match => Assert.Equal("é".Length, match.Length));

        var straddling = document.Find("aéX", FindOptions.Plain(matchCase: true), default, windowBytes);
        Assert.Single(straddling);
        Assert.Equal(1, straddling[0].Start);
        Assert.Equal("aéX".Length, straddling[0].Length);
    }

    [Fact]
    public void Find_and_replace_reach_past_the_materialize_limit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "editor-find-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wide.txt");
        const string marker = "MARKER";
        var size = TextDocument.MaterializeLimit + 64 * 1024;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)'b');
            var left = size - marker.Length;
            while (left > 0)
            {
                var count = (int)Math.Min(block.Length, left);
                stream.Write(block, 0, count);
                left -= count;
            }
            stream.Write(System.Text.Encoding.ASCII.GetBytes(marker));
        }

        using var document = TextDocument.Open(path);
        Assert.True(document.ByteLength > TextDocument.MaterializeLimit);
        Assert.Throws<NotSupportedException>(() => document.GetText());

        var matches = document.Find(marker, FindOptions.Plain(matchCase: true));
        Assert.Single(matches);
        Assert.Equal((int)(size - marker.Length), matches[0].Start);

        var replaced = document.ReplaceAll(marker, "OK", FindOptions.Plain(matchCase: true));
        Assert.Equal(1, replaced);
        Assert.Empty(document.Find(marker, FindOptions.Plain(matchCase: true)));
        var found = document.Find("OK", FindOptions.Plain(matchCase: true));
        Assert.Single(found);
        Assert.Equal((int)(size - marker.Length), found[0].Start);

        Assert.True(document.Undo());
        var restored = document.Find(marker, FindOptions.Plain(matchCase: true));
        Assert.Single(restored);
        Assert.Equal((int)(size - marker.Length), restored[0].Start);
    }
}
