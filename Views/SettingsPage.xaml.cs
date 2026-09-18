using Microsoft.UI.Xaml.Controls;
using PhotoRenameAIHash.ViewModels;

namespace PhotoRenameAIHash.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A-07：本页为 NavigationCacheMode="Required"（整会话只构造一次），而 DefaultFolder 由
        // 整理页拥有；每次导航回来时从磁盘同步该只读展示字段，避免显示陈旧值。
        ViewModel.SyncOwnedFieldsFromDisk();
    }
}
