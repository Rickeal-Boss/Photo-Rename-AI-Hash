using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Helpers;

public static class ThemeHelper
{
    public static void Apply(Window? window, AppTheme theme)
    {
        ElementTheme effective = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = effective;
        }

        // P0-3：标题栏一体化后，系统标题栏按钮前景色需按有效主题手动设置，
        // 否则浅色主题下按钮可能因默认白色而不可见。仅 Win11+ 支持自定义。
        if (window == null) return;
        var titleBar = window.AppWindow.TitleBar;
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;

        // P0-1 修复：跟随系统主题下不能使用 Application.Current.RequestedTheme——
        // 它返回的是 manifest 静态声明值（默认 Light），不随系统主题变化；
        // 应改用根元素 ActualTheme（RequestedTheme 赋值后即解析出实际生效主题，
        // Light/Dark 显式分支同样正确）。
        bool isDark = (window?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        var fg = isDark ? Windows.UI.Colors.White : Windows.UI.Colors.Black;
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonPressedForegroundColor = fg;
        titleBar.ButtonInactiveForegroundColor = fg;
    }
}
