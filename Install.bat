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
:: 关键点（两个已踩过的坑）：
::   坑1：不能把“所有包作为数组”一次性传给 -Path（本机 Add-AppxPackage -Path 是
::        string 单值，传数组会报 Cannot convert String[] to String）。
::   坑2：不能对每个包单独调用并重复传 -DependencyPath（会报 0x80073CF9 重复）。
:: 正确做法：先逐个注册 3 个 Main（x64/x86/arm64，彼此无依赖，是 leaf 包），
::          最后再注册 umbrella（它依赖这 3 个 Main，此时已就绪）。
:: 兜底：若上述都失败，联网运行官方运行时安装器（装的是 1.5.240428000，>= 所需版本）。
:: ===========================================================================
echo [2/3] Ensuring Windows App SDK framework (side-by-side, non-destructive)...
powershell -NoProfile -Command "$fw = @(Get-ChildItem -Path '.' -Recurse -Include 'Microsoft.WindowsAppRuntime*.msix','Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue); $provisioned = $false; if ($fw.Count -gt 0) { Write-Host ('  found ' + $fw.Count + ' bundled framework package(s); installing side-by-side...'); $umbrella = @($fw | Where-Object { $_.BaseName -eq 'Microsoft.WindowsAppRuntime.1.5' }); $mains = @($fw | Where-Object { $_.BaseName -ne 'Microsoft.WindowsAppRuntime.1.5' }); foreach ($m in $mains) { try { Add-AppxPackage -Path $m.FullName -ErrorAction Stop } catch { Write-Host ('    warning (main): ' + $_.Exception.Message) } }; if ($umbrella.Count -eq 1) { try { Add-AppxPackage -Path $umbrella[0].FullName -ErrorAction Stop; $provisioned = $true } catch { Write-Host ('    warning (umbrella): ' + $_.Exception.Message) } } }; if (-not $provisioned) { $p = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5' | Where-Object { $_.Version -ge [Version]'5001.119.156.0' }; if ($p) { $provisioned = $true } }; if (-not $provisioned) { Write-Host '  bundled framework not usable; trying official online installer...'; try { $url = 'https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe'; $exe = Join-Path $env:TEMP 'war-install.exe'; Invoke-WebRequest -Uri $url -OutFile $exe -ErrorAction Stop; Start-Process -FilePath $exe -ArgumentList '/quiet','/norestart' -Wait -ErrorAction Stop; $p = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.5' | Where-Object { $_.Version -ge [Version]'5001.119.156.0' }; if ($p) { $provisioned = $true; Write-Host ('  framework installed via official installer: ' + $p.Version) } } catch { Write-Host ('  online installer failed: ' + $_.Exception.Message) } }; if ($provisioned) { Write-Host '  Windows App SDK framework ready.' } else { Write-Host '  WARNING: framework not provisioned; app install may fail (internet required).' }"

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
