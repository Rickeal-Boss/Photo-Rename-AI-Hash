using CommunityToolkit.Mvvm.ComponentModel;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appTitle = "Photo Rename AI · Hash";

    [ObservableProperty]
    private string _version = AppSettings.AppVersion;
}
