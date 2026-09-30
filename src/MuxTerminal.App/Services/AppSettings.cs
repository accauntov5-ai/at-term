using System.IO;
using System.Text.Json;

namespace MuxTerminal.App.Services;

/// <summary>Профиль подключения: порт, скорость, команда MUX и каналы для конкретного модема.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public bool HardwareFlowControl { get; set; }
    public bool Dtr { get; set; } = true;
    public string CmuxCommand { get; set; } = "AT+CMUX=0";
    public string Channels { get; set; } = "1,2,3";
    public bool SkipCmux { get; set; }

    public override string ToString() => Name;
}

/// <summary>Положение и размер главного окна.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Настройки программы, сохраняются в %AppData%\MuxTerminal\settings.json.</summary>
public sealed class AppSettings
{
    // Текущие параметры подключения (последние использованные).
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public bool HardwareFlowControl { get; set; }
    public bool Dtr { get; set; } = true;
    public string CmuxCommand { get; set; } = "AT+CMUX=0";
    public string Channels { get; set; } = "1,2,3";
    public bool SkipCmux { get; set; }

    /// <summary>Переподключаться автоматически, если связь с модемом потеряна.</summary>
    public bool AutoReconnect { get; set; }

    /// <summary>Писать журнал сеанса в %AppData%\MuxTerminal\logs.</summary>
    public bool SessionLog { get; set; } = true;

    /// <summary>Писать в журнал сеанса и сырые MUX-кадры (объёмно).</summary>
    public bool SessionLogRawFrames { get; set; }

    public List<ConnectionProfile> Profiles { get; set; } = new();
    public string? LastProfile { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Оформление вывода, правила подсветки, «Копилка» и быстрые команды.</summary>
    public DisplaySettings Display { get; set; } = DisplaySettings.CreateDefault();

    public Dictionary<int, string> ChannelNames { get; set; } = new()
    {
        [1] = "AT",
        [2] = "Data/SMS",
        [3] = "GPS/NMEA",
    };

    private static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    public ConnectionProfile ToProfile(string name) => new()
    {
        Name = name,
        PortName = PortName,
        BaudRate = BaudRate,
        HardwareFlowControl = HardwareFlowControl,
        Dtr = Dtr,
        CmuxCommand = CmuxCommand,
        Channels = Channels,
        SkipCmux = SkipCmux,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                settings.Display ??= DisplaySettings.CreateDefault();
                settings.Display.Normalize();
                settings.Profiles ??= new List<ConnectionProfile>();
                settings.ChannelNames ??= new Dictionary<int, string>();
                return settings;
            }
        }
        catch (Exception ex)
        {
            // Повреждённый файл настроек — начинаем с умолчаний, старый файл сохраняем рядом.
            CrashLog.Write(ex, "settings.json");
            try
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch
            {
                // Не удалось — не страшно.
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            // Сначала во временный файл: при сбое посреди записи старые настройки не теряются.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "settings.json (save)");
        }
    }
}
