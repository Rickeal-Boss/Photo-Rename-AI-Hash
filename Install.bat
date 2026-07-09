@echo off
setlocal
cd /d "%~dp0"

echo Installing PhotoRenameAIHash...
echo.

REM ============================================================
REM [1/3] Import signing certificate into machine TrustedPeople
REM        (+ Root as fallback) so the MSIX trust chain resolves.
REM ============================================================
echo [1/3] Importing certificate (machine-trusted)...
powershell -NoProfile -Command "try { $c = @(Get-ChildItem -Path '.' -Recurse -Filter '*.cer' -ErrorAction SilentlyContinue | Select-Object -First 1); if ($c.Count -eq 0) { Write-Host '  no .cer file found; skipping cert import' } else { $raw = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($c.FullName); $tp = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine'); $tp.Open('ReadWrite'); $tp.Add($raw); $tp.Close(); $rt = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root','LocalMachine'); $rt.Open('ReadWrite'); try { $rt.Add($raw) } catch {} ; $rt.Close(); Write-Host ('  imported cert: ' + $c.Name) } } catch { Write-Host ('  cert import warning: ' + $_.Exception.Message) }"
echo.

REM ============================================================
REM [2/3] Best-effort pre-stage the LEAF framework packages
REM        (Main / Singleton / DDLM). These have no dependencies
REM        of their own, so singular install is safe. The umbrella
REM        is deliberately NOT pre-staged here -- it is resolved
REM        atomically together with the app in [3/3].
REM ============================================================
echo [2/3] Ensuring Windows App SDK framework (side-by-side, non-destructive)...
powershell -NoProfile -Command "$fw = @(Get-ChildItem -Path '.' -Recurse -Include 'Microsoft.WindowsAppRuntime*.msix','Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue); if ($fw.Count -eq 0) { Write-Host '  no bundled framework MSIX found; will rely on [3/3] atomic resolution / online fetch' } else { Write-Host ('  found ' + $fw.Count + ' bundled framework package(s)'); foreach ($f in $fw) { if ($f.Name -like '*.Main.*' -or $f.Name -like '*.Singleton_*' -or $f.Name -like '*.DDLM_*') { try { Add-AppxPackage -Path $f.FullName -ErrorAction SilentlyContinue; Write-Host ('  staged: ' + $f.Name) } catch { Write-Host ('  (skip) ' + $f.Name + ': ' + $_.Exception.Message) } } }; Write-Host '  leaf framework pre-staging done.' }"
echo.

REM ============================================================
REM [3/3] Install the application, supplying ALL bundled
REM        framework packages (including the umbrella) as
REM        -DependencyPath so Windows resolves the dependency
REM        graph atomically in a single deployment.
REM        Fallback: official online runtime installer.
REM ============================================================
echo [3/3] Installing application...
powershell -NoProfile -Command "$app = @(Get-ChildItem -Path '.' -Recurse -Filter 'PhotoRenameAIHash*.msix' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*WindowsAppRuntime*' -and $_.Name -notlike '*WindowsAppSDK*' } | Select-Object -First 1); $fw = @(Get-ChildItem -Path '.' -Recurse -Include 'Microsoft.WindowsAppRuntime*.msix','Microsoft.WindowsAppSDK*.msix' -ErrorAction SilentlyContinue); if ($app.Count -eq 0) { Write-Host 'ERROR: PhotoRenameAIHash*.msix not found in this folder.'; pause; exit 1 }; try { if ($fw.Count -gt 0) { Write-Host ('  installing ' + $app.Name + ' with ' + $fw.Count + ' framework dependency package(s)...'); Add-AppxPackage -Path $app.FullName -DependencyPath $fw.FullName -ErrorAction Stop } else { Write-Host ('  installing ' + $app.Name + ' (framework will be fetched automatically)...'); Add-AppxPackage -Path $app.FullName -ErrorAction Stop }; Write-Host 'Application installed successfully.' } catch { Write-Host ('ERROR: ' + $_.Exception.Message); Write-Host 'Attempting official Windows App SDK runtime installer as fallback...'; try { $url='https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe'; $tmp=[System.IO.Path]::GetTempFileName()+'.exe'; (New-Object System.Net.WebClient).DownloadFile($url,$tmp); Start-Process -FilePath $tmp -ArgumentList '/quiet','/norestart' -Wait; Add-AppxPackage -Path $app.FullName -ErrorAction Stop; Write-Host 'Application installed successfully (via runtime installer).' } catch { Write-Host 'Installation failed. If the error mentions a missing Windows App SDK framework dependency,'; Write-Host 'download and run the runtime installer, then try again:'; Write-Host '  https://aka.ms/windowsappsdk/1.5/1.5.240428000/windowsappruntimeinstall-x64.exe'; pause; exit 1 } }"
echo.
echo Done.
pause
