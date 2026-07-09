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
echo [1/3] Importing certificate...
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

:: Step 2: Check Windows App SDK runtime (1.5, >= 5001.404.1901.0)
echo [2/3] Checking Windows App SDK runtime...
powershell -NoProfile -Command "$p=Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5'; if ($p -and [Version]$p.Version -ge [Version]'5001.404.1901.0') { exit 0 } else { exit 1 }" >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo Windows App SDK 1.5 runtime ^(version ^>= 5001.404.1901.0^) is missing.
    echo This is required to run PhotoRenameAIHash.
    echo.
    echo Downloading installer now...
    echo.
    start "" "https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe"
    echo After installation completes, re-run Install.bat.
    pause
    exit /b 1
)

:: Step 3: Install MSIX (requires Windows 10 19041+)
:: Add-AppxPackage auto-discovers a "Dependencies" folder next to the .msix
:: and tries to install bundled framework MSIX files. If the system already
:: has a newer Windows App Runtime, this fails with 0x80073D06.
:: Temporarily hide Dependencies to force the installer to use the system's
:: existing runtime.
echo [3/3] Installing application...
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

:: Run Add-AppxPackage with visible error output so the user can see the real reason
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
