// =====================================================================
// NoteWindow.cs —— 「片刻收纳」桌面原生便签（待办清单 & 随手备忘）
// ---------------------------------------------------------------------
// 100% 对齐 DeskBox 收纳框浮窗（PanelWindow）原生设计规范与视觉语言：
//   * 统一材质与毛玻璃：采用与收纳框完全一致的 DWM Acrylic 亚克力背板与 paperSurface 中性着色层。
//   * 统一 8px 圆角与 1px 细线边框：严格遵循 DeskBox 三级圆角规范（WidgetCornerRadiusLarge = 8）。
//   * 统一 30px 标题条布局：左侧 18×18 微圆角图标 Mark、13px 标题、分类计数胶囊徽章、悬停工具栏与折叠按钮。
//   * 统一交互习惯：悬停显现操作工具（新建、模式切换、置顶），平时保持宁静克制；支持完整右键 Fluent 弹出菜单。
//   * 统一待办条目与快捷添加栏：微圆角复选框、划线完成动效、与收纳框搜索栏尺寸材质 1:1 对齐的快速录入条。
//   * 约定：C# 5 语法（csc /langversion:5），中文注释，UTF-8 无 BOM。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace PivkeyOrganizer
{
    internal sealed class NoteWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const int WcaAccentPolicy = 19;
        private const uint AccentDisabled = 0;
        private const uint AccentEnableBlurBehind = 3;
        private const uint AccentEnableAcrylicBlurBehind = 4;
        private const int MinAcrylicBuild = 17134;
        private const int MaxAcrylicBuild = 21999;
        private static int osBuild = -1;

        internal const double HeaderBarHeight = 30; // 严格对齐 PanelWindow.HeaderBarHeight
        private const double PanelRadius = 8;        // 严格对齐 PanelWindow.PanelRadius
        private static readonly FontFamily ItemLabelFont = FontResources.SystemUiFont;
        private static readonly FontFamily MenuUiFont = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

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

        [DllImport("user32.dll")]
        private static extern bool SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { public int X; public int Y; }
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CursorPoint point);

        internal const double CapsuleSize = 48; // 对齐 PanelWindow.CapsuleSize
        internal const double CapsuleRadius = 14; // 对齐 PanelWindow.CapsuleRadius
        internal bool capsuleMode;
        private double expandedWidth = 280;

        // 胶囊收纳视图（48×48 微型图标，严格对齐 PanelWindow capsuleView）
        private Border capsuleView;
        private Border capsuleHighlight;
        private Grid capsuleInner;
        private ShapePath capsuleIconShape;
        private Border capsuleBadge;
        private TextBlock capsuleBadgeText;
        private bool capsuleHovered;
        private bool capsuleGestureActive;
        private bool capsuleCapsuleMoved;
        private Point capsuleDownScreen;
        private double capsuleStartLeft;
        private double capsuleStartTop;

        private readonly DesktopWindow host;
        private readonly NoteData data;
        private IntPtr hwnd;
        private bool isResizing;
        private Point resizeStartPos;
        private double resizeStartWidth;
        private double resizeStartHeight;
        private double expandedHeight = 340;

        // 全局材质与主题状态（与 PanelWindow 同步）
        private bool darkTheme;
        private Color themeAccent = Color.FromRgb(52, 120, 246);
        private Color paperSurface = Color.FromRgb(255, 255, 255);
        private int glassOpacity = 88;
        private string materialMode = "acrylic";
        private bool acrylicApplied;

        // 窗口核心容器（对齐 PanelWindow）
        private Border frameBorder;
        private Border headerBorder;
        private Grid headerGrid;

        // 标题栏元素（1:1 对齐收纳框浮窗）
        private Border mark;
        private ShapePath markIcon;
        private TextBlock titleBlock;
        private Border countBadge;
        private TextBlock countText;
        private StackPanel headerToolsPanel;
        private Button newButton;
        private Button modeButton;
        private Button pinButton;
        private Button collapseButton;

        // 便签主体布局
        private Grid mainGrid;
        private Grid bodyGrid;

        // 待办清单视图
        private Grid todoView;
        private ScrollViewer todoScrollViewer;
        private StackPanel todoItemsPanel;
        private Border addTodoBar;
        private TextBox addTodoBox;
        private TextBlock addTodoPlaceholder;

        // 随手备忘视图
        private Grid noteView;
        private TextBox noteTextBox;
        private TextBlock notePlaceholder;
        private DispatcherTimer noteSaveTimer;

        // 缩放把手
        private Border resizeGrip;

        public NoteWindow(DesktopWindow host, NoteData note)
        {
            this.host = host;
            this.data = note != null ? note.Clone() : new NoteData();
            if (String.IsNullOrWhiteSpace(this.data.Id)) this.data.Id = Guid.NewGuid().ToString("N");
            if (String.IsNullOrWhiteSpace(this.data.Mode)) this.data.Mode = "todo";
            if (String.IsNullOrWhiteSpace(this.data.Title))
            {
                this.data.Title = this.data.Mode == "note" ? "随手备忘" : "今日待办";
            }
            if (String.IsNullOrWhiteSpace(this.data.Color)) this.data.Color = "default";

            Title = "片刻便签 - " + this.data.Title;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            FontFamily = ItemLabelFont;
            MinWidth = 260;
            MinHeight = HeaderBarHeight;

            // 文字清晰度三件套（严格对齐 PanelWindow）
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            expandedWidth = Math.Max(280, this.data.Width > 0 ? this.data.Width : 280);
            expandedHeight = this.data.Height > HeaderBarHeight + 20 ? this.data.Height : 340;
            Width = expandedWidth;
            Height = this.data.Collapsed ? HeaderBarHeight : expandedHeight;
            Left = !Double.IsNaN(this.data.X) && (this.data.X != 0 || this.data.Y != 0) ? this.data.X : 240;
            Top = !Double.IsNaN(this.data.Y) && (this.data.X != 0 || this.data.Y != 0) ? this.data.Y : 160;

            SourceInitialized += OnSourceInitialized;

            BuildUi();
            ApplyTheme();
            UpdateModeView(this.data.Mode);
            UpdateCollapsedState(this.data.Collapsed);

            noteSaveTimer = new DispatcherTimer();
            noteSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
            noteSaveTimer.Tick += delegate
            {
                noteSaveTimer.Stop();
                data.NoteContent = noteTextBox.Text;
                PostNoteUpdate();
            };
        }

        private void OnSourceInitialized(object sender, EventArgs args)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow));

            UpdateWindowLayer();
            ApplyAccentBackdrop(materialMode != "solid" && OsSupportsAcrylic());
        }

        internal IntPtr NativeHandle { get { return hwnd; } }
        internal bool IsPinned { get { return data != null && data.Pinned; } }

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

        private void UpdateWindowLayer()
        {
            if (hwnd == IntPtr.Zero) return;
            if (data.Pinned)
            {
                WidgetLayer.Detach(hwnd);
                Topmost = true;
            }
            else
            {
                Topmost = false;
                WidgetLayer.Attach(hwnd);
            }
            if (pinButton != null)
            {
                Color text2 = darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90);
                pinButton.Foreground = new SolidColorBrush(data.Pinned ? themeAccent : text2);
                UpdateHeaderToolsVisibility(headerBorder != null && headerBorder.IsMouseOver);
            }
        }

        private void BuildUi()
        {
            Grid root = new Grid();

            // 外层卡片边框（严格对齐 PanelWindow：PanelRadius = 8，1px 细线边框）
            frameBorder = new Border
            {
                CornerRadius = new CornerRadius(PanelRadius),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent
            };

            mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderBarHeight) }); // 0: 30px 标题条
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: 内容区
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                  // 2: 底部快速录入条

            // ================= 0. 标题栏 (30px，1:1 对齐 PanelWindow) =================
            headerBorder = new Border
            {
                CornerRadius = new CornerRadius(PanelRadius, PanelRadius, 0, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll
            };
            headerBorder.MouseLeftButtonDown += OnHeaderMouseDown;
            headerBorder.ContextMenu = CreateHeaderMenu();

            headerGrid = new Grid { Margin = new Thickness(8, 0, 6, 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Column 0: 18x18 mark
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Column 1: title
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Column 2: countBadge
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Column 3: headerToolsPanel
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); // Column 4: collapseButton

            // Mark 图标槽（对齐 PanelWindow.cs：18×18，CornerRadius 6，微透底色）
            mark = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            markIcon = new ShapePath
            {
                Stretch = Stretch.Uniform,
                Width = 11,
                Height = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            mark.Child = markIcon;
            Grid.SetColumn(mark, 0);
            headerGrid.Children.Add(mark);

            // 标题（13px Medium 微软雅黑/Segoe UI，严格对齐 PanelWindow）
            titleBlock = new TextBlock
            {
                Text = data.Title,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                FontFamily = ItemLabelFont,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "双击修改标题",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 4, 0)
            };
            titleBlock.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                {
                    if (data != null && data.Collapsed)
                    {
                        ToggleCollapse();
                        e.Handled = true;
                    }
                    else
                    {
                        BeginRename();
                        e.Handled = true;
                    }
                }
            };
            Grid.SetColumn(titleBlock, 1);
            headerGrid.Children.Add(titleBlock);

            // 计数微型徽章（对齐 PanelWindow.cs：CornerRadius 7，Padding 5,1,5,1，10.5px Medium）
            countBadge = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(2, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            countText = new TextBlock
            {
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                FontFamily = ItemLabelFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            countBadge.Child = countText;
            Grid.SetColumn(countBadge, 2);
            headerGrid.Children.Add(countBadge);

            // 右侧悬停工具栏（对齐 PanelWindow：默认 Collapsed，悬停时优雅现身）
            headerToolsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            newButton = HeaderButton("新建待办清单或随手备忘");
            newButton.Content = CreatePhosphorIcon("plus", 14, true);
            newButton.Click += delegate
            {
                ContextMenu menu = CreateNewMenu();
                menu.PlacementTarget = newButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            headerToolsPanel.Children.Add(newButton);

            modeButton = HeaderButton("切换待办清单 / 随手备忘模式");
            modeButton.Content = CreatePhosphorIcon("arrows-left-right", 14, true);
            modeButton.Click += delegate { ToggleMode(); };
            headerToolsPanel.Children.Add(modeButton);

            pinButton = HeaderButton("固定 / 贴在桌面底层");
            pinButton.Content = CreatePhosphorIcon("pin", 14, true);
            pinButton.Click += delegate { TogglePin(); };
            headerToolsPanel.Children.Add(pinButton);

            Grid.SetColumn(headerToolsPanel, 3);
            headerGrid.Children.Add(headerToolsPanel);

            // 折叠按钮（24×24，对齐 PanelWindow collapseButton）
            collapseButton = HeaderButton("折叠 / 展开");
            collapseButton.Content = CreatePhosphorIcon("chevron-down", 14, true);
            collapseButton.Click += delegate { ToggleCollapse(); };
            Grid.SetColumn(collapseButton, 4);
            headerGrid.Children.Add(collapseButton);

            headerBorder.MouseEnter += delegate { UpdateHeaderToolsVisibility(true); };
            headerBorder.MouseLeave += delegate { UpdateHeaderToolsVisibility(false); };

            headerBorder.Child = headerGrid;
            Grid.SetRow(headerBorder, 0);
            mainGrid.Children.Add(headerBorder);

            // ================= 1. 内容主体区域 =================
            bodyGrid = new Grid();
            bodyGrid.ContextMenu = CreateHeaderMenu();

            BuildTodoView();
            BuildNoteView();

            bodyGrid.Children.Add(todoView);
            bodyGrid.Children.Add(noteView);
            Grid.SetRow(bodyGrid, 1);
            mainGrid.Children.Add(bodyGrid);

            // ================= 2. 底部添加条（对齐 PanelWindow.searchBar 材质与尺寸） =================
            addTodoBox = new TextBox
            {
                Height = 24,
                Margin = new Thickness(0),
                Padding = new Thickness(8, 2, 8, 2),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 11.5,
                FontFamily = ItemLabelFont,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            addTodoBox.TextChanged += delegate
            {
                addTodoPlaceholder.Visibility = String.IsNullOrEmpty(addTodoBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            addTodoBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    string text = addTodoBox.Text != null ? addTodoBox.Text.Trim() : "";
                    if (text.Length > 0)
                    {
                        AddTodoItem(text);
                        addTodoBox.Text = "";
                    }
                    e.Handled = true;
                }
            };

            addTodoPlaceholder = new TextBlock
            {
                Text = "+ 添加新待办事项... (Enter 确认)",
                FontSize = 11.5,
                FontFamily = ItemLabelFont,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Margin = new Thickness(8, 0, 0, 0)
            };

            Grid addGrid = new Grid();
            addGrid.Children.Add(addTodoPlaceholder);
            addGrid.Children.Add(addTodoBox);

            addTodoBar = new Border
            {
                Child = addGrid,
                Height = 29,
                Margin = new Thickness(8, 3, 8, 6),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Visible
            };
            Grid.SetRow(addTodoBar, 2);
            mainGrid.Children.Add(addTodoBar);

            // ================= 3. 右下角缩放把手（严格对齐 PanelWindow.resizeGrip） =================
            resizeGrip = new Border
            {
                Width = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 1.5, 1.5),
                CornerRadius = new CornerRadius(0, 0, 8, 0),
                Cursor = Cursors.SizeNWSE,
                Visibility = (data != null && data.Collapsed) ? Visibility.Collapsed : Visibility.Visible
            };
            resizeGrip.MouseLeftButtonDown += OnResizeMouseDown;

            // ================= 4. 图标胶囊收纳视图 (48×48，严格对齐 PanelWindow) =================
            capsuleView = new Border
            {
                Width = CapsuleSize,
                Height = CapsuleSize,
                CornerRadius = new CornerRadius(CapsuleRadius),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            capsuleHighlight = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(CapsuleRadius - 1),
                BorderThickness = new Thickness(1)
            };
            capsuleIconShape = new ShapePath
            {
                Stroke = null,
                StrokeThickness = 0,
                Stretch = Stretch.Uniform,
                Width = 24,
                Height = 24
            };
            capsuleInner = new Grid { Width = 24, Height = 24 };
            capsuleInner.RenderTransform = new ScaleTransform(1, 1, 12, 12);
            capsuleIconShape.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 1.0,
                Direction = 225,
                Opacity = 0.25,
                Color = Colors.Black
            };
            capsuleInner.Children.Add(capsuleIconShape);

            capsuleBadge = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(4, 0.5, 4, 0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            capsuleBadgeText = new TextBlock
            {
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            capsuleBadge.Child = capsuleBadgeText;

            Grid capsuleLayer = new Grid();
            capsuleLayer.Children.Add(capsuleHighlight);
            capsuleLayer.Children.Add(capsuleInner);
            capsuleLayer.Children.Add(capsuleBadge);
            capsuleView.Child = capsuleLayer;

            capsuleView.MouseEnter += delegate { capsuleHovered = true; ApplyCapsuleVisual(true); };
            capsuleView.MouseLeave += delegate { capsuleHovered = false; ApplyCapsuleVisual(false); };
            capsuleView.MouseLeftButtonDown += OnCapsuleMouseDown;
            capsuleView.MouseMove += OnCapsuleMouseMove;
            capsuleView.MouseLeftButtonUp += OnCapsuleMouseUp;
            capsuleView.LostMouseCapture += delegate { FinishCapsuleGesture(capsuleCapsuleMoved); };
            capsuleView.ContextMenu = CreateHeaderMenu();

            root.Children.Add(frameBorder);
            root.Children.Add(resizeGrip);
            root.Children.Add(capsuleView);

            frameBorder.Child = mainGrid;
            Content = root;
        }

        private void UpdateHeaderToolsVisibility(bool hover)
        {
            if (headerToolsPanel == null) return;
            if (hover && (data == null || !data.Collapsed))
            {
                headerToolsPanel.Visibility = Visibility.Visible;
                newButton.Visibility = Visibility.Visible;
                modeButton.Visibility = Visibility.Visible;
                pinButton.Visibility = Visibility.Visible;
            }
            else
            {
                bool showPin = data != null && data.Pinned && !data.Collapsed;
                pinButton.Visibility = showPin ? Visibility.Visible : Visibility.Collapsed;
                newButton.Visibility = Visibility.Collapsed;
                modeButton.Visibility = Visibility.Collapsed;
                headerToolsPanel.Visibility = showPin ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // 构建 Todo 视图
        private void BuildTodoView()
        {
            todoView = new Grid();
            todoScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 4, 8, 4),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            todoItemsPanel = new StackPanel { Background = Brushes.Transparent, Orientation = Orientation.Vertical };
            todoScrollViewer.Content = todoItemsPanel;
            todoView.Children.Add(todoScrollViewer);
        }

        // 构建 Note 备忘视图
        private void BuildNoteView()
        {
            noteView = new Grid { Visibility = Visibility.Collapsed };

            notePlaceholder = new TextBlock
            {
                Text = "随手记录灵感、草稿与备忘...",
                FontSize = 12.5,
                FontFamily = ItemLabelFont,
                Margin = new Thickness(12, 10, 10, 8),
                IsHitTestVisible = false
            };

            noteTextBox = new TextBox
            {
                Text = data.NoteContent != null ? data.NoteContent : "",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 12.5,
                FontFamily = ItemLabelFont,
                Padding = new Thickness(10, 8, 10, 8),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            noteTextBox.TextChanged += delegate
            {
                notePlaceholder.Visibility = String.IsNullOrEmpty(noteTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                noteSaveTimer.Stop();
                noteSaveTimer.Start();
            };
            notePlaceholder.Visibility = String.IsNullOrEmpty(noteTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;

            noteView.Children.Add(notePlaceholder);
            noteView.Children.Add(noteTextBox);
        }

        // 渲染待办清单项
        private void RefreshTodoListUi()
        {
            todoItemsPanel.Children.Clear();
            int total = data.Todos.Count;
            int done = 0;

            for (int i = 0; i < data.Todos.Count; i++)
            {
                TodoItemData item = data.Todos[i];
                if (item.Done) done++;
                todoItemsPanel.Children.Add(CreateTodoItemRow(item));
            }

            if (data.Mode == "todo")
            {
                countText.Text = string.Format("{0}/{1}", done, total);
            }
            UpdateCapsuleIcon();
        }

        private UIElement CreateTodoItemRow(TodoItemData item)
        {
            Border rowBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 28
            };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            // DeskBox 16×16 微圆角复选框 (CornerRadius = 4)
            Border checkBox = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand
            };

            if (item.Done)
            {
                checkBox.Background = new SolidColorBrush(themeAccent);
                checkBox.BorderBrush = new SolidColorBrush(themeAccent);
                checkBox.Child = CreatePhosphorIcon("check", 10, false);
            }
            else
            {
                checkBox.Background = Brushes.Transparent;
                checkBox.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0));
            }

            checkBox.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                item.Done = !item.Done;
                RefreshTodoListUi();
                PostNoteUpdate();
                e.Handled = true;
            };
            Grid.SetColumn(checkBox, 0);
            row.Children.Add(checkBox);

            // 待办文本（12.5px 微软雅黑/Segoe UI）
            Color textColor = darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
            TextBlock textBlock = new TextBlock
            {
                Text = item.Text,
                FontSize = 12.5,
                FontFamily = ItemLabelFont,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
                Foreground = new SolidColorBrush(textColor),
                Cursor = Cursors.Hand
            };
            textBlock.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                item.Done = !item.Done;
                RefreshTodoListUi();
                PostNoteUpdate();
                e.Handled = true;
            };

            if (item.Done)
            {
                textBlock.TextDecorations = TextDecorations.Strikethrough;
                textBlock.Opacity = 0.45;
            }
            Grid.SetColumn(textBlock, 1);
            row.Children.Add(textBlock);

            // 单项删除小叉（悬停显现）
            Button delBtn = HeaderButton("删除该待办");
            delBtn.Width = 18;
            delBtn.Height = 18;
            delBtn.Content = CreatePhosphorIcon("close", 10, true);
            delBtn.Opacity = 0;
            delBtn.Click += delegate
            {
                data.Todos.Remove(item);
                RefreshTodoListUi();
                PostNoteUpdate();
            };
            Grid.SetColumn(delBtn, 2);
            row.Children.Add(delBtn);

            rowBorder.MouseEnter += delegate
            {
                rowBorder.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(14, themeAccent.R, themeAccent.G, themeAccent.B));
                delBtn.Opacity = 0.75;
            };
            rowBorder.MouseLeave += delegate
            {
                rowBorder.Background = Brushes.Transparent;
                delBtn.Opacity = 0;
            };

            rowBorder.Child = row;
            return rowBorder;
        }

        private void AddTodoItem(string text)
        {
            TodoItemData item = new TodoItemData();
            item.Id = Guid.NewGuid().ToString("N");
            item.Text = text;
            item.Done = false;
            item.CreatedAt = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            data.Todos.Add(item);
            RefreshTodoListUi();
            PostNoteUpdate();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (todoScrollViewer != null) todoScrollViewer.ScrollToBottom();
            }));
        }

        private void ClearCompletedTodos()
        {
            List<TodoItemData> remaining = new List<TodoItemData>();
            foreach (TodoItemData t in data.Todos)
            {
                if (!t.Done) remaining.Add(t);
            }
            data.Todos = remaining;
            RefreshTodoListUi();
            PostNoteUpdate();
        }

        // 切换模式：待办 ⇋ 记事
        private void ToggleMode()
        {
            data.Mode = data.Mode == "todo" ? "note" : "todo";
            UpdateModeView(data.Mode);
            PostNoteUpdate();
        }

        private void UpdateModeView(string mode)
        {
            if (mode == "note")
            {
                todoView.Visibility = Visibility.Collapsed;
                addTodoBar.Visibility = Visibility.Collapsed;
                noteView.Visibility = Visibility.Visible;
                SetMarkIcon("file-text");
                modeButton.ToolTip = "当前：随手备忘（点击切换为待办清单）";
                countText.Text = "备忘";
                countBadge.Visibility = data.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                todoView.Visibility = Visibility.Visible;
                addTodoBar.Visibility = data.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                noteView.Visibility = Visibility.Collapsed;
                SetMarkIcon("check-square");
                modeButton.ToolTip = "当前：待办清单（点击切换为随手备忘）";
                RefreshTodoListUi();
                countBadge.Visibility = Visibility.Visible;
            }
            UpdateCapsuleIcon();
        }

        private void SetMarkIcon(string iconName)
        {
            StreamGeometry geom = (StreamGeometry)Geometry.Parse(PhosphorGeometry(iconName));
            geom = geom.Clone();
            geom.FillRule = FillRule.Nonzero;
            markIcon.Data = geom;
            markIcon.Fill = new SolidColorBrush(themeAccent);
        }

        internal void ApplyCapsuleMode(bool mode)
        {
            if (capsuleMode == mode) return;
            capsuleMode = mode;
            UpdateCollapsedState(data.Collapsed);
            ApplyCapsuleVisual(capsuleHovered);
        }

        // 折叠 / 展开便签（支持 30px 胶囊条与 48×48 小图标两种形态）
        private void ToggleCollapse()
        {
            data.Collapsed = !data.Collapsed;
            UpdateCollapsedState(data.Collapsed);
            PostGeometry();
            PostNoteUpdate();
        }

        private void UpdateCollapsedState(bool collapsed)
        {
            Color text2 = darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90);
            if (resizeGrip != null)
            {
                resizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            if (collapsed)
            {
                if (Height > HeaderBarHeight + 20) expandedHeight = Height;
                if (Width > CapsuleSize + 20) expandedWidth = Width;

                if (capsuleMode)
                {
                    frameBorder.Visibility = Visibility.Collapsed;
                    capsuleView.Visibility = Visibility.Visible;
                    MinWidth = CapsuleSize;
                    MinHeight = CapsuleSize;
                    Width = CapsuleSize;
                    Height = CapsuleSize;
                    UpdateCapsuleIcon();
                    ApplyCapsuleVisual(capsuleHovered);
                }
                else
                {
                    capsuleView.Visibility = Visibility.Collapsed;
                    frameBorder.Visibility = Visibility.Visible;
                    bodyGrid.Visibility = Visibility.Collapsed;
                    addTodoBar.Visibility = Visibility.Collapsed;
                    MinWidth = 260;
                    MinHeight = HeaderBarHeight;
                    Width = Math.Max(260, expandedWidth);
                    Height = HeaderBarHeight;
                    headerBorder.CornerRadius = new CornerRadius(PanelRadius);
                    headerBorder.BorderThickness = new Thickness(0);
                    collapseButton.Content = CreatePhosphorIcon("chevron-up", 14, true);
                    collapseButton.ToolTip = "展开便签";
                    countBadge.Visibility = Visibility.Visible;
                    if (titleBlock != null)
                    {
                        titleBlock.Cursor = Cursors.SizeAll;
                        titleBlock.ToolTip = "双击展开便签，拖拽移动位置";
                    }
                    UpdateHeaderToolsVisibility(false);
                }
            }
            else
            {
                capsuleView.Visibility = Visibility.Collapsed;
                frameBorder.Visibility = Visibility.Visible;
                bodyGrid.Visibility = Visibility.Visible;
                addTodoBar.Visibility = data.Mode == "todo" ? Visibility.Visible : Visibility.Collapsed;
                MinWidth = 260;
                MinHeight = HeaderBarHeight;
                Width = Math.Max(260, expandedWidth);
                Height = Math.Max(140, expandedHeight);
                headerBorder.CornerRadius = new CornerRadius(PanelRadius, PanelRadius, 0, 0);
                headerBorder.BorderThickness = new Thickness(0, 0, 0, 1);
                collapseButton.Content = CreatePhosphorIcon("chevron-down", 14, true);
                collapseButton.ToolTip = capsuleMode ? "收纳为小图标" : "折叠为胶囊条";
                countBadge.Visibility = data.Mode == "todo" ? Visibility.Visible : Visibility.Collapsed;
                if (titleBlock != null)
                {
                    titleBlock.Cursor = Cursors.Hand;
                    titleBlock.ToolTip = "双击修改标题";
                }
                UpdateHeaderToolsVisibility(headerBorder != null && headerBorder.IsMouseOver);
            }
        }

        private void UpdateCapsuleIcon()
        {
            if (capsuleIconShape == null) return;
            string iconName = (data != null && data.Mode == "note") ? "file-text" : "check-square";
            StreamGeometry geom = (StreamGeometry)Geometry.Parse(PhosphorGeometry(iconName));
            geom = geom.Clone();
            geom.FillRule = FillRule.Nonzero;
            capsuleIconShape.Data = geom;
            capsuleIconShape.Fill = new SolidColorBrush(themeAccent);

            if (capsuleBadge != null && capsuleBadgeText != null)
            {
                if (data != null && data.Mode == "todo" && data.Todos != null && data.Todos.Count > 0)
                {
                    int pending = 0;
                    for (int i = 0; i < data.Todos.Count; i++)
                    {
                        if (!data.Todos[i].Done) pending++;
                    }
                    if (pending > 0)
                    {
                        capsuleBadgeText.Text = pending > 99 ? "99+" : pending.ToString();
                        capsuleBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        capsuleBadge.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    capsuleBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ApplyCapsuleVisual(bool hover)
        {
            if (capsuleView == null) return;
            byte alpha = (byte)(hover ? 255 : 246);
            Color surface = darkTheme
                ? Color.FromRgb(hover ? (byte)42 : (byte)31, hover ? (byte)42 : (byte)31, hover ? (byte)42 : (byte)31)
                : Color.FromRgb(hover ? (byte)252 : (byte)243, hover ? (byte)252 : (byte)243, hover ? (byte)252 : (byte)243);
            capsuleView.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
            capsuleView.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x28, 255, 255, 255) : Color.FromArgb(0x1E, 0, 0, 0));
            capsuleView.BorderThickness = new Thickness(1);
            if (capsuleHighlight != null)
            {
                capsuleHighlight.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
            }
            if (capsuleBadge != null)
            {
                capsuleBadge.Background = new SolidColorBrush(themeAccent);
            }
            if (capsuleInner != null)
            {
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
        }

        private void OnCapsuleMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (hwnd == IntPtr.Zero || isResizing) return;
            CursorPoint cursor;
            GetCursorPos(out cursor);
            capsuleGestureActive = true;
            capsuleCapsuleMoved = false;
            capsuleDownScreen = new Point(cursor.X, cursor.Y);
            capsuleStartLeft = Left;
            capsuleStartTop = Top;
            Mouse.Capture(capsuleView, CaptureMode.Element);
            args.Handled = true;
        }

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
            double scale = 1.0;
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            double dx = (cursor.X - capsuleDownScreen.X) / scale;
            double dy = (cursor.Y - capsuleDownScreen.Y) / scale;
            if (!capsuleCapsuleMoved)
            {
                if (Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
                capsuleCapsuleMoved = true;
            }
            Left = capsuleStartLeft + dx;
            Top = capsuleStartTop + dy;
            args.Handled = true;
        }

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
            if (Mouse.Captured == capsuleView) Mouse.Capture(null);
            if (moved)
            {
                PostGeometry();
            }
            else
            {
                ToggleCollapse();
            }
        }

        private void TogglePin()
        {
            data.Pinned = !data.Pinned;
            UpdateWindowLayer();
            PostNoteUpdate();
        }

        // 核心视觉刷新：100% 对齐 PanelWindow 的表面材质、透明度与边框色彩
        internal void ApplyThemeVisuals(string theme, string accentHex, string surfaceHex, int opacity, string material)
        {
            bool nextDark = String.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
            Color nextAccent = themeAccent;
            if (!String.IsNullOrWhiteSpace(accentHex))
            {
                try { nextAccent = (Color)ColorConverter.ConvertFromString(accentHex); } catch { }
            }
            Color nextSurface = paperSurface;
            if (!String.IsNullOrWhiteSpace(surfaceHex))
            {
                try { nextSurface = (Color)ColorConverter.ConvertFromString(surfaceHex); } catch { }
            }
            else
            {
                nextSurface = nextDark ? Color.FromRgb(34, 33, 30) : Color.FromRgb(255, 255, 255);
            }
            int nextOpacity = opacity >= 0 && opacity <= 100 ? opacity : 88;
            string nextMaterial = String.IsNullOrWhiteSpace(material) ? "acrylic" : material;

            bool changed = darkTheme != nextDark || themeAccent != nextAccent || paperSurface != nextSurface || glassOpacity != nextOpacity || materialMode != nextMaterial;
            darkTheme = nextDark;
            themeAccent = nextAccent;
            paperSurface = nextSurface;
            glassOpacity = nextOpacity;
            materialMode = nextMaterial;

            if (changed)
            {
                ApplyTheme();
                if (hwnd != IntPtr.Zero)
                {
                    ApplyAccentBackdrop(materialMode != "solid" && OsSupportsAcrylic());
                }
            }
        }

        private void ApplyTheme()
        {
            // DeskBox Widget 令牌（与 PanelWindow.ApplyTheme 完全一致）：
            Color text1 = darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
            Color text2 = darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90);
            Color text3 = darkTheme ? Color.FromRgb(140, 140, 140) : Color.FromRgb(122, 122, 122);

            // 背景透明度计算（严格移植 PanelWindow.ApplyFrameChrome）
            bool acrylic = materialMode != "solid" && OsSupportsAcrylic();
            double mappedAlpha;
            if (acrylic)
            {
                mappedAlpha = 20 + glassOpacity * 2.0;
            }
            else
            {
                mappedAlpha = Math.Max(90, Math.Min(250, glassOpacity * 2.25));
            }
            byte glassAlpha = (byte)Math.Max(0, Math.Min(255, Math.Round(mappedAlpha)));
            Color paper = paperSurface;
            if (paper.R == 0 && paper.G == 0 && paper.B == 0)
                paper = darkTheme ? Color.FromRgb(31, 31, 31) : Color.FromRgb(255, 255, 255);

            frameBorder.Background = new SolidColorBrush(Color.FromArgb(glassAlpha, paper.R, paper.G, paper.B));
            frameBorder.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x28, 255, 255, 255) : Color.FromArgb(0x28, themeAccent.R, themeAccent.G, themeAccent.B));
            frameBorder.BorderThickness = new Thickness(1);

            headerBorder.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x18, themeAccent.R, themeAccent.G, themeAccent.B));
            headerBorder.BorderThickness = data.Collapsed ? new Thickness(0) : new Thickness(0, 0, 0, 1);

            // Mark 槽与图标
            mark.Background = new SolidColorBrush(Color.FromArgb(30, themeAccent.R, themeAccent.G, themeAccent.B));
            markIcon.Fill = new SolidColorBrush(themeAccent);

            // 标题
            titleBlock.Foreground = new SolidColorBrush(text1);

            // 计数徽章
            countBadge.Background = new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)32 : (byte)22, themeAccent.R, themeAccent.G, themeAccent.B));
            countText.Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(255, 180, 185) : themeAccent);

            // 工具按钮
            pinButton.Foreground = new SolidColorBrush(data.Pinned ? themeAccent : text2);
            collapseButton.Foreground = new SolidColorBrush(text2);
            modeButton.Foreground = new SolidColorBrush(text2);
            newButton.Foreground = new SolidColorBrush(text2);

            // 底部快速输入条（对齐 PanelWindow.searchBar 风格）
            addTodoBar.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(18, themeAccent.R, themeAccent.G, themeAccent.B));
            addTodoBar.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(26, themeAccent.R, themeAccent.G, themeAccent.B));
            addTodoPlaceholder.Foreground = new SolidColorBrush(text3);
            addTodoBox.Foreground = new SolidColorBrush(text1);

            // 备忘编辑
            noteTextBox.Foreground = new SolidColorBrush(text1);
            notePlaceholder.Foreground = new SolidColorBrush(text3);

            // 右下角缩放手柄（对齐 PanelWindow.resizeGrip）
            if (resizeGrip != null)
            {
                resizeGrip.BorderBrush = new SolidColorBrush(darkTheme ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(52, 0, 0, 0));
                resizeGrip.Visibility = (data != null && data.Collapsed) ? Visibility.Collapsed : Visibility.Visible;
            }

            ApplyCapsuleVisual(capsuleHovered);
            UpdateCapsuleIcon();
            RefreshTodoListUi();
            UpdateHeaderToolsVisibility(headerBorder != null && headerBorder.IsMouseOver);
        }

        // 真亚克力材质挂载（移植 PanelWindow.ApplyAccentBackdrop）
        private void ApplyAccentBackdrop(bool acrylic)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!acrylic)
            {
                if (acrylicApplied) { acrylicApplied = false; SetAccentPolicy(AccentDisabled, 0); }
                return;
            }
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
                WindowCompositionAttributeData d = new WindowCompositionAttributeData();
                d.Attribute = WcaAccentPolicy;
                d.SizeOfData = Marshal.SizeOf(typeof(AccentPolicy));
                d.Data = buffer;
                SetWindowCompositionAttribute(hwnd, ref d);
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

        // ================= DeskBox Fluent 风格右键菜单 (1:1 对齐 PanelWindow) =================
        private ContextMenu StyledMenu()
        {
            ContextMenu menu = new ContextMenu
            {
                MinWidth = 208,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HasDropShadow = false,
                FontFamily = MenuUiFont,
                FontSize = 13,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(menu, ClearTypeHint.Enabled);
            menu.Template = BuildMenuSurfaceTemplate(8);
            return menu;
        }

        private ControlTemplate BuildMenuSurfaceTemplate(double cornerRadius)
        {
            ControlTemplate template = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(darkTheme ? Color.FromArgb(246, 38, 38, 38) : Color.FromArgb(248, 252, 252, 252)));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(darkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0)));
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
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (!String.IsNullOrEmpty(iconName))
            {
                FrameworkElement icon = CreatePhosphorIcon(iconName, 16, false);
                ShapePath path = icon as ShapePath;
                if (path != null)
                {
                    path.Fill = new SolidColorBrush(darkTheme ? Color.FromRgb(185, 185, 185) : Color.FromRgb(92, 92, 92));
                }
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
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26))
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            MenuItem item = new MenuItem { Header = row };
            ControlTemplate template = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.Name = "Root";

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.MarginProperty, new Thickness(3, 1, 3, 1));
            border.SetValue(Border.PaddingProperty, new Thickness(4, 0, 6, 0));
            border.SetValue(Border.MinHeightProperty, 30.0);

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            border.AppendChild(content);
            dock.AppendChild(border);

            template.VisualTree = dock;

            Trigger highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(darkTheme ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0)), "Bd"));
            template.Triggers.Add(highlightTrigger);

            item.Template = template;
            return item;
        }

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

            MenuItem newTodo = StyledMenuItem("新建待办清单", "check");
            newTodo.Click += delegate { host.PostManagerEvent("noteCreate", "todo"); };
            menu.Items.Add(newTodo);

            MenuItem newMemo = StyledMenuItem("新建随手备忘", "file-text");
            newMemo.Click += delegate { host.PostManagerEvent("noteCreate", "note"); };
            menu.Items.Add(newMemo);

            MenuItem toggleMode = StyledMenuItem(data.Mode == "note" ? "切换为待办清单" : "切换为随手备忘", "arrows-left-right");
            toggleMode.Click += delegate { ToggleMode(); };
            menu.Items.Add(toggleMode);

            MenuItem rename = StyledMenuItem("重命名便签", "pencil");
            rename.Click += delegate { RunAfterMenuClosed(menu, BeginRename); };
            menu.Items.Add(rename);

            MenuItem pin = StyledMenuItem(data.Pinned ? "取消固定" : "固定到最顶层", "pin");
            pin.Click += delegate { TogglePin(); };
            menu.Items.Add(pin);

            MenuItem collapse = StyledMenuItem(data.Collapsed ? "展开便签" : (capsuleMode ? "收纳为小图标" : "折叠为胶囊条"), data.Collapsed ? "chevron-down" : "chevron-up");
            collapse.Click += delegate { ToggleCollapse(); };
            menu.Items.Add(collapse);

            if (data.Mode == "todo")
            {
                int doneCount = 0;
                foreach (TodoItemData t in data.Todos) if (t.Done) doneCount++;
                if (doneCount > 0)
                {
                    MenuItem clear = StyledMenuItem(string.Format("清空已完成项目 ({0})", doneCount), "check");
                    clear.Click += delegate { ClearCompletedTodos(); };
                    menu.Items.Add(clear);
                }
            }

            menu.Items.Add(StyledSeparator());

            MenuItem deleteItem = StyledMenuItem("删除此便签", "close");
            deleteItem.Click += delegate { ConfirmDelete(); };
            menu.Items.Add(deleteItem);

            return menu;
        }

        private ContextMenu CreateNewMenu()
        {
            ContextMenu menu = StyledMenu();

            MenuItem todoItem = StyledMenuItem("新建待办清单", "check");
            todoItem.Click += delegate { host.PostManagerEvent("noteCreate", "todo"); };
            menu.Items.Add(todoItem);

            MenuItem noteItem = StyledMenuItem("新建随手备忘", "file-text");
            noteItem.Click += delegate { host.PostManagerEvent("noteCreate", "note"); };
            menu.Items.Add(noteItem);

            return menu;
        }

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

        // 标题原地行内编辑（1:1 对齐 PanelWindow.cs BeginRename）
        private void BeginRename()
        {
            TextBox editor = new TextBox
            {
                Text = data.Title,
                Width = Math.Max(100, titleBlock.ActualWidth),
                Height = 22,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontFamily = ItemLabelFont
            };
            Grid parent = titleBlock.Parent as Grid;
            if (parent == null) return;
            int column = Grid.GetColumn(titleBlock);
            parent.Children.Remove(titleBlock);
            Grid.SetColumn(editor, column);
            parent.Children.Add(editor);
            editor.Focus();
            editor.SelectAll();
            bool committed = false;
            Action commit = delegate
            {
                if (committed) return;
                committed = true;
                string requested = editor.Text != null ? editor.Text.Trim() : "";
                parent.Children.Remove(editor);
                Grid.SetColumn(titleBlock, column);
                parent.Children.Add(titleBlock);
                if (requested.Length > 0 && requested != data.Title)
                {
                    data.Title = requested;
                    titleBlock.Text = requested;
                    Title = "片刻便签 - " + requested;
                    PostNoteUpdate();
                }
            };
            editor.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    commit();
                    args.Handled = true;
                }
                else if (args.Key == Key.Escape)
                {
                    if (!committed)
                    {
                        committed = true;
                        parent.Children.Remove(editor);
                        Grid.SetColumn(titleBlock, column);
                        parent.Children.Add(titleBlock);
                    }
                    args.Handled = true;
                }
            };
            editor.LostKeyboardFocus += delegate { commit(); };
        }

        private void ConfirmDelete()
        {
            if (ConfirmDialog.ShowDelete(this, "片刻便签", "确定要删除此便签吗？", "删除后便签及其待办内容将无法恢复。", darkTheme, themeAccent))
            {
                host.PostManagerEvent("noteDelete", data.Id);
                Close();
            }
        }

        private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && e.Source as Button == null)
            {
                ToggleCollapse();
                e.Handled = true;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed && e.Source as Button == null)
            {
                try
                {
                    DragMove();
                    PostGeometry();
                }
                catch { }
            }
        }

        // 缩放把手（右下角）
        private void OnResizeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (data.Collapsed) return;
            isResizing = true;
            resizeStartPos = PointToScreen(e.GetPosition(this));
            resizeStartWidth = ActualWidth;
            resizeStartHeight = ActualHeight;
            Mouse.Capture(sender as UIElement);

            MouseMove += OnResizeMouseMove;
            MouseUp += OnResizeMouseUp;
            e.Handled = true;
        }

        private void OnResizeMouseMove(object sender, MouseEventArgs e)
        {
            if (!isResizing || e.LeftButton != MouseButtonState.Pressed) return;
            Point current = PointToScreen(e.GetPosition(this));
            double scale = 1.0;
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }

            double dx = (current.X - resizeStartPos.X) / scale;
            double dy = (current.Y - resizeStartPos.Y) / scale;

            Width = Math.Max(230, resizeStartWidth + dx);
            Height = Math.Max(140, resizeStartHeight + dy);
            expandedWidth = Width;
            expandedHeight = Height;
            e.Handled = true;
        }

        private void OnResizeMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isResizing)
            {
                isResizing = false;
                MouseMove -= OnResizeMouseMove;
                MouseUp -= OnResizeMouseUp;
                Mouse.Capture(null);
                PostGeometry();
                e.Handled = true;
            }
        }

        private void PostGeometry()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["id"] = data.Id;
            payload["x"] = Left;
            payload["y"] = Top;
            payload["width"] = (data.Collapsed && capsuleMode) ? expandedWidth : Width;
            payload["height"] = data.Collapsed ? expandedHeight : Height;
            host.PostManagerEvent("noteGeometry", payload);
        }

        private void PostNoteUpdate()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["id"] = data.Id;
            payload["title"] = data.Title;
            payload["mode"] = data.Mode;
            payload["color"] = data.Color;
            payload["pinned"] = data.Pinned;
            payload["collapsed"] = data.Collapsed;
            payload["noteContent"] = data.NoteContent != null ? data.NoteContent : "";

            List<object> todos = new List<object>();
            if (data.Todos != null)
            {
                foreach (TodoItemData t in data.Todos)
                {
                    Dictionary<string, object> td = new Dictionary<string, object>();
                    td["id"] = t.Id;
                    td["text"] = t.Text;
                    td["done"] = t.Done;
                    td["createdAt"] = t.CreatedAt;
                    todos.Add(td);
                }
            }
            payload["todos"] = todos;

            host.PostManagerEvent("noteUpdate", payload);
        }

        internal void UpdateData(NoteData latest)
        {
            if (latest == null) return;
            data.Title = latest.Title;
            data.Mode = latest.Mode;
            data.Color = latest.Color;
            data.Pinned = latest.Pinned;
            data.Collapsed = latest.Collapsed;
            data.NoteContent = latest.NoteContent;
            data.Todos = latest.Todos != null ? latest.Todos : new List<TodoItemData>();

            if (latest.Width > 0 && Math.Abs(latest.Width - expandedWidth) > 1 && !data.Collapsed)
            {
                expandedWidth = latest.Width;
                Width = expandedWidth;
            }
            if (latest.Height > 0 && Math.Abs(latest.Height - expandedHeight) > 1 && !data.Collapsed)
            {
                expandedHeight = latest.Height;
                Height = expandedHeight;
            }

            titleBlock.Text = data.Title;
            ApplyTheme();
            UpdateModeView(data.Mode);
            UpdateCollapsedState(data.Collapsed);
            UpdateWindowLayer();
            if (data.Mode == "note" && noteTextBox != null)
            {
                if (noteTextBox.Text != data.NoteContent)
                {
                    noteTextBox.Text = data.NoteContent != null ? data.NoteContent : "";
                }
            }
        }

        // ================= 标题栏按钮（1:1 移植 PanelWindow.HeaderButton） =================
        private Button HeaderButton(string tooltip)
        {
            Button button = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(90, 90, 90)),
                ToolTip = tooltip,
                Cursor = Cursors.Hand,
                Focusable = false,
                Template = FlatButtonTemplate()
            };
            button.MouseEnter += delegate
            {
                button.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(0x20, 255, 255, 255) : Color.FromArgb(0x1C, themeAccent.R, themeAccent.G, themeAccent.B));
            };
            button.MouseLeave += delegate { button.Background = Brushes.Transparent; };
            return button;
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

        private static FrameworkElement CreatePhosphorIcon(string name, double size, bool bindToButton)
        {
            StreamGeometry iconGeometry = (StreamGeometry)Geometry.Parse(PhosphorGeometry(name));
            iconGeometry = iconGeometry.Clone();
            iconGeometry.FillRule = FillRule.Nonzero;
            ShapePath icon = new ShapePath
            {
                Data = iconGeometry,
                Stroke = null,
                StrokeThickness = 0,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size
            };
            if (bindToButton)
            {
                icon.SetBinding(ShapePath.FillProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
            }
            return icon;
        }

        private static string PhosphorGeometry(string name)
        {
            if (name == "check") return "M216.49,80.49a12,12,0,0,1,0,17l-104,104a12,12,0,0,1-17,0l-56-56a12,12,0,0,1,17-17L104,176l95.51-95.51A12,12,0,0,1,216.49,80.49Z";
            if (name == "check-square") return "M208,32H48A24,24,0,0,0,24,56V200a24,24,0,0,0,24,24H208a24,24,0,0,0,24-24V56A24,24,0,0,0,208,32Zm0,168H48V56H208V200Zm-31.51-99.51a12,12,0,0,1,0,17l-56,56a12,12,0,0,1-17,0l-24-24a12,12,0,1,1,17-17L112,143l47.51-47.51A12,12,0,0,1,176.49,100.49Z";
            if (name == "close") return "M205.66,194.34a12,12,0,0,1-17,17L128,150.97l-60.69,60.37a12,12,0,0,1-16.97-16.97L111.03,134,50.34,73.66a12,12,0,0,1,16.97-17L128,117.03l60.69-60.37a12,12,0,0,1,17,17L144.97,134Z";
            if (name == "plus") return "M228,128a12,12,0,0,1-12,12H140v76a12,12,0,0,1-24,0V140H40a12,12,0,0,1,0-24h76V40a12,12,0,0,1,24,0v76h76A12,12,0,0,1,228,128Z";
            if (name == "arrows-left-right") return "M220.49,155.51a12,12,0,0,1,0,17l-32,32a12,12,0,0,1-17-17L187,172H40a12,12,0,0,1,0-24H187l-15.51-15.51a12,12,0,0,1,17-17ZM68.49,88.49,84,72H216a12,12,0,0,0,0-24H84L68.49,31.51a12,12,0,0,0-17,17l32,32a12,12,0,0,0,17,0Z";
            if (name == "pin") return "M216,164h-5.93L190.3,52H192a12,12,0,0,0,0-24H64a12,12,0,0,0,0,24h1.7L45.93,164H40a12,12,0,0,0,0,24h76v52a12,12,0,0,0,24,0V188h76a12,12,0,0,0,0-24ZM90.07,52h75.86L185.7,164H70.3Z";
            if (name == "chevron-down") return "M216.49,104.49l-80,80a12,12,0,0,1-17,0l-80-80a12,12,0,0,1,17-17L128,159l71.51-71.52a12,12,0,0,1,17,17Z";
            if (name == "chevron-up") return "M216.49,168.49a12,12,0,0,1-17,0L128,97,56.49,168.49a12,12,0,0,1-17-17l80-80a12,12,0,0,1,17,0l80,80A12,12,0,0,1,216.49,168.49Z";
            if (name == "file-text") return "M216.49,79.52l-56-56A12,12,0,0,0,152,20H56A20,20,0,0,0,36,40V216a20,20,0,0,0,20,20H200a20,20,0,0,0,20-20V88A12,12,0,0,0,216.49,79.52ZM160,57l23,23H160ZM60,212V44h76V92a12,12,0,0,0,12,12h48V212Zm112-80a12,12,0,0,1-12,12H96a12,12,0,0,1,0-24h64A12,12,0,0,1,172,132Zm0,40a12,12,0,0,1-12,12H96a12,12,0,0,1,0-24h64A12,12,0,0,1,172,172Z";
            if (name == "pencil") return "M227.31,73.37,182.63,28.69a16,16,0,0,0-22.63,0L36.69,152A15.86,15.86,0,0,0,32,163.31V208a16,16,0,0,0,16,16H92.69A15.86,15.86,0,0,0,104,219.31L227.31,96a16,16,0,0,0,0-22.63ZM92.69,208H48V163.31l88-88L180.69,120ZM192,108.69,147.31,64l24-24L216,84.69Z";
            return "M128,20l18.9,67.1L214,106l-67.1,18.9L128,192l-18.9-67.1L42,106l67.1-18.9Z";
        }
    }
}
