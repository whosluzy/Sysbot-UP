@echo off
title FusionBot Builder
cd /d "%~dp0"

echo.
echo ========================================
echo  Pulling latest updates from upstream...
echo ========================================
git pull upstream main
if errorlevel 1 (
    echo.
    echo ERROR: git pull failed. Check your internet connection or resolve any conflicts.
    pause
    exit /b 1
)

echo.
echo ========================================
echo  Building FusionBot exe...
echo ========================================
dotnet publish SysBot.Pokemon.WinForms/SysBot.Pokemon.WinForms.csproj -c Release
if errorlevel 1 (
    echo.
    echo ERROR: Build failed. See errors above.
    pause
    exit /b 1
)

echo.
echo ========================================
echo  Done! Opening output folder...
echo ========================================
explorer "SysBot.Pokemon.WinForms\bin\Release\net10.0-windows\win-x64\publish"
echo.
echo Your exe is in the folder that just opened.
echo Copy it to wherever you run the bot from.
echo.
pause
