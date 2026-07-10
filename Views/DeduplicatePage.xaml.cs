using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoRenameAIHash.Services;
using PhotoRenameAIHash.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoRenameAIHash.Views;

public sealed partial class DeduplicatePage : Page
{
    // 绑定到 AppServices 单例：页面实例可随标签页切换被回收，但 VM 与运行中的扫描/删除任务始终存活
    public DeduplicateViewModel ViewModel => AppServices.DeduplicateVm;

    public DeduplicatePage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(PhotoRenameAIHash.App.MainWindow!);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) ViewModel.FolderPath = folder.Path;
    }
}
