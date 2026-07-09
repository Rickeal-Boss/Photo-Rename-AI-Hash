@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo Installing PhotoRenameAIHash...

:: ===========================================================================
:: [1/3] 导入签名证书到本机受信任存储
:: MSIX 旁加载要求证书位于本机（LocalMachine）TrustedPeople（Root 兜底）。
:: ===========================================================================
echo [1/3] Importing certificate (machine-trusted)...
powershell -NoProfile -Command "Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null; Import-Certificate -FilePath 'PhotoRenameAIHash.cer' -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null"
if errorlevel 1 (
    echo ERROR: Certificate import failed. Make sure you run this as Administrator.
    pause
    exit /b 1
)

:: ===========================================================================
:: [2/3] 确保 Windows App SDK 框架运行时（并列安装，非破坏性）
:: 框架包（Microsoft.WindowsAppRuntime.1.5）是 side-by-side 的：安装匹配版本不会
:: 覆盖、也不会影响系统上已有的其它版本/其它应用。若系统已满足版本要求则跳过。
:: ===========================================================================
echo [2/3] Ensuring Windows App SDK framework (side-by-side, non-destructive)...
powershell -NoProfile -Command "Get-ChildItem -Path '.' -Recurse -Filter 'Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue | ForEach-Object { Add-AppxPackage -Path $_.FullName -ErrorAction SilentlyContinue }; $p = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5' | Where-Object { $_.Version -ge [Version]'5001.404.1901.0' }; if (-not $p) { Write-Host 'ERROR: required Windows App SDK framework (>= 5001.404.1901.0) could not be provisioned.'; Write-Host 'Download it from: https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe'; exit 1 }"
if errorlevel 1 (
    echo ERROR: Windows App SDK framework not available.
    pause
    exit /b 1
)

:: ===========================================================================
:: [3/3] 安装应用
:: ===========================================================================
echo [3/3] Installing application...
powershell -NoProfile -Command "$a = Get-ChildItem -Path '.' -Recurse -Filter 'PhotoRenameAIHash_*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $a) { Write-Host 'ERROR: PhotoRenameAIHash_*.msix not found in this folder.'; exit 1 }; try { Add-AppxPackage -Path $a.FullName } catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 (
    echo Installation failed. See error above.
    pause
    exit /b 1
)

echo.
echo Done! PhotoRenameAIHash is installed. Launch it from the Start menu.
pause
