using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        DpiAwarenessText = DpiAwarenessHelper.GetAwarenessText();
    }

    /// <summary>
    /// 通过 user32 P/Invoke 读取当前线程的 DPI 感知模式。
    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4。
    /// </summary>
    private static class DpiAwarenessHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

        private static readonly IntPtr PerMonitorV2 = new(-4);

        public static string GetAwarenessText()
        {
            try
            {
                IntPtr ctx = GetThreadDpiAwarenessContext();
                int awareness = GetAwarenessFromDpiAwarenessContext(ctx);
                string baseName = awareness switch
                {
                    0 => "Unaware",
                    1 => "System-Aware",
                    2 => "Per-Monitor",
                    _ => $"Unknown({awareness})",
                };
                bool isV2 = AreDpiAwarenessContextsEqual(ctx, PerMonitorV2);
                return isV2 ? $"Per-Monitor v2（{baseName}）" : baseName;
            }
            catch (Exception ex)
            {
                return $"N/A（{ex.GetType().Name}）";
            }
        }
    }
}
