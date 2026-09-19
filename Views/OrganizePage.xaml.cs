using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PhotoRenameAIHash.Services;
using PhotoRenameAIHash.ViewModels;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoRenameAIHash.Views;

public sealed partial class OrganizePage : Page
{
    // 绑定到 AppServices 单例：页面实例可随标签页切换被回收，但 VM 与运行中的任务始终存活
    public OrganizeViewModel ViewModel => AppServices.OrganizeVm;

    // 日志 TextBox 内部的 ScrollViewer 懒查找缓存（WinUI 3 的 TextBox 没有 ScrollToEnd，那是 WPF API）
    private ScrollViewer? _logScrollViewer;

    public OrganizePage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
        // 重命名实际执行前，由 VM 通过该回调弹出备份文件夹选择
        ViewModel.BackupFolderPicker = static () => PickFolderAsync();
        // P1-5：日志更新时自动滚动到底部（NavigationCacheMode=Required 仅构造一次，无重复订阅）
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrganizeViewModel.LogText))
            {
                // VM 属性可能从非 UI 线程触发（Progress 回调经同步上下文，多数在 UI 线程，
                // 但统一包一层 TryEnqueue 兜底，滚动操作必须在 UI 线程执行）
                this.DispatcherQueue.TryEnqueue(() => ScrollLogToEnd());
            }
        };
    }

    /// <summary>
    /// 每次导航进入本页（本页为 NavigationCacheMode=Required，实例复用但本回调仍会触发）
    /// 把引擎选择同步为磁盘最新值：VM 是单例，其 AiProviderIndex 不会自动感知「设置」页的保存，
    /// 不同步就会出现「设置页显示 A、整理页实际用 B」的谎报。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.SyncProviderFromDisk();
    }

    // WinUI 3 TextBox 自动滚到底：经可视树找到内部 ScrollViewer 后 ChangeView（禁用动画避免高频抖动）
    private void ScrollLogToEnd()
    {
        if (_logScrollViewer is null)
        {
            _logScrollViewer = FindDescendantScrollViewer(LogBox);
        }

        _logScrollViewer?.ChangeView(null, _logScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private async void PickSource_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null) ViewModel.SourceFolder = folder;
    }

    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null) ViewModel.OutputFolder = folder;
    }

    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        // 重命名模式输出即源目录；其余模式用输出目录。为空或不存在则回退源目录。
        var path = ViewModel.OutputFolder;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
            path = ViewModel.SourceFolder;

        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
        {
            return; // 无有效目录：静默忽略（按钮为便捷入口，不做打断式提示）
        }

        try
        {
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch
        {
            // 某些环境（策略限制/路径不可访问）下 LaunchFolderPathAsync 不可用，退化为 explorer
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch
            {
                // 两者均失败则忽略，不影响主流程
            }
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var mainWindow = PhotoRenameAIHash.App.MainWindow;
        if (mainWindow != null)
        {
            var hwnd = WindowNative.GetWindowHandle(mainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
