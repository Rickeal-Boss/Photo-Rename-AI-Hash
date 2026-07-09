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

:: Step 1: Import certificate to Trusted Root
echo [1/3] Importing certificate...
certutil -addstore -f "Root" "PhotoRenameAIHash.cer" >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to import certificate.
    pause
    exit /b 1
)

:: Step 2: Check Windows App SDK runtime (1.5)
echo [2/3] Checking Windows App SDK runtime...
powershell -NoProfile -Command "if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5') { exit 0 } else { exit 1 }" >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo Windows App SDK 1.5 runtime is NOT installed.
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
echo [3/3] Installing application...
set "MSIX="
for %%f in (*.msix) do set "MSIX=%%f"
if not defined MSIX (
    echo ERROR: No .msix file found in this folder.
    pause
    exit /b 1
)
powershell -Command "Add-AppxPackage -Path '%MSIX%' -ErrorAction Stop" >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Installation failed.
    echo If already installed, uninstall first via: Settings ^> Apps ^> PhotoRenameAIHash ^> Uninstall
    pause
    exit /b 1
)

echo.
echo Done! Launch "PhotoRenameAIHash" from Start menu.
timeout /t 3 >nul
