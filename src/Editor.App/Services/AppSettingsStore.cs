using Editor.Core;

namespace Editor.App.Services;

internal static class AppSettingsStore
{
    public static EditorSettings Load()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Editor",
                "settings.json");
            if (File.Exists(path))
                return EditorSettings.Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (FormatException)
        {
        }

        var settings = EditorSettings.CreateDefault();
        if (OperatingSystem.IsWindows())
        {
            var page = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            if (page is > 0 and < 65001)
                settings = settings with { AnsiCodePage = page };
        }
        return settings;
    }
}
