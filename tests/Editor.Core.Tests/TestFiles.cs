using System.Security.Cryptography;

namespace Editor.Core.Tests;

internal static class TestFiles
{
    public static string NewFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-tests", Guid.NewGuid().ToString("n") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static string HashFile(string path) => Hash(File.ReadAllBytes(path));
}

public static class FeatureModes
{
    public static IEnumerable<object[]> All()
    {
        yield return [EditorSettings.AllEnabled()];
        foreach (var name in FeatureNames.All)
            yield return [EditorSettings.WithFeature(name, false)];
    }
}
