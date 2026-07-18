@echo off
setlocal enableextensions
rem Running from a network share (e.g. a VM shared folder) leaves cmd without
rem a valid working directory; pushd maps one to a temporary drive letter.
pushd "%~dp0" >nul 2>nul
title Build

rem Builds the app (named by shared/app-spec.json) with the .NET SDK and, on
rem the first run, downloads the PDFium engine (pdfium.dll) it depends on.

set "ROOT=%~dp0"
set "OUT=%ROOT%build"
set "PDFIUM=%ROOT%lib\pdfium.dll"

echo.
echo Building...
echo.

rem --- Check for the .NET SDK (version 10 or later) ---
where dotnet >nul 2>nul
if errorlevel 1 goto :no_sdk
dotnet --list-sdks | findstr /r /b /c:"1[0-9]\." >nul
if errorlevel 1 goto :no_sdk

rem --- Ensure pdfium.dll is present (downloaded once, then reused) ---
if not exist "%PDFIUM%" (
    echo pdfium.dll was not found. Downloading a prebuilt copy...
    call :fetch_pdfium
    if errorlevel 1 (
        echo.
        echo Could not download pdfium.dll automatically.
        echo Download "pdfium-win-x64.tgz" from
        echo   https://github.com/bblanchon/pdfium-binaries/releases/latest
        echo and copy its bin\pdfium.dll into:
        echo   "%ROOT%lib"
        echo Then run this builder again.
        echo.
        if not defined CI pause
        exit /b 1
    )
    echo pdfium.dll ready.
    echo.
)

rem --- Build ---
dotnet publish "%ROOT%App.csproj" -c Release -o "%OUT%" -p:DebugType=None
if errorlevel 1 (
    echo.
    echo Build failed.
    echo.
    if not defined CI pause
    exit /b 1
)

rem --- Remove intermediate build files; build\ holds the finished app ---
rd /s /q "%ROOT%bin" >nul 2>nul
rd /s /q "%ROOT%obj" >nul 2>nul

rem The exe is named from the spec; find it rather than hardcoding the name.
set "EXE="
for %%F in ("%OUT%\*.exe") do set "EXE=%%F"

echo.
echo Built successfully:
echo   "%EXE%"
echo.
echo Everything is packed into that single exe. Move it anywhere, pin it to
echo the Start menu or taskbar, drag PDF files onto it, or open it and drop
echo PDFs into the window.
echo.
echo Note: running the app on a machine without the .NET Desktop Runtime
echo prompts with a free download link the first time.
echo.
if not defined CI explorer /select,"%EXE%"
if not defined CI pause
exit /b 0

:no_sdk
echo The .NET SDK (version 10 or later) is required to build this app.
echo Install it with:
echo   winget install Microsoft.DotNet.SDK.10
echo or download it from:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo Then run this builder again.
echo.
if not defined CI pause
exit /b 1

rem ---------------------------------------------------------------------------
:fetch_pdfium
setlocal
rem The "latest" URL always resolves to the newest release. To pin a specific
rem version instead, replace "latest/download" with "download/chromium%%2FNNNN".
set "URL=https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-win-x64.tgz"
set "TMP=%ROOT%lib\pdfium-download"
if not exist "%ROOT%lib" mkdir "%ROOT%lib"
if exist "%TMP%" rmdir /s /q "%TMP%"
mkdir "%TMP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; try { Invoke-WebRequest -Uri '%URL%' -OutFile '%TMP%\pdfium.tgz'; tar -xf '%TMP%\pdfium.tgz' -C '%TMP%'; Copy-Item '%TMP%\bin\pdfium.dll' '%PDFIUM%' -Force; exit 0 } catch { Write-Host $_; exit 1 }"
set "RC=%ERRORLEVEL%"
rmdir /s /q "%TMP%" 2>nul
endlocal & exit /b %RC%
