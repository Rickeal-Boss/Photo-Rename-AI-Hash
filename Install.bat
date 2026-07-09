@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo Installing PhotoRenameAIHash...

:: Check admin rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo ERROR: Administrator privileges required.
    echo Right-click Install.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

:: Step 1: Import certificate to Local Machine Trusted People
:: MSIX sideloading requires the signing cert in the Local Machine store,
:: NOT the Current User store. TrustedPeople is the standard location.
echo [1/2] Importing certificate...
powershell -NoProfile -Command "Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop" >nul 2>&1
if %errorlevel% neq 0 (
    :: Some Windows configs use Root instead
    powershell -NoProfile -Command "Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\Root' -ErrorAction Stop" >nul 2>&1
    if !errorlevel! neq 0 (
        echo ERROR: Failed to import certificate to machine store.
        pause
        exit /b 1
    )
)

:: Step 2: Install MSIX
:: The app is self-contained (WindowsAppSDKSelfContained=true) — it bundles
:: its own Windows App SDK runtime, so no system runtime check is needed.
:: Temporarily hide Dependencies folder to prevent Add-AppxPackage from
:: trying to install bundled framework packages that conflict with system version.
echo [2/2] Installing application...
set "DEPS_HIDDEN=0"
if exist "Dependencies" (
    ren "Dependencies" "_Deps_hidden" >nul 2>&1
    if !errorlevel! equ 0 set "DEPS_HIDDEN=1"
)

set "MSIX="
for %%f in (*.msix) do set "MSIX=%%f"
if not defined MSIX (
    echo ERROR: No .msix file found in this folder.
    if "%DEPS_HIDDEN%"=="1" (
        if exist "_Deps_hidden" ren "_Deps_hidden" "Dependencies" >nul 2>&1
    )
    pause
    exit /b 1
)

:: Show real error on failure
powershell -NoProfile -Command "try { Add-AppxPackage -Path '%MSIX%' -ErrorAction Stop; Write-Host 'OK' } catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
set "INSTALL_ERR=%errorlevel%"

:: Restore Dependencies folder (best-effort)
if "%DEPS_HIDDEN%"=="1" (
    if exist "_Deps_hidden" ren "_Deps_hidden" "Dependencies" >nul 2>&1
)

if %INSTALL_ERR% neq 0 (
    echo.
    echo Installation failed. See error above for details.
    pause
    exit /b 1
)

echo.
echo Done! Launch "PhotoRenameAIHash" from Start menu.
timeout /t 3 >nul
