using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Helpers;

public static class ThemeHelper
{
    public static void Apply(Window? window, AppTheme theme)
    {
        if (window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
