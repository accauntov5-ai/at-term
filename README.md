# GSM 07.10 MUX Terminal

**English** | [Русский](README.ru.md)

A Windows terminal for cellular modems that implements the **GSM 07.10 / 3GPP TS 27.010 multiplexer** (Basic Option)
itself — no drivers — and shows every logical channel (DLC) in its own tab over a single COM port.

![icon](src/MuxTerminal.App/Assets/app.png)

## Download

**[Latest release → MuxTerminal.exe](https://github.com/accauntov5-ai/at-term/releases/latest)** — a single file, no installation.

Requires **Windows 7 SP1 or later** with **.NET Framework 4.8**
(built into Windows 10 1903+ and 11; for Windows 7/8.1 install `ndp48-x86-x64-allos-enu.exe` from Microsoft).

## Quick start

The interface is in Russian; button names are given in brackets.

1. Pick the modem's COM port and baud rate (or **«Эмулятор модема»**, the modem emulator, to try it without hardware).
2. **Open port** («Открыть порт») — a plain AT terminal on the «COM-порт» tab (check the modem: `AT`, `ATI`, `AT+CPIN?`).
3. **Start MUX** («Старт MUX») — the modem switches to `AT+CMUX`, channels DLC 1–3 open in their own tabs.
4. **Stop MUX** («Стоп MUX») — back to the plain AT terminal; **Close port** («Закрыть порт») — done.

## Features

- Multiplexer: frame parsing with FCS and resync, flow control, MSC, N1 splitting, proper shutdown (DISC + CLD),
  recovery of a modem left in MUX mode, auto-reconnect.
- Windows: drag tabs to split the window or float them into separate windows; the layout is remembered.
- Terminal: text/HEX, timestamps, command history, quick-command buttons and sequences, send file.
- Highlighting: configurable colors and monospace fonts per text type; search rules that highlight matches
  and collect important lines (errors, calls, new SMS) into the **Collector** («Копилка») tab.
- Session logs and crash log in `%AppData%\MuxTerminal`.
- Built-in modem emulator (AT commands, SMS, NMEA stream, binary data, reboot).

## Build

.NET SDK 8 (on Windows or Linux):

```
./build-release.sh        # Linux / macOS → dist/MuxTerminal.exe
build-release.cmd         # Windows
dotnet test tests/MuxTerminal.Core.Tests
start /wait MuxTerminal.exe --selftest   # on the target PC (cmd): exit code 0 = works (see selftest.log)
```

More: [technical details, spec notes, test coverage](docs/DETAILS.md) (Russian) ·
[acceptance checklist](docs/TESTING.md) (Russian).

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)
(AvalonDock — Ms-PL; AvalonEdit, Costura, Microsoft .NET libraries — MIT).
