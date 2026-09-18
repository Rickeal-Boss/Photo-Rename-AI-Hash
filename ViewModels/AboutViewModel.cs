using CommunityToolkit.Mvvm.ComponentModel;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _version = ResolveVersion();

    /// <summary>P2-2：版本以 MSIX 包清单为事实源；未打包（本地调试）回退常量。</summary>
    private static string ResolveVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            return AppSettings.AppVersion;
        }
    }

    [ObservableProperty]
    private string _description =
        "基于 WinUI 3 与 Windows App SDK 构建的图片整理工具。\n" +
        "采用 MVVM 架构、响应式导航视图与明暗主题支持。\n\n" +
        "功能：\n" +
        "• 整理：按拍摄/修改日期与自定义规则批量重命名、复制、移动或按日期归档";
}
