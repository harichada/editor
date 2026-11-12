namespace Editor.Core;

/// <summary>
/// Names of the feature switches. Every module reads <c>features.&lt;name&gt;</c>.
/// </summary>
public static class FeatureNames
{
    public const string FindReplace = "findReplace";
    public const string GoToLine = "goToLine";
    public const string StatusBar = "statusBar";
    public const string AtomicSave = "atomicSave";
    public const string EncodingDetection = "encodingDetection";

    public static IReadOnlyList<string> All { get; } =
    [
        FindReplace,
        GoToLine,
        StatusBar,
        AtomicSave,
        EncodingDetection
    ];

    public static bool IsKnown(string name) => All.Contains(name);
}

/// <summary>
/// Per-feature switches. Defaults are on. Each feature has a safe off-path:
/// the editor keeps working and the disabled feature becomes a no-op.
/// </summary>
public sealed record FeatureSettings
{
    public bool FindReplace { get; init; } = true;
    public bool GoToLine { get; init; } = true;
    public bool StatusBar { get; init; } = true;
    public bool AtomicSave { get; init; } = true;
    public bool EncodingDetection { get; init; } = true;

    public bool IsEnabled(string name) => name switch
    {
        FeatureNames.FindReplace => FindReplace,
        FeatureNames.GoToLine => GoToLine,
        FeatureNames.StatusBar => StatusBar,
        FeatureNames.AtomicSave => AtomicSave,
        FeatureNames.EncodingDetection => EncodingDetection,
        _ => throw new ArgumentException($"Unknown feature '{name}'.", nameof(name))
    };

    public FeatureSettings Set(string name, bool enabled) => name switch
    {
        FeatureNames.FindReplace => this with { FindReplace = enabled },
        FeatureNames.GoToLine => this with { GoToLine = enabled },
        FeatureNames.StatusBar => this with { StatusBar = enabled },
        FeatureNames.AtomicSave => this with { AtomicSave = enabled },
        FeatureNames.EncodingDetection => this with { EncodingDetection = enabled },
        _ => throw new ArgumentException($"Unknown feature '{name}'.", nameof(name))
    };
}
