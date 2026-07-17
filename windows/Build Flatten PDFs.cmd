@echo off
setlocal enableextensions
title Build Flatten PDFs

rem Builds Flatten PDFs.exe with the .NET SDK and, on the first run, downloads
rem the PDFium engine (pdfium.dll) it depends on.

set "ROOT=%~dp0"
set "OUT=%ROOT%build"
set "PDFIUM=%ROOT%lib\pdfium.dll"

echo.
echo Building Flatten PDFs...
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
        pause
        exit /b 1
    )
    echo pdfium.dll ready.
    echo.
)

rem --- Build ---
dotnet publish "%ROOT%FlattenPDFs.csproj" -c Release -o "%OUT%" -p:DebugType=None
if errorlevel 1 (
    echo.
    echo Build failed.
    echo.
    pause
    exit /b 1
)

rem --- Remove intermediate build files; build\ holds the finished app ---
rd /s /q "%ROOT%bin" >nul 2>nul
rd /s /q "%ROOT%obj" >nul 2>nul

echo.
echo Built successfully:
echo   "%OUT%\Flatten PDFs.exe"
echo.
echo Keep pdfium.dll in the same folder as the .exe. You can move the whole
echo "build" folder anywhere, pin the .exe to the Start menu or taskbar, drag
echo PDF files onto it, or open it and drop PDFs into the window.
echo.
echo Note: running the app on a machine without the .NET Desktop Runtime
echo prompts with a free download link the first time.
echo.
explorer /select,"%OUT%\Flatten PDFs.exe"
pause
exit /b 0

:no_sdk
echo The .NET SDK (version 10 or later) is required to build this app.
echo Install it with:
echo   winget install Microsoft.DotNet.SDK.10
echo or download it from:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo Then run this builder again.
echo.
pause
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
