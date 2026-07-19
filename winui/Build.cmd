@echo off
setlocal enableextensions
title Build

rem Builds the app (named by shared/app-spec.json) with the .NET SDK and, on
rem the first run, downloads the PDFium engine (pdfium.dll) it depends on.

set "ROOT=%~dp0"
rem ROOT must be captured before pushd: %~dp0 of a relatively-invoked script
rem re-resolves against the current directory at every expansion. The pushd
rem gives cmd a valid working directory when run from a network share (e.g.
rem a VM shared folder) by mapping a temporary drive letter. Every exit
rem path runs popd, or the mapping leaks one drive letter per build.
pushd "%ROOT%" >nul 2>nul
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
        popd >nul 2>nul
        exit /b 1
    )
    echo pdfium.dll ready.
    echo.
)

rem --- Intermediates: local disk when the project lives on a network share ---
rem The single-file bundler (and MSBuild generally) is unreliable and slow
rem over SMB shares (e.g. a VM shared folder), so obj/ and bin/ go to the
rem local temp disk in that case; the finished build\ still lands here.
set "INTOPTS="
set "BINROOT=%ROOT%bin"
if not "%ROOT:~0,2%"=="\\" goto :local_disk
for %%I in ("%ROOT%..") do set "REPO=%%~nxI"
set "INTROOT=%TEMP%\build\%REPO%-winui"
set "BINROOT=%INTROOT%\bin"
set "INTOPTS=-p:BaseIntermediateOutputPath="%INTROOT%/obj/" -p:BaseOutputPath="%INTROOT%/bin/""
:local_disk

rem Remove stale in-place intermediates from earlier builds first; when
rem intermediates are redirected to the temp disk, leftovers here would
rem otherwise be globbed as source files.
rd /s /q "%ROOT%bin" >nul 2>nul
rd /s /q "%ROOT%obj" >nul 2>nul

rem --- Build ---
rem The output folder of a plain build is used as the deliverable rather
rem than dotnet publish: for unpackaged WinUI, the build output is exactly
rem what Visual Studio runs (the most-tested deployment shape), while the
rem publish flow has a history of producing folders whose windows fail to
rem load at runtime.
if exist "%OUT%" rd /s /q "%OUT%"
dotnet build "%ROOT%App.csproj" -c Release -p:DebugType=None %INTOPTS%
if errorlevel 1 (
    echo.
    echo Build failed.
    echo.
    if not defined CI pause
    popd >nul 2>nul
    exit /b 1
)

rem Locate the built exe's folder under bin\ and mirror it into build\.
set "SRC="
for /r "%BINROOT%" %%F in (*.exe) do set "SRC=%%~dpF"
if not defined SRC (
    echo.
    echo Build produced no executable.
    echo.
    if not defined CI pause
    popd >nul 2>nul
    exit /b 1
)
xcopy "%SRC%*" "%OUT%\" /e /i /y /q >nul
if not exist "%OUT%\resources.pri" (
    echo WARNING: resources.pri is missing from the output; the app will not start.
)

rem --- Remove intermediate build files; build\ holds the finished app ---
rd /s /q "%ROOT%bin" >nul 2>nul
rd /s /q "%ROOT%obj" >nul 2>nul
if defined INTROOT rd /s /q "%INTROOT%" >nul 2>nul

rem The exe is named from the spec; find it rather than hardcoding the name.
set "EXE="
for %%F in ("%OUT%\*.exe") do set "EXE=%%F"

echo.
echo Built successfully:
echo   "%EXE%"
echo.
echo Keep the contents of the "build" folder together (WinUI does not support
echo single-file publish). You can move the folder anywhere, pin the exe, drag
echo PDF files onto it, or open it and drop PDFs into the window.
echo.
echo Note: running the app needs the free .NET Desktop Runtime and Windows App
echo Runtime; if either is missing, launch prompts with a download link.
echo.
if not defined CI explorer /select,"%EXE%"
if not defined CI pause
popd >nul 2>nul
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
popd >nul 2>nul
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
