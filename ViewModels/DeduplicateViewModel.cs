using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class DeduplicateViewModel : ObservableObject
{
    private readonly IPhotoService _photoService = AppServices.PhotoService;

    [ObservableProperty]
    private string _folderPath = "";

    [ObservableProperty]
    private int _aHashThreshold = 8;

    [ObservableProperty]
    private int _dHashThreshold = 10;

    [ObservableProperty]
    private ObservableCollection<DuplicateGroup> _groups = new();

    [ObservableProperty]
    private string _statusText = "选择文件夹并点击扫描以查找相似图片。";

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            StatusText = "请先选择有效文件夹。";
            return;
        }

        IsBusy = true;
        StatusText = "正在计算感知哈希…";
        try
        {
            var files = await _photoService.ScanAsync(FolderPath, CancellationToken.None);
            var found = await _photoService.FindDuplicatesAsync(
                files, AHashThreshold, DHashThreshold, CancellationToken.None);

            Groups.Clear();
            foreach (var g in found) Groups.Add(g);

            StatusText = found.Count == 0
                ? "未找到相似图片组。"
                : $"找到 {found.Count} 组相似图片（保留每组首个，其余可删除）。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        int removed = 0;
        foreach (var g in Groups)
        {
            for (int i = 0; i < g.Members.Count; i++)
            {
                if (i == g.KeepIndex) continue;
                try
                {
                    File.Delete(g.Members[i].Path);
                    removed++;
                }
                catch
                {
                    // Ignore files that are locked or already gone.
                }
            }
        }

        StatusText = $"已删除 {removed} 个重复文件。";
        Groups.Clear();
    }
}
