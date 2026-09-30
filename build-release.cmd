@echo off
rem Сборка программы для клиента на Windows: dist\MuxTerminal.exe. Нужен .NET SDK 8.
rem Подпись (необязательно): set SIGN_PFX=C:\cert.pfx & set SIGN_PASSWORD=... перед запуском (нужен signtool из Windows SDK).
setlocal
cd /d "%~dp0"
dotnet build src\MuxTerminal.App -c Release || exit /b 1
if exist dist rmdir /s /q dist
mkdir dist
copy /y src\MuxTerminal.App\bin\Release\net48\MuxTerminal.exe dist\ >nul
copy /y src\MuxTerminal.App\bin\Release\net48\MuxTerminal.exe.config dist\ >nul
copy /y LICENSE dist\ >nul
copy /y THIRD-PARTY-NOTICES.txt dist\ >nul
if not "%SIGN_PFX%"=="" (
  signtool sign /f "%SIGN_PFX%" /p "%SIGN_PASSWORD%" /fd sha256 /tr http://timestamp.digicert.com /td sha256 /d "GSM 07.10 MUX Terminal" dist\MuxTerminal.exe || exit /b 1
)
echo Готово: dist\MuxTerminal.exe
