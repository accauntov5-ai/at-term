using System.IO;
using System.Text.Json;

namespace MuxTerminal.App.Services;

/// <summary>Настройки подключения, сохраняются в %AppData%\MuxTerminal\settings.json.</summary>
public sealed class AppSettings
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public bool HardwareFlowControl { get; set; }
    public bool Dtr { get; set; } = true;
    public string CmuxCommand { get; set; } = "AT+CMUX=0";
    public string Channels { get; set; } = "1,2,3";
    public bool SkipCmux { get; set; }
    /// <summary>Оформление вывода, правила подсветки и «Копилки».</summary>
    public DisplaySettings Display { get; set; } = DisplaySettings.CreateDefault();

    public Dictionary<int, string> ChannelNames { get; set; } = new()
    {
        [1] = "AT",
        [2] = "Data/SMS",
        [3] = "GPS/NMEA",
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuxTerminal", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                settings.Display ??= DisplaySettings.CreateDefault();
                settings.Display.Normalize();
                return settings;
            }
        }
        catch
        {
            // Повреждённый файл настроек — начинаем с умолчаний.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Настройки — не критично.
        }
    }
}
