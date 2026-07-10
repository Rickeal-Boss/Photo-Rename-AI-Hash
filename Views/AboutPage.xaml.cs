using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.ViewModels;

namespace PhotoRenameAIHash.Views;

public sealed partial class AboutPage : Page
{
    public AboutViewModel ViewModel { get; } = new();

    public static readonly DependencyProperty DpiScaleTextProperty =
        DependencyProperty.Register(nameof(DpiScaleText), typeof(string), typeof(AboutPage),
            new PropertyMetadata("检测中…"));

    public static readonly DependencyProperty DpiAwarenessTextProperty =
        DependencyProperty.Register(nameof(DpiAwarenessText), typeof(string), typeof(AboutPage),
            new PropertyMetadata("检测中…"));

    public static readonly DependencyProperty DpiForceTextProperty =
        DependencyProperty.Register(nameof(DpiForceText), typeof(string), typeof(AboutPage),
            new PropertyMetadata("—"));

    public string DpiScaleText
    {
        get => (string)GetValue(DpiScaleTextProperty);
        set => SetValue(DpiScaleTextProperty, value);
    }

    public string DpiAwarenessText
    {
        get => (string)GetValue(DpiAwarenessTextProperty);
        set => SetValue(DpiAwarenessTextProperty, value);
    }

    public string DpiForceText
    {
        get => (string)GetValue(DpiForceTextProperty);
        set => SetValue(DpiForceTextProperty, value);
    }

    public AboutPage()
    {
        this.InitializeComponent();
        this.DataContext = ViewModel;
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WinUI 3 桌面应用的正确做法：用 XamlRoot.RasterizationScale 读取 DPI 缩放。
        // （DisplayInformation.GetForCurrentView() 在 WinUI 3 桌面未被正确暴露，会抛 0x80070490。）
        try
        {
            XamlRoot? root = this.XamlRoot ?? this.Content?.XamlRoot;
            if (root is not null)
            {
                double scale = root.RasterizationScale;
                int pct = (int)Math.Round(scale * 100);
                DpiScaleText = $"{pct}%  (RasterizationScale = {scale:0.###})";
            }
            else
            {
                DpiScaleText = "不可用（XamlRoot 为空）";
            }
        }
        catch (Exception ex)
        {
            DpiScaleText = $"读取失败：{ex.GetType().Name}";
        }

        // 直读进程 DPI 感知模式，确认是否为 Per-Monitor v2（发虚排查根因 #1）。
        DpiAwarenessText = DpiHelper.GetAwarenessText();
        DpiForceText = DpiHelper.GetForceResultText();
    }
}
