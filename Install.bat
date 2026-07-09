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
:: 框架包（Microsoft.WindowsAppRuntime.1.5*.msix）是 side-by-side 的。
:: 关键：必须在一个 Add-AppxPackage 调用里把「所有」框架包作为 -Path 数组传入，
:: 由 Windows 在「单次部署」中解析依赖图（umbrella 依赖 Main x64/x86/arm64），
:: 切忌对每个包单独调用并重复传 -DependencyPath，否则会报
:: 0x80073CF9「指定了多次相同的程序包」。
:: ===========================================================================
echo [2/3] Ensuring Windows App SDK framework (side-by-side, non-destructive)...
powershell -NoProfile -Command "$fw = @(Get-ChildItem -Path '.' -Recurse -Include 'Microsoft.WindowsAppRuntime*.msix','Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue); if ($fw.Count -gt 0) { Write-Host ('  found ' + $fw.Count + ' bundled framework package(s); installing side-by-side...'); try { Add-AppxPackage -Path $fw.FullName -ErrorAction Stop; Write-Host '  Windows App SDK framework installed.' } catch { Write-Host ('  warning: ' + $_.Exception.Message) } } else { Write-Host '  no bundled framework MSIX in this folder; Windows will fetch the Windows App SDK runtime automatically during app install (requires internet).' }"

:: ===========================================================================
:: [3/3] 安装应用
:: ===========================================================================
echo [3/3] Installing application...
powershell -NoProfile -Command "$a = Get-ChildItem -Path '.' -Recurse -Filter 'PhotoRenameAIHash_*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $a) { Write-Host 'ERROR: PhotoRenameAIHash_*.msix not found in this folder.'; exit 1 }; Write-Host ('  installing ' + $a.Name); try { Add-AppxPackage -Path $a.FullName -ErrorAction Stop; Write-Host '  done.' } catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 (
    echo.
    echo Installation failed. If the error mentions a missing Windows App SDK framework dependency,
    echo download and run the runtime installer, then try again:
    echo   https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe
    pause
    exit /b 1
)

echo.
echo Done! PhotoRenameAIHash is installed. Launch it from the Start menu.
pause
