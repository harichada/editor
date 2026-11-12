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

        return EditorSettings.CreateDefault() with { AnsiCodePage = SystemCodePage.Ansi };
    }
}
