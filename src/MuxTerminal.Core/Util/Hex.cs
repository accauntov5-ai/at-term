using System.Globalization;
using System.Text;

namespace MuxTerminal.Core.Util;

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data, int maxBytes = int.MaxValue)
    {
        int n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 3 + 4);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        if (n < data.Length)
            sb.Append(" …");
        return sb.ToString();
    }

    /// <summary>ASCII-представление: печатные символы как есть, \r \n явно, прочее — точкой.</summary>
    public static string ToPrintable(ReadOnlySpan<byte> data, int maxBytes = int.MaxValue)
    {
        int n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n + 8);
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            switch (b)
            {
                case 0x0D: sb.Append("\\r"); break;
                case 0x0A: sb.Append("\\n"); break;
                case >= 0x20 and < 0x7F: sb.Append((char)b); break;
                default: sb.Append('.'); break;
            }
        }
        if (n < data.Length)
            sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Разбирает строку вида "41 54 0D", "41540D" или "0x41,0x54".</summary>
    public static bool TryParse(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var digits = new StringBuilder();
        foreach (var token in text.Split(new[] { ' ', ',', ';', '\t', '\r', '\n', '-', ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (t.Length % 2 != 0)
                t = "0" + t;
            digits.Append(t);
        }
        if (digits.Length == 0)
            return false;
        try
        {
            bytes = Convert.FromHexString(digits.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
