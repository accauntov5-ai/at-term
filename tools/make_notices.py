"""Собирает THIRD-PARTY-NOTICES.txt из лицензий пакетов NuGet, которые встраиваются в MuxTerminal.exe.
Запуск после восстановления пакетов: python3 tools/make_notices.py"""
import hashlib
from pathlib import Path

NUGET = Path.home() / ".nuget" / "packages"
ROOT = Path(__file__).resolve().parent.parent

MIT = """Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE."""

# Библиотеки Microsoft (.NET), встраиваемые в exe для работы на .NET Framework 4.8.
MS_PACKAGES = [
    ("System.Memory", "system.memory/4.5.5"),
    ("System.Buffers", "system.buffers/4.5.1"),
    ("System.Numerics.Vectors", "system.numerics.vectors/4.5.0"),
    ("System.Runtime.CompilerServices.Unsafe", "system.runtime.compilerservices.unsafe/6.0.0"),
    ("System.Text.Json", "system.text.json/8.0.5"),
    ("System.Text.Encodings.Web", "system.text.encodings.web/8.0.0"),
    ("System.Threading.Channels", "system.threading.channels/8.0.0"),
    ("System.Threading.Tasks.Extensions", "system.threading.tasks.extensions/4.5.4"),
    ("Microsoft.Bcl.AsyncInterfaces", "microsoft.bcl.asyncinterfaces/8.0.0"),
    ("System.ValueTuple", "system.valuetuple/4.5.0"),
]

SEP = "=" * 78


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").strip()


def main() -> None:
    out = []
    out.append("GSM 07.10 MUX Terminal — сторонние компоненты / Third-party notices")
    out.append(SEP)
    out.append("""Программа распространяется по лицензии MIT (см. LICENSE). В MuxTerminal.exe встроены
следующие компоненты сторонних авторов; их лицензии приведены ниже.

This program is licensed under the MIT License (see LICENSE). MuxTerminal.exe embeds
the following third-party components; their license texts follow.

  Компонент / Component                     Версия     Лицензия / License
  AvalonDock (Dirkster99, Xceed)            4.72.1     Microsoft Public License (Ms-PL)
  AvalonDock.Themes.VS2013 (Dirkster99)     4.72.1     Microsoft Public License (Ms-PL)
  AvalonEdit (AlphaSierraPapa/SharpDevelop) 6.3.0.90   MIT
  Costura (Fody)                            6.0.0      MIT""")
    for name, pkg in MS_PACKAGES:
        version = pkg.split("/")[1]
        out.append(f"  {name:<41} {version:<10} MIT (Microsoft / .NET Foundation)")
    out.append("""
Microsoft .NET Framework 4.8 не входит в программу и не распространяется вместе с ней:
он является компонентом Windows либо устанавливается отдельно на условиях лицензии Microsoft.
Microsoft .NET Framework 4.8 is not included in or redistributed with this program; it is part
of Windows or is installed separately under the Microsoft license terms.
""")

    out += [SEP, "AvalonDock, AvalonDock.Themes.VS2013 — https://github.com/Dirkster99/AvalonDock", SEP,
            read(NUGET / "dirkster.avalondock/4.72.1/LICENSE"), ""]
    out += [SEP, "AvalonEdit — https://github.com/icsharpcode/AvalonEdit", SEP,
            "MIT License\n\nCopyright (c) 2000-2023 AlphaSierraPapa for the SharpDevelop Team\n\n" + MIT, ""]
    out += [SEP, "Costura — https://github.com/Fody/Costura", SEP,
            "The MIT License (MIT)\n\nCopyright (c) 2012 Simon Cropp and contributors\n\n" + MIT, ""]

    out += [SEP, "Microsoft .NET libraries — https://github.com/dotnet/runtime", SEP,
            "Applies to: " + ", ".join(n for n, _ in MS_PACKAGES) + "\n",
            read(NUGET / MS_PACKAGES[0][1] / "LICENSE.TXT"), ""]

    # Уведомления о коде третьих сторон внутри библиотек .NET (одинаковые файлы — один раз).
    seen = set()
    for name, pkg in MS_PACKAGES:
        text = read(NUGET / pkg / "THIRD-PARTY-NOTICES.TXT")
        digest = hashlib.sha1(text.encode()).hexdigest()
        if digest in seen:
            continue
        seen.add(digest)
        users = [n for n, p in MS_PACKAGES
                 if hashlib.sha1(read(NUGET / p / "THIRD-PARTY-NOTICES.TXT").encode()).hexdigest() == digest]
        out += [SEP, "Third-party notices of " + ", ".join(users), SEP, text, ""]

    (ROOT / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(out) + "\n", encoding="utf-8")
    print("written", ROOT / "THIRD-PARTY-NOTICES.txt")


if __name__ == "__main__":
    main()
