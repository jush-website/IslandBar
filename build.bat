@echo off
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [X] .NET SDK not found. Please install .NET 8 SDK:
  echo     https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

taskkill /im IslandBar.exe /f >nul 2>nul

echo === Building IslandBar.exe (first build downloads packages, please wait) ===
dotnet publish src\IslandBar\IslandBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 goto :fail

echo === Building MSI installer ===
dotnet build installer\IslandBar.Installer.wixproj -c Release -o dist
if errorlevel 1 goto :fail

echo.
echo [OK] Done
echo      Installer : %~dp0dist\IslandBar-Setup.msi
echo      Portable  : %~dp0publish\IslandBar.exe
pause
exit /b 0

:fail
echo.
echo [X] Build failed. Copy the error messages above to Claude.
pause
exit /b 1
