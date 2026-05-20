@echo off
setlocal enabledelayedexpansion

:: Always run relative to the folder this script lives in (the project folder).
cd /d "%~dp0"

:: --- CONFIGURATION ---
set "PROJECT_NAME=PinBoard"
set "PLATFORM=x64"
set "CONFIG=Release"
set "CONFIG_FROM_FLAG="

:: --- PARSE ARGS: -r/--release and -d/--debug (mutually exclusive, default Release) ---
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="-r"        ( set "NEW_CONFIG=Release" & goto :set_cfg )
if /i "%~1"=="--release" ( set "NEW_CONFIG=Release" & goto :set_cfg )
if /i "%~1"=="-d"        ( set "NEW_CONFIG=Debug"   & goto :set_cfg )
if /i "%~1"=="--debug"   ( set "NEW_CONFIG=Debug"   & goto :set_cfg )
if /i "%~1"=="-h"        goto :usage
if /i "%~1"=="--help"    goto :usage
if /i "%~1"=="/?"        goto :usage
echo [ERROR] Unknown argument: %~1
goto :usage

:set_cfg
if defined CONFIG_FROM_FLAG (
    echo [ERROR] -r/--release and -d/--debug are mutually exclusive.
    goto :usage
)
set "CONFIG=%NEW_CONFIG%"
set "CONFIG_FROM_FLAG=1"
shift
goto :parse_args

:args_done

echo ===================================================
echo  PinBoard MSIX: Clean, Build, Install, Launch
echo ===================================================

set "CSPROJ_FILE=%CD%\%PROJECT_NAME%.csproj"
set "SOURCE_MANIFEST=%CD%\Package.appxmanifest"
set "APPX_DIR=%CD%\AppPackages"
set "BIN_DIR=%CD%\bin"
set "OBJ_DIR=%CD%\obj"

if not exist "%CSPROJ_FILE%" (
    echo [ERROR] Could not find %CSPROJ_FILE%
    goto :error
)
if not exist "%SOURCE_MANIFEST%" (
    echo [ERROR] Could not find %SOURCE_MANIFEST%
    goto :error
)

:: Locate MSBuild via vswhere (works on Community/Pro/Enterprise + Preview).
:: NOTE: %ProgramFiles(x86)% contains a literal "(x86)" which breaks if-blocks
:: that reference it via %VAR% (cmd parses the block body before evaluating
:: the condition, and the unquoted ")" closes the block early). We use the
:: delayed-expansion form !VAR! inside blocks to avoid that, and use goto
:: chains instead of paren-bodies for the existence checks just to be safe.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" goto :vswhere_missing

set "MSBUILD="
for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not exist "!MSBUILD!" goto :msbuild_missing
goto :tools_found

:vswhere_missing
echo [ERROR] vswhere.exe not found at !VSWHERE!
goto :error

:msbuild_missing
echo [ERROR] Could not locate MSBuild.exe via vswhere.
goto :error

:tools_found

:: Parse package identity from the source manifest so uninstall/launch work
:: even when the Identity Name is a GUID rather than %PROJECT_NAME%.
for /f "delims=" %%i in ('powershell -NoProfile -Command "([xml](Get-Content -LiteralPath '%SOURCE_MANIFEST%')).Package.Identity.Name"') do set "PKG_NAME=%%i"
if "%PKG_NAME%"=="" (
    echo [ERROR] Could not parse Identity Name from %SOURCE_MANIFEST%
    goto :error
)

echo Project:           %CSPROJ_FILE%
echo MSBuild:           %MSBUILD%
echo Package Identity:  %PKG_NAME%
echo Configuration:     %CONFIG% ^| %PLATFORM%
echo.

:: 1. KILL + UNINSTALL PREVIOUS
echo [1/5] Killing running instances and uninstalling previous package...
taskkill /F /IM %PROJECT_NAME%.exe >nul 2>&1
powershell -NoProfile -Command "Get-AppxPackage -Name '%PKG_NAME%' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue"

:: 2. CLEAN
echo.
echo [2/5] Cleaning previous build output...
if exist "%BIN_DIR%"  rmdir /s /q "%BIN_DIR%"
if exist "%OBJ_DIR%"  rmdir /s /q "%OBJ_DIR%"
if exist "%APPX_DIR%" rmdir /s /q "%APPX_DIR%"

:: 3. BUILD (produces MSIX because csproj has GeneratePackageOnBuild=True)
echo.
echo [3/5] Building %PROJECT_NAME% (%CONFIG% ^| %PLATFORM%)...
"%MSBUILD%" "%CSPROJ_FILE%" /restore /nologo /v:minimal ^
    /p:Configuration=%CONFIG% ^
    /p:Platform=%PLATFORM% ^
    /p:RuntimeIdentifier=win-%PLATFORM% ^
    /p:GenerateAppxPackageOnBuild=true ^
    /p:AppxBundle=Never ^
    /p:UapAppxPackageBuildMode=SideloadOnly
if %errorlevel% neq 0 (echo Build failed & goto :error)

:: 4. LOCATE NEWEST MSIX + RUN Add-AppDevPackage.ps1 (handles cert + deps + install)
echo.
echo [4/5] Installing MSIX...
if not exist "%APPX_DIR%" (
    echo [ERROR] No AppPackages folder produced. Did GeneratePackageOnBuild run?
    goto :error
)

set "PKG_FOLDER="
for /f "delims=" %%i in ('powershell -NoProfile -Command "(Get-ChildItem -Path '%APPX_DIR%' -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Add-AppDevPackage.ps1') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName"') do set "PKG_FOLDER=%%i"

if "%PKG_FOLDER%"=="" (
    echo [ERROR] No package folder with Add-AppDevPackage.ps1 found under %APPX_DIR%
    goto :error
)
echo Package folder: %PKG_FOLDER%

powershell -NoProfile -ExecutionPolicy Bypass -File "%PKG_FOLDER%\Add-AppDevPackage.ps1" -Force
if %errorlevel% neq 0 (echo MSIX install failed & goto :error)

:: 5. LAUNCH
:: Launch via packaged activation (shell:appsfolder\<AUMID>) — direct .exe
:: invocation does not always apply the package activation context, which
:: causes WinAppRuntime WinRT classes to fail to register (0x80040154).
echo.
echo [5/5] Launching %PROJECT_NAME%...
powershell -NoProfile -Command "$pkg = Get-AppxPackage -Name '%PKG_NAME%'; if (-not $pkg) { Write-Host 'Package %PKG_NAME% not found after install.'; exit 1 }; $aumid = $pkg.PackageFamilyName + [char]33 + 'App'; $url = 'shell:appsfolder\' + $aumid; Write-Host (\"Launching: $url\"); Start-Process -FilePath 'explorer.exe' -ArgumentList $url"
if %errorlevel% neq 0 (echo Launch failed & goto :error)

echo.
echo ===================================================
echo  SUCCESS: %PROJECT_NAME% is running!
echo ===================================================
timeout /t 3 >nul
exit /b 0

:error
echo.
echo [ERROR] Script failed. Check logs above.
pause
exit /b 1

:usage
echo.
echo Usage: build-msix.bat [-r^|--release ^| -d^|--debug]
echo.
echo   -r, --release   Build the Release configuration (default).
echo   -d, --debug     Build the Debug configuration.
echo   -h, --help      Show this help.
echo.
echo The two configuration flags are mutually exclusive.
exit /b 1
