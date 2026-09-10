@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo   PhotoRenameAIHash - One-click Installer (self-contained)
echo ============================================================
echo.

echo [1/2] Trusting publisher certificate (run as Administrator)...
REM Import into LOCAL MACHINE TrustedPeople (and Root as fallback) so the MSIX
REM trust chain resolves. App Installer / Add-AppxPackage validates the
REM signature against the machine store, not the current-user store.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\Root'" >nul 2>&1
if errorlevel 1 (
  echo   ERROR: certificate import failed.
  echo   Please right-click Install.bat and choose "Run as administrator".
  pause
  exit /b 1
)
echo   OK.
REM 自诊断：打印被信任的证书指纹，与 MSIX 实际签名证书比对，防止 0x800B0109 静默复发
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('PhotoRenameAIHash.cer'); Write-Host ('  Trusted cert thumbprint: ' + $c.Thumbprint)"

echo [2/2] Installing PhotoRenameAIHash...
echo   (The Windows App SDK runtime is bundled inside the .msix,
echo    so no separate runtime / VCLibs / framework install is needed.)
set "APPMSIX="
for /f "delims=" %%f in ('dir /b PhotoRenameAIHash*_x64.msix 2^>nul') do set "APPMSIX=%%f"
if not defined APPMSIX (
  echo   ERROR: PhotoRenameAIHash*.msix not found in this folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-AppxPackage -Path '%APPMSIX%'"
if errorlevel 1 (
  echo   ERROR: application install failed. See message above.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo   Done! Launch "PhotoRenameAIHash" from the Start menu.
echo ============================================================
pause
