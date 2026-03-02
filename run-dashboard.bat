@echo off
cd /d "%~dp0dashboard"
if exist "node_modules\electron\dist\electron.exe" (
    "node_modules\electron\dist\electron.exe" .
) else (
    npx electron .
)
