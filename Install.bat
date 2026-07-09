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
:: [2/3] 确保 Windows App SDK 框架运行时（并列安装，非破坏性，最佳努力不阻断）
:: 框架包（Microsoft.WindowsAppRuntime.1.5）是 side-by-side 的：安装匹配版本不会
:: 覆盖、也不会影响系统上已有的其它版本/其它应用。
:: 若制品里带了框架 MSIX（Dependencies/ 下），则并列安装；若没带或安装失败，
:: 也不阻断 —— 第 [3/3] 步安装应用时，Windows 会联网自动拉取该运行时。
:: ===========================================================================
echo [2/3] Ensuring Windows App SDK framework (side-by-side, non-destructive)...
powershell -NoProfile -Command "$fw = @(Get-ChildItem -Path '.' -Recurse -Include 'Microsoft.WindowsAppRuntime*.msix','Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue); if ($fw.Count -gt 0) { Write-Host ('  found ' + $fw.Count + ' bundled framework package(s); installing side-by-side...'); foreach ($f in $fw) { $deps = $fw | Where-Object { $_.FullName -ne $f.FullName }; Write-Host ('  installing ' + $f.Name); try { Add-AppxPackage -Path $f.FullName -DependencyPath $deps.FullName -ErrorAction Stop } catch { Write-Host ('    warning: ' + $_.Exception.Message) } } } else { Write-Host '  no bundled framework MSIX in this folder; Windows will fetch the Windows App SDK runtime automatically during app install (requires internet).' }; $p = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5' | Where-Object { $_.Version -ge [Version]'5001.404.1901.0' }; if ($p) { Write-Host ('  Windows App SDK runtime already present: ' + $p.Version) } else { Write-Host '  Windows App SDK runtime not yet provisioned; it will be fetched automatically when the app is installed (requires internet).' }"

:: ===========================================================================
:: [3/3] 安装应用（声明了 PackageDependency，Windows 会联网自动拉取框架运行时）
:: ===========================================================================
echo [3/3] Installing application...
powershell -NoProfile -Command "$a = Get-ChildItem -Path '.' -Recurse -Filter 'PhotoRenameAIHash_*.msix' -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $a) { Write-Host 'ERROR: PhotoRenameAIHash_*.msix not found in this folder.'; exit 1 }; Write-Host ('  installing ' + $a.Name); try { Add-AppxPackage -Path $a.FullName } catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
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
