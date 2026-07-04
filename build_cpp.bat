@echo off
where cl >nul 2>&1
if %ERRORLEVEL%==0 (
  echo Building with cl (MSVC)...
  cl /EHsc /O2 optimizer.cpp /Fe:optimizer_cpp.exe
  exit /b %ERRORLEVEL%
)
where g++ >nul 2>&1
if %ERRORLEVEL%==0 (
  echo Building with g++...
  g++ -O2 optimizer.cpp -o optimizer_cpp.exe
  exit /b %ERRORLEVEL%
)
echo No supported C++ compiler found. Install Visual Studio Build Tools or MinGW and ensure `cl` or `g++` is on PATH.
exit /b 1