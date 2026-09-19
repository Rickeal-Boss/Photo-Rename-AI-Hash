@echo off
setlocal
REM 中文提示按 UTF-8 输出；本文件保存为 UTF-8（无 BOM）。
REM chcp 必须在任何中文 echo 之前执行，否则 cmd 按 936 解析 UTF-8 字节 -> 乱码。
REM 文档唯一权威源 = README 的 Installing 章节；本脚本内的提示与随包 安装说明.txt
REM 都是它的精简版。改安装相关说明时先改 README，再同步这两处，避免三份各自演化。
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================================
echo   PhotoRenameAIHash - One-click Installer (self-contained)
echo ============================================================
echo.

echo [1/3] 检查运行环境与安装文件 ...
REM --- 管理员权限自检：写 Cert:\LocalMachine\* 必须提权。
REM     没有自检时，非管理员双击会在第 2 步静默失败，一路走到安装才抛看不懂的错。
REM     net session 在少数机器上因 Server 服务被禁用而误判，故用 fltmc 做或运算兜底。
set "ISADMIN=0"
net session >nul 2>&1
if not errorlevel 1 set "ISADMIN=1"
fltmc >nul 2>&1
if not errorlevel 1 set "ISADMIN=1"
if "%ISADMIN%"=="0" (
  echo.
  echo   [失败] 当前没有管理员权限。
  echo   导入证书要写入 LocalMachine 证书存储，必须提权。
  echo   请右键本脚本，选择「以管理员身份运行」。
  echo.
  pause
  exit /b 1
)

set "CER=PhotoRenameAIHash.cer"
if not exist "%CER%" (
  echo.
  echo   [失败] 同目录下缺少 %CER% 。
  echo   它与 .msix 一起分发，必须同时下载。
  echo   只下载 .msix 直接双击会因为发布者不受信任而安装失败（0x800B0109）。
  echo.
  pause
  exit /b 1
)

set "APPMSIX="
for /f "delims=" %%f in ('dir /b PhotoRenameAIHash*_x64.msix 2^>nul') do set "APPMSIX=%%f"
if not defined APPMSIX (
  echo.
  echo   [失败] 本目录下未找到 PhotoRenameAIHash*_x64.msix 。
  echo   请把 .msix / .cer / Install.bat 三个文件放在同一个文件夹里。
  echo.
  pause
  exit /b 1
)
echo   证书:   %CER%
echo   安装包: %APPMSIX%
echo   OK.

echo [2/3] 信任发布者证书 ...
REM 只导入 TrustedPeople：MSIX 旁加载（Add-AppxPackage）的信任链只需要它。
REM 不再导入 Root：本项目证书是自签名的【终端实体】证书（无 CA 基本约束），
REM 放进 Root 等价于宣布「该私钥签的任何东西在本机都被信任」，是机器级的安全放大，
REM 而旁加载并不需要它。
REM 输出不再重定向到 nul，并对本次导入单独判 errorlevel —— PowerShell -Command 对
REM cmdlet 的非终止错误可能不置位退出码，故用 try/catch + 显式 exit 保证可判定。
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { Import-Certificate -FilePath '%CER%' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null; exit 0 } catch { Write-Host ('  证书导入失败: ' + $_.Exception.Message); exit 2 }"
if errorlevel 1 (
  echo.
  echo   [失败] 证书导入失败，原因见上一行。
  echo.
  pause
  exit /b 1
)
REM 自诊断：打印被信任的证书指纹，与 MSIX 实际签名证书比对，防止 0x800B0109 静默复发
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('%CER%'); Write-Host ('  已信任证书指纹: ' + $c.Thumbprint)"
echo   OK.

echo [3/3] 安装 PhotoRenameAIHash ...
echo   （Windows App SDK 与 .NET 8 运行时已打进 .msix，无需另行安装任何运行时。）
REM 不直接抛 Windows 的英文原文：先抽取 0x 错误码，再给出中文含义，原文附在最后备查。
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { Add-AppxPackage -Path '%APPMSIX%'; exit 0 } catch { $s=$_.Exception.Message; $c=([regex]::Match($s,'0x[0-9A-Fa-f]{8}')).Value; if(-not $c){$c='(未识别)'}; $map=@{'0x800B0109'='证书未受信任：请确认 .cer 与 .msix 来自同一次构建，且已导入 TrustedPeople';'0x80073D02'='应用正在运行：请完全退出 PhotoRenameAIHash 后重试';'0x80073CF3'='依赖校验或包冲突失败（常见于已装更高版本）：请先卸载旧版本';'0x80073CFB'='包已存在（常见于重复安装同一版本）：请先卸载旧版本，或换更高版本号的包'}; $h=$map[$c]; if(-not $h){$h='未收录的错误码，请结合下方原文排查'}; Write-Host ('  [失败] 错误码: ' + $c); Write-Host ('  [含义] ' + $h); Write-Host ('  [原文] ' + $s); exit 3 }"
if errorlevel 1 (
  echo.
  echo   安装未完成。
  echo   若错误码为 0x800B0109，请核对上面「已信任证书指纹」与 .msix
  echo   实际签名的证书指纹是否一致（两者不一致说明 .cer 与 .msix 不是同一次构建）。
  echo.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo   安装完成！请从开始菜单启动 "PhotoRenameAIHash"。
echo ============================================================
echo.
echo   卸载方法（MSIX 装在 WindowsApps 下，资源管理器里看不到）：
echo     设置 -^> 已安装的应用 -^> PhotoRenameAIHash -^> 卸载
echo   或在 PowerShell 中执行：
echo     Get-AppxPackage *PhotoRenameAIHash* ^| Remove-AppxPackage
echo.
pause
