@echo off
title Polymarket Bot (LIVE)

:: Enable ANSI colors in Windows console
reg add HKCU\Console /v VirtualTerminalLevel /t REG_DWORD /d 1 /f >nul 2>&1

:: Config is read from polymarket_bot_config.json in this directory.
:: Edit that file to change any settings (API keys, risk params, email, etc.)
set CONFIG_FILE=%~dp0polymarket_bot_config.json

cd /d "%~dp0dotnet\PolymarketBot"
dotnet run -- --console

pause
