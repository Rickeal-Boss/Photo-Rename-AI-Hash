# Photo-Rename-AI-Hash

A modern Windows desktop application for **photo organization and perceptual-hash
duplicate detection**, built with **WinUI 3 + Windows App SDK** and **C#/XAML**.

It is the WinUI 3 rewrite of the original Go-based `Photo-rename-AI` tool, following the
**MVVM** pattern with responsive navigation, data binding, and light/dark theme support.

## Features

| Page | Description |
|------|-------------|
| **整理 (Organize)** | Pick a folder, preview new filenames from a date/pattern rule, and batch-rename. |
| **去重 (Deduplicate)** | Scan a folder, compute `aHash` / `dHash` perceptual hashes, group near-duplicates by a tunable Hamming-distance threshold, and delete the extras. |
| **设置 (Settings)** | Theme (Light / Dark / System), default folder, hash thresholds, language — persisted to local JSON. |
| **关于 (About)** | Version and description. |

## Architecture

```
PhotoRenameAIHash/
├─ PhotoRenameAIHash.csproj   # .NET 8 + WinUI 3 (unpackaged)
├─ app.manifest              # DPI-aware / long-path declaration + WinAppSDK dependency
├─ App.xaml / .cs            # Entry point, global resource dictionaries
├─ MainWindow.xaml / .cs     # NavigationView + Frame host
├─ Models/                   # PhotoFile, DuplicateGroup, AppSettings, RenameItem
├─ Services/
│  ├─ Interfaces/            # IHashService, IPhotoService, ISettingsService
│  ├─ HashService.cs         # Pure-C# aHash/dHash + Hamming distance
│  ├─ PhotoService.cs        # Folder scan, duplicate grouping, rename
│  ├─ SettingsService.cs     # Local JSON persistence
│  └─ AppServices.cs         # Singleton service locator
├─ Helpers/                  # ImageDecoder (Gray8 decode), ThemeHelper
├─ ViewModels/               # Main / Organize / Deduplicate / Settings / About (CommunityToolkit.Mvvm)
├─ Views/                    # Organize / Deduplicate / Settings / About pages (XAML + code-behind)
└─ Styles/Styles.xaml        # Shared styles & resources
```

- **MVVM**: `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `RelayCommand`, `ObservableObject`).
- **Navigation**: `NavigationView` + `Frame.Navigate(Type)` in code-behind; view models are
  injected via a lightweight service locator (`AppServices`).
- **Theming**: `ThemeHelper.Apply(window, AppTheme)` sets `RequestedTheme` (Light/Dark/Default).
- **Hashing**: `Windows.Graphics.Imaging.BitmapDecoder` resizes the image to an 8×8 (aHash) or
  9×8 (dHash) `Gray8` buffer; no native dependencies.

## Build & Run

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Windows App SDK (WinUI 3) runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
  (the unpackaged app loads the framework from the installed runtime)

### Local build
```bash
dotnet restore PhotoRenameAIHash.csproj
dotnet build  PhotoRenameAIHash.csproj -c Release -p:Platform=x64
# output: bin/Release/net8.0-windows10.0.19041.0/win-x64/PhotoRenameAIHash.exe
```

### CI
Pushing to `main` triggers GitHub Actions (`windows-latest`, .NET 8) which builds the
unpackaged executable and uploads it as the `PhotoRenameAIHash-windows` artifact.

## Notes
- The project builds **unpackaged** (`<WindowsPackageType>None</WindowsPackageType>`) to keep CI
  simple — no MSIX signing required.
- EXIF-based date parsing falls back to file last-write time when metadata is unavailable.
