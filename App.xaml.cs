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
        // 在创建任何窗口之前强制 Per-Monitor v2 DPI 感知（双保险）。
        // 若 embedded app.manifest 已正确声明 dpiAwareness=PerMonitorV2，此调用会返回
        // ERROR_ACCESS_DENIED（视为已生效）；否则在此补足，避免 125%+ 缩放下整窗被位图拉伸发虚。
        DpiHelper.EnsurePerMonitorV2();

        MainWindow = new MainWindow();

        // Apply saved theme preference before the window is shown (avoids flash).
        var settings = AppServices.SettingsService.Load();
        ThemeHelper.Apply(MainWindow, settings.Theme);

        MainWindow.Activate();
    }
}
