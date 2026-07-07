using Microsoft.UI.Xaml;

namespace PhotoRenameAIHash;

public partial class App : Application
{
    public static Window? MainWindow { get; set; }

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
