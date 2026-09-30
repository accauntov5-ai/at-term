using System.IO.Ports;

namespace MuxTerminal.Core.Transport;

public sealed class SerialPortSettings
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 115200;
    public Handshake Handshake { get; set; } = Handshake.None;
    /// <summary>Большинству модемов нужен активный DTR, иначе они не принимают команды.</summary>
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;
}

/// <summary>Монопольно открытый COM-порт. Работаем через BaseStream (асинхронный ввод-вывод).</summary>
public sealed class SerialPortTransport : IMuxTransport
{
    private readonly SerialPort _port;

    private SerialPortTransport(SerialPort port)
    {
        _port = port;
        Stream = port.BaseStream;
    }

    public string Name => $"{_port.PortName} @ {_port.BaudRate}";
    public Stream Stream { get; }

    public static SerialPortTransport Open(SerialPortSettings settings)
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = settings.Handshake,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 16 * 1024,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        port.Open(); // бросает UnauthorizedAccessException, если порт занят другим приложением
        // Handshake RequestToSend управляет RTS сам — ручная установка запрещена.
        if (settings.Handshake is Handshake.None or Handshake.XOnXOff)
            port.RtsEnable = settings.RtsEnable;
        port.DtrEnable = settings.DtrEnable;
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return new SerialPortTransport(port);
    }

    public static string[] GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct().OrderBy(PortSortKey).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int PortSortKey(string name)
        => int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_port.IsOpen)
            {
                _port.DtrEnable = false;
                _port.Close(); // прерывает ожидающий ReadAsync
            }
        }
        catch
        {
            // Порт мог быть физически отключён (USB-модем) — закрываем как можем.
        }
        _port.Dispose();
        return ValueTask.CompletedTask;
    }
}
