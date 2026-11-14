using System.IO;
using Microsoft.Win32;

namespace avmsoftware;

internal static class MediaTools
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVM", "tools-folder.txt");

    internal static string? ExternalFolder
    {
        get
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string folder = File.ReadAllText(SettingsPath).Trim();
                    if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
                        return folder;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    internal static bool HasTools(string folder) =>
        new[] { "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe", "deno.exe" }
            .All(name => File.Exists(Path.Combine(folder, name)));

    internal static void Save(string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, folder);
    }
}
