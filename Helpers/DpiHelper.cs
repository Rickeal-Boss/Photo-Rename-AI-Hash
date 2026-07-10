using System;
using System.Runtime.InteropServices;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// DPI 感知修复工具。
/// 背景：WinUI 3 自包含(MSIX)打包下，若 .exe 未嵌入含 dpiAwareness=PerMonitorV2 的 Win32 清单，
/// 进程 DPI 感知可能落到 Unaware，导致 OS 在 125%+ 缩放下对整窗做位图拉伸 → 文字发虚。
/// 这里在窗口创建前用 user32 强制 SetProcessDpiAwarenessContext(PerMonitorV2) 作为双保险。
/// </summary>
public static class DpiHelper
{
    // DPI_AWARENESS_CONTEXT 取值（参见 Win32 文档）
    // 0 = UNAWARE, 1 = SYSTEM_AWARE, 2 = PER_MONITOR_AWARE, -3 = PER_MONITOR_AWARE_V2 之前的别名,
    // -4 = PER_MONITOR_AWARE_V2
    private static readonly IntPtr DpiAwarenessContextUnaware = new(0);
    private static readonly IntPtr DpiAwarenessContextSystemAware = new(1);
    private static readonly IntPtr DpiAwarenessContextPerMonitorAware = new(2);
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    /// <summary>EnsurePerMonitorV2() 调用结果。null=未执行/异常；true=成功设定或本已设定；false=设定失败。</summary>
    public static bool? ForceSucceeded { get; private set; }

    /// <summary>
    /// 在 App.OnLaunched 创建 MainWindow 之前调用，强制进程 DPI 感知为 Per-Monitor v2。
    /// 若清单/运行时已设定（返回 ERROR_ACCESS_DENIED），视为无需动作，ForceSucceeded 记为 true。
    /// </summary>
    public static void EnsurePerMonitorV2()
    {
        try
        {
            bool ok = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
            if (ok)
            {
                ForceSucceeded = true;
                return;
            }

            int err = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED(5)：进程 DPI 感知已被（清单/运行时）设定，不允许二次更改 —— 属正常情况。
            ForceSucceeded = err == 5;
        }
        catch (Exception)
        {
            ForceSucceeded = null;
        }
    }

    /// <summary>读取当前线程 DPI 感知模式文本，用于关于页自诊。</summary>
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
            bool isV2 = AreDpiAwarenessContextsEqual(ctx, DpiAwarenessContextPerMonitorAwareV2);
            return isV2 ? $"Per-Monitor v2（{baseName}）" : baseName;
        }
        catch (Exception ex)
        {
            return $"N/A（{ex.GetType().Name}）";
        }
    }

    /// <summary>仅供诊断展示：当前进程的感知上下文对应的基础枚举值。</summary>
    public static string GetForceResultText()
    {
        return ForceSucceeded switch
        {
            true => "已生效（清单/运行时已设定 PMv2，或本函数强制成功）",
            false => "强制失败（可能被 .exe 兼容性 DPI 覆盖；请检查 exe 属性→兼容性）",
            null => "未执行/异常",
        };
    }
}
