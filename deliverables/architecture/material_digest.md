# PhotoRenameAIHash · 资料摘要

> 本文档做一件事：**精读主理人转交的全部原始资料，逐份、逐文件做出摘要**——后面任何人拿到这份摘要，都能通过 `D编号，§文件` 快速定位回原始文件/类的对应位置。
>
> 上游输入：主理人转交的项目说明（README）、现状代码库（PhotoRenameAIHash/ 源码）、以及重组目标规范（D3，来自任务说明）。
> 产出者：`knowledge-ingest-engineer`（知识摄入工程师 - 闻资料），经 G1 校验与人工审核通过后交付。
>
> 适配说明：本次原始资料为**源码（C#/XAML）+ Markdown 说明 + 内联规范**，非 docx/pdf。按主理人指示，将模板的"按文档章节摘要"适配为"按代码模块/文件摘要"，出处映射保留为 `D编号，§文件`（粒度到文件名与关键类/方法）。§1 类型字段相应适配为 `markdown` / `csharp-src` / `spec`。

---

## 0. 元信息

```yaml
标题: PhotoRenameAIHash - 资料摘要 v1.0
版本: v1.0
状态: Draft
创建日期: 2026-07-06
整理人: knowledge-ingest-engineer（闻资料）
审核人:
  - 主理人齐构成

原始资料清单:
  - D1 README.md: 项目说明（架构、功能、构建方式、unpackaged 说明）
  - D2 PhotoRenameAIHash/: 现状代码库（WinUI 3 + C#/XAML 源码，按模块/文件精读）
  - D3 用户目标规范（任务说明内联）: 本次重组的目标基线（WinUI 3 原生体验 + self-contained 便携发布）
```

| 版本 | 日期 | 作者 | 变更内容 |
| --- | --- | --- | --- |
| v0.1 | 2026-07-06 | knowledge-ingest-engineer | 初稿：D1/D2 逐文件摘要 + D3 目标对照与差距清单 |

---

## 1. 资料清单

> 列出全部原始资料，每份标注解析状态。解析失败或跳过的必须注明原因。

| 编号 | 文件名 | 类型 | 来源 | 解析状态 | 说明 |
| --- | --- | --- | --- | --- | --- |
| D1 | `README.md` | markdown | 主理人转交（项目根目录） | 已解析 | 共 68 行，涵盖 Features / Architecture / Build & Run / Notes |
| D2 | `PhotoRenameAIHash/`（源码目录） | csharp-src | 主理人转交（现状代码库） | 已解析 | 25 个源文件全部逐文件精读；`Resources/` 为空目录，无资源文件 |
| D3 | 用户目标规范（任务说明内联） | spec | 主理人（齐构成）任务说明 | 已解析 | WinUI 3 原生体验规范 + 免安装 self-contained 便携发布要求 |

**类型枚举（本次适配）**：`markdown` / `csharp-src` / `spec`（模板原枚举 docx/pdf/pptx/xlsx 不适用于源码类输入，依主理人指示适配）

---

## 2. 资料内容摘要

> 逐份资料按自身结构（文档段落 / 代码模块与文件）做摘要。每条摘要标注 `D编号，§文件`，后面任何人想核实某个点，直接定位回原文对应位置即可。

### D1：`README.md`（项目说明）

> WinUI 3 重写版照片整理 / 感知哈希去重桌面应用的项目说明 — 来源：项目根目录

| 章节 | 内容摘要 |
| --- | --- |
| §Features | 四个页面：整理（选文件夹、按日期/规则预览新文件名、批量重命名）；去重（扫描文件夹、计算 aHash/dHash、按可调汉明距离阈值分组、删除多余项）；设置（主题 Light/Dark/System、默认文件夹、哈希阈值、语言，持久化到本地 JSON）；关于（版本与描述）。 |
| §Architecture | 目录结构：`.csproj`（.NET 8 + WinUI 3，unpackaged）、`app.manifest`（DPI/长路径声明 + WinAppSDK 依赖）、`App.xaml/.cs`（入口、全局资源字典）、`MainWindow.xaml/.cs`（NavigationView + Frame 宿主）、`Models/`、`Services/`（含 `Interfaces/`、`HashService`、`PhotoService`、`SettingsService`、`AppServices`）、`Helpers/`、`ViewModels/`、`Views/`、`Styles/Styles.xaml`。 |
| §Architecture-MVVM | 使用 `CommunityToolkit.Mvvm`（`[ObservableProperty]`、`RelayCommand`、`ObservableObject`）。 |
| §Architecture-Navigation | `NavigationView` + `Frame.Navigate(Type)`（code-behind）；ViewModel 通过轻量 service locator（`AppServices`）注入。 |
| §Architecture-Theming | `ThemeHelper.Apply(window, AppTheme)` 设置 `RequestedTheme`（Light/Dark/Default）。 |
| §Architecture-Hashing | 使用 `Windows.Graphics.Imaging.BitmapDecoder` 将图像缩放为 8×8（aHash）或 9×8（dHash）的 `Gray8` 缓冲；无原生依赖。 |
| §Build&Run-Prerequisites | 需要 .NET 8 SDK 与 Windows App SDK (WinUI 3) 运行时；unpackaged 应用从已安装的运行时加载框架。 |
| §Build&Run-LocalBuild | `dotnet restore` → `dotnet build -c Release -p:Platform=x64`；输出 `bin/Release/net8.0-windows10.0.19041.0/win-x64/PhotoRenameAIHash.exe`。 |
| §Build&Run-CI | 推送到 `main` 触发 GitHub Actions（windows-latest, .NET 8），构建 unpackaged 可执行文件并作为 `PhotoRenameAIHash-windows` artifact 上传。 |
| §Notes | 项目以 unpackaged（`&lt;WindowsPackageType&gt;None&lt;/WindowsPackageType&gt;`）构建以简化 CI，无需 MSIX 签名；EXIF 日期解析在元数据不可用时回退到文件最后写入时间。 |

### D2：`PhotoRenameAIHash/`（现状代码库）

> 现状实现逐模块/文件摘要（WinUI 3 + C#/XAML，MVVM）— 来源：主理人转交现状代码库。下文按 §2.1–§2.6 六组模块组织；详细文件引用格式为 `D2,§文件名`（粒度到文件），模块级引用格式为 `D2,§2.N`。

**D2 模块编号索引（§2.1–§2.6）**

| 编号 | 模块 | 对应文件/类 |
| --- | --- | --- |
| D2,§2.1 | 项目配置与入口 | PhotoRenameAIHash.csproj、app.manifest、App.xaml(.cs)、MainWindow.xaml(.cs) |
| D2,§2.2 | Models | PhotoFile.cs、DuplicateGroup.cs、AppSettings.cs、RenameItem.cs |
| D2,§2.3 | Services | Interfaces/*、HashService.cs、PhotoService.cs、SettingsService.cs、AppServices.cs |
| D2,§2.4 | Helpers | ImageDecoder.cs、ThemeHelper.cs |
| D2,§2.5 | ViewModels | MainViewModel.cs、OrganizeViewModel.cs、DeduplicateViewModel.cs、SettingsViewModel.cs、AboutViewModel.cs |
| D2,§2.6 | Views | OrganizePage.xaml(.cs)、DeduplicatePage.xaml(.cs)、SettingsPage.xaml(.cs)、AboutPage.xaml(.cs)、Styles.xaml |

**2.1 项目配置与入口（D2,§2.1）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§PhotoRenameAIHash.csproj | .NET 8 控制台式 WinExe（`OutputType=WinExe`）；`TargetFramework=net8.0-windows10.0.19041.0`；`WindowsPackageType=None`（unpackaged）；`Platforms=x64`、`RuntimeIdentifiers=win-x64`；`UseWinUI=true`；包引用 `Microsoft.WindowsAppSDK 1.5.240428000`、`Microsoft.Windows.SDK.BuildTools 10.0.22621.3233`、`CommunityToolkit.Mvvm 8.2.2`；`Nullable`/`ImplicitUsings` 开启。 |
| D2,§app.manifest | 声明 `requestedExecutionLevel=asInvoker`；`dpiAware=true/pm` 与 `dpiAwareness=PerMonitorV2, PerMonitor`（高 DPI 适配）；`longPathAware=true`；无 `uiAccess`、无 MUI/主题扩展。 |
| D2,§App.xaml | `Application.Resources` 合并 `XamlControlsResources` 与 `Styles/Styles.xaml`。 |
| D2,§App.xaml.cs | `App : Application`；`OnLaunched` 创建并激活 `MainWindow`；静态属性 `MainWindow`。 |
| D2,§MainWindow.xaml | `Window` 内含 `NavigationView`（`NavView`）：`MenuItems` 为 整理(Organize, Pictures 图标, 默认选中)、去重(Deduplicate, Map)、设置(Settings, Setting)；`FooterMenuItems` 为 关于(About, Help)；`ContentFrame` 作为页面宿主；`Header` 绑定 `ViewModel.AppTitle`；`IsBackButtonVisible=Collapsed`。 |
| D2,§MainWindow.xaml.cs | `MainViewModel ViewModel`；`_pages` 字典映射 Tag→Page 类型（Organize/Deduplicate/Settings/About）；构造函数 `InitializeComponent` 后默认导航到 Organize，并 `AppServices.SettingsService.Load()` 后 `ThemeHelper.Apply(this, settings.Theme)`；`NavView_SelectionChanged` 按 `item.Tag` 用 `ContentFrame.Navigate(type)` 切换页面。 |

**2.2 Models（D2,§2.2）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§Models/PhotoFile.cs | `PhotoFile`：属性 `Path`、`Name`、`SizeBytes`、`LastModified`(DateTime)、`NewName?`、`Status?`。注意 `LastModified` 在扫描时由 `FileInfo.LastWriteTimeUtc` 填充（见 PhotoService），无 EXIF 字段。 |
| D2,§Models/DuplicateGroup.cs | `DuplicateGroup`：`Id`、`Members`(List&lt;PhotoFile&gt;)、`KeepIndex`(默认 0，表示删除时保留每组首个)。 |
| D2,§Models/AppSettings.cs | `enum AppTheme { Light, Dark, System }`；`AppSettings`：`AppVersion="1.0.0"`、`Theme=System`、`DefaultFolder=""`、`AHashThreshold=8`、`DHashThreshold=10`、`Language="zh-CN"`。 |
| D2,§Models/RenameItem.cs | `RenameItem`：`SourcePath`、`DestinationPath`（重命名迁移单元）。 |

**2.3 Services（D2,§2.3）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§Services/Interfaces/IHashService.cs | 接口：`ComputeAHashAsync(Stream,int size=8)`、`ComputeDHashAsync(Stream,int size=8)`、`HammingDistance(ulong,ulong)`、`Similarity(ulong,ulong)`（[0,1]，1 表示相同）。 |
| D2,§Services/Interfaces/IPhotoService.cs | 接口：`ScanAsync(folder)`、`FindDuplicatesAsync(files,aHashThreshold,dHashThreshold)`、`RenameAsync(items)`。 |
| D2,§Services/Interfaces/ISettingsService.cs | 接口：`Load()`、`SaveAsync(settings)`。 |
| D2,§Services/HashService.cs | `HashService : IHashService`。`ComputeAHashAsync`：调用 `ImageDecoder.DecodeGrayAsync(image,8,8)` 后 `AHashFromGray`（均值二值化，≥均值置 1，共 64 位）。`ComputeDHashAsync`：解码为 9×8 后 `DHashFromGray`（相邻列左&lt;右置 1）。`HammingDistance` 用 `x &= x-1` 计数；`Similarity = 1 - Hamming/64`。 |
| D2,§Services/PhotoService.cs | `PhotoService : IPhotoService`。`ScanAsync`：仅枚举 `Directory.EnumerateFiles`（**非递归**），按扩展名白名单（.jpg/.jpeg/.png/.bmp/.gif/.tif/.tiff/.webp）过滤，填充 `PhotoFile`（含 `LastWriteTimeUtc`）。`FindDuplicatesAsync`：对每张图开流分别算 aHash/dHash（同一流 `Position=0` 复用），try/catch 跳过不可读图像；用 `used[]` 做贪心分组，当 `da&lt;=aHashThreshold || dd&lt;=dHashThreshold`（**OR 条件**）即并入同组；仅保留 `Members.Count&gt;1` 的组。`RenameAsync`：`File.Move` 逐个迁移（目标不存在才移动）。 |
| D2,§Services/SettingsService.cs | `SettingsService : ISettingsService`。`Load()`：从 `%LocalAppData%/PhotoRenameAIHash/settings.json` 读取并反序列化，文件缺失/损坏时回退默认 `new AppSettings()`。`SaveAsync`：创建目录后 `JsonSerializer.Serialize`（缩进）写回。 |
| D2,§Services/AppServices.cs | 静态 service locator（单例）：`HashService`、`PhotoService`、`SettingsService` 各 `new` 一个实现。注释说明"构造函数注入通过将实例传入各 ViewModel 实现"。 |

**2.4 Helpers（D2,§2.4）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§Helpers/ImageDecoder.cs | 静态 `ImageDecoder.DecodeGrayAsync(source,w,h)`：将流写入 `InMemoryRandomAccessStream`，用 `BitmapDecoder.CreateAsync` 解码；`BitmapTransform` 缩放至目标宽高、`InterpolationMode=Linear`、`BitmapPixelFormat=Gray8`、`BitmapAlphaMode=Ignore`、`ExifOrientationMode=IgnoreExifOrientation`、`ColorManagementMode.DoNotColorManage`；返回 `DetachPixelData()` 字节数组。纯 WinRT 成像 API，无原生依赖。 |
| D2,§Helpers/ThemeHelper.cs | 静态 `ThemeHelper.Apply(Window?, AppTheme)`：取 `window.Content` 作为 `FrameworkElement`，按 Light/Dark/System 设置 `root.RequestedTheme`（ElementTheme.Light/Dark/Default）。注意仅作用于 Content 根元素，未覆盖窗口背景/Mica。 |

**2.5 ViewModels（D2,§2.5）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§ViewModels/MainViewModel.cs | `AppTitle="Photo Rename AI · Hash"`、`Version=AppSettings.AppVersion`。 |
| D2,§ViewModels/OrganizeViewModel.cs | 持有 `_photoService=AppServices.PhotoService`；属性 `FolderPath`、`Pattern`(默认 `"{yyyy}{MM}{dd}_{n}"`)、`Preview`(ObservableCollection&lt;PhotoFile&gt;)、`StatusText`、`IsBusy`。`ScanAsync`：校验目录后 `ScanAsync` 并逐文件 `BuildName` 生成 `NewName`、`Status="待重命名"`。`RenameAsync`：构造 `RenameItem`(目标=同目录+NewName) 调 `RenameAsync`，状态置"已重命名"。`BuildName` 用 `f.LastModified` 做 `{yyyy}{MM}{dd}{HH}{mm}{n}{name}` 文本替换（**未解析 EXIF**，直接取 last-write 时间）。 |
| D2,§ViewModels/DeduplicateViewModel.cs | 持有 `_photoService`；属性 `FolderPath`、`AHashThreshold=8`、`DHashThreshold=10`、`Groups`、`StatusText`、`IsBusy`。`ScanAsync`：`ScanAsync`→`FindDuplicatesAsync(AHashThreshold,DHashThreshold)` 填充 `Groups`。`DeleteSelected`：遍历各组，`i==KeepIndex` 保留、其余 `File.Delete`（catch 忽略锁定/已删），最后 `Groups.Clear()`。 |
| D2,§ViewModels/SettingsViewModel.cs | 持有 `_settings=AppServices.SettingsService`；构造时 `Load()` 并映射到属性。`ThemeIndex` 在 AppTheme 与 RadioButtons 索引(0=Light,1=Dark,2=System)间互转。`OnThemeChanged`：写回 `_model.Theme`、`ThemeHelper.Apply(App.MainWindow!, newValue)` 即时换肤、`OnPropertyChanged(nameof(ThemeIndex))`。`SaveAsync`：聚合各属性到 `_model` 并 `SaveAsync`。 |
| D2,§ViewModels/AboutViewModel.cs | `Version=AppSettings.AppVersion`；`Description` 为工具简介文案（WinUI 3 + Windows App SDK、MVVM、整理/去重功能）。 |

**2.6 Views（D2,§2.6）**

| 章节 | 内容摘要 |
| --- | --- |
| D2,§Views/OrganizePage.xaml(.cs) | 网格 `Padding=24`，行定义 Auto/Auto/*/Auto：行0 选文件夹按钮(FolderPicker)+路径 TextBox(TwoWay)+扫描按钮(ScanCommand)；行1 命名规则 TextBox(TwoWay)+执行重命名按钮(RenameCommand)；行2 ListView 绑定 `Preview`，DataTemplate 显示 `Name` 与 `NewName`(Accent 色)；行3 状态 TextBlock。`PickFolder_Click` 用 `FolderPicker` + `InitializeWithWindow` 绑定 `App.MainWindow` 句柄，选后写 `ViewModel.FolderPath`。 |
| D2,§Views/DeduplicatePage.xaml(.cs) | 同布局：行0 选文件夹+扫描相似图片；行1 aHash/dHash `Slider`(0–20, TwoWay)；删除非保留项按钮(DeleteSelectedCommand)；行2 ListView 绑定 `Groups`（显示 Id 与成员名）；行3 状态。`PickFolder_Click` 同 OrganizePage。 |
| D2,§Views/SettingsPage.xaml(.cs) | `StackPanel Padding=24 MaxWidth=560`：标题 + 主题 RadioButtons(`SelectedIndex`↔`ThemeIndex`, 浅色/深色/跟随系统) + 默认文件夹 TextBox + aHash/dHash Slider(0–20) + 语言 TextBox + 保存按钮(SaveCommand) + 状态。 |
| D2,§Views/AboutPage.xaml(.cs) | `StackPanel Padding=24`：标题 + `Version`(SemiBold) + `Description`(Wrap)。 |
| D2,§Styles/Styles.xaml | `ResourceDictionary`：仅 `PageTitleStyle`(FontSize=28, SemiBold)、`PagePadding=24,24,24,24`、`SectionHeaderStyle`(FontSize=14, SemiBold)。**无 Mica/Acrylic、无 CornerRadius/阴影/字体统一设定**。 |

### D3：用户目标规范（重组目标基线）

> WinUI 3 原生 Windows 11 体验 + 免安装 self-contained 便携发布要求 — 来源：主理人（齐构成）任务说明

| 章节 | 内容摘要 |
| --- | --- |
| D3,§技术框架 | WinUI 3 + Windows App SDK，原生 Windows 11 风格；**免安装便携版、打包即用**（self-contained 自包含发布，无需预装 WinAppSDK 运行时）。 |
| D3,§UI-MicaAcrylic | Mica 材质背景 + Acrylic 半透明。 |
| D3,§UI-Fluent | Fluent Design 圆角 `CornerRadius=8px`；阴影层级；间距规范。 |
| D3,§UI-Navigation | 导航用 `NavigationView`。 |
| D3,§UI-TitleBar | 顶部 Windows 11 分层标题栏（`ExtendsContentIntoTitleBar` + 自定义标题栏）。 |
| D3,§UI-Controls | 所有控件使用 WinUI 3 原生样式并含 Hover/Click 视觉反馈动画。 |
| D3,§UI-Theme | 深色/浅色主题无缝切换。 |
| D3,§UI-DPI | 适配高 DPI。 |
| D3,§UI-Font | 全局 `Segoe UI Variable` 字体。 |

### D2 现状 vs D3 目标 逐条对照（差距清单，仅记录不裁决）

> 下列对照为后续架构师（business-architect / system-architect）输入基线。状态标记：**已具备 / 部分满足 / 缺失**。本工程师只记录，不做裁决。

| 编号 | D3 目标项 | 现状（D2 出处） | 状态 |
| --- | --- | --- | --- |
| G1 | 免安装 self-contained 便携发布、无需预装 WinAppSDK 运行时 | `WindowsPackageType=None`（D2,§.csproj）；README 声明依赖已安装运行时（D1 §Build&Run/§Notes） | **缺失** |
| G2 | Mica 材质背景 + Acrylic 半透明 | 无 Mica/Acrylic 实现；Styles.xaml 仅文本样式（D2,§Styles/Styles.xaml、§MainWindow.xaml） | **缺失** |
| G3 | Windows 11 分层标题栏（ExtendsContentIntoTitleBar + 自定义标题栏） | MainWindow 用默认标题栏（`Title="Photo Rename AI · Hash"`），未 ExtendsContentIntoTitleBar（D2,§MainWindow.xaml） | **缺失** |
| G4 | Fluent 圆角 8px / 阴影层级 / 间距规范 | 仅 `PagePadding=24` 与默认控件外观；无显式 CornerRadius/阴影（D2,§Styles/Styles.xaml） | **部分满足** |
| G5 | 控件 Hover/Click 视觉反馈动画 | 依赖 WinUI 3 原生默认视觉状态；无自定义动画（D2,§Views/*） | **部分满足** |
| G6 | 全局 Segoe UI Variable 字体 | 未显式声明；WinUI 3 默认字体即 Segoe UI Variable（D2 全量源码无字体设定） | **部分满足** |
| G7 | 适配高 DPI | app.manifest 声明 `PerMonitorV2, PerMonitor`（D2,§app.manifest）；无逐控件处理 | **部分满足** |
| G8 | 深色/浅色主题无缝切换 | `ThemeHelper.Apply` 设置 Content 根 `RequestedTheme`，设置页即时换肤（D2,§Helpers/ThemeHelper.cs、§ViewModels/SettingsViewModel.cs） | **已具备**（仅作用于 Content 根，未覆盖窗口背景） |
| G9 | 导航用 NavigationView | MainWindow 已实现 NavigationView + Frame 导航（D2,§MainWindow.xaml/.cs） | **已具备** |
| G10 | WinUI 3 + Windows App SDK 技术栈 | .NET 8 + WinUI 3 + WindowsAppSDK 1.5（D2,§.csproj、App.xaml） | **已具备** |
| G11 | 感知哈希去重（aHash/dHash + 可调阈值） | HashService/PhotoService 已实现（D2,§Services/HashService.cs、§PhotoService.cs） | **已具备** |
| G12 | 设置 JSON 持久化 | SettingsService 写入 `%LocalAppData%/PhotoRenameAIHash/settings.json`（D2,§Services/SettingsService.cs） | **已具备** |

---

## 3. 冲突记录

> 不同资料对同一事实描述矛盾，或目标规范与现状实现存在出入时，**并列保留两个版本**，不做裁决。主理人/下游可据此裁决。

| 编号 | 冲突主题 | 版本 A | 出处 A | 版本 B | 出处 B | 差异说明 |
| --- | --- | --- | --- | --- | --- | --- |
| X1 | 发布形态 / 运行时依赖 | 免安装 self-contained 自包含发布，无需预装 WinAppSDK 运行时 | D3,§技术框架 | 以 unpackaged（`&lt;WindowsPackageType&gt;None&lt;/WindowsPackageType&gt;`）构建，依赖已安装的 WinAppSDK 运行时加载框架 | D1,§Build&Run / §Notes；D2,§PhotoRenameAIHash.csproj | 目标与现状相反：现状缺失 self-contained，需预装运行时 |
| X2 | EXIF 日期解析 | "EXIF-based date parsing falls back to file last-write time when metadata is unavailable"（声称先解析 EXIF，不可用时回退） | D1,§Notes | `OrganizeViewModel.BuildName` 直接取 `f.LastModified`（`PhotoFile.LastModified` = `FileInfo.LastWriteTimeUtc`），代码中**无任何 EXIF 解析逻辑** | D2,§ViewModels/OrganizeViewModel.cs（BuildName）、§Models/PhotoFile.cs、§Services/PhotoService.cs | D1 描述与 D2 实际实现不一致：代码仅用 last-write 时间，无 EXIF 解析路径 |
| X3 | Mica / Acrylic 材质背景 | Mica 材质背景 + Acrylic 半透明 | D3,§UI-MicaAcrylic | 无 Mica/Acrylic 实现；Window/Page 使用默认背景 | D2,§MainWindow.xaml、§Styles/Styles.xaml | 现状缺失目标要求的材质背景 |
| X4 | 自定义分层标题栏 | Windows 11 分层标题栏：`ExtendsContentIntoTitleBar` + 自定义标题栏 | D3,§UI-TitleBar | MainWindow 使用默认窗口标题栏（`Title` 属性），未 ExtendsContentIntoTitleBar | D2,§MainWindow.xaml / .xaml.cs | 现状缺失目标要求的标题栏实现 |
| X5 | Fluent 圆角 / 阴影 / 间距规范 | Fluent Design 圆角 8px、阴影层级、间距规范（显式统一） | D3,§UI-Fluent | 仅 `PagePadding=24` 与默认控件外观，无显式 CornerRadius/阴影/统一间距令牌 | D2,§Styles/Styles.xaml | 现状部分满足（依赖默认，未显式规范化） |
| X6 | 全局字体 | 全局 `Segoe UI Variable` 字体（显式声明） | D3,§UI-Font | 未显式声明字体；依赖 WinUI 3 默认（即 Segoe UI Variable） | D2 全量源码（无 FontFamily 设定） | 现状部分满足（默认即满足，但未显式声明/统一） |
| X7 | 高 DPI 适配 | 适配高 DPI（完整） | D3,§UI-DPI | 仅在 app.manifest 声明 `PerMonitorV2, PerMonitor`，无逐控件/布局层处理 | D2,§app.manifest | 现状部分满足（manifest 层声明，缺布局级处理） |
| X8 | 主题切换覆盖范围 | 深色/浅色主题无缝切换（隐含覆盖整窗 incl. 背景） | D3,§UI-Theme | `ThemeHelper.Apply` 仅设置 Content 根 `RequestedTheme`，未覆盖窗口背景/Mica 等 | D2,§Helpers/ThemeHelper.cs | 现状已具备基础切换，但覆盖范围与目标期望可能不一致（待裁决） |

---

## 4. 硬指标清单

| 章节 | 硬指标 | 状态 |
| --- | --- | --- |
| §1 | 每份资料有解析状态，失败/跳过注明原因 | ✅ |
| §2 | 每份文档按章节逐条摘要，每条标注了 `D编号，§文件` | ✅ |
| §3 | 冲突信息并列保留，不做裁决 | ✅ |
| §0 | 元信息（标题/版本/日期/整理人/审核人/资料清单）完整 | ✅ |
| 附录 | 附录 A（生成流程）与附录 B（解析 Skill）齐全 | ✅ |
| 占位符 | 全文无未填充的角括号占位、样板前缀、未填日期、空白标记残留 | ✅ |
| 现状对照 | D3 目标 vs D2 现状 逐条差距对照已标注（已具备/缺失/部分满足） | ✅ |

---

## 附录 A：生成流程

### 流程总览

| 步骤 | 动作 | 落入章节 |
| --- | --- | --- |
| Step0 | 读取模板 + 全部原始资料（D1 README、D2 源码、D3 内联规范） | — |
| Step1 | 盘点资料清单，标注解析状态 | §1 |
| Step2 | 逐份打开资料，按自身结构（文档段落 / 代码模块与文件）逐条摘要 | §2 |
| Step3 | 交叉比对不同资料/目标与现状，发现并记录矛盾（含 D3↔D2 差距、D1↔D2 矛盾） | §3 |
| Step4 | 逐项核验硬指标 | §4 |

```mermaid
flowchart LR
    S0[读取模板与资料] --> S1[盘点资料清单]
    S1 --> S2[逐份精读逐文件摘要]
    S2 --> S3[交叉比对记录冲突与差距]
    S3 --> S4[硬指标自检]
```

### 整理原则

1. **逐份精读，不跨文档归并**：摘要按资料自身结构（文档段落 / 代码文件）组织，不做跨文档主题重组（下游职责）。
2. **出处即章节号**：每条摘要标注 `D编号，§文件`，直接映射回原文位置（源码粒度到文件名与关键类/方法）。
3. **冲突保留**：矛盾信息并列保留两个版本，不擅自裁决（含 D3 目标规范 vs D2 现状实现）。
4. **事实驱动**：以原始资料中的事实为准（含源码事实），不添加主观推断；差距仅标注状态，不裁决。

---

## 附录 B：解析 Skill（本次适配）

- `markdown`（D1）：项目说明类 Markdown 文档，按段落/小节提取。
- `csharp-src`（D2）：C#/XAML 源码树，按模块与文件（csproj / manifest / xaml / .cs）逐文件精读，映射到 `D编号，§文件`。
- `spec`（D3）：主理人内联目标规范，按技术框架 / UI 规范子项提取，作为差距对照基线。

> 注：模板原枚举为 `docx` / `pdf` / `pptx` / `xlsx`（Office 类资料）。本次输入为源码 + Markdown + 内联规范，依主理人指示适配为上述三类，解析方式相应调整。
