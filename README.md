# Photo-Rename-AI-Hash

A modern Windows desktop application for **photo organization (batch rename / copy /
move / archive by date)**, built with **WinUI 3 + Windows App SDK** and **C#/XAML**.

It is the WinUI 3 rewrite of the original Go-based `Photo-rename-AI` tool, following the
**MVVM** pattern with responsive navigation, data binding, and light/dark theme support.

## Features

| Page | Description |
|------|-------------|
| **整理 (Organize)** | Pick a folder, preview new filenames from a date/pattern rule, and batch-rename. |
| **设置 (Settings)** | Theme (Light / Dark / System), default folder, language, AI vision engine & API keys — persisted to local JSON. |
| **关于 (About)** | Version and description. |

## Architecture

```
PhotoRenameAIHash/
├─ PhotoRenameAIHash.csproj   # .NET 8 + WinUI 3 (packaged MSIX, self-contained)
├─ app.manifest              # DPI-aware declaration (PerMonitorV2)；MSIX 打包下刻意不放 longPathAware
├─ App.xaml / .cs            # Entry point, global resource dictionaries
├─ MainWindow.xaml / .cs     # NavigationView + Frame host
├─ Models/                   # AiProvider, AppSettings, ConflictStrategy, ImageAnalysisResult, OperationMode, OrganizeTypes, PhotoFile, RenameLogEntry
├─ Services/
│  ├─ Interfaces/            # IHashService, IPhotoService, ISettingsService
│  ├─ HashService.cs         # MD5 content hashing (conflict detection / rename log)
│  ├─ PhotoService.cs        # Folder scan, EXIF date-taken, rename
│  ├─ SettingsService.cs     # Local JSON persistence (API keys DPAPI-encrypted)
│  ├─ OrganizeService.cs     # Organize task orchestration (scan / rename / copy / move / archive)
│  ├─ RenameLogService.cs    # Rename log persistence
│  ├─ IImageAnalysisService.cs / IOrganizeService.cs  # Service contracts
│  ├─ ZhipuImageAnalysisService.cs   # AI vision engine: 智谱 GLM-4V
│  ├─ QwenImageAnalysisService.cs    # AI vision engine: 通义千问 VL
│  ├─ NvidiaImageAnalysisService.cs  # AI vision engine: NVIDIA
│  ├─ CustomImageAnalysisService.cs  # AI vision engine: 自定义 OpenAI 兼容端点
│  └─ AppServices.cs         # Singleton service locator
├─ Helpers/                  # ImageDecoder (resize/JPEG re-encode), ImageAnalysisHelper, RateGate, ThemeHelper, DpiHelper, AiPermanentException, PermanentOperationException
├─ ViewModels/               # Main / Organize / Settings / About (CommunityToolkit.Mvvm)
├─ Views/                    # Organize / Settings / About pages (XAML + code-behind)
└─ Styles/Styles.xaml        # Shared styles & resources
```

- **MVVM**: `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `RelayCommand`, `ObservableObject`).
- **Navigation**: `NavigationView` + `Frame.Navigate(Type)` in code-behind; view models are
  injected via a lightweight service locator (`AppServices`).
- **Theming**: `ThemeHelper.Apply(window, AppTheme)` sets `RequestedTheme` (Light/Dark/Default).
- **MD5 hashing**: `HashService` computes content MD5 (via `System.Security.Cryptography`)
  for conflict detection and the rename log; no native dependencies.

## Installing

The release artifact contains **four files, all of which must be kept in the same folder**.
Only the first three take part in the installation — `安装说明.txt` is documentation
(`Install.bat` locates the package by `dir /b PhotoRenameAIHash*_x64.msix` and never reads it):

| File | Purpose |
|------|---------|
| `PhotoRenameAIHash_<version>_x64.msix` | the app package (self-contained, no runtime prerequisites) — **required** |
| `PhotoRenameAIHash.cer` | the publisher certificate (public key only) — **required** |
| `Install.bat` | trusts the certificate, then installs the package — **required** |
| `安装说明.txt` | Chinese install guide — documentation only, not required to install |

Then **right-click `Install.bat` and choose "Run as administrator"**. Elevation is required
because the certificate is imported into the machine-level `TrustedPeople` store.

> Double-clicking the `.msix` alone will fail with *publisher not trusted*: the certificate has
> to be trusted first, which is exactly what `Install.bat` does.

To uninstall: **Settings → Installed apps → PhotoRenameAIHash**, or in PowerShell:

```powershell
Get-AppxPackage *PhotoRenameAIHash* | Remove-AppxPackage
```

Settings and saved API keys are stored in `%USERPROFILE%\.PhotoRenameAIHash\settings.json`.
This location is **not** subject to MSIX write virtualization, so it survives uninstalling or
resetting the app. A `settings.json` left behind by an older version in
`%LOCALAPPDATA%\PhotoRenameAIHash\` is migrated automatically on first launch.

## Build & Run

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (build-time only)

### Local build
```bash
dotnet restore PhotoRenameAIHash.csproj
dotnet build  PhotoRenameAIHash.csproj -c Release -p:Platform=x64
# output: bin/Release/net8.0-windows10.0.19041.0/win-x64/PhotoRenameAIHash.exe
```

### CI
Pushing to `main` / `Hy` / `pfx` / `fix` triggers GitHub Actions (`windows-latest`, .NET 8) which
publishes a **self-contained, signed MSIX** (both the Windows App SDK and the .NET 8 runtime
are bundled inside the package — no runtime prerequisites on the target machine) and uploads
it as the `PhotoRenameAIHash-msix` artifact together with the signing certificate (`PhotoRenameAIHash.cer`).

## Notes
- The project ships as a **self-contained MSIX** (`WindowsAppSDKSelfContained=true` +
  `SelfContained=true`): end users install with `Add-AppxPackage` only (see `Install.bat`),
  no separate .NET / Windows App SDK runtime install is needed.
- EXIF-based date parsing uses the photo's original capture time (`DateTimeOriginal`) and falls
  back to file last-write time when metadata is unavailable (toggleable on the Organize page).
- **Uninstall & settings**: MSIX apps live under `C:\Program Files\WindowsApps` and are invisible
  in Explorer — uninstall from **Settings → Installed apps → PhotoRenameAIHash**, or run
  `Get-AppxPackage *PhotoRenameAIHash* | Remove-AppxPackage` in PowerShell.
  Settings and API keys live in `%USERPROFILE%\.PhotoRenameAIHash\settings.json`; the profile
  root is **not** subject to MSIX write-virtualization, so uninstalling does **not** delete them.
  A `settings.json` left in `%LOCALAPPDATA%\PhotoRenameAIHash\` by an older version is migrated
  automatically on the first launch of the new version.
