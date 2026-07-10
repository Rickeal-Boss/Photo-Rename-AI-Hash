using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Services;

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

        // Apply saved theme preference before the window is shown (avoids flash).
        var settings = AppServices.SettingsService.Load();
        ThemeHelper.Apply(MainWindow, settings.Theme);

        MainWindow.Activate();
    }
}
