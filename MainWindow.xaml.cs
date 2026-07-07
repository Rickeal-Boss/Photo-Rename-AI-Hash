using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        ["Deduplicate"] = typeof(Views.DeduplicatePage),
        ["Settings"] = typeof(Views.SettingsPage),
        ["About"] = typeof(Views.AboutPage),
    };

    public MainWindow()
    {
        this.InitializeComponent();
        if (_pages.TryGetValue("Organize", out var first))
        {
            ContentFrame.Navigate(first);
        }

        var settings = AppServices.SettingsService.Load();
        ThemeHelper.Apply(this, settings.Theme);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item &&
            item.Tag is string tag &&
            _pages.TryGetValue(tag, out var type))
        {
            ContentFrame.Navigate(type);
        }
    }
}
