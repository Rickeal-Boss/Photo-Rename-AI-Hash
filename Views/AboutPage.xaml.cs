using Microsoft.UI.Xaml.Controls;
using PhotoRenameAIHash.ViewModels;

namespace PhotoRenameAIHash.Views;

public sealed partial class AboutPage : Page
{
    public AboutViewModel ViewModel { get; } = new();

    public AboutPage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
    }
}
