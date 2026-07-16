@echo off
setlocal enableextensions
title Build Flatten PDFs

rem Builds Flatten PDFs.exe using the C# compiler that ships in-box with the
rem .NET Framework (no Visual Studio, SDK, or NuGet required) and, on the first
rem run, downloads the PDFium engine (pdfium.dll) it depends on.

set "ROOT=%~dp0"
set "SRC=%ROOT%Sources\Program.cs"
set "ICON=%ROOT%Resources\app.ico"
set "OUT=%ROOT%build"
set "EXE=%OUT%\Flatten PDFs.exe"
set "PDFIUM=%OUT%\pdfium.dll"

echo.
echo Building Flatten PDFs...
echo.

rem --- Locate the in-box C# compiler (.NET Framework 4.x) ---
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the .NET Framework C# compiler ^(csc.exe^).
    echo It ships with the .NET Framework, which is present on Windows 10 and 11.
    echo If it is missing, install the .NET Framework 4.x from Microsoft and run this again.
    echo.
    pause
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

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
        echo   "%OUT%"
        echo Then run this builder again.
        echo.
        pause
        exit /b 1
    )
    echo pdfium.dll ready.
    echo.
)

rem --- Compile ---
"%CSC%" /nologo /target:winexe /optimize+ /platform:x64 ^
    /win32icon:"%ICON%" ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /out:"%EXE%" ^
    "%SRC%"

if errorlevel 1 (
    echo.
    echo Build failed.
    echo.
    pause
    exit /b 1
)

echo.
echo Built successfully:
echo   "%EXE%"
echo.
echo Keep pdfium.dll in the same folder as the .exe. You can move the whole
echo "build" folder anywhere, pin the .exe to the Start menu or taskbar, drag
echo PDF files onto it, or open it and drop PDFs into the window.
echo.
explorer /select,"%EXE%"
pause
exit /b 0

rem ---------------------------------------------------------------------------
:fetch_pdfium
setlocal
rem The "latest" URL always resolves to the newest release. To pin a specific
rem version instead, replace "latest/download" with "download/chromium%%2FNNNN".
set "URL=https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-win-x64.tgz"
set "TMP=%OUT%\pdfium-download"
if exist "%TMP%" rmdir /s /q "%TMP%"
mkdir "%TMP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; try { Invoke-WebRequest -Uri '%URL%' -OutFile '%TMP%\pdfium.tgz'; tar -xf '%TMP%\pdfium.tgz' -C '%TMP%'; Copy-Item '%TMP%\bin\pdfium.dll' '%PDFIUM%' -Force; exit 0 } catch { Write-Host $_; exit 1 }"
set "RC=%ERRORLEVEL%"
rmdir /s /q "%TMP%" 2>nul
endlocal & exit /b %RC%
