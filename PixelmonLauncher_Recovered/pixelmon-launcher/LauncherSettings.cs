using System.IO;
using System.Text.Json;

namespace PixelmonLauncher;

internal sealed class LauncherSettings
{
    public string Nickname { get; set; } = "";
    public string DisplayVersion { get; set; } = "Mon-1.21.1";

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new LauncherSettings();
            }

            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path))
                   ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}
