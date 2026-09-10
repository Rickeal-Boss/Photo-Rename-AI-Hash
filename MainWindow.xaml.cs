using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;
using PhotoRenameAIHash.ViewModels;
using System;
using System.Collections.Generic;

namespace PhotoRenameAIHash;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private readonly Dictionary<string, Type> _pages = new()
    {
        ["Organize"] = typeof(Views.OrganizePage),
        ["Settings"] = typeof(Views.SettingsPage),
        ["About"] = typeof(Views.AboutPage),
    };

    public MainWindow()
    {
        this.InitializeComponent();

        // P0-3：标题栏一体化——内容延伸至标题栏，自绘 AppTitleBar 接管拖拽区
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        // P1-1 修复：加 ContentFrame.Content is null 守卫——XAML 中 Organize 项 IsSelected="True"
        // 会在初始化期同步触发 SelectionChanged 完成首次导航，此处若无条件 Navigate 会造成
        // 双重导航 + 返回栈残留一条 Organize 记录
        if (_pages.TryGetValue("Organize", out var first) && ContentFrame.Content is null)
        {
            ContentFrame.Navigate(first);
        }

        var settings = AppServices.SettingsService.Load();
        ThemeHelper.Apply(this, settings.Theme);

        // P0-2：Mica 背景——仅 Win11（Build 22000+）启用；Win10 保持实底背景，规避无 Mica 时的异常底色
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            RootGrid.Background = null; // 让 Mica 透出
        }

        // P1-2+3：返回按钮走统一返回逻辑
        NavView.BackRequested += (_, __) => GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item &&
            item.Tag is string tag &&
            _pages.TryGetValue(tag, out var type))
        {
            // 返回导航同步高亮时会再次触发 SelectionChanged，此时已处于目标页，跳过避免重复入栈
            if (ContentFrame.Content?.GetType() == type)
            {
                return;
            }
            ContentFrame.Navigate(type);
        }
    }

    // P1-2+3：按 tag 导航（设置选中项，由 SelectionChanged 完成导航并同步高亮）
    private void NavigateTo(string tag)
    {
        var item = FindNavItem(tag);
        if (item != null)
        {
            NavView.SelectedItem = item;
        }
    }

    private NavigationViewItem? FindNavItem(string tag)
    {
        foreach (var i in NavView.MenuItems)
        {
            if (i is NavigationViewItem n && n.Tag as string == tag) return n;
        }
        foreach (var i in NavView.FooterMenuItems)
        {
            if (i is NavigationViewItem n && n.Tag as string == tag) return n;
        }
        return null;
    }

    // P1-2+3：返回导航——回退 Frame 后按当前页面类型反查 tag，同步侧边栏高亮
    private void GoBack()
    {
        if (!ContentFrame.CanGoBack) return;

        ContentFrame.GoBack();
        var current = ContentFrame.Content?.GetType();
        foreach (var kv in _pages)
        {
            if (kv.Value == current)
            {
                var item = FindNavItem(kv.Key);
                if (item != null)
                {
                    NavView.SelectedItem = item;
                }
                break;
            }
        }
    }

    // P1-2：Ctrl+1/2/3 页面导航加速键
    private void NavAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var tag = args.KeyboardAccelerator?.Key switch
        {
            Windows.System.VirtualKey.Number1 => "Organize",
            Windows.System.VirtualKey.Number2 => "Settings",
            Windows.System.VirtualKey.Number3 => "About",
            _ => null,
        };
        if (tag != null)
        {
            NavigateTo(tag);
            args.Handled = true;
        }
    }

    // P1-3：Alt+← 返回加速键
    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        GoBack();
        args.Handled = true;
    }
}
