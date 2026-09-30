#!/usr/bin/env bash
# Сборка программы для клиента: один файл dist/MuxTerminal.exe (.NET Framework 4.8, Windows 7 SP1+).
# Нужен .NET SDK 8. На Windows то же самое: dotnet build src\MuxTerminal.App -c Release
set -euo pipefail
cd "$(dirname "$0")"
dotnet build src/MuxTerminal.App -c Release
rm -rf dist && mkdir -p dist
cp src/MuxTerminal.App/bin/Release/net48/MuxTerminal.exe dist/
cp src/MuxTerminal.App/bin/Release/net48/MuxTerminal.exe.config dist/
cp LICENSE THIRD-PARTY-NOTICES.txt dist/
echo "Готово: dist/MuxTerminal.exe"
