using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public OrganizePage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
        // 重命名实际执行前，由 VM 通过该回调弹出备份文件夹选择
        ViewModel.BackupFolderPicker = static () => PickFolderAsync();
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

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(PhotoRenameAIHash.App.MainWindow!);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
