using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PhotoRenameAIHash.ViewModels;
using System;
using System.Collections.Generic;
using Windows.Foundation;

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

        // P2-12：主题由 App.OnLaunched 统一应用（窗口显示前），此处不再重复 Load settings。

        // P0-2：Mica 背景——仅 Win11（Build 22000+）启用；Win10 保持实底背景，规避无 Mica 时的异常底色
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            RootGrid.Background = null; // 让 Mica 透出
        }

        // P1-2+3：返回按钮走统一返回逻辑
        NavView.BackRequested += (_, __) => GoBack();

        // ②（第十一轮真机验收）：给导航项图标挂上「按压旋转 / 悬停放大」的指针反馈。
        // 放在构造函数里（InitializeComponent 之后）即可——三个 NavigationViewItem 由 XAML 静态声明，
        // 此时已全部就位；变换与指针事件都不依赖布局完成，故无需等 Loaded。
        AttachNavIconFeedback();

        // D-1.1：给窗口一个合理初始尺寸——默认尺寸偏小，而 NavigationView 为 Left 常驻模式，
        // 内容区会被导航栏挤压（OrganizePage 的横向控件组在窄宽度下会溢出裁剪）。
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));

        // 最小窗口尺寸约束：OverlappedPresenter.PreferredMinimumWidth/Height 需 Windows App SDK 1.7+，
        // 本项目锁定 1.5（Microsoft.WindowsAppSDK 1.5.240428000），该 API 不存在，故此处不做最小尺寸限制。
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

    // ═══════════════════════════════════════════════════════════════════
    // ② 导航项图标的按压 / 悬停动效（第十一轮真机验收）
    //
    // 用户反馈「WinUI 这些按钮按下时的动态效果消失了」。查仓库历史没有任何旋转动效相关
    // commit，说明用户记忆中的那版从未入库（或在别的机器上的临时版本），此处重新实现并入库。
    //
    // 【为什么不用「隐式样式 + VisualStateManager」】
    //   1) VisualStateManager.VisualStateGroups 是**附加集合**属性，Style 的 Setter 无法设置
    //      集合，所以「隐式样式里写 VSM」在 WinUI 语法上就不成立；
    //   2) VSM 的 Storyboard.TargetName 只在 ControlTemplate 的名字域内解析，拿不到模板外
    //      （即 XAML 里手写的）FontIcon；
    //   3) 要保住 VSM 路线就必须整份复制 NavigationViewItem 的默认 ControlTemplate
    //      （约 200 行，且随 WinAppSDK 版本漂移），而本机无 .NET SDK、无法编译验证，
    //      一旦模板抄错就是整个导航栏不可用——风险远大于收益。
    //   故改用事件方式：遍历导航项、直接驱动图标上的 RotateTransform / ScaleTransform。
    //   与「隐式样式」等价的收益（新增菜单项自动继承）由「遍历全部导航项」这一做法保住。
    //
    // 【动效规格】对齐 Fluent 标准
    //   PointerEntered  → 缩放 1.08（轻微放大，提示可点）
    //   PointerPressed  → 旋转 15°（轻量反馈；刻意不用 360°，整圈会让图标闪一下）
    //   Released / Exited / Canceled / CaptureLost → 复位（0° / 1.0）
    //   过渡时长 120ms，落在 100–150ms 区间内
    // 只作用于图标（FontIcon）自身，不动 NavigationViewItem 的布局。
    // ═══════════════════════════════════════════════════════════════════
    private const double NavIconHoverScale = 1.08;
    private const double NavIconPressedAngle = 15.0;
    private static readonly Duration NavIconTransition = new(TimeSpan.FromMilliseconds(120));

    private void AttachNavIconFeedback()
    {
        foreach (var item in EnumerateNavItems())
        {
            // 只对 FontIcon 生效；图标若是别种 IconElement（IconSourceElement 等）就跳过，不抛错
            if (item.Icon is not FontIcon icon) continue;

            // 以图标中心为旋转 / 缩放基准（0.5,0.5 相对自身）
            icon.RenderTransformOrigin = new Point(0.5, 0.5);

            var rotate = new RotateTransform();
            var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(scale);
            icon.RenderTransform = group;

            // 每个图标只建一条 Storyboard（角度 + 两轴缩放三个补间），状态切换时改 To 再 Begin()。
            // 「同一条 Storyboard 反复 Begin」是确定的重启语义，天然避开多条 Storyboard 抢同一属性时
            // 谁 HoldEnd 生效的不确定性；而重启会把未改动的补间重放到「当前值 → 当前值」，视觉无副作用。
            var angleTween = CreateTween(rotate, "Angle");
            var scaleXTween = CreateTween(scale, "ScaleX");
            var scaleYTween = CreateTween(scale, "ScaleY");
            var storyboard = new Storyboard();
            storyboard.Children.Add(angleTween);
            storyboard.Children.Add(scaleXTween);
            storyboard.Children.Add(scaleYTween);

            // 用两个「期望终值」变量记住当前状态：Unpress（松开）只把角度归零、
            // 不动缩放，这样「按着不放并移出」再松开时不会误把悬停放大也一起复位。
            double targetAngle = 0;
            double targetScale = 1;

            void Apply()
            {
                angleTween.To = targetAngle;
                scaleXTween.To = targetScale;
                scaleYTween.To = targetScale;
                storyboard.Begin();
            }

            void Hover(object _, PointerRoutedEventArgs __)
            {
                targetAngle = 0;
                targetScale = NavIconHoverScale;
                Apply();
            }

            void Press(object _, PointerRoutedEventArgs __)
            {
                targetAngle = NavIconPressedAngle;
                Apply();
            }

            void Unpress(object _, PointerRoutedEventArgs __)
            {
                targetAngle = 0;
                Apply();
            }

            void Reset(object _, PointerRoutedEventArgs __)
            {
                targetAngle = 0;
                targetScale = 1;
                Apply();
            }

            // 用 AddHandler(handledEventsToo: true)：ListViewItem / NavigationViewItem 自身会在
            // PointerPressed 里标记 Handled（选中、指针捕获），XAML 上的 `PointerPressed="..."`
            // 或普通 += 订阅在这种情形下根本不会触发，必须显式要求「已被处理的事件也回调」。
            item.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(Hover), true);
            item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Press), true);
            item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Unpress), true);
            item.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Reset), true);
            item.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Reset), true);
            // NavigationViewItem 按下时会捕获指针，松开或指针被抢走时靠这条兜底复位，
            // 避免出现「旋转卡在 15° 回不来」
            item.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Reset), true);
        }
    }

    // 导航栏里全部 NavigationViewItem（菜单项 + 页脚项）。与 FindNavItem 同为「遍历两处」，
    // 但用途不同（此处要全量挂事件），故不复用。
    private IEnumerable<NavigationViewItem> EnumerateNavItems()
    {
        foreach (var i in NavView.MenuItems)
        {
            if (i is NavigationViewItem n) yield return n;
        }
        foreach (var i in NavView.FooterMenuItems)
        {
            if (i is NavigationViewItem n) yield return n;
        }
    }

    // 建一条「目标对象.属性 → 目标值」的补间。目标既可以是 FrameworkElement 也可以是 Transform
    // （此处都是 Transform）：代码里显式 SetTarget，不依赖 XAML 名字域。
    private static DoubleAnimation CreateTween(DependencyObject target, string property)
    {
        var animation = new DoubleAnimation
        {
            Duration = NavIconTransition,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // 变换类动画在 WinUI 里通常已是独立动画（合成线程执行），此处置 true 是保险：
            // 独立动画会忽略该标志，而万一被判为依赖动画，缺了它会在运行期直接抛异常。
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
}
