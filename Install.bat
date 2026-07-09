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
echo [1/2] Importing certificate...
certutil -addstore -f "Root" "PhotoRenameAIHash.cer" >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Failed to import certificate.
    pause
    exit /b 1
)

:: Step 2: Install MSIX (requires Windows 10 19041+)
echo [2/2] Installing application...
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
