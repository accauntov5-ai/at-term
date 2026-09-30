#!/usr/bin/env bash
# Сборка программы для клиента: dist/MuxTerminal.exe (.NET Framework 4.8, Windows 7 SP1+), все библиотеки внутри.
# Нужен .NET SDK 8. На Windows — build-release.cmd.
#
# Подпись (необязательно): задайте сертификат для подписи кода и пароль —
#   SIGN_PFX=/путь/к/cert.pfx SIGN_PASSWORD=... ./build-release.sh
# Метка времени по умолчанию — http://timestamp.digicert.com (SIGN_TIMESTAMP_URL — другой сервер).
# Нужна утилита osslsigncode (sudo apt install osslsigncode).
set -euo pipefail
cd "$(dirname "$0")"
dotnet build src/MuxTerminal.App -c Release
rm -rf dist && mkdir -p dist
cp src/MuxTerminal.App/bin/Release/net48/MuxTerminal.exe dist/
cp src/MuxTerminal.App/bin/Release/net48/MuxTerminal.exe.config dist/
cp LICENSE THIRD-PARTY-NOTICES.txt dist/

if [[ -n "${SIGN_PFX:-}" ]]; then
  # Метка времени: подпись остаётся действительной и после истечения срока сертификата.
  # SIGN_TIMESTAMP_URL=none — без метки (только для проверки).
  TS=("-ts" "${SIGN_TIMESTAMP_URL:-http://timestamp.digicert.com}")
  [[ "${SIGN_TIMESTAMP_URL:-}" == "none" ]] && TS=()
  osslsigncode sign -pkcs12 "$SIGN_PFX" -pass "${SIGN_PASSWORD:-}" -h sha256 \
    -n "GSM 07.10 MUX Terminal" -i "https://github.com/accauntov5-ai/at-term" \
    "${TS[@]}" -in dist/MuxTerminal.exe -out dist/MuxTerminal.signed.exe
  mv dist/MuxTerminal.signed.exe dist/MuxTerminal.exe
  echo "Подписано: dist/MuxTerminal.exe"
fi
echo "Готово: dist/MuxTerminal.exe"
