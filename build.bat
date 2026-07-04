@echo off
setlocal ENABLEDELAYEDEXPANSION
where dotnet >nul 2>&1
if %ERRORLEVEL%==0 (
  echo Building with dotnet...
  dotnet build -c Release
  exit /b %ERRORLEVEL%
)
:: Try to find csc.exe in common Framework locations
set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.8\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.8\csc.exe"
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if exist "%WINDIR%\Microsoft.NET\Framework\v4.8\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.8\csc.exe"
if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if defined CSC (
  echo Found csc at %CSC%
  "%CSC%" optimizer.cs /target:exe /out:optimizer_cs.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll
  exit /b %ERRORLEVEL%
)

echo No dotnet SDK or csc.exe found. Please install the .NET SDK (recommended) or Visual Studio / Build Tools.
exit /b 1