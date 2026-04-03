@echo off
setlocal

set BUILD=%~dp0build

echo === HamDigiSharp Build ===
echo.

echo [1/3] Cleaning...
if exist "%BUILD%" rmdir /s /q "%BUILD%"
mkdir "%BUILD%\nuget"
mkdir "%BUILD%\demo"

echo [2/3] Packing NuGet...
dotnet pack "%~dp0HamDigiSharp\HamDigiSharp.csproj" ^
    -c Release ^
    -o "%BUILD%\nuget" ^
    --verbosity quiet
if errorlevel 1 goto :fail

echo [3/3] Publishing demo...
dotnet publish "%~dp0HamDigiSharp.Demo\HamDigiSharp.Demo.csproj" ^
    -c Release ^
    --self-contained false ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%BUILD%\demo" ^
    --verbosity quiet
if errorlevel 1 goto :fail

echo.
echo === Done ===
echo.
echo NuGet  : %BUILD%\nuget
for /f %%f in ('dir /b "%BUILD%\nuget\*.nupkg" 2^>nul') do echo   %%f
echo Demo   : %BUILD%\demo
for /f %%f in ('dir /b "%BUILD%\demo" 2^>nul') do echo   %%f
goto :eof

:fail
echo.
echo Build FAILED.
exit /b 1
