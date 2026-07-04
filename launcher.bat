@echo off
REM ============================================================
REM Neko Cpu Optimizer - .NET 8.0 Bootstrapper
REM ============================================================
setlocal enabledelayedexpansion

REM Check if .NET 8.0 is installed
"C:\Program Files\dotnet\dotnet.exe" --list-runtimes | findstr /i "Microsoft.WindowsDesktop.App 8.0" >nul 2>&1
if %errorlevel% equ 0 (
    REM .NET 8.0 found, run the app
    goto RUN_APP
)

REM .NET 8.0 not found, download and install
echo [*] .NET 8.0 not detected. Downloading...
echo.

REM Download .NET 8.0 SDK
set DOTNET_INSTALLER=%TEMP%\dotnet-sdk-8.0.exe
echo [*] Downloading .NET SDK 8.0...
powershell -Command "try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object System.Net.WebClient).DownloadFile('https://dot.net/v1/dotnet-install.ps1', '%TEMP%\dotnet-install.ps1'); Write-Host '[+] Downloaded dotnet-install.ps1' } catch { Write-Host '[-] Download failed'; exit 1 }" || goto FAIL

REM Run .NET installer
echo [*] Installing .NET 8.0...
powershell -ExecutionPolicy Bypass -File "%TEMP%\dotnet-install.ps1" -Channel 8.0 -InstallDir "C:\Program Files\dotnet" >nul 2>&1
if %errorlevel% neq 0 (
    echo [-] Installation failed. Trying winget...
    winget install --id Microsoft.DotNet.SDK.8 -e >nul 2>&1
    if %errorlevel% neq 0 goto FAIL
)

echo [+] .NET 8.0 installed successfully!
echo.

:RUN_APP
REM Run the optimizer
set OPTIMIZER_PATH=%~dp0bin\Release\net8.0-windows\optimizer.exe
if not exist "%OPTIMIZER_PATH%" (
    echo [-] optimizer.exe not found at: %OPTIMIZER_PATH%
    pause
    exit /b 1
)

echo [*] Starting Neko Cpu Optimizer v1.0...
"%OPTIMIZER_PATH%" %*
exit /b %errorlevel%

:FAIL
echo [-] Failed to install .NET 8.0
echo.
echo [i] Please install .NET 8.0 manually from:
echo     https://dotnet.microsoft.com/download/dotnet/8.0
echo.
pause
exit /b 1
