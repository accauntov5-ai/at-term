using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace MuxTerminal.App.Services;

/// <summary>Сведения о программе и системе (из атрибутов сборки, заданных в Directory.Build.props).</summary>
public static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    public static string Product => Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "GSM 07.10 MUX Terminal";

    public static string Version
    {
        get
        {
            var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
                return informational!.Split('+')[0];
            return Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string Description => Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";

    public static string Copyright => Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public static string RepositoryUrl => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "";

    public static string Title => $"{Product} {Version}";

    /// <summary>Текст встроенного ресурса (LICENSE, THIRD-PARTY-NOTICES.txt).</summary>
    public static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return $"(ресурс {name} не найден)";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string WindowsVersion
    {
        get
        {
            var v = Environment.OSVersion.Version;
            string name = (v.Major, v.Minor) switch
            {
                (6, 1) => "Windows 7",
                (6, 2) => "Windows 8",
                (6, 3) => "Windows 8.1",
                (10, 0) when v.Build >= 22000 => "Windows 11",
                (10, 0) => "Windows 10",
                _ => "Windows",
            };
            string sp = string.IsNullOrEmpty(Environment.OSVersion.ServicePack) ? "" : " " + Environment.OSVersion.ServicePack;
            return $"{name}{sp} ({v}, {(Environment.Is64BitOperatingSystem ? "64" : "32")}-бит)";
        }
    }

    /// <summary>Установленная версия .NET Framework 4.x (по ключу Release в реестре).</summary>
    public static string FrameworkVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                if (key?.GetValue("Release") is int release)
                {
                    string version = release switch
                    {
                        >= 533320 => "4.8.1",
                        >= 528040 => "4.8",
                        >= 461808 => "4.7.2",
                        >= 461308 => "4.7.1",
                        >= 460798 => "4.7",
                        _ => "4.6.x или старее",
                    };
                    return $".NET Framework {version} (CLR {Environment.Version})";
                }
            }
            catch
            {
                // Нет доступа к реестру — покажем только версию CLR.
            }
            return $"CLR {Environment.Version}";
        }
    }

    /// <summary>Сведения для сообщения о проблеме.</summary>
    public static string Diagnostics =>
        $"{Title}\r\n{WindowsVersion}\r\n{FrameworkVersion}\r\nПроцесс: {(Environment.Is64BitProcess ? "64" : "32")}-бит\r\n";
}
