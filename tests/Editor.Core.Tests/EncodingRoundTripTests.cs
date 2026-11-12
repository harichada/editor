using System.Text;

namespace Editor.Core.Tests;

public class EncodingRoundTripTests
{
    public static IEnumerable<object[]> Samples()
    {
        yield return ["empty", Array.Empty<byte>()];
        yield return ["utf8-no-newline", "hello"u8.ToArray()];
        yield return ["utf8-nul", new byte[] { 0x61, 0x00, 0x62 }];
        yield return ["utf8-bom", new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }];
        yield return ["utf8-bom-only", new byte[] { 0xEF, 0xBB, 0xBF }];
        yield return ["utf16-le-nul", new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x00, 0x00, 0x42, 0x00 }];
        yield return ["utf16-le-bom-only", new byte[] { 0xFF, 0xFE }];
        yield return ["utf16-be-nul", new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x00, 0x00, 0x00, 0x42 }];
        yield return ["ansi-1252", new byte[] { 0x48, 0x69, 0x20, 0x93 }];
        yield return ["ansi-nul", new byte[] { 0x41, 0x00, 0x93 }];
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Unmodified_save_matches_sha256(string name, byte[] original)
    {
        _ = name;
        var source = TestFiles.NewFile(original);
        var dest = TestFiles.NewFile([]);
        using (var document = TextDocument.Open(source))
        {
            document.Save(dest);
        }
        Assert.Equal(TestFiles.Hash(original), TestFiles.HashFile(dest));
    }

    [Fact]
    public void Utf16_le_preserves_nul_in_text()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0x41, 0x00, 0x00, 0x00, 0x42, 0x00 };
        using var document = TextDocument.FromBytes(bytes);
        Assert.Equal(TextEncodingKind.Utf16Le, document.Encoding.Kind);
        Assert.Equal("A\0B", document.GetText());
    }

    [Fact]
    public void Utf16_be_round_trips_text()
    {
        var bytes = new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x00, 0x00, 0x00, 0x42 };
        using var document = TextDocument.FromBytes(bytes);
        Assert.Equal(TextEncodingKind.Utf16Be, document.Encoding.Kind);
        Assert.Equal("A\0B", document.GetText());
    }

    [Fact]
    public void Ansi_invalid_utf8_is_detected_and_edited_save_uses_code_page()
    {
        var bytes = new byte[] { 0x93 };
        using var document = TextDocument.FromBytes(bytes, new EditorSettings { AnsiCodePage = 1252 });
        Assert.Equal(TextEncodingKind.Ansi, document.Encoding.Kind);
        Assert.Equal("\u201C", document.GetText());
        var session = new EditingSession(document);
        session.Move(CaretMove.DocumentEnd, extend: false);
        session.InsertText("A");
        var dest = TestFiles.NewFile([]);
        document.Save(dest);
        var saved = File.ReadAllBytes(dest);
        Assert.Equal(new byte[] { 0x93, 0x41 }, saved);
    }

    [Fact]
    public void Utf8_bom_edit_keeps_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 };
        using var document = TextDocument.FromBytes(bytes);
        Assert.Equal("hi", document.GetText());
        new EditingSession(document).InsertText("!");
        var dest = TestFiles.NewFile([]);
        document.Save(dest);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x21, 0x68, 0x69 }, File.ReadAllBytes(dest));
    }

    [Theory]
    [MemberData(nameof(FeatureModes.All), MemberType = typeof(FeatureModes))]
    public void Detection_flag_off_still_preserves_bytes(EditorSettings settings)
    {
        var bytes = new byte[] { 0x93, 0x00, 0x41 };
        var source = TestFiles.NewFile(bytes);
        var dest = TestFiles.NewFile([]);
        using var document = TextDocument.Open(source, settings);
        if (settings.Features.EncodingDetection)
            Assert.Equal(TextEncodingKind.Ansi, document.Encoding.Kind);
        else
            Assert.Equal(TextEncodingKind.Utf8, document.Encoding.Kind);
        document.Save(dest);
        Assert.Equal(TestFiles.Hash(bytes), TestFiles.HashFile(dest));
    }

    [Fact]
    public void Bom_is_honored_when_detection_is_off()
    {
        var settings = EditorSettings.WithFeature(FeatureNames.EncodingDetection, false);
        var bytes = new byte[] { 0xFF, 0xFE, 0x41, 0x00 };
        using var document = TextDocument.FromBytes(bytes, settings);
        Assert.Equal(TextEncodingKind.Utf16Le, document.Encoding.Kind);
        Assert.Equal("A", document.GetText());
    }

    [Fact]
    public void Provisional_guess_keeps_unedited_bytes_when_the_rest_of_the_file_is_not_utf8()
    {
        var bytes = new byte[(64 * 1024) + 8];
        Array.Fill(bytes, (byte)'a');
        bytes[^3] = 0x93;
        bytes[^2] = (byte)'b';
        bytes[^1] = (byte)'c';
        var source = TestFiles.NewFile(bytes);
        var dest = TestFiles.NewFile([]);
        using var document = TextDocument.Open(source, new EditorSettings { AnsiCodePage = 1252 });
        Assert.Equal(TextEncodingKind.Utf8, document.Encoding.Kind);
        Assert.True(document.Encoding.IsProvisional);
        document.InsertAtUtf16(0, "Z");
        document.Save(dest);
        var saved = File.ReadAllBytes(dest);
        Assert.Equal((byte)'Z', saved[0]);
        Assert.Contains((byte)0x93, saved);
        Assert.Equal((byte)'b', saved[^2]);
        Assert.Equal(-1, IndexOf(saved, [0xEF, 0xBF, 0xBD]));
    }

    [Fact]
    public void Provisional_utf8_keeps_a_multibyte_character_past_the_sample()
    {
        var bytes = new byte[(64 * 1024) + 4];
        Array.Fill(bytes, (byte)'a');
        bytes[^2] = 0xC3;
        bytes[^1] = 0xA9;
        var source = TestFiles.NewFile(bytes);
        var dest = TestFiles.NewFile([]);
        using var document = TextDocument.Open(source);
        Assert.True(document.Encoding.IsProvisional);
        document.InsertAtUtf16(0, "Q");
        document.Save(dest);
        var saved = File.ReadAllBytes(dest);
        Assert.Equal((byte)'Q', saved[0]);
        Assert.Equal((byte)0xC3, saved[^2]);
        Assert.Equal((byte)0xA9, saved[^1]);
    }

    [Fact]
    public void Save_as_encoding_transcodes_the_whole_document()
    {
        using var document = TextDocument.FromText("hi", new EditorSettings { AnsiCodePage = 1252 });
        document.SetEncoding(TextEncodingKind.Utf16Le, bom: true);
        Assert.True(document.IsModified);
        var dest = TestFiles.NewFile([]);
        document.Save(dest);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x68, 0x00, 0x69, 0x00 }, File.ReadAllBytes(dest));
        Assert.False(document.IsModified);
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    [Fact]
    public void No_trailing_newline_is_one_line()
    {
        using var document = TextDocument.FromBytes("hello"u8.ToArray());
        document.EnsureLines(int.MaxValue);
        Assert.Equal(1, document.CompleteLineCount);
        Assert.Equal("hello", document.GetLineContent(0));
        Assert.Equal(LineEndingKind.None, document.GetLineEnding(0));
    }
}
