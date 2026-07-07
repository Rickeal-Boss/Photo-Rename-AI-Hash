using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class OrganizeViewModel : ObservableObject
{
    private readonly IPhotoService _photoService = AppServices.PhotoService;

    [ObservableProperty]
    private string _folderPath = "";

    [ObservableProperty]
    private string _pattern = "{yyyy}{MM}{dd}_{n}";

    [ObservableProperty]
    private ObservableCollection<PhotoFile> _preview = new();

    [ObservableProperty]
    private string _statusText = "选择一个文件夹以开始整理。";

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
        StatusText = "正在扫描…";
        try
        {
            var files = await _photoService.ScanAsync(FolderPath, CancellationToken.None);
            Preview.Clear();
            int n = 1;
            foreach (var f in files)
            {
                f.NewName = BuildName(f, Pattern, n++);
                f.Status = "待重命名";
                Preview.Add(f);
            }

            StatusText = $"共找到 {Preview.Count} 张图片。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Preview.Count == 0) return;

        IsBusy = true;
        try
        {
            var items = Preview
                .Where(p => !string.IsNullOrEmpty(p.NewName))
                .Select(p => new RenameItem
                {
                    SourcePath = p.Path,
                    DestinationPath = Path.Combine(Path.GetDirectoryName(p.Path)!, p.NewName!),
                })
                .ToList();

            int done = await _photoService.RenameAsync(items, CancellationToken.None);
            foreach (var p in Preview) p.Status = "已重命名";
            StatusText = $"已重命名 {done} 个文件。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildName(PhotoFile f, string pattern, int index)
    {
        var dt = f.LastModified;
        return pattern
            .Replace("{yyyy}", dt.ToString("yyyy"))
            .Replace("{MM}", dt.ToString("MM"))
            .Replace("{dd}", dt.ToString("dd"))
            .Replace("{HH}", dt.ToString("HH"))
            .Replace("{mm}", dt.ToString("mm"))
            .Replace("{n}", index.ToString("D3"))
            .Replace("{name}", Path.GetFileNameWithoutExtension(f.Name));
    }
}
