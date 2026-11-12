using System.Globalization;
using System.Runtime.InteropServices;

namespace Editor.Core;

/// <summary>
/// ANSI files use the operating system's ANSI code page.
/// Hosts that have no ANSI page (or report UTF-8 as the ANSI page) fall back to 1252
/// so a byte still maps to a single character.
/// </summary>
public static class SystemCodePage
{
    public static int Ansi
    {
        get
        {
            var page = OperatingSystem.IsWindows() ? WindowsAcp() : CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return page is > 0 and < 65001 ? page : 1252;
        }
    }

    private static int WindowsAcp()
    {
        try
        {
            return GetACP();
        }
        catch (DllNotFoundException)
        {
            return CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();
}
