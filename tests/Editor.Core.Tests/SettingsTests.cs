namespace Editor.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Json_uses_features_object_names()
    {
        const string json = """
            {
              "features": {
                "findReplace": false,
                "goToLine": false,
                "statusBar": true,
                "atomicSave": false,
                "encodingDetection": false
              },
              "ansiCodePage": 1252
            }
            """;
        var settings = EditorSettings.Parse(json);
        Assert.False(settings.Features.FindReplace);
        Assert.False(settings.Features.GoToLine);
        Assert.True(settings.Features.StatusBar);
        Assert.False(settings.Features.AtomicSave);
        Assert.False(settings.Features.EncodingDetection);
        Assert.Equal(1252, settings.AnsiCodePage);

        var roundTrip = EditorSettings.Parse(settings.ToJson());
        Assert.Equal(settings.Features, roundTrip.Features);
    }

    [Fact]
    public void Unknown_feature_names_are_ignored()
    {
        var settings = EditorSettings.Parse("""{ "features": { "regex": false, "findReplace": false } }""");
        Assert.False(settings.Features.FindReplace);
        Assert.True(settings.Features.GoToLine);
    }

    [Fact]
    public void Environment_disable_list_changes_the_default()
    {
        var previous = Environment.GetEnvironmentVariable(EditorSettings.DisableEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(EditorSettings.DisableEnvironmentVariable, "statusBar, atomicSave");
            var settings = EditorSettings.CreateDefault();
            Assert.False(settings.Features.StatusBar);
            Assert.False(settings.Features.AtomicSave);
            Assert.True(settings.Features.FindReplace);
            Assert.True(settings.Features.GoToLine);
            Assert.True(settings.Features.EncodingDetection);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EditorSettings.DisableEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void CreateDefault_is_the_settings_object_a_document_reads()
    {
        var settings = EditorSettings.CreateDefault();
        using var document = TextDocument.FromText("alpha beta alpha", settings);
        var matches = document.Find("alpha", FindOptions.Plain(matchCase: true));
        if (settings.Features.FindReplace)
            Assert.NotEmpty(matches);
        else
            Assert.Empty(matches);

        var moved = document.TryGoToLine(1, out _);
        Assert.Equal(settings.Features.GoToLine, moved);
        Assert.Equal(settings.Features.StatusBar, document.TryGetStatus(0, 0) is not null);
    }

    [Fact]
    public void Invalid_json_is_rejected()
    {
        Assert.Throws<FormatException>(() => EditorSettings.Parse("{"));
    }
}
