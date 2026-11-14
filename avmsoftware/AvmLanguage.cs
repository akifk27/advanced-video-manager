using System.Globalization;
using Microsoft.Win32;

namespace avmsoftware;

internal static class AvmLanguage
{
    internal static string Current { get; } = ReadLanguage();

    private static string ReadLanguage()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\AVM");
            return settings?.GetValue("Language") as string == "en" ? "en" : "tr";
        }
        catch (UnauthorizedAccessException) { return "tr"; }
        catch (System.Security.SecurityException) { return "tr"; }
        catch (System.IO.IOException) { return "tr"; }
    }

    internal static void Save(string language)
    {
        if (language is not ("tr" or "en"))
            throw new ArgumentException(nameof(language));
        using var settings = Registry.CurrentUser.CreateSubKey(@"Software\AVM");
        settings.SetValue("Language", language, RegistryValueKind.String);
    }

    internal static string Text(string turkish, string english) => Current == "en" ? english : turkish;

    internal static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo(Text("tr-TR", "en-US"));
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

