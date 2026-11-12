using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.Core;

/// <summary>
/// Single settings model. Feature modules read <see cref="Features"/> only;
/// they do not keep private copies of the switches.
/// </summary>
public sealed record EditorSettings
{
    public const string DisableEnvironmentVariable = "EDITOR_DISABLE_FEATURES";

    public FeatureSettings Features { get; init; } = new();

    /// <summary>
    /// Code page used when a file is detected as ANSI. 1252 is the default so
    /// detection is stable on hosts whose system ANSI page is UTF-8.
    /// The Windows shell substitutes the active code page when it is a real ANSI page.
    /// </summary>
    public int AnsiCodePage { get; init; } = 1252;

    /// <summary>Line ending inserted by Enter when the file has no breaks yet.</summary>
    public LineEndingKind DefaultLineEnding { get; init; } = LineEndingKind.Crlf;

    /// <summary>All features on. Ignores the environment.</summary>
    public static EditorSettings AllEnabled() => new();

    public static EditorSettings WithFeature(string name, bool enabled) =>
        new() { Features = new FeatureSettings().Set(name, enabled) };

    /// <summary>
    /// Defaults, then turns features off listed in <see cref="DisableEnvironmentVariable"/>.
    /// Use <c>none</c> or an empty value for all on, <c>*</c> for all off,
    /// or a comma-separated list of feature names.
    /// </summary>
    public static EditorSettings CreateDefault()
    {
        var settings = new EditorSettings();
        var raw = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            return settings;

        if (raw.Trim() == "*")
        {
            foreach (var name in FeatureNames.All)
                settings = settings with { Features = settings.Features.Set(name, false) };
            return settings;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FeatureNames.IsKnown(part))
                continue;
            settings = settings with { Features = settings.Features.Set(part, false) };
        }

        return settings;
    }

    public static EditorSettings Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Settings JSON is not valid.", ex);
        }

        if (root is not JsonObject obj)
            throw new FormatException("Settings JSON must be an object.");

        var settings = new EditorSettings();
        if (obj["features"] is JsonObject features)
        {
            foreach (var name in FeatureNames.All)
            {
                if (features[name] is JsonValue value && value.TryGetValue<bool>(out var enabled))
                    settings = settings with { Features = settings.Features.Set(name, enabled) };
            }
        }

        if (obj["ansiCodePage"] is JsonValue pageValue && pageValue.TryGetValue<int>(out var page) && page > 0)
            settings = settings with { AnsiCodePage = page };

        if (obj["defaultLineEnding"] is JsonValue endingValue && endingValue.TryGetValue<string>(out var endingText)
            && TryParseLineEnding(endingText, out var ending))
        {
            settings = settings with { DefaultLineEnding = ending };
        }

        return settings;
    }

    public string ToJson()
    {
        var features = new JsonObject();
        foreach (var name in FeatureNames.All)
            features[name] = Features.IsEnabled(name);

        var root = new JsonObject
        {
            ["features"] = features,
            ["ansiCodePage"] = AnsiCodePage,
            ["defaultLineEnding"] = DefaultLineEnding switch
            {
                LineEndingKind.Lf => "lf",
                LineEndingKind.Cr => "cr",
                _ => "crlf"
            }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryParseLineEnding(string text, out LineEndingKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "crlf":
                kind = LineEndingKind.Crlf;
                return true;
            case "lf":
                kind = LineEndingKind.Lf;
                return true;
            case "cr":
                kind = LineEndingKind.Cr;
                return true;
            default:
                kind = LineEndingKind.Crlf;
                return false;
        }
    }
}
