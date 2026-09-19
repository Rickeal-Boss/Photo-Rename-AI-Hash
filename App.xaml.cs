using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash;

public partial class App : Application
{
    public static Window? MainWindow { get; set; }

    public App()
    {
        // 在任何可能抛异常的代码之前挂上兜底：AppServices 的静态单例（含 OrganizeViewModel 构造里
        // 同步读盘）在 OnLaunched 首次触碰时才构造，任一失败即 TypeInitializationException，
        // 此前会直接闪退且零日志。
        // 反向风险是 AsyncRelayCommand 会把命令内异常收进 ExecutionTask 而不重抛，
        // 这类失败既不崩溃也不提示，所以第三道把「被吞掉的」异常也留痕。
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException; // 线程池致命异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;          // 被吞掉的 Task 异常
        this.UnhandledException += OnAppUnhandledException;                          // XAML 栈上的未处理异常

        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Launch(args);
        }
        catch (Exception ex)
        {
            // 启动失败（如静态单例构造异常）：此前是零日志闪退，这里至少留下现场。
            // 重新抛出以保持原有终止行为，不做静默吞掉——半启动状态无法安全继续。
            WriteCrashLog("OnLaunched 启动失败", ex);
            throw;
        }
    }

    private static void Launch(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 在创建任何窗口之前强制 Per-Monitor v2 DPI 感知（双保险）。
        // 若 embedded app.manifest 已正确声明 dpiAwareness=PerMonitorV2，此调用会返回
        // ERROR_ACCESS_DENIED（视为已生效）；否则在此补足，避免 125%+ 缩放下整窗被位图拉伸发虚。
        DpiHelper.EnsurePerMonitorV2();

        MainWindow = new MainWindow();

        // Apply saved theme preference before the window is shown (avoids flash).
        var settings = AppServices.SettingsService.Load();
        ThemeHelper.Apply(MainWindow, settings.Theme);

        MainWindow.Activate();
    }

    /// <summary>崩溃日志路径：与 settings.json 同目录，便于用户直接把现场提供给我们排障。</summary>
    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoRenameAIHash", "crash.log");

    private static void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI 未处理异常（已拦截，应用继续运行）", e.Exception);
        // 置 Handled 避免进程直接退出：多数非致命异常（某个页面 / 命令失败）拦截后仍可继续操作
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        // 线程池 / 终结器线程上的致命异常：无法安全恢复，仅留痕后任其终止
        WriteCrashLog("AppDomain 未处理异常（进程将终止）", e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // AsyncRelayCommand 会把命令内异常收进 ExecutionTask 而不重抛：既不崩溃也不提示。
        // 标记已观察（避免 GC 终结时升级为 AppDomain.UnhandledException 直接杀进程）并留痕。
        WriteCrashLog("未观察的 Task 异常（多来自 AsyncRelayCommand）", e.Exception);
        e.SetObserved();
    }

    /// <summary>最佳努力写日志：任何失败都必须被吞掉，否则兜底逻辑本身会变成新的崩溃源（甚至递归）。</summary>
    private static void WriteCrashLog(string kind, object? exception)
    {
        try
        {
            var path = CrashLogPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志不可写（磁盘满 / 权限不足）：放弃，绝不抛出二次异常
        }
    }
}
