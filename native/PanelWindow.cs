using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading.Tasks;
using ShapePath = System.Windows.Shapes.Path;

namespace PivkeyOrganizer
{
    public sealed class PanelWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int GwlHwndParent = -8;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExTransparent = 0x00000020L;
        private const int WmNcLButtonDown = 0x00A1;
        private const int WmSysCommand = 0x0112;
        private const int ScMinimize = 0xF020;
        private const int WmNcHitTest = 0x0084;
        private const int ScSizeBottomRight = 0xF008;
        private const int HtBottomRight = 17;
        private const int HtTransparent = -1;
        private const int WmDpiChanged = 0x02E0;
        private const int WmSize = 0x0005;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const int WcaAccentPolicy = 19;
        private const uint AccentDisabled = 0;
        private const uint AccentEnableBlurBehind = 3;
        private const uint AccentEnableAcrylicBlurBehind = 4;
        // ACCENT_ENABLE_ACRYLICBLURBEHIND 需要 Win10 1809（build 17134）起才稳定；
        // Win11（build 22000）起 SWCA 亚克力背板按窗口矩形绘制，无视逐像素 alpha 与
        // 窗口 region，白色雾色会从圆角外四个角漏出，因此 Win11 停用（玻璃感由
        // WPF 渐变层独立承担），Win10 保持真亚克力。
        private const int MinAcrylicBuild = 17134;
        private const int MaxAcrylicBuild = 21999;
        private static int osBuild = -1;

        private readonly DesktopWindow host;
        private readonly string panelId;
        private readonly Border frame;
        private readonly Border header;
        private readonly TextBlock title;
        private readonly TextBlock count;
        private readonly Button pinButton;
        private readonly Button collapseButton;
        private readonly Button viewButton;
        private readonly Button sizeButton;
        private readonly Button searchButton;
        private readonly StackPanel headerToolsPanel;
        private readonly Border countBadge;
        private readonly Border searchBar;
        private readonly TextBox searchBox;
        private readonly ScrollViewer scroll;
        // 外层使用竖向 StackPanel，行内再用 Grid 等分可用宽度。
        // WrapPanel 会按最后一行实际子项宽度从左侧开始排，项目数不是整行倍数时
        // 很容易在右侧留下大块空白；行内 Grid 可以让不完整的最后一行也保持均衡。
        private readonly StackPanel itemsPanel;
        private readonly Border resizeGrip;
        private readonly Border mark;
        private readonly Image markImage;
        private readonly Border capsuleView;  // 图标胶囊（折叠 + 胶囊模式时显示的 48×48 小方块）
        private readonly ShapePath iconShape; // 胶囊上的内置 Phosphor 字形（与自定义图片互斥显示）
        private readonly Image capsuleImage;  // 胶囊上的自定义图片（与内置字形互斥显示）
        private readonly Border breathOverlay; // 新文件到达呼吸光晕（覆盖整个面板的 accent 描边）
        private readonly Border capsuleHighlight; // 胶囊内高光（内缩 1px 的半透明白描边）
        private readonly Grid capsuleInner;       // 胶囊内容（图标层 26×26，hover 缩放动画载体）
        private bool capsuleHovered;              // 胶囊 hover 态（MouseEnter/MouseLeave 驱动）
        private readonly DispatcherTimer scrollAnimationTimer;
        private readonly DispatcherTimer layoutAnimationTimer;
        // 图标解码缓存：按估算字节数（PixelWidth x PixelHeight x 4）限制内存，替代旧的条目数上限 600。
        // 256/512px 大图若按条目计数会轻松撑爆内存预算，按字节逐出更贴合实际内存目标。
        private static readonly Dictionary<string, ImageSource> decodedImageCache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        private static readonly Queue<string> decodedImageCacheOrder = new Queue<string>(); // 插入序（逐出顺序）
        private static long decodedImageCacheBytes; // 当前缓存估算字节
        private static readonly object decodedImageCacheLock = new object();
        private const long MaxDecodedImageCacheBytes = 32L * 1024 * 1024; // 缓存上限 32MB
        private const long MinDecodedImageCacheBytes = 24L * 1024 * 1024;  // 低水位：逐出到 24MB 停止（滞回，避免边界反复抖动）
        // 解码任务去重与并发上限：同一 key 只排一次队；同时最多 4 个后台解码，避免批量大图同时解压占满 CPU/内存
        private static readonly HashSet<string> pendingIconDecodes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Threading.Semaphore iconDecodeSlots = new System.Threading.Semaphore(4, 4);
        private IntPtr hwnd;
        private bool pinned;
        private bool collapsed;
        internal bool capsuleMode;             // 图标胶囊模式（全局开关，与前端同步）
        internal string categoryIcon = "folder"; // 分类图标：Phosphor 图标名 或 data: 图片 URL
        private ImageSource capsuleIconSource; // 自定义图片解码结果（categoryIcon 变化时重解）
        private bool dragGlowActive;         // 拖拽悬停光晕是否已开启（避免 DragOver 高频重复设置）
        private double expandedHeight = 238;
        private int lastRevision;
        private string contentItemsSignature = "";
        private string categoryName = "分区";
        private bool readOnlyPanel;
        private Color accent = Color.FromRgb(140, 115, 80);
        private Color themeAccent = Color.FromRgb(140, 115, 80);
        private Color paperSurface = Color.FromRgb(255, 255, 255);
        private bool darkTheme;
        private bool markShowsImage;
        private int glassOpacity = 88;
        private string materialMode = "acrylic";   // 材质模式：acrylic=真亚克力模糊（DeskBox 式系统材质），solid=纯色渐变
        private bool acrylicApplied;               // 当前窗口是否已成功挂上 accent 亚克力背景
        private bool compact;
        private double itemSize = 76;
        private double iconSize = 54;
        private double itemGap = 6;
        private double labelSize = 12;
        // 界面缩放（应用级，百分比，80-130）与系统 DPI 缩放（GetDpiForWindow/96）。
        // WPF 的 Width/FontSize 等 DIP 属性只乘 uiScale，绝不重复乘 dpiScale；只有位图像素请求需要乘 dpiScale。
        private double uiScale = 100;
        private double dpiScale = 1.0;
        private bool narrowHeader;             // 标题条窄态（宽度 < 200 DIP）：只留标题 + 折叠 + 菜单按钮
        private string itemAlignment = "left";
        private int itemColumns;
        private int renderedColumns;
        private bool showLabels = true;
        private bool showExtensions;
        private string labelPosition = "bottom";
        private bool autoHide;
        private int autoHideDelaySeconds = 2;
        private bool autoHidden;
        private DateTime mouseLeftAt = DateTime.UtcNow;
        private readonly HashSet<string> selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string searchText = "";
        private Point itemDragOrigin;
        private string itemDragPath;
        private bool itemDragReadOnly;
        private string viewMode = "grid";
        private string sortMode = "name";
        private string pendingViewMode;
        private string pendingSortMode;
        private readonly List<Dictionary<string, object>> currentItems = new List<Dictionary<string, object>>();
        private bool resizing;
        private ResizeDirection resizeDirection;   // 当前拖拽方向（Windows 式八方向边框缩放）
        private Point resizeStartScreen;
        private double resizeStartLeft;
        private double resizeStartTop;
        private double resizeStartWidth;
        private double resizeStartHeight;
        private readonly List<Border> resizeAdorners = new List<Border>();  // 四边+四角缩放热区（折叠时统一隐藏）
        private bool capsuleGestureActive;   // 胶囊按下（点击/拖动判定中）
        private bool hasSyncedBounds;        // 是否已收到首次同步（DPI 恢复只对已同步窗口生效）
        private double syncedX, syncedY, syncedWidth, syncedHeight;   // 最近一次同步的原始 DIP 尺寸/位置（工作区相对）
        private bool dpiRestorePending;      // DPI 变化发生在拖拽/缩放手势中：手势结束后再恢复
        private bool capsuleCapsuleMoved;    // 已超过点击阈值进入拖动
        private Point capsuleDownScreen;     // 按下时的屏幕坐标
        private double capsuleStartLeft;     // 按下时的窗口位置
        private double capsuleStartTop;
        private bool moving;
        private Point moveStartScreen;
        private double moveStartLeft;
        private double moveStartTop;
        private CacheMode movingPreviousCache;
        private double scrollTargetOffset;
        private DateTime lastScrollAnimationTick;                   // 滚动动画最近一次 tick 的时间戳（基于时间的指数平滑）
        private const double LayoutAnimationDurationMs = 210;      // 折叠/展开高度补间总时长（毫秒）
        private bool layoutAnimating;                              // 高度补间是否进行中
        private double layoutFromHeight;                           // 补间起始高度
        private double layoutToHeight;                             // 补间目标高度
        private double layoutFromWidth;                            // 补间起始宽度（胶囊模式宽高同时过渡）
        private double layoutToWidth;                              // 补间目标宽度
        private double expandedWidth = 292;                        // 展开态宽度（UpdateBounds 维护）
        private DateTime layoutAnimationStart;                     // 补间开始时间（UtcNow）
        private readonly DispatcherTimer moveAnimationTimer;       // 避让移动补间计时器（Manager 避让推挤/还原时滑动过渡）
        private bool moveAnimating;                                // 避让位置补间是否进行中
        private double moveToLeft, moveToTop;                      // 补间目标位置
        private DateTime lastMoveAnimationTick;                    // 避让动画最近一帧时间戳
        private Storyboard breathStoryboard;                       // 呼吸动画 Storyboard（重触发时先停旧时钟）
        private int breathRun;                                     // 呼吸触发序号：防止旧动画 Completed 误收起新动画

        internal const double HeaderBarHeight = 30;               // 标题条高度：PaperTodo 式细条
        internal const double CapsuleSize = 56;                   // 图标胶囊边长（胶囊模式折叠时窗口尺寸）
        // DeskBox 三级圆角语言（Small 4 / Medium 6 / Large 8）：窗口与折叠条用 Large，
        // 胶囊卡片略软用 12；条目/按钮/菜单项统一 4。
        private const int CapsuleRadius = 12;                    // 图标胶囊圆角
        private const double PanelRadius = 8;                    // 展开面板圆角 = WidgetCornerRadiusLarge；折叠标题条同值
        private const string FallbackGeometry = "M4,4 L20,4 L20,20 L4,20 Z"; // PhosphorGeometry 未知图标的回退路径
        private const double PanelGap = 0;
        private const double SnapDistance = 7;

        // 缩放宽高最小值（展开态）与缩放热区厚度
        private const double ResizeMinWidth = 230;
        private const double ResizeMinHeight = 150;
        private const double ResizeEdgeThickness = 6;   // 四边热区厚度
        private const double ResizeCornerSize = 16;     // 四角热区边长
        private static readonly FontFamily ItemLabelFont = FontResources.SystemUiFont;

        private enum ResizeDirection { None, North, East, South, West, NorthEast, NorthWest, SouthEast, SouthWest }

        public PanelWindow(DesktopWindow host, string panelId)
        {
            this.host = host;
            this.panelId = panelId;
            Title = "PivkeyPanel-" + panelId;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = false;
            Topmost = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            MinWidth = Dip(230);
            MinHeight = ScaledHeaderBarHeight();
            AllowDrop = true;

            // 文字清晰度三件套：分层透明窗口默认被 WPF 禁用 ClearType，Ideal 模式的
            // 亚像素定位又会让小字号发虚；Display 按整像素对齐 + 强制 ClearType +
            // 布局取整后，10-12px 的 Maple Mono 才能恢复锐利边缘。
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            Grid root = new Grid();
            frame = new Border();
            frame.CornerRadius = new CornerRadius(PanelRadius);
            frame.BorderThickness = new Thickness(1);
            frame.Background = Brushes.Transparent;
            root.Children.Add(frame);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ScaledHeaderBarHeight()) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            frame.Child = content;

            header = new Border();
            header.CornerRadius = new CornerRadius(PanelRadius, PanelRadius, 0, 0);
            header.MouseLeftButtonDown += OnHeaderMouseDown;
            header.MouseMove += OnHeaderMouseMove;
            header.MouseLeftButtonUp += OnHeaderMouseUp;
            header.LostMouseCapture += delegate { FinishMove(); };
            header.ContextMenu = CreateHeaderMenu();
            Grid.SetRow(header, 0);
            content.Children.Add(header);

            Grid headerGrid = new Grid { Margin = new Thickness(Dip(8), 0, Dip(6), 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Dip(20)) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Dip(24)) });
            header.Child = headerGrid;

            mark = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0), IsHitTestVisible = false };
            markImage = new Image { Width = 18, Height = 18, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            mark.Child = markImage;
            Grid.SetColumn(mark, 0);
            headerGrid.Children.Add(mark);

            title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = Dip(14), FontWeight = FontWeights.Medium, FontFamily = ItemLabelFont, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 4, 0) };
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);

            countBadge = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromArgb(20, accent.R, accent.G, accent.B)),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(2, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = Dip(11), FontWeight = FontWeights.Medium, FontFamily = ItemLabelFont, Foreground = new SolidColorBrush(themeAccent) };
            countBadge.Child = count;
            Grid.SetColumn(countBadge, 2);
            headerGrid.Children.Add(countBadge);

            headerToolsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

            searchButton = HeaderButton("面板内搜索");
            searchButton.Content = CreatePhosphorIcon("search", Dip(15), true);
            searchButton.Click += delegate { ToggleSearchBar(); };
            headerToolsPanel.Children.Add(searchButton);

            sizeButton = HeaderButton("调整项目显示大小");
            sizeButton.Content = CreatePhosphorIcon("sliders", Dip(15), true);
            sizeButton.Click += delegate
            {
                sizeButton.ContextMenu = CreateItemSizeMenu();
                sizeButton.ContextMenu.PlacementTarget = sizeButton;
                sizeButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                sizeButton.ContextMenu.IsOpen = true;
            };
            headerToolsPanel.Children.Add(sizeButton);

            viewButton = HeaderButton("切换视图；右键选择排序");
            viewButton.Content = CreatePhosphorIcon("grid", Dip(15), true);
            viewButton.Click += delegate { SetViewMode(viewMode == "grid" ? "list" : "grid", true); };
            viewButton.ContextMenu = CreateViewMenu();
            headerToolsPanel.Children.Add(viewButton);

            pinButton = HeaderButton("固定");
            pinButton.Content = CreatePhosphorIcon("pin", Dip(15), true);
            pinButton.Click += delegate { host.PostManagerEvent("panelPin", panelId); };
            headerToolsPanel.Children.Add(pinButton);

            Grid.SetColumn(headerToolsPanel, 3);
            headerGrid.Children.Add(headerToolsPanel);

            collapseButton = HeaderButton("折叠");
            collapseButton.Content = CreatePhosphorIcon("chevron-down", Dip(15), true);
            collapseButton.Click += delegate { host.PostManagerEvent("panelCollapse", panelId); };
            Grid.SetColumn(collapseButton, 4);
            headerGrid.Children.Add(collapseButton);

            header.MouseEnter += delegate { UpdateHeaderToolsVisibility(true); };
            header.MouseLeave += delegate { UpdateHeaderToolsVisibility(false); };

            searchBox = new TextBox
            {
                Height = 24,
                Margin = new Thickness(0),
                Padding = new Thickness(8, 2, 8, 2),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = Dip(11),
                FontFamily = ItemLabelFont,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "搜索名称、扩展名或路径"
            };
            searchBox.TextChanged += delegate
            {
                searchText = searchBox.Text ?? "";
                RebuildItems();
                UpdateSearchVisuals();
            };
            // 搜索框持有键盘焦点期间视为交互中（唤起会话不被监视器回收）
            searchBox.GotKeyboardFocus += delegate { BeginLayerInteraction(); };
            searchBox.LostKeyboardFocus += delegate { EndLayerInteraction(); };
            searchBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    searchBox.Clear();
                    ToggleSearchBar(false);
                    args.Handled = true;
                }
            };
            searchBar = new Border
            {
                Child = searchBox,
                Height = 29,
                Margin = new Thickness(8, 2, 8, 3),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(searchBar, 1);
            content.Children.Add(searchBar);

            scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, CanContentScroll = false, Background = Brushes.Transparent, Padding = new Thickness(10, 8, 10, 12), ClipToBounds = true, SnapsToDevicePixels = true, UseLayoutRounding = true };
            scroll.ContextMenu = CreateScrollMenu();
            scroll.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ChangedButton == MouseButton.Left && args.ClickCount == 1)
                {
                    DependencyObject source = args.OriginalSource as DependencyObject;
                    bool isInteractive = false;
                    while (source != null && source != scroll)
                    {
                        if (source is Button || source is TextBox || source is System.Windows.Controls.Primitives.ScrollBar)
                        {
                            isInteractive = true;
                            break;
                        }
                        source = VisualTreeHelper.GetParent(source);
                    }
                    if (!isInteractive)
                    {
                        OnHeaderMouseDown(header, args);
                    }
                }
            };
            AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollMouseWheel), true);
            Grid.SetRow(scroll, 2);
            content.Children.Add(scroll);
            itemsPanel = new StackPanel { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Orientation = Orientation.Vertical };
            scroll.Content = itemsPanel;
            scrollAnimationTimer = new DispatcherTimer(DispatcherPriority.Render);
            scrollAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
            scrollAnimationTimer.Tick += OnScrollAnimationTick;
            layoutAnimationTimer = new DispatcherTimer(DispatcherPriority.Render);
            layoutAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
            layoutAnimationTimer.Tick += OnLayoutAnimationTick;
            moveAnimationTimer = new DispatcherTimer(DispatcherPriority.Render);
            moveAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
            moveAnimationTimer.Tick += OnMoveAnimationTick;

            // 右下角缩放手柄：仅作视觉提示（"可缩放"线索），交互由 SE 角热区承担
            resizeGrip = new Border { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 1.5, 1.5), CornerRadius = new CornerRadius(0, 0, 8, 0), Cursor = Cursors.SizeNWSE, IsHitTestVisible = false };
            root.Children.Add(resizeGrip);

            // Windows 式八方向缩放热区：四边 6px + 四角 16px，覆盖整个面板边框，
            // 鼠标悬停显示对应方向光标；折叠/胶囊状态统一隐藏（见 UpdateContent/RefreshCapsuleVisibility）
            AddResizeAdorner(ResizeDirection.North, root);
            AddResizeAdorner(ResizeDirection.East, root);
            AddResizeAdorner(ResizeDirection.South, root);
            AddResizeAdorner(ResizeDirection.West, root);
            AddResizeAdorner(ResizeDirection.NorthEast, root);
            AddResizeAdorner(ResizeDirection.NorthWest, root);
            AddResizeAdorner(ResizeDirection.SouthEast, root);
            AddResizeAdorner(ResizeDirection.SouthWest, root);

            // 图标胶囊：折叠 + 胶囊模式时替代标题条显示，点击展开分区
            capsuleView = new Border
            {
                Width = ScaledCapsuleSize(),
                Height = ScaledCapsuleSize(),
                CornerRadius = new CornerRadius(CapsuleRadius),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            // 不设常驻阴影：静止胶囊是干净玻璃片；浮起反馈由 hover 的极淡投影承担（见 ApplyCapsuleVisual）
            // 内高光：内缩 1px 的半透明白描边，叠在渐变背景之上（不拦截点击）
            capsuleHighlight = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(CapsuleRadius - 1),
                BorderThickness = new Thickness(1)
            };
            iconShape = new ShapePath { Stroke = null, StrokeThickness = 0, Stretch = Stretch.Uniform, Width = Dip(28), Height = Dip(28) };
            capsuleImage = new Image { Width = Dip(28), Height = Dip(28), Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            capsuleInner = new Grid { Width = Dip(28), Height = Dip(28) };
            capsuleInner.RenderTransform = new ScaleTransform(1, 1, Dip(14), Dip(14)); // hover 缩放中心：内容区中心点
            iconShape.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1.0,
                Direction = 225,
                Opacity = 0.28,
                Color = Colors.Black
            };
            capsuleInner.Children.Add(iconShape);
            capsuleInner.Children.Add(capsuleImage);
            Grid capsuleLayer = new Grid();
            capsuleLayer.Children.Add(capsuleHighlight);
            capsuleLayer.Children.Add(capsuleInner);
            capsuleView.Child = capsuleLayer;
            // hover：背景提亮 / 描边加亮（MouseEnter/MouseLeave）
            capsuleView.MouseEnter += delegate { capsuleHovered = true; ApplyCapsuleVisual(true); };
            capsuleView.MouseLeave += delegate { capsuleHovered = false; ApplyCapsuleVisual(false); };
            // 点击胶囊展开：复用现有折叠/展开事件流（前端收到后把该分区从 collapsed 数组移除 → 回同步 → StartLayoutTween）
            // 胶囊手势：按下后区分"点击（展开）"与"拖动（移动胶囊位置）"——
            // 位移超过阈值进入拖动，松手回传新位置；否则视为点击展开
            capsuleView.MouseLeftButtonDown += OnCapsuleMouseDown;
            capsuleView.MouseMove += OnCapsuleMouseMove;
            capsuleView.MouseLeftButtonUp += OnCapsuleMouseUp;
            capsuleView.LostMouseCapture += delegate { FinishCapsuleGesture(capsuleCapsuleMoved); };
            // 右键菜单与标题条一致
            capsuleView.ContextMenu = CreateHeaderMenu();
            root.Children.Add(capsuleView);
            // 新文件到达呼吸光晕：叠加在窗口最上层，IsHitTestVisible=false 不拦截鼠标
            breathOverlay = new Border
            {
                IsHitTestVisible = false,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(PanelRadius),
                BorderBrush = new SolidColorBrush(themeAccent),
                Opacity = 0,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(breathOverlay);
            UpdateCapsuleIcon();
            Content = root;

            SourceInitialized += OnSourceInitialized;
            StateChanged += OnStateChanged;
            Drop += OnPanelDrop;
            DragEnter += delegate(object sender, DragEventArgs args)
            {
                if (ResolveDropEffect(args) != DragDropEffects.None) UpdateDragGlow(true);
            };
            DragLeave += delegate { UpdateDragGlow(false); };
            DragOver += delegate(object sender, DragEventArgs args)
            {
                DragDropEffects effect = ResolveDropEffect(args);
                args.Effects = effect;
                UpdateDragGlow(effect != DragDropEffects.None);
                args.Handled = true;
            };
            Loaded += delegate { ApplyTheme(); };
            MouseEnter += delegate { mouseLeftAt = DateTime.UtcNow; autoHidden = false; };
            MouseLeave += delegate { mouseLeftAt = DateTime.UtcNow; };
            Closed += delegate { scrollAnimationTimer.Stop(); layoutAnimationTimer.Stop(); moveAnimationTimer.Stop(); SetItemsCache(false); if (hwnd != IntPtr.Zero) LayerSession.OnWindowClosed(hwnd); };
            SizeChanged += delegate
            {
                if (resizing) return;
                RefreshItemLayoutForSize();
            };
            ApplyScaleMetrics();   // 首帧按默认 uiScale 定字号，UpdateContent 收到真实值时再刷一次
        }

        // 逻辑尺寸换算：仅应用界面缩放，不含系统 DPI（WPF 会自动把 DIP 换算成物理像素）
        private double Dip(double value)
        {
            return value * uiScale / 100.0;
        }

        // 折叠态高度：胶囊模式为胶囊边长，普通模式为标题条高度；两者都随界面缩放
        private double ScaledCollapsedHeight()
        {
            return Dip(capsuleMode ? CapsuleSize : HeaderBarHeight);
        }

        // 标题条高度（随界面缩放）
        private double ScaledHeaderBarHeight()
        {
            return Dip(HeaderBarHeight);
        }

        // 胶囊边长（随界面缩放）
        private double ScaledCapsuleSize()
        {
            return Dip(CapsuleSize);
        }

        // 当前图标需要的位图像素档位（逻辑尺寸 × 界面缩放 × 系统 DPI，向上取 16 倍数）
        private int IconTargetPx()
        {
            return DesktopWindow.ResolveIconPixelSize(iconSize, uiScale, dpiScale);
        }

        // 长期存活控件的字号/尺寸随 uiScale 刷新（菜单项每次右键重建，直接在构建时用 Dip 即可）
        private void ApplyScaleMetrics()
        {
            if (title != null) title.FontSize = Dip(14);
            if (count != null) count.FontSize = Dip(11);
            if (searchBox != null) searchBox.FontSize = Dip(11);
            ApplyHeaderIconScale(searchButton);
            ApplyHeaderIconScale(sizeButton);
            ApplyHeaderIconScale(viewButton);
            ApplyHeaderIconScale(pinButton);
            ApplyHeaderIconScale(collapseButton);
            ApplyItemLabelFontSize();
            // 几何尺寸随界面缩放刷新：标题行高 / 窗口最小高度 / 胶囊方块与内容
            MinHeight = ScaledHeaderBarHeight();
            Grid contentGrid = frame == null ? null : frame.Child as Grid;
            if (contentGrid != null && contentGrid.RowDefinitions.Count > 0)
                contentGrid.RowDefinitions[0].Height = new GridLength(ScaledHeaderBarHeight());
            if (capsuleView != null)
            {
                capsuleView.Width = ScaledCapsuleSize();
                capsuleView.Height = ScaledCapsuleSize();
            }
            if (capsuleInner != null)
            {
                capsuleInner.Width = Dip(28);
                capsuleInner.Height = Dip(28);
                ScaleTransform capsuleScale = capsuleInner.RenderTransform as ScaleTransform;
                if (capsuleScale != null) { capsuleScale.CenterX = Dip(14); capsuleScale.CenterY = Dip(14); }
            }
            if (iconShape != null) { iconShape.Width = Dip(28); iconShape.Height = Dip(28); }
            if (capsuleImage != null) { capsuleImage.Width = Dip(28); capsuleImage.Height = Dip(28); }
        }

        // 标题栏按钮图标（Phosphor 的 Viewbox）：宽度/高度随界面缩放
        private void ApplyHeaderIconScale(Button button)
        {
            if (button == null) return;
            FrameworkElement content = button.Content as FrameworkElement;
            if (content == null) return;
            content.Width = Dip(15);
            content.Height = Dip(15);
        }

        // 条目标签字号：列表视图在 labelSize 基础上 +1.5（下方 +8 字号）
        private void ApplyItemLabelFontSize()
        {
            double next = viewMode == "list" ? Dip(Math.Max(10, labelSize + 1.5)) : Dip(labelSize);
            foreach (Button button in EnumerateItemButtons())
            {
                StackPanel stack = button.Content as StackPanel;
                if (stack == null || stack.Children.Count < 2) continue;
                TextBlock label = stack.Children[1] as TextBlock;
                if (label == null) continue;
                label.FontSize = next;
            }
        }

        // 遍历 itemsPanel 里所有条目 Button（跳过行容器 Grid/StackPanel）
        private IEnumerable<Button> EnumerateItemButtons()
        {
            List<Button> found = new List<Button>();
            if (itemsPanel == null) return found;
            foreach (UIElement child in itemsPanel.Children)
            {
                Button direct = child as Button;
                if (direct != null) { found.Add(direct); continue; }
                Panel container = child as Panel;
                if (container == null) continue;
                foreach (UIElement grandChild in container.Children)
                {
                    Button nested = grandChild as Button;
                    if (nested != null) found.Add(nested);
                }
            }
            return found;
        }

        private Button HeaderButton(string tooltip)
        {
            double box = HeaderButtonBoxSize();
            Button button = new Button { Width = box, Height = box, Padding = new Thickness(0), Margin = new Thickness(1), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90)), ToolTip = tooltip, Cursor = Cursors.Hand, Focusable = false, Template = FlatButtonTemplate() };
            // 吉伊卡哇萌系圆角悬停：柔和粉色微光
            button.MouseEnter += delegate { button.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x1C, themeAccent.R, themeAccent.G, themeAccent.B)); };
            button.MouseLeave += delegate { button.Background = Brushes.Transparent; };
            return button;
        }

        // 标题栏按钮热区边长：随界面缩放但不高过缩放后的标题条高度
        private double HeaderButtonBoxSize()
        {
            return Math.Max(16, Math.Min(Dip(24), ScaledHeaderBarHeight() - 2));
        }

        private void UpdateHeaderToolsVisibility(bool hover)
        {
            if (headerToolsPanel == null) return;
            // 窄宽度（< 200 DIP）标题条：只保留标题 + 折叠 + 菜单按钮，四个工具按钮彻底收起（hover 也不出）
            if (narrowHeader && !hover)
            {
                searchButton.Visibility = Visibility.Collapsed;
                sizeButton.Visibility = Visibility.Collapsed;
                viewButton.Visibility = Visibility.Collapsed;
                pinButton.Visibility = Visibility.Collapsed;
                headerToolsPanel.Visibility = Visibility.Collapsed;
                return;
            }
            if (hover && !narrowHeader)
            {
                searchButton.Visibility = Visibility.Visible;
                sizeButton.Visibility = Visibility.Visible;
                viewButton.Visibility = Visibility.Visible;
                pinButton.Visibility = Visibility.Visible;
                headerToolsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                bool showSearch = !narrowHeader && searchBar != null && searchBar.Visibility == Visibility.Visible;
                bool showPin = !narrowHeader && pinned;
                searchButton.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
                pinButton.Visibility = showPin ? Visibility.Visible : Visibility.Collapsed;
                sizeButton.Visibility = Visibility.Collapsed;
                viewButton.Visibility = Visibility.Collapsed;
                headerToolsPanel.Visibility = (showSearch || showPin) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // 窄标题条状态：宽度 < 200 DIP 时收起四个工具按钮，操作改走右键菜单
        private void UpdateNarrowHeaderState(double nextWidth)
        {
            bool narrow = nextWidth > 0 && nextWidth < 200;
            if (narrow == narrowHeader) return;
            narrowHeader = narrow;
            if (narrow) UpdateHeaderToolsVisibility(false);
            else UpdateHeaderToolsVisibility(header != null && header.IsMouseOver);
        }

        private static FrameworkElement CreatePhosphorIcon(string name, double size, bool bindToButton)
        {
            // Geometry.Parse 返回冻结的 StreamGeometry，需 Clone 后才能设置 FillRule
            StreamGeometry iconGeometry = (StreamGeometry)Geometry.Parse(PhosphorGeometry(name));
            iconGeometry = iconGeometry.Clone();
            iconGeometry.FillRule = FillRule.Nonzero;
            ShapePath icon = new ShapePath
            {
                Data = iconGeometry,
                Stroke = null,
                StrokeThickness = 0,
                Stretch = Stretch.Uniform,
                Width = 24,
                Height = 24
            };
            if (bindToButton)
            {
                icon.SetBinding(ShapePath.FillProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
            }
            else
            {
                icon.Fill = new SolidColorBrush(Color.FromRgb(140, 115, 80));
            }
            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Child = icon,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private FrameworkElement CreateMenuPhosphorIcon(string name)
        {
            return CreateMenuPhosphorIcon(name, themeAccent, 14, HorizontalAlignment.Left);
        }

        private FrameworkElement CreateMenuPhosphorIcon(string name, Color color, double size, HorizontalAlignment alignment)
        {
            // Geometry.Parse 返回冻结的 StreamGeometry，需 Clone 后才能设置 FillRule
            StreamGeometry iconGeometry = (StreamGeometry)Geometry.Parse(PhosphorGeometry(name));
            iconGeometry = iconGeometry.Clone();
            iconGeometry.FillRule = FillRule.Nonzero;
            ShapePath icon = new ShapePath
            {
                Data = iconGeometry,
                Fill = new SolidColorBrush(color),
                Stroke = null,
                StrokeThickness = 0,
                Stretch = Stretch.Uniform,
                Width = 24,
                Height = 24
            };
            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Child = icon,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = alignment
            };
        }

        // Phosphor bold icon geometry (official phosphor-icons/core assets/bold/*-bold.svg,
        // 256x256 coordinate space, filled outline style - requires FillRule.Nonzero to render).
        // Name keys keep the historical call-site names, mapped to the closest Phosphor icon.
        private static string PhosphorGeometry(string name)
        {
            if (name == "search") return "M112,20a92,92,0,1,0,58.01,163.42l43.28,43.28a12,12,0,0,0,16.97-16.97L187,166.45A92,92,0,0,0,112,20Zm0,160a68,68,0,1,1,68-68A68.08,68.08,0,0,1,112,180Z"; // magnifying-glass
            if (name == "sparkle") return "M128,20l18.9,67.1L214,106l-67.1,18.9L128,192l-18.9-67.1L42,106l67.1-18.9Z"; // sparkle
            if (name == "check") return "M216.49,80.49a12,12,0,0,1,0,17l-104,104a12,12,0,0,1-17,0l-56-56a12,12,0,0,1,17-17L104,176l95.51-95.51A12,12,0,0,1,216.49,80.49Z"; // check
            if (name == "close") return "M205.66,194.34a12,12,0,0,1-17,17L128,150.97l-60.69,60.37a12,12,0,0,1-16.97-16.97L111.03,134,50.34,73.66a12,12,0,0,1,16.97-17L128,117.03l60.69-60.37a12,12,0,0,1,17,17L144.97,134Z"; // x
            if (name == "sliders") return "M40,92H70.06a36,36,0,0,0,67.88,0H216a12,12,0,0,0,0-24H137.94a36,36,0,0,0-67.88,0H40a12,12,0,0,0,0,24Zm64-24A12,12,0,1,1,92,80,12,12,0,0,1,104,68Zm112,96H201.94a36,36,0,0,0-67.88,0H40a12,12,0,0,0,0,24h94.06a36,36,0,0,0,67.88,0H216a12,12,0,0,0,0-24Zm-48,24a12,12,0,1,1,12-12A12,12,0,0,1,168,188Z"; // sliders-horizontal
            if (name == "grid") return "M100,36H56A20,20,0,0,0,36,56v44a20,20,0,0,0,20,20h44a20,20,0,0,0,20-20V56A20,20,0,0,0,100,36ZM96,96H60V60H96ZM200,36H156a20,20,0,0,0-20,20v44a20,20,0,0,0,20,20h44a20,20,0,0,0,20-20V56A20,20,0,0,0,200,36Zm-4,60H160V60h36Zm-96,40H56a20,20,0,0,0-20,20v44a20,20,0,0,0,20,20h44a20,20,0,0,0,20-20V156A20,20,0,0,0,100,136Zm-4,60H60V160H96Zm104-60H156a20,20,0,0,0-20,20v44a20,20,0,0,0,20,20h44a20,20,0,0,0,20-20V156A20,20,0,0,0,200,136Zm-4,60H160V160h36Z"; // squares-four
            if (name == "list") return "M228,128a12,12,0,0,1-12,12H40a12,12,0,0,1,0-24H216A12,12,0,0,1,228,128ZM40,76H216a12,12,0,0,0,0-24H40a12,12,0,0,0,0,24ZM216,180H40a12,12,0,0,0,0,24H216a12,12,0,0,0,0-24Z"; // list
            if (name == "pin") return "M216,164h-5.93L190.3,52H192a12,12,0,0,0,0-24H64a12,12,0,0,0,0,24h1.7L45.93,164H40a12,12,0,0,0,0,24h76v52a12,12,0,0,0,24,0V188h76a12,12,0,0,0,0-24ZM90.07,52h75.86L185.7,164H70.3Z"; // push-pin-simple
            if (name == "chevron-right") return "M184.49,136.49l-80,80a12,12,0,0,1-17-17L159,128,87.51,56.49a12,12,0,1,1,17-17l80,80A12,12,0,0,1,184.49,136.49Z"; // caret-right
            if (name == "chevron-down") return "M216.49,104.49l-80,80a12,12,0,0,1-17,0l-80-80a12,12,0,0,1,17-17L128,159l71.51-71.52a12,12,0,0,1,17,17Z"; // caret-down
            if (name == "chevron-up") return "M216.49,168.49a12,12,0,0,1-17,0L128,97,56.49,168.49a12,12,0,0,1-17-17l80-80a12,12,0,0,1,17,0l80,80A12,12,0,0,1,216.49,168.49Z"; // caret-up
            if (name == "pencil") return "M230.14,70.54,185.46,25.85a20,20,0,0,0-28.29,0L33.86,149.17A19.85,19.85,0,0,0,28,163.31V208a20,20,0,0,0,20,20H92.69a19.86,19.86,0,0,0,14.14-5.86L230.14,98.82a20,20,0,0,0,0-28.28ZM91,204H52V165l84-84,39,39ZM192,103,153,64l18.34-18.34,39,39Z"; // pencil-simple
            if (name == "folder") return "M216,68H133.39l-26-29.29a20,20,0,0,0-15-6.71H40A20,20,0,0,0,20,52V200.62A19.41,19.41,0,0,0,39.38,220H216.89A19.13,19.13,0,0,0,236,200.89V88A20,20,0,0,0,216,68ZM44,56H90.61l10.67,12H44ZM212,196H44V92H212Z"; // folder
            if (name == "refresh") return "M228,48V96a12,12,0,0,1-12,12H168a12,12,0,0,1,0-24h19l-7.8-7.8a75.55,75.55,0,0,0-53.32-22.26h-.43A75.49,75.49,0,0,0,72.39,75.57,12,12,0,1,1,55.61,58.41a99.38,99.38,0,0,1,69.87-28.47H126A99.42,99.42,0,0,1,196.2,59.23L204,67V48a12,12,0,0,1,24,0ZM183.61,180.43a75.49,75.49,0,0,1-53.09,21.63h-.43A75.55,75.55,0,0,1,76.77,179.8L69,172H88a12,12,0,0,0,0-24H40a12,12,0,0,0-12,12v48a12,12,0,0,0,24,0V189l7.8,7.8A99.42,99.42,0,0,0,130,226.06h.56a99.38,99.38,0,0,0,69.87-28.47,12,12,0,0,0-16.78-17.16Z"; // arrows-clockwise
            if (name == "sort") return "M120.49,167.51a12,12,0,0,1,0,17l-32,32a12,12,0,0,1-17,0l-32-32a12,12,0,1,1,17-17L68,179V48a12,12,0,0,1,24,0V179l11.51-11.52A12,12,0,0,1,120.49,167.51Zm96-96-32-32a12,12,0,0,0-17,0l-32,32a12,12,0,0,0,17,17L164,77V208a12,12,0,0,0,24,0V77l11.51,11.52a12,12,0,0,0,17-17Z"; // arrows-down-up
            if (name == "clock") return "M128,20A108,108,0,1,0,236,128,108.12,108.12,0,0,0,128,20Zm0,192a84,84,0,1,1,84-84A84.09,84.09,0,0,1,128,212Zm68-84a12,12,0,0,1-12,12H157l19.52,19.51a12,12,0,0,1-17,17l-40-40A12,12,0,0,1,128,116h56A12,12,0,0,1,196,128Z"; // clock-afternoon
            if (name == "drive") return "M224,60H32A20,20,0,0,0,12,80v96a20,20,0,0,0,20,20H224a20,20,0,0,0,20-20V80A20,20,0,0,0,224,60Zm-4,112H36V84H220Zm-56-44a16,16,0,1,1,16,16A16,16,0,0,1,164,128Z"; // hard-drive
            if (name == "open") return "M228,104a12,12,0,0,1-24,0V69l-59.51,59.51a12,12,0,0,1-17-17L187,52H152a12,12,0,0,1,0-24h64a12,12,0,0,1,12,12Zm-44,24a12,12,0,0,0-12,12v64H52V84h64a12,12,0,0,0,0-24H48A20,20,0,0,0,28,80V208a20,20,0,0,0,20,20H176a20,20,0,0,0,20-20V140A12,12,0,0,0,184,128Z"; // arrow-square-out
            if (name == "undo") return "M228,128a100,100,0,0,1-98.66,100H128a99.39,99.39,0,0,1-68.62-27.29,12,12,0,0,1,16.48-17.45,76,76,0,1,0-1.57-109c-.13.13-.25.25-.39.37L54.89,92H72a12,12,0,0,1,0,24H24a12,12,0,0,1-12-12V56a12,12,0,0,1,24,0V76.72L57.48,57.06A100,100,0,0,1,228,128Z"; // arrow-counter-clockwise
            if (name == "stack") return "M234.36,170A12,12,0,0,1,230,186.37l-96,56a12,12,0,0,1-12.1,0l-96-56a12,12,0,0,1,12.09-20.74l90,52.48L218,165.63A12,12,0,0,1,234.36,170ZM218,117.63,128,170.11,38.05,117.63A12,12,0,0,0,26,138.37l96,56a12,12,0,0,0,12.1,0l96-56A12,12,0,0,0,218,117.63ZM20,80a12,12,0,0,1,6-10.37l96-56a12.06,12.06,0,0,1,12.1,0l96,56a12,12,0,0,1,0,20.74l-96,56a12,12,0,0,1-12.1,0l-96-56A12,12,0,0,1,20,80Zm35.82,0L128,122.11,200.18,80,128,37.89Z"; // stack
            if (name == "layers") return "M234.36,170A12,12,0,0,1,230,186.37l-96,56a12,12,0,0,1-12.1,0l-96-56a12,12,0,0,1,12.09-20.74l90,52.48L218,165.63A12,12,0,0,1,234.36,170ZM218,117.63,128,170.11,38.05,117.63A12,12,0,0,0,26,138.37l96,56a12,12,0,0,0,12.1,0l96-56A12,12,0,0,0,218,117.63ZM20,80a12,12,0,0,1,6-10.37l96-56a12.06,12.06,0,0,1,12.1,0l96,56a12,12,0,0,1,0,20.74l-96,56a12,12,0,0,1-12.1,0l-96-56A12,12,0,0,1,20,80Zm35.82,0L128,122.11,200.18,80,128,37.89Z"; // layers = stack 别名（兼容旧数据）
            if (name == "images") return "M160,88a16,16,0,1,1,16,16A16,16,0,0,1,160,88Zm76-32V160a20,20,0,0,1-20,20H204v20a20,20,0,0,1-20,20H40a20,20,0,0,1-20-20V88A20,20,0,0,1,40,68H60V56A20,20,0,0,1,80,36H216A20,20,0,0,1,236,56ZM180,180H80a20,20,0,0,1-20-20V92H44V196H180Zm-21.66-24L124,121.66,89.66,156ZM212,60H84v67.72l25.86-25.86a20,20,0,0,1,28.28,0L192.28,156H212Z"; // images
            if (name == "music-note") return "M211.45,52.51l-80-24A12,12,0,0,0,116,40V140.22A52,52,0,1,0,140,184V104.13l64.55,19.36A12,12,0,0,0,220,112V64A12,12,0,0,0,211.45,52.51ZM88,212a28,28,0,1,1,28-28A28,28,0,0,1,88,212ZM196,95.87l-56-16.8V56.13l56,16.8Z"; // music-note
            if (name == "file-text") return "M216.49,79.52l-56-56A12,12,0,0,0,152,20H56A20,20,0,0,0,36,40V216a20,20,0,0,0,20,20H200a20,20,0,0,0,20-20V88A12,12,0,0,0,216.49,79.52ZM160,57l23,23H160ZM60,212V44h76V92a12,12,0,0,0,12,12h48V212Zm112-80a12,12,0,0,1-12,12H96a12,12,0,0,1,0-24h64A12,12,0,0,1,172,132Zm0,40a12,12,0,0,1-12,12H96a12,12,0,0,1,0-24h64A12,12,0,0,1,172,172Z"; // file-text
            if (name == "video") return "M216,36H40A20,20,0,0,0,20,56V160a20,20,0,0,0,20,20H216a20,20,0,0,0,20-20V56A20,20,0,0,0,216,36Zm-4,120H44V60H212Zm24,52a12,12,0,0,1-12,12H32a12,12,0,0,1,0-24H224A12,12,0,0,1,236,208ZM104,128V88a12,12,0,0,1,18.36-10.18l32,20a12,12,0,0,1,0,20.36l-32,20A12,12,0,0,1,104,128Z"; // video
            if (name == "archive") return "M224,44H32A20,20,0,0,0,12,64V88a20,20,0,0,0,16,19.6V192a20,20,0,0,0,20,20H208a20,20,0,0,0,20-20V107.6A20,20,0,0,0,244,88V64A20,20,0,0,0,224,44ZM36,68H220V84H36ZM52,188V108H204v80Zm112-52a12,12,0,0,1-12,12H104a12,12,0,0,1,0-24h48A12,12,0,0,1,164,136Z"; // archive
            if (name == "book") return "M208,20H72A36,36,0,0,0,36,56V224a12,12,0,0,0,12,12H192a12,12,0,0,0,0-24H60v-4a12,12,0,0,1,12-12H208a12,12,0,0,0,12-12V32A12,12,0,0,0,208,20ZM196,172H72a35.59,35.59,0,0,0-12,2.06V56A12,12,0,0,1,72,44H196Z"; // book
            if (name == "star") return "M243,96a20.33,20.33,0,0,0-17.74-14l-56.59-4.57L146.83,24.62a20.36,20.36,0,0,0-37.66,0L87.35,77.44,30.76,82A20.45,20.45,0,0,0,19.1,117.88l43.18,37.24-13.2,55.7A20.37,20.37,0,0,0,79.57,233L128,203.19,176.43,233a20.39,20.39,0,0,0,30.49-22.15l-13.2-55.7,43.18-37.24A20.43,20.43,0,0,0,243,96ZM172.53,141.7a12,12,0,0,0-3.84,11.86L181.58,208l-47.29-29.08a12,12,0,0,0-12.58,0L74.42,208l12.89-54.4a12,12,0,0,0-3.84-11.86L41.2,105.24l55.4-4.47a12,12,0,0,0,10.13-7.38L128,41.89l21.27,51.5a12,12,0,0,0,10.13,7.38l55.4,4.47Z"; // star
            if (name == "heart") return "M178,36c-20.09,0-37.92,7.93-50,21.56C115.92,43.93,98.09,36,78,36a66.08,66.08,0,0,0-66,66c0,72.34,105.81,130.14,110.31,132.57a12,12,0,0,0,11.38,0C138.19,232.14,244,174.34,244,102A66.08,66.08,0,0,0,178,36Zm-5.49,142.36A328.69,328.69,0,0,1,128,210.16a328.69,328.69,0,0,1-44.51-31.8C61.82,159.77,36,131.42,36,102A42,42,0,0,1,78,60c17.8,0,32.7,9.4,38.89,24.54a12,12,0,0,0,22.22,0C145.3,69.4,160.2,60,178,60a42,42,0,0,1,42,42C220,131.42,194.18,159.77,172.51,178.36Z"; // heart
            if (name == "briefcase") return "M100,100a12,12,0,0,1,12-12h32a12,12,0,0,1,0,24H112A12,12,0,0,1,100,100ZM236,68V196a20,20,0,0,1-20,20H40a20,20,0,0,1-20-20V68A20,20,0,0,1,40,48H76V40a28,28,0,0,1,28-28h48a28,28,0,0,1,28,28v8h36A20,20,0,0,1,236,68ZM100,48h56V40a4,4,0,0,0-4-4H104a4,4,0,0,0-4,4ZM44,72v35.23A180.06,180.06,0,0,0,128,128a180,180,0,0,0,84-20.78V72ZM212,192V133.94A204.27,204.27,0,0,1,128,152a204.21,204.21,0,0,1-84-18.06V192Z"; // briefcase
            if (name == "code") return "M71.68,97.22,34.74,128l36.94,30.78a12,12,0,1,1-15.36,18.44l-48-40a12,12,0,0,1,0-18.44l48-40A12,12,0,0,1,71.68,97.22Zm176,21.56-48-40a12,12,0,1,0-15.36,18.44L221.26,128l-36.94,30.78a12,12,0,1,0,15.36,18.44l48-40a12,12,0,0,0,0-18.44ZM164.1,28.72a12,12,0,0,0-15.38,7.18l-64,176a12,12,0,0,0,7.18,15.37A11.79,11.79,0,0,0,96,228a12,12,0,0,0,11.28-7.9l64-176A12,12,0,0,0,164.1,28.72Z"; // code
            if (name == "rocket") return "M156,228a12,12,0,0,1-12,12H112a12,12,0,0,1,0-24h32A12,12,0,0,1,156,228ZM128,116a16,16,0,1,0-16-16A16,16,0,0,0,128,116Zm99.53,40.7-12.36,55.63a19.9,19.9,0,0,1-12.88,14.53A20.16,20.16,0,0,1,195.6,228a19.87,19.87,0,0,1-12.29-4.27L157.17,204H98.83L72.69,223.74A19.87,19.87,0,0,1,60.4,228a20.16,20.16,0,0,1-6.69-1.15,19.9,19.9,0,0,1-12.88-14.53L28.47,156.7a20.1,20.1,0,0,1,4.16-17.14l27.83-33.4A127,127,0,0,1,69.11,69.7c13.27-33.25,37-54.1,46.64-61.52a20,20,0,0,1,24.5,0c9.6,7.42,33.37,28.27,46.64,61.52a127,127,0,0,1,8.65,36.46l27.83,33.4A20.1,20.1,0,0,1,227.53,156.7ZM101.79,180h52.42c19.51-35.7,23-69.78,10.39-101.4C154.4,53,136.2,35.9,128,29.12,119.8,35.9,101.6,53,91.4,78.6,78.78,110.22,82.28,144.3,101.79,180Zm-22.55,8.72a168,168,0,0,1-16.92-47.3l-10,12,10.58,47.64Zm124.43-35.31-10-12a168,168,0,0,1-16.92,47.3l16.33,12.33Z"; // rocket
            if (name == "presentation") return "M216,36H140V24a12,12,0,0,0-24,0V36H40A20,20,0,0,0,20,56V176a20,20,0,0,0,20,20H71l-16.4,20.5a12,12,0,0,0,18.74,15l28.4-35.5h52.46l28.4,35.5a12,12,0,0,0,18.74-15L185,196h31a20,20,0,0,0,20-20V56A20,20,0,0,0,216,36Zm-4,136H44V60H212Z"; // presentation
            if (name == "image") return "M144,96a16,16,0,1,1,16,16A16,16,0,0,1,144,96Zm92-40V200a20,20,0,0,1-20,20H40a20,20,0,0,1-20-20V56A20,20,0,0,1,40,36H216A20,20,0,0,1,236,56ZM44,60v79.72l33.86-33.86a20,20,0,0,1,28.28,0L147.31,147l17.18-17.17a20,20,0,0,1,28.28,0L212,149.09V60Zm0,136H162.34L92,125.66l-48,48Zm168,0V183l-33.37-33.37L164.28,164l32,32Z"; // image
            if (name == "play") return "M234.49,111.07,90.41,22.94A20,20,0,0,0,60,39.87V216.13a20,20,0,0,0,30.41,16.93l144.08-88.13a19.82,19.82,0,0,0,0-33.86ZM84,208.85V47.15L216.16,128Z"; // play
            if (name == "sheet" || name == "table") return "M224,44H32A12,12,0,0,0,20,56V192a20,20,0,0,0,20,20H216a20,20,0,0,0,20-20V56A12,12,0,0,0,224,44ZM44,116H76v24H44Zm56,0H212v24H100ZM212,68V92H44V68ZM44,164H76v24H44Zm56,24V164H212v24Z"; // sheet = table（表格）别名
            return FallbackGeometry; // fallback for unknown names
        }

        private static ControlTemplate FlatButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private void OnScrollMouseWheel(object sender, MouseWheelEventArgs args)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                double nextItem = Math.Max(44, Math.Min(112, itemSize + (args.Delta > 0 ? 4 : -4)));
                double nextIcon = Math.Max(24, Math.Min(72, Math.Round(nextItem * 0.71)));
                double nextLabel = Math.Max(8, Math.Min(18, Math.Round(nextItem * 0.132 * 2) / 2));
                SetItemDisplaySize(nextItem, nextIcon, nextLabel, true);
                args.Handled = true;
                return;
            }
            if (scroll.ScrollableHeight <= 0) return;
            int lines = SystemParameters.WheelScrollLines;
            double step = lines < 0 ? Math.Max(48, scroll.ViewportHeight * 0.85) : Math.Max(48, Math.Min(180, lines * 18.0));
            double origin = scrollAnimationTimer.IsEnabled ? scrollTargetOffset : scroll.VerticalOffset;
            scrollTargetOffset = Math.Max(0, Math.Min(scroll.ScrollableHeight, origin - args.Delta / 120.0 * step));
            if (Math.Abs(scrollTargetOffset - scroll.VerticalOffset) > 0.25)
            {
                // 目标变化时重置时间基准，避免长时间间隔导致首帧跳变
                lastScrollAnimationTick = DateTime.UtcNow;
                scrollAnimationTimer.Start();
            }
            args.Handled = true;
        }

        private void OnScrollAnimationTick(object sender, EventArgs args)
        {
            double maximum = scroll.ScrollableHeight;
            scrollTargetOffset = Math.Max(0, Math.Min(maximum, scrollTargetOffset));
            double distance = scrollTargetOffset - scroll.VerticalOffset;
            if (Math.Abs(distance) < 0.5)
            {
                scroll.ScrollToVerticalOffset(scrollTargetOffset);
                scrollAnimationTimer.Stop();
                return;
            }
            // 基于时间的指数平滑：k 与帧率无关，高刷新率下滚动手感一致
            double dt = (DateTime.UtcNow - lastScrollAnimationTick).TotalMilliseconds;
            if (dt < 4) dt = 4;
            if (dt > 32) dt = 32;
            lastScrollAnimationTick = DateTime.UtcNow;
            double k = 1 - Math.Exp(-dt / 78.0);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + distance * k);
        }

        private void StartLayoutTween(double targetHeight)
        {
            // 窗口尚未显示（启动首帧同步）：直接归位，不启动补间（否则会在默认位置闪动后再跳变）
            if (!IsVisible)
            {
                layoutAnimationTimer.Stop();
                layoutAnimating = false;
                SetItemsCache(false);
                Height = Math.Max(targetHeight, ScaledHeaderBarHeight());
                if (capsuleMode) Width = collapsed ? ScaledCapsuleSize() : Math.Max(expandedWidth, ScaledCapsuleSize());
                RefreshCapsuleVisibility();
                ApplyFrameChrome();
                return;
            }
            double from = ActualHeight > 0 ? ActualHeight : Height;
            double to = Math.Max(targetHeight, ScaledHeaderBarHeight());
            if (Math.Abs(to - from) < 0.5)
            {
                // 起止高度几乎一致：直接归位，不启动补间
                Height = to;
                layoutAnimationTimer.Stop();
                layoutAnimating = false;
                SetItemsCache(false);
                RefreshCapsuleVisibility();
                ApplyFrameChrome();
                return;
            }
            layoutFromHeight = from;
            layoutToHeight = to;
            // 胶囊模式补间：宽度从当前值平滑过渡到目标（展开 292 / 折叠 CapsuleSize），
            // 让胶囊"长成"面板而不是宽度瞬间跳变（左下角圆角→直角突变即由此产生）
            double fromWidth = ActualWidth > 0 ? ActualWidth : Width;
            layoutFromWidth = fromWidth;
            layoutToWidth = capsuleMode ? (collapsed ? ScaledCapsuleSize() : Math.Max(expandedWidth, ScaledCapsuleSize())) : fromWidth;
            layoutAnimationStart = DateTime.UtcNow;
            layoutAnimating = true;
            SetItemsCache(true);
            layoutAnimationTimer.Start();
        }

        // 折叠目标高度：胶囊模式为胶囊边长，普通模式为标题条高度；两者都随界面缩放
        private double TargetCollapsedHeight()
        {
            return ScaledCollapsedHeight();
        }

        private void OnLayoutAnimationTick(object sender, EventArgs args)
        {
            double elapsed = (DateTime.UtcNow - layoutAnimationStart).TotalMilliseconds;
            double t = elapsed / LayoutAnimationDurationMs;
            if (t >= 1 || elapsed > 600)
            {
                Height = layoutToHeight;
                if (capsuleMode) Width = layoutToWidth;
                layoutAnimationTimer.Stop();
                layoutAnimating = false;
                SetItemsCache(false);
                // 补间完成：恢复胶囊与面板控件可见性
                RefreshCapsuleVisibility();
                ApplyFrameChrome();
                return;
            }
            // ease-out cubic：先快后慢
            double eased = 1 - Math.Pow(1 - t, 3);
            Height = layoutFromHeight + (layoutToHeight - layoutFromHeight) * eased;
            if (capsuleMode) Width = layoutFromWidth + (layoutToWidth - layoutFromWidth) * eased;
            // 用 this.Height 而非 ActualHeight（布局可能滞后一帧），并同步精确区域
        }

        /// 避让位置补间：仅在"位置变了、尺寸没变"时由 UpdateBounds 触发（Manager 避让推挤/还原），
        /// 用户拖拽/缩放/手势与折叠补间期间不触发。重复同步到同一目标时不重启动画。
        private void StartMoveTween(double toLeft, double toTop)
        {
            if (moveAnimating && Math.Abs(moveToLeft - toLeft) < 0.75 && Math.Abs(moveToTop - toTop) < 0.75) return;
            double fromLeft = Double.IsNaN(Left) ? toLeft : Left;
            double fromTop = Double.IsNaN(Top) ? toTop : Top;
            if (Math.Abs(toLeft - fromLeft) < 0.75 && Math.Abs(toTop - fromTop) < 0.75)
            {
                moveAnimationTimer.Stop(); moveAnimating = false;
                Left = toLeft; Top = toTop;
                return;
            }
            moveToLeft = toLeft; moveToTop = toTop;
            lastMoveAnimationTick = DateTime.UtcNow;
            moveAnimating = true;
            moveAnimationTimer.Start();
        }

        private void OnMoveAnimationTick(object sender, EventArgs args)
        {
            double nowDistance = Math.Abs(moveToLeft - Left) + Math.Abs(moveToTop - Top);
            if (nowDistance < 0.6)
            {
                moveAnimationTimer.Stop(); moveAnimating = false;
                Left = moveToLeft; Top = moveToTop;
                return;
            }
            // 按渲染时间做指数平滑：避让目标连续变化时保留当前位置和速度感，
            // 不再每次同步都从新的 cubic 起点重置，避免窗口被推挤时出现顿挫。
            DateTime now = DateTime.UtcNow;
            double dt = (now - lastMoveAnimationTick).TotalMilliseconds;
            if (dt < 4) dt = 4;
            if (dt > 32) dt = 32;
            lastMoveAnimationTick = now;
            double k = 1 - Math.Exp(-dt / 120.0);
            Left += (moveToLeft - Left) * k;
            Top += (moveToTop - Top) * k;
        }

        // 动画期间给项目面板启用位图缓存（一次光栅化、逐帧平移复用），动画结束后必须成对清除
        private void SetItemsCache(bool enable)
        {
            if (enable)
            {
                if (itemsPanel.CacheMode == null)
                    itemsPanel.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true };
            }
            else
            {
                itemsPanel.CacheMode = null;
            }
        }

        // 弹层不挂 SWCA 亚克力——分层弹窗上按矩形绘制会圆角外漏白（同面板
        // MaxAcrylicBuild 的结论），用高不透明中性底色近似 Win11 菜单层。
        // =============================================================
        private static readonly FontFamily MenuUiFont = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");

        private Color MenuSurfaceColor()
        {
            return darkTheme ? Color.FromArgb(246, 38, 38, 38) : Color.FromArgb(248, 252, 252, 252);
        }

        private Color MenuBorderColor()
        {
            return darkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        }

        private Color MenuHoverColor()
        {
            return darkTheme ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);
        }

        private Color MenuTextColor()
        {
            return darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
        }

        private Color MenuIconColor()
        {
            return darkTheme ? Color.FromRgb(185, 185, 185) : Color.FromRgb(92, 92, 92);
        }

        private Color MenuCheckColor()
        {
            return Color.FromRgb(52, 120, 246);
        }

        private ContextMenu StyledMenu()
        {
            ContextMenu menu = new ContextMenu
            {
                MinWidth = 208,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow = false,
                FontFamily = MenuUiFont,
                FontSize = Dip(13),
                RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new ScaleTransform(1, 1),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(menu, ClearTypeHint.Enabled);
            menu.Template = BuildMenuSurfaceTemplate(8);
            TrackMenuInteraction(menu);
            return menu;
        }

        // 菜单弹层表面（顶层菜单与子菜单共用）：圆角 Border + Win11 立体阴影 + ItemsPresenter
        private ControlTemplate BuildMenuSurfaceTemplate(double cornerRadius)
        {
            ControlTemplate template = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(MenuSurfaceColor()));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(MenuBorderColor()));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            border.SetValue(Border.PaddingProperty, new Thickness(2, 4, 2, 4));
            border.SetValue(UIElement.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Direction = 270,
                Color = Colors.Black,
                Opacity = darkTheme ? 0.38 : 0.14
            });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private MenuItem StyledMenuItem(string text, string iconName)
        {
            return BuildMenuItem(text, iconName, false, false);
        }

        private MenuItem StyledCheckMenuItem(string text, string iconName)
        {
            MenuItem item = BuildMenuItem(text, iconName, true, false);
            item.IsCheckable = true;
            return item;
        }

        // 子菜单父项（DeskBox 的 MenuFlyoutSubItem 对应物）：悬停展开右侧弹层
        private MenuItem StyledSubMenuItem(string text, string iconName)
        {
            return BuildMenuItem(text, iconName, false, true);
        }

        private void UpdateMenuItemDisplay(MenuItem item, string text, string iconName)
        {
            if (item == null) return;
            Grid row = item.Header as Grid;
            if (row == null) return;

            foreach (UIElement child in row.Children)
            {
                TextBlock tb = child as TextBlock;
                if (tb != null)
                {
                    tb.Text = text;
                    break;
                }
            }

            if (!String.IsNullOrEmpty(iconName))
            {
                for (int i = 0; i < row.Children.Count; i++)
                {
                    UIElement child = row.Children[i];
                    if (Grid.GetColumn(child) == 0)
                    {
                        row.Children.RemoveAt(i);
                        break;
                    }
                }
                FrameworkElement newIcon = CreateMenuPhosphorIcon(iconName, MenuIconColor(), 16, HorizontalAlignment.Center);
                newIcon.Width = 16;
                newIcon.Height = 16;
                newIcon.HorizontalAlignment = HorizontalAlignment.Center;
                newIcon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(newIcon, 0);
                row.Children.Insert(0, newIcon);
            }
        }

        private MenuItem BuildMenuItem(string text, string iconName, bool checkable, bool hasSubmenu)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (hasSubmenu)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            if (!String.IsNullOrEmpty(iconName))
            {
                FrameworkElement icon = CreateMenuPhosphorIcon(iconName, MenuIconColor(), 16, HorizontalAlignment.Center);
                icon.Width = 16;
                icon.Height = 16;
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);
            }

            TextBlock label = new TextBlock
            {
                Text = text,
                FontFamily = MenuUiFont,
                FontSize = Dip(12.5),
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Foreground = new SolidColorBrush(MenuTextColor())
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (hasSubmenu)
            {
                FrameworkElement caret = CreateMenuPhosphorIcon("chevron-right", MenuIconColor(), 12, HorizontalAlignment.Right);
                caret.HorizontalAlignment = HorizontalAlignment.Right;
                caret.VerticalAlignment = VerticalAlignment.Center;
                caret.Margin = new Thickness(4, 0, 2, 0);
                Grid.SetColumn(caret, 2);
                row.Children.Add(caret);
            }

            MenuItem item = new MenuItem { Header = row, Tag = label };
            ControlTemplate template = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.Name = "Root";

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.MarginProperty, new Thickness(3, 1, 3, 1));
            border.SetValue(Border.PaddingProperty, new Thickness(4, 0, 6, 0));
            border.SetValue(Border.MinHeightProperty, 32.0);

            FrameworkElementFactory inner = new FrameworkElementFactory(typeof(DockPanel));
            inner.SetValue(DockPanel.LastChildFillProperty, true);

            // 对勾槽：矢量 Checkmark，置于右侧保证所有项左侧图标槽绝对对齐
            if (checkable)
            {
                FrameworkElementFactory checkContainer = new FrameworkElementFactory(typeof(Grid));
                checkContainer.Name = "CheckSlot";
                checkContainer.SetValue(FrameworkElement.WidthProperty, 20.0);
                checkContainer.SetValue(DockPanel.DockProperty, Dock.Right);
                checkContainer.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 2, 0));

                FrameworkElementFactory checkShape = new FrameworkElementFactory(typeof(ShapePath));
                checkShape.Name = "Check";
                StreamGeometry checkGeo = (StreamGeometry)Geometry.Parse("M216.49,80.49a12,12,0,0,1,0,17l-104,104a12,12,0,0,1-17,0l-56-56a12,12,0,0,1,17-17L104,176l95.51-95.51A12,12,0,0,1,216.49,80.49Z");
                checkGeo = checkGeo.Clone();
                checkGeo.FillRule = FillRule.Nonzero;
                checkShape.SetValue(ShapePath.DataProperty, checkGeo);
                checkShape.SetValue(ShapePath.FillProperty, new SolidColorBrush(MenuCheckColor()));
                checkShape.SetValue(ShapePath.WidthProperty, 12.0);
                checkShape.SetValue(ShapePath.HeightProperty, 12.0);
                checkShape.SetValue(ShapePath.StretchProperty, Stretch.Uniform);
                checkShape.SetValue(ShapePath.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                checkShape.SetValue(ShapePath.VerticalAlignmentProperty, VerticalAlignment.Center);
                checkShape.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                checkContainer.AppendChild(checkShape);

                inner.AppendChild(checkContainer);
            }

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(DockPanel.DockProperty, Dock.Left);
            inner.AppendChild(content);

            border.AppendChild(inner);
            dock.AppendChild(border);

            if (hasSubmenu)
            {
                FrameworkElementFactory popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup));
                popup.Name = "PART_Popup";
                popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
                popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Right);
                popup.SetValue(System.Windows.Controls.Primitives.Popup.FocusableProperty, false);
                popup.SetValue(System.Windows.Controls.Primitives.Popup.StaysOpenProperty, true);
                popup.SetValue(System.Windows.Controls.Primitives.Popup.PopupAnimationProperty, System.Windows.Controls.Primitives.PopupAnimation.Fade);
                Binding openBinding = new Binding("IsSubmenuOpen");
                openBinding.RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent);
                popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty, openBinding);

                FrameworkElementFactory dropShadow = new FrameworkElementFactory(typeof(Border));
                dropShadow.SetValue(Border.BackgroundProperty, new SolidColorBrush(MenuSurfaceColor()));
                dropShadow.SetValue(Border.BorderBrushProperty, new SolidColorBrush(MenuBorderColor()));
                dropShadow.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                dropShadow.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                dropShadow.SetValue(Border.PaddingProperty, new Thickness(2, 4, 2, 4));

                FrameworkElementFactory subScroll = new FrameworkElementFactory(typeof(ScrollViewer));
                subScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
                FrameworkElementFactory itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
                subScroll.AppendChild(itemsPresenter);
                dropShadow.AppendChild(subScroll);
                popup.AppendChild(dropShadow);
                dock.AppendChild(popup);
            }

            template.VisualTree = dock;

            Trigger highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(MenuHoverColor()), "Bd"));
            template.Triggers.Add(highlightTrigger);

            if (checkable)
            {
                Trigger checkTrigger = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
                checkTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Check"));
                template.Triggers.Add(checkTrigger);
            }

            Trigger disabled = new Trigger { Property = MenuItem.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(DockPanel.OpacityProperty, 0.36, "Root"));
            template.Triggers.Add(disabled);

            item.Template = template;
            return item;
        }

        // 分组分隔线：Win11 菜单的通栏 hairline，上下留 4px 呼吸
        private Separator StyledSeparator()
        {
            Separator separator = new Separator();
            ControlTemplate template = new ControlTemplate(typeof(Separator));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.HeightProperty, 1.0);
            border.SetValue(Border.MarginProperty, new Thickness(1, 4, 1, 4));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(darkTheme ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0)));
            template.VisualTree = border;
            separator.Template = template;
            return separator;
        }

        private ContextMenu CreateHeaderMenu()
        {
            ContextMenu menu = StyledMenu();
            MenuItem rename = StyledMenuItem("重命名分区", "pencil");
            rename.Click += delegate { RunAfterMenuClosed(menu, BeginRename); };
            MenuItem pin = StyledMenuItem(pinned ? "取消固定" : "固定到桌面", "pin");
            pin.Click += delegate { host.PostManagerEvent("panelPin", panelId); };
            MenuItem collapse = StyledMenuItem(collapsed ? "展开格子" : "折叠格子", collapsed ? "chevron-down" : "chevron-up");
            collapse.Click += delegate { host.PostManagerEvent("panelCollapse", panelId); };
            MenuItem openFolder = StyledMenuItem("打开文件夹", "folder");
            openFolder.Click += delegate
            {
                try
                {
                    string path = DesktopWindow.GetManagedCategoryPath(categoryName);
                    Directory.CreateDirectory(path);
                    OpenPath(path);
                }
                catch (Exception error)
                {
                    host.PostManagerEvent("operationError", error.Message);
                }
            };
            MenuItem refresh = StyledMenuItem("刷新分区", "refresh");
            refresh.Click += delegate { host.PostManagerEvent("panelRefresh", true); };

            // 窄标题条（< 200 DIP）下四个工具按钮被收起，这里提供等价入口（宽标题条下同样可用）
            MenuItem search = StyledMenuItem("面板内搜索", "search");
            search.Click += delegate { RunAfterMenuClosed(menu, delegate { ToggleSearchBar(); }); };
            MenuItem size = BuildItemSizeSubmenu(menu);

            MenuItem newTodo = StyledMenuItem("新建待办清单", "check");
            newTodo.Click += delegate { host.PostManagerEvent("noteCreate", "todo"); };
            MenuItem newMemo = StyledMenuItem("新建随手备忘", "file-text");
            newMemo.Click += delegate { host.PostManagerEvent("noteCreate", "note"); };

            menu.Opened += delegate
            {
                UpdateMenuItemDisplay(pin, pinned ? "取消固定" : "固定到桌面", "pin");
                UpdateMenuItemDisplay(collapse, collapsed ? "展开格子" : "折叠格子", collapsed ? "chevron-down" : "chevron-up");
            };

            menu.Items.Add(rename);
            menu.Items.Add(pin);
            menu.Items.Add(collapse);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(search);
            menu.Items.Add(size);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(newTodo);
            menu.Items.Add(newMemo);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(openFolder);
            menu.Items.Add(refresh);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(BuildArrangeSubmenu(true, menu));
            return menu;
        }

        // 标题条右键菜单里的「调整项目显示大小」子菜单：与尺寸按钮弹出的预设菜单共享同一组档位
        private MenuItem BuildItemSizeSubmenu(ContextMenu owner)
        {
            MenuItem parent = StyledSubMenuItem("调整项目显示大小", "sliders");
            parent.Items.Add(BuildItemSizePreset(owner, "紧凑", 60, 40, 9));
            parent.Items.Add(BuildItemSizePreset(owner, "标准", 76, 54, 10));
            parent.Items.Add(BuildItemSizePreset(owner, "大", 92, 64, 11));
            parent.Items.Add(BuildItemSizePreset(owner, "超大", 108, 72, 12));
            parent.SubmenuOpened += delegate
            {
                foreach (object raw in parent.Items)
                {
                    MenuItem option = raw as MenuItem;
                    if (option == null || option.Tag == null) continue;
                    option.IsChecked = Math.Abs(itemSize - Convert.ToDouble(option.Tag)) < 0.5;
                }
            };
            return parent;
        }

        private MenuItem BuildItemSizePreset(ContextMenu owner, string text, double nextItemSize, double nextIconSize, double nextLabelSize)
        {
            MenuItem option = StyledCheckMenuItem(text, "sliders");
            option.Tag = nextItemSize;
            option.Click += delegate { RunAfterMenuClosed(owner, delegate { SetItemDisplaySize(nextItemSize, nextIconSize, nextLabelSize, true); }); };
            return option;
        }

        // “视图与排序”子菜单：视图单选 + 排序单选（DeskBox MenuFlyoutSubItem 分组方式）。
        // 打开时按当前 viewMode/sortMode 回填勾选，避免快照过期。
        private MenuItem BuildArrangeSubmenu(bool includeArrangeHeader, ContextMenu owner)
        {
            MenuItem arrange = StyledSubMenuItem(includeArrangeHeader ? "视图与排序" : "排序方式", includeArrangeHeader ? "sliders" : "sort");
            MenuItem grid = StyledCheckMenuItem("网格视图", "grid");
            grid.Click += delegate { RunAfterMenuClosed(owner, delegate { SetViewMode("grid", true); }); };
            MenuItem list = StyledCheckMenuItem("列表视图", "list");
            list.Click += delegate { RunAfterMenuClosed(owner, delegate { SetViewMode("list", true); }); };
            MenuItem byName = StyledCheckMenuItem("按首字母排序", "sort");
            byName.Click += delegate { RunAfterMenuClosed(owner, delegate { SetSortMode("name", true); }); };
            MenuItem byModified = StyledCheckMenuItem("按时间排序", "clock");
            byModified.Click += delegate { RunAfterMenuClosed(owner, delegate { SetSortMode("modified", true); }); };
            MenuItem bySize = StyledCheckMenuItem("按大小排序", "drive");
            bySize.Click += delegate { RunAfterMenuClosed(owner, delegate { SetSortMode("size", true); }); };
            arrange.Items.Add(grid);
            arrange.Items.Add(list);
            arrange.Items.Add(StyledSeparator());
            arrange.Items.Add(byName);
            arrange.Items.Add(byModified);
            arrange.Items.Add(bySize);
            arrange.SubmenuOpened += delegate
            {
                grid.IsChecked = viewMode == "grid";
                list.IsChecked = viewMode == "list";
                byName.IsChecked = sortMode == "name";
                byModified.IsChecked = sortMode == "modified";
                bySize.IsChecked = sortMode == "size";
            };
            return arrange;
        }

        private ContextMenu CreateScrollMenu()
        {
            ContextMenu menu = StyledMenu();
            MenuItem refresh = StyledMenuItem("刷新分区", "refresh");
            refresh.Click += delegate { host.PostManagerEvent("panelRefresh", true); };
            MenuItem openFolder = StyledMenuItem("打开文件夹", "folder");
            openFolder.Click += delegate
            {
                try
                {
                    string path = DesktopWindow.GetManagedCategoryPath(categoryName);
                    Directory.CreateDirectory(path);
                    OpenPath(path);
                }
                catch (Exception error)
                {
                    host.PostManagerEvent("operationError", error.Message);
                }
            };
            MenuItem grid = StyledCheckMenuItem("网格视图", "grid");
            grid.Click += delegate { RunAfterMenuClosed(menu, delegate { SetViewMode("grid", true); }); };
            MenuItem list = StyledCheckMenuItem("列表视图", "list");
            list.Click += delegate { RunAfterMenuClosed(menu, delegate { SetViewMode("list", true); }); };
            MenuItem sortSubmenu = BuildArrangeSubmenu(false, menu);
            MenuItem collapse = StyledMenuItem(collapsed ? "展开格子" : "折叠格子", collapsed ? "chevron-down" : "chevron-up");
            collapse.Click += delegate { host.PostManagerEvent("panelCollapse", panelId); };
            MenuItem pin = StyledMenuItem(pinned ? "取消固定" : "固定到桌面", "pin");
            pin.Click += delegate { host.PostManagerEvent("panelPin", panelId); };
            menu.Opened += delegate
            {
                grid.IsChecked = viewMode == "grid";
                list.IsChecked = viewMode == "list";
                UpdateMenuItemDisplay(collapse, collapsed ? "展开格子" : "折叠格子", collapsed ? "chevron-down" : "chevron-up");
                UpdateMenuItemDisplay(pin, pinned ? "取消固定" : "固定到桌面", "pin");
            };
            menu.Items.Add(refresh);
            menu.Items.Add(openFolder);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(grid);
            menu.Items.Add(list);
            menu.Items.Add(sortSubmenu);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(collapse);
            menu.Items.Add(pin);
            return menu;
        }

        private ContextMenu CreateViewMenu()
        {
            ContextMenu menu = StyledMenu();
            MenuItem grid = StyledCheckMenuItem("网格视图", "grid");
            grid.Click += delegate { RunAfterMenuClosed(menu, delegate { SetViewMode("grid", true); }); };
            MenuItem list = StyledCheckMenuItem("列表视图", "list");
            list.Click += delegate { RunAfterMenuClosed(menu, delegate { SetViewMode("list", true); }); };
            MenuItem byName = StyledCheckMenuItem("按首字母排序", "sort");
            byName.Click += delegate { RunAfterMenuClosed(menu, delegate { SetSortMode("name", true); }); };
            MenuItem byModified = StyledCheckMenuItem("按时间排序（最新优先）", "clock");
            byModified.Click += delegate { RunAfterMenuClosed(menu, delegate { SetSortMode("modified", true); }); };
            MenuItem bySize = StyledCheckMenuItem("按大小排序（最大优先）", "drive");
            bySize.Click += delegate { RunAfterMenuClosed(menu, delegate { SetSortMode("size", true); }); };
            menu.Opened += delegate
            {
                grid.IsChecked = viewMode == "grid";
                list.IsChecked = viewMode == "list";
                byName.IsChecked = sortMode == "name";
                byModified.IsChecked = sortMode == "modified";
                bySize.IsChecked = sortMode == "size";
            };
            menu.Items.Add(grid);
            menu.Items.Add(list);
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(byName);
            menu.Items.Add(byModified);
            menu.Items.Add(bySize);
            return menu;
        }

        private ContextMenu CreateItemSizeMenu()
        {
            ContextMenu menu = StyledMenu();
            AddItemSizePreset(menu, "紧凑", "sliders", 60, 40, 9);
            AddItemSizePreset(menu, "标准", "sliders", 76, 54, 10);
            AddItemSizePreset(menu, "大", "sliders", 92, 64, 11);
            AddItemSizePreset(menu, "超大", "sliders", 108, 72, 12);
            menu.Opened += delegate
            {
                foreach (object raw in menu.Items)
                {
                    MenuItem option = raw as MenuItem;
                    if (option == null || option.Tag == null) continue;
                    option.IsChecked = Math.Abs(itemSize - Convert.ToDouble(option.Tag)) < 0.5;
                }
            };
            return menu;
        }

        private void AddItemSizePreset(ContextMenu menu, string text, string iconName, double nextItemSize, double nextIconSize, double nextLabelSize)
        {
            MenuItem option = StyledCheckMenuItem(text, iconName);
            option.Tag = nextItemSize;
            option.Click += delegate { RunAfterMenuClosed(menu, delegate { SetItemDisplaySize(nextItemSize, nextIconSize, nextLabelSize, true); }); };
            menu.Items.Add(option);
        }

        private void SetItemDisplaySize(double nextItemSize, double nextIconSize, double nextLabelSize, bool notify)
        {
            itemSize = Math.Max(44, Math.Min(112, nextItemSize));
            iconSize = Math.Max(24, Math.Min(72, nextIconSize));
            labelSize = Math.Max(8, Math.Min(18, nextLabelSize));
            RebuildItems();
            if (!notify) return;
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["id"] = panelId;
            payload["itemSize"] = itemSize;
            payload["iconSize"] = iconSize;
            payload["labelSize"] = labelSize;
            host.PostManagerEvent("panelItemSize", payload);
        }

        private void SetViewMode(string mode, bool notify)
        {
            if (mode != "grid" && mode != "list") mode = "grid";
            if (viewMode == mode && notify) return;
            viewMode = mode;
            viewButton.Content = CreatePhosphorIcon(viewMode == "grid" ? "grid" : "list", Dip(15), true);
            RebuildItems();
            if (notify)
            {
                pendingViewMode = viewMode;
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["id"] = panelId;
                payload["mode"] = viewMode;
                host.PostManagerEvent("panelView", payload);
            }
        }

        private void SetSortMode(string mode, bool notify)
        {
            if (mode != "name" && mode != "modified" && mode != "size") mode = "name";
            if (sortMode == mode && notify) return;
            sortMode = mode;
            RebuildItems();
            if (notify)
            {
                pendingSortMode = sortMode;
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["id"] = panelId;
                payload["mode"] = sortMode;
                host.PostManagerEvent("panelSort", payload);
            }
        }

        private void OnSourceInitialized(object sender, EventArgs args)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                source.AddHook(WndProc);
            }
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow));
            // 桌面层挂载（DeskBox 策略，WidgetLayer 带回读验证）：优先挂 SHELLDLL_DefView
            // （图标视图之上、普通窗口之下，Win+D 不收走），桌面未就绪时回退 Progman 并自动升级。
            WidgetLayer.Attach(hwnd);
            RefreshDpiScale();
            UpdateWindowRegion();   // 首次显示前裁出圆角轮廓（后续由 WM_SIZE 保持同步）
        }

        // 当前窗口所在显示器的 DPI 缩放（GetDpiForWindow/96），用于把图标解码到正确的物理像素档位
        private void RefreshDpiScale()
        {
            double scale = 1.0;
            if (hwnd != IntPtr.Zero)
            {
                try { scale = GetDpiForWindow(hwnd) / 96.0; }
                catch { scale = 1.0; }
            }
            dpiScale = scale > 0 ? scale : 1.0;
        }

        // 宿主需要的原生句柄（TransferIntoCategory 用作 IFileOperation 的 owner 窗口等）
        internal IntPtr NativeHandle { get { return hwnd; } }

        // 唤起会话用：无激活显示（DeskBox RaiseWidgetsFromTrayAsync 先 Show 后统一激活的语义）
        internal void ShowForLayerRaise()
        {
            bool previous = ShowActivated;
            ShowActivated = false;
            try
            {
                if (!IsVisible) Show();
                else if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
            finally { ShowActivated = previous; }
        }

        public void UpdateBounds(double x, double y, double width, double height, bool isPinned)
        {
            pinned = isPinned;
            // 记录最近一次同步的原始 DIP 参数：DPI 变化时用它恢复窗口尺寸（防止 WPF 缩放被写回配置）
            hasSyncedBounds = true;
            syncedX = x; syncedY = y; syncedWidth = width; syncedHeight = height;
            pinButton.Opacity = pinned ? 1 : 0.45;
            pinButton.Foreground = new SolidColorBrush(pinned ? accent : (darkTheme ? Color.FromRgb(196, 201, 208) : Color.FromRgb(86, 92, 101)));
            Rect workArea = GetVirtualScreenBounds();
            // WPF 会按 MinWidth 强制钳制窗口宽度：胶囊模式折叠时放宽到胶囊边长，否则恢复面板最小宽度。
            // 注意：MinWidth 缩小时 WPF 不会自动重排 Left/Width，窗口位置/尺寸依赖下方每次同步的显式赋值。
            // 补间期间保持胶囊宽度：展开补间中 collapsed=false，若 MinWidth 立即恢复 230，
            if (layoutAnimating && (DateTime.UtcNow - layoutAnimationStart).TotalMilliseconds > 600)
            {
                layoutAnimationTimer.Stop();
                layoutAnimating = false;
                SetItemsCache(false);
            }
            // WPF 会把补间中的 Width（从 52 起步）强制钳到 230，宽度动画变成"先跳 230 再补 62px"。
            MinWidth = ((collapsed && capsuleMode) || (capsuleMode && layoutAnimating)) ? ScaledCapsuleSize() : Dip(230);
            double nextWidth = (collapsed && capsuleMode) ? ScaledCapsuleSize() : Math.Max(MinWidth, Math.Min(workArea.Width, width));
            expandedWidth = Math.Max(MinWidth, Math.Min(workArea.Width, width));   // 记录展开宽度（胶囊补间目标）
            double nextExpandedHeight = Math.Max(150, Math.Min(workArea.Height, height));
            double nextHeight = collapsed ? TargetCollapsedHeight() : nextExpandedHeight;
            // 边缘溢出修复：胶囊折叠态对位置做二次钳制并额外留 10px 边距（展开态保持原贴边行为）。
            // 展开面板若原本贴着工作区右/下缘，52px 胶囊沿用同一坐标会顶到屏幕边缘/部分出界；
            // 钳制后胶囊在任何 DPI/布局下都不会在屏幕边缘溢出。
            // 权衡：贴边展开的面板在折叠/展开瞬间，胶囊位置会比展开位最多偏移 10px（视觉跳动很小）。
            double edgeMargin = (collapsed && capsuleMode) ? 10 : 0;
            double requestedLeft = SystemParameters.WorkArea.Left + x;
            double requestedTop = SystemParameters.WorkArea.Top + y;
            double nextLeft = Math.Max(workArea.Left + edgeMargin, Math.Min(requestedLeft, workArea.Right - nextWidth - edgeMargin));
            double nextTop = Math.Max(workArea.Top + edgeMargin, Math.Min(requestedTop, workArea.Bottom - nextHeight - edgeMargin));
            expandedHeight = nextExpandedHeight;
            // 自愈守护：折叠且胶囊模式下，若控件可见性异常，立即强制拉起胶囊
            if (collapsed && capsuleMode && capsuleView.Visibility != Visibility.Visible)
            {
                RefreshCapsuleVisibility();
            }
            bool positionChanged = Double.IsNaN(Left) ||
                Math.Abs(Left - nextLeft) > 0.75 || Math.Abs(Top - nextTop) > 0.75;
            bool sizeChanged = Math.Abs(Width - nextWidth) > 0.75 || Math.Abs(Height - nextHeight) > 0.75;
            bool geometryChanged = positionChanged || sizeChanged;
            UpdateNarrowHeaderState(nextWidth);   // 窄宽度标题条：< 200 DIP 时只留标题 + 折叠 + 菜单按钮
            if (!geometryChanged) return;
            // 纯位置变化（Manager 避让推挤/还原）：滑动过去；拖拽/缩放/手势/补间期间直接归位
            if (positionChanged && !sizeChanged && !layoutAnimating && !moving && !resizing && !capsuleGestureActive && !Double.IsNaN(Left))
                StartMoveTween(nextLeft, nextTop);
            else
            {
                moveAnimationTimer.Stop();
                moveAnimating = false;
                Left = nextLeft;
                Top = nextTop;
            }
            if (!layoutAnimating) { Width = nextWidth; Height = nextHeight; }   // 补间中宽高都由补间驱动
            RefreshItemLayoutForSize();
        }

        internal void UpdateContent(PanelSyncData value)
        {
            // Coarse skip: an unchanged revision means nothing in this panel's
            // sync payload changed, so there is nothing to re-apply.
            if (value.revision == lastRevision) return;
            lastRevision = value.revision;
            string nextItemsSignature = BuildItemsSignature(value);
            bool itemsChanged = nextItemsSignature != contentItemsSignature;
            bool isFirstSync = contentItemsSignature.Length == 0; // 首次同步（初始填充）不触发呼吸，避免应用启动时全分区一起闪动
            contentItemsSignature = nextItemsSignature;
            categoryName = String.IsNullOrWhiteSpace(value.name) ? "分区" : value.name;
            readOnlyPanel = value.readOnly;
            title.Text = categoryName;
            accent = ParseColor(String.IsNullOrWhiteSpace(value.color) ? "#e98687" : value.color);
            themeAccent = ParseColor(String.IsNullOrWhiteSpace(value.themeAccent) ? "#e98687" : value.themeAccent);
            glassOpacity = value.glassOpacity >= 0 && value.glassOpacity <= 100 ? (int)Math.Round(value.glassOpacity) : 88;
            // 界面缩放：越界（含旧前端未同步时的 0）一律回落到 100，避免字号被压成 0
            double nextUiScale = value.uiScale >= 80 && value.uiScale <= 130 ? value.uiScale : 100;
            bool uiScaleChanged = Math.Abs(nextUiScale - uiScale) > 0.01;
            uiScale = nextUiScale;
            if (uiScaleChanged) ApplyScaleMetrics();
            if (!String.IsNullOrWhiteSpace(value.material)) materialMode = value.material;
            darkTheme = value.theme == "dark";
            paperSurface = String.IsNullOrWhiteSpace(value.headerSurface)
                ? (darkTheme ? Color.FromRgb(34, 33, 30) : Color.FromRgb(255, 255, 255))
                : ParseColor(value.headerSurface);
            compact = value.compact;
            itemSize = value.itemSize >= 44 && value.itemSize <= 112 ? value.itemSize : compact ? 60 : 76;
            iconSize = value.iconSize >= 24 && value.iconSize <= 72 ? value.iconSize : compact ? 40 : 54;
            itemGap = value.itemGap >= 0 && value.itemGap <= 24 ? value.itemGap : 6;
            labelSize = value.labelSize >= 8 && value.labelSize <= 18 ? value.labelSize : (compact ? 11 : 12);
            itemAlignment = value.itemAlignment == "center" || value.itemAlignment == "right" ? value.itemAlignment : "left";
            itemColumns = Math.Max(0, Math.Min(8, value.itemColumns));
            showLabels = value.showLabels;
            showExtensions = value.showExtensions;
            labelPosition = value.labelPosition == "right" ? "right" : "bottom";
            autoHide = value.autoHide;
            autoHideDelaySeconds = value.autoHideDelaySeconds >= 1 && value.autoHideDelaySeconds <= 10 ? value.autoHideDelaySeconds : 2;
            if (!autoHide && autoHidden) { autoHidden = false; Show(); }
            UpdateSearchVisuals();
            string incomingViewMode = String.IsNullOrWhiteSpace(value.viewMode) ? "grid" : value.viewMode;
            string incomingSortMode = String.IsNullOrWhiteSpace(value.sortMode) ? "name" : value.sortMode;
            if (!String.IsNullOrEmpty(pendingViewMode))
            {
                if (incomingViewMode == pendingViewMode) pendingViewMode = null;
                else incomingViewMode = pendingViewMode;
            }
            if (!String.IsNullOrEmpty(pendingSortMode))
            {
                if (incomingSortMode == pendingSortMode) pendingSortMode = null;
                else incomingSortMode = pendingSortMode;
            }
            viewMode = incomingViewMode == "list" ? "list" : "grid";
            sortMode = incomingSortMode == "modified" || incomingSortMode == "size" ? incomingSortMode : "name";
            viewButton.Content = CreatePhosphorIcon(viewMode == "grid" ? "grid" : "list", Dip(15), true);
            // 胶囊模式（全局开关）与分类图标：同 JSON 属性名直传；图标变化时重渲染胶囊
            bool previousCapsuleMode = capsuleMode;
            capsuleMode = value.capsuleMode;
            string nextCategoryIcon = value.categoryIcon == null ? "folder" : value.categoryIcon;
            bool categoryIconChanged = !String.Equals(categoryIcon, nextCategoryIcon, StringComparison.Ordinal);
            categoryIcon = nextCategoryIcon;
            if (categoryIconChanged || iconShape.Data == null || (uiScaleChanged && categoryIcon.StartsWith("data:", StringComparison.Ordinal))) UpdateCapsuleIcon();

            bool nextCollapsed = value.collapsed;
            if (nextCollapsed != collapsed || capsuleMode != previousCapsuleMode)
            {
                collapsed = nextCollapsed;
                // 胶囊模式补间中（折叠/展开）内容与标题条都保持隐藏：窗口宽高正在过渡，
                // 提前显示会挤压变形；补间结束由 RefreshCapsuleVisibility 统一恢复
                itemsPanel.Visibility = (collapsed || (capsuleMode && layoutAnimating)) ? Visibility.Collapsed : Visibility.Visible;
                collapseButton.Content = CreatePhosphorIcon(collapsed ? "chevron-right" : "chevron-down", Dip(15), true);
                // 胶囊模式折叠：无条件保持胶囊可见，避免补间中断或计时器挂起导致窗口隐形消失
                capsuleView.Visibility = (collapsed && capsuleMode) ? Visibility.Visible : Visibility.Collapsed;
                header.Visibility = (collapsed && capsuleMode || (capsuleMode && layoutAnimating)) ? Visibility.Collapsed : Visibility.Visible;
                // 折叠时隐藏右下角缩放手柄与四边缩放热区（缩放只对展开面板有意义）
                resizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                foreach (Border adorner in resizeAdorners) adorner.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                // UpdateContent 在 UpdateBounds 之前执行，expandedHeight 要到 UpdateBounds 才刷新；
                // 若展开与尺寸变化发生在同一次同步，优先用本次同步负载里的最新高度（带防御）。
                double expandTarget = collapsed ? TargetCollapsedHeight() : (Double.IsNaN(value.height) || value.height <= 0 ? expandedHeight : value.height);
                StartLayoutTween(expandTarget);
            }
            PanelItemData[] items = value.items;
            if (itemsChanged)
            {
                WarmDecodeIcons(items);
                // 检测新增项目 id（新文件到达提示）：面板未折叠且出现新 id 时，重建后触发呼吸光晕
                bool newItemsArrived = !collapsed && items != null && !isFirstSync && HasNewItemIds(items);
                // 同步负载对未变化的项目省略 iconUrl，这里复用上一次的图标，避免面板上的图标闪失。
                Dictionary<string, string> previousIcons = new Dictionary<string, string>();
                foreach (Dictionary<string, object> entry in currentItems)
                {
                    string previousId = Convert.ToString(entry["id"]);
                    string previousIcon = entry.ContainsKey("iconUrl") ? Convert.ToString(entry["iconUrl"]) : null;
                    if (!String.IsNullOrEmpty(previousId) && !String.IsNullOrEmpty(previousIcon)) previousIcons[previousId] = previousIcon;
                }
                currentItems.Clear();
                if (items != null)
                {
                    foreach (PanelItemData data in items)
                    {
                        if (data == null || String.IsNullOrEmpty(data.id)) continue;
                        string iconUrl = data.iconUrl;
                        if (String.IsNullOrEmpty(iconUrl)) previousIcons.TryGetValue(data.id, out iconUrl);
                        Dictionary<string, object> entry = new Dictionary<string, object>();
                        entry["id"] = data.id;
                        entry["name"] = data.name;
                        entry["path"] = data.path;
                        entry["extension"] = data.extension;
                        entry["modifiedAt"] = data.modifiedAt;
                        entry["size"] = data.size;
                        entry["kind"] = data.kind;
                        entry["readOnly"] = data.readOnly;
                        entry["favorite"] = data.favorite;
                        entry["pinned"] = data.pinned;
                        entry["iconUrl"] = iconUrl;
                        currentItems.Add(entry);
                    }
                }
                RebuildItems();
                if (newItemsArrived) TriggerBreath();
            }
            if (String.IsNullOrWhiteSpace(searchText)) count.Text = currentItems.Count.ToString();
            else
            {
                int visibleCount = 0;
                foreach (Dictionary<string, object> entry in currentItems) if (MatchesSearch(entry)) visibleCount++;
                count.Text = visibleCount.ToString() + "/" + currentItems.Count.ToString();
            }
            ApplyTheme();
            RefreshCapsuleVisibility();
        }

        // 后台预解码新增项目图标（复用静态缓存）：避免首屏 UI 线程逐个 base64 解码卡顿造成的闪烁。
        // 去重（缓存已有或已在排队）+ 并发上限 4（后台任务里同步 Wait，C# 5 下不用 async/await）
        private void WarmDecodeIcons(PanelItemData[] items)
        {
            if (items == null) return;
            int targetPx = IconTargetPx();   // 同一次批量用同一个像素档位，避免逐项重复解析
            foreach (PanelItemData data in items)
            {
                if (data == null || String.IsNullOrWhiteSpace(data.iconUrl)) continue;
                string iconUrl = data.iconUrl;
                string key = targetPx + "|" + iconUrl;
                lock (decodedImageCacheLock)
                {
                    if (decodedImageCache.ContainsKey(key)) continue;
                    if (!pendingIconDecodes.Add(key)) continue;   // 已在排队：跳过重复任务
                }
                System.Threading.Tasks.Task.Run(delegate
                {
                    iconDecodeSlots.WaitOne();
                    try { DecodeImage(iconUrl, targetPx); }
                    finally
                    {
                        iconDecodeSlots.Release();
                        lock (decodedImageCacheLock) { pendingIconDecodes.Remove(key); }
                    }
                });
            }
        }
        // 新文件到达：面板边框呼吸光晕一次（0.85 淡出到 0，EaseOut），完成后隐藏归位
        private void TriggerBreath()
        {
            if (collapsed || breathOverlay == null) return;
            int run = ++breathRun;
            if (breathStoryboard != null) breathStoryboard.Stop(breathOverlay);
            Storyboard story = new Storyboard { FillBehavior = FillBehavior.Stop };
            DoubleAnimation fade = new DoubleAnimation
            {
                From = 0.85,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(900),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, breathOverlay);
            Storyboard.SetTargetProperty(fade, new PropertyPath(Border.OpacityProperty));
            story.Children.Add(fade);
            story.Completed += delegate
            {
                if (breathRun != run) return; // 已被新的触发接管，忽略旧动画的完成回调
                breathOverlay.Opacity = 0;
                breathOverlay.Visibility = Visibility.Collapsed;
                breathStoryboard = null;
            };
            breathStoryboard = story;
            breathOverlay.Opacity = 0.85;
            breathOverlay.Visibility = Visibility.Visible;
            story.Begin(breathOverlay, true);
        }

        // 判断新 items 中是否存在当前列表没有的新 id（用于新文件到达的呼吸提示）
        private bool HasNewItemIds(PanelItemData[] items)
        {
            HashSet<string> previousIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> entry in currentItems)
            {
                string previousId = Convert.ToString(entry["id"]);
                if (!String.IsNullOrEmpty(previousId)) previousIds.Add(previousId);
            }
            foreach (PanelItemData data in items)
            {
                if (data == null || String.IsNullOrEmpty(data.id)) continue;
                if (!previousIds.Contains(data.id)) return true;
            }
            return false;
        }

        // 胶囊可见性刷新：折叠小图标与展开卡片互斥切换
        private void RefreshCapsuleVisibility()
        {
            if (collapsed && capsuleMode)
            {
                capsuleView.Visibility = Visibility.Visible;
                header.Visibility = Visibility.Collapsed;
                itemsPanel.Visibility = Visibility.Collapsed;
                resizeGrip.Visibility = Visibility.Collapsed;
                foreach (Border adorner in resizeAdorners) adorner.Visibility = Visibility.Collapsed;
                frame.Visibility = Visibility.Collapsed;
            }
            else
            {
                capsuleView.Visibility = Visibility.Collapsed;
                header.Visibility = Visibility.Visible;
                itemsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                resizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                foreach (Border adorner in resizeAdorners) adorner.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                frame.Visibility = Visibility.Visible;
            }
        }

        private static string CharacterIconPath(string name)
        {
            string fileName = null;
            switch (name)
            {
                case "chiikawa-idle": fileName = "chiikawa-idle.png"; break;
                case "chiikawa-wave": fileName = "chiikawa-wave.png"; break;
                case "chiikawa-search": fileName = "chiikawa-search.png"; break;
                case "chiikawa-read": fileName = "chiikawa-read.png"; break;
                case "chiikawa-carry-folder": fileName = "chiikawa-carry-folder.png"; break;
                case "chiikawa-organize": fileName = "chiikawa-organize.png"; break;
            }
            return fileName == null ? null : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "characters", fileName);
        }

        private static ImageSource DecodeCharacterImage(string name, int targetPx)
        {
            string path = CharacterIconPath(name);
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                BitmapImage image = new BitmapImage();
                using (FileStream stream = File.OpenRead(path))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = targetPx;   // 胶囊图标跟随界面缩放/DPI 的像素档位
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        private void UpdateMarkImage(ImageSource source)
        {
            markShowsImage = source != null;
            markImage.Source = source;
            markImage.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
            mark.Background = source == null
                ? new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B))
                : Brushes.Transparent;
        }

        // 渲染角色姿势或 data: 自定义图片；没有图片时回退到内置 Phosphor 字形（未知名回退 folder）
        private void UpdateCapsuleIcon()
        {
            ImageSource characterSource = DecodeCharacterImage(categoryIcon, IconTargetPx());
            if (characterSource != null)
            {
                capsuleIconSource = characterSource;
                capsuleImage.Source = characterSource;
                capsuleImage.Visibility = Visibility.Visible;
                iconShape.Visibility = Visibility.Collapsed;
                UpdateMarkImage(characterSource);
                return;
            }
            if (categoryIcon.StartsWith("data:", StringComparison.Ordinal))
            {
                ImageSource source = DecodeImage(categoryIcon, IconTargetPx());
                if (source != null)
                {
                    capsuleIconSource = source;
                    capsuleImage.Source = source;
                    capsuleImage.Visibility = Visibility.Visible;
                    iconShape.Visibility = Visibility.Collapsed;
                    UpdateMarkImage(source);
                    return;
                }
            }
            // Geometry.Parse 返回冻结的 StreamGeometry，需 Clone 后才能设 FillRule.Nonzero（同 CreatePhosphorIcon）
            string name = String.Equals(PhosphorGeometry(categoryIcon), FallbackGeometry, StringComparison.Ordinal) ? "folder" : categoryIcon;
            StreamGeometry iconGeometry = (StreamGeometry)Geometry.Parse(PhosphorGeometry(name));
            iconGeometry = iconGeometry.Clone();
            iconGeometry.FillRule = FillRule.Nonzero;
            iconShape.Data = iconGeometry;
            iconShape.Fill = CreateIconFill(accent);
            capsuleIconSource = null;
            iconShape.Visibility = Visibility.Visible;
            capsuleImage.Visibility = Visibility.Collapsed;
            UpdateMarkImage(null);
        }

        private void RebuildItems()
        {
            // WPF applies this visual-tree mutation as one render pass. Clearing
            // first guarantees grid and list controls can never coexist.
            itemsPanel.Children.Clear();
            List<Dictionary<string, object>> ordered = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in currentItems)
            {
                if (MatchesSearch(item)) ordered.Add(item);
            }
            ordered.Sort(CompareItems);
            double availableWidth = Math.Max(1, ActualWidth - 20);
            int maxColumns = ComputeItemColumns(availableWidth, ordered.Count);
            renderedColumns = maxColumns;
            double itemWidth = ComputeItemWidth(availableWidth, maxColumns);
            double groupWidth = ComputeItemGroupWidth(itemWidth, maxColumns);
            for (int start = 0; start < ordered.Count;)
            {
                int rowCount = viewMode == "list" ? 1 : Math.Min(maxColumns, ordered.Count - start);
                Grid row = new Grid
                {
                    Width = groupWidth,
                    HorizontalAlignment = GetItemRowAlignment(),
                    VerticalAlignment = VerticalAlignment.Top
                };
                for (int column = 0; column < maxColumns; column++)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(itemWidth + itemGap, GridUnitType.Pixel) });
                }
                int startColumn = GetItemRowStartColumn(maxColumns, rowCount);
                for (int column = 0; column < rowCount; column++)
                {
                    Button button = CreateItem(ordered[start + column], itemWidth);
                    Grid.SetColumn(button, startColumn + column);
                    row.Children.Add(button);
                }
                itemsPanel.Children.Add(row);
                start += rowCount;
            }
            count.Text = searchText.Length == 0 ? currentItems.Count.ToString() : ordered.Count.ToString() + "/" + currentItems.Count.ToString();
            UpdateItemWidths();
        }

        private bool MatchesSearch(Dictionary<string, object> item)
        {
            if (String.IsNullOrWhiteSpace(searchText)) return true;
            string query = searchText.Trim();
            string name = Convert.ToString(item["name"]);
            string path = Convert.ToString(item["path"]);
            string extension = item.ContainsKey("extension") ? Convert.ToString(item["extension"]) : "";
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || extension.IndexOf(query.TrimStart('.'), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ToggleSearchBar(bool? open = null)
        {
            bool next = open.HasValue ? open.Value : searchBar.Visibility != Visibility.Visible;
            searchBar.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
            if (!next) searchBox.Clear();
            if (next)
            {
                // 普通折叠态仍保留标题条，搜索按钮可以被点击；先请求展开，
                // 否则搜索行会被 30px 的折叠窗口裁掉。焦点延后一帧，等 Manager
                // 回传展开尺寸后再进入输入状态。
                if (collapsed) host.PostManagerEvent("panelCollapse", panelId);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                {
                    if (searchBar.Visibility != Visibility.Visible) return;
                    searchBox.Focus();
                    searchBox.SelectAll();
                }));
            }
            UpdateSearchVisuals();
            UpdateHeaderToolsVisibility(header != null && header.IsMouseOver);
        }

        private void UpdateSearchVisuals()
        {
            if (searchBox == null || searchBar == null || searchButton == null) return;
            // DeskBox 输入层：亮 #FBFCFD/#24000000 描边，暗 #222830/#52FFFFFF 描边
            searchBar.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(210, 34, 40, 48) : Color.FromArgb(240, 251, 252, 253));
            searchBar.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x52, 255, 255, 255) : Color.FromArgb(0x24, 0, 0, 0));
            searchBar.BorderThickness = new Thickness(1);
            searchBox.Background = Brushes.Transparent;
            searchBox.BorderBrush = Brushes.Transparent;
            searchBox.Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26));
            searchButton.Background = searchBar.Visibility == Visibility.Visible ? new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)38 : (byte)26, themeAccent.R, themeAccent.G, themeAccent.B)) : Brushes.Transparent;
        }

        private static string BuildItemsSignature(PanelSyncData value)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            signature.Append(value.itemSize).Append('|');
            signature.Append(value.iconSize).Append('|');
            signature.Append(value.itemGap).Append('|');
            signature.Append(value.labelSize).Append('|');
            signature.Append(value.uiScale).Append('|');
            signature.Append(value.itemAlignment).Append('|');
            signature.Append(value.itemColumns).Append('|');
            signature.Append(value.showLabels ? '1' : '0').Append('|');
            signature.Append(value.viewMode).Append('|');
            signature.Append(value.sortMode).Append('|');
            signature.Append(value.color).Append('|');
            signature.Append(value.showExtensions ? '1' : '0').Append('|');
            signature.Append(value.labelPosition).Append('|');
            if (value.items != null)
            {
                foreach (PanelItemData data in value.items)
                {
                    if (data == null || String.IsNullOrEmpty(data.id)) continue;
                    signature.Append('|').Append(data.id);
                    signature.Append(':').Append(data.name);
                    signature.Append(':').Append(data.modifiedAt);
                    signature.Append(':').Append(data.size);
                    signature.Append(':').Append(data.readOnly ? '1' : '0');
                    signature.Append(':').Append(data.favorite ? '1' : '0');
                    signature.Append(':').Append(data.pinned ? '1' : '0');
                    signature.Append(':').Append(data.iconUrl == null ? 0 : data.iconUrl.Length).Append(':').Append(HashText(data.iconUrl));
                }
            }
            return signature.ToString();
        }

        private static uint HashText(string value)
        {
            if (String.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return hash;
            }
        }

        private int CompareItems(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            string leftName = Convert.ToString(left["name"]);
            string rightName = Convert.ToString(right["name"]);
            if (sortMode == "modified")
            {
                string leftDate = Convert.ToString(left.ContainsKey("modifiedAt") ? left["modifiedAt"] : "");
                string rightDate = Convert.ToString(right.ContainsKey("modifiedAt") ? right["modifiedAt"] : "");
                int dateResult = String.CompareOrdinal(rightDate, leftDate);
                if (dateResult != 0) return dateResult;
            }
            if (sortMode == "size")
            {
                long leftSize = left.ContainsKey("size") ? Convert.ToInt64(left["size"]) : 0L;
                long rightSize = right.ContainsKey("size") ? Convert.ToInt64(right["size"]) : 0L;
                int sizeResult = rightSize.CompareTo(leftSize);
                if (sizeResult != 0) return sizeResult;
            }
            return String.Compare(leftName, rightName, StringComparison.CurrentCultureIgnoreCase);
        }

        private Button CreateItem(Dictionary<string, object> data, double itemWidth)
        {
            string path = Convert.ToString(data["path"]);
            bool readOnly = data.ContainsKey("readOnly") && Convert.ToBoolean(data["readOnly"]);
            bool list = viewMode == "list";
            double currentIconSize = list ? Math.Min(iconSize, 56) : iconSize;
            double iconSurfaceSize = currentIconSize + Dip(list ? 2 : 4);
            // 行高跟字号一起缩放，保证两行标签的 MaxHeight 不会把放大后的文字裁掉
            double labelLineHeight = Dip(list ? Math.Max(14, labelSize * 1.45) : Math.Max(12, labelSize * 1.35));
            bool labelRight = !list && labelPosition == "right";
            double itemHeight = list
                ? Math.Max(Dip(46), iconSurfaceSize + Dip(8))
                : labelRight ? Math.Max(iconSurfaceSize + Dip(12), labelLineHeight + Dip(12)) : iconSurfaceSize + (showLabels ? Dip(4) + labelLineHeight * 2 : 0) + Dip(12);
            bool selected = selectedPaths.Contains(path);
            VerticalAlignment contentVAlign = (list || labelRight) ? VerticalAlignment.Center : VerticalAlignment.Top;
            HorizontalAlignment contentAlignment = list
                ? HorizontalAlignment.Left
                : itemAlignment == "right" ? HorizontalAlignment.Right
                : itemAlignment == "center" ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
            Button button = new Button
            {
                Width = itemWidth,
                Height = itemHeight,
                Padding = list ? new Thickness(6, 4, 8, 4) : new Thickness(3, 6, 3, 5),
                Margin = new Thickness(itemGap / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = selected ? new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)46 : (byte)36, themeAccent.R, themeAccent.G, themeAccent.B)) : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = path,
                ToolTip = readOnly ? Convert.ToString(data["name"]) + "\n只读引用" : Convert.ToString(data["name"]),
                Focusable = true,
                HorizontalContentAlignment = contentAlignment,
                VerticalContentAlignment = contentVAlign,
                Template = FlatButtonTemplate()
            };
            StackPanel stack = new StackPanel { HorizontalAlignment = contentAlignment, VerticalAlignment = contentVAlign, Orientation = list || labelRight ? Orientation.Horizontal : Orientation.Vertical };
            string iconUrl = data.ContainsKey("iconUrl") ? Convert.ToString(data["iconUrl"]) : null;
            bool hasShellIcon = !String.IsNullOrWhiteSpace(iconUrl);
            Border iconSurface = new Border
            {
                Width = iconSurfaceSize,
                Height = iconSurfaceSize,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = contentAlignment,
                Margin = list ? new Thickness(0, 0, 9, 0) : labelRight ? new Thickness(0) : new Thickness(0)
            };
            ImageSource decoded = DecodeImage(iconUrl, IconTargetPx());
            if (decoded != null)
            {
                Image image = new Image { Width = currentIconSize, Height = currentIconSize, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                image.Source = decoded;
                iconSurface.Child = image;
            }
            else
            {
                string kind = data.ContainsKey("kind") ? Convert.ToString(data["kind"]) : "";
                string iconName = String.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "file-text";
                iconSurface.Child = CreatePhosphorIcon(iconName, currentIconSize * 0.8, true);
            }
            stack.Children.Add(iconSurface);
            string displayName = showExtensions ? Path.GetFileName(Convert.ToString(data["name"])) : Path.GetFileNameWithoutExtension(Convert.ToString(data["name"]));
            Color labelColor = darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
            double labelMaxWidth = list ? Math.Max(Dip(60), itemWidth - Dip(58)) : labelRight ? Math.Max(Dip(44), itemWidth - iconSurfaceSize - Dip(14)) : itemWidth - Dip(4);
            double labelMaxHeight = list || labelRight ? labelLineHeight * (labelRight ? 2 : 1) + 1 : labelLineHeight * 2 + 1;
            Thickness labelMargin = list ? new Thickness(0) : labelRight ? new Thickness(Dip(8), 0, 0, 0) : new Thickness(0, Dip(4), 0, 0);
            TextBlock label = new TextBlock
            {
                Text = displayName,
                FontSize = list ? Dip(Math.Max(10, labelSize + 1.5)) : Dip(labelSize),
                FontWeight = FontWeights.Normal,
                FontFamily = ItemLabelFont,
                TextAlignment = list ? TextAlignment.Left : itemAlignment == "right" ? TextAlignment.Right : itemAlignment == "center" ? TextAlignment.Center : TextAlignment.Left,
                VerticalAlignment = contentVAlign,
                HorizontalAlignment = contentAlignment,
                Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = list ? TextWrapping.NoWrap : TextWrapping.Wrap,
                MaxWidth = labelMaxWidth,
                LineHeight = labelLineHeight,
                MaxHeight = labelMaxHeight,
                Margin = labelMargin,
                Foreground = new SolidColorBrush(labelColor)
            };
            // 与标题同走 ClearType + Display：灰阶 + Display 在 150% DPI 的小字号上
            // 会被网格对齐吃掉过渡像素，输出接近无抗锯齿的点阵（实测发糊）；
            // ClearType 背景由玻璃渐变（默认不透明度下 alpha≈247）兜底，彩边可控。
            TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(label, TextRenderingMode.ClearType);
            stack.Children.Add(label);
            button.Content = stack;
            button.PreviewMouseDoubleClick += delegate { OpenPath(path); };
            button.MouseDoubleClick += delegate { OpenPath(path); };
            button.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter || args.Key == Key.Space)
                {
                    OpenPath(path);
                    args.Handled = true;
                }
            };
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2 && args.ChangedButton == MouseButton.Left)
                {
                    itemDragPath = null;
                    OpenPath(path);
                    args.Handled = true;
                    return;
                }
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    ToggleSelection(path);
                    args.Handled = true;
                    return;
                }
                itemDragOrigin = args.GetPosition(button);
                itemDragPath = readOnly ? null : path;
                itemDragReadOnly = readOnly;
            };
            button.PreviewMouseMove += OnItemMouseMove;
            // 吉伊卡哇萌系悬停/选中态：柔和珊瑚粉微光与圆角高亮卡片
            Color hoverFill = darkTheme ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x1E, themeAccent.R, themeAccent.G, themeAccent.B);
            Color hoverBorder = Color.FromArgb(darkTheme ? (byte)0x22 : (byte)0x2A, themeAccent.R, themeAccent.G, themeAccent.B);
            button.MouseEnter += delegate
            {
                if (!selectedPaths.Contains(path))
                {
                    button.Background = new SolidColorBrush(hoverFill);
                    button.BorderBrush = new SolidColorBrush(hoverBorder);
                }
            };
            button.MouseLeave += delegate
            {
                bool isSelected = selectedPaths.Contains(path);
                button.Background = isSelected ? new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)46 : (byte)36, themeAccent.R, themeAccent.G, themeAccent.B)) : Brushes.Transparent;
                button.BorderBrush = isSelected ? new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)80 : (byte)70, themeAccent.R, themeAccent.G, themeAccent.B)) : Brushes.Transparent;
            };
            button.ContextMenu = CreateItemMenu(path, readOnly);
            return button;
        }

        private void UpdateItemsSelectionVisuals()
        {
            Color selectedFill = Color.FromArgb(darkTheme ? (byte)46 : (byte)36, themeAccent.R, themeAccent.G, themeAccent.B);
            Color selectedBorder = Color.FromArgb(darkTheme ? (byte)80 : (byte)70, themeAccent.R, themeAccent.G, themeAccent.B);
            foreach (UIElement rowElement in itemsPanel.Children)
            {
                Grid row = rowElement as Grid;
                if (row == null) continue;
                foreach (UIElement child in row.Children)
                {
                    Button btn = child as Button;
                    if (btn == null) continue;
                    string p = btn.Tag as string;
                    if (p == null) continue;
                    bool isSelected = selectedPaths.Contains(p);
                    btn.Background = isSelected ? new SolidColorBrush(selectedFill) : Brushes.Transparent;
                    btn.BorderBrush = isSelected ? new SolidColorBrush(selectedBorder) : Brushes.Transparent;
                }
            }
        }

        private void ToggleSelection(string path)
        {
            if (selectedPaths.Contains(path)) selectedPaths.Remove(path); else selectedPaths.Add(path);
            UpdateItemsSelectionVisuals();
        }

        private int CountVisibleItems()
        {
            int visibleCount = 0;
            foreach (Dictionary<string, object> item in currentItems)
            {
                if (MatchesSearch(item)) visibleCount++;
            }
            return visibleCount;
        }

        // 项目单元以用户设置的 itemSize 为首选宽度，同时保证图标不会被单元裁切。
        // 宽窗口只增加列数，不把单个项目无限拉宽。
        private double ComputePreferredItemWidth()
        {
            if (viewMode == "list") return Math.Max(120, ActualWidth - 26);
            double iconSurfaceSize = iconSize + 4;
            double minimumWidth = iconSurfaceSize + 6;
            if (labelPosition == "right" && showLabels) minimumWidth = Math.Max(minimumWidth, iconSurfaceSize + 44 + 14);
            return Math.Max(44, Math.Max(itemSize, minimumWidth));
        }

        // 自动列数：按首选单元宽度计算当前窗口能容纳的列数，并限制为当前项目数。
        // 旧配置中的固定列数作为偏好上限使用：窄窗口会自动减列避免裁切；空间足够
        // 放下全部项目时不再强制多余换行，保证宽窗口第一行可以完整容纳项目。
        private int ComputeItemColumns(double availableWidth, int itemCount)
        {
            if (viewMode == "list") return 1;
            if (itemCount <= 0) return 1;
            double preferredWidth = ComputePreferredItemWidth();
            int capacity = Math.Max(1, (int)Math.Floor((availableWidth + itemGap) / (preferredWidth + itemGap)));
            int columns = Math.Min(itemCount, capacity);
            if (itemColumns > 0 && itemCount > capacity) columns = Math.Min(columns, itemColumns);
            return Math.Max(1, columns);
        }

        // 所有行共用同一个项目宽度。窗口过窄时才收缩到当前列数能容纳的宽度，
        // 因此最后一行不会因为项目较少而被重新拉伸。
        private double ComputeItemWidth(double availableWidth, int columns)
        {
            if (viewMode == "list") return Math.Max(120, availableWidth - itemGap);
            int safeColumns = Math.Max(1, columns);
            double maximumWidth = (availableWidth - itemGap * safeColumns) / safeColumns;
            return Math.Max(44, Math.Min(ComputePreferredItemWidth(), maximumWidth));
        }

        private double ComputeItemGroupWidth(double itemWidth, int columns)
        {
            return Math.Max(1, Math.Max(1, columns) * (itemWidth + itemGap));
        }

        private HorizontalAlignment GetItemRowAlignment()
        {
            // 网格外层始终居中：项目单元保持固定宽度，不会因为最后一行项目少
            // 而拉伸，同时左右留白保持对称。itemAlignment 只负责单元内部内容。
            return viewMode == "list" ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        }

        private int GetItemRowStartColumn(int columns, int rowCount)
        {
            return viewMode == "list" ? 0 : Math.Max(0, (columns - rowCount) / 2);
        }

        private void RefreshItemLayoutForSize()
        {
            double availableWidth = Math.Max(1, ActualWidth - Dip(20));
            int nextColumns = ComputeItemColumns(availableWidth, CountVisibleItems());
            if (nextColumns != renderedColumns)
            {
                RebuildItems();
                return;
            }
            UpdateItemWidths();
        }

        private void UpdateItemWidths()
        {
            bool list = viewMode == "list";
            itemsPanel.HorizontalAlignment = list ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            itemsPanel.VerticalAlignment = VerticalAlignment.Top;
            double availableWidth = Math.Max(1, ActualWidth - Dip(20));
            scroll.HorizontalContentAlignment = list ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            double panelWidth = list
                ? availableWidth
                : ComputeItemGroupWidth(ComputeItemWidth(availableWidth, Math.Max(1, renderedColumns)), Math.Max(1, renderedColumns));
            itemsPanel.Width = Math.Min(availableWidth, Math.Max(1, panelWidth));
            foreach (UIElement rowElement in itemsPanel.Children)
            {
                Grid row = rowElement as Grid;
                if (row == null) continue;
                int columns = Math.Max(1, row.ColumnDefinitions.Count > 0 ? row.ColumnDefinitions.Count : renderedColumns);
                double width = ComputeItemWidth(availableWidth, columns);
                row.Width = ComputeItemGroupWidth(width, columns);
                row.HorizontalAlignment = GetItemRowAlignment();
                for (int column = 0; column < row.ColumnDefinitions.Count; column++)
                {
                    row.ColumnDefinitions[column].Width = new GridLength(width + itemGap, GridUnitType.Pixel);
                }
                foreach (UIElement child in row.Children)
                {
                    Button button = child as Button;
                    if (button == null) continue;
                    button.Width = width;
                    StackPanel stack = button.Content as StackPanel;
                    if (stack == null || stack.Children.Count < 2) continue;
                    TextBlock label = stack.Children[1] as TextBlock;
                    if (label == null) continue;
                    bool labelRight = !list && labelPosition == "right";
                    double labelLineHeight = Dip(list ? Math.Max(14, labelSize * 1.45) : Math.Max(12, labelSize * 1.35));
                    double iconSurfaceSize = (list ? Math.Min(iconSize, 56) : iconSize) + Dip(list ? 2 : 4);
                    label.MaxWidth = list
                        ? Math.Max(Dip(60), width - Dip(58))
                        : labelRight
                            ? Math.Max(Dip(44), width - iconSurfaceSize - Dip(14))
                            : width - Dip(4);
                    label.MaxHeight = list || labelRight ? labelLineHeight * (labelRight ? 2 : 1) + 1 : labelLineHeight * 2 + 1;
                }
            }
        }

        private ContextMenu CreateItemMenu(string path, bool readOnly)
        {
            ContextMenu menu = StyledMenu();
            bool favorite = IsItemMarked(path, "favorite");
            bool itemPinned = IsItemMarked(path, "pinned");
            MenuItem open = StyledMenuItem("打开", "open");
            open.Click += delegate { RunAfterMenuClosed(menu, delegate { OpenPath(path); }); };
            menu.Items.Add(open);
            MenuItem favoriteItem = StyledMenuItem(favorite ? "取消收藏" : "加入收藏", "sparkle");
            favoriteItem.Click += delegate { host.PostManagerEvent("itemFavorite", new Dictionary<string, object> { { "path", path } }); };
            menu.Items.Add(favoriteItem);
            MenuItem pinnedItem = StyledMenuItem(itemPinned ? "取消固定项目" : "固定项目", "pin");
            pinnedItem.Click += delegate { host.PostManagerEvent("itemPin", new Dictionary<string, object> { { "path", path } }); };
            menu.Items.Add(pinnedItem);
            MenuItem select = StyledMenuItem(selectedPaths.Contains(path) ? "取消选择" : "加入批量选择", "check");
            select.Click += delegate { ToggleSelection(path); };
            menu.Items.Add(select);
            if (readOnly)
            {
                MenuItem reveal = StyledMenuItem("打开原位置", "folder");
                reveal.Click += delegate { RunAfterMenuClosed(menu, delegate { OpenContainingFolder(path); }); };
                menu.Items.Add(reveal);
                MenuItem restoreSelected = StyledMenuItem("放回桌面", "undo");
                restoreSelected.Click += delegate { host.PostManagerEvent("panelBatchRestore", new Dictionary<string, object> { { "paths", SelectedPaths(path) } }); };
                menu.Items.Add(restoreSelected);
            }
            else
            {
                MenuItem restore = StyledMenuItem("放回桌面", "undo");
                restore.Click += delegate { host.PostManagerEvent("panelBatchRestore", new Dictionary<string, object> { { "paths", SelectedPaths(path) } }); };
                menu.Items.Add(restore);
            }
            if (selectedPaths.Count > 0)
            {
                MenuItem batchMove = StyledMenuItem("批量收纳到本分区", "folder");
                batchMove.Click += delegate { host.PostManagerEvent("panelBatchMove", new Dictionary<string, object> { { "categoryId", panelId }, { "paths", SelectedPaths(path) } }); selectedPaths.Clear(); };
                menu.Items.Add(batchMove);
                MenuItem clear = StyledMenuItem("清除批量选择", "close");
                clear.Click += delegate { selectedPaths.Clear(); UpdateItemsSelectionVisuals(); };
                menu.Items.Add(clear);
            }
            MenuItem system = StyledMenuItem("Windows 原生菜单", "drive");
            system.Click += delegate
            {
                RunAfterMenuClosed(menu, delegate
                {
                    if (!LaunchShellMenu(path)) host.PostManagerEvent("operationError", "Windows 原生菜单启动失败");
                });
            };
            menu.Items.Add(StyledSeparator());
            menu.Items.Add(system);
            // DeskBox 在每次打开菜单时都从当前 widget/file 状态生成动作；这里保留
            // 菜单实例以减少视觉树分配，但在 Opened 时刷新会变化的文案，避免收藏、
            // 固定或批量选择后右键菜单仍显示旧状态。
            menu.Opened += delegate
            {
                UpdateMenuItemText(favoriteItem, IsItemMarked(path, "favorite") ? "取消收藏" : "加入收藏");
                UpdateMenuItemText(pinnedItem, IsItemMarked(path, "pinned") ? "取消固定项目" : "固定项目");
                UpdateMenuItemText(select, selectedPaths.Contains(path) ? "取消选择" : "加入批量选择");
            };
            return menu;
        }

        private static void UpdateMenuItemText(MenuItem item, string text)
        {
            if (item == null) return;
            TextBlock label = item.Tag as TextBlock;
            if (label != null) label.Text = text;
        }

        private bool IsItemMarked(string path, string key)
        {
            foreach (Dictionary<string, object> item in currentItems)
            {
                if (!String.Equals(Convert.ToString(item["path"]), path, StringComparison.OrdinalIgnoreCase)) continue;
                return item.ContainsKey(key) && Convert.ToBoolean(item[key]);
            }
            return false;
        }

        private string[] SelectedPaths(string fallback)
        {
            HashSet<string> selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
            if (!selected.Contains(fallback)) selected.Add(fallback);
            string[] result = new string[selected.Count];
            selected.CopyTo(result);
            return result;
        }

        private void OnItemMouseMove(object sender, MouseEventArgs args)
        {
            if (itemDragReadOnly || args.LeftButton != MouseButtonState.Pressed || String.IsNullOrWhiteSpace(itemDragPath)) return;
            Point point = args.GetPosition(sender as IInputElement);
            if (Math.Abs(point.X - itemDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - itemDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            string path = itemDragPath;
            itemDragPath = null;
            itemDragReadOnly = false;
            DataObject data = new DataObject(DataFormats.FileDrop, new string[] { path });
            // 允许复制/移动/链接由放置目标选择（原生 Explorer 语义：同卷默认移动、跨卷复制、
            // 右键放置目标自定）；拖放是阻塞调用，用 try/finally 保证交互深度必定配对释放
            // （DeskBox 经验：capture 被 alt-tab/UAC 抢走时也必须 End）。
            BeginLayerInteraction();
            try { DragDrop.DoDragDrop(sender as DependencyObject, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            finally { EndLayerInteraction(); }
            host.PostManagerEvent("panelRefresh", true);
        }

        private void OpenContainingFolder(string path)
        {
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(folder)) OpenPath(folder);
        }

        private void MoveBackToDesktop(string path)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string destination = NativeFileOps.GetAvailablePath(Path.Combine(desktop, Path.GetFileName(path)), null);
                // 走原生操作桥：同卷 rename，跨卷自动退回 Shell 静默移动
                NativeFileOps.MoveSingle(path, destination);
                host.PostManagerEvent("panelRefresh", true);
            }
            catch (Exception error)
            {
                host.PostManagerEvent("operationError", "放回桌面失败：" + error.Message);
            }
        }

        private void OnHeaderMouseDown(object sender, MouseButtonEventArgs args)
        {
            // 双击标题栏 = 折叠 / 展开（在移动守卫之前判定，pinned 不阻止折叠）
            if (args.ChangedButton == MouseButton.Left && args.ClickCount == 2 && args.Source as Button == null)
            {
                host.PostManagerEvent("panelCollapse", panelId);
                args.Handled = true;
                return;
            }
            // 移除 pinned 阻止移动的限制（固定只代表置顶与阻止自动隐藏，用户始终可以通过拖拽调整窗口位置）
            if (args.ChangedButton != MouseButton.Left || moving || resizing || args.Source as Button != null || args.ClickCount > 1) return;
            CursorPoint cursor;
            GetCursorPos(out cursor);
            // 拖拽接管位置：取消进行中的避让滑动（从当前位置继续，不回跳）
            moveAnimationTimer.Stop();
            moveAnimating = false;
            moving = true;
            moveStartScreen = new Point(cursor.X, cursor.Y);
            moveStartLeft = Left;
            moveStartTop = Top;
            movingPreviousCache = frame.CacheMode;
            frame.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = true, EnableClearType = true };
            itemsPanel.IsHitTestVisible = false;
            resizeGrip.Visibility = Visibility.Hidden;
            UpdateWindowRegion();
            Mouse.Capture(header, CaptureMode.Element);
            BeginLayerInteraction();
            args.Handled = true;
        }

        private void OnHeaderMouseMove(object sender, MouseEventArgs args)
        {
            if (!moving) return;
            if (args.LeftButton != MouseButtonState.Pressed)
            {
                FinishMove();
                return;
            }
            CursorPoint cursor;
            GetCursorPos(out cursor);
            double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
            double nextLeft = moveStartLeft + (cursor.X - moveStartScreen.X) / scale;
            double nextTop = moveStartTop + (cursor.Y - moveStartScreen.Y) / scale;

            Rect workArea = GetVirtualScreenBounds();
            double clampedLeft = Math.Max(workArea.Left - ActualWidth + 40, Math.Min(workArea.Right - 40, nextLeft));
            double clampedTop = Math.Max(workArea.Top, Math.Min(workArea.Bottom - ScaledHeaderBarHeight(), nextTop));

            List<Rect> obstacles = host.GetPanelBounds(panelId);
            Rect requested = new Rect(clampedLeft, clampedTop, ActualWidth, ActualHeight);
            Rect candidate = SnapMove(requested, obstacles, workArea);
            Left = candidate.Left;
            Top = candidate.Top;
            args.Handled = true;
        }

        private void OnHeaderMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (!moving || args.ChangedButton != MouseButton.Left) return;
            FinishMove();
            args.Handled = true;
        }

        private void FinishMove()
        {
            if (!moving) return;
            moving = false;
            EndLayerInteraction();
            if (Mouse.Captured == header) Mouse.Capture(null);
            itemsPanel.IsHitTestVisible = true;
            resizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            frame.CacheMode = movingPreviousCache;

            List<Rect> obstacles = host.GetPanelBounds(panelId);
            Rect current = new Rect(Left, Top, ActualWidth, ActualHeight);
            if (HasActualOverlap(current, obstacles))
            {
                Rect resolved = ResolveMove(Left, Top, ActualWidth, ActualHeight);
                Left = resolved.Left;
                Top = resolved.Top;
            }

            if (dpiRestorePending) { dpiRestorePending = false; RestoreSyncedBounds(); }
            PostGeometry();
        }

        private void BeginRename()
        {
            TextBox editor = new TextBox { Text = categoryName, Width = Math.Max(100, title.ActualWidth), Height = Dip(22), VerticalContentAlignment = VerticalAlignment.Center, FontSize = Dip(13) };
            Grid parent = title.Parent as Grid;
            if (parent == null) return;
            int column = Grid.GetColumn(title);
            parent.Children.Remove(title);
            Grid.SetColumn(editor, column);
            parent.Children.Add(editor);
            editor.Focus();
            editor.SelectAll();
            bool committed = false;
            Action commit = delegate
            {
                if (committed) return;
                committed = true;
                string requested = editor.Text.Trim();
                parent.Children.Remove(editor);
                Grid.SetColumn(title, column);
                parent.Children.Add(title);
                if (requested.Length > 0 && requested != categoryName)
                {
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["id"] = panelId;
                    payload["name"] = requested;
                    host.PostManagerEvent("panelRename", payload);
                }
            };
            editor.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Enter) { commit(); args.Handled = true; } };
            editor.LostKeyboardFocus += delegate { commit(); };
        }

        // 创建单个方向的缩放热区（透明 Border，命中测试用；折叠时隐藏）
        private void AddResizeAdorner(ResizeDirection direction, Grid root)
        {
            bool west = direction == ResizeDirection.West || direction == ResizeDirection.NorthWest || direction == ResizeDirection.SouthWest;
            bool east = direction == ResizeDirection.East || direction == ResizeDirection.NorthEast || direction == ResizeDirection.SouthEast;
            bool north = direction == ResizeDirection.North || direction == ResizeDirection.NorthWest || direction == ResizeDirection.NorthEast;
            bool south = direction == ResizeDirection.South || direction == ResizeDirection.SouthWest || direction == ResizeDirection.SouthEast;
            Cursor cursor;
            if (west && north) cursor = Cursors.SizeNWSE;
            else if (east && south) cursor = Cursors.SizeNWSE;
            else if (east && north) cursor = Cursors.SizeNESW;
            else if (west && south) cursor = Cursors.SizeNESW;
            else if (east || west) cursor = Cursors.SizeWE;
            else cursor = Cursors.SizeNS;

            bool isCorner = (west || east) && (north || south);
            Border adorner = new Border
            {
                Tag = direction,
                Background = Brushes.Transparent,
                Cursor = cursor,
                HorizontalAlignment = west ? HorizontalAlignment.Left : east ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                VerticalAlignment = north ? VerticalAlignment.Top : south ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Width = isCorner ? ResizeCornerSize : (east || west ? ResizeEdgeThickness : Double.NaN),
                Height = isCorner ? ResizeCornerSize : (north || south ? ResizeEdgeThickness : Double.NaN)
            };
            adorner.MouseLeftButtonDown += OnResizeMouseDown;
            adorner.MouseMove += OnResizeMouseMove;
            adorner.MouseLeftButtonUp += OnResizeMouseUp;
            adorner.LostMouseCapture += delegate { FinishResize(); };
            resizeAdorners.Add(adorner);
            root.Children.Add(adorner);
        }

        // 胶囊按下：记录起点（点击/拖动判定），捕获鼠标
        private void OnCapsuleMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (hwnd == IntPtr.Zero || moving || resizing) return;
            CursorPoint cursor;
            GetCursorPos(out cursor);
            // 胶囊手势接管位置：取消进行中的避让滑动（从当前位置继续，不回跳）
            moveAnimationTimer.Stop();
            moveAnimating = false;
            capsuleGestureActive = true;
            capsuleCapsuleMoved = false;
            capsuleDownScreen = new Point(cursor.X, cursor.Y);
            capsuleStartLeft = Left;
            capsuleStartTop = Top;
            Mouse.Capture(capsuleView, CaptureMode.Element);
            BeginLayerInteraction();
            args.Handled = true;
        }

        // 胶囊拖动：位移超过 5px 进入拖动（移动胶囊窗口位置），否则保持点击
        private void OnCapsuleMouseMove(object sender, MouseEventArgs args)
        {
            if (!capsuleGestureActive) return;
            if (args.LeftButton != MouseButtonState.Pressed)
            {
                FinishCapsuleGesture(capsuleCapsuleMoved);
                return;
            }
            CursorPoint cursor;
            GetCursorPos(out cursor);
            double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
            double dx = (cursor.X - capsuleDownScreen.X) / scale;
            double dy = (cursor.Y - capsuleDownScreen.Y) / scale;
            if (!capsuleCapsuleMoved)
            {
                if (Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;   // 尚未超过点击阈值
                capsuleCapsuleMoved = true;
                UpdateWindowRegion();
            }
            Rect workArea = GetVirtualScreenBounds();
            double nextLeft = capsuleStartLeft + dx;
            double nextTop = capsuleStartTop + dy;
            double clampedLeft = Math.Max(workArea.Left - ActualWidth + 20, Math.Min(workArea.Right - 20, nextLeft));
            double clampedTop = Math.Max(workArea.Top, Math.Min(workArea.Bottom - ScaledCapsuleSize(), nextTop));

            List<Rect> obstacles = host.GetPanelBounds(panelId);
            Rect requested = new Rect(clampedLeft, clampedTop, ActualWidth, ActualHeight);
            Rect candidate = SnapMove(requested, obstacles, workArea);
            Left = candidate.Left;
            Top = candidate.Top;
            args.Handled = true;
        }

        // 胶囊松手：拖动过 → 回传新位置；否则视为点击 → 展开分区
        private void OnCapsuleMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (!capsuleGestureActive || args.ChangedButton != MouseButton.Left) return;
            FinishCapsuleGesture(capsuleCapsuleMoved);
            args.Handled = true;
        }

        private void FinishCapsuleGesture(bool moved)
        {
            if (!capsuleGestureActive) return;
            capsuleGestureActive = false;
            EndLayerInteraction();
            if (Mouse.Captured == capsuleView) Mouse.Capture(null);
            if (moved)
            {
                List<Rect> obstacles = host.GetPanelBounds(panelId);
                Rect current = new Rect(Left, Top, ActualWidth, ActualHeight);
                if (HasActualOverlap(current, obstacles))
                {
                    Rect resolved = ResolveMove(Left, Top, ActualWidth, ActualHeight);
                    Left = resolved.Left;
                    Top = resolved.Top;
                }
                if (dpiRestorePending) { dpiRestorePending = false; RestoreSyncedBounds(); }
                PostGeometry();
            }
            else
            {
                // 点击（无拖动）：展开分区（复用现有折叠/展开事件流）
                host.PostManagerEvent("panelCollapse", panelId);
            }
        }

        private void OnResizeMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (hwnd == IntPtr.Zero || collapsed || moving) return;
            // 手动拖拽缩放时取消进行中的折叠/展开补间与避让滑动
            moveAnimationTimer.Stop();
            moveAnimating = false;
            layoutAnimationTimer.Stop();
            layoutAnimating = false;
            SetItemsCache(false);
            RefreshCapsuleVisibility();
            resizing = true;
            Border adorner = sender as Border;
            resizeDirection = adorner != null && adorner.Tag is ResizeDirection ? (ResizeDirection)adorner.Tag : ResizeDirection.SouthEast;
            CursorPoint cursor;
            GetCursorPos(out cursor);
            resizeStartScreen = new Point(cursor.X, cursor.Y);
            resizeStartLeft = Left;
            resizeStartTop = Top;
            resizeStartWidth = ActualWidth;
            resizeStartHeight = ActualHeight;
            UpdateWindowRegion();
            Mouse.Capture(adorner, CaptureMode.Element);
            BeginLayerInteraction();
            args.Handled = true;
        }

        private void OnResizeMouseMove(object sender, MouseEventArgs args)
        {
            if (!resizing || args.LeftButton != MouseButtonState.Pressed) return;
            CursorPoint cursor;
            GetCursorPos(out cursor);
            Point screen = new Point(cursor.X, cursor.Y);
            double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
            double dx = (screen.X - resizeStartScreen.X) / scale;
            double dy = (screen.Y - resizeStartScreen.Y) / scale;
            double left = resizeStartLeft;
            double top = resizeStartTop;
            double width = resizeStartWidth;
            double height = resizeStartHeight;
            switch (resizeDirection)
            {
                case ResizeDirection.East: width = resizeStartWidth + dx; break;
                case ResizeDirection.West: left = resizeStartLeft + dx; width = resizeStartWidth - dx; break;
                case ResizeDirection.South: height = resizeStartHeight + dy; break;
                case ResizeDirection.North: top = resizeStartTop + dy; height = resizeStartHeight - dy; break;
                case ResizeDirection.NorthEast: top = resizeStartTop + dy; height = resizeStartHeight - dy; width = resizeStartWidth + dx; break;
                case ResizeDirection.NorthWest: top = resizeStartTop + dy; height = resizeStartHeight - dy; left = resizeStartLeft + dx; width = resizeStartWidth - dx; break;
                case ResizeDirection.SouthWest: left = resizeStartLeft + dx; width = resizeStartWidth - dx; height = resizeStartHeight + dy; break;
                default: width = resizeStartWidth + dx; height = resizeStartHeight + dy; break;   // SouthEast 及兜底
            }
            Rect resolved = ResolveResize(left, top, width, height);
            Left = resolved.Left;
            Top = resolved.Top;
            Width = resolved.Width;
            Height = resolved.Height;
            expandedHeight = Height;
            args.Handled = true;
        }

        private void OnResizeMouseUp(object sender, MouseButtonEventArgs args)
        {
            FinishResize();
            args.Handled = true;
        }

        private void FinishResize()
        {
            if (!resizing) return;
            resizing = false;
            EndLayerInteraction();
            if (Mouse.Captured != null) Mouse.Capture(null);
            RefreshItemLayoutForSize();
            if (dpiRestorePending) { dpiRestorePending = false; RestoreSyncedBounds(); }
            PostGeometry();
        }

        private Rect ResolveMove(double left, double top, double width, double height)
        {
            Rect workArea = GetVirtualScreenBounds();
            Rect requested = ClampRect(new Rect(left, top, Math.Min(width, workArea.Width), Math.Min(height, workArea.Height)), workArea);
            List<Rect> obstacles = host.GetPanelBounds(panelId);
            Rect candidate = SnapMove(requested, obstacles, workArea);
            if (!HasCollision(candidate, obstacles)) return candidate;

            List<double> xCandidates = new List<double> { candidate.Left, workArea.Left, workArea.Right - candidate.Width };
            List<double> yCandidates = new List<double> { candidate.Top, workArea.Top, workArea.Bottom - candidate.Height };
            foreach (Rect obstacle in obstacles)
            {
                xCandidates.Add(obstacle.Left - PanelGap - candidate.Width);
                xCandidates.Add(obstacle.Right + PanelGap);
                yCandidates.Add(obstacle.Top - PanelGap - candidate.Height);
                yCandidates.Add(obstacle.Bottom + PanelGap);
            }

            Rect? best = null;
            double bestDistance = Double.MaxValue;
            foreach (double x in xCandidates)
            {
                foreach (double y in yCandidates)
                {
                    Rect option = ClampRect(new Rect(x, y, candidate.Width, candidate.Height), workArea);
                    if (HasCollision(option, obstacles)) continue;
                    double distance = (option.Left - requested.Left) * (option.Left - requested.Left) + (option.Top - requested.Top) * (option.Top - requested.Top);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = option;
                    }
                }
            }
            if (best.HasValue) return best.Value;
            Rect current = ClampRect(new Rect(Left, Top, candidate.Width, candidate.Height), workArea);
            return HasActualOverlap(current, obstacles) ? candidate : current;
        }

        private Rect ResolveResize(double left, double top, double width, double height)
        {
            Rect workArea = GetVirtualScreenBounds();
            double maxRight = workArea.Right - 10;
            double maxBottom = workArea.Bottom - 10;
            double minLeft = workArea.Left + 10;
            double minTop = workArea.Top + 10;
            // 尺寸上限随 left/top 变化（西/北拖动时 right/bottom 固定）
            double maxWidth = Math.Max(ResizeMinWidth, maxRight - left);
            double maxHeight = Math.Max(ResizeMinHeight, maxBottom - top);
            width = Math.Max(ResizeMinWidth, Math.Min(maxWidth, width));
            height = Math.Max(ResizeMinHeight, Math.Min(maxHeight, height));
            // 西/北向把 left/top 超界量折进尺寸，保持右/下边不动
            if (left < minLeft) { width = Math.Max(ResizeMinWidth, width - (minLeft - left)); left = minLeft; }
            if (top < minTop) { height = Math.Max(ResizeMinHeight, height - (minTop - top)); top = minTop; }
            Rect requested = new Rect(left, top, width, height);
            List<Rect> obstacles = host.GetPanelBounds(panelId);
            List<double> rightEdges = new List<double> { maxRight };
            List<double> bottomEdges = new List<double> { maxBottom };
            foreach (Rect obstacle in obstacles)
            {
                rightEdges.Add(obstacle.Left - PanelGap);
                rightEdges.Add(obstacle.Left);
                rightEdges.Add(obstacle.Right);
                bottomEdges.Add(obstacle.Top - PanelGap);
                bottomEdges.Add(obstacle.Top);
                bottomEdges.Add(obstacle.Bottom);
            }
            requested.Width = Math.Max(ResizeMinWidth, Math.Min(maxWidth, SnapEdge(requested.Right, rightEdges) - requested.Left));
            requested.Height = Math.Max(ResizeMinHeight, Math.Min(maxHeight, SnapEdge(requested.Bottom, bottomEdges) - requested.Top));
            if (!HasCollision(requested, obstacles)) return requested;

            List<double> widths = new List<double> { requested.Width };
            List<double> heights = new List<double> { requested.Height };
            foreach (Rect obstacle in obstacles)
            {
                double safeWidth = obstacle.Left - PanelGap - requested.Left;
                double safeHeight = obstacle.Top - PanelGap - requested.Top;
                if (safeWidth >= ResizeMinWidth) widths.Add(safeWidth);
                if (safeHeight >= ResizeMinHeight) heights.Add(safeHeight);
            }
            Rect? best = null;
            double bestDistance = Double.MaxValue;
            foreach (double nextWidth in widths)
            {
                foreach (double nextHeight in heights)
                {
                    Rect option = new Rect(requested.Left, requested.Top, Math.Max(ResizeMinWidth, Math.Min(maxWidth, nextWidth)), Math.Max(ResizeMinHeight, Math.Min(maxHeight, nextHeight)));
                    if (HasCollision(option, obstacles)) continue;
                    double distance = (option.Width - width) * (option.Width - width) + (option.Height - height) * (option.Height - height);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = option;
                    }
                }
            }
            if (best.HasValue) return best.Value;
            Rect current = new Rect(requested.Left, requested.Top, Math.Max(ResizeMinWidth, Math.Min(maxWidth, ActualWidth)), Math.Max(ResizeMinHeight, Math.Min(maxHeight, ActualHeight)));
            return HasActualOverlap(current, obstacles) ? requested : current;
        }

        private static Rect SnapMove(Rect requested, List<Rect> obstacles, Rect workArea)
        {
            List<double> xCandidates = new List<double> { workArea.Left, workArea.Right - requested.Width };
            List<double> yCandidates = new List<double> { workArea.Top, workArea.Bottom - requested.Height };
            foreach (Rect obstacle in obstacles)
            {
                if (RangesOverlap(requested.Top, requested.Bottom, obstacle.Top, obstacle.Bottom, SnapDistance))
                {
                    xCandidates.Add(obstacle.Left);
                    xCandidates.Add(obstacle.Right - requested.Width);
                    xCandidates.Add(obstacle.Left - PanelGap - requested.Width);
                    xCandidates.Add(obstacle.Right + PanelGap);
                }
                if (RangesOverlap(requested.Left, requested.Right, obstacle.Left, obstacle.Right, SnapDistance))
                {
                    yCandidates.Add(obstacle.Top);
                    yCandidates.Add(obstacle.Bottom - requested.Height);
                    yCandidates.Add(obstacle.Top - PanelGap - requested.Height);
                    yCandidates.Add(obstacle.Bottom + PanelGap);
                }
            }
            return ClampRect(new Rect(SnapValue(requested.Left, xCandidates), SnapValue(requested.Top, yCandidates), requested.Width, requested.Height), workArea);
        }

        private static double SnapEdge(double value, List<double> candidates)
        {
            double result = value;
            double nearest = SnapDistance + 1;
            foreach (double candidate in candidates)
            {
                double distance = Math.Abs(candidate - value);
                if (distance <= SnapDistance && distance < nearest)
                {
                    nearest = distance;
                    result = candidate;
                }
            }
            return result;
        }

        private static double SnapValue(double value, List<double> candidates)
        {
            return SnapEdge(value, candidates);
        }

        private static bool RangesOverlap(double startA, double endA, double startB, double endB, double tolerance)
        {
            return endA + tolerance > startB && endB + tolerance > startA;
        }

        private static bool HasCollision(Rect candidate, List<Rect> obstacles)
        {
            foreach (Rect obstacle in obstacles)
            {
                if (candidate.Left < obstacle.Right + PanelGap && candidate.Right > obstacle.Left - PanelGap && candidate.Top < obstacle.Bottom + PanelGap && candidate.Bottom > obstacle.Top - PanelGap) return true;
            }
            return false;
        }

        private static bool HasActualOverlap(Rect candidate, List<Rect> obstacles)
        {
            foreach (Rect obstacle in obstacles)
            {
                if (candidate.Left < obstacle.Right && candidate.Right > obstacle.Left && candidate.Top < obstacle.Bottom && candidate.Bottom > obstacle.Top) return true;
            }
            return false;
        }

        private static Rect ClampRect(Rect value, Rect workArea)
        {
            double width = Math.Min(value.Width, workArea.Width);
            double height = Math.Min(value.Height, workArea.Height);
            double left = Math.Max(workArea.Left, Math.Min(value.Left, workArea.Right - width));
            double top = Math.Max(workArea.Top, Math.Min(value.Top, workArea.Bottom - height));
            return new Rect(left, top, width, height);
        }

        // WPF 的 WorkArea 只代表主显示器；虚拟屏幕边界允许面板保留在左侧/右侧显示器。
        // 仍以主屏工作区作为配置坐标原点，兼容既有配置且不引入常驻显示器状态。
        private static Rect GetVirtualScreenBounds()
        {
            double width = SystemParameters.VirtualScreenWidth;
            double height = SystemParameters.VirtualScreenHeight;
            if (width <= 0 || height <= 0) return SystemParameters.WorkArea;
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, width, height);
        }

        // 拖拽悬停视觉反馈：FileDrop 悬停时用主题色加粗 frame/header 边框形成光晕，离开/放下后恢复
        private void UpdateDragGlow(bool active)
        {
            if (active == dragGlowActive) return;
            dragGlowActive = active;
            if (active)
            {
                Color glow = Color.FromArgb(150, themeAccent.R, themeAccent.G, themeAccent.B);
                frame.BorderBrush = new SolidColorBrush(glow);
                frame.BorderThickness = new Thickness(2);
                header.BorderBrush = new SolidColorBrush(glow);
                header.BorderThickness = new Thickness(2);
            }
            else
            {
                // 光晕关闭：frame 恢复玻璃渐变 + 亮边（不是 Transparent），header 恢复 hairline
                ApplyFrameChrome();
            }
        }

        // 拖放效果解析（DeskBox NativeDropEffectPolicy）：Ctrl=复制、右键拖放=松开时弹菜单、
        // 默认=移动；源只允许复制（跨卷拖入）时退化为复制。readOnly 分区一律拒绝。
        private DragDropEffects ResolveDropEffect(DragEventArgs args)
        {
            if (readOnlyPanel) return DragDropEffects.None;
            if (args.Data == null || !args.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
            DragDropEffects allowed = args.AllowedEffects;
            if ((args.KeyStates & DragDropKeyStates.ControlKey) != 0 && (allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
            if ((allowed & DragDropEffects.Move) != 0) return DragDropEffects.Move;
            if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
            return DragDropEffects.None;
        }

        // 拖文件进分区：解析用户意图后上报 Manager（契约：effect=move|copy，移动/复制进分类）
        private void OnPanelDrop(object sender, DragEventArgs args)
        {
            UpdateDragGlow(false);
            if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = args.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (readOnlyPanel)
            {
                host.PostManagerEvent("operationError", "门户分区只引用文件，不会移动文件");
                args.Handled = true;
                return;
            }
            if ((args.KeyStates & DragDropKeyStates.RightMouseButton) != 0)
            {
                // 右键拖放（原生 Explorer 语义）：在松开位置弹移动/复制选择菜单（沿用面板菜单主题）
                ShowDropChoiceMenu(files);
            }
            else
            {
                PostPanelDrop(files, (args.KeyStates & DragDropKeyStates.ControlKey) != 0 ? "copy" : "move");
            }
            args.Handled = true;
        }

        private void PostPanelDrop(string[] files, string effect)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["categoryId"] = panelId;
            payload["paths"] = files;
            payload["effect"] = effect;
            host.PostManagerEvent("panelDrop", payload);
        }

        private void ShowDropChoiceMenu(string[] files)
        {
            ContextMenu menu = StyledMenu();
            MenuItem moveItem = StyledMenuItem("移动到「" + categoryName + "」", "folder");
            MenuItem copyItem = StyledMenuItem("复制到「" + categoryName + "」", "images");
            moveItem.Click += delegate { PostPanelDrop(files, "move"); };
            copyItem.Click += delegate { PostPanelDrop(files, "copy"); };
            menu.Items.Add(moveItem);
            menu.Items.Add(copyItem);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void ApplyTheme()
        {
            // DeskBox Widget 令牌（App.xaml ThemeDictionaries）：
            // 文本 #1A1A1A/#F5F5F5，次级 #5A5A5A/#A5A5A5，分隔线 #D0D0D0/#3C3C3C
            Color text1 = darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
            Color text2 = darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90);
            Color text3 = darkTheme ? Color.FromRgb(140, 140, 140) : Color.FromRgb(122, 122, 122);

            ApplyFrameChrome();
            header.CornerRadius = collapsed ? new CornerRadius(PanelRadius) : new CornerRadius(PanelRadius, PanelRadius, 0, 0);
            header.Background = Brushes.Transparent;
            // 胶囊视觉（中性表面/描边）统一在 ApplyCapsuleVisual 应用；字形填充保持分类色
            ApplyCapsuleVisual(capsuleHovered);
            iconShape.Fill = CreateIconFill(accent);
            mark.Background = markShowsImage
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(30, accent.R, accent.G, accent.B));
            mark.BorderBrush = Brushes.Transparent;
            title.Foreground = new SolidColorBrush(text1);
            if (countBadge != null) countBadge.Background = new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)32 : (byte)22, themeAccent.R, themeAccent.G, themeAccent.B));
            count.Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(255, 180, 185) : themeAccent);
            pinButton.Foreground = new SolidColorBrush(pinned ? themeAccent : text2);
            collapseButton.Foreground = new SolidColorBrush(text2);
            viewButton.Foreground = new SolidColorBrush(text2);
            UpdateHeaderToolsVisibility(header != null && header.IsMouseOver);
            // DeskBox 无彩色缩放把手：中性 hairline 提示可缩放
            resizeGrip.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(52, 0, 0, 0));
            resizeGrip.Background = Brushes.Transparent;
            breathOverlay.BorderBrush = new SolidColorBrush(themeAccent);
        }

        // 面板 chrome（DeskBox Widget 外观）：中性表面 #F3F3F3/#1F1F1F 平铺，
        // 1px 语义描边（亮 #16000000 / 暗 #20FFFFFF），标题条底部 1px 分隔线。
        // 旧的暖纸渐变/白色亮边是"液态玻璃"时代的样式，与 DeskBox 的
        // 中性 Fluent 卡片语言不一致，已移除。
        private void ApplyFrameChrome()
        {
            // 外层 frame 与标题条/胶囊共享同一套圆角几何。展开与收起补间期间
            // 保持胶囊圆角，避免从 56px 胶囊切回面板时出现一帧方角或错位。
            double cornerRadius = capsuleMode && (collapsed || layoutAnimating)
                ? CapsuleRadius
                : collapsed ? PanelRadius : PanelRadius;
            frame.CornerRadius = new CornerRadius(cornerRadius);
            breathOverlay.CornerRadius = new CornerRadius(cornerRadius);
            // 背景透明度分两种材质态（对齐 DeskBox 的材质/不透明度分层思路）：
            // 亚克力态：DWM 把壁纸模糊后垫在窗口下，WPF 只保留半透明中性着色层
            //   ——0→20（近纯模糊），88→196，100→220；纯色态维持原映射（40→215 拐点）。
            bool acrylic = materialMode != "solid" && OsSupportsAcrylic();
            double mappedAlpha;
            if (acrylic)
            {
                mappedAlpha = 20 + glassOpacity * 2.0;
            }
            else
            {
                // 吉伊卡哇通透奶油质感：不再暴力锁死在 215-255 实心纸片，
                // 而是让半透明奶油纸面（#FFFDF7）自然透出桌面壁纸光影（88 对应约 198 alpha，恰到好处的温润通透感）
                mappedAlpha = Math.Max(90, Math.Min(250, glassOpacity * 2.25));
            }
            byte glassAlpha = (byte)Math.Max(0, Math.Min(255, Math.Round(mappedAlpha)));
            Color paper = paperSurface;
            if (paper.R == 0 && paper.G == 0 && paper.B == 0)
                paper = darkTheme ? Color.FromRgb(31, 31, 31) : Color.FromRgb(255, 255, 255);
            frame.Background = new SolidColorBrush(Color.FromArgb(glassAlpha, paper.R, paper.G, paper.B));
            frame.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x28, 255, 255, 255) : Color.FromArgb(0x28, themeAccent.R, themeAccent.G, themeAccent.B));
            frame.BorderThickness = new Thickness(1);
            header.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x18, themeAccent.R, themeAccent.G, themeAccent.B));
            header.BorderThickness = new Thickness(0, 0, 0, 1);
            ApplyAccentBackdrop(acrylic);
        }

        // 真亚克力材质（移植 DeskBox 的系统材质思路：材质由系统合成器绘制，而不是窗口内模拟）。
        // 通过 SetWindowCompositionAttribute 挂 ACCENT_ENABLE_ACRYLICBLURBEHIND，让 DWM 把
        // 壁纸模糊后垫在窗口后面；WPF 半透明纸色渐变充当着色层。build < 17134 退化为
        // ACCENT_ENABLE_BLURBEHIND，调用失败再退回纯色渐变（原始表现），保证任何系统可用。
        private void ApplyAccentBackdrop(bool acrylic)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!acrylic)
            {
                if (acrylicApplied) { acrylicApplied = false; SetAccentPolicy(AccentDisabled, 0); }
                return;
            }
            // accent 着色保持极淡的雾度，主要着色交给 WPF 渐变层（避免两层叠加发闷）
            uint policyState = OsBuildNumber() >= MinAcrylicBuild ? AccentEnableAcrylicBlurBehind : AccentEnableBlurBehind;
            uint fogAlpha = darkTheme ? 0x26u : 0x30u;
            uint fogColor = darkTheme ? 0x141414u : 0xffffffu;
            SetAccentPolicy(policyState, (fogAlpha << 24) | fogColor);
            acrylicApplied = true;
        }

        private void SetAccentPolicy(uint state, uint gradientColor)
        {
            AccentPolicy policy = new AccentPolicy();
            policy.AccentState = state;
            policy.AccentFlags = 2;
            policy.GradientColor = gradientColor;
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AccentPolicy)));
            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                data.Attribute = WcaAccentPolicy;
                data.SizeOfData = Marshal.SizeOf(typeof(AccentPolicy));
                data.Data = buffer;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch { }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool OsSupportsAcrylic()
        {
            int build = OsBuildNumber();
            return build >= MinAcrylicBuild && build <= MaxAcrylicBuild;
        }

        private static int OsBuildNumber()
        {
            if (osBuild < 0)
            {
                try { osBuild = Environment.OSVersion.Version.Build; }
                catch { osBuild = 0; }
            }
            return osBuild;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public uint AccentState;
            public uint AccentFlags;
            public uint GradientColor;
            public uint AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

        // 胶囊视觉：分类色渐变背景（headerColor 派生，深浅主题一致）+ 1px 分类色描边
        // （44%，hover 加亮到 75%）+ 内高光 + hover 缩放；hover 由 MouseEnter/MouseLeave 驱动，
        // 主题刷新时按当前 hover 态恢复。
        private void ApplyCapsuleVisual(bool hover)
        {
            // DeskBox 中性胶囊：不透明中性表面（亮 #F3F3F3 系 / 暗 #1F1F1F 系）+
            // 语义描边，hover 仅提亮表面；分类色只保留在字形/缩略图本身。
            byte alpha = (byte)(hover ? 255 : 246);
            Color surface = darkTheme
                ? Color.FromRgb(hover ? (byte)42 : (byte)31, hover ? (byte)42 : (byte)31, hover ? (byte)42 : (byte)31)
                : Color.FromRgb(hover ? (byte)252 : (byte)243, hover ? (byte)252 : (byte)243, hover ? (byte)252 : (byte)243);
            capsuleView.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
            capsuleView.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x28, 255, 255, 255) : Color.FromArgb(0x1E, 0, 0, 0));
            capsuleView.BorderThickness = new Thickness(1);
            capsuleHighlight.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
            // 胶囊不启用任何投影（含 hover）：收起状态下悬浮在桌面的玻璃片，
            // hover 反馈仅用背景提亮与描边加亮表达，避免鼠标停留时出现"错误阴影"。
            // hover 缩放：内容层 180ms ease-out 放大到 1.12（中心 13,13），纯视觉不影响窗口尺寸/region
            ScaleTransform capsuleScale = capsuleInner.RenderTransform as ScaleTransform;
            if (capsuleScale != null)
            {
                double target = hover ? 1.12 : 1.0;
                DoubleAnimation scaleX = new DoubleAnimation { To = target, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                DoubleAnimation scaleY = new DoubleAnimation { To = target, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                capsuleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                capsuleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            }
        }

        private static Color Blend(Color background, Color foreground, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Round(background.R * (1 - amount) + foreground.R * amount),
                (byte)Math.Round(background.G * (1 - amount) + foreground.G * amount),
                (byte)Math.Round(background.B * (1 - amount) + foreground.B * amount));
        }

        // 图标填充：对角渐变 + 极淡投影，小尺寸下更有层次（只作用于字形，不作用于胶囊本体）
        private static LinearGradientBrush CreateIconFill(Color accent)
        {
            LinearGradientBrush brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            Color light = Blend(accent, Colors.White, 0.30);
            Color dark = Blend(accent, Colors.Black, 0.32);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(240, light.R, light.G, light.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(240, accent.R, accent.G, accent.B), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(240, dark.R, dark.G, dark.B), 1.0));
            return brush;
        }
        /// DPI 变化后把窗口恢复到最近一次同步的 DIP 尺寸/位置（UpdateBounds 会按当前工作区重新钳制）。
        /// WPF 在 PerMonitorV2 下按物理尺寸等比缩放窗口，DIP 尺寸会被改变；不恢复的话，
        /// 用户后续拖动/缩放会把被缩放过的错误尺寸写回配置（面板越变越宽直至全屏）。
        private void RestoreSyncedBounds()
        {
            if (hwnd == IntPtr.Zero || !hasSyncedBounds) return;
            RefreshDpiScale();   // 跨屏 DPI 变化：刷新图标解码档位（下一次投递/重建时生效）
            try { UpdateBounds(syncedX, syncedY, syncedWidth, syncedHeight, pinned); }
            catch { }
        }

        private IntPtr WndProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 尺寸变化（补间/缩放手势/DPI）时同步圆角 Region，轮廓始终跟随内容形状
            if (message == WmSize)
            {
                UpdateWindowRegion();
            }

            // 显示器切换 / DPI 变化：不拦截（让 WPF 正常处理），但安排恢复到配置的 DIP 尺寸。
            // 拖拽/缩放手势中不立即恢复（避免拖拽被抢），置标记由手势结束时统一恢复。
            if (message == WmDpiChanged)
            {
                if (moving || resizing || capsuleGestureActive) dpiRestorePending = true;
                else Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RestoreSyncedBounds));
                return IntPtr.Zero;
            }

            // 前台感知激活抑制（DeskBox 指针激活策略 + WidgetLayer 前台分类）：
            // 其他应用在前台时，点击分区不抢占焦点、不把分区抬到应用之上（保持桌面层行为）；
            // 前台是桌面壳/任务栏/本应用其他窗口，点击落在搜索框（需要键盘焦点），
            // 或唤起会话中（分区浮于普通窗口之上）时正常激活。
            if (message == WmMouseActivate && ShouldSuppressActivation())
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            // 收纳条属于桌面层，不能被“最小化所有窗口”(Win+D/Win+M) 收走。
            if (message == WmSysCommand && (wParam.ToInt32() & 0xFFF0) == ScMinimize)
            {
                handled = true;
                return IntPtr.Zero;
            }
            if (message != WmNcHitTest || IsInteractivePoint(lParam)) return IntPtr.Zero;
            handled = true;
            return new IntPtr(HtTransparent);
        }

        // 是否抑制本次点击的窗口激活（对齐 DeskBox 的 RelativeLayerRestore/PointerActivation 策略）
        // 圆角窗口 Region：把 OS 层面的窗口轮廓裁成与内容一致的形状（展开面板/标题条/胶囊）。
        // 注意 layered 窗口的 Region 不参与渲染合成，也裁不掉 DWM accent 背板
        // （Win11 的 SWCA 亚克力按整个矩形绘制且无视 Region，因此停用，见 MaxAcrylicBuild）；
        // 保留它是为了让系统轮廓（命中测试边界、窗口 Outline）与视觉圆角一致。
        // Region 随 WM_SIZE 持续同步，移动时随窗口自动跟随。
        private void UpdateWindowRegion()
        {
            if (hwnd == IntPtr.Zero) return;
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;
            double radius = capsuleMode && (collapsed || layoutAnimating)
                ? CapsuleRadius
                : PanelRadius;
            double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
            int physicalRadius = Math.Max(1, (int)Math.Round(radius * scale));
            // CreateRoundRectRgn 末两参是圆角椭圆的宽高（直径=2×半径），传半径会让
            // 裁剪弧线只有 WPF 圆角的一半，accent 白背板从两条弧线之间的月牙区漏出。
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, physicalRadius * 2, physicalRadius * 2);
            if (region == IntPtr.Zero) return;
            SetWindowRgn(hwnd, region, false);   // 分层窗口由合成器刷新，false 避免补间中重绘风暴
        }

        private bool ClickNeedsActivation()
        {
            CursorPoint cursor;
            if (!GetCursorPos(out cursor)) return true;
            try
            {
                Point local = PointFromScreen(new Point(cursor.X, cursor.Y));
                DependencyObject current = InputHitTest(local) as DependencyObject;
                while (current != null)
                {
                    if (current is TextBox || current is Button || current is MenuItem) return true;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private bool ShouldSuppressActivation()
        {
            if (LayerSession.IsRaised) return false;   // 唤起会话中分区浮于普通窗口之上：正常激活
            if (pinned) return false;                  // 用户显式置顶的分区允许激活抬升
            if (ClickNeedsActivation()) return false;  // 点击按钮、输入框、文件项或菜单时立即放行激活与输入，0 延迟响应
            return WidgetLayer.ShouldSuppressPointerActivation();
        }

        // =============================================================
        // 交互深度（DeskBox WidgetSessionManager 语义）：拖动/缩放/胶囊手势/菜单/文本输入
        // 期间 Begin，结束时 End。唤起会话中深度 > 0 会阻止恢复监视器回落（防误触）；
        // 计数同时联动 Manager.interactionActive（暂停自动收纳）。看门狗由 LayerSession 兜底。
        // =============================================================
        private int layerInteractionDepth;

        private void BeginLayerInteraction()
        {
            layerInteractionDepth++;
            if (hwnd != IntPtr.Zero) LayerSession.BeginInteraction(hwnd);
        }

        private void EndLayerInteraction()
        {
            if (layerInteractionDepth <= 0) return;
            layerInteractionDepth--;
            if (hwnd != IntPtr.Zero) LayerSession.EndInteraction(hwnd);
        }

        // 菜单 opened/closed 成对挂接（Opened 必然触发，保证配对）
        private void TrackMenuInteraction(ContextMenu menu)
        {
            if (menu == null) return;
            menu.Opened += delegate { BeginLayerInteraction(); AnimateMenuOpen(menu); };
            menu.Closed += delegate { EndLayerInteraction(); ResetMenuVisual(menu); };
        }

        // WPF ContextMenu 的默认 Popup 没有 WinUI MenuFlyout 的轻量入场过渡。
        // 只动 opacity/scale，不参与布局，避免菜单出现时推动面板或造成尺寸抖动；
        // 同时跟随系统“菜单动画”开关，给减少动效用户一个可预期的静态状态。
        private static void AnimateMenuOpen(ContextMenu menu)
        {
            if (menu == null) return;
            ScaleTransform scale = menu.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1, 1);
                menu.RenderTransform = scale;
            }
            menu.RenderTransformOrigin = new Point(.5, .5);
            menu.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (!SystemParameters.MenuAnimation)
            {
                menu.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                return;
            }

            menu.Opacity = 0;
            scale.ScaleX = .985;
            scale.ScaleY = .985;
            CubicEase easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration duration = new Duration(TimeSpan.FromMilliseconds(160));
            menu.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.985, 1, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.985, 1, duration) { EasingFunction = easing });
        }

        private static void ResetMenuVisual(ContextMenu menu)
        {
            if (menu == null) return;
            menu.BeginAnimation(UIElement.OpacityProperty, null);
            ScaleTransform scale = menu.RenderTransform as ScaleTransform;
            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
            menu.Opacity = 1;
        }

        // 菜单动作如果会打开外部窗口/编辑器，先让 ContextMenu 完整收起，再把动作
        // 放回 WPF 队列；这对应 DeskBox 在 Flyout.Closed 后再启动后续 transient UI。
        private void RunAfterMenuClosed(ContextMenu menu, Action action)
        {
            if (action == null) return;
            if (menu == null || !menu.IsOpen)
            {
                Dispatcher.BeginInvoke(action);
                return;
            }
            RoutedEventHandler closed = null;
            closed = delegate(object sender, RoutedEventArgs args)
            {
                menu.Closed -= closed;
                Dispatcher.BeginInvoke(action);
            };
            menu.Closed += closed;
            menu.IsOpen = false;
        }

        // 点击目标是否需要键盘焦点（搜索框等文本输入）：抑制激活会导致输入框拿不到光标，
        // 因此命中文本控件时放弃抑制，正常激活窗口。
        private bool ClickNeedsKeyboardFocus()
        {
            CursorPoint cursor;
            if (!GetCursorPos(out cursor)) return false;
            try
            {
                Point local = PointFromScreen(new Point(cursor.X, cursor.Y));
                DependencyObject current = InputHitTest(local) as DependencyObject;
                while (current != null)
                {
                    if (current is TextBox) return true;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
                return true;   // 命中测试失败时宁可正常激活，避免输入功能失效
            }
            return false;
        }

        private void OnStateChanged(object sender, EventArgs args)
        {
            // 兜底：即使有其它路径直接调用了 ShowWindow(SW_MINIMIZE)，
            // 也立即把分区恢复为正常状态，避免收纳条从桌面消失。
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }

        private bool IsInteractivePoint(IntPtr lParam)
        {
            long packed = lParam.ToInt64();
            double screenX = unchecked((short)(packed & 0xffff));
            double screenY = unchecked((short)((packed >> 16) & 0xffff));
            Point localPoint = PointFromScreen(new Point(screenX, screenY));
            // 四角透明弧区（圆角外）保持穿透：取代旧 SetWindowRgn 的点击裁剪。
            // 展开态只处理顶角（底部弧区旧行为即在窗口内）；任何折叠态四角全部穿透。
            double radius = collapsed ? (capsuleMode ? CapsuleRadius : PanelRadius) : PanelRadius;
            if (localPoint.X < radius && localPoint.Y < radius &&
                DistSq(localPoint.X, localPoint.Y, radius, radius) > radius * radius) return false;
            if (localPoint.X > ActualWidth - radius && localPoint.Y < radius &&
                DistSq(localPoint.X, localPoint.Y, ActualWidth - radius, radius) > radius * radius) return false;
            if (collapsed)
            {
                if (localPoint.X < radius && localPoint.Y > ActualHeight - radius &&
                    DistSq(localPoint.X, localPoint.Y, radius, ActualHeight - radius) > radius * radius) return false;
                if (localPoint.X > ActualWidth - radius && localPoint.Y > ActualHeight - radius &&
                    DistSq(localPoint.X, localPoint.Y, ActualWidth - radius, ActualHeight - radius) > radius * radius) return false;
            }

            // Do not rely on the visual hit-test ancestry for wheel input. The
            // transparent margins between item buttons can resolve to different
            // template visuals, while the whole viewport is still scrollable.
            if (!collapsed && scroll.IsVisible)
            {
                Point viewportOrigin = scroll.TranslatePoint(new Point(0, 0), this);
                Rect viewportBounds = new Rect(viewportOrigin, new Size(scroll.ActualWidth, scroll.ActualHeight));
                if (viewportBounds.Contains(localPoint)) return true;
            }

            DependencyObject current = InputHitTest(localPoint) as DependencyObject;
            while (current != null)
            {
                if (current == header || current == resizeGrip || current == capsuleView || current == searchBar || current is Button || current is TextBox) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static double DistSq(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        public void SetClickThrough(bool enabled)
        {
            if (hwnd == IntPtr.Zero) return;
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            if (enabled) style |= WsExTransparent; else style &= ~WsExTransparent;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        }

        internal void AutoHideTick(int screenX, int screenY)
        {
            // LayerSession 有泄漏看门狗：若系统在菜单交互期间抢走窗口，静态本地计数
            // 可能晚于全局计数复位；两者同时成立才阻止自动隐藏，避免永久卡在“保持显示”。
            if (!autoHide || pinned || collapsed || moving || resizing || (layerInteractionDepth > 0 && LayerSession.IsInteractionActive) || searchBox.IsKeyboardFocusWithin || ContextMenu != null && ContextMenu.IsOpen)
            {
                if (autoHidden) { autoHidden = false; Show(); }
                mouseLeftAt = DateTime.UtcNow;
                return;
            }
            if (autoHidden)
            {
                double width = ActualWidth > 0 ? ActualWidth : Width;
                double height = ActualHeight > 0 ? ActualHeight : Height;
                if (screenX >= Left - 24 && screenX <= Left + width + 24 && screenY >= Top - 24 && screenY <= Top + height + 24)
                {
                    Show();
                    autoHidden = false;
                    mouseLeftAt = DateTime.UtcNow;
                }
                return;
            }
            if (IsMouseOver || IsKeyboardFocusWithin) { mouseLeftAt = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - mouseLeftAt).TotalSeconds >= autoHideDelaySeconds)
            {
                Hide();
                autoHidden = true;
            }
        }

        private void PostGeometry()
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            value["id"] = panelId;
            value["x"] = Math.Round(Left - SystemParameters.WorkArea.Left);
            value["y"] = Math.Round(Top - SystemParameters.WorkArea.Top);
            double persistedWidth = !Double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
            double persistedHeight = !Double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
            value["width"] = Math.Round(collapsed && capsuleMode ? expandedWidth : persistedWidth);
            value["height"] = Math.Round(collapsed ? expandedHeight : persistedHeight);
            host.PostManagerEvent("panelGeometry", value);
        }

        private void OpenPath(string path)
        {
            host.PostManagerEvent("itemOpened", path);
            // DeskBox 式原生启动：Explorer 进程内 ShellExecute → 本进程回退 → 无关联弹“打开方式”
            try
            {
                DesktopWindow.Log("面板项目触发启动打开: " + path);
                ShellLauncher.Open(path);
            }
            catch (Exception ex)
            {
                DesktopWindow.Log("面板项目启动失败 (" + path + "): " + ex.Message);
            }
        }

        // 启动系统原生右键菜单 helper（ShellMenu.exe，与主程序同目录）；成功返回 true，失败返回 false（调用方回退自定义菜单）
        private static bool LaunchShellMenu(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                System.Diagnostics.Process.Start(Path.Combine(directory, "ShellMenu.exe"), "\"" + path + "\"");
                return true;
            }
            catch { return false; }
        }

        // 目标重名解析统一走 NativeFileOps.GetAvailablePath（支持批量内 reserved 集合）

        // 估算图像缓存字节：冻结位图按 BGRA32 每像素 4 字节计，仅用于内存预算，无需精确到压缩格式
        private static long EstimateImageBytes(ImageSource source)
        {
            BitmapSource bitmap = source as BitmapSource;
            if (bitmap == null) return 0L;
            return (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        }

        private static ImageSource DecodeImage(string value, int targetPx)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(value)) return null;
                // 缓存键包含像素档位：DPI/界面缩放变化后按新档位重新解码，旧档位由 LRU 自然淘汰
                string cacheKey = targetPx + "|" + value;
                lock (decodedImageCacheLock)
                {
                    ImageSource cached;
                    if (decodedImageCache.TryGetValue(cacheKey, out cached)) return cached;
                }
                int comma = value.IndexOf(',');
                byte[] bytes = Convert.FromBase64String(comma >= 0 ? value.Substring(comma + 1) : value);
                BitmapImage image = new BitmapImage();
                using (MemoryStream stream = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = targetPx; // 按调用方算出的物理像素档位解码（图标显示尺寸 × 界面缩放 × DPI），避免大图全分辨率解码爆内存
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                }
                long entryBytes = (long)image.PixelWidth * image.PixelHeight * 4;
                lock (decodedImageCacheLock)
                {
                    ImageSource replaced;
                    if (decodedImageCache.TryGetValue(cacheKey, out replaced))
                    {
                        // 同 key 覆盖：先扣旧条目字节并从字典移除（队列中旧位置自然失效，逐出时跳过）
                        decodedImageCacheBytes -= EstimateImageBytes(replaced);
                        decodedImageCache.Remove(cacheKey);
                    }
                    decodedImageCache[cacheKey] = image;
                    decodedImageCacheBytes += entryBytes;
                    decodedImageCacheOrder.Enqueue(cacheKey);
                    // 逐出：仅当超出 32MB 上限时启动，一次删到 24MB 低水位（滞回，避免边界反复逐出）
                    if (decodedImageCacheBytes > MaxDecodedImageCacheBytes)
                    {
                        while (decodedImageCacheBytes > MinDecodedImageCacheBytes && decodedImageCacheOrder.Count > 0)
                        {
                            string first = decodedImageCacheOrder.Dequeue();
                            ImageSource stale;
                            if (!decodedImageCache.TryGetValue(first, out stale)) continue; // 已被覆盖/逐出，跳过失效位置
                            decodedImageCache.Remove(first);
                            decodedImageCacheBytes -= EstimateImageBytes(stale);
                        }
                    }
                }
                return image;
            }
            catch { return null; }
        }

        private static Color ParseColor(string value)
        {
            try { return (Color)ColorConverter.ConvertFromString(value); } catch { return Color.FromRgb(140, 115, 80); }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
    }
}
