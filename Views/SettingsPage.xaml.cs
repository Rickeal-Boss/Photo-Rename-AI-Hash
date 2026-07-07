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
}
