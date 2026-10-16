@echo off
chcp 65001 > nul
rem Локальная сборка одного WinTweaker.exe (нужен .NET 8 SDK)
cd /d "%~dp0"
dotnet publish WinTweaker.csproj -c Release -o publish || (echo. & echo Сборка не удалась & pause & exit /b 1)
echo.
echo Готово: %~dp0publish\WinTweaker.exe
pause
