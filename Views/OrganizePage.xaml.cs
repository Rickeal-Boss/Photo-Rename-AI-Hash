using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoRenameAIHash.ViewModels;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoRenameAIHash.Views;

public sealed partial class OrganizePage : Page
{
    public OrganizeViewModel ViewModel { get; } = new();

    public OrganizePage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
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
